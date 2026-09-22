#include "gear_vr_controller.h"
#include "remote_control.h"
#include <assert.h>
#include <stdbool.h>
#include <stdint.h>
#include <string.h>

static marvin_remote_intent_t captured;
static unsigned submitted, disconnected;

esp_err_t marvin_remote_submit(const marvin_remote_intent_t *intent){
    assert(intent);
    captured=*intent;
    submitted++;
    return ESP_OK;
}
void marvin_remote_disconnect(void){disconnected++;}
static void send(uint8_t *report){assert(marvin_gear_vr_report(report,60)==ESP_OK);}

int main(void){
    uint8_t report[60]={0};
    assert(marvin_gear_vr_report(NULL,sizeof(report))==ESP_ERR_INVALID_SIZE);
    assert(marvin_gear_vr_report(report,sizeof(report)-1)==ESP_ERR_INVALID_SIZE);

    send(report);
    assert(submitted==1&&captured.throttle==0&&captured.turn==0);
    assert(captured.head_yaw==0&&captured.head_pitch==0&&!captured.autonomous_head);

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
