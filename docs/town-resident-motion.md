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
