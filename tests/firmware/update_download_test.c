#include "marvin_update_download.h"
#include "esp_http_client.h"
#include <assert.h>
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
static uint8_t manifest[176];static int mode,opened,cleaned,reads,begins,writes,selected,aborted;static size_t written;
struct fixture_http {bool image;size_t at,bytes;};
int esp_crt_bundle_attach(void *p){(void)p;return 0;}
void esp_log_level_set(const char *p,int level){(void)p;(void)level;}
int64_t esp_timer_get_time(void){return mode==7&&reads>2?130000000:0;}
esp_http_client_handle_t esp_http_client_init(const esp_http_client_config_t *c){assert(c->transport_type==HTTP_TRANSPORT_OVER_SSL&&c->disable_auto_redirect&&c->timeout_ms==5000);assert(!strncmp(c->url,"https://marvin.test/api/device/firmware/",39));assert(!strcmp(c->cert_pem,"fixture-ca"));struct fixture_http *h=calloc(1,sizeof(*h));assert(h);h->image=strstr(c->url,"/image")!=NULL;h->bytes=h->image?4096:176;opened++;return h;}
int esp_http_client_set_header(esp_http_client_handle_t h,const char *key,const char *value){(void)h;if(!strcmp(key,"Authorization"))assert(strlen(value)==50&&!strncmp(value,"Bearer ",7));else assert(!strcmp(key,"Accept-Encoding")&&!strcmp(value,"identity"));return 0;}
int esp_http_client_open(esp_http_client_handle_t h,int n){(void)h;assert(!n);return 0;}
int64_t esp_http_client_fetch_headers(esp_http_client_handle_t h){return h->bytes+(mode==2?1:0);}
int esp_http_client_get_status_code(esp_http_client_handle_t h){(void)h;return mode==1?302:200;}
int esp_http_client_read(esp_http_client_handle_t h,char *out,int count){reads++;if(mode==4&&h->image&&h->at>0)return 0;size_t n=h->bytes-h->at;if(n>(size_t)count)n=count;if(n>83)n=83;if(h->image)memset(out,mode==5?0x43:0x42,n);else{memcpy(out,manifest+h->at,n);if(mode==3&&h->at==0)out[0]^=1;}h->at+=n;return (int)n;}
bool esp_http_client_is_complete_data_received(esp_http_client_handle_t h){return h->at==h->bytes;}
int esp_http_client_close(esp_http_client_handle_t h){(void)h;return 0;}
int esp_http_client_cleanup(esp_http_client_handle_t h){free(h);cleaned++;return 0;}
static bool begin(void *p,uint32_t n){(void)p;assert(n==4096);begins++;return true;}
static bool write_image(void *p,const uint8_t *data,size_t n){(void)p;(void)data;written+=n;writes++;return true;}
static bool finish(void *p){(void)p;assert(written==4096);return true;}
static bool select_image(void *p,const marvin_update_image_t *i){(void)p;assert(i->sequence==4&&written==4096);selected++;return true;}
static void abort_image(void *p){(void)p;aborted++;}
static bool cancel(void *p){(void)p;return mode==6&&writes>2;}
static void reset(int m){mode=m;opened=cleaned=reads=begins=writes=selected=aborted=0;written=0;}
int main(int argc,char **argv){assert(argc==2);char path[512],pem[1024]={0},token[44];memset(token,'a',43);token[43]=0;
 snprintf(path,sizeof(path),"%s/public.pem",argv[1]);FILE *f=fopen(path,"rb");assert(f);assert(fread(pem,1,sizeof(pem)-1,f));fclose(f);
 snprintf(path,sizeof(path),"%s/transfer.bin",argv[1]);f=fopen(path,"rb");assert(f);assert(fread(manifest,1,176,f)==176);fclose(f);
 marvin_update_trust_t trust={pem,"waveshare-esp32s3-audio","afe-v1",3,0x1e0000};marvin_update_writer_t writer={NULL,begin,write_image,finish,select_image,abort_image};
 for(int trial=0;trial<10;trial++)for(int m=0;m<8;m++){reset(m);bool ok=marvin_update_download("https://marvin.test",token,"fixture-ca",4,&trust,writer,cancel,NULL);assert(ok==(m==0));assert(opened==cleaned);assert(selected==(m==0));if(!ok&&begins)assert(aborted==1);if(m==1||m==2||m==3)assert(!begins);}
 const char *bad[]={"http://marvin.test","https://marvin.test/evil","https://marvin.test?evil","https://user@marvin.test","https://marvin.test\r\nHost:evil","https://"};for(size_t i=0;i<sizeof(bad)/sizeof(*bad);i++){reset(0);assert(!marvin_update_download(bad[i],token,"fixture-ca",4,&trust,writer,NULL,NULL));assert(!opened);}
 reset(0);assert(!marvin_update_download("https://marvin.test","invalid","fixture-ca",4,&trust,writer,NULL,NULL));assert(!opened);
 reset(0);assert(!marvin_update_download("https://marvin.test",token,"fixture-ca",5,&trust,writer,NULL,NULL));assert(opened==1&&!begins);
 puts("HTTPS update fixture: 80 success/redirect/framing/signature/truncation/digest/cancel/deadline trials and origin/token/version rejection passed; no failed download selected an image.");
}
