#include "gear_vr_controller.h"
#include "remote_control.h"
#include <assert.h>
#include <stdbool.h>
#include <stdint.h>
#include <string.h>

static marvin_remote_intent_t captured;
static unsigned submitted, disconnected, unpair_requests;

esp_err_t marvin_remote_submit(const marvin_remote_intent_t *intent){
    assert(intent);
    captured=*intent;
    submitted++;
    return ESP_OK;
}
void marvin_remote_disconnect(void){disconnected++;}
static void request_unpair(void){unpair_requests++;}
static void put16(uint8_t *report,size_t offset,int16_t value){
    report[offset]=(uint8_t)value;
    report[offset+1]=(uint8_t)((uint16_t)value>>8);
}
static void put32(uint8_t *report,uint32_t value){
    report[0]=(uint8_t)value;
    report[1]=(uint8_t)(value>>8);
    report[2]=(uint8_t)(value>>16);
    report[3]=(uint8_t)(value>>24);
}
static void send(uint8_t *report){assert(marvin_gear_vr_report(report,60)==ESP_OK);}

int main(void){
    uint8_t report[60]={0};
    marvin_gear_vr_set_unpair_handler(request_unpair);
    assert(marvin_gear_vr_report(NULL,sizeof(report))==ESP_ERR_INVALID_SIZE);
    assert(marvin_gear_vr_report(report,sizeof(report)-1)==ESP_ERR_INVALID_SIZE);

    send(report);
    assert(submitted==1&&captured.throttle==0&&captured.turn==0);
    assert(captured.head_yaw==0&&captured.head_pitch==0&&captured.autonomous_head);

    report[58]=16;send(report);
    assert(captured.head_yaw==1000&&captured.head_pitch==0);
    report[58]=32;send(report);
    assert(captured.head_yaw==-1000&&captured.head_pitch==0);
    report[58]=16|32;send(report);
    assert(captured.head_yaw==0&&captured.head_pitch==0);

    memset(report,0,sizeof(report));
    memset(report+4,0xff,4);send(report);
    assert(captured.throttle==0&&captured.turn==0);
    assert(captured.head_yaw==0&&captured.head_pitch==0);

    /* Released, stationary samples establish gyro bias. A trigger press is
     * zero regardless of the controller's earlier orientation. */
    memset(report,0,sizeof(report));put32(report,100000);put16(report,10,20);put16(report,14,-10);send(report);
    report[58]=1;put32(report,110000);send(report);
    assert(captured.head_yaw==0&&captured.head_pitch==0);

    /* Rotation while held integrates relative to the press pose. X controls
     * pitch and -Z controls yaw; both saturate at their useful head ranges. */
    put32(report,210000);put16(report,10,4306);put16(report,14,-5724);send(report);
    assert(captured.head_yaw>990&&captured.head_yaw<=1000);
    assert(captured.head_pitch>990&&captured.head_pitch<=1000);

    /* The +/- yaw fallback keeps its existing authority during IMU control. */
    report[58]=1|32;put32(report,220000);send(report);
    assert(captured.head_yaw==-1000&&captured.head_pitch>0);

    /* Releasing the clutch removes IMU offsets; every new press starts from
     * zero instead of inheriting drift from the previous gesture. */
    report[58]=0;put32(report,230000);send(report);
    assert(captured.head_yaw==0&&captured.head_pitch==0);
    report[58]=1;put32(report,240000);send(report);
    assert(captured.head_yaw==0&&captured.head_pitch==0);

    memset(report,0,sizeof(report));
    report[54]=0x04;report[55]=0xec;report[56]=0;
    send(report);
    assert(captured.turn==1000&&captured.throttle==1000);
    assert(captured.autonomous_head);
    memset(report,0,sizeof(report));send(report);
    assert(captured.turn==0&&captured.throttle==0);

    /* A short Home press keeps controlling normally. A continuous three
     * second hold stops immediately and requests exactly one durable unpair,
     * even if reports continue before the BLE disconnect completes. */
    memset(report,0,sizeof(report));report[58]=2;put32(report,1000000);send(report);
    put32(report,3999999);send(report);
    assert(unpair_requests==0);
    unsigned before=submitted;
    put32(report,4000000);send(report);
    assert(unpair_requests==1&&disconnected==1&&submitted==before);
    put32(report,5000000);send(report);
    assert(unpair_requests==1&&disconnected==1);
    report[58]=0;send(report);
    report[58]=2;put32(report,6000000);send(report);
    put32(report,9000000);send(report);
    assert(unpair_requests==2&&disconnected==2);

    marvin_gear_vr_reset();
    assert(disconnected==3);

    /* A Home button still held from the controller's own pairing gesture is
     * ignored after connection until a released report arms local unpairing. */
    report[58]=2;put32(report,10000000);send(report);
    put32(report,14000000);send(report);
    assert(unpair_requests==2);
    report[58]=0;send(report);
    report[58]=2;put32(report,15000000);send(report);
    put32(report,18000000);send(report);
    assert(unpair_requests==3&&disconnected==4);
    return 0;
}
