#pragma once
/* Bench entry point; no radio, credential, motor or provider access. */
void marvin_audio_diagnostic(void);

#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>
#include "esp_err.h"
/* One capture reader and one playback writer. Fixed 16 kHz, signed 16-bit mono. */
esp_err_t marvin_audio_open(void);
esp_err_t marvin_audio_read(int16_t *mic1,int16_t *mic2,size_t capacity,size_t *frames);
esp_err_t marvin_audio_write(const int16_t *mono,size_t frames);
esp_err_t marvin_audio_mute(bool mute);
/* AFE profile only: completed microphone DMA paired with completed speaker DMA. */
esp_err_t marvin_audio_read_afe(int16_t *interleaved, size_t *frames);

/* Requires all application audio tasks parked. One-way until reboot. */
esp_err_t marvin_audio_quiesce(void);

esp_err_t marvin_audio_set_volume(unsigned volume);
unsigned marvin_audio_volume(void);
