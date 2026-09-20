#pragma once
#include <stdbool.h>
#include "esp_err.h"

typedef struct {
 bool available;
 unsigned voltage_mv;
 unsigned level_percent;
 /* Revision 1.1 has no MCU-readable charger signal. Keep this explicit so a
  * later board can supply it without changing the device or UI protocol. */
 bool charging_supported;
 bool charging;
} marvin_battery_status_t;

esp_err_t marvin_battery_monitor_init(void);
bool marvin_battery_monitor_read(marvin_battery_status_t *status);
