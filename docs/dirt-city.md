# Mos Aster: city rebuild

The dirt circuit now sits inside a continuous city. The rectangular perimeter
road and distant mesa ring are removed. Market and dock roads form an irregular,
connected branching network, and the city extends roughly 150 scene units in each
direction so neither the original comparison overview nor the gameplay overview
shows an empty desert outskirts. Scenery ground is worn paving; the racing spline,
track mesh and physics remain unchanged.

## Film references and visual comparisons

The reference images were viewed directly before implementation. They are visual
research only; no film images are shipped as textures or other game assets.

- [Mos Eisley exterior, *A New Hope*, SHOT.CAFE](https://shot.cafe/movie/star-wars-a-new-hope-1977-301/exterior): the viewed frame has densely joined roof volumes, broad domes, circular docking courts, layered civic towers, narrow openings, pedestrians and machinery. Those features informed the compound layout, landing bays and skyline hierarchy.
- [Mos Espa street, *The Phantom Menace*, MovieMaps](https://moviemaps.org/images/fdc): the viewed film frame combines rounded plaster buildings, substantial buttresses, shade cloth, recessed entrances and a passing speeder. This informed softened masonry, arched doorways, stalls and parked utility vehicles.
- [Mos Espa film-still collection](https://mitchdarbyarchitect.com/blog/most-remote-mos-espa): the market frame emphasizes overlapping cloth shelters, customers, repair hardware and varied small wares. The grandstand market and infield repair equipment use the same density principle.
- [Official Mos Espa databank](https://www.starwars.com/databank/mos-espa) provides the broader setting context.

This is an original, stylized city inspired by those references, not a claim of
photorealism or a recreation of a specific set.

Three native render passes were reviewed against the references:

| Pass | Finding | Resulting refinement |
| --- | --- | --- |
| City layout | Continuous city coverage and branching roads worked; surfaces looked noisy and streets lifeless. | Subtle tileable plaster, smooth dome normals, contact shading, market stalls, more street residents and speeders. |
| Lighting and activity | Depth and silhouettes improved; paving had horizontal banding and overview LOD lost useful details. | Hashed stone variation, longer near-LOD distance, roof vents/pipes, detailed civic tower and continuous grandstand tower support. |
| Final | City fills the comparison/game overviews; market and repair views remain readable; road geometry serves destinations. | Release smoke and sustained 1080p benchmark on the final build. |

The comparison overview camera remains `(0,46,-52)` looking at the origin, exactly
matching the preceding revision. A separate capture uses the actual game overview
`(0,38,-33)`. The density improvement does not depend on cropping the old camera.
Street cameras moved to the new streets rather than remaining inside new buildings.

## Rendering and geometry

- 1,327 modules; most are joined compounds with several volumes. Near buildings
  use nine deterministic arrangements, softened edges, doors, terraces, awnings,
  roof equipment and contact shading. Far districts only generate silhouettes.
- 298 residents/spectators. Fifteen animate, with a cap of sixteen and four slots
  reserved for the plaza walkers. Streets also contain static pedestrian groups.
- 130 spatial batches: 16-unit cells nearby and 32-unit cells farther away.
  Static geometry totals 316,842 near triangles and 265,662 far triangles, plus
  the small separate animated crowd and text signs. Geometry is generated once.
- Shared 256×256 plaster and locally generated paving textures. No new network
  dependency, art asset download or per-frame geometry generation.
- Near details switch out at 82 units, preserving useful detail in the overview.
  Outer buildings omit fine detail from both tiers. Shadows are bounded to nearby
  cells; sun map remains 2048². SSAO uses intensity 0.70/radius 1.6 and resets to
  disabled in Sandbox. [Apple SSAO documentation](https://developer.apple.com/documentation/scenekit/scncamera/screenspaceambientocclusionintensity)
- Geometry checks cap near/far triangles at 360,000/290,000 and batches below 150.
  These replace the earlier small-town geometry budget; measured frame pacing,
  rather than the obsolete building count, determines viability.

## Validation

The release app builds and signs successfully. Native town smoke passes:
full lot clearance, zero infield houses, both repair-tent footprints, populated
outer city in all eight sectors, connected streets with no cycle, camera
obstruction, crowd pause, stable scene after reset, and bounded geometry.
The street check requires one connected tree, preventing a ring road from
silently reappearing in later layout changes.

The prior 28 simulation checks and full-race smoke baseline are recorded in
[dirt-town-v1.md](dirt-town-v1.md); no physics source changed in this rebuild.
The pre-existing buried-trail smoke failure is not claimed fixed.

Native screenshots and machine-readable reports for this task are stored locally:

- `../marvin-town-planning/city-pass-1/`
- `../marvin-town-planning/city-pass-2/`
- `../marvin-town-planning/city-final/`
- `../marvin-town-planning/city-final-benchmark/`

Paths above are relative to the repository root. The screenshots include the
unchanged comparison overview, game overview, grandstand, market, repair tents,
outskirts, spaceport, skyline and racing view.

The second-pass 45-second 1920×1080 check averaged 59.99 SceneKit render callbacks
per second, p95 19.51 ms, p99 21.56 ms, with no intervals over 25 ms. These timings
are render-callback wall-clock intervals, not GPU execution or display-presentation
timestamps. The same limitation applies to the final run below.

### Final three-minute result

On the local M2 MacBook Air, the final build completed three laps during a
180.0-second 1920×1080 run. Mean cadence was 59.99 callbacks/second; interval p50
16.64 ms, p95 21.58 ms, p99 22.59 ms. Nine of 10,619 intervals exceeded 25 ms
(0.085%); none exceeded 50 ms. CPU update p95 was 17.82 ms and final thermal state
was fair (1). The run includes chase and repeated aerial views, then existing
post-finish driving. This supports approximately 60 Hz average rendering with
some short pacing variation; it does not establish a strict 16.67 ms frame budget,
GPU timings, display-presentation timing, or a fifteen-minute thermal soak.

Raw result: `../marvin-town-planning/city-final-benchmark/benchmark.json`.

### Track palette refinement

The track now uses a lighter, muted rose-clay albedo, distinct from the pale city
paving. A constant material modifier remaps the scanned color while retaining
its grain, normal/roughness maps and tread detail, shared across the driving lane,
berms and shoulders. Release build and town smoke pass; overview and racing views
were reviewed in `../marvin-town-planning/clay-track/`. This palette-only change
was not separately benchmarked; the three-minute timings above precede it.
