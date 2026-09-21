#ifndef PINS_ESP32C3_SUPERMINI_H
#define PINS_ESP32C3_SUPERMINI_H

// Pin map — ESP32-C3 SuperMini.
//
// The C3 exposes GPIO 0-10 and 20-21 on its header. Of those, GPIO 2, 8 and 9
// are strapping pins and GPIO 18/19 are the native USB pair, so the usable set
// is 0, 1, 3, 4, 5, 6, 7, 10, 20 and 21. GPIO 20/21 are UART0, which is free
// here because the board's console runs over native USB-CDC.
//
// Voice works on this board, without the wake word: the C3 has no PSRAM and no
// vector unit, so there is nowhere to run a keyword model. Conversations are
// started by hand instead — see VOICE_PUSH_TO_TALK in config.h. Everything
// else, including full-duplex I2S, is the same as on the S3.

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

// --- Audio (I2S) ---
//
// The four pins left on this board that have no boot or system role. The
// microphone and the amplifier share BCLK and WS on the C3's single I2S
// peripheral, exactly as on the S3.
#define PIN_I2S_BCLK        3   // mic SCK  + amp BCLK
#define PIN_I2S_WS          0   // mic WS   + amp LRC
#define PIN_I2S_DIN         21  // mic SD   -> ESP32
#define PIN_I2S_DOUT        7   // ESP32    -> amp DIN

// Amplifier shutdown/enable — deliberately not wired by default.
//
// The only pin left is GPIO 2, and GPIO 2 must read HIGH at reset or the chip
// does not boot. Driving it low to mute the amplifier is safe; being mid-mute
// when the board resets is not, and a robot that will not start is a far worse
// problem than the amplifier's idle hiss.
//
// To wire it anyway: connect the amplifier's SD pin to GPIO 2 **with a 10 kΩ
// pull-up to 3V3**, which holds the strapping requirement through a reset, and
// uncomment the line below.
//
// #define PIN_AMP_SD       2

// Free for future use: GPIO 8, 9 (both strapping — see above).

#endif // PINS_ESP32C3_SUPERMINI_H
