#include "device_identity.h"
#include "nvs.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
static const char *key_file,*fingerprint;
esp_err_t nvs_open_from_partition(const char *partition,const char *ns,int mode,nvs_handle_t *h){(void)mode;if(strcmp(partition,"factory")||strcmp(ns,"identity"))return ESP_FAIL;*h=1;return ESP_OK;}
esp_err_t nvs_get_blob(nvs_handle_t h,const char *key,void *out,size_t *size){(void)h;if(strcmp(key,"dev_key"))return ESP_FAIL;FILE *f=fopen(key_file,"rb");if(!f)return ESP_FAIL;size_t n=fread(out,1,*size,f);int extra=fgetc(f);fclose(f);if(extra!=EOF)return ESP_FAIL;*size=n;return ESP_OK;}
esp_err_t nvs_get_str(nvs_handle_t h,const char *key,char *out,size_t *size){(void)h;if(strcmp(key,"device_id")||strlen(fingerprint)+1>*size)return ESP_FAIL;strcpy(out,fingerprint);*size=strlen(fingerprint)+1;return ESP_OK;}
void nvs_close(nvs_handle_t h){(void)h;}
void esp_fill_random(void *out,size_t size){arc4random_buf(out,size);}
int main(int argc,char **argv){
 if(argc!=3)return 1;key_file=argv[1];fingerprint=argv[2];
 if(marvin_identity_init()!=ESP_OK){puts("rejected");return 0;}
 cJSON *reply=cJSON_CreateObject();if(marvin_identity_challenge("claim",1789265093146.0,reply)!=ESP_OK)return 2;
 char *json=cJSON_PrintUnformatted(reply);puts(json);free(json);cJSON_Delete(reply);
 char proof[100];if(marvin_identity_redemption_proof("synthetic.ticket.signature","Café test",proof)!=ESP_OK)return 3;puts(proof);
 if(marvin_identity_redemption_proof("","network",proof)==ESP_OK)return 4;
 return 0;
}
