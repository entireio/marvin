# Marvin

**An open source, 3D-printable, tracked robot built around the ESP32.**

Marvin is a small tank-tracked robot with an expressive two-axis head. It is
designed to be reproducible by anyone with a 3D printer and a soldering iron:
every part of the robot — firmware, electronics, mechanics, and the software you
drive it with — lives in this repository under an open licence.

> **Project status: early prototype.** The drivetrain, head, and Bluetooth
> control link work today. Sensing, the custom PCB, and the eye displays are in
> progress. Interfaces will change without notice until the first tagged release.

---

## Contents

- [What Marvin does](#what-marvin-does)
- [Repository map](#repository-map)
- [Quick start](#quick-start)
- [Hardware overview](#hardware-overview)
- [Control protocol](#control-protocol)
- [Roadmap](#roadmap)
- [Contributing](#contributing)
- [Licence](#licence)

---

## What Marvin does

| Subsystem | Status | Notes |
| --- | --- | --- |
| **Drivetrain** — two DC motors, tank tracks | ✅ Working | DRV8833 dual H-bridge, PWM speed control |
| **Head** — 2-axis pan/tilt | ✅ Working | Two SG90-class servos, smoothed motion |
| **Connectivity** — Bluetooth Low Energy | ✅ Working | Nordic UART Service, browser-controllable |
| **Demo mode** — autonomous exploration loop | ✅ Working | Scripted state machine, see `firmware/src/demo.cpp` |
| **Voice** — ask a question, get an answer | ✅ Working | Both boards: microphone, speaker, Wi-Fi, a backend |
| **Wake word** — "Hey Marvin" | 🚧 In progress | S3 only, eventually. Start a conversation from the controller meanwhile |
| **Gestures** — the nod that says "I'm listening" | ✅ Working | `firmware/src/gestures.cpp` |
| **Odometry** — wheel encoders | 🚧 Planned | For closed-loop speed and distance |
| **Distance sensing** — time-of-flight | 🚧 Planned | Obstacle avoidance |
| **Social sensing** — infrared | 🚧 Planned | Marvin-to-Marvin detection and interaction |
| **Expression** — two round displays as eyes | 🚧 Planned | |
| **Talking over Marvin** | 🚧 Planned | Needs acoustic echo cancellation |
| **Intents** — "take a note", "turn around" | 🚧 Planned | The backend harness has the seam for it |
| **Custom PCB** | 🚧 Planned | Replaces the current breakout-board wiring |

---

## Repository map

```
marvin/
├── firmware/       C++ firmware for the on-board ESP32 (PlatformIO / Arduino)
├── controller/     Web Bluetooth app for driving the robot from a browser
├── electronics/    Schematics, PCB layout, BOM, power and battery design
├── mechanical/     CAD source and printable STLs for the chassis
├── docs/           Source for the project website (GitHub Pages)
├── server/         The backend: the robot's endpoint, the controller, the website
└── README.md       You are here
```

Each folder has its own `README.md` with build instructions and conventions.
Start there before changing anything inside it.

| Folder | Read this first | Toolchain |
| --- | --- | --- |
| [`firmware/`](firmware/README.md) | Pin map, build and flash, adding a command | PlatformIO |
| [`controller/`](controller/README.md) | Setting a robot up, browser support | Any static file server |
| [`electronics/`](electronics/README.md) | Board plan, power budget, BOM format | KiCad (planned) |
| [`mechanical/`](mechanical/README.md) | Part list, print settings, large-file policy | Rhino 3D, any slicer |
| [`docs/`](docs/README.md) | Website plan and deployment | Any static file server |
| [`server/`](server/README.md) | Voice architecture, providers, config | Go, Cloud Run or ECS |

> **Note on naming:** `controller/` is the app you *set up and drive the robot
> with*. `docs/` is the *project website*. `server/` serves both, and is also
> what the robot itself connects to when it talks.

---

## Quick start

### 1. Flash the firmware

Requires [PlatformIO](https://platformio.org/install/cli).

```bash
cd firmware && ./flash.sh
```

Then open a serial monitor at **115200 baud** (`pio device monitor`) — you should
get a `Marvin>` prompt.

### 2. Drive it from a browser

Requires Chrome or Edge (Web Bluetooth is not available in Firefox or Safari).

```bash
cd controller && ./serve.sh
```

Open <http://localhost:3000>, click **Scan**, and pick `Marvin` from the device
picker. Web Bluetooth requires either `localhost` or HTTPS — this is why a file
opened directly with `file://` will not work.

### 3. Give it a voice

Needs the S3 build, a microphone and a speaker
([`electronics/README.md`](electronics/README.md)), and a backend for the robot
to talk to.

**On your own machine, with Marvin on the same Wi-Fi** — no cloud account, no
GitHub OAuth app, no certificate:

```bash
export OPENAI_API_KEY=sk-...     # or GEMINI_API_KEY
./run_local.sh
```

Then open <http://localhost:8080/app/> and follow **Setup**. It prints the
address to give the robot — your machine's LAN address, which the setup form
fills in for you.

Open it at **`localhost`, not your LAN address**. Web Bluetooth only runs in a
secure context, which means HTTPS or localhost, and Scan fails anywhere else.
That is a browser rule, not something this project can work around.

**In the cloud**, when you want it to work from outside the house:

```bash
./deploy_google_cloud.sh --set-openai-key    # or ./deploy_aws.sh
```

Either way, **Setup** writes the Wi-Fi credentials and a device token to the
robot over an encrypted Bluetooth link. Power-cycle it, and ask it something.

### 4. Print the chassis

STLs are in [`mechanical/stl/`](mechanical/stl). See
[`mechanical/README.md`](mechanical/README.md) for the part list, orientation,
and print settings.

---

## Hardware overview

### Two builds

| | ESP32-C3 SuperMini | ESP32-S3 SuperMini |
| --- | --- | --- |
| PlatformIO environment | `esp32c3-supermini` | `esp32s3-supermini` |
| Drive, head, BLE, demo | ✅ | ✅ |
| Voice | ✅ | ✅ |
| Wake word | — push-to-talk | planned |
| Talking over a reply | — | planned |
| Module | 4 MB flash, no PSRAM | ESP32-S3FH4R2: 4 MB flash, 2 MB PSRAM |

**Both boards hold a conversation.** What the C3 cannot do is listen for its own
name — no PSRAM and no vector unit means nowhere to run a keyword model — so on
that board you press **Start listening** in the web controller instead. The S3
is where "Hey Marvin" and talking over a reply will land; both need the PSRAM.
Nothing else differs.

| | |
| --- | --- |
| **Framework** | Arduino, on ESP-IDF 5.x via the pioarduino platform |
| **Motor driver** | DRV8833 dual H-bridge |
| **Servos** | 2 × SG90-class (pan + tilt) |
| **Radio** | On-chip BLE via NimBLE; Wi-Fi on the S3 |
| **Microphone** | INMP441, I²S |
| **Amplifier** | MAX98357A, I²S — sharing one peripheral with the microphone |

### Pin assignment

The single source of truth is [`firmware/src/boards/`](firmware/src/boards) —
change pins there, never inline in the drivers. Full wiring, including the parts
that are not pin numbers, is in
[`electronics/README.md`](electronics/README.md).

| Function | C3 | S3 |
| --- | --- | --- |
| Servo — tilt | 1 | 1 |
| Servo — pan/rotation | 4 | 2 |
| Motor A — IN1 / IN2 | 5 / 6 | 4 / 5 |
| Motor B — IN3 / IN4 | 20 / 10 | 6 / 7 |
| I²S BCLK / WS | 3 / 0 | 15 / 16 |
| I²S data in / out | 21 / 7 | 17 / 18 |
| Amplifier enable | — (see below) | 21 |

The C3 has no pin left for the amplifier's enable input: the only candidate is
GPIO 2, which must read HIGH at reset or the chip will not boot. Leave it
unconnected there and accept a little idle hiss — a robot that will not start is
the worse problem. [`electronics/README.md`](electronics/README.md) has the
wiring if you want it anyway.

Motor B's inputs are wired inverted on both boards, so a positive speed on both
channels drives forward without per-side sign correction in the firmware.

Motor pins are driven LOW at the very start of `setup()`, before anything else,
so the tracks do not lurch while the GPIOs are still floating at boot. Keep it
that way when you add peripherals.

---

## Control protocol

Marvin speaks one simple line-based text protocol over **both** the USB serial
port and BLE. The BLE transport is the Nordic UART Service (NUS), the de-facto
standard for serial-over-Bluetooth:

| UUID | Role |
| --- | --- |
| `6E400001-B5A3-F393-E0A9-E50E24DCCA9E` | Service |
| `6E400002-…` | RX — client writes commands here |
| `6E400003-…` | TX — robot notifies responses here |
| `6E400004-…` | Provisioning — scan, Wi-Fi and the backend token, encrypted link only |

Commands are case-insensitive, one per line:

| Command | Range | Meaning |
| --- | --- | --- |
| `R<angle>` | 0–180 | Set head rotation (pan), e.g. `R90` |
| `T<angle>` | 30–85 | Set head tilt, e.g. `T45` |
| `M<speed>` | −255–255 | Both motors, e.g. `M128` |
| `A<speed>` | −255–255 | Motor A only |
| `B<speed>` | −255–255 | Motor B only |
| `G<name>` | — | Play a gesture: `wake_ack`, `nod`, `shake`, `centre` |
| `S` | — | Stop all motors |
| `D` | — | Toggle demo mode |
| `W` | — | Toggle listening. `W1` starts, `W0` stops and asks for the reply |
| `L` | — | Microphone-to-speaker loopback (voice builds) |
| `?` | — | Network, backend and voice status (voice builds) |

Any movement command automatically cancels demo mode. Angles and speeds are
clamped to the limits in `config.h` rather than rejected.

Responses are ANSI-coloured on serial and stripped to plain text for BLE — see
`respond()` in [`firmware/src/main.cpp`](firmware/src/main.cpp). If you add a
command, add it in `processCommand()` and it works over both transports for
free; then update the help text in `main.cpp`, the cheat sheet in
`controller/index.html`, and the table above.

---

## Roadmap

1. **"Hey Marvin" on the robot** — the conversation path works today on both
   boards, started from the controller. What is missing is a wake word model
   small enough for the S3 and free to train.
2. **Talking over Marvin** — acoustic echo cancellation, so the microphone can
   stay open while the speaker is playing.
3. **Intents** — "take a note", "turn around". The backend already has the seam:
   a handler declares a tool, the model calls it, the handler acts.
4. **Custom PCB** — collapse the breakout-board wiring into one board with
   proper power distribution and a battery charger.
5. **Closed-loop drive** — wheel encoders for straight lines and known distances.
6. **Sensing** — time-of-flight for obstacles, IR for spotting other Marvins.
7. **Eyes** — two round displays and an expression system.

---

## Contributing

Contributions are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md) for the
development setup, coding conventions, and the large-binary-file policy (this
repository uses Git LFS for CAD).

The short version:

```bash
git clone https://github.com/spedemon/marvin.git
cd marvin
git lfs install && git lfs pull
```

---

## Licence

Marvin is dual-licensed, which is normal for open hardware:

- **Software** — `firmware/`, `controller/`, `server/`, `docs/` — under the
  [MIT Licence](LICENSE).
- **Hardware** — `electronics/`, `mechanical/` — under the
  [CERN Open Hardware Licence v2, Strongly Reciprocal](LICENSE-hardware)
  (CERN-OHL-S-2.0). If you make and distribute a modified Marvin, share your
  design changes too.

See [LICENSE](LICENSE) and [LICENSE-hardware](LICENSE-hardware) for the full
terms.
