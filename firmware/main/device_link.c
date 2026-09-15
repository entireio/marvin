#include "device_link.h"
#include "device_wire.h"
#include "body_audio.h"
#include "esp_websocket_client.h"
#include "esp_crt_bundle.h"
#include "esp_random.h"
#include "esp_timer.h"
#include "esp_log.h"
#include "esp_heap_caps.h"
#include "esp_wifi.h"
#include "esp_idf_version.h"
#if ESP_IDF_VERSION >= ESP_IDF_VERSION_VAL(5,5,1)
#error "Review and disable WebSocket cross-origin redirects before upgrading ESP-IDF; device bearer credentials must stay on the pinned origin."
#endif
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/queue.h"
#include "cJSON.h"
#include "mbedtls/platform_util.h"
#include "mbedtls/sha256.h"
#include <stdatomic.h>
#include <stdio.h>
#include <string.h>
#include <math.h>
#include <sys/time.h>
#include "freertos/semphr.h"
/* Presence-only runtime: no audio/motion capabilities are advertised until their
 * real drivers and local safety lifecycle are connected. All queues are bounded. */
/* A bounded2.56s PCM ingress window absorbs observed coalesced TCP bursts. */
#define RECEIVE_FRAMES 32
static QueueHandle_t incoming;
static StaticQueue_t incoming_control;
static marvin_link_snapshot_t read_identity;
static const char *trusted_ca;
static atomic_bool connected,failed,online,quiescing,link_parked;
static atomic_uint voice_request,link_fault,rx_frames,rx_binary,rx_max_us,control_max_us;
static void fail(unsigned reason){unsigned zero=0;atomic_compare_exchange_strong(&link_fault,&zero,reason);atomic_store(&failed,true);}
static atomic_uint pcm_sent,max_write_us,send_attempts,link_stack;
static int16_t *uplink_pcm;
static SemaphoreHandle_t uplink_lock;
static esp_websocket_client_handle_t uplink_client;
static atomic_bool uplink_enabled,upload_parked;
static atomic_uint upload_stack;
static bool voice_pending,voice_active,wake_requested;
static int64_t voice_since;
static marvin_wire_t wire;
static marvin_frame_t frame;
static marvin_link_identity_t identity,current;
static char boot_id[37];
static int64_t milliseconds(void){struct timeval t;gettimeofday(&t,NULL);return (int64_t)t.tv_sec*1000+t.tv_usec/1000;}
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
static bool hello(esp_websocket_client_handle_t client){
 cJSON *message=cJSON_CreateObject();if(!message)return false;
 cJSON_AddStringToObject(message,"type","hello");cJSON *protocol=cJSON_AddObjectToObject(message,"protocol");cJSON_AddNumberToObject(protocol,"major",1);cJSON_AddNumberToObject(protocol,"minor",3);
 cJSON_AddStringToObject(message,"deviceId",identity.device_id);cJSON_AddStringToObject(message,"bootId",boot_id);cJSON *caps=cJSON_AddArrayToObject(message,"capabilities");if(marvin_body_audio_available())cJSON_AddItemToArray(caps,cJSON_CreateString("voice"));cJSON_AddNumberToObject(message,"audioInputRate",16000);cJSON_AddStringToObject(message,"firmware","marvin-owner-audio-0.1");return send_json(client,message);
}
static bool uuid(const char *text,uint8_t bytes[16]){
 if(!text||strlen(text)!=36)return false;
 unsigned n=0;
 for(unsigned i=0;i<36;){if(i==8||i==13||i==18||i==23){if(text[i++]!='-')return false;continue;}int a=text[i],b=text[i+1];a=a>='0'&&a<='9'?a-'0':a>='a'&&a<='f'?a-'a'+10:-1;b=b>='0'&&b<='9'?b-'0':b>='a'&&b<='f'?b-'a'+10:-1;if(a<0||b<0||n>=16)return false;bytes[n++]=(uint8_t)(a*16+b);i+=2;}return n==16;
}
static bool voice_command(esp_websocket_client_handle_t client,const char *type){cJSON *m=cJSON_CreateObject();cJSON_AddStringToObject(m,"type",type);if(!strcmp(type,"voice_start"))cJSON_AddStringToObject(m,"reason",wake_requested?"wake":"button");return send_json(client,m);}
static void voice_stop_local(void){atomic_store(&uplink_enabled,false);marvin_body_capture(false);marvin_body_audio_flush();voice_pending=voice_active=false;esp_wifi_set_ps(WIFI_PS_MIN_MODEM);}
static bool receive(bool welcomed){
 if(frame.binary)return welcomed&&marvin_body_audio_available()&&marvin_body_audio_append((const uint8_t*)frame.text,frame.length);
 bool ok=marvin_wire_control(frame.text,identity.epoch,welcomed);
 if(ok){if(!welcomed)atomic_store(&online,true);return true;}
 if(!welcomed||!marvin_body_audio_available())return false;
 cJSON *m=cJSON_ParseWithOpts(frame.text,NULL,true);const cJSON *type=cJSON_GetObjectItemCaseSensitive(m,"type");
 if(cJSON_IsString(type)){
  if(!strcmp(type->valuestring,"voice_ready")&&voice_pending){const cJSON *rate=cJSON_GetObjectItemCaseSensitive(m,"sampleRate"),*channels=cJSON_GetObjectItemCaseSensitive(m,"channels"),*format=cJSON_GetObjectItemCaseSensitive(m,"format");const cJSON *input=cJSON_GetObjectItemCaseSensitive(m,"inputSampleRate");ok=cJSON_IsNumber(input)&&input->valuedouble==16000&&cJSON_IsNumber(rate)&&rate->valuedouble==24000&&cJSON_IsNumber(channels)&&channels->valuedouble==1&&cJSON_IsString(format)&&!strcmp(format->valuestring,"s16le");if(ok){voice_pending=false;voice_active=true;voice_since=esp_timer_get_time();atomic_store(&uplink_enabled,true);printf("{\"voice\":\"listening\"}\n");}}
  else if(!strcmp(type->valuestring,"voice_announcement")&&!voice_active&&!voice_pending){const cJSON *rate=cJSON_GetObjectItemCaseSensitive(m,"sampleRate"),*channels=cJSON_GetObjectItemCaseSensitive(m,"channels"),*format=cJSON_GetObjectItemCaseSensitive(m,"format"),*value=cJSON_GetObjectItemCaseSensitive(m,"interactionId");uint8_t id[16];ok=cJSON_IsNumber(rate)&&rate->valuedouble==24000&&cJSON_IsNumber(channels)&&channels->valuedouble==1&&cJSON_IsString(format)&&!strcmp(format->valuestring,"s16le")&&cJSON_IsString(value)&&uuid(value->valuestring,id);if(ok){marvin_body_capture(false);marvin_body_audio_turn(id);voice_active=true;voice_since=esp_timer_get_time();atomic_store(&uplink_enabled,false);printf("{\"voice\":\"announcing_link\"}\n");}}
  else if(!strcmp(type->valuestring,"voice_turn")&&voice_active){uint8_t id[16];const cJSON *value=cJSON_GetObjectItemCaseSensitive(m,"interactionId");ok=cJSON_IsString(value)&&uuid(value->valuestring,id);if(ok)marvin_body_audio_turn(id);}
  else if(!strcmp(type->valuestring,"audio_flush")){marvin_body_audio_flush();ok=true;}
  else if(!strcmp(type->valuestring,"voice_turn_end")){ok=voice_active;}
  else if(!strcmp(type->valuestring,"voice_closed")||!strcmp(type->valuestring,"voice_error")){voice_stop_local();printf("{\"voice\":\"closed\"}\n");ok=true;}
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
    size_t n=0;for(int part=0;part<12;part++){size_t count=marvin_body_take_input(uplink_pcm+n,1920-n);if(!count)break;n+=count;}
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
  if(!read_identity(&identity)||milliseconds()<1577836800000LL||milliseconds()>=identity.expires_ms){vTaskDelay(pdMS_TO_TICKS(1000));continue;}
  char uri[240],headers[128];
  if(strncmp(identity.origin,"https://",8)||strpbrk(identity.origin+8,"/?#@\r\n")||strpbrk(identity.credential,"\r\n")){vTaskDelay(pdMS_TO_TICKS(1000));continue;}
  snprintf(uri,sizeof(uri),"wss://%.192s/api/device/socket",identity.origin+8);snprintf(headers,sizeof(headers),"Authorization: Bearer %.95s\r\n",identity.credential);
  atomic_store(&connected,false);atomic_store(&failed,false);atomic_store(&link_fault,0);atomic_store(&online,false);xQueueReset(incoming);marvin_wire_reset(&wire);wire.allow_audio=marvin_body_audio_available();voice_stop_local();atomic_store(&voice_request,0);
  esp_websocket_client_config_t config={.uri=uri,.headers=headers,.cert_pem=trusted_ca&&trusted_ca[0]?trusted_ca:NULL,.crt_bundle_attach=trusted_ca&&trusted_ca[0]?NULL:esp_crt_bundle_attach,.disable_auto_reconnect=true,.network_timeout_ms=5000,.task_stack=6144,.task_core_id=1,.buffer_size=4096,.tcp_nodelay=marvin_body_audio_available(),.ping_interval_sec=5,.pingpong_timeout_sec=10};
  esp_websocket_client_handle_t client=esp_websocket_client_init(&config);mbedtls_platform_zeroize(headers,sizeof(headers));
  if(client){
   xSemaphoreTake(uplink_lock,portMAX_DELAY);uplink_client=client;xSemaphoreGive(uplink_lock);
   bool started=esp_websocket_register_events(client,WEBSOCKET_EVENT_ANY,event,NULL)==ESP_OK&&esp_websocket_client_start(client)==ESP_OK;
   if(!started)atomic_store(&failed,true);
   bool sent=false;uint32_t sequence=0;int64_t began=esp_timer_get_time(),last_received=began,last_sent=began;
   while(!atomic_load(&failed)&&!atomic_load(&quiescing)){
    int64_t now=esp_timer_get_time();
    if(!read_identity(&current)||current.epoch!=identity.epoch||strcmp(current.credential,identity.credential)||milliseconds()>=identity.expires_ms){atomic_store(&failed,true);}
    mbedtls_platform_zeroize(&current,sizeof(current));
    if(atomic_load(&connected)&&!sent){sent=hello(client);if(!sent)atomic_store(&failed,true);}
    for(unsigned received=0;received<2&&xQueueReceive(incoming,&frame,0)==pdTRUE;received++){int64_t began=esp_timer_get_time();if(!receive(atomic_load(&online)))fail(frame.binary?5:4);else last_received=now;unsigned elapsed=esp_timer_get_time()-began;if(elapsed>atomic_load(&rx_max_us))atomic_store(&rx_max_us,elapsed);}
    if((!atomic_load(&online)&&now-began>10000000)||(atomic_load(&online)&&now-last_received>15000000))atomic_store(&failed,true);
    if(atomic_load(&online)&&now-last_sent>=5000000){cJSON *m=cJSON_CreateObject();cJSON_AddStringToObject(m,"type","heartbeat");cJSON_AddNumberToObject(m,"seq",sequence++);if(!send_json(client,m))atomic_store(&failed,true);last_sent=now;}
    if(atomic_load(&online)&&marvin_body_audio_available()){
     unsigned request=atomic_exchange(&voice_request,0);
     if((request==1||request==4)&&!voice_active&&!voice_pending){wake_requested=request==4;voice_pending=true;voice_since=now;marvin_body_capture(true);esp_wifi_set_ps(WIFI_PS_NONE);if(!voice_command(client,"voice_start"))atomic_store(&failed,true);}
     unsigned audio_fault=marvin_body_audio_fault();if(audio_fault)printf("{\"audioFault\":%u}\n",audio_fault);
     if(request==2||audio_fault||(voice_pending&&now-voice_since>20000000)||(voice_active&&now-voice_since>900000000)){
      bool existed=voice_active||voice_pending;voice_stop_local();if(existed&&!voice_command(client,"voice_stop"))atomic_store(&failed,true);
     }else if((request==3||request==4)&&voice_active){marvin_body_audio_flush();if(!voice_command(client,"voice_interrupt"))atomic_store(&failed,true);}

    }
    atomic_store(&link_stack,uxTaskGetStackHighWaterMark(NULL));
    if(atomic_load(&online)&&now-began>60000000)retry=0;
    vTaskDelay(pdMS_TO_TICKS(20));
   }
   printf("{\"linkFault\":%u,\"rxFrames\":%u,\"rxBinary\":%u,\"rxMaxUs\":%u,\"controlMaxUs\":%u}\n",atomic_load(&link_fault),atomic_load(&rx_frames),atomic_load(&rx_binary),atomic_load(&rx_max_us),atomic_load(&control_max_us));voice_stop_local();atomic_store(&online,false);xSemaphoreTake(uplink_lock,portMAX_DELAY);uplink_client=NULL;xSemaphoreGive(uplink_lock);if(started)esp_websocket_client_stop(client);esp_websocket_client_destroy(client);
  }
  mbedtls_platform_zeroize(&identity,sizeof(identity));
  unsigned delay=(1u<<(retry<5?retry:5))*1000+(esp_random()%1000);if(retry<5)retry++;for(unsigned waited=0;waited<delay&&!atomic_load(&quiescing);waited+=100)vTaskDelay(pdMS_TO_TICKS(100));
 }
}
esp_err_t marvin_device_link_start(marvin_link_snapshot_t snapshot,const char *ca){
 if(incoming||!snapshot)return ESP_ERR_INVALID_STATE;
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

void marvin_device_voice_start(void){if(atomic_load(&online)&&marvin_body_audio_available())atomic_store(&voice_request,1);}
void marvin_device_voice_wake(void){if(atomic_load(&online)&&marvin_body_audio_available())atomic_store(&voice_request,4);}
void marvin_device_voice_stop(void){marvin_body_capture(false);marvin_body_audio_flush();atomic_store(&voice_request,2);}
void marvin_device_voice_interrupt(void){marvin_body_audio_flush();atomic_store(&voice_request,3);}
void marvin_device_link_status(void){printf("{\"uploadStackFree\":%u}\n",atomic_load(&upload_stack));printf("{\"uplink\":{\"online\":%s,\"pcmSamples16k\":%u,\"attempts\":%u,\"maxWriteUs\":%u,\"stackFree\":%u}}\n",atomic_load(&online)?"true":"false",atomic_load(&pcm_sent),atomic_load(&send_attempts),atomic_load(&max_write_us),atomic_load(&link_stack));}

bool marvin_device_link_quiesce(void){
 if(!incoming)return true;
 atomic_store(&quiescing,true);marvin_device_voice_stop();
 for(unsigned i=0;i<1000;i++){if(atomic_load(&link_parked)&&(!uplink_pcm||atomic_load(&upload_parked)))return true;vTaskDelay(pdMS_TO_TICKS(10));}
 return false;
}
