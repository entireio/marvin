#include "gear_vr_controller.h"
#include "body_audio.h"
#include "remote_control.h"
#include <stdbool.h>
#include "esp_log.h"

enum { REPORT_BYTES=60, VOLUME_UP=16, VOLUME_DOWN=32, CALIBRATION_SAMPLES=20 };
typedef struct {
    int64_t lateral_sum,forward_sum;
    int lateral_zero,forward_zero,head_yaw,head_pitch;
    unsigned samples;
    uint8_t last_buttons;
} gear_state_t;
static gear_state_t state;
static int clamp(int v,int low,int high){return v<low?low:v>high?high:v;}
static int16_t le16(const uint8_t *p){return (int16_t)((uint16_t)p[0]|((uint16_t)p[1]<<8));}

void marvin_gear_vr_reset(void){state=(gear_state_t){0};marvin_remote_disconnect();}
esp_err_t marvin_gear_vr_report(const uint8_t *data,size_t length){
    if(!data||length<REPORT_BYTES)return ESP_ERR_INVALID_SIZE;
    const uint8_t buttons=data[58];
    const uint8_t pressed=(uint8_t)(buttons&~state.last_buttons);
    if(pressed&VOLUME_UP)marvin_body_adjust_volume(5);
    if(pressed&VOLUME_DOWN)marvin_body_adjust_volume(-5);
    if(buttons!=state.last_buttons){
        ESP_LOGW("gear_vr_input","buttons volume_up=%d volume_down=%d",(buttons&VOLUME_UP)!=0,(buttons&VOLUME_DOWN)!=0);
        state.last_buttons=buttons;
    }

    /* The ET-YO324 reports lateral and forward acceleration at bytes 4..7.
     * Average the first reports to establish the user's neutral grip, then
     * low-pass both head axes to reject hand tremor. */
    const int lateral=le16(data+4),forward=le16(data+6);
    if(state.samples<CALIBRATION_SAMPLES){
        state.lateral_sum+=lateral;state.forward_sum+=forward;state.samples++;
        if(state.samples==CALIBRATION_SAMPLES){
            state.lateral_zero=(int)(state.lateral_sum/CALIBRATION_SAMPLES);
            state.forward_zero=(int)(state.forward_sum/CALIBRATION_SAMPLES);
        }
    }else{
        int yaw=clamp((lateral-state.lateral_zero)*1000/750,-1000,1000);
        int pitch=clamp((forward-state.forward_zero)*1000/750,-1000,1000);
        state.head_yaw=(state.head_yaw*3+yaw)/4;
        state.head_pitch=(state.head_pitch*3+pitch)/4;
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
    return marvin_remote_submit(&(marvin_remote_intent_t){.throttle=throttle,.turn=turn,.head_yaw=state.head_yaw,.head_pitch=state.head_pitch,.autonomous_head=false});
}
