#ifndef DEMO_H
#define DEMO_H

#include <Arduino.h>
#include "config.h"
#include "motor.h"
#include "head_servos.h"

// A looping demo sequence that makes the robot explore a small area
// while moving its head in organic, curiosity-driven patterns.
class Demo {
public:
    Demo(Motor& motorA, Motor& motorB, HeadServos& head);

    void enable();
    void disable();
    bool isEnabled() const { return _enabled; }

    // Call from loop(). Advances the state machine when enabled.
    void update();

private:
    // A single step in the demo sequence
    struct Step {
        int   motorASpeed;     // -255..255
        int   motorBSpeed;     // -255..255
        float headRotation;    // 0..180
        float headTilt;        // 0..180
        unsigned long durationMs;
    };

    Motor&      _motorA;
    Motor&      _motorB;
    HeadServos& _head;

    bool  _enabled;
    int   _stepIndex;
    unsigned long _stepStartMs;

    static const Step _sequence[];
    static const int  _sequenceLength;

    void _startStep(int index);
};

#endif // DEMO_H
