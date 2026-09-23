# Marvin

> **Created by Stefano Pedemonte · Hosted and maintained by Entire.**

Marvin is an open-source embodied agent: a private conversational workspace,
an expressive tracked desktop pet, and the hardware files needed to build it.
This repository is the single source of truth for the web application, server,
device protocol, current ESP32-S3 firmware, public build website, and physical
design.

The current pet uses the Waveshare ESP32-S3-AUDIO-Board and is deeply integrated
with the agent server. The original SuperMini/Arduino pet remains documented as
**Pet v1** in the build website and in the `pet-v1-final` tag; its server,
controller, and firmware are historical rather than parallel implementations.

## Repository map

| Path | Purpose |
| --- | --- |
| [`apps/server`](apps/server/README.md) | Fastify API, authentication, device gateway, voice, and web serving |
| [`apps/web`](apps/web/README.md) | React/Vite personal workspace and pet controls |
| [`apps/build-docs`](apps/build-docs/README.md) | Static public build website, including the Pet v1 guide |
| [`packages`](packages) | Contracts, persistence, runtime, provisioning, and operations |
| [`firmware`](firmware/README.md) | Current ESP-IDF firmware for the Waveshare ESP32-S3 audio board |
| [`hardware/mechanical`](hardware/mechanical/README.md) | CAD, STEP, slicer projects, and printable meshes |
| [`hardware/electronics`](hardware/electronics/README.md) | Pet v1 wiring reference and future PCB design area |
| [`docs`](docs) | Architecture, development, operations, and acceptance records |
| [`tests`](tests) | Unit, browser, firmware-host, and recorded acceptance evidence |

For the active development slice, start with the
[current development checkpoint](docs/current-development.md). The completed
M0–M11 implementation record is in [delivery status](docs/delivery-status.md).

## Run the agent locally

Use Node **22.16.0** or a compatible Node >=22.13 and npm **11.6.4**:

```sh
npm ci
cp .env.example .env
npm run dev
```

Open <http://127.0.0.1:5173> and choose **Enter local preview**. This loopback
mode uses fixture models and never opens Bluetooth or simulates a hardware
claim. Configure a real model provider in `.env` when needed; API keys remain
on the server.

Physical-pet setup requires the stable-hostname HTTPS workflow in
[local development](docs/local-development.md). Firmware build, profile, and
guarded flashing instructions are in [firmware/README.md](firmware/README.md).

## Run the build website

The website has no build step:

```sh
cd apps/build-docs
python3 -m http.server 4173
```

Open <http://127.0.0.1:4173>. Its current assembly and electronics instructions
describe Pet v1 unless a page explicitly says otherwise. Do not apply its
SuperMini pin map to the current Waveshare firmware.

## Verify

```sh
npm run check
npx playwright install chromium
npm run test:e2e
```

CI also builds the firmware. PostgreSQL conformance uses a disposable database
through `TEST_DATABASE_URL`; `DATABASE_URL` selects PostgreSQL for a deployed
application, while SQLite remains the local default.

## Deployment boundaries

The monorepo intentionally produces two deployables:

- `Dockerfile` builds the authenticated agent server and web application.
- `Dockerfile.docs` serves the static build website.

Deploy these independently. Documentation can be public and cacheable, while
the agent owns authentication, persistent data, model credentials, firmware
releases, and long-lived device/browser WebSockets. `PUBLIC_DOCS_URL` connects
the two experiences.

The agent image listens on `PORT`. A cloud deployment must use durable
PostgreSQL storage, managed secrets, an approved OIDC client, reconnecting
WebSocket clients, and one API instance until a distributed socket broker is
implemented. See [deployment](docs/deployment.md).

## History

Both former repositories retain their complete Git histories in this graph.
Thomas Dohmke's `ashtom/website-design` work is merged without squashing. The
last complete standalone pet tree is tagged `pet-v1-final`.

## Licensing

Software, firmware, documentation, and website source are licensed under the
[MIT License](LICENSE). Hardware design source under `hardware/` is licensed
under [CERN-OHL-S-2.0](LICENSE-hardware). Third-party components retain their
own notices and licences.

The full-resolution 2.7 GB Rhino working file is not stored in Git because it
exceeds both GitHub and Git LFS single-file limits. The reduced editable Rhino
model and STEP export are tracked; see the
[mechanical documentation](hardware/mechanical/README.md).
