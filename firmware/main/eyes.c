#include "eyes.h"
#include "eyes_render.h"
#include "battery_monitor.h"
#include "board_i2c.h"
#include "driver/i2c_master.h"
#include "esp_lcd_io_i2c.h"
#include "esp_lcd_panel_io.h"
#include "esp_lcd_panel_ops.h"
#include "esp_lcd_panel_ssd1306.h"
#include "esp_log.h"
#include "esp_random.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "nvs.h"
#include <stdatomic.h>
#include <string.h>

enum {
    LEFT_ADDRESS=0x3c,RIGHT_ADDRESS=0x3d,
    SSD1306_CMD_DISPLAY_ON=0xaf,
    LOGIC_TICK_MS=20,FRAME_PERIOD_US=66667,
    GAZE_X_PIXELS=16,GAZE_Y_PIXELS=3,
    BATTERY_SAMPLE_US=15000000,VOLUME_OVERLAY_US=2400000,
    LOW_BATTERY_ENTER_PERCENT=15,LOW_BATTERY_EXIT_PERCENT=20,
    SEQUENCE_NONE=0,SEQUENCE_WAKE=1,SEQUENCE_SLEEP=2,
    SEQUENCE_WAKE_INSTANT=3,SEQUENCE_SLEEP_INSTANT=4,
};
typedef struct {
    esp_lcd_panel_io_handle_t io;
    esp_lcd_panel_handle_t panel;
    uint8_t pixels[MARVIN_EYE_BYTES];
    uint8_t shown[MARVIN_EYE_BYTES];
    unsigned failures;
    bool shown_valid;
    bool enabled;
} display_t;

static const char *TAG="marvin_eyes";
static display_t displays[2];
static atomic_bool pair_available,awake;
static atomic_int pending_sequence;
static atomic_int requested_expression=MARVIN_EYES_NEUTRAL;
static atomic_int requested_design=MARVIN_EYE_DESIGN_CLASSIC;
static atomic_int gaze_x,gaze_y,head_yaw,head_pitch,target_yaw,target_pitch;
static atomic_uint voice_level;
static atomic_llong gaze_until_us;
static atomic_uint overlay_volume;
static atomic_llong volume_until_us;

static int clamp(int value,int low,int high){return value<low?low:value>high?high:value;}
static int approach(int value,int target,int divisor){
    int difference=target-value;
    if(!difference)return value;
    int step=difference/divisor;
    if(!step)step=difference>0?1:-1;
    return value+step;
}
static int smoothstep(int value){
    value=clamp(value,0,1000);
    return value*value*(3000-2*value)/1000000;
}
static int mix(int from,int to,int progress){return from+(to-from)*smoothstep(progress)/1000;}
static marvin_eye_pose_t pose(int outer_rx,int outer_ry,int inner_rx,int inner_ry,int center_y){
    return (marvin_eye_pose_t){
        .center_x=MARVIN_EYE_WIDTH/2,.center_y=center_y,
        .outer_rx=outer_rx,.outer_ry=outer_ry,
        .inner_rx=inner_rx,.inner_ry=inner_ry,
    };
}
static marvin_eye_pose_t pose_mix(marvin_eye_pose_t from,marvin_eye_pose_t to,int progress){
    marvin_eye_pose_t result;
#define MIX_FIELD(field) result.field=mix(from.field,to.field,progress)
    MIX_FIELD(center_x);MIX_FIELD(center_y);MIX_FIELD(outer_rx);MIX_FIELD(outer_ry);
    MIX_FIELD(inner_rx);MIX_FIELD(inner_ry);
#undef MIX_FIELD
    return result;
}
static void pose_approach(marvin_eye_pose_t *value,marvin_eye_pose_t target,int divisor){
#define APPROACH_FIELD(field) value->field=approach(value->field,target.field,divisor)
    APPROACH_FIELD(center_x);APPROACH_FIELD(center_y);APPROACH_FIELD(outer_rx);APPROACH_FIELD(outer_ry);
    APPROACH_FIELD(inner_rx);APPROACH_FIELD(inner_ry);
#undef APPROACH_FIELD
}
static marvin_eye_pose_t expression_pose(marvin_eye_expression_t expression,unsigned eye,int64_t now,marvin_eye_design_t design){
    if(design==MARVIN_EYE_DESIGN_FRIENDLY){
        switch(expression){
            case MARVIN_EYES_LISTENING:return pose(27,32,0,0,31);
            case MARVIN_EYES_THINKING:return pose(23,28,0,0,29);
            case MARVIN_EYES_SPEAKING:{
                int phase=(int)((now/80000+eye*2)%10),wave=phase<5?phase:9-phase;
                int energy=clamp((int)atomic_load(&voice_level),0,1000);
                return pose(25+wave/2+energy*2/1000,30+wave/2+energy*2/1000,0,0,32-energy/500);
            }
            case MARVIN_EYES_CONCERNED:return pose(23,26,0,0,eye?33:31);
            case MARVIN_EYES_SLEEPING:return pose(23,2,0,0,34);
            default:return pose(25,31,0,0,32);
        }
    }
    switch(expression){
        case MARVIN_EYES_LISTENING:
            return pose(eye?31:32,eye?31:32,design==MARVIN_EYE_DESIGN_CLASSIC?8:0,design==MARVIN_EYE_DESIGN_CLASSIC?8:0,32);
        case MARVIN_EYES_THINKING:
            if(design==MARVIN_EYE_DESIGN_SOLID)return eye?pose(29,29,0,0,32):pose(26,26,0,0,32);
            return eye?pose(31,31,9,9,32):pose(32,32,5,5,32);
        case MARVIN_EYES_SPEAKING:{
            int phase=(int)((now/70000+eye*3)%12);
            int wave=phase<6?phase:11-phase;
            int energy=clamp((int)atomic_load(&voice_level),0,1000);
            if(design==MARVIN_EYE_DESIGN_SOLID){
                int radius=clamp(32-wave/2-energy*5/1000,23,32);
                return pose(radius,radius,0,0,32-energy/500);
            }
            /* Voice energy opens the silhouette and contracts the core. A
             * quieter phase-offset ripple prevents lifeless digital holds. */
            int emphasis=energy*3/1000;
            int inner=clamp(11-wave/2-energy*4/1000,5,11);
            return pose(29+wave/3+emphasis,29+wave/3+emphasis,inner,inner,32-energy/500);
        }
        case MARVIN_EYES_CONCERNED:
            if(design==MARVIN_EYE_DESIGN_SOLID)return eye?pose(28,26,0,0,33):pose(29,27,0,0,31);
            return eye?pose(29,27,9,8,33):pose(31,29,7,6,31);
        case MARVIN_EYES_SLEEPING:
            return pose(20,2,0,0,34);
        default:
            return pose(32,32,design==MARVIN_EYE_DESIGN_CLASSIC?12:0,design==MARVIN_EYE_DESIGN_CLASSIC?12:0,32);
    }
}
static marvin_eye_pose_t sequence_pose(int sequence,unsigned eye,int elapsed,marvin_eye_pose_t from,marvin_eye_design_t design){
    int delay=sequence==SEQUENCE_WAKE?(eye?130:0):(eye?0:120);
    int local=elapsed-delay;
    if(local<=0)return from;
    marvin_eye_pose_t solid=pose(32,32,0,0,32);
    marvin_eye_pose_t neutral=expression_pose(MARVIN_EYES_NEUTRAL,eye,0,design);
    marvin_eye_pose_t sleeping=expression_pose(MARVIN_EYES_SLEEPING,eye,0,design);
    if(sequence==SEQUENCE_WAKE){
        if(local<420)return pose_mix(from,solid,local*1000/420);
        if(local<560)return solid;
        if(local<920)return pose_mix(solid,neutral,(local-560)*1000/360);
        return neutral;
    }
    if(local<260)return pose_mix(from,solid,local*1000/260);
    if(local<620)return pose_mix(solid,sleeping,(local-260)*1000/360);
    return sleeping;
}
static int squeeze_amount(int elapsed,unsigned eye){
    elapsed-=(int)eye*25;
    if(elapsed<0)return 0;
    if(elapsed<80)return smoothstep(elapsed*1000/80);
    if(elapsed<115)return 1000;
    if(elapsed<270)return 1000-smoothstep((elapsed-115)*1000/155);
    return 0;
}
static esp_err_t install_display(display_t *display,i2c_master_bus_handle_t bus,uint8_t address){
    esp_err_t err=i2c_master_probe(bus,address,100);
    if(err!=ESP_OK)return err;
    esp_lcd_panel_io_i2c_config_t io_config={
        .dev_addr=address,.scl_speed_hz=400000,.control_phase_bytes=1,
        .dc_bit_offset=6,.lcd_cmd_bits=8,.lcd_param_bits=8,
    };
    if((err=esp_lcd_new_panel_io_i2c(bus,&io_config,&display->io))!=ESP_OK)return err;
    esp_lcd_panel_ssd1306_config_t ssd1306={.height=MARVIN_EYE_HEIGHT};
    esp_lcd_panel_dev_config_t panel_config={.reset_gpio_num=-1,.bits_per_pixel=1,.vendor_config=&ssd1306};
    if((err=esp_lcd_new_panel_ssd1306(display->io,&panel_config,&display->panel))!=ESP_OK){esp_lcd_panel_io_del(display->io);display->io=NULL;return err;}
    if((err=esp_lcd_panel_reset(display->panel))==ESP_OK)err=esp_lcd_panel_init(display->panel);
    /* The eye modules are mounted connector-up, 180 degrees from the
     * SSD1306 driver's native scan direction. Rotate at the panel boundary so
     * every rendered cue keeps the framebuffer's normal top-left origin. */
    if(err==ESP_OK)err=esp_lcd_panel_mirror(display->panel,true,true);
    if(err==ESP_OK)err=esp_lcd_panel_invert_color(display->panel,false);
    if(err!=ESP_OK){esp_lcd_panel_del(display->panel);esp_lcd_panel_io_del(display->io);memset(display,0,sizeof(*display));return err;}
    display->enabled=true;
    return ESP_OK;
}
static void flush(display_t *display){
    if(!display->enabled)return;
    if(display->shown_valid&&!memcmp(display->pixels,display->shown,sizeof(display->pixels)))return;
    esp_err_t err=esp_lcd_panel_draw_bitmap(display->panel,0,0,MARVIN_EYE_WIDTH,MARVIN_EYE_HEIGHT,display->pixels);
    if(err==ESP_OK){
        memcpy(display->shown,display->pixels,sizeof(display->shown));
        display->shown_valid=true;display->failures=0;return;
    }
    if(++display->failures==3){
        display->enabled=false;atomic_store(&pair_available,false);
        ESP_LOGE(TAG,"eye display disabled after repeated I2C failures: %s",esp_err_to_name(err));
    }
}
static void task(void *unused){
    (void)unused;
    marvin_eye_pose_t current[2]={pose(20,2,0,0,34),pose(20,2,0,0,34)};
    marvin_eye_pose_t sequence_from[2];
    int sequence=SEQUENCE_NONE,look_x=0,look_y=0,idle_x=0,idle_y=0;
    int64_t now=esp_timer_get_time(),sequence_began=0,squeeze_began=0,next_frame=now;
    int64_t next_idle=now+1000000,next_squeeze=now+2600000+(esp_random()%2600000);
    int64_t next_battery=now;
    bool low_battery=false;
    for(;;){
        now=esp_timer_get_time();
        marvin_eye_design_t design=(marvin_eye_design_t)clamp(atomic_load(&requested_design),MARVIN_EYE_DESIGN_CLASSIC,MARVIN_EYE_DESIGN_COUNT-1);
        if(now>=next_battery){
            marvin_battery_status_t battery;
            if(marvin_battery_monitor_read(&battery)&&battery.available){
                low_battery=battery.level_percent<=(low_battery?LOW_BATTERY_EXIT_PERCENT:LOW_BATTERY_ENTER_PERCENT);
            }
            next_battery=now+BATTERY_SAMPLE_US;
        }
        int requested_sequence=atomic_exchange(&pending_sequence,SEQUENCE_NONE);
        if(requested_sequence==SEQUENCE_WAKE_INSTANT||requested_sequence==SEQUENCE_SLEEP_INSTANT){
            marvin_eye_expression_t state=requested_sequence==SEQUENCE_WAKE_INSTANT?MARVIN_EYES_NEUTRAL:MARVIN_EYES_SLEEPING;
            current[0]=expression_pose(state,0,now,design);current[1]=expression_pose(state,1,now,design);
            sequence=SEQUENCE_NONE;requested_sequence=SEQUENCE_NONE;
        }
        if(requested_sequence!=SEQUENCE_NONE){
            sequence=requested_sequence;sequence_began=now;squeeze_began=0;
            sequence_from[0]=current[0];sequence_from[1]=current[1];
        }
        bool is_awake=atomic_load(&awake);
        marvin_eye_expression_t expression=is_awake?(marvin_eye_expression_t)clamp(atomic_load(&requested_expression),MARVIN_EYES_NEUTRAL,MARVIN_EYES_CONCERNED):MARVIN_EYES_SLEEPING;
        if(is_awake&&now>=next_idle){
            int amplitude=expression==MARVIN_EYES_LISTENING?260:500;
            idle_x=(int)(esp_random()%(unsigned)(amplitude*2+1))-amplitude;
            idle_y=(int)(esp_random()%(unsigned)(amplitude+1))-amplitude/2;
            next_idle=now+1400000+(esp_random()%2800000);
        }
        bool explicit_gaze=is_awake&&now<atomic_load(&gaze_until_us);
        int desired_x=explicit_gaze?atomic_load(&gaze_x):idle_x;
        int desired_y=explicit_gaze?atomic_load(&gaze_y):idle_y;
        int remaining_yaw=atomic_load(&target_yaw)-atomic_load(&head_yaw);
        int remaining_pitch=atomic_load(&target_pitch)-atomic_load(&head_pitch);
        desired_x=clamp(desired_x+remaining_yaw*18,-1000,1000);
        desired_y=clamp(desired_y-remaining_pitch*18,-1000,1000);
        if(expression==MARVIN_EYES_THINKING&&!explicit_gaze){desired_x=clamp(desired_x+260,-1000,1000);desired_y=clamp(desired_y-220,-1000,1000);}
        look_x=approach(look_x,is_awake?desired_x:0,4);look_y=approach(look_y,is_awake?desired_y:0,4);
        if(sequence==SEQUENCE_NONE&&is_awake&&!squeeze_began&&now>=next_squeeze)squeeze_began=now;
        int elapsed_sequence=(int)((now-sequence_began)/1000);
        if(sequence==SEQUENCE_WAKE&&elapsed_sequence>=1070){sequence=SEQUENCE_NONE;current[0]=expression_pose(MARVIN_EYES_NEUTRAL,0,now,design);current[1]=expression_pose(MARVIN_EYES_NEUTRAL,1,now,design);}
        if(sequence==SEQUENCE_SLEEP&&elapsed_sequence>=740){sequence=SEQUENCE_NONE;current[0]=expression_pose(MARVIN_EYES_SLEEPING,0,now,design);current[1]=expression_pose(MARVIN_EYES_SLEEPING,1,now,design);}
        if(squeeze_began&&now-squeeze_began>=300000){squeeze_began=0;next_squeeze=now+2400000+(esp_random()%3800000);}
        if(now>=next_frame){
            for(unsigned eye=0;eye<2;eye++){
                if(sequence!=SEQUENCE_NONE)current[eye]=sequence_pose(sequence,eye,elapsed_sequence,sequence_from[eye],design);
                else pose_approach(&current[eye],expression_pose(expression,eye,now,design),3);
                marvin_eye_pose_t frame=current[eye];
                if(sequence==SEQUENCE_NONE&&squeeze_began)marvin_eye_squeeze(&frame,squeeze_amount((int)((now-squeeze_began)/1000),eye));
                if(sequence==SEQUENCE_NONE&&is_awake){
                    /* Gaze belongs to the robot eye as a whole, never to the
                     * black core independently. */
                    frame.center_x+=look_x*GAZE_X_PIXELS/1000;
                    frame.center_y+=look_y*GAZE_Y_PIXELS/1000;
                }
                marvin_eye_render(displays[eye].pixels,&frame,(marvin_eye_render_design_t)design);
            }
            if(now<atomic_load(&volume_until_us))marvin_eye_render_volume(displays[0].pixels,atomic_load(&overlay_volume));
            if(low_battery)marvin_eye_render_low_battery(displays[1].pixels);
            flush(&displays[0]);flush(&displays[1]);
            do next_frame+=FRAME_PERIOD_US;while(next_frame<=now);
        }
        vTaskDelay(pdMS_TO_TICKS(LOGIC_TICK_MS));
    }
}
esp_err_t marvin_eyes_init(void){
    nvs_handle_t preferences;
    if(nvs_open("pet_eyes",NVS_READONLY,&preferences)==ESP_OK){
        uint8_t design=0;if(nvs_get_u8(preferences,"design",&design)==ESP_OK&&design<MARVIN_EYE_DESIGN_COUNT)atomic_store(&requested_design,design);
        nvs_close(preferences);
    }
    esp_err_t err=marvin_board_i2c_init();if(err!=ESP_OK)return err;
    i2c_master_bus_handle_t bus=marvin_board_i2c_bus();
    esp_err_t left=install_display(&displays[0],bus,LEFT_ADDRESS);
    esp_err_t right=install_display(&displays[1],bus,RIGHT_ADDRESS);
    marvin_eye_design_t design=marvin_eyes_design();
    marvin_eye_pose_t sleeping=expression_pose(MARVIN_EYES_SLEEPING,0,0,design);
    marvin_eye_render(displays[0].pixels,&sleeping,(marvin_eye_render_design_t)design);marvin_eye_render(displays[1].pixels,&sleeping,(marvin_eye_render_design_t)design);
    /* Preload both dark panels and issue DISPLAY_ON back-to-back. The generic
     * helper waits 100 ms per call and would make the eyes visibly stagger. */
    if(left==ESP_OK)left=esp_lcd_panel_draw_bitmap(displays[0].panel,0,0,MARVIN_EYE_WIDTH,MARVIN_EYE_HEIGHT,displays[0].pixels);
    if(right==ESP_OK)right=esp_lcd_panel_draw_bitmap(displays[1].panel,0,0,MARVIN_EYE_WIDTH,MARVIN_EYE_HEIGHT,displays[1].pixels);
    if(left==ESP_OK){memcpy(displays[0].shown,displays[0].pixels,sizeof(displays[0].shown));displays[0].shown_valid=true;}
    if(right==ESP_OK){memcpy(displays[1].shown,displays[1].pixels,sizeof(displays[1].shown));displays[1].shown_valid=true;}
    if(left==ESP_OK)left=esp_lcd_panel_io_tx_param(displays[0].io,SSD1306_CMD_DISPLAY_ON,NULL,0);
    if(right==ESP_OK)right=esp_lcd_panel_io_tx_param(displays[1].io,SSD1306_CMD_DISPLAY_ON,NULL,0);
    displays[0].enabled=left==ESP_OK;displays[1].enabled=right==ESP_OK;
    if(left!=ESP_OK&&right!=ESP_OK){ESP_LOGW(TAG,"no SSD1306 eyes found at 0x3c/0x3d");return ESP_ERR_NOT_FOUND;}
    atomic_store(&pair_available,left==ESP_OK&&right==ESP_OK);
    if(!atomic_load(&pair_available))ESP_LOGW(TAG,"one eye missing; continuing in degraded display mode");
    if(xTaskCreate(task,"eyes",4096,NULL,2,NULL)!=pdPASS)return ESP_ERR_NO_MEM;
    ESP_LOGI(TAG,"cinematic eye animation started: left=%s right=%s rate=15fps",left==ESP_OK?"ready":"missing",right==ESP_OK?"ready":"missing");
    return ESP_OK;
}
bool marvin_eyes_available(void){return atomic_load(&pair_available);}
marvin_eye_design_t marvin_eyes_design(void){return (marvin_eye_design_t)clamp(atomic_load(&requested_design),MARVIN_EYE_DESIGN_CLASSIC,MARVIN_EYE_DESIGN_COUNT-1);}
esp_err_t marvin_eyes_set_design(marvin_eye_design_t design){
    if(design<MARVIN_EYE_DESIGN_CLASSIC||design>=MARVIN_EYE_DESIGN_COUNT)return ESP_ERR_INVALID_ARG;
    nvs_handle_t preferences;esp_err_t result=nvs_open("pet_eyes",NVS_READWRITE,&preferences);if(result!=ESP_OK)return result;
    result=nvs_set_u8(preferences,"design",(uint8_t)design);if(result==ESP_OK)result=nvs_commit(preferences);nvs_close(preferences);
    if(result==ESP_OK)atomic_store(&requested_design,design);
    return result;
}
void marvin_eyes_expression(marvin_eye_expression_t expression){if(expression>=MARVIN_EYES_NEUTRAL&&expression<=MARVIN_EYES_CONCERNED)atomic_store(&requested_expression,expression);}
void marvin_eyes_connection(bool connected,bool animate){
    bool previous=atomic_exchange(&awake,connected);
    if(connected)atomic_store(&requested_expression,MARVIN_EYES_NEUTRAL);
    if(previous==connected)return;
    atomic_store(&pending_sequence,animate?(connected?SEQUENCE_WAKE:SEQUENCE_SLEEP):(connected?SEQUENCE_WAKE_INSTANT:SEQUENCE_SLEEP_INSTANT));
}
void marvin_eyes_wake(void){marvin_eyes_connection(true,true);}
void marvin_eyes_sleep(void){marvin_eyes_connection(false,true);}
void marvin_eyes_gaze(int x,int y,uint32_t duration_ms){
    atomic_store(&gaze_x,clamp(x,-1000,1000));atomic_store(&gaze_y,clamp(y,-1000,1000));
    atomic_store(&gaze_until_us,esp_timer_get_time()+(int64_t)clamp((int)duration_ms,100,3000)*1000);
}
void marvin_eyes_voice_level(unsigned level){atomic_store(&voice_level,(unsigned)clamp((int)level,0,1000));}
void marvin_eyes_head_target(int yaw,int pitch){atomic_store(&target_yaw,clamp(yaw,-80,80));atomic_store(&target_pitch,clamp(pitch,-45,45));}
void marvin_eyes_head_pose(int yaw,int pitch){atomic_store(&head_yaw,clamp(yaw,-80,80));atomic_store(&head_pitch,clamp(pitch,-45,45));}
void marvin_eyes_drive(int left,int right,uint32_t duration_ms){
    int turn=clamp((left-right)*6,-1000,1000);
    int travel=clamp(-(left+right)*2,-300,300);
    marvin_eyes_gaze(turn,travel,duration_ms);
}
void marvin_eyes_volume(unsigned volume_percent){
    atomic_store(&overlay_volume,(unsigned)clamp((int)volume_percent,0,100));
    atomic_store(&volume_until_us,esp_timer_get_time()+VOLUME_OVERLAY_US);
}
