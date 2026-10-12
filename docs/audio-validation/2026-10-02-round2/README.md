# Second sound, passage-camera and gate correction

The user rejected the previous WALL-E/Marvin driving textures and boost. The
previous Astra Ultra review was a technical review, not a listening comparison
with movie playback; its approval did not establish perceived quality.

## Sound design changes

The prior high motors and boosts contained a drill recording; Marvin and WALL-E
contact contained a ratchet recording. These were inappropriate sources for the
requested mechanical character. All four drivetrain banks are replaced with
licensed electric-machine and electric-scooter recordings, quieter rolling
contact, and broadband pressure boost with softened onset/release. Separate low
and high banks still follow speed/load; the new loops stabilize recording gain
so their source dynamics do not contradict the player's steady throttle.

Reference: [Disney's account of Ben Burtt's WALL-E design](https://news.disney.com/fun-facts-wall-e-sound)
describes a hand-cranked field-radio generator for slow movement and an antique
biplane inertia starter at speed. This guided the mechanical direction; the new
recordings are original-source adaptations, not those film effects. No claim of
having heard or matched film playback is made.

Source licenses, URLs and hashes are recorded in `scripts/audio/sources.json`
and bundled Audio/CREDITS.md. `soundscape.py SOURCE --mechanical-only` reproduces
the replacement assets without remastering voices. City/storm layers remain.

## Reproduced visual failures

The ramp multiplied the whole raised bank by a side taper to town-floor height.
Across gate-local along 3.98/4.02, out −1, one flank changed approximately 0.676 m and
the other 0.197 m over 4 cm. Its lateral blend now returns to the ordinary shoulder.
The outside fill uses the track's offset-curve mesh instead of overlapping its
lane with a rectangular mesh.

The camera used a lookahead point that could enter a facade, broad compound
bounds covering empty passages, and an 8% minimum boom that could exceed available
room. New queries use individual rotated solids including moving doors, a pivot
above the selected chassis, and nearby-wall clearance to anticipate compression.

`--passage-smoke-test` drives three narrow generated access routes out and back
with actual physics/control input, in follow and orbit modes at 30/60/120 Hz, and
captures both gate flanks. Initial replacement failures were retained locally:
penetration of door panels, abrupt corner retraction, and self-occluding views.
Validation results below must refer to the final implementation, not those runs.

## Service-area correction

The service entrance uses the same supported terrain treatment as the city gate.
The retained brick edge and gradual entry are joined by broader, irregular sand
shoulders, with collision height shared by the visible embankment. A flat clay
spill decal previously cut through the raised sand, producing a hard beige island
and a serrated boundary. The baked world-space spill mask now belongs to the
terrain material itself, so its color follows the slope and continues into the
infield without intersecting a separate sheet.

The follow-up aerial review found a separate real geometry defect: the global
nearest-course projection switched branches within the service shoulder. Height
jumped 57.6 mm over a 5 mm move. Restricting the nearest-curve query still left
another branch cut. The final height field uses monotone entrance cross-sections
extended into the yard; its texture coordinates follow those same columns. A
dense 5 mm regression scan now checks the whole formerly affected shoulder.
Both ramp clay layers inherit track coordinates at their mouths and matching
surface normals. Their pigment covers the top surface only; the closed earth
mesh supplies the supported sides and underside.

## Technical audio results

`audio.json`: 58/58 native audio assertions pass. Slow-to-full-speed driving rises
13.99–16.02 dB across the four droids. Boost at a fixed 6 m/s adds 5.06–6.24 dB;
release returns within 0.18 dB of the preceding steady drive. Peak output is 0.82.
`assets.json` verifies the bundled WAVs; decoded stereo storage is 205.36 MB.
These are signal/playback checks, not listening or film resemblance approval.

The local audition page contains old/revised 20-second comparisons for all four
droids, city ambience and storm audio. Boost begins at 10 seconds. No user
listening verdict has been received for this revision.

## Terrain and camera validation

The final release build and full core checks pass. The core checks include all
four chassis crossing the service entrance both ways, access to both tents,
solid adjacent fences, and a dense scan of the service shoulder's former cut.
The native gate route covers 103.8 m through town and back with zero solid
penetration; all 13 assertions pass (`city-escape.json`). An earlier baseline
run failed its rival-containment check; that failure was retained locally.

`passages.json`: all 18 actual-input narrow-route cases pass (three routes,
follow/orbit, 30/60/120 Hz), with zero camera wall or robot-body penetrations.
Peak camera travel is 4.603 m/s. The low-doorway pivot changes at most 4.54 cm
for a 2.5 cm player move; the dune-crest regression changes 1.00 cm for a 1 cm
move. The camera run used the final camera code before subsequent ramp-only
material/height corrections. `ramp-camera-preflight.json` was rerun on the final
terrain build.

The author and independent Astra Ultra judge viewed both final aerials at
matching 18 m camera height, gate flanks, and service track/infield/shifted views.
The judge accepts the targeted terrain and camera repairs: supported continuous
shoulders, no visible holes, service teeth, detached patch, or black mouth line.
The subsequent material correction removes the artificial smooth pale strip at
both mouths. Each narrow shoulder previously restarted V=0…1, compressing the
texture into 15 cm instead of the lane’s 3.9 m. Lane, berms, shoulders and both
ramps now share continuous offset-based texture coordinates at the lane scale.
The native release build and ramp/camera preflight pass after this UV-only change;
geometry and collision support are unchanged. Aerial and low-angle views were
recaptured and inspected. The independent Astra Ultra judge accepted both joins
after reviewing nine native captures, with no new visible seam, hole or detached
surface. This is a targeted seam fix, not a claim of AAA art parity.

![City exit aerial](city-exit-aerial.png)
![Service exit aerial](service-exit-aerial.png)

Full native captures and retained failures are under
`/Users/thomi/Projects/marvin-town-planning/round2/`; final ramp captures are in
`ramps-continuous-uv` (previous comparison in `ramps-final`), camera routes in `verified-v4`, and gate driving in `escape-final`.

## Town frame-rate measurement

Final build on MacBook Air M2, 24 GB, Metal, 1920×1080. Each town drive ran for
120 seconds with a 3-second warm-up excluded from render statistics, sound on,
and no concurrent simulator, build, or heavy probe. The benchmark now uses the
ordinary interpolated camera update instead of snapping it every frame. It drives
actual control input through town and back; measured travel was 260 m in clear
weather and 292 m in the storm run.

| Condition | Average FPS | P99 frame time | Worst frame | Frames over 25 ms |
| --- | ---: | ---: | ---: | ---: |
| Clear, daylight 0.5 | 60.000 | 17.52 ms | 20.39 ms | 0 |
| Clear, daylight 0.357 | 59.966 | 17.54 ms | 34.54 ms | 4 |
| Storm, daylight 0.5 | 60.000 | 17.40 ms | 19.47 ms | 0 |

These are SceneKit render-callback intervals, not GPU execution or measured
screen presentation. They show near-60 pacing on these routes, not universally
constant 60 FPS. In the four slower frames, the nearest measured application
update took 1.44–2.42 ms; these samples do not establish the rendering/scheduling
cause. No M4 Air measurement was available, so these results do not dismiss the
user's reported M4 stutter. An earlier 20-second probe on an intermediate build
averaged 50.35 FPS; it remains under `performance-probe` and is not presented as
an equivalent controlled before/after comparison.

Individual reports and `performance-summary.json` are included here. Raw frame
and update timelines remain beside each local benchmark report in `round2`.
Audio perception is not independently approved.

### UV correction performance check

A further 45-second clear-town drive after the UV correction used the same M2,
Metal 1920×1080, daylight 0.5 and active audio. The first 3 seconds were excluded.
It measured 59.953 FPS, P99 18.00 ms, two intervals above 25 ms and none above
50 ms (`performance-continuous-uv.json`). These remain SceneKit callback timings,
not display presentation measurements or evidence of constant 60 FPS on an M4.
