#include "enrollment_client.h"
#include "device_identity.h"
#include "esp_http_client.h"
#include "esp_crt_bundle.h"
#include "mbedtls/platform_util.h"
#include "cJSON.h"
#include <string.h>
#include <stdio.h>
#include <math.h>
#include <stdlib.h>
typedef struct {char data[1024];size_t length;bool overflow;} response_t;
static bool receipt_shape(const cJSON *data){
 if(!cJSON_IsObject(data))return false;
 for(const cJSON *a=data->child;a;a=a->next){
  if(!a->string)return false;
  if(strcmp(a->string,"linked")&&strcmp(a->string,"deviceId")&&strcmp(a->string,"epoch")&&strcmp(a->string,"credential")&&strcmp(a->string,"credentialExpiresAt"))return false;
  for(const cJSON *b=a->next;b;b=b->next)if(b->string&&!strcmp(a->string,b->string))return false;
 }
 return true;
}
static esp_err_t collect(esp_http_client_event_t *event){
 response_t *r=event->user_data;
 if(event->event_id==HTTP_EVENT_ON_DATA){if(event->data_len<0||(size_t)event->data_len>=sizeof(r->data)-r->length){r->overflow=true;return ESP_FAIL;}memcpy(r->data+r->length,event->data,event->data_len);r->length+=event->data_len;r->data[r->length]=0;}
 return ESP_OK;
}
marvin_redeem_status_t marvin_enrollment_redeem(const char *origin,const char *ca,const char *ticket,const char *network,uint32_t epoch,bool claim,marvin_redeem_receipt_t *receipt){
 if(!receipt)return MARVIN_REDEEM_INVALID;
 memset(receipt,0,sizeof(*receipt));
 if(!origin||strncmp(origin,"https://",8)||strlen(origin)>200||strpbrk(origin+8,"/?#@")||!epoch||!marvin_identity_device_id())return MARVIN_REDEEM_INVALID;
 char proof[100],url[256];if(marvin_identity_redemption_proof(ticket,network,proof)!=ESP_OK)return MARVIN_REDEEM_INVALID;
 snprintf(url,sizeof(url),"%s/api/device/enrollment/redeem",origin);
 cJSON *body=cJSON_CreateObject();if(!body)return MARVIN_REDEEM_RETRY;
 cJSON_AddStringToObject(body,"ticket",ticket);cJSON_AddStringToObject(body,"network",network);cJSON_AddStringToObject(body,"proof",proof);
 char *serialized=cJSON_PrintUnformatted(body);cJSON_Delete(body);if(!serialized)return MARVIN_REDEEM_RETRY;
 response_t response={0};esp_http_client_config_t config={.url=url,.method=HTTP_METHOD_POST,.timeout_ms=15000,.disable_auto_redirect=true,.event_handler=collect,.user_data=&response,.cert_pem=ca&&ca[0]?ca:NULL,.crt_bundle_attach=ca&&ca[0]?NULL:esp_crt_bundle_attach};
 esp_http_client_handle_t client=esp_http_client_init(&config);marvin_redeem_status_t status=MARVIN_REDEEM_RETRY;
 if(client){
  esp_http_client_set_header(client,"Content-Type","application/json");esp_http_client_set_post_field(client,serialized,strlen(serialized));
  esp_err_t error=esp_http_client_perform(client);int http=esp_http_client_get_status_code(client);
  if(error==ESP_OK&&!response.overflow){
   if(http==403)status=MARVIN_REDEEM_REJECTED;
   else if(http==200){
    const char *end=NULL;cJSON *data=memchr(response.data,0,response.length)||strstr(response.data,"\\u0000")?NULL:cJSON_ParseWithLengthOpts(response.data,response.length+1,&end,true);
    const cJSON *linked=cJSON_GetObjectItemCaseSensitive(data,"linked"),*device=cJSON_GetObjectItemCaseSensitive(data,"deviceId"),*version=cJSON_GetObjectItemCaseSensitive(data,"epoch"),*credential=cJSON_GetObjectItemCaseSensitive(data,"credential"),*expires=cJSON_GetObjectItemCaseSensitive(data,"credentialExpiresAt");
    bool valid=receipt_shape(data)&&cJSON_IsTrue(linked)&&cJSON_IsString(device)&&!strcmp(device->valuestring,marvin_identity_device_id())&&cJSON_IsNumber(version)&&version->valuedouble==epoch;
    if(claim){valid=valid&&cJSON_IsString(credential)&&strlen(credential->valuestring)==43&&strspn(credential->valuestring,"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_")==43&&cJSON_IsNumber(expires)&&isfinite(expires->valuedouble)&&floor(expires->valuedouble)==expires->valuedouble&&expires->valuedouble>1577836800000.0&&expires->valuedouble<=4102444800000.0;}
    else valid=valid&&!credential&&!expires;
    if(valid){if(claim){memcpy(receipt->credential,credential->valuestring,44);receipt->expires_ms=(int64_t)expires->valuedouble;}status=MARVIN_REDEEM_OK;}
    else status=MARVIN_REDEEM_INVALID;
    if(cJSON_IsString(credential)){mbedtls_platform_zeroize(credential->valuestring,strlen(credential->valuestring));}
    cJSON_Delete(data);
   }
  }
  esp_http_client_cleanup(client);
 }
 mbedtls_platform_zeroize(serialized,strlen(serialized));free(serialized);mbedtls_platform_zeroize(&response,sizeof(response));return status;
}
