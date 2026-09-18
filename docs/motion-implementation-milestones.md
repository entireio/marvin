# Motion and liveness implementation milestones — 18 September 2026

This plan implements [the motion architecture](motion-control-plan.md). Milestones are sequential. Record evidence at each gate before installing the next image on the Pet.

## M0 — Establish a reliable physical baseline

1. Record the running app slot, OTA state, flash size, partition table, firmware and wake-model hashes, factory/owner identity, backend version, and a fresh verified double-read flash backup.
2. Reproduce and classify the recent device-link faults. Confirm authenticated presence, wake activation, a voice turn, and a safe head command over the current firmware/backend pairing. Verify servo power and GPIO8/9 wiring without enabling tracks.
3. Measure wake-to-head-signal, request-end-to-head-signal, acceptance/completion, and speech timing. Record physical movement separately from command status.

**Gate:** A baseline report distinguishes transport failure, command rejection, PWM output, servo movement, and voice output. Recovery images and backup are identified. Do not migrate flash while the link is unstable.

## M1 — Expand ESP32-S3 application capacity

The current `firmware/partitions-afe.csv` has two `0x1e0000` (1,966,080-byte) OTA app slots. The signed release image is 1,932,176 bytes, leaving **33,904 bytes**, about 1.7%. The motion AFE image is 1,917,344 bytes, leaving 48,736 bytes. Substantial new firmware work needs more room. The board has 16 MB flash; the current 4 MB wake-model partition ends at `0x7e0000`.

1. Set a capacity target of two app slots of at least 3 MiB and a proposed release-build guard requiring 10% free space. Confirm the model size and all other flash reservations.
2. Validate a proposed `afe-v2` map: preserve NVS, PHY, OTA metadata, and factory offsets through `0x20000`; `ota_0` at `0x20000`, size `0x300000`; `ota_1` at `0x320000`, size `0x300000`; model at `0x620000`, size `0x400000`. This ends at `0xa20000`, within 16 MB. Check alignment, bootloader assumptions, model loading, and the actual board flash map before adoption.
3. Update the partition CSV, build profiles, compiled layout identifiers, signed release packaging, backend validation, factory trust, bootstrap installer, and host tests together. Use a distinct `afe-v2` identifier. Reject cross-layout releases.
4. Build a guarded **layout migration** path separate from application-only OTA. Verify the exact board and fresh backup. Stage and hash both new app images and relocated wake model; preserve factory identity, owner/Wi-Fi NVS, and recovery data. Switch table/boot selection only after verifying all staged regions. Test interruption at each write boundary. Where overlap prevents in-place rollback, require the external verified backup and a tested restore procedure before writing.
5. Prove migration in a synthetic flash fixture and on the actual board. Verify healthy boot, owner link, wake/audio, signed update to the other enlarged slot, and rollback.

**Gate:** Both enlarged slots accept signed images, update and rollback work, identity and model survive, and the release build enforces the capacity guard. If the migration gate fails, keep the existing layout and defer substantial firmware motion additions.

## M2 — Define motion semantics and one local owner

1. Specify the mapping for “look left/right/up/down,” “turn left/right,” “move forward a bit,” and “move around.” Bare “look” controls the head. Resolve bare “turn” consistently and state when track turns are unavailable; never promise an exact turn angle without feedback.
2. Add a firmware motion coordinator as the sole owner of head goals, voice/wake cues, idle behavior, and track admission. Its calls from audio and link tasks must be nonblocking. Priority: hard safety/setup, explicit user request, wake/voice cue, idle.
3. Route command ID, deadline, binding/boot fencing, cancellation, and the existing deduplication ledger through it. Define `accepted` as locally admitted, `completed` as the programmed trajectory or pulse ended, and separate open-loop completion from sensor-confirmed motion.
4. Test arbitration, deadline expiry, duplicate delivery, reboot, disconnect, cue restoration, and bounded queue/memory with a fake clock.

**Gate:** One component owns actuator state. A user command cannot be overwritten by an idle or voice cue; retries cannot repeat motion.

## M3 — Smooth and calibrate head movement

1. Measure and record servo center, direction, mechanical limits, supply behavior, and rest policy for the assembled Pet. Preserve GPIO8/9 and USB operation.
2. Replace direct pose jumps with a fixed-tick trajectory using velocity and acceleration limits and clamped intermediate poses. Define `durationMs` as trajectory time or replace it consistently in firmware and server contracts.
3. Define safe boot, reconnect, cancellation, and output-fault behavior. Keep track EEP default-off and local expiry independent of the head controller.
4. Measure physical start latency, travel time, overshoot, servo noise, power draw, and effects on wake/microphone/AEC; tune on hardware.

**Gate:** Head motion starts promptly, moves smoothly within calibrated limits, and does not impair voice. `completed` means the programmed trajectory ended, not that position feedback verified the pose.

## M4 — Make spoken requests act promptly and answer briefly

1. Add body-voice-specific behavior: call the bounded physical tool for a direct request, then give one short acknowledgement such as “Sure.” Remove acceptance-state narration and post-action speech. Give one brief truthful reason for local rejection.
2. Keep detailed statuses in telemetry and tool records. Late success is silent. Escalate only a failure that needs user action or affects safety.
3. Test “look right,” the observed “took to the right” transcription, “turn right,” interruption, offline link, unavailable head, bench-disabled tracks, and web/body routing. Measure request-end-to-motion latency. If model tool selection is too slow, add a narrow high-confidence direct-intent path using the same gateway, authorization, and command ledger.

**Gate:** At least 20 varied direct head requests on the physical Pet yield the intended bounded motion, one brief acknowledgement, and no trailing explanation. Rejections are concise and accurate. Web text/voice never actuates the Pet.

## M5 — Add wake attention and idle liveness

1. Deliver a local wake event to the motion coordinator at detection time, before backend `voice_ready`. Preempt idle motion with a small attention gesture, then transition through actual listening/thinking/speaking states. Never imply listening when wake activation is off.
2. Add occasional small, slow idle head gestures with varied pauses and a rate ceiling. Tracks stay still. Idle yields immediately to wake, explicit requests, setup, fault, sleep, and low-power policy. Make cadence/amplitude tunable.
3. Run online and offline idle observations. Measure wake-to-motion latency, gesture frequency, preemption, servo noise/power/temperature, wake interference, provider sessions, and room-audio upload.

**Gate:** “Hey Marvin” creates visible attention before voice readiness. During a 60-minute idle run, the Pet feels subtly alive while tracks remain still and no provider session or room-audio upload occurs. Offline liveness and safety remain local.

## M6 — Accept safe track movement and embodied behavior

1. Keep ordinary track motion disabled until the assembled Pet's cliff, obstacle, pickup, and local stop behavior are physically verified. Retain the short supervised bench arm for development. If required sensors are absent, release a head-only experience and state that scope explicitly.
2. Test every named primitive, duplicate/expired command, disconnect, cancel, pickup, safety input, power fault, and watchdog stop. Measure motor-enable cutoff and physical stopping distance separately. Calibrate open-loop turns without claiming exact angles.
3. Complete the M8/M9 physical gates: mixed-surface routing, conversation continuity, audio latency/barge-in, safety injections, duplicate/reboot behavior, and an extended motion/audio soak. Review naturalness and responsiveness with the owner.

**Gate:** Track motion is available only in a validated safety mode. Physical M8/M9 evidence is recorded and delivery status names any remaining head-only or sensor limitations.

**Order:** M0 baseline → M1 flash migration → M2 coordinator → M3 head trajectories → M4 voice behavior → M5 liveness → M6 track safety. A useful head-only experience can be accepted after M5 while M6 remains gated by hardware safety.
