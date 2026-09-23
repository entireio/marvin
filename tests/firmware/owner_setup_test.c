#include "owner_setup.h"
#include "device_identity.h"
#include "marvin_journal.h"
#include "nvs.h"
#include <sys/time.h>
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
static char *public_key;
static marvin_journal_record_t disk,pending;
static bool disk_exists,fail_storage,erase_pending;
static int64_t monotonic=1000000;
static marvin_redeem_status_t backend=MARVIN_REDEEM_OK;
static char *file(const char *path){FILE *f=fopen(path,"rb");assert(f);fseek(f,0,SEEK_END);long n=ftell(f);rewind(f);char *s=calloc((size_t)n+1,1);assert(s&&fread(s,1,(size_t)n,f)==(size_t)n);fclose(f);return s;}
int owner_test_time(struct timeval *tv,void *zone){(void)zone;tv->tv_sec=1789263000;tv->tv_usec=0;return 0;}
int64_t esp_timer_get_time(void){return monotonic;}
const char *marvin_identity_device_id(void){return "marvin_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";}
esp_err_t marvin_identity_challenge(const char *op,double when,cJSON *reply){cJSON *c=cJSON_AddObjectToObject(reply,"challenge");cJSON_AddStringToObject(c,"nonce","1111111111111111111111111111111111111111111111111111111111111111");cJSON_AddStringToObject(c,"operation",op);cJSON_AddNumberToObject(c,"issuedAt",when);return ESP_OK;}
esp_err_t nvs_open_from_partition(const char *p,const char *ns,int mode,nvs_handle_t *h){(void)mode;assert(!strcmp(p,"factory")&&!strcmp(ns,"identity"));*h=1;return ESP_OK;}
esp_err_t nvs_open(const char *ns,int mode,nvs_handle_t *h){assert(!strcmp(ns,"ownership"));if(mode==NVS_READONLY&&!disk_exists)return ESP_ERR_NVS_NOT_FOUND;*h=2;return ESP_OK;}
esp_err_t nvs_get_str(nvs_handle_t h,const char *key,char *out,size_t *len){assert(h==1);const char *s=!strcmp(key,"enroll_iss")?"https://marvin.example":public_key;assert(strlen(s)+1<=*len);strcpy(out,s);*len=strlen(s)+1;return ESP_OK;}
esp_err_t nvs_get_blob(nvs_handle_t h,const char *key,void *out,size_t *len){assert(h==2&&!strcmp(key,"journal")&&*len==sizeof(disk));if(!disk_exists)return ESP_ERR_NVS_NOT_FOUND;memcpy(out,&disk,sizeof(disk));return ESP_OK;}
esp_err_t nvs_set_blob(nvs_handle_t h,const char *key,const void *data,size_t len){assert(h==2&&!strcmp(key,"journal")&&len==sizeof(disk));memcpy(&pending,data,len);return ESP_OK;}
esp_err_t nvs_erase_all(nvs_handle_t h){assert(h==2);memset(&pending,0,sizeof(pending));erase_pending=true;return ESP_OK;}
esp_err_t nvs_commit(nvs_handle_t h){assert(h==2);if(fail_storage)return ESP_FAIL;if(erase_pending){disk_exists=false;erase_pending=false;}else{disk=pending;disk_exists=true;}return ESP_OK;}
void nvs_close(nvs_handle_t h){(void)h;}
marvin_redeem_status_t marvin_enrollment_redeem(const char *origin,const char *ca,const char *ticket,const char *network,uint32_t epoch,bool claim,marvin_redeem_receipt_t *receipt){assert(!strcmp(origin,"https://marvin.example")&&!strcmp(ca,"test-ca")&&strlen(ticket)>100&&network[0]&&epoch==7);memset(receipt,0,sizeof(*receipt));if(claim){strcpy(receipt->credential,"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");receipt->expires_ms=2000000000000;}return backend;}
static cJSON *command(uint32_t session,const char *json){cJSON *request=cJSON_Parse(json),*reply=cJSON_CreateObject();assert(request&&marvin_owner_setup_control(session,request,reply));cJSON_Delete(request);return reply;}
static void ok(uint32_t session,const char *json,const char *field){cJSON *r=command(session,json);if(!cJSON_GetObjectItem(r,field)||cJSON_GetObjectItem(r,"error")){char *shown=cJSON_PrintUnformatted(r);fprintf(stderr,"command failed: %s => %s\n",json,shown?shown:"<json error>");free(shown);}assert(cJSON_GetObjectItem(r,field)&&!cJSON_GetObjectItem(r,"error"));cJSON_Delete(r);}
static void transfer_ticket(const char *ticket){char request[400],hex[241];snprintf(request,sizeof(request),"{\"op\":\"ticket_begin\",\"length\":%zu}",strlen(ticket));ok(7,request,"accepted");for(size_t i=0;i<strlen(ticket);i+=120){size_t n=strlen(ticket)-i;if(n>120)n=120;for(size_t j=0;j<n;j++)snprintf(hex+2*j,3,"%02x",(unsigned char)ticket[i+j]);snprintf(request,sizeof(request),"{\"op\":\"ticket_chunk\",\"offset\":%zu,\"hex\":\"%s\"}",i,hex);ok(7,request,"received");}ok(7,"{\"op\":\"ticket_finish\"}","verified");ok(7,"{\"op\":\"ticket_finish\"}","verified");}
int main(int argc,char **argv){assert(argc==3);public_key=file(argv[1]);char *raw=file(argv[2]);cJSON *cases=cJSON_Parse(raw);const char *ticket=cJSON_GetObjectItem(cJSON_GetArrayItem(cases,0),"ticket")->valuestring;assert(marvin_owner_setup_init()==ESP_OK);
 ok(7,"{\"op\":\"challenge\",\"operation\":\"claim\",\"issuedAt\":1789263000000}","challenge");cJSON *r=command(8,"{\"op\":\"ticket_begin\",\"length\":100}");assert(cJSON_GetObjectItem(r,"error"));cJSON_Delete(r);
 transfer_ticket(ticket);wifi_config_t candidate={0},readback;memcpy(candidate.sta.ssid,"Synthetic AP",13);memcpy(candidate.sta.password,"not-a-real-password",20);
 assert(!marvin_owner_setup_stage(8,&candidate));fail_storage=true;assert(!marvin_owner_setup_stage(7,&candidate));fail_storage=false;assert(marvin_owner_setup_stage(7,&candidate));assert(marvin_owner_setup_pending(&readback));assert(!memcmp(&candidate,&readback,sizeof(candidate)));
 backend=MARVIN_REDEEM_RETRY;assert(marvin_owner_setup_redeem("test-ca")==MARVIN_REDEEM_RETRY);marvin_owner_setup_disconnected();assert(marvin_owner_setup_init()==ESP_OK);assert(marvin_owner_setup_pending(&readback));
 backend=MARVIN_REDEEM_OK;fail_storage=true;assert(marvin_owner_setup_redeem("test-ca")==MARVIN_REDEEM_RETRY);assert(marvin_owner_setup_pending(&readback));fail_storage=false;assert(marvin_owner_setup_redeem("test-ca")==MARVIN_REDEEM_OK);assert(marvin_owner_setup_linked()&&!marvin_owner_setup_pending(&readback));assert(marvin_owner_setup_network(&readback));
 r=command(7,"{\"op\":\"challenge\",\"operation\":\"claim\",\"issuedAt\":1789263000000}");assert(cJSON_GetObjectItem(r,"error"));cJSON_Delete(r);
 ok(7,"{\"op\":\"challenge\",\"operation\":\"network\",\"issuedAt\":1789263000000}","challenge");ticket=cJSON_GetObjectItem(cJSON_GetArrayItem(cases,1),"ticket")->valuestring;transfer_ticket(ticket);monotonic+=120000000;assert(!marvin_owner_setup_stage(7,&candidate));
 marvin_journal_record_t linked_disk=disk;monotonic=1000000;ok(7,"{\"op\":\"challenge\",\"operation\":\"recover\",\"issuedAt\":1789263000000}","challenge");ticket=cJSON_GetObjectItem(cJSON_GetArrayItem(cases,3),"ticket")->valuestring;transfer_ticket(ticket);ok(7,"{\"op\":\"clear_returned\"}","cleared");assert(!marvin_owner_setup_linked()&&!disk_exists);
 disk=linked_disk;disk_exists=true;pending=disk;erase_pending=false;assert(marvin_owner_setup_init()==ESP_OK&&marvin_owner_setup_linked());
 monotonic=1000000;ok(7,"{\"op\":\"challenge\",\"operation\":\"reconcile\",\"issuedAt\":1789263000000}","challenge");ticket=cJSON_GetObjectItem(cJSON_GetArrayItem(cases,2),"ticket")->valuestring;transfer_ticket(ticket);
 fail_storage=true;r=command(7,"{\"op\":\"clear_owner\"}");assert(cJSON_GetObjectItem(r,"error")&&marvin_owner_setup_linked());cJSON_Delete(r);fail_storage=false;
 ok(7,"{\"op\":\"clear_owner\"}","cleared");assert(!marvin_owner_setup_linked());
 r=command(7,"{\"op\":\"clear_owner\"}");assert(cJSON_GetObjectItem(r,"error"));cJSON_Delete(r);
 cJSON_Delete(cases);free(raw);free(public_key);puts("Owner setup: real ES256 ticket transfer, session fence, persistence failure, fleet return recovery, signed reconciliation and challenge expiry passed.");
}
