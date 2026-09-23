#include "device_identity.h"
#include <string.h>
#include <stdio.h>
#include <math.h>
#include "nvs.h"
#include "esp_random.h"
#include "mbedtls/pk.h"
#include "mbedtls/ecdsa.h"
#include "mbedtls/sha256.h"
#include "mbedtls/base64.h"
#include "mbedtls/platform_util.h"
static mbedtls_pk_context key;
static char device_id[48];
static bool initialized;
static int random_data(void *ctx,unsigned char *out,size_t len){(void)ctx;esp_fill_random(out,len);return 0;}
esp_err_t marvin_identity_init(void){
 nvs_handle_t nvs;esp_err_t err=nvs_open_from_partition("factory","identity",NVS_READONLY,&nvs);if(err!=ESP_OK)return err;
 unsigned char der[256];size_t size=sizeof(der),len=sizeof(device_id);
 err=nvs_get_blob(nvs,"dev_key",der,&size);if(err==ESP_OK)err=nvs_get_str(nvs,"device_id",device_id,&len);nvs_close(nvs);
 if(err!=ESP_OK){mbedtls_platform_zeroize(der,sizeof(der));return err;}
 mbedtls_pk_init(&key);int result=mbedtls_pk_parse_key(&key,der,size,NULL,0,random_data,NULL);mbedtls_platform_zeroize(der,sizeof(der));
 if(result||!mbedtls_pk_can_do(&key,MBEDTLS_PK_ECDSA)||mbedtls_pk_get_bitlen(&key)!=256||mbedtls_pk_ec(key)->MBEDTLS_PRIVATE(grp).id!=MBEDTLS_ECP_DP_SECP256R1||strlen(device_id)!=39){mbedtls_pk_free(&key);return ESP_ERR_INVALID_ARG;}
 unsigned char public_der[128],fingerprint[32];char expected[48];
 int public_len=mbedtls_pk_write_pubkey_der(&key,public_der,sizeof(public_der));
 if(public_len<=0||mbedtls_sha256(public_der+sizeof(public_der)-public_len,public_len,fingerprint,0)){mbedtls_pk_free(&key);return ESP_FAIL;}
 memcpy(expected,"marvin_",7);for(size_t i=0;i<16;i++)snprintf(expected+7+2*i,3,"%02x",fingerprint[i]);
 if(strcmp(expected,device_id)){mbedtls_pk_free(&key);return ESP_ERR_INVALID_ARG;}
 initialized=true;return ESP_OK;
}
static esp_err_t sign_digest(const unsigned char digest[32],char proof[100]){
 unsigned char signature[64];size_t proof_len=0;
 mbedtls_mpi r,s;mbedtls_mpi_init(&r);mbedtls_mpi_init(&s);mbedtls_ecp_keypair *ec=mbedtls_pk_ec(key);
 int result=mbedtls_ecdsa_sign(&ec->MBEDTLS_PRIVATE(grp),&r,&s,&ec->MBEDTLS_PRIVATE(d),digest,32,random_data,NULL);
 if(!result)result=mbedtls_mpi_write_binary(&r,signature,32);
 if(!result)result=mbedtls_mpi_write_binary(&s,signature+32,32);
 mbedtls_mpi_free(&r);mbedtls_mpi_free(&s);if(result)return ESP_FAIL;
 if(mbedtls_base64_encode((unsigned char*)proof,100,&proof_len,signature,sizeof(signature)))return ESP_FAIL;
 for(size_t i=0;i<proof_len;i++){if(proof[i]=='+')proof[i]='-';else if(proof[i]=='/')proof[i]='_';else if(proof[i]=='='){proof_len=i;break;}}proof[proof_len]=0;
 return ESP_OK;
}
esp_err_t marvin_identity_challenge(const char *operation,double browser_time,cJSON *reply){
 if(!initialized)return ESP_ERR_INVALID_STATE;
 if((strcmp(operation,"claim")&&strcmp(operation,"network")&&strcmp(operation,"reconcile")&&strcmp(operation,"recover"))||!isfinite(browser_time)||browser_time<1||browser_time>9007199254740991.0||floor(browser_time)!=browser_time)return ESP_ERR_INVALID_ARG;
 unsigned char random[32],digest[32];char nonce[65],message[220],proof[100];
 esp_fill_random(random,sizeof(random));for(size_t i=0;i<sizeof(random);i++)snprintf(nonce+2*i,3,"%02x",random[i]);
 int length=snprintf(message,sizeof(message),"marvin-setup-v1:%s:%s:%s:%.0f",device_id,nonce,operation,browser_time);if(length<0||length>=sizeof(message))return ESP_ERR_INVALID_SIZE;
 if(mbedtls_sha256((unsigned char*)message,length,digest,0))return ESP_FAIL;
 if(sign_digest(digest,proof)!=ESP_OK)return ESP_FAIL;
 cJSON *challenge=cJSON_AddObjectToObject(reply,"challenge");if(!challenge)return ESP_ERR_NO_MEM;
 cJSON_AddStringToObject(challenge,"deviceId",device_id);cJSON_AddStringToObject(challenge,"nonce",nonce);cJSON_AddStringToObject(challenge,"operation",operation);cJSON_AddNumberToObject(challenge,"issuedAt",browser_time);cJSON_AddStringToObject(reply,"proof",proof);
 return ESP_OK;
}

const char *marvin_identity_device_id(void){return initialized?device_id:NULL;}
esp_err_t marvin_identity_redemption_proof(const char *ticket,const char *network,char proof[100]){
 if(!initialized||!ticket||!network||!proof)return ESP_ERR_INVALID_ARG;
 size_t ticket_len=strnlen(ticket,4097),network_len=strnlen(network,129);
 if(!ticket_len||ticket_len>4096||!network_len||network_len>128)return ESP_ERR_INVALID_SIZE;
 unsigned char hash[32];char hex[65],encoded[180],message[300];size_t len=0;
 if(mbedtls_sha256((const unsigned char*)ticket,ticket_len,hash,0))return ESP_FAIL;
 for(size_t i=0;i<32;i++)snprintf(hex+2*i,3,"%02x",hash[i]);
 if(mbedtls_base64_encode((unsigned char*)encoded,sizeof(encoded),&len,(const unsigned char*)network,network_len))return ESP_FAIL;
 for(size_t i=0;i<len;i++){if(encoded[i]=='+')encoded[i]='-';else if(encoded[i]=='/')encoded[i]='_';else if(encoded[i]=='='){len=i;break;}}encoded[len]=0;
 int size=snprintf(message,sizeof(message),"marvin-redeem-v1:%s:%s",hex,encoded);
 if(size<0||size>=sizeof(message)||mbedtls_sha256((unsigned char*)message,size,hash,0))return ESP_FAIL;
 return sign_digest(hash,proof);
}
