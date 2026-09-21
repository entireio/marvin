#pragma once
#include <stdint.h>
#include <stddef.h>
typedef int esp_err_t;
typedef unsigned esp_ota_handle_t;
typedef struct {uint32_t address,size;} esp_partition_t;
typedef enum {ESP_OTA_IMG_PENDING_VERIFY,ESP_OTA_IMG_VALID} esp_ota_img_states_t;
#define ESP_OK 0
const esp_partition_t *esp_ota_get_running_partition(void);
const esp_partition_t *esp_ota_get_next_update_partition(const esp_partition_t *p);
int esp_ota_begin(const esp_partition_t*,size_t,esp_ota_handle_t*);
int esp_ota_write(esp_ota_handle_t,const void*,size_t);
int esp_ota_end(esp_ota_handle_t);
int esp_ota_abort(esp_ota_handle_t);
int esp_ota_set_boot_partition(const esp_partition_t*);
int esp_ota_get_state_partition(const esp_partition_t*,esp_ota_img_states_t*);
int esp_ota_mark_app_valid_cancel_rollback(void);
int esp_partition_read(const esp_partition_t*,size_t,void*,size_t);
int esp_ota_mark_app_invalid_rollback_and_reboot(void);

#include <stdbool.h>
bool esp_ota_check_rollback_is_possible(void);
