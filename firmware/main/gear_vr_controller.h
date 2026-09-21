#pragma once
#include "esp_err.h"
#include <stddef.h>
#include <stdint.h>

/* Samsung ET-YO324 custom-GATT sensor packet (not a generic HID report). */
esp_err_t marvin_gear_vr_report(const uint8_t *data, size_t length);
void marvin_gear_vr_reset(void);
