#ifndef AUDIO_IO_H
#define AUDIO_IO_H

#include "config.h"

#ifdef MARVIN_VOICE

#include <Arduino.h>
#include <driver/i2s_std.h>

// Full-duplex I2S: the INMP441 microphone and the MAX98357A amplifier on one
// peripheral, sharing BCLK and WS.
//
// Sharing is not a shortcut, it is the design. Two independent buses would need
// four more pins than the SuperMini has to spare, and the ESP32-S3's I2S
// controller pairs a TX and an RX channel on the same port precisely so that a
// microphone and a speaker can run off one clock. The price is that capture and
// playback must agree on a sample rate — which is why the whole robot runs at
// 16 kHz and the backend does the converting.
//
// Wire format on the bus is 32-bit stereo slots at 16 kHz, so 64 BCLK per
// frame. That is not negotiable: the INMP441 is a 24-bit part that needs 32-bit
// slots, and full duplex requires both directions to use the same frame size.
// Everything above this class deals in 16-bit mono.

class AudioIO {
public:
    // Install and start the I2S driver, and put the amplifier in its muted
    // state. Returns false if the driver will not start, which in practice
    // means a pin conflict.
    bool begin();
    void end();

    // Read one frame of AUDIO_FRAME_SAMPLES mono samples. Blocks until a frame
    // is available or timeoutMs elapses; returns false on timeout.
    bool readFrame(int16_t* dst, uint32_t timeoutMs = 100);

    // Queue one frame of AUDIO_FRAME_SAMPLES mono samples for playback.
    // Returns false if the DMA queue stayed full for timeoutMs, which means the
    // network is delivering faster than real time and a frame has to go.
    bool writeFrame(const int16_t* src, uint32_t timeoutMs = 100);

    // Power the amplifier. Muted at boot and between replies: the MAX98357A
    // hisses audibly whenever it is enabled with a live clock and nothing to
    // play, and Marvin sitting silently should be silent.
    void setAmpEnabled(bool on);
    bool ampEnabled() const { return _ampOn; }

    // Discard queued playback and fill the DMA buffers with silence. Used when
    // a reply is cut short, so the tail of the old one does not leak out.
    void flushPlayback();

    // Frames dropped because playback could not keep up, since begin().
    uint32_t framesDropped() const { return _dropped; }

    // Bench test: pipe the microphone straight to the speaker on a dedicated
    // task. This is how the hardware gets proved before any of the network
    // stack exists, and it stays in the firmware because it is also the fastest
    // way to tell a dead microphone from a dead backend.
    bool startLoopback();
    void stopLoopback();
    bool loopbackRunning() const { return _loopbackTask != nullptr; }

private:
    i2s_chan_handle_t _tx = nullptr;
    i2s_chan_handle_t _rx = nullptr;
    bool          _ampOn = false;
    uint32_t      _dropped = 0;
    TaskHandle_t  _loopbackTask = nullptr;
    volatile bool _loopbackStop = false;

    // One bus frame is two 32-bit slots, so the scratch buffers are four times
    // the size of the mono frames the callers pass in.
    int32_t _rxScratch[AUDIO_FRAME_SAMPLES * 2];
    int32_t _txScratch[AUDIO_FRAME_SAMPLES * 2];

    static void _loopbackTrampoline(void* arg);
    void _runLoopback();
};

#endif // MARVIN_VOICE
#endif // AUDIO_IO_H
