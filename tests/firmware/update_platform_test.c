#include "marvin_update_esp.h"
#include "nvs.h"
#include <assert.h>
#include <string.h>
#include <stdio.h>
static bool rollback_available=true;
static esp_partition_t slots[2]={{0x20000,4096},{0x200000,4096}};
static unsigned running,selected,valid,aborts,floor_seq,staged_floor,commit_fail,begin_fail,finish_fail;
static uint8_t flash[4096],pending[48],staged[48];static size_t pending_size,staged_size;
const esp_partition_t *esp_ota_get_running_partition(void){return &slots[running];}
const esp_partition_t *esp_ota_get_next_update_partition(const esp_partition_t *p){(void)p;return &slots[1-running];}
int esp_ota_begin(const esp_partition_t *p,size_t bytes,esp_ota_handle_t *h){assert(p!=&slots[running]&&bytes<=4096);*h=1;return begin_fail?1:0;}
int esp_ota_write(esp_ota_handle_t h,const void *p,size_t n){(void)h;memcpy(flash,p,n);return 0;}
int esp_ota_end(esp_ota_handle_t h){(void)h;return finish_fail?1:0;}
int esp_ota_abort(esp_ota_handle_t h){(void)h;aborts++;return 0;}
bool esp_ota_check_rollback_is_possible(void){return rollback_available;}
int esp_ota_set_boot_partition(const esp_partition_t *p){assert(pending_size==48);selected=p==&slots[1];return 0;}
int esp_ota_get_state_partition(const esp_partition_t *p,esp_ota_img_states_t *s){(void)p;*s=valid?ESP_OTA_IMG_VALID:ESP_OTA_IMG_PENDING_VERIFY;return 0;}
int esp_ota_mark_app_valid_cancel_rollback(void){valid=1;return 0;}
int esp_partition_read(const esp_partition_t *p,size_t at,void *out,size_t n){(void)p;memcpy(out,flash+at,n);return 0;}
int nvs_open(const char *name,int mode,nvs_handle_t *h){(void)name;(void)mode;*h=1;memcpy(staged,pending,48);staged_size=pending_size;staged_floor=floor_seq;return 0;}
void nvs_close(nvs_handle_t h){(void)h;}
int nvs_set_blob(nvs_handle_t h,const char *key,const void *p,size_t n){(void)h;(void)key;assert(n==48);memcpy(staged,p,n);staged_size=n;return 0;}
int nvs_get_blob(nvs_handle_t h,const char *key,void *p,size_t *n){(void)h;(void)key;if(!pending_size)return ESP_ERR_NVS_NOT_FOUND;if(!p){*n=pending_size;return 0;}assert(*n>=pending_size);memcpy(p,pending,pending_size);*n=pending_size;return 0;}
int nvs_get_u32(nvs_handle_t h,const char *key,uint32_t *n){(void)h;(void)key;*n=floor_seq;return 0;}
int nvs_set_u32(nvs_handle_t h,const char *key,uint32_t n){(void)h;(void)key;staged_floor=n;return 0;}
int nvs_erase_key(nvs_handle_t h,const char *key){(void)h;(void)key;staged_size=0;return 0;}
int nvs_commit(nvs_handle_t h){(void)h;if(commit_fail)return 1;memcpy(pending,staged,48);pending_size=staged_size;floor_seq=staged_floor;return 0;}
static void reset(void){running=selected=valid=aborts=floor_seq=staged_floor=commit_fail=begin_fail=finish_fail=0;pending_size=staged_size=0;valid=1;rollback_available=true;memset(flash,7,sizeof(flash));}
static marvin_update_writer_t prepare(void){marvin_update_esp_t *c;static marvin_update_esp_t context;c=&context;marvin_update_writer_t w;assert(marvin_update_esp_writer(c,&w));assert(w.begin(c,64));assert(w.write(c,flash,64));assert(w.finish(c));return w;}
static marvin_update_image_t image(void){marvin_update_image_t i={.sequence=1,.image_bytes=64};assert(!mbedtls_sha256(flash,64,i.image_sha256,0));return i;}
int main(void){for(int trial=0;trial<10;trial++){
 reset();valid=0;assert(!marvin_update_esp_confirm(true));marvin_update_esp_t unavailable;marvin_update_writer_t refused;assert(!marvin_update_esp_writer(&unavailable,&refused));valid=1;marvin_update_writer_t w=prepare();marvin_update_image_t i=image();assert(w.select(w.context,&i));assert(selected==1&&!floor_seq);assert(!marvin_update_esp_confirm(false));assert(!floor_seq);running=1;valid=0;assert(marvin_update_esp_confirm(true));assert(valid&&floor_seq==1&&!pending_size);assert(marvin_update_esp_confirm(true));
 reset();w=prepare();i=image();commit_fail=1;assert(!w.select(w.context,&i));assert(!selected&&!floor_seq);
 reset();w=prepare();i=image();assert(w.select(w.context,&i));running=1;valid=0;commit_fail=1;assert(!marvin_update_esp_confirm(true));assert(valid&&!floor_seq&&pending_size);commit_fail=0;assert(marvin_update_esp_confirm(true));assert(floor_seq==1&&!pending_size);
 reset();w=prepare();i=image();assert(w.select(w.context,&i));running=1;valid=0;flash[0]^=1;assert(!marvin_update_esp_confirm(true));assert(!valid&&!floor_seq);
 reset();w=prepare();i=image();assert(w.select(w.context,&i));valid=0;assert(!marvin_update_esp_confirm(true));valid=1;assert(marvin_update_esp_confirm(true));assert(!floor_seq&&valid); /* Old usable image after rollback. */
 reset();w=prepare();i=image();floor_seq=1;assert(!w.select(w.context,&i));assert(!selected);
 }
 puts("OTA platform fixture: ten repetitions of confirmation, failed persistence, power-cut recovery, image mismatch, rollback and stale-sequence rejection passed. Not a physical OTA test.");return 0;
}
