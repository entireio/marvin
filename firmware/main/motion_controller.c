#include "motion_controller.h"
#include "actuators.h"
#include "esp_random.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"

enum { TICK_MS = 20, IDLE_MIN_MS = 12000, IDLE_SPAN_MS = 18000 };
typedef struct { int yaw, pitch; uint32_t duration_ms; } wake_pose_t;
/* Start by looking up 45 degrees, then trace a slow oval with both servos. */
static const wake_pose_t wake_poses[]={
    {0,45,850}, {20,32,1050}, {30,0,1050}, {20,-32,1050},
    {0,-40,1050}, {-20,-32,1050}, {-30,0,1050}, {-20,32,1050}, {0,0,850},
};
typedef struct {
    int yaw, pitch;
    int from_yaw, from_pitch, target_yaw, target_pitch;
    int64_t began_us, until_us, idle_at_us;
    int cue_from, cue_to;
    int64_t cue_began_us, cue_until_us;
    marvin_motion_cue_t cue;
    bool started, explicit, idle_enabled, wake_active;
    size_t wake_pose;
    int remote_throttle,remote_turn,remote_head_yaw,remote_head_pitch;
    int drive_left,drive_right,remote_yaw,remote_pitch;
    int64_t remote_at_us;
    bool remote_active,remote_auto;
} controller_t;
static controller_t state;
static SemaphoreHandle_t lock;

static int clamp(int value, int low, int high){return value<low?low:value>high?high:value;}
static int approach(int current,int target,int step){return current<target?(current+step>target?target:current+step):(current-step<target?target:current-step);}
static int signed_curve(int value){int sign=value<0?-1:1, magnitude=value<0?-value:value;if(magnitude<80)return 0;return sign*magnitude*magnitude/1000;}
/* Point the head according to how rotational the drive gesture is.  A pure
 * sideways command may lead the body by 50 degrees.  Adding forward motion
 * reduces that lead: for example, 100% forward plus 30% turn produces about
 * 8 degrees rather than treating the curve like an in-place rotation.
 *
 * Use the uncurved stick intent for the ratio so the relationship follows the
 * direction the operator is holding, while `turn` supplies the established
 * dead zone and direction.  Integer arithmetic keeps this deterministic in
 * the 50 Hz control loop. */
static int autonomous_turn_yaw(int throttle_intent,int turn_intent,int turn){
    if(!turn)return 0;
    int throttle_magnitude=throttle_intent<0?-throttle_intent:throttle_intent;
    int turn_magnitude=turn_intent<0?-turn_intent:turn_intent;
    int total=throttle_magnitude+turn_magnitude;
    if(!total)return 0;
    int rotation_share=turn_magnitude*1000/total;
    int maximum=20+30*rotation_share/1000;
    int yaw=turn_magnitude*maximum/1000;
    return clamp(turn>0?yaw:-yaw,-50,50);
}
/* The loaded pet does not overcome track stiction reliably below about 55%.
 * Preserve fine stick control in the intent domain, then map any requested
 * motion above the dead zone into the usable 60..100% motor range. The ramp
 * below keeps this compensation from becoming a mechanical step. */
static int drive_percent(int value){
    int sign=value<0?-1:1,magnitude=value<0?-value:value;
    if(!magnitude)return 0;
    magnitude=clamp(magnitude,0,1000);
    return sign*(60+magnitude*40/1000);
}
static int cue_pitch(marvin_motion_cue_t cue){
    switch(cue){case MARVIN_MOTION_CUE_WAKE: return 5;case MARVIN_MOTION_CUE_LISTENING:return 8;case MARVIN_MOTION_CUE_THINKING:return -5;case MARVIN_MOTION_CUE_SPEAKING:return 3;default:return 0;}
}
static int eased(int progress){return progress*progress*(3000-2*progress)/1000000;}
static int cue_value_locked(int64_t now){
    int64_t total=state.cue_until_us-state.cue_began_us, elapsed=now-state.cue_began_us;
    int progress=total<=0?1000:clamp((int)(elapsed*1000/total),0,1000);
    return state.cue_from+(state.cue_to-state.cue_from)*eased(progress)/1000;
}
static int64_t next_idle(int64_t now){return now+(int64_t)(IDLE_MIN_MS+(esp_random()%IDLE_SPAN_MS))*1000;}
static void target_locked(int yaw,int pitch,uint32_t duration,int64_t now){
    state.from_yaw=state.yaw;state.from_pitch=state.pitch;
    state.target_yaw=clamp(yaw,-40,40);state.target_pitch=clamp(pitch,-45,45);
    state.began_us=now;state.until_us=now+(int64_t)duration*1000;state.started=true;
}
static void task(void *unused){
    (void)unused;
    for(;;){
        int yaw=0,pitch=0;bool output=false,drive=false;int left=0,right=0;int64_t now=esp_timer_get_time();
        xSemaphoreTake(lock,portMAX_DELAY);
        /* Remote control owns the real-time layer. It receives intention, not
         * PWM: a 50Hz loop curves sticks, limits acceleration and holds a
         * 120ms actuator watchdog. Loss of either transport is therefore safe. */
        if(state.remote_active){
            bool fresh=now-state.remote_at_us<=250000;
            int throttle=fresh?signed_curve(state.remote_throttle):0;
            int turn=fresh?signed_curve(state.remote_turn):0;
            int requested_left=drive_percent(clamp(throttle+turn*7/10,-1000,1000));
            int requested_right=drive_percent(clamp(throttle-turn*7/10,-1000,1000));
            /* Reach usable torque in roughly 150 ms, while stopping faster
             * when the operator releases the control. */
            int acceleration=(requested_left==0&&requested_right==0)?12:8;
            state.drive_left=approach(state.drive_left,requested_left,acceleration);
            state.drive_right=approach(state.drive_right,requested_right,acceleration);
            if(!fresh&&state.drive_left==0&&state.drive_right==0)state.remote_active=false;
            if(state.remote_active){
                /* Look into a turn before the body reaches full turn rate, then
                 * ease home as stick turn returns to centre. Straight travel
                 * uses a slow, bounded scan. Manual input has full physical
                 * authority and blends with (rather than disables) the
                 * autonomous pose. */
                int auto_yaw=0,auto_pitch=0;
                if(state.remote_auto){
                    if(turn){
                        auto_yaw=autonomous_turn_yaw(state.remote_throttle,state.remote_turn,turn);
                    }else{
                        int phase=(int)((now/100000)%80);
                        auto_yaw=(phase<40?phase-20:60-phase)/2;
                        auto_pitch=((phase+17)%32-16)/5;
                    }
                }
                int desired_yaw=clamp(auto_yaw+state.remote_head_yaw*80/1000,-80,80);
                int desired_pitch=clamp(auto_pitch+state.remote_head_pitch*30/1000,-30,30);
                state.remote_yaw=approach(state.remote_yaw,desired_yaw,2);
                state.remote_pitch=approach(state.remote_pitch,desired_pitch,2);
                state.yaw=state.remote_yaw;state.pitch=state.remote_pitch;
                yaw=state.yaw;pitch=state.pitch;output=true;left=state.drive_left;right=state.drive_right;drive=true;
            }
        }
        if(!state.remote_active&&state.started){
            int64_t total=state.until_us-state.began_us, elapsed=now-state.began_us;
            int progress=total<=0?1000:clamp((int)(elapsed*1000/total),0,1000);
            /* Smoothstep avoids the audible jerk at either end of a move. */
            int curve=eased(progress);
            state.yaw=state.from_yaw+(state.target_yaw-state.from_yaw)*curve/1000;
            state.pitch=state.from_pitch+(state.target_pitch-state.from_pitch)*curve/1000;
            if(progress==1000){
                state.yaw=state.target_yaw;state.pitch=state.target_pitch;state.started=false;
                if(state.wake_active && ++state.wake_pose<sizeof(wake_poses)/sizeof(wake_poses[0])){
                    wake_pose_t next=wake_poses[state.wake_pose];target_locked(next.yaw,next.pitch,next.duration_ms,now);
                }else{
                    state.wake_active=false;
                    if(state.explicit){state.cue_from=0;state.cue_began_us=now;state.cue_until_us=now+160000;}
                    state.explicit=false;state.idle_at_us=next_idle(now);
                }
            }
            output=true;
        }else if(!state.remote_active&&state.idle_enabled&&state.cue==MARVIN_MOTION_CUE_NONE&&now>=state.idle_at_us){
            int idle_yaw=clamp(state.yaw+(int)(esp_random()%11)-5,-20,20);
            int idle_pitch=clamp(state.pitch+(int)(esp_random()%7)-3,-12,12);
            target_locked(idle_yaw,idle_pitch,900+(esp_random()%700),now);
            output=true;
        }
        if(output){yaw=state.yaw;pitch=clamp(state.pitch+(state.explicit?0:cue_value_locked(now)),-30,30);}
        xSemaphoreGive(lock);
        if(output)(void)marvin_head_pose(yaw,pitch);
        if(drive)(void)marvin_tracks_set(left,right,120);
        vTaskDelay(pdMS_TO_TICKS(TICK_MS));
    }
}
esp_err_t marvin_motion_init(void){
    if(lock)return ESP_ERR_INVALID_STATE;
    lock=xSemaphoreCreateMutex();if(!lock)return ESP_ERR_NO_MEM;
    int64_t now=esp_timer_get_time();state.idle_enabled=true;state.idle_at_us=next_idle(now);
    if(xTaskCreate(task,"motion",3072,NULL,4,NULL)!=pdPASS){vSemaphoreDelete(lock);lock=NULL;return ESP_ERR_NO_MEM;}
    return ESP_OK;
}
esp_err_t marvin_motion_head_request(int yaw,int pitch,uint32_t duration){
    if(!lock||yaw < -40||yaw > 40||pitch < -30||pitch > 30||duration<100||duration>3000)return ESP_ERR_INVALID_ARG;
    int64_t now=esp_timer_get_time();xSemaphoreTake(lock,portMAX_DELAY);
    /* Begin the user request at the visible cue pose, never by snapping it off. */
    state.pitch=clamp(state.pitch+cue_value_locked(now),-20,20);state.cue=MARVIN_MOTION_CUE_NONE;state.cue_from=state.cue_to=0;state.cue_began_us=state.cue_until_us=now;
    state.remote_active=false;state.wake_active=false;target_locked(yaw,pitch,duration,now);state.explicit=true;xSemaphoreGive(lock);return ESP_OK;
}
esp_err_t marvin_remote_input(int throttle,int turn,int head_yaw,int head_pitch,bool autonomous_head){
    if(!lock||throttle < -1000||throttle > 1000||turn < -1000||turn > 1000||head_yaw < -1000||head_yaw > 1000||head_pitch < -1000||head_pitch > 1000)return ESP_ERR_INVALID_ARG;
    int64_t now=esp_timer_get_time();xSemaphoreTake(lock,portMAX_DELAY);
    if(!state.remote_active){state.remote_yaw=state.yaw;state.remote_pitch=state.pitch;state.drive_left=state.drive_right=0;}
    state.started=false;state.wake_active=false;state.explicit=false;state.remote_active=true;state.remote_throttle=throttle;state.remote_turn=turn;state.remote_head_yaw=head_yaw;state.remote_head_pitch=head_pitch;state.remote_auto=autonomous_head;state.remote_at_us=now;xSemaphoreGive(lock);return ESP_OK;
}
void marvin_remote_stop(void){if(!lock)return;xSemaphoreTake(lock,portMAX_DELAY);state.remote_active=false;state.drive_left=state.drive_right=0;xSemaphoreGive(lock);marvin_tracks_stop();}
void marvin_motion_cue(marvin_motion_cue_t cue){
    if(!lock)return;
    int64_t now=esp_timer_get_time();
    xSemaphoreTake(lock,portMAX_DELAY);
    if(state.wake_active && cue!=MARVIN_MOTION_CUE_NONE){xSemaphoreGive(lock);return;}
    int previous=cue_value_locked(now);
    state.cue=cue;state.cue_from=previous;state.cue_to=cue_pitch(cue);
    state.cue_began_us=now;state.cue_until_us=now+180000;state.idle_at_us=next_idle(now);
    /* A cue interrupts only idle motion; explicit trajectories retain priority. */
    if(!state.started)target_locked(state.yaw,state.pitch,180,now);
    xSemaphoreGive(lock);
}
void marvin_motion_wake(void){
    if(!lock)return;
    int64_t now=esp_timer_get_time();xSemaphoreTake(lock,portMAX_DELAY);
    /* The device-link wake request follows the local detector; do not restart it. */
    if(!state.wake_active){
        state.cue=MARVIN_MOTION_CUE_NONE;state.cue_from=state.cue_to=0;
        state.cue_began_us=state.cue_until_us=now;state.explicit=false;
        state.wake_active=true;state.wake_pose=0;
        wake_pose_t first=wake_poses[0];target_locked(first.yaw,first.pitch,first.duration_ms,now);
    }
    xSemaphoreGive(lock);
}
void marvin_motion_idle_enabled(bool enabled){if(!lock)return;xSemaphoreTake(lock,portMAX_DELAY);state.idle_enabled=enabled;state.idle_at_us=next_idle(esp_timer_get_time());xSemaphoreGive(lock);}
void marvin_motion_stop(void){if(!lock)return;xSemaphoreTake(lock,portMAX_DELAY);state.cue=MARVIN_MOTION_CUE_NONE;state.wake_active=false;state.started=false;state.remote_active=false;state.idle_enabled=false;xSemaphoreGive(lock);marvin_tracks_stop();}
