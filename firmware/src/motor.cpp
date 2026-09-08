#include "motor.h"

Motor::Motor(uint8_t pinIN1, uint8_t pinIN2)
    : _pinIN1(pinIN1), _pinIN2(pinIN2) {}

void Motor::begin() {
    // Drive pins LOW immediately to prevent motor spin from floating pins at boot.
    pinMode(_pinIN1, OUTPUT);
    pinMode(_pinIN2, OUTPUT);
    digitalWrite(_pinIN1, LOW);
    digitalWrite(_pinIN2, LOW);
}

void Motor::setSpeed(int speed) {
    speed = constrain(speed, -MOTOR_MAX_SPEED, MOTOR_MAX_SPEED);

    if (speed > 0) {
        analogWrite(_pinIN1, speed);
        analogWrite(_pinIN2, 0);
    } else if (speed < 0) {
        analogWrite(_pinIN1, 0);
        analogWrite(_pinIN2, -speed);
    } else {
        stop();
    }
}

void Motor::stop() {
    analogWrite(_pinIN1, 0);
    analogWrite(_pinIN2, 0);
}
