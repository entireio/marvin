#pragma once
#include "esp_err.h"
#include <stdbool.h>
typedef struct {int event_id,data_len;void *user_data,*data;} esp_http_client_event_t;
typedef struct {const char *url,*cert_pem;int method,timeout_ms;bool disable_auto_redirect;esp_err_t (*event_handler)(esp_http_client_event_t*);void *user_data;esp_err_t (*crt_bundle_attach)(void*);} esp_http_client_config_t;
typedef void *esp_http_client_handle_t;
#define HTTP_EVENT_ON_DATA 1
#define HTTP_METHOD_POST 1
esp_http_client_handle_t esp_http_client_init(const esp_http_client_config_t*);
esp_err_t esp_http_client_set_header(esp_http_client_handle_t,const char*,const char*);
esp_err_t esp_http_client_set_post_field(esp_http_client_handle_t,const char*,int);
esp_err_t esp_http_client_perform(esp_http_client_handle_t);
int esp_http_client_get_status_code(esp_http_client_handle_t);
esp_err_t esp_http_client_cleanup(esp_http_client_handle_t);
