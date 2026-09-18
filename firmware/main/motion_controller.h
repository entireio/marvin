#pragma once
#include "esp_err.h"
#include <stdbool.h>
#include <stdint.h>

typedef enum {
    MARVIN_MOTION_CUE_NONE = 0,
    MARVIN_MOTION_CUE_WAKE,
    MARVIN_MOTION_CUE_LISTENING,
    MARVIN_MOTION_CUE_THINKING,
    MARVIN_MOTION_CUE_SPEAKING,
} marvin_motion_cue_t;

/* Owns all head trajectories. Calls are non-blocking and bounded. */
esp_err_t marvin_motion_init(void);
esp_err_t marvin_motion_head_request(int yaw_degrees, int pitch_degrees, uint32_t duration_ms);
void marvin_motion_cue(marvin_motion_cue_t cue);
void marvin_motion_wake(void);
void marvin_motion_idle_enabled(bool enabled);
void marvin_motion_stop(void);
