# ESP32-S3 firmware — bench provisioning and Waveshare diagnostics

Original ESP-IDF **v5.4.2** C firmware targeting the Waveshare ESP32-S3-AUDIO-Board (`esp32s3`). No previous Arduino/SuperMini board assignments are reused. Conservative baseline: 4 MB flash, no PSRAM required, two 1.875 MB app partitions. Verify the exact carrier, regulator, antenna, USB/UART and peripheral wiring before flashing.

## Wired actuators

The installed dual DC motor module is a **DRV8833** carrier. `EEP` is its active-high nSLEEP input: low disables both H-bridges.

| ESP32-S3 GPIO | Connection | Output |
| --- | --- | --- |
| 7 / 6 | IN1 / IN2 | OUT1 left track −, OUT2 left track + |
| 5 / 4 | IN3 / IN4 | OUT3 right track −, OUT4 right track + |
| 3 | EEP (DRV8833 nSLEEP) | Low at startup and after stopping |
| 8 / 9 | Head tilt / rotation servo signal | 50 Hz, 1–2 ms pulses on explicit command |

`actuators.c` configures 20 kHz motor PWM, holds EEP low while idle, raises it only after a bounded track command has set the inputs, and lowers it before clearing them on stop or expiry. Each track segment lasts at most 500 ms. Positive track speed selects IN2/IN4 because the motor positive leads are on OUT2/OUT4. The head outputs start without pulses and are configured only when explicitly commanded. The previous GPIO4/5/6 audio button placeholders are disabled by default.

The authenticated body voice link supports `head`, `tracks`, and named `motion` commands with boot ID, epoch, deadline and a write-ahead command ID ledger. Named motions are `forward_bit` (350 ms), `turn_right` and `turn_left` (two 500 ms segments), and `move_around` (a short forward segment and two gentle arcs). These are timed open-loop movements: actual distance and turn angle require physical calibration. Small head tilts mark **listening**, **thinking**, and **speaking** after those states are actually reached; a spoken look direction becomes the new resting pose.

Track motion remains **off by default** because no cliff or pickup sensor is connected. For the audible supervised profile, run `firmware/tools/build-waveshare-motion-afe.sh`. It includes the local Hey Marvin wake engine and `CONFIG_MARVIN_TRACK_BENCH_MODE=y`; both must be verified in the generated config before flashing. Send `B` on the local firmware console to arm a 120-second window. The device advertises named motions only in this bench profile, and checks the local arm again before every segment. The window expires automatically, and disconnect, cancel, or a command deadline puts EEP low. Without the bench build and arm, spoken track requests receive a refusal. Never run these tests near an edge.

**Boot and reset safety requires hardware:** GPIO3 is not driven by firmware until `app_main` runs. On DRV8833 carriers with an EEP pull-up jumper (often marked J1), open that jumper and provide an external pull-down from EEP to ground, for example 10 kΩ, so EEP remains low while the ESP32-S3 is reset, unpowered, flashing, or booting. Verify the actual carrier circuit and measure EEP low through a full power cycle before connecting the tracks. The chip's internal 500 kΩ pull-down is weak; an onboard pull-up can override it. Firmware alone cannot guarantee an idle driver during boot.

GPIO19/20 are the ESP32-S3 native USB pair on this board. Do not connect servo signals to them: USB traffic can look like servo pulses and cause random motion. GPIO8/9 are direct MCU pins also routed to the optional screen interface; do not attach a screen that drives those lines at the same time. Add a pull-down to each servo signal so it stays low before firmware configures PWM. GPIO3 is a boot strapping pin: confirm that the EEP pull-down allows normal boot. Power servos and motors from suitable supplies with a common ground; check the carrier's EEP voltage before connecting it directly to the 3.3 V MCU GPIO.

## Build

Install Espressif ESP-IDF v5.4.2 and its esp32s3 tools using the official installation workflow, then source `export.sh`:

```sh
idf.py -C firmware build
idf.py -C firmware size
```

For the toolchain installed during this task:

```sh
export IDF_TOOLS_PATH="$PWD/work/idf-tools"
. ./work/esp-idf/export.sh
IDF_COMPONENT_MANAGER=0 idf.py -C firmware build
```

Disabling the optional component manager is supported here because dependencies are bundled with ESP-IDF or vendored under `components` with provenance. It avoids process enumeration prohibited by this host's sandbox. No SDK source patches were needed. The original compile is recorded under `tests/acceptance/results/M00`. A Waveshare ESP32-S3-AUDIO-Board is now connected and backed up; both diagnostic and provisioning profiles have been flashed, and encrypted radio scans now pass.

## Unique setup credentials

The firmware fails closed when the dedicated `factory` NVS partition lacks a 16-byte SRP salt and 384-byte verifier. There is no shared/default setup password. Generate a unique credential per module, in an excluded private directory:

```sh
python firmware/tools/factory.py --output work/device-001
python "$IDF_PATH/components/nvs_flash/nvs_partition_generator/nvs_partition_gen.py" generate work/device-001/factory.csv work/device-001/factory.bin 0x6000
```

The generator creates private files (0600, directory0700) without printing credentials. Retain the setup secret offline with the physical robot. `factory.bin` belongs at **0x12000** in this partition table. Review the carrier/port and existing flash before using `idf.py -C firmware -p PORT flash` and Espressif's `write_flash 0x12000 work/device-001/factory.bin` command. The guarded board flash helper requires an explicit `--flash`; it never programs security eFuses. Production protected identity, signed firmware and encrypted NVS require M7/M11 design; the bench NVS is not protected against physical flash extraction.

## Protocol and bench sequence

Each boot opens an authenticated five-minute BLE setup window, then stops BLE. Power cycling is the bench physical-presence gesture. Production reconfiguration must add fresh owner-bound authorization; knowledge of the setup secret alone is intentionally **not** advertised as sufficient for production ownership.

Service `0000ff50-0000-1000-8000-00805f9b34fb`; characteristics use `ff51` version, `ff52` `prov-session`, `ff53` `marvin-control`. Standard Espressif Protocomm Security 2 patch1 provides SRP6a mutual proof and AES-GCM with counter nonces. Version is public; controls are encrypted after authentication. No homemade cryptography. One client session at a time.

Encrypted JSON commands (maximum384 bytes):

- `{"op":"scan"}` starts an asynchronous **robot radio scan**. Poll `status` until `scan_complete`.
- `{"op":"status"}` reports phase/error, scan generation/count, saved-network flag and `linked:false`.
- `{"op":"network","index":0}` returns one robot-observed network, RSSI/channel and security support; bounded to32 observations. Read indices separately to stay below BLE response limits.
- `{"op":"apply","index":0,"scanGeneration":1,"password":"…"}` tests a network from that exact scan. Stale indices, enterprise/WEP/WPA3-only APs and invalid credentials are rejected. Baseline supports visible 2.4 GHz open and WPA2-Personal (including WPA/WPA2 mixed) networks; hidden SSIDs are deferred to M7. UI hidden-network behavior remains simulation only.

`WIFI_STORAGE_RAM` stages the candidate without replacing saved NVS credentials. Success requires DHCP, a clock from the authenticated BLE session (or SNTP fallback), and certificate-verified HTTPS200 from a fixed configured backend health URL. Set **Marvin → probe URL** in `idf.py -C firmware menuconfig`. Empty URL rejects apply. A laptop's localhost is not reachable from the robot; use a robot-reachable HTTPS server. No TLS bypass or redirect following. The result is `network_verified`, **not account linked**. Failed connection/probe/storage restores the previous network, and failed restoration is represented by the failed state rather than a ready claim. Power loss before commit leaves the prior NVS record. Credentials are not logged.

The bench client uses Espressif's Security2 implementation and `bleak` in your activated Python environment:

```sh
python -m pip install -r firmware/tools/requirements.txt
python firmware/tools/bench.py --credentials work/device-001/setup-secret.json
python firmware/tools/bench.py --credentials work/device-001/setup-secret.json --apply
```

It prompts locally for network index/password, never stores the Wi-Fi password, and does not call Marvin's account APIs. Do not turn on verbose security logging. The TypeScript Security2 client has now passed physical radio/network tests below. The standalone Web Bluetooth adapter has operation deadlines, disconnect cleanup and version rejection tests; live browser interoperability and M7 ownership authorization remain required before portal setup is enabled.

M0 HIL acceptance: ten consecutive encrypted scan/connect cycles, including reconfiguration; bad proof rejection; laptop-only AP absent; wrong password/DHCP/DNS/TLS failure rollback; power-cycle persistence; setup-window closure. Record board ordering code, flash/heap, browser/client OS, AP security/RSSI, timings and every failure. Firmware compilation alone does not satisfy this gate.

## Waveshare ESP32-S3-AUDIO-Board diagnostic

`firmware/tools/build-waveshare.sh` builds the separate `build-waveshare` profile for 16 MB flash and 8 MB octal PSRAM. It starts a local USB console before any networking: `t` plays a quiet half-second test tone, `m` measures microphone levels, and the amplifier is otherwise muted. It does not run enrollment, robot voice, or motion. Codec source and provenance are documented in `components/README.md`.

The original board flash has been read twice and verified in `work/board/backup-20260912T221258Z`. The guarded `board-flash.py --backup PATH` command validates and previews the diagnostic flash plan; only adding `--flash` writes it. It checks the board identity, flash capacity and security state. It never programs eFuses. `board-monitor.py` provides the text-only diagnostic console. Full system access now permits direct serial and BLE tests from this task.

Factory generation now also creates a per-device P-256 identity key and public key. Firmware can sign setup challenges inside Security2. Full ticket verification, enrollment redemption, persistent ownership recovery and production BLE browser transport are unfinished; this image must not be advertised as account-linked firmware.


## Measured board evidence — 12 September 2026

The first audio image exposed a main-task stack overflow. Static audio buffers and an 8192-byte diagnostic stack fixed it. Ten subsequent two-second microphone measurements and the quiet tone transfer passed, with 5136 bytes minimum free stack. Slots 1 and 3 carry the two microphones. This proves local data transfer and mute control, not acoustic quality, echo cancellation, wake-word accuracy, or the complete M8 gate. `board-smoke.py --cycles 10 --tone` reproduces the numeric test without saving audio.

Build the separate BLE bench profile with `sh firmware/tools/build-waveshare-provisioning.sh`. The flash helper accepts `--profile provisioning --factory PRIVATE_FACTORY_DIRECTORY --backup VERIFIED_BACKUP_DIRECTORY --flash`; it validates board identity and image layout and includes the unique factory NVS partition. Never reuse one robot's factory directory for another robot.

`node --import tsx scripts/ble-smoke.ts PRIVATE_FACTORY_DIRECTORY` exercises the same TypeScript SRP/AES-GCM implementation used by the portal project through a private Python BLE transport. It requests protocol version first, requires Security2 patch1, proves the P-256 identity and reads only robot-observed networks. Ten complete physical scan trials passed; an incorrect setup secret was rejected. Results omit SSIDs and secrets. These tests do not apply Wi-Fi or redeem an ownership ticket.

The bounded transfer component has host tests for payload limits, duplicate integrity, expiry, session isolation and clearing. It is not yet wired into the enrollment endpoint. Complete firmware ticket verification/redemption, power-loss recovery and UI integration are still required before enabling real account setup.

## Wi-Fi recovery evidence and private test configuration

Create `work/board/wifi-test.json` locally with only `ssid` and `password`, mode 0600. The TypeScript smoke runner reads it into memory and sends the password only inside the authenticated BLE session. Do not commit or print it. Add `--wifi-file work/board/wifi-test.json` to `scripts/ble-smoke.ts` to apply that robot-visible network. `--wrong-wifi` tests an intentionally incorrect password, while `--verify-saved` first requires a saved network and an active connection after reset. Results exclude SSIDs and credentials.

The authenticated `clock` operation accepts UTC milliseconds before applying Wi-Fi. TLS still checks certificate validity and the configured trust root. For the local bench server, `bench-trust.py` adds only its HTTPS health URL and public CA to the unique factory directory; regenerate its NVS image afterward. Never transfer a CA private key to the robot. This is explicit bench trust, not finished production enrollment bootstrap.

Ten real encrypted scan/connect cycles passed in `tests/acceptance/results/M07/physical-wifi-matrix.json`, with Wi-Fi application/HTTPS verification taking 3.69–4.90 seconds. Trials include resets and reuse of the previously committed AP. A wrong-password trial took 22.8 seconds and restored the prior active connection. `status.networkConnected` distinguishes actual reconnection from mere retention of saved credentials; failed recovery reports `PREVIOUS_NETWORK_UNAVAILABLE`. These are same-AP bench tests, not the M7 20 first-time/20 different-location ownership trials.

The audio diagnostic also supports `a`: a bounded numeric 440 Hz acoustic loopback test with no saved audio. After increasing the original inaudible tone, the two microphones measured tone-energy increases of 42.47 dB and 11.43 dB. This establishes an acoustic path, not speech intelligibility, echo cancellation or wake-word performance.

Automatic USB reset remains intermittent. A successful BLE operation does not prove the ROM bootloader is reachable. The flash helper stops when board identity cannot be read; it never proceeds on an assumed connection. Preserve the backup and unique factory identity during recovery.

## Owner enrollment profile

`build-waveshare-owner.sh` builds the separate owner-authorized profile with signed tickets, a durable submitted journal and HTTPS redemption. It has passed physical claim, saved-owner recovery, same-AP network change and authenticated WSS presence on the development board. The bench profile remains separate. Native browser BLE, different-AP/power-loss matrices and broader acceptance remain open; see [physical setup status](../docs/m7-physical-setup.md).

## Signed release profile

`build-waveshare-release.sh` performs a clean rollback-enabled audible build with the selected Hey Marvin alpha detector and volume95. `build-waveshare-ota-failure.sh` builds a separate silent, deliberately unhealthy trial image for guarded rollback testing; never publish that profile as a release. `ota-bootstrap-install.py` is the only supported first-installation helper for this partition layout. It requires a fresh verified double-read snapshot, a signed bootstrap bundle, matching factory trust, an attested prior application and an explicit `--flash`. It preserves runtime NVS, PHY data, the prior application and the AFE model, then writes OTA selection metadata last. It never changes eFuses.

After bootstrap, `ota-boot-check.py` verifies physical confirmation, connectivity, audio and advancing AFE processing. `ota-rollback-trials.py` exercises signed download, quiescence, an intentionally failed90-second trial, bootloader rollback and recovered release health. The local `uSEQUENCE` console command is a development/operator trigger; firmware does not poll for or automatically install releases. See [signed firmware updates](../docs/m11-firmware-updates.md).

At the approved volume95, the physical speaker's residual echo caused sequence5 to classify its own first words as barge-in and cancel playback. Sequence6 suppresses AFE output from the network uplink while playback is active and for a300ms tail. Capture clocks and AFE processing continue. This is intentionally half-duplex: it prevents self-interruption, while acoustic barge-in remains deferred pending measured AEC alignment. `body-audio-smoke.py` includes suppressed echo samples when checking continuous audio processing.
