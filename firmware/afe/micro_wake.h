#pragma once
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include "esp_err.h"
#ifdef __cplusplus
extern "C" {
#endif
esp_err_t marvin_micro_wake_open(void);
bool marvin_micro_wake_feed(const int16_t *pcm,size_t count,bool *detected);
void marvin_micro_wake_reset(void);
void marvin_micro_wake_status(void);
#ifdef __cplusplus
}
#endif
