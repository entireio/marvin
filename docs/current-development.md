# Current development checkpoint

Updated 19 September 2026. This is the starting point for continued work on
the current branch. It describes the active working tree, including changes
that are not committed yet. It is not an acceptance report: a feature listed
as implemented still needs the stated physical or end-to-end evidence before
it is treated as release-ready.

## Start here

1. Read this file, then [local development](local-development.md) for the
   local hostname, certificate and fast-flash workflow.
2. Run `git status --short` before editing. The workspace deliberately
   contains related in-progress changes across the portal, server, device
   gateway, firmware, tests, and documentation. Do not reset or clean them.
3. For a software-only check, run `npm run check`. For hardware work, read
   [the firmware guide](../firmware/README.md) and use its guarded flash
   procedures. Never flash an unverified board or erase factory/owner data to
   recover from a development problem.

M0–M11 is a completed first implementation phase. Its original requirements
and historical evidence remain in [implementation plan](implementation-plan.md)
and [delivery status](delivery-status.md); they are useful context, not a
current completion gate. The older operational record is in
[`CODEX_HANDOFF.md`](../CODEX_HANDOFF.md). Prefer this checkpoint for current
work because historical documents can contain superseded instructions or
references to a previous installed image.

## What changed in the active slice

| Area | Current implementation | What still needs proof or work |
| --- | --- | --- |
| Local deployment | `local-dev` uses the stable `marvin.local` hostname, a persistent Caddy CA and optional private cardless setup data. The firmware has an app-only local-dev flash path so iteration does not rewrite factory, Wi-Fi, owner or OTA metadata. | Confirm the whole setup/reconnect flow after a real DHCP change on the intended network. Keep the local CA and factory trust root aligned. |
| Portal remote | The portal sends a normalized remote intent every 80 ms: throttle, turn, head yaw/pitch and autonomous-head state. Releasing a stick, closing the remote, or a connection failure sends or becomes zero intent. | Verify the assembled Pet, joystick ergonomics and stop distance. The UI is not evidence that motors or servos moved. |
| Motion firmware | A 50 Hz coordinator applies an 8% dead zone, squared response, 55% maximum drive command, acceleration/braking limits, a 250 ms input freshness limit and a 120 ms motor watchdog. It combines web and controller input, blends manual head input with autonomous turn/idle poses, and stops tracks on disconnect. Named body-voice motions now include `forward_bit`, `backward_bit`, `turn_around`, `turn_right`, `turn_left`, and `move_around`. | No encoder, cliff, obstacle or calibrated position feedback is present. Movement is open-loop and cannot claim a distance or angle. Confirm EEP, motor polarity, servo limits and all stop paths on hardware before unattended or edge-adjacent use. |
| Motion admission | The current branch intentionally removed the former app-level movement-arm gate. The gateway still requires an authenticated owner, linked online device, advertised capability, bounded inputs and a false stable `pickedUp` state. Firmware timeouts and the EEP stop remain local. | This is a deliberate development behavior, not a completed safety case. Do not reintroduce a remote-only gate as a substitute for physical safety sensing. |
| Body voice and direct speech | The body prompt treats Marvin as speaking for itself, maps direct backward and generic turn requests to the added primitives, uses larger head gestures, and gives a single short acknowledgement after a successful dispatch. An authenticated owner can request a direct spoken line when the Pet is idle. Device playback pacing now uses a small configurable headroom; Deepgram has a streaming synthesis implementation. | Run real provider and physical regression tests for cancellation, audio continuity, direct speech and each new motion phrase. Body playback remains intentionally half-duplex during its echo-suppression tail. |
| Bluetooth controller | Firmware includes a Samsung Gear VR ET-YO324 central client and report mapper. The circular touch surface drives forward/reverse and turn like the web drive stick with autonomous head motion always enabled. Holding the trigger uses its press pose as zero and maps relative gyro rotation to head yaw/pitch; releasing it returns head control to the autonomous pose. The volume +/- buttons remain direct yaw fallbacks. Holding Home for three seconds stops motion, durably forgets the peer, plays a descending local cue and disconnects without rescanning. A pet with no saved peer accepts the first named controller found during its 45-second startup window. | Reconnect/takeover/power-loss/axis-direction testing on assembled hardware remains incomplete. Capture advertisements to determine whether the controller exposes a pairing-mode marker; until then the startup window and one-powered-pet procedure provide the physical-presence boundary. Treat it as an experimental controller path. |
| BLE voice transport | Matching TypeScript and C frame codecs exist for bounded ATT-sized control/audio fragmentation, reassembly, expiry and oversize rejection. Their unit tests and strict standalone C compilation passed. `firmware/tools/ble_voice_bridge.py` is an early macOS CoreBluetooth-to-WebSocket bridge prototype. | There is no deployed GATT voice service, authenticated BLE session, bridge packaging/token issuance, transport selection, or physical throughput result. Wi-Fi/WSS remains the only working live Pet transport. Do not advertise BLE-only voice or skip Wi-Fi provisioning on this basis. |

## Transport and trust boundaries

The normal live path is:

```
Pet firmware -- mutually authenticated WSS over Wi-Fi --> Marvin server
Portal -------------------------------------------------> Marvin server
```

BLE currently has two distinct roles that must not be conflated:

- Security2 BLE provisioning establishes a protected setup session and can
  apply Wi-Fi/owner enrollment flows. It is not a streaming transport.
- The experimental Gear VR central link is a local controller input source.
  It does not replace the Pet/server connection.

The proposed BLE-only voice route needs a persistent nearby bridge on the
server machine, a separate authenticated/encrypted streaming session,
back-pressure and audio-codec decisions, and a recovery model. The checked-in
macOS bridge prototype only sketches the relay; it must not be run with a
long-lived device token or treated as a supported service. A browser is not a
durable replacement for that bridge. Keep this work additive and retain WSS
compatibility.

## Build and configuration map

| Goal | Entry point | Important boundary |
| --- | --- | --- |
| Web/server development | `npm run dev` | Default `.env` is loopback-only fixture mode. |
| Production-like local service | `docker compose -f deploy/compose.yaml up -d --build` | Requires private `MARVIN_ENV_FILE`, reachable HTTPS hostname and trusted Caddy public root. |
| Stable local hardware demo | [local development](local-development.md) | Use `marvin.local`; never bake a DHCP IP into firmware or factory NVS. |
| Fast local firmware iteration | `sh firmware/tools/build-waveshare-local-dev.sh` then guarded `board-flash.py --profile local-dev --app-only ... --flash` | App-only writes are local-dev only and cannot bootstrap signed OTA. |

The active hardware layout is `afe-v3`: two 4 MiB OTA application slots at
`0x20000` and `0x420000`, plus the 4 MiB wake-model partition at `0x820000`.
The current firmware leaves about 54% of each application slot free, and the
layout keeps 3.875 MiB at the end of flash unallocated for future eye assets,
diagnostics, or another reviewed data partition. Builds enforce at least 10%
application-slot headroom.
| Audible motion image | `sh firmware/tools/build-waveshare-motion-afe.sh` | Enables AFE/wake/motion profile. Use the guarded full flash procedure in the firmware guide, not app-only flashing. `MARVIN_FLASH=1` is required by its build-and-flash wrapper. |

Configuration is validated in `apps/server/src/config.ts`. In particular,
`local-secure` and `cloud` reject IP-address origins, while hardware setup in
`local-dev` also requires a stable HTTPS hostname. `LOCAL_DEV_SETUP_CARDS_FILE`
is allowed only with password-protected local authentication and is private
operator input, never browser storage or source control.

## Immediate continuation checklist

1. Establish a reproducible physical baseline: protocol compatibility,
   authenticated presence, wake, one voice turn, head movement, motor polarity
   and every local stop path. Record observed movement separately from an
   `accepted` device result.
2. Finish or explicitly defer the BLE-only transport design. The next viable
   implementation is a small macOS bridge plus firmware GATT service and
   authenticated session—not wiring raw frames into the provisioning service.
3. Add hardware-in-the-loop coverage for remote watchdog expiry, stick release,
   connection loss, picked-up refusal, Gear VR disconnect and named motion
   cancellation. Keep tests away from edges and use a clear, level surface.
4. Reconcile the longer motion plans with the current no-arm-gate decision
   before using them as implementation instructions. Their coordinator,
   calibration and physical-evidence recommendations remain relevant; their
   former gate assumption does not describe this branch.

## Known limits

- The active changes have automated coverage for the TypeScript-facing remote
  and BLE frame behavior, but the newly added firmware paths do not constitute
  a release build or physical acceptance.
- No browser-originated text or web voice turn may actuate the Pet; only the
  dedicated authenticated remote API and body-voice physical tools can send
  commands.
- The robot has no verified cliff/obstacle sensing, wheel encoders, or servo
  position feedback. Treat all movement as supervised open-loop operation.
- Keep secrets, owner/factory records, Wi-Fi credentials, backups and raw
  board logs in ignored private storage. Do not copy them into documentation,
  test fixtures or issue reports.
