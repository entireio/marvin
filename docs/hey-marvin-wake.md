# Hey Marvin wake requirement and evaluation

The required user-facing phrase is **Hey Marvin**. Hi,ESP and renamed stock models do not meet this requirement. Idle detection must remain on the ESP32-S3; it must not upload room audio or keep a provider session open.

## Sensitivity-first alpha selection

The alpha now uses the public microWakeWord V1 Hey Marvin model with a three-inference sliding window and cutoff128. Wake activation starts automatically in the audible release profile. The final scoped physical confirmation detected all five generated positive cases exactly once, with no unattributed detections, inference/feed faults or microphone upload. Earlier broader measurements found19/20 positives and10/20 negative false activations in direct host audio, and18/20 positives with9/20 negative false activations on the physical board.

This is an explicit alpha tradeoff: favor recall and accept false positives for now. It does **not** pass the original ≥95% representative quiet/noisy recall and ≤1 false wake/hour gate. The corpus is generated speech in a limited bench arrangement, and redistribution licensing for the public model still needs resolution. Sanitized evidence is in `tests/acceptance/results/M08/hey-marvin-alpha-selection.json`; detailed audio artifacts remain private under `work/`.

The installed ESP-SR2.4.4 package does not include a Hey Marvin WakeNet model. Espressif's custom model process remains an external option. A local fixed-phrase MultiNet prototype is being evaluated; it is not a trained Hey Marvin WakeNet model or a release-approved wake engine. Espressif documents MultiNet primarily after WakeNet activation, so continuous use here requires explicit CPU, false-activation and acoustic validation.

The initial MultiNet7 image exceeded the existing OTA application slots (0x24ae30 bytes vs0x1e0000 capacity); it was never flashed. Testing the smaller quantized MultiNet5 with a single phonetic command. Only command1 may activate voice. Pronunciation is CMU `HH EY1 M AA1 R V IH0 N`, mapped through ESP-SR's documented alphabet to `hd MnRVgN`. The graph is static, with no general text-to-phoneme conversion in application code. Existing owner, factory, update and model partition offsets are unchanged. Image and model bounds must pass before flashing.

Required evidence: exact phrase positive trials; near-match and ordinary-speech negatives; repeated wake/session/idle transitions; continuity through immediate post-wake speech; no unwanted reply interruption; measured detection and audible-stop timing; idle traffic and sustained resource measurements. A successful demo does not meet the original95% recall/≤1 false wake per hour acoustic corpus gate.

Sources:
- https://docs.espressif.com/projects/esp-sr/en/latest/esp32s3/speech_command_recognition/README.html
- https://github.com/espressif/esp-sr/issues/88
- https://raw.githubusercontent.com/cmusphinx/cmudict/master/cmudict.dict
- Installed ESP-SR `tool/multinet_g2p.py`, `esp_mn_iface.h` and model libraries.

## Initial generated acoustic trials

MultiNet5 at native threshold0.90 detected0/20 positives. At0.50, removing a duplicate application score cutoff yielded1/20 positives and0/20 negative detections (`work/board/wake-corpus-1789321830550.json`). Both fail recall. Firmware processed audio with zero feed faults and uploaded no microphone samples in observation mode. These short negative tests do not establish an hourly false-wake rate. The newer MultiNet6 English model is being evaluated with the grapheme `HEY MARVIN`; it fits the existing partitions. Neither model is accepted.


## Dedicated microWakeWord evaluation

A dedicated public Hey Marvin model was located after the MultiNet experiments: Tater Totterson's `microWakeWordsV3/hey_marvin.tflite`, commit `e2e4f5ad41b7c944016d95350fc9d6fa17f3fa8f`,63520bytes, SHA256`32d550a8dae155ceb407981df189e6380f0abaf4545534b132821ef97036584a`. Metadata specifies10ms feature steps, a3-output window and0.68 probability cutoff. The original model repository is archived. The model is ignored by Git and available through `firmware/tools/fetch-wake-model.py` for private evaluation; redistribution/license provenance remains a release gate. No external model-training request or message was sent.

The original firmware wrapper uses pinned `espressif/esp-tflite-micro`1.3.3~1, `espressif/esp-nn`1.1.2, and `esphome/esp-micro-speech-features`1.2.3. It validates the FlatBuffer/schema/tensor dimensions, registers only the13 operators present in the pinned model, uses fixed64KiB tensor and4KiB variable arenas, and performs the documented40-bin feature scaling. Stream state resets when capture arms/disarms. A settling period and2-second cooldown prevent immediate reactivation. Inference failure follows the audio fault path. All idle processing remains local.

The first4 generated acoustic positives passed, each with exactlyone detection (`work/board/wake-corpus-1789324852648.json`), zero inference/feed faults andzero uploaded microphone samples. This is an initial diagnostic result, not the full acoustic acceptance gate. The completed40-case corpus failed; details follow. Speaker remains at the user-approved95/100.

Technical references:
- https://raw.githubusercontent.com/TaterTotterson/microWakeWords/e2e4f5ad41b7c944016d95350fc9d6fa17f3fa8f/microWakeWordsV3/hey_marvin.json
- https://github.com/TaterTotterson/Tater-Wake-Words
- https://github.com/esphome/esphome/tree/dev/esphome/components/micro_wake_word (feature parameters and streaming-model reference, not a runtime dependency on ESPHome)

The MultiNet6 direct-injection diagnostic failed at0.50 and detected8 times at0.05 from a single phrase plus silence (`work/board/wake-direct-result-1789324365227.json`). That low threshold was applied only to the injected diagnostic and restored before live audio. This diagnostic does not establish a usable wake engine.


## Completed contrastive evaluations (not accepted)

| Engine | Generated positives detected | Negative phrases falsely accepted | Evidence |
| --- | --- | --- | --- |
| microWakeWord V3, published .68/3 settings | 16/20 | 10/20 | `work/board/wake-corpus-1789325171360.json` |
| microWakeWord V1, published .97/5 settings | 18/20 | 9/20 | `work/board/wake-corpus-1789325970999.json` |
| MultiNet6, VAD segments,15 competing phrases,.20 threshold | 0/5 | 0/5 | `work/board/wake-corpus-1789326338239.json` |

The segmented MultiNet trial repeatedly classified the positive phrase as HEY MARTIN. Both dedicated model versions also accepted near matches; V1 accepted an ordinary sentence at maximum score. Increasing the probability threshold alone is therefore insufficient. These results drove the documented sensitivity-first alpha choice above; they remain the reason the original acoustic gate is open.

The full corpus requires exactly one detection per positive, zero per negative, fresh status, continuous processing, no detections outside case windows, and no microphone upload. Physical link faults are recorded but do not invalidate offline-only detection unless they interrupt processing. The live voice helper explicitly arms wake during a bounded trial, suppresses it afterward, and evaluates wake/interruption counter deltas rather than lifetime totals.


## Direct-audio reference isolation

`work/wake-research/reference-corpus.json` evaluates the same40 generated clips through desktop TensorFlow Lite2.18.1 with no microphone, AFE or network. V1 detected19/20 positives but falsely accepted10/20 negatives; V3 detected17/20 with9/20 negative false accepts. Both assigned maximum confidence to multiple near matches. This reproduces the broad physical failure independently of the board.

The firmware's pinned C microfrontend was compiled on the host and compared against `pymicro-features`2.0.2 over674 feature windows: all40 raw bins matched exactly after accounting for the reference package's25.6 output scaling. The diagnostic initially treated its float output as raw integer features; that host-only mistake was corrected before the reported run. Firmware scaling already matched the ESPHome reference.

A private synthetic training feasibility experiment is being prepared using the Apache-2.0 OHF micro-wake-word source at commit `4665173cd35f1cff9a61e06fc427f124766c488e`. Training and validation voices are separated; the existing40-case diagnostic corpus is excluded from training. Generated noise/reflections are augmentations, not evidence of real noisy-room acceptance. No human room audio is recorded or uploaded, and no external training service is used. A trained artifact must still pass independent acoustic, idle and lifecycle gates before deployment.

The base experimental profile keeps `MARVIN_WAKE_AUTOSTART` off so diagnostic builds remain controlled. The audible and release profiles set it on for the selected alpha behavior.


The first local training experiment (`work/wake-research/training-v1`) is rejected: nonstreaming validation on end-of-phrase windows was misleading; continuous host evaluation accepted20/20 negative phrases. A float streaming/nonstreaming comparison reproduced the failure, so quantization was not its cause. The initial feature builder retained178 frames while the chosen architecture needs214; zero padding and sparse negative windows did not represent continuous inference. The second experiment preserves the full context and samples negative speech and following silence in sliding windows. Neither candidate has been flashed.

The OHF conversion helper in the private research checkout has one recorded local change: representative quantization examples discard an incomplete final stride instead of asserting the context length is divisible by the stride. The model shape and trained weights are unchanged by that calibration-only adjustment.


## Private-training checkpoint

V2's direct40-case result was14/20 positives and13/20 false negative-class activations. V3 broadened training to Samantha/Karen/Moira and retained Daniel/Tessa as reserved test voices. It failed the reserved168-case continuous test:8/8 positives detected but160/160 negative phrases also accepted. V3's float nonstreaming/streaming paths both falsely accepted Hey Mary, before quantization. V4 corrected temporal sampling but reached only16/20 positives with7/20 false activations on the original direct corpus and5/8 positives with8/160 false activations on the reserved Daniel/Tessa set. These trained candidates remain rejected and were not flashed. Further optimization is deferred for the alpha; all failed artifacts and logs remain under `work/wake-research`.
