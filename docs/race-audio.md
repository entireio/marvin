# Race audio plan and implementation

## Sound palette

The four chassis share restrained mechanical sound design without borrowed film
voices. Marvin has a warm 92 Hz motor and light tread modulation; R2-D2 has a
higher, smoother 165 Hz wheel motor; BB-8 combines a 58 Hz rolling texture with
a soft gyro tone; WALL-E uses a lower 64 Hz motor and heavier tread pulses.
Playback pitch and level rise with speed and turning; airborne machines have
less ground/mechanical presence. The selected player character determines the
player's voice, rather than assuming Marvin is always selected.

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
wall-occlusion simulation. Gain budgets reserve headroom even with eight nearby
sources. No music, dialogue, horns or impact effects are added in this pass.

Sounds run only after the countdown while the player's race is active. Intro,
countdown, pause, loss of app focus, restart, menus and player finish stop the
voices. Leaving town after the race is silent. The Simulation menu's “Mute race
sound” preference persists between launches. Output-device start failures are
reported and retried at most once every two seconds; they do not stop gameplay.

## Performance and assets

Eight fixed AVAudioPlayerNode/AVAudioUnitVarispeed chains feed AVAudioEngine's
stereo mixer. Audio renders on Apple's audio thread; gameplay only updates
small gain/pan/rate parameters. All six loops are decoded when the dirt level
loads. No per-frame synthesis, downloads, decoding or growing sound-node list.
The original motor synthesis and crowd processing recipe lives in
`scripts/audio/prepare.py`. This offline script requires the source downloads
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

Final native run (`../marvin-town-planning/race-audio-final`) passes, including
mute/unmute and completion of three laps through the race lifecycle gate.
Rendered peak is 0.0503 and RMS 0.00501 across the scripted 40-second fixture;
no non-finite samples or clipping. The fixture demonstrates moderate throttle
ramps, not the worst-case eight-source loudness. The mix's conservative maximum
gain budget also leaves headroom at full motion. Preview MP3 preserves the
rendered level; it has not been loudness-normalized to exaggerate the result.

The live 45-second 1920×1080 M2 Metal benchmark (`race-audio-performance`)
confirms `audioActive: true`: 59.975 render callbacks/s, p95 interval 17.415 ms,
CPU update p95 1.371 ms, two intervals above 25 ms, none above 50 ms. Activity
Monitor was paused/restored and no builds, audio exports or other simulations
ran concurrently. These callback measurements are not GPU/display timestamps.
The app builds and packages all sound files and About credits successfully.
