#pragma once
#include <stdbool.h>
#include <stdint.h>
#include <stddef.h>
#include "esp_err.h"
esp_err_t marvin_body_audio_init(void);
bool marvin_body_audio_available(void);
void marvin_body_capture(bool active);
size_t marvin_body_take_input(int16_t *pcm,size_t capacity);
size_t marvin_body_input_waiting(void);
void marvin_body_audio_turn(const uint8_t id[16]);
bool marvin_body_audio_append(const uint8_t *frame,size_t length);
void marvin_body_audio_flush(void);
unsigned marvin_body_audio_fault(void);
bool marvin_body_audio_playing(void);
/* Queue one short local cue after the authenticated backend welcome. */
bool marvin_body_audio_ready_cue(void);

void marvin_body_audio_status(void);

/* Stops audio tasks/DMA until reboot; call only after transport shutdown. */
bool marvin_body_quiesce(void);

/* Boot-health diagnostics; never activates capture or playback. */
bool marvin_body_health_ready(void);
uint32_t marvin_body_health_progress(void);

/* Persistent Pet audio settings, serialized with playback. */
bool marvin_body_adjust_volume(int delta);
bool marvin_body_set_volume(unsigned volume);
unsigned marvin_body_volume(void);
bool marvin_body_set_microphone_gain(unsigned gain_db);
unsigned marvin_body_microphone_gain(void);
bool marvin_body_set_microphone_muted(bool muted);
bool marvin_body_microphone_muted(void);

void marvin_body_wake_activation(bool enabled);
