#pragma once

#include "esp_err.h"
#include <stdbool.h>
#include <stdint.h>

typedef enum {
    MARVIN_EYES_NEUTRAL = 0,
    MARVIN_EYES_LISTENING,
    MARVIN_EYES_THINKING,
    MARVIN_EYES_SPEAKING,
    MARVIN_EYES_CONCERNED,
    MARVIN_EYES_SLEEPING,
} marvin_eye_expression_t;

typedef enum {
    MARVIN_EYE_DESIGN_CLASSIC = 0,
    MARVIN_EYE_DESIGN_SOLID,
    MARVIN_EYE_DESIGN_FRIENDLY,
    MARVIN_EYE_DESIGN_COUNT,
} marvin_eye_design_t;

/* Missing displays are non-fatal: init returns ESP_ERR_NOT_FOUND and the rest
 * of Marvin remains usable.  A single responding display runs in degraded
 * mode, while available() reports true only for a complete pair. */
esp_err_t marvin_eyes_init(void);
bool marvin_eyes_available(void);

/* The selected design is stored in the Pet's NVS and restored before the
 * first display frame is rendered. */
marvin_eye_design_t marvin_eyes_design(void);
esp_err_t marvin_eyes_set_design(marvin_eye_design_t design);

/* All control calls are non-blocking.  Coordinates are normalized to
 * -1000..1000 in Marvin's frame; positive X means Marvin's right and positive
 * Y means down.  A bounded gaze eases home after duration_ms. */
void marvin_eyes_expression(marvin_eye_expression_t expression);
void marvin_eyes_gaze(int x, int y, uint32_t duration_ms);
void marvin_eyes_voice_level(unsigned level);

/* Connection transitions are local and non-blocking.  With animation off the
 * same persistent awake/asleep state is applied immediately. */
void marvin_eyes_connection(bool connected, bool animate);
void marvin_eyes_wake(void);
void marvin_eyes_sleep(void);

/* Motion publishes both its destination and interpolated logical pose.  Eyes
 * lead a commanded head turn, then naturally recenter as the head catches up. */
void marvin_eyes_head_target(int yaw_degrees, int pitch_degrees);
void marvin_eyes_head_pose(int yaw_degrees, int pitch_degrees);

/* Track motion gives the eyes a short anticipatory look into a turn. */
void marvin_eyes_drive(int left_percent, int right_percent, uint32_t duration_ms);

/* Briefly show speaker level at the outside edge of the left display. */
void marvin_eyes_volume(unsigned volume_percent);

/* Enable or suppress every battery overlay, including the critical warning. */
void marvin_eyes_show_battery(bool show);
