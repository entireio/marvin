#ifndef MOTOR_H
#define MOTOR_H

#include <Arduino.h>
#include "config.h"

// Represents a single DC motor channel on a DRV8833 H-bridge.
// IN1 HIGH / IN2 LOW = forward, IN1 LOW / IN2 HIGH = reverse, both LOW = coast.
class Motor {
public:
    Motor(uint8_t pinIN1, uint8_t pinIN2);

    // Initialize GPIO pins. Call in setup() before any other motor operations.
    void begin();

    // Set motor speed: -255 (full reverse) to +255 (full forward), 0 = coast stop.
    void setSpeed(int speed);

    // Coast stop (both pins LOW).
    void stop();

private:
    uint8_t _pinIN1;
    uint8_t _pinIN2;
};

#endif // MOTOR_H
