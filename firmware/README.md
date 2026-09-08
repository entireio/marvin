# Marvin — Firmware

C++ firmware for the on-board ESP32, built with [PlatformIO](https://platformio.org)
against the Arduino framework.

---

## Build and flash

```bash
./build.sh          # compile only
./flash.sh          # compile and upload over USB
pio device monitor   # serial console at 115200 baud
```

Both scripts are one-line wrappers around `pio run` and `pio run -t upload`, so
plain PlatformIO commands work identically.

After flashing, the serial console gives you a `Marvin>` prompt with a command
summary. Bluetooth advertising starts automatically under the name `Marvin`.

---

## Target board

`platformio.ini` currently defines a single environment:

```ini
[env:esp32-super-mini]
board = lolin_c3_mini    ; ESP32-C3 SuperMini
```

Earlier prototypes ran on ESP32-C6 boards (`esp32-c6-devkitm-1`,
`nanoESP32-C6-v1711`). Nothing in the source is C3-specific — the pin map is
isolated in `config.h` — so adding another board means adding another `[env:…]`
block and a matching pin section, not editing drivers.

Dependencies are resolved by PlatformIO:

- `madhephaestus/ESP32Servo` — servo PWM
- `h2zero/NimBLE-Arduino` — the BLE stack (considerably smaller than the stock Arduino BLE library)

---

## Source layout

```
src/
├── config.h          Every pin number and tuning constant. Single source of truth.
├── main.cpp          Setup, command parser, main loop, dual serial/BLE output
├── motor.h/.cpp      One DC motor channel on a DRV8833 H-bridge
├── head_servos.h/.cpp  Pan/tilt pair with smooth interpolation
├── ble_serial.h/.cpp   Nordic UART Service over NimBLE
└── demo.h/.cpp       Scripted autonomous exploration sequence
```

### `config.h`

All GPIO assignments, servo limits, PWM settings, and speed presets live here.
**Never hard-code a pin or a magic angle inside a driver** — put it here and
reference the macro. This is what makes retargeting to a different ESP32 board
a config change rather than a rewrite.

| Function | GPIO |
| --- | --- |
| Servo — tilt | 1 |
| Servo — pan/rotation | 4 |
| Motor A — IN1 / IN2 | 5 / 6 |
| Motor B — IN3 / IN4 | 20 / 10 (wired inverted) |

Servo travel is deliberately clamped below the full 0–180° range on tilt
(`SERVO_TILT_MIN` 30°, `SERVO_TILT_MAX` 85°) to stay inside the head's
mechanical limits. Do not widen these without checking the CAD.

### `motor.h`

`Motor` wraps one DRV8833 channel. `setSpeed(-255..255)` where negative is
reverse and `0` coasts. The driver has no enable pin, so both inputs LOW is the
idle state.

### `head_servos.h`

`HeadServos` holds a target and a current angle per axis and walks the current
value toward the target by `SERVO_STEP_SIZE` degrees every
`SERVO_UPDATE_INTERVAL_MS`. This is why head motion looks organic rather than
snapping. Call `update()` every loop iteration; it rate-limits internally.

### `ble_serial.h`

Implements the Nordic UART Service, the de-facto standard for
serial-over-Bluetooth, so any generic BLE terminal can talk to Marvin — not
just this project's own controller app.

| UUID | Role |
| --- | --- |
| `6E400001-B5A3-F393-E0A9-E50E24DCCA9E` | Service |
| `6E400002-…` | RX — client writes commands |
| `6E400003-…` | TX — robot notifies responses |

Incoming writes are line-buffered and dispatched to the same
`processCommand()` used by the serial console. Outgoing text is fragmented into
200-byte chunks, a conservative size that works before MTU negotiation.

### `demo.h`

A table of `{motorA, motorB, headRotation, headTilt, durationMs}` steps that
loops. Adding a behaviour means adding rows to `_sequence[]` in `demo.cpp` — no
control-flow changes needed.

---

## Conventions

- **`loop()` never blocks.** Every periodic behaviour is a millis-based state
  machine (`Demo::update()`, `HeadServos::update()`). No `delay()` in the loop —
  it would stall BLE and the command console.
- **Motor pins go LOW first in `setup()`,** before `Serial.begin()` and before
  anything else. ESP32 GPIOs float during boot, and a floating H-bridge input
  makes the tracks jump. Keep this ordering.
- **Use `respond()` / `respondf()`, not `Serial.print()`,** for anything a user
  should see. They mirror output to BLE and strip ANSI colour codes on the way,
  since BLE terminals render escape sequences as garbage.
- One class per peripheral, in its own `.h`/`.cpp` pair.

---

## Adding a command

Commands are single-letter, optionally followed by an integer, and are parsed in
exactly one place. To add one, touch these four spots:

1. `processCommand()` in `main.cpp` — the parsing and the action
2. The help block in `setup()` in `main.cpp` — the serial cheat sheet
3. `controller/index.html` — the cheat sheet shown in the web app
4. The protocol table in the root [`README.md`](../README.md)

Because both transports funnel through `processCommand()`, a new command works
over serial and BLE with no extra wiring. Clamp inputs with `constrain()`
against the limits in `config.h` rather than rejecting out-of-range values.
