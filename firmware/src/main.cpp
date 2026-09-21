#include <Arduino.h>
#include "config.h"
#include "motor.h"
#include "head_servos.h"
#include "demo.h"
#include "ble_serial.h"
#include "gestures.h"
#include "audio_io.h"
#include "settings.h"
#include "net_wifi.h"
#include "cloud_link.h"
#include "voice.h"

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
Gestures gestures(head);
BleSerial bleSerial;
#ifdef MARVIN_VOICE
AudioIO   audio;
Settings  settings;
NetWiFi   wifi;
CloudLink cloud;
Voice     voice;
#endif

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
#ifdef MARVIN_VOICE
// Callback for writes to the encrypted provisioning characteristic
void onBleProvision(const String& line);

// Wi-Fi state changes and scan results, to whoever is watching.
//
// Through respond(), so they reach the serial console and the web controller
// alike — provisioning fails for dull reasons and the person holding the robot
// should not have to guess which one.
void onNetEvent(const String& line) {
    respond((line + "\r\n").c_str());
}
#endif

void printPrompt() {
    Serial.print(ANSI_COLOR_GREEN "Marvin> " ANSI_COLOR_RESET);
    // Don't send prompt over BLE — the web terminal has its own prompt
}

void processCommand(String cmd);

#ifdef MARVIN_VOICE
// Commands arriving from the backend take the same path as serial and
// Bluetooth ones. processCommand takes its argument by value because it trims
// it; this adapter is what bridges that to the handler signature.
static void onVoiceCommand(const String& cmd) { processCommand(cmd); }
#endif

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
    Serial.print("  " ANSI_COLOR_YELLOW "G<name>"  ANSI_COLOR_RESET " : Play a gesture (wake_ack, nod, shake, centre)\r\n");
    Serial.print("  " ANSI_COLOR_YELLOW "D"        ANSI_COLOR_RESET " : Toggle demo mode\r\n");
#ifdef MARVIN_VOICE
    Serial.print("  " ANSI_COLOR_YELLOW "L"        ANSI_COLOR_RESET " : Toggle microphone-to-speaker loopback\r\n");
    Serial.print("  " ANSI_COLOR_YELLOW "W"        ANSI_COLOR_RESET " : Start/stop listening (W1 start, W0 stop)\r\n");
    Serial.print("  " ANSI_COLOR_YELLOW "?"        ANSI_COLOR_RESET " : Show network, backend and voice status\r\n");
    Serial.print("Provisioning (over an encrypted Bluetooth link only):\r\n");
    Serial.print("  " ANSI_COLOR_YELLOW "Q"        ANSI_COLOR_RESET " : Scan for Wi-Fi networks\r\n");
    Serial.print("  " ANSI_COLOR_YELLOW "N<ssid>|<password>" ANSI_COLOR_RESET " : Wi-Fi network, and connect\r\n");
    Serial.print("  " ANSI_COLOR_YELLOW "U<url>"   ANSI_COLOR_RESET " : Backend, e.g. wss://host/v1/device\r\n");
    Serial.print("  " ANSI_COLOR_YELLOW "K<token>" ANSI_COLOR_RESET " : Device token from the web controller\r\n");
    Serial.print("  " ANSI_COLOR_YELLOW "I<id>"    ANSI_COLOR_RESET " : Device name\r\n");
    Serial.print("  " ANSI_COLOR_YELLOW "X!"       ANSI_COLOR_RESET " : Forget everything above\r\n");
#endif
    Serial.print("\r\n");

#ifdef MARVIN_VOICE
    settings.begin();

    // Before BLE: both want memory, and a failure here should be visible in the
    // log rather than buried under the Bluetooth stack's own output.
    if (!audio.begin()) {
        Serial.print(ANSI_COLOR_RED "Audio failed to start — voice is unavailable\r\n" ANSI_COLOR_RESET);
    }

    wifi.begin(settings, onNetEvent);

    // The link is started whether or not Wi-Fi is up yet: it retries on its own,
    // and starting it here means a robot that is provisioned but out of range
    // connects by itself the moment the network comes back.
    if (settings.haveCloud()) {
        cloud.begin(settings, Voice::handleCloudAudio);
        voice.begin(settings, audio, cloud, gestures, wifi, onVoiceCommand);
    } else {
        Serial.print(ANSI_COLOR_YELLOW
                     "Not provisioned — connect the web controller over Bluetooth to set up Wi-Fi\r\n"
                     ANSI_COLOR_RESET);
    }
#endif

    // Initialise BLE (must be after Serial.begin so log output works)
#ifdef MARVIN_VOICE
    bleSerial.begin(settings.deviceId().c_str(), onBleCommand, onBleProvision);
#else
    bleSerial.begin(BLE_DEVICE_NAME, onBleCommand);
#endif

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
#ifdef MARVIN_VOICE
        if (type == 'L' || type == 'l') {
            if (audio.loopbackRunning()) {
                audio.stopLoopback();
                respond(ANSI_COLOR_CYAN "Audio loopback OFF\r\n" ANSI_COLOR_RESET);
            } else if (audio.startLoopback()) {
                respond(ANSI_COLOR_CYAN "Audio loopback ON — speak into the microphone\r\n" ANSI_COLOR_RESET);
            } else {
                respond(ANSI_COLOR_RED "Audio loopback failed to start\r\n" ANSI_COLOR_RESET);
            }
            return;
        }
#endif
#ifdef MARVIN_VOICE
        if (type == 'W' || type == 'w') {
            // Bare W toggles, which is what a person at a terminal wants.
            // W1 and W0 are explicit, which is what the web controller wants —
            // a toggle desynchronises the moment one message goes missing.
            switch (voice.state()) {
            case Voice::State::Idle:      voice.startListening(); break;
            case Voice::State::Listening: voice.stopListening();  break;
            default:                      voice.cancel();         break;
            }
            return;
        }
        if (type == '?') {
            respond(settings.summary().c_str());
            respond(wifi.summary().c_str());
            respond(cloud.summary().c_str());
            respond(voice.summary().c_str());
            return;
        }
#endif
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
#ifdef MARVIN_VOICE
    else if (type == 'W' || type == 'w') {
        if (value == 1) {
            voice.startListening();
        } else if (value == 0) {
            voice.stopListening();
        } else {
            respond(ANSI_COLOR_RED "Use W1 to start listening, W0 to stop.\r\n" ANSI_COLOR_RESET);
        }
    }
#endif
    else if (type == 'G' || type == 'g') {
        String name = cmd.substring(1);
        name.trim();
        // Gestures fight the demo sequence for the head, and the demo wins by
        // rewriting the targets on its next step — so cancel it, the same way
        // every other manual command does.
        if (gestures.play(name.c_str())) {
            respondf(ANSI_COLOR_BLUE "Gesture \"%s\"\r\n" ANSI_COLOR_RESET, name.c_str());
        } else {
            respondf(ANSI_COLOR_RED "Unknown gesture \"%s\". Try wake_ack, nod, shake, centre.\r\n"
                     ANSI_COLOR_RESET, name.c_str());
        }
    }
    else {
        respond(ANSI_COLOR_RED "Unknown command. Use R/T/M/A/B/G/S/D.\r\n" ANSI_COLOR_RESET);
    }
}

void onBleCommand(const String& cmd) {
    processCommand(cmd);
}

#ifdef MARVIN_VOICE
// Writes to the provisioning characteristic. Only reachable over an encrypted,
// bonded link — see ble_serial.cpp.
//
// Replies deliberately never echo what was written. The values coming through
// here are a Wi-Fi password and a bearer token, and a terminal that repeats
// them back is a terminal someone will paste into a bug report.
void onBleProvision(const String& line) {
    if (line.length() < 1) return;
    char type = line.charAt(0);
    String value = line.substring(1);
    value.trim();

    switch (type) {
    case 'Q': case 'q':
        // List the networks in range, so the controller can offer them rather
        // than asking someone to type a name exactly right.
        wifi.startScan();
        return;

    case 'N': case 'n': {
        int bar = value.indexOf('|');
        if (bar < 0) {
            respond(ANSI_COLOR_RED "Expected N<ssid>|<password>\r\n" ANSI_COLOR_RESET);
            return;
        }
        settings.setWiFi(value.substring(0, bar), value.substring(bar + 1));
        // Connect straight away rather than waiting for the next backoff tick:
        // somebody just pressed a button and is watching for the result.
        wifi.reconnectNow();
        return;
    }
    case 'U': case 'u':
        if (!value.startsWith("ws://") && !value.startsWith("wss://")) {
            respond(ANSI_COLOR_RED "Backend URL must start with wss:// or ws://\r\n" ANSI_COLOR_RESET);
            return;
        }
        settings.setCloud(value, "");
        respond(ANSI_COLOR_GREEN "Backend saved; restart Marvin to connect\r\n" ANSI_COLOR_RESET);
        return;

    case 'K': case 'k':
        settings.setCloud("", value);
        respond(ANSI_COLOR_GREEN "Device token saved; restart Marvin to connect\r\n" ANSI_COLOR_RESET);
        return;

    case 'I': case 'i':
        settings.setDeviceId(value);
        respond(ANSI_COLOR_GREEN "Device name saved; restart Marvin to advertise it\r\n" ANSI_COLOR_RESET);
        return;

    case 'X':
        // Confirmation required: this is the one command here that destroys
        // something, and a stray write should not wipe a robot's setup.
        if (value == "!") {
            settings.clear();
            respond(ANSI_COLOR_MAGENTA "All settings cleared; restart Marvin\r\n" ANSI_COLOR_RESET);
        } else {
            respond(ANSI_COLOR_YELLOW "Send X! to confirm clearing every setting\r\n" ANSI_COLOR_RESET);
        }
        return;

    default:
        respond(ANSI_COLOR_RED "Unknown provisioning command. Use Q/N/U/K/I/X!.\r\n" ANSI_COLOR_RESET);
    }
}
#endif

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

    // Advance any gesture in flight (no-op when none is)
    gestures.update();

#ifdef MARVIN_VOICE
    // Network housekeeping and anything the backend has asked for. Both are
    // handled here rather than on their own tasks so that nothing arriving off
    // the network moves a servo from a foreign task.
    wifi.update();
    voice.update();
#endif

    // Step servos toward their targets
    head.update();
}
