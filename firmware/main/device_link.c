#include "device_link.h"
#include "device_wire.h"
#include "body_audio.h"
#include "actuators.h"
#include "motion_controller.h"
#include "eyes.h"
#include "remote_control.h"
#include "battery_monitor.h"
#include "esp_websocket_client.h"
#include "esp_crt_bundle.h"
#include "esp_random.h"
#include "esp_timer.h"
#include "esp_log.h"
#include "esp_heap_caps.h"
#include "esp_wifi.h"
#include "esp_netif_sntp.h"
#include "esp_idf_version.h"
#if ESP_IDF_VERSION >= ESP_IDF_VERSION_VAL(5,5,1)
#error "Review and disable WebSocket cross-origin redirects before upgrading ESP-IDF; device bearer credentials must stay on the pinned origin."
#endif
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/queue.h"
#include "nvs.h"
#include "cJSON.h"
#include "mbedtls/platform_util.h"
#include "mbedtls/sha256.h"
#include <stdatomic.h>
#include <stdio.h>
#include <string.h>
#include <math.h>
#include <sys/time.h>
#include "freertos/semphr.h"
/* Motion remains locally gated even when the authenticated device link is live. */
/* A bounded2.56s PCM ingress window absorbs observed coalesced TCP bursts. */
#define RECEIVE_FRAMES 32
static QueueHandle_t incoming;
static StaticQueue_t incoming_control;
static marvin_link_snapshot_t read_identity;
static const char *trusted_ca;
static atomic_bool connected,failed,online,quiescing,link_parked;
static atomic_uint voice_request,link_fault,rx_frames,rx_binary,rx_max_us,control_max_us;
static atomic_bool audio_settings_pending,head_calibration_pending,eye_settings_pending;
static void fail(unsigned reason){unsigned zero=0;atomic_compare_exchange_strong(&link_fault,&zero,reason);atomic_store(&failed,true);}
static atomic_uint pcm_sent,max_write_us,send_attempts,link_stack,link_attempts,link_state;
static int16_t *uplink_pcm;
static SemaphoreHandle_t uplink_lock;
static esp_websocket_client_handle_t uplink_client;
static atomic_bool uplink_enabled,upload_parked;
static atomic_uint upload_stack;
static bool voice_pending,voice_active,wake_requested;
static char playback_done_id[37];
static bool ready_feedback_sent;
static int64_t voice_since;
static marvin_wire_t wire;
static marvin_frame_t frame;
static marvin_link_identity_t identity,current;
static char boot_id[37];
typedef struct {char id[37];uint8_t state;} motion_entry_t;
typedef struct {uint32_t magic,next;motion_entry_t entries[64];} motion_ledger_t;
static motion_ledger_t motion_ledger;
static nvs_handle_t motion_nvs;
static bool motion_ledger_ready;
static char active_motion_id[37];
static int64_t active_motion_until;
static bool active_motion_tracks;
static unsigned head_signal_state;
typedef struct {int left,right;uint16_t duration_ms;} motion_step_t;
/* Named motions retain short, re-checked segments. The remote controller has
 * its own 120 ms watchdog and does not rely on a fixed 500 ms drive limit. */
enum { MOTION_PWM_PERCENT=100, MOTION_SEGMENT_MS=500, MOTION_SEGMENTS=4 };
static motion_step_t motion_steps[MOTION_SEGMENTS];
static unsigned motion_step_count,motion_step_index;
static void head_signal(unsigned state);
static int64_t milliseconds(void){struct timeval t;gettimeofday(&t,NULL);return (int64_t)t.tv_sec*1000+t.tv_usec/1000;}
/* Credentials and TLS validation both require a usable wall clock.  A USB
 * reset clears the ESP32-S3 clock, so do not wait for the authenticated link
 * (which itself needs TLS) to supply the first timestamp. */
static bool sync_clock(void){
 if(milliseconds()>=1577836800000LL)return true;
 esp_sntp_config_t config=ESP_NETIF_SNTP_DEFAULT_CONFIG("pool.ntp.org");
 if(esp_netif_sntp_init(&config)!=ESP_OK)return false;
 bool synced=esp_netif_sntp_sync_wait(pdMS_TO_TICKS(15000))==ESP_OK;
 esp_netif_sntp_deinit();
 return synced&&milliseconds()>=1577836800000LL;
}
static void event(void *arg,esp_event_base_t base,int32_t id,void *data){
 (void)arg;(void)base;
 if(id==WEBSOCKET_EVENT_CONNECTED){atomic_store(&connected,true);return;}
 if(id==WEBSOCKET_EVENT_DISCONNECTED||id==WEBSOCKET_EVENT_CLOSED||id==WEBSOCKET_EVENT_ERROR){fail(1);atomic_store(&online,false);return;}
 if(id!=WEBSOCKET_EVENT_DATA||atomic_load(&failed))return;
 esp_websocket_event_data_t *e=data;
 marvin_wire_result_t result=marvin_wire_append(&wire,e->op_code,e->fin,e->payload_offset,e->payload_len,e->data_ptr,e->data_len);
 if(result==MARVIN_WIRE_ERROR)fail(2);
 if(result==MARVIN_WIRE_COMPLETE){atomic_fetch_add(&rx_frames,1);if(wire.message.binary)atomic_fetch_add(&rx_binary,1);if(xQueueSend(incoming,&wire.message,pdMS_TO_TICKS(100))!=pdTRUE)fail(3);}
}
static bool send_json(esp_websocket_client_handle_t client,cJSON *message){
 char *text=message?cJSON_PrintUnformatted(message):NULL;cJSON_Delete(message);if(!text)return false;
 int64_t began=esp_timer_get_time();size_t size=strlen(text);bool ok=esp_websocket_client_send_text(client,text,(int)size,pdMS_TO_TICKS(2000))==(int)size;cJSON_free(text);unsigned elapsed=esp_timer_get_time()-began;if(elapsed>atomic_load(&control_max_us))atomic_store(&control_max_us,elapsed);return ok;
}
static cJSON *audio_settings(void){cJSON *settings=cJSON_CreateObject();if(settings){cJSON_AddNumberToObject(settings,"volume",marvin_body_volume());cJSON_AddBoolToObject(settings,"muted",marvin_body_microphone_muted());cJSON_AddNumberToObject(settings,"microphoneGainDb",marvin_body_microphone_gain());cJSON_AddBoolToObject(settings,"allowPlaybackMic",marvin_body_playback_mic());cJSON_AddNumberToObject(settings,"followupSeconds",marvin_body_followup_seconds());}return settings;}
static bool send_audio_settings(esp_websocket_client_handle_t client){cJSON *message=audio_settings();if(!message)return false;cJSON_AddStringToObject(message,"type","audio_settings");return send_json(client,message);}
static cJSON *head_calibration(void){marvin_head_calibration_t value;marvin_head_calibration_get(&value);cJSON *settings=cJSON_CreateObject();if(settings){cJSON_AddNumberToObject(settings,"yawCenter",value.yaw_center);cJSON_AddNumberToObject(settings,"pitchCenter",value.pitch_center);cJSON_AddBoolToObject(settings,"yawReversed",value.yaw_reversed);cJSON_AddBoolToObject(settings,"pitchReversed",value.pitch_reversed);}return settings;}
static bool send_head_calibration(esp_websocket_client_handle_t client){cJSON *message=head_calibration();if(!message)return false;cJSON_AddStringToObject(message,"type","head_calibration");return send_json(client,message);}
static const char *eye_design_name(marvin_eye_design_t design){return design==MARVIN_EYE_DESIGN_SOLID?"solid":design==MARVIN_EYE_DESIGN_FRIENDLY?"friendly":"classic";}
static cJSON *eye_settings(void){cJSON *settings=cJSON_CreateObject();if(settings)cJSON_AddStringToObject(settings,"design",eye_design_name(marvin_eyes_design()));return settings;}
static bool send_eye_settings(esp_websocket_client_handle_t client){cJSON *message=eye_settings();if(!message)return false;cJSON_AddStringToObject(message,"type","eye_settings");return send_json(client,message);}
static cJSON *battery_status(void){
 marvin_battery_status_t value;cJSON *status=cJSON_CreateObject();if(!status)return NULL;
 if(marvin_battery_monitor_read(&value)&&value.available){cJSON_AddNumberToObject(status,"levelPercent",value.level_percent);cJSON_AddNumberToObject(status,"voltageMv",value.voltage_mv);}
 else {cJSON_AddNullToObject(status,"levelPercent");cJSON_AddNullToObject(status,"voltageMv");}
 if(value.available&&value.charging_supported)cJSON_AddBoolToObject(status,"charging",value.charging);else cJSON_AddNullToObject(status,"charging");
 return status;
}
static bool send_battery_status(esp_websocket_client_handle_t client){cJSON *message=battery_status();if(!message)return false;cJSON_AddStringToObject(message,"type","battery_status");return send_json(client,message);}
static bool hello(esp_websocket_client_handle_t client){
 cJSON *message=cJSON_CreateObject();if(!message)return false;
 cJSON_AddStringToObject(message,"type","hello");cJSON *protocol=cJSON_AddObjectToObject(message,"protocol");cJSON_AddNumberToObject(protocol,"major",1);cJSON_AddNumberToObject(protocol,"minor",MARVIN_DEVICE_PROTOCOL_MINOR);
 cJSON_AddStringToObject(message,"deviceId",identity.device_id);cJSON_AddStringToObject(message,"bootId",boot_id);cJSON *caps=cJSON_AddArrayToObject(message,"capabilities");if(marvin_body_audio_available()){cJSON *settings=audio_settings();if(!settings){cJSON_Delete(message);return false;}cJSON_AddItemToArray(caps,cJSON_CreateString("voice"));cJSON_AddItemToObject(message,"audioSettings",settings);}cJSON_AddItemToArray(caps,cJSON_CreateString("head"));cJSON *calibration=head_calibration();if(!calibration){cJSON_Delete(message);return false;}cJSON_AddItemToObject(message,"headCalibration",calibration);cJSON_AddItemToArray(caps,cJSON_CreateString("eyes"));cJSON *eyes=eye_settings();if(!eyes){cJSON_Delete(message);return false;}cJSON_AddItemToObject(message,"eyeSettings",eyes);cJSON *battery=battery_status();if(!battery){cJSON_Delete(message);return false;}cJSON_AddItemToObject(message,"batteryStatus",battery);
 cJSON_AddItemToArray(caps,cJSON_CreateString("tracks"));cJSON_AddItemToArray(caps,cJSON_CreateString("motion"));cJSON_AddItemToArray(caps,cJSON_CreateString("remote"));
 cJSON_AddNumberToObject(message,"audioInputRate",16000);
#ifdef CONFIG_MARVIN_LOCAL_DEV_MODE
 cJSON_AddStringToObject(message,"firmware","marvin-local-dev");
#else
 cJSON_AddStringToObject(message,"firmware","marvin-owner-remote-0.4");
#endif
 return send_json(client,message);
}
static bool uuid(const char *text,uint8_t bytes[16]){
 if(!text||strlen(text)!=36)return false;
 unsigned n=0;
 for(unsigned i=0;i<36;){if(i==8||i==13||i==18||i==23){if(text[i++]!='-')return false;continue;}int a=text[i],b=text[i+1];a=a>='0'&&a<='9'?a-'0':a>='a'&&a<='f'?a-'a'+10:-1;b=b>='0'&&b<='9'?b-'0':b>='a'&&b<='f'?b-'a'+10:-1;if(a<0||b<0||n>=16)return false;bytes[n++]=(uint8_t)(a*16+b);i+=2;}return n==16;
}
static bool motion_save(void){
 if(!motion_ledger_ready)return false;
 return nvs_set_blob(motion_nvs,"ledger",&motion_ledger,sizeof(motion_ledger))==ESP_OK&&nvs_commit(motion_nvs)==ESP_OK;
}
static motion_entry_t *motion_find(const char *id){
 for(unsigned i=0;i<64;i++)if(!strcmp(motion_ledger.entries[i].id,id))return &motion_ledger.entries[i];
 return NULL;
}
static bool motion_record(const char *id,uint8_t state){
 motion_entry_t *entry=motion_find(id);
 if(!entry){entry=&motion_ledger.entries[motion_ledger.next++%64];memset(entry,0,sizeof(*entry));snprintf(entry->id,sizeof(entry->id),"%s",id);}
 entry->state=state;
 return motion_save();
}
static bool motion_status(esp_websocket_client_handle_t client,const char *id,const char *status,const char *code){
 cJSON *m=cJSON_CreateObject();if(!m)return false;
 cJSON_AddStringToObject(m,"type","command_status");cJSON_AddStringToObject(m,"id",id);
 cJSON_AddStringToObject(m,"status",status);if(code)cJSON_AddStringToObject(m,"code",code);
 return send_json(client,m);
}
static bool whole(const cJSON *value,double min,double max){
 return cJSON_IsNumber(value)&&isfinite(value->valuedouble)&&floor(value->valuedouble)==value->valuedouble&&value->valuedouble>=min&&value->valuedouble<=max;
}
static bool unique_fields(const cJSON *object){
 if(!cJSON_IsObject(object))return false;
 for(const cJSON *a=object->child;a;a=a->next){
  if(!a->string)return false;
  for(const cJSON *b=a->next;b;b=b->next)if(b->string&&!strcmp(a->string,b->string))return false;
 }
 return true;
}
static bool motion_command(esp_websocket_client_handle_t client,const cJSON *m){
 const cJSON *id=cJSON_GetObjectItemCaseSensitive(m,"id"),*device=cJSON_GetObjectItemCaseSensitive(m,"deviceId"),
  *epoch=cJSON_GetObjectItemCaseSensitive(m,"epoch"),*boot=cJSON_GetObjectItemCaseSensitive(m,"bootId"),
  *deadline=cJSON_GetObjectItemCaseSensitive(m,"deadline"),*action=cJSON_GetObjectItemCaseSensitive(m,"action"),
  *args=cJSON_GetObjectItemCaseSensitive(m,"args");
 uint8_t bytes[16];
 if(!unique_fields(m)||!unique_fields(args)||!cJSON_IsString(id)||!uuid(id->valuestring,bytes)||!cJSON_IsString(device)||strcmp(device->valuestring,identity.device_id)||
    !whole(epoch,0,UINT32_MAX)||epoch->valuedouble!=identity.epoch||!cJSON_IsString(boot)||strcmp(boot->valuestring,boot_id)||
    !cJSON_IsString(action)||!cJSON_IsObject(args))return true;
 int64_t now_ms=milliseconds();
 if(!whole(deadline,0,4102444800000.0)||deadline->valuedouble<=now_ms||deadline->valuedouble>now_ms+5000)
  return motion_status(client,id->valuestring,"failed","DEADLINE_INVALID");
 bool tracks=!strcmp(action->valuestring,"tracks"),head=!strcmp(action->valuestring,"head"),preset=!strcmp(action->valuestring,"motion"),remote=!strcmp(action->valuestring,"remote");
 if(!tracks&&!head&&!preset&&!remote)return motion_status(client,id->valuestring,"failed","CAPABILITY_UNAVAILABLE");
 /* Remote packets are deliberately not durable motions. They are short-lived
  * intent samples; the firmware watchdog stops if the stream disappears. */
 if(remote){
  const cJSON *throttle=cJSON_GetObjectItemCaseSensitive(args,"throttle"),*turn=cJSON_GetObjectItemCaseSensitive(args,"turn"),*head_yaw=cJSON_GetObjectItemCaseSensitive(args,"headYaw"),*head_pitch=cJSON_GetObjectItemCaseSensitive(args,"headPitch"),*auto_head=cJSON_GetObjectItemCaseSensitive(args,"autonomousHead"),*sequence=cJSON_GetObjectItemCaseSensitive(args,"sequence");
  if(!cJSON_IsNumber(throttle)||!cJSON_IsNumber(turn)||!cJSON_IsNumber(head_yaw)||!cJSON_IsNumber(head_pitch)||!cJSON_IsBool(auto_head)||!whole(sequence,0,UINT32_MAX)||fabs(throttle->valuedouble)>1||fabs(turn->valuedouble)>1||fabs(head_yaw->valuedouble)>1||fabs(head_pitch->valuedouble)>1)return motion_status(client,id->valuestring,"failed","INVALID_REMOTE_INPUT");
  /* Taking the remote is an explicit operator takeover of any older queued
   * body motion; never let two writers fight over the tracks. */
  if(active_motion_id[0]){if(active_motion_tracks)marvin_tracks_stop();motion_record(active_motion_id,4);active_motion_id[0]=0;active_motion_until=0;}
  marvin_remote_intent_t input={(int)round(throttle->valuedouble*1000),(int)round(turn->valuedouble*1000),(int)round(head_yaw->valuedouble*1000),(int)round(head_pitch->valuedouble*1000),cJSON_IsTrue(auto_head)};
  esp_err_t result=marvin_remote_submit(&input);
  return motion_status(client,id->valuestring,result==ESP_OK?"accepted":"failed",result==ESP_OK?NULL:"MOTION_UNSAFE");
 }
 motion_entry_t *old=motion_find(id->valuestring);
 if(old)return motion_status(client,id->valuestring,old->state==2?"completed":"failed",old->state==2?NULL:"DUPLICATE_UNCERTAIN");
 if(!motion_ledger_ready||active_motion_id[0])return motion_status(client,id->valuestring,"failed",!motion_ledger_ready?"LEDGER_UNAVAILABLE":"MOTION_BUSY");
 const cJSON *duration=cJSON_GetObjectItemCaseSensitive(args,"durationMs");
 if(!preset&&!whole(duration,tracks?50:100,tracks?30000:3000))return motion_status(client,id->valuestring,"failed","INVALID_MOTION");
 int left=0,right=0,yaw=0,pitch=0;
 if(tracks){
  const cJSON *l=cJSON_GetObjectItemCaseSensitive(args,"left"),*r=cJSON_GetObjectItemCaseSensitive(args,"right");
  if(!cJSON_IsNumber(l)||!cJSON_IsNumber(r)||!isfinite(l->valuedouble)||!isfinite(r->valuedouble)||fabs(l->valuedouble)>0.3||fabs(r->valuedouble)>0.3)
   return motion_status(client,id->valuestring,"failed","INVALID_MOTION");
  left=(int)round(l->valuedouble*100);right=(int)round(r->valuedouble*100);
 }else if(head){
  const cJSON *y=cJSON_GetObjectItemCaseSensitive(args,"yaw"),*p=cJSON_GetObjectItemCaseSensitive(args,"pitch");
  if(!cJSON_IsNumber(y)||!cJSON_IsNumber(p)||!isfinite(y->valuedouble)||!isfinite(p->valuedouble)||fabs(y->valuedouble)>40||fabs(p->valuedouble)>30)
   return motion_status(client,id->valuestring,"failed","INVALID_MOTION");
  yaw=(int)round(y->valuedouble);pitch=(int)round(p->valuedouble);
 }else{
  const cJSON *primitive=cJSON_GetObjectItemCaseSensitive(args,"primitive");
  if(!cJSON_IsString(primitive))return motion_status(client,id->valuestring,"failed","INVALID_MOTION");
  if(!strcmp(primitive->valuestring,"forward_bit")){
   /* The 10-bit motor PWM period is 0..1023.  Full power produces duty 1023;
    * four watchdog-bounded segments keep both motors running forward for
    * approximately two seconds. */
   for(unsigned i=0;i<MOTION_SEGMENTS;i++)motion_steps[i]=(motion_step_t){MOTION_PWM_PERCENT,MOTION_PWM_PERCENT,MOTION_SEGMENT_MS};
   motion_step_count=MOTION_SEGMENTS;
  }
  else if(!strcmp(primitive->valuestring,"backward_bit")){for(unsigned i=0;i<MOTION_SEGMENTS;i++)motion_steps[i]=(motion_step_t){-MOTION_PWM_PERCENT,-MOTION_PWM_PERCENT,MOTION_SEGMENT_MS};motion_step_count=MOTION_SEGMENTS;}
  /* Opposite track directions rotate Marvin about its centre. Bare “turn
   * around” uses the rightward convention; explicit left/right remain available. */
  else if(!strcmp(primitive->valuestring,"turn_around")||!strcmp(primitive->valuestring,"turn_right")){for(unsigned i=0;i<MOTION_SEGMENTS;i++)motion_steps[i]=(motion_step_t){MOTION_PWM_PERCENT,-MOTION_PWM_PERCENT,MOTION_SEGMENT_MS};motion_step_count=MOTION_SEGMENTS;}
  else if(!strcmp(primitive->valuestring,"turn_left")){for(unsigned i=0;i<MOTION_SEGMENTS;i++)motion_steps[i]=(motion_step_t){-MOTION_PWM_PERCENT,MOTION_PWM_PERCENT,MOTION_SEGMENT_MS};motion_step_count=MOTION_SEGMENTS;}
  else if(!strcmp(primitive->valuestring,"move_around")){motion_steps[0]=(motion_step_t){18,18,400};motion_steps[1]=(motion_step_t){18,10,450};motion_steps[2]=(motion_step_t){10,18,450};motion_step_count=3;}
  else return motion_status(client,id->valuestring,"failed","INVALID_MOTION");
 }
 /* Commit the command ID before any physical side effect. An uncertain reset
  * causes a duplicate to fail rather than repeat movement. */
 if(!motion_record(id->valuestring,1))return motion_status(client,id->valuestring,"failed","LEDGER_WRITE_FAILED");
 if(tracks){motion_steps[0]=(motion_step_t){left,right,(uint16_t)duration->valuedouble};motion_step_count=1;}
 motion_step_index=0;
 esp_err_t result=head?marvin_motion_head_request(yaw,pitch,(uint32_t)duration->valuedouble):marvin_tracks_set(motion_steps[0].left,motion_steps[0].right,motion_steps[0].duration_ms);
 if(result!=ESP_OK){motion_record(id->valuestring,3);return motion_status(client,id->valuestring,"failed",head?"HEAD_UNAVAILABLE":"MOTION_UNSAFE");}
 if(!head)marvin_eyes_drive(motion_steps[0].left,motion_steps[0].right,motion_steps[0].duration_ms);
 snprintf(active_motion_id,sizeof(active_motion_id),"%s",id->valuestring);
 active_motion_tracks=!head;active_motion_until=esp_timer_get_time()+(int64_t)(head?duration->valuedouble:motion_steps[0].duration_ms)*1000;
 return motion_status(client,id->valuestring,"accepted",NULL);
}
static bool motion_cancel(esp_websocket_client_handle_t client,const cJSON *m){
 const cJSON *id=cJSON_GetObjectItemCaseSensitive(m,"id"),*epoch=cJSON_GetObjectItemCaseSensitive(m,"epoch"),*boot=cJSON_GetObjectItemCaseSensitive(m,"bootId");
 if(!cJSON_IsString(id)||!whole(epoch,0,UINT32_MAX)||epoch->valuedouble!=identity.epoch||!cJSON_IsString(boot)||strcmp(boot->valuestring,boot_id))return true;
 if(strcmp(active_motion_id,id->valuestring))return true;
 if(active_motion_tracks)marvin_tracks_stop();
 active_motion_id[0]=0;active_motion_until=0;motion_record(id->valuestring,4);
 return motion_status(client,id->valuestring,"cancelled",NULL);
}
static bool motion_tick(esp_websocket_client_handle_t client){
 if(!active_motion_id[0]||esp_timer_get_time()<active_motion_until)return true;
 if(active_motion_tracks){
  marvin_tracks_stop();
  if(++motion_step_index<motion_step_count){
   motion_step_t step=motion_steps[motion_step_index];
   if(marvin_tracks_set(step.left,step.right,step.duration_ms)==ESP_OK){marvin_eyes_drive(step.left,step.right,step.duration_ms);active_motion_until=esp_timer_get_time()+(int64_t)step.duration_ms*1000;return true;}
   char failed_id[37];snprintf(failed_id,sizeof(failed_id),"%s",active_motion_id);active_motion_id[0]=0;active_motion_until=0;
   motion_record(failed_id,3);return motion_status(client,failed_id,"failed","MOTION_UNSAFE");
  }
 }
 char id[37];snprintf(id,sizeof(id),"%s",active_motion_id);active_motion_id[0]=0;active_motion_until=0;
 head_signal(head_signal_state);
 bool saved=motion_record(id,2);
 return motion_status(client,id,saved?"completed":"failed",saved?NULL:"LEDGER_WRITE_FAILED");
}
static void head_signal(unsigned state){
 if(state==0&&head_signal_state==0)return;
 head_signal_state=state;
 marvin_eyes_expression(state==1?MARVIN_EYES_LISTENING:state==2?MARVIN_EYES_THINKING:state==3?MARVIN_EYES_SPEAKING:MARVIN_EYES_NEUTRAL);
 if(active_motion_id[0])return;
 marvin_motion_cue(state==1?MARVIN_MOTION_CUE_LISTENING:state==2?MARVIN_MOTION_CUE_THINKING:state==3?MARVIN_MOTION_CUE_SPEAKING:MARVIN_MOTION_CUE_NONE);
}
static bool voice_command(esp_websocket_client_handle_t client,const char *type){cJSON *m=cJSON_CreateObject();cJSON_AddStringToObject(m,"type",type);if(!strcmp(type,"voice_start"))cJSON_AddStringToObject(m,"reason",wake_requested?"wake":"button");return send_json(client,m);}
static void voice_stop_local(void){atomic_store(&uplink_enabled,false);marvin_body_capture(false);marvin_body_audio_flush();voice_pending=voice_active=false;playback_done_id[0]=0;esp_wifi_set_ps(WIFI_PS_MIN_MODEM);}
static bool receive(esp_websocket_client_handle_t client,bool welcomed){
 if(frame.binary){if(welcomed&&voice_active&&head_signal_state!=3)head_signal(3);return welcomed&&marvin_body_audio_available()&&marvin_body_audio_append((const uint8_t*)frame.text,frame.length);}
 bool ok=marvin_wire_control(frame.text,identity.epoch,welcomed);
 if(ok){
  if(!welcomed){
   /* Welcome is received over the pinned, authenticated WSS connection.
    * Sync the wall clock used for short command deadlines to server time;
    * a valid TLS clock can still be seconds or minutes out of step. */
   cJSON *welcome=cJSON_ParseWithOpts(frame.text,NULL,true);
   const cJSON *server_time=cJSON_GetObjectItemCaseSensitive(welcome,"serverTime");
   if(!unique_fields(welcome)||!whole(server_time,1577836800000.0,4102444800000.0)){cJSON_Delete(welcome);return false;}
   int64_t ms=(int64_t)server_time->valuedouble;
   struct timeval wall={.tv_sec=(time_t)(ms/1000),.tv_usec=(suseconds_t)(ms%1000)*1000};
   cJSON_Delete(welcome);
   if(settimeofday(&wall,NULL)!=0)return false;
   atomic_store(&online,true);
#ifdef CONFIG_MARVIN_CONNECTION_EYES_ANIMATION
   /* This is the authenticated server transition, not merely Wi-Fi. The
    * controller coordinates head, tracks and eyes and remains cancellable. */
   marvin_motion_connection(true);
#endif
   if(!ready_feedback_sent){
#ifdef CONFIG_MARVIN_CONNECTION_TONE
    marvin_body_audio_ready_cue();
#endif
    ready_feedback_sent=true;
   }
  }
  return true;
 }
 if(!welcomed)return false;
 cJSON *m=cJSON_ParseWithOpts(frame.text,NULL,true);const cJSON *type=cJSON_GetObjectItemCaseSensitive(m,"type");
 if(cJSON_IsString(type)&&!strcmp(type->valuestring,"command")){bool result=motion_command(client,m);cJSON_Delete(m);return result;}
 if(cJSON_IsString(type)&&!strcmp(type->valuestring,"cancel")){bool result=motion_cancel(client,m);cJSON_Delete(m);return result;}
 if(cJSON_IsString(type)&&!strcmp(type->valuestring,"head_calibration")){
  const cJSON *yaw=cJSON_GetObjectItemCaseSensitive(m,"yawCenter"),*pitch=cJSON_GetObjectItemCaseSensitive(m,"pitchCenter"),*yaw_reverse=cJSON_GetObjectItemCaseSensitive(m,"yawReversed"),*pitch_reverse=cJSON_GetObjectItemCaseSensitive(m,"pitchReversed");
  ok=unique_fields(m)&&whole(yaw,60,120)&&whole(pitch,60,120)&&cJSON_IsBool(yaw_reverse)&&cJSON_IsBool(pitch_reverse);
  if(ok){marvin_head_calibration_t value={(uint8_t)yaw->valueint,(uint8_t)pitch->valueint,cJSON_IsTrue(yaw_reverse),cJSON_IsTrue(pitch_reverse)};ok=marvin_head_calibration_set(&value)==ESP_OK&&marvin_motion_head_request(0,0,500)==ESP_OK;if(ok)atomic_store(&head_calibration_pending,true);}
  cJSON_Delete(m);return ok;
 }
 if(cJSON_IsString(type)&&!strcmp(type->valuestring,"eye_settings")){
  const cJSON *design=cJSON_GetObjectItemCaseSensitive(m,"design");marvin_eye_design_t value=MARVIN_EYE_DESIGN_COUNT;
  if(cJSON_IsString(design)){if(!strcmp(design->valuestring,"classic"))value=MARVIN_EYE_DESIGN_CLASSIC;else if(!strcmp(design->valuestring,"solid"))value=MARVIN_EYE_DESIGN_SOLID;else if(!strcmp(design->valuestring,"friendly"))value=MARVIN_EYE_DESIGN_FRIENDLY;}
  ok=unique_fields(m)&&value<MARVIN_EYE_DESIGN_COUNT&&marvin_eyes_set_design(value)==ESP_OK;if(ok)atomic_store(&eye_settings_pending,true);
  cJSON_Delete(m);return ok;
 }
 if(!marvin_body_audio_available()){cJSON_Delete(m);return false;}
 if(cJSON_IsString(type)){
  if(!strcmp(type->valuestring,"voice_ready")&&voice_pending){const cJSON *rate=cJSON_GetObjectItemCaseSensitive(m,"sampleRate"),*channels=cJSON_GetObjectItemCaseSensitive(m,"channels"),*format=cJSON_GetObjectItemCaseSensitive(m,"format");const cJSON *input=cJSON_GetObjectItemCaseSensitive(m,"inputSampleRate");ok=cJSON_IsNumber(input)&&input->valuedouble==16000&&cJSON_IsNumber(rate)&&rate->valuedouble==24000&&cJSON_IsNumber(channels)&&channels->valuedouble==1&&cJSON_IsString(format)&&!strcmp(format->valuestring,"s16le");if(ok){voice_pending=false;voice_active=true;voice_since=esp_timer_get_time();atomic_store(&uplink_enabled,true);head_signal(1);printf("{\"voice\":\"listening\"}\n");}}
  else if(!strcmp(type->valuestring,"voice_announcement")&&!voice_active&&!voice_pending){const cJSON *rate=cJSON_GetObjectItemCaseSensitive(m,"sampleRate"),*channels=cJSON_GetObjectItemCaseSensitive(m,"channels"),*format=cJSON_GetObjectItemCaseSensitive(m,"format"),*value=cJSON_GetObjectItemCaseSensitive(m,"interactionId");uint8_t id[16];ok=cJSON_IsNumber(rate)&&rate->valuedouble==24000&&cJSON_IsNumber(channels)&&channels->valuedouble==1&&cJSON_IsString(format)&&!strcmp(format->valuestring,"s16le")&&cJSON_IsString(value)&&uuid(value->valuestring,id);if(ok){marvin_body_capture(false);marvin_body_audio_turn(id);voice_active=true;voice_since=esp_timer_get_time();atomic_store(&uplink_enabled,false);printf("{\"voice\":\"announcing_link\"}\n");}}
  else if(!strcmp(type->valuestring,"voice_turn")&&voice_active){uint8_t id[16];const cJSON *value=cJSON_GetObjectItemCaseSensitive(m,"interactionId");ok=cJSON_IsString(value)&&uuid(value->valuestring,id);if(ok){playback_done_id[0]=0;marvin_body_audio_turn(id);head_signal(2);}}
  else if(!strcmp(type->valuestring,"audio_flush")){playback_done_id[0]=0;marvin_body_audio_flush();ok=true;}
  else if(!strcmp(type->valuestring,"audio_settings")){
   const cJSON *volume=cJSON_GetObjectItemCaseSensitive(m,"volume"),*muted=cJSON_GetObjectItemCaseSensitive(m,"muted");
   const cJSON *gain=cJSON_GetObjectItemCaseSensitive(m,"microphoneGainDb"),*playback=cJSON_GetObjectItemCaseSensitive(m,"allowPlaybackMic"),*followup=cJSON_GetObjectItemCaseSensitive(m,"followupSeconds");
   ok=cJSON_IsNumber(volume)&&isfinite(volume->valuedouble)&&volume->valuedouble==volume->valueint&&volume->valueint>=0&&volume->valueint<=100&&cJSON_IsBool(muted)&&cJSON_IsNumber(gain)&&isfinite(gain->valuedouble)&&gain->valuedouble==gain->valueint&&gain->valueint>=0&&gain->valueint<=36&&gain->valueint%6==0&&cJSON_IsBool(playback)&&cJSON_IsNumber(followup)&&isfinite(followup->valuedouble)&&followup->valuedouble==followup->valueint&&followup->valueint>=0&&followup->valueint<=30;
   if(ok){ok=marvin_body_set_volume((unsigned)volume->valueint)&&marvin_body_set_microphone_gain((unsigned)gain->valueint)&&marvin_body_set_microphone_muted(cJSON_IsTrue(muted))&&marvin_body_set_playback_mic(cJSON_IsTrue(playback))&&marvin_body_set_followup_seconds((unsigned)followup->valueint);if(ok){atomic_store(&audio_settings_pending,true);if(cJSON_IsTrue(muted))atomic_store(&voice_request,2);}}
  }
  else if(!strcmp(type->valuestring,"voice_turn_end")){const cJSON *value=cJSON_GetObjectItemCaseSensitive(m,"interactionId");uint8_t id[16];ok=voice_active&&cJSON_IsString(value)&&uuid(value->valuestring,id);if(ok)snprintf(playback_done_id,sizeof(playback_done_id),"%s",value->valuestring);}
  else if(!strcmp(type->valuestring,"voice_closed")||!strcmp(type->valuestring,"voice_error")){voice_stop_local();head_signal(0);printf("{\"voice\":\"closed\"}\n");ok=true;}
 }
 cJSON_Delete(m);return ok;
}
/* A blocking TLS write must never prevent control/RX queue service. The
 * lifecycle lock keeps the client alive until an in-flight write returns. */
static void upload(void *unused){
 (void)unused;
 for(;;){
  if(atomic_load(&quiescing)){atomic_store(&upload_parked,true);vTaskDelay(pdMS_TO_TICKS(100));continue;}
  atomic_store(&upload_stack,uxTaskGetStackHighWaterMark(NULL));
  if(atomic_load(&uplink_enabled)&&!atomic_load(&failed)&&marvin_body_input_waiting()>=8){
   xSemaphoreTake(uplink_lock,portMAX_DELAY);
   if(uplink_client&&atomic_load(&uplink_enabled)&&!atomic_load(&failed)){
    /* Keep individual TLS writes short enough that a jittery AP cannot hold
     * the control/receive path for half a second.  The capture queue remains
     * the bounded cushion; this is not a retry or an unbounded spool. */
    size_t n=0;for(int part=0;part<6;part++){size_t count=marvin_body_take_input(uplink_pcm+n,960-n);if(!count)break;n+=count;}
    if(n&&atomic_load(&uplink_enabled)){
     int64_t began=esp_timer_get_time();atomic_fetch_add(&send_attempts,1);
     int bytes=esp_websocket_client_send_bin(uplink_client,(const char*)uplink_pcm,(int)(n*2),pdMS_TO_TICKS(500));
     unsigned elapsed=esp_timer_get_time()-began;if(elapsed>atomic_load(&max_write_us))atomic_store(&max_write_us,elapsed);
     if(bytes!=(int)(n*2))fail(6);else atomic_fetch_add(&pcm_sent,n);
    }
    memset(uplink_pcm,0,1920*sizeof(int16_t));
   }
   xSemaphoreGive(uplink_lock);
  }
  vTaskDelay(1);
 }
}
static void run(void *unused){
 (void)unused;unsigned retry=0;
 for(;;){
  if(atomic_load(&quiescing)){atomic_store(&link_parked,true);vTaskDelay(pdMS_TO_TICKS(100));continue;}
  mbedtls_platform_zeroize(&identity,sizeof(identity));
  if(!read_identity(&identity)){atomic_store(&link_state,1);vTaskDelay(pdMS_TO_TICKS(1000));continue;}
  if(!sync_clock()){atomic_store(&link_state,2);vTaskDelay(pdMS_TO_TICKS(1000));continue;}
  if(milliseconds()>=identity.expires_ms){atomic_store(&link_state,3);vTaskDelay(pdMS_TO_TICKS(1000));continue;}
  char uri[240],headers[128];
  if(strncmp(identity.origin,"https://",8)||strpbrk(identity.origin+8,"/?#@\r\n")||strpbrk(identity.credential,"\r\n")){atomic_store(&link_state,4);vTaskDelay(pdMS_TO_TICKS(1000));continue;}
  snprintf(uri,sizeof(uri),"wss://%.192s/api/device/socket",identity.origin+8);snprintf(headers,sizeof(headers),"Authorization: Bearer %.95s\r\n",identity.credential);
  atomic_fetch_add(&link_attempts,1);atomic_store(&link_state,5);
  atomic_store(&connected,false);atomic_store(&failed,false);atomic_store(&link_fault,0);atomic_store(&online,false);xQueueReset(incoming);marvin_wire_reset(&wire);wire.allow_audio=marvin_body_audio_available();voice_stop_local();atomic_store(&voice_request,0);atomic_store(&audio_settings_pending,false);atomic_store(&head_calibration_pending,false);atomic_store(&eye_settings_pending,false);
  esp_websocket_client_config_t config={.uri=uri,.headers=headers,.cert_pem=trusted_ca&&trusted_ca[0]?trusted_ca:NULL,.crt_bundle_attach=trusted_ca&&trusted_ca[0]?NULL:esp_crt_bundle_attach,.disable_auto_reconnect=true,.network_timeout_ms=5000,.task_stack=6144,.task_core_id=1,.buffer_size=4096,.tcp_nodelay=marvin_body_audio_available(),.ping_interval_sec=5,.pingpong_timeout_sec=10};
  esp_websocket_client_handle_t client=esp_websocket_client_init(&config);mbedtls_platform_zeroize(headers,sizeof(headers));
  if(client){
   xSemaphoreTake(uplink_lock,portMAX_DELAY);uplink_client=client;xSemaphoreGive(uplink_lock);
   bool started=esp_websocket_register_events(client,WEBSOCKET_EVENT_ANY,event,NULL)==ESP_OK&&esp_websocket_client_start(client)==ESP_OK;
   if(!started){atomic_store(&link_state,6);atomic_store(&failed,true);}
   bool sent=false;uint32_t sequence=0;int64_t began=esp_timer_get_time(),last_received=began,last_sent=began,last_battery=began;
   while(!atomic_load(&failed)&&!atomic_load(&quiescing)){
    int64_t now=esp_timer_get_time();
    if(!read_identity(&current)||current.epoch!=identity.epoch||strcmp(current.credential,identity.credential)||milliseconds()>=identity.expires_ms){atomic_store(&failed,true);}
    mbedtls_platform_zeroize(&current,sizeof(current));
    if(atomic_load(&connected)&&!sent){sent=hello(client);if(!sent)atomic_store(&failed,true);}
    for(unsigned received=0;received<2&&xQueueReceive(incoming,&frame,0)==pdTRUE;received++){int64_t began=esp_timer_get_time();if(!receive(client,atomic_load(&online)))fail(frame.binary?5:4);else last_received=now;unsigned elapsed=esp_timer_get_time()-began;if(elapsed>atomic_load(&rx_max_us))atomic_store(&rx_max_us,elapsed);}
    if(atomic_load(&online)&&!motion_tick(client))atomic_store(&failed,true);
    if((!atomic_load(&online)&&now-began>10000000)||(atomic_load(&online)&&now-last_received>15000000))atomic_store(&failed,true);
    if(atomic_load(&online)&&now-last_sent>=5000000){cJSON *m=cJSON_CreateObject();cJSON_AddStringToObject(m,"type","heartbeat");cJSON_AddNumberToObject(m,"seq",sequence++);if(!send_json(client,m))atomic_store(&failed,true);last_sent=now;}
    if(atomic_load(&online)&&now-last_battery>=30000000){if(!send_battery_status(client))atomic_store(&failed,true);last_battery=now;}
    if(atomic_load(&online)&&atomic_exchange(&audio_settings_pending,false)&&!send_audio_settings(client))atomic_store(&failed,true);
    if(atomic_load(&online)&&atomic_exchange(&head_calibration_pending,false)&&!send_head_calibration(client))atomic_store(&failed,true);
    if(atomic_load(&online)&&atomic_exchange(&eye_settings_pending,false)&&!send_eye_settings(client))atomic_store(&failed,true);
    if(atomic_load(&online)&&playback_done_id[0]&&marvin_body_audio_drained()){cJSON *done=cJSON_CreateObject();cJSON_AddStringToObject(done,"type","voice_playback_done");cJSON_AddStringToObject(done,"interactionId",playback_done_id);playback_done_id[0]=0;if(!send_json(client,done))atomic_store(&failed,true);}
    if(atomic_load(&online)&&marvin_body_audio_available()){
     unsigned request=atomic_exchange(&voice_request,0);
     if((request==1||request==4)&&!voice_active&&!voice_pending){wake_requested=request==4;if(wake_requested)marvin_motion_wake();voice_pending=true;voice_since=now;marvin_body_capture(true);esp_wifi_set_ps(WIFI_PS_NONE);if(!voice_command(client,"voice_start"))atomic_store(&failed,true);}
     unsigned audio_fault=marvin_body_audio_fault();if(audio_fault)printf("{\"audioFault\":%u}\n",audio_fault);
     if(request==2||audio_fault||(voice_pending&&now-voice_since>20000000)||(voice_active&&now-voice_since>900000000)){
      bool existed=voice_active||voice_pending;voice_stop_local();head_signal(0);if(existed&&!voice_command(client,"voice_stop"))atomic_store(&failed,true);
     }else if((request==3||request==4)&&voice_active){marvin_body_audio_flush();cJSON *m=cJSON_CreateObject();cJSON_AddStringToObject(m,"type","voice_interrupt");if(request==4)cJSON_AddStringToObject(m,"reason","wake");if(!send_json(client,m))atomic_store(&failed,true);}

    }
    atomic_store(&link_stack,uxTaskGetStackHighWaterMark(NULL));
    if(atomic_load(&online)&&now-began>60000000)retry=0;
    vTaskDelay(pdMS_TO_TICKS(20));
   }
   printf("{\"linkFault\":%u,\"rxFrames\":%u,\"rxBinary\":%u,\"rxMaxUs\":%u,\"controlMaxUs\":%u}\n",atomic_load(&link_fault),atomic_load(&rx_frames),atomic_load(&rx_binary),atomic_load(&rx_max_us),atomic_load(&control_max_us));marvin_motion_connection(false);marvin_tracks_stop();if(active_motion_id[0]){motion_record(active_motion_id,3);active_motion_id[0]=0;}voice_stop_local();head_signal(0);atomic_store(&online,false);xSemaphoreTake(uplink_lock,portMAX_DELAY);uplink_client=NULL;xSemaphoreGive(uplink_lock);if(started)esp_websocket_client_stop(client);esp_websocket_client_destroy(client);
  }else{
   atomic_store(&link_state,7);
  }
  mbedtls_platform_zeroize(&identity,sizeof(identity));
  unsigned delay=(1u<<(retry<5?retry:5))*1000+(esp_random()%1000);if(retry<5)retry++;for(unsigned waited=0;waited<delay&&!atomic_load(&quiescing);waited+=100)vTaskDelay(pdMS_TO_TICKS(100));
 }
}
esp_err_t marvin_device_link_start(marvin_link_snapshot_t snapshot,const char *ca){
 if(incoming||!snapshot)return ESP_ERR_INVALID_STATE;
 esp_err_t battery_error=marvin_battery_monitor_init();if(battery_error!=ESP_OK)ESP_LOGW("marvin","Battery ADC unavailable: %s",esp_err_to_name(battery_error));
 esp_err_t ledger_error=nvs_open("motion",NVS_READWRITE,&motion_nvs);
 if(ledger_error==ESP_OK){size_t len=sizeof(motion_ledger);ledger_error=nvs_get_blob(motion_nvs,"ledger",&motion_ledger,&len);if(ledger_error==ESP_ERR_NVS_NOT_FOUND){memset(&motion_ledger,0,sizeof(motion_ledger));motion_ledger.magic=0x4d4f544e;motion_ledger_ready=true;}else motion_ledger_ready=ledger_error==ESP_OK&&len==sizeof(motion_ledger)&&motion_ledger.magic==0x4d4f544e;}
 if(!motion_ledger_ready)ESP_LOGE("marvin","Motion ledger unavailable; remote motion disabled");
 uplink_lock=xSemaphoreCreateMutex();if(!uplink_lock)return ESP_ERR_NO_MEM;
 uint8_t bytes[16];esp_fill_random(bytes,sizeof(bytes));bytes[6]=(bytes[6]&15)|64;bytes[8]=(bytes[8]&63)|128;
 snprintf(boot_id,sizeof(boot_id),"%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",bytes[0],bytes[1],bytes[2],bytes[3],bytes[4],bytes[5],bytes[6],bytes[7],bytes[8],bytes[9],bytes[10],bytes[11],bytes[12],bytes[13],bytes[14],bytes[15]);
 read_identity=snapshot;trusted_ca=ca;
 /* Public trust-anchor fingerprint only; never log device headers or credentials. */
 uint8_t ca_hash[32];size_t ca_size=ca?strlen(ca):0;mbedtls_sha256((const unsigned char*)(ca?ca:""),ca_size,ca_hash,0);
 char ca_hex[65];for(unsigned i=0;i<32;i++)snprintf(ca_hex+2*i,3,"%02x",ca_hash[i]);
 printf("{\"deviceLink\":\"trust\",\"caBytes\":%u,\"caSha256\":\"%s\"}\n",(unsigned)ca_size,ca_hex);
 /* PCM frames must not exhaust the internal RAM needed by Wi-Fi, TLS and DMA. */
 uint8_t *queue_storage=heap_caps_calloc(RECEIVE_FRAMES,sizeof(marvin_frame_t),MALLOC_CAP_SPIRAM|MALLOC_CAP_8BIT);
 if(queue_storage)incoming=xQueueCreateStatic(RECEIVE_FRAMES,sizeof(marvin_frame_t),queue_storage,&incoming_control);
 else incoming=xQueueCreate(RECEIVE_FRAMES,sizeof(marvin_frame_t));
 if(!incoming)return ESP_ERR_NO_MEM;
 if(marvin_body_audio_available()){
  uplink_pcm=heap_caps_calloc(1920,sizeof(int16_t),MALLOC_CAP_SPIRAM|MALLOC_CAP_8BIT);
  if(!uplink_pcm)return ESP_ERR_NO_MEM;
 }
 /* Third-party debug logs can contain HTTP headers; never enable them here. */
 esp_log_level_set("websocket_client",ESP_LOG_WARN);esp_log_level_set("transport_ws",ESP_LOG_WARN);
 if(uplink_pcm&&xTaskCreatePinnedToCore(upload,"body_upload",6144,NULL,5,NULL,1)!=pdPASS)return ESP_ERR_NO_MEM;
 /* The high-priority microphone feed owns core0. Keep network consumers
  * with the socket task on core1 so feed work cannot starve receive queues. */
 if(xTaskCreatePinnedToCore(run,"device_link",6144,NULL,5,NULL,1)!=pdPASS){vQueueDelete(incoming);incoming=NULL;return ESP_ERR_NO_MEM;}
 return ESP_OK;
}
bool marvin_device_link_online(void){return atomic_load(&online);}

void marvin_device_voice_start(void){if(atomic_load(&online)&&marvin_body_audio_available()&&!marvin_body_microphone_muted())atomic_store(&voice_request,1);}
void marvin_device_voice_wake(void){
 if(atomic_load(&online)&&marvin_body_audio_available()&&!marvin_body_microphone_muted()){
  atomic_store(&voice_request,4);
 }
}
void marvin_device_voice_stop(void){marvin_body_capture(false);marvin_body_audio_flush();atomic_store(&voice_request,2);}
void marvin_device_voice_interrupt(void){marvin_body_audio_flush();atomic_store(&voice_request,3);}
void marvin_device_voice_interrupt_wake(void){marvin_body_audio_flush();atomic_store(&voice_request,4);}
void marvin_device_audio_changed(void){atomic_store(&audio_settings_pending,true);if(marvin_body_microphone_muted())marvin_device_voice_stop();}
void marvin_device_link_status(void){printf("{\"uploadStackFree\":%u}\n",atomic_load(&upload_stack));printf("{\"uplink\":{\"online\":%s,\"pcmSamples16k\":%u,\"attempts\":%u,\"maxWriteUs\":%u,\"stackFree\":%u,\"connectionAttempts\":%u,\"state\":%u}}\n",atomic_load(&online)?"true":"false",atomic_load(&pcm_sent),atomic_load(&send_attempts),atomic_load(&max_write_us),atomic_load(&link_stack),atomic_load(&link_attempts),atomic_load(&link_state));}

bool marvin_device_link_quiesce(void){
 if(!incoming)return true;
 atomic_store(&quiescing,true);marvin_tracks_stop();marvin_device_voice_stop();
 for(unsigned i=0;i<1000;i++){if(atomic_load(&link_parked)&&(!uplink_pcm||atomic_load(&upload_parked)))return true;vTaskDelay(pdMS_TO_TICKS(10));}
 return false;
}
