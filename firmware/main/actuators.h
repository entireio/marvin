#pragma once
#include "esp_err.h"
#include <stdbool.h>
#include <stdint.h>

typedef struct {
    uint8_t yaw_center;
    uint8_t pitch_center;
    bool yaw_reversed;
    bool pitch_reversed;
} marvin_head_calibration_t;

/* Signed track speed: positive drives current from OUT2/OUT4 (+) to OUT1/OUT3 (-).
 * A command has a caller-selected watchdog. Remote control renews a short
 * watchdog from its own loop; long scripted moves are no longer capped at 500ms. */
esp_err_t marvin_actuators_init(void);
esp_err_t marvin_tracks_set(int left_percent, int right_percent, uint32_t duration_ms);
void marvin_tracks_stop(void);

/* Servo pulses are emitted only after an explicit call on GPIO8 and GPIO9. */
esp_err_t marvin_head_set(uint8_t rotation_degrees, uint8_t tilt_degrees);
/* Semantic pose relative to the configured servo centers. */
esp_err_t marvin_head_pose(int yaw_degrees, int pitch_degrees);
void marvin_head_calibration_get(marvin_head_calibration_t *calibration);
esp_err_t marvin_head_calibration_set(const marvin_head_calibration_t *calibration);
/* Applies a RAM-only center for interactive calibration. The durable value is
 * unchanged and can be restored after a dropped browser/server connection. */
esp_err_t marvin_head_calibration_preview(const marvin_head_calibration_t *calibration);
void marvin_head_calibration_restore(void);
