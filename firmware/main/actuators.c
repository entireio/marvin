#include "actuators.h"
#include "driver/gpio.h"
#include "driver/ledc.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"
#include "sdkconfig.h"
#include <stdatomic.h>

#ifdef CONFIG_MARVIN_AUDIO_BUTTONS
#define ACTUATOR_PIN(p) ((p)>=3 && (p)<=9)
#if ACTUATOR_PIN(CONFIG_MARVIN_VOLUME_UP_GPIO) || ACTUATOR_PIN(CONFIG_MARVIN_VOLUME_DOWN_GPIO) || ACTUATOR_PIN(CONFIG_MARVIN_MIC_MUTE_GPIO)
#error "Audio button GPIO overlaps an actuator GPIO"
#endif
#undef ACTUATOR_PIN
#endif

/* Waveshare ESP32-S3-AUDIO-Board header labels are native ESP32-S3 GPIOs.
 * The pictured IN1..IN4 / EEP / OUT1..OUT4 carrier is a DRV8833 module.
 * EEP is its active-high nSLEEP input, not an enable PWM input. */
enum { LEFT_IN1=7, LEFT_IN2=6, RIGHT_IN3=5, RIGHT_IN4=4,
       DRIVER_EEP=3, HEAD_ROTATE=8, HEAD_TILT=9 };
_Static_assert(HEAD_ROTATE!=19 && HEAD_ROTATE!=20 && HEAD_TILT!=19 && HEAD_TILT!=20,
               "Servos must not use the ESP32-S3 USB data pins");
enum { MOTOR_DUTY_MAX=1023, SERVO_PERIOD_US=20000 };
static SemaphoreHandle_t actuator_lock;
static int64_t drive_deadline_us;
#ifdef CONFIG_MARVIN_TRACK_BENCH_MODE
static atomic_uint bench_deadline_ms;
#endif
static bool servo_started;
static const int motor_pins[]={LEFT_IN1,LEFT_IN2,RIGHT_IN3,RIGHT_IN4};

static esp_err_t duty(ledc_channel_t channel, uint32_t value){
    esp_err_t err=ledc_set_duty(LEDC_LOW_SPEED_MODE,channel,value);
    return err==ESP_OK?ledc_update_duty(LEDC_LOW_SPEED_MODE,channel):err;
}
static void stop_locked(void){
    /* Disable the H-bridges first; only then clear the PWM inputs. */
    gpio_set_level(DRIVER_EEP,0);
    for(int i=0;i<4;i++)duty((ledc_channel_t)i,0);
    drive_deadline_us=0;
}
void marvin_tracks_stop(void){
    if(!actuator_lock)return;
    xSemaphoreTake(actuator_lock,portMAX_DELAY);
    stop_locked();
    xSemaphoreGive(actuator_lock);
}
bool marvin_tracks_bench_armed(void){
#ifdef CONFIG_MARVIN_TRACK_BENCH_MODE
    uint32_t now=(uint32_t)(esp_timer_get_time()/1000);
    return actuator_lock && (int32_t)(now-atomic_load(&bench_deadline_ms))<0;
#else
    return false;
#endif
}
esp_err_t marvin_tracks_bench_arm(uint32_t seconds){
#ifdef CONFIG_MARVIN_TRACK_BENCH_MODE
    if(!actuator_lock || seconds<1 || seconds>120)return ESP_ERR_INVALID_ARG;
    xSemaphoreTake(actuator_lock,portMAX_DELAY);
    stop_locked();
    atomic_store(&bench_deadline_ms,(uint32_t)(esp_timer_get_time()/1000)+seconds*1000);
    xSemaphoreGive(actuator_lock);
    return ESP_OK;
#else
    (void)seconds;
    return ESP_ERR_NOT_SUPPORTED;
#endif
}
static void expiry_task(void *unused){
    (void)unused;
    for(;;){
        if(xSemaphoreTake(actuator_lock,pdMS_TO_TICKS(10))==pdTRUE){
            if(drive_deadline_us && (esp_timer_get_time()>=drive_deadline_us || !marvin_tracks_bench_armed()))stop_locked();
            xSemaphoreGive(actuator_lock);
        }
        vTaskDelay(pdMS_TO_TICKS(10));
    }
}
esp_err_t marvin_actuators_init(void){
    /* EEP must have a physical pull-down during reset/boot. Firmware cannot
     * control GPIO3 until this point. Clear the output latch before GPIO mux. */
    esp_err_t err=gpio_set_level(DRIVER_EEP,0);
    if(err!=ESP_OK)return err;
    gpio_config_t sleep_pin={.pin_bit_mask=1ULL<<DRIVER_EEP,.mode=GPIO_MODE_OUTPUT,
        .pull_up_en=GPIO_PULLUP_DISABLE,.pull_down_en=GPIO_PULLDOWN_ENABLE,.intr_type=GPIO_INTR_DISABLE};
    if((err=gpio_config(&sleep_pin))!=ESP_OK)return err;
    ledc_timer_config_t timer={.speed_mode=LEDC_LOW_SPEED_MODE,.duty_resolution=LEDC_TIMER_10_BIT,
        .timer_num=LEDC_TIMER_0,.freq_hz=20000,.clk_cfg=LEDC_AUTO_CLK};
    if((err=ledc_timer_config(&timer))!=ESP_OK)return err;
    for(int i=0;i<4;i++){
        ledc_channel_config_t channel={.gpio_num=motor_pins[i],.speed_mode=LEDC_LOW_SPEED_MODE,
            .channel=(ledc_channel_t)i,.intr_type=LEDC_INTR_DISABLE,.timer_sel=LEDC_TIMER_0,.duty=0,.hpoint=0};
        if((err=ledc_channel_config(&channel))!=ESP_OK)return err;
    }
    actuator_lock=xSemaphoreCreateMutex();
    if(!actuator_lock)return ESP_ERR_NO_MEM;
    if(xTaskCreate(expiry_task,"drive_expiry",2048,NULL,5,NULL)!=pdPASS){
        stop_locked();
        vSemaphoreDelete(actuator_lock);
        actuator_lock=NULL;
        return ESP_ERR_NO_MEM;
    }
    return ESP_OK;
}
esp_err_t marvin_tracks_set(int left_percent,int right_percent,uint32_t duration_ms){
    if(!actuator_lock)return ESP_ERR_INVALID_STATE;
    if(left_percent < -100 || left_percent > 100 || right_percent < -100 || right_percent > 100 || duration_ms>500)return ESP_ERR_INVALID_ARG;
    xSemaphoreTake(actuator_lock,portMAX_DELAY);
    stop_locked();
    if(duration_ms==0 || (left_percent==0 && right_percent==0)){
        xSemaphoreGive(actuator_lock);return ESP_OK;
    }
    if(!marvin_tracks_bench_armed()){
        xSemaphoreGive(actuator_lock);return ESP_ERR_NOT_SUPPORTED;
    }
    /* OUT1/3 are motor minus, OUT2/4 are motor plus: forward is IN2/4 PWM. */
    int speeds[]={left_percent,right_percent};
    esp_err_t err=ESP_OK;
    for(int side=0;side<2;side++){
        int speed=speeds[side];
        if(!speed)continue;
        int index=side*2+(speed>0?1:0);
        int magnitude=speed>0?speed:-speed;
        err=duty((ledc_channel_t)index,(uint32_t)(magnitude*MOTOR_DUTY_MAX/100));
        if(err!=ESP_OK)break;
    }
    if(err==ESP_OK){
        /* Wake the DRV8833 only after all four inputs have their intended
         * PWM values. The expiry task puts EEP low before clearing them. */
        drive_deadline_us=esp_timer_get_time()+(int64_t)duration_ms*1000;
        err=gpio_set_level(DRIVER_EEP,1);
    }
    if(err!=ESP_OK)stop_locked();
    xSemaphoreGive(actuator_lock);
    return err;
}
esp_err_t marvin_head_set(uint8_t rotation_degrees,uint8_t tilt_degrees){
    if(!actuator_lock)return ESP_ERR_INVALID_STATE;
    if(rotation_degrees>180 || tilt_degrees>180)return ESP_ERR_INVALID_ARG;
    xSemaphoreTake(actuator_lock,portMAX_DELAY);
    esp_err_t err=ESP_OK;
    if(!servo_started){
        ledc_timer_config_t timer={.speed_mode=LEDC_LOW_SPEED_MODE,.duty_resolution=LEDC_TIMER_14_BIT,
            .timer_num=LEDC_TIMER_1,.freq_hz=1000000/SERVO_PERIOD_US,.clk_cfg=LEDC_AUTO_CLK};
        err=ledc_timer_config(&timer);
        const int pins[]={HEAD_ROTATE,HEAD_TILT};
        for(int i=0;i<2 && err==ESP_OK;i++){
            ledc_channel_config_t channel={.gpio_num=pins[i],.speed_mode=LEDC_LOW_SPEED_MODE,
                .channel=(ledc_channel_t)(4+i),.intr_type=LEDC_INTR_DISABLE,.timer_sel=LEDC_TIMER_1,.duty=0,.hpoint=0};
            err=ledc_channel_config(&channel);
        }
        if(err==ESP_OK)servo_started=true;
    }
    if(err==ESP_OK){
        const uint8_t angles[]={rotation_degrees,tilt_degrees};
        for(int i=0;i<2 && err==ESP_OK;i++){
            uint32_t pulse_us=1000+(uint32_t)angles[i]*1000/180;
            err=duty((ledc_channel_t)(4+i),pulse_us*16384/SERVO_PERIOD_US);
        }
    }
    xSemaphoreGive(actuator_lock);
    return err;
}
esp_err_t marvin_head_pose(int yaw_degrees,int pitch_degrees){
    if(yaw_degrees < -30 || yaw_degrees > 30 || pitch_degrees < -20 || pitch_degrees > 20)return ESP_ERR_INVALID_ARG;
    int yaw=CONFIG_MARVIN_HEAD_YAW_CENTER + yaw_degrees;
    int pitch=CONFIG_MARVIN_HEAD_PITCH_CENTER + pitch_degrees;
#ifdef CONFIG_MARVIN_HEAD_YAW_REVERSE
    yaw=CONFIG_MARVIN_HEAD_YAW_CENTER-yaw_degrees;
#endif
#ifdef CONFIG_MARVIN_HEAD_PITCH_REVERSE
    pitch=CONFIG_MARVIN_HEAD_PITCH_CENTER-pitch_degrees;
#endif
    return marvin_head_set((uint8_t)yaw,(uint8_t)pitch);
}
