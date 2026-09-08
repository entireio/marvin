#include <Arduino.h>
#include "config.h"
#include "motor.h"
#include "head_servos.h"
#include "demo.h"
#include "ble_serial.h"

// ANSI color codes for terminal
#define ANSI_COLOR_RED     "\x1b[31m"
#define ANSI_COLOR_GREEN   "\x1b[32m"
#define ANSI_COLOR_YELLOW  "\x1b[33m"
#define ANSI_COLOR_BLUE    "\x1b[34m"
#define ANSI_COLOR_MAGENTA "\x1b[35m"
#define ANSI_COLOR_CYAN    "\x1b[36m"
#define ANSI_COLOR_RESET   "\x1b[0m"
#define ANSI_CLEAR_SCREEN  "\x1b[2J\x1b[H"

// --- Peripherals ---
Motor motorA(PIN_MOTOR_A_IN1, PIN_MOTOR_A_IN2);
Motor motorB(PIN_MOTOR_B_IN3, PIN_MOTOR_B_IN4);
HeadServos head;
Demo demo(motorA, motorB, head);
BleSerial bleSerial;

String commandStr = "";

// ---------------------------------------------------------------------------
// Dual-output helpers: Serial + BLE
// ---------------------------------------------------------------------------
// BLE clients don't understand ANSI escape codes, so we strip them for BLE
// and send the raw text. Serial keeps the coloured output.

void respond(const char* text) {
    Serial.print(text);
    if (bleSerial.isConnected()) {
        // Strip ANSI escape sequences for BLE output
        String clean;
        const char* p = text;
        while (*p) {
            if (*p == '\x1b') {
                // Skip the escape sequence (ESC [ ... letter)
                p++;
                if (*p == '[') {
                    p++;
                    while (*p && !((*p >= 'A' && *p <= 'Z') || (*p >= 'a' && *p <= 'z'))) p++;
                    if (*p) p++; // skip the final letter
                }
            } else {
                clean += *p;
                p++;
            }
        }
        bleSerial.send(clean);
    }
}

// printf-style variant
void respondf(const char* fmt, ...) {
    char buf[256];
    va_list args;
    va_start(args, fmt);
    vsnprintf(buf, sizeof(buf), fmt, args);
    va_end(args);
    respond(buf);
}

// Callback for BLE-received commands (same path as Serial)
void onBleCommand(const String& cmd);

void printPrompt() {
    Serial.print(ANSI_COLOR_GREEN "Marvin> " ANSI_COLOR_RESET);
    // Don't send prompt over BLE — the web terminal has its own prompt
}

void processCommand(String cmd);

void setup() {
    // Motor pins driven LOW first to prevent spin from floating GPIOs
    motorA.begin();
    motorB.begin();

    Serial.begin(115200);

    head.begin();

    // Brief pause so the serial connection is ready before clearing screen
    delay(100);

    Serial.print(ANSI_CLEAR_SCREEN);
    Serial.print(ANSI_COLOR_CYAN "=========================================\r\n" ANSI_COLOR_RESET);
    Serial.print(ANSI_COLOR_GREEN "        Marvin Robot Initialized!        \r\n" ANSI_COLOR_RESET);
    Serial.print(ANSI_COLOR_CYAN "=========================================\r\n" ANSI_COLOR_RESET);
    Serial.print("Commands:\r\n");
    Serial.print("  " ANSI_COLOR_YELLOW "R<angle>" ANSI_COLOR_RESET " : Set Rotation (0-180) e.g., R90\r\n");
    Serial.print("  " ANSI_COLOR_YELLOW "T<angle>" ANSI_COLOR_RESET " : Set Tilt (0-180) e.g., T45\r\n");
    Serial.print("  " ANSI_COLOR_YELLOW "M<speed>" ANSI_COLOR_RESET " : Both motors (-255..255) e.g., M128\r\n");
    Serial.print("  " ANSI_COLOR_YELLOW "A<speed>" ANSI_COLOR_RESET " : Motor A only (-255..255)\r\n");
    Serial.print("  " ANSI_COLOR_YELLOW "B<speed>" ANSI_COLOR_RESET " : Motor B only (-255..255)\r\n");
    Serial.print("  " ANSI_COLOR_YELLOW "S"        ANSI_COLOR_RESET " : Stop all motors\r\n");
    Serial.print("  " ANSI_COLOR_YELLOW "D"        ANSI_COLOR_RESET " : Toggle demo mode\r\n\r\n");

    // Initialise BLE (must be after Serial.begin so log output works)
    bleSerial.begin(BLE_DEVICE_NAME, onBleCommand);

    printPrompt();
}

void processCommand(String cmd) {
    cmd.trim();
    if (cmd.length() < 1) return;

    char type = cmd.charAt(0);

    // Single-character commands
    if (cmd.length() == 1) {
        if (type == 'S' || type == 's') {
            demo.disable();
            motorA.stop();
            motorB.stop();
            respond(ANSI_COLOR_MAGENTA "All motors stopped\r\n" ANSI_COLOR_RESET);
            return;
        }
        if (type == 'D' || type == 'd') {
            if (demo.isEnabled()) {
                demo.disable();
                respond(ANSI_COLOR_CYAN "Demo mode OFF\r\n" ANSI_COLOR_RESET);
            } else {
                demo.enable();
                respond(ANSI_COLOR_CYAN "Demo mode ON\r\n" ANSI_COLOR_RESET);
            }
            return;
        }
    }

    // Commands that take a value
    if (cmd.length() < 2) return;

    // Any manual command disables demo mode
    if (demo.isEnabled()) {
        demo.disable();
        respond(ANSI_COLOR_CYAN "Demo mode auto-disabled\r\n" ANSI_COLOR_RESET);
    }

    int value = cmd.substring(1).toInt();

    if (type == 'R' || type == 'r') {
        int angle = constrain(value, SERVO_ROTATION_MIN, SERVO_ROTATION_MAX);
        head.setRotation((float)angle);
        respondf(ANSI_COLOR_BLUE "Rotation target set to %d degrees\r\n" ANSI_COLOR_RESET, angle);
    }
    else if (type == 'T' || type == 't') {
        int angle = constrain(value, SERVO_TILT_MIN, SERVO_TILT_MAX);
        head.setTilt((float)angle);
        respondf(ANSI_COLOR_BLUE "Tilt target set to %d degrees\r\n" ANSI_COLOR_RESET, angle);
    }
    else if (type == 'M' || type == 'm') {
        int speed = constrain(value, -MOTOR_MAX_SPEED, MOTOR_MAX_SPEED);
        motorA.setSpeed(speed);
        motorB.setSpeed(speed);
        respondf(ANSI_COLOR_MAGENTA "Both motors set to %d\r\n" ANSI_COLOR_RESET, speed);
    }
    else if (type == 'A' || type == 'a') {
        int speed = constrain(value, -MOTOR_MAX_SPEED, MOTOR_MAX_SPEED);
        motorA.setSpeed(speed);
        respondf(ANSI_COLOR_MAGENTA "Motor A set to %d\r\n" ANSI_COLOR_RESET, speed);
    }
    else if (type == 'B' || type == 'b') {
        int speed = constrain(value, -MOTOR_MAX_SPEED, MOTOR_MAX_SPEED);
        motorB.setSpeed(speed);
        respondf(ANSI_COLOR_MAGENTA "Motor B set to %d\r\n" ANSI_COLOR_RESET, speed);
    }
    else {
        respond(ANSI_COLOR_RED "Unknown command. Use R/T/M/A/B/S/D.\r\n" ANSI_COLOR_RESET);
    }
}

void onBleCommand(const String& cmd) {
    processCommand(cmd);
}

void loop() {
    // Process serial input
    while (Serial.available()) {
        char c = Serial.read();

        if (c == '\r' || c == '\n') {
            Serial.print("\r\n");
            if (commandStr.length() > 0) {
                processCommand(commandStr);
                commandStr = "";
            }
            printPrompt();
        } else if (c == '\b' || c == 127) {
            if (commandStr.length() > 0) {
                commandStr.remove(commandStr.length() - 1);
                Serial.print("\b \b");
            }
        } else if (c >= 32 && c <= 126) {
            commandStr += c;
            Serial.print(c);
        }
    }

    // Advance demo state machine (no-op when disabled)
    demo.update();

    // Step servos toward their targets
    head.update();
}
