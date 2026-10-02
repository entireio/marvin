# Mos Aster: city rebuild

The dirt circuit now sits inside a continuous city. The rectangular perimeter
road and distant mesa ring are removed. Market and dock roads form an irregular,
connected branching network, and the city extends roughly 150 scene units in each
direction so neither the original comparison overview nor the gameplay overview
shows an empty desert outskirts. The historical rebuild below used worn paving. The October 2 revision uses
trampled sand, reachable courtyards, and an irregular settlement boundary; see
the latest comparison and validation notes at the end of this document.

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

## Superseded: streets, neighborhood lanes and doorstep access

The visible city now has 25 connected routes with a hierarchy of surfaces:
4–5.2 m muted brown main roads, 2.5–3.2 m warm earthen neighborhood lanes,
1.4–2 m pale alleys, and wider light-colored civic approaches. Neighborhood
loops replace the previous restriction that the street network had to be a
tree. Routes lead to the market, spectator terrace, cargo forecourt, comms
tower and landing bays; both round landing bays have openings aligned with
their approach paths. The freight route passes beside the hangar.

Roads reserve space before building placement. Smaller infill houses face the
new lanes and occupy plots too small for the original compound grid. There
are 74 generated doorway connections in this layout. Connectors are rejected
if they cross another lot or the racing corridor; the generator does not claim
that every distant decorative building has a navigable route. Main roads,
lanes and door paths remain static batched geometry, with the existing LOD and
crowd budgets. Buildings total 1,320, with near/far architecture at
351,444/251,520 triangles; street ribbons are counted separately.

The release build, all 31 simulation checks, and native scene checks pass.
The orange tent's new position remains completely clear of the entrance, and
all four chassis can reach the second tent and turn at every service waypoint.
Screenshots are in `../marvin-town-planning/town-streets-final/`.

A 45-second 1920×1080 race benchmark averaged 59.91 render callbacks/s, with
CPU update p95 2.70 ms, four callback intervals over 25 ms and none over 50 ms.
Metal HUD reported GPU p95/p99 of 15.47/16.18 ms and presentation p95/p99 of
16.67/16.67 ms, with a maximum presentation interval of 33.33 ms. Thermal state
was nominal. This short run is comparable to the preceding layout, but still
includes occasional missed frames; it does not establish a locked 60 fps.
HUD samples can repeat and include HUD overhead. Raw results and the parsed
HUD report are in `../marvin-town-planning/town-streets-performance/`.


## Mos Eisley / Mos Espa layout correction

The 25-route/74-doorway-path layout above was rejected visually. It created
suburban setbacks and painted driveways, rather than a Tatooine street fabric.
The correction removes all individual connectors and the extra residential
road loops. Trackside compounds return to the spaces those loops had cleared.
Six shared through routes remain, 2–4.2 m wide, with freight access beside the
hangar. Local circulation reads as the unpainted sandy space between clustered
buildings and around their courtyards. It is not a claim of a fully navigable
city: distant compounds remain backdrop scenery.

References inspected in-browser:
- [Mos Espa, official Databank](https://www.starwars.com/databank/mos-espa): dense clusters and larger civic spaces.
- [Mos Eisley, official Databank](https://www.starwars.com/databank/mos-eisley-spaceport): buildings and awnings open directly onto shared sandy ground.
- [Phantom Menace street still](https://mitchdarbyarchitect.com/blog/most-remote-mos-espa): continuous walls, arched passages and sand running directly to entrances.

The interpretation is an original arrangement around the race, not a copied
map. Roads are now subtle worn-earth overlays with feathered, uneven edges;
there are no parallel curb-color bands or pale doorstep strips. Different
widths and a restrained difference in wear distinguish the through roads
from smaller lanes. The orange tent and its shared collision geometry remain
in the approved corner position.

Architecture remains batched and within the existing budgets: 1,338 buildings,
354,516 near / 253,082 far triangles. The detailed dressing radius is 52 m to
retain the restored houses without exceeding the 360k near-architecture budget.
Final images are in `../marvin-town-planning/mos-layout-final/`.

Validation: release build and native town smoke pass (layout clearance, city
coverage, connected through routes, camera obstruction, crowd pause and reset).
Overhead and market screenshots were inspected against the references; shader
compilation/blending issues found during that inspection were corrected before
these final captures. Physics and the shared infield layout were not changed.
A 45-second 1080p benchmark averaged 59.94 render callbacks/s, CPU update p95
2.59 ms, with three callback intervals over 25 ms and none over 50 ms. Metal HUD
GPU p95/p99 was 15.50/16.13 ms; presentation p95/p99 16.67/16.67 ms, maximum
33.33 ms. Nominal thermal state. This is comparable to the preceding run,
not proof of locked 60 fps; HUD logging may repeat samples and adds overhead.
Results: `../marvin-town-planning/mos-layout-performance/`.

## Filled service entrance and rotated repair tent

The previous entrance fell from the track shoulder to flat ground over only
0.45 m. Cropping an offset ribbon to the gate left its lateral edges open.
The entrance now descends over 3.6 m, with smooth longitudinal easing and broad
side shoulders. Rendering samples the same height function as driving. A closed
heightfield covers the opening and overlaps the existing track beneath its
surface; perimeter skirts and a bottom face fill it below ground. A small render
bias avoids coplanar surfaces. The sand base uses the surrounding ground material,
with irregularly distributed track clay fading over it. The existing infield
and outside-wall dirt deposits remain in place.

The orange tent is at (-3.8, -9.2), rotated 30 degrees clockwise from its previous
orientation. Canopy, triangular woven rug, furniture, mechanic, sign and collision
bodies all share its transform. Placement validation samples the actual triangular
footprint, instead of rejecting usable space outside its shape. The entry-facing
canopy corner clears the opening by more than 1.5 m. The service route remains
clear to the blue tent. Salvage placement excludes the raised ramp, and the gate
markers follow its ground height.

All 31 simulation checks pass, including a new centimetre-resolution slope check
below 1:4 through the driving corridor, gate passage in both directions for every
chassis, turns along the full service route, canopy clearance and salvage contacts.
Native scene checks also pass. New low front and side camera captures explicitly
inspect the entrance for exposed undersides. Screenshots are in
`../marvin-town-planning/service-embankment/`.

The final 45-second 1080p benchmark averaged 59.91 render callbacks/s, with CPU
update p95 2.86 ms and four intervals over 25 ms (none over 50 ms). Metal HUD GPU
p95/p99 was 15.49/16.31 ms; presentation p95/p99 16.67/16.67 ms, maximum 33.33 ms.
Thermal state was nominal. This short run remains comparable to the preceding
layout, with occasional missed frames; HUD samples may repeat and logging adds
overhead. Results: `../marvin-town-planning/service-embankment-performance/`.

## Turn-one city escape

G is an unlisted Dirt Track command that toggles a 3.2 m metal gate on the
outside of the first bend. It opens outward through 100 degrees on a motor-driven
hinge, with bounded acceleration and angular speed. Its rendered transform and
upright collision body share the same angle. Current and predicted robot contact
stalls the motor; G can reverse it. Pause stops the gate, key repeats do not toggle
it repeatedly, and reset closes it. Both adjacent walls rise to the gate head,
then step down in level brick courses into the original wall.

The exit has a filled 5.5 m earth ramp with a closed underside, a flattened and
feathered threshold, broad shoulders, and irregular clay fading into town sand.
The physics heightfield owns the driving surface. The moving leaf clears the
terrain throughout its swing; both fixed posts also collide. Only the apron and
swing corridor reserve space in the city layout. Existing infield access is intact.

Town construction now emits conservative upright collision proxies alongside its
render geometry: adobe walls, boxes, cylinders, support beams, landing-bay wall
segments and citizens. The 6,400 solids use 8 m spatial buckets rather than scanning
the whole town per robot. Buildings and citizens are immovable; there is no
ragdoll or destruction simulation. Existing robot motion is an upright game
simulation with traction, momentum and vertical terrain response, not a full
six-axis rigid-body engine. Camera bounds extend to the outer neighbourhoods.

Race assists and collision recovery yield to manual control near an open exit
and while outside. A previous-position wall-side check prevents impacts from
pushing racers through other parts of the outer wall. Rivals accidentally nudged
through the doorway steer back through it instead of driving into the wall.

Validation includes all 32 SimulationChecks, gate ground-clearance and acceleration
sweeps, blocked closing/reopening, closed/open crossings for all four chassis,
centimetre-resolution ramp slopes below 1:4, outer-wall impulse containment, and
spatial bucket boundary coverage. The native `--city-escape-smoke-test <directory>`
drives ordinary inputs through the live coupled simulation: G, exit, four town
locations, return, close, pause and reset. No route step teleports Marvin. It also
runs boosted impacts and reversing against 12 generated fixtures per chassis.
The final route exceeded 100 m with no detected town/gate penetration and retained
all rivals in the race. Screenshots include closed, moving and open gate states,
Marvin crossing the ramp, town locations, robot-eye and bird's-eye views. Results
are in `../marvin-town-planning/city-escape-final/`.

`--town-benchmark <directory> --city-roam` exercises the town route with the normal
chase camera. Without `--city-roam`, the benchmark retains its racing and overhead
camera sequence. Benchmarks use a 1920 × 1080 drawable and separate CPU callback
measurements from Apple Metal HUD GPU/presentation samples.

Final 45-second measurements on the local machine, with a 1080p drawable:

| Route | Mean render callbacks/s | CPU update p95 | GPU p95 / p99 | Callback gaps over 25 ms |
| --- | ---: | ---: | ---: | ---: |
| Race | 59.86 | 2.89 ms | 15.48 / 16.44 ms | 6 |
| Roaming | 59.99 | 2.72 ms | 14.50 / 15.32 ms | 1 |

Both runs remained in nominal thermal state, with no callback gaps over 50 ms.
Metal HUD presentation p95/p99 was 16.67/16.67 ms for both, with occasional
33.33 ms intervals. These short runs are comparable to the previous build;
they do not establish a locked 60 fps. HUD logs can repeat samples and add overhead.
The final native escape run covered 112.23 m over 167.27 simulated seconds,
with zero measured town/gate penetration. All 13 native escape checks and the
existing town smoke suite passed after raising both entrance walls.

## Missed-frame investigation: Activity Monitor interference

On this MacBook Air M2, the recurring presentation gaps followed Activity
Monitor's activity. Temporarily suspending that application removed the gaps;
resuming it brought them back. No game-quality reduction was needed for the
successful control. This identifies an external trigger in this setup, not a
proof that every possible missed frame has the same cause.

The release build now has benchmark-only per-frame telemetry: render callback
intervals, SceneKit stage spans, game physics, robot models, dust/trails, camera,
town updates, position, and camera mode. JSON is written only after measurement.
`analyze-frame-timeline.py` correlates long callback intervals with nearby update
work. These spans are wall-clock callback durations, not exclusive CPU time or
GPU command-buffer timestamps. The normal game does not collect this telemetry.

All trials used a 1920 × 1080 drawable; the first three seconds are excluded
from callback statistics. Trials ran one at a time. Rows with shorter durations
are explicitly shown, so raw event counts should not be compared as rates.

| Control | Run length | Mean callbacks/s | Intervals >25 ms | Game update p95 |
| --- | ---: | ---: | ---: | ---: |
| hud-baseline | 72 s | 59.919 | 6 | 3.19 ms |
| no-hud | 72 s | 59.904 | 7 | 2.62 ms |
| display-link | 72 s | 59.872 | 9 | 3.71 ms |
| msaa2 | 72 s | 59.918 | 6 | 2.64 ms |
| no-shadows | 72 s | 59.912 | 6 | 2.92 ms |
| without-town | 72 s | 59.903 | 7 | 3.09 ms |
| monitor-paused | 72 s | 59.999 | 0 | 2.60 ms |
| monitor-resumed | 45 s | 59.937 | 3 | 2.58 ms |

The first six controls retained Activity Monitor. Turning off the HUD, switching
the update timer to an NSView display link, using 2× MSAA, disabling shadows, or
hiding the town did not eliminate the periodic gaps. They often recurred at
multiples of roughly 5.3 seconds, at different track positions and in different
camera modes. One additional first-aerial-camera hitch was observed, but camera
switching does not explain the recurring stalls.

The decisive control retained the whole city, all collision work, 4× MSAA,
shadows, normal timer and Metal HUD. Only Activity Monitor was suspended, using
SIGSTOP with an EXIT trap to restore it via SIGCONT. Its settings and permissions
were not changed. The 72-second run averaged 59.9986 callbacks/s with zero
intervals above 25 ms. Metal HUD's final 65-second window reported presentation
intervals of 16.67 ms throughout, despite GPU p99 of 16.18 ms and a 17.29 ms
maximum. The immediately following 45-second run with Activity Monitor resumed
had three long callback intervals; 33.33 ms presentations returned, despite
similar GPU time (p99 16.05 ms, max 17.30 ms). Activity Monitor was left resumed.

The city-hidden control is also important: gaps persisted with renderer cycles
of only 3–6 ms, so a long SceneKit cycle in an earlier trial was insufficient
reason to attribute the problem to city geometry or renderer execution. Game
updates around ordinary missed frames were typically 1.5–3.7 ms. Physics was
usually below 1 ms; the display-link trial had a 6.83 ms catch-up outlier, still
below a frame budget. There is no evidence here for a collision-bound frame.

Two attempts to obtain a macOS thread sample did not complete and were stopped;
their benchmark runs are excluded. Xcode/Instruments and metalperftrace were not
available. The exact OS sampling/locking path used by Activity Monitor has not
been traced; the causal claim is based on the reversible running/paused/resumed
control. Metal HUD logs can repeat samples, so HUD counts are not unique-frame
counts. A short successful run does not prove a universally locked frame rate.

For representative playing and profiling, close Activity Monitor or otherwise
stop its live collection before taking baseline measurements. Keep the current
art and collision settings. The benchmark flags remain diagnostic only:
`--benchmark-display-link`, `--benchmark-msaa2`, `--benchmark-no-shadows`, and
`--benchmark-chase-only`. No timing or rendering defaults changed.

Artifacts and raw timelines: `../marvin-town-planning/frame-investigation/`.
To reproduce a timeline summary, run:

```sh
python3 scripts/rendering/analyze-frame-timeline.py /path/to/benchmark-directory
```

### Town departure recording

`--town-departure-movie <output-directory>` captures a continuous drive from the
last eastern street onto the drivable dunes.
Only the initial spawn is placed; the route uses ordinary throttle, steering,
60 Hz coupled physics and town collisions. The camera checks buildings and the dune surface. Output is 1280×720 JPEG frames at 30 fps plus `departure.json`
with arrival, travel distance and maximum solid penetration. This offline
recording is not a real-time performance benchmark.

Encode the captured frames with:

```sh
ffmpeg -framerate 30 -i /path/to/output/frame-%05d.jpg -c:v libx264 -crf 18 -pix_fmt yuv420p -movflags +faststart /path/to/departure.mp4
```


### Drivable dune field

The outer settlement transitions into a deterministic dune heightfield beyond
156 m on each axis, keeping existing foundations level. Warped asymmetric ridges
have broad windward climbs, shorter lee slopes and varied heights. The current
sampled field peaks around 9.1 m with a maximum sampled slope of 32.9 degrees.
The dune profile follows [USGS descriptions of windward and slipface slopes](https://pubs.usgs.gov/gip/deserts/eolian/).

A cached 2 m grid supplies exactly the same triangular interpolation for the near
render mesh, wheel height, chassis pitch/roll, ground collision and track marks.
Dune driving adds the downhill component of gravity to the existing traction
limited drivetrain. Camera booms stop before intersecting a ridge. The field is
finite (1,536 m across), tapering back to flat at its distant perimeter.

Static 64 m terrain tiles have prebuilt 2/4/8 m detail levels at 0/130/260 m view
distances; every level retains the same 2 m boundary vertices to avoid cracks.
No terrain meshes regenerate during play. Dunes receive lighting but do not add
shadow-map submissions. Distance-based terrain detail follows the principle of
spending geometry near the viewer described in [NVIDIA's terrain rendering chapter](https://developer.nvidia.com/gpugems/gpugems2/part-i-geometric-complexity/chapter-2-terrain-rendering-using-gpu-based-geometry);
this implementation uses SceneKit tile LOD rather than GPU clipmaps.

`SimulationChecks --dunes-only` covers flat town foundations, collision/render
height agreement, boundary continuity, slope bounds and all four playable robots
traversing a dune. The native departure capture records actual ascent, tilt,
solid penetration and terrain penetration in `departure.json`.
`--town-benchmark <directory> --dune-roam` measures normal real-time rendering on
a repeated dune drive; the offline movie export is not a frame-rate benchmark.

Validation: all 33 simulation checks passed, including all four chassis climbing
and descending beyond a dune crest. The 65.2 s native departure movie travelled
66.7 m, reached 5.49 m elevation and 12.43° pitch, with zero measured ground or
solid penetration. Images include a fresh dune overview.

At 1920×1080 with normal quality settings, a 45 s dune benchmark averaged 59.90
render callbacks/s with four intervals over 25 ms and none over 50 ms. Repeating
with Activity Monitor temporarily paused averaged 59.996 callbacks/s with zero
intervals over 25 ms; CPU update p95 was 2.88 ms. Activity Monitor was restored.
These are SceneKit callback measurements, not GPU/display presentation timing,
and do not establish a universal frame-rate guarantee. Artifacts are under
`../marvin-town-planning/dunes-final`, `dunes-performance` and
`dunes-performance-isolated`; movie: `../marvin-town-planning/marvin-enters-dunes.mp4`.

### Surface-aware tracks, race departure and loading

Ground impressions now multiply the already-lit surface color, preserving the
underlying sand/clay hue and texture, instead of overlaying fixed brown paint.
Edges are feathered; dune marks use lighter contrast. Vertex normals follow the
sampled terrain. Native pixel comparisons on sand, clay and dark ground check
that marks remain subtle and do not shift the ground hue.

Three-lap classification and finish times remain frozen while a separate signed
cooldown counter records actual travel. After **all four** racers finish and
complete at least one additional lap, the city gate opens. Racers use ordinary
steering, throttle, braking and collisions to leave in sequence, then follow four
separate town routes. Routes are prepared while loading, with clearance for the
tallest chassis; the narrow gate approach is reserved one racer at a time.
The results panel becomes compact during departure. Once everyone arrives, the
player can drive around town. Pause freezes the sequence and reset clears it.

Choosing Dirt Track in the main menu displays a progress screen immediately.
Town/terrain construction and route planning run on a worker queue; stage and
tile completion reports update a monotonic progress bar on the main thread.
Scene preparation is asynchronous, and 100% is shown only after the first frame
is ready. Repeat visits reuse the constructed world. The progress bar exposes an
accessibility role/value; its painted fill matches the numeric percentage.

Validation commands (packaged executable):

- `--trail-material-smoke-test <directory>` checks rendered hue/contrast on three ground colors.
- `--loading-smoke-test <directory>` checks monotonic progress, completion, and responsive UI ticks, and captures the loading screen.
- `--postrace-smoke-test <directory>` drives a real race through cooldown and four town destinations; checks no early opening, frozen results, pause, reset and static collision penetration.

The 34 core checks include full cooldown-lap accounting, rejecting reverse/forward
oscillation and teleports. Two native full-race runs with different shuffled grids
completed all four escape routes with zero measured solid penetration. The dune
recording was repeated with corrected marks and zero terrain penetration.

Final 45 s 1080p dune timing check with Activity Monitor temporarily paused:
59.983 render callbacks/s, one 26.33 ms interval, no intervals over 50 ms,
CPU update p95 3.09 ms. Activity Monitor was restored. The delayed interval
coincided with a 33.94 ms tick gap, while its local CPU update stayed at 2.30 ms;
these callback measurements do not identify GPU/display presentation timing.
Artifacts: `../marvin-town-planning/trails-performance`, `trail-material`,
`loading-release`, `postrace-final`, and `dune-tracks-final`.

### Ground-dependent spray and dust

Debris now takes its color and cohesion from its emission location. Clay retains
large clods; packed town earth and dunes emit millimeter-scale grit, with a
higher proportion of fine dust. The town-to-dune blend follows the terrain
shader's 156–192 m feather. Particles retain their source color throughout their
lifetime. Pale dust fades in/out, expands slowly and loses launch momentum;
grains follow gravity and settle against the terrain heightfield. Ground contact
is sampled separately for each emitter, including slopes and reverse driving.
Tread impressions are unchanged. The pool remains bounded to 1,600 slots and two
draw batches; emission is capped with speed.

`--debris-smoke-test <directory>` checks clay/dune size and color differences,
contact heights, batched vertex colors, pause, stopped emission, expiry and reset.
The native tread pixel checks and 34 simulation checks also pass. A 65-second
close-up departure capture traversed 66.7 m with zero measured terrain/solid
penetration. Set `MARVIN_DEPARTURE_CLOSEUP=1` with `--town-departure-movie` to
repeat that framing.

A 45 s 1080p dune run measured 59.997 render callbacks/s, zero intervals over
25 ms, and CPU update p95 1.28 ms. Activity Monitor was temporarily paused and
restored. These are SceneKit callback timings, not GPU presentation measurements.
Artifacts: `../marvin-town-planning/ground-spray-closeup`, `spray-performance`,
and `marvin-ground-aware-spray.mp4`.

### Twin suns and per-race daylight

The planet uses a fictional circumbinary arrangement, inspired by real systems
such as [Kepler-16](https://science.nasa.gov/exoplanets/other-stars-other-worlds/kepler-16-b-almost-a-real-life-tatooine/).
A planet outside a close binary sees two nearby stars; their projected separation
changes with binary phase. Our model places the binary components at 0.06 and
0.14 AU from their barycenter, with the observer approximately 1 AU away, at
25° latitude on an equinox. This is a plausible geometric model, not a claim to
reproduce Kepler-16 or a canonical Star Wars ephemeris. Stellar brightness and
angular sizes are art-directed for this fictional planet.

Each race/reset draws a binary phase and a uniform time within the pair's shared
daylight interval. Conjunctions are excluded so both suns remain distinguishable.
The selected sky is frozen over a race. Both suns share the same direction data
with their directional lights. Color/extinction use a Beer–Lambert approximation
with [Kasten–Young optical air mass](https://doi.org/10.1364/AO.28.004735): low suns
lose more blue light. A procedural sky dome supplies the discs and forward haze;
a matching diffuse environment probe replaces the old fixed-sun HDR image.
Directional lights provide specular highlights at the correct angles.

Both lights use forward shadows, so each star can illuminate the other star's
shadow. Primary/secondary maps are bounded to 2048/1024 pixels. HDR bloom comes
from the visible sun discs; opaque world geometry occludes them normally, with
no always-visible flare overlay. Exposure is fixed to prevent brightness pumping
when a sun enters the camera. See Apple's [shadow modes](https://developer.apple.com/documentation/scenekit/scnlight/shadowmode),
[bloom threshold](https://developer.apple.com/documentation/scenekit/scncamera/bloomthreshold),
and [exposure adaptation](https://developer.apple.com/documentation/scenekit/scncamera/wantsexposureadaptation).

`--binary-sky-smoke-test <directory>` samples 2,000 random skies, checks daylight
and light/disc direction alignment, runs three full four-robot races at stratified
random morning/midday/evening times, captures overview and sky screenshots, and
renders an opaque-screen sun/glare occlusion comparison. Race resets must select
fresh daylight. `MARVIN_DAYLIGHT_FRACTION` selects repeatable sun angles for
`--town-benchmark` only; ordinary race starts remain random.

Validation (2026-09-30): all four racers completed three laps in each of the
morning, midday and evening runs; all 2,000 daylight samples and the sun-occlusion
pixel check passed. The 34 core simulation checks also passed. Captures and JSON
are in `../marvin-town-planning/binary-races-final`.

Two 45 s 1080p circuit benchmarks measured 59.975 (midday) and 59.999 (evening)
render callbacks/s. Each had one interval over 25 ms and none over 50 ms; CPU
update p95 was 1.01/0.99 ms. The midday 32.71 ms gap coincided with a 32.51 ms
simulation tick gap (local update 1.25 ms, renderer cycle 4.41 ms). The evening
25.84 ms gap occurred in the aerial view (renderer cycle 14.76 ms, local update
1.13 ms). These measurements do not prove GPU/display presentation cadence.
Activity Monitor was temporarily paused and restored for both runs. Artifacts:
`binary-perf-midday` and `binary-perf-evening` beside the captures.

Low-sun art pass: a narrow golden horizon now transitions into cool dawn air or
copper/rose dusk. Directional atmospheric haze follows the same two sun vectors;
soft disc edges, limb darkening and restrained bloom replace the hard flat discs.
Brighter sky fill and stronger direct lighting preserve rooftop highlights and
readable racing shadows. The sky still uses one dome draw, with the existing two
shadow maps; no screen-space flare overlays or added lights. Native captures use
a lower 7.5 m viewpoint. Five full four-robot race runs (sunrise, sunset, morning,
midday, evening), random-daylight checks and the opaque-screen glare test passed.
Captures: `../marvin-town-planning/atmosphere-v2` and `atmosphere-daylight-check`.
The revised 45 s 1080p low-sun circuit benchmark averaged 59.974 render callbacks/s,
with one interval over 25 ms, none over 50 ms, and CPU update p95 1.00 ms.
Activity Monitor was restored after the run. Callback timing is not GPU/display
presentation timing. Report: `../marvin-town-planning/atmosphere-performance`.

## Sandstorm weather

Each race/reset independently draws a 10% chance of Sandstorm; there is no
weather setting or persisted preference. A storm shares the race's 240 Hz
clock, so pause stops wind, deposition and effects, and reset starts fresh.
Only 11 of the normal 296 people remain outside, including sparse spectators;
absent citizens also lose their collision bodies.

`Sandstorm` supplies gusting crosswinds to every chassis using relative air
velocity, frontal area and mass. Nearby buildings reduce wind exposure.
Wind advects robot dust faster than heavy clods. Seventeen irregular deposits
grow over three minutes to roughly 7–15 cm, reducing traction, adding rolling
resistance and changing body pitch/roll. Rendered sand and collision heights
use the same analytic field; a 10 cm mesh approximates it within 4.02 mm in the
coverage test. Fresh spray transitions from clay clods to fine sand on deposits;
tread impressions fade as wind buries them. This is a bounded game weather
model, not fluid dynamics or a simulation of every transported sand grain.

Rendering adds one static displaced drift mesh and two bounded particle batches
(960 grains/haze quads total), without new shadow-casting lights. Dust fog and a
muted sky/environment probe obscure the twin suns. Thin near-ground sheets and
streaks move with wind; distance clipping keeps the effect local to the camera.

Validation: all 35 core checks pass, including identical storm simulation at
30/60/120 Hz and unchanged clear terrain. Native full-race tests verify all four
finish, collision contact over deposits, pause/reset, reduced population and
removed citizen colliders. The storm post-race test also completed the cooldown
lap and four separate town escape routes, with no measured obstacle penetration.
`--sandstorm-smoke-test <directory>` captures overview, chase, deposits and stands;
`MARVIN_STORM_MOVIE=1` additionally records 450 JPEG frames at 30 fps.
`MARVIN_SANDSTORM=1` enables storm benchmark and post-race test runs.

The 45-second 1920×1080 circuit comparison measured 59.977 render callbacks/s
in the storm versus 59.975 clear. Both recorded two intervals over 25 ms and
none over 50 ms. CPU update p95 was 1.70 ms storm versus 1.01 ms clear. The storm
gaps occurred at a delayed timer tick and the first aerial camera transition;
there was no sustained frame-rate loss. Activity Monitor was temporarily paused
and restored for each run. These are SceneKit callback/CPU measurements, not GPU
completion or display presentation timestamps. Reports and native captures are
in `../marvin-town-planning/storm-performance`, `storm-clear-baseline`,
`storm-release`, `storm-menu`, and `storm-clear-trails`.


### Natural dust revision

Ground-level reference photos: [NWS Amarillo, February 14, 2023](https://www.weather.gov/ama/February_14_2023_HighWind_Dust),
especially Chelsey Snook's Hooker street view and Dustin Sides Valdez's Guymon
road view. These show continuous suspended dust and distance-dependent loss of
contrast; the old isolated bright circles did not match that appearance.
Particles now explicitly consume vertex tint/opacity, use irregular density
textures instead of radial gradients, and stretch low dust along projected wind.
Clay clods retain their existing geometry and physics. References were inspected
as photographs; no claim of watching ground-level video is made.

Star Wars still references also inspected:
- [The Phantom Menace: Mos Espa sandstorm production still](https://starwarsaficionado.blogspot.com/2019/08/the-phantom-at-twenty-caught-in.html): thin low dust sweeping across the street, with buildings still legible.
- [Lucasfilm's Return of the Jedi deleted-scene guide](https://www.starwars.com/news/jedi-at-40-deleted-scenes): the Tatooine sandstorm still has dense warm haze and obscured background detail. It is a deleted sequence, not part of the theatrical film.
These guide low dust layers and earthy tint; movie imagery is reference only and
is not included as a game texture. The ambient haze remains bounded to the same
two particle batches.

Revision validation: 35 core checks pass, including 1,006 storms from 10,000
seeded independent weather draws. Native settings, debris emission and full
four-robot storm race checks pass. The revised 45 s 1080p benchmark averaged
59.975 render callbacks/s, two intervals over 25 ms, none over 50 ms, and CPU
update p95 1.78 ms. Activity Monitor was restored. Capture/report directories:
`../marvin-town-planning/storm-film-reference`, `storm-natural-menu`,
`storm-natural-debris`, and `storm-natural-performance`.

### Town driving, clod color and gaze corrections

The circuit shoulder speed penalty previously applied everywhere outside the
track, reducing the drivetrain cap by 72% even on open town and dune sand.
It now applies only inside the track boundary. Loose-ground traction, slopes,
wind and physical drag from accumulated storm sand still affect motion.
Track-bend head lookahead stops when a robot starts its escape route or leaves
the circuit; nearby-robot glances and manual head controls remain available.

Clod materials now explicitly consume their per-particle ground tint and alpha,
as dust already did. The native debris check renders the actual clod material
under white lighting and samples pixels, verifying distinct brown clay and
lighter sand, in addition to emission size, tint and terrain contact checks.

Validation: the packaged macOS build and all 36 core checks pass. Native debris,
four-route post-race escape and full four-robot storm race checks pass. Escape
reported zero obstacle penetration; maximum storm terrain penetration was
0.083 mm. Captures are in `../marvin-town-planning/town-driving-fixes` and
`../marvin-town-planning/town-fixes-storm`.

### Dust visibility investigation

`--dust-visibility-test <directory>` drives Marvin for 1.5 seconds on clay,
town soil and dunes at 70% throttle (4.2 m/s drivetrain speed), under fixed
midday lighting. It captures identical states with only the dust batch toggled.
The diagnostic reports emission and image differences; its exit status checks
emission, not visual quality. Particle variation remains stochastic.

The inspected captures confirm dust is emitted but visually too weak: one run
contained 35/77/83 live dust particles on clay/town/dunes. Texture alpha averages
0.0665, multiplied by a maximum particle alpha of 0.30: about 2% mean coverage
before lifetime fading (17% at the strongest texture point). Small, low puffs
lasting 0.65–1.15 seconds blend into their soil-colored background. Clay clods
and tread marks remain visible. This diagnostic changes no gameplay visuals;
further tuning should improve plume size, persistence and optical density
while preserving source-ground color and the existing two particle batches.
Captures: `../marvin-town-planning/dust-visibility`.

### Visible airborne dust correction

Tire dust now uses a denser irregular texture, 12–20 cm initial particle radii,
1.2–1.8 second lifetimes and greater loft with slower air damping. Ground contact
preserves upward dust velocity instead of cancelling the launch kick; clods
still settle normally. Lambert lighting replaces unlit tire dust so it responds
to scene lights. Soil tint, clay clods, ambient storm texture, premultiplied
blending, emission rate, 1,600-particle pool and two draw batches are preserved.
Mean texture coverage rises from 6.6% to 15%; clear particle opacity is 48%,
with lower opacity in storms. This gives about 7.2% mean initial coverage before
fading, rather than 2%. Capture comparisons rejected an overly dense dark plume
and an incorrect straight-alpha experiment before selecting the final result.

The packaged build, native debris color/contact/pause/expiry checks, and a full
four-robot storm race pass. Final clay/town/dune paired captures were inspected
in `../marvin-town-planning/dust-lift-final`; storm captures are in
`../marvin-town-planning/dust-lift-storm`.

Final 45-second 1080p timing: 59.975 SceneKit callbacks/s, 1 intervals over 25 ms, 0 over 50 ms; CPU update p95 1.19 ms. These measure callbacks, not GPU completion or display presentation. Activity Monitor was paused and restored. Report: `../marvin-town-planning/dust-lift-performance`.

### Level city-gate threshold

Raised the banked approach to a level sill across the closed gate's full width,
retaining its small uniform operating clearance. A 2.3 m trackward blend feeds
the threshold; the outside ramp holds that level for 20 cm before smoothly
falling to town soil over the existing 5.5 m run. Lateral shoulders taper into
the ground. Terrain rendering, tread placement and chassis contacts all use the
shared height function, including a short continuous blend at the lane edge.

Validation: packaged macOS build and all 36 simulation checks pass. New samples
across the complete gate width/depth stay within 3 mm of the sill; existing
ramp grade, gate-sweep clearance, closed-gate blocking and four-chassis
bidirectional crossing checks pass. Native gate opening, town roaming, return,
closing/reset, rival containment and obstacle collision checks pass with zero
measured solid penetration. Closed/open screenshots were inspected in
`../marvin-town-planning/gate-threshold-release`.

### Camera, crowd, shadows and driving-assist regression pass

The race-finished camera now stays at the track overview throughout cooldown
and all four escape routes. Camera cycling, orbit and zoom cannot change it;
resetting or starting another race releases the lock. The post-race smoke test
attempts all three inputs after every finished-race step and checks the exact
camera transform, alongside the existing gate timing, frozen results, pause,
route completion and obstacle-penetration checks.

Crowd boots previously used a fixed unisex center that missed the authored
ankles. The exporter now fits each shaft to its source ankle and overlaps the
trouser hem before posing and simplification. All 12 variants and three LODs
were rebuilt. `scripts/city/check-crowd.py` ray-tests each trouser-hem center
against the closed boot volume: all 72 posed ankles pass; the old assets fail.
This check runs as part of the existing asset/build validation.

Directional shadow maps no longer refit when the camera rotates. They stay
anchored over the track and move in light-space texel increments during town
exploration. The primary/companion maps are 4096/2048 with eight samples,
retaining the 58 m half-extent and independent forward lighting from both suns.
The extra resolution addresses the large self-shadow breakup seen on the CAD
shell at close range. Mesh subdivision, inset duplicate casters, back-face-only
casting and deferred shadow compositing were tested and rejected; none ships.

`--visual-regression-test <directory>` renders stationary robots from elevated
and low cameras, compares shadowed/unshadowed ground samples in world space,
checks that all four robots cast shadows, captures a close orbit in the actual
town, and renders front/back sheets for every citizen LOD. Silhouette-edge
samples are excluded from the interior stability metric because pixel coverage
changes with viewing angle. The fixture includes distant buildings and the
terrain's full extent rather than assuming a small isolated scene represents
the town. `MARVIN_SHADOW_AUTO=1` restores the old projection/resolution settings
for a negative control. The image metric does not replace inspecting the CAD
surfaces, silhouette edges and citizen sheets.

Assist design references: [Gran Turismo 7's official driving-options manual](https://www.gran-turismo.com/au/gt7/manual/drivingoption/03)
separates predictive corner braking from steering/countersteering assistance
and describes reserving tire grip for cornering under ABS. [Forza's official
Drivatar/physics discussion](https://forza.net/news/forza-motorsport-drivatars-tire-physics)
distinguishes planning braking points/entry speeds from the driving controller.
These informed the approach; this is not either game's implementation.

Brake help now previews curve speed limits and braking distance, trims excess
throttle and applies proportional deceleration before a tight turn. The grip
budget accounts for soil and storm accumulation and retains lateral grip under
assisted braking. Manual braking takes priority. Steering keeps at least 80%
of full directional input instead of the former 25%, never starts without
input, and never reverses the requested turn. Off-track, reverse, paused and
airborne control gates remain in place; assist-off dynamics are preserved.

All 36 simulation checks pass, including independent switches, stopping
authority, steering continuity and 30/120 Hz equivalence. Across 12 scripted
missed-braking cases, wall-contact frames fell from 216 to 126 and summed peak
line deviation fell from 24.15 to 22.56 m. The deliberately simple driver still
makes mistakes: this evidence supports the change but does not prove subjective
driving feel or perfect cornering. Centerline adherence alone is no longer the
success criterion for steering help.

Release validation: the final 24-view shadow sweep passes (2,686 repeated
world samples, zero interior samples varying by more than 15%; shadows detected
for every robot). Restoring the old projection/resolution produces 123 unstable
samples and fails the same test. Clear and storm post-race runs both complete
all four routes with the camera locked, results frozen and zero measured solid
penetration. Captures/reports are in `../marvin-town-planning/quality-regression-final`,
`quality-negative-control`, `quality-postrace` and `quality-postrace-storm`.

The final 45-second 1080p run averages 59.974 SceneKit callbacks/s, with two
intervals over 25 ms and none over 50 ms; CPU update p95 is 1.414 ms. Activity
Monitor was paused for the run and restored afterward. These are callback
measurements, not GPU-completion or display-presentation guarantees. Report:
`../marvin-town-planning/quality-performance/benchmark.json`.

The native daylight regression also passes three complete four-robot races
(morning, midday, evening), 2,000 sampled sun configurations and the sun/glare
occlusion check. Overhead captures were inspected at both low-sun extremes;
reports are in `../marvin-town-planning/quality-daylight`.

### Continuous post-race town exploration

Once every competitor has completed its extra lap, the HUD and toolbar disappear.
The existing fixed overhead camera remains locked. Departures use different gate
lanes and approach timing, and keep the ordinary drive, terrain and collision
simulation. After leaving, robots follow closed town circuits and switch circuits
on subsequent visits, with different cruising speeds and short pauses. They do
not park permanently or return control to the player at a final waypoint.

Town routes are prepared during level loading. Navigation includes building
footprint clearance, gate posts and the open gate leaf. Swept, rounded corners
and local lookahead steering replace the previous stop/pivot/straight movement.
Opposing traffic can pull into checked street space; blocked robots can reverse
briefly using the same physical controls and collision solver. Steering rejoins
the local route segment after yielding instead of aiming across an inside corner
at a distant waypoint.

`--postrace-smoke-test DIR` now gives all four robots twelve simulated minutes
after the last robot leaves, in addition to the complete race, extra laps and
earlier departure time. It rejects persistent
stops, repeated recovery loops, missing tours, lack of late exploration, or a
minute without moving robots passing near the track. It checks static and robot
contacts, camera locking, hidden HUD/toolbar, frozen results, pause and reset.
`MARVIN_SANDSTORM=1` exercises the same run in a storm. Optional
`MARVIN_ROAM_MOVIE=1` records normal-speed 10 fps excerpts of the first three
minutes and thirty seconds beginning at minute eleven, using the locked camera.
The rest of the endurance simulation is accelerated; these are not claims of a
continuous real-time rendering benchmark.

Final validation: `../marvin-town-planning/roam-release` (clear) and
`roam-v9-storm` both pass twelve minutes after departure. All four robots complete
two circuits, visit new two-metre cells in every minute after departure settles,
and pass near the track throughout. Clear-weather travel is 921–1,264 m per robot,
with no reverse recoveries; storm travel is 933–1,054 m with 1–4 recoveries. The
largest measured contact overlap is 0.22 mm (solver tolerance), with no building
or gate penetration beyond tolerance. HUD, toolbar, camera-input lock, cooldown,
frozen scores, pause and reset checks pass. All 36 simulation checks also pass.

`--town-benchmark DIR --postrace-roam` advances into town exploration before
measuring the actual live renderer. The 45-second 1920×1080 release run averages
59.975 callbacks/s, with one interval over 25 ms, none over 50 ms and CPU update
p95 of 1.261 ms. Activity Monitor was paused and restored for this measurement.
These are SceneKit callback timings, not GPU/presentation guarantees. Report:
`../marvin-town-planning/roam-performance/benchmark.json`.

### Animated spectators and residents

`CitizenMotion` keeps spectator head turns and intermittent, independently phased
one-arm waves inside the existing crowd batches. Joint masks follow the authored
sleeve/hand mesh islands at every LOD, so a seated skirt cannot be mistaken for an
arm. Extra texture-coordinate channels carry each person's bind frame and joint
weights (SceneKit supports [multiple texture-coordinate sources](https://developer.apple.com/documentation/SceneKit/SCNGeometrySource/Semantic-swift.struct/texcoord)).
The shader also updates normals; conservative geometry bounds include gestures.
Only walking residents require separate nodes/material uniforms. Gait phase is
based on distance travelled, and CPU evaluation of the same sole transforms
keeps the lowest boot on the street or doorway threshold.

Ten residents use twenty cached routes between thirteen physical house entrances.
The selected facades contain actual openings and recessed vestibules, with
sliding door leaves whose collision proxies follow their rendered positions.
Residents appear behind closed doors, wait for clearance before leaving, and
remain visible inside until the door closes. Schedules vary their destination,
pace and time indoors. Narrow routes are reserved before departure to avoid
opposing pedestrian queues; approaching robots trigger collision-checked
step-aside motion where space permits. Doors, residents, scenery and robots
participate in collision checks. Moving proxies are attached to the immutable
city spatial index without rebuilding it every frame. Most residents shelter
indoors during a storm; spectators still use the existing reduced storm crowd.

`--people-smoke-test DIR` simulates ten minutes of repeated trips, checking every
resident makes progress, house entries, overlaps, pause, storm shelter and reset.
`MARVIN_PEOPLE_MOVIE=1` also records normal-speed close-ups of walking/doorways,
spectator reactions and an overhead view. The twelve-minute post-race test now
updates residents alongside all four robots and checks both populations remain
active, in addition to its existing camera, HUD and route-progress assertions.

Validation for this change: all 36 SimulationChecks and the bundled crowd asset
checks pass. The isolated ten-minute run (`../marvin-town-planning/people-release`)
completed 37 entries/39 exits, with every resident visiting another house and
zero measured pedestrian penetration. The mixed twelve-minute clear run
(`people-roaming-v3`) completed 52 entries and two circuits per robot; the storm
run (`people-roaming-storm`) completed eleven entries for its single outdoor
resident and two circuits per robot. Both preserve the fixed camera and hidden
HUD, pass collision tolerances, and keep residents moving.

The 45-second 1920×1080 post-race rendering benchmark (`people-performance`)
measured 59.976 SceneKit callbacks/s, one interval over 25 ms, none over 50 ms,
and CPU update p95 of 1.397 ms. The preceding implementation measured 59.975
callbacks/s and CPU p95 of 1.261 ms with the same benchmark setup. Activity
Monitor was paused and restored; this is callback cadence, not a GPU timestamp
or a guarantee about every displayed frame. No per-frame city-index rebuilds or
route searches are introduced.

### Camera-aware resident updates

The live game and benchmarks pass SceneKit's
[frustum test](https://developer.apple.com/documentation/scenekit/scnscenerenderer/isnode(_:insidefrustumof:))
to the resident scheduler. Visible walkers retain the normal update rate, with a
350 ms grace period after leaving the view to avoid oscillating at its edges.
Off-screen route movement and indoor schedules update at 10 Hz. Residents near
robots, open-house thresholds or other pedestrians retain full-rate collision
responsiveness. Coarse steps sweep their complete movement; they cannot jump
through thin geometry between updates. Visibility returns gait to the current
travelled-distance phase on the next update, without teleporting or resetting it.
Off-screen walkers use a cached resting sole height instead of evaluating gait.

This is conservative frustum culling, not a promise to detect people hidden
behind buildings inside the view. The batched spectators still share just three
clock uniforms: their limb deformation runs in GPU geometry shaders and their
non-shadow-casting, off-screen batches are culled by the renderer. Duplicating
materials per spectator merely to suppress those three assignments would add
work. Off-screen residents keep collision proxies and doorway schedules alive.

`MARVIN_PEOPLE_OFFSCREEN=1 --people-smoke-test DIR` exercises ten minutes with
all detailed resident poses disabled, then checks immediate camera-cut wake-up,
gait phase, bounded movement, pause, storm shelter and reset. The first run
(`../marvin-town-planning/visibility-offscreen`) completed 38 house entries,
zero measured overlaps, zero detailed pose updates and 83,169 navigation updates
versus 360,000 full-rate resident updates (77% fewer). Benchmark reports now
record the actual rendering API and GPU device, alongside update counters.

Rendering uses SceneKit's GPU backend; lighting, materials, shadows and crowd
geometry deformation are graphics work. The custom simulation, collision solver,
route planning and race rules execute on the CPU. The implementation is not a
GPU-compute physics or navigation engine. `metalRenderer` and `gpuDevice` in the
benchmark report verify the active backend rather than assuming the default.

The camera-throttling regression exposed a deadline assumption in the mixed
traffic test: the last robot's queue/track-exit time was consuming its two-circuit
window. In `visibility-roaming`, every resident progressed and robot movement,
collision and per-minute checks passed, but one robot reached only waypoint 432
of its second circuit before the old cutoff. The endurance window now starts
when all four have departed. It retains the two-circuit minimum and checks every
complete minute from first departure, including the longer run; the final partial
minute is not treated as a full minute's required travel.

The longer run then exposed a genuine recovery loop at the northeast building
corner (`visibility-roaming-final`): WALL-E reversed away from traffic and tried
to rejoin a route segment beyond the corner, repeatedly hitting the intervening
building. Recovery now selects a swept-clear earlier route sample before
restoring forward lookahead. This is a bounded check of existing route points,
not a global route search. The core regression includes both a reachable earlier
leg and a blocked direct rejoin; neither teleports nor bypasses collision.

The clear follow-up (`visibility-recovery-clear`) passes 850.9 seconds from first
departure, including 720 seconds after all four leave: every robot completes two
circuits, residents complete 61 house entries, and camera/HUD, per-minute movement
and collision checks pass. The storm follow-up (`visibility-recovery-storm`)
**does not pass**: BB-8 stalls during the race after one lap at approximately
(19.944, -9.132), before the gate opens. Residents continue their shelter schedule
(26 entries by the outdoor resident), but this is not a successful storm escape
endurance run. The race/closed-gate stall was investigated and fixed in the
follow-up below; this original failed result is retained for comparison.

Final visibility-aware pedestrian capture (`visibility-people-final`) passes ten
minutes, 38 house entries, camera wake-up/phase, pause, shelter and reset, with
zero measured pedestrian penetration. All 36 SimulationChecks pass, including
the new reverse-rejoin fixture. The 45-second 1920×1080 live benchmark
(`visibility-performance`) confirms `metalRenderer: true` on **Apple M2** and
measures 59.975 callbacks/s, one interval over 25 ms, none over 50 ms, and CPU
update p95 of 1.368 ms. It records 10,453 resident navigation updates and 5,203
pose updates. Activity Monitor was paused/restored as before. These callback
measurements do not establish GPU utilization or display-presentation timing.


### BB-8 storm race stall: closed-gate vault

The stalled position was outside the closed gate. A native deterministic sweep
of all 24 starting-grid permutations reproduced the escape with grid
`[1, 2, 0, 3]` at 35.25 seconds (`bb8-diagnosis/escape.json`). BB-8 hit a storm
drift while boosting at approximately 10.7 m/s, then landed on the rising bank.
At that landing, vertical speed changed from -4.31 to +4.79 m/s without a
corresponding horizontal slowdown: the old terrain attachment added energy.
BB-8 subsequently slid across the closed gate's top and fell outside. The gate
collider was present; the AI then tried unsuccessfully to rejoin through it.

Rigid-body terrain landings now remove inward normal velocity and preserve
only tangential motion, using the actual terrain plus storm-deposit normal.
Storm AI previews deposits ahead on its lane, disables automatic boost and
uses proportional braking with a 3.2–5.2 m/s target. Clear-weather AI and manual
player boost are unchanged. There are no teleport recoveries or invisible
barriers above the gate.

`--storm-race-test DIR` resets all 24 grid permutations and uses the native race
physics, city collisions and wind shelter. It fails on premature BB-8 escape
or any unfinished racer, saves the last two seconds of motion on escape, and
writes `storm-grids.json` on success. This race-only test does not advance
resident animation; the separate endurance test does. All 24 arrangements
passed after the fix, each with all four racers finishing. All 37 core checks
pass, including conservation of tangential landing velocity, non-increasing
impact energy, storm accumulation pacing and retained clear-weather boost.

The clear endurance run (`bb8-fixed-clear`) passes 899.2 simulated seconds from
first departure, including 720 seconds after all robots leave. In
`bb8-fixed-storm`, the race stall is gone: all robots finish, the gate opens
only after cooldown, all four leave and complete two town circuits, and
camera/HUD, collision, resident and frozen-score checks pass over 891.8 seconds
from first departure. **The storm endurance test as a whole still fails** its
sustained-progress criteria: BB-8 has a 20-second stop and several minute bins
have too little spatial progress. This is a remaining town-roaming issue,
not a successful full endurance result; its thresholds were not relaxed.

A 45-second 1920×1080 storm **racing** benchmark (`bb8-storm-performance`)
includes the new AI preview cost: Apple M2 Metal renderer, 59.975 callbacks/s,
one interval over 25 ms, none over 50 ms, CPU update p95 2.003 ms. Activity
Monitor was paused and restored; no builds or other simulations ran during this
measurement. These are renderer callback intervals, not GPU utilization or
presentation timing, and this workload is not the earlier clear postrace one.

### Command-R weather verification

`--weather-reset-test DIR` dispatches actual Command-R key equivalents through
AppKit's main menu. It verifies forced storm and clear outcomes, including the
rendered-world weather, population and window title, then removes the test-only
weather override and performs 100 independent production-RNG resets. The run
in `weather-command-r` handled all shortcuts and produced 14 storms; both
weather screenshots were inspected. The seeded 10,000-draw core check produced
1,006 storms and all 37 core checks passed. No production probability change
was necessary: each reset has an independent 10% chance (a 20-reset dry streak
has probability 0.9^20, approximately 12.2%). Smoke-test launches intentionally
force weather; ordinary app launches leave `weatherOverride` nil. This test
explicitly clears that override before testing random resets.

Storm races show “A storm is coming...” in a centered amber pre-race overlay
during the camera introduction, then in the countdown card. The message is
removed at the start and is absent for clear races, pause and results. Cmd-R
updates the warning from the newly selected weather. The native weather-reset
check captures both weather states during introduction/countdown and verifies
that the warning becomes inactive when the countdown reaches zero.

### Outer-town detail during exploration

The old outer compounds (beyond 52 metres on either axis) were permanently
silhouettes, rather than merely distant LODs. Their doors, windows, awnings and
roof fittings now have separate, prebuilt 16-metre geometry batches. A moving
camera window enables those batches only with the player outside the central
24-metre square and the camera below 12 metres. Detail is fully visible within
42 metres of each batch center and smoothly fades to hidden at 58 metres.
SceneKit still performs frustum culling. The overhead finish view and racing
views leave this extra geometry hidden; their original architecture meshes and
82-metre LOD threshold are unchanged.

The new fittings are cosmetic dressing on existing solid buildings. They do
not add ground obstacles, change collision hulls or navigation, add animated
people, or submit extra shadow casters. Outer courtyard windows and low-roof
fittings are constrained to their supporting walls/roofs. Meshes are generated
once during loading and CPU builders released afterward; no generation or
asset loading occurs while driving. This trades additional resident geometry
memory/loading work for richer nearby views, rather than drawing a detailed
whole town every frame.

`--town-smoke-test` now compares simple/detailed shots at three distances along
the actual eastbound street and checks that race and overhead views disable the
extra batches. `--outer-town-survey` on the live benchmark follows that same
street with a low camera; `--benchmark-simple-town` disables the added detail
for a same-build comparison. This is a rendering survey while race physics
continues, not an autonomous robot navigation test.

Same-build 45-second, 1920×1080 Apple M2 Metal comparisons:
- Simple street survey: 60.004 callbacks/s, p95 interval 17.488 ms, CPU update
  p95 1.634 ms, zero intervals over 25/50 ms.
- Detailed street survey: 59.975 callbacks/s, p95 interval 17.402 ms, CPU update
  p95 1.817 ms, one interval over 25 ms and none over 50 ms; 26 detail batches
  active at the final camera position.

These are SceneKit render callback measurements, not GPU/display timestamps.
Activity Monitor was paused/restored and no builds or other simulation tests
ran alongside the benchmarks. The extra detail preserved approximately 60 FPS
in this survey; it does not establish zero cost or a guarantee on other hardware.

The 45-second race-camera benchmark (`exploration-perf-racing`) records 59.974
callbacks/s, CPU update p95 1.230 ms, one interval above 25 ms and none above
50 ms, with zero exploration batches active. The base near/far architecture
triangle counts remain 350,392 / 251,522.

Final visual verification (`exploration-detail-final`) passes town layout,
coverage, camera clearance, pause/reset and exploration visibility checks. The
added meshes total 982,044 triangles across 327 mostly hidden batches; base
architecture counts are unchanged. Three paired screenshots verify detail
from the outer district through the edge of the dunes.

The final long roaming check (`exploration-soak-final`) ran 849.1 simulated
seconds from first departure, including 720 after all four left. Camera/HUD,
score, gate timing, resident movement and collision tolerance checks pass
(maximum measured robot overlap 0.000774 m). **The full endurance test fails**:
R2-D2 and BB-8 complete only one circuit and several minutes have too little
spatial progress. The earlier exploratory run (`exploration-collision-soak`)
also failed, with a blocked resident and WALL-E reversals. These traffic findings
remain open; the final cosmetic meshes do not modify the existing collision
world or navigation, and endurance thresholds were not relaxed.


## October 2: accessible districts and reference-driven iteration

The current pass replaces the square, nearly touching outer grid with street-facing
compounds and connected pedestrian alleys. The established circuit, stands, gate,
service area, and trackside building centers remain in place. Larger core compounds
are narrowed where necessary to open walking access; the test audits final scenery,
not just the initial placement plan.

### Reference comparison

Directly inspected the Mos Eisley establishing frame in the SHOT.CAFE collection
above and the Mos Espa street and market film frames in the Mitch Darby collection.
The official Mos Espa databank aerial is a later television depiction; it was not
misidentified as a Phantom Menace aerial. Film images are research only.

The aerial reference has joined low volumes, domes, circular landing courts,
unequal heights, and distinct civic towers. Street references show rough plaster,
deep shade, recessed openings, worn sand, and useful objects collected at façades.
An iteration is not accepted simply because its collision tests pass.

| Observed gap in native captures | Change and evidence |
| --- | --- |
| Impenetrable residential blocks and repeated entrance positions | Rotated footprint clearance, five door families, entrance selection using a connected pedestrian grid, and final obstacle audit. 492/492 compounds and all three public venues connect to streets. |
| Same roof silhouette and no identifiable destinations | Nine compound families: unequal domed rooms, stepped terraces, windcatchers, vaulted workshops, open patios, and linked flat-roof rooms. Droid Exchange has a repair crane and dismantled equipment; Twin Suns has shaded tables; Traders Court has varied stalls and wares. |
| Flat, unshadowed outskirts | Architecture shadow casters now follow exploration within a bounded 75-unit radius. Fixed postrace overview retains the race-centered caster set. Survey captures also move the sun-shadow footprint correctly. |
| Featureless ground | Bundled CC0 Park Sand diffuse/normal/roughness maps provide trampled relief. A static mesh blends this finish out before the track and dunes. Separate soft patches follow actual doorway approaches. No paving grid or driveway stamp. |
| Uniform smooth walls | Two plaster scans, independently seeded maintenance histories, repaired areas, lower-wall dirt, selected roof runoff, and existing geometric cavities. Clay plaster is used on selected compounds outside the protected core. |
| Oversized, repetitive market props | Lower counters, staggered stalls, hanging wares, arched shopfronts, cantina cups and table supports, finned motors and dismantled droids. Storage, water containers, benches, and deliveries are placed beside clear entrances. |
| Stair treads did not reach upper rooms | Flights now terminate at the actual upper-room edge; rise and run follow the level difference. |
| Repeated skyline utilities | Four distinct towers now serve communications, observation, ventilation, and condensation, within their established reserved plots. |
| Windows floated beside nonrectangular buildings | Door and window placement share actual exposed wall faces; 1,886 probes verify wall support behind both ends of every window. |
| Residents cut doorway corners and stalled | Residents turn before stepping around tight corners and follow their swept planned direction, with a smaller waypoint arrival threshold. |

### Native evidence so far

Artifacts are under `/Users/thomi/Projects/marvin-town-planning/`:

- `town-reference-v3` through `town-reference-v13`: nine district aerial views,
  three public-place aerial views, three pedestrian-height venue views, three
  alley views, five door families, and activity/collision reports. The comparison
  uses the same daylight and camera coordinates across passes.
- `town-reference-v10-navigation`: a physical 163.9-second track → town → dunes →
  town → track drive, including camera cycling in town and dunes. Region sequence
  and smooth-follow checks pass. This includes whole-town and course overviews,
  town/dune overview transitions, and robot chase-view captures along the route.
- `town-reference-v10-postrace`: 720 seconds after all robots have departed;
  all four continue visiting town, 60 resident house entries, no blocked residents
  at completion, zero maximum measured robot penetration, locked overview, hidden HUD.
- `town-reference-v13`: 492 house routes and three venue routes, 21,212
  pedestrian samples, zero blocked approaches/routes, and 1,886 supported-window
  probes. Nine district aerials plus the oblique and street views were inspected.
- `town-reference-v13-viewport`: HUD hide/show and restart preserve the camera,
  viewport, and projected landmarks at three window sizes (zero measured drift).
- `town-reference-v13-perf-race`: 45 seconds at 1920×1080 on Apple M2, 59.97 mean
  renderer callbacks/s, p95 17.22 ms, p99 18.42 ms, one interval >25 ms and none
  >50 ms. CPU update p95 2.05 ms.
- `town-reference-v13-perf-town`: same duration/device/resolution, 59.98 mean
  callbacks/s, p95 17.50 ms, p99 18.03 ms, one interval >25 ms and none >50 ms.
  CPU update p95 5.08 ms with up to 16 exploration cells active at completion.
  Both slow intervals occurred near simulated 15.07 seconds, with measured CPU
  updates of 1.76/3.69 ms. These measurements do not identify the cause of that
  isolated delay. They measure SceneKit callback intervals, not GPU execution or
  display presentation timestamps; they are not a guarantee of zero dropped frames.
- Core simulation checks and city asset integrity/attribution checks pass.

Current map behavior: COURSE uses the racing map. TOWN caches the actual street
and building layout; DUNES adds terrain contours and the player's relation to the
town. TOWN and DUNES do not plot rival positions. The 270-wide panel is unchanged.
Outside the circuit, bird's-eye view follows the player through a critically damped
transition; reentry restores the course overview. The postrace camera lock takes
precedence over this exploration behavior.

The second density pass adds 64 small workshops in safe gaps and three enclosed
residential forecourts. Outdoor spaces are accepted only when final pedestrian
routes remain clear; no density target overrides access. The market has deep
masonry arcades with filled spandrels, hanging stock, and open produce trays.
Venue plots are included in the cached town map.

The comparison includes high district aerials, a low oblique establishing angle,
pedestrian-height venue views, and actual physical drive-through captures. This is
not a claim of photographic equivalence to film imagery: the procedural residents,
merchandise and regular building silhouettes remain visibly simpler than the sets.
The improved composition, material relief, enclosure, and destination detail are
separately assessed rather than using a successful collision test as visual approval.

### Ground boundary and everyday outdoor areas (October 2)

The square visible around Mos Aster came from the terrain material's
`max(abs(x), abs(z))` colour mask, independent of the irregular building layout.
The central ground plane and surrounding terrain now share a world-space surface
shader. Its desert blend follows an irregular radial boundary. Fragment world
coordinates also work on the displaced dune patches without depending on a
material geometry modifier that a geometry-level modifier can replace. Existing
terrain heights, robot contact and racecourse geometry are unchanged.

Town pigment combines warped broad and smaller-scale noise with the scanned
surface. Doorway wear is retained, and furnished household areas receive local
worn-earth or lighter deposited-sand patches. The first high-contrast attempt
produced an excessive dark fringe in the straight-down image; it was rejected
and the palette/transition were softened. Dune ripple shading is retained.

130 distributed household areas add shaded workbenches with tools, water storage
and plumbing, pallet deliveries, seats and rolled mats, repair supplies, and clay
storage vessels. Placement tests the whole furnishing envelope against scenery
and pedestrian routes before constructing collision geometry. Selection is
spatially distributed rather than stopping at the first houses generated. Fixtures
are baked into existing spatial meshes, without per-frame prop updates.

The entrance survey now includes an orthographic, straight-down `town-plan.png`
and six household detail views, alongside nine district views and venue/doorway
captures. Every `saveTownFrame` capture rejects substantial shader-error magenta;
a successful scene build alone cannot pass that failure. This is a targeted
render guard, not a substitute for visual review.

Outskirts roads now lose coverage starting inside the final building fringe.
The road opacity breaks into anisotropic windblown tongues and fades completely
22 metres beyond the irregular town footprint. External path endpoints also
feather over their final 14 metres, eliminating square-cut ribbon ends. The
underlying terrain shows through the buried sections; navigation corridors and
collision surfaces remain continuous. `road-into-sand.png` adds a straight-down
close view of the eastern departure road to the visual survey.

### Resident sidestep recovery (October 2)

The v20 postrace audit found a resident blocked for 533.2 seconds. A later replay
reproduced the same failure at a building corner: the resident had safely stepped
aside for traffic, then aimed diagonally toward a route waypoint through nearby
scenery. Collision detection stopped that step indefinitely. Static entrance
checks could not detect this off-route return maneuver.

Residents now remember the sidestep origin, retrace the swept outbound segment,
and resume their route only after returning. Route waypoints stay fixed during
the maneuver. Each return step still checks scenery, doors, robots, and people;
there is no teleport or disabled collision response.

A native regression finds an obstructed shortcut back from a valid sidestep in
the actual town collision geometry. The old code failed to reach its waypoint in
40 simulated seconds (39.27 seconds blocked). The corrected code reaches it,
with a longest stop of 0.52 seconds and zero penetration. Run with
`MARVIN_YIELD_RECOVERY_TEST=1` and `--people-smoke-test <output-directory>`.
The extended postrace audit now checks the peak resident blocked duration over
the entire run, in addition to completed visits and final state.

Validation artifacts under `marvin-town-planning`: `resident-corner-before` and
`resident-corner-after` record the deterministic failure/fix. Both
`resident-fixed-roaming-1` and `resident-fixed-roaming-2` pass 720 seconds after
all four robots depart: all ten residents visit homes (57 and 58 total entries),
longest stationary intervals 11.68 and 3.12 seconds, no resident left stuck,
collision assertions pass, and robots keep touring with the HUD hidden and
camera locked. The macOS release build and asset validation also pass.
