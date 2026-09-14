# Physical audio runtime — development evidence

The available hardware is a **standalone Waveshare ESP32-S3 AUDIO board**. It has no attached actuators, display, IR, TOF or IMU. Firmware must not advertise those capabilities. Full physical M8/M9 acceptance remains unavailable on this assembly.

The owner runtime now connects the ES7210/ES8311 drivers to authenticated device voice: explicit activation, mono capture, 16-to-24-kHz resampling, bounded WSS upload, per-turn `MVA1` playback routing, 24-to-16-kHz resampling, mute/interrupt and disconnect cleanup. Idle capture does not start a provider session. USB diagnostic controls are `v` (activate), `x` (stop/mute), `i` (interrupt), `s` (numeric status). The current AFE profile includes 300ms local pre-roll; production button behavior still requires verification.

The current capture queue is bounded at 5.12 seconds and playback at eight seconds; overflow closes the session rather than growing memory. A generation counter rejects samples left over from an earlier capture session. Playback is fenced by the interaction UUID and cleared on interruption. The original owner audio profile uses a temporary half-duplex echo safeguard; that profile does not claim AEC or acoustic barge-in.

## Quiet testing

While the owner is sleeping, `CONFIG_MARVIN_SILENT_TEST=y` forces the amplifier-enable output off. No computer-speaker stimulus or board tone is permitted. This setting also prevents experimental wake detections from opening provider sessions. Live status reports the compiled setting. Silent capture tests require that report before explicit activation. These tests do not establish audibility or acoustic performance.

`firmware/tools/body-audio-silent.py` performs one bounded microphone/provider session and stops in `finally`. `board-runtime-soak.py` observes local idle processing without activating voice. Private raw serial logs remain in `work/board`; sanitized failures are retained in `tests/acceptance/results/M08/silent-audio-runtime-attempts.json`.

## Experimental local AFE profile

`sh firmware/tools/build-waveshare-afe.sh` uses the committed dependency lock: ESP-SR2.4.4, ESP-DSP1.8.0, dl_fft0.2.0 and cJSON1.7.19 on ESP-IDF5.4.2. These are separately downloaded, licensed Espressif components; the application firmware is original. ESP-SR's Espressif MIT license permits use on Espressif products and is retained by the component manager. See the [official AFE interface](https://docs.espressif.com/projects/esp-sr/en/latest/esp32s3/audio_front_end/README.html) and [pinned component release](https://components.espressif.com/components/espressif/esp-sr/versions/2.4.4).

The original profile used the supplied **“Hi, ESP” development wake model**. The alpha uses the required **“Hey Marvin”** phrase locally with the selected microWakeWord V1 model; see [wake evaluation](hey-marvin-wake.md). Historical Hi ESP trials below do not validate Hey Marvin. The model occupies a new partition at `0x3e0000`; existing ownership, factory and application offsets are preserved. The current runtime feeds the primary microphone plus PCM copied from completed speaker DMA into the AFE (`MR`, full-duplex low-cost configuration). The HAL samples both physical microphones, but this pipeline uses one; the earlier dual-microphone configuration exceeded its processing budget during playback. Muted output produces a zero reference. Shared I2S clocks constrain the initial reference alignment to a DMA window; alignment and attenuation need measurement on the actual assembly. No numerical AEC claim is made.

Initial integration exposed internal-RAM pressure when AFE, BLE and TLS started together. The experimental profile is being tuned to place bulk data in PSRAM while preserving bounded queues. Its first physical boot failed. A subsequent90-second silent run passed at15,992 processed samples/second with zero feed faults after moving bulk allocations and scheduling local processing on the supported240-MHz configuration. Live voice stability and acoustic acceptance remain open. Production protected storage and update signing remain separate release gates.

## Retained findings

- A five-millisecond idle delay rounded to zero at the configured100-Hz RTOS tick, starving the idle task. It now yields for a full tick.
- Audio/WSS queues consumed internal RAM needed for TLS. Moving bulk queue storage to PSRAM restored verified TLS connections without weakening certificate checks.
- The diagnostic console needed more stack when reporting runtime heap metrics.
- Early live capture attempts still ended on uplink backpressure. Wi-Fi power saving is disabled during an explicit voice session and restored on stop; the send deadline and bounded capture cushion are under physical test.
- The host resampler passes exact rate/count, chunk continuity, passband gain, >49-dB rejection of a10-kHz input during downsampling, and capacity rollback checks under address/undefined-behavior sanitizers. Wire framing covers audio/text limits and malformed fragments. Neither is a physical acoustic measurement.

The sequence4 release completed an uninterrupted28,799.9-second physical soak. It sustained16,000.18 processed samples/s with no AFE faults, capture, playback, provider uplink or fatal diagnostics; minimum internal free memory was42,771bytes. Wake activation was intentionally suppressed, while the detector still counted10 candidates. Afterward wake activation was restored and the authenticated device link returned online following correction of the local proxy's default TLS name. See `tests/acceptance/results/M08/physical-release-soak-8h.json`.

Remaining gates include the representative production wake/acoustic corpus, instrumented audible-stop latency, supply-power/enclosure-temperature instrumentation, full hardware fixtures and measured safety timing. M8 and M9 are not accepted.

## Native microphone transport (protocol 1.2)

The current experimental board sends native mono16 kHz s16le samples in80–120 ms bounded frames. The server negotiates `audioInputRate:16000` and performs streaming63-tap FIR conversion to the provider's24 kHz format. Speaker output remains24 kHz on the wire and16 kHz at the codec. Protocol1.0/1.1 clients retain their24 kHz input path; the new input-rate field requires1.2. New firmware requires a1.2 welcome and explicit16 kHz input acknowledgement.

Conversion tests check duration, arbitrary chunk continuity, passband gain, malformed-input rejection and reset. Routed body-turn tests cover both input rates. This change reduces upload bandwidth from48 to32 kB/s; sustained physical capture and acoustic acceptance remain separate checks.

## Body voice lifecycle fencing

The server now associates pending work with the specific device voice session and its interruption generation. Stop/interrupt during context retrieval prevents late generation; stop/interrupt during database admission cancels the resulting turn. Old provider failures and queued callbacks cannot stop a replacement session for the same device. Local cleanup continues even if a provider throws while closing. These paths are tested through authenticated simulated device sockets at both delayed boundaries, in addition to normal16/24 kHz routing tests. No physical audio quality or timing claim follows from these fixtures.

## Attended quiet audio investigation (September 13)

The isolated `afe-audible` profile enables the amplifier independently of the computer volume. It preserves the silent profile for unattended work. Short physical speech has traversed the computer speaker, board microphone, live API and board playback driver; see `tests/acceptance/results/M08/attended-paced-audio-smoke.json`. Early trials exposed crackling and truncated counts; subsequent transport, pacing and resampling changes produced complete clear replies confirmed by the user.

Physical reply bursts exposed capture/receive queue overflows. Server output now has a160ms pacing lead and cancellation fencing, with burst/interruption regression coverage for both device input rates. Playback resampling runs outside the network receive path and follows the I2S clock without an extra tick delay. Feed processing uses core0; inference, capture, playback and transport use core1 with distinct priorities. Separate upload and receive tasks, a separate TX lock and bounded receive backpressure avoid serializing both directions on microphone writes. A passing short smoke does not establish sustained duplex or acoustic barge-in acceptance.


The subsequent complete-count trial passed25.05seconds of capture and13.4seconds of submitted playback with no firmware or transport faults; the saved canonical reply contains1–10 and completed successfully. See `tests/acceptance/results/M08/attended-complete-count.json`. OpenAI cancellation now observes remote completion immediately rather than waiting for paced output to drain, sends cancellation at most once, and checks abort between PCM chunks. Three local real-SDK regressions cover cancellation before creation, during generation and after remote completion. This trial does not establish acoustic latency or sustained-duplex acceptance.

- User-requested full-count replay: user feedback “Perfect”. Test submitted214,400 playback samples (13.4s), captured401,280 samples, zero audio/link faults, AFE readError0, no intentional interruption. Subjective quality confirmed for this trial. Evidence: `tests/acceptance/results/M08/attended-count-user-confirmed.json`. Voice stopped normally; broader M8–M11 gates remain open.


After the user confirmed clear count playback, they replaced the speaker with an8Ω/1W unit. They confirmed codec volume95/100 as loud enough and clear; the audible and release profiles now use that startup value. The silent profile is unchanged.


## Wake startup and small-speaker bench checkpoint

The user replaced the speaker with an8Ω/1W unit on the same output and confirmed95/100 as loud enough and clear. The attended profile now boots at95; local console `+`/`-` changes volume by5, serialized with playback and without unmuting. These codec settings are not electrical power measurements. The prior speaker's acoustic evidence does not validate the replacement.

Wake-only acoustic trials deliver replies without a USB voice-start command. A300ms rolling RAM pre-roll feeds a bounded5.12s startup queue once wake/button activation is accepted. Capture begins before provider readiness; only upload waits for readiness. Overflow stops the session rather than silently losing speech. Pre-roll wrap, chronological output, partial drains and erasure pass host sanitizer tests. Five bounded I2C probe/bus-reset attempts handle codec startup recovery before reporting unavailable hardware.

Local speech interruption remains experimental: initial settings falsely interrupted replies. Explicit processed-channel selection, AGC disabled and more conservative VAD settings removed this behavior in complete-count trials, but the quiet synthetic overlapping-speech test then failed to interrupt. Natural-voice verification and further tuning remain required. There is no acoustic latency or corpus acceptance claim. Both audible and silent profiles compile with these changes.

- User confirmed natural-voice wake/interruption: “Worked”. Backend independently confirms count turn cancelled after5 and following request “Stop counting and say blue sky” completed with “Blue sky.” Scoped evidence `tests/acceptance/results/M08/natural-wake-interrupt-user-confirmed.json`. Natural interaction now demonstrated; instrumented acoustic timing, corpus accuracy and sustained reliability remain unaccepted. No proof here distinguishes local from provider VAD cancellation.

## Hey Marvin alpha behavior

The sensitivity-first alpha enables automatic **Hey Marvin** detection after boot. The selected public V1 detector measured19/20 positive direct-host detections and18/20 earlier physical detections, but also accepted10/20 and9/20 negative cases respectively. A final scoped board check detected5/5 positive cases exactly once with zero unattributed detections, feed/inference faults or microphone upload. This satisfies the user-approved alpha choice and keeps the original false-wake/reliability gate open. See `tests/acceptance/results/M08/hey-marvin-alpha-selection.json`.
