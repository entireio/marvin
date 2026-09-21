#ifndef CONFIG_H
#define CONFIG_H

// --- Board selection ---
//
// Pin maps live in boards/. Each PlatformIO environment defines exactly one of
// these; adding a board means adding a header there and an [env:...] block in
// platformio.ini, with no change to any driver.

#if defined(MARVIN_BOARD_ESP32S3_SUPERMINI)
  #include "boards/pins_esp32s3_supermini.h"
#elif defined(MARVIN_BOARD_ESP32C3_SUPERMINI)
  #include "boards/pins_esp32c3_supermini.h"
#else
  #error "No board selected. Define MARVIN_BOARD_* in platformio.ini."
#endif

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

#ifdef MARVIN_VOICE

// --- Task placement ---
//
// The S3 has two cores and the C3 has one, so this is not a tuning knob: asking
// for core 1 on a single-core chip fails outright and the task never starts.
//
// Where there are two, audio gets the one the radios are not on. A frame is a
// 20 ms deadline and missing it is audible; Wi-Fi and Bluetooth are bursty
// neighbours. Where there is one, everything shares it and the priorities below
// are what keep audio ahead of the network.
#if defined(MARVIN_BOARD_ESP32C3_SUPERMINI)
  #define AUDIO_TASK_CORE     0
  #define NETWORK_TASK_CORE   0
#else
  #define AUDIO_TASK_CORE     1
  #define NETWORK_TASK_CORE   0
#endif

// --- Wake word ---
//
// VOICE_PUSH_TO_TALK means conversations are started by hand — from the web
// controller, or with the W command — rather than by saying "Hey Marvin".
//
// Always on for the C3, which has no PSRAM and no vector unit and therefore
// nowhere to run a keyword model. On the S3 it is the state of things until a
// model lands; wakeword.h is the seam.
#if defined(MARVIN_BOARD_ESP32C3_SUPERMINI)
  #define VOICE_PUSH_TO_TALK  1
#endif

// --- Audio Configuration ---
//
// One rate for capture and playback, because the microphone and the amplifier
// share an I2S peripheral and therefore a clock. 16 kHz is what wake word
// models expect and is ample for speech; the backend resamples to whatever the
// AI provider wants.
#define AUDIO_SAMPLE_RATE     16000
#define AUDIO_FRAME_MS        20                                    // one network frame
#define AUDIO_FRAME_SAMPLES   (AUDIO_SAMPLE_RATE / 1000 * AUDIO_FRAME_MS)  // 320
#define AUDIO_FRAME_BYTES     (AUDIO_FRAME_SAMPLES * 2)             // 640, PCM16 mono

// How much audio the I2S driver buffers in each direction. Enough to ride out a
// scheduling hiccup or a burst of network jitter, short enough that cutting a
// reply short is not heard as a delay.
//
// Smaller on the C3, where 400 KB of SRAM has to hold Wi-Fi, TLS, Bluetooth and
// this at the same time. It buys less slack, and on a single core there is less
// slack to begin with — so expect the occasional gap on a busy network.
#if defined(MARVIN_BOARD_ESP32C3_SUPERMINI)
  #define AUDIO_DMA_DESC_NUM  4
  #define AUDIO_DMA_FRAME_NUM 240   // about 60 ms
#else
  #define AUDIO_DMA_DESC_NUM  6
  #define AUDIO_DMA_FRAME_NUM 240   // about 90 ms
#endif

// The INMP441 is a 24-bit part that clocks 32-bit slots, and full-duplex I2S
// requires both directions to agree on frame size — so playback uses 32-bit
// slots too, with the 16-bit sample sitting at the top.
#define AUDIO_SLOT_BITS       32

// Digital gain applied to captured samples. The INMP441 is quiet: its full
// scale is 120 dB SPL, so ordinary speech at a metre lands far down the range.
#define AUDIO_MIC_GAIN        4

// --- Voice Configuration ---
#define VOICE_WAKE_WORD       "Hey Marvin"

// How long to keep streaming before giving up on a turn the backend never
// closed. A backstop, not the usual path — normally the provider's own turn
// detection ends it.
//
// Generous, because in push-to-talk this is also how long a conversation may
// run before the robot stops listening on its own. Too short and it cuts you
// off mid-sentence; too long and a forgotten open microphone stays open.
#define VOICE_TURN_TIMEOUT_MS 30000

#endif // MARVIN_VOICE

#endif // CONFIG_H
