#pragma once
#include <stdbool.h>
#include <stdint.h>
#include <stddef.h>
#include "esp_err.h"
/* All audio is 16-kHz mono PCM. Feed the configured microphone channels plus the played reference. */
esp_err_t marvin_afe_open(void);
size_t marvin_afe_feed_size(void);
size_t marvin_afe_feed_channels(void);
bool marvin_afe_feed(const int16_t *interleaved, size_t frames);
typedef struct {
    const int16_t *pcm;
    size_t frames;
    bool wake;
    bool speech;
    bool fault;
} marvin_afe_result_t;
bool marvin_afe_fetch(marvin_afe_result_t *result);

/* Called only by the fetch task. Discards recognizer context when disarmed. */
void marvin_afe_wake_enabled(bool enabled);

void marvin_afe_status(void);
