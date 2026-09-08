#ifndef HEAD_SERVOS_H
#define HEAD_SERVOS_H

#include <ESP32Servo.h>
#include "config.h"

// Controls the two head servos (rotation / pan and tilt) with smooth interpolation.
class HeadServos {
public:
    HeadServos();

    // Attach servos to their pins. Call in setup().
    void begin();

    // Set target angles (0-180). The servos interpolate toward these targets
    // each time update() is called.
    void setRotation(float angle);
    void setTilt(float angle);

    // Step servos toward their targets. Call from loop() every iteration;
    // internally rate-limits to SERVO_UPDATE_INTERVAL_MS.
    void update();

    float getRotation() const { return _currentRotation; }
    float getTilt()     const { return _currentTilt; }

private:
    Servo _rotationServo;
    Servo _tiltServo;

    float _currentRotation;
    float _targetRotation;
    float _currentTilt;
    float _targetTilt;

    unsigned long _lastUpdate;
};

#endif // HEAD_SERVOS_H
