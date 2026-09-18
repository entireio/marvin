#pragma once
#include <stdbool.h>
#include <stdint.h>
#include "esp_err.h"
/* Snapshot provider must serialize access to the ownership journal. */
typedef struct {
 char origin[201],device_id[65],credential[96];
 uint32_t epoch;
 int64_t expires_ms;
} marvin_link_identity_t;
typedef bool (*marvin_link_snapshot_t)(marvin_link_identity_t *identity);
esp_err_t marvin_device_link_start(marvin_link_snapshot_t snapshot,const char *ca);
bool marvin_device_link_online(void);

void marvin_device_voice_start(void);
void marvin_device_voice_wake(void);
void marvin_device_voice_stop(void);
void marvin_device_voice_interrupt(void);
/* Report a locally changed GPIO audio setting to the connected web app. */
void marvin_device_audio_changed(void);
void marvin_device_link_status(void);

/* Disconnects the provider/device socket and parks until reboot. */
bool marvin_device_link_quiesce(void);
