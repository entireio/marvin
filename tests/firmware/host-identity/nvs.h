#pragma once
#include <stddef.h>
#include <stdbool.h>
#include "esp_err.h"
typedef int nvs_handle_t;
#define NVS_READONLY 0
esp_err_t nvs_open_from_partition(const char*,const char*,int,nvs_handle_t*);
esp_err_t nvs_get_blob(nvs_handle_t,const char*,void*,size_t*);
esp_err_t nvs_get_str(nvs_handle_t,const char*,char*,size_t*);
void nvs_close(nvs_handle_t);
