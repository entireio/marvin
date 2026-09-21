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
/* A transport-neutral remote intent. Values are normalized -1000..1000 and
 * must be renewed; stale input brings the tracks smoothly to rest. */
esp_err_t marvin_remote_input(int throttle, int turn, int head_yaw, int head_pitch, bool autonomous_head);
void marvin_remote_stop(void);
void marvin_motion_cue(marvin_motion_cue_t cue);
/* Feed the current speaker envelope (0..1000). Speech motion remains
 * transport-neutral: audio supplies energy, motion decides how to perform it. */
void marvin_motion_voice_level(unsigned level);
/* Starts or cancels the randomized authenticated-connection performance. */
void marvin_motion_connection(bool connected);
void marvin_motion_wake(void);
void marvin_motion_idle_enabled(bool enabled);
void marvin_motion_stop(void);
