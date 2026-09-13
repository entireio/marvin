# Initial OTA bootstrap review — not an installation procedure

The existing bench flasher writes the application toota0 and resets OTA selection metadata. Reusing it for the OTA profile would not establish the verified fallback required by the new installer guard. It remains intentionally unsupported for that profile.

Inspection of the pinned ESP-IDF5.4.2 sources confirms two32-byte selection entries in separate4 KiB sectors. Each has a sequence, state and CRC covering only the sequence. The active slot is derived from the highest valid sequence. NEW transitions to PENDING_VERIFY on first boot; an unconfirmed trial is subsequently ABORTED. VALID and UNDEFINED have different health semantics even though both can be selected. A valid CRC alone proves neither image integrity nor successful health checks.

`firmware/tools/ota_metadata.py` is an offline format helper with no command-line writer or serial functionality. Tests compare generated CRCs directly with the pinned ESP-IDF ROM CRC implementation, then cover selection, invalid/aborted entries and corruption. This is format validation, not a bootloader or power-loss test.

Before constructing a real installation sequence after the soak:

- Read and verify a **fresh** complete board backup. The original vendor backup is recovery evidence, not a snapshot of current ownership, application or model state.
- Identify the actually selected/running application and validate its ESP image, partition map, model data and current factory identity. Preserve the owner NVS and the existing usable application bytes.
- Stage the experimental OTA application into the inactive slot. Preserve the partition layout. Review bootloader compatibility and any factory-trust addition against the fresh snapshot; do not regenerate device identity.
- Define explicit initial-health confirmation. Marking the candidate VALID during installation bypasses trial-health verification; marking it NEW without corresponding signed pending metadata would be refused by the current confirmation code. Neither shortcut is an accepted bootstrap procedure.
- Retain a USB recovery route and a known selectable prior image before changing boot selection. The operator must be present for the first bootstrap. No automatic unattended initial install is authorized by the implementation itself.

An acceptable next design is a narrowly scoped physical bootstrap confirmation that records initial health without bypassing signed-update verification for later releases, or a signed pending record staged with the new image while preserving existing NVS. This remains a design decision requiring implementation/tests before the initial installation helper is enabled. Subsequent application-only signed update trials then exercise the normal runtime, including the original ten physical recovery trials.

Relevant pinned sources: `work/esp-idf/components/bootloader_support/include/esp_flash_partitions.h`, `bootloader_support/src/bootloader_common_loader.c`, `app_update/esp_ota_ops.c`, and `esp_rom/linux/esp_rom_crc.c`.

## Implemented design update

The chosen implementation uses a public **signed factory bootstrap capsule**, rather than an unsigned manual confirmation. See `m11-firmware-updates.md`. The trial runtime validates the capsule, current image, initial sequence, absence of another pending update and existence of a usable fallback after normal health checks. It then creates its own pending metadata without rewriting owner NVS. Host fault tests pass. The preserving USB installation planner and all physical trials are still pending; this document is not authorization to use the legacy flasher for OTA bootstrap.
