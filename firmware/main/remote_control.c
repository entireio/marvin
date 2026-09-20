#include "remote_control.h"
#include "motion_controller.h"

esp_err_t marvin_remote_submit(const marvin_remote_intent_t *intent){
    if(!intent || intent->throttle < -1000 || intent->throttle > 1000 || intent->turn < -1000 || intent->turn > 1000 || intent->head_yaw < -1000 || intent->head_yaw > 1000 || intent->head_pitch < -1000 || intent->head_pitch > 1000)return ESP_ERR_INVALID_ARG;
    return marvin_remote_input(intent->throttle,intent->turn,intent->head_yaw,intent->head_pitch,intent->autonomous_head);
}
void marvin_remote_disconnect(void){marvin_remote_stop();}
