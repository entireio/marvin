#include "demo.h"

// ---------------------------------------------------------------------------
// The demo sequence table.
//
// Each row: { motorA, motorB, headRotation, headTilt, durationMs }
//
// Design notes – the patterns are meant to feel organic:
//   • The head often moves *before* the body turns (anticipation).
//   • Pauses with head sweeps simulate curiosity / scanning.
//   • Asymmetric durations avoid a robotic, metronomic feel.
//   • A mix of arcs, pivots, and straight runs keeps it lively.
// ---------------------------------------------------------------------------
const Demo::Step Demo::_sequence[] = {
    // --- Wake up: look around from standstill ---
    {  0,   0,   SERVO_ROTATION_CENTER,     SERVO_TILT_CENTER,      600 },  // centre gaze
    {  0,   0,   SERVO_ROTATION_CENTER + 40, SERVO_TILT_CENTER - 15, 800 },  // glance right-up
    {  0,   0,   SERVO_ROTATION_CENTER - 35, SERVO_TILT_CENTER + 10, 900 },  // glance left-down
    {  0,   0,   SERVO_ROTATION_CENTER,      SERVO_TILT_CENTER - 5,  500 },  // back to centre

    // --- Trundle forward, head scanning ---
    {  MOTOR_SPEED_MEDIUM, MOTOR_SPEED_MEDIUM, SERVO_ROTATION_CENTER + 20, SERVO_TILT_CENTER,      1200 },
    {  MOTOR_SPEED_MEDIUM, MOTOR_SPEED_MEDIUM, SERVO_ROTATION_CENTER - 25, SERVO_TILT_CENTER - 10, 1000 },
    {  MOTOR_SPEED_MEDIUM, MOTOR_SPEED_MEDIUM, SERVO_ROTATION_CENTER,      SERVO_TILT_CENTER,       600 },

    // --- Pause and look curiously ---
    {  0,   0,   SERVO_ROTATION_CENTER + 50, SERVO_TILT_CENTER + 20, 1100 },  // look far right-down
    {  0,   0,   SERVO_ROTATION_CENTER + 50, SERVO_TILT_CENTER - 20, 700  },  // tilt up (surprised?)
    {  0,   0,   SERVO_ROTATION_CENTER,      SERVO_TILT_CENTER,       500 },  // reset

    // --- Gentle right arc ---
    {  MOTOR_SPEED_MEDIUM, MOTOR_SPEED_SLOW, SERVO_ROTATION_CENTER + 30, SERVO_TILT_CENTER,  1400 },

    // --- Short reverse + pivot left (head leads) ---
    {  0,   0,   SERVO_ROTATION_CENTER - 45, SERVO_TILT_CENTER + 10,  700 },  // look left first
    { -MOTOR_SPEED_SLOW, MOTOR_SPEED_SLOW,  SERVO_ROTATION_CENTER - 45, SERVO_TILT_CENTER,  900 },  // pivot left
    {  0,   0,   SERVO_ROTATION_CENTER,      SERVO_TILT_CENTER,        400 },  // settle

    // --- Forward burst then slow ---
    {  MOTOR_SPEED_FAST, MOTOR_SPEED_FAST, SERVO_ROTATION_CENTER, SERVO_TILT_CENTER - 15, 700 },  // dash (head up)
    {  MOTOR_SPEED_SLOW, MOTOR_SPEED_SLOW, SERVO_ROTATION_CENTER, SERVO_TILT_CENTER,       800 },  // coast

    // --- Wide left arc, scanning ---
    {  MOTOR_SPEED_SLOW, MOTOR_SPEED_MEDIUM, SERVO_ROTATION_CENTER - 30, SERVO_TILT_CENTER + 5,  1200 },
    {  MOTOR_SPEED_SLOW, MOTOR_SPEED_MEDIUM, SERVO_ROTATION_CENTER + 15, SERVO_TILT_CENTER - 10, 1000 },

    // --- Stop and do a curious double-take ---
    {  0,   0,   SERVO_ROTATION_CENTER + 35, SERVO_TILT_CENTER,       500 },
    {  0,   0,   SERVO_ROTATION_CENTER - 10, SERVO_TILT_CENTER,       300 },  // snap back
    {  0,   0,   SERVO_ROTATION_CENTER + 35, SERVO_TILT_CENTER - 15,  600 },  // look again!
    {  0,   0,   SERVO_ROTATION_CENTER,      SERVO_TILT_CENTER,       500 },

    // --- Reverse a little, then spin ---
    { -MOTOR_SPEED_MEDIUM, -MOTOR_SPEED_MEDIUM, SERVO_ROTATION_CENTER, SERVO_TILT_CENTER + 15, 800 },
    {  MOTOR_SPEED_MEDIUM, -MOTOR_SPEED_MEDIUM, SERVO_ROTATION_CENTER - 40, SERVO_TILT_CENTER,  1000 },  // spin right
    {  0,   0,   SERVO_ROTATION_CENTER,      SERVO_TILT_CENTER,       600 },

    // --- Gentle forward to close the loop ---
    {  MOTOR_SPEED_SLOW, MOTOR_SPEED_SLOW, SERVO_ROTATION_CENTER + 10, SERVO_TILT_CENTER - 5, 1000 },
    {  MOTOR_SPEED_SLOW, MOTOR_SPEED_SLOW, SERVO_ROTATION_CENTER - 10, SERVO_TILT_CENTER + 5, 1000 },
    {  0,   0,   SERVO_ROTATION_CENTER,      SERVO_TILT_CENTER,       800 },  // full stop, reset head
};

const int Demo::_sequenceLength = sizeof(Demo::_sequence) / sizeof(Demo::_sequence[0]);

// ---------------------------------------------------------------------------

Demo::Demo(Motor& motorA, Motor& motorB, HeadServos& head)
    : _motorA(motorA)
    , _motorB(motorB)
    , _head(head)
    , _enabled(DEMO_ENABLED_AT_BOOT)
    , _stepIndex(0)
    , _stepStartMs(0) {}

void Demo::enable() {
    _enabled = true;
    _stepIndex = 0;
    _stepStartMs = millis();
    _startStep(0);
}

void Demo::disable() {
    _enabled = false;
    _motorA.stop();
    _motorB.stop();
    _head.setRotation(SERVO_ROTATION_CENTER);
    _head.setTilt(SERVO_TILT_CENTER);
}

void Demo::update() {
    if (!_enabled) return;

    unsigned long elapsed = millis() - _stepStartMs;
    if (elapsed >= _sequence[_stepIndex].durationMs) {
        // Advance to next step, wrapping around
        _stepIndex = (_stepIndex + 1) % _sequenceLength;
        _stepStartMs = millis();
        _startStep(_stepIndex);
    }
}

void Demo::_startStep(int index) {
    const Step& s = _sequence[index];
    _motorA.setSpeed(s.motorASpeed);
    _motorB.setSpeed(s.motorBSpeed);
    _head.setRotation(s.headRotation);
    _head.setTilt(s.headTilt);
}
