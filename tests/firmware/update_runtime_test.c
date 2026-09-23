/* Compile the real coordinator against deterministic platform boundaries. */
#include <assert.h>
#include <setjmp.h>
#include <limits.h>
#include "../../firmware/main/ota_runtime.c"
static jmp_buf exit_task;
static int mode,boot_state,order,downloads,restarts,rollbacks,confirmed,reads;
static unsigned elapsed;
static esp_partition_t partition={0x200000,0x1e0000};
static bool identity_read(marvin_link_identity_t *out){reads++;memset(out,0,sizeof(*out));strcpy(out->origin,"https://marvin.test");strcpy(out->device_id,"fixture");memset(out->credential,'a',43);out->epoch=mode==8&&reads>=3?2:1;out->expires_ms=INT64_MAX;return true;}
bool marvin_owner_setup_linked(void){return mode!=25;}
int xTaskCreate(void (*task)(void*),const char *name,unsigned stack,void *arg,unsigned priority,void *handle){(void)task;(void)name;(void)arg;(void)priority;(void)handle;assert(stack>=10240);return pdPASS;}
void vTaskDelay(unsigned ticks){elapsed+=ticks;if(boot_state<0&&!atomic_load(&busy))longjmp(exit_task,1);}
_Noreturn void esp_restart(void){restarts++;longjmp(exit_task,2);}
const esp_partition_t *esp_ota_get_running_partition(void){return &partition;}
const esp_partition_t *esp_ota_get_next_update_partition(const esp_partition_t *p){(void)p;return &partition;}
int esp_ota_get_state_partition(const esp_partition_t *p,esp_ota_img_states_t *state){(void)p;if(boot_state<0)return 1;*state=(esp_ota_img_states_t)boot_state;return 0;}
int esp_ota_mark_app_invalid_rollback_and_reboot(void){rollbacks++;return 1;}
bool marvin_device_link_online(void){return mode!=20&&mode!=25;}
bool marvin_body_health_ready(void){return mode!=21;}
uint32_t marvin_body_health_progress(void){return mode==22?0:elapsed+1;}
bool marvin_update_esp_confirm(bool healthy){assert(healthy);confirmed++;if(mode==24)boot_state=ESP_OTA_IMG_VALID;return mode!=23&&mode!=24&&mode!=25;}
bool marvin_update_factory_trust(char *buffer,size_t capacity,const char *layout,uint32_t bytes,marvin_update_trust_t *trust){assert(capacity==1024&&!strcmp(layout,"afe-v1")&&bytes==partition.size);strcpy(buffer,"public-fixture");*trust=(marvin_update_trust_t){buffer,"waveshare-esp32s3-audio",layout,3,bytes};return mode!=1;}
bool marvin_device_link_quiesce(void){assert(order==0);order=1;return mode!=3;}
void marvin_motion_idle_enabled(bool enabled){assert(!enabled);}
bool marvin_body_quiesce(void){assert(order==1);order=2;return mode!=4;}
bool marvin_update_esp_writer(marvin_update_esp_t *context,marvin_update_writer_t *writer){(void)context;memset(writer,0,sizeof(*writer));assert(order==2);order=3;return mode!=5;}
bool marvin_update_download(const char *origin,const char *token,const char *ca,uint32_t sequence,const marvin_update_trust_t *trust,marvin_update_writer_t writer,bool (*cancel)(void*),void *context){(void)writer;assert(order==3&&!strcmp(origin,"https://marvin.test")&&strlen(token)==43&&!strcmp(ca,"fixture-ca")&&sequence==4&&trust->committed_sequence==3);downloads++;if(mode==7)marvin_ota_cancel();return !cancel(context)&&mode!=6;}
bool marvin_update_factory_bootstrap(uint8_t *manifest){(void)manifest;return mode==25;}
bool marvin_update_esp_bootstrap(bool healthy,const uint8_t *manifest,size_t bytes,const marvin_update_trust_t *trust){(void)manifest;(void)bytes;(void)trust;return healthy&&mode==25;}
static void reset(int m,int state){mode=m;boot_state=state;order=downloads=restarts=rollbacks=confirmed=reads=0;elapsed=0;atomic_store(&requested,0);atomic_store(&busy,false);atomic_store(&cancel_requested,false);snapshot=NULL;certificate=NULL;assert(marvin_ota_start(identity_read,"fixture-ca")==ESP_OK);}
int main(void){
 for(int trial=0;trial<10;trial++)for(int m=0;m<=8;m++){
  reset(m,-1);assert(!marvin_ota_request(0));assert(marvin_ota_request(4));assert(!marvin_ota_request(5));if(m==2)marvin_ota_cancel();
  if(!setjmp(exit_task))run(NULL);
  if(m==1||m==2){assert(!order&&!downloads&&!restarts&&!atomic_load(&busy));}
  else{assert(restarts==1);if(m==3||m==4||m==5||m==8)assert(!downloads);else assert(downloads==1);}
 }
 reset(0,ESP_OTA_IMG_PENDING_VERIFY);confirm_boot();assert(confirmed==1&&elapsed==5000&&!restarts);
 for(int m=20;m<=23;m++){reset(m,ESP_OTA_IMG_PENDING_VERIFY);if(!setjmp(exit_task))confirm_boot();assert(elapsed==90000&&rollbacks==1&&restarts==1);}
 reset(24,ESP_OTA_IMG_PENDING_VERIFY);confirm_boot();assert(elapsed==90000&&!rollbacks&&!restarts);
 reset(25,ESP_OTA_IMG_PENDING_VERIFY);confirm_boot();assert(elapsed==5000&&confirmed==1&&!rollbacks&&!restarts);
 reset(20,ESP_OTA_IMG_VALID);confirm_boot();assert(elapsed==90000&&!rollbacks&&!restarts);
 reset(0,ESP_OTA_IMG_VALID);confirm_boot();assert(confirmed==1&&!restarts);
 puts("Update coordinator fixture: 90 admission/cancel/shutdown/download trials plus stable trial boot, four failed health modes and valid-image reconciliation passed. No physical OTA claim.");
}
