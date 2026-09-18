#include "pet_controls.h"
#include "body_audio.h"
#include "device_link.h"
#include "driver/gpio.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "esp_timer.h"
#include "sdkconfig.h"
#include <stdbool.h>

#if CONFIG_MARVIN_VOLUME_UP_GPIO == CONFIG_MARVIN_VOLUME_DOWN_GPIO || CONFIG_MARVIN_VOLUME_UP_GPIO == CONFIG_MARVIN_MIC_MUTE_GPIO || CONFIG_MARVIN_VOLUME_DOWN_GPIO == CONFIG_MARVIN_MIC_MUTE_GPIO
#error "Desktop Pet audio buttons must use three distinct GPIOs"
#endif

#define DEBOUNCE_US 40000
typedef enum { BUTTON_VOLUME_UP, BUTTON_VOLUME_DOWN, BUTTON_MIC_MUTE } action_t;
typedef struct {gpio_num_t pin;action_t action;bool sample,stable;int64_t changed_at;} button_t;
static button_t buttons[]={
 {.pin=(gpio_num_t)CONFIG_MARVIN_VOLUME_UP_GPIO,.action=BUTTON_VOLUME_UP},
 {.pin=(gpio_num_t)CONFIG_MARVIN_VOLUME_DOWN_GPIO,.action=BUTTON_VOLUME_DOWN},
 {.pin=(gpio_num_t)CONFIG_MARVIN_MIC_MUTE_GPIO,.action=BUTTON_MIC_MUTE}
};

static void pressed(action_t action){
 bool changed=action==BUTTON_VOLUME_UP?marvin_body_adjust_volume(5):action==BUTTON_VOLUME_DOWN?marvin_body_adjust_volume(-5):marvin_body_set_microphone_muted(!marvin_body_microphone_muted());
 if(changed)marvin_device_audio_changed();
}
static void poll(void *unused){
 (void)unused;
 for(;;){
  int64_t now=esp_timer_get_time();
  for(unsigned i=0;i<sizeof(buttons)/sizeof(buttons[0]);i++){
   bool sample=gpio_get_level(buttons[i].pin)==0;
   if(sample!=buttons[i].sample){buttons[i].sample=sample;buttons[i].changed_at=now;}
   else if(sample!=buttons[i].stable&&now-buttons[i].changed_at>=DEBOUNCE_US){buttons[i].stable=sample;if(sample)pressed(buttons[i].action);}
  }
  vTaskDelay(pdMS_TO_TICKS(10));
 }
}
esp_err_t marvin_pet_controls_start(void){
 uint64_t mask=(1ULL<<CONFIG_MARVIN_VOLUME_UP_GPIO)|(1ULL<<CONFIG_MARVIN_VOLUME_DOWN_GPIO)|(1ULL<<CONFIG_MARVIN_MIC_MUTE_GPIO);
 gpio_config_t config={.pin_bit_mask=mask,.mode=GPIO_MODE_INPUT,.pull_up_en=GPIO_PULLUP_ENABLE,.pull_down_en=GPIO_PULLDOWN_DISABLE,.intr_type=GPIO_INTR_DISABLE};
 esp_err_t result=gpio_config(&config);if(result!=ESP_OK)return result;
 int64_t now=esp_timer_get_time();for(unsigned i=0;i<sizeof(buttons)/sizeof(buttons[0]);i++){buttons[i].sample=buttons[i].stable=gpio_get_level(buttons[i].pin)==0;buttons[i].changed_at=now;}
 return xTaskCreate(poll,"pet_buttons",3072,NULL,4,NULL)==pdPASS?ESP_OK:ESP_ERR_NO_MEM;
}
