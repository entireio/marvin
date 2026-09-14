# Physical onboarding completion plan

14 September 2026.

## Target journey

1. A signed-in owner chooses **Set up your Marvin**.
2. The owner chooses **Find Marvin**. The browser opens its native Bluetooth chooser.
3. The owner selects **Marvin** and chooses **Pair**.
4. Marvin and the portal establish an authenticated Espressif Security 2 session using the unique setup card. The card can be scanned or pasted; its secret is never sent to the web server or saved by the browser.
5. The portal displays the Wi-Fi networks reported by that Marvin. The owner chooses a compatible network and enters its password.
6. The password travels only inside encrypted BLE. Marvin joins the network, validates the configured HTTPS service, redeems the signed owner ticket, and reconnects as the one robot belonging to that account.
7. The portal verifies both the robot's local state and the server binding, then displays **You and Marvin, connected**.

The Bluetooth display name is presentation only. The portal trusts the setup-card identity, the Security 2 proof, the device-signed challenge, and the backend's owner binding.

## Finding behind the current dead end

The portal at `http://127.0.0.1:5173` is a local development preview. Its backend currently reports `hardwareProvisioningAvailable: false`; `HARDWARE_PROVISIONING_ENABLED`, `ENROLLMENT_KEYS_FILE`, and `DEVICE_PUBLIC_ORIGIN` are absent. React therefore loads the legacy `Setup` component. That component discovers the BLE service and then deliberately raises `SECURE_SETUP_PENDING`, producing the M0–M3 message and offering simulation.

Enabling the boolean alone would be incorrect. A physical claim spans the portal database, enrollment signer, robot-reachable HTTPS origin, and redemption endpoint. A loopback-only backend cannot complete that transaction because Marvin cannot reach it and because a second backend would not share the browser owner's binding.

The isolated LAN deployment at `https://192.168.1.5:8443` currently reports `hardwareProvisioningAvailable: true` and loads `PhysicalSetup`. It has the enrollment trust, persistent binding database, TLS endpoint, and robot-reachable origin. The implemented secure flow has already passed scoped physical claim and same-network reprovisioning tests. Two product gaps remain visible: the secure wizard requests the setup code before the chooser, and firmware advertises `Marvin setup` rather than `Marvin`.

## P0 — Make the active environment unambiguous

**Build**

- Add a documented hardware-development launch command that starts or validates one HTTPS deployment shared by browser enrollment and robot redemption.
- Make the portal show a clear non-interactive explanation when physical provisioning is unavailable. Remove the legacy behavior that opens a chooser and then declares setup unfinished.
- Keep simulation explicitly labeled as a preview. Never present it as the recovery action for a real robot that was just paired.
- Show the current portal origin and provisioning readiness in operator diagnostics without exposing keys or device credentials.

**Measurable success**

- The hardware launch check refuses a loopback `DEVICE_PUBLIC_ORIGIN`, missing enrollment trust, mismatched portal/device origins, or a database that is not the redemption database.
- On a disabled deployment, clicking the setup entry produces zero Bluetooth chooser calls and gives a link/instruction for the configured hardware-capable portal.
- On the hardware deployment, `/api/config` reports provisioning available and the real wizard renders; the M0–M3 message is absent from the shipped path.
- Automated tests cover both configurations and detect any return of the misleading chooser dead end.

## P1 — Pair first, authenticate second

**Build**

- Make **Find Marvin** the first enabled action. Call `navigator.bluetooth.requestDevice` directly from that user gesture.
- After native pairing, retain the selected device only in memory and show **Marvin selected**.
- Present **Scan setup card** and **Enter setup code** after pairing. A camera/QR option is progressive enhancement; paste remains the accessible fallback.
- Open Security 2 only after the code is supplied. Verify enrollment-capable firmware and require the authenticated device identity to equal the card identity.
- Change the firmware BLE display name from `Marvin setup` to `Marvin`. Do not use this name for authorization.

**Measurable success**

- A browser integration test proves the call order: user click → chooser → selected device → setup code → Security 2. No setup secret, Wi-Fi password, or enrollment request exists before device selection.
- Canceling the chooser returns to **Find Marvin** with no account or robot state change.
- An incorrect card, a card for another device, incompatible firmware, and a dropped GATT connection each fail before Wi-Fi credentials or enrollment issuance.
- Chrome's native chooser displays **Marvin** on the physical board after the signed firmware update.
- Keyboard and screen-reader checks announce selection, code errors, retry, and cancellation at 360, 768, and 1440 px with no serious accessibility findings.

## P2 — Show only networks Marvin can use

**Build**

- After Security 2 succeeds, seed the device clock and request an on-device scan.
- Display the returned 2.4 GHz networks ordered by signal. Merge only equal SSID/security pairs while retaining the strongest robot-side AP index.
- Mark unsupported security modes unavailable, support scan retry, and retain the bounded hidden WPA2 path.
- Expire a scan after 30 seconds before any credential transfer.

**Measurable success**

- Captured BLE commands prove every displayed normal network came from Marvin and that a laptop-only network never appears.
- Tests cover zero results, duplicate APs, Unicode SSIDs, weak signal, open networks, WPA2, unsupported WPA3-only entries, hidden SSIDs, rescan, and stale selection.
- Twenty physical scans complete without leaking SSIDs into server requests or logs; every selected AP is one the board reported.

## P3 — Connect Wi-Fi and bind one owner atomically

**Build**

- Request a fresh device-signed claim challenge only after the owner selects a network.
- Have the authenticated portal issue a short-lived owner ticket, transfer it in bounded encrypted BLE chunks, and require firmware verification before sending the Wi-Fi password.
- Keep the previous network until candidate Wi-Fi, DHCP, time, certificate-verified HTTPS, ticket redemption, durable credential storage, and server binding all succeed.
- Confirm readiness from both sides: firmware reports the saved/connected network and linked state; the portal database reports the same device and network for the current owner.
- Enforce one Marvin per account and reject a device already owned by another account with an actionable ownership message.

**Measurable success**

- Browser/network inspection finds the Wi-Fi password in zero HTTP, WebSocket, URL, persistence, analytics, screenshot, or log payloads.
- A successful physical run ends with the exact device bound once, authenticated native presence online, saved network matching the selected network, and the Ready page shown within 120 seconds.
- Duplicate submission and reconnect cannot create a second binding or redeem a ticket twice.
- Cross-account claim, forged device identity, expired/revoked ticket, and a second-robot claim are rejected without replacing the existing binding.

## P4 — Recovery that never asks the owner to guess

**Build**

- Map wrong password, AP loss, TLS/service failure, expired setup, BLE loss, browser close, and power interruption to distinct recovery text.
- Offer **Try password again** only after confirmed candidate rollback. Offer **Resume setup** when firmware has a durable pending transaction or completed binding.
- For an already linked Marvin, route the owner to **Change Wi-Fi** and use a network ticket; preserve the owner binding.
- Explain that setup mode is available for five minutes after restart and provide a countdown/restart instruction when the advertisement disappears.

**Measurable success**

- Failure injection at every documented boundary ends in exactly one classified state: previous network restored, resumable pending setup, successfully linked, or explicit service assistance.
- Closing and reopening the portal during redemption resumes without resending the password.
- Ten wrong-password trials restore the prior usable network; ten interruption trials never produce a partial/foreign owner binding.
- A linked-device Wi-Fi change preserves device ID and ownership epoch and returns native presence online on the new network.

## P5 — Physical release acceptance

**Build and test**

- Install the signed firmware containing the final BLE name and run the flow through the hardware-enabled HTTPS portal in supported Chrome.
- Test a fresh claim, cancellation, wrong-password retry, browser/BLE interruption and resume, power-cycle recovery, same-owner Wi-Fi change, and already-owned rejection.
- Record sanitized evidence only. Keep setup codes, passwords, tickets, bearer credentials, SSIDs, and signing material out of committed artifacts.

**Measurable success**

- Ten consecutive fresh or restored end-to-end onboarding attempts reach confirmed Wi-Fi plus owner binding; every failure has a categorized recovery outcome.
- Median active user time is at most three minutes, excluding Wi-Fi/backend waiting; four of five uninvolved participants find **Set up your Marvin** and reach the network list without coaching.
- The complete provisioning unit/integration suite, production build, accessibility suite, and ESP-IDF release build pass.
- The public feature flag changes to enabled only for deployments with the complete trust/database/TLS configuration and passing physical evidence.

## Recommended implementation order

Implement P0 and P1 first because they remove the exact dead end and align the visible journey. P2 and most of P3 already have working components and tests; adapt them to the new ordering without weakening Security 2 or identity checks. Complete P4 before broad enablement, then use P5 to decide whether the hardware-enabled flow can replace the disabled public default.
