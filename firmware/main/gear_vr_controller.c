#include "gear_vr_controller.h"
#include "remote_control.h"
#include <stdbool.h>
#include "esp_log.h"

enum { REPORT_BYTES=60, HEAD_RIGHT=16, HEAD_LEFT=32 };
typedef struct { uint8_t last_buttons; } gear_state_t;
static gear_state_t state;
static int clamp(int v,int low,int high){return v<low?low:v>high?high:v;}

void marvin_gear_vr_reset(void){state=(gear_state_t){0};marvin_remote_disconnect();}
esp_err_t marvin_gear_vr_report(const uint8_t *data,size_t length){
    if(!data||length<REPORT_BYTES)return ESP_ERR_INVALID_SIZE;
    const uint8_t buttons=data[58];
    const uint8_t head_buttons=buttons&(HEAD_RIGHT|HEAD_LEFT);
    const bool head_right=(buttons&HEAD_RIGHT)!=0;
    const bool head_left=(buttons&HEAD_LEFT)!=0;
    if(head_buttons!=state.last_buttons){
        ESP_LOGW("gear_vr_input","buttons head_right=%d head_left=%d",head_right,head_left);
        state.last_buttons=head_buttons;
    }

    /* The circular touch surface is the drive stick. Coordinates 0,0 mean no
     * finger, which provides the same release-to-stop behavior as the UI. */
    int turn=0,throttle=0;
    const int x=(((data[54]&0x0f)<<6)|((data[55]&0xfc)>>2))&0x3ff;
    const int y=(((data[55]&0x03)<<8)|data[56])&0x3ff;
    if(x||y){
        turn=clamp((x-157)*1000/157,-1000,1000);
        throttle=clamp((157-y)*1000/157,-1000,1000);
    }
    const int head_yaw=head_right==head_left?0:(head_right?1000:-1000);
    return marvin_remote_submit(&(marvin_remote_intent_t){.throttle=throttle,.turn=turn,.head_yaw=head_yaw,.head_pitch=0,.autonomous_head=false});
}
