# Physical account linking and network changes

The owner enrollment profile and guided UI are implemented, compiled and host-tested. **Physical owner enrollment, same-AP reprovisioning and native gateway presence now pass on the isolated LAN test deployment.** `HARDWARE_PROVISIONING_ENABLED=false` keeps the public setup path closed until native-browser BLE and the remaining recovery matrix pass. The original bench profile remains available separately and makes no account-link claim.

The browser uses the unique setup card to establish Security2. The robot supplies the network scan, including supported security modes. The browser obtains a signed owner ticket only after a network has been chosen, transfers it in bounded120-byte chunks, and sends the Wi-Fi password only after the device confirms ticket verification. The server never receives the Wi-Fi password. A network-change ticket must name the existing owner, device and ownership epoch.

The robot pins its deployment’s P-256 verification key and HTTPS issuer in factory NVS. It checks ticket signature, device, nonce, operation, owner/epoch, wall time and the monotonic challenge lifetime. Disconnecting BLE clears transient challenges and ticket fragments. It does not erase a durable transaction that may already have reached the backend.

Before trying a candidate network, firmware atomically journals the pending ticket and candidate configuration. Before HTTP redemption, it commits a separate submitted flag. A lost HTTP response therefore retains enough state to retry the same receipt. An authoritative rejection clears the pending transaction; an uncertain transport or storage failure offers resume. Failed Wi-Fi before submission restores the previous network. The browser cancels the unused reservation after confirmed rollback so password correction can retry immediately. Network changes retain the existing device credential and owner.

The browser confirms both robot connectivity and the backend binding before showing success. Resume reads the pending network over encrypted BLE; it does not resend a password. Closing setup releases Bluetooth and leaves an already submitted transaction recoverable. Full reset, native device WSS, credential renewal and physical voice remain separate work.

## Verification

- Actual C ticket verification:27 positive/negative vectors against ESP-IDF MbedTLS/cJSON.
- Actual C device identity signing: backend-compatible P-256 challenge and UTF-8 network redemption proofs, including the factory PKCS8 encoding; wrong fingerprint and P384 rejection.
- C journal tests: staging, failed/ambiguous commits, restart, submitted flag, receipt retry, owner/epoch checks and credential-preserving network changes.
- C setup coordinator: real ES256 ticket chunks, session fencing, repeated finish, failed persistence, reboot/redeem retry and expiry.
- HTTP client: bounded chunked receipts, credential shape, device/epoch, duplicate fields, TLS errors, redirects and HTTP failures. These use a host transport fixture, not a claim of physical TLS success.
- Browser protocol tests: robot-only networks, no password in HTTP requests, cancellation and reservation cleanup, preserved identity, and resume.
- Setup entry/error UI:360/768/1440px, no overflow or serious accessibility violations. Network-selection and full physical flows still need browser/hardware verification.

Reproduce host checks after the ESP-IDF workspace is installed:

```sh
sh firmware/tools/test-tickets-host.sh
sh firmware/tools/test-identity-host.sh
sh firmware/tools/test-enrollment-host.sh
sh firmware/tools/build-waveshare-owner.sh
npm run check
```

## Current Waveshare test fixture

A separate private factory image at `work/board/factory-owner-288485b2ab10` preserves the board identity and setup secret while adding the test deployment’s public key and CA. It has not been flashed. Deployment signing keys are in a private Docker volume; the dedicated LAN backend recognizes the public device identity and passes HTTPS login/health checks. The setup card is a private file; never put it in Git, screenshots, logs, URLs or messages.

After the board enters its ROM bootloader, the prepared install and physical checks are:

```sh
work/idf-tools/python_env/idf5.4_py3.13_env/bin/python firmware/tools/board-flash.py \
  --backup work/board/backup-20260912T221258Z --profile owner \
  --factory work/board/factory-owner-288485b2ab10 --flash
npx tsx scripts/owner-ble-smoke.ts --presence
npx tsx scripts/owner-ble-smoke.ts --change
# After an interrupted transaction:
npx tsx scripts/owner-ble-smoke.ts --resume
```

These target only the isolated LAN test account and the user-supplied Wi-Fi test file. They do not print credentials or SSIDs. The guided browser flow must also pass on supported Chrome before enabling the feature for users. No eFuses have been changed; production protected storage and firmware update security are not claimed.

## Native gateway presence

The owner profile now starts the pinned Espressif WebSocket client after enrollment and Wi-Fi connectivity. It sends its stored credential only in the TLS-authenticated gateway request, checks the welcome epoch, sends a heartbeat every five seconds and reconnects with bounded exponential backoff and jitter. The receiver limits messages to 2 KiB and its queue to eight messages; oversized, malformed and unexpected frames close the connection. Neither voice nor physical capabilities are advertised by this profile yet. Identity snapshots are serialized against provisioning work. Credential expiry closes the connection; renewal is still outstanding.

`sh firmware/tools/test-device-wire-host.sh` exercises every split boundary in a welcome, continuation frames, interleaved ping, overflow, invalid epoch, unsupported action and 10,000 deterministic malformed frame sequences under ASan/UBSan. This verifies framing and control validation, not the actual TLS/network task or hardware reconnection. The target image compiles and physical WSS presence now passes. Five five-second presence checks pass before and after an isolated backend restart, with the same boot ID and ownership epoch. Full network impairment, credential renewal and runtime resource measurements remain open.

## Physical verification — 13 September 2026

A power cycle restored serial flashing. Owner claim completed in22.4s and matched the gateway ownership epoch. The initial network-change trial lost BLE before confirmation; the completed journal survived, but resume after restart incorrectly returned NO_PENDING_SETUP. Firmware now reports the saved SSID, and the shared client can confirm an idle, connected, linked device against that network and the server binding. Its regression also rejects a different server binding.

The revised image was flashed with esptool hash verification. Post-update/reboot resume passed in2.45s, without resending the password or obtaining a new ticket. A fresh same-AP network-change trial passed in18.26s with owner preservation and matching native WSS presence. These are physical tests through the Python BLE bridge and the same TypeScript client used by the UI. They do not prove direct browser BLE, another access point, arbitrary power-cut timing, audio, full reset or credential renewal. Sanitized reports retain both the initial failure and successful rechecks.
