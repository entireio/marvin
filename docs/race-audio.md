# Race audio plan and implementation

## Sound palette

Each robot has a mechanical movement loop and short expressive reactions. These
are original synthesized recreations informed by the films, not official film
recordings or verified exact voice matches:

- R2-D2: fluid whistles, quick electronic syllables and rising/falling questions.
- BB-8: rounded, bubbling vowel shapes over a low rolling/gyro texture.
- WALL-E: rougher, lower vowel phrases over tread chatter and servos.
- Marvin: warm, measured three-note phrases with a glassy transient, combining
  expressive pitch, rounded vowels and mechanical texture into an original voice.

The sound-design references emphasize expression, not just oscillator pitch:
[Ben Burtt on R2-D2](https://www.starwars.com/news/5-iconic-star-wars-sound-effects-and-how-they-were-made-starwars-com),
[Disney on BB-8's performed vocal starting point](https://d23.com/star-wars-sounds/),
and [Burtt's WALL-E sound masterclass](https://editorial.rottentomatoes.com/article/exclusive-ben-burtts-walle-sound-masterclass/).
Our formant synthesis approximates vocal shapes; it does not include an actor's
performance. Listening comparison remains necessary to judge character fidelity.

Acknowledgement, acceleration/effort and new-contact/startle each have three
variants. A robot speaks at most every 9–14 seconds, with two seconds between
new phrases globally. Only robots within 16 metres can initiate a phrase;
playback continues to use distance attenuation as they move. Contacts held over
multiple frames do not repeatedly trigger reactions. Mechanical playback pitch
and level rise with speed and turning, independently of the voice phrase.
Airborne machines have less ground/mechanical presence. The selected character
determines the player's sound, rather than assuming Marvin is always selected.

Seated spectator positions define small sound zones. The nearest four zones
play recorded sports crowd chatter/cheering, swelling when racers pass. Storm
zones use their actual remaining spectator counts and sparse individual cheers,
not a stadium recording merely played more quietly. Each zone uses a slightly
different playback rate to avoid perfectly synchronized repetition.

## Mix and lifecycle

The listener is at the player's position, with stereo orientation from the
camera. This preserves nearby sound in an overhead view. Robot sources fade to
zero at 24 metres and spectator zones at 28 metres; distance gain and stereo pan
are smoothed over 120 ms. This is distance-based stereo, not binaural HRTF or
wall-occlusion simulation. Gain budgets reserve headroom with the eight loop sources and two expressive players. No music is added.

Sounds run only after the countdown while the player's race is active. Intro,
countdown, pause, loss of app focus, restart, menus and player finish stop the
voices. Leaving town after the race is silent. The Simulation menu's “Mute race
sound” preference persists between launches. Output-device start failures are
reported and retried at most once every two seconds; they do not stop gameplay.

## Performance and assets

Eight fixed AVAudioPlayerNode/AVAudioUnitVarispeed loop chains plus two fixed
one-shot players feed AVAudioEngine's stereo mixer. Audio renders on Apple's audio thread; gameplay only updates
small gain/pan/rate parameters. All six loops and 36 expression clips are decoded when the dirt level loads. No per-frame synthesis, downloads, decoding or growing sound-node list.
The original motor synthesis and crowd processing recipe lives in
`scripts/audio/prepare.py`; the original character generator is
`scripts/audio/characters.py`. This offline script requires the source downloads
and ffmpeg; it is not part of normal builds. Bundled WAVs work offline.

Apple reference: https://developer.apple.com/documentation/avfaudio/avaudioplayernode

Crowd recording: “Free Crowd Cheering Sounds” by Gregor Quendel, CC BY 4.0,
https://opengameart.org/content/free-crowd-cheering-sounds . Sparse cheers:
“Cheers” by Nocturnal_Vanguard / AuraVoice, CC0,
https://opengameart.org/content/cheers-0 . Excerpts are filtered, normalized,
converted to mono and crossfaded/spaced for looping. Exact attribution and
license links ship in `Resources/Audio/CREDITS.md` and the app's About credits.

## Validation

`--audio-smoke-test DIR` uses AVAudioEngine offline rendering to write a
40-second preview: four five-second motor ramps, ten seconds of normal crowd,
then ten seconds of sparse storm crowd. It checks finite output, headroom,
left/right and rotated-listener pan, out-of-range silence, initial countdown,
race start, suspension, restart and menu teardown. Live racing benchmarks
include the audio update and actual audio engine playback.

The native regression also checks expression cooldowns, distance eligibility,
variant rotation, effort/startle selection, the two-expression voice cap and
teardown. The isolated comparison preview plays R2-D2, BB-8, WALL-E, then Marvin;
each has acknowledgement, effort and startled phrases. Automated checks verify
playback and numerical signal integrity, not subjective resemblance to the films.

Validation for this revision: the 40-second native render passed with ten
expressions, peak 0.2026 and RMS 0.0321; all 36 clips have silent boundaries and
no clipped samples. The 45-second 1920×1080 Apple M2 live race played 16
expressions with audio active: 59.975 render callbacks/s, p95 interval 17.434 ms,
CPU update p95 1.352 ms, one interval over 25 ms and none over 50 ms. Activity
Monitor was paused/restored; no build or media encoding ran concurrently.
These are SceneKit callback measurements, not GPU/display presentation timing.
Artifacts: `../marvin-town-planning/character-audio` and
`../marvin-town-planning/character-audio-performance`.
