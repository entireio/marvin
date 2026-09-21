#pragma once

#include <stddef.h>
#include <stdint.h>

enum { MARVIN_CONNECTION_CHOREOGRAPHY_MAX_STEPS = 18 };

typedef enum {
    MARVIN_CONNECTION_EYES_CURIOUS = 0,
    MARVIN_CONNECTION_EYES_FOCUSED,
    MARVIN_CONNECTION_EYES_NEUTRAL,
} marvin_connection_eyes_t;

typedef struct {
    int8_t yaw;
    int8_t pitch;
    int8_t turn_percent;
    int16_t gaze_x;
    int16_t gaze_y;
    uint16_t duration_ms;
    marvin_connection_eyes_t eyes;
} marvin_connection_step_t;

typedef struct {
    marvin_connection_step_t steps[MARVIN_CONNECTION_CHOREOGRAPHY_MAX_STEPS];
    size_t count;
} marvin_connection_choreography_t;

/* Builds a bounded look-turn-look-return performance. The caller supplies the
 * random seed so planning stays deterministic and host-testable. */
void marvin_connection_choreography_plan(marvin_connection_choreography_t *plan,
                                         uint32_t seed);
