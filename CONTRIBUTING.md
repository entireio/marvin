# Contributing to Marvin

Marvin combines web software, cloud services, embedded firmware, and open
hardware. Read the README nearest the subsystem you intend to change, and keep
one authoritative implementation of every protocol or hardware target.

## Setup

Install Git LFS before cloning so CAD and mesh pointers are resolved:

```sh
git lfs install
git clone https://github.com/spedemon/marvin.git
cd marvin
npm ci
```

Use Node 22.16.0 or compatible >=22.13 and npm 11.6.4 for the agent. Current
firmware uses ESP-IDF 5.4.2; it does not use the retired Pet v1 PlatformIO
target. Hardware contributors need Rhino or another STEP-capable CAD tool for
mechanics and KiCad 8 or later for electronics.

## Before opening a pull request

Run the checks relevant to the change:

```sh
npm run check
npm run test:e2e
idf.py -C firmware build
```

Browser tests require Playwright Chromium. Firmware changes should name the
exact board/profile tested and distinguish compilation, host tests, and
physical evidence. Never claim physical acceptance from a build alone.

## Repository conventions

- `apps/server`, `apps/web`, `packages`, and `firmware` are the active product.
- `apps/build-docs` is dependency-free static HTML/CSS/JavaScript. Keep asset
  paths relative and update shared navigation consistently across its pages.
- Pet v1 documentation must remain clearly labelled; do not mix its SuperMini
  pin map or Arduino commands with the current Waveshare/ESP-IDF firmware.
- `hardware/mechanical` and `hardware/electronics` are hardware source. Commit
  editable source and reviewable interchange formats, not generated tool
  caches, Gerbers, or machine-specific G-code.
- Binary CAD and meshes use Git LFS. Website media and application screenshots
  are deliberately exempt because they must be served directly from Git.
- Never commit `.env`, credentials, factory data, enrollment/release private
  keys, databases, raw audio, SDK downloads, or generated firmware images.

The full-resolution `hardware/mechanical/src/marvin_design.3dm` is intentionally
ignored because it exceeds hosting limits. Do not force-add it. Use the reduced
`marvin_design_v3_hq.3dm` as the tracked editable model.

## Commits and reviews

Use an imperative subject with a useful subsystem prefix, for example:

- `firmware: clamp track commands at the safety boundary`
- `web: explain pet reconnection state`
- `docs: correct the Pet v1 wiring diagram`
- `mechanical: add the revised neck STEP export`

Keep logical changes reviewable and include tests or evidence proportional to
risk. Changes to device messages should update the shared contract, server,
simulator, firmware, and tests together.

## Licensing

By contributing, you agree that software, firmware, and documentation are
provided under the [MIT License](LICENSE), while hardware design contributions
under `hardware/` are provided under
[CERN-OHL-S-2.0](LICENSE-hardware). Preserve third-party notices.
