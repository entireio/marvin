# Marvin software

Private personal portal and backend for Marvin, plus original ESP-IDF firmware for an ESP32-S3-WROOM module. The public robot-building documentation remains a separate application. Implementation covers the M0–M3 development slice; hardware, approved Entire identity, and live-provider acceptance have outstanding gates. See [delivery status](docs/delivery-status.md).

## Run locally

Use Node **22.16.0** (or compatible >=22.13) and npm **11.6.4**. Dependencies are locked.

```sh
npm ci
cp .env.example .env
npm run dev
```

Open **http://127.0.0.1:5173** and select **Enter local preview**. No credentials are needed in the explicitly labeled loopback-only development mode. Conversations persist in `data/marvin.sqlite`. Settings → Connections provides sample repository data. Settings → Your Marvin provides simulated setup, Change Wi-Fi, and Unlink Marvin. The simulation never provisions hardware; its Wi-Fi password stays in transient browser memory.

To use an actual text model, set `MODEL_PROVIDER=openai`, `OPENAI_API_KEY`, and an explicit supported `OPENAI_MODEL` in `.env`, then restart. The server uses the Responses API with storage disabled and rebuilds context from its own database. No keys are sent to the browser. `npm run smoke:provider` performs one billable adapter smoke request and writes sanitized timing evidence; `MARVIN_SMOKE_SAMPLES=100` opts into 100 requests. Adapter timing is not browser latency acceptance.

For local passphrase authentication, run `npm run password:hash`, set `AUTH_MODE=local`, and put the generated value in `LOCAL_PASSWORD_HASH`. This is a single-owner local deployment. Hosted multi-user authentication uses `AUTH_MODE=oidc` with an **approved** issuer/client registration. Generic OIDC authorization-code flow includes PKCE, state, nonce, and issuer/subject identity. Entire's CLI tokens are not used as web login assertions. [Entire discovery](docs/entire-discovery.md) records the unresolved external contract.

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

SQLite and PostgreSQL run the same persistence contract. `DATABASE_URL` selects PostgreSQL for the application. Transactions and constraints enforce one generation per conversation, one reserved/linked robot per owner, and one owner per device. Claims are groundwork; production hardware enrollment is M7.

## Build and deployment boundaries

`npm run build` builds both server and UI. `npm start` serves them from port 4310; set `APP_ORIGIN=http://127.0.0.1:4310` for a local built preview. Production configuration rejects development sign-in, fixture models, and non-HTTPS origins. Use a TLS reverse proxy with WebSocket support, production OIDC, persistent database storage, and server secrets. This slice has no deployment, operational SLA, backups, or distributed socket broker; those are later release work. Run one API process for live stream delivery.

Sources: `apps/web` React/Vite portal; `apps/server` Fastify API/auth; `packages/contracts` validation; `packages/persistence` migrations/store; `packages/runtime` orchestration/provider/policy; `packages/provisioning` UI transport boundary; `firmware` ESP-IDF implementation. [Firmware build and bench procedure](firmware/README.md).

CI defines software, PostgreSQL, browser and firmware builds. It has not been run on a remote CI service in this task. Never commit `.env`, databases, factory secrets, SDK/toolchain downloads, or raw credential-bearing diagnostics.
