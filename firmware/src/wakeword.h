#ifndef WAKEWORD_H
#define WAKEWORD_H

#include "config.h"

#ifdef MARVIN_VOICE

#include <Arduino.h>

// Deciding that someone said "Hey Marvin".
//
// The interface exists before the model does, on purpose. Which engine ends up
// behind it is the least settled decision in this project — esp-sr's WakeNet
// needs a phrase Espressif has trained, a custom microWakeWord model has to be
// trained and then made to run outside ESPHome, and there is always the fallback
// of gating on speech and confirming the phrase in the backend. Every one of
// those is a different implementation of these four methods and nothing else.
//
// Until one lands, NullWakeWord never fires and the conversation is started by
// hand — the `W` command over serial or Bluetooth. That is enough to prove the
// entire rest of the path.

class WakeWordDetector {
public:
    virtual ~WakeWordDetector() {}

    // Prepare the detector. Returns false if it cannot run here.
    virtual bool begin() { return true; }

    // Feed one frame of AUDIO_FRAME_SAMPLES mono samples. Returns true on the
    // frame where the wake word completes.
    //
    // Called from the audio task every 20 ms, so an implementation has well
    // under that to decide.
    virtual bool feed(const int16_t* samples) = 0;

    // Forget any partial match. Called when a conversation starts, so that the
    // robot's own voice cannot finish a phrase the user began.
    virtual void reset() {}

    virtual const char* name() const = 0;
};

// The placeholder: consumes audio, never fires.
class NullWakeWord : public WakeWordDetector {
public:
    bool feed(const int16_t*) override { return false; }
    const char* name() const override { return "none (use the W command)"; }
};

#endif // MARVIN_VOICE
#endif // WAKEWORD_H
