# Marvin — Electronics

Schematics, PCB layout, bill of materials, and power system design.

> **Status: not started.** This folder is scaffolding for work in progress. The
> current prototype is wired from off-the-shelf breakout boards on protoboard —
> the intent is to collapse that into a single custom PCB.

---

## Folder layout

```
electronics/
├── schematics/    KiCad schematic sources (.kicad_sch)
├── pcb/           KiCad layout, footprints, and the board project (.kicad_pcb, .kicad_pro)
├── power/         Battery selection, charging circuit, power budget
├── datasheets/    Datasheets for non-obvious parts
├── BOM.csv        Bill of materials — see format below
└── README.md      You are here
```

---

## Current prototype wiring

Until the PCB exists, this is the reference wiring. It matches the pin maps in
[`firmware/src/boards/`](../firmware/src/boards), which are the authority — if
these ever disagree, the headers are right.

**Both boards do voice.** What the C3 cannot do is listen for its own name: it
has no PSRAM and no vector unit, so there is nowhere to run a keyword model, and
conversations are started from the web controller instead. The S3 is where "Hey
Marvin" will land, along with the echo cancellation that allows talking over a
reply — both need the PSRAM. Everything else is identical, the microphone and
amplifier wiring included.

### ESP32-C3 SuperMini

| Component | Part | Connection |
| --- | --- | --- |
| MCU | ESP32-C3 SuperMini | — |
| Motor driver | DRV8833 breakout | IN1 → GPIO 5, IN2 → GPIO 6, IN3 → GPIO 20, IN4 → GPIO 10 |
| Left/right motors | 2 × brushed DC gearmotor | DRV8833 outputs A/B |
| Head pan servo | SG90-class | GPIO 4 |
| Head tilt servo | SG90-class | GPIO 1 |
| Microphone | INMP441 breakout | SCK → GPIO 3, WS → GPIO 0, SD → GPIO 21, L/R → GND |
| Amplifier | MAX98357A breakout | BCLK → GPIO 3, LRC → GPIO 0, DIN → GPIO 7 |

Those four I²S pins are the only ones left on this board with no boot or system
role. GPIO 2, 8 and 9 are strapping pins, GPIO 8 also drives the onboard LED and
GPIO 9 is BOOT.

**Leave the amplifier's SD pin unconnected on the C3.** The only pin left for it
is GPIO 2, which must read HIGH at reset or the chip does not boot — and being
mid-mute when the board resets would leave you with a robot that will not start.
A robot that hisses quietly when idle is a much smaller problem. If the hiss
does bother you, wire SD to GPIO 2 **with a 10 kΩ pull-up to 3V3**, which holds
the strapping requirement through a reset, and uncomment `PIN_AMP_SD` in
[`firmware/src/boards/pins_esp32c3_supermini.h`](../firmware/src/boards/pins_esp32c3_supermini.h).

### ESP32-S3 SuperMini

| Component | Part | Connection |
| --- | --- | --- |
| MCU | ESP32-S3 SuperMini (ESP32-S3FH4R2) | 4 MB flash, 2 MB quad PSRAM |
| Motor driver | DRV8833 breakout | IN1 → GPIO 4, IN2 → GPIO 5, IN3 → GPIO 6, IN4 → GPIO 7 |
| Head pan servo | SG90-class | GPIO 2 |
| Head tilt servo | SG90-class | GPIO 1 |
| Microphone | INMP441 breakout | see below |
| Amplifier | MAX98357A breakout | see below |
| Spare | — | GPIO 8, 38 for the planned I²C bus |

Only the thirteen header GPIOs with no boot or system role are used: 1, 2, 4, 5,
6, 7, 8, 15, 16, 17, 18, 21 and 38. Everything else on this board is a strapping
pin (3, 9–14, 45, 46), USB/JTAG (39–41), flash and PSRAM (26–37), or the RGB LED
(48). Driving one of those is how a board stops booting.

---

## Microphone and speaker

Both parts sit on **one I²S peripheral in full-duplex mode**, sharing BCLK and
WS. That is not a trick to save a soldering joint — it is why the whole robot
runs at 16 kHz. Two independent buses would need four more pins than the
SuperMini has to spare, and both chips pair a transmit and a receive channel on
the same port precisely so a microphone and a speaker can run off one clock. The
C3 has exactly one I²S peripheral, which is all this needs. The price is that
capture and playback must agree on a sample rate, and the backend resamples for
whatever the AI service wants.

### INMP441 microphone

| INMP441 | C3 | S3 | Note |
| --- | --- | --- | --- |
| VDD | 3V3 | 3V3 | about 1.4 mA — safe on the regulator |
| GND | GND | GND | its own wire back to the ESP32's GND pin |
| SCK | GPIO 3 | GPIO 15 | bit clock, shared with the amplifier |
| WS | GPIO 0 | GPIO 16 | word select, shared with the amplifier |
| SD | GPIO 21 | GPIO 17 | data into the ESP32 |
| L/R | GND | GND | puts the microphone in the left slot |

### MAX98357A amplifier

| MAX98357A | C3 | S3 | Note |
| --- | --- | --- | --- |
| VIN | **5 V rail — not 3V3** | same | peaks near 1 A; see below |
| GND | GND | GND | its own wire back to the ESP32's GND pin |
| BCLK | GPIO 3 | GPIO 15 | shared with the microphone |
| LRC | GPIO 0 | GPIO 16 | shared with the microphone |
| DIN | GPIO 7 | GPIO 18 | data out of the ESP32 |
| SD | *leave unconnected* | GPIO 21 + 100 kΩ pull-up to 3V3 | see the C3 note above |
| GAIN | leave unconnected | leave unconnected | 9 dB, which is right for a small speaker |
| +, − | speaker | speaker | 4 Ω 3 W, or 8 Ω 2 W |

Four things that will cost you an evening if you skip them:

- **On the S3, the pull-up on SD is not optional.** Breakout boards pull SD
  down, so a high-impedance GPIO would leave the amplifier shut down — and,
  worse, would hold no clean logic level. The pull-up keeps it enabled whenever
  the firmware is not deliberately muting it, and the firmware mutes it whenever
  Marvin is not speaking, because the MAX98357A hisses audibly with a live clock
  and nothing to play. On the C3 the pin is left unwired and the amplifier is
  always on; the hiss is the price of a board that reliably boots.
- **The amplifier runs from the 5 V rail with its own bulk capacitance** —
  100 µF and 0.1 µF at the VIN pin. Brownout under load is already the standing
  reliability problem on this robot, and the amplifier is the largest transient
  load yet added. Feeding it from the ESP32's 3V3 regulator resets the board on
  the first loud syllable.
- **Give the microphone and the amplifier separate ground wires** back to the
  ESP32's GND pin. The amplifier's current spikes modulate the ground it shares,
  and a microphone reading its signal against that ground hears them.
- **Keep the four I²S wires short** — under about 10 cm — and away from the
  motor leads. BCLK runs at 16 000 × 32 bits × 2 slots = **1.024 MHz**.

The firmware writes the same mono sample into both I²S slots, so playback is
correct whichever channel the amplifier's SD voltage happens to select.

### Checking it works

Flash either build, connect over serial or Bluetooth, and send `L`. That pipes
the microphone straight to the speaker. If you hear yourself, the wiring, the
clock and both parts are good, and anything that fails later is software. Send
`L` again to stop.

---

## Planned board

The custom PCB should carry:

- **ESP32 module** — footprint to be decided; the firmware is board-agnostic by
  design, so the choice is not urgent
- **Dual H-bridge** for the track motors
- **Servo headers** with their own supply rail
- **Battery management** — Li-ion cell, protection, USB-C charging
- **Encoder inputs** for closed-loop drive
- **I²C bus** broken out for the time-of-flight sensor and the eye displays
- **IR emitter and receiver** for Marvin-to-Marvin interaction
- **Microphone and amplifier** on the board rather than on flying leads, with
  the microphone placed as far from the speaker as the chassis allows — the two
  being centimetres apart is the reason there is no talking over Marvin yet

### Power notes

The single biggest reliability problem on the current prototype is brownout:
servos and motors drawing current together sag the rail enough to reset the
ESP32, which shows up as a Bluetooth disconnect mid-drive. The board should
give the logic rail its own regulator and adequate bulk capacitance rather than
sharing directly with the actuators.

Contents of `power/` should eventually include the cell choice and datasheet,
the charge and protection circuit, a measured current budget per subsystem, and
the expected runtime.

---

## Bill of materials

`BOM.csv` should be committed as CSV — it diffs cleanly and both spreadsheets
and JLCPCB/PCBWay assembly tools read it directly. Use these columns:

```csv
Reference,Quantity,Value,Description,Package,Manufacturer,MPN,Supplier,SupplierPN,UnitPrice,Notes
```

Keep a separate row per distinct part, and put the reference designators
(`R1,R2,R5`) in the first column so the file can be checked against the
schematic automatically.

The mechanical BOM — fasteners, bearings, motors, printed parts — belongs in
[`mechanical/`](../mechanical), not here. This file covers the PCB only.

---

## Conventions

- **KiCad 8 or later.** Its files are text-based and diff reasonably.
- Commit `.kicad_sch`, `.kicad_pcb`, `.kicad_pro`, and any project-local
  footprint or symbol libraries. `.gitignore` already drops `*-backups/`,
  `*.kicad_prl`, and `fp-info-cache`.
- **Do not commit generated Gerbers or drill files.** Attach them to a release
  instead — they are build output, and regenerating them from the layout is one
  menu click.
- Datasheets go in `datasheets/` and are tracked through Git LFS (`*.pdf` is
  already configured in [`.gitattributes`](../.gitattributes)). Only add ones
  that are genuinely hard to find; link to the manufacturer otherwise.

---

## Licence

Everything in this folder is licensed under the
[CERN Open Hardware Licence v2, Strongly Reciprocal](../LICENSE-hardware) —
not the MIT licence that covers the project's software.
