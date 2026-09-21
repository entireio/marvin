#ifndef CLOUD_LINK_H
#define CLOUD_LINK_H

#include "config.h"

#ifdef MARVIN_VOICE

#include <Arduino.h>
#include <freertos/FreeRTOS.h>
#include <freertos/queue.h>
#include <freertos/semphr.h>
#include "settings.h"

// The robot's end of the link to the backend.
//
// One TLS WebSocket carries the whole conversation: binary frames are audio,
// text frames are JSON control messages. It stays open between conversations
// rather than dialling on each one, because a TLS handshake takes about as long
// as a person expects an answer to take, and an idle socket on a modem-sleeping
// radio costs almost nothing.
//
// Everything that touches the socket runs on one task, pinned to core 0
// alongside the network stack. Sends from other tasks are serialised behind a
// mutex, and received control messages go onto a queue that the main loop
// drains — so nothing off the network reaches the servos on a foreign task.

// One control message from the backend, flattened into fixed buffers so it can
// be copied through a queue without pointing at memory the parser has freed.
struct CloudControl {
    char type[24];
    char provider[24];
    char cmd[24];
    char gesture[24];
    char message[96];
};

// Called on the network task when speech arrives. Expected to hand the audio
// straight to the I2S driver and return; anything slower stalls the socket.
typedef void (*CloudAudioHandler)(const uint8_t* pcm, size_t bytes);

class CloudLink {
public:
    // Connect, and keep reconnecting. Returns false only if the settings do not
    // name a backend, or a task or queue could not be created.
    bool begin(Settings& settings, CloudAudioHandler onAudio);
    void end();

    bool isConnected() const { return _connected; }

    // Queue one frame of microphone audio. Returns false when the link is down
    // or the socket is too far behind — in which case the frame is dropped,
    // because stale audio is worse than missing audio.
    bool sendAudioFrame(const int16_t* samples);

    // Control messages. Safe to call from any task.
    bool sendHello();
    bool sendWake();
    bool sendTurnEnd();
    bool sendState(int rssi, int batteryMv);

    // Take the next control message from the backend, if any. Call from the
    // main loop; returns false when the queue is empty.
    bool pollControl(CloudControl& out);

    String summary() const;

private:
    Settings*         _settings = nullptr;
    CloudAudioHandler _onAudio = nullptr;

    volatile bool _connected = false;
    volatile bool _stop = false;
    TaskHandle_t  _task = nullptr;
    QueueHandle_t _controlQueue = nullptr;
    SemaphoreHandle_t _socketMutex = nullptr;

    String   _authHeader; // must outlive the call that hands it to the library
    String   _host;
    String   _path;
    uint16_t _port = 443;
    bool     _secure = true;

    uint16_t _seq = 0;
    uint32_t _dropped = 0;

    bool _parseURL(const String& url);
    bool _sendText(const String& json);

    static void _taskTrampoline(void* arg);
    void _run();
    void _onEvent(int type, uint8_t* payload, size_t length);
    void _handleBinary(const uint8_t* data, size_t length);
    void _handleText(const uint8_t* data, size_t length);
};

#endif // MARVIN_VOICE
#endif // CLOUD_LINK_H
