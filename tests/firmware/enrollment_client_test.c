#include "enrollment_client.h"
#include "device_identity.h"
#include "esp_http_client.h"
#include <assert.h>
#include <stdio.h>
#include <string.h>
static esp_http_client_config_t config;
static const char *response;
static int http=200;
static bool tls_failure;
static const char *device="marvin_0123456789abcdef0123456789abcdef";
const char *marvin_identity_device_id(void){return device;}
esp_err_t marvin_identity_redemption_proof(const char *ticket,const char *network,char proof[100]){assert(!strcmp(ticket,"signed-ticket"));assert(!strcmp(network,"Synthetic AP"));strcpy(proof,"test-proof");return ESP_OK;}
esp_err_t esp_crt_bundle_attach(void *p){(void)p;return ESP_OK;}
esp_http_client_handle_t esp_http_client_init(const esp_http_client_config_t *c){config=*c;assert(c->disable_auto_redirect&&c->timeout_ms==15000&&c->cert_pem&&!c->crt_bundle_attach);assert(!strcmp(c->url,"https://backend.test/api/device/enrollment/redeem"));return &config;}
esp_err_t esp_http_client_set_header(esp_http_client_handle_t c,const char *name,const char *value){(void)c;assert(!strcmp(name,"Content-Type")&&!strcmp(value,"application/json"));return ESP_OK;}
esp_err_t esp_http_client_set_post_field(esp_http_client_handle_t c,const char *data,int length){(void)c;assert(length==(int)strlen(data));cJSON *json=cJSON_Parse(data);assert(json);assert(!strcmp(cJSON_GetObjectItem(json,"ticket")->valuestring,"signed-ticket"));assert(!cJSON_GetObjectItem(json,"password"));cJSON_Delete(json);return ESP_OK;}
esp_err_t esp_http_client_perform(esp_http_client_handle_t c){(void)c;if(tls_failure)return ESP_FAIL;for(size_t i=0;i<strlen(response);i+=7){size_t len=strlen(response)-i;if(len>7)len=7;esp_http_client_event_t event={.event_id=HTTP_EVENT_ON_DATA,.user_data=config.user_data,.data=(void*)(response+i),.data_len=(int)len};if(config.event_handler(&event)!=ESP_OK)return ESP_FAIL;}return ESP_OK;}
int esp_http_client_get_status_code(esp_http_client_handle_t c){(void)c;return http;}
esp_err_t esp_http_client_cleanup(esp_http_client_handle_t c){(void)c;return ESP_OK;}
static marvin_redeem_status_t redeem(bool claim,marvin_redeem_receipt_t *receipt){return marvin_enrollment_redeem("https://backend.test","test-ca","signed-ticket","Synthetic AP",7,claim,receipt);}
int main(void){
 marvin_redeem_receipt_t receipt;const char *valid="{\"linked\":true,\"deviceId\":\"marvin_0123456789abcdef0123456789abcdef\",\"epoch\":7,\"credential\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"credentialExpiresAt\":2000000000000}";
 response=valid;assert(redeem(true,&receipt)==MARVIN_REDEEM_OK);assert(strlen(receipt.credential)==43&&receipt.expires_ms==2000000000000);
 assert(redeem(false,&receipt)==MARVIN_REDEEM_INVALID);assert(!receipt.credential[0]);
 response="{\"linked\":true,\"deviceId\":\"marvin_0123456789abcdef0123456789abcdef\",\"epoch\":7}";assert(redeem(false,&receipt)==MARVIN_REDEEM_OK);assert(!receipt.credential[0]);assert(redeem(true,&receipt)==MARVIN_REDEEM_INVALID);
 response="{\"linked\":true,\"deviceId\":\"another\",\"epoch\":7}";assert(redeem(false,&receipt)==MARVIN_REDEEM_INVALID);
 response="{\"linked\":true,\"deviceId\":\"marvin_0123456789abcdef0123456789abcdef\",\"epoch\":8}";assert(redeem(false,&receipt)==MARVIN_REDEEM_INVALID);
 response=valid;http=403;assert(redeem(true,&receipt)==MARVIN_REDEEM_REJECTED);http=500;assert(redeem(true,&receipt)==MARVIN_REDEEM_RETRY);http=302;assert(redeem(true,&receipt)==MARVIN_REDEEM_RETRY);http=200;tls_failure=true;assert(redeem(true,&receipt)==MARVIN_REDEEM_RETRY);tls_failure=false;
 char large[1100];memset(large,'x',sizeof(large));large[1099]=0;response=large;assert(redeem(true,&receipt)==MARVIN_REDEEM_RETRY);
 response="{}{}";assert(redeem(true,&receipt)==MARVIN_REDEEM_INVALID);
 response="{\"linked\":true,\"deviceId\":\"marvin_0123456789abcdef0123456789abcdef\",\"epoch\":7,\"epoch\":8}";assert(redeem(false,&receipt)==MARVIN_REDEEM_INVALID);
 puts("Enrollment HTTP client: chunked receipts, identity/epoch, credential shape, TLS/HTTP errors and bounded response tests passed.");
}
