#ifndef VOICE_H
#define VOICE_H

#include "config.h"

#ifdef MARVIN_VOICE

#include <Arduino.h>
#include "audio_io.h"
#include "cloud_link.h"
#include "gestures.h"
#include "net_wifi.h"
#include "settings.h"
#include "wakeword.h"

// The conversation: wake, listen, answer, back to waiting.
//
// Audio capture runs on its own task because a frame is a 20 ms deadline and
// the main loop is busy with servos. Everything that has consequences for the
// robot's body — gestures, commands from the backend — is handled in update()
// on the main loop instead, so that nothing arriving off the network moves a
// servo from a foreign task.

// Commands from the backend are executed through the same interpreter as serial
// and Bluetooth, rather than a second one that would drift from it.
typedef void (*VoiceCommandHandler)(const String& command);

class Voice {
public:
    // Idle      waiting — for the wake word, or for someone to press a button
    // Listening streaming the microphone to the backend
    // Thinking  the person has finished; waiting for the reply to start
    // Speaking  playing the reply, microphone ignored
    enum class State { Idle, Listening, Thinking, Speaking };

    bool begin(Settings& settings, AudioIO& audio, CloudLink& cloud,
               Gestures& gestures, NetWiFi& wifi, VoiceCommandHandler onCommand);

    // Call from loop(). Handles backend messages, timeouts and the amplifier.
    void update();

    // Start listening. Called by the wake word detector where there is one, and
    // by hand — from the web controller or the W command — where there is not.
    void startListening();

    // Stop listening and ask for the reply.
    //
    // In push-to-talk this is the more important half: it says the person has
    // finished speaking, which is the cue the model would otherwise have to
    // guess at from silence. Pressing stop gets an answer immediately instead of
    // after the service's own silence window.
    void stopListening();

    // Abandon a conversation in progress, wanting no reply.
    void cancel();

    // True when this build has no wake word and conversations must be started
    // by hand. Always so on the C3.
    static bool pushToTalk() {
#ifdef VOICE_PUSH_TO_TALK
        return true;
#else
        return false;
#endif
    }

    // Play speech arriving from the backend. Passed to CloudLink::begin, which
    // calls it on the network task — so it is static, and does nothing until
    // begin() has given it an audio device.
    static void handleCloudAudio(const uint8_t* pcm, size_t bytes);

    State state() const { return _state; }
    String summary() const;

private:
    Settings*   _settings = nullptr;
    AudioIO*    _audio = nullptr;
    CloudLink*  _cloud = nullptr;
    Gestures*   _gestures = nullptr;
    NetWiFi*    _wifi = nullptr;
    VoiceCommandHandler _onCommand = nullptr;

    NullWakeWord      _nullDetector;
    WakeWordDetector* _detector = &_nullDetector;

    volatile State _state = State::Idle;
    unsigned long  _thinkingSinceMs = 0;
    volatile bool  _wakePending = false;   // set by the audio task, read by loop()
    unsigned long  _turnStartedMs = 0;
    unsigned long  _muteAtMs = 0;          // when to power the amplifier down
    unsigned long  _lastStateSentMs = 0;

    TaskHandle_t  _captureTask = nullptr;
    volatile bool _stopCapture = false;

    static void _captureTrampoline(void* arg);
    void _runCapture();
    void _handleControl(const CloudControl& msg);
};

#endif // MARVIN_VOICE
#endif // VOICE_H
