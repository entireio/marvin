#include "board_i2c.h"

enum { BOARD_I2C_PORT = 0, BOARD_I2C_SDA = 11, BOARD_I2C_SCL = 10 };
static i2c_master_bus_handle_t bus;

esp_err_t marvin_board_i2c_init(void){
    if(bus)return ESP_OK;
    i2c_master_bus_config_t config={
        .i2c_port=BOARD_I2C_PORT,
        .sda_io_num=BOARD_I2C_SDA,
        .scl_io_num=BOARD_I2C_SCL,
        .clk_source=I2C_CLK_SRC_DEFAULT,
        .glitch_ignore_cnt=7,
    };
    return i2c_new_master_bus(&config,&bus);
}

i2c_master_bus_handle_t marvin_board_i2c_bus(void){return bus;}
