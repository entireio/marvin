#include "marvin_update_esp.h"
#include "nvs.h"
#include "sdkconfig.h"
#include <string.h>
#define PENDING_MAGIC 0x4d525531u
typedef struct {uint32_t magic,address,sequence,bytes;uint8_t digest[32];} pending_t;
static bool save_pending(const pending_t *pending){nvs_handle_t nvs;if(nvs_open("marvin_update",NVS_READWRITE,&nvs)!=ESP_OK)return false;bool ok=nvs_set_blob(nvs,"pending",pending,sizeof(*pending))==ESP_OK&&nvs_commit(nvs)==ESP_OK;nvs_close(nvs);return ok;}
bool marvin_update_esp_sequence(uint32_t *sequence){
 if(!sequence)return false;
 *sequence=0;nvs_handle_t nvs;esp_err_t err=nvs_open("marvin_update",NVS_READONLY,&nvs);if(err==ESP_ERR_NVS_NOT_FOUND)return true;if(err!=ESP_OK)return false;
 err=nvs_get_u32(nvs,"sequence",sequence);nvs_close(nvs);return err==ESP_OK||err==ESP_ERR_NVS_NOT_FOUND;
}
static bool begin(void *context,uint32_t bytes){marvin_update_esp_t *c=context;const esp_partition_t *running=esp_ota_get_running_partition();
 if(c->opened||!running||!c->partition||c->partition->address==running->address||bytes>c->partition->size)return false;
 c->validated=false;c->opened=esp_ota_begin(c->partition,bytes,&c->handle)==ESP_OK;return c->opened;
}
static bool write_image(void *context,const uint8_t *data,size_t bytes){marvin_update_esp_t *c=context;return c->opened&&esp_ota_write(c->handle,data,bytes)==ESP_OK;}
static bool finish(void *context){marvin_update_esp_t *c=context;if(!c->opened)return false;c->opened=false;c->validated=esp_ota_end(c->handle)==ESP_OK;return c->validated;}
static bool select_image(void *context,const marvin_update_image_t *image){
 marvin_update_esp_t *c=context;uint32_t committed;if(!c->validated||!marvin_update_esp_sequence(&committed)||image->sequence<=committed)return false;
 pending_t pending={.magic=PENDING_MAGIC,.address=c->partition->address,.sequence=image->sequence,.bytes=image->image_bytes};memcpy(pending.digest,image->image_sha256,32);
 /* Persist first. A power cut before boot selection leaves a harmless record
  * for a non-running slot, never an advanced anti-downgrade floor. */
 return save_pending(&pending)&&esp_ota_set_boot_partition(c->partition)==ESP_OK;
}
static void abort_image(void *context){marvin_update_esp_t *c=context;if(c->opened){esp_ota_abort(c->handle);c->opened=false;}c->validated=false;}
bool marvin_update_esp_writer(marvin_update_esp_t *context,marvin_update_writer_t *writer){
 if(!context||!writer)return false;
 memset(context,0,sizeof(*context));memset(writer,0,sizeof(*writer));
#ifndef CONFIG_BOOTLOADER_APP_ROLLBACK_ENABLE
 return false;
#endif
 const esp_partition_t *running=esp_ota_get_running_partition();esp_ota_img_states_t state;
 if(!running||esp_ota_get_state_partition(running,&state)!=ESP_OK||state!=ESP_OTA_IMG_VALID)return false;
 context->partition=esp_ota_get_next_update_partition(NULL);if(!context->partition)return false;
 *writer=(marvin_update_writer_t){.context=context,.begin=begin,.write=write_image,.finish=finish,.select=select_image,.abort=abort_image};return true;
}
bool marvin_update_esp_confirm(bool healthy){
 if(!healthy)return false;
 const esp_partition_t *running=esp_ota_get_running_partition();if(!running)return false;
 nvs_handle_t nvs;if(nvs_open("marvin_update",NVS_READWRITE,&nvs)!=ESP_OK)return false;
 pending_t pending;size_t size=sizeof(pending);esp_err_t err=nvs_get_blob(nvs,"pending",&pending,&size);
 if(err==ESP_ERR_NVS_NOT_FOUND){nvs_close(nvs);esp_ota_img_states_t state;return esp_ota_get_state_partition(running,&state)==ESP_OK&&state==ESP_OTA_IMG_VALID;}
 if(err!=ESP_OK||size!=sizeof(pending)||pending.magic!=PENDING_MAGIC||!pending.bytes||pending.bytes>running->size){nvs_close(nvs);return false;}
 if(pending.address!=running->address){nvs_close(nvs);esp_ota_img_states_t state;return esp_ota_get_state_partition(running,&state)==ESP_OK&&state==ESP_OTA_IMG_VALID;} /* Prior usable image after rollback. */
 uint32_t committed=0;err=nvs_get_u32(nvs,"sequence",&committed);if((err!=ESP_OK&&err!=ESP_ERR_NVS_NOT_FOUND)||pending.sequence<committed){nvs_close(nvs);return false;}
 uint8_t block[512],digest[32];mbedtls_sha256_context hash;mbedtls_sha256_init(&hash);bool ok=mbedtls_sha256_starts(&hash,0)==0;
 for(size_t at=0;ok&&at<pending.bytes;){size_t bytes=pending.bytes-at;if(bytes>sizeof(block))bytes=sizeof(block);ok=esp_partition_read(running,at,block,bytes)==ESP_OK&&mbedtls_sha256_update(&hash,block,bytes)==0;at+=bytes;}
 ok=ok&&mbedtls_sha256_finish(&hash,digest)==0&&!memcmp(digest,pending.digest,32);mbedtls_sha256_free(&hash);
 esp_ota_img_states_t state;if(ok)ok=esp_ota_get_state_partition(running,&state)==ESP_OK;
 if(ok&&state==ESP_OTA_IMG_PENDING_VERIFY)ok=esp_ota_mark_app_valid_cancel_rollback()==ESP_OK;
 else if(ok)ok=state==ESP_OTA_IMG_VALID;
 /* Confirmation precedes floor advancement. If this commit loses power, the
  * pending digest is checked again on the next boot, including VALID images. */
 if(ok)ok=nvs_set_u32(nvs,"sequence",pending.sequence)==ESP_OK&&nvs_erase_key(nvs,"pending")==ESP_OK&&nvs_commit(nvs)==ESP_OK;
 nvs_close(nvs);return ok;
}
bool marvin_update_esp_bootstrap(bool healthy,const uint8_t *manifest,size_t bytes,const marvin_update_trust_t *trust){
 if(!healthy||!trust)return false;
 uint32_t committed;if(!marvin_update_esp_sequence(&committed)||committed!=trust->committed_sequence)return false;
 const esp_partition_t *running=esp_ota_get_running_partition();esp_ota_img_states_t state;
 if(!running||esp_ota_get_state_partition(running,&state)!=ESP_OK||state!=ESP_OTA_IMG_PENDING_VERIFY||!esp_ota_check_rollback_is_possible())return false;
 marvin_update_image_t image;if(!marvin_update_verify(manifest,bytes,trust,&image)||image.sequence<=committed||image.image_bytes>running->size)return false;
 nvs_handle_t nvs;if(nvs_open("marvin_update",NVS_READWRITE,&nvs)!=ESP_OK)return false;
 size_t existing=0;esp_err_t err=nvs_get_blob(nvs,"pending",NULL,&existing);nvs_close(nvs);if(err!=ESP_ERR_NVS_NOT_FOUND)return false;
 /* Verify current flash before creating any durable bootstrap claim. */
 uint8_t chunk[512],digest[32];mbedtls_sha256_context hash;mbedtls_sha256_init(&hash);bool ok=mbedtls_sha256_starts(&hash,0)==0;
 for(size_t at=0;ok&&at<image.image_bytes;){size_t n=image.image_bytes-at;if(n>sizeof(chunk))n=sizeof(chunk);ok=esp_partition_read(running,at,chunk,n)==ESP_OK&&mbedtls_sha256_update(&hash,chunk,n)==0;at+=n;}
 ok=ok&&mbedtls_sha256_finish(&hash,digest)==0&&!memcmp(digest,image.image_sha256,32);mbedtls_sha256_free(&hash);if(!ok)return false;
 pending_t pending={.magic=PENDING_MAGIC,.address=running->address,.sequence=image.sequence,.bytes=image.image_bytes};memcpy(pending.digest,image.image_sha256,32);
 return save_pending(&pending)&&marvin_update_esp_confirm(true);
}
