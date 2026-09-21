#include "actuators.h"
#include "driver/gpio.h"
#include "driver/ledc.h"
#include "esp_timer.h"
#include "nvs.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"
#include "sdkconfig.h"

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
/* The assembled pet has both motor polarities and track sides opposite the
 * original logical convention. Keep the correction here so motion callers
 * can continue using positive left/right speeds for forward motion. */
enum { LEFT_IN1=5, LEFT_IN2=4, RIGHT_IN3=6, RIGHT_IN4=7,
       DRIVER_EEP=3, HEAD_ROTATE=9, HEAD_TILT=8 };
_Static_assert(HEAD_ROTATE!=19 && HEAD_ROTATE!=20 && HEAD_TILT!=19 && HEAD_TILT!=20,
               "Servos must not use the ESP32-S3 USB data pins");
enum { MOTOR_DUTY_MAX=1023, SERVO_PERIOD_US=20000,
       HEAD_ROTATE_MIN_US=600, HEAD_ROTATE_MAX_US=2400,
       HEAD_TILT_MIN_US=700, HEAD_TILT_MAX_US=2300 };
static SemaphoreHandle_t actuator_lock;
static int64_t drive_deadline_us;
static bool servo_started;
static nvs_handle_t calibration_nvs;
typedef struct {uint32_t magic;uint8_t version;marvin_head_calibration_t value;} stored_head_calibration_t;
enum { HEAD_CALIBRATION_MAGIC=0x4843414c, HEAD_CALIBRATION_VERSION=1 };
static marvin_head_calibration_t head_calibration={
    .yaw_center=CONFIG_MARVIN_HEAD_YAW_CENTER,
    .pitch_center=CONFIG_MARVIN_HEAD_PITCH_CENTER,
#ifdef CONFIG_MARVIN_HEAD_YAW_REVERSE
    .yaw_reversed=true,
#endif
#ifdef CONFIG_MARVIN_HEAD_PITCH_REVERSE
    .pitch_reversed=true,
#endif
};
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
static void expiry_task(void *unused){
    (void)unused;
    for(;;){
        if(xSemaphoreTake(actuator_lock,pdMS_TO_TICKS(10))==pdTRUE){
            if(drive_deadline_us && esp_timer_get_time()>=drive_deadline_us)stop_locked();
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
    esp_err_t calibration_error=nvs_open("head_cal",NVS_READWRITE,&calibration_nvs);
    if(calibration_error==ESP_OK){
        stored_head_calibration_t saved;size_t saved_size=sizeof(saved);
        calibration_error=nvs_get_blob(calibration_nvs,"settings",&saved,&saved_size);
        if(calibration_error==ESP_OK&&saved_size==sizeof(saved)&&saved.magic==HEAD_CALIBRATION_MAGIC&&saved.version==HEAD_CALIBRATION_VERSION&&saved.value.yaw_center>=60&&saved.value.yaw_center<=120&&saved.value.pitch_center>=60&&saved.value.pitch_center<=120)head_calibration=saved.value;
        else if(calibration_error!=ESP_ERR_NVS_NOT_FOUND){(void)nvs_erase_key(calibration_nvs,"settings");(void)nvs_commit(calibration_nvs);}
    }else calibration_nvs=0;
    if(xTaskCreate(expiry_task,"drive_expiry",2048,NULL,5,NULL)!=pdPASS){
        stop_locked();
        if(calibration_nvs)nvs_close(calibration_nvs);
        calibration_nvs=0;
        vSemaphoreDelete(actuator_lock);
        actuator_lock=NULL;
        return ESP_ERR_NO_MEM;
    }
    return ESP_OK;
}
esp_err_t marvin_tracks_set(int left_percent,int right_percent,uint32_t duration_ms){
    if(!actuator_lock)return ESP_ERR_INVALID_STATE;
    if(left_percent < -100 || left_percent > 100 || right_percent < -100 || right_percent > 100 || duration_ms>30000)return ESP_ERR_INVALID_ARG;
    xSemaphoreTake(actuator_lock,portMAX_DELAY);
    stop_locked();
    if(duration_ms==0 || (left_percent==0 && right_percent==0)){
        xSemaphoreGive(actuator_lock);return ESP_OK;
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
        /* The previous 1000..2000 us mapping used only about half of these
         * SG90-class servos' travel. Keep the 1500 us neutral point, while
         * calibrating pan to 1.8x and tilt to 1.6x the former pulse travel. */
        const uint16_t minimum_us[]={HEAD_ROTATE_MIN_US,HEAD_TILT_MIN_US};
        const uint16_t maximum_us[]={HEAD_ROTATE_MAX_US,HEAD_TILT_MAX_US};
        for(int i=0;i<2 && err==ESP_OK;i++){
            uint32_t pulse_us=minimum_us[i]+(uint32_t)angles[i]*(maximum_us[i]-minimum_us[i])/180;
            err=duty((ledc_channel_t)(4+i),pulse_us*16384/SERVO_PERIOD_US);
        }
    }
    xSemaphoreGive(actuator_lock);
    return err;
}
esp_err_t marvin_head_pose(int yaw_degrees,int pitch_degrees){
    /* A 90-degree center leaves ten degrees of servo margin at the remote
     * controller's full +/-80-degree yaw range. Logical positive yaw means
     * look right; the assembled linkage requires the configured reversal. */
    if(yaw_degrees < -80 || yaw_degrees > 80 || pitch_degrees < -45 || pitch_degrees > 45)return ESP_ERR_INVALID_ARG;
    xSemaphoreTake(actuator_lock,portMAX_DELAY);
    marvin_head_calibration_t calibration=head_calibration;
    xSemaphoreGive(actuator_lock);
    int yaw=calibration.yaw_center+(calibration.yaw_reversed?-yaw_degrees:yaw_degrees);
    int pitch=calibration.pitch_center+(calibration.pitch_reversed?-pitch_degrees:pitch_degrees);
    if(yaw<0)yaw=0;else if(yaw>180)yaw=180;
    if(pitch<0)pitch=0;else if(pitch>180)pitch=180;
    return marvin_head_set((uint8_t)yaw,(uint8_t)pitch);
}
void marvin_head_calibration_get(marvin_head_calibration_t *calibration){
    if(!calibration||!actuator_lock)return;
    xSemaphoreTake(actuator_lock,portMAX_DELAY);*calibration=head_calibration;xSemaphoreGive(actuator_lock);
}
esp_err_t marvin_head_calibration_set(const marvin_head_calibration_t *calibration){
    if(!actuator_lock||!calibration||calibration->yaw_center<60||calibration->yaw_center>120||calibration->pitch_center<60||calibration->pitch_center>120)return ESP_ERR_INVALID_ARG;
    if(!calibration_nvs)return ESP_ERR_INVALID_STATE;
    xSemaphoreTake(actuator_lock,portMAX_DELAY);
    stored_head_calibration_t saved={.magic=HEAD_CALIBRATION_MAGIC,.version=HEAD_CALIBRATION_VERSION,.value=*calibration};
    esp_err_t err=nvs_set_blob(calibration_nvs,"settings",&saved,sizeof(saved));
    if(err==ESP_OK)err=nvs_commit(calibration_nvs);
    if(err==ESP_OK)head_calibration=*calibration;
    xSemaphoreGive(actuator_lock);
    return err;
}
