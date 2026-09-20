# Marvin first-phase delivery record (M0–M11)

The M0–M11 first implementation phase was closed on 19 September 2026. This
document preserves the measured evidence and the original release-certification
criteria used during that phase; it is not a claim that the phase remains open.
Post-phase work is tracked in [current development](current-development.md).

Historical measured evidence through 13 September 2026. Project: `/Users/stefanopedemonte/Projects/Marvin_software`.

## Current branch note — 19 September 2026

This report preserves milestone evidence; it is not a description of every
uncommitted feature in the active branch. The current branch adds local-hostname
development tooling, web remote control, a motion coordinator, experimental
Gear VR input, voice refinements, and a BLE frame codec. Those changes are
described with their explicit non-acceptance boundaries in the
[current development checkpoint](current-development.md). In particular,
the branch no longer has the former app-level motion-arm gate, and BLE voice is
not an operating transport. Do not infer either physical motion acceptance or
BLE-only deployment support from this historical evidence.

The code is runnable and the independent automated gates below pass. The
original production-certification evidence was intentionally incomplete in
places (physical hardware, approved Entire identity, live-provider measurement
and representative-user evaluation). Those are post-phase operational or
release follow-ups, not evidence that M0–M3 implementation is unfinished.

| Milestone | Implemented and verified | Outstanding acceptance |
| --- | --- | --- |
| M0 | Entire public-contract discovery and explicit external dependencies; docs-compatible clickable journeys; original ESP-IDF5.4.2 firmware compiles for ESP32-S3-WROOM. Unique SRP factory credential generation and NVS image generation work. BLE/Security2 bench client imports and runs its help entry point. | The Waveshare audio board has verified firmware, backup and audio/radio evidence. Ten encrypted scan/connect cycles now pass, including saved-network reconnection after reset; wrong-password rollback also restores connectivity. Laptop-only AP isolation and additional physical peripheral coverage remain open. Entire external client registration remains unproven. |
| M1 | Locked TypeScript workspaces, validation, API/auth/provider boundaries, SQLite/PostgreSQL migrations, durable state, one robot/account constraints and lease-fenced generation. Identical conformance tests run against both DBs;100-way account/device/generation races each yield one winner. PostgreSQL and SQLite upgrades/reopen retain data. CI configuration and clean browser-container install pass. | CI workflow exists but has not run on a remote CI service. Production deployment/operations belong to later milestones. |
| M2 | Responsive portal at360/768/1440px; light/dark; conversation/history/repository/settings; local login and generic OIDC adapter; labeled simulated setup/network-change/unlink; native dialogs/focus recovery; keyboard network selection. Seven browser tests pass with zero serious/critical axe findings at audited states. | Approved Entire/OIDC end-to-end login, manual screen-reader evaluation, five representative participants. Hardware setup is deliberately unavailable in the browser until secure transport and owner authorization are proven. |
| M3 | Streaming runtime, cancel, durable history/context compaction,20 seeded reconstruction cases, one active generation/conversation, idempotency, output-route checks, WebSocket replay and immediate logout revocation. OpenAI Responses adapter tested through the real SDK against a local streaming HTTP fixture, including tool round-trip and failure/truncation handling. Physical and repository-write tools are excluded/rejected for web interactions. | Funded live generation passes (1,031 ms first server delta). An initial browser run stopped after50 observations and is retained as failed. A paced100-sample run passed with no failures/HTTP errors: first-visible-response p50=480 ms, p95=724 ms on headless Chrome152/macOS. The development rig meets the100-observation p95≤3s target. Controlled baseline certification and broader semantic continuity remain open. Synthetic adapter/context checks cannot prove semantic reasoning or end-to-end latency. |

## M4 implementation record

The local Entire CLI adapter, repository explorer and runtime evidence tools are implemented. The adapter is explicitly refused with hosted OIDC authentication. Full system access resolved the CLI keychain restriction. Live summary, search and history smoke reads now pass for `spedemon/marvin`; the final20-question live review passed20/20 with the separately configured GPT-5.4 repository model. Earlier Mini failures remain recorded. Graph, missing-data/service behavior and approved hosted identity remain post-phase certification follow-ups. See [M4 setup and evidence](m4-entire.md) and [the hosted integration decision](decisions/0004-entire-integration.md).

The following M0–M3 evidence is retained from its delivery; current M4 verification is recorded under `tests/acceptance/results/M04`.

## M5 implementation record

Realtime voice capture/playback, server mediation, shared durable transcripts, repository tools, interruption, mute, idle cleanup and recovery are implemented. The automated suite includes 20 reconstructed text–voice–text journeys and 100 routed audio turns with recorded responses. Funded live voice passes with saved transcript and 122,400 audio bytes; detected-end to received audio was1,262 ms. Twenty live text–voice–text journeys preserve the required facts across five synthetic recall scenarios and fresh provider sessions. All100 live browser capture/playback rounds passed; synthetic input-end to scheduled audio p95=2,040 ms (includes VAD).100 local synthetic interruption trials stop playback at p95=60 ms. Physical acoustic and broader continuity measurement is post-phase work. See [voice setup and evidence](m5-voice.md).

## M6–M11 implementation record

These implementation milestones are closed. The right-hand column preserves
post-phase hardening and release-certification work; it must not be read as a
list of missing M6–M11 implementation.

| Milestone | Current implementation | Remaining work and evidence |
| --- | --- | --- |
| M6 | Authenticated native device WebSocket gateway; expiring credentials and ownership epochs; durable command admission, deduplication, replay and boot fencing; bounded traffic; simulator and concurrency tests. The latest ten-device run collected83,587seconds of continuous observations with no failures, connection loss, audio upload or memory growth. | Post-run validation found a7,119-second terminal observation gap even though the original harness accepted90,706seconds of wall time. The run is retained as not accepted, and the harness now rejects any observation gap over30seconds, including the terminal interval. Physical WSS presence/heartbeats and backend restart recovery pass without changing the device boot ID. A complete continuous24-hour run and the remaining physical command/reconnect matrix remain open. |
| M7 | Owner-bound enrollment tickets, signed device challenges, atomic redemption and encrypted retry receipts; completed replies recover after ticket expiry only while the binding and credential remain current. SQLite/PostgreSQL unlink races pass. Sequence6 retains the physically passing Security2 visible/hidden application, wrong-password rollback, post-disconnect resume and direct Chrome Web Bluetooth behavior while preserving owner epoch and native presence. | A second physical AP, laptop-only-AP isolation, arbitrary power-cut boundaries, full physical reset, offline unlink/relink,19-of-20 rate trials and participant measurements remain open; the public flow stays disabled by default. See [M7 details](m7-physical-setup.md). |
| M8 | Waveshare audio runtime: native16 kHz microphone upload, server resampling, bounded pre-roll/playback, local AFE/Hey Marvin processing and bounded queues. The user confirmed clear voice at volume95 on the8Ω/1W speaker. Sequence6 fixes deterministic speaker-echo self-interruption: two exact-question physical trials completed15.35–15.45seconds of playback with zero local interruptions and completed durable turns. Silent30-second live upload and60-minute application-layer idle observation pass. The clean sequence4 release completed an uninterrupted28,799.9-second physical soak at16,000.18 processed samples/s with zero audio/uplink activity or faults; wake and authenticated connectivity were restored afterward. | Volume95 body playback is now intentionally half-duplex; measured echo-resistant spoken barge-in remains open. The selected wake engine has high hard-negative false activations by explicit alpha tradeoff and does not pass the original acoustic gate. Instrumented supply power/thermal behavior, acoustic latency/AEC and unavailable peripheral gates remain open. |
| M9 | Canonical body voice, capability-gated tools, designated output routing, session/interrupt fencing and cancellation. Tests cover100 routed turns at each16/24 kHz input rate, plus stop/restart and interrupt races across context/admission. | Physical mixed-surface continuity, body first-audio/barge-in timing, trained wake behavior and actual peripheral operation remain open. Fixture routing and silent sample counts do not establish acoustic acceptance. |
| M10 | Anthropic text SDK adapter; second speech adapter using Deepgram STT/TTS around the canonical text provider. Protocol fixtures and20 authenticated fresh voice sessions preserve saved history and retire sockets. | Live second-provider credentials/quality/latency, cloud/local parity, provider-switch acceptance and independent installation trials remain open. See [provider portability](m10-provider-portability.md). |
| M11 | Privacy/export/retention, backup fixtures, hardened packaging and synthetic load evidence. Signed manifest, bounded authenticated HTTPS transfer, explicit rollout, separate factory release trust, quiescence, coordinator, health/rollback logic and guarded preserving bootstrap are implemented. Health-rejected updates recovered the prior release10/10 times. Clean sequences4–6 were installed through the one-device rollout; sequence6 passed signed selection and boot health while sequence5 remains the fallback. | Production key protection/rotation, flash power-cut injection, real-provider load, broader deployment recovery, all earlier release gates and the seven-day ten-user beta remain required. See [firmware updates](m11-firmware-updates.md). |

The ESP32-S3 backup at `work/board/backup-20260912T221258Z` contains two matching 16,777,216-byte flash reads. SHA-256: `043e7c53a67ad581f6fb4efd51972bf6271c13383980418dba153eb789666eb2`. It is private and excluded from version control. The board is an ESP32-S3 revision v0.2 with 16 MB flash and 8 MB PSRAM. Full system access permits serial and Bluetooth access, but entering the USB ROM bootloader is intermittently failing and sometimes requires a physical BOOT/RESET gesture. Both bench profiles have been flashed with esptool verification. The owner profile is now flashed and verified. Physical claim, saved-owner recovery and same-AP network change pass on the isolated LAN test deployment; native WSS presence confirms the matching epoch. One BLE disconnect during the initial network-change trial is retained as a failure. Its recovery exposed and fixed a completed-setup resume bug. The previously flashed experimental audio profile advertised voice only. The current workspace adds bounded head and named track motion, an EEP sleep gate, a durable motion-command ledger, and listening/thinking/speaking head cues. The first supervised motion bench image flashed to the MAC-matched board on 17 September 2026 with image hashes verified, and its serial console confirmed audio availability and authenticated uplink online. Investigation after the user reported no response to “Hey Marvin” found that this build omitted `MARVIN_LOCAL_AFE` and wake autostart because its build did not enable the optional AFE component. The corrected audible motion image was flashed after a MAC check, with all five programmed images hash-verified; it contains the previous speech model and restores wake autostart. The serial console recorded a wake event and the user confirmed an audible response to “Hey Marvin”. The updated local backend was deployed and passed its TLS health check. On 18 September 2026 the head servo signals moved from the USB pins GPIO19/20 to GPIO8/9; the matching audible motion image was flashed to the MAC-matched board with all five image hashes verified. USB serial remained enumerated after boot, and the user confirmed the tilt servo stays still while idle on USB-C. The serial check also recorded device-link faults; voice and commanded head/track motion remain for later investigation and physical acceptance.

## Current verification

- `npm run check`: **162 tests passed**, strict TypeScript checking and production build passed. Includes transport-loss normalization, confirmed Wi-Fi rollback/retry, and post-handshake idle-audio rejection.
- `TEST_DATABASE_URL=… npm run test:postgres`: **53 tests passed**, covering both engines, completed-enrollment recovery after expiry, concurrent unlink, persistence, command admission and privacy.
- Updated browser suite: **14 tests passed** across independently started portal, privacy and voice suites in the official Playwright Linux container. Page/static requests no longer consume API rate limits; login/API rate limiting remains enforced by a new regression test.
- Waveshare diagnostic firmware compiles with ESP-IDF 5.4.2. This is a local diagnostic image, not the complete robot runtime.
- The configured OpenAI key is funded and working. Live text and backend voice smoke checks pass; sanitized evidence is under M03/M05. Twenty live cross-surface recall journeys also pass their stated narrow rubric. A live repository voice query retrieved committed source and answered its framework/baud-rate question correctly. Entire live authentication and read smoke checks pass. Fixture adapter tests do not demonstrate live model quality.

Logs for the current development checks are in the ignored `work/board` directory. Sanitized historical acceptance evidence is retained in `tests/acceptance/results`.

## Historical M0–M3 evidence


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

## Historical certification boundaries

The following describes certification evidence that was not claimed at the time;
it does not reopen M0–M11. The bench profile verifies Wi-Fi through TLS without
linking an owner. The separate owner profile implements account enrollment and
recovery and its claim/recovery flows have been verified physically; neither
profile yet provides the complete decommissioning/runtime flow. Its five-minute
boot setup window and proof-of-possession are a bench experiment. No Wi-Fi
passwords travel through the portal API. Generic OIDC is ready for an approved
provider; no fake Entire login is shipped. Public docs were inspected for
aesthetics and remain unchanged/separately served.

## Current attended checkpoint — September 13

Early attended audio exposed crackling and incomplete playback. After transport, pacing, resampling and cancellation fixes, the user confirmed complete clear speech and selected volume95/100 for the8Ω/1W speaker. Existing ownership remains preserved.

Firmware changes separate audio conversion from network reception, optimize resampling, pace playback from I2S, and use a lower-compute single-microphone AFE with the actual playback reference. The latest count test correctly transcribed the request and had no firmware or transport fault, but the provider session closed early. Server cancellation fixes now track remote completion independently of paced playback and cancel at most once. Three regression tests bring the full passing suite to155. The next physical count completed1–10 with25.05s capture and13.4s submitted playback, no firmware/transport fault and a completed canonical reply. The user-requested replay also passed and the user rated its sound “Perfect”; quality is confirmed for that quiet count trial. This does not establish the broader acoustic acceptance gates. Current artifacts and failed trials remain in `work/board` and are summarized in [the handoff](overnight-handoff.md).

The earlier physical soak stopped after approximately6.1 hours, but the fresh sequence4 run completed its full28,799.9-second observation and passed. The release-mode harness suppressed wake explicitly and verified capture/playback, uplink counters, AFE rate, memory and fatal diagnostics without creating provider sessions. Wake is enabled again and authenticated device presence is online. The latest simulator run was healthy for83,587continuous seconds but is not accepted as a24-hour result because a terminal7,119-second observation gap crossed the target; the corrected harness rejects this condition.

The whole-system release-certification record was incomplete at this checkpoint.
The remaining physical, provider, identity, installation, usability and beta
gates are post-phase work; they do not change the completed M0–M11 status.


Latest audio checkpoint: the user replaced the speaker with an8Ω/1W unit and confirmed95/100 as loud enough and clear; this is now the release startup setting. Wake-only activation and300ms local pre-roll pass scoped physical trials. Earlier local VAD settings allowed one user-confirmed natural interruption but later caused deterministic self-interruption from volume95 speaker echo. Sequence6 therefore uses an echo-safe half-duplex playback policy and completes repeated replies. The sensitivity-first Hey Marvin alpha detects the scoped positive set but retains poor hard-negative performance. Full acoustic acceptance remains open. Audible, silent and signed-release firmware profiles build.

- Historical sequence4 evidence records one user-confirmed natural interruption: “Worked”. Backend independently confirms the count turn cancelled after5 and the following request completed with “Blue sky.” Sequence6 intentionally supersedes that admission behavior because volume95 echo later cancelled every ordinary response. Spoken barge-in during body playback is again open; explicit interruption remains deterministic.
