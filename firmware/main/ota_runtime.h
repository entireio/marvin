#pragma once
#include "device_link.h"
/* Optional profile only. No polling or automatic release installation. */
esp_err_t marvin_ota_start(marvin_link_snapshot_t snapshot,const char *ca);
bool marvin_ota_request(uint32_t sequence);
void marvin_ota_cancel(void);
