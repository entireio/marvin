#include "voice.h"

#ifdef MARVIN_VOICE

// How long after the last audio frame to leave the amplifier powered.
//
// The I2S DMA holds about 90 ms of queued samples, so cutting power the instant
// the backend says the reply is over clips the last word. Waiting is also what
// keeps a conversational pause from producing an audible click between
// sentences.
static const unsigned long AMP_DRAIN_MS = 200;

// How often to report signal strength while connected. Rare on purpose: it is a
// nicety for the web controller, not telemetry anybody is paid to collect.
static const unsigned long STATE_INTERVAL_MS = 30000;

// The audio callback the network task calls is a plain function pointer, so it
// needs a module-level handle the same way the BLE and WebSocket callbacks do.
static AudioIO* _audioForDownlink = nullptr;

// Voice::handleCloudAudio plays speech as it arrives, on the network task.
//
// Written straight to I2S rather than queued: the DMA buffer is already the
// jitter buffer, and a second one in front of it would only add latency and a
// place for audio to be left behind when a reply is cut short.
void Voice::handleCloudAudio(const uint8_t* pcm, size_t bytes) {
    if (!_audioForDownlink) return;

    static int16_t frame[AUDIO_FRAME_SAMPLES];
    static size_t  filled = 0;

    for (size_t i = 0; i + 1 < bytes; i += 2) {
        frame[filled++] = (int16_t)((uint16_t)pcm[i] | ((uint16_t)pcm[i + 1] << 8));
        if (filled == AUDIO_FRAME_SAMPLES) {
            _audioForDownlink->writeFrame(frame, 100);
            filled = 0;
        }
    }
}

bool Voice::begin(Settings& settings, AudioIO& audio, CloudLink& cloud,
                  Gestures& gestures, NetWiFi& wifi, VoiceCommandHandler onCommand) {
    _settings = &settings;
    _audio = &audio;
    _cloud = &cloud;
    _gestures = &gestures;
    _wifi = &wifi;
    _onCommand = onCommand;
    _audioForDownlink = &audio;

    if (!_detector->begin()) {
        Serial.printf("[voice] wake word detector \"%s\" would not start\r\n", _detector->name());
    }

    _stopCapture = false;
    // Away from the radios where the chip has a second core; sharing with them
    // where it does not, at a higher priority than the network task. A missed
    // 20 ms deadline here is a gap in what the robot heard, and a wake word
    // model would live on this task too.
    if (xTaskCreatePinnedToCore(_captureTrampoline, "voice_capture", 8192, this,
                                6, &_captureTask, AUDIO_TASK_CORE) != pdPASS) {
        Serial.println("[voice] could not start the capture task");
        _captureTask = nullptr;
        return false;
    }

    Serial.printf("[voice] ready; wake word: %s\r\n", _detector->name());
    return true;
}

void Voice::_captureTrampoline(void* arg) {
    static_cast<Voice*>(arg)->_runCapture();
}

// _runCapture reads the microphone forever and decides what to do with it.
//
// It runs even when nothing is listening, because the wake word detector needs
// a continuous stream to find a phrase in. While Marvin is speaking it keeps
// reading and throws the audio away: the microphone is centimetres from the
// speaker and there is no echo canceller yet, so anything captured now is
// mostly Marvin.
void Voice::_runCapture() {
    int16_t frame[AUDIO_FRAME_SAMPLES];

    while (!_stopCapture) {
        if (!_audio->readFrame(frame, 100)) continue;

        switch (_state) {
        case State::Idle:
            if (_detector->feed(frame)) {
                // Flagged rather than acted on: waking moves the head, and the
                // head is the main loop's business.
                _wakePending = true;
            }
            break;

        case State::Listening:
            _cloud->sendAudioFrame(frame);
            break;

        case State::Thinking:
        case State::Speaking:
            // Discarded. See above.
            break;
        }
    }
    _captureTask = nullptr;
    vTaskDelete(nullptr);
}

void Voice::startListening() {
    // begin() is skipped entirely on a robot that has never been provisioned,
    // so everything below may be null. The W command is reachable regardless —
    // it is how you find out the robot is not set up.
    if (!_cloud || !_gestures) {
        Serial.println("[voice] not set up — use the web controller over Bluetooth");
        return;
    }
    if (_state != State::Idle) return;

    if (!_cloud->isConnected()) {
        Serial.println("[voice] asked to listen, but the backend is not connected");
        _gestures->play(GESTURE_SHAKE);
        return;
    }

    // The gesture first, and locally. Marvin nods because it heard you, not
    // because a server in another country agreed that it did — which is the
    // difference between a robot that feels attentive and one that feels laggy.
    // It is worth just as much when a button started the conversation: the nod
    // is how you know the robot is actually listening.
    _gestures->play(GESTURE_WAKE_ACK);

    _detector->reset();
    _state = State::Listening;
    _turnStartedMs = millis();
    _cloud->sendWake();
    Serial.println("[voice] listening");
}

void Voice::stopListening() {
    if (!_cloud || _state != State::Listening) return;

    // Stop sending before saying so, or the frames still in flight arrive after
    // the turn is closed and the backend has nowhere to put them.
    _state = State::Thinking;
    _thinkingSinceMs = millis();
    _cloud->sendTurnEnd();
    Serial.println("[voice] waiting for a reply");
}

void Voice::cancel() {
    if (!_audio || _state == State::Idle) return;
    if (_state == State::Speaking) _audio->flushPlayback();
    _state = State::Idle;
    _detector->reset();
    Serial.println("[voice] cancelled");
}

void Voice::update() {
    if (!_cloud || !_audio) return;

    // The audio task saw the wake word; do the parts that move servos here.
    if (_wakePending) {
        _wakePending = false;
        startListening();
    }

    CloudControl msg;
    while (_cloud->pollControl(msg)) {
        _handleControl(msg);
    }

    // Power the amplifier down once the DMA has drained.
    if (_muteAtMs && millis() >= _muteAtMs) {
        _muteAtMs = 0;
        _audio->setAmpEnabled(false);
    }

    // Nobody pressed stop and the service's turn detection did not fire either.
    // The backstop matters most in push-to-talk, where an open microphone stays
    // open until someone comes back to it.
    if (_state == State::Listening && millis() - _turnStartedMs > VOICE_TURN_TIMEOUT_MS) {
        Serial.println("[voice] listened long enough; asking for the reply");
        stopListening();
    }

    // A reply that never arrived. Without this the robot would sit in Thinking
    // for good and refuse to start another conversation.
    if (_state == State::Thinking && millis() - _thinkingSinceMs > VOICE_TURN_TIMEOUT_MS) {
        Serial.println("[voice] no reply came; giving up on this turn");
        _gestures->play(GESTURE_SHAKE);
        _state = State::Idle;
    }

    if (_cloud->isConnected() && millis() - _lastStateSentMs > STATE_INTERVAL_MS) {
        _lastStateSentMs = millis();
        _cloud->sendState(_wifi->rssi(), 0);
    }
}

void Voice::_handleControl(const CloudControl& msg) {
    const String type(msg.type);

    if (type == "ready") {
        Serial.printf("[voice] backend accepted the session%s\r\n",
                      msg.provider[0] ? (String(" (") + msg.provider + ")").c_str() : "");

    } else if (type == "listen") {
        _state = State::Listening;
        _turnStartedMs = millis();

    } else if (type == "speak_begin") {
        _state = State::Speaking;
        _muteAtMs = 0;
        _audio->setAmpEnabled(true);

    } else if (type == "speak_end") {
        _state = State::Idle;
        // Not muted here: there is still queued audio in the DMA buffers.
        _muteAtMs = millis() + AMP_DRAIN_MS;
        _detector->reset();

    } else if (type == "act") {
        // Actions from the backend. This is where the intent harness will
        // arrive; today only the web controller produces them.
        if (msg.gesture[0]) {
            if (!_gestures->play(msg.gesture)) {
                Serial.printf("[voice] backend asked for unknown gesture \"%s\"\r\n", msg.gesture);
            }
        } else if (msg.cmd[0] && _onCommand) {
            _onCommand(String(msg.cmd));
        }

    } else if (type == "error") {
        Serial.printf("[voice] backend error: %s\r\n", msg.message);
        _gestures->play(GESTURE_SHAKE);
        _state = State::Idle;
        _muteAtMs = millis() + AMP_DRAIN_MS;

    } else if (type == "ping") {
        // The library answers protocol-level pings itself; this is the
        // application-level one, and needs no reply beyond staying alive.
    }
}

String Voice::summary() const {
    String s = "voice:  ";
    switch (_state) {
    case State::Idle:      s += "idle"; break;
    case State::Listening: s += "listening"; break;
    case State::Thinking:  s += "waiting for a reply"; break;
    case State::Speaking:  s += "speaking"; break;
    }
    if (pushToTalk()) {
        s += ", push-to-talk (no wake word on this board)";
    } else {
        s += ", wake word: ";
        s += _detector->name();
    }
    s += "\r\n";
    return s;
}

#endif // MARVIN_VOICE
