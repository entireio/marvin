# Contributing to Marvin

Thanks for your interest. Marvin is an early-stage open hardware project, so
the most useful contributions right now are build reports, pin-map corrections,
and anything that makes the robot easier for the next person to reproduce.

---

## Before you start

Read the [`README.md`](README.md) in the folder you intend to change. Each
subsystem has its own toolchain and conventions, and they are documented
locally rather than all in one place.

---

## Setting up

This repository uses **Git LFS** for CAD and mesh files. Install it *before*
cloning, or you will get small text pointer files instead of real geometry:

```bash
git lfs install
git clone https://github.com/spedemon/marvin.git
```

Already cloned without LFS? Recover with:

```bash
git lfs install && git lfs pull
```

Per-subsystem toolchains:

| Working on | You need |
| --- | --- |
| `firmware/` | [PlatformIO Core](https://platformio.org/install/cli) |
| `controller/` | Node.js (only for `npx serve`) and Chrome or Edge |
| `electronics/` | KiCad 8 or later |
| `mechanical/` | Rhino 3D for sources; any slicer for the STLs |
| `docs/` | To be decided — see `docs/README.md` |

---

## Large files: the rules

A hardware repository dies quickly if raw CAD exports go into Git history. Two
rules keep clones fast:

1. **Anything binary and re-exportable goes through Git LFS.** The patterns are
   already set up in [`.gitattributes`](.gitattributes) — `*.stl`, `*.3dm`,
   `*.step`, `*.pdf`, images. You do not need to do anything special; just
   commit normally with LFS installed.

2. **Nothing over ~50 MB goes in the repository at all.** GitHub hard-rejects
   any single file above 100 MB, and LFS bandwidth on the free tier is shared
   by everyone who clones. Before committing a mesh, check its size:

   ```bash
   ls -lh mechanical/stl/
   ```

   If a mesh is enormous, it is almost always over-tessellated rather than
   genuinely complex. Re-export it with a coarser tolerance — a chassis part
   rarely needs more than a few hundred thousand triangles, and no slicer
   benefits from more.

Two files currently exceed these limits and are distributed out-of-band. They
are listed in [`.gitignore`](.gitignore) with an explanation, and
[`mechanical/README.md`](mechanical/README.md) says where to get them.

---

## Coding conventions

### Firmware (`firmware/`)

- **All pin numbers and tuning constants live in `src/config.h`.** Never
  hard-code a GPIO or a magic angle inside a driver.
- One class per peripheral, in its own `.h`/`.cpp` pair (`motor`, `head_servos`,
  `ble_serial`, `demo`). Follow that shape when adding hardware.
- `loop()` must stay non-blocking. Use millis-based state machines — look at
  `Demo::update()` and `HeadServos::update()` for the pattern. No `delay()` in
  the main loop.
- Drive motor pins LOW at the top of `setup()`, before anything else. Floating
  GPIOs at boot make the robot lurch.
- 4-space indent, `camelCase` methods, `_underscorePrefixed` private members.

### Adding a command

Commands are parsed in one place, `processCommand()` in `src/main.cpp`, and are
shared by the serial and BLE transports. To add one, update:

1. `processCommand()` in `firmware/src/main.cpp` — the parsing
2. The help block in `setup()` in the same file
3. The cheat sheet in `controller/index.html`
4. The protocol table in the root [`README.md`](README.md)

Use `respond()` / `respondf()` rather than `Serial.print()` so the output
reaches BLE clients too, with ANSI colour codes stripped automatically.

### Controller (`controller/`)

Plain HTML, CSS, and JavaScript — no build step and no framework, deliberately,
so the app can be served from anywhere. Keep it that way. Web Bluetooth logic
stays inside the `BleConnection` class; UI code should not touch
`navigator.bluetooth` directly.

### Electronics (`electronics/`)

KiCad project files are text and diff reasonably. Commit the schematic, layout,
and project files; let `.gitignore` drop the caches and autosaves. Generated
Gerbers belong in a release, not in the tree.

---

## Commits and pull requests

- Prefix the subject with the subsystem: `firmware:`, `controller:`,
  `electronics:`, `mechanical:`, `docs:`.
- Write in the imperative: `firmware: clamp tilt to mechanical limits`.
- One logical change per pull request.
- If you changed firmware, say in the PR description **which board you tested
  on** and what you observed. There is no CI or hardware-in-the-loop testing
  yet, so your report is the only evidence a change works.

---

## Licensing of contributions

By contributing you agree that your work is licensed under the project's
licences: [MIT](LICENSE) for software (`firmware/`, `controller/`, `docs/`) and
[CERN-OHL-S-2.0](LICENSE-hardware) for hardware (`electronics/`,
`mechanical/`).
