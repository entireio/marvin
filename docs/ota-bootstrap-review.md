# Initial OTA bootstrap review and guarded installer

The existing bench flasher writes the application toota0 and resets OTA selection metadata. Reusing it for the OTA profile would not establish the verified fallback required by the new installer guard. It remains intentionally unsupported for that profile.

Inspection of the pinned ESP-IDF5.4.2 sources confirms two32-byte selection entries in separate4 KiB sectors. Each has a sequence, state and CRC covering only the sequence. The active slot is derived from the highest valid sequence. NEW transitions to PENDING_VERIFY on first boot; an unconfirmed trial is subsequently ABORTED. VALID and UNDEFINED have different health semantics even though both can be selected. A valid CRC alone proves neither image integrity nor successful health checks.

`firmware/tools/ota_metadata.py` is an offline format helper with no command-line writer or serial functionality. Tests compare generated CRCs directly with the pinned ESP-IDF ROM CRC implementation, then cover selection, invalid/aborted entries and corruption. This is format validation, not a bootloader or power-loss test.

Before constructing a real installation sequence after the soak:

- Read and verify a **fresh** complete board backup. The original vendor backup is recovery evidence, not a snapshot of current ownership, application or model state.
- Identify the actually selected/running application and validate its ESP image, partition map, model data and current factory identity. Preserve the owner NVS and the existing usable application bytes.
- Stage the experimental OTA application into the inactive slot. Preserve the partition layout. Review bootloader compatibility and any factory-trust addition against the fresh snapshot; do not regenerate device identity.
- Define explicit initial-health confirmation. Marking the candidate VALID during installation bypasses trial-health verification; marking it NEW without corresponding signed pending metadata would be refused by the current confirmation code. Neither shortcut is an accepted bootstrap procedure.
- Retain a USB recovery route and a known selectable prior image before changing boot selection. The operator must be present for the first bootstrap. No automatic unattended initial install is authorized by the implementation itself.

The implemented design uses a narrowly scoped signed bootstrap capsule and pending trial record while preserving existing NVS. Subsequent application-only signed update trials exercise the normal runtime, including the original ten physical recovery trials.

Relevant pinned sources: `work/esp-idf/components/bootloader_support/include/esp_flash_partitions.h`, `bootloader_support/src/bootloader_common_loader.c`, `app_update/esp_ota_ops.c`, and `esp_rom/linux/esp_rom_crc.c`.

## Implemented design update

The chosen implementation uses a public **signed factory bootstrap capsule**. See `m11-firmware-updates.md`. The trial runtime validates the capsule, current image, initial sequence, absence of another pending update and existence of a usable fallback after normal health checks. It then creates its own pending metadata without rewriting owner NVS.

`firmware/tools/ota-bootstrap-install.py` now enforces this plan. It requires a verified double-read16MB snapshot, a reviewed rollback-enabled build, a matching signed bundle/public key, an attested prior application and a compatible partition table. It verifies the current slot and partition on the physical board, preserves runtime NVS, PHY data, the prior application and AFE model, writes the boot selector last, and never changes eFuses. The first physical sequence1 installation passed: the new trial boot confirmed in12.863seconds, returned online, exposed audio and advanced AFE processing. Sanitized evidence is in `tests/acceptance/results/M11/physical-signed-bootstrap.json`.

The legacy bench flasher remains inappropriate for OTA bootstrap. Production secure boot, flash encryption and protected release-key storage remain separate hardware-release work.
