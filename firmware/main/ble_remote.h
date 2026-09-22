#pragma once
#include "esp_err.h"

/* Starts the ET-YO324 custom-GATT BLE central when enabled. Pairing is opened
 * on first boot or by the local USB-console gesture, never by a network command. */
esp_err_t marvin_ble_remote_start(void);
void marvin_ble_remote_open_pairing(void);
