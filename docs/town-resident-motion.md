# Town resident motion

The street placement code previously discarded selected walking positions and
passed only their count to the doorway visitor system. Its ten walkers spent
much of their time indoors or waiting for whole-route reservations. The rest of
the streets consisted of stationary batches, often sharing a heading.

The town now has a separate, capped population of 24 street pedestrians alongside
the ten doorway visitors. They start on distributed street paths, walk in both
directions with independent speeds, pause at the ends of local errands, and turn
back when blocked. Paths are clipped against scene collision geometry; runtime
steps are swept against scenery, doors, robots, and other residents. Robots also
receive the new pedestrians as dynamic collision bodies. These local errands
complement existing house visits; they do not provide arbitrary town-wide goals.

Off-screen walkers navigate at 10 Hz with swept collision checks. Visible walkers
and those near another dynamic body update responsively. Off-screen gait work and
uniform writes sleep, while returning to the camera immediately restores the
current distance-driven gait. Standing figures remain batched on the GPU, with
independent head scans, upper-body turns, and gestures. Conversation pairs face
one another, street onlookers face both ways, and spectator orientations retain
their view of the race. Storms hide street pedestrians and remove their collision
bodies; pause and restart preserve their respective semantics.

## Validation

Build the native app with `apps/simulator-macos/build-app.sh`. Run
`MarvinSimulator --people-smoke-test OUTPUT`, optionally with
`MARVIN_PEOPLE_OFFSCREEN=1` or `MARVIN_STREET_MOVIE=1`.

The final visible and off-screen tests each simulate 600 seconds. All 24 street
walkers continue moving; the visible run records 331–529 metres per walker, a
longest continuous stop of 4.18 seconds, and zero detected penetration. Existing
house visitors complete 37 entries. Off-screen pose update counts are zero.
The same test checks camera wake-up, pause, storm sheltering, restart, and a
30-second parked-robot obstruction: the affected walker continues for 17.2 metres
without penetration. Fixed street cameras capture pairs of frames in three
areas; the optional eight-second clip shows walking and idle gestures together.

Artifacts for this change are in the local `marvin-town-planning` directories
`street-life-final`, `street-life-offscreen`, and `street-life-performance`.

The 45-second outer-town camera survey, with exploration details and audio active,
measured 59.977 SceneKit render callbacks/second at 1920×1080 on Apple M2:
17.26 ms p95 interval, one interval over 25 ms, none over 50 ms, and 3.69 ms p95
CPU update. These are callback intervals, not GPU/display presentation timing.
The matching race/chase/overhead run measured 59.973 callbacks/second, 17.42 ms
p95 interval, one interval over 25 ms, none over 50 ms, and 2.11 ms p95 CPU update.

## Street-facing entrances

Entrance placement now runs after all compound footprints are known. A temporary
pedestrian-clearance grid is flooded from the authored streets, excluding the
course and infield. Candidate positions on the actual exposed walls are scored
by public walking distance, with deterministic offsets to avoid repeated door
positions. Walls facing disconnected or obstructed gaps do not get a decorative
entrance. Building positions and road geometry are unchanged. The grid is released
before gameplay.

The five related doorway families use rounded arches, clipped lintels, pointed
arches, broad workshop arches, and rectangular service frames. Width, height,
panel divisions, trim, access controls, and shade vary. Panels fill the opening
profile. Working resident doors retain a clear 0.92 m by 1.30 m opening; their
vestibules, moving leaves, and navigation endpoints use the selected wall and
offset. Store signs and awnings follow the entrance, and windows avoid it.

Visual references inspected:
- [The Phantom Menace street stills and Mos Espa set photographs](https://mitchdarbyarchitect.com/blog/most-remote-mos-espa)
- [A New Hope: Ajim Street / Mos Eisley checkpoint still](https://moviemaps.org/images/g5b)

The references show a shared plaster-and-metal vocabulary with varied entrance
silhouettes and setbacks, rather than the former identical arch duplicated on
both faces of every compound. The game uses original procedural variants.

`--entrance-smoke-test OUTPUT` captures all five designs and audits permanent
approach clearance separately from pedestrians temporarily crossing a threshold.
The updated house-visit regression also caught a door-holding bug: street walkers
had been passed as robots, triggering robot yielding and doorway requests.
Pedestrians now remain collision obstacles without those robot behaviours. In
the updated 600-second visible test all ten house visitors completed visits,
with 50 entries and zero measured penetration; all 24 street walkers continued
moving. Artifact directories use the `entrances-` prefix in `marvin-town-planning`.

Final entrance validation covers all 363 generated entries, with no permanent
approach obstruction, all four wall directions, and family counts 68/69/85/59/82.
Landmarks are built before entrance planning, so the map includes actual towers,
dock walls, and other ground-level obstacles. Both visible and off-screen
600-second resident regressions pass with 50 house entries and zero measured
penetration. The 45-second 1920×1080 outer-town benchmark on Apple M2 measures
60.002 SceneKit render callbacks/second, 17.30 ms p95 interval, no intervals over
25 ms, and 3.50 ms p95 CPU update. These are renderer callback measurements, not
GPU/display presentation timestamps.

## Purposeful standing residents

Street idlers are now authored as complete conversation pairs/trios, vendor/customer
pairs across a real counter, or solitary residents facing a real doorway from beside
its approach. Walking errands, repair mechanics and race-facing spectators retain
their existing roles. No stationary companion is placed beside a departing walker.

Candidates are admitted after scenery construction and before navigation construction.
Each entire group must clear scenery, the circuit and door approaches; accepted groups
also maintain separation from earlier groups. All activity members shelter during a
storm, and their collision bodies disappear with them. Outfit variation stays independent
of this weather policy. A per-vertex activity value keeps the existing GPU batches:
conversation/trading gestures are smaller, while door waiters do not wave and keep their
head movement directed toward the door.

The entrance smoke test now checks group completeness, permanent scenery clearance,
storm collision removal, role coverage and all door approaches, and saves street-level
activity screenshots. The ten-minute people test also caught a short retreat-target
oscillation when a street walker encountered a house visitor; blocked walkers now
choose a retreat point roughly one metre behind their actual path position.
A second deadlock occurred when that retreat reached a route endpoint before leaving
room for the house visitor. House visitors now use their existing swept sidestep after
a brief pedestrian stand-off as well; pedestrians still do not request doors or trigger
robot stopping behavior. Street collision bodies carry actual heading for this yielding.

Validation on the final build: 46 complete conversation groups (96 people), four
vendor/customer pairs, seven door waiters, zero scenery conflicts and zero blocked
approaches across 363 entrances. Both visible and offscreen 600-second runs passed:
all 24 street walkers remained active, all ten house visitors entered houses (48 entries
in each run), zero detected penetration; longest street stops were 9.50/9.38 seconds.
Offscreen pose updates were zero. The 45-second 1920×1080 Apple M2 outer-town benchmark
averaged 59.98 render callbacks/s, p95 17.32 ms, one interval over 25 ms and none over
50 ms; CPU update p95 3.42 ms. These are SceneKit callback intervals, not GPU/display
presentation timings. Evidence is in `marvin-town-planning/purposeful-residents-final`,
`purposeful-people-complete`, `purposeful-offscreen-final`, and `purposeful-performance`
beside this repository.
