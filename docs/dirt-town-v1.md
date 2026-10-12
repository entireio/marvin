# Desert spaceport v1

Implemented September 29, 2026, in the native macOS simulator.

This records the initial deliveries. The current city rebuild and film-reference
comparison are documented in [dirt-city.md](dirt-city.md).

## Initial v1 delivery

- A deterministic, stylized desert settlement with 76 building modules, domes,
  parapets, awnings, utility details, roads, a hangar, parked shuttle, communication
  towers, and a distant mesa skyline.
- An occupied finish-line grandstand built above a market arcade, with shade
  roofs, access stairs, aisles, bookend towers, and mounted race signage.
- 197 spectators/residents, including human, alien, and droid variations. Sixteen
  nearby figures can animate; four have short plaza movement loops. There is also
  a secondary viewing terrace across the course.
- Spatially batched static geometry: 66 cells, 89,034 near-tier triangles and
  40,890 distant-tier triangles, plus the small animated crowd and signs.
- Shared 128×128 plaster texture, opaque materials, distant LOD, and simple camera
  obstruction bounds. No network assets or additional build dependencies.
- Existing course, 240 Hz race physics, lap rules, and driving assists preserved.

The v1 uses procedural geometry constructed once at scene preparation. The larger
plan's authored asset/export pipeline, crowd navigation, baked visibility sets,
and configurable quality tiers remain future work. The current crowd uses simple
poses and bounded motion rather than full skeletal character animation.

## Rendering work

Initial 45-second A/B runs at 1920×1080 on the M2 MacBook Air averaged 23.28 render
callbacks/sec with the town and 23.43 without it. A process sample found extensive
SceneKit Metal resource allocation and transaction blocking in the existing race.

Two changes recovered headroom:

1. Pool simulation remains, but airborne dirt now renders as two batched meshes
   instead of up to 1,600 independent particle nodes.
2. Dirt coating no longer sets the same shader value on every robot part every
   frame. Logical accumulation remains exact; visual uniforms use 32 levels and
   at most 12 changed surfaces per character per update, with immediate reset.

The race also uses 2× MSAA and a 2048² shadow map, and the hidden menu portrait
stops continuous rendering. Sandbox restores its existing 4× MSAA setting.

A subsequent 45-second town run averaged 59.98 render callbacks/sec, p95 18.45 ms,
p99 19.50 ms, with no intervals over 50 ms. These are SceneKit callback intervals,
not GPU execution or display-presentation timestamps. They demonstrate the local
improvement but do not certify the original plan's strict presentation percentile
or GPU budget targets.

## Verification

- Release app builds and is ad-hoc signed.
- All 28 simulation checks pass.
- Town smoke passes footprint clearance, bounded geometry/population, camera
  obstruction, crowd pause, and stable scene contents after reset.
- Full native smoke: 34 reported checks pass. The existing full-race trail check
  still fails on the exact same buried sample as the pre-change baseline:
  x=4.95181956, z=-15.10225530, depth=0.00161665 scene units. This v1 does not alter
  the track surface or fix that pre-existing discrepancy.
- Native screenshots reviewed: overview, occupied grandstand, street, spaceport,
  and race view. Screenshots and raw benchmark reports are saved in
  `/Users/thomi/Projects/marvin-town-planning/` for this local task.

See the simulator README for `--town-smoke-test`, `--town-benchmark`, and
`--without-town` commands.

## Final three-minute run

The final build completed three laps during a 180.0-second
run at 1920×1080, averaging 60.00 render callbacks/sec.
Callback interval p95 was 18.57 ms and p99 19.66 ms; no intervals exceeded
25 ms. CPU update p95 was 15.78 ms. Thermal state was fair (1) at the end.
These measurements include racing and repeated aerial views, followed by the
existing post-finish driving. They are a three-minute local check, not the
original plan's fifteen-minute soak or an Instruments presentation/GPU capture.

## Visual revision: varied town and infield repairs

The screenshot feedback is implemented with six different building silhouettes:
round adobe dwellings, stepped terrace houses, low vaulted workshops, narrow
watchtowers with service wings, shaded L-shaped courtyard houses, and twin-silo
service blocks. The town expands from 76 to 176 modules, with courtyard walls,
paved lot aprons, cargo stacks and more residents. The distant mesas move outward
to avoid overlapping the expanded neighborhoods.

All residential lots must now lie outside the closed racing spline, in addition
to clearing the track shoulder with their entire footprint. Two open-sided,
pitched-roof tents replace the infield houses. Each contains a mechanic, droid
on a lift with exposed circuitry, spare motors, hand tools on a workbench, stacked
wheels, an engine hoist and toolbox. The tent roofs participate in camera clearance.
The smoke test requires both tent footprints to fit and rejects infield houses.

The geometry remains batched with distant LOD; no new per-frame scenery work or
physics bodies are added. Animation remains capped at 16 people, with four slots
reserved for plaza walkers so a larger residential population cannot exhaust them.
New native screenshots include repair and outskirts close-ups, alongside the
same overview used to compare the original scene. Evidence is in
`/Users/thomi/Projects/marvin-town-planning/revision/`.

Revision validation: release packaging/signing and town smoke pass. The scene has
274 residents/spectators, 16 animated people (four walkers), 96 spatial cells,
106,536 near triangles and 65,224 far triangles. The smoke report confirms zero
infield houses and two valid repair tents, plus camera, pause and reset checks.
The prior simulation and full-race results above remain the baseline; this
revision changes scenery generation and screenshot coverage only.

The revised scene completed a fresh 180-second, three-lap 1920×1080 run:
60.00 render callbacks/sec on average, p95 18.78 ms, p99 19.95 ms, and zero
intervals above 25 ms. CPU update p95 was 12.93 ms; final thermal state was fair.
As above, these are SceneKit render callback intervals, not GPU or display
presentation timings. Raw report: `../marvin-town-planning/revision-benchmark/benchmark.json`
(relative to the repository root).
