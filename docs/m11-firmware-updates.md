# Signed firmware updates

The update verifier, bounded authenticated transfer, explicit rollout service, ESP-IDF installer, task quiescence, trial-health confirmation and rollback path are integrated. The guarded first installation passed on the physical Waveshare board. Repeated rollback evidence is recorded below. Releases remain operator-created local artifacts; the signing tool does not publish them, and no eFuses are changed.

`scripts/firmware-release.ts` creates a new local directory containing an ESP32-S3 application image, a signed binary manifest and readable metadata. It requires a dedicated P-256 private release key. Keep that key off the application server and robot; provision only the corresponding public key as trusted factory data. The deployment enrollment key is a separate trust domain.

Example, using operator-supplied paths:

```sh
npx tsx scripts/firmware-release.ts --key /private/release-key.pem \
  --image build-waveshare-afe/marvin.bin --sequence 1 \
  --minimum-sequence 0 --layout afe-v1 --out work/releases/1
```

The176-byte manifest has a fixed112-byte payload followed by a64-byte raw ES256 signature. It contains the `MRVOTA01` format marker, unsigned big-endian release sequence and image size, SHA-256 image digest, zero-padded board/layout identifiers, minimum committed prior sequence, and twelve reserved zero bytes. Board and layout identifiers are compared with trusted local values. The image must fit the existing inactive application slot. Model/partition migrations are not application-only OTA updates.

The verifier rejects wrong keys, modified fields, another board/layout, stale or downgraded sequence, an unmet minimum prior version, unknown reserved fields, oversized images, truncation and malformed signatures. The transfer engine verifies this manifest **before** opening the inactive slot. It bounds total bytes, hashes each chunk and only allows next-boot selection after exact size/digest checks and platform image validation. Cancellation and failure never select an image; abort is idempotent. Platform callbacks must guarantee that a failed `begin` cleans up its own partial resources and a failed `select` has not changed the selected boot slot.

`sh firmware/tools/test-update-host.sh` passes15 signed/invalid vectors, all truncations,10,000 malformed envelopes and ten repetitions each of successful transfer plus seven failure/interruption modes under address/undefined-behavior sanitizers. The injected platform writer is a fixture, so these checks complement rather than replace the physical trials.

The remaining hardware-release security work is production secure boot, flash encryption, protected identity/release-key storage and a reviewed rotation ceremony. Those controls are deliberately separate from the development board's verified signed-update path.

## ESP-IDF platform binding

`esp_writer.c` implements inactive-slot begin/write/end/select callbacks, durable pending-image metadata and health-confirmed sequence advancement. It refuses to provide an installer writer when the bootloader lacks rollback support. `ota_runtime.c` owns the application call site, admits one update, checks the owner-bound identity, parks transport/audio, invokes the HTTPS installer, selects the trial and evaluates boot health.

Pending metadata is committed before boot selection. Confirmation compares the running slot and the exact signed file digest. Only a healthy, validated running image advances the committed sequence. If power fails between boot confirmation and the sequence commit, the retained pending record allows idempotent recovery on the next boot. Returning to the previous slot after rollback does not advance the sequence.

The platform code compiles for ESP32-S3. Host tests substitute ESP-IDF/NVS operations and repeat confirmation, failed persistence, recovery after a lost commit, image mismatch, prior-image rollback and stale-version rejection ten times under sanitizers. These are simulated persistence boundaries; the physical suite covers complete signed updates and health-triggered rollback, while flash power-cut injection remains separate.

## Explicit device rollout on the backend

The optional `FIRMWARE_ROLLOUT_FILE` is an absolute path to an operator-maintained JSON file containing `publicKeyFile`, `bundleDirectory`, and an explicit nonempty `deviceIds` allowlist. Both file paths are absolute. The public key must be a PEM public P-256 key; private-key files are refused. The bundle is the local signing tool's `manifest.bin` and `image.bin`. Leave this setting absent to offer no updates. The isolated physical deployment used this exact configuration with one allowlisted device.

At startup, the backend bounds each file read, verifies the manifest signature, board/layout, image size, ESP32-S3 header and exact SHA-256 digest, and retains immutable verified bytes. An operator must restart to change the selected release; edits to bundle files cannot change an in-flight release. No upload, signing, publishing or automatic activation endpoint exists.

Direct authenticated devices may query `GET /api/device/firmware`. Devices outside the explicit rollout receive204. A selected device receives versioned manifest/image paths. Downloads require the current owner-bound device credential on every request; browser cookies or origins are refused. Unlinking invalidates subsequent downloads. Unknown versions or devices cannot retrieve the bundle. The device must still verify the signature, layout, sequence and image itself before installing: server verification is additional protection, not the trust anchor.

Local tests cover signature/image tampering, oversized input, accidental private-key configuration, immutable loaded bytes, targeted downloads, wrong-version denial, browser-origin refusal and credential revocation. The physical update trials exercise these endpoints through the enrolled board credential and TLS origin.

## Firmware HTTPS download component

`marvin_update_download` now downloads a caller-selected sequence from fixed versioned paths on the enrolled HTTPS origin. It accepts only the current device credential format, uses the provisioned CA or the ESP-IDF public bundle, disables redirects, and requires exact response lengths. It verifies the manifest before opening the inactive slot and checks the requested sequence, full image digest, transfer completion, cancellation and a120-second overall deadline before boot selection. Each network operation has a5-second timeout. HTTP credential-bearing debug logs are suppressed. Temporary authorization and audio-sized transfer buffers are cleared.

The ESP32-S3 build passes. An HTTP transport fixture runs80 success/failure trials under sanitizers: redirects, wrong lengths, invalid manifests, truncated bodies, wrong image content, cancellation and deadline expiry never select an image. Invalid origins/tokens are rejected before network access, and a sequence mismatch is rejected before opening the slot. This is component verification, not a real TLS/OTA installation test.

The application loads dedicated factory release trust, admits only one update, quiesces audio, invokes this component, reboots and runs bounded boot-health confirmation. Host fault fixtures and physical signed-release trials cover the integrated path.

## Dedicated factory release trust

New factory identities can include `factory.py --release-public-key /operator/release-public.pem`. For an existing identity, `release-trust.py --factory-csv /private/source.csv --public-key /operator/release-public.pem --output-csv /private/new.csv` creates a separate CSV and preserves every existing identity field. It never overwrites the source or destination, rotates an existing release key, or flashes hardware. It refuses private/non-P-256 keys and reuse of the configured enrollment signing key. Regenerating a factory identity is not a key-update procedure.

The public key is stored as a NUL-terminated `release_pub` blob. `marvin_update_factory_trust` loads it from read-only factory NVS, validates its P-256 public format, obtains the committed release sequence, and binds it to the compiled layout and actual slot size. Its buffer must remain alive during verification/download. Missing or malformed trust fails closed. A dedicated development public release key is installed on the connected board; the private key remains outside firmware and server storage.

Temporary factory fixtures verify new-identity creation with optional trust, identity-preserving augmentation, private file permissions, refusal to overwrite/rotate/reuse keys, duplicate fields and wrong curves. Host loader tests cover valid trust, missing/malformed data, failed sequence reads and buffer/layout limits. The ESP32-S3 build and update sanitizer suite pass. Key storage remains subject to the project's existing physical-security limitations; no eFuses or secure-boot settings were changed.

## Audio shutdown before flash writes

The runtime now exposes one-way shutdown helpers. `marvin_device_link_quiesce` closes the voice/device connection and waits up to10 seconds for the transport task to park. Reconnect backoff becomes interruptible. `marvin_body_quiesce`, called only after transport shutdown, prevents renewed capture, waits up to2 seconds for capture/feed/playback tasks to park, then clears output state and mutes/disables both I2S DMA channels. Lock acquisition is bounded. Failed or partial shutdown must abort installation. These helpers intentionally require reboot to resume normal service; they are not a microphone-mute feature.

The helper code compiles in both AFE and fallback owner profiles. The physical signed-update path invokes both helpers before every flash write; observed `quiescing` and later boot/health events bound the integrated shutdown/restart path. This does not substitute for an oscilloscope-level DMA or amplifier measurement.

## Optional runtime and isolated build

`build-waveshare-release.sh` creates the clean audible release profile with signed updates, bootloader rollback, volume95 and automatic Hey Marvin activation. It regenerates its build/configuration each time and combines the reviewed provisioning, owner, AFE and release defaults. `build-waveshare-ota-failure.sh` creates the isolated silent test image that deliberately rejects trial health; its `MARVIN_OTA_TEST_REJECT_BOOT` flag is absent from the release profile. The older `build-waveshare-ota.sh` remains a silent component-test profile. Normal owner/AFE profiles leave update activation disabled.

In the optional profile, the local serial command `u` followed by a positive decimal release sequence and newline requests that exact release (for example, `u4` then Return). `x` cancels a pending/running update. This is an operator test interface, not a browser rollout control. Only one request is admitted. Preflight requires current owner credentials, online presence, factory public release trust, an inactive slot and a newer sequence. The worker closes transport, parks audio, verifies/downloads the signed release and restarts; failed transfer restarts the prior selected image. Ownership/credential changes, expiration and cancellation are checked during download. No update polling or automatic installation is enabled.

A trial boot has a90-second health window. It requires five consecutive seconds of online device presence, available audio, stack/memory headroom and advancing AFE processing without feed faults before confirming the signed pending image and committed sequence. Failed trial health requests rollback. An already-valid image can reconcile pending sequence metadata after a lost commit; inability to do so does not invalidate that previously usable image. Missing/mismatched pending metadata cannot falsely confirm a trial image.

The runtime is installed on the physical board with a dedicated development release key. The lower-level signature/transfer/HTTP/NVS fixtures provide fault coverage, while the guarded bootstrap and repeated signed-update trials exercise the end-to-end worker and bootloader. The legacy `board-flash.py` path remains disabled for this profile because it resets OTA selection rather than preserving a verified fallback.

## Coordinator fault coverage and fallback prerequisite

The real update coordinator now runs against simulated task/platform boundaries in host tests. Ninety trials cover single-request admission, preflight refusal, cancellation, transport/audio shutdown failure, writer failure, download failure and ownership changes. Separate boot cases cover stable confirmation, unavailable presence/audio, stalled AFE processing, invalid confirmation metadata, and reconciliation on an already-valid image. The tests advance a simulated clock; they are not physical timing measurements.

An additional installer guard requires the running application to be recorded as `VALID` before opening an update writer. A freshly flashed image with missing OTA metadata, or an unconfirmed trial image, cannot serve as the assumed fallback. The initial installation procedure must establish and verify a usable valid image before any update trial; merely flashing the rollback-enabled bootloader is insufficient. The existing flash helper intentionally still refuses the OTA profile; `ota-bootstrap-install.py` is the reviewed preserving procedure.

## Signed first-installation capsule

The optional runtime supports a public signed `boot_manifest` capsule in factory NVS for initial trial confirmation. It uses the normal release manifest/signature and dedicated release key. Factory tools accept an optional `--bootstrap-manifest` and validate the signature, board/layout and zero minimum prior sequence before writing a new CSV. The physical sequence1 bootstrap used this capsule.

After the normal five-second health check, initial bootstrap is allowed only when the committed sequence is zero, no pending update exists, the running image is PENDING_VERIFY, and ESP-IDF reports a usable rollback image. It independently verifies the signed capsule and exact running-image digest before persisting pending metadata and invoking normal confirmation. Once sequence commitment succeeds, the capsule cannot bootstrap again. Existing pending updates are never overwritten. A confirmed VALID image is not invalidated if only its later sequence-persistence step continues to fail; metadata reconciliation remains pending.

Seventy host bootstrap trials cover valid confirmation, unhealthy/mismatched images, signature failure, persistence failure, wrong state, existing pending metadata, missing fallback and replay refusal. Coordinator tests additionally cover confirmation succeeding before a failed metadata commit. Factory capsule tamper tests and the isolated ESP32-S3 build pass. These are not physical power-cut trials.

`ota-bootstrap-install.py` requires a fresh double-read board snapshot, stages the signed image into the inactive slot, preserves ownership/application/model state, marks the prior image VALID and writes the candidate NEW selector last. It verifies every input and the physical prior slot/partition before writing. Sequence1 confirmed healthy in12.863seconds on the Waveshare board; see `tests/acceptance/results/M11/physical-signed-bootstrap.json`. The legacy bench flasher remains unsuitable and disabled for this profile. No automatic install is enabled.

## Physical rollback evidence

The physical failure campaign passed10/10 trials. Each trial started from the online audible release, requested allowlisted sequence2, observed task quiescence and signed-image selection, then observed `boot_health_failed` after90.946–91.136seconds. The bootloader restored the prior audible release; every trial regained authenticated online presence, available audio and at least17,920 new AFE samples. Eight recovery `boot_confirmed` markers were captured directly. USB-JTAG dropped the reboot edge twice, so those two recoveries were identified through the prior image's immutable audible setting plus live health; the failure image is compiled silent. See `tests/acceptance/results/M11/physical-rollback-10-trials.json`.

This passes the original10/10 failed-update recovery count for health-rejected signed images. It does not cover power removal during flash writes, production secure boot/flash encryption, signer rotation or fleet rollout behavior.

## Clean release rollout

Commit `89eca54` was rebuilt with ESP-IDF5.4.2 and signed as private release sequence4. The 1,918,000-byte application has SHA256 `44995666f20e79f2eb5144817a5c66b77dec7deccaeaa9aedb22fbdffd3c383d` and leaves48,080 bytes in its OTA slot. The isolated one-device backend offered that exact bundle. The board selected it and confirmed health in13.464seconds with authenticated online presence, audio availability and90,624 processed AFE samples. Sanitized evidence is in `tests/acceptance/results/M11/physical-final-release-install.json`; signing material and release bundles remain ignored.

This proves the guarded release path on one development board. It is not a fleet rollout, independent install trial, production-key ceremony, secure-boot result or power-cut result.
