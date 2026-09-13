#include "marvin_update_download.h"
#include "esp_http_client.h"
#include "esp_crt_bundle.h"
#include "esp_timer.h"
#include "esp_log.h"
#include "mbedtls/platform_util.h"
#include <stdio.h>
#include <string.h>
static bool valid_origin(const char *origin){
 if(!origin||strncmp(origin,"https://",8)||!origin[8]||strlen(origin)>192)return false;
 for(const char *p=origin+8;*p;p++)if(!((*p>='a'&&*p<='z')||(*p>='A'&&*p<='Z')||(*p>='0'&&*p<='9')||strchr(".-:[]",*p)))return false;
 return true;
}
static bool valid_token(const char *token){if(!token||strlen(token)!=43)return false;for(const char *p=token;*p;p++)if(!((*p>='a'&&*p<='z')||(*p>='A'&&*p<='Z')||(*p>='0'&&*p<='9')||*p=='_'||*p=='-'))return false;return true;}
static bool stopped(int64_t deadline,bool (*cancelled)(void*),void *context){return esp_timer_get_time()>=deadline||(cancelled&&cancelled(context));}
static esp_http_client_handle_t open_file(const char *origin,const char *token,const char *ca,uint32_t sequence,const char *part,size_t bytes){
 char url[256],authorization[52];snprintf(url,sizeof(url),"%s/api/device/firmware/%u/%s",origin,(unsigned)sequence,part);snprintf(authorization,sizeof(authorization),"Bearer %s",token);
 esp_http_client_config_t config={.url=url,.method=HTTP_METHOD_GET,.transport_type=HTTP_TRANSPORT_OVER_SSL,.cert_pem=ca&&ca[0]?ca:NULL,.crt_bundle_attach=ca&&ca[0]?NULL:esp_crt_bundle_attach,.timeout_ms=5000,.buffer_size=1024,.buffer_size_tx=1024,.disable_auto_redirect=true};
 esp_http_client_handle_t client=esp_http_client_init(&config);bool ok=client&&esp_http_client_set_header(client,"Authorization",authorization)==ESP_OK&&esp_http_client_set_header(client,"Accept-Encoding","identity")==ESP_OK;
 mbedtls_platform_zeroize(authorization,sizeof(authorization));
 if(ok)ok=esp_http_client_open(client,0)==ESP_OK&&esp_http_client_fetch_headers(client)==(int64_t)bytes&&esp_http_client_get_status_code(client)==200;
 if(!ok&&client){esp_http_client_close(client);esp_http_client_cleanup(client);client=NULL;}return client;
}
bool marvin_update_download(const char *origin,const char *token,const char *ca,uint32_t sequence,const marvin_update_trust_t *trust,marvin_update_writer_t writer,bool (*cancelled)(void*),void *context){
 if(!valid_origin(origin)||!valid_token(token)||!sequence||!trust)return false;
 int64_t deadline=esp_timer_get_time()+120000000;
 if(stopped(deadline,cancelled,context))return false;
 /* ESP-IDF HTTP debug logging can contain request headers. */
 esp_log_level_set("HTTP_CLIENT",ESP_LOG_WARN);esp_log_level_set("TRANSPORT_BASE",ESP_LOG_WARN);
 uint8_t manifest[MARVIN_UPDATE_MANIFEST_BYTES],chunk[1024];size_t received=0;
 esp_http_client_handle_t client=open_file(origin,token,ca,sequence,"manifest",sizeof(manifest));
 if(!client)return false;
 bool ok=true;while(received<sizeof(manifest)&&ok){if(stopped(deadline,cancelled,context)){ok=false;break;}int n=esp_http_client_read(client,(char*)manifest+received,sizeof(manifest)-received);if(n<=0)ok=false;else received+=(size_t)n;}
 ok=ok&&esp_http_client_is_complete_data_received(client);esp_http_client_close(client);esp_http_client_cleanup(client);
 marvin_update_image_t image;
 if(!ok||!marvin_update_verify(manifest,sizeof(manifest),trust,&image)||image.sequence!=sequence||stopped(deadline,cancelled,context))return false;
 /* Authenticate and validate response framing before opening an inactive slot. */
 client=open_file(origin,token,ca,sequence,"image",image.image_bytes);if(!client)return false;
 marvin_update_transfer_t transfer;marvin_update_transfer_init(&transfer,writer);ok=!stopped(deadline,cancelled,context)&&marvin_update_transfer_begin(&transfer,manifest,sizeof(manifest),trust);received=0;
 while(ok&&received<image.image_bytes){if(stopped(deadline,cancelled,context)){ok=false;break;}size_t bytes=image.image_bytes-received;if(bytes>sizeof(chunk))bytes=sizeof(chunk);int n=esp_http_client_read(client,(char*)chunk,bytes);if(n<=0)ok=false;else{ok=marvin_update_transfer_write(&transfer,chunk,(size_t)n);received+=(size_t)n;}}
 ok=ok&&esp_http_client_is_complete_data_received(client)&&!stopped(deadline,cancelled,context);esp_http_client_close(client);esp_http_client_cleanup(client);mbedtls_platform_zeroize(chunk,sizeof(chunk));
 if(ok)ok=marvin_update_transfer_finish(&transfer);
 if(!ok)marvin_update_transfer_cancel(&transfer);
 return ok;
}
