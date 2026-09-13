#pragma once
#include <stddef.h>
#include <stdint.h>
typedef int nvs_handle_t;
#define NVS_READWRITE 1
#define NVS_READONLY 0
#define ESP_ERR_NVS_NOT_FOUND 2
int nvs_open(const char*,int,nvs_handle_t*);
void nvs_close(nvs_handle_t);
int nvs_set_blob(nvs_handle_t,const char*,const void*,size_t);
int nvs_get_blob(nvs_handle_t,const char*,void*,size_t*);
int nvs_get_u32(nvs_handle_t,const char*,uint32_t*);
int nvs_set_u32(nvs_handle_t,const char*,uint32_t);
int nvs_erase_key(nvs_handle_t,const char*);
int nvs_commit(nvs_handle_t);

int nvs_open_from_partition(const char*,const char*,int,nvs_handle_t*);
