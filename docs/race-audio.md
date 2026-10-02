# Race audio

## Research and audition status

The first oscillator-only pass was rejected by the player. Correct playback,
headroom and frame timing did not establish convincing character sound. This
revision uses recorded vocal performances and mechanical foley. It remains a
listening candidate: automated tests do not measure resemblance to movie voices,
and the agent did not hear the rendered audio.

The concrete design references are:

- [Ben Burtt on R2-D2](https://www.starwars.com/news/5-iconic-star-wars-sound-effects-and-how-they-were-made-starwars-com): vocal inflections blended with electronic sound. Here, recorded efforts shape pitch/envelopes, with alternating whistles, chirps, trills and vocal rasp.
- [Matthew Wood and David Acord on BB-8](https://bigshinyrobot.com/star-wars/interview-star-wars-force-awakens-sound-editing-team/): electronic material performed through a talkbox. Here, a 22-band analysis/synthesis vocoder transfers a recorded mouth's changing spectrum onto a rich electronic carrier. This approximates that process; it is not a recording of a physical talkbox.
- [Ben Burtt's WALL-E masterclass](https://editorial.rottentomatoes.com/article/exclusive-ben-burtts-walle-sound-masterclass/): human expression combined with machinery and recorded physical sources. WALL-E now blends processed vocal effort with a lower electronic carrier and mechanical recordings.
- Marvin uses the same performed-sound approach with a warmer, restrained register and smoother filtering. The rejected repeating three-note signature is removed.

No film audio or actor imitation recording is bundled. Vocal material is Tinsin's
[Generic Hero Effort Noises](https://opengameart.org/content/generic-hero-effort-noises),
CC BY 3.0. Driving material is bart's recorded
[68 Workshop Sounds](https://opengameart.org/content/68-workshop-sounds), CC0.
Derived clips, modifications and license links are documented in
`apps/simulator-macos/Resources/Audio/CREDITS.md` and the app's About credits.

## Speech behavior

Each character has 30 clips: acknowledgement, effort, startle, overtaking the
player and being passed by the player, each with six different phrases. Variants
change source syllables, number of syllables, pauses and inflections—not just
pitch. A per-character/per-reaction shuffled bag exhausts all six before reuse
and prevents a repeat across bag boundaries. Its seed changes between races.

Steady driving produces no periodic chatter. Only an initial moving greeting,
a fresh acceleration from low speed, a new contact, or a nearby rank crossing
can schedule a line. Continuous lap progress detects overtakes without a false
crossing at the start/finish seam; a ±0.035-radian margin rejects side-by-side
jitter. The rival addresses the player in both pass directions. No speech is
triggered for exchanges solely between AI opponents.

Overtakes take priority over effort/greetings, with a six-second pair cooldown.
Other speech waits 7–11 seconds per robot. New phrases are spaced 2.6 seconds
apart globally, expire from the queue after two seconds, and require the source
to remain within 12 metres. At most two expressions play. Pause/mute suspend
conversation state; a new race or return to the menu clears it.

## Driving and crowd

Burtt describes recording a hand-cranked generator to follow WALL-E's driving,
with an inertia starter for faster movement in this
[first-person interview](https://designingsound.org/2009/09/ben-burtt-special-wall-e-the-definitive-interview/).
The implementation follows the recorded-mechanism and motion-driven layering
approach; it does not claim to contain those same historical devices.

Each robot has separate preloaded motor and ground-contact layers, edited from
recorded drills/machinery and ratchets/scraping. Pitch follows speed; level
follows motion and turning. Stationary robots do not play a constant engine hum.
Ground-contact sound fades out when airborne, independently of motor sound.

During racing, the listener follows the player, with camera-oriented stereo.
Sources fade to zero at 24 metres (robots) and 28 metres (crowd). Motor/ground
mix changes smooth over 120 ms; voice gain/pan smooth over 60 ms. These are
stereo distance cues, not binaural audio or wall-occlusion simulation.

Seated crowd zones determine population and position. The nearest four zones
play ordinary cheers during racing. Player finish switches them to Gregor
Quendel's “03 - Strong cheering - I”; storm races retain sparse individual
cheers. The overhead outro keeps the crowd audible near any remaining racer,
then fades to silence when all four leave earshot. Once departure is complete,
roaming back near the stadium does not restart the crowd. This is an audio
lifecycle change, not a new animation of spectators leaving their seats.

Intro, countdown, pause, lost focus, menus and mute still silence the engine.
The Simulation menu's “Mute race sound” preference persists. Device startup
failures are reported/retried rather than blocking gameplay.

## Runtime and reproduction

Twelve loop players (four motors, four contact layers, four crowd zones) plus
two one-shot players feed AVAudioEngine. Audio renders on Apple's audio thread.
All assets are decoded during loading: approximately 91.7 MB of stereo float PCM.
There is no per-frame synthesis, decoding, download or growing voice list.

`scripts/audio/sources.json` records the two source downloads and SHA-256 hashes.
Download those files to a source folder and extract `workshop.7z` there, retaining
its `workshop/` subdirectory. Then run:

```sh
python scripts/audio/characters.py SOURCE_FOLDER PREVIEW_FOLDER
```

The offline renderer requires numpy, scipy and ffmpeg. It writes 120 expressive
clips and eight movement layers, plus separate voice and driving previews for
each character. The driving previews contain rest, acceleration, cruising,
braking and rest, at the runtime mix level. They are not loudness-boosted.

`scripts/audio/prepare.py CROWD_SOURCE_FOLDER` separately masters the normal,
sparse and finish crowd recordings. Crowd source and attribution:
[Gregor Quendel, CC BY 4.0](https://opengameart.org/content/free-crowd-cheering-sounds),
[Nocturnal_Vanguard / AuraVoice, CC0](https://opengameart.org/content/cheers-0).

## Validation

`--audio-smoke-test DIR` renders a native AVAudioEngine fixture and checks
non-clipping output, stereo orientation, range, stationary driving silence,
mute/pause/resume, race finish cheering, remaining-racer audibility, departure
silence, restart and menu teardown. A deterministic director fixture simulates
ten minutes of steady motion to reject repeating chatter, fourteen alternating
rank positions to exercise both pass directions, shuffled phrase exhaustion,
side-by-side jitter, distance eligibility, effort and startle.

These tests establish behavior and signal integrity only. The separate audio
previews are the basis for user listening feedback before calling the character
sound design successful.

The 45-second Apple M2 1920×1080 live racing benchmark played nine expressions:
59.975 render callbacks/s, p95 interval 17.441 ms, CPU update p95 1.377 ms,
two intervals over 25 ms and none over 50 ms. These are SceneKit callback
intervals, not GPU/display presentation timestamps. Activity Monitor was paused
and restored; no build, encoding or other simulation ran during measurement.
The final two changes affect only departure silence and outro panning, not the
active-racing benchmark path. Artifacts are in
`../marvin-town-planning/robot-audio-v2/`.

Final native regression passed, including manual escape silence. The 40-second
render's peak was 0.1938; the separately rendered finish cheer peaked at 0.0695.
All 120 expression assets are distinct, silent at their boundaries and free of
clipped samples. The eight driving loop seams remain within normal adjacent
sample changes. These measurements do not establish perceived sound quality.
