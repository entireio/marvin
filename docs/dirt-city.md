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

## Grandstand, bazaar and service-pit reference pass

The first focused venue pass replaces the grandstand's solid plinth with an
open masonry arcade, recessed shelving and supported shop awnings. The shade
roof now has pitched canvas, a hem following its ridge, scalloped valances and
separate supports. Tower windows sit in projecting housings instead of cutting
through curved walls. Local plaster repairs, utility risers, conduit straps,
vented service boxes and fascia fasteners give the venue construction detail.

Market stock includes hollow lathed pottery, slatted cases, joined countertops
and suspended goods. The repair bay has a tapered service droid, collar and
jointed limbs, tool rack, drawers, removable floor plates, finned motor and hose.
Its sign sits ahead of the cloth trim with suspension rods reaching the roof.
The race title is smaller, exposing the arcade; text-fit checks still cover all
25 signs. The distant city layout and track palette are unchanged.

Crowd variation is deterministic and baked into the existing three material
batches: independent clothing/skin palettes, body proportions, neck turns,
gentle lean and occasional shoulder mantles. Concourse groups replace orderly
queues; seating has small placement/heading differences and vacancies. There
are 294 residents, 15 independently animated, and 77 crowd cells. This improves
variation but does not replace the underlying face meshes or add facial motion.

Cloth is one double-sided surface instead of overlapping front/back meshes.
Near architecture totals 356,960 triangles, far architecture 256,098; the original
360,000/290,000 limits remain. Crowd totals are 1,912,586 near and 104,637 far.
No new runtime lights, shadow maps, physics bodies or per-prop scene nodes were
added. The normal game still uses SceneKit; the renderer study remains separate.

The release build and native city smoke pass layout clearance, city coverage,
street connectivity, camera avoidance, pause, reset, signs and geometry budgets.
The eleven actual app captures are in `../marvin-town-planning/hero-final/`.
Close-ups still show the limitations of the existing faces, broad wall shapes
and procedural props; this is a focused construction/variation pass, not a
photorealistic asset replacement.

The 180-second 1920×1080 run on the M2 MacBook Air averaged **59.89 render
callbacks/sec** across 10,600 intervals. Callback p95/p99 were 17.35/17.82 ms;
21 intervals (0.20%) exceeded 25 ms, none exceeded 50 ms. CPU update p95 was
2.81 ms and final thermal state was nominal (0). This is near 60, not a claim
of zero missed frames. The earlier clean baseline was 60.00 callbacks/sec with
one interval above 25 ms. These are single-run observations, not a controlled
statistical attribution of every timing difference to the art changes. Results:
`../marvin-town-planning/hero-benchmark/benchmark.json`. A final sign suspension
endpoint correction changed positions only after this run; mesh counts, materials
and runtime behavior were unchanged.

The final build's separate 45-second Apple Metal HUD check reported GPU p50/p95/
p99 of 13.51/15.76/16.39 ms, versus the previous full-city baseline's
13.72/15.43/16.02 ms. Logged presentation intervals were 16.67 ms at p50/p95/p99,
with a maximum of 33.33 ms. Only the final 34.8 seconds were summarized. HUD
samples may repeat and include HUD overhead; these are not unique-frame counts
or a GPU encoder trace. The p95 GPU budget leaves less than 1 ms of headroom at
60 Hz, so broad propagation of extra detail should wait for further optimization.
Raw summary: `../marvin-town-planning/hero-gpu/metal-hud.json`.

## Infield service access and carried clay

A 2.5-meter opening in the inner fence aligns with the first repair tent at
x = -7.5. Rail segments and posts are omitted there; striped bollards and a
small SERVICE ACCESS plate identify it without spanning the opening. The outer
fence remains closed. Shared SimulationCore access bounds include chassis
clearance. Both movement and coupled robot collision constraints admit traffic
in both directions. Once inside, robots can move around the infield; approaching
the intact inner fence from the field keeps them on that side instead of snapping
them back onto the racing surface. Lap progress retains its existing on-track
eligibility check.

One 768-square RGBA dirt map is baked at scene initialization using exact course
projection. It supplies an irregular, feathered red-clay spill along inner edges,
a wider dusty fan at the service entrance, and paired worn wheel paths toward
the repair area. Pale paving remains visible through thinner deposits. The map
is a single two-triangle ground layer with depth testing, no depth writes and no
shadow casting; it adds no per-frame projection work or particle emitters.

All 30 simulation checks pass, including every playable chassis traversing the
opening in both directions, free movement at the repair apron, and intact fences
blocking from both sides. The release city smoke passes with 26 fitted signs and
357,112 near architecture triangles. Actual screenshots, including the added
service-access review camera, are in `../marvin-town-planning/service-access-final/`.

A separate 45-second 1920×1080 run with Metal HUD enabled averaged 59.90 render
callbacks/sec, CPU update p95 2.48 ms, with four callback intervals above 25 ms
and none above 50 ms. GPU p50/p95/p99 were 13.55/15.64/16.47 ms, close to the
preceding venue pass (13.51/15.76/16.39 ms). Logged presentation p50/p95/p99 were
16.67 ms; maximum 33.33 ms. This short check shows no material regression, not
a guarantee of zero missed frames. Raw results are in
`../marvin-town-planning/service-access-performance/`; the same HUD sampling
limitations described above apply.

## Organic wear and textile service floors

The tiled base texture is replaced by packed earth with grain and irregular
multi-scale variation. The initial removal of both tent platforms was revised
at the user's request: each tent now has a thin woven rug, with muted checks,
border motifs and uneven fringe. The surroundings remain earth and carried clay;
there is no rigid grid of raised floor plates.

Research informed the revised weathering approach:

- [Adobe's dirt generator guidance](https://experienceleague.adobe.com/en/docs/substance-3d-painter/using/effects/generators/dirt)
  recommends controlling dirt through masks that reflect an asset's environment
  and history.
- [Adobe's parametric decal breakdown](https://www.adobe.com/products/substance3d/magazine/effortless-grunge-look-with-new-substance-3d-assets-parametric-decals.html)
  distinguishes the reusable base material from localized patches, cracks and
  leaks, with independent variation to avoid repeating marks.
- [NPS adobe preservation guidance](https://www.nps.gov/orgs/1739/upload/preservation-brief-05-adobe.pdf)
  documents causes and locations of deterioration, including exposed surfaces,
  wall bases and previous repairs. This guides placement; the game does not
  simulate adobe erosion or assume that all desert walls are water-damaged.
- [Epic's decal performance guidance](https://dev.epicgames.com/documentation/unreal-engine/decal-materials-in-unreal-engine)
  identifies screen coverage and material complexity as major costs. Here the
  marks are baked into a shared atlas rather than many projected decal passes.

The abandoned blanket grunge pattern is replaced by a 64-cell, 1024-square wear
atlas on a second UV channel. A stable hash assigns each building a maintenance
profile, with separate variants per wall: maintained, dusty, repaired, or neglected.
Masks place deposits near the base, limited runs beneath roof edges and irregular
resurfacing patches. Roofs, roads and equipment do not inherit the wall atlas.
The profile distribution is weighted toward maintained/dusty buildings; large
recessed damage is limited to selected nearby neglected walls. Cavity locations,
sizes and outlines vary; the face is replaced by a jagged opening with inset
reveals and backing, not covered by a black polygon. Existing distant LODs omit
these small cavities. Texture noise is baked at startup, not evaluated per frame.

The native review includes bird's-eye, service, market and racing views, plus a
new camera just ahead of Marvin's head at 0.46 m above the ground. This inspection
camera does not change normal follow/orbit controls.

The release build and native city smoke pass. Final captures are in
`../marvin-town-planning/organic-city-final/`. Near/far architecture totals are
357,456/256,152 triangles, within the existing budgets. A 45-second 1920×1080
check averaged 59.91 render callbacks/sec, with CPU update p95 2.82 ms, five
callback intervals above 25 ms and none above 50 ms. Metal HUD GPU p95/p99 were
15.60/16.46 ms; presentation p95/p99 were 16.67 ms, with a 33.33 ms maximum.
This short run shows no material regression from the service-access check, but
does not guarantee a locked 60 FPS. Results and HUD sampling limitations are in
`../marvin-town-planning/organic-city-performance/`.

## Brick track boundaries

Both tape fences are replaced with three staggered courses of clay brick. The
upper course varies from roughly 0.36 to 0.40 m above its local ground, matching
the former 0.40 m posts, with individually sloping, chipped top edges. Shared
plaster microdetail and varied warm brick colors keep the masonry in the city's
palette. Each boundary is one static batched mesh instead of individual brick
nodes; dirt remains one startup-baked ground layer.

The 0.15 m wall thickness extends away from the existing track collision line.
Infield-side collision clearance includes that thickness. The service entrance
remains open, including staggered course ends. The existing elevated grandstand
(first seating platform above 1.4 m) and north terrace (1.26 m platform) retain
sightlines above these low walls.

The infield clay mask and service wheel paths are unchanged. The same ground map
now also deposits clay outside the outer boundary: broad, noise-varied fans fade
into the town ground over approximately 1.5–3.6 m. This is baked visual deposition
from thrown soil, not a new runtime particle simulation.

Native screenshots and scene checks are in
`../marvin-town-planning/brick-walls-final/`.

Release build, native scene checks and all 30 simulation checks pass. The
45-second 1920×1080 benchmark averaged 59.89 render callbacks/sec, with five
intervals above 25 ms and none above 50 ms. CPU update p95 was 2.47 ms; Metal HUD
GPU p95/p99 were 15.70/16.42 ms (previous pass 15.60/16.46 ms). Presentation
p95/p99 remained 16.67 ms, with a 33.33 ms maximum. This shows similar performance
in this short run, not a locked-60 guarantee. The wall meshes are separate from
the town architecture triangle counters. Measurements are in
`../marvin-town-planning/brick-walls-performance/`.

## Continuous town streets

Street centerlines now use interpolating Hermite curves with tangents limited
by adjacent block lengths. They retain the existing destination and junction
anchors while easing the former sharp polyline corners. Continuous ground
ribbons replace overlapping road boxes and circular corner caps; the rigid
parallel cart markings are removed. Narrow tonal shoulders frame the earth
surface. Building exclusion uses the sampled curves, and street pedestrians
are placed along the same rendered routes.

The streets are one static mesh generated at scene initialization. The road
mesh is separate from town architecture triangle counters. Release build and
native layout, network, city coverage, camera, pause/reset and sign checks pass.
Final captures are in `../marvin-town-planning/curved-streets-final/`.

The 45-second 1920×1080 check averaged 59.89 render callbacks/sec, with five
intervals above 25 ms and none above 50 ms. HUD GPU p95/p99 were 15.69/16.45 ms,
comparable to the brick-wall pass (15.70/16.42 ms). Presentation p95/p99 were
16.67 ms; the maximum was 33.33 ms. Results are in
`../marvin-town-planning/curved-streets-performance/`; this short check does not
guarantee a locked 60 FPS.

## Full-height stepped retaining walls

The fence-only brick treatment above is superseded. Masonry now starts at a
common foundation below surrounding ground and retains the entire raised track
side. Every bed joint is horizontal at a fixed 0.13 m course spacing. Changes
in track elevation add or remove complete courses, producing stepped tops;
bricks no longer tilt to follow the track. Small top-edge chips remain, without
sloping the structural courses. The crest follows the old fence clearance,
quantized to brick courses.

The exposed earthen side slopes are removed from rendering and terrain height
outside the solid walls. The service opening retains its original dirt ramp;
wall thickness and chassis clearances remain coordinated. Infield and outside
clay spill maps and tent rugs are unchanged. Release build, all 30 simulation
checks and native scene checks pass. Screenshots are in
`../marvin-town-planning/retaining-walls-final/`.

The 45-second 1080p check averaged 59.91 render callbacks/sec; four intervals
exceeded 25 ms and none exceeded 50 ms. GPU p95/p99 were 15.54/16.22 ms;
presentation p95/p99 were 16.67 ms, maximum 33.33 ms. Results are in
`../marvin-town-planning/retaining-walls-performance/`. Performance remains
comparable in this short check, with occasional missed frames.

## Connected infield service access and salvage

The orange tent is now a smaller three-corner canopy at (-4.6, -9.4), rotated
180 degrees into the narrower infield pocket. Its woven rug is clipped to the
same triangular outline, retaining the checks, border and fringe. Equipment
and colliders use the same rotation and are repositioned with it. A shared service lane runs
from the gate past the open side to the front of the blue tent; this entire
route, including the turns, is reserved before scattering salvage. Sixteen
seeded rusty brake housings, engine blocks and ribbed panels occupy other
infield pockets. Their transforms are shared by rendering and physics.

Infield fixtures use fixed oriented boxes/cylinders in the existing 240 Hz
collision solver. Bench legs and elevated worktops, lift/droid, hoist, cabinets,
crates, pottery, tent poles and stationary mechanics have individual bounds.
Canopies use elevated convex envelopes, with the actual triangular footprint
for the orange canopy: there is no invisible ground-level tent box across the
passage. These are coarse collision envelopes, not deformable cloth or loose
rigid-body salvage. Small decorative tools, cables and fabric fringe are not
individual collision bodies. Static collisions stay enabled when robot-to-robot
collisions are disabled.

All 31 simulation checks pass. The new checks cover every chassis along the
whole access route and turning at its waypoints, 8 m/s approaches to every
salvage piece, canopy footprint/overhead clearance, and reversing away from a
pole. Native scene checks also pass. Screenshots are in
`../marvin-town-planning/triangular-service-final/`.

The final rotated layout's 45-second 1920×1080 check averaged 59.89 render
callbacks/sec. CPU update p95 was 2.96 ms; GPU p95/p99 were 15.57/16.34 ms.
Five callback intervals exceeded 25 ms, none exceeded 50 ms. Presentation
p95/p99 remained 16.67 ms, maximum 33.33 ms. The new static contacts show no
material regression in this short run. Measurements are in
`../marvin-town-planning/triangular-service-performance/`.

The orange workshop is tucked farther into the narrow pocket. A regression
check requires its canopy footprint to clear the complete service opening,
while the full route to the second tent still passes for every chassis.
