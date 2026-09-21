#include "local_afe.h"
#include "esp_afe_sr_iface.h"
#include "esp_afe_sr_models.h"
#include "esp_afe_config.h"
#include "model_path.h"
#include "freertos/FreeRTOS.h"
#include "esp_timer.h"
#include <string.h>
#include <stdio.h>
#include <stdatomic.h>
#include "sdkconfig.h"
#ifdef CONFIG_MARVIN_MICRO_WAKE_WORD
#include "micro_wake.h"
static bool wake_enabled=true;
#else
#ifdef CONFIG_MARVIN_HEY_MARVIN_WAKE
#include "esp_mn_models.h"
#include "esp_heap_caps.h"
#define WAKE_HISTORY 5120
static esp_mn_iface_t *wake_api;
static model_iface_data_t *wake_state;
static int16_t *wake_pcm,*history;
static size_t wake_chunk,wake_used,history_head,history_count,silence_samples;
static bool wake_enabled=true,segment_active,wake_latched;
static int64_t resume_at;
static atomic_uint wake_calls,wake_max_us,wake_peak;

static void recognizer_reset(void){
 if(wake_state)wake_api->clean(wake_state);
 wake_used=0;segment_active=false;wake_latched=false;silence_samples=0;
}
static bool recognize_chunk(void){
 int64_t start=esp_timer_get_time();
 esp_mn_state_t result=wake_api->detect(wake_state,wake_pcm);
 unsigned elapsed=esp_timer_get_time()-start;
 atomic_fetch_add(&wake_calls,1);
 if(elapsed>atomic_load(&wake_max_us))atomic_store(&wake_max_us,elapsed);
 if(result==ESP_MN_STATE_TIMEOUT){wake_api->clean(wake_state);return false;}
 if(result!=ESP_MN_STATE_DETECTED)return false;
 esp_mn_results_t *matches=wake_api->get_results(wake_state);
 if(!matches||!matches->num)return false;
 printf("{\"wakeCandidate\":{\"commandId\":%d,\"probability\":%.5f}}\n",matches->command_id[0],matches->prob[0]);
 if(matches->command_id[0]!=1||wake_latched)return false;
 wake_latched=true;
 printf("{\"wakeDetectedUs\":%lld}\n",(long long)esp_timer_get_time());
 return true;
}
static bool consume(const int16_t *pcm,size_t count){
 bool detected=false;
 for(size_t i=0;i<count;i++){
  wake_pcm[wake_used++]=pcm[i];
  if(wake_used==wake_chunk){wake_used=0;detected=recognize_chunk()||detected;}
 }
 return detected;
}
static bool recognize_segment(const int16_t *pcm,size_t count,bool speech){
 if(!wake_enabled)return false;
 for(size_t i=0;i<count;i++){
  int v=pcm[i];unsigned peak=v<0?-v:v;
  if(peak>atomic_load(&wake_peak))atomic_store(&wake_peak,peak);
  history[history_head]=pcm[i];history_head=(history_head+1)%WAKE_HISTORY;
  if(history_count<WAKE_HISTORY)history_count++;
 }
 if(esp_timer_get_time()<resume_at)return false;
 bool detected=false;
 if(speech&&!segment_active){
  recognizer_reset();segment_active=true;
  size_t start=(history_head+WAKE_HISTORY-history_count)%WAKE_HISTORY;
  size_t first=WAKE_HISTORY-start;if(first>history_count)first=history_count;
  detected=consume(history+start,first);
  detected=consume(history,history_count-first)||detected;
 }else if(segment_active){
  detected=consume(pcm,count);
 }
 if(segment_active){
  silence_samples=speech?0:silence_samples+count;
  if(silence_samples>=12800)recognizer_reset();
 }
 return detected;
}
#endif
#endif
static const esp_afe_sr_iface_t *api;
static esp_afe_sr_data_t *state;
static srmodel_list_t *models;
esp_err_t marvin_afe_open(void){
 if(state)return ESP_ERR_INVALID_STATE;
 models=esp_srmodel_init("model");
 if(!models||!models->num)return ESP_ERR_NOT_FOUND;
 afe_config_t *config=afe_config_init("MR",models,AFE_TYPE_FD,AFE_MODE_LOW_COST);
 if(!config)return ESP_ERR_NO_MEM;
 config->aec_init=true;config->vad_init=true;
 config->vad_min_speech_ms=96;config->vad_mode=VAD_MODE_3;
 config->agc_init=false;config->fixed_first_channel=false;config->fixed_output_channel=false;
 config->vad_min_noise_ms=128;config->vad_delay_ms=64;
 // No stock wake-word fallback: Marvin only accepts its configured Hey Marvin detector.
 config->wakenet_init=false;
 config->memory_alloc_mode=AFE_MEMORY_ALLOC_MORE_PSRAM;
 config->afe_perferred_core=1;config->afe_perferred_priority=6;config->afe_ringbuf_size=10;
 api=esp_afe_handle_from_config(config);state=api?api->create_from_config(config):NULL;
 afe_config_free(config);
 if(!state)return ESP_ERR_NO_MEM;
#ifdef CONFIG_MARVIN_MICRO_WAKE_WORD
 return marvin_micro_wake_open();
#elif defined(CONFIG_MARVIN_HEY_MARVIN_WAKE)
 char *name=esp_srmodel_filter(models,"mn6","en");
 wake_api=name?esp_mn_handle_from_name(name):NULL;
 wake_state=wake_api?wake_api->create(name,8000):NULL;
 if(!wake_state)return ESP_ERR_NO_MEM;
 wake_chunk=wake_api->get_samp_chunksize(wake_state);
 if(!wake_chunk||wake_chunk>1024||wake_api->get_samp_rate(wake_state)!=16000)return ESP_ERR_INVALID_SIZE;
 wake_pcm=heap_caps_calloc(wake_chunk,sizeof(int16_t),MALLOC_CAP_SPIRAM|MALLOC_CAP_8BIT);
 history=heap_caps_calloc(WAKE_HISTORY,sizeof(int16_t),MALLOC_CAP_SPIRAM|MALLOC_CAP_8BIT);
 if(!wake_pcm||!history)return ESP_ERR_NO_MEM;
 static char *commands[]={"HEY MARVIN","HEY MARTIN","HEY MARLIN","HEY DARWIN","HEY MORGAN","HEY KEVIN","HEY ROBIN","HEY MARTHA","HELLO MARVIN","GOOD MORNING","HOW ARE YOU","THANK YOU","TURN ON THE LIGHT","PLEASE STOP TALKING","WHAT DO YOU THINK ABOUT MARVIN"};
 enum {N=sizeof(commands)/sizeof(commands[0])};
 static esp_mn_phrase_t phrases[N];static esp_mn_node_t nodes[N+1];
 for(unsigned i=0;i<N;i++){
  if(!wake_api->check_speech_command(wake_state,commands[i]))return ESP_FAIL;
  phrases[i].string=commands[i];phrases[i].command_id=i+1;
  nodes[i].next=&nodes[i+1];nodes[i+1].phrase=&phrases[i];
 }
 esp_mn_error_t *errors=wake_api->set_speech_commands(wake_state,nodes);
 if(errors&&errors->num)return ESP_FAIL;
 wake_api->set_det_threshold(wake_state,0.20f);
 resume_at=esp_timer_get_time()+2000000;
 printf("{\"wakeEngine\":\"multinet6-segment-evaluation\",\"wakePhrase\":\"Hey Marvin\"}\n");
#endif
 return ESP_OK;
}
size_t marvin_afe_feed_size(void){return state?api->get_feed_chunksize(state):0;}
size_t marvin_afe_feed_channels(void){return state?api->get_feed_channel_num(state):0;}
bool marvin_afe_feed(const int16_t *pcm,size_t frames){return state&&pcm&&frames==marvin_afe_feed_size()&&api->feed(state,pcm)>=0;}
bool marvin_afe_fetch(marvin_afe_result_t *out){
 if(!out)return false;
 memset(out,0,sizeof(*out));if(!state)return false;
 afe_fetch_result_t *r=api->fetch_with_delay(state,pdMS_TO_TICKS(100));
 if(!r||r->ret_value==ESP_FAIL||r->data_size<=0||r->data_size%2)return false;
 out->pcm=r->data;out->frames=r->data_size/2;out->speech=r->vad_state==VAD_SPEECH;
#ifdef CONFIG_MARVIN_MICRO_WAKE_WORD
 if(wake_enabled&&!marvin_micro_wake_feed(out->pcm,out->frames,&out->wake)){out->fault=true;return false;}
#elif defined(CONFIG_MARVIN_HEY_MARVIN_WAKE)
 out->wake=recognize_segment(out->pcm,out->frames,out->speech);
#else
 out->wake=false;
#endif
 return true;
}
void marvin_afe_wake_enabled(bool enabled){
#ifdef CONFIG_MARVIN_MICRO_WAKE_WORD
 if(enabled!=wake_enabled){wake_enabled=enabled;marvin_micro_wake_reset();}
#elif defined(CONFIG_MARVIN_HEY_MARVIN_WAKE)
 if(enabled!=wake_enabled){
  wake_enabled=enabled;recognizer_reset();history_head=history_count=0;
  memset(history,0,WAKE_HISTORY*sizeof(int16_t));memset(wake_pcm,0,wake_chunk*sizeof(int16_t));
  resume_at=esp_timer_get_time()+2000000;
 }
#else
 (void)enabled;
#endif
}
void marvin_afe_status(void){
#ifdef CONFIG_MARVIN_MICRO_WAKE_WORD
 marvin_micro_wake_status();
#elif defined(CONFIG_MARVIN_HEY_MARVIN_WAKE)
 printf("{\"wakeRuntime\":{\"phrase\":\"Hey Marvin\",\"model\":\"multinet6-segment-evaluation\",\"calls\":%u,\"maxInferenceUs\":%u,\"inputPeak\":%u}}\n",atomic_load(&wake_calls),atomic_load(&wake_max_us),atomic_load(&wake_peak));
#endif
}
