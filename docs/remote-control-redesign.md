# Remote control redesign

> **Implementation note — 19 September 2026:** the web remote and its shared
> firmware intent loop are present in the active working tree, but physical
> acceptance is outstanding. The Gear VR client is experimental. In particular,
> the code currently opens an initial bounded scan when no peer is stored; the
> planned explicit physical pairing gesture and confirmation flow below have
> not been implemented. Read the [current development checkpoint](current-development.md)
> before using this as operating guidance.

## Delivered architecture

Both transports submit normalized intent (`throttle`, `turn`, `headYaw`,
`headPitch`, `autonomousHead`) to the firmware. The web app sends it every
80 ms over the existing authenticated device WebSocket. The transport does not
directly choose PWM or servo angles.

At 50 Hz firmware applies an 8% stick dead zone, a squared response curve,
55% maximum track command, bounded acceleration and faster release braking.
Each motor command has a 120 ms local watchdog; input older than 250 ms ramps
to zero. This replaces the old 500 ms drive-command limit while retaining a
much shorter failure-safe stop in the remote layer. Explicit long motions may
now run for up to 30 seconds.

With Autonomous Head enabled, steering makes the head lead into the turn,
then recenter as steering returns to zero. On straight runs it performs a
slow, small scan. Manual head stick input is blended with this pose and
servo-rate-limited, so it feels like a nudge rather than a fight. The BLE
controller always enables this behavior, while the web remote retains its
switch. Both transports use the same firmware loop.

## Samsung ET-YO324 controller and pairing flow

The selected Samsung Gear VR controller is BLE 4.2 and has trigger, touchpad,
buttons and IMU. Its sensor stream is Samsung custom GATT rather than generic
HID: service `4f63756c-7573-2054-6872-65656d6f7465`, notify characteristic
`c8c51726-81bc-483b-a052-f7a14ea3d281`, command characteristic
`c8c51726-81bc-483b-a052-f7a14ea3d282`, and sensor-mode command `01 00`.
Each 60-byte notification supplies acceleration, gyroscope, touch coordinates
and button bits. This is implemented by `gear_vr_controller.c`.

1. A pet with a stored peer reconnects only to that controller. A pet without
   a peer scans for 45 seconds after boot and stores the first named Gear VR
   controller for which discovery, subscription and sensor-mode setup finish.
   Persistence must succeed before the controller is declared ready.
2. While connected, holding Home continuously for three seconds invokes
   Marvin's local unpair gesture. Motion stops at recognition, the saved peer
   is erased and committed, buffered speech is stopped, a short descending
   confirmation tone plays, and BLE disconnects. The pet deliberately remains
   idle rather than scanning, preventing it from reclaiming the controller.
   No success tone is played if durable erase fails.
3. To transfer ownership, unpair from pet 1, power pet 1 off, power pet 2 on,
   and hold the controller Home button until its RGB LED flashes. Pet 2 must
   see it during its startup window. A Home button still held as the new BLE
   connection completes is ignored until released, so pairing cannot trigger
   the pet-side three-second unpair gesture.
4. The circular touch surface maps to the same forward/reverse and turn axes as
   the web drive stick; lifting the finger stops the tracks. Its steering also
   feeds the always-on autonomous head behavior used by the web remote.
5. The trigger acts as an IMU clutch. Each press treats the current grip as
   zero; while held, gyro deltas control head yaw and pitch. Releasing it drops
   the manual offset, bounds gyro drift to one gesture, and lets autonomous
   head motion resume. Gyro bias is learned only from nearly stationary,
   trigger-released samples, and a small rate dead zone rejects hand tremor.
6. The volume +/- buttons remain direct full-left/full-right head-yaw controls
   and override IMU yaw while pressed, providing a dependable fallback.
   Disconnect, pairing expiry, bad report, or stale reports call the same
   remote dead-man stop.

`ble_remote.c` is physical-presence gated by saved-peer state and a bounded
startup window. It performs NimBLE central discovery, CCCD subscription and
the sensor-mode write before forwarding reports. Test the full two-pet flow,
reconnect, stale report, takeover, pairing timeout, power loss, audio feedback
and calibration on assembled hardware before enabling it in a release image.
