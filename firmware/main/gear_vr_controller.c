#include "gear_vr_controller.h"
#include "remote_control.h"
#include <stdbool.h>
#include "esp_log.h"

enum {
    REPORT_BYTES=60,
    TRIGGER=1,
    HOME=2,
    HEAD_RIGHT=16,
    HEAD_LEFT=32,
    HOME_HOLD_US=3000000,
    GYRO_STILL_RAW=100,
    GYRO_DEAD_ZONE_RAW=8,
    MAX_GYRO_DELTA_US=100000,
    YAW_RANGE_MDEG=40000,
    PITCH_RANGE_MDEG=30000,
};
typedef struct {
    int gyro_x_bias_q8,gyro_z_bias_q8;
    int yaw_mdeg,pitch_mdeg;
    unsigned bias_samples;
    uint32_t last_timestamp;
    uint32_t home_down_timestamp;
    uint8_t last_head_buttons;
    bool trigger_was_down,home_release_seen,home_was_down,home_unpair_sent;
} gear_state_t;
static gear_state_t state;
static marvin_gear_vr_unpair_handler_t unpair_handler;
static int clamp(int v,int low,int high){return v<low?low:v>high?high:v;}
static int absolute(int v){return v<0?-v:v;}
static int16_t le16(const uint8_t *p){return (int16_t)((uint16_t)p[0]|((uint16_t)p[1]<<8));}
static uint32_t le32(const uint8_t *p){return (uint32_t)p[0]|((uint32_t)p[1]<<8)|((uint32_t)p[2]<<16)|((uint32_t)p[3]<<24);}
static int without_dead_zone(int v){return absolute(v)<=GYRO_DEAD_ZONE_RAW?0:v;}

static void learn_gyro_bias(int gyro_x,int gyro_z){
    /* Learn only from nearly stationary released samples. Moving the remote
     * between gestures must not be mistaken for sensor bias. */
    if(absolute(gyro_x)>GYRO_STILL_RAW||absolute(gyro_z)>GYRO_STILL_RAW)return;
    if(!state.bias_samples){
        state.gyro_x_bias_q8=gyro_x*256;
        state.gyro_z_bias_q8=gyro_z*256;
    }else{
        state.gyro_x_bias_q8=(state.gyro_x_bias_q8*31+gyro_x*256)/32;
        state.gyro_z_bias_q8=(state.gyro_z_bias_q8*31+gyro_z*256)/32;
    }
    if(state.bias_samples<255)state.bias_samples++;
}

static void update_trigger_head(bool trigger,uint32_t timestamp,int gyro_x,int gyro_z){
    if(!trigger){
        learn_gyro_bias(gyro_x,gyro_z);
        state.yaw_mdeg=state.pitch_mdeg=0;
        state.last_timestamp=timestamp;
        state.trigger_was_down=false;
        return;
    }
    if(!state.trigger_was_down){
        /* A trigger press is the clutch point: the current grip becomes zero.
         * Do not integrate motion that happened before this report. */
        state.yaw_mdeg=state.pitch_mdeg=0;
        state.last_timestamp=timestamp;
        state.trigger_was_down=true;
        return;
    }
    const uint32_t elapsed_us=timestamp-state.last_timestamp;
    state.last_timestamp=timestamp;
    if(!elapsed_us||elapsed_us>MAX_GYRO_DELTA_US)return;
    const int bias_x=state.bias_samples?state.gyro_x_bias_q8/256:0;
    const int bias_z=state.bias_samples?state.gyro_z_bias_q8/256:0;
    const int pitch_rate=without_dead_zone(gyro_x-bias_x);
    const int yaw_rate=without_dead_zone(gyro_z-bias_z);
    /* Raw gyro units are 14.285 counts per degree/second. The controller's
     * wand axes map X to pitch and -Z to yaw. Keep milli-degrees so the
     * integration is deterministic without floating point. */
    state.pitch_mdeg=clamp(state.pitch_mdeg+(int)((int64_t)pitch_rate*elapsed_us/14285),-PITCH_RANGE_MDEG,PITCH_RANGE_MDEG);
    state.yaw_mdeg=clamp(state.yaw_mdeg-(int)((int64_t)yaw_rate*elapsed_us/14285),-YAW_RANGE_MDEG,YAW_RANGE_MDEG);
}

void marvin_gear_vr_set_unpair_handler(marvin_gear_vr_unpair_handler_t handler){unpair_handler=handler;}
void marvin_gear_vr_reset(void){state=(gear_state_t){0};marvin_remote_disconnect();}
esp_err_t marvin_gear_vr_report(const uint8_t *data,size_t length){
    if(!data||length<REPORT_BYTES)return ESP_ERR_INVALID_SIZE;
    const uint8_t buttons=data[58];
    const bool trigger=(buttons&TRIGGER)!=0;
    const bool home=(buttons&HOME)!=0;
    const uint8_t head_buttons=buttons&(HEAD_RIGHT|HEAD_LEFT);
    const bool head_right=(buttons&HEAD_RIGHT)!=0;
    const bool head_left=(buttons&HEAD_LEFT)!=0;
    if(head_buttons!=state.last_head_buttons){
        ESP_LOGW("gear_vr_input","buttons head_right=%d head_left=%d",head_right,head_left);
        state.last_head_buttons=head_buttons;
    }

    const uint32_t timestamp=le32(data);
    if(!home){
        state.home_release_seen=true;
        state.home_was_down=false;
        state.home_unpair_sent=false;
    }else if(!state.home_release_seen){
        /* The controller may still be held in its own pairing gesture when a
         * new pet connects. Require a release before arming Marvin's hold so
         * the successful pairing cannot immediately erase itself. */
    }else if(!state.home_was_down){
        state.home_was_down=true;
        state.home_down_timestamp=timestamp;
    }else if(!state.home_unpair_sent&&(uint32_t)(timestamp-state.home_down_timestamp)>=HOME_HOLD_US){
        /* Home is pointer-recenter while connected, so Marvin can safely use
         * a deliberate hold as its local forget gesture. Stop the robot at
         * the recognition point; the BLE task performs durable erase and
         * disconnect outside the NimBLE notification callback. */
        state.home_unpair_sent=true;
        marvin_remote_disconnect();
        if(unpair_handler)unpair_handler();
        return ESP_OK;
    }

    update_trigger_head(trigger,timestamp,le16(data+10),le16(data+14));

    /* The circular touch surface is the drive stick. Coordinates 0,0 mean no
     * finger, which provides the same release-to-stop behavior as the UI. */
    int turn=0,throttle=0;
    const int x=(((data[54]&0x0f)<<6)|((data[55]&0xfc)>>2))&0x3ff;
    const int y=(((data[55]&0x03)<<8)|data[56])&0x3ff;
    if(x||y){
        turn=clamp((x-157)*1000/157,-1000,1000);
        throttle=clamp((157-y)*1000/157,-1000,1000);
    }
    const int imu_yaw=state.yaw_mdeg*1000/YAW_RANGE_MDEG;
    const int imu_pitch=state.pitch_mdeg*1000/PITCH_RANGE_MDEG;
    /* Preserve the buttons as a dependable yaw fallback. A single pressed
     * button overrides IMU yaw, while pitch can still follow the trigger. */
    const int head_yaw=head_right==head_left?imu_yaw:(head_right?1000:-1000);
    return marvin_remote_submit(&(marvin_remote_intent_t){.throttle=throttle,.turn=turn,.head_yaw=head_yaw,.head_pitch=imu_pitch,.autonomous_head=true});
}
