#include "gear_vr_controller.h"
#include "remote_control.h"
#include <assert.h>
#include <stdbool.h>
#include <stdint.h>
#include <string.h>

static marvin_remote_intent_t captured;
static unsigned submitted, disconnected;
static int volume_delta;

esp_err_t marvin_remote_submit(const marvin_remote_intent_t *intent){
    assert(intent);
    captured=*intent;
    submitted++;
    return ESP_OK;
}
void marvin_remote_disconnect(void){disconnected++;}
bool marvin_body_adjust_volume(int delta){volume_delta+=delta;return true;}

static void put16(uint8_t *report,size_t offset,int16_t value){
    report[offset]=(uint8_t)value;
    report[offset+1]=(uint8_t)((uint16_t)value>>8);
}
static void send(uint8_t *report){assert(marvin_gear_vr_report(report,60)==ESP_OK);}

int main(void){
    uint8_t report[60]={0};
    assert(marvin_gear_vr_report(NULL,sizeof(report))==ESP_ERR_INVALID_SIZE);
    assert(marvin_gear_vr_report(report,sizeof(report)-1)==ESP_ERR_INVALID_SIZE);

    put16(report,4,100);put16(report,6,-200);
    for(unsigned i=0;i<20;i++)send(report);
    assert(submitted==20&&captured.throttle==0&&captured.turn==0);
    assert(captured.head_yaw==0&&captured.head_pitch==0&&!captured.autonomous_head);

    report[58]=16;send(report);send(report);
    assert(volume_delta==5);
    report[58]=0;send(report);report[58]=32;send(report);
    assert(volume_delta==0);

    report[58]=0;put16(report,4,850);put16(report,6,550);send(report);
    assert(captured.throttle==0&&captured.turn==0);
    assert(captured.head_yaw>0&&captured.head_yaw<=1000);
    assert(captured.head_pitch>0&&captured.head_pitch<=1000);

    memset(report,0,sizeof(report));
    report[54]=0x04;report[55]=0xec;report[56]=0;
    send(report);
    assert(captured.turn==1000&&captured.throttle==1000);
    memset(report,0,sizeof(report));send(report);
    assert(captured.turn==0&&captured.throttle==0);

    marvin_gear_vr_reset();
    assert(disconnected==1);
    return 0;
}
