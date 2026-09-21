#pragma once

#include "driver/i2c_master.h"
#include "esp_err.h"

/* The Waveshare board routes its codecs, GPIO expander and Marvin's two eye
 * displays over the same physical bus.  This module is the only owner of the
 * controller; individual devices may still select their own supported clock. */
esp_err_t marvin_board_i2c_init(void);
i2c_master_bus_handle_t marvin_board_i2c_bus(void);
