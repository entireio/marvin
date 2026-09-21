#include "gestures.h"

// ---------------------------------------------------------------------------
// The gesture tables.
//
// Tilt runs 30 (back) to 85 (forward) with 57 as neutral, so "down" is a larger
// number and "up" a smaller one. Ranges are kept well inside the limits in
// config.h: a gesture that slams into a mechanical stop sounds wrong even when
// it looks fine.
//
// Durations assume the interpolation rate set by SERVO_STEP_SIZE and
// SERVO_UPDATE_INTERVAL_MS — 100 degrees per second at the defaults — and leave
// a little slack at the end of each step so the head settles before moving on.
// ---------------------------------------------------------------------------

// "I'm listening." A dip forward, a glance up, and back to neutral. Deliberately
// asymmetric: the dip is quick and the recovery unhurried, which reads as
// attention rather than as a twitch.
static const Gestures::Step WAKE_ACK_STEPS[] = {
    { GESTURE_KEEP, SERVO_TILT_CENTER + 13, 220 },  // 70: dip down
    { GESTURE_KEEP, SERVO_TILT_CENTER -  9, 280 },  // 48: look up
    { GESTURE_KEEP, SERVO_TILT_CENTER,      200 },  // 57: settle
};

static const Gestures::Step NOD_STEPS[] = {
    { GESTURE_KEEP, SERVO_TILT_CENTER + 15, 200 },
    { GESTURE_KEEP, SERVO_TILT_CENTER - 12, 240 },
    { GESTURE_KEEP, SERVO_TILT_CENTER + 10, 200 },
    { GESTURE_KEEP, SERVO_TILT_CENTER,      220 },
};

static const Gestures::Step SHAKE_STEPS[] = {
    { SERVO_ROTATION_CENTER + 22, GESTURE_KEEP, 260 },
    { SERVO_ROTATION_CENTER - 22, GESTURE_KEEP, 340 },
    { SERVO_ROTATION_CENTER + 12, GESTURE_KEEP, 260 },
    { SERVO_ROTATION_CENTER,      GESTURE_KEEP, 260 },
};

static const Gestures::Step CENTRE_STEPS[] = {
    { SERVO_ROTATION_CENTER, SERVO_TILT_CENTER, 400 },
};

#define SEQ(name, table) { name, table, (int)(sizeof(table) / sizeof(table[0])) }

const Gestures::Sequence Gestures::_sequences[GESTURE_COUNT] = {
    SEQ("wake_ack", WAKE_ACK_STEPS),
    SEQ("nod",      NOD_STEPS),
    SEQ("shake",    SHAKE_STEPS),
    SEQ("centre",   CENTRE_STEPS),
};

#undef SEQ

// ---------------------------------------------------------------------------

Gestures::Gestures(HeadServos& head)
    : _head(head)
    , _current(GESTURE_NONE)
    , _stepIndex(0)
    , _stepStartMs(0) {}

bool Gestures::play(GestureId id) {
    if (id < 0 || id >= GESTURE_COUNT) return false;
    _current = id;
    _stepIndex = 0;
    _stepStartMs = millis();
    _startStep(0);
    return true;
}

bool Gestures::play(const char* name) {
    if (!name) return false;
    for (int i = 0; i < GESTURE_COUNT; i++) {
        if (strcmp(name, _sequences[i].name) == 0) {
            return play((GestureId)i);
        }
    }
    return false;
}

void Gestures::cancel() {
    _current = GESTURE_NONE;
}

void Gestures::update() {
    if (_current == GESTURE_NONE) return;

    const Sequence& seq = _sequences[_current];
    if (millis() - _stepStartMs < seq.steps[_stepIndex].durationMs) return;

    _stepIndex++;
    if (_stepIndex >= seq.length) {
        // Gestures do not loop. Unlike the demo sequence they are punctuation,
        // not behaviour.
        _current = GESTURE_NONE;
        return;
    }
    _stepStartMs = millis();
    _startStep(_stepIndex);
}

void Gestures::_startStep(int index) {
    const Step& s = _sequences[_current].steps[index];
    if (s.rotation != GESTURE_KEEP) _head.setRotation(s.rotation);
    if (s.tilt     != GESTURE_KEEP) _head.setTilt(s.tilt);
}
