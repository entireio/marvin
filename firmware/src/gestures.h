#ifndef GESTURES_H
#define GESTURES_H

#include <Arduino.h>
#include "config.h"
#include "head_servos.h"

// Short, named head movements that mean something to a person watching.
//
// Marvin has no display, so a gesture is the only way it can say "I heard you"
// or "I didn't understand". The wake acknowledgement is the one that matters
// most: it fires locally the instant the wake word is recognised, before any
// network round trip, so the robot feels attentive rather than laggy.
//
// This is also where actions sent down from the backend land — play(name)
// takes the same strings the server emits.

enum GestureId {
    GESTURE_NONE = -1,
    GESTURE_WAKE_ACK = 0,  // "I'm listening": dip down, glance up, settle
    GESTURE_NOD,           // yes / acknowledged
    GESTURE_SHAKE,         // no / didn't understand
    GESTURE_CENTRE,        // return to neutral
    GESTURE_COUNT
};

// Sentinel for a step that leaves one axis alone. A gesture that is all tilt
// should not drag the pan servo back to centre behind it.
constexpr float GESTURE_KEEP = -1.0f;

class Gestures {
public:
    // One step: where to put each axis, and how long to stay there. Timing is
    // wall-clock rather than "until the servo arrives" because HeadServos
    // interpolates at a known rate and polling for arrival buys nothing.
    struct Step {
        float rotation;             // 0..180, or GESTURE_KEEP
        float tilt;                 // 0..180, or GESTURE_KEEP
        unsigned long durationMs;
    };

    struct Sequence {
        const char* name;
        const Step* steps;
        int         length;
    };

    explicit Gestures(HeadServos& head);

    // Start a gesture, replacing whatever was playing. Returns false only for
    // an unknown name.
    bool play(GestureId id);
    bool play(const char* name);

    // Call from loop(). Advances the sequence; a no-op when nothing is playing.
    void update();

    bool isPlaying() const { return _current != GESTURE_NONE; }

    // Stop where we are. Leaves the head wherever the gesture had got to,
    // rather than snapping it — a snap reads as a fault, not a cancellation.
    void cancel();

private:
    HeadServos&   _head;
    GestureId     _current;
    int           _stepIndex;
    unsigned long _stepStartMs;

    static const Sequence _sequences[GESTURE_COUNT];

    void _startStep(int index);
};

#endif // GESTURES_H
