# Motion control and embodied voice plan — 18 September 2026

> **Current-branch note (19 September):** this document is the longer
> acceptance-oriented design plan. The active branch now has a remote-intent
> coordinator and intentionally has no app-level movement-arm gate. Read the
> [current development checkpoint](current-development.md) before treating any
> “bench-disabled tracks” or gate language below as current behavior. The
> coordinator, calibration and physical-evidence work in this plan remains
> unfinished.

## Current state

M8 called for local gaze, bounded trajectories, safety, and expressions that
continue without a network connection. M9 called for capability-gated physical
requests, asynchronous completion, interruption, and physical acceptance. The
M0–M11 implementation phase is closed; this document is now a post-phase
motion hardening plan. The current code provides a useful command and safety
skeleton, while its physical validation remains ongoing.

| Area | Implemented | Missing or unverified |
| --- | --- | --- |
| Request routing | Physical tools are available only to voice turns from the authenticated body. Head angles and four named track motions have bounded schemas. The device gateway enforces capability, binding, route, deadline, and picked-up checks. | Spoken intent and reply style rely on the model prompt. There are no end-to-end behavioral checks for “look right,” concise acknowledgement, or no post-action speech. |
| Command lifecycle | Commands have IDs, deadlines, boot/epoch fencing, a device-side durable deduplication ledger, immediate acceptance, later completion/failure, and local track expiry. | The model receives the initial dispatch result, usually `accepted`, not a verified completion. Completion is recorded by the gateway but is not translated into the voice interaction. The current prompt encourages a spoken explanation of this distinction. |
| Head | GPIO8/9 servo output, configured centers/reversal, bounded yaw/pitch, and simple listening/thinking/speaking pitch cues exist. | A pose change jumps directly to its final PWM value; `durationMs` is used as command occupancy, not trajectory time. There is no velocity/acceleration limit, calibration record, rest policy, or independent motion scheduler. A cue is triggered at `voice_ready`, not on local wake. No idle behavior exists. Servo output has not been physically accepted after the latest pin change. |
| Tracks | DRV8833 EEP sleep control, 120 ms remote watchdog renewal, and named sequences exist. | There is no accepted cliff/pickup safety configuration for free movement, encoder feedback, obstacle policy, or verified turning angle. Named turns are open-loop approximations. |
| Device state | The body receives voice lifecycle events and stops tracks on link failure. | Motion state is split across the link task and actuator module. Voice cues are skipped while a command is active and are not rescheduled afterward. There is no single priority/arbitration owner for wake, conversation, explicit requests, idle movement, safety, and setup. |
| Physical evidence | The latest motion image was flashed and USB enumeration checked. The user observed an idle tilt servo and an audible wake reply. | The delivery status records link faults. Commanded head/track movement, safety stops, and liveness have no physical acceptance evidence. |

## Desired interaction contract

- On a local “Hey Marvin” detection, begin a small, visible head attention motion immediately, before provider connection or speech. Continue the normal listening cue once voice is ready. The movement works offline when wake detection is active.
- For a simple, safe motion request, start the motion promptly and say one short acknowledgement such as “Sure.” Do not narrate dispatch states or speak again when it completes. If a local safety gate rejects the request, give one brief, truthful reason. Do not claim completion from acceptance alone.
- Treat “look right” as a head request. Treat “turn right” as ambiguous between head and body rotation unless conversation context resolves it; choose a documented default and test both common phrasings. Track rotation requires an enabled, physically accepted safety mode.
- While awake but idle, make occasional small, slow head movements with randomized intervals and pauses. Idle motion yields instantly to wake, speech, explicit commands, safety events, setup, and power constraints. It never drives the tracks or opens a microphone/provider session.

## Architecture

1. **Local motion coordinator.** Add one firmware-owned coordinator above the actuator HAL. It owns head target/current pose, bounded trajectories, one-shot gestures, idle scheduling, and preemption. Inputs are local wake, voice state, explicit commands, setup/sleep, connectivity, and safety. Priority: hard safety/setup > explicit command > wake/listening/speaking cue > idle. Keep deterministic limits and stop behavior in firmware; the backend supplies only bounded semantic intent. Make the coordinator nonblocking so audio and link tasks never wait for a servo sweep.
2. **Actuator HAL and calibration.** Keep PWM and EEP control below the coordinator. Use calibrated center, direction, mechanical limits, maximum speed, and acceleration for each servo. Interpolate at a fixed tick; clamp every intermediate pose. Define boot and power-down behavior that avoids a sudden sweep or servo buzz. Preserve the track expiry task and physical EEP default-off behavior.
3. **Command lifecycle.** Route remote head and track commands into the coordinator and return `accepted` only after local admission. Emit `completed`, `failed`, or `cancelled` from the coordinator, with a clear distinction between commanded trajectory completion and measured physical execution. Maintain the existing durable command ID ledger and boot/epoch/deadline fences. Safety preemption and link loss stop tracks; a head command should have an explicit safe settle policy.
4. **Voice intent and speech.** Add a body-voice motion policy that maps direct requests to bounded semantic actions and produces a short acknowledgement. Remove dispatch-state narration from the body-voice answer guidance while retaining truthful diagnostics for rejection. A successful acknowledgement can be spoken after local acceptance; late completion updates diagnostics silently. Keep web-originated physical tools forbidden. If model tool selection is too slow for “as soon as possible,” add a narrow deterministic intent path for high-confidence, direct motion phrases, still using the same authorization and device command path; measure before adding it.
5. **Liveness policy.** Generate idle head gestures locally with small amplitude, smooth speed, variable dwell, and a configurable rate. Suppress during sleep, setup, mute-sensitive states as appropriate, active user motion, fault, and low-power operation. On wake, preempt idle and make a distinct attention gesture immediately. Motion should never imply listening if wake activation is disabled.
6. **Track safety.** Keep drive pulses bounded and validate the actual sensor configuration, environment, and local stop latency before accepting autonomous movement. A head-only experience can ship before track motion.

## Delivery sequence and gates

1. **Stabilize the hardware path.** Diagnose current device-link faults; verify current firmware/backend protocol, authenticated presence, head GPIO8/9 wiring, servo power, center/direction, and EEP polarity on the assembled device. Record a safe head command and its accepted/completed statuses. Do not infer physical movement from command status alone.
2. **Fix request behavior.** Add fixtures for “look right/left/up/down,” “turn right/left,” “move forward a bit,” ambiguous wording, rejection, timeout, and cancellation. Require one brief acknowledgement for an admitted action, no trailing commentary, and a concise reason for rejection. Verify no web turn can move the Pet and no duplicate command moves it twice.
3. **Build the local coordinator and trajectory.** Unit-test bounds, timing, preemption, stale commands, cue restoration, and fault handling with a fake actuator clock. On hardware, measure start latency, travel time, overshoot/buzz, and head motion during audio capture/playback. Confirm track stop on timeout, disconnect, cancellation, pickup, and any available safety input.
4. **Add wake and idle liveness.** Start the attention gesture from the local wake event. Run a 60-minute offline and online idle observation: visible but unobtrusive head movement, no track motion, no provider session, and no room-audio upload. Test wake during idle movement and explicit command during each voice phase.
5. **Physical acceptance.** Repeat the M8/M9 safety and mixed-surface gates on the actual assembled Pet. Collect user observations for naturalness, noise, and responsiveness; tune bounds and cadence without weakening safety. Only then enable wider track capability or claim embodied-motion acceptance.
