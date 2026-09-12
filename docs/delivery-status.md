# Marvin M0–M3 delivery status

12 September 2026 · Development implementation with measured evidence. Project: `/Users/stefanopedemonte/Projects/Marvin_software`.

The code is runnable and the independent automated gates below pass. **M0–M3 are not all accepted for production:** physical hardware, approved Entire identity, live-provider measurements, and representative-user evaluation are still required by the original plan. Targets have not been relaxed to call fixtures a release.

| Milestone | Implemented and verified | Outstanding acceptance |
| --- | --- | --- |
| M0 | Entire public-contract discovery and explicit external dependencies; docs-compatible clickable journeys; original ESP-IDF5.4.2 firmware compiles for ESP32-S3-WROOM. Unique SRP factory credential generation and NVS image generation work. BLE/Security2 bench client imports and runs its help entry point. | Actual board ordering code/carrier/peripheral pinout; ten encrypted scan/connect cycles, including reconfiguration; physical memory/heap/radio/timing evidence. No board was connected or flashed. Entire external client registration remains unproven. |
| M1 | Locked TypeScript workspaces, validation, API/auth/provider boundaries, SQLite/PostgreSQL migrations, durable state, one robot/account constraints and lease-fenced generation. Identical conformance tests run against both DBs;100-way account/device/generation races each yield one winner. PostgreSQL and SQLite upgrades/reopen retain data. CI configuration and clean browser-container install pass. | CI workflow exists but has not run on a remote CI service. Production deployment/operations belong to later milestones. |
| M2 | Responsive portal at360/768/1440px; light/dark; conversation/history/repository/settings; local login and generic OIDC adapter; labeled simulated setup/network-change/unlink; native dialogs/focus recovery; keyboard network selection. Seven browser tests pass with zero serious/critical axe findings at audited states. | Approved Entire/OIDC end-to-end login, manual screen-reader evaluation, five representative participants. Hardware setup is deliberately unavailable in the browser until secure transport and owner authorization are proven. |
| M3 | Streaming runtime, cancel, durable history/context compaction,20 seeded reconstruction cases, one active generation/conversation, idempotency, output-route checks, WebSocket replay and immediate logout revocation. OpenAI Responses adapter tested through the real SDK against a local streaming HTTP fixture, including tool round-trip and failure/truncation handling. Physical and repository-write tools are excluded/rejected for web interactions. | No real model credentials were supplied. Live provider generation/semantic continuity and100-observation first-visible-token p95≤3s remain unmeasured. Synthetic adapter/context checks cannot prove semantic reasoning or end-to-end latency. |

## Evidence

- `npm run check`: **35 tests passed**, strict TypeScript checking and production builds passed.
- `TEST_DATABASE_URL=… npm run test:postgres`: **24 tests passed**, including both persistence backends and PostgreSQL isolated-schema upgrade/reopen.
- Playwright1.63 official Linux container: **7 tests passed**; desktop/mobile and dark screenshots retained. Native Chromium could not launch under the macOS process sandbox, so the browser suite ran inside the isolated container. No browser security bypass was introduced into the application.
- ESP-IDF5.4.2 / xtensa GCC14.2: **firmware build passed**, binary **1,199,680 bytes**,39% of the smallest app partition free. Linker DIRAM use138,723/341,760 bytes; runtime free heap and stack high-water marks require hardware. The size tool's separate IRAM segment is almost full by layout; that is not a measured system-wide runtime heap claim.
- Factory CSV → NVS binary generation passed; bench Python entry point/import validation passed. This does not prove BLE session establishment.
- `npm audit`: zero reported vulnerabilities at the time of verification; not a security certification.

Sanitized logs and source hashes are under `tests/acceptance/results`. Browser fixtures were isolated from the local preview database. Node25.2.1/npm11.6.4 were used on the host. A separate clean Linux container also passed install,35 tests, typecheck and build with the declared Node22.16.0/npm11.6.4. Browser tests ran on Node24.20.0. Remote CI is configured to use the declared Node version.

## Try the result

Run `npm run dev`, open **http://127.0.0.1:5173**, and choose **Enter local preview**. Connect a sample repository through Settings → Connections. Robot setup can be explored from the welcome screen or Settings → Your Marvin. Changing Wi-Fi preserves the simulated robot identity. Unlinking preserves conversations and repository connection and does not claim to erase an offline robot.

Actual text generation is enabled by setting `MODEL_PROVIDER=openai`, `OPENAI_API_KEY`, and `OPENAI_MODEL` in the server environment. `npm run smoke:provider` makes one billable smoke request and writes sanitized adapter timing. Full user-facing latency acceptance needs browser instrumentation and100 trials with the declared network/provider/model conditions.

Entire login findings and authoritative sources are in [entire-discovery.md](entire-discovery.md). Firmware build/credential/bench steps and exact unimplemented boundaries are in [firmware/README.md](../firmware/README.md). The original [implementation plan](implementation-plan.md) remains the acceptance baseline.

## Explicit boundaries

M4 real Entire repository retrieval, M5 voice, M6 body gateway, M7 production provisioning/ownership, and M8–M11 physical runtime/release work are not claimed. The firmware verifies a candidate network through TLS before saving it; it does not enroll an owner or decommission a robot. Its five-minute boot setup window and proof-of-possession are a bench experiment. No Wi-Fi passwords travel through the portal API. Generic OIDC is ready for an approved provider; no fake Entire login is shipped. Public docs were inspected for aesthetics and remain unchanged/separately served.
