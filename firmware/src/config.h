#ifndef CONFIG_H
#define CONFIG_H

// --- Hardware Pins Configuration ---

// Servo Motors
#define PIN_SERVO_TILT      1   // GPIO 1 for Tilt
#define PIN_SERVO_ROTATION  4   // GPIO 4 for Rotation (pan)

// DC Motor Driver (DRV8833 - always enabled, no enable pin)
// Motor A (e.g., left wheel)
#define PIN_MOTOR_A_IN1     5   // GPIO 5  - DRV8833 IN1
#define PIN_MOTOR_A_IN2     6   // GPIO 6  - DRV8833 IN2
// Motor B (e.g., right wheel)
#define PIN_MOTOR_B_IN3     20  // GPIO 20 - DRV8833 IN4 (inverted)
#define PIN_MOTOR_B_IN4     10  // GPIO 10 - DRV8833 IN3 (inverted)

// Future configurations (e.g., I2C, TOF, IR, Encoders) can be added here
// #define PIN_I2C_SDA      x
// #define PIN_I2C_SCL      x

// --- Servo Configuration ---
#define SERVO_MIN_PULSE_WIDTH 500  // Standard SG90 min pulse
#define SERVO_MAX_PULSE_WIDTH 2400 // Standard SG90 max pulse

// --- Servo Movement Configuration ---
#define SERVO_UPDATE_INTERVAL_MS 20   // Update servos every 20 ms (50Hz)
#define SERVO_STEP_SIZE          2.0f // Degrees to move per update (controls speed)

// --- Servo Angle Limits ---
#define SERVO_ROTATION_MIN   0     // Min rotation angle (degrees)
#define SERVO_ROTATION_MAX   180   // Max rotation angle (degrees)
#define SERVO_ROTATION_CENTER 90   // Centre rotation angle
#define SERVO_TILT_MIN       30    // Min tilt angle (degrees) - max tilt back
#define SERVO_TILT_MAX       85    // Max tilt angle (degrees) - max tilt forward
#define SERVO_TILT_CENTER    57    // Centre tilt angle (neutral)

// --- Motor PWM Configuration ---
#define MOTOR_PWM_FREQ       1000  // 1 kHz PWM frequency for DC motors
#define MOTOR_PWM_RESOLUTION 8     // 8-bit resolution (0-255)
#define MOTOR_MAX_SPEED      255   // Max PWM duty cycle

// --- Motor Speed Presets ---
#define MOTOR_SPEED_SLOW     80    // Slow cruise speed
#define MOTOR_SPEED_MEDIUM   140   // Medium cruise speed
#define MOTOR_SPEED_FAST     200   // Fast speed

// --- Demo Mode Configuration ---
#define DEMO_ENABLED_AT_BOOT false // Start with demo disabled

// --- BLE Configuration ---
#define BLE_DEVICE_NAME "Marvin"

#endif // CONFIG_H
