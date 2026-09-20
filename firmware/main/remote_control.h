#pragma once
#include "esp_err.h"
#include <stdbool.h>
#include <stdint.h>

/* BLE and the authenticated web link use this same small intent surface. */
typedef struct { int throttle, turn, head_yaw, head_pitch; bool autonomous_head; } marvin_remote_intent_t;
esp_err_t marvin_remote_submit(const marvin_remote_intent_t *intent);
void marvin_remote_disconnect(void);
