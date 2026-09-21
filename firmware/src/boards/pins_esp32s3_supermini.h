#ifndef PINS_ESP32S3_SUPERMINI_H
#define PINS_ESP32S3_SUPERMINI_H

// Pin map — ESP32-S3 SuperMini (the voice build).
//
// Only the thirteen header GPIOs with no boot or system role are used:
//
//     1, 2, 4, 5, 6, 7, 8, 15, 16, 17, 18, 21, 38
//
// Everything else on this board is spoken for — strapping (3, 9-14, 45, 46),
// USB/JTAG (39-41), flash and PSRAM (26-37), or the RGB LED (48). Driving any
// of those is how a board stops booting.
//
// The S3 is here rather than the C3 because voice needs PSRAM: the wake word
// model runs from it, and acoustic echo cancellation will need it later.

// Servo Motors
#define PIN_SERVO_TILT      1
#define PIN_SERVO_ROTATION  2

// DC Motor Driver (DRV8833 - always enabled, no enable pin)
// Motor A (e.g., left wheel)
#define PIN_MOTOR_A_IN1     4
#define PIN_MOTOR_A_IN2     5
// Motor B (e.g., right wheel) — wired inverted, as on the C3
#define PIN_MOTOR_B_IN3     6
#define PIN_MOTOR_B_IN4     7

// --- Audio (I2S) ---
//
// The microphone (INMP441) and the amplifier (MAX98357A) share one I2S
// peripheral in full-duplex mode, so BCLK and WS are common to both and only
// the two data lines are separate. Sharing the clock is what forces capture and
// playback to the same sample rate; the backend resamples for whatever the AI
// provider wants.
#define PIN_I2S_BCLK        15  // mic SCK  + amp BCLK
#define PIN_I2S_WS          16  // mic WS   + amp LRC
#define PIN_I2S_DIN         17  // mic SD   -> ESP32
#define PIN_I2S_DOUT        18  // ESP32    -> amp DIN

// Amplifier shutdown/enable. Needs a 100k pull-up to 3V3 on the board: breakout
// modules pull SD down, so a high-impedance pin would leave the amp muted and
// hold no clean logic level.
#define PIN_AMP_SD          21

// Free for future use: GPIO 8, 38 — earmarked for the planned I2C bus
// (time-of-flight sensor, eye displays).

#endif // PINS_ESP32S3_SUPERMINI_H
