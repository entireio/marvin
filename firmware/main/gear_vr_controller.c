#include "gear_vr_controller.h"
#include "remote_control.h"
#include <stdbool.h>

enum { REPORT_BYTES=60, TRIGGER=1, HOME=2, BACK=4, TOUCH=8 };
typedef struct { int lateral_zero,lateral; unsigned samples; bool auto_head,home_was_down; } gear_state_t;
static gear_state_t state={.auto_head=true};
static int clamp(int v,int low,int high){return v<low?low:v>high?high:v;}
static int16_t le16(const uint8_t *p){return (int16_t)((uint16_t)p[0]|((uint16_t)p[1]<<8));}

void marvin_gear_vr_reset(void){state=(gear_state_t){.auto_head=true};marvin_remote_disconnect();}
esp_err_t marvin_gear_vr_report(const uint8_t *data,size_t length){
    if(!data||length<REPORT_BYTES)return ESP_ERR_INVALID_SIZE;
    const uint8_t buttons=data[58];
    const bool trigger=(buttons&TRIGGER)!=0,home=(buttons&HOME)!=0,back=(buttons&BACK)!=0,touch=(buttons&TOUCH)!=0;
    /* The reverse-engineered packet holds acceleration X at bytes 4..5.
     * When held like a wand, X is lateral; estimate an at-rest zero only
     * while the trigger is released, then low-pass it to reject hand tremor. */
    int lateral=le16(data+4);
    if(!trigger&&!touch){
        state.lateral_zero=state.samples?((state.lateral_zero*15+lateral)/16):lateral;
        if(state.samples<255)state.samples++;
    }
    int tilt=state.samples>=20?clamp((lateral-state.lateral_zero)*1000/750,-1000,1000):0;
    state.lateral=(state.lateral*3+tilt)/4;
    /* Home is a rising-edge mode toggle. Holding Home is reserved by the
     * pairing flow, so it never toggles repeatedly. Back + trigger provides
     * a deliberate reverse without adding another control mode. */
    if(home&&!state.home_was_down)state.auto_head=!state.auto_head;
    state.home_was_down=home;
    int throttle=trigger?(back?-650:650):0;
    int head_yaw=0,head_pitch=0;
    if(touch){
        /* ET-YO324 observed touch range is 0..315 despite its 10-bit fields. */
        int x=(((data[54]&0x0f)<<6)|((data[55]&0xfc)>>2))&0x3ff;
        int y=(((data[55]&0x03)<<8)|data[56])&0x3ff;
        head_yaw=clamp((x-157)*1000/157,-1000,1000);
        head_pitch=clamp((y-157)*1000/157,-1000,1000);
    }
    return marvin_remote_submit(&(marvin_remote_intent_t){.throttle=throttle,.turn=state.lateral,.head_yaw=head_yaw,.head_pitch=head_pitch,.autonomous_head=state.auto_head});
}
