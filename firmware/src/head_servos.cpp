#include "head_servos.h"

HeadServos::HeadServos()
    : _currentRotation((float)SERVO_ROTATION_CENTER)
    , _targetRotation((float)SERVO_ROTATION_CENTER)
    , _currentTilt((float)SERVO_TILT_CENTER)
    , _targetTilt((float)SERVO_TILT_CENTER)
    , _lastUpdate(0) {}

void HeadServos::begin() {
    // Allocate all ESP32 hardware timers for servo PWM
    ESP32PWM::allocateTimer(0);
    ESP32PWM::allocateTimer(1);
    ESP32PWM::allocateTimer(2);
    ESP32PWM::allocateTimer(3);

    _rotationServo.setPeriodHertz(50);
    _rotationServo.attach(PIN_SERVO_ROTATION, SERVO_MIN_PULSE_WIDTH, SERVO_MAX_PULSE_WIDTH);

    _tiltServo.setPeriodHertz(50);
    _tiltServo.attach(PIN_SERVO_TILT, SERVO_MIN_PULSE_WIDTH, SERVO_MAX_PULSE_WIDTH);

    // Move to initial centre position
    _rotationServo.write((int)_currentRotation);
    _tiltServo.write((int)_currentTilt);
}

void HeadServos::setRotation(float angle) {
    _targetRotation = constrain(angle, (float)SERVO_ROTATION_MIN, (float)SERVO_ROTATION_MAX);
}

void HeadServos::setTilt(float angle) {
    _targetTilt = constrain(angle, (float)SERVO_TILT_MIN, (float)SERVO_TILT_MAX);
}

void HeadServos::update() {
    unsigned long now = millis();
    if (now - _lastUpdate < SERVO_UPDATE_INTERVAL_MS) return;
    _lastUpdate = now;

    // Interpolate rotation
    if (fabsf(_targetRotation - _currentRotation) > 0.01f) {
        if (_targetRotation > _currentRotation) {
            _currentRotation += SERVO_STEP_SIZE;
            if (_currentRotation > _targetRotation) _currentRotation = _targetRotation;
        } else {
            _currentRotation -= SERVO_STEP_SIZE;
            if (_currentRotation < _targetRotation) _currentRotation = _targetRotation;
        }
        int us = SERVO_MIN_PULSE_WIDTH
               + (_currentRotation / 180.0f) * (SERVO_MAX_PULSE_WIDTH - SERVO_MIN_PULSE_WIDTH);
        _rotationServo.writeMicroseconds(us);
    }

    // Interpolate tilt
    if (fabsf(_targetTilt - _currentTilt) > 0.01f) {
        if (_targetTilt > _currentTilt) {
            _currentTilt += SERVO_STEP_SIZE;
            if (_currentTilt > _targetTilt) _currentTilt = _targetTilt;
        } else {
            _currentTilt -= SERVO_STEP_SIZE;
            if (_currentTilt < _targetTilt) _currentTilt = _targetTilt;
        }
        int us = SERVO_MIN_PULSE_WIDTH
               + (_currentTilt / 180.0f) * (SERVO_MAX_PULSE_WIDTH - SERVO_MIN_PULSE_WIDTH);
        _tiltServo.writeMicroseconds(us);
    }
}
