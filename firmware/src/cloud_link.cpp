#include "cloud_link.h"

#ifdef MARVIN_VOICE

#include <ArduinoJson.h>
#include <WebSocketsClient.h>
#include "certs.h"

// Frame header on the wire, matching server/protocol/frame.go:
//   [type:1][flags:1][seq:2 big-endian]
static const uint8_t FRAME_AUDIO_UP   = 0x01;
static const uint8_t FRAME_AUDIO_DOWN = 0x02;
static const size_t  FRAME_HEADER_LEN = 4;

// Depth of the inbound control queue. Control messages are rare — a handful per
// conversation — so anything deeper would only be hiding a main loop that had
// stopped running.
static const int CONTROL_QUEUE_LEN = 8;

// The library keeps C-style callbacks, so it needs a module-level instance, the
// same way BleSerial does.
static CloudLink*         _instance = nullptr;
static WebSocketsClient   _ws;

// ---------------------------------------------------------------------------

bool CloudLink::begin(Settings& settings, CloudAudioHandler onAudio) {
    _settings = &settings;
    _onAudio = onAudio;
    _instance = this;

    if (!_settings->haveCloud()) {
        Serial.println("[cloud] no backend configured — set a URL and token over Bluetooth");
        return false;
    }
    if (!_parseURL(_settings->url())) {
        Serial.printf("[cloud] cannot parse backend URL \"%s\"\r\n", _settings->url().c_str());
        return false;
    }

    _controlQueue = xQueueCreate(CONTROL_QUEUE_LEN, sizeof(CloudControl));
    _socketMutex = xSemaphoreCreateMutex();
    if (!_controlQueue || !_socketMutex) {
        Serial.println("[cloud] out of memory");
        return false;
    }

    _authHeader = "Authorization: Bearer " + _settings->token();
    _ws.setExtraHeaders(_authHeader.c_str());
    _ws.onEvent([](WStype_t type, uint8_t* payload, size_t length) {
        if (_instance) _instance->_onEvent((int)type, payload, length);
    });
    // Ping every 30 s and give up after two missed pongs. Without this a link
    // dropped by a router or a load balancer looks alive until the next time
    // someone speaks, which is exactly when it must not.
    _ws.enableHeartbeat(30000, 10000, 2);
    _ws.setReconnectInterval(5000);

    if (_secure) {
        // Certificate verification, not decoration: the token in that header
        // authorises a live microphone. See certs.h.
        _ws.beginSslWithCA(_host.c_str(), _port, _path.c_str(), MARVIN_ROOT_CAS);
    } else {
        // Plain ws:// is for a backend on the bench. Say so out loud — a robot
        // quietly shipping a credential in clear text is worth a warning.
        Serial.println("[cloud] WARNING: ws:// is unencrypted; the device token is sent in the clear");
        _ws.begin(_host.c_str(), _port, _path.c_str());
    }

    _stop = false;
    // Beside the Wi-Fi and Bluetooth stacks. On the S3 that leaves the other
    // core to audio; on the single-core C3 it shares, and the lower priority
    // here than the capture task is what keeps audio ahead of the network.
    if (xTaskCreatePinnedToCore(_taskTrampoline, "cloud_link", 8192, this, 4,
                                &_task, NETWORK_TASK_CORE) != pdPASS) {
        Serial.println("[cloud] could not start the network task");
        _task = nullptr;
        return false;
    }

    Serial.printf("[cloud] connecting to %s://%s:%u%s\r\n",
                  _secure ? "wss" : "ws", _host.c_str(), _port, _path.c_str());
    return true;
}

void CloudLink::end() {
    if (!_task) return;
    _stop = true;
    while (_task) vTaskDelay(pdMS_TO_TICKS(5));
    _ws.disconnect();
    _connected = false;
}

// _parseURL splits "wss://host[:port][/path]" into its parts.
bool CloudLink::_parseURL(const String& url) {
    String rest;
    if (url.startsWith("wss://")) {
        _secure = true;
        _port = 443;
        rest = url.substring(6);
    } else if (url.startsWith("ws://")) {
        _secure = false;
        _port = 80;
        rest = url.substring(5);
    } else {
        return false;
    }

    int slash = rest.indexOf('/');
    String hostPort = (slash < 0) ? rest : rest.substring(0, slash);
    _path = (slash < 0) ? "/v1/device" : rest.substring(slash);

    int colon = hostPort.indexOf(':');
    if (colon < 0) {
        _host = hostPort;
    } else {
        _host = hostPort.substring(0, colon);
        _port = (uint16_t)hostPort.substring(colon + 1).toInt();
    }
    return _host.length() > 0 && _port > 0;
}

void CloudLink::_taskTrampoline(void* arg) {
    static_cast<CloudLink*>(arg)->_run();
}

void CloudLink::_run() {
    while (!_stop) {
        if (xSemaphoreTake(_socketMutex, pdMS_TO_TICKS(20)) == pdTRUE) {
            _ws.loop();
            xSemaphoreGive(_socketMutex);
        }
        // Yield: loop() is a poll, and spinning on it would starve everything
        // else on this core including the Wi-Fi driver.
        vTaskDelay(pdMS_TO_TICKS(2));
    }
    _task = nullptr;
    vTaskDelete(nullptr);
}

void CloudLink::_onEvent(int type, uint8_t* payload, size_t length) {
    switch (type) {
    case WStype_CONNECTED:
        _connected = true;
        Serial.printf("[cloud] connected to %s\r\n", _host.c_str());
        sendHello();
        break;

    case WStype_DISCONNECTED:
        if (_connected) Serial.println("[cloud] disconnected — will retry");
        _connected = false;
        break;

    case WStype_BIN:
        _handleBinary(payload, length);
        break;

    case WStype_TEXT:
        _handleText(payload, length);
        break;

    case WStype_ERROR:
        Serial.printf("[cloud] socket error (%u bytes)\r\n", (unsigned)length);
        break;

    default:
        break;
    }
}

void CloudLink::_handleBinary(const uint8_t* data, size_t length) {
    if (length < FRAME_HEADER_LEN) return;
    if (data[0] != FRAME_AUDIO_DOWN) return;
    if (!_onAudio) return;
    _onAudio(data + FRAME_HEADER_LEN, length - FRAME_HEADER_LEN);
}

void CloudLink::_handleText(const uint8_t* data, size_t length) {
    JsonDocument doc;
    if (deserializeJson(doc, data, length) != DeserializationError::Ok) {
        Serial.println("[cloud] undecodable control message");
        return;
    }

    CloudControl msg = {};
    auto copyField = [&doc](const char* key, char* dst, size_t cap) {
        const char* v = doc[key] | "";
        strlcpy(dst, v, cap);
    };
    copyField("t", msg.type, sizeof(msg.type));
    copyField("provider", msg.provider, sizeof(msg.provider));
    copyField("cmd", msg.cmd, sizeof(msg.cmd));
    copyField("gesture", msg.gesture, sizeof(msg.gesture));
    copyField("message", msg.message, sizeof(msg.message));

    if (msg.type[0] == '\0') return;

    // Never block the network task on a main loop that has stalled: the link
    // staying up matters more than any single message getting through.
    if (xQueueSend(_controlQueue, &msg, 0) != pdTRUE) {
        Serial.printf("[cloud] control queue full; dropped \"%s\"\r\n", msg.type);
    }
}

bool CloudLink::pollControl(CloudControl& out) {
    if (!_controlQueue) return false;
    return xQueueReceive(_controlQueue, &out, 0) == pdTRUE;
}

bool CloudLink::sendAudioFrame(const int16_t* samples) {
    if (!_connected || !_socketMutex) return false;

    uint8_t frame[FRAME_HEADER_LEN + AUDIO_FRAME_BYTES];
    frame[0] = FRAME_AUDIO_UP;
    frame[1] = 0;
    frame[2] = (uint8_t)(_seq >> 8);
    frame[3] = (uint8_t)(_seq & 0xFF);
    _seq++;

    // Little-endian PCM16, matching every other end of this pipeline.
    for (int i = 0; i < AUDIO_FRAME_SAMPLES; i++) {
        frame[FRAME_HEADER_LEN + i * 2]     = (uint8_t)(samples[i] & 0xFF);
        frame[FRAME_HEADER_LEN + i * 2 + 1] = (uint8_t)((samples[i] >> 8) & 0xFF);
    }

    // A short wait, then give up. Blocking here would back the audio task up
    // behind the network, and by the time the frame went out it would be stale
    // — which sounds worse to the listener than a twenty-millisecond gap.
    if (xSemaphoreTake(_socketMutex, pdMS_TO_TICKS(10)) != pdTRUE) {
        _dropped++;
        return false;
    }
    bool ok = _ws.sendBIN(frame, sizeof(frame));
    xSemaphoreGive(_socketMutex);
    if (!ok) _dropped++;
    return ok;
}

bool CloudLink::_sendText(const String& json) {
    if (!_socketMutex) return false;
    if (xSemaphoreTake(_socketMutex, pdMS_TO_TICKS(200)) != pdTRUE) return false;
    bool ok = _ws.sendTXT(json.c_str(), json.length());
    xSemaphoreGive(_socketMutex);
    return ok;
}

bool CloudLink::sendHello() {
    JsonDocument doc;
    doc["t"] = "hello";
    doc["device_id"] = _settings->deviceId();
    doc["fw"] = "marvin-voice";
    doc["codec"] = "pcm16";
    doc["sample_rate"] = AUDIO_SAMPLE_RATE;

    String out;
    serializeJson(doc, out);
    return _sendText(out);
}

bool CloudLink::sendWake()    { return _sendText("{\"t\":\"wake\"}"); }
bool CloudLink::sendTurnEnd() { return _sendText("{\"t\":\"turn_end\"}"); }

bool CloudLink::sendState(int rssi, int batteryMv) {
    JsonDocument doc;
    doc["t"] = "state";
    doc["rssi"] = rssi;
    if (batteryMv > 0) doc["battery_mv"] = batteryMv;

    String out;
    serializeJson(doc, out);
    return _sendText(out);
}

String CloudLink::summary() const {
    if (!_settings || !_settings->haveCloud()) return "cloud:  no backend configured\r\n";
    String s = "cloud:  ";
    s += _connected ? "connected to " : "connecting to ";
    s += _host;
    if (_dropped) s += " (" + String(_dropped) + " frames dropped)";
    s += "\r\n";
    return s;
}

#endif // MARVIN_VOICE
