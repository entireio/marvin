#include "motion_controller.h"
#include "actuators.h"
#include "esp_random.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"

enum { TICK_MS = 20, IDLE_MIN_MS = 12000, IDLE_SPAN_MS = 18000 };
typedef struct {
    int yaw, pitch;
    int from_yaw, from_pitch, target_yaw, target_pitch;
    int64_t began_us, until_us, idle_at_us;
    int cue_from, cue_to;
    int64_t cue_began_us, cue_until_us;
    marvin_motion_cue_t cue;
    bool started, explicit, idle_enabled;
} controller_t;
static controller_t state;
static SemaphoreHandle_t lock;

static int clamp(int value, int low, int high){return value<low?low:value>high?high:value;}
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
    state.target_yaw=clamp(yaw,-30,30);state.target_pitch=clamp(pitch,-20,20);
    state.began_us=now;state.until_us=now+(int64_t)duration*1000;state.started=true;
}
static void task(void *unused){
    (void)unused;
    for(;;){
        int yaw=0,pitch=0;bool output=false;int64_t now=esp_timer_get_time();
        xSemaphoreTake(lock,portMAX_DELAY);
        if(state.started){
            int64_t total=state.until_us-state.began_us, elapsed=now-state.began_us;
            int progress=total<=0?1000:clamp((int)(elapsed*1000/total),0,1000);
            /* Smoothstep avoids the audible jerk at either end of a move. */
            int curve=eased(progress);
            state.yaw=state.from_yaw+(state.target_yaw-state.from_yaw)*curve/1000;
            state.pitch=state.from_pitch+(state.target_pitch-state.from_pitch)*curve/1000;
            if(progress==1000){state.yaw=state.target_yaw;state.pitch=state.target_pitch;state.started=false;if(state.explicit){state.cue_from=0;state.cue_began_us=now;state.cue_until_us=now+160000;}state.explicit=false;state.idle_at_us=next_idle(now);}
            output=true;
        }else if(state.idle_enabled&&state.cue==MARVIN_MOTION_CUE_NONE&&now>=state.idle_at_us){
            int idle_yaw=clamp(state.yaw+(int)(esp_random()%11)-5,-20,20);
            int idle_pitch=clamp(state.pitch+(int)(esp_random()%7)-3,-12,12);
            target_locked(idle_yaw,idle_pitch,900+(esp_random()%700),now);
            output=true;
        }
        if(output){yaw=state.yaw;pitch=clamp(state.pitch+(state.explicit?0:cue_value_locked(now)),-20,20);}
        xSemaphoreGive(lock);
        if(output)(void)marvin_head_pose(yaw,pitch);
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
    if(!lock||yaw < -30||yaw > 30||pitch < -20||pitch > 20||duration<100||duration>3000)return ESP_ERR_INVALID_ARG;
    int64_t now=esp_timer_get_time();xSemaphoreTake(lock,portMAX_DELAY);
    /* Begin the user request at the visible cue pose, never by snapping it off. */
    state.pitch=clamp(state.pitch+cue_value_locked(now),-20,20);state.cue=MARVIN_MOTION_CUE_NONE;state.cue_from=state.cue_to=0;state.cue_began_us=state.cue_until_us=now;
    target_locked(yaw,pitch,duration,now);state.explicit=true;xSemaphoreGive(lock);return ESP_OK;
}
void marvin_motion_cue(marvin_motion_cue_t cue){
    if(!lock)return;
    int64_t now=esp_timer_get_time();
    xSemaphoreTake(lock,portMAX_DELAY);
    int previous=cue_value_locked(now);
    state.cue=cue;state.cue_from=previous;state.cue_to=cue_pitch(cue);
    state.cue_began_us=now;state.cue_until_us=now+180000;state.idle_at_us=next_idle(now);
    /* A cue interrupts only idle motion; explicit trajectories retain priority. */
    if(!state.started)target_locked(state.yaw,state.pitch,180,now);
    xSemaphoreGive(lock);
}
void marvin_motion_wake(void){marvin_motion_cue(MARVIN_MOTION_CUE_WAKE);}
void marvin_motion_idle_enabled(bool enabled){if(!lock)return;xSemaphoreTake(lock,portMAX_DELAY);state.idle_enabled=enabled;state.idle_at_us=next_idle(esp_timer_get_time());xSemaphoreGive(lock);}
void marvin_motion_stop(void){if(!lock)return;xSemaphoreTake(lock,portMAX_DELAY);state.cue=MARVIN_MOTION_CUE_NONE;state.started=false;state.idle_enabled=false;xSemaphoreGive(lock);}
