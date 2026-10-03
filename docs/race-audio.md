# Race and Mos Aster audio

## Acceptance and evidence

The October 2 redesign responds to the player's rejection of inaudible driving,
missing boost, excessive squeaks and silent exploration. The previous implementation's
passing smoke test explicitly required departure silence and never measured speed
loudness or boost. Those were inadequate acceptance criteria.

This revision is an audition candidate until actual listening establishes perceived
quality. Native AVAudioEngine renders and independent Codex Astra Ultra review can
establish behavior, levels, headroom and code correctness. Neither constitutes
hearing the result or establishes AAA/film parity.

Evidence is collected under `../marvin-town-planning/audio-redesign/`. `baseline/`
contains the former native fixture. `pass1/` preserves the first redesign render,
including failed speed-dynamics checks. The final validation paths and measured
results are recorded below when verification completes.

## Second October 2 correction

The user rejected the first replacement's drill-like WALL-E and abrasive boost.
The drill and ratchet recordings have now been removed from all drivetrain,
boost and contact assets. Licensed electric-machine and scooter recordings provide
separate low/high motors; soft rolling contact sits lower in the mix. Boost is
broadband pressure with a low motor body, with quieter transient cues, especially
for opponents. Source gain is stabilized, and the motor banks occupy separate
frequency ranges to limit phase-dependent loudness fluctuations.

[Second-round evidence](audio-validation/2026-10-02-round2/README.md) supersedes
previous sound-quality assumptions. Sandstorm and localized city layers remain.
The bundled decoded audio uses about205MB of stereo float buffers. No listening
comparison with WALL-E has been performed by the agent or judge.

## Research and resulting design

- [BeamNG's engine audio tuning](https://documentation.beamng.com/modding/vehicle/sections/sounds/engine_audio/)
  separates RPM, load and smoothing. This design uses drivetrain speed for mechanical
  pitch, actual post-assist throttle/braking for load, and chassis speed for contact.
- [Wwise blend containers](https://www.audiokinetic.com/en/public-library/2024.1.7_8863/?id=blend_container_property_editor&source=Help)
  provide a layered-speed design reference. Low and high mechanisms crossfade,
  rather than relying on one weak pitched loop at every speed.
- [Ben Burtt's first-person WALL-E interview](https://designingsound.org/2009/09/ben-burtt-special-wall-e-the-definitive-interview/)
  describes performed machinery, including a generator for motion. The new bodies
  combine recorded electric mechanisms with different filters, rates and contact
  textures. No film recordings or claim of using Burtt's original devices is made.
- Earlier voice research remains relevant: [R2-D2's vocal/electronic construction](https://www.starwars.com/news/5-iconic-star-wars-sound-effects-and-how-they-were-made-starwars-com),
  [BB-8's performed talkbox approach](https://bigshinyrobot.com/star-wars/interview-star-wars-force-awakens-sound-editing-team/).
  Existing licensed vocal performances are now short, filtered accents; resemblance
  has not been established through listening by the agent.

## Mechanical feedback

Every droid has low transmission, high-load mechanism, hard-ground contact, sand
contact and sustained boost layers. Marvin uses a warm servo body, R2 a lighter
mechanism, BB-8 a lower rolling mass, and WALL-E a heavier fluttering tracked layer.
These are design intentions, not a subjective quality verdict.

`Simulation.appliedDriveInput` records the command after player assistance or AI
selection. Effective braking includes assisted brake pressure. Boost responds to
that command immediately, including at matched speed, with distinct onset,
sustained thrust and release. It does not infer boost from a speed threshold.
A bounded critical one-shot pool keeps boost separate from speech and collisions.
Counters record successfully scheduled events and can be inspected per robot.

Mechanical pitch and gain develop over the full 0–12 m/s range. Airborne contact
layers fade away while powered mechanisms remain. Surface contact crossfades from
course material to sand outside the track. Motors remain active in town, dunes,
and after the finish; only their distance to the listener can silence them.

## Voices and race events

Only the player's initial movement gets a greeting. Ordinary acceleration uses
mechanical feedback and never adds a voice line. Significant fresh contacts can
trigger a restrained startle; near player/rival overtakes retain directional
reactions and continuous-progress hysteresis. Six-variant shuffled bags avoid
immediate phrase repetition.

The global speech interval is eight seconds; a character normally waits 22–30
seconds, and overtake pairs wait eighteen seconds. Only one expression plays at
once. Lines are shortened to 0.85–1.15 seconds and mastered below active drivetrain
feedback. This removes the old multi-second squeak dominance without deleting all
character expression. Tests include stop/go/contact/rank-crossing activity, not
just constant-speed silence.

Countdown, go, finish and collision cues are separate from voices. Crowd zones
stay audible through the finish and the last racer's extra laps. After all racers
leave earshot, the stadium stays quiet, while the rest of the soundscape continues.

## City, weather and listening perspective

Town sources are derived from actual authored venues, repair tents and populated
conversation/market groups. The market has licensed field-recorded walla; workshops
have machinery and sparse tools; the cantina has muffled walla and original foley
percussion. These are proximity sources, not a globally playing city soundtrack.
Distant sources soften spectrally and fade out. Desert wind continues beyond town.

A sandstorm adds separate low turbulent pressure and dry airborne-grit layers.
Gain, timbre and stereo direction follow the actual wind vector, accumulation and
building shelter used by the simulation. Outdoor market/workshop activity reduces
during storms; cantina activity remains sheltered. A fixed postrace view samples
wind at the course, independent of the departing player's shelter. Tests compare
clear, onset, full gust, shelter, driving, boost and clear-again renders.

During driving, the listener follows the player with camera-oriented stereo.
After the finish it stays at the course center, matching the fixed overview.
These are stereo distance/filter cues, not HRTF binaural sound or a room-acoustics
simulation. Solid-building acoustic occlusion is not modeled.

## Runtime and lifecycle

Thirty looping voices and twenty bounded one-shot slots feed AVAudioEngine through
an Apple peak limiter. Sixteen one-shot slots are reserved for short boost edges;
four serve speech and other events. The output stage retains headroom. All WAVs
are decoded during level preparation (approximately 182 MB stereo float PCM).
There is no decoding, download or sample synthesis during gameplay updates.

Mix smoothing is 75 ms for moving sources and 650 ms for environmental changes.
Pause, lost focus, mute and menus stop playback; restart clears conversation and
weather/crowd lifecycle state. Countdown can play audio while motors remain quiet.
Device startup failure is logged and retried without blocking gameplay.

## Reproduction and licensing

Full attribution and modifications are in
`apps/simulator-macos/Resources/Audio/CREDITS.md` and the app's About credits.
Workshop recordings are bart's CC0 collection. Voices derive from Tinsin's CC BY
3.0 Generic Hero Effort Noises. Stadium recordings retain Gregor Quendel's CC BY
4.0 and AuraVoice's CC0 credits. Market walla uses bolkmar's CC0 field recording;
its source is the publicly available HQ preview, documented in `sources.json`.
Original DSP supplies wind, storm pressure, grit, boost pressure and race cues.

Download and verify `scripts/audio/sources.json` into a source folder. Extract
`workshop.7z` there, keeping `workshop/`. With numpy, scipy and ffmpeg available:

```sh
python scripts/audio/characters.py SOURCE_FOLDER SOURCE_PREVIEW_FOLDER
python scripts/audio/soundscape.py SOURCE_FOLDER
python scripts/audio/verify-assets.py ASSET_REPORT.json
```

Always run both generators in that order: soundscape mastering intentionally
replaces the older character output. `SOURCE_PREVIEW_FOLDER` is intermediate;
final playable evidence must come from the native engine, not those old-gain
source previews. No manual normalization is applied to native audition renders.

Build with `apps/simulator-macos/build-app.sh`, then:

```sh
"apps/simulator-macos/.build/Marvin Simulator.app/Contents/MacOS/MarvinSimulator" \
  --audio-smoke-test OUTPUT_DIRECTORY
MARVIN_AUDIO_CAPTURE=1 \
  "apps/simulator-macos/.build/Marvin Simulator.app/Contents/MacOS/MarvinSimulator" \
  --navigation-smoke-test NAVIGATION_OUTPUT_DIRECTORY
```

The first command renders each character, matched-speed boost, real physics racing,
city categories and storm states. It checks speed dynamics, boost transitions,
voice density, airborne contact, output peak, stadium departure and lifecycle.
The second adds native audio and a timed position/mix log to the existing actual
G/C-input, gate passage, town/dunes/reentry validation. Its initial course fixture
is positioned at the gate; the subsequent trip uses driving and collision physics.
Neither test is a real-time output-device recording or a perceptual listening test.

## Verified October 2 results

The final native suite passed **58 checks**. Committed machine-readable evidence
is in [audio-validation/2026-10-02](audio-validation/2026-10-02/).

| Droid | Slow→cruise rise | Cruise→full rise | Matched-speed boost rise |
| --- | ---: | ---: | ---: |
| Marvin | 7.72 dB | 7.35 dB | 5.55 dB |
| R2-D2 | 7.19 dB | 7.72 dB | 5.15 dB |
| BB-8 | 7.73 dB | 7.62 dB | 5.92 dB |
| WALL-E | 10.99 dB | 7.39 dB | 6.17 dB |

The one-minute changing-physics render played three expressions. Scheduled boost
onsets were verified separately for each rival. Native fixtures also pass rapid
100 ms taps, actual assisted braking, native-camera stereo orientation, head-on
collision feedback (9.025 m/s collision velocity loss), fixed-overview storm
perspective, pause/mute/restart and continued world sound after stadium departure.

All 161 bundled WAVs pass PCM/level/boundary checks. The real-race render peaks at
0.6732, collision fixture at 0.7075 and storm sequence at 0.5695; stress output
remains below 0.95. The limiter is not a substitute for a balanced audible mix.

The final navigation test drove COURSE→TOWN→DUNES→TOWN→COURSE over **166.85 simulated
seconds** and passed. Town/dune motors persist, local activity fades away in dunes
and returns on reentry. This trip does not visit the cantina; its evidence is the
separate city fixture. Final PCM files are in `audio-redesign/final/` and
`audio-redesign/navigation-final/` under the external artifact directory above.
`audio-redesign/audition/index.html` provides unnormalized MP3 previews and labeled
jump points. These are offline native-engine renders; separate live runs verify
output-engine operation.

Two sequential **45-second live runs on Apple M2, 1920×1080, midday chase camera**
ran with audio active, without concurrent building, encoding or other simulator
instances. Clear weather: 59.93 callbacks/s, p95 17.39 ms, p99 19.66 ms, three
intervals >25 ms, none >50 ms; CPU update p95 5.05 ms. Storm: 60.00 callbacks/s,
p95 17.28 ms, p99 18.33 ms, no intervals >25 or >50 ms; CPU update p95 2.14 ms.
These measure SceneKit callback intervals, not GPU execution or display presentation.
No same-session baseline live benchmark was captured, so no performance improvement
claim is made. Core SimulationChecks also passed after collision telemetry changes.

Independent Astra Ultra review identified and closed reversed camera stereo,
post-collision speed hiding head-on impacts, assisted braking retaining boost,
rapid-tap release loss and misleading cumulative event checks. Perceptual listening
and AAA parity remain unverified; neither agent had an audio-perception tool.

## Repair-area proximity correction

The infield tents now use a 14 m audible radius and a 2.5 m proximity scale,
with a smooth 2.5 m fade into the infield beyond the retaining wall. From the
racing lane, repair gain is suppressed and low-pass filtered to 1.8 kHz; it
opens up near the tents. Other town workshop emitters keep their existing mix.

The rebuilt native audio suite passed 61 checks; see
[audio-validation/2026-10-02/repair-proximity.json](audio-validation/2026-10-02/repair-proximity.json).
Across 768 course positions at the centerline and both lane edges, maximum
combined repair gain fell from 0.18979 to 0.00274 (36.8 dB). Both tent approach
sweeps increase monotonically to 0.2600. The PCM approach/return render is at
`/tmp/marvee-repair-audio/repair-approach.wav`. These are synthetic spatial sweeps
through the native mixer, not collision-tested drives through the entrance.
Independent review also checked rising gain along the actual entrance coordinates.
Perceptual loudness remains unverified; these measurements are not listening acceptance.
