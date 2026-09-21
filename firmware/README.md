# Marvin — Firmware

C++ firmware for the on-board ESP32, built with [PlatformIO](https://platformio.org)
against the Arduino framework.

---

## Build and flash

```bash
./build.sh                                  # compile both targets
./flash.sh                                  # compile and upload over USB
pio run -e esp32s3-supermini -t upload      # one target only
pio device monitor                          # serial console at 115200 baud
```

Both scripts are one-line wrappers around `pio run` and `pio run -t upload`, so
plain PlatformIO commands work identically. With two environments defined, a
bare `pio run -t upload` will try to flash both — name the one you want.

After flashing, the serial console gives you a `Marvin>` prompt with a command
summary. Bluetooth advertising starts automatically under the name `Marvin`.

---

## Target boards

Two environments, one source tree:

```ini
[env:esp32c3-supermini]   board = lolin_c3_mini          ; voice, no wake word
[env:esp32s3-supermini]   board = esp32-s3-devkitc-1     ; voice, and room for one
```

Neither board has an upstream PlatformIO definition, so each borrows a
compatible one. The S3 entry then narrows it to the SuperMini's ESP32-S3FH4R2
module — 4 MB of flash with 2 MB of *quad* PSRAM, which is what
`board_build.arduino.memory_type = qio_qspi` selects. Getting that wrong gives a
board that boots and then finds no PSRAM.

**Both boards do voice.** What the C3 cannot do is listen for its own name: no
PSRAM and no vector unit means nowhere to run a keyword model, so
`VOICE_PUSH_TO_TALK` is set for that board and conversations are started by hand
— from the web controller, or with `W`. The S3 is where a wake word will land,
along with the echo cancellation that allows talking over a reply. Nothing else
differs: same drivers, same protocol, same backend.

Two things the C3 forces that are worth knowing about, both handled in
`config.h`:

- **It is single-core.** `AUDIO_TASK_CORE` is core 1 on the S3 and core 0 on the
  C3, because asking for a core that does not exist fails and the task never
  starts. On one core the task priorities are what keep audio ahead of the
  network.
- **It has 400 KB of SRAM**, which has to hold Wi-Fi, TLS, Bluetooth and the
  audio buffers at once. The I²S DMA ring is smaller there, which buys less
  slack against jitter.

Earlier prototypes ran on ESP32-C6 boards (`esp32-c6-devkitm-1`,
`nanoESP32-C6-v1711`); those environments are not currently maintained. Nothing
in the source is chip-specific — pin maps live in `src/boards/` — so adding a
board means adding an `[env:…]` block and a matching header, not editing
drivers.

### Why the pioarduino platform

```ini
platform = https://github.com/pioarduino/platform-espressif32/releases/download/55.03.311/platform-espressif32.zip
```

Not `platformio/espressif32`. That one is still shipping Arduino 2.0.17 on
ESP-IDF 4.4, whose only I²S driver is the deprecated one and which has no route
to the speech components at all. pioarduino tracks Espressif's own Arduino 3.x
releases on IDF 5.x, which brings the three things this project needs: the
`i2s_std` full-duplex driver, `esp-sr` (wake word, and later the acoustic echo
cancellation that talking over Marvin depends on), and `esp-tflite-micro`.

It is pinned to an exact release. The Arduino API surface the drive and head
code uses is unchanged across the move.

### Dependencies

Resolved by PlatformIO:

- `madhephaestus/ESP32Servo` — servo PWM
- `h2zero/NimBLE-Arduino` — the BLE stack (considerably smaller than the stock Arduino BLE library)

S3 only:

- `links2004/WebSockets` — the link to the backend
- `bblanchon/ArduinoJson` — control messages

---

## Source layout

```
src/
├── boards/            One header per board. Pins and nothing else.
├── config.h           Selects a board, then every tuning constant.
├── main.cpp           Setup, command parser, main loop, dual serial/BLE output
├── motor.h/.cpp       One DC motor channel on a DRV8833 H-bridge
├── head_servos.h/.cpp Pan/tilt pair with smooth interpolation
├── gestures.h/.cpp    Named head movements — the wake nod, yes, no
├── ble_serial.h/.cpp  Nordic UART Service over NimBLE, plus provisioning
└── demo.h/.cpp        Scripted autonomous exploration sequence

    voice, on the S3 only (everything below is behind MARVIN_VOICE):
├── audio_io.h/.cpp    Full-duplex I²S: microphone in, speaker out
├── wakeword.h         The detector interface, and a placeholder that never fires
├── settings.h/.cpp    Wi-Fi, backend and token, kept in NVS
├── net_wifi.h/.cpp    Joining the network, and rejoining it
├── cloud_link.h/.cpp  The TLS WebSocket to the backend
├── certs.h            Root certificates the robot will accept
└── voice.h/.cpp       Wake, listen, answer, back to waiting
```

### Where the work happens

Three places, and the split matters:

| | |
| --- | --- |
| `voice_capture` | Reads the microphone every 20 ms and runs the wake word detector. A missed deadline here is a gap in what Marvin heard. Core 1 on the S3; core 0 at a higher priority on the single-core C3. |
| `cloud_link` | Owns the socket, beside the Wi-Fi and Bluetooth stacks. Always core 0. |
| **`loop()`** | Servos, motors, gestures, and anything the backend has asked for. |

Nothing arriving off the network moves a servo from a foreign task: control
messages go onto a queue that `loop()` drains. The wake word is the same —
the audio task raises a flag, and `loop()` plays the nod.

### `config.h` and `boards/`

`boards/` holds one header per board and nothing but pins. `config.h` picks one
from the `MARVIN_BOARD_*` flag the environment defines, and then holds every
other tuning constant — servo limits, PWM settings, speed presets, audio format.

**Never hard-code a pin or a magic angle inside a driver.** Put it here and
reference the macro. This is what makes retargeting a config change rather than
a rewrite.

| Function | C3 | S3 |
| --- | --- | --- |
| Servo — tilt | 1 | 1 |
| Servo — pan/rotation | 4 | 2 |
| Motor A — IN1 / IN2 | 5 / 6 | 4 / 5 |
| Motor B — IN3 / IN4 | 20 / 10 | 6 / 7 |
| I²S BCLK / WS | — | 15 / 16 |
| I²S data in / out | — | 17 / 18 |
| Amplifier enable | — | 21 |

Wiring, including the parts that are not pin numbers, is in
[`electronics/README.md`](../electronics/README.md).

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

Provisioning — scanning, Wi-Fi credentials and the backend token — goes through
a fourth characteristic, `6E400004-…`, declared `WRITE_ENC` so the link must be
encrypted.

| | |
| --- | --- |
| `Q` | scan for networks in range |
| `N<ssid>\|<password>` | save credentials **and connect straight away** |
| `U<url>` | backend address |
| `K<token>` | device token |
| `I<id>` | device name |
| `X!` | forget all of the above |

`N` connects immediately rather than waiting for the next backoff tick, because
somebody has just pressed a button and is watching for the result. A separate characteristic rather than more commands on RX, because
anyone with a radio can read an unencrypted BLE write from the next room, and
because writing to a characteristic that requires encryption is what makes the
browser and the operating system pair in the first place.

Pairing is Just Works: Marvin has no display and no keypad, so there is no way
to show or check a passkey. That is open to an attacker present at the moment of
pairing and protects everything afterwards — the right trade for a robot set up
once, indoors.

### `net_wifi.h`

Joining the network, rejoining it, and **saying out loud what is happening**.
That second half is not decoration: provisioning fails for dull reasons — a typo
in the password, a 5 GHz-only network, one simply out of range — and a robot
that reports "not connected" leaves you guessing which. Every state change
becomes a line on the serial console and, through the same `respond()` path, in
the web controller:

```
[WIFI] scanning
[NET]  -47|secure|MyNetwork          one per network found
[WIFI] scan_done 6
[WIFI] connecting MyNetwork
[WIFI] connected 192.168.1.15 -55
[WIFI] failed auth                   auth | notfound | timeout | other
[WIFI] retry 4000
[WIFI] disconnected
```

The controller picks these out of the terminal stream by prefix. `[NET]` lines
feed the network dropdown and are not printed — two dozen of them would bury
everything else — while `[WIFI]` lines are shown, because they are what you want
to see when it is not working.

The driver's numeric disconnect reason is collapsed to those four words. The
distinction that matters to somebody standing at the robot is "your password is
wrong" versus "that network is not here"; the rest is noise.

Scanning is asynchronous and polled from `update()`, because a blocking scan
takes three seconds during which the robot would answer neither Bluetooth nor
its own servos. It is refused mid-connect — scanning takes the radio off the
attempt and would fail it for a reason that has nothing to do with the
credentials.

### `demo.h`

A table of `{motorA, motorB, headRotation, headTilt, durationMs}` steps that
loops. Adding a behaviour means adding rows to `_sequence[]` in `demo.cpp` — no
control-flow changes needed.

### `gestures.h`

Short, named head movements that mean something to a person watching. Marvin has
no display, so a gesture is the only way it can say "I heard you". Same table
shape as the demo, and the same reason: adding one is data, not control flow.

`wake_ack` is the one that matters — a dip forward, a glance up, and back to
neutral. It fires the instant the wake word is recognised, locally, before any
network round trip. That is the difference between a robot that feels attentive
and one that feels laggy.

---

## Voice

Behind `MARVIN_VOICE`, on the S3 only.

### `audio_io.h`

One I²S peripheral in full duplex: the INMP441 and the MAX98357A share BCLK and
WS, and only the data lines are separate. Sharing the clock is what forces
capture and playback to the same rate, which is why the whole robot runs at
16 kHz and the backend resamples for whichever AI service is in use.

The bus carries 32-bit stereo slots — the INMP441 is a 24-bit part that needs
them, and full duplex requires both directions to agree on frame size.
Everything above this class deals in 16-bit mono.

Send `L` over serial or Bluetooth to pipe the microphone straight to the
speaker. It is the fastest way to tell a dead microphone from a dead backend,
which is why it stays in the firmware rather than living in a test sketch.

### `wakeword.h`

An interface with a placeholder behind it. Which engine ends up there is the
least settled decision in this project — `esp-sr`'s WakeNet needs a phrase
Espressif has trained, a custom microWakeWord model has to be trained and then
made to run outside ESPHome, and there is always the fallback of gating on
speech and confirming the phrase in the backend.

Until one lands — and permanently on the C3 — `NullWakeWord` never fires and a
conversation is started by hand:

| | |
| --- | --- |
| `W` | toggle: start listening, or stop and ask for the reply |
| `W1` | start listening |
| `W0` | stop listening and ask for the reply |

The web controller uses `W1` and `W0` rather than the toggle, because a toggle
desynchronises the moment one message goes missing and then the button says the
opposite of what the robot is doing. They arrive as `act` messages from the
backend and go through the same `processCommand()` as the serial console.

`W0` is the more interesting half. It says the person has finished speaking,
which is the cue the AI service would otherwise have to guess at from silence —
so pressing stop gets an answer immediately rather than after the service's own
silence window.

### `cloud_link.h`

One TLS WebSocket carrying the whole conversation: binary frames are audio, text
frames are JSON. Held open between conversations rather than dialled per
question, because a TLS handshake takes about as long as a person expects an
answer to take.

Certificates are verified against the roots in `certs.h`. This is not a
formality — the robot presents a bearer token that authorises a live microphone
in someone's home, and without verification anyone on the same network could
accept that connection and keep the token.

### `voice.h`

Idle → Listening → Thinking → Speaking → Idle. *Thinking* is the gap between
"I have finished speaking" and the first audio of the reply; it exists so the
robot stops sending the moment the turn closes, and so a reply that never
arrives cannot leave it stuck.

While Marvin is speaking it keeps reading the microphone and throws the audio
away: the microphone is centimetres from the
speaker with no echo canceller between them, so anything captured then is mostly
Marvin. That is also why there is no talking over him yet.

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
