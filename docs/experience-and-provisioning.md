# Marvin experience and provisioning design

## A private conversation portal

After login, open the most recent conversation, or a calm empty conversation with “What would you like to explore?” An inline “Connect Entire” card explains repository access when needed. Hardware setup is optional and dismissible. Prefer “Continue with Entire” only after M0 confirms a supported identity integration. Reuse an existing Entire session when its supported flow allows it; do not collect Entire passwords in Marvin.

Desktop chrome contains the MARVIN wordmark, compact repository selector, robot status when linked, and account/settings access. A collapsible history rail contains “New conversation” and recent conversations. Conversation is a comfortably readable central column; the composer has text input, send/stop, and a clearly labeled “Talk to Marvin” control. Use structured repository cards with source/revision links, concise progress, and expandable detail. Keep partial/error states readable after a disconnected stream.

On mobile, history moves into a drawer; settings becomes a full screen; the composer stays reachable above the keyboard. No admin dashboard as the landing screen, no fleet list, and no persistent setup checklist competing with chat.

| Destination | Contents and primary action |
| --- | --- |
| Conversation | Text, voice, repository context, recent history |
| Settings → Connections | Entire status, connect/reconnect/disconnect |
| Settings → Your Marvin | One robot's status, network, firmware, capabilities; or “Set up your Marvin” |
| Settings → Voice and models | Voice selection and deployment-appropriate provider settings |
| Settings → Privacy and history | Retention, export/delete controls, microphone behavior |
| Settings → Advanced | Local endpoint, bounded diagnostics; no consumer motor-test controls |
| Help → Build documentation | Configurable public URL, accessible without portal login |

No robot linked: show a low-emphasis optional “Have a Marvin robot? Set it up” card and the permanent settings entry. Once linked, replace that invitation with a status chip such as “Marvin online” or “Marvin offline.” Clicking it opens the same “Your Marvin” details as settings. Never show an add-second-robot action.

## Visual compatibility

The inspected implementation, rather than the older aspirational design brief, establishes the starting palette. Sources: the existing `docs/assets/css/industry.css`, `docs/assets/css/site.css`, and `docs/index.html` in `/Users/stefanopedemonte/Projects/Marvin/`.

| Role | Portal starting point |
| --- | --- |
| Light background / surface / ink | `#f2f2f3` / `#e9e9ea` / `#1d1f20` |
| Dark background / surface / ink | `#17181a` / `#1e1f21` / `#efece6` |
| Brand accent | Restrained technical blue `#5980a6`; lighter `#94bce3` in dark mode |
| Interactive text | Darker blue `#416180` in light mode; `#bad4ea` in dark mode |
| Typography | Barlow body; Barlow Condensed headings/wordmark; IBM Plex Mono for code and small metadata |
| Shape and spacing | Fine rules, modest corners, generous whitespace, sparse elevation |

Use sentence case and 16 px minimum primary body/input text. Keep the wordmark's condensed technical character but avoid oversized editorial numerals in task flows. Use mono sparingly; do not copy the docs' tiny uppercase labels into essential controls. A blue brand token is not automatically a valid text/button contrast combination: test every semantic pairing before M2 sign-off. Provide at least 44 px primary touch targets, strong focus rings, 4.5:1 normal-text contrast and 3:1 essential UI/large-text contrast targets.

Voice mode presents expressive eyes, one clear status label, a compact transcript, mute, and “End voice.” The semantic vocabulary is shared with firmware: connecting, listening, thinking, speaking, interrupted, concerned/error. Do not imply microphone readiness while connecting. Animation follows actual state; reduced motion uses static expressions. Screen readers get concise final statuses instead of announcements for every token/audio frame. Self-host fonts where licensing permits to support local installs and avoid a runtime dependency on the docs site.

The apps share tokens and recognizable navigation vocabulary, not session cookies or global stylesheets. `PUBLIC_DOCS_URL` and a future docs-to-portal URL are configurable. If co-hosted later, preserve anonymous `/docs/` routes and protect `/app/` and `/api/`; migrating hosting must not put docs behind authentication. Do not assume localStorage/theme state is shared across origins.

## Initial robot setup

Entry: “Set up your Marvin” from the optional card or Settings → Your Marvin. A short four-stage wizard retains context and offers cancel/retry without changing account state prematurely.

1. **Find Marvin.** Explain “Keep Marvin nearby and turn on setup mode.” Show the physical gesture and its display signal, then “Find Marvin” opens the browser's native device chooser. Check browser capability and secure context first. Confirm the selected unit with its display code/short ID; never trust its advertised name as identity.
2. **Choose Wi-Fi.** After authenticating the BLE session, ask Marvin to scan. Say “These are the networks Marvin can see.” Show compatible networks sorted by signal, with lock/security labels, “Scan again,” and “Hidden network.” Merge repeated SSIDs only when security modes match; retain AP-level data internally. Preserve unusual/Unicode SSIDs safely. Do not show laptop scans or guessed nearby SSIDs.
3. **Connect.** Password input has show/hide and clear inline errors. State “Your password is sent directly to Marvin.” Show observed stages: connecting to Wi-Fi, checking Marvin's server, linking to your account. The selected backend is normally managed automatically; local deployment exposes a human-readable server label and advanced endpoint details.
4. **Ready.** Show “Marvin is connected” only when the authenticated backend confirms the binding and fresh presence. Return to the existing chat with its conversation and repository intact. Explain that Bluetooth is only needed for setup/network changes.

No misleading percentage timer. Each stage has a deadline and a recovery action. Persist safe progress IDs to the backend; keep the password only in transient memory and clear it on completion/cancel. If the tab closes after credential delivery, reopen the pending setup by querying status, without issuing a second claim.

## Change Wi-Fi while retaining the same Marvin

Entry: Settings → Your Marvin → Network → **Change Wi-Fi**. Also offer it in the offline details card: “Moved to a different Wi-Fi network?” The action is available while offline and needs no command delivered through the old network.

Explain “Marvin will stay linked to your account. Keep it nearby while you change its Wi-Fi.” The owner signs in using any available browser internet connection, such as mobile data, opens physical setup mode, and connects over BLE to the exact linked device. The same scan/password/connect components are reused. Verify device identity against the account binding before transmitting network data. Temporarily disable body voice and motion during network switching, with clear device feedback.

The flow obtains a short-lived owner-authorized reconfiguration ticket while the browser can reach the configured Marvin backend. It changes only Wi-Fi settings; it cannot alter owner, deployment endpoint, device identity, or credentials. Online robots require the physical setup gesture too, avoiding accidental nearby reconfiguration.

Marvin retains the previous committed configuration until the candidate Wi-Fi and authenticated backend connection succeed. If the new AP is reachable but the backend is not, say “Connected to Wi-Fi, but Marvin can't reach its server,” with retry/help options. On failure, attempt the previous network and keep BLE recovery available. At a friend's house the previous AP may also be unavailable: preserve ownership and show “Try another network,” without claiming rollback restored connectivity.

If neither browser nor robot can reach the Marvin backend, ordinary account-authorized provisioning is unavailable. Explain that a connection to Marvin's server is needed and preserve all settings. In local deployment, a LAN backend may be unreachable away from home even after Wi-Fi succeeds; remote secure access or bringing the local server is a prerequisite, not a Wi-Fi wizard feature. Offline pre-issued owner tickets or changing deployments are future capabilities, not hidden MVP promises.

## Unlinking and reset

Place **Unlink Marvin** at the bottom of Settings → Your Marvin in a separated ownership section. It is not next to “Change Wi-Fi,” and not called “Decommission,” which suggests permanently disabling the product.

Confirmation copy:

> Unlink this Marvin from your account?
>
> This robot will lose access to your Marvin conversations and services. Your chats and Entire connection will stay with your account. You can link a robot again later.
>
> Unlinking does not confirm that Wi-Fi settings have been erased from an offline robot. Before giving it to someone else, reset it using the instructions below.

Buttons: “Keep linked” and “Unlink Marvin.” Use recent authentication for this account-sensitive operation. Server-side unlink is immediate even when the robot is offline: advance the ownership epoch, revoke credentials and tickets, cancel outstanding actions/audio, and terminate any live device socket. A best-effort online clear command can be sent, but only acknowledge erasure after firmware confirms it. Releasing the account slot must not depend on receiving that confirmation.

**Reset for a new owner** is separate physical guidance. It clears stored Wi-Fi, deployment credentials, transient owner data, and playback buffers, and returns the robot to setup mode. It retains or securely regenerates the stable hardware identity according to the chosen bootstrap design; it must not let an attacker evade an existing backend ownership binding. Resetting hardware alone never frees that server binding. A new claim requires the previous owner to unlink, or a separately designed assisted recovery process. No silent ownership takeover on factory reset.

## Provisioning architecture and proposed protocol

Web Bluetooth is a transport adapter implementing Marvin-owned setup operations. Secure BLE setup must work with the firmware while Wi-Fi is absent; the normal body WSS connection is a different channel. ESP32-S3 is a 2.4 GHz device, so its scans and capability descriptor determine selectable networks. See [Espressif's ESP32-S3 guide](https://documentation.espressif.com/esp-idf/en/v5.2.6/esp32s3/index.html).

Use an audited authenticated provisioning transport rather than designing cryptography. M0's preferred candidate is Espressif protocomm Security 2, which uses SRP6a and AES-GCM, with a browser client verified against the chosen ESP-IDF/component version. Add Marvin-specific endpoints behind that secure session; UUID allocation, framing, and payload schema become versioned implementation artifacts in M1/M7. Security 0 and unauthenticated setup are not production fallbacks. [Espressif provisioning guidance](https://developer.espressif.com/blog/2026/05/simple-provisioning/) documents the transport/security separation.

**Identity bootstrap:** generate a unique device key pair on first trusted flashing/boot; protect the private key in device storage appropriate to production/DIY mode. Establish a unique high-entropy setup secret/verifier via a protected label/QR or display-assisted local process. Never ship one fleet-wide setup password. The initial claim requires physical setup mode, proof of the setup secret, and binding of the device public key to a backend enrollment ticket. DIY identities prove possession, not manufacturer attestation; record this distinction. Linked-device reconfiguration additionally requires an owner ticket signed by the already trusted backend. A factory reset cannot replace a trusted deployment silently while its binding remains active.

The BLE handshake must authenticate and integrity-bind the advertised device key/identity; copying a plain public ID is not enough. M0 proves how the selected protocomm session and the setup credential bind this identity. Freeze key storage, recovery, secret rotation, and user-visible pairing confirmation before M7 production firmware.

| Logical operation | Required behavior |
| --- | --- |
| `GetInfo` | Protocol versions, device key fingerprint/ID, 2.4 GHz and AP-security capabilities, setup state; no secrets |
| `AuthenticateSetup` | Proven possession, secure session, physical setup window, bounded attempts |
| `ScanNetworks` | Firmware performs scan; returns scan ID, timestamp/age, paginated bounded AP records |
| `GetScanResults` | SSID bytes/display label, AP identifier, channel/band, RSSI, auth mode, compatibility; never password |
| `StageNetwork` | Scan ID and selected record or explicit hidden SSID, credentials, transaction ID; candidate only |
| `AuthorizeOperation` | Initial-claim or existing-owner ticket, matching device key, nonce, operation and deployment |
| `ApplyCandidate` | Test candidate network and authenticated intended backend; emit separate progress phases |
| `GetTransactionStatus` | Idempotent recovery after BLE/tab loss; monotonic state version and categorized result |
| `CancelTransaction` | Abandon uncommitted candidate, retain previous configuration, release/expire claim reservation |

All privileged operations follow secure-session establishment. Versioned envelopes carry request/transaction IDs and monotonic sequence numbers; enforce maximum size, page count, reassembly time, and duplicate handling. BLE fragmentation/MTU differences are transport concerns. No raw provider protocol, Entire secret, user session cookie, or provider key travels over BLE.

Suggested scan policy: up to 50 AP records per scan, page size 10; label results stale after 30 seconds and rescan before applying an older selection. Firmware validates SSID length by bytes, supported authentication, credential format, and current compatibility. Hidden-network entry is advanced and still requires a successful connection by Marvin itself. WPA2-Personal is the baseline; advertise WPA3-Personal only after hardware/firmware tests. Unsupported enterprise/captive-portal networks get a clear explanation; do not promise a robot web-login browser. Open networks require an explicit supported-mode decision and disclosure in M0.

**Initial ownership transaction:**

```mermaid
sequenceDiagram
  participant U as Signed-in browser
  participant D as Marvin over BLE
  participant S as Marvin backend
  U->>D: Physical setup + authenticated secure session
  D-->>U: Verified device identity and robot Wi-Fi scan
  U->>S: Reserve owner's empty robot slot for device key
  S-->>U: Scoped, expiring claim ticket
  U->>D: Candidate Wi-Fi, trusted endpoint, claim ticket
  D->>D: Associate, obtain address, verify backend TLS
  D->>S: Redeem ticket + prove device key possession
  S->>S: Atomically bind owner and device; consume ticket
  S-->>D: Scoped credential associated with binding epoch
  D->>D: Persist credential and committed Wi-Fi atomically
  D->>S: Authenticate normal connection, report ready
  S-->>U: Binding confirmed and device online
```

Proposed claim ticket lifetime: five minutes. Bind it to owner, deployment/audience, device public key, operation, nonce and reservation. Redemption is single-use; a retry of the same transaction/key returns the same logical result. Credentials are established directly between device and backend, not exposed as long-lived browser-readable values. Account/device uniqueness is checked at reservation and again at redemption under database transaction/constraints. Expiration releases only the reservation; a completed binding is never erased by a stale cancellation.

Network state is `committed → candidate → associating → address_acquired → backend_verified → committed`, or `candidate → failed → previous_config_retained`. Binding state is independently `unlinked → reserved → linked → revoked`. Link success requires both committed device configuration and fresh normal-session presence. Power-cut recovery uses a journal/two-slot configuration with commit markers. If the server committed but the robot did not receive its credential, the same device-key proof resumes the pending transaction; it cannot claim a second slot or silently revert ownership. A reset after server commitment shows the existing binding and owner recovery path.

For Wi-Fi changes, the same network state machine runs under an existing-owner ticket; no new enrollment or binding write is involved. The firmware validates ticket signature, device key, operation, nonce, trusted deployment, and ownership epoch. In the absence of a trustworthy device clock, bind freshness to a newly generated device challenge and a short monotonic setup window; do not rely on unchecked wall-clock expiry. Revocation while offline cannot be known instantly by firmware, but the backend rejects stale epochs at reconnection. Old tickets cannot restore service access.

Credentials are never echoed in status/error events. Protect Wi-Fi/device keys at rest using the selected ESP32 storage/security configuration; raw NVS defaults are not a confidentiality claim. Backend TLS verification is mandatory in production. Local deployments need a certificate/trust bootstrap both browser and body can validate. A deliberately insecure LAN development mode must be explicit, excluded from release tests, and visibly labeled.

## Recovery and browser support

| Condition | User-visible recovery and state guarantee |
| --- | --- |
| Bluetooth unavailable / insecure origin | Explain requirements before the chooser; retain chat access; hand off setup to a supported signed-in device |
| Chooser cancelled / permission denied | Return to Find Marvin with a retry; no ownership change |
| Wrong robot selected | Explain mismatch before sending network credentials; reopen chooser |
| Wrong Wi-Fi password | Stay on network step, allow edit; retain previous committed config |
| AP disappeared / weak signal | Ask Marvin to scan again; never replace list from host OS |
| BLE disconnect after Apply | Query backend progress, or reconnect BLE to transaction; don't repeat the claim blindly |
| DHCP / DNS / TLS / backend error | Name the failed stage; preserve old configuration and offer retry/help |
| Ticket expired | Reauthorize the same transaction where safe; do not ask for an Entire token |
| Device already owned / account already has robot | Refuse takeover or second binding; show existing robot management path |
| Firmware incompatible | Stop before mutation, explain required firmware version and link upgrade guidance |
| Offline unlink | Revoke server access now; explicitly distinguish this from confirmed device erasure |

Web Bluetooth requires a secure context and a user gesture for the chooser; provision only on tested browser/OS combinations. [Chrome's Web Bluetooth documentation](https://developer.chrome.com/docs/capabilities/bluetooth) describes these requirements. Chrome on iPhone/iPad does not support website-to-Bluetooth connections, according to [Google's platform guidance](https://support.google.com/chrome/answer/6362090?co=GENIE.Platform%3DiOS&hl=en-GB).

Baseline candidate platforms are Chrome on macOS/Windows/Android, with exact releases verified in M0. Other browsers are feature-detected and tested before being advertised. For unsupported platforms, show a short continuation URL/QR containing only an opaque expiring setup reference, never a password or bearer claim token. The destination requires sign-in to the same account and a supported BLE environment. Direct iPhone setup would need a separately scoped native/approved BLE client using the same domain protocol. Ordinary portal text/voice support is evaluated independently of BLE support.
