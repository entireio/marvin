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

Until the PCB exists, this is the reference wiring. It matches the pin map in
[`firmware/src/config.h`](../firmware/src/config.h), which is the authority — if
these two ever disagree, `config.h` is right.

| Component | Part | Connection |
| --- | --- | --- |
| MCU | ESP32-C3 SuperMini | — |
| Motor driver | DRV8833 breakout | IN1 → GPIO 5, IN2 → GPIO 6, IN3 → GPIO 20, IN4 → GPIO 10 |
| Left/right motors | 2 × brushed DC gearmotor | DRV8833 outputs A/B |
| Head pan servo | SG90-class | GPIO 4 |
| Head tilt servo | SG90-class | GPIO 1 |

Motor B's inputs are wired inverted (IN3/IN4 swapped relative to IN1/IN2) so
that a positive speed on both channels drives the robot forward without the
firmware needing per-side sign correction.

The DRV8833 breakout has no enable pin — both inputs LOW is coast. The firmware
relies on this and drives all four pins LOW at the very start of `setup()`.

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
