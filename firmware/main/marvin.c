/* Marvin M0 bench firmware. No actuator pins are configured. */
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
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
    esp_sntp_config_t time_config=ESP_NETIF_SNTP_DEFAULT_CONFIG("pool.ntp.org");
    if(esp_netif_sntp_init(&time_config)!=ESP_OK) return false;
    bool synced=esp_netif_sntp_sync_wait(pdMS_TO_TICKS(15000))==ESP_OK;
    esp_netif_sntp_deinit();
    if(!synced) return false;
    esp_http_client_config_t cfg={.url=CONFIG_MARVIN_PROBE_URL,.timeout_ms=10000,.crt_bundle_attach=esp_crt_bundle_attach,.disable_auto_redirect=true};
    esp_http_client_handle_t client=esp_http_client_init(&cfg);
    if (!client) return false;
    bool ok=esp_http_client_perform(client)==ESP_OK && esp_http_client_get_status_code(client)==200;
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
            else { state("checking_backend",NULL); if(!probe()) error="BACKEND_UNREACHABLE"; }
            if(!error && !save(&job.candidate)) error="STORAGE_FAILED";
            if(error) {
                state("restoring_previous",error);
                if(has_committed) connect_config(&committed); else esp_wifi_disconnect();
                state("failed",error);
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
    return !strcmp(phase,"scanning") || !strcmp(phase,"connecting") || !strcmp(phase,"checking_backend") || !strcmp(phase,"restoring_previous");
}
static esp_err_t control(uint32_t session,const uint8_t *input,ssize_t length,uint8_t **output,ssize_t *out_length,void *arg) {
    if(length<2 || length>384 || memchr(input,0,length)) return ESP_ERR_INVALID_ARG;
    char buffer[385];memcpy(buffer,input,length);buffer[length]=0;
    cJSON *request=cJSON_ParseWithOpts(buffer,NULL,true), *reply=cJSON_CreateObject();
    cJSON *op=cJSON_GetObjectItemCaseSensitive(request,"op");
    if(!request || !reply || !cJSON_IsString(op)) { cJSON_Delete(request);cJSON_Delete(reply);memset(buffer,0,sizeof(buffer));return ESP_ERR_INVALID_ARG; }
    xSemaphoreTake(lock,portMAX_DELAY);
    cJSON_AddNumberToObject(reply,"version",1);
    if(!strcmp(op->valuestring,"status")) {
        cJSON_AddStringToObject(reply,"phase",phase);cJSON_AddStringToObject(reply,"error",failure);
        cJSON_AddBoolToObject(reply,"hasSavedNetwork",has_committed);
        cJSON_AddBoolToObject(reply,"linked",false);
        cJSON_AddNumberToObject(reply,"scanGeneration",scan_generation);cJSON_AddNumberToObject(reply,"count",ap_count);
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
            cJSON_AddNumberToObject(reply,"rssi",ap->rssi);cJSON_AddNumberToObject(reply,"channel",ap->primary);
            cJSON_AddNumberToObject(reply,"scanGeneration",scan_generation);
            cJSON_AddBoolToObject(reply,"supported",ap->authmode==WIFI_AUTH_OPEN || ap->authmode==WIFI_AUTH_WPA2_PSK || ap->authmode==WIFI_AUTH_WPA_WPA2_PSK);
        } else cJSON_AddStringToObject(reply,"error","INVALID_NETWORK");
    } else if(!strcmp(op->valuestring,"apply") && !busy()) {
        cJSON *index=cJSON_GetObjectItemCaseSensitive(request,"index"), *password=cJSON_GetObjectItemCaseSensitive(request,"password"), *generation=cJSON_GetObjectItemCaseSensitive(request,"scanGeneration");
        if(strncmp(CONFIG_MARVIN_PROBE_URL,"https://",8)) cJSON_AddStringToObject(reply,"error","BACKEND_NOT_CONFIGURED");
        else if(!cJSON_IsNumber(index) || index->valuedouble!=index->valueint || index->valueint<0 || index->valueint>=ap_count || !cJSON_IsNumber(generation) || generation->valuedouble!=scan_generation || !cJSON_IsString(password) || strlen(password->valuestring)>63) cJSON_AddStringToObject(reply,"error","INVALID_REQUEST");
        else {
            wifi_ap_record_t *ap=&aps[index->valueint];size_t len=strlen(password->valuestring);
            bool open=ap->authmode==WIFI_AUTH_OPEN;
            if((!open && ap->authmode!=WIFI_AUTH_WPA2_PSK && ap->authmode!=WIFI_AUTH_WPA_WPA2_PSK) || (open?len!=0:len<8)) cJSON_AddStringToObject(reply,"error","UNSUPPORTED_CREDENTIALS");
            else {
                job_t job={.operation=2};memcpy(job.candidate.sta.ssid,ap->ssid,sizeof(job.candidate.sta.ssid));
                memcpy(job.candidate.sta.password,password->valuestring,len);
                memcpy(job.candidate.sta.bssid,ap->bssid,6);job.candidate.sta.bssid_set=true;
                job.candidate.sta.threshold.authmode=open?WIFI_AUTH_OPEN:WIFI_AUTH_WPA2_PSK;
                if(xQueueSend(jobs,&job,0)==pdTRUE) { snprintf(phase,sizeof(phase),"connecting");failure[0]=0;cJSON_AddStringToObject(reply,"phase",phase); }
                else cJSON_AddStringToObject(reply,"error","BUSY");
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
void app_main(void) {
    /* Never erase NVS automatically on a version/space error. Preserve recovery data. */
    ESP_ERROR_CHECK(nvs_flash_init());ESP_ERROR_CHECK(esp_netif_init());ESP_ERROR_CHECK(esp_event_loop_create_default());
    lock=xSemaphoreCreateMutex();jobs=xQueueCreate(1,sizeof(job_t));events=xEventGroupCreate();assert(lock && jobs && events);
    esp_netif_create_default_wifi_sta();wifi_init_config_t init=WIFI_INIT_CONFIG_DEFAULT();ESP_ERROR_CHECK(esp_wifi_init(&init));
    ESP_ERROR_CHECK(esp_wifi_set_storage(WIFI_STORAGE_RAM));ESP_ERROR_CHECK(esp_wifi_set_mode(WIFI_MODE_STA));
    ESP_ERROR_CHECK(esp_event_handler_register(WIFI_EVENT,ESP_EVENT_ANY_ID,wifi_event,NULL));ESP_ERROR_CHECK(esp_event_handler_register(IP_EVENT,IP_EVENT_STA_GOT_IP,wifi_event,NULL));
    ESP_ERROR_CHECK(esp_wifi_start());
    nvs_handle_t h;size_t len=sizeof(committed);
    if(nvs_open("network",NVS_READONLY,&h)==ESP_OK) {has_committed=nvs_get_blob(h,"committed",&committed,&len)==ESP_OK && len==sizeof(committed);nvs_close(h);}
    if(has_committed) connect_config(&committed);
    ESP_ERROR_CHECK(nvs_flash_init_partition("factory"));
    if(nvs_open_from_partition("factory","identity",NVS_READONLY,&h)!=ESP_OK) {ESP_LOGE("marvin","Unique factory SRP credentials missing; BLE disabled");return;}
    size_t sl=sizeof(salt),vl=sizeof(verifier);
    bool valid=nvs_get_blob(h,"salt",salt,&sl)==ESP_OK && nvs_get_blob(h,"verifier",verifier,&vl)==ESP_OK && sl==sizeof(salt) && vl==sizeof(verifier);nvs_close(h);
    if(!valid) {ESP_LOGE("marvin","Invalid factory SRP credentials; BLE disabled");return;}
    assert(xTaskCreate(worker,"network_setup",6144,NULL,5,NULL)==pdPASS);
    security=(protocomm_security2_params_t){.salt=salt,.salt_len=sizeof(salt),.verifier=verifier,.verifier_len=sizeof(verifier)};
    protocomm_t *pc=protocomm_new();assert(pc);
    static protocomm_ble_name_uuid_t endpoints[]={{"proto-ver",0xff51},{"prov-session",0xff52},{"marvin-control",0xff53}};
    protocomm_ble_config_t ble={.device_name="Marvin setup",.service_uuid={0xfb,0x34,0x9b,0x5f,0x80,0x00,0x00,0x80,0x00,0x10,0x00,0x00,0x50,0xff,0x00,0x00},.nu_lookup_count=3,.nu_lookup=endpoints};
    ESP_ERROR_CHECK(protocomm_ble_start(pc,&ble));
    ESP_ERROR_CHECK(protocomm_set_version(pc,"proto-ver","{\"marvin\":1,\"security\":2,\"bench\":true}"));
    ESP_ERROR_CHECK(protocomm_set_security(pc,"prov-session",&protocomm_security2,&security));
    ESP_ERROR_CHECK(protocomm_add_endpoint(pc,"marvin-control",control,NULL));
    /* Bench physical presence is a power cycle. M7 adds owner-bound authorization. */
    vTaskDelay(pdMS_TO_TICKS(300000));
    ESP_ERROR_CHECK(protocomm_ble_stop(pc));protocomm_delete(pc);
}
