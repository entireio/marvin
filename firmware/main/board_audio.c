#include "board_audio.h"
#include "sdkconfig.h"
#if defined(CONFIG_MARVIN_WAVESHARE_AUDIO_DIAGNOSTIC) || defined(CONFIG_MARVIN_BODY_AUDIO)
#include <stdio.h>
#include <string.h>
#include <math.h>
#include <inttypes.h>
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "driver/i2c_master.h"
#include "driver/i2s_std.h"
#include "esp_codec_dev.h"
#include "esp_codec_dev_defaults.h"
#include "esp_heap_caps.h"
#include "esp_timer.h"
#include "freertos/queue.h"
#include <stdatomic.h>

/* Waveshare schematic revision 1.1. GPIO19/20 are the native USB pair;
 * actuator PWM claims them only when the head is explicitly commanded. */
static i2c_master_bus_handle_t bus;
static i2c_master_dev_handle_t expander;
static i2s_chan_handle_t tx,rx;
static esp_codec_dev_handle_t input,output;
static atomic_bool output_muted=true;
static atomic_uint speaker_volume=CONFIG_MARVIN_SPEAKER_VOLUME;
#ifdef CONFIG_MARVIN_LOCAL_AFE
/* These callbacks run before the driver's DMA auto-clear. The reference is the
 * PCM actually sent by I2S, never the network download queue. TX/RX share clocks;
 * the latest completed TX window precedes RX by at most one 10-ms DMA block.
 * Acoustic alignment and AEC attenuation still require assembly measurements. */
typedef struct { int16_t pcm[160*3]; } afe_dma_t;
static StaticQueue_t afe_queue_control;
static QueueHandle_t afe_queue;
static int16_t sent_reference[160];
static portMUX_TYPE reference_lock=portMUX_INITIALIZER_UNLOCKED;
static atomic_bool dma_overflow;
static bool sent(i2s_chan_handle_t handle,i2s_event_data_t *event,void *context){
    (void)handle;(void)context;
    const int32_t *pcm=event->dma_buf;
    portENTER_CRITICAL_ISR(&reference_lock);
    for(size_t i=0;i<160;i++)sent_reference[i]=!pcm||event->size!=1280||atomic_load(&output_muted)?0:(int16_t)(pcm[2*i]/65536);
    portEXIT_CRITICAL_ISR(&reference_lock);
    return false;
}
static bool received(i2s_chan_handle_t handle,i2s_event_data_t *event,void *context){
    (void)handle;(void)context;
    static afe_dma_t frame;
    if(!afe_queue||!event->dma_buf||event->size!=1280)return false;
    const int16_t *pcm=event->dma_buf;
    portENTER_CRITICAL_ISR(&reference_lock);
    for(size_t i=0;i<160;i++){frame.pcm[3*i]=pcm[4*i+1];frame.pcm[3*i+1]=pcm[4*i+3];frame.pcm[3*i+2]=sent_reference[i];}
    portEXIT_CRITICAL_ISR(&reference_lock);
    BaseType_t wake=pdFALSE;
    if(xQueueSendFromISR(afe_queue,&frame,&wake)!=pdTRUE)atomic_store(&dma_overflow,true);
    return wake==pdTRUE;
}
esp_err_t marvin_audio_read_afe(int16_t *interleaved,size_t *frames){
    if(!afe_queue||!interleaved||!frames)return ESP_ERR_INVALID_STATE;
    *frames=0;
    if(atomic_exchange(&dma_overflow,false)){xQueueReset(afe_queue);return ESP_ERR_INVALID_STATE;}
    if(xQueueReceive(afe_queue,interleaved,pdMS_TO_TICKS(100))!=pdTRUE)return ESP_ERR_TIMEOUT;
    *frames=160;return ESP_OK;
}
#else
esp_err_t marvin_audio_read_afe(int16_t *interleaved,size_t *frames){(void)interleaved;(void)frames;return ESP_ERR_NOT_SUPPORTED;}
#endif
static esp_err_t pa(bool enabled) {
#ifdef CONFIG_MARVIN_SILENT_TEST
    enabled=false;
#endif
    uint8_t reg=3,value;
    esp_err_t err=i2c_master_transmit_receive(expander,&reg,1,&value,1,100);
    if(err!=ESP_OK)return err;
    value=(value&~1U)|(enabled?1:0);uint8_t data[]={3,value};
    return i2c_master_transmit(expander,data,sizeof(data),100);
}
static esp_err_t init(void) {
    i2c_master_bus_config_t bc={.i2c_port=0,.sda_io_num=11,.scl_io_num=10,.clk_source=I2C_CLK_SRC_DEFAULT,.glitch_ignore_cnt=7};
    ESP_ERROR_CHECK(i2c_new_master_bus(&bc,&bus));
    const uint8_t expected[]={0x18,0x40,0x20};
    for(size_t i=0;i<sizeof(expected);i++) {
        esp_err_t e=ESP_FAIL;
        /* USB resets need not reset the codecs. Recover a held bus before
         * declaring required hardware absent; retries remain bounded. */
        for(unsigned attempt=0;attempt<5;attempt++){
            e=i2c_master_probe(bus,expected[i],100);if(e==ESP_OK)break;
            i2c_master_bus_reset(bus);vTaskDelay(pdMS_TO_TICKS(100));
        }
        printf("{\"test\":\"i2c\",\"address\":%u,\"ok\":%s}\n",expected[i],e==ESP_OK?"true":"false");
        if(e!=ESP_OK)return e;
    }
    i2c_device_config_t dc={.dev_addr_length=I2C_ADDR_BIT_LEN_7,.device_address=0x20,.scl_speed_hz=100000};
    ESP_ERROR_CHECK(i2c_master_bus_add_device(bus,&dc,&expander));
    /* Set the amplifier latch low before making only EXIO8 an output. */
    ESP_ERROR_CHECK(pa(false));
    uint8_t reg=7,value;ESP_ERROR_CHECK(i2c_master_transmit_receive(expander,&reg,1,&value,1,100));
    uint8_t conf[]={7,value&~1U};ESP_ERROR_CHECK(i2c_master_transmit(expander,conf,2,100));
    i2s_chan_config_t cc=I2S_CHANNEL_DEFAULT_CONFIG(I2S_NUM_1,I2S_ROLE_MASTER);
    cc.auto_clear=true;cc.dma_desc_num=4;cc.dma_frame_num=160;
    ESP_ERROR_CHECK(i2s_new_channel(&cc,&tx,&rx));
    i2s_std_config_t sc={.clk_cfg=I2S_STD_CLK_DEFAULT_CONFIG(16000),.slot_cfg=I2S_STD_PHILIPS_SLOT_DEFAULT_CONFIG(I2S_DATA_BIT_WIDTH_32BIT,I2S_SLOT_MODE_STEREO),.gpio_cfg={.mclk=12,.bclk=13,.ws=14,.dout=16,.din=15}};
    ESP_ERROR_CHECK(i2s_channel_init_std_mode(tx,&sc));ESP_ERROR_CHECK(i2s_channel_init_std_mode(rx,&sc));
#ifdef CONFIG_MARVIN_LOCAL_AFE
#ifdef CONFIG_I2S_ISR_IRAM_SAFE
#error "AFE PSRAM DMA queue requires non-IRAM I2S callbacks; pause audio before flash writes."
#endif
    uint8_t *afe_queue_storage=heap_caps_calloc(16,sizeof(afe_dma_t),MALLOC_CAP_SPIRAM|MALLOC_CAP_8BIT);
    if(!afe_queue_storage)return ESP_ERR_NO_MEM;
    afe_queue=xQueueCreateStatic(16,sizeof(afe_dma_t),afe_queue_storage,&afe_queue_control);
    i2s_event_callbacks_t tx_events={.on_sent=sent},rx_events={.on_recv=received};
    ESP_ERROR_CHECK(i2s_channel_register_event_callback(tx,&tx_events,NULL));
    ESP_ERROR_CHECK(i2s_channel_register_event_callback(rx,&rx_events,NULL));
#endif
    audio_codec_i2s_cfg_t din={.port=I2S_NUM_1,.rx_handle=rx},dout={.port=I2S_NUM_1,.tx_handle=tx};
    audio_codec_i2c_cfg_t cin={.addr=ES7210_CODEC_DEFAULT_ADDR,.bus_handle=bus},cout={.addr=ES8311_CODEC_DEFAULT_ADDR,.bus_handle=bus};
    es7210_codec_cfg_t adc={.ctrl_if=audio_codec_new_i2c_ctrl(&cin),.mic_selected=ES7210_SEL_MIC1|ES7210_SEL_MIC2|ES7210_SEL_MIC3|ES7210_SEL_MIC4};
    es8311_codec_cfg_t dac={.ctrl_if=audio_codec_new_i2c_ctrl(&cout),.gpio_if=audio_codec_new_gpio(),.codec_mode=ESP_CODEC_DEV_WORK_MODE_DAC,.pa_pin=-1,.use_mclk=false};
    esp_codec_dev_cfg_t inc={.dev_type=ESP_CODEC_DEV_TYPE_IN,.codec_if=es7210_codec_new(&adc),.data_if=audio_codec_new_i2s_data(&din)};
    esp_codec_dev_cfg_t outc={.dev_type=ESP_CODEC_DEV_TYPE_OUT,.codec_if=es8311_codec_new(&dac),.data_if=audio_codec_new_i2s_data(&dout)};
    input=esp_codec_dev_new(&inc);output=esp_codec_dev_new(&outc);if(!input||!output)return ESP_ERR_NO_MEM;
    esp_codec_dev_sample_info_t fs={.sample_rate=16000,.channel=2,.bits_per_sample=32};
    if(esp_codec_dev_open(input,&fs)||esp_codec_dev_open(output,&fs))return ESP_FAIL;
    if(esp_codec_dev_set_in_gain(input,CONFIG_MARVIN_MICROPHONE_GAIN_DB)||esp_codec_dev_set_out_vol(output,CONFIG_MARVIN_SPEAKER_VOLUME)||esp_codec_dev_set_out_mute(output,true))return ESP_FAIL;
    return ESP_OK;
}
esp_err_t marvin_audio_open(void){return init();}
esp_err_t marvin_audio_quiesce(void){
 if(!tx&&!rx)return ESP_OK;
 if(!tx||!rx)return ESP_ERR_INVALID_STATE;
 esp_err_t muted=marvin_audio_mute(true);
 esp_err_t stopped_rx=i2s_channel_disable(rx),stopped_tx=i2s_channel_disable(tx);
 bool rx_ok=stopped_rx==ESP_OK||stopped_rx==ESP_ERR_INVALID_STATE;
 bool tx_ok=stopped_tx==ESP_OK||stopped_tx==ESP_ERR_INVALID_STATE;
#ifdef CONFIG_MARVIN_LOCAL_AFE
 if(rx_ok&&tx_ok){xQueueReset(afe_queue);memset(sent_reference,0,sizeof(sent_reference));atomic_store(&dma_overflow,false);}
#endif
 return muted==ESP_OK&&rx_ok&&tx_ok?ESP_OK:ESP_FAIL;
}

esp_err_t marvin_audio_read(int16_t *mic1,int16_t *mic2,size_t capacity,size_t *frames){
    int16_t packed[640];size_t count=0;*frames=0;
    if(!input||!mic1||capacity<160)return ESP_ERR_INVALID_ARG;
    esp_err_t err=i2s_channel_read(rx,packed,sizeof(packed),&count,100);
    if(err!=ESP_OK||count%8)return ESP_FAIL;
    *frames=count/8;
    for(size_t i=0;i<*frames;i++){mic1[i]=packed[4*i+1];if(mic2)mic2[i]=packed[4*i+3];}
    memset(packed,0,sizeof(packed));return ESP_OK;
}
esp_err_t marvin_audio_write(const int16_t *mono,size_t frames){
    int32_t stereo[320];size_t written=0;
    if(!output||!mono||frames>160)return ESP_ERR_INVALID_ARG;
    for(size_t i=0;i<frames;i++)stereo[2*i]=stereo[2*i+1]=(int32_t)mono[i]*65536;
    esp_err_t err=i2s_channel_write(tx,stereo,frames*8,&written,100);
    memset(stereo,0,sizeof(stereo));return err==ESP_OK&&written==frames*8?ESP_OK:ESP_FAIL;
}
esp_err_t marvin_audio_set_volume(unsigned volume){
    if(!output||volume>100)return ESP_ERR_INVALID_ARG;
    if(esp_codec_dev_set_out_vol(output,(int)volume))return ESP_FAIL;
    atomic_store(&speaker_volume,volume);return ESP_OK;
}
unsigned marvin_audio_volume(void){return atomic_load(&speaker_volume);}
esp_err_t marvin_audio_mute(bool mute){
    if(!output)return ESP_ERR_INVALID_STATE;
    atomic_store(&output_muted,mute);
#ifdef CONFIG_MARVIN_SILENT_TEST
    atomic_store(&output_muted,true);
#endif
    if(mute){esp_err_t err=pa(false);return esp_codec_dev_set_out_mute(output,true)==0?err:ESP_FAIL;}
    if(esp_codec_dev_set_out_mute(output,false))return ESP_FAIL;
    return pa(true);
}
static void tone(void) {
    /* 0.5 seconds, 440 Hz, amplitude -18 dBFS before -20 dB codec attenuation. */
    static int32_t samples[320];size_t bytes;bool ok=true;
    if(esp_codec_dev_set_out_mute(output,false)||pa(true)!=ESP_OK)ok=false;
    vTaskDelay(pdMS_TO_TICKS(10));
    for(int chunk=0;ok&&chunk<50;chunk++) {
        for(int i=0;i<160;i++)samples[2*i]=samples[2*i+1]=(int32_t)(sinf(2*3.14159265f*440*(chunk*160+i)/16000)*270352173.0f);
        ok=i2s_channel_write(tx,samples,sizeof(samples),&bytes,100)==ESP_OK&&bytes==sizeof(samples);
    }
    memset(samples,0,sizeof(samples));i2s_channel_write(tx,samples,sizeof(samples),&bytes,100);
    vTaskDelay(pdMS_TO_TICKS(50));
    esp_err_t disabled=pa(false);int muted=esp_codec_dev_set_out_mute(output,true);
    printf("{\"test\":\"tone\",\"ok\":%s,\"durationMs\":500}\n",ok&&disabled==ESP_OK&&!muted?"true":"false");
}
static void levels(void) {
    static int16_t samples[640];double sum[4]={0};int peak[4]={0};uint32_t frames=0;bool ok=true;
    int64_t deadline=esp_timer_get_time()+3500000;
    /* Discard stale DMA samples before a bounded two-second measurement. */
    for(int i=0;i<4;i++){size_t n;i2s_channel_read(rx,samples,sizeof(samples),&n,100);}
    while(frames<32000&&esp_timer_get_time()<deadline) {
        size_t n=0;if(i2s_channel_read(rx,samples,sizeof(samples),&n,100)!=ESP_OK||n%8){ok=false;break;}
        for(size_t i=0;i<n/2;i++){int x=samples[i],a=x<0?-x:x;int c=i%4;sum[c]+=(double)x*x;if(a>peak[c])peak[c]=a;}
        frames+=n/8;
    }
    for(int c=0;c<4;c++)printf("{\"test\":\"levels\",\"slot\":%d,\"frames\":%"PRIu32",\"rms\":%.2f,\"peak\":%d,\"ioOk\":%s}\n",c,frames,frames?sqrt(sum[c]/frames):0,peak[c],ok&&frames>=32000?"true":"false");
    memset(samples,0,sizeof(samples));
}

/* Numeric loopback analysis only: samples are discarded in RAM, never retained. */
static struct {float amplitude[8][2];bool ok;TaskHandle_t owner;} acoustic_result;
static void acoustic_reader(void *unused) {
    static int16_t samples[640];acoustic_result.ok=true;int64_t deadline=esp_timer_get_time()+3000000;
    for(int window=0;window<8;window++) {
        float q1[2]={0},q2[2]={0};unsigned frames=0;
        while(frames<3200&&esp_timer_get_time()<deadline){size_t count=0;if(i2s_channel_read(rx,samples,sizeof(samples),&count,100)!=ESP_OK||count%8){acoustic_result.ok=false;break;}
            for(size_t i=0;i<count/2;i+=4){for(int mic=0;mic<2;mic++){float q=samples[i+1+2*mic]+1.97021865f*q1[mic]-q2[mic];q2[mic]=q1[mic];q1[mic]=q;}}frames+=count/8;
        }
        if(frames<3200)acoustic_result.ok=false;
        for(int mic=0;mic<2;mic++){float power=q1[mic]*q1[mic]+q2[mic]*q2[mic]-1.97021865f*q1[mic]*q2[mic];acoustic_result.amplitude[window][mic]=frames?2*sqrtf(fmaxf(0,power))/frames:0;}
    }
    memset(samples,0,sizeof(samples));xTaskNotifyGive(acoustic_result.owner);vTaskDelete(NULL);
}
static void acoustic(void) {
    memset(&acoustic_result,0,sizeof(acoustic_result));acoustic_result.owner=xTaskGetCurrentTaskHandle();
    (void)ulTaskNotifyTake(pdTRUE,0);
    if(xTaskCreate(acoustic_reader,"acoustic_probe",4096,NULL,5,NULL)!=pdPASS){printf("Acoustic sampler could not start\n");return;}
    vTaskDelay(pdMS_TO_TICKS(600));tone();
    if(!ulTaskNotifyTake(pdTRUE,pdMS_TO_TICKS(3000))){printf("Acoustic sampler timed out\n");return;}
    for(int mic=0;mic<2;mic++){
        float baseline=0,during=0;for(int w=0;w<3;w++)baseline=fmaxf(baseline,acoustic_result.amplitude[w][mic]);for(int w=3;w<6;w++)during=fmaxf(during,acoustic_result.amplitude[w][mic]);
        printf("{\"test\":\"acoustic\",\"mic\":%d,\"frequencyHz\":440,\"baselineAmplitude\":%.2f,\"duringAmplitude\":%.2f,\"gainDb\":%.2f,\"ioOk\":%s}\n",mic,baseline,during,20*log10f(fmaxf(during,.001f)/fmaxf(baseline,.001f)),acoustic_result.ok?"true":"false");
    }
}
void marvin_audio_diagnostic(void) {
    setvbuf(stdout,NULL,_IONBF,0);
    printf("Marvin Waveshare audio diagnostic. No networking or audio recording.\n");
    esp_err_t result=init();if(result!=ESP_OK){printf("Audio init failed: %s\n",esp_err_to_name(result));return;}
    printf("{\"test\":\"ready\",\"psramBytes\":%u,\"speakerMuted\":true}\n",(unsigned)heap_caps_get_total_size(MALLOC_CAP_SPIRAM));
    printf("Commands: t = quiet half-second tone; m = two-second local level measurement.\n");
    while(true){int c=getchar();if(c=='t')tone();else if(c=='a')acoustic();else if(c=='m')levels();else if(c=='s')printf("{\"test\":\"resources\",\"stackFreeMinBytes\":%u,\"internalFreeBytes\":%u}\n",(unsigned)uxTaskGetStackHighWaterMark(NULL),(unsigned)heap_caps_get_free_size(MALLOC_CAP_INTERNAL));else {if(c==EOF)clearerr(stdin);vTaskDelay(pdMS_TO_TICKS(20));}}
}
#else
void marvin_audio_diagnostic(void) {}
esp_err_t marvin_audio_set_volume(unsigned volume){(void)volume;return ESP_ERR_NOT_SUPPORTED;}
unsigned marvin_audio_volume(void){return 0;}
esp_err_t marvin_audio_open(void){return ESP_ERR_NOT_SUPPORTED;}
esp_err_t marvin_audio_quiesce(void){return ESP_OK;}
esp_err_t marvin_audio_read(int16_t *a,int16_t *b,size_t c,size_t *d){(void)a;(void)b;(void)c;(void)d;return ESP_ERR_NOT_SUPPORTED;}
esp_err_t marvin_audio_write(const int16_t *a,size_t b){(void)a;(void)b;return ESP_ERR_NOT_SUPPORTED;}
esp_err_t marvin_audio_mute(bool mute){(void)mute;return ESP_ERR_NOT_SUPPORTED;}
#endif
