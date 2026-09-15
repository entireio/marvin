#include "board_audio.h"
#include "ota_runtime.h"
#include "body_audio.h"
#include "device_identity.h"
#include "owner_setup.h"
/* Marvin M0 bench firmware. No actuator pins are configured. */
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <sys/time.h>
#include <math.h>
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/semphr.h"
#include "freertos/queue.h"
#include "freertos/event_groups.h"
#include "nvs_flash.h"
#include "nvs.h"
#include "esp_wifi.h"
#include "esp_event.h"
#include "esp_netif.h"
#include "esp_netif_sntp.h"
#include "esp_http_client.h"
#include "esp_crt_bundle.h"
#include "esp_log.h"
#include "protocomm.h"
#include "protocomm_ble.h"
#include "protocomm_security2.h"
#include "cJSON.h"

#ifdef CONFIG_MARVIN_OWNER_ENROLLMENT
static const bool owner_mode=true;
#else
static const bool owner_mode=false;
#endif
#define MAX_APS 32
#define IP_READY BIT0
static SemaphoreHandle_t lock;
static QueueHandle_t jobs;
static EventGroupHandle_t events;
static wifi_ap_record_t aps[MAX_APS];
static uint16_t ap_count;
static uint32_t scan_generation;
static wifi_config_t committed;
static bool has_committed;
static char phase[32]="idle";
static char failure[40]="";
static char salt[16], verifier[384];
/* Bench deployment trust is provisioned locally in factory NVS, never learned from HTTP. */
static char probe_url[256]=CONFIG_MARVIN_PROBE_URL;
static char probe_ca[2048];
static bool setup_clock_seeded;
static const char *probe_failure="BACKEND_UNREACHABLE";
static protocomm_security2_params_t security;
typedef struct { int operation; wifi_config_t candidate; } job_t;

static void state(const char *next, const char *error) {
    xSemaphoreTake(lock, portMAX_DELAY);
    snprintf(phase,sizeof(phase),"%s",next);
    snprintf(failure,sizeof(failure),"%s",error ? error : "");
    xSemaphoreGive(lock);
}
static void wifi_event(void *arg, esp_event_base_t base, int32_t id, void *data) {
    if (base==IP_EVENT && id==IP_EVENT_STA_GOT_IP) xEventGroupSetBits(events,IP_READY);
    if (base==WIFI_EVENT && id==WIFI_EVENT_STA_DISCONNECTED) xEventGroupClearBits(events,IP_READY);
}
static bool connect_config(const wifi_config_t *config) {
    esp_wifi_disconnect();
    /* Drain the previous connection's event before testing the candidate. */
    vTaskDelay(pdMS_TO_TICKS(200));
    xEventGroupClearBits(events,IP_READY);
    if (esp_wifi_set_config(WIFI_IF_STA,(wifi_config_t *)config)!=ESP_OK || esp_wifi_connect()!=ESP_OK) return false;
    return (xEventGroupWaitBits(events,IP_READY,pdFALSE,pdFALSE,pdMS_TO_TICKS(20000)) & IP_READY)!=0;
}
static bool probe(void) {
    if(!setup_clock_seeded){
        esp_sntp_config_t time_config=ESP_NETIF_SNTP_DEFAULT_CONFIG("pool.ntp.org");
        if(esp_netif_sntp_init(&time_config)!=ESP_OK){probe_failure="CLOCK_SYNC_FAILED";return false;}
        bool synced=esp_netif_sntp_sync_wait(pdMS_TO_TICKS(15000))==ESP_OK;
        esp_netif_sntp_deinit();
        if(!synced){probe_failure="CLOCK_SYNC_FAILED";return false;}
    }
    esp_http_client_config_t cfg={.url=probe_url,.timeout_ms=10000,.crt_bundle_attach=probe_ca[0]?NULL:esp_crt_bundle_attach,.cert_pem=probe_ca[0]?probe_ca:NULL,.disable_auto_redirect=true};
    esp_http_client_handle_t client=esp_http_client_init(&cfg);
    if (!client) return false;
    esp_err_t request_error=esp_http_client_perform(client);
    bool ok=request_error==ESP_OK && esp_http_client_get_status_code(client)==200;
    probe_failure=request_error!=ESP_OK?"TLS_OR_TRANSPORT_FAILED":"BACKEND_HTTP_FAILED";
    esp_http_client_cleanup(client);
    return ok;
}
static bool save(const wifi_config_t *candidate) {
    nvs_handle_t h;
    if (nvs_open("network",NVS_READWRITE,&h)!=ESP_OK) return false;
    esp_err_t err=nvs_set_blob(h,"committed",candidate,sizeof(*candidate));
    if(err==ESP_OK) err=nvs_commit(h);
    nvs_close(h);
    return err==ESP_OK;
}
static void worker(void *arg) {
    job_t job;
    while(xQueueReceive(jobs,&job,portMAX_DELAY)) {
        if(job.operation==1) {
            wifi_scan_config_t config={.show_hidden=false};
            esp_err_t err=esp_wifi_scan_start(&config,true);
            xSemaphoreTake(lock,portMAX_DELAY);
            ap_count=MAX_APS;
            if(err==ESP_OK) err=esp_wifi_scan_get_ap_records(&ap_count,aps);
            if(err!=ESP_OK) ap_count=0;
            scan_generation++;
            xSemaphoreGive(lock);
            state(err==ESP_OK?"scan_complete":"failed",err==ESP_OK?NULL:"SCAN_FAILED");
        } else {
            const char *error=NULL;
            if(!connect_config(&job.candidate)) error="WIFI_CONNECTION_FAILED";
            else { state("checking_backend",NULL); if(!probe()) error=probe_failure; }
            if(!error && owner_mode){
                state("linking",NULL);marvin_redeem_status_t result=marvin_owner_setup_redeem(probe_ca);
                if(result!=MARVIN_REDEEM_OK){error=result==MARVIN_REDEEM_REJECTED?"ENROLLMENT_REVOKED":"ENROLLMENT_RETRY";if(result==MARVIN_REDEEM_REJECTED)marvin_owner_setup_cancel();}
            }
            if(!error && !owner_mode && !save(&job.candidate)) error="STORAGE_FAILED";
            if(error) {
                wifi_config_t pending;
                if(owner_mode&&marvin_owner_setup_pending(&pending)){
                    memset(&pending,0,sizeof(pending));
                    if(marvin_owner_setup_submitted()||!marvin_owner_setup_cancel()){state("awaiting_backend",error);memset(&job,0,sizeof(job));continue;}
                }
                state("restoring_previous",error);
                bool restored=has_committed && connect_config(&committed);
                if(!has_committed) esp_wifi_disconnect();
                state("failed",has_committed && !restored?"PREVIOUS_NETWORK_UNAVAILABLE":error);
            } else {
                xSemaphoreTake(lock,portMAX_DELAY);
                memcpy(&committed,&job.candidate,sizeof(committed));has_committed=true;
                xSemaphoreGive(lock);
                state("network_verified",NULL);
            }
            memset(&job,0,sizeof(job));
        }
    }
}
static bool busy(void) {
    return !strcmp(phase,"scanning") || !strcmp(phase,"connecting") || !strcmp(phase,"checking_backend") || !strcmp(phase,"linking") || !strcmp(phase,"restoring_previous");
}
static bool link_snapshot(marvin_link_identity_t *identity){
    xSemaphoreTake(lock,portMAX_DELAY);
    bool ready=!busy()&&(xEventGroupGetBits(events)&IP_READY)&&marvin_owner_setup_identity(identity);
    xSemaphoreGive(lock);return ready;
}
static esp_err_t control(uint32_t session,const uint8_t *input,ssize_t length,uint8_t **output,ssize_t *out_length,void *arg) {
    if(length<2 || length>384 || memchr(input,0,length)) return ESP_ERR_INVALID_ARG;
    char buffer[385];memcpy(buffer,input,length);buffer[length]=0;
    cJSON *request=cJSON_ParseWithOpts(buffer,NULL,true), *reply=cJSON_CreateObject();
    cJSON *op=cJSON_GetObjectItemCaseSensitive(request,"op");
    if(!request || !reply || !cJSON_IsString(op)) { cJSON_Delete(request);cJSON_Delete(reply);memset(buffer,0,sizeof(buffer));return ESP_ERR_INVALID_ARG; }
    xSemaphoreTake(lock,portMAX_DELAY);
    cJSON_AddNumberToObject(reply,"version",1);
    if(owner_mode&&!busy()&&marvin_owner_setup_control(session,request,reply)) {
        /* Authenticated owner ticket commands are handled before bench commands. */
    } else if(!strcmp(op->valuestring,"clock") && !busy()) {
        cJSON *utc=cJSON_GetObjectItemCaseSensitive(request,"utcMs");
        if(!cJSON_IsNumber(utc)||!isfinite(utc->valuedouble)||floor(utc->valuedouble)!=utc->valuedouble||utc->valuedouble<1577836800000.0||utc->valuedouble>4102444800000.0)cJSON_AddStringToObject(reply,"error","INVALID_CLOCK");
        else {struct timeval now={.tv_sec=(time_t)(utc->valuedouble/1000),.tv_usec=(suseconds_t)((uint64_t)utc->valuedouble%1000)*1000};setup_clock_seeded=settimeofday(&now,NULL)==0;cJSON_AddBoolToObject(reply,"clockSet",setup_clock_seeded);}
    } else if(!strcmp(op->valuestring,"challenge") && !busy()) {
        cJSON *operation=cJSON_GetObjectItemCaseSensitive(request,"operation"),*issued=cJSON_GetObjectItemCaseSensitive(request,"issuedAt");
        if(!cJSON_IsString(operation)||!cJSON_IsNumber(issued)||marvin_identity_challenge(operation->valuestring,issued->valuedouble,reply)!=ESP_OK)cJSON_AddStringToObject(reply,"error","IDENTITY_CHALLENGE_UNAVAILABLE");
    } else if(!strcmp(op->valuestring,"status")) {
        cJSON_AddStringToObject(reply,"phase",phase);cJSON_AddStringToObject(reply,"error",failure);
        cJSON_AddBoolToObject(reply,"hasSavedNetwork",has_committed);
        if(has_committed){char saved[33];memcpy(saved,committed.sta.ssid,32);saved[32]=0;cJSON_AddStringToObject(reply,"savedNetwork",saved);}
        cJSON_AddBoolToObject(reply,"networkConnected",(xEventGroupGetBits(events)&IP_READY)!=0);
        cJSON_AddBoolToObject(reply,"linked",owner_mode&&marvin_owner_setup_linked());
        cJSON_AddBoolToObject(reply,"backendConnected",owner_mode&&marvin_device_link_online());
        if(owner_mode&&!busy()){wifi_config_t pending;if(marvin_owner_setup_pending(&pending)){char name[33];memcpy(name,pending.sta.ssid,32);name[32]=0;cJSON_AddStringToObject(reply,"pendingNetwork",name);}memset(&pending,0,sizeof(pending));}
        cJSON_AddNumberToObject(reply,"scanGeneration",scan_generation);cJSON_AddNumberToObject(reply,"count",ap_count);
    } else if(owner_mode&&!strcmp(op->valuestring,"resume")&&!busy()) {
        job_t job={.operation=3};
        if(!marvin_owner_setup_pending(&job.candidate))cJSON_AddStringToObject(reply,"error","NO_PENDING_SETUP");
        else if(xQueueSend(jobs,&job,0)==pdTRUE){snprintf(phase,sizeof(phase),"connecting");failure[0]=0;cJSON_AddStringToObject(reply,"phase",phase);}
        else cJSON_AddStringToObject(reply,"error","BUSY");
        memset(&job,0,sizeof(job));
    } else if(!strcmp(op->valuestring,"scan") && !busy()) {
        job_t job={.operation=1};
        snprintf(phase,sizeof(phase),"scanning");failure[0]=0;
        if(xQueueSend(jobs,&job,0)!=pdTRUE) snprintf(phase,sizeof(phase),"failed");
        cJSON_AddStringToObject(reply,"phase",phase);
    } else if(!strcmp(op->valuestring,"network") && !busy()) {
        cJSON *index=cJSON_GetObjectItemCaseSensitive(request,"index");
        if(cJSON_IsNumber(index) && index->valuedouble==index->valueint && index->valueint>=0 && index->valueint<ap_count) {
            wifi_ap_record_t *ap=&aps[index->valueint];
            cJSON_AddStringToObject(reply,"source","robot");cJSON_AddStringToObject(reply,"ssid",(char *)ap->ssid);
            cJSON_AddStringToObject(reply,"security",ap->authmode==WIFI_AUTH_OPEN?"open":ap->authmode==WIFI_AUTH_WPA3_PSK?"wpa3-personal":(ap->authmode==WIFI_AUTH_WPA2_PSK||ap->authmode==WIFI_AUTH_WPA_WPA2_PSK)?"wpa2-personal":"unsupported");
            cJSON_AddNumberToObject(reply,"rssi",ap->rssi);cJSON_AddNumberToObject(reply,"channel",ap->primary);
            cJSON_AddNumberToObject(reply,"scanGeneration",scan_generation);
            cJSON_AddBoolToObject(reply,"supported",ap->authmode==WIFI_AUTH_OPEN || ap->authmode==WIFI_AUTH_WPA2_PSK || ap->authmode==WIFI_AUTH_WPA_WPA2_PSK);
        } else cJSON_AddStringToObject(reply,"error","INVALID_NETWORK");
    } else if((!strcmp(op->valuestring,"apply")||!strcmp(op->valuestring,"apply_hidden")) && !busy()) {
        cJSON *index=cJSON_GetObjectItemCaseSensitive(request,"index"), *password=cJSON_GetObjectItemCaseSensitive(request,"password"), *generation=cJSON_GetObjectItemCaseSensitive(request,"scanGeneration");
        bool hidden=!strcmp(op->valuestring,"apply_hidden");cJSON *ssid=cJSON_GetObjectItemCaseSensitive(request,"ssid"),*security_mode=cJSON_GetObjectItemCaseSensitive(request,"security");
        if(strncmp(probe_url,"https://",8)) cJSON_AddStringToObject(reply,"error","BACKEND_NOT_CONFIGURED");
        else if(!cJSON_IsString(password) || strlen(password->valuestring)>63 || (hidden?(!cJSON_IsString(ssid)||!strlen(ssid->valuestring)||strlen(ssid->valuestring)>32||!cJSON_IsString(security_mode)||strcmp(security_mode->valuestring,"wpa2-personal")):(!cJSON_IsNumber(index)||index->valuedouble!=index->valueint||index->valueint<0||index->valueint>=ap_count||!cJSON_IsNumber(generation)||generation->valuedouble!=scan_generation))) cJSON_AddStringToObject(reply,"error","INVALID_REQUEST");
        else {
            wifi_ap_record_t hidden_ap={0},*ap;if(hidden){memcpy(hidden_ap.ssid,ssid->valuestring,strlen(ssid->valuestring));hidden_ap.authmode=WIFI_AUTH_WPA2_PSK;ap=&hidden_ap;}else ap=&aps[index->valueint];size_t len=strlen(password->valuestring);bool open=ap->authmode==WIFI_AUTH_OPEN;
            if((!open && ap->authmode!=WIFI_AUTH_WPA2_PSK && ap->authmode!=WIFI_AUTH_WPA_WPA2_PSK) || (open?len!=0:len<8)) cJSON_AddStringToObject(reply,"error","UNSUPPORTED_CREDENTIALS");
            else {
                job_t job={.operation=2};memcpy(job.candidate.sta.ssid,ap->ssid,sizeof(job.candidate.sta.ssid));
                memcpy(job.candidate.sta.password,password->valuestring,len);
                /* Choose the network rather than pinning one access point. Mesh
                 * and multi-AP networks may move Marvin to another BSSID. */
                job.candidate.sta.bssid_set=false;
                job.candidate.sta.threshold.authmode=open?WIFI_AUTH_OPEN:WIFI_AUTH_WPA2_PSK;
                if(owner_mode&&!marvin_owner_setup_stage(session,&job.candidate))cJSON_AddStringToObject(reply,"error","OWNER_TICKET_REQUIRED");
                else if(xQueueSend(jobs,&job,0)==pdTRUE) { snprintf(phase,sizeof(phase),"connecting");failure[0]=0;cJSON_AddStringToObject(reply,"phase",phase); }
                else {if(owner_mode)marvin_owner_setup_cancel();cJSON_AddStringToObject(reply,"error","BUSY");}
                memset(&job,0,sizeof(job));
            }
        }
        if(cJSON_IsString(password)) memset(password->valuestring,0,strlen(password->valuestring));
    } else cJSON_AddStringToObject(reply,"error",busy()?"BUSY":"UNKNOWN_OPERATION");
    xSemaphoreGive(lock);
    char *serialized=cJSON_PrintUnformatted(reply);cJSON_Delete(reply);cJSON_Delete(request);memset(buffer,0,sizeof(buffer));
    if(!serialized) return ESP_ERR_NO_MEM;
    *output=(uint8_t *)serialized;*out_length=strlen(serialized);return ESP_OK;
}
static void ble_lifecycle(void *arg,esp_event_base_t base,int32_t id,void *data){
    (void)arg;(void)base;(void)data;
    if(owner_mode&&id==PROTOCOMM_TRANSPORT_BLE_DISCONNECTED){xSemaphoreTake(lock,portMAX_DELAY);marvin_owner_setup_disconnected();xSemaphoreGive(lock);}
}
#ifdef CONFIG_MARVIN_BODY_AUDIO
static void audio_console(void *unused){
    (void)unused;setvbuf(stdout,NULL,_IONBF,0);
    printf("Marvin audio: v starts microphone/provider; x stops and mutes; i interrupts playback. No microphone upload while idle.\n");
    #ifdef CONFIG_MARVIN_SIGNED_OTA
    bool update_line=false,invalid=false;uint32_t sequence=0;unsigned digits=0;
#endif
    for(;;){int c=getchar();
#ifdef CONFIG_MARVIN_SIGNED_OTA
      if(update_line){
        if(c=='x'){marvin_ota_cancel();marvin_device_voice_stop();update_line=false;continue;}
        if(c=='\n'){bool accepted=!invalid&&digits&&marvin_ota_request(sequence);printf("{\"update\":\"%s\"}\n",accepted?"requested":"request_refused");update_line=false;continue;}
        if(c>='0'&&c<='9'){unsigned digit=c-'0';if(++digits>10||sequence>(UINT32_MAX-digit)/10)invalid=true;else sequence=sequence*10+digit;continue;}
        if(c!='\r'&&c!=EOF)invalid=true;
        if(c==EOF){clearerr(stdin);vTaskDelay(pdMS_TO_TICKS(20));}
        continue;
      }else if(c=='u'){update_line=true;invalid=false;sequence=digits=0;continue;}
#endif
      if(c=='x')marvin_ota_cancel();
      if(c=='w'){marvin_body_wake_activation(false);marvin_device_voice_stop();}else if(c=='W')marvin_body_wake_activation(true);else if(c=='+')marvin_body_adjust_volume(5);else if(c=='-')marvin_body_adjust_volume(-5);else if(c=='v')marvin_device_voice_start();else if(c=='x')marvin_device_voice_stop();else if(c=='i')marvin_device_voice_interrupt();else if(c=='s'){marvin_body_audio_status();marvin_device_link_status();}else{if(c==EOF)clearerr(stdin);vTaskDelay(pdMS_TO_TICKS(20));}}
}
#endif
void app_main(void) {
#ifdef CONFIG_MARVIN_WAVESHARE_AUDIO_DIAGNOSTIC
    marvin_audio_diagnostic();return;
#endif
    /* Never erase NVS automatically on a version/space error. Preserve recovery data. */
    ESP_ERROR_CHECK(nvs_flash_init());ESP_ERROR_CHECK(esp_netif_init());ESP_ERROR_CHECK(esp_event_loop_create_default());
    lock=xSemaphoreCreateMutex();jobs=xQueueCreate(1,sizeof(job_t));events=xEventGroupCreate();assert(lock && jobs && events);
    #ifdef CONFIG_MARVIN_SIGNED_OTA
    ESP_ERROR_CHECK(marvin_ota_start(link_snapshot,probe_ca));
#endif
    esp_netif_create_default_wifi_sta();wifi_init_config_t init=WIFI_INIT_CONFIG_DEFAULT();ESP_ERROR_CHECK(esp_wifi_init(&init));
    ESP_ERROR_CHECK(esp_wifi_set_storage(WIFI_STORAGE_RAM));ESP_ERROR_CHECK(esp_wifi_set_mode(WIFI_MODE_STA));
    ESP_ERROR_CHECK(esp_event_handler_register(WIFI_EVENT,ESP_EVENT_ANY_ID,wifi_event,NULL));ESP_ERROR_CHECK(esp_event_handler_register(IP_EVENT,IP_EVENT_STA_GOT_IP,wifi_event,NULL));
    ESP_ERROR_CHECK(esp_wifi_start());
    nvs_handle_t h;size_t len=sizeof(committed);
    if(nvs_open("network",NVS_READONLY,&h)==ESP_OK) {has_committed=nvs_get_blob(h,"committed",&committed,&len)==ESP_OK && len==sizeof(committed);nvs_close(h);}
    if(has_committed&&!owner_mode) connect_config(&committed);
    ESP_ERROR_CHECK(nvs_flash_init_partition("factory"));
    if(nvs_open_from_partition("factory","identity",NVS_READONLY,&h)!=ESP_OK) {ESP_LOGE("marvin","Unique factory SRP credentials missing; BLE disabled");return;}
    size_t url_len=sizeof(probe_url),ca_len=sizeof(probe_ca);
    if(nvs_get_str(h,"probe_url",probe_url,&url_len)!=ESP_OK)snprintf(probe_url,sizeof(probe_url),"%s",CONFIG_MARVIN_PROBE_URL);
    if(nvs_get_blob(h,"probe_ca",probe_ca,&ca_len)!=ESP_OK || ca_len>=sizeof(probe_ca))memset(probe_ca,0,sizeof(probe_ca));else probe_ca[ca_len]=0;
    size_t sl=sizeof(salt),vl=sizeof(verifier);
    bool valid=nvs_get_blob(h,"salt",salt,&sl)==ESP_OK && nvs_get_blob(h,"verifier",verifier,&vl)==ESP_OK && sl==sizeof(salt) && vl==sizeof(verifier);nvs_close(h);
    if(!valid) {ESP_LOGE("marvin","Invalid factory SRP credentials; BLE disabled");return;}
    if(marvin_identity_init()!=ESP_OK)ESP_LOGW("marvin","Device signing identity missing; enrollment disabled");
    if(owner_mode){
        if(marvin_owner_setup_init()!=ESP_OK){ESP_LOGE("marvin","Owner trust or journal unavailable; setup disabled");return;}
        has_committed=marvin_owner_setup_network(&committed);if(has_committed&&connect_config(&committed))probe();
    }
    assert(xTaskCreate(worker,"network_setup",10240,NULL,5,NULL)==pdPASS);
    if(owner_mode){job_t pending={.operation=3};if(marvin_owner_setup_pending(&pending.candidate)){snprintf(phase,sizeof(phase),"connecting");assert(xQueueSend(jobs,&pending,0)==pdTRUE);}memset(&pending,0,sizeof(pending));}
    ESP_ERROR_CHECK(esp_event_handler_register(PROTOCOMM_TRANSPORT_BLE_EVENT,ESP_EVENT_ANY_ID,ble_lifecycle,NULL));
    security=(protocomm_security2_params_t){.salt=salt,.salt_len=sizeof(salt),.verifier=verifier,.verifier_len=sizeof(verifier)};
    protocomm_t *pc=protocomm_new();assert(pc);
    static protocomm_ble_name_uuid_t endpoints[]={{"proto-ver",0xff51},{"prov-session",0xff52},{"marvin-control",0xff53}};
    protocomm_ble_config_t ble={.device_name="Marvin setup",.service_uuid={0xfb,0x34,0x9b,0x5f,0x80,0x00,0x00,0x80,0x00,0x10,0x00,0x00,0x50,0xff,0x00,0x00},.nu_lookup_count=3,.nu_lookup=endpoints};
    ESP_ERROR_CHECK(protocomm_ble_start(pc,&ble));
    ESP_ERROR_CHECK(protocomm_set_version(pc,"proto-ver",owner_mode?"{\"marvin\":1,\"security\":2,\"patch\":1,\"enrollment\":1,\"reconciliation\":1}":"{\"marvin\":1,\"security\":2,\"patch\":1,\"bench\":true}"));
    ESP_ERROR_CHECK(protocomm_set_security(pc,"prov-session",&protocomm_security2,&security));
    ESP_ERROR_CHECK(protocomm_add_endpoint(pc,"marvin-control",control,NULL));
    /* Finish BLE allocation before audio and TLS startup, avoiding simultaneous
     * transient allocations. A missing audio assembly must preserve setup. */
#ifdef CONFIG_MARVIN_BODY_AUDIO
    if(marvin_body_audio_init()==ESP_OK)assert(xTaskCreate(audio_console,"audio_console",6144,NULL,3,NULL)==pdPASS);
    else ESP_LOGE("marvin","Audio hardware unavailable; voice capability disabled");
#endif
    if(owner_mode)ESP_ERROR_CHECK(marvin_device_link_start(link_snapshot,probe_ca));
    /* NimBLE resumes advertising after every disconnect. Keep the transport
     * available so an interrupted setup never requires a power cycle. Security2
     * and the signed owner ticket continue to gate privileged operations. */
    for(;;)vTaskDelay(pdMS_TO_TICKS(3600000));
}
