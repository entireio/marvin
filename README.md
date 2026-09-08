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
| **Odometry** — wheel encoders | 🚧 Planned | For closed-loop speed and distance |
| **Distance sensing** — time-of-flight | 🚧 Planned | Obstacle avoidance |
| **Social sensing** — infrared | 🚧 Planned | Marvin-to-Marvin detection and interaction |
| **Expression** — two round displays as eyes | 🚧 Planned | |
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
└── README.md       You are here
```

Each folder has its own `README.md` with build instructions and conventions.
Start there before changing anything inside it.

| Folder | Read this first | Toolchain |
| --- | --- | --- |
| [`firmware/`](firmware/README.md) | Pin map, build and flash, adding a command | PlatformIO |
| [`controller/`](controller/README.md) | Running the app, browser support | Any static file server |
| [`electronics/`](electronics/README.md) | Board plan, power budget, BOM format | KiCad (planned) |
| [`mechanical/`](mechanical/README.md) | Part list, print settings, large-file policy | Rhino 3D, any slicer |
| [`docs/`](docs/README.md) | Website plan and deployment | To be decided |

> **Note on naming:** `controller/` is the app you *drive the robot with*.
> `docs/` is the *project website*. They are separate things.

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

### 3. Print the chassis

STLs are in [`mechanical/stl/`](mechanical/stl). See
[`mechanical/README.md`](mechanical/README.md) for the part list, orientation,
and print settings.

---

## Hardware overview

### Current prototype

The firmware currently builds for a single target, defined in
[`firmware/platformio.ini`](firmware/platformio.ini):

| | |
| --- | --- |
| **Board** | ESP32-C3 SuperMini (`lolin_c3_mini`) |
| **Framework** | Arduino |
| **Motor driver** | DRV8833 dual H-bridge |
| **Servos** | 2 × SG90-class (pan + tilt) |
| **Radio** | On-chip BLE via NimBLE |

Earlier prototypes targeted ESP32-C6 boards, and the pin map in `config.h` is
kept deliberately board-agnostic so other ESP32 variants can be added as new
PlatformIO environments.

### Pin assignment

The single source of truth is
[`firmware/src/config.h`](firmware/src/config.h) — change pins there, never
inline in the drivers.

| Function | GPIO | Notes |
| --- | --- | --- |
| Servo — tilt | 1 | |
| Servo — pan/rotation | 4 | |
| Motor A — IN1 | 5 | DRV8833 |
| Motor A — IN2 | 6 | DRV8833 |
| Motor B — IN3 | 20 | Wired inverted |
| Motor B — IN4 | 10 | Wired inverted |

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

Commands are case-insensitive, one per line:

| Command | Range | Meaning |
| --- | --- | --- |
| `R<angle>` | 0–180 | Set head rotation (pan), e.g. `R90` |
| `T<angle>` | 30–85 | Set head tilt, e.g. `T45` |
| `M<speed>` | −255–255 | Both motors, e.g. `M128` |
| `A<speed>` | −255–255 | Motor A only |
| `B<speed>` | −255–255 | Motor B only |
| `S` | — | Stop all motors |
| `D` | — | Toggle demo mode |

Any movement command automatically cancels demo mode. Angles and speeds are
clamped to the limits in `config.h` rather than rejected.

Responses are ANSI-coloured on serial and stripped to plain text for BLE — see
`respond()` in [`firmware/src/main.cpp`](firmware/src/main.cpp). If you add a
command, add it in `processCommand()` and it works over both transports for
free; then update the help text in `main.cpp`, the cheat sheet in
`controller/index.html`, and the table above.

---

## Roadmap

1. **Custom PCB** — collapse the breakout-board wiring into one board with
   proper power distribution and a battery charger.
2. **Closed-loop drive** — wheel encoders for straight lines and known distances.
3. **Sensing** — time-of-flight for obstacles, IR for spotting other Marvins.
4. **Eyes** — two round displays and an expression system.
5. **Project website** — an animated site in `docs/`, served on GitHub Pages.

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

- **Software** — `firmware/`, `controller/`, `docs/` — under the
  [MIT Licence](LICENSE).
- **Hardware** — `electronics/`, `mechanical/` — under the
  [CERN Open Hardware Licence v2, Strongly Reciprocal](LICENSE-hardware)
  (CERN-OHL-S-2.0). If you make and distribute a modified Marvin, share your
  design changes too.

See [LICENSE](LICENSE) and [LICENSE-hardware](LICENSE-hardware) for the full
terms.
