# Marvin software

Private personal portal and backend for Marvin, plus original ESP-IDF firmware for an ESP32-S3-WROOM module and the Waveshare ESP32-S3 audio board. The public robot-building documentation remains a separate application. The M0–M11 first implementation phase is closed: it delivered the portal, persistence, provider and repository adapters, secure provisioning, native device transport, body voice, local Hey Marvin detection, deployment packaging, privacy controls and signed firmware updates. Ongoing work is post-phase operational hardening and new capabilities, including experimental BLE voice transport—not unfinished M0–M11 implementation. See [delivery record](docs/delivery-status.md).

For the active in-progress slice—local hostname deployment, web remote control,
motion firmware, experimental Gear VR input and the unfinished BLE voice
transport—start with the [current development checkpoint](docs/current-development.md).

## Run locally

Use Node **22.16.0** (or compatible >=22.13) and npm **11.6.4**. Dependencies are locked.

```sh
npm ci
cp .env.example .env
npm run dev
```

Open **http://127.0.0.1:5173** and select **Enter local preview**. No credentials are needed in the explicitly labeled loopback-only development mode. Conversations persist in `data/marvin.sqlite`. Settings → Connections provides sample repository data. Physical Pet setup requires the secure local deployment described in [local development](docs/local-development.md); a preview server never opens Bluetooth or simulates a hardware claim.

To use an actual text model, set `MODEL_PROVIDER=openai`, `OPENAI_API_KEY`, and an explicit supported `OPENAI_MODEL` in `.env`, then restart. The server uses the Responses API with storage disabled and rebuilds context from its own database. No keys are sent to the browser. `npm run smoke:provider` performs one billable adapter smoke request and writes sanitized timing evidence; `MARVIN_SMOKE_SAMPLES=100` opts into 100 requests. Adapter timing is not browser latency acceptance.

For local passphrase authentication, run `npm run password:hash`, set `AUTH_MODE=local`, and put the generated value in `LOCAL_PASSWORD_HASH`. This is a single-owner local deployment. Hosted multi-user authentication uses `AUTH_MODE=oidc` with an **approved** issuer/client registration. Generic OIDC authorization-code flow includes PKCE, state, nonce, and issuer/subject identity. Entire's CLI tokens are not used as web login assertions. [Entire discovery](docs/entire-discovery.md) records the unresolved external contract.

For local Entire repository access, follow [M4 setup and acceptance status](docs/m4-entire.md). This adapter uses the installed CLI and is not the hosted multi-user integration.

For realtime speech, see [M5 voice setup and acceptance status](docs/m5-voice.md). Audio is backend-mediated. Browser voice transcripts appear in the open chat. Use the Pet link control beside a conversation to choose where Desktop Pet speech and responses appear; if no conversation is linked on first use, Marvin creates one. Link changes take effect when the Pet starts listening again. Funded OpenAI text and voice smokes have passed. The standalone Waveshare board runs the16kHz microphone/audio path and sensitivity-first Hey Marvin alpha detector; see [physical audio evidence](docs/m8-audio-runtime.md).

## Verify

```sh
npm run check
npx playwright install chromium
npm run test:e2e
```

Browser tests use a separate `work/e2e.sqlite` when starting their own server. Stop the development server before running them to avoid reusing your preview database. They exercise real UI behavior with explicit model/robot fixtures. On restricted macOS hosts where Chromium cannot launch, use the matching official Playwright Linux image; test evidence here was collected that way.

For PostgreSQL, supply a **disposable** test database:

```sh
docker run --rm --name marvin-test-db -e POSTGRES_USER=marvin_test -e POSTGRES_PASSWORD=test-only -e POSTGRES_DB=marvin_test -p 127.0.0.1:15439:5432 postgres:17.6
# In another terminal:
TEST_DATABASE_URL=postgresql://marvin_test:test-only@127.0.0.1:15439/marvin_test npm run test:postgres
```

SQLite and PostgreSQL run the same persistence contract. `DATABASE_URL` selects PostgreSQL for the application. Transactions and constraints enforce one generation per conversation, one reserved/linked robot per owner, and one owner per device. Owner-bound physical enrollment and Wi-Fi provisioning are implemented and have passed same-AP board trials; broader browser/platform/location acceptance remains open.

## Build and deployment boundaries

`npm run build` builds both server and UI. `npm start` serves them from port 4310; set `APP_ORIGIN=http://127.0.0.1:4310` for a local built preview. Production configuration rejects development sign-in, fixture models, and non-HTTPS origins. For physical local development, use the stable-hostname, persistent-CA and fast-flash workflow in [local development](docs/local-development.md), rather than putting a DHCP address in firmware. Local HTTPS/PostgreSQL packaging, backups, recovery drills, retention/export/delete, rate limits and bounded diagnostics are implemented. Use a TLS reverse proxy with WebSocket support, approved production OIDC, persistent database storage and managed secrets. Run one API process for live stream delivery; a distributed socket broker is outside the current topology.

Sources: `apps/web` React/Vite portal; `apps/server` Fastify API/auth; `packages/contracts` validation; `packages/persistence` migrations/store; `packages/runtime` orchestration/provider/policy; `packages/provisioning` UI transport boundary; `firmware` ESP-IDF implementation. [Firmware build and bench procedure](firmware/README.md).

CI defines software, PostgreSQL, browser and firmware builds. It has not been run on a remote CI service in this task. Never commit `.env`, databases, factory secrets, SDK/toolchain downloads, or raw credential-bearing diagnostics.
