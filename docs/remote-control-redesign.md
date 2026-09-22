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
servo-rate-limited, so it feels like a nudge rather than a fight. The same
loop is used for web and BLE input.

## Samsung ET-YO324 controller and pairing flow

The selected Samsung Gear VR controller is BLE 4.2 and has trigger, touchpad,
buttons and IMU. Its sensor stream is Samsung custom GATT rather than generic
HID: service `4f63756c-7573-2054-6872-65656d6f7465`, notify characteristic
`c8c51726-81bc-483b-a052-f7a14ea3d281`, command characteristic
`c8c51726-81bc-483b-a052-f7a14ea3d282`, and sensor-mode command `01 00`.
Each 60-byte notification supplies acceleration, touch coordinates and button
bits. This is implemented by `gear_vr_controller.c`.

1. The intended release design is that Marvin starts non-discoverable and
   restores only its previously bonded peer from NVS; this works without Wi-Fi
   or the web server. The current experimental implementation scans for a
   bounded initial pairing window when no peer is stored, then reconnects only
   to that stored peer.
2. A deliberate physical gesture (hold the existing assigned button for five
   seconds, or a dedicated pair button) should open a 60-second pairing window
   and give a visible/audio confirmation. This remains to be wired.
3. Hold the ET-YO324 Home button to enter pairing mode. A production flow must
   display/announce a confirmation code and require a physical press before
   storing one bonded peer. That confirmation is not implemented yet.
4. The circular touch surface maps to the same forward/reverse and turn axes as
   the web drive stick; lifting the finger stops the tracks. After a short
   neutral calibration, forward and side wrist tilt map to head pitch and yaw.
   The volume buttons adjust speaker volume once per press. Disconnect, pairing
   expiry, bad report, or stale reports call the same remote dead-man stop.

`ble_remote.c` remains physical-presence gated: it does not scan at boot. The
next hardware pass binds its pairing window to the existing button gesture,
then performs NimBLE central discovery, CCCD subscription and the sensor-mode
write before forwarding reports. The report parser, mapping and safety stop are
already in place. Test reconnect, stale report, takeover, pairing timeout,
power loss, and calibration before enabling it in a release image.
