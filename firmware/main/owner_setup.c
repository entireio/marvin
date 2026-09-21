#include "owner_setup.h"
#include "device_identity.h"
#include "marvin_journal.h"
#include "marvin_transfer.h"
#include "esp_timer.h"
#include "nvs.h"
#include "mbedtls/platform_util.h"
#include <string.h>
#include <stdio.h>
#include <math.h>
#include <sys/time.h>
#include <stdatomic.h>
static marvin_journal_t journal;
static marvin_journal_record_t loaded;
static marvin_transfer_t transfer;
static marvin_ticket_claims_t claims;
static char issuer[201],public_key[1024],nonce[65],operation[12],verified_ticket[4097];
static uint32_t challenge_session;
static int64_t challenged_at;
static bool ready,verified;
static atomic_bool linked_view;
_Static_assert(sizeof(wifi_config_t)<=MARVIN_NETWORK_BYTES,"Wi-Fi configuration exceeds journal capacity");
static bool persist(void *context,const marvin_journal_record_t *record){
 (void)context;nvs_handle_t h;if(nvs_open("ownership",NVS_READWRITE,&h)!=ESP_OK)return false;
 esp_err_t err=nvs_set_blob(h,"journal",record,sizeof(*record));if(err==ESP_OK)err=nvs_commit(h);nvs_close(h);return err==ESP_OK;
}
esp_err_t marvin_owner_setup_init(void){
 nvs_handle_t h;size_t length=sizeof(issuer);ready=false;
 if(!marvin_identity_device_id()||nvs_open_from_partition("factory","identity",NVS_READONLY,&h)!=ESP_OK)return ESP_ERR_INVALID_STATE;
 esp_err_t err=nvs_get_str(h,"enroll_iss",issuer,&length);length=sizeof(public_key);if(err==ESP_OK)err=nvs_get_str(h,"enroll_pub",public_key,&length);nvs_close(h);
 if(err!=ESP_OK||strncmp(issuer,"https://",8)||strpbrk(issuer+8,"/?#@"))return ESP_ERR_INVALID_ARG;
 err=nvs_open("ownership",NVS_READONLY,&h);
 if(err==ESP_ERR_NVS_NOT_FOUND)ready=marvin_journal_open(&journal,NULL,persist,NULL);
 else if(err==ESP_OK){length=sizeof(loaded);err=nvs_get_blob(h,"journal",&loaded,&length);nvs_close(h);if(err==ESP_ERR_NVS_NOT_FOUND)ready=marvin_journal_open(&journal,NULL,persist,NULL);else if(err==ESP_OK&&length==sizeof(loaded))ready=marvin_journal_open(&journal,&loaded,persist,NULL);mbedtls_platform_zeroize(&loaded,sizeof(loaded));}
 atomic_store(&linked_view,ready&&journal.current.linked);return ready?ESP_OK:ESP_ERR_INVALID_STATE;
}
void marvin_owner_setup_disconnected(void){marvin_transfer_clear(&transfer);mbedtls_platform_zeroize(verified_ticket,sizeof(verified_ticket));mbedtls_platform_zeroize(&claims,sizeof(claims));nonce[0]=0;operation[0]=0;verified=false;challenged_at=0;}
static bool fresh(uint32_t session){return ready&&nonce[0]&&session==challenge_session&&esp_timer_get_time()-challenged_at>=0&&esp_timer_get_time()-challenged_at<120000000;}
static bool integer(const cJSON *value,double max){return cJSON_IsNumber(value)&&isfinite(value->valuedouble)&&floor(value->valuedouble)==value->valuedouble&&value->valuedouble>=0&&value->valuedouble<=max;}
static int unhex(char c){if(c>='0'&&c<='9')return c-'0';if(c>='a'&&c<='f')return c-'a'+10;return -1;}
bool marvin_owner_setup_control(uint32_t session,const cJSON *request,cJSON *reply){
 const cJSON *op=cJSON_GetObjectItemCaseSensitive(request,"op");if(!cJSON_IsString(op))return false;
 if(strcmp(op->valuestring,"challenge")&&strncmp(op->valuestring,"ticket_",7)&&strcmp(op->valuestring,"clear_owner"))return false;
 if(!ready){cJSON_AddStringToObject(reply,"error","ENROLLMENT_UNAVAILABLE");return true;}
 if(journal.current.pending&&strcmp(op->valuestring,"challenge")&&strcmp(op->valuestring,"clear_owner")&&strncmp(op->valuestring,"ticket_",7)){cJSON_AddStringToObject(reply,"error","SETUP_PENDING");return true;}
 const char *error=NULL;
 if(!strcmp(op->valuestring,"challenge")){
  marvin_owner_setup_disconnected();const cJSON *kind=cJSON_GetObjectItemCaseSensitive(request,"operation"),*issued=cJSON_GetObjectItemCaseSensitive(request,"issuedAt");
  bool reconcile=cJSON_IsString(kind)&&!strcmp(kind->valuestring,"reconcile");
  if(!cJSON_IsString(kind)||!cJSON_IsNumber(issued)||(!strcmp(kind->valuestring,"network")&&!journal.current.linked)||(!strcmp(kind->valuestring,"claim")&&journal.current.linked)||(reconcile&&!journal.current.linked)||(journal.current.pending&&!reconcile))error=journal.current.pending?"SETUP_PENDING":"OWNERSHIP_STATE";
  else if(marvin_identity_challenge(kind->valuestring,issued->valuedouble,reply)!=ESP_OK)error="INVALID_CHALLENGE";
  else{const cJSON *challenge=cJSON_GetObjectItemCaseSensitive(reply,"challenge"),*value=cJSON_GetObjectItemCaseSensitive(challenge,"nonce");if(!cJSON_IsString(value)||strlen(value->valuestring)!=64)error="INVALID_CHALLENGE";else{memcpy(nonce,value->valuestring,65);strcpy(operation,kind->valuestring);challenge_session=session;challenged_at=esp_timer_get_time();}}
 }else if(!fresh(session))error="CHALLENGE_EXPIRED";
 else if(!strcmp(op->valuestring,"ticket_begin")){
  verified=false;mbedtls_platform_zeroize(verified_ticket,sizeof(verified_ticket));const cJSON *total=cJSON_GetObjectItemCaseSensitive(request,"length");if(!integer(total,4096)||!marvin_transfer_begin(&transfer,session,(size_t)total->valuedouble,(uint64_t)(esp_timer_get_time()/1000)))error="INVALID_TICKET_LENGTH";else cJSON_AddBoolToObject(reply,"accepted",true);
 }else if(!strcmp(op->valuestring,"ticket_chunk")){
  const cJSON *offset=cJSON_GetObjectItemCaseSensitive(request,"offset"),*hex=cJSON_GetObjectItemCaseSensitive(request,"hex");uint8_t bytes[120];size_t size=cJSON_IsString(hex)?strlen(hex->valuestring):0;
  bool valid=integer(offset,4096)&&size&&size<=240&&size%2==0;
  for(size_t i=0;valid&&i<size;i+=2){int a=unhex(hex->valuestring[i]),b=unhex(hex->valuestring[i+1]);if(a<0||b<0)valid=false;else bytes[i/2]=(uint8_t)(a*16+b);}
  if(!valid||!marvin_transfer_append(&transfer,session,(size_t)offset->valuedouble,bytes,size/2,(uint64_t)(esp_timer_get_time()/1000)))error="INVALID_TICKET_CHUNK";else cJSON_AddNumberToObject(reply,"received",transfer.used);mbedtls_platform_zeroize(bytes,sizeof(bytes));
 }else if(!strcmp(op->valuestring,"ticket_finish")){
  if(verified){cJSON_AddBoolToObject(reply,"verified",true);return true;}
  size_t size=0;const uint8_t *ticket=marvin_transfer_finish(&transfer,session,(uint64_t)(esp_timer_get_time()/1000),&size);struct timeval now;gettimeofday(&now,NULL);
  bool reconcile=!strcmp(operation,"reconcile");marvin_ticket_expectation_t expected={.public_key_pem=public_key,.issuer=issuer,.device_id=marvin_identity_device_id(),.nonce=nonce,.operation=operation,.owner=journal.current.linked?journal.current.owner:NULL,.epoch=journal.current.linked&&!reconcile?journal.current.epoch:0,.now_seconds=now.tv_sec,.challenge_age_ms=(uint32_t)((esp_timer_get_time()-challenged_at)/1000)};
  if(!ticket||memchr(ticket,0,size)||!marvin_ticket_verify((const char*)ticket,size,&expected,&claims))error="TICKET_INVALID";
  else{memcpy(verified_ticket,ticket,size);verified_ticket[size]=0;verified=true;cJSON_AddBoolToObject(reply,"verified",true);}marvin_transfer_clear(&transfer);
 }else if(!strcmp(op->valuestring,"clear_owner")){
  if(!verified||strcmp(operation,"reconcile"))error="OWNER_TICKET_REQUIRED";
  else if(!marvin_journal_revoke(&journal,claims.owner,journal.current.epoch))error="OWNERSHIP_STATE";
  else{atomic_store(&linked_view,false);marvin_owner_setup_disconnected();cJSON_AddBoolToObject(reply,"cleared",true);}
 }else error="UNKNOWN_OPERATION";
 if(error){cJSON_AddStringToObject(reply,"error",error);}
 return true;
}
bool marvin_owner_setup_stage(uint32_t session,const wifi_config_t *candidate){
 if(!fresh(session)||!verified||!candidate)return false;
 struct timeval now;gettimeofday(&now,NULL);if(now.tv_sec>=claims.expires_at)return false;
 bool saved=marvin_journal_stage(&journal,&claims,verified_ticket,!strcmp(operation,"network"),candidate,sizeof(*candidate));if(saved)marvin_owner_setup_disconnected();return saved;
}
bool marvin_owner_setup_pending(wifi_config_t *candidate){if(!ready||!journal.current.pending||journal.current.pending_network_size!=sizeof(*candidate))return false;memcpy(candidate,journal.current.pending_network,sizeof(*candidate));return true;}
bool marvin_owner_setup_network(wifi_config_t *network){if(!ready||!journal.current.linked||journal.current.network_size!=sizeof(*network))return false;memcpy(network,journal.current.network,sizeof(*network));return true;}
bool marvin_owner_setup_linked(void){return atomic_load(&linked_view);}
marvin_redeem_status_t marvin_owner_setup_redeem(const char *ca){
 if(!ready||!journal.current.pending)return MARVIN_REDEEM_INVALID;
 if(!marvin_journal_submitted(&journal))return MARVIN_REDEEM_RETRY;
 wifi_config_t network;if(!marvin_owner_setup_pending(&network))return MARVIN_REDEEM_INVALID;
 char ssid[33];memcpy(ssid,network.sta.ssid,32);ssid[32]=0;mbedtls_platform_zeroize(&network,sizeof(network));
 marvin_redeem_receipt_t receipt;bool claim=!journal.current.network_change;
 marvin_redeem_status_t result=marvin_enrollment_redeem(issuer,ca,journal.current.ticket,ssid,journal.current.pending_epoch,claim,&receipt);
 if(result==MARVIN_REDEEM_OK&&!marvin_journal_complete(&journal,journal.current.pending_epoch,claim?receipt.credential:NULL,receipt.expires_ms))result=MARVIN_REDEEM_RETRY;
 atomic_store(&linked_view,journal.current.linked);mbedtls_platform_zeroize(&receipt,sizeof(receipt));return result;
}
bool marvin_owner_setup_cancel(void){return ready&&marvin_journal_cancel(&journal);}
bool marvin_owner_setup_reset_local_development(void){
#ifdef CONFIG_MARVIN_LOCAL_DEV_MODE
 nvs_handle_t h;if(nvs_open("ownership",NVS_READWRITE,&h)!=ESP_OK)return false;
 esp_err_t err=nvs_erase_all(h);if(err==ESP_OK)err=nvs_commit(h);nvs_close(h);
 if(err!=ESP_OK)return false;
 marvin_owner_setup_disconnected();atomic_store(&linked_view,false);return true;
#else
 return false;
#endif
}

bool marvin_owner_setup_submitted(void){return ready&&journal.current.pending&&journal.current.submitted;}
bool marvin_owner_setup_identity(marvin_link_identity_t *identity){
 if(!identity||!ready||!journal.current.linked||journal.current.pending)return false;
 memset(identity,0,sizeof(*identity));
 memcpy(identity->origin,issuer,strlen(issuer)+1);
 snprintf(identity->device_id,sizeof(identity->device_id),"%s",marvin_identity_device_id());
 memcpy(identity->credential,journal.current.credential,sizeof(identity->credential));
 identity->epoch=journal.current.epoch;identity->expires_ms=journal.current.credential_expires_ms;return true;
}
