#include "body_audio.h"
#include "eyes.h"
#include "motion_controller.h"
#include "board_audio.h"
#include "audio_rate.h"
#include "audio_preroll.h"
#include "audio_playback_policy.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/queue.h"
#include "freertos/semphr.h"
#include "esp_heap_caps.h"
#include <stdatomic.h>
#include <string.h>
#include <stdio.h>
#include "sdkconfig.h"
#include "esp_timer.h"
#include "nvs.h"
#if defined(CONFIG_MARVIN_SIGNED_OTA) && !defined(CONFIG_MARVIN_OTA_TEST_REJECT_BOOT) && !defined(CONFIG_MARVIN_WAKE_AUTOSTART)
#error "Signed release firmware must arm wake activation at boot"
#endif
static atomic_uint flush_max_us,append_max_us,flush_requests,convert_max_us,write_max_us;
#ifdef CONFIG_MARVIN_LOCAL_AFE
#include "local_afe.h"
#include "device_link.h"
#include "motion_controller.h"
#include "esp_timer.h"
#endif
#define OUTPUT_SAMPLES (24000*8)
#define CAPTURE_PACKETS 512 /* 5.12 s startup cushion in PSRAM; fail closed on overflow. */
typedef struct {unsigned generation;size_t count;int16_t pcm[160];} capture_t;
static QueueHandle_t input_queue;
static StaticQueue_t input_control;
static SemaphoreHandle_t lock,output_lock;
static int16_t *ring;
static size_t head,used;
static uint8_t turn[16];
static bool has_turn,available;
static atomic_bool capture_active,playing,quiescing,turn_active,turn_ended,prewarm_requested;
static atomic_bool ready_cue_pending,unpaired_cue_pending,reconnecting_cue_pending,ready_cue_playing;
static atomic_bool microphone_muted;
static atomic_bool playback_mic_enabled;
static atomic_uint followup_seconds=5;
#ifdef CONFIG_MARVIN_WAKE_AUTOSTART
static atomic_bool wake_activation_enabled=true;
#else
static atomic_bool wake_activation_enabled=false;
#endif
static atomic_uint parked;
static bool park(unsigned bit){if(!atomic_load(&quiescing))return false;atomic_fetch_or(&parked,bit);vTaskDelay(pdMS_TO_TICKS(100));return true;}
static atomic_uint fault;
static atomic_uint capture_generation,captured_samples,played_samples,microphone_peak,queue_peak,capture_stack,playback_stack,playback_leadin_samples;
static atomic_uint take_calls,dequeued_packets,discarded_packets;
static marvin_rate_t output_rate;
#ifndef CONFIG_MARVIN_LOCAL_AFE
static void capture_task(void *unused){
 (void)unused;int16_t mic1[160],mic2[160];capture_t packet;unsigned generation=0,echo_tail=0;
 for(;;){
  atomic_store(&capture_stack,uxTaskGetStackHighWaterMark(NULL));
  if(park(1))continue;
  if(!atomic_load(&capture_active)){vTaskDelay(pdMS_TO_TICKS(10));continue;}
  unsigned next=atomic_load(&capture_generation);
  if(generation!=next){generation=next;for(int i=0;i<4;i++){size_t n;marvin_audio_read(mic1,mic2,160,&n);}}
  size_t frames=0;if(marvin_audio_read(mic1,mic2,160,&frames)!=ESP_OK){atomic_store(&fault,1);atomic_store(&capture_active,false);continue;}
  /* Half-duplex fallback until the real AEC path is validated. Keep reading and discard speaker echo locally. */
  if(atomic_load(&playing))echo_tail=20;else if(echo_tail)echo_tail--;
  unsigned peak=0;for(size_t i=0;i<frames;i++){unsigned v=mic1[i]<0?-(int)mic1[i]:mic1[i];if(v>peak)peak=v;}if(peak>atomic_load(&microphone_peak))atomic_store(&microphone_peak,peak);atomic_fetch_add(&captured_samples,frames);atomic_store(&capture_stack,uxTaskGetStackHighWaterMark(NULL));
  packet.generation=generation;packet.count=frames;memcpy(packet.pcm,mic1,frames*2);
  memset(mic1,0,sizeof(mic1));memset(mic2,0,sizeof(mic2));
  if(!echo_tail&&atomic_load(&capture_active)&&generation==atomic_load(&capture_generation)&&(!packet.count||xQueueSend(input_queue,&packet,0)!=pdTRUE)){atomic_store(&fault,2);atomic_store(&capture_active,false);}
  memset(&packet,0,sizeof(packet));
 }
}
#else
static atomic_uint local_wakes,afe_samples,afe_faults,afe_feed_max_us,afe_read_error,afe_feed_calls,afe_feed_total_us;
static int16_t *afe_feed_buffer;
static marvin_preroll_t *preroll;
static atomic_uint preroll_samples,local_interrupts,echo_suppressed_samples;
static size_t afe_chunk,afe_channels;
static void feed_task(void *unused){
 (void)unused;size_t used=0;int16_t input[160*3];
 for(;;){
  if(park(2))continue;
  size_t n=0;esp_err_t result=marvin_audio_read_afe(input,&n);
  if(result!=ESP_OK){atomic_store(&afe_read_error,(unsigned)result);used=0;atomic_fetch_add(&afe_faults,1);if(atomic_load(&capture_active))atomic_store(&fault,5);vTaskDelay(pdMS_TO_TICKS(10));continue;}
  if(afe_channels==3)memcpy(afe_feed_buffer+used*3,input,n*3*sizeof(int16_t));
  else for(size_t i=0;i<n;i++){afe_feed_buffer[(used+i)*2]=input[i*3];afe_feed_buffer[(used+i)*2+1]=input[i*3+2];}
  used+=n;
  while(used>=afe_chunk){
   int64_t began=esp_timer_get_time();
   if(!marvin_afe_feed(afe_feed_buffer,afe_chunk)){atomic_fetch_add(&afe_faults,1);atomic_store(&fault,6);}
   unsigned elapsed=esp_timer_get_time()-began;atomic_fetch_add(&afe_feed_calls,1);atomic_fetch_add(&afe_feed_total_us,elapsed);if(elapsed>atomic_load(&afe_feed_max_us))atomic_store(&afe_feed_max_us,elapsed);
   used-=afe_chunk;memmove(afe_feed_buffer,afe_feed_buffer+afe_chunk*afe_channels,used*afe_channels*sizeof(int16_t));
  }
  memset(input,0,sizeof(input));
 }
}
static void capture_task(void *unused){
 (void)unused;capture_t packet;unsigned generation=0,echo_tail=0;int64_t last_wake=0,wake_resume_at=0;int16_t partial[160];size_t partial_count=0;
 for(;;){
  if(park(1))continue;
  /* Keep the phrase detector running during a response so "Hey Marvin" can
   * interrupt playback without treating every loudspeaker syllable as speech. */
  marvin_afe_wake_enabled(true);
  marvin_afe_result_t result;
  if(!marvin_afe_fetch(&result)){if(result.fault)atomic_store(&fault,6);vTaskDelay(pdMS_TO_TICKS(10));continue;}
  atomic_fetch_add(&afe_samples,result.frames);
  atomic_store(&capture_stack,uxTaskGetStackHighWaterMark(NULL));
  int64_t now=esp_timer_get_time();if(atomic_load(&playing))wake_resume_at=now+(atomic_load(&ready_cue_playing)?50000:2000000);
  if(result.wake&&now-last_wake>2000000){last_wake=now;
#ifndef CONFIG_MARVIN_SILENT_TEST
   if(atomic_load(&capture_active)&&atomic_load(&playing)){atomic_fetch_add(&local_interrupts,1);marvin_device_voice_interrupt_wake();}
   else if(!atomic_load(&capture_active)&&!atomic_load(&playing)&&now>=wake_resume_at&&!atomic_load(&microphone_muted)){atomic_fetch_add(&local_wakes,1);if(atomic_load(&wake_activation_enabled)){marvin_motion_wake();if(!marvin_device_voice_wake())marvin_body_audio_reconnecting_cue();}}
#endif
  }
  if(!atomic_load(&capture_active)){partial_count=0;marvin_preroll_push(preroll,result.pcm,result.frames);continue;}
  unsigned next=atomic_load(&capture_generation);if(next!=generation){generation=next;partial_count=0;
   while(preroll->count){packet.generation=generation;packet.count=marvin_preroll_take(preroll,packet.pcm,160);
    if(xQueueSend(input_queue,&packet,0)!=pdTRUE){atomic_store(&fault,2);atomic_store(&capture_active,false);marvin_preroll_clear(preroll);break;}
    atomic_fetch_add(&preroll_samples,packet.count);atomic_fetch_add(&captured_samples,packet.count);memset(&packet,0,sizeof(packet));
   }
  }
  /* The present AFE/board alignment does not reject the 95/100 loudspeaker
   * strongly enough for reliable full-duplex VAD. Forwarding that residual to
   * either the local or provider VAD cancels every answer as its first word is
   * played. Keep the microphones clocked and the AFE fed, but do not upload
   * speaker echo or its short acoustic tail. This makes body turns dependable
   * half-duplex until measured echo-resistant barge-in is available. */
  if(atomic_load(&playing))echo_tail=4800;
  else if(echo_tail>result.frames)echo_tail-=result.frames;
  else echo_tail=0;
  if(echo_tail&&!atomic_load(&playback_mic_enabled)){partial_count=0;atomic_fetch_add(&echo_suppressed_samples,result.frames);continue;}
  for(size_t i=0;i<result.frames;i++){
   int16_t sample=result.pcm[i];unsigned peak=sample<0?-(int)sample:sample;
   if(peak>atomic_load(&microphone_peak))atomic_store(&microphone_peak,peak);
   partial[partial_count++]=sample;
   if(partial_count==160){
    packet.generation=generation;packet.count=160;memcpy(packet.pcm,partial,320);partial_count=0;
    if(atomic_load(&capture_active)&&generation==atomic_load(&capture_generation)&&xQueueSend(input_queue,&packet,0)!=pdTRUE){atomic_store(&fault,2);atomic_store(&capture_active,false);break;}
    atomic_fetch_add(&captured_samples,160);memset(&packet,0,sizeof(packet));memset(partial,0,sizeof(partial));
   }
  }
  atomic_store(&capture_stack,uxTaskGetStackHighWaterMark(NULL));
 }
}
#endif
static unsigned speech_level(const int16_t *samples,size_t count){
 unsigned long long total=0;
 for(size_t i=0;i<count;i++)total+=(unsigned)(samples[i]<0?-(int)samples[i]:samples[i]);
 if(!count)return 0;
 unsigned average=(unsigned)(total/count);
 /* Ignore the codec floor, then saturate around ordinary synthetic speech.
  * Average magnitude follows phrases more steadily than sample peaks. */
 if(average<180)return 0;
 unsigned level=(average-180)*1000/5200;
 return level>1000?1000:level;
}
static void playback_task(void *unused){
 (void)unused;int16_t source[240],samples[160],silence[160]={0};int64_t last_write=0,amplifier_ready_at=0;
 /* Two short rising notes, each faded in and out to avoid clicks. */
 static const int16_t low[20]={0,2472,4702,6472,7608,8000,7608,6472,4702,2472,0,-2472,-4702,-6472,-7608,-8000,-7608,-6472,-4702,-2472};
 static const int16_t high[16]={0,3061,5657,7391,8000,7391,5657,3061,0,-3061,-5657,-7391,-8000,-7391,-5657,-3061};
 for(;;){
  if(park(4))continue;
  if(atomic_load(&flush_requests)){vTaskDelay(1);continue;}
  atomic_store(&playback_stack,uxTaskGetStackHighWaterMark(NULL));
  xSemaphoreTake(output_lock,portMAX_DELAY);
  if(atomic_exchange(&prewarm_requested,false)){
   /* voice_turn arrives before provider generation. Start the analog path now
    * and let model latency overlap the NS4150B's startup interval. */
   last_write=0;
   atomic_store(&playing,true);
   if(marvin_audio_mute(false)!=ESP_OK)atomic_store(&fault,3);
   amplifier_ready_at=esp_timer_get_time()+MARVIN_AMPLIFIER_STARTUP_US;
   /* Prime the TX path, but use the monotonic deadline above—not DMA queue
    * depth—as the proof that the physical amplifier is ready. */
   for(unsigned lead=0;lead<4;lead++){
    if(marvin_audio_write(silence,160)!=ESP_OK){atomic_store(&fault,4);break;}
    atomic_fetch_add(&playback_leadin_samples,160);
   }
  }
  int64_t now=esp_timer_get_time();
  bool response_ready=!atomic_load(&turn_active)||marvin_amplifier_ready(now,amplifier_ready_at);
  xSemaphoreTake(lock,portMAX_DELAY);
  size_t n=response_ready?(used<240?used:240):0;
  for(size_t i=0;i<n;i++)source[i]=ring[(head+i)%OUTPUT_SAMPLES];
  head=(head+n)%OUTPUT_SAMPLES;used-=n;
  xSemaphoreGive(lock);
  bool reconnecting_cue=!n&&!atomic_load(&playing)&&atomic_exchange(&reconnecting_cue_pending,false);
  bool unpaired_cue=!reconnecting_cue&&!n&&!atomic_load(&playing)&&atomic_exchange(&unpaired_cue_pending,false);
  if(!n&&!atomic_load(&playing)&&(reconnecting_cue||unpaired_cue||atomic_exchange(&ready_cue_pending,false))){
   atomic_store(&ready_cue_playing,true);atomic_store(&playing,true);
   bool ok=marvin_audio_mute(false)==ESP_OK;
   /* Local cues do not have a preceding voice_turn, so supply the complete
    * conservative NS4150B startup interval here. */
   for(unsigned lead=0;ok&&lead<15;lead++)ok=marvin_audio_write(silence,160)==ESP_OK;
   for(unsigned note=0;ok&&note<(reconnecting_cue?3u:2u);note++){
    /* Ready rises; unpairing falls so the two local acknowledgements remain
     * distinguishable without an application or network connection. */
    const bool use_high=reconnecting_cue?note==1:unpaired_cue?note==0:note!=0;
    const int16_t *wave=use_high?high:low;unsigned period=use_high?16:20;
    for(unsigned chunk=0;ok&&chunk<8;chunk++){
     for(unsigned i=0;i<160;i++){
      unsigned index=chunk*160+i;
      unsigned fade=160;
      if(index<160)fade=index;
      else if(index>1120)fade=1280-index;
      samples[i]=(int16_t)(wave[index%period]*(int)fade/160);
     }
     ok=marvin_audio_write(samples,160)==ESP_OK;
    }
    if(note<(reconnecting_cue?2u:1u))for(unsigned gap=0;ok&&gap<2;gap++)ok=marvin_audio_write(silence,160)==ESP_OK;
   }
   /* I2S write queues DMA. Clock out its four 10 ms buffers before muting. */
   for(unsigned tail=0;ok&&tail<6;tail++)ok=marvin_audio_write(silence,160)==ESP_OK;
   if(marvin_audio_mute(true)!=ESP_OK||!ok)atomic_store(&fault,4);
   atomic_store(&playing,false);atomic_store(&ready_cue_playing,false);
  }
  if(n){
   int64_t began=esp_timer_get_time();size_t frames=marvin_rate_convert(&output_rate,source,n,samples,160);
   unsigned elapsed=esp_timer_get_time()-began;if(elapsed>atomic_load(&convert_max_us))atomic_store(&convert_max_us,elapsed);
   if(frames){
    marvin_motion_voice_level(speech_level(samples,frames));
    began=esp_timer_get_time();if(marvin_audio_write(samples,frames)!=ESP_OK)atomic_store(&fault,4);
    else {atomic_fetch_add(&played_samples,frames);last_write=esp_timer_get_time();}
    elapsed=esp_timer_get_time()-began;if(elapsed>atomic_load(&write_max_us))atomic_store(&write_max_us,elapsed);
   }
  }else if(atomic_load(&turn_active)&&atomic_load(&playing)&&marvin_playback_should_power_down(atomic_load(&turn_ended),true,now,last_write,amplifier_ready_at)){
   marvin_audio_mute(true);atomic_store(&playing,false);atomic_store(&turn_active,false);marvin_motion_voice_level(0);
  }
  xSemaphoreGive(output_lock);
  memset(source,0,sizeof(source));memset(samples,0,sizeof(samples));
  /* DMA supplies the playback clock. A10ms task delay would insert gaps. */
  if(n)taskYIELD();else vTaskDelay(pdMS_TO_TICKS(10));
 }
}
esp_err_t marvin_body_audio_init(void){
 if(lock)return ESP_ERR_INVALID_STATE;
#ifdef CONFIG_MARVIN_LOCAL_AFE
 esp_err_t afe_error=marvin_afe_open();if(afe_error!=ESP_OK)return afe_error;
 afe_chunk=marvin_afe_feed_size();afe_channels=marvin_afe_feed_channels();if(!afe_chunk||afe_chunk>1024||(afe_channels!=2&&afe_channels!=3))return ESP_ERR_INVALID_SIZE;
 afe_feed_buffer=heap_caps_calloc((afe_chunk+160)*afe_channels,sizeof(int16_t),MALLOC_CAP_SPIRAM|MALLOC_CAP_8BIT);
 preroll=heap_caps_calloc(1,sizeof(*preroll),MALLOC_CAP_SPIRAM|MALLOC_CAP_8BIT);
 if(!afe_feed_buffer||!preroll)return ESP_ERR_NO_MEM;
#endif
 esp_err_t err=marvin_audio_open();if(err!=ESP_OK)return err;
 nvs_handle_t preferences;
 if(nvs_open("pet_audio",NVS_READONLY,&preferences)==ESP_OK){
  uint8_t volume,muted,gain_db,playback_mic,followup;
  if(nvs_get_u8(preferences,"volume",&volume)==ESP_OK&&volume<=100)marvin_audio_set_volume(volume);
  if(nvs_get_u8(preferences,"mic_gain",&gain_db)==ESP_OK&&gain_db<=36&&gain_db%6==0)marvin_audio_set_microphone_gain(gain_db);
  if(nvs_get_u8(preferences,"mic_muted",&muted)==ESP_OK&&muted<=1)atomic_store(&microphone_muted,muted!=0);
  if(nvs_get_u8(preferences,"play_mic",&playback_mic)==ESP_OK&&playback_mic<=1)atomic_store(&playback_mic_enabled,playback_mic!=0);
  if(nvs_get_u8(preferences,"followup",&followup)==ESP_OK&&followup<=30)atomic_store(&followup_seconds,followup);
  nvs_close(preferences);
 }
 lock=xSemaphoreCreateMutex();output_lock=xSemaphoreCreateMutex();uint8_t *storage=heap_caps_calloc(CAPTURE_PACKETS,sizeof(capture_t),MALLOC_CAP_SPIRAM|MALLOC_CAP_8BIT);
 if(storage)input_queue=xQueueCreateStatic(CAPTURE_PACKETS,sizeof(capture_t),storage,&input_control);
 ring=heap_caps_calloc(OUTPUT_SAMPLES,sizeof(int16_t),MALLOC_CAP_SPIRAM|MALLOC_CAP_8BIT);
 if(!lock||!output_lock||!input_queue||!ring)return ESP_ERR_NO_MEM;
 marvin_rate_init(&output_rate,false);
#ifdef CONFIG_MARVIN_LOCAL_AFE
 /* Floating-point work pins a task to its current core in ESP-IDF. Choose the
  * fetch/resampling core explicitly so activation cannot pin it onto the AEC
  * feed/radio core and starve the lower-priority device transport. */
 if(xTaskCreatePinnedToCore(capture_task,"body_capture",6144,NULL,6,NULL,1)!=pdPASS)return ESP_ERR_NO_MEM;
 if(xTaskCreatePinnedToCore(feed_task,"afe_feed",6144,NULL,6,NULL,0)!=pdPASS)return ESP_ERR_NO_MEM;
#else
 if(xTaskCreatePinnedToCore(capture_task,"body_capture",6144,NULL,6,NULL,1)!=pdPASS)return ESP_ERR_NO_MEM;
#endif
 if(xTaskCreatePinnedToCore(playback_task,"body_playback",6144,NULL,7,NULL,1)!=pdPASS)return ESP_ERR_NO_MEM;
 available=true;return ESP_OK;
}
bool marvin_body_audio_available(void){return available&&!atomic_load(&quiescing);}
void marvin_body_capture(bool active){atomic_store(&capture_active,false);atomic_fetch_add(&capture_generation,1);if(input_queue)xQueueReset(input_queue);atomic_store(&capture_active,active&&!atomic_load(&quiescing)&&!atomic_load(&microphone_muted));}
size_t marvin_body_input_waiting(void){return input_queue?uxQueueMessagesWaiting(input_queue):0;}
size_t marvin_body_take_input(int16_t *pcm,size_t capacity){
 atomic_fetch_add(&take_calls,1);
 capture_t packet;if(!input_queue||!pcm||capacity<160)return 0;
 while(xQueueReceive(input_queue,&packet,0)==pdTRUE){atomic_fetch_add(&dequeued_packets,1);if(atomic_load(&capture_active)&&packet.generation==atomic_load(&capture_generation)){memcpy(pcm,packet.pcm,packet.count*2);size_t n=packet.count;memset(&packet,0,sizeof(packet));return n;}atomic_fetch_add(&discarded_packets,1);}
 return 0;
}
void marvin_body_audio_flush(void){
 if(!lock||!output_lock)return;
 int64_t began=esp_timer_get_time();atomic_fetch_add(&flush_requests,1);
 /* Same order as playback; receive enqueue never takes the hardware lock. */
 xSemaphoreTake(output_lock,portMAX_DELAY);xSemaphoreTake(lock,portMAX_DELAY);
 used=head=0;has_turn=false;memset(turn,0,sizeof(turn));atomic_store(&turn_active,false);atomic_store(&turn_ended,false);atomic_store(&prewarm_requested,false);atomic_store(&ready_cue_pending,false);atomic_store(&unpaired_cue_pending,false);atomic_store(&reconnecting_cue_pending,false);xSemaphoreGive(lock);
 marvin_rate_init(&output_rate,false);marvin_audio_mute(true);atomic_store(&playing,false);
 marvin_motion_voice_level(0);
 xSemaphoreGive(output_lock);atomic_fetch_sub(&flush_requests,1);
 unsigned elapsed=esp_timer_get_time()-began;if(elapsed>atomic_load(&flush_max_us))atomic_store(&flush_max_us,elapsed);
}
void marvin_body_audio_turn(const uint8_t id[16]){
 marvin_body_audio_flush();xSemaphoreTake(lock,portMAX_DELAY);memcpy(turn,id,16);has_turn=true;atomic_store(&turn_ended,false);atomic_store(&turn_active,true);atomic_store(&prewarm_requested,true);xSemaphoreGive(lock);
}
bool marvin_body_audio_turn_end(const uint8_t id[16]){
 if(!lock||!id)return false;
 xSemaphoreTake(lock,portMAX_DELAY);
 bool current=has_turn&&!memcmp(turn,id,16);
 if(current)atomic_store(&turn_ended,true);
 xSemaphoreGive(lock);
 return current;
}
bool marvin_body_audio_append(const uint8_t *frame,size_t length){
 if(!lock||!frame||length<22||length>3860||(length-20)%2||memcmp(frame,"MVA1",4))return false;
 int64_t began=esp_timer_get_time();size_t count=(length-20)/2;
 xSemaphoreTake(lock,portMAX_DELAY);bool current=has_turn&&!memcmp(turn,frame+4,16),ok=true;
 if(current){
  if(count>OUTPUT_SAMPLES-used)ok=false;
  else {for(size_t i=0;i<count;i++)ring[(head+used+i)%OUTPUT_SAMPLES]=(int16_t)((uint16_t)frame[20+2*i]|((uint16_t)frame[21+2*i]<<8));used+=count;if(used>atomic_load(&queue_peak))atomic_store(&queue_peak,used);}
 }
 xSemaphoreGive(lock);unsigned elapsed=esp_timer_get_time()-began;if(elapsed>atomic_load(&append_max_us))atomic_store(&append_max_us,elapsed);return ok;
}
unsigned marvin_body_audio_fault(void){return atomic_exchange(&fault,0);}
bool marvin_body_audio_playing(void){return atomic_load(&playing);}
bool marvin_body_audio_drained(void){if(!lock)return false;xSemaphoreTake(lock,portMAX_DELAY);bool empty=used==0;xSemaphoreGive(lock);return empty&&!atomic_load(&playing);}
bool marvin_body_audio_ready_cue(void){
#ifdef CONFIG_MARVIN_SILENT_TEST
 return false;
#else
 if(!marvin_body_audio_available()||!output_lock||marvin_audio_volume()==0||atomic_load(&microphone_muted))return false;
 atomic_store(&ready_cue_pending,true);return true;
#endif
}
bool marvin_body_audio_unpaired_cue(void){
#ifdef CONFIG_MARVIN_SILENT_TEST
 return false;
#else
 /* The request may arrive during the small startup interval before audio is
  * marked available. Atomics are initialized statically, and the playback
  * task will consume it once started. Speaker volume zero remains an
  * intentional user choice. */
 if(marvin_audio_volume()==0)return false;
 atomic_store(&unpaired_cue_pending,true);return true;
#endif
}

bool marvin_body_audio_reconnecting_cue(void){
#ifdef CONFIG_MARVIN_SILENT_TEST
 return false;
#else
 if(!marvin_body_audio_available()||!output_lock||marvin_audio_volume()==0||atomic_load(&microphone_muted))return false;
 atomic_store(&reconnecting_cue_pending,true);return true;
#endif
}

void marvin_body_audio_status(void){printf("{\"speakerVolume\":%u,\"microphoneMuted\":%s,\"allowPlaybackMic\":%s,\"followupSeconds\":%u}\n",marvin_audio_volume(),atomic_load(&microphone_muted)?"true":"false",atomic_load(&playback_mic_enabled)?"true":"false",atomic_load(&followup_seconds));printf("{\"playbackTiming\":{\"convertMaxUs\":%u,\"writeMaxUs\":%u}}\n",atomic_load(&convert_max_us),atomic_load(&write_max_us));printf("{\"audioTiming\":{\"flushMaxUs\":%u,\"appendMaxUs\":%u}}\n",atomic_load(&flush_max_us),atomic_load(&append_max_us));printf("{\"audio\":{\"available\":%s,\"silent\":%s,\"captureActive\":%s,\"playing\":%s,\"capturedSamples16k\":%u,\"playedSamples16k\":%u,\"mic1Peak\":%u,\"queuedSamplesPeak\":%u,\"captureStackFree\":%u,\"playbackStackFree\":%u,\"internalFreeBytes\":%u,\"internalLargestBlock\":%u}}\n",available?"true":"false",
#ifdef CONFIG_MARVIN_SILENT_TEST
 "true",
#else
 "false",
#endif
 atomic_load(&capture_active)?"true":"false",atomic_load(&playing)?"true":"false",atomic_load(&captured_samples),atomic_load(&played_samples),atomic_load(&microphone_peak),atomic_load(&queue_peak),atomic_load(&capture_stack),atomic_load(&playback_stack),(unsigned)heap_caps_get_free_size(MALLOC_CAP_INTERNAL),(unsigned)heap_caps_get_largest_free_block(MALLOC_CAP_INTERNAL));
 printf("{\"captureQueue\":{\"takeCalls\":%u,\"dequeuedPackets\":%u,\"discardedPackets\":%u,\"waiting\":%u}}\n",atomic_load(&take_calls),atomic_load(&dequeued_packets),atomic_load(&discarded_packets),input_queue?(unsigned)uxQueueMessagesWaiting(input_queue):0);
#ifdef CONFIG_MARVIN_LOCAL_AFE
 marvin_afe_status();
 printf("{\"localInterrupts\":%u}\n",atomic_load(&local_interrupts));
 printf("{\"echoSuppressedSamples16k\":%u}\n",atomic_load(&echo_suppressed_samples));
 printf("{\"playbackLeadInSamples16k\":%u}\n",atomic_load(&playback_leadin_samples));
 printf("{\"prerollSamples16k\":%u}\n",atomic_load(&preroll_samples));
 printf("{\"afeChannels\":%u}\n",(unsigned)afe_channels);
 printf("{\"afeFeedBudget\":{\"framesPerCall\":%u,\"calls\":%u,\"totalUs\":%u}}\n",(unsigned)afe_chunk,atomic_load(&afe_feed_calls),atomic_load(&afe_feed_total_us));
 printf("{\"afeTiming\":{\"feedMaxUs\":%u,\"readError\":%u}}\n",atomic_load(&afe_feed_max_us),atomic_load(&afe_read_error));
 printf("{\"afe\":{\"processedSamples16k\":%u,\"wakeDetections\":%u,\"feedFaults\":%u,\"wakeActivationDisabled\":%s}}\n",atomic_load(&afe_samples),atomic_load(&local_wakes),atomic_load(&afe_faults),
#ifdef CONFIG_MARVIN_SILENT_TEST
 "true"
#else
 atomic_load(&wake_activation_enabled)?"false":"true"
#endif
 );
#endif
}

/* One-way until reboot. A failed shutdown never permits a flash operation. */
bool marvin_body_quiesce(void){
 if(!available)return marvin_audio_quiesce()==ESP_OK;
 atomic_store(&quiescing,true);marvin_body_capture(false);
 unsigned required=1|4;
#ifdef CONFIG_MARVIN_LOCAL_AFE
 required|=2;
#endif
 for(unsigned i=0;i<200;i++){
  if((atomic_load(&parked)&required)==required){
   if(xSemaphoreTake(lock,pdMS_TO_TICKS(100))!=pdTRUE)return false;
   used=head=0;has_turn=false;memset(turn,0,sizeof(turn));marvin_rate_init(&output_rate,false);atomic_store(&playing,false);xSemaphoreGive(lock);
   return marvin_audio_quiesce()==ESP_OK;
  }
  vTaskDelay(pdMS_TO_TICKS(10));
 }
 return false;
}

bool marvin_body_health_ready(void){
 bool ready=marvin_body_audio_available()&&!atomic_load(&fault)&&atomic_load(&capture_stack)>512&&atomic_load(&playback_stack)>512&&heap_caps_get_free_size(MALLOC_CAP_INTERNAL)>8192;
#ifdef CONFIG_MARVIN_LOCAL_AFE
 ready=ready&&atomic_load(&afe_faults)==0;
#endif
 return ready;
}
uint32_t marvin_body_health_progress(void){
#ifdef CONFIG_MARVIN_LOCAL_AFE
 return atomic_load(&afe_samples);
#else
 return atomic_load(&captured_samples);
#endif
}

bool marvin_body_adjust_volume(int delta){
 if(!output_lock||atomic_load(&quiescing)||delta < -5||delta > 5)return false;
 int volume=(int)marvin_audio_volume()+delta;if(volume<0)volume=0;if(volume>100)volume=100;
 bool ok=marvin_body_set_volume((unsigned)volume);
 printf("{\"speakerVolume\":%u,\"volumeChanged\":%s}\n",marvin_audio_volume(),ok?"true":"false");return ok;
}

static bool save_u8(const char *key,uint8_t value){
 nvs_handle_t preferences;if(nvs_open("pet_audio",NVS_READWRITE,&preferences)!=ESP_OK)return false;
 esp_err_t result=nvs_set_u8(preferences,key,value);if(result==ESP_OK)result=nvs_commit(preferences);nvs_close(preferences);return result==ESP_OK;
}
bool marvin_body_set_volume(unsigned volume){
 if(!output_lock||atomic_load(&quiescing)||volume>100)return false;
 xSemaphoreTake(output_lock,portMAX_DELAY);bool changed=marvin_audio_volume()!=volume;bool ok=marvin_audio_set_volume(volume)==ESP_OK;xSemaphoreGive(output_lock);
 if(!ok)return false;
 if(changed)marvin_eyes_volume(volume);
 return !changed||save_u8("volume",(uint8_t)volume);
}
unsigned marvin_body_volume(void){return marvin_audio_volume();}
bool marvin_body_set_microphone_gain(unsigned gain_db){
 if(!output_lock||atomic_load(&quiescing)||gain_db>36||gain_db%6)return false;
 xSemaphoreTake(output_lock,portMAX_DELAY);bool changed=marvin_audio_microphone_gain()!=gain_db;bool ok=marvin_audio_set_microphone_gain(gain_db)==ESP_OK;xSemaphoreGive(output_lock);
 return ok&&(!changed||save_u8("mic_gain",(uint8_t)gain_db));
}
unsigned marvin_body_microphone_gain(void){return marvin_audio_microphone_gain();}
bool marvin_body_set_microphone_muted(bool muted){
 bool changed=atomic_load(&microphone_muted)!=muted;atomic_store(&microphone_muted,muted);if(muted)marvin_body_capture(false);
 return !changed||save_u8("mic_muted",muted?1:0);
}
bool marvin_body_microphone_muted(void){return atomic_load(&microphone_muted);}
bool marvin_body_set_playback_mic(bool enabled){bool changed=atomic_load(&playback_mic_enabled)!=enabled;atomic_store(&playback_mic_enabled,enabled);return !changed||save_u8("play_mic",enabled?1:0);}
bool marvin_body_playback_mic(void){return atomic_load(&playback_mic_enabled);}
bool marvin_body_set_followup_seconds(unsigned seconds){if(seconds>30)return false;bool changed=atomic_load(&followup_seconds)!=seconds;atomic_store(&followup_seconds,seconds);return !changed||save_u8("followup",(uint8_t)seconds);}
unsigned marvin_body_followup_seconds(void){return atomic_load(&followup_seconds);}

void marvin_body_wake_activation(bool enabled){
 atomic_store(&wake_activation_enabled,enabled);
 printf("{\"wakeActivationEnabled\":%s}\n",enabled?"true":"false");
}
