#include "battery_monitor.h"
#include "battery_level.h"
#include "esp_adc/adc_oneshot.h"
#include "esp_adc/adc_cali.h"
#include "esp_adc/adc_cali_scheme.h"
#include <string.h>

/* Waveshare ESP32-S3-AUDIO-Board revision 1.1:
 * BAT -- 200K -- BAT_ADC/GPIO1 -- 100K -- GND, so BAT_ADC is one third of
 * the cell voltage. C11 provides 100 nF filtering at the ADC node. */
#define BATTERY_CHANNEL ADC_CHANNEL_0
#define BATTERY_DIVIDER 3U
#define BATTERY_SAMPLES 16U

static adc_oneshot_unit_handle_t adc;
static adc_cali_handle_t calibration;

esp_err_t marvin_battery_monitor_init(void){
 if(adc&&calibration)return ESP_OK;
 adc_oneshot_unit_init_cfg_t unit={.unit_id=ADC_UNIT_1,.ulp_mode=ADC_ULP_MODE_DISABLE};
 esp_err_t err=adc_oneshot_new_unit(&unit,&adc);if(err!=ESP_OK)return err;
 adc_oneshot_chan_cfg_t channel={.atten=ADC_ATTEN_DB_6,.bitwidth=ADC_BITWIDTH_DEFAULT};
 err=adc_oneshot_config_channel(adc,BATTERY_CHANNEL,&channel);if(err!=ESP_OK)return err;
 adc_cali_curve_fitting_config_t config={.unit_id=ADC_UNIT_1,.chan=BATTERY_CHANNEL,.atten=ADC_ATTEN_DB_6,.bitwidth=ADC_BITWIDTH_DEFAULT};
 return adc_cali_create_scheme_curve_fitting(&config,&calibration);
}

bool marvin_battery_monitor_read(marvin_battery_status_t *status){
 if(!status)return false;
 memset(status,0,sizeof(*status));
 if(!adc||!calibration)return false;
 int discarded;if(adc_oneshot_read(adc,BATTERY_CHANNEL,&discarded)!=ESP_OK)return false;
 unsigned sum=0;
 for(unsigned i=0;i<BATTERY_SAMPLES;i++){
  int raw,millivolts;
  if(adc_oneshot_read(adc,BATTERY_CHANNEL,&raw)!=ESP_OK||adc_cali_raw_to_voltage(calibration,raw,&millivolts)!=ESP_OK||millivolts<0)return false;
  sum+=(unsigned)millivolts;
 }
 unsigned voltage_mv=(sum+BATTERY_SAMPLES/2)/BATTERY_SAMPLES*BATTERY_DIVIDER;
 if(voltage_mv<2500||voltage_mv>5000)return false;
 status->available=true;status->voltage_mv=voltage_mv;status->level_percent=marvin_battery_percent(voltage_mv);
 status->charging_supported=false;status->charging=false;
 return true;
}
