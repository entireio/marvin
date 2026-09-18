#pragma once
#include "esp_err.h"
#include <stdbool.h>
#include <stdint.h>

/* Signed track speed: positive drives current from OUT2/OUT4 (+) to OUT1/OUT3 (-).
 * A command expires after at most 500 ms; the caller must renew it. */
esp_err_t marvin_actuators_init(void);
esp_err_t marvin_tracks_set(int left_percent, int right_percent, uint32_t duration_ms);
void marvin_tracks_stop(void);
/* Optional supervised bench window; no cliff sensor is currently wired. */
esp_err_t marvin_tracks_bench_arm(uint32_t seconds);
bool marvin_tracks_bench_armed(void);

/* Servo pulses are emitted only after an explicit call. GPIO20 and GPIO19
 * share the board's native USB path, so USB must be disconnected for use. */
esp_err_t marvin_head_set(uint8_t rotation_degrees, uint8_t tilt_degrees);
/* Semantic pose relative to the configured servo centers. */
esp_err_t marvin_head_pose(int yaw_degrees, int pitch_degrees);
