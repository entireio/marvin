# Signed firmware updates — implementation in progress

The update verifier and bounded transfer engine are implemented and host-tested. **The download/activation integration and physical rollback trials are not complete.** No release is published by the signing tool, and no eFuses are changed.

`scripts/firmware-release.ts` creates a new local directory containing an ESP32-S3 application image, a signed binary manifest and readable metadata. It requires a dedicated P-256 private release key. Keep that key off the application server and robot; provision only the corresponding public key as trusted factory data. The deployment enrollment key is a separate trust domain.

Example, using operator-supplied paths:

```sh
npx tsx scripts/firmware-release.ts --key /private/release-key.pem \
  --image build-waveshare-afe/marvin.bin --sequence 1 \
  --minimum-sequence 0 --layout afe-v1 --out work/releases/1
```

The176-byte manifest has a fixed112-byte payload followed by a64-byte raw ES256 signature. It contains the `MRVOTA01` format marker, unsigned big-endian release sequence and image size, SHA-256 image digest, zero-padded board/layout identifiers, minimum committed prior sequence, and twelve reserved zero bytes. Board and layout identifiers are compared with trusted local values. The image must fit the existing inactive application slot. Model/partition migrations are not application-only OTA updates.

The verifier rejects wrong keys, modified fields, another board/layout, stale or downgraded sequence, an unmet minimum prior version, unknown reserved fields, oversized images, truncation and malformed signatures. The transfer engine verifies this manifest **before** opening the inactive slot. It bounds total bytes, hashes each chunk and only allows next-boot selection after exact size/digest checks and platform image validation. Cancellation and failure never select an image; abort is idempotent. Platform callbacks must guarantee that a failed `begin` cleans up its own partial resources and a failed `select` has not changed the selected boot slot.

`sh firmware/tools/test-update-host.sh` passes15 signed/invalid vectors, all truncations,10,000 malformed envelopes and ten repetitions each of successful transfer plus seven failure/interruption modes under address/undefined-behavior sanitizers. The injected platform writer is a fixture. **These results do not satisfy the physical ten-trial interrupted-update/rollback gate.**

Next integration work: authenticated download from the enrolled HTTPS origin without redirects; factory release-key provisioning; quiescing microphone/speaker tasks before flash writes; ESP-IDF inactive-slot callbacks; durable pending-version metadata; rollback-enabled bootloader and bounded first-boot health confirmation; signed-release serving and operator rollout controls. The real board must recover its previous usable image in the original plan's ten injected trials. Production secure boot, protected identity/key storage and key rotation must be reviewed before claiming release security.

## ESP-IDF platform binding

`esp_writer.c` implements inactive-slot begin/write/end/select callbacks, durable pending-image metadata and health-confirmed sequence advancement. It refuses to provide an installer writer when the bootloader lacks rollback support. No application call site enables downloads or invokes installation yet. The current silent soak image is unchanged.

Pending metadata is committed before boot selection. Confirmation compares the running slot and the exact signed file digest. Only a healthy, validated running image advances the committed sequence. If power fails between boot confirmation and the sequence commit, the retained pending record allows idempotent recovery on the next boot. Returning to the previous slot after rollback does not advance the sequence.

The platform code compiles for ESP32-S3. Host tests substitute ESP-IDF/NVS operations and repeat confirmation, failed persistence, recovery after a lost commit, image mismatch, prior-image rollback and stale-version rejection ten times under sanitizers. These are simulated persistence boundaries, not real flash power-cut trials. Authenticated download, release-key provisioning, task quiescence and boot health call sites still need integration before physical OTA acceptance.

## Explicit device rollout on the backend

The optional `FIRMWARE_ROLLOUT_FILE` is an absolute path to an operator-maintained JSON file containing `publicKeyFile`, `bundleDirectory`, and an explicit nonempty `deviceIds` allowlist. Both file paths are absolute. The public key must be a PEM public P-256 key; private-key files are refused. The bundle is the local signing tool's `manifest.bin` and `image.bin`. Leave this setting absent to offer no updates. No rollout is configured on the current board backend.

At startup, the backend bounds each file read, verifies the manifest signature, board/layout, image size, ESP32-S3 header and exact SHA-256 digest, and retains immutable verified bytes. An operator must restart to change the selected release; edits to bundle files cannot change an in-flight release. No upload, signing, publishing or automatic activation endpoint exists.

Direct authenticated devices may query `GET /api/device/firmware`. Devices outside the explicit rollout receive204. A selected device receives versioned manifest/image paths. Downloads require the current owner-bound device credential on every request; browser cookies or origins are refused. Unlinking invalidates subsequent downloads. Unknown versions or devices cannot retrieve the bundle. The device must still verify the signature, layout, sequence and image itself before installing: server verification is additional protection, not the trust anchor.

Local tests cover signature/image tampering, oversized input, accidental private-key configuration, immutable loaded bytes, targeted downloads, wrong-version denial, browser-origin refusal and credential revocation. These endpoints have not been enabled on the physical deployment. The firmware HTTP consumer, release-key provisioning, audio quiescence and boot-health integration remain next steps.

## Firmware HTTPS download component

`marvin_update_download` now downloads a caller-selected sequence from fixed versioned paths on the enrolled HTTPS origin. It accepts only the current device credential format, uses the provisioned CA or the ESP-IDF public bundle, disables redirects, and requires exact response lengths. It verifies the manifest before opening the inactive slot and checks the requested sequence, full image digest, transfer completion, cancellation and a120-second overall deadline before boot selection. Each network operation has a5-second timeout. HTTP credential-bearing debug logs are suppressed. Temporary authorization and audio-sized transfer buffers are cleared.

The ESP32-S3 build passes. An HTTP transport fixture runs80 success/failure trials under sanitizers: redirects, wrong lengths, invalid manifests, truncated bodies, wrong image content, cancellation and deadline expiry never select an image. Invalid origins/tokens are rejected before network access, and a sequence mismatch is rejected before opening the slot. This is component verification, not a real TLS/OTA installation test.

The application still needs to load dedicated factory release trust, admit only one update, quiesce audio, invoke this component, reboot and run bounded boot-health confirmation. The current board firmware has not been replaced or updated during its ongoing soak.

## Dedicated factory release trust

New factory identities can include `factory.py --release-public-key /operator/release-public.pem`. For an existing identity, `release-trust.py --factory-csv /private/source.csv --public-key /operator/release-public.pem --output-csv /private/new.csv` creates a separate CSV and preserves every existing identity field. It never overwrites the source or destination, rotates an existing release key, or flashes hardware. It refuses private/non-P-256 keys and reuse of the configured enrollment signing key. Regenerating a factory identity is not a key-update procedure.

The public key is stored as a NUL-terminated `release_pub` blob. `marvin_update_factory_trust` loads it from read-only factory NVS, validates its P-256 public format, obtains the committed release sequence, and binds it to the compiled layout and actual slot size. Its buffer must remain alive during verification/download. Missing or malformed trust fails closed. No public release key has been added to the connected board in this iteration.

Temporary factory fixtures verify new-identity creation with optional trust, identity-preserving augmentation, private file permissions, refusal to overwrite/rotate/reuse keys, duplicate fields and wrong curves. Host loader tests cover valid trust, missing/malformed data, failed sequence reads and buffer/layout limits. The ESP32-S3 build and update sanitizer suite pass. Key storage remains subject to the project's existing physical-security limitations; no eFuses or secure-boot settings were changed.

## Audio shutdown before flash writes

The runtime now exposes one-way shutdown helpers. `marvin_device_link_quiesce` closes the voice/device connection and waits up to10 seconds for the transport task to park. Reconnect backoff becomes interruptible. `marvin_body_quiesce`, called only after transport shutdown, prevents renewed capture, waits up to2 seconds for capture/feed/playback tasks to park, then clears output state and mutes/disables both I2S DMA channels. Lock acquisition is bounded. Failed or partial shutdown must abort installation. These helpers intentionally require reboot to resume normal service; they are not a microphone-mute feature.

The helper code compiles in both AFE and fallback owner profiles. No physical shutdown test has run because the board's eight-hour soak is still active. DMA cessation, bounded task shutdown and failed-update recovery require physical checks before the update feature is accepted. No application updater currently invokes these helpers.

## Optional runtime and isolated build

`build-waveshare-ota.sh` creates a separate silent AFE build/configuration with signed updates and bootloader rollback enabled. It does not replace the running board or the existing build profile. The normal owner/AFE profiles leave update activation disabled.

In the optional profile, the local serial command `u` followed by a positive decimal release sequence and newline requests that exact release (for example, `u4` then Return). `x` cancels a pending/running update. This is an operator test interface, not a browser rollout control. Only one request is admitted. Preflight requires current owner credentials, online presence, factory public release trust, an inactive slot and a newer sequence. The worker closes transport, parks audio, verifies/downloads the signed release and restarts; failed transfer restarts the prior selected image. Ownership/credential changes, expiration and cancellation are checked during download. No update polling or automatic installation is enabled.

A trial boot has a90-second health window. It requires five consecutive seconds of online device presence, available audio, stack/memory headroom and advancing AFE processing without feed faults before confirming the signed pending image and committed sequence. Failed trial health requests rollback. An already-valid image can reconcile pending sequence metadata after a lost commit; inability to do so does not invalidate that previously usable image. Missing/mismatched pending metadata cannot falsely confirm a trial image.

The runtime is experimental and not yet physically installed. The lower-level signature/transfer/HTTP/NVS fixtures pass; these do not prove the end-to-end update worker, audio shutdown, or physical boot rollback. The existing `board-flash.py` profile allowlist still needs a reviewed initial OTA-profile installation path after the soak, with a retained usable fallback image and matching factory trust. No board flash, release activation or signer-key creation occurred here.

## Coordinator fault coverage and fallback prerequisite

The real update coordinator now runs against simulated task/platform boundaries in host tests. Ninety trials cover single-request admission, preflight refusal, cancellation, transport/audio shutdown failure, writer failure, download failure and ownership changes. Separate boot cases cover stable confirmation, unavailable presence/audio, stalled AFE processing, invalid confirmation metadata, and reconciliation on an already-valid image. The tests advance a simulated clock; they are not physical timing measurements.

An additional installer guard requires the running application to be recorded as `VALID` before opening an update writer. A freshly flashed image with missing OTA metadata, or an unconfirmed trial image, cannot serve as the assumed fallback. The initial installation procedure must establish and verify a usable valid image before any update trial; merely flashing the rollback-enabled bootloader is insufficient. The existing flash helper intentionally still refuses the experimental OTA profile pending that reviewed bootstrap procedure.

## Signed first-installation capsule

The optional runtime now supports a public signed `boot_manifest` capsule in factory NVS for initial trial confirmation. It uses the normal release manifest/signature and dedicated release key. Factory tools accept an optional `--bootstrap-manifest` and validate the signature, board/layout and zero minimum prior sequence before writing a new CSV. No real board capsule has been created or installed.

After the normal five-second health check, initial bootstrap is allowed only when the committed sequence is zero, no pending update exists, the running image is PENDING_VERIFY, and ESP-IDF reports a usable rollback image. It independently verifies the signed capsule and exact running-image digest before persisting pending metadata and invoking normal confirmation. Once sequence commitment succeeds, the capsule cannot bootstrap again. Existing pending updates are never overwritten. A confirmed VALID image is not invalidated if only its later sequence-persistence step continues to fail; metadata reconciliation remains pending.

Seventy host bootstrap trials cover valid confirmation, unhealthy/mismatched images, signature failure, persistence failure, wrong state, existing pending metadata, missing fallback and replay refusal. Coordinator tests additionally cover confirmation succeeding before a failed metadata commit. Factory capsule tamper tests and the isolated ESP32-S3 build pass. These are not physical power-cut trials.

Initial installation still requires a fresh board snapshot, staging into the inactive slot, preserving current ownership/application/model state and a reviewed OTA metadata plan selecting the candidate NEW with the prior usable image VALID. The legacy bench flasher remains unsuitable and disabled for this profile. No automatic install is enabled.
