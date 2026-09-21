#include "audio_io.h"

#ifdef MARVIN_VOICE

bool AudioIO::begin() {
#ifdef PIN_AMP_SD
    // Amplifier off before the clock starts. The MAX98357A thumps if it is
    // live while the bus comes up.
    pinMode(PIN_AMP_SD, OUTPUT);
    digitalWrite(PIN_AMP_SD, LOW);
#endif
    _ampOn = false;
    _dropped = 0;

    i2s_chan_config_t chanCfg = I2S_CHANNEL_DEFAULT_CONFIG(I2S_NUM_0, I2S_ROLE_MASTER);
    chanCfg.dma_desc_num  = AUDIO_DMA_DESC_NUM;
    chanCfg.dma_frame_num = AUDIO_DMA_FRAME_NUM;
    // With auto_clear the driver sends silence when we fall behind, instead of
    // replaying whatever was last in the buffer. A stutter is forgivable; a
    // 90 ms fragment of the previous sentence on loop is not.
    chanCfg.auto_clear = true;

    // Asking for both handles at once is what puts the port in full duplex:
    // the two channels then share BCLK and WS instead of fighting for pins.
    if (i2s_new_channel(&chanCfg, &_tx, &_rx) != ESP_OK) {
        Serial.println("[audio] i2s_new_channel failed");
        return false;
    }

    i2s_std_config_t stdCfg = {
        .clk_cfg  = I2S_STD_CLK_DEFAULT_CONFIG(AUDIO_SAMPLE_RATE),
        .slot_cfg = I2S_STD_PHILIPS_SLOT_DEFAULT_CONFIG(
                        I2S_DATA_BIT_WIDTH_32BIT, I2S_SLOT_MODE_STEREO),
        .gpio_cfg = {
            .mclk = I2S_GPIO_UNUSED,   // neither part needs a master clock
            .bclk = (gpio_num_t)PIN_I2S_BCLK,
            .ws   = (gpio_num_t)PIN_I2S_WS,
            .dout = (gpio_num_t)PIN_I2S_DOUT,
            .din  = (gpio_num_t)PIN_I2S_DIN,
            .invert_flags = { .mclk_inv = false, .bclk_inv = false, .ws_inv = false },
        },
    };

    // Identical configuration on both channels. Full duplex requires the same
    // frame timing on each side, and the driver enforces it by demoting the
    // second channel to slave on the shared clock.
    if (i2s_channel_init_std_mode(_tx, &stdCfg) != ESP_OK ||
        i2s_channel_init_std_mode(_rx, &stdCfg) != ESP_OK) {
        Serial.println("[audio] i2s_channel_init_std_mode failed");
        end();
        return false;
    }
    if (i2s_channel_enable(_tx) != ESP_OK || i2s_channel_enable(_rx) != ESP_OK) {
        Serial.println("[audio] i2s_channel_enable failed");
        end();
        return false;
    }

    Serial.printf("[audio] I2S full duplex up: %d Hz, BCLK %.3f MHz on GPIO %d/%d, "
                  "mic in %d, amp out %d\r\n",
                  AUDIO_SAMPLE_RATE,
                  (AUDIO_SAMPLE_RATE * AUDIO_SLOT_BITS * 2) / 1000000.0,
                  PIN_I2S_BCLK, PIN_I2S_WS, PIN_I2S_DIN, PIN_I2S_DOUT);
    return true;
}

void AudioIO::end() {
    stopLoopback();
    setAmpEnabled(false);
    if (_tx) { i2s_channel_disable(_tx); i2s_del_channel(_tx); _tx = nullptr; }
    if (_rx) { i2s_channel_disable(_rx); i2s_del_channel(_rx); _rx = nullptr; }
}

bool AudioIO::readFrame(int16_t* dst, uint32_t timeoutMs) {
    if (!_rx || !dst) return false;

    size_t got = 0;
    if (i2s_channel_read(_rx, _rxScratch, sizeof(_rxScratch), &got,
                         pdMS_TO_TICKS(timeoutMs)) != ESP_OK) {
        return false;
    }
    if (got < sizeof(_rxScratch)) return false;

    for (int i = 0; i < AUDIO_FRAME_SAMPLES; i++) {
        // Left slot only: the INMP441's L/R pin is tied low, so the right slot
        // is whatever the bus was left holding.
        //
        // The part is 24-bit, left-justified in its 32-bit slot, so the top
        // sixteen bits are already the sample we want.
        int32_t s = (_rxScratch[i * 2] >> 16) * AUDIO_MIC_GAIN;

        // Saturate rather than wrap. A wrapped sample turns a loud voice into a
        // burst of noise, which is both unpleasant and poison to a wake word
        // model.
        if (s >  32767) s =  32767;
        if (s < -32768) s = -32768;
        dst[i] = (int16_t)s;
    }
    return true;
}

bool AudioIO::writeFrame(const int16_t* src, uint32_t timeoutMs) {
    if (!_tx || !src) return false;

    for (int i = 0; i < AUDIO_FRAME_SAMPLES; i++) {
        // Same sample into both slots. The MAX98357A picks left, right, or the
        // average depending on the voltage on its SD pin, which depends on the
        // breakout's resistors — duplicating the sample makes playback correct
        // whichever way that lands.
        int32_t s = (int32_t)src[i] << 16;
        _txScratch[i * 2]     = s;
        _txScratch[i * 2 + 1] = s;
    }

    size_t written = 0;
    esp_err_t err = i2s_channel_write(_tx, _txScratch, sizeof(_txScratch), &written,
                                      pdMS_TO_TICKS(timeoutMs));
    if (err != ESP_OK || written < sizeof(_txScratch)) {
        _dropped++;
        return false;
    }
    return true;
}

void AudioIO::setAmpEnabled(bool on) {
    if (on == _ampOn) return;
    _ampOn = on;
#ifdef PIN_AMP_SD
    digitalWrite(PIN_AMP_SD, on ? HIGH : LOW);
#else
    // No pin wired to the amplifier's SD input, so it is always on and this is
    // only bookkeeping. The cost is idle hiss; see the board header for why
    // that is the better trade on the C3.
#endif
}

void AudioIO::flushPlayback() {
    // Muting is what actually stops the sound. Whatever is already in the DMA
    // descriptors — up to about 90 ms — still clocks out to a dead amplifier,
    // and pushing silence behind it keeps that tail from being heard if the
    // amplifier comes back before the buffers drain.
    setAmpEnabled(false);

    memset(_txScratch, 0, sizeof(_txScratch));
    for (int i = 0; i < 2; i++) {
        size_t written = 0;
        i2s_channel_write(_tx, _txScratch, sizeof(_txScratch), &written, 0);
    }
}

// ---------------------------------------------------------------------------
// Loopback bench test
// ---------------------------------------------------------------------------

bool AudioIO::startLoopback() {
    if (_loopbackTask || !_rx || !_tx) return false;
    _loopbackStop = false;
    setAmpEnabled(true);
    // AUDIO_TASK_CORE is core 1 where there are two and core 0 where there is
    // one — asking for a core that does not exist fails and the task never
    // starts. A frame is 20 ms of real time; missing that deadline is audible.
    if (xTaskCreatePinnedToCore(_loopbackTrampoline, "audio_loopback", 4096, this,
                                5, &_loopbackTask, AUDIO_TASK_CORE) != pdPASS) {
        _loopbackTask = nullptr;
        setAmpEnabled(false);
        return false;
    }
    return true;
}

void AudioIO::stopLoopback() {
    if (!_loopbackTask) return;
    _loopbackStop = true;
    // The task clears its own handle once it has left the loop.
    while (_loopbackTask) vTaskDelay(pdMS_TO_TICKS(5));
    setAmpEnabled(false);
}

void AudioIO::_loopbackTrampoline(void* arg) {
    static_cast<AudioIO*>(arg)->_runLoopback();
}

void AudioIO::_runLoopback() {
    int16_t frame[AUDIO_FRAME_SAMPLES];
    while (!_loopbackStop) {
        if (readFrame(frame, 100)) {
            writeFrame(frame, 100);
        }
    }
    _loopbackTask = nullptr;
    vTaskDelete(nullptr);
}

#endif // MARVIN_VOICE
