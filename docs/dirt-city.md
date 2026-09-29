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

## Initial city rebuild rendering and geometry

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

## Initial city rebuild validation

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


## Realism and signage upgrade

The subsequent art pass keeps the layout, track color and racing physics. It
replaces the primitive crowd with clothed derivatives of Blender Studio's CC0
Human Base Meshes. The twelve variants cover male/female base meshes, standing
and seated poses, and three garment/hood variations, with varied runtime colors.
Faces, fingers and anatomy come from authored geometry. Tunics, scarves, hoods,
belts and closed boots are constructed offline; the seated pose preserves thigh
volume. The old sliding plaza motion is removed; fifteen figures have subtle,
distance-limited idle movement. These are game-scale static crowd assets, not
facial-animation or motion-capture characters.

Architecture gains rounded corner normals and profiles, denser near dome rings,
layered doorway arches and metal leaves, curved cloth canopies, scanned plaster,
cloth and metal normals/roughness, and HDR environment fill. Materials explicitly
convert the sRGB palette to linear light and carry vertex tint through the Metal shader so the scanned albedo does not erase
outfit or district colors. World-projected UVs preserve wall texture scale.

Signage uses a coherent enamel-plate design with numbered/color-coded badges,
main titles, district/authority labels, and secondary directions. Race branding,
gates 1–2, sectors A–D, repair tents, dock/bay numbers, market stalls, nearby shops,
and street directions all have physical frames. Shop mounts follow façade yaw;
market and repair signs have suspension hardware. Direction arrows follow the
street approaches. Text is fitted to both width and height using actual font
metrics, then rasterized once and mipmapped. Signs are never camera billboards.

Runtime art is bundled with SHA-256 verification. Normal builds remain offline;
Blender 4.5 is needed only to re-export crowd art. The source .blend stays outside
the app bundle. See [asset provenance](../apps/simulator-macos/Resources/City/ATTRIBUTION.md)
and [reproduction instructions](../scripts/city/README.md).

### Detail and performance limits

- Architecture remains below the existing 360,000 near / 290,000 far triangle
  budgets, in 130 cells. No scenery rigid bodies were added.
- Crowd geometry has independent 8-meter spatial cells (77 occupied cells) and
  approximately 6,500 / 1,600 / 350 triangles per person. Static crowd detail
  changes at 10 / 26 units; individually animated people at 8 / 24 units.
  The ~1.94 million high-detail crowd triangles are the stored total across the
  whole city, not the expected visible total in an overview.
- Crowd skin, fabric and leather share three surfaces. Vertex tint provides
  clothing/skin variety without a separate material per resident. Export buffers
  and JSON models are released after SceneKit geometry preparation.
- Fine detail and cloth lighting are shared, textures are mipmapped, and the
  crowd has no individual real-time shadow passes. Near architecture still casts
  bounded sun shadows; SSAO supplies small-scale contact shading.

### Visual QA

Native review covers the unchanged overview, racing camera, market, grandstand,
repair area, outskirts, spaceport, skyline, plus new citizen and spectator
close-ups. Iteration corrected washed-out vertex tint, excessive cloth folds,
uninitialized source-eye transforms, arm deformation leaking into garment
vertices, collapsed seated thighs, and sign text exceeding its allotted height.
The final images are actual SceneKit captures, not generated concept images.


Final release smoke passed all layout, coverage, street connectivity, camera,
crowd pause, reset and art-budget checks. All 25 sign designs passed the measured
text-fit check. Architecture totals 353,876 near / 254,164 far triangles; crowd
storage totals 1,936,791 near / 104,245 far triangles. Captures and raw smoke
results are in `../marvin-town-planning/realism-delivery/`.

The first 45-second 1080p art benchmark averaged 58.89 render callbacks/sec,
p95 22.59 ms, p99 24.34 ms, with 11 intervals over 25 ms and none over 50 ms.
Crowd batching was then increased from 6- to 8-meter cells and the high-detail
range tightened, keeping full detail for close-up inspection. The sustained
result below uses those final settings and the final color-space correction.


### Final sustained art benchmark

On the same local M2 MacBook Air, the final 180.0-second 1920×1080 run completed
three laps and averaged **58.04 SceneKit render callbacks/sec**. Interval p50 was
17.16 ms, p95 23.04 ms, p99 25.06 ms. Of 10,273 intervals, 112 exceeded 25 ms
(1.09%) and two exceeded 50 ms (0.019%). CPU update p95 was 18.68 ms; final thermal
state was fair (1). This is approximately a 2 FPS average cost versus the previous
city build on this machine, not a locked 60 FPS result. Measurements are callback
wall-clock cadence, not GPU execution or display-presentation timestamps.

Raw final report: `../marvin-town-planning/realism-benchmark-final/benchmark.json`.
No builds, Blender exports, or other rendering jobs ran concurrently with it.


## Performance recovery after the art pass

A five-second macOS `sample` trace of the unchanged art build identified the
exhaustive course projection in debris ground contact as a major main-thread
cost (711 sampled stacks under that call, versus 1,610 under the benchmark tick).
The profiling run is diagnostic, not the final timing result, because sampling
adds overhead. Its files are `/tmp/marvin-perf-sample.txt` and
`../marvin-town-planning/perf-profile/benchmark.json`.

`DirtCourse.projection` now searches a static balanced bounding-box tree over the
same 768 line segments, visiting nearby bounds first and rejecting arcs that
cannot beat the closest candidate. Leaf calculations retain the original segment
projection, and equal-distance ties still select the lowest segment index.
Bounds include floating-point slack. There is no terrain approximation or new
scenery quality reduction. The tree also benefits physics and track callers.

An independent exhaustive reference agrees at 24,499 positions: every sampled
vertex, five offsets at every segment midpoint, a dense grid spanning the course,
and distant city locations. Four overflow-sized inputs additionally check the
original fallback. All 29 simulation checks pass, including coupled races and
30/60/120 Hz behavior. The native town smoke also passes. The resident count,
architecture/crowd triangle counts, 25 signs, LOD settings and textures match the
art build before this optimization.

The first 180-second run after the projection change reduced CPU update p95
from 18.68 to 5.16 ms and eliminated intervals over 50 ms (2 → 0), but callback
cadence remained 57.56/sec. This was not a recovered 60 FPS result. A 45-second
city-hidden diagnostic reached 59.98/sec. Architecture batches now also omit
empty material elements, retaining the matching material slots and all visible
triangles. A short full-city run with that change measured 58.10/sec; its shorter
duration prevents a direct comparison with the sustained runs.

During isolation, an Android emulator was observed consuming approximately 233%
CPU. The user requested its termination, and `adb emu kill` shut it down; the
device list and process list confirmed it had exited. A crowd-hidden diagnostic
spanned that shutdown and is therefore excluded from performance conclusions.
Earlier runs were not controlled for this external load. The following full-city
run retains all people, materials, shadow settings, LOD ranges and resolution.

### Sustained run with the emulator stopped

The 180.01-second, 1920×1080 full-city run averaged **59.997 render callbacks/sec**
(10,619 measured intervals after warmup). Frame intervals: p50 16.66 ms,
p95 17.09 ms, p99 18.08 ms; one interval exceeded 25 ms and none exceeded 50 ms.
CPU update p95 was 2.52 ms, and thermal state remained fair (1). The race completed
three laps; the benchmark continued rendering and alternating chase/aerial views
after race completion. All 1,327 buildings, 298 residents and 25 signs remained
enabled, with the same geometry counts, LOD ranges and materials as the art pass.

Raw report: `../marvin-town-planning/perf-emulator-stopped/benchmark.json`.
This establishes recovered approximately 60 Hz callback cadence on this machine
under the cleaner load, not a guarantee of presentation timing or performance on
other hardware. Do not attribute the whole improvement to code: emulator removal
was a material change in test conditions. The earlier same-load projection test
independently demonstrated the CPU reduction from 18.68 to 5.16 ms.
