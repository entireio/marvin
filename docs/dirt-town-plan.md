# Dirt Track: desert spaceport environment plan

Status: original proposal, September 28, 2026. A first version is now implemented; see [v1 delivery and verification](dirt-town-v1.md). The remainder of this document records the original plan and targets. Based on repository commit `709bfce`, source inspection, the local release build, and the primary sources below. Performance numbers below are proposed acceptance targets, not measured results.

Baseline runtime observation: the native smoke run launched and produced race/overview captures at 2560×1384 pixels. The aggregate test failed because `fullRaceTrailsPassed` was false; the other reported Boolean checks passed, including race completion, opponents, collisions, camera, and reset. The trail report records one buried sample. Diagnose that existing failure during baseline work before claiming a clean regression result; it is not caused by town changes. Reports and captures from this planning session are in `/tmp/marvin-town-baseline/` (temporary evidence, not committed assets). This run does not measure real-time frame pacing.

## Intent and visual direction

Turn the existing race into a circuit through a compact, inhabited-looking desert spaceport inspired by Star Wars: sand plaster, rounded walls, domes, exposed machinery, repair shops, antennas, cargo yards, and a distant spaceport skyline. Make the town coherent at street level and in the opening aerial view.

Confirmed intent: the **spectator stands** should be embedded into the town. Make them an architectural centerpiece: stepped seating above shops, viewing terraces on roofs, and balconies facing the circuit, connected to town streets through stairs and public plazas. Preserve the existing circuit; rerouting the race through streets or adding standings displays is not required.

The town should have recognizable districts, connected streets, courtyards, and buildings on both sides of selected race sections. Avoid an even ring of disconnected houses outside the fence. Keep open areas around jumps and the hill so the race alternates enclosed streets, open yards, and skyline reveals. Use original environment assets with a consistent art direction.

## Existing system and constraints

| Component | Evidence in repository | Consequence |
| --- | --- | --- |
| Course | `Sources/SimulationCore/DirtRace.swift`: fixed closed spline, 768 samples, lane half-width 1.95, fence offset 2.45, terrain edge 3.0 | Keep this geometry and its collision envelope authoritative. |
| Terrain | `DirtCourse.elevation`, `surfaceHeight`, `height`: banked lane, jumps, shoulders declining toward the surrounding ground | Building foundations and roads must account for the real height surface. |
| Racing | `DirtRacePhysics.swift`: four racers on a 240 Hz fixed-step solver; lap progress follows contacts | Scenery should add no work to the physics loop in the first version. |
| Rendering | `MarvinSimulator/DirtWorld.swift`: SceneKit scene, five surface meshes, a directional sun, 4096² shadow map, fog, bleachers | Add a separate environment subtree; share the current light rig. |
| Geometry | Five course meshes each use 768 × 32 × 2 triangles: about 245,760 terrain triangles before other objects | The current scene already has a meaningful geometry cost. This is a source-derived count, not a GPU capture. |
| Racers | Marvin exports 695,172 CAD triangles, plus animated running gear; rival meshes add further geometry | Imported CAD count is not identical to rendered triangle count because some parts are hidden. Profile before assigning remaining budget. |
| Effects | `DirtWorld`: 1,600 pooled fleck nodes; `DirtTrail`: up to 128 chunks per racer, current geometry recreated on update | Late-race trails and dust must be included in worst-case measurements. |
| Frame scheduling | `App.swift`: 60 Hz main-thread timer, continuously rendering SCNView targeting 60 fps, 4× MSAA | Timer duration alone is not renderer frame time; collect both CPU and GPU evidence. |
| Cameras | Chase camera starts 4.5 units away; zoom spans 1.6–9; orbit and overview; intro/outro reach (0,38,-33) | Check camera clearance and skyline from all supported views, including resets and reversed driving. |
| Preparation | `startDirtTrack()` already calls `prepare` and `snapshot` before revealing the scene | Extend preparation to town resources and alternate detail levels. |

Paths in the table are relative to `apps/simulator-macos/` unless otherwise stated. Use scene units consistently: the robot presentation is rescaled, with R2-D2 at 0.885 scene units. Do not apply the README's raw CAD conversion blindly to building sizes. Establish scale using doors and racers in the first blockout.

## Spatial design

Phase ranges below are normalized spline phase (`phase / 2π`), not equal-distance sectors. They are initial art-direction zones; precise lot placement must use the sampled course and exclusion geometry.

| Course phase | District | What the driver sees | Track integration |
| --- | --- | --- | --- |
| 0.94–1.00, 0.00–0.12 | Finish plaza | Race gateway, repair arcade, control booth, stepped spectator terraces | Existing start grid and finish remain clearly visible; stands become part of civic buildings and rooftop terraces. |
| 0.12–0.28 | Market quarter | Domed plaster shops, fabric canopies, recessed doors, short side alleys | Building fronts frame turns; keep the jump landing and braking sightline open. |
| 0.28–0.47 | Service district | Garages, tanks, pipes, cargo stacks, small courtyards | The circuit reads as a dirt service road; closed side streets connect the larger town. |
| 0.47–0.65 | Spaceport apron | Hangar frontage, landing-pad silhouette, parked utility craft | Broad open view around rhythm jumps; no overhead geometry through the flight envelope. |
| 0.65–0.81 | Ridge settlement | Terraced buildings, retaining walls, one tall communications landmark | The hill reveals the skyline; foundations step with terrain instead of floating. |
| 0.81–0.94 | Outskirts and return | Utility sheds, moisture-tower-like machinery, perimeter road | Open sightline through whoops, with the finish landmark visible ahead. |

Start with roughly 60–100 building volumes across the full settlement. This is an art target, not a minimum: density is determined by useful screen coverage and measured cost. Give a small number of near-track facades detailed doors, awnings, vents, and cables; let middle and distant blocks communicate density through roofs and silhouette.

Road network: author a small graph of intersections and roads connecting districts, yards, and the outer access road. Reserve road corridors before placing lots. At race intersections, use visible race barriers and marshaling props; side roads remain scenery in v1. Keep the drivable surface dirt with packed-earth shoulders. Covering jumps with flat asphalt or adding traversable intersections would require a separate driving/terrain change.

Build an exclusion mask from the **whole** course, not just the nearest placement phase. Reject oriented building footprints intersecting the lane, fence, shoulders, or another course branch. Start building walls beyond the 3.0-unit terrain edge plus a provisional 0.5-unit buffer, then enlarge setbacks where camera sweeps require it. Tiny infield pockets become low courtyards or utility yards rather than forced buildings.

The clearance validator must check complete footprints and edges, not only centers. Overhangs, signs, gateways, and cables additionally need vertical checks against the tallest racer and boosted jump envelope. Prefer no track-spanning structures in the first slice. Orient doors toward connected streets; use foundation skirts or terraced plinths where a flat floor meets sloping ground. Extend the outer ground coherently into dunes and distant rock forms.

## Spectator stands and people

People are part of the environment scope, not an optional finishing touch. The first polished district must demonstrate both inhabited stands and some street activity before expanding the town.

- **Main stands:** replace the isolated finish bleachers with stepped spectator terraces built above repair shops and a shaded arcade. Show supporting structure, railings, aisles, access stairs, and entrances from the plaza. Align seating toward the finish and validate sightlines from representative seats. Scale stairs, doors, and people consistently against the racers.
- **Secondary viewing areas:** smaller roof terraces, balconies, and raised seating overlooking a readable turn or jump. Keep spectators behind barriers and all seating outside the course and camera-clearance envelopes. Avoid placing crowds where walls block their view of the race.
- **Population:** a visually varied mix of humans, original alien silhouettes, and service droids. Start with 6–8 low-poly body/outfit variants, shared material atlases, several seated/standing poses, and restrained color variation. Distribute people in uneven groups with empty seats, clear aisles, vendors, and small conversations rather than uniform rows of duplicates.
- **Activity:** selected near-camera spectators turn their heads, shift weight, or cheer as racers pass. Vary animation phase and reaction timing; use a shared event with per-character delay/cooldown rather than simultaneous repeated waving. A few vendors, mechanics, and pedestrians occupy safe plazas and short authored walking loops. Characters do not cross the course or require general navigation, avoidance, or physics in v1.
- **Detail tiers:** near spectators use simple articulated low-poly 3D characters; middle-distance groups use merged posed meshes with only occasional animated figures; distant and aerial views use simplified opaque 3D clusters. Retain roof-view silhouettes. Test any future billboard solution for orbit/aerial readability and transparency cost before adopting it.
- **Runtime bounds:** provisional authoring target of 150–250 total spectator/resident placements, with at most 16 fully animated visible characters and at most 4 walking characters within that cap. Choose active characters by projected importance with hysteresis; freeze or replace others with matching static poses. Stagger reduced-rate updates for secondary motion and use interpolation where needed. Respect pause and focus-loss behavior.
- **Cost:** people must fit inside the existing town budget. Initially reserve up to 30k of the 150k near-view town triangles and 20 of the 80 additional main-pass draws for people. Share assets, batch static crowd groups per cell, and limit crowd shadow casting. These are proposed ceilings to validate on the target Mac, not measured capacity or guaranteed population density.
- **Crowd validation:** benchmark the fully occupied finish stands during a four-racer start and finish, plus the aerial view. Check animation/crowd LOD transitions, repeated race resets, pause/resume, memory, blocked aisles, seat penetration, and sightlines. Reduce individual detail and animation before making the town visibly empty.

## Research applied to this engine

1. **Race-oriented offline preparation.** Turn 10 describes dividing a track into zones, sampling visibility around the centerline, and preparing geometry/detail decisions before runtime. For Marvin, adopt offline sector metadata and coarse visibility sets. Do not copy the original console memory limits or implement a large streaming system for this small circuit. [Forza Motorsport: Streaming Massive Environments, Chris Tector, GDC 2010](https://media.gdcvault.com/gdc10/slides/Tector_Chris_StreamingMassiveEnvironments.pdf).
2. **Modular districts.** Epic's City Sample describes a procedural pipeline with road networks and building placement. Borrow its authoring order: roads, blocks, lots, buildings, detail. Nanite and World Partition are Unreal features, not capabilities of this SceneKit app. [City Sample](https://dev.epicgames.com/documentation/unreal-engine/city-sample-project-unreal-engine-demonstration).
3. **Distant block proxies.** Epic's HLOD system groups static meshes into proxy meshes/materials to reduce draw calls. Implement a small equivalent for town cells: a detailed representation nearby and a merged simplified roof/facade shell farther away. [Epic HLOD documentation](https://dev.epicgames.com/documentation/en-us/unreal-engine/world-partition---hierarchical-level-of-detail-in-unreal-engine).
4. **SceneKit-specific batching and LOD.** Apple documents draw-command overhead and flattening static subtrees, and supports geometry detail selected by projected size or camera distance. Merge only within small spatial cells, preserving useful culling; do not flatten the entire town. Shared mesh references save resources but must not be assumed to provide automatic GPU instancing. [Flattening](https://developer.apple.com/documentation/scenekit/scnnode/flattenedclone%28%29), [LOD](https://developer.apple.com/documentation/scenekit/scnlevelofdetail).
5. **Measure frame pacing, not just average FPS.** Apple's game-performance guidance emphasizes profiling CPU/GPU work, stalls, and thermal behavior. Record presented-frame intervals and expensive passes, alongside application update timings. [Metal Game Performance Optimization](https://developer.apple.com/videos/play/wwdc2018/612/).

These are adaptations to this repository, not claims that SceneKit has Unreal's complete environment pipeline. Keep SceneKit for this scoped feature; keep layout and exported assets separate from renderer code so they remain reusable.

## Asset and runtime architecture

Proposed files, not yet implemented:

- `Resources/Town/layout.json`: versioned district, road, lot, landmark, and cell definitions; deterministic seed; explicit authored overrides; scene-unit convention.
- `Resources/Town/Source/`: modular authored assets and source metadata. A small Blender kit is suitable for finished art; generated simple meshes are sufficient for the blockout. Blender is an offline authoring dependency, not a requirement for launching the app.
- `scripts/export-town-simulator.py`: validate layout and clearance, tessellate road/ground meshes, build material groups, generate bounds and coarse visibility metadata, and export packed geometry plus manifest. Use the existing packed-mesh pipeline conventions rather than introducing a runtime glTF loader.
- `Resources/Town/Generated/`: near geometry, simplified cell proxies, material atlas references, and manifest. Extend `prepare-simulator-assets.py` with input hashes and caching; extend `build-app.sh` to package the Town directory. Generated output follows existing asset conventions.
- `TownWorld.swift`: scene assembly, immutable static cell nodes, shared materials, separate sparse dynamic props. Attach once under `DirtWorld.scene` and reuse on race restart.
- `TownVisibility.swift`: view-dependent cell/LOD selection, separate street and aerial policies, conservative camera coverage, stable thresholds.
- `TownQuality.swift`: explicit High/Balanced/Low rendering profiles. Quality settings never alter physics, race timing, assists, or collisions.
- `TownCrowd.swift`: seeded spectator placements, static crowd-cell meshes, bounded near-character animation, race reaction events, and authored pedestrian loops; shares town visibility and quality policies.
- `TownSmoke.swift` and `RaceBenchmark.swift`: reproducible camera captures and an actual real-time frame-pacing route.

Kit: 8–12 primary building shells; interchangeable domes, parapets, doors, shade canopies, and roof machinery; 3–4 landmark forms. Bake small surface relief into textures. Prefer opaque, single-sided materials, closed shells with sensible backfaces, and shared atlases. The current general `material()` helper enables double-sided rendering, so introduce a town-specific material factory instead of inheriting that cost by default.

Prepare geometry/material resources during loading. CPU asset decoding can happen off the UI thread; attach scene mutations through one defined owner. Prepare all required LODs before driving, with explicit checks for variants not reached by the initial overview. Avoid disk I/O, texture decoding, mesh generation, and synchronous resource preparation during laps. Keep this compact town resident initially; add streaming only if measured memory use warrants it.

## Frame-rate plan

The first target machine is the available M2 MacBook Air, 24 GB, macOS 26.6.2. It is fanless, so a short cold run is insufficient. Select a baseline render resolution of **1920×1080 actual drawable pixels**, and separately report performance at the native Retina/fullscreen drawable size. Window points are not pixel resolution. The hardware configuration is an initial benchmark target, not a claim about minimum supported Macs.

| Budget | Initial proposed target |
| --- | --- |
| Frame rate | Stable 60 fps on the baseline target, 16.67 ms frame interval |
| Whole-frame headroom | CPU critical work and GPU frame time each preferably ≤12 ms at p95; assess their overlap rather than adding them blindly |
| Town incremental cost | ≤2 ms GPU and ≤0.5 ms CPU at p95 in matching A/B camera paths |
| Town geometry | ≤150k visible triangles in street views; ≤50k for distant/aerial town proxies, excluding existing racers/track |
| Town submissions | Provisional ≤80 additional main-pass draws street-level, ≤40 aerial; measure shadow-pass submissions separately |
| Texture residency | ≤128 MiB incremental town texture allocation, including mipmaps; total town resource growth ≤192 MiB initially |
| Warm-run pacing | Aim for p99 presented intervals ≤17.5 ms on a fixed 60 Hz display, missed refresh intervals <0.5%, and no repeatable scene-caused >33.3 ms stalls |

These are gates for the first slice, not universal SceneKit limits. If the existing scene already misses 60 fps, identify and fix that bottleneck before filling the remaining budget with art. An average FPS number alone does not pass the gate.

Techniques in priority order:

1. **Small spatial batches.** Begin with roughly 6–8 scene-unit cells, adjusted to actual streets. Merge static surfaces by shared material within cells. Keep animated props, signs with changing content, and different shadow policies separate. Benchmark cell sizes: tiny cells raise draw overhead; huge cells defeat culling.
2. **Three representations.** Near: complete shell and selected detail. Middle: simplified facade/roof shapes without small props. Far/aerial: merged district shells with roof detail retained. Screen-space LOD fits changing zoom; use hysteresis for manually switched cell representations. Preserve landmark silhouettes at every tier. Avoid transparent cross-fades unless profiling justifies their overlap cost.
3. **Conservative visibility.** Begin with SceneKit frustum culling, cell bounds, and distance/projected-size LOD. Add baked potentially-visible sets only if draw calls remain a bottleneck. Track-phase-only visibility is unsafe: orbit, reverse travel, jumps, intro/outro, and a nearby nonadjacent track branch can expose other districts. Use camera-aware sets and a conservative fallback for unsupported views. Do not assume buildings automatically occlusion-cull all geometry behind them.
4. **One shadow-casting sun.** Use baked ambient occlusion and texture/vertex shading on distant architecture; only nearby substantial scenery casts dynamic shadows. Reuse the existing sun, evaluating 2048² versus 4096² maps and appropriate coverage through captures. Do not increase the existing shadow footprint to the whole town without measuring lost resolution and cost. Decorative windows and signs use emissive materials without adding real lights.
5. **Opaque detail first.** Model awnings as opaque cloth, use dark opaque window insets, and limit dust/hologram layers and translucent crowd cards. Keep small trim in atlas textures. The race already has transparent trails and particles.
6. **Bounded animation.** Most town objects are static. Add only a few distant moving props after the budget passes; use a lower-rate animation update where acceptable and stop offscreen updates. Spectator density should initially come from simplified static clustered figures, not hundreds of individually animated characters.
7. **Profile existing costs before changing them.** Candidates include batching fence posts/shared materials, lowering shoulder tessellation without changing physics heights, render-only LOD for Marvin's dense CAD mesh, reducing costly trail rebuilds, and dust submission count. Each change needs its own visual/behavior check; none is a prerequisite unless the baseline needs headroom.
8. **Quality fallback.** Reduce distant props, shadow resolution/casters, and particle density before reducing resolution or anti-aliasing. Expose deliberate profiles first. If automatic quality is later added, base it on sustained measured pressure with hysteresis, avoid oscillation, and never change the physics step. A stable optional 30 fps mode can be supported on weaker hardware, but does not satisfy the 60 fps target.

## Implementation sequence and acceptance

1. **Baseline and performance harness.** Add a seeded real-time three-lap route and camera tour. Capture CPU update sections, renderer/presentation intervals, memory, and GPU passes using Instruments/Metal tools when available. Report resolution, quality, hardware, thermals, and percentiles in `benchmark.json`. Existing fixed-timestep smoke tests validate correctness, not wall-clock rendering performance. Gate: repeatable baseline with town disabled.
2. **Town blockout.** Author roads, district masses, finish plaza, and distant silhouettes around the exact course. Add footprint/vertical-clearance validation and a camera obstruction check against simple building bounds. Keep existing barriers initially; in the art pass replace them with themed segments at the same collision boundary. Gate: full drive, reverse/orbit/zoom, aerial fly-in/out, jump landing visibility, and no course intrusion or hidden collisions.
3. **One finished district.** Build the market plus finish-plaza slice with occupied integrated stands, representative animated spectators and street activity, and the complete material, cell batching, LOD, export, and preparation pipeline. Compare town-on/town-off under identical camera and effect loads, including the crowded start and finish. Gate: visual quality and incremental budgets pass before expanding.
4. **Complete settlement.** Populate the remaining districts using the validated kit. Connect alleys and access roads; add authored landmarks, foundation transitions, rooftop spectators, and the distant skyline. Keep aerial proxy assets in the same milestone. Gate: no obvious LOD popping or empty backsides, every district legible from both driving and aerial cameras, memory remains bounded.
5. **Performance and regression pass.** Run full races with all playable characters, maximum late-race trail history, crowded start/contact scenes, pause/resume, reset, focus loss, and repeated mode switching. Run a 15-minute warm soak and compare High/Balanced/Low at 1080p and native pixels. Gate: documented frame-pacing targets, no memory growth across repeated races, existing simulation/smoke tests still pass.

Do not make detailed art for the entire town before the first district demonstrates the target frame rate. The first reviewable implementation should be the real course surrounded by simple town blocks plus a reproducible performance report; the second should be one polished, measured district.

## Validation specifics

- Data checks: deterministic exports, valid references, lot/road overlap, complete footprint clearance, finite bounds, material/triangle budgets, and LOD resource presence.
- Camera checks: all four racer heights; forward/reverse; inner/outer lane; hill crest; boosted jumps; maximum/minimum zoom; orbit sweeps; intro/outro; resets. Test near-plane and camera volumes, not only a center ray. If setbacks cannot solve obstruction, shorten the Dirt Track camera boom against simple scenery bounds with smoothing and bounded work.
- Visual captures: plaza start, market turn, service yard, spaceport jump, ridge skyline, return whoops, overview, and every LOD transition. Check terrain seams, z-fighting, road connections, roof silhouettes, and shadow coverage.
- Performance: separate cold loading from warm driving; fixed seed and input route; compare equivalent screenshots and drawables. Inspect GPU captures for main/shadow pass draws, overdraw, and texture allocations. Run multiple warm samples and keep OS interruption outliers identifiable rather than silently dropping them.
- Behavior: retain current three-lap rules, fixed physics, pause/focus gating, assists, finish order, and trail grounding. Town visuals do not become physics bodies accidentally. If side roads or collisions are later made interactive, treat that as a separate feature with physics and AI changes.
