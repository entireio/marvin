# Firmware tools

These commands build, inspect or exercise the Waveshare ESP32-S3 board. Run
them from the repository root. They use the checked-in ESP-IDF project and the
private toolchain under `work/`; none is a general-purpose flasher.

## Choose the right command

| Goal | Command | Writes hardware |
| --- | --- | --- |
| Build the daily local firmware | `sh firmware/tools/build-waveshare-local-dev.sh` | No |
| Build the audible motion firmware | `sh firmware/tools/build-waveshare-motion-afe.sh` | No |
| Build the cloud release firmware | `sh firmware/tools/build-waveshare-release-v3.sh` | No |
| Inspect a proposed flash | `python3 firmware/tools/board-flash.py --backup PATH --profile PROFILE ...` | No |
| Fast local-dev application update | Add `--app-only --flash` to the local-dev flash command | Application only |
| Install an audible motion image | `MARVIN_FLASH=1 sh firmware/tools/build-and-flash-motion-afe.sh` | OTA metadata and application only |
| Install a cloud release on a matching v3 base | `python3 firmware/tools/board-flash.py --profile release-v3 --reuse-verified-base ... --flash` | OTA metadata and application only |
| Capture a new board backup | `python3 firmware/tools/board-backup.py --port PORT` | No |
| Migrate an AFE v1 development board to 4 MiB slots | `python3 firmware/tools/migrate-afe-v3-local-dev.py ...` | Model, application, partition table |
| Run the diagnostic console | `python3 firmware/tools/board-monitor.py --port PORT` | No |

Build helpers regenerate their profile configuration. They do not flash. A
successful build is not proof of correct wiring, ownership, network setup, or
motion safety.

## Flashing rules

1. Use a fresh, verified full backup from `board-backup.py` for the exact
   board. Never use a backup from another board.
2. First run `python3 firmware/tools/board-flash.py` without `--flash` and
   review its JSON plan.
   It performs no serial access in plan mode.
3. Add `--flash` only after the selected port, backup, profile and factory
   directory are correct. The helper checks the board MAC, security state,
   flash size, generated image layout and hashes before writing.
4. `--app-only` is local-dev only. It writes only the application partition
   and preserves factory, Wi-Fi/owner state, bootloader, partition table,
   model and OTA metadata. Before it writes, it verifies both the installed
   AFE partition table and wake model, so a stale table cannot orphan a model
   at an otherwise-valid raw flash offset. It cannot bootstrap signed updates.
5. `--reuse-verified-base` verifies the installed bootloader, partition table,
   wake model and factory partition against the reviewed build. It writes only
   OTA metadata and the application. A mismatch stops the operation; it does
   not overwrite the factory partition to make the check pass.

The daily local-development build uses the `afe-v3` 16 MB layout: two 4 MiB
OTA slots followed by the existing 4 MiB wake-model partition. Its build fails
if less than 10% of an application slot remains. Layout migration is separate
from app-only flashing and requires a fresh double-read backup.

The `build-and-flash-motion-afe.sh` wrapper adds a second explicit guard:
`MARVIN_FLASH=1`. It discovers the newest private backup/factory directories
only as a convenience. Set `MARVIN_BACKUP`, `MARVIN_FACTORY`, and `MARVIN_PORT`
yourself when any ambiguity exists.

Never flash at an edge, during a physical test, or while another process owns
the serial port. The EEP hardware pull-down and power wiring requirements in
[the firmware guide](../README.md) still apply before any track test.

## Supporting tools

- `ble-bridge.py`, `ble-smoke.ts`, and `owner-ble-smoke.ts` exercise the
  existing Security2 provisioning service. They use private factory material
  and must not print or commit it.
- `ble_voice_bridge.py` is an unfinished macOS BLE-to-WebSocket prototype. The
  Pet does not yet expose its expected `ff60` GATT voice service, so this is
  not an operational deployment command.
- `board-smoke.py`, `body-audio-smoke.py`, and `board-runtime-soak.py` record
  bounded diagnostics. Their results are evidence for their stated scope, not
  a broad acoustic or release acceptance claim.
- OTA, migration, update and wake-corpus scripts are specialized recovery or
  evidence tools. Read their corresponding document in `docs/` before running
  them; do not substitute them for `board-flash.py`.
- `recovery-trust.py` derives a factory CSV containing the public fleet-return
  recovery key without changing device identity. The private recovery key must
  remain outside firmware and deployment files; see `docs/pet-lifecycle.md`.
