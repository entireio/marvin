#pragma once
#include "esp_err.h"

/* Active-low, pull-up GPIO buttons for speaker +/− and microphone mute. */
esp_err_t marvin_pet_controls_start(void);
