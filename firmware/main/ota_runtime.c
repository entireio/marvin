#include "ota_runtime.h"
#include "sdkconfig.h"
#ifdef CONFIG_MARVIN_SIGNED_OTA
#ifndef CONFIG_BOOTLOADER_APP_ROLLBACK_ENABLE
#error "Signed OTA requires a rollback-enabled bootloader"
#endif
#include "body_audio.h"
#include "motion_controller.h"
#include "marvin_update_factory.h"
#include "marvin_update_download.h"
#include "marvin_update_esp.h"
#include "esp_system.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "mbedtls/platform_util.h"
#include <stdatomic.h>
#include <inttypes.h>
#include <stdio.h>
#include <string.h>
#include <sys/time.h>
static atomic_uint requested;
static atomic_bool busy,cancel_requested;
static bool bootstrap_failure_reported;
static marvin_link_snapshot_t snapshot;
static const char *certificate;
static int64_t now_ms(void){struct timeval now;gettimeofday(&now,NULL);return (int64_t)now.tv_sec*1000+now.tv_usec/1000;}
static bool cancelled(void *context){
 const marvin_link_identity_t *original=context;marvin_link_identity_t latest={0};
 bool cancel=atomic_load(&cancel_requested)||!snapshot(&latest)||latest.epoch!=original->epoch||strcmp(latest.device_id,original->device_id)||strcmp(latest.origin,original->origin)||strcmp(latest.credential,original->credential)||now_ms()>=latest.expires_ms;
 mbedtls_platform_zeroize(&latest,sizeof(latest));return cancel;
}
static bool confirm_initial_boot(void){
 const esp_partition_t *running=esp_ota_get_running_partition();if(!running)return false;
 char key[1024];uint8_t manifest[MARVIN_UPDATE_MANIFEST_BYTES];marvin_update_trust_t trust;
#ifdef CONFIG_MARVIN_LOCAL_AFE
 #ifdef CONFIG_MARVIN_AFE_LAYOUT_V2
 const char *layout="afe-v2";
 #else
 const char *layout="afe-v1";
 #endif
#else
 const char *layout="owner-v1";
#endif
 bool factory_ok=marvin_update_factory_trust(key,sizeof(key),layout,running->size,&trust);
 bool manifest_ok=factory_ok&&marvin_update_factory_bootstrap(manifest);
 bool ok=manifest_ok&&marvin_update_esp_bootstrap(true,manifest,sizeof(manifest),&trust);
 if(!ok&&!bootstrap_failure_reported){printf("{\"update\":\"bootstrap_refused\",\"factory\":%s,\"manifest\":%s,\"sequence\":%" PRIu32 "}\n",factory_ok?"true":"false",manifest_ok?"true":"false",trust.committed_sequence);bootstrap_failure_reported=true;}
 mbedtls_platform_zeroize(key,sizeof(key));return ok;
}
static void confirm_boot(void){
 const esp_partition_t *running=esp_ota_get_running_partition();esp_ota_img_states_t state;
 if(!running||esp_ota_get_state_partition(running,&state)!=ESP_OK||(state!=ESP_OTA_IMG_PENDING_VERIFY&&state!=ESP_OTA_IMG_VALID))return;
 bool trial=state==ESP_OTA_IMG_PENDING_VERIFY;
 unsigned stable=0;uint32_t previous=marvin_body_health_progress();
#ifdef CONFIG_MARVIN_OTA_TEST_REJECT_BOOT
 printf("{\"update\":\"trial_health_reject_fixture\"}\n");
#endif
 for(unsigned i=0;i<180;i++){
  vTaskDelay(pdMS_TO_TICKS(500));uint32_t progress=marvin_body_health_progress();
  bool healthy=marvin_device_link_online()&&marvin_body_health_ready();
#ifdef CONFIG_MARVIN_LOCAL_AFE
  healthy=healthy&&progress>previous;
#endif
#ifdef CONFIG_MARVIN_OTA_TEST_REJECT_BOOT
  healthy=false;
#endif
  previous=progress;stable=healthy?stable+1:0;
  if(stable>=10&&(marvin_update_esp_confirm(true)||(trial&&confirm_initial_boot()))){printf("{\"update\":\"boot_confirmed\"}\n");return;}
 }
 esp_ota_img_states_t latest;
 if(trial&&esp_ota_get_state_partition(running,&latest)==ESP_OK&&latest==ESP_OTA_IMG_VALID)trial=false;
 if(trial){printf("{\"update\":\"boot_health_failed\"}\n");esp_ota_mark_app_invalid_rollback_and_reboot();esp_restart();}
 else printf("{\"update\":\"metadata_reconciliation_pending\"}\n");
}
static void run(void *unused){
 (void)unused;confirm_boot();
 for(;;){uint32_t sequence=atomic_exchange(&requested,0);if(!sequence){vTaskDelay(pdMS_TO_TICKS(100));continue;}
  char public_key[1024];marvin_update_trust_t trust;marvin_link_identity_t identity={0};
  const esp_partition_t *slot=esp_ota_get_next_update_partition(NULL);
#ifdef CONFIG_MARVIN_LOCAL_AFE
  #ifdef CONFIG_MARVIN_AFE_LAYOUT_V2
  const char *layout="afe-v2";
  #else
  const char *layout="afe-v1";
  #endif
#else
  const char *layout="owner-v1";
#endif
  bool ready=slot&&snapshot(&identity)&&now_ms()<identity.expires_ms&&marvin_device_link_online()&&marvin_update_factory_trust(public_key,sizeof(public_key),layout,slot->size,&trust)&&sequence>trust.committed_sequence;
  if(!ready||cancelled(&identity)){printf("{\"update\":\"preflight_refused\"}\n");mbedtls_platform_zeroize(&identity,sizeof(identity));mbedtls_platform_zeroize(public_key,sizeof(public_key));atomic_store(&busy,false);continue;}
  printf("{\"update\":\"quiescing\"}\n");marvin_motion_idle_enabled(false);bool quiet=marvin_device_link_quiesce()&&marvin_body_quiesce();
  marvin_update_esp_t platform;marvin_update_writer_t writer;
  bool selected=quiet&&!cancelled(&identity)&&marvin_update_esp_writer(&platform,&writer)&&marvin_update_download(identity.origin,identity.credential,certificate,sequence,&trust,writer,cancelled,&identity);
  mbedtls_platform_zeroize(&identity,sizeof(identity));mbedtls_platform_zeroize(public_key,sizeof(public_key));
  printf("{\"update\":\"%s\"}\n",selected?"selected":"failed");
  /* Shutdown is one-way. Failure restarts the prior selected image. */
  vTaskDelay(pdMS_TO_TICKS(100));esp_restart();
 }
}
esp_err_t marvin_ota_start(marvin_link_snapshot_t read,const char *ca){if(snapshot||!read)return ESP_ERR_INVALID_STATE;snapshot=read;certificate=ca;return xTaskCreate(run,"signed_update",10240,NULL,3,NULL)==pdPASS?ESP_OK:ESP_ERR_NO_MEM;}
bool marvin_ota_request(uint32_t sequence){bool expected=false;if(!snapshot||!sequence||!atomic_compare_exchange_strong(&busy,&expected,true))return false;atomic_store(&cancel_requested,false);atomic_store(&requested,sequence);return true;}
void marvin_ota_cancel(void){atomic_store(&cancel_requested,true);}
#else
esp_err_t marvin_ota_start(marvin_link_snapshot_t snapshot,const char *ca){(void)snapshot;(void)ca;return ESP_ERR_NOT_SUPPORTED;}
bool marvin_ota_request(uint32_t sequence){(void)sequence;return false;}
void marvin_ota_cancel(void){}
#endif
