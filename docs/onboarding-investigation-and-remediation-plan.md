# Login, physical onboarding, and Entire remediation plan

Date: 2026-09-14. Target: `https://192.168.1.5:8443`.

This document began as the investigation plan. The browser, backend, host companion, and firmware work described in the execution status below was implemented and tested on 2026-09-14.

## Execution status

Completed and deployed to `https://192.168.1.5:8443`:

- The matching documentation video is served by the login page and the old video captions are removed.
- Setup now reports chooser, secure connection, Wi-Fi scan, authorization, Wi-Fi connection, and account confirmation as distinct states. The Wi-Fi page appears while scanning.
- Browser chooser cancellation is silent; native exception text is not shown.
- The 30-second Wi-Fi result rejection and its old test are removed.
- Visible resume/reconnect controls are removed. Reconnection automatically checks durable device/backend state before scanning or resuming.
- Wi-Fi selection no longer pins a mesh network to one BSSID.
- Firmware keeps BLE commissioning available after disconnect instead of stopping it after five minutes.
- A live trace identified a 4 KB NimBLE host stack overflow as the immediate Bluetooth reset. The combined owner/audio firmware now uses an 8 KB NimBLE host stack.
- The stale robot/server ownership split was reconciled after taking a private NVS backup. Fresh enrollment and a subsequent same-owner network change both completed without another power cycle, including native gateway presence confirmation.
- A token-authenticated, loopback macOS Entire companion reuses the logged-in user's CLI/Keychain session. It is installed as a launch agent. The LAN owner is bound to `spedemon/marvin`, and the live application reports Entire connected.

Validated: 162 automated tests, production web/server build, three viewport accessibility/overflow checks, firmware build, HTTPS deployment asset/status smoke test, physical fresh claim, physical network change, and device gateway presence.

Still dependent on product hardware/platform work:

- Removing the setup code securely requires session-specific physical approval on Marvin plus a new authenticated key exchange. The currently attached Waveshare audio board has no display, button, or complete Marvin body, so this approval path cannot be implemented or validated on this hardware. The existing code remains as the secure compatibility bootstrap.
- A fully custom Bluetooth device list cannot be implemented by a normal web page. It requires the native BLE portion of the planned companion. The browser version retains Chrome's required chooser and now handles it cleanly.

## Findings

| Reported issue | Evidence and diagnosis | Required change |
| --- | --- | --- |
| Old login image and text | The LAN endpoint serves `index-QEEwxbNh.js`. Inside its Docker container, that bundle has no video reference and the video file is absent. Current `App.tsx:66` includes the video, but also retains `MARVIN / 01`, `MOSTLY HARMLESS`, and marketing copy. The public video and `Marvin/docs/assets/video/marvin_moving_20sec_pingpong.mp4` have identical SHA-256 hashes. | Complete the caption/copy removal, then rebuild and redeploy the actual LAN service. Verify the served artifact, not just the source or a development preview. |
| Setup code before Find Marvin | `PhysicalSetup.tsx:16–17` decodes the card before selection; the card contains device ID and unique SRP username/password. `BluetoothSetupSession.open` requires these to establish Security 2. | Replace the authentication bootstrap before removing the field. Device identity, physical confirmation, and account authorization must remain separate checks. |
| Browser pairing modal and cancellation error | `chooseBluetoothRobot` calls `navigator.bluetooth.requestDevice`; `failed()` renders any `Error.message`. The browser owns the chooser. | Normalize cancellation; implement the surrounding progress UI now. Fully custom discovery requires a native client/companion. |
| No clear pairing feedback; delayed Wi-Fi page | `find()` awaits `scan()`, and `scan()` changes to step 1 only after collecting all networks. | Enter the Wi-Fi screen immediately after secure connection succeeds; show a connected indicator and scanning animation while the robot scans. |
| Wi-Fi list expires after 30 seconds | `PhysicalRobot.connect()` explicitly throws `SCAN_EXPIRED` before requesting authorization. Firmware checks scan generation and index but does not impose this age limit. A unit test enforces the unwanted behavior. | Remove elapsed-time rejection. Preserve selection identity and compatibility checks; transparently refresh/reconcile only when needed. |
| Resume/reconnect buttons | Firmware journals a pending transaction because Wi-Fi and backend enrollment can continue after the browser disappears. UI exposes this recovery mechanism directly. | Keep durable recovery, make it automatic, and remove both buttons/concepts from the normal journey. |
| Device disappears until power cycle | `firmware/main/marvin.c:298–300` unconditionally stops and deletes BLE five minutes after startup, including during active work. The checked-in NimBLE transport already restarts advertising on disconnect. | Replace the unconditional shutdown with a lifecycle state machine. Separately reproduce failures occurring within the five-minute window; those are not explained by the timer alone. |
| Correct Wi-Fi password produces generic failure | `physical.ts:22` maps unrecognized firmware codes to exactly the reported message. Examples include `OWNERSHIP_STATE`, `TICKET_INVALID`, `INVALID_REQUEST`, and `BACKEND_NOT_CONFIGURED`. Wi-Fi association is only one of several steps. Existing September 14 hardware records show interrupted hidden-network and wrong-password attempts after about 12–15 seconds. | Capture the exact failing operation/code and deployed firmware identity, then fix the failing layer. Do not infer an incorrect password from this message or claim the exact root cause is established. |
| Entire says it needs configuration | The LAN backend is Docker container `marvin-board-backend-marvin-1`, with neither `ENTIRE_CLI_PATH` nor `ENTIRE_BINDINGS_FILE` configured. On macOS, both are set in the workspace `.env`, a repository binding exists, and `entire auth status` confirms login using macOS Keychain. These are different execution environments. | Connect the LAN application to the intended host-side Entire integration, with bindings for the LAN database's owner. Improve capability/status reporting. |

Relevant source: `apps/web/src/{App,PhysicalSetup}.tsx`, `packages/provisioning/src/{bluetooth,physical,security2,setup-code}.ts`, `firmware/main/{marvin,owner_setup,device_identity}.c`, `packages/enrollment/src/service.ts`, `packages/runtime/src/entire-cli.ts`, `apps/server/src/{app,config}.ts`, and `scripts/entire-setup.ts`.

## 1. Code-free setup security

**Yes, setup can work without typing a code. Removing the existing field alone would break authentication.** The current Security 2 exchange authenticates knowledge of a factory secret and encrypts BLE traffic. Separately, a registered device signs a fresh challenge, the backend issues an account-bound ticket, and the device verifies/redeems it before ownership is committed.

Showing only unclaimed devices is useful discovery behavior, but does not prove who should claim one. A nearby person could otherwise claim an unowned robot first. Advertisements can be spoofed; a linked robot with an outdated local state must not bypass backend ownership checks.

### Recommended replacement

1. Unclaimed Marvin advertises a commissioning service. Linked Marvin stops advertising that service; owner maintenance uses a separate service/mode. Pending claims have explicit recovery behavior rather than becoming permanently hidden.
2. The client obtains authenticated device identity through a versioned, reviewed secure-channel protocol. Build on standard authenticated key exchange and the provisioned P-256 identity/trust registry; do not replace SRP with a shared password, advertise its secret, or use plaintext credentials. Evaluate a supported library/protocol before selecting the exact wire format.
3. Bind the device identity, ephemeral channel keys, account authorization, operation, and fresh nonce into the authenticated transcript. The backend verifies the registered identity and atomically checks ownership/reservation state.
4. Marvin presents the particular pending account/session for physical approval, using its display and a validated button or touch action. A short matching visual indicator can distinguish simultaneous requests without asking the user to type a setup code. Approval must authorize that specific session, not whichever request happens to arrive next.
5. Only after channel authentication and physical approval may the account-bound client send Wi-Fi credentials. Keep credentials off backend APIs, logs, and persistent browser storage.
6. Retain signed tickets, expiration, replay prevention, unique ownership constraints, durable enrollment receipts, and idempotent redemption. Bind new tickets to the approved secure session. Same-owner network maintenance uses owner authorization; another account cannot claim or reconfigure a linked robot.
7. Add explicit unlink/reset reconciliation between backend epochs and device ownership state. Opening setup mode must never erase ownership. Handle stale reservations and unclaimed/pending/linked disagreement without silently overwriting an owner.

This changes firmware endpoints/security integration, client transport, provisioning factory/trust metadata as needed, backend challenge/ticket schemas, and recovery tests. Version the protocol and coordinate firmware/client deployment; old firmware should receive a clear compatibility path.

**Hardware dependency:** validate an available physical control and display prompt on the actual Waveshare assembly. The current implementation treats a power cycle plus secret as physical presence. A session-specific approval requires new firmware input handling. This remains to be demonstrated on hardware.

## 2. Discovery UI and browser boundary

The production Web Bluetooth flow cannot replace or style the initial browser device chooser, nor keep it open after the browser dismisses it. Chrome documents that `requestDevice()` needs a user gesture and prompts with a chooser: [Chrome Web Bluetooth documentation](https://developer.chrome.com/docs/capabilities/bluetooth). Installing a PWA does not itself provide a native discovery API.

### Browser implementation

- One primary action: **Connect Marvin**.
- Explain the browser selection step briefly, then launch it directly from the click.
- Treat chooser dismissal as a neutral return to the initial state. Never display DOM exception strings.
- After selection, show **Connecting to Marvin…**, then **Confirm on Marvin** when required by the replacement security protocol.
- When secure connection succeeds, show **Marvin connected** and immediately open **Choose a Wi-Fi network**, with **Marvin is looking for Wi-Fi networks…** and a visible accessible animation.
- Retry an already authorized device using the retained handle where possible; feature-detect previously permitted device APIs. Permission is not proof that a device is nearby, connectable, or account-owned. Fall back to another user-triggered chooser only when needed.

### Fully custom scan/results, as requested

Implement a native desktop/mobile client or a companion running on the computer that has the Bluetooth radio. It can scan continuously and stream discovery/results to the Marvin setup UI. OS Bluetooth permission prompts still exist, but scan progress and selection can be designed within the app.

For this local installation, a macOS companion is a practical shared foundation for BLE and host Entire access. Keep these as separate, narrowly scoped capabilities. Authenticate the app/companion connection, restrict approved origins and accounts, prevent arbitrary command execution, handle installation/offline/permission states, and ensure an unrelated website cannot claim hardware or read repositories. A remote server's Bluetooth radio is not a substitute for the user's local radio. Validate browser local-network/TLS transport constraints before choosing how the web UI connects to the companion.

Deliver browser UX improvements independently; the companion is a separate milestone required for the exact custom-discovery experience.

## 3. One setup state machine, automatic recovery

Replace the coarse step number plus unrelated `busy`/`resume` flags with explicit states:

`ready → selecting_device → connecting_device → confirming_device → scanning_wifi → choosing_wifi → authorizing → connecting_wifi → confirming_account → complete`

Recovery states: `connection_interrupted`, `checking_existing_setup`, `waiting_for_service`, and actionable categorized failures.

- Show each transition before starting its asynchronous work. Do not show “no networks” while scanning. Use `role=status`, appropriate focus movement, and reduced-motion alternatives.
- Define Bluetooth connected separately from Wi-Fi connected and account linked. Only show overall completion after device and backend agree.
- Enter the progress screen immediately on Wi-Fi submission. Use stage-specific text: authorizing, connecting to the selected network, contacting Marvin service, confirming account.
- Remove **Resume an earlier setup**, **Resume setup**, and **Reconnect** from the normal UI. On reopening, query device and backend state automatically and either restore network choice, observe ongoing work, recover a submitted transaction, or show completion.
- Keep only safe recovery metadata, such as an opaque attempt ID and authorized device handle. Never persist Wi-Fi passwords, setup secrets, or authorization tickets in browser storage.
- Do not resend `apply` after an ambiguous disconnect. First determine whether the existing transaction is running, committed, safely retryable, or revoked. Handle a lost response after successful server commit through the existing receipt/journal design.
- Audit `resume()` before calling it automatically: it currently sends `clock` before querying status, which can return `BUSY` during active work. Observe status first; resume only a recoverable inactive transaction.
- Serialize GATT operations, discard stale async callbacks, and close a device selected after the dialog was dismissed. Bound automatic retries and then offer a plain-language action such as **Try again**.
- Closing the dialog releases the client connection but does not falsely imply cancellation of durable work. Automatically reconcile when reopened.

## 4. Wi-Fi selection and correct-password failure

### Remove artificial scan expiration

- Delete the 30-second rejection and revise its test/specification.
- Continue validating robot source, supported security, SSID byte length, and scan generation/index consistency. Do not apply an obsolete index to a new scan.
- If the generation is unchanged, attempt the selected network even after several minutes. If a refresh is necessary, retain the entered password in transient memory and resolve by SSID plus security; never silently downgrade security.
- Firmware currently pins a scanned connection to a BSSID. Review this behavior for mesh/multiple-AP networks: a departed AP should not prevent connecting to the same compatible network through another AP. Use a supported network-level connection policy or transparent rescan/reselection.
- If the network is actually unavailable or authentication fails, report that actual outcome and retain the network choice for retry. Keep **Scan again** as an optional action.

### Diagnose the failing connection before declaring a fix

1. Record running web/backend build IDs and flashed firmware hash/version. Existing source and LAN image already differ.
2. Add sanitized per-attempt stage/operation/error-code/timing diagnostics across BLE, authorization, Wi-Fi, TLS/probe, enrollment, and journal persistence. Preserve structured transport causes internally instead of collapsing all failures into disconnection. Never record passwords, tickets, keys, bearer tokens, or SSIDs in committed diagnostics.
3. Capture firmware disconnect reason, Wi-Fi disconnect reason, free heap/largest block, reset reason, and advertising start/stop results alongside the browser/server attempt. Existing older logs contain memory assertions; they are leads, not evidence that this user's run had the same fault.
4. Reproduce correct-password setup with both a fresh claim and a same-owner network change. Check firmware/backend ownership consistency before choosing `claim` versus `network`.
5. Trace ticket verification failures separately from Wi-Fi failures; verify issuer, trust key, nonce, clock, expiry, epoch, and claim operation against the deployed versions. Check backend probe configuration and device-reachable TLS trust.
6. Categorize every firmware error in the UI. Unknown errors should present a useful retry/support reference while retaining the precise code in sanitized diagnostics. A network password message is appropriate only when authentication evidence supports it.
7. Reproduce interruption both immediately and after five minutes. Check for a lingering GATT client, re-advertising failure, stack reset, and resource exhaustion before attributing disappearance to the timeout.

## 5. Firmware lifecycle without power cycling

- Replace the fixed five-minute sleep/stop/delete with a lifecycle manager that can start, stop, and reopen commissioning safely.
- Keep initial unclaimed commissioning discoverable under an explicit policy; physical approval still gates enrollment. Do not terminate active pairing, scanning, or recovery because a boot timer elapsed.
- On disconnect, invalidate session-specific challenges and buffers, preserve durable work, and resume advertising when eligible. Confirm behavior with the actual configured NimBLE transport.
- Prevent stale callbacks or competing connections from closing a newer session. Bound idle connections and reclaim their resources.
- Provide a validated physical gesture to reopen maintenance without a reboot. Keep linked devices out of public claim discovery, while still supporting authorized owner recovery and Wi-Fi changes.
- Make teardown/reinitialization safe alongside audio, TLS, and the device gateway. Hardware tests must cover combined runtime behavior, not only a provisioning-only build.

## 6. Entire: make local login usable

The immediate failure is deployment configuration, not an absent macOS login. `/api/settings` defines availability from a configured repository binding; it does not probe the host's current CLI session. It also returns configuration errors that `App.tsx` hides unless already connected.

### Recommended local architecture

Run the Entire adapter in the logged-in macOS user's context, with access to Keychain and approved checkouts. Keep the LAN Docker backend and its device-facing origin/trust stable, and connect it to a narrowly scoped authenticated host service. This can share companion infrastructure with native BLE, but repository access remains separately authorized.

For a simpler all-host development deployment, running the backend as that macOS user is also viable, but requires deliberate migration of the LAN database, enrollment keys, device origin/trust, and proxy routing. Pointing the browser at another backend would create a different owner/device state and is not a complete fix.

- Bind repositories to the owner in the active LAN deployment database. The existing host binding's owner UUID must not be assumed to match Docker's database.
- Discover CLI availability/context/login status on the host and let the user select repositories in the UI. Preserve explicit per-account repository scope and authorization checks on every read.
- Expose distinct states: host integration unavailable, CLI missing, login required, repository selection required, connected, and access revoked. Show actionable errors while disconnected too.
- Reuse the existing macOS login. Copying host `.env` paths into Docker is insufficient: the binary, checkouts, configuration, and Keychain are outside that Linux environment. Do not copy bearer tokens into browser code or image layers.
- Update launch/deployment documentation and startup diagnostics so “local” clearly identifies where the adapter runs.

## 7. Delivery order and acceptance gates

1. **Deployment and diagnostics:** complete caption removal, rebuild the actual LAN image, verify video playback/file serving, add build identity and sanitized failure traces. Establish a reproducible hardware baseline.
2. **Reliable setup foundation:** explicit UI states, early scan feedback, normalized cancellation/errors, no age rejection, firmware lifecycle fixes, and automatic transaction recovery. Resolve the traced connection failure.
3. **Code-free commissioning:** review and implement authenticated channel plus session-specific physical approval, versioned firmware/client/backend support, and ownership reconciliation. Remove setup-code UI as part of this coordinated release.
4. **Local companion and Entire:** implement host integration and repository selection; add custom BLE discovery to fulfill the fully in-window scanning requirement. Browser fallback remains clear and usable.
5. **Deployment acceptance:** verify on the real LAN endpoint, production assets, actual browser, and combined hardware firmware. Document exact tested versions and limitations.

Required checks:

- Login video loads and loops on the LAN deployment; captions/old hero copy are absent; narrow screens and reduced-motion/accessibility behavior are checked.
- Chooser dismissal, tab switch, permission denial, empty discovery, and disconnect never expose native exception text. Custom discovery remains active through application focus changes when the OS permits it.
- Secure-connection success is immediately visible; the Wi-Fi page is rendered before scan completion with a genuine loading state.
- Select Wi-Fi, wait 31 seconds and several minutes, then connect without mandatory manual refresh. Cover unchanged/changed scan generation, mesh AP changes, hidden networks, open networks, and unsupported security.
- Correct password completes; incorrect password gives an evidence-based error and can be corrected without rebooting. Backend/TLS/ticket failures are distinguishable and recoverable.
- Interrupt at selection, scan, credential transfer, Wi-Fi association, backend redemption, and lost completion response; reopen and continue automatically. Repeat before and after the old five-minute limit.
- At least ten consecutive connect/disconnect/recovery cycles without power cycling; advertising returns within a measured bounded interval after disconnection when eligible. No heap failures or leaked connections.
- Confirm physical approval rejects a neighboring account/session, spoofed identity, replay, expired ticket, claim races, and already-owned claims. Owner Wi-Fi changes remain available without enabling foreign claims.
- Verify Entire reuses macOS login, respects the active LAN owner binding and selected repositories, handles logout/revocation, and never grants another account ambient access to the host's repositories.
- Update relevant unit/integration and browser tests, run typecheck/build and firmware builds, then record hardware evidence. Existing headless Bluetooth fixtures and individual earlier successful runs are not substitutes for these physical checks.

### Investigation limits

No new Wi-Fi credentials were submitted and no board was reset, reflashed, unlinked, or claimed. The exact generic setup failure and within-window rediscovery failure remain unconfirmed until a correlated live trace is captured. All other confirmed findings above came from source, read-only runtime inspection, asset comparison, or CLI authentication status.

Protocol reference: [Espressif protocomm Security 2](https://docs.espressif.com/projects/esp-idf/en/stable/esp32h2/api-reference/provisioning/protocomm.html) documents SRP6a key exchange and AES-GCM; the current application's setup code supplies its unique SRP credentials.
