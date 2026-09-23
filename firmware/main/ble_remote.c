#include "ble_remote.h"
#include "gear_vr_controller.h"
#include "body_audio.h"
#include "sdkconfig.h"

/* The ET-YO324's useful input stream is Samsung's custom GATT service, not
 * the standard HID service advertised by some retail listings.  This client
 * deliberately shares the already-running NimBLE host used by setup BLE. */
#ifdef CONFIG_MARVIN_BLE_REMOTE
#include <string.h>
#include <stdatomic.h>
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "esp_log.h"
#include "nvs.h"
#include "host/ble_hs.h"
#include "host/ble_gap.h"
#include "host/ble_gatt.h"
#include "host/util/util.h"
#include "os/os_mbuf.h"

#define TAG "gear_vr_ble"
#define REMOTE_NAMESPACE "gear_remote"
#define REMOTE_KEY "peer"
#define SCAN_SECONDS 5
#define PAIR_SCAN_SECONDS 45
#define RETRY_SECONDS 20
typedef struct __attribute__((packed)) { uint32_t magic; uint8_t addr_type; uint8_t addr[6]; } remote_peer_t;
#define REMOTE_MAGIC 0x47565231u

static const ble_uuid128_t service_uuid = BLE_UUID128_INIT(0x65,0x74,0x6f,0x6d,0x65,0x65,0x72,0x68,0x54,0x20,0x73,0x75,0x6c,0x75,0x63,0x4f);
static const ble_uuid128_t notify_uuid = BLE_UUID128_INIT(0x81,0xd2,0xa3,0x4e,0xa1,0xf7,0x52,0xa0,0x3b,0x48,0xbc,0x81,0x26,0x17,0xc5,0xc8);
static const ble_uuid128_t command_uuid = BLE_UUID128_INIT(0x82,0xd2,0xa3,0x4e,0xa1,0xf7,0x52,0xa0,0x3b,0x48,0xbc,0x81,0x26,0x17,0xc5,0xc8);
static remote_peer_t saved_peer;
static bool have_saved_peer, scanning, connecting, connected, enrolled, pairing_window_open;
static atomic_bool pairing_requested,unpair_requested;
static uint8_t own_addr_type;
static uint16_t conn_handle = BLE_HS_CONN_HANDLE_NONE, service_start, service_end, notify_handle, command_def_handle, command_handle, cccd_handle;
static ble_addr_t candidate_addr;
static TickType_t next_scan, pairing_deadline;
static int gap_event(struct ble_gap_event *event, void *arg);
static void begin_scan(bool pairing_window);

static bool addr_matches(const ble_addr_t *addr) { return have_saved_peer && addr->type == saved_peer.addr_type && !memcmp(addr->val, saved_peer.addr, sizeof(addr->val)); }
static bool is_controller_advertisement(const struct ble_gap_disc_desc *disc) {
    struct ble_hs_adv_fields fields; static const char name[]="Gear VR Controller";
    return !ble_hs_adv_parse_fields(&fields,disc->data,disc->length_data) && fields.name && fields.name_len>=sizeof(name)-1 && !memcmp(fields.name,name,sizeof(name)-1);
}
static bool persist_peer(const ble_addr_t *addr) {
    nvs_handle_t nvs; remote_peer_t peer={.magic=REMOTE_MAGIC,.addr_type=addr->type}; memcpy(peer.addr,addr->val,sizeof(peer.addr));
    esp_err_t result=nvs_open(REMOTE_NAMESPACE,NVS_READWRITE,&nvs);
    if(result==ESP_OK){result=nvs_set_blob(nvs,REMOTE_KEY,&peer,sizeof(peer));if(result==ESP_OK)result=nvs_commit(nvs);nvs_close(nvs);}
    if(result==ESP_OK){saved_peer=peer;have_saved_peer=true;return true;}
    ESP_LOGW(TAG,"could not remember controller: %s",esp_err_to_name(result));return false;
}
static void load_peer(void) { nvs_handle_t nvs;size_t len=sizeof(saved_peer);if(nvs_open(REMOTE_NAMESPACE,NVS_READONLY,&nvs)==ESP_OK){if(nvs_get_blob(nvs,REMOTE_KEY,&saved_peer,&len)==ESP_OK&&len==sizeof(saved_peer)&&saved_peer.magic==REMOTE_MAGIC)have_saved_peer=true;nvs_close(nvs);} }
static bool forget_peer(void) {
    nvs_handle_t nvs;esp_err_t result=nvs_open(REMOTE_NAMESPACE,NVS_READWRITE,&nvs);
    if(result==ESP_OK){result=nvs_erase_key(nvs,REMOTE_KEY);if(result==ESP_ERR_NVS_NOT_FOUND)result=ESP_OK;if(result==ESP_OK)result=nvs_commit(nvs);nvs_close(nvs);}
    if(result!=ESP_OK){ESP_LOGE(TAG,"could not forget controller: %s",esp_err_to_name(result));return false;}
    memset(&saved_peer,0,sizeof(saved_peer));have_saved_peer=false;return true;
}
static void request_unpair(void){atomic_store(&unpair_requested,true);}
static void stop_scan(void) { if(scanning){ble_gap_disc_cancel();scanning=false;} }
static void schedule_retry(void) { next_scan=xTaskGetTickCount()+pdMS_TO_TICKS(RETRY_SECONDS*1000); }
static void begin_scan(bool pairing_window) {
    if(connected||connecting||scanning) return;
    struct ble_gap_disc_params p={0};
    p.passive=0; p.filter_duplicates=1; p.itvl=0x0060; p.window=0x0030;
    int rc=ble_gap_disc(own_addr_type,pairing_window?PAIR_SCAN_SECONDS*1000:SCAN_SECONDS*1000,&p,gap_event,NULL);if(!rc){scanning=true;ESP_LOGW(TAG,"%s scan started",pairing_window?"pairing":"reconnect");}else{ESP_LOGW(TAG,"scan could not start: %d",rc);schedule_retry();}
}
static void terminate_gatt(const char *step,int status){ESP_LOGW(TAG,"%s failed: %d",step,status);if(conn_handle!=BLE_HS_CONN_HANDLE_NONE)ble_gap_terminate(conn_handle,BLE_ERR_REM_USER_CONN_TERM);}
static int sensor_write_done(uint16_t ch,const struct ble_gatt_error *e,struct ble_gatt_attr *a,void *arg){
    (void)ch;(void)a;(void)arg;if(e->status){terminate_gatt("sensor-mode write",e->status);return 0;}
    if(!have_saved_peer&&!persist_peer(&candidate_addr)){terminate_gatt("controller enrollment persistence",BLE_HS_ESTORE_CAP);return 0;}
    enrolled=true;pairing_window_open=false;ESP_LOGW(TAG,"controller ready");return 0;
}
static int subscription_write_done(uint16_t ch,const struct ble_gatt_error *e,struct ble_gatt_attr *a,void *arg){
    (void)ch;(void)a;(void)arg;if(e->status){terminate_gatt("notification subscription",e->status);return 0;}
    const uint8_t sensor[]={1,0};int rc=ble_gattc_write_flat(conn_handle,command_handle,sensor,sizeof(sensor),sensor_write_done,NULL);if(rc)terminate_gatt("sensor-mode start",rc);return 0;
}
static int dsc_cb(uint16_t ch,const struct ble_gatt_error *e,uint16_t value_handle,const struct ble_gatt_dsc *d,void *arg){
    (void)ch;(void)value_handle;(void)arg;if(!e->status&&d&&ble_uuid_u16(&d->uuid.u)==0x2902)cccd_handle=d->handle;
    if(e->status==BLE_HS_EDONE){if(!cccd_handle){terminate_gatt("notification descriptor discovery",BLE_HS_ENOENT);return 0;}const uint8_t enable[]={1,0};int rc=ble_gattc_write_flat(conn_handle,cccd_handle,enable,sizeof(enable),subscription_write_done,NULL);if(rc)terminate_gatt("notification subscription start",rc);}else if(e->status)terminate_gatt("notification descriptor discovery",e->status);return 0;
}
static int command_chr_cb(uint16_t ch,const struct ble_gatt_error *e,const struct ble_gatt_chr *chr,void *arg){(void)ch;(void)arg;if(!e->status&&chr){command_def_handle=chr->def_handle;command_handle=chr->val_handle;}if(e->status==BLE_HS_EDONE){if(!command_handle||!notify_handle||command_def_handle<=notify_handle+1){terminate_gatt("command characteristic discovery",BLE_HS_ENOENT);return 0;}/* This API begins at start_handle + 1, so pass the value handle itself. */int rc=ble_gattc_disc_all_dscs(conn_handle,notify_handle,command_def_handle-1,dsc_cb,NULL);if(rc)terminate_gatt("notification descriptor discovery start",rc);}else if(e->status)terminate_gatt("command characteristic discovery",e->status);return 0;}
static int notify_chr_cb(uint16_t ch,const struct ble_gatt_error *e,const struct ble_gatt_chr *chr,void *arg){(void)ch;(void)arg;if(!e->status&&chr)notify_handle=chr->val_handle;if(e->status==BLE_HS_EDONE){if(!notify_handle){terminate_gatt("notification characteristic discovery",BLE_HS_ENOENT);return 0;}int rc=ble_gattc_disc_chrs_by_uuid(conn_handle,service_start,service_end,&command_uuid.u,command_chr_cb,NULL);if(rc)terminate_gatt("command characteristic discovery start",rc);}else if(e->status)terminate_gatt("notification characteristic discovery",e->status);return 0;}
static int service_cb(uint16_t ch,const struct ble_gatt_error *e,const struct ble_gatt_svc *svc,void *arg){(void)ch;(void)arg;if(!e->status&&svc){service_start=svc->start_handle;service_end=svc->end_handle;}if(e->status==BLE_HS_EDONE){if(!service_start||!service_end){terminate_gatt("service discovery",BLE_HS_ENOENT);return 0;}int rc=ble_gattc_disc_chrs_by_uuid(conn_handle,service_start,service_end,&notify_uuid.u,notify_chr_cb,NULL);if(rc)terminate_gatt("notification characteristic discovery start",rc);}else if(e->status)terminate_gatt("service discovery",e->status);return 0;}
static void discover(void){if(ble_gattc_disc_svc_by_uuid(conn_handle,&service_uuid.u,service_cb,NULL))ble_gap_terminate(conn_handle,BLE_ERR_REM_USER_CONN_TERM);}
static int gap_event(struct ble_gap_event *event,void *arg){
    (void)arg;switch(event->type){
    case BLE_GAP_EVENT_DISC:
        if(!connecting&&((have_saved_peer&&addr_matches(&event->disc.addr))||(!have_saved_peer&&pairing_window_open&&is_controller_advertisement(&event->disc)))){candidate_addr=event->disc.addr;stop_scan();connecting=true;ESP_LOGW(TAG,"controller found; connecting");if(ble_gap_connect(own_addr_type,&event->disc.addr,30000,NULL,gap_event,NULL)){connecting=false;schedule_retry();}}return 0;
    case BLE_GAP_EVENT_CONNECT:
        connecting=false;if(event->connect.status){ESP_LOGW(TAG,"connection failed: %d",event->connect.status);schedule_retry();return 0;}connected=true;conn_handle=event->connect.conn_handle;service_start=service_end=notify_handle=command_def_handle=command_handle=cccd_handle=0;enrolled=false;ESP_LOGW(TAG,"controller connected; discovering controls");discover();return 0;
    case BLE_GAP_EVENT_NOTIFY_RX:{if(event->notify_rx.attr_handle!=notify_handle)return 0;uint8_t buf[80];uint16_t len=OS_MBUF_PKTLEN(event->notify_rx.om);if(len>sizeof(buf))len=sizeof(buf);if(!os_mbuf_copydata(event->notify_rx.om,0,len,buf))marvin_gear_vr_report(buf,len);return 0;}
    case BLE_GAP_EVENT_DISCONNECT:ESP_LOGW(TAG,"controller disconnected: %d",event->disconnect.reason);connected=false;connecting=false;conn_handle=BLE_HS_CONN_HANDLE_NONE;enrolled=false;marvin_gear_vr_reset();if(pairing_window_open)next_scan=xTaskGetTickCount();else schedule_retry();return 0;
    case BLE_GAP_EVENT_DISC_COMPLETE:scanning=false;schedule_retry();return 0;
    default:return 0;}
}
static void remote_task(void *arg){
    (void)arg;load_peer();marvin_gear_vr_set_unpair_handler(request_unpair);pairing_window_open=!have_saved_peer;pairing_deadline=xTaskGetTickCount()+pdMS_TO_TICKS(PAIR_SCAN_SECONDS*1000);/* Setup BLE starts NimBLE later; this task never delays app_main. */while(ble_hs_id_infer_auto(0,&own_addr_type))vTaskDelay(pdMS_TO_TICKS(250));begin_scan(pairing_window_open);
    for(;;){
        if(atomic_exchange(&unpair_requested,false)){
            if(forget_peer()){
                pairing_window_open=false;next_scan=portMAX_DELAY;stop_scan();
                marvin_body_audio_flush();
                marvin_body_audio_unpaired_cue();
                ESP_LOGW(TAG,"Home hold accepted; controller forgotten and pairing remains closed");
                if(connected)ble_gap_terminate(conn_handle,BLE_ERR_REM_USER_CONN_TERM);
            }else ESP_LOGE(TAG,"Home hold rejected; saved controller retained");
        }
        if(atomic_exchange(&pairing_requested,false)){
            if(forget_peer()){
                pairing_window_open=true;pairing_deadline=xTaskGetTickCount()+pdMS_TO_TICKS(PAIR_SCAN_SECONDS*1000);next_scan=0;
                ESP_LOGW(TAG,"USB pairing request accepted; pairing scan open for %d seconds",PAIR_SCAN_SECONDS);
                if(connected)ble_gap_terminate(conn_handle,BLE_ERR_REM_USER_CONN_TERM);else stop_scan();
            }else ESP_LOGE(TAG,"USB pairing request rejected; saved controller retained");
        }
        if(pairing_window_open&&(int32_t)(xTaskGetTickCount()-pairing_deadline)>=0){pairing_window_open=false;stop_scan();ESP_LOGW(TAG,"pairing window closed");}
        if((have_saved_peer||pairing_window_open)&&!connected&&!connecting&&!scanning&&(int32_t)(xTaskGetTickCount()-next_scan)>=0)begin_scan(pairing_window_open);
        vTaskDelay(pdMS_TO_TICKS(250));
    }
}
#endif
esp_err_t marvin_ble_remote_start(void){
#ifdef CONFIG_MARVIN_BLE_REMOTE
    return xTaskCreate(remote_task,"gear_vr_ble",6144,NULL,4,NULL)==pdPASS?ESP_OK:ESP_ERR_NO_MEM;
#else
    return ESP_OK;
#endif
}
void marvin_ble_remote_open_pairing(void){
#ifdef CONFIG_MARVIN_BLE_REMOTE
    atomic_store(&pairing_requested,true);
#endif
}
