#pragma once

#include <stdint.h>

enum {
    MARVIN_EYE_WIDTH = 128,
    MARVIN_EYE_HEIGHT = 64,
    MARVIN_EYE_BYTES = MARVIN_EYE_WIDTH * MARVIN_EYE_HEIGHT / 8,
};

typedef enum {
    MARVIN_EYE_RENDER_CLASSIC = 0,
    MARVIN_EYE_RENDER_SOLID,
    MARVIN_EYE_RENDER_FRIENDLY,
} marvin_eye_render_design_t;

/* One eye is a soft white body with an optional centered black core. Gaze
 * translates center_x/center_y for the complete shape. */
typedef struct {
    int center_x;
    int center_y;
    int outer_rx;
    int outer_ry;
    int inner_rx;
    int inner_ry;
} marvin_eye_pose_t;

/* Temporarily compress the complete eye by amount (0..1000), preserving both
 * layers and widening them like a soft object under pressure. */
void marvin_eye_squeeze(marvin_eye_pose_t *pose, int amount);
void marvin_eye_render(uint8_t buffer[MARVIN_EYE_BYTES], const marvin_eye_pose_t *pose, marvin_eye_render_design_t design);

/* Pixel-native status overlays occupy the unused outside edges of the wide
 * displays. They intentionally draw over an already-rendered eye frame. */
void marvin_eye_render_volume(uint8_t buffer[MARVIN_EYE_BYTES], unsigned volume);
void marvin_eye_render_battery(uint8_t buffer[MARVIN_EYE_BYTES], unsigned level_percent, int critical);
