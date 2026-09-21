# Marvin — historical Codex handoff

Updated **2026-09-19**. The detailed checkpoint below was captured on
**2026-09-14 23:00 UTC / 16:00 America/Los_Angeles** and is retained as a
historical operational record.

> M0–M11 first-phase implementation closed on 19 September 2026. Older “not
> accepted” findings below describe historical release-certification evidence,
> not current milestone status.
Project root: `/Users/stefanopedemonte/Projects/Marvin_software`.

## Read this first

For the active working tree, begin with
[docs/current-development.md](docs/current-development.md). Since this record
was captured, the branch has gained local-hostname development tooling, web
remote control, a motion coordinator, experimental Gear VR controller support,
voice refinements and an incomplete BLE voice transport foundation. The current
branch deliberately removed the old application-level movement-arm gate; it
has not completed physical motion acceptance. Treat all below as historical
unless it agrees with the current checkpoint and source.

Development resumed on the user's instruction. The user explicitly selected a sensitivity-first **Hey Marvin** alpha and accepted poor hard-negative performance for now. Public microWakeWord V1 is selected at cutoff128/window3; the final scoped physical confirmation passed5/5 positives exactly once with zero local faults or microphone upload. Earlier broader tests measured18/20 physical positives with9/20 negative false activations and19/20 direct-host positives with10/20 false activations. This is an alpha decision, not the original acoustic gate. Model redistribution licensing remains unresolved.

Signed OTA is integrated physically. Sequence1 was installed with the guarded preserving bootstrap, confirmed healthy in12.863seconds and returned online with audio/AFE progress. A sequence2 signed failure image forces the90-second health timeout. The physical rollback campaign passed10/10, with every `boot_health_failed` marker observed and the prior audible image recovering online/audio/AFE health. Private details are in `work/releases/2-failure/physical-rollback-trials-final.json`; sanitized evidence is in `tests/acceptance/results/M11/physical-rollback-10-trials.json`. The harness may reset the Espressif USB device once through Homebrew libusb when USB-JTAG remains enumerated but silent. Clean commit `89eca54` was rebuilt and installed as sequence4; clean `1dfa600` became sequence5. Clean commit `183eb17` is now installed as `afe-v1` sequence6, confirmed in35.223seconds and independently reset-checked in13.427seconds; it fixes volume95 playback self-interruption. Sequence5 remains the fallback. Sequence3 was a dirty-tree build and remains unused.

The latest M6 ten-device soak at `work/m6/soak-24h-final-20260913` has stopped. It was healthy across83,587seconds of continuous ten-second observations, but a7,119-second terminal observation gap crossed the24-hour target. The original harness incorrectly accepted90,706seconds of wall time because it did not validate that terminal interval. Sanitized evidence retains the run as not accepted at `tests/acceptance/results/M06/platform-soak-terminal-gap.json`; `scripts/device-soak.ts` now rejects any observation gap over30seconds, including the terminal gap. The sequence4 release completed its uninterrupted28,799.9-second physical soak:16,000.18 processed samples/s, minimum42,771bytes internal free memory, no faults and zero capture/playback/uplink. Wake activation was restored and authenticated uplink is online. Sanitized physical evidence is `tests/acceptance/results/M08/physical-release-soak-8h.json`.

This file summarizes the current checkpoint. `docs/overnight-handoff.md` is a chronological journal containing superseded process IDs, configurations, and “currently running” statements. Its older sections are historical, not current operating instructions. Prefer fresh process/artifact checks and this checkpoint.

## User intent and persistent decisions

- Implement the original specification through M11, with measurable milestone gates. The latest focus narrows execution to robust voice/wake; it does not waive other gates.
- Source specification: `/Users/stefanopedemonte/Downloads/Marvin_System_Architecture_and_Implementation_Specification_v2.docx`.
- Original public documentation website: `/Users/stefanopedemonte/Projects/Marvin/docs/`. The authenticated portal is a separate application with compatible aesthetics: refined, simple, intuitive. Do not merge it with the docs unnecessarily.
- Exactly one robot per account. Login precedes linking; support guided linking, deliberate unlinking, and Wi-Fi changes without changing ownership.
- Wi-Fi choices must come from the robot's scan, not the computer's scan. Provision/reprovision over secure BLE; account ownership and network settings are separate concerns.
- Entire test repository: `spedemon/marvin`. Entire's CLI login uses GitHub OAuth. Its CLI login is **not** evidence that third-party portal SSO or a hosted integration contract exists.
- Firmware is original ESP-IDF work for ESP32-S3 WROOM, currently tested on the connected Waveshare ESP32-S3-AUDIO-Board: <https://docs.waveshare.com/ESP32-S3-AUDIO-Board>.
- The board is **standalone**: no motors, servos, external sensors, or display. Do not invent wiring or claim their physical tests passed.
- The user authorized real board flashing/testing and funded OpenAI API tests. They replaced/funded the key in the project's `.env`; earlier billing/401 failures are historical, not a reason to ask for the key again.
- Speaker is now a **tiny 1 W, 8 Ω speaker on the same output**. User reported 80/90 too quiet and **95/100 loud enough and clear**. Keep 95 unless testing a justified change. Do not arbitrarily increase computer volume; last observed computer output was 38%, unchanged by the agent.
- “Hey Marvin” is mandatory. Renaming a stock detector or falling back to “Hi ESP” does not satisfy the task. Idle detection must remain local, without streaming room audio to a provider.
- The user dislikes repeated confirmations. Existing authorization covers routine reversible implementation/tests after resumption. Do not send external messages, submit training-service requests, publish, or burn eFuses on that basis.

## Workspace and secrets

- This session had full filesystem/network/serial access. Prior sandbox restrictions were resolved. A new session should inspect its actual permissions instead of assuming old restrictions still apply.
- No `AGENTS.md` was found in the project during this work. Recheck if new files have been added.
- No sub-agent delegation was authorized. Do not create other user-visible tasks merely to continue this one.
- Git baseline: `339fb58` — `Implement Marvin M0-M3 development slice with portal, runtime and ESP32-S3 firmware`.
- The user authorized a checkpoint commit/push of all project source, tests and documentation to the new repository. Read `git log -1` and `git status` for its final commit/state; do not assume the earlier uncommitted-work snapshot is current. **Do not reset, clean or overwrite local work.**
- `work/` contains essential local evidence, environments, model experiments and backups, but is ignored by Git. A clean checkout will not contain those artifacts. Do not delete it to free space.
- Never print/copy credentials from `.env`, `work/board/backend.env`, `backend-login.json`, `owner-deployment-keys.json`, factory CSV/images, Wi-Fi test JSON, CLI keychain contents, or authentication cookies. Raw board logs may include SSIDs. Prefer selective numeric diagnostics and sanitized result files.
- The authorization URL from the earlier Entire login is ephemeral and should not be reused. No `entire enable` or repository-hook mutation was performed by the agent.

## Running processes and services

### M6 simulator soak — completed but not accepted

- Final raw report: `work/m6/soak-24h-final-20260913/results.json`; no soak process remains active.
- Continuous sampled span: **83,587 seconds**, with no in-run gap over11seconds, no failures, no connection sample failures and no audio upload.
- The terminal gap was **7,119 seconds**, so the original wall-clock acceptance result is invalid. The sanitized result is `tests/acceptance/results/M06/platform-soak-terminal-gap.json`.
- A future release acceptance run must use the corrected harness and finish a continuous24-hour observation. Development flashing and provisioning may continue under the user's earlier instruction; do not represent this run as M6 acceptance.
- Original `work/m6/soak-24h/results.json` run failed around 73,358 seconds (~20.4 hours) because of ENOSPC while writing results. Preserve it as a failure.
- Do not start a duplicate soak. A new session cannot rely on this session's tool handles; inspect the OS process and progress file.

### Local services

Docker was recovered after disk exhaustion using Docker Desktop's CLI. At this handoff:

- `marvin-board-backend-marvin-1`: running, healthy.
- `marvin-board-backend-proxy-1`: running.
- Other existing packaging and unrelated containers are also running; do not disturb them.
- Physical backend origin: `https://192.168.1.5:8443` (recheck LAN address if network changes).
- CA: `work/board/board-backend-root.crt`.
- Compose/configuration: `work/board/owner-compose.yaml`, private files listed above.
- Safe diagnostic helper: `node_modules/.bin/tsx work/board/read-physical.ts`. It reads credentials internally and prints selected device/turn information. Do not dump its private input files.
- Local portal normally uses `http://127.0.0.1:5173`; do not assume a preview process remains alive just because a browser tab exists.
- Disk free space was about 28 GiB after recovery. Monitor before large dataset/build downloads; do not delete unrelated files.
- The old `marvin-overnight-implementation` automation was paused. Inspect existing automations before creating or reactivating a soak monitor; avoid duplicates.

## Hardware and current firmware

| Item | Value |
| --- | --- |
| Serial | `/dev/cu.usbmodem1101` (rediscover after USB changes) |
| Chip | ESP32-S3 revision 0.2, 16 MB quad flash, 8 MB PSRAM |
| MAC | `28:84:85:b2:ab:10` |
| Device ID | `marvin_e7c81f395a72d47a3abe299aeefb974c` |
| I²C | SDA11, SCL10; ES8311 `0x18`, ES7210 `0x40`, TCA9555 `0x20` |
| I²S | I²S1, MCLK12, BCLK13, LRCK14, DOUT16, DIN15 |
| Amplifier | TCA9555 EXIO8, register3 bit0 |
| Audio | 16 kHz capture; ADC slots1/3; 32-bit stereo I²S TX with mono samples scaled ×65536 |

### Preserve these artifacts

- Original vendor flash backup: `work/board/backup-20260912T221258Z` — two matching 16,777,216-byte reads.
- Backup SHA256: `043e7c53a67ad581f6fb4efd51972bf6271c13383980418dba153eb789666eb2`.
- This is the **original vendor image**, not a snapshot of the currently owned robot.
- Current private owner factory image: `work/board/factory-owner-288485b2ab10`.
- Flash manifest: `work/board/afe-audible-flash.json`.
- Flashed application SHA256 at checkpoint: `1cb41236e0a40d045c8bf46c120034588fa3ad55d79a901e35426b8283b9aee6`.

Partition layout remains unchanged: NVS `0x9000/0x6000`, PHY `0xf000/0x1000`, OTA data `0x10000/0x2000`, factory `0x12000/0x6000`, OTA0 `0x20000/0x1e0000`, OTA1 `0x200000/0x1e0000`, model `0x3e0000/0x400000`.

### Flashed versus compiled

**Flashed:** clean signed `afe-v1` release sequence6 from commit `183eb17`, application SHA256 `8a5fcb0326f29af469cea9cb05428eb0f2dfcbf1a7ac3844e38403a473cba18f`. Its signed OTA check passed. Wake activation is enabled, the phrase is `Hey Marvin`, audio is available, and authenticated uplink is online. Two exact-question acoustic repetitions produced complete15-second responses with zero local interruption; sequence5 remains the fallback. Body playback is deliberately half-duplex until echo-resistant barge-in is measured. None of the locally trained wake candidates was installed on the board.

### Serial/build discipline

Use one serial owner at a time. Never flash during a board test. Wait for build exit0 before flashing. Do not expand partitions, erase ownership/factory data, or burn eFuses as a shortcut.

ESP-IDF5.4.2 is at `work/esp-idf`; tools at `work/idf-tools`. Use the Python path **without resolving its symlink**:

```sh
cd /Users/stefanopedemonte/Projects/Marvin_software
work/idf-tools/python_env/idf5.4_py3.13_env/bin/python --version
CMAKE_BUILD_PARALLEL_LEVEL=4 firmware/tools/build-waveshare-afe-audible.sh
```

After a valid candidate is deliberately selected and reviewed, the existing flasher is:

```sh
work/idf-tools/python_env/idf5.4_py3.13_env/bin/python firmware/tools/board-flash.py \
  --backup work/board/backup-20260912T221258Z --profile afe-audible \
  --factory work/board/factory-owner-288485b2ab10 --reuse-verified-base --flash
```

**Do not run that now:** the current compiled wake asset is rejected. `--reuse-verified-base` first verifies the physical bootloader/partition/model/factory images against the build, then writes only OTA metadata and application. It aborts on a base mismatch. It is **not an OTA bootstrap tool** and must not be repurposed for signed-update trials.

The silent generated configuration historically retained MultiNet5 despite newer defaults. Inspect/regenerate it before relying on silent-profile builds. Do not build AFE profiles concurrently: ESP-SR model packaging shares a managed-component target directory.

If USB genuinely becomes unresponsive, bounded serial retries are appropriate. A physical BOOT+RESET or unplug/reconnect previously restored it. Do not repeatedly reset a healthy device or ask the user to reconnect before checking access/process ownership.

## Voice implementation already present

Read `docs/m8-audio-runtime.md`, `firmware/main/body_audio.c`, the device voice/link implementation, and `packages/runtime/src/openai-voice.ts` before altering the audio pipeline.

- User heard and approved complete, clear speech after earlier crackling/truncation fixes. Speaker95/100 is the accepted bench setting.
- Protocol1.2 uses native16 kHz s16le microphone upload; server resamples to24 kHz. Downlink remains24 kHz. Old1.0/1.1 clients remain supported at24 kHz.
- Capture: 300 ms pre-roll and a bounded5.12-second PSRAM queue while provider startup completes; overflow fails closed.
- Playback: 384 KB/8-second queue, streaming63-tap FIR, I²S DMA pacing. The extra10 ms sleep that caused underruns was removed.
- AFE: MR, AEC enabled, AGC off, VAD mode3, minimum96 ms speech,64 ms delay. Local VAD interruption and provider cancellation are implemented.
- Natural acoustic interruption worked in an earlier sequence4 test, but volume95 echo later cancelled every ordinary response. Sequence6 deliberately suppresses uplink audio during playback and its300ms tail. USB interruption remains deterministic; spoken barge-in is open again pending measured echo-resistant admission.
- Lifecycle protections include session/generation fencing, immediate cancellation, bounded provider sessions and no automatic audio resumption after connection loss.
- Earlier physical successes used the stock wake phrase. They **do not validate Hey Marvin**.

Newest source guards (compiled, not all flashed):

- `MARVIN_WAKE_AUTOSTART` defaults off until acceptance.
- No stock HiESP fallback in `firmware/afe/local_afe.c`.
- `micro_wake.cpp` validates model/schema, input/output shapes and quantization; registers13 required operators; uses64 KiB tensor and4 KiB variable arenas; cleans up failed initialization; resets stream state and uses a stride-aware two-second settling period.
- `body-audio-smoke.py` now explicitly confirms diagnostic wake arming, uses this-run wake/interruption counter deltas and suppresses activation afterward. Python compilation passed; this revision has not had a live voice trial.
- `wake-corpus.py` requires suppressed activation, continuous/fresh AFE status, exactly one detection per positive, zero per negative, no out-of-case detections and no microphone uploads. It records link faults without automatically failing a local-only detector trial. It leaves activation suppressed afterward.

Console diagnostics: `s` status; `w` suppress wake activation/stop voice; `W` arm wake activation; `v` explicitly start voice; `x` stop voice; `i` interrupt; `+`/`-` adjust volume by5. `v` may start a billable provider session. Do not use console activation to claim acoustic wake passed.

## Wake investigation: failures and evidence

Read `docs/hey-marvin-wake.md`. Names below distinguish **public model versions** from **our private training experiments**.

| Detector/test | Positive detections | Negative false activations | Evidence under `work/` |
| --- | --- | --- | --- |
| MultiNet5 native threshold.50 | 1/20 | 0/20 | `board/wake-corpus-1789321830550.json` |
| Public microWakeWord V3, physical | 16/20 | 10/20 | `board/wake-corpus-1789325171360.json` |
| Public microWakeWord V1, physical | 18/20 | 9/20 | `board/wake-corpus-1789325970999.json` |
| MultiNet6 segmented graph, physical | 0/5 | 0/5 | `board/wake-corpus-1789326338239.json` |
| Public V1, direct host audio | 19/20 | 10/20 | `wake-research/reference-corpus.json` |
| Public V3, direct host audio | 17/20 | 9/20 | same file |
| Local training V1, direct | 20/20 | 20/20 | `wake-research/custom-v1-reference-corpus.json` |
| Local training V2, direct | 14/20 | 13/20 | `wake-research/custom-v2-reference-corpus.json` |
| Local training V3, original direct corpus | 20/20 | 20/20 | `wake-research/custom-v3-reference-corpus.json` |
| Local training V3, reserved Daniel/Tessa voices | 8/8 | 160/160 | `wake-research/custom-v3-heldout-corpus.json` |

The local training candidates and MultiNet experiments are rejected. Public V1 is selected only for the sensitivity-first alpha; its negative results remain disqualifying for the original production acoustic gate. Increasing a confidence cutoff alone cannot solve negatives scored at maximum confidence.

Other findings:

- MultiNet6 often labels Hey Marvin as HEY MARTIN. The segmented graph has15 competing phrases, a320 ms history, VAD-controlled segments and800 ms silence tail. Only command1 can activate voice. Current source retains this experimental branch.
- MultiNet7 exceeded the existing OTA application slot and was not flashed. Do not casually enlarge partitions to fit it.
- The firmware's exact pinned C microfrontend was compiled on the host and matched all40 bins over674 feature windows against `pymicro-features`2.0.2 after accounting for its25.6 float scaling. See `compare-frontends.py` and `frontend-reference.c/.dylib` under research.
- The initial host reference script mistakenly treated those floats as raw features; that was corrected before the reported results. Firmware's feature scaling already matched the ESPHome reference.
- Local training V1 kept178 frames while the architecture needs214. V2/V3 corrected context length and expanded negative sliding windows, but did **not** resolve false activations.
- `conversion-check-v3.json`: negative “Hey Mary” scored about.995 nonstreaming and.985 float streaming, before quantization. This rules out quantization as the sole explanation for that example, not every possible pipeline defect.
- High nonstreaming end-window validation metrics were misleading. Preserve the failed results; do not cherry-pick a favorable checkpoint metric as acceptance.

### Research environment and artifacts

All under `work/wake-research/`:

- `venv`: Python3.12, TensorFlow2.18.1, pymicro-features2.0.2, soundfile and the trainer dependencies. Separate from ESP-IDF's Python.
- `micro-wake-word`: OHF Apache-2.0 source at commit `4665173cd35f1cff9a61e06fc427f124766c488e`.
- One local trainer patch in `microwakeword/utils.py`: discard the incomplete final stride in representative quantization samples rather than assert context length is stride-divisible. It does not change model shape/weights.
- `prepare-training.py`, `prepare-training-v2.py`, `prepare-training-v3.py`; matching `run-training*.sh`, `export-training*.py`, logs and `training-v*/` weights/features/manifests.
- `reference-corpus.py`, `custom-reference-corpus*.py`, `heldout-reference-v3.py`, `check-conversion*.py` and JSON/log results.
- `heldout-natural/manifest.json`:168 generated cases, Daniel/Tessa reserved from V3 training.
- `board/hey-marvin-corpus/manifest.json` is actually under `work/board/`: original40 generated clips,5 voices ×4 positive rates and20 negatives; `interleaved.json` provides balanced ordering.
- V3 training uses Eddy/Flo/Reed/Rocko plus Samantha/Karen/Moira. Validation uses Sandy/Shelley. Original corpus Samantha/Karen/Moira voices are therefore **no longer held out** after V3. No human audio was recorded; no external training service was contacted.
- Private macOS TTS-generated feasibility datasets are not a production-data/provenance clearance or representative human acoustic benchmark.

Public model provenance:

- V1: Tater-Wake-Words commit `740bd31af8f28b1700daf49c36444d278715d298`, `microWakeWordsV1/hey_marvin.tflite`,63,536 bytes; SHA256 `f8297bc0e1d42e173e4a47e09f073d93aeddc5fb3aec9791cc5afeac5c431f50`.
- V3: microWakeWords commit `e2e4f5ad41b7c944016d95350fc9d6fa17f3fa8f`, `microWakeWordsV3/hey_marvin.tflite`,63,520 bytes; SHA256 `32d550a8dae155ceb407981df189e6380f0abaf4545534b132821ef97036584a`.
- `firmware/tools/fetch-wake-model.py` fetches pinned variants and refuses to overwrite an unknown custom asset. Public model redistribution licensing was not established; evaluation only. Model files are ignored by Git.

## Concrete next steps

1. The latest M6 simulator soak has finished but is not accepted because of its terminal observation gap. Use the corrected harness for any future24-hour release acceptance run; do not reinterpret the retained result as a pass.
2. Continue only the unavailable M7 acceptance cases if new infrastructure is supplied: a second physical AP, laptop-only-AP isolation, commit-boundary power cuts, full reset/offline unlink-relink, rate trials and participant evaluation. Sequence5 and direct Chrome Web Bluetooth already pass the available single-AP matrix; preserve sequence4 as fallback and the current owner/factory state.
3. Record sanitized provisioning evidence without claiming a second AP, arbitrary physical power cuts, physical reset or participant trials unless actually performed.
4. M6 post-run validation and sanitized evidence are complete. A new continuous24-hour run remains an acceptance gate.
5. Do not spend more alpha time optimizing wake. Preserve the measured false positives and production acoustic gate; revisit only with representative data and a licensed model path.

## Software architecture and useful commands

- `apps/web`: React/Vite portal. `apps/server`: Fastify API, authentication, WebSocket and deployment composition.
- `packages/contracts`: schemas/protocols; `packages/persistence`: SQLite/PostgreSQL; `packages/runtime`: canonical conversation/runtime, provider adapters, Entire integration; `firmware`: ESP-IDF robot.
- `docs/architecture.md`, `docs/experience-and-provisioning.md`, `docs/implementation-plan.md`: design and original measurable gates.
- `tests/acceptance/results`: sanitized evidence with scope limitations. Private verbose logs live in ignored `work/`.

From the project root, after resumption as needed:

```sh
npm run dev
npm run check
npm run test:postgres
npm run test:e2e
```

`npm run check` runs typecheck, Vitest and production build. The latest full check passes162 tests, typecheck and the production build. The idle-audio regression now completes the native hello handshake before proving that an authenticated but inactive voice transport is rejected. Delivery status also records53 PostgreSQL tests and14 browser tests from earlier scoped runs. Native browser sandbox limitations were historically worked around with the official Playwright Linux container, not application security bypasses.

Live smoke commands (`npm run smoke:provider`, `npm run smoke:voice`, `npm run entire:check`) may call external services. Do not print their credentials. The last selected OpenAI models were `gpt-5.4-mini-2026-03-17` for text, `gpt-5.4-2026-03-05` for repository reasoning, `gpt-realtime-2.1` for voice, and `gpt-4o-mini-transcribe` for transcription. Inspect configuration names safely if verifying current settings; do not blindly replace them with newer models.

Entire local CLI reads and the20-question repository review succeeded after full keychain access was available. This adapter is deliberately rejected for hosted OIDC use. See `docs/m4-entire.md` and `docs/decisions/0004-entire-integration.md`; never repurpose the CLI OAuth client as portal login.

## Milestones still requiring acceptance

Use `docs/implementation-plan.md` as the original contract and `docs/delivery-status.md` for broader evidence. Some prose there predates the latest successful audio work; this handoff and `docs/hey-marvin-wake.md` give the current wake state. Do not rewrite gates to match available fixtures.

- Earlier milestones still have external/representative gates: approved Entire identity/hosted API contract, real browser BLE interoperability and different-AP/power-loss provisioning matrix, accessibility/participant evaluation and broader semantic continuity. A runnable preview is not full acceptance.
- **M6:** the latest run provided83,587seconds of healthy continuous evidence but failed final24-hour validation because of a7,119-second terminal observation gap. The harness is corrected; a new continuous24-hour run and the physical command/reconnect matrix remain incomplete.
- **M7:** signed `afe-v1` sequence6 is installed and retains sequence5's passing direct Chrome-to-board Security2 behavior. The available single-AP matrix covers robot scans, unsupported-network disabling, stale-scan rejection, visible and hidden application, wrong-password rollback, BLE-loss resume, owner/epoch continuity and native presence. A second AP, laptop-only-AP isolation, arbitrary power cuts, physical reset/offline unlink-relink, rate trials and participant evaluation remain.
- **M8:** the sensitivity-first Hey Marvin alpha is selected with known poor hard-negative performance. The full eight-hour physical resource soak now passes, but the harness cannot measure supply power or enclosure thermals. The original ≥95% representative quiet/noisy wake and ≤1 false wake/hour gate and unavailable peripheral evidence remain open.
- **M9:**20 actual web-text→web-voice→body-voice→web-text journeys;100-observation latency/routing checks and physical action/safety evidence. Standalone audio board cannot prove absent actuators/sensors.
- **M10:** live second-provider credentials and parity, local/cloud suite parity and three independent installation trials. Anthropic/Deepgram adapters and fixtures exist; fixtures do not establish live parity.
- **M11:** preserving signed OTA bootstrap,10/10 health-rejected update recovery and clean sequence4→5→6 one-device rollout are complete. Sequence5 remains the fallback. Real-provider load, production recovery/rotation, all earlier release blockers and a seven-day ten-user beta remain. Normal bench flashing resets OTA metadata and is not the preserving bootstrap. No eFuses were programmed.

Development is active under the user's instruction to complete all implementable milestone work.

## Repository checkpoint scope

Repository: `https://github.com/spedemon/marvin.git`, branch `main`. The agent and standalone-pet histories were unified after this checkpoint; use the root README and current development checkpoint for the present repository layout. The checkpoint includes project source, tests, sanitized acceptance evidence, firmware dependencies vendored with their licenses, and this handoff. Private configuration, `work/` experiments/environments/backups, generated build trees, temporary diagnostic audio embeddings, unapproved model binaries, and unused upstream WebSocket examples containing demo keys remain local/ignored. This is not a release or milestone-acceptance commit.
