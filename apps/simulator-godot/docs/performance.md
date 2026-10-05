# Godot port performance: the real gap to the macOS game

Measured on 2026-10-05 on this Mac: Mac mini M2 (8-core CPU, 10-core GPU), 16 GB, macOS 27, a 1x 1920 x 1080 display.
Both games ran the same scenes at the **same drawable sizes**, one GPU-heavy process at a time, back to back and
interleaved (macOS, Godot, macOS, Godot ...). The macOS game is the build in `apps/simulator-macos/.build` (gameplay
sources as of 1c8bb11; the later commits only add diagnostics); the Godot port is this branch with the measurement
tooling of `tools/perf` (commit 6a9f018 and the telemetry committed with this document). Raw runs are in the session
scratchpad, not in the repository; every number below can be reproduced with the commands at the end.

## Summary

- **960 x 540: both games hold 60 FPS** in the town roam and the race (Godot 59.3-59.9 FPS, p99 18.3-19.5 ms, 0-1
  frames over 25 ms per 45 s; macOS 60.0, p99 18.2-20.3 ms, 0-1 frames over 25 ms). Ten minutes of town roaming: both
  60 FPS with no frame over 25 ms. But Godot needs 9.7 ms of GPU per frame where SceneKit needs 7.9 ms, and a single
  main thread that is 66 % busy (11 ms of the 16.7 ms frame) where SceneKit spreads the work over its main thread
  (23 % busy) and its render thread (50 %).
- **1920 x 1080: macOS 60 FPS, Godot 29 FPS (town roam) and 35 FPS (race)**, with 1,200-1,350 frames over 25 ms per
  45 s. Godot is GPU-bound: **22.1 ms of GPU work per frame against SceneKit's 11.0 ms** (Metal System Trace, town
  roam). Even with the GPU to itself Godot could not exceed about 45 FPS at 1080p.
- The C# build is not the problem: the editor runtime's Debug assembly (compiled optimised, as the project has done
  since the performance pass), the C# compiled as ExportRelease and an exported release build (release engine template,
  no editor) give the same frame rates within 1 %. The export uses 150-630 MB less memory.
- **The gap is fragment shading and directional shadows**: Godot's shadows cost about 7.4 ms per 1080p frame
  (SceneKit about 1.4 ms), its opaque and transparent passes shade the same screen 1.6x as expensively even without
  shadows, and its post-processing chain and depth prepass cost about 3 ms more. SSAO costs the same in both.
- **Loading freezes the loading screen for 2.8-3.4 s at 93 %** (macOS: 0.75 s), almost all of it the facade's
  synchronous first flush of 4,884 nodes (2.6 s) and a PNG-encoded snapshot whose image is thrown away (0.3 s).
  Launch to race: 13.1 s against 8.6 s.
- Gameplay transitions are smooth; the "2-15 fps transitions" of the earlier report are the playthrough harness's own
  window captures (260-420 ms each), not the game.
- Memory: 3.8-3.96 GB resident in the editor runtime, 3.30-3.77 GB exported, macOS 3.23 GB. Over ten minutes neither leaks
  (Godot +24 MB, macOS +51 MB). The node count grows by about 1,000 in ten minutes (trail chunks, as on macOS) and the
  .NET heap holds about 1 GB. The main thread allocates 1.6 MB per frame, so the GC runs 12-14 gen0 collections per
  second and pauses 16-19 ms per second.
- **After the CPU pass** (section below): facade flush 1.9 → 1.2 ms per frame, main-thread allocation 1.6 → 0.6 MB per
  frame (gen0 collections 12 → 4.6 per second, GC pauses 17 → 7 ms per second), no new GPU texture per dune update,
  no orphan nodes left by race resets, and the freeze at 93 % 2.8 → 0.3 s (launch to race 15.0 → 12.2 s in the
  loading check). Frame rates are unchanged: vsync-bound at 960 x 540, GPU-bound at 1080p.

### Ranked costs (what separates Godot from SceneKit)

| # | Cost | Godot | macOS | Gap | Evidence |
|---|---|---|---|---|---|
| 1 | Directional shadows (two suns): maps + sampling | 7.4 ms GPU/frame at 1080p (maps 3.1 incl. the atlas clear; sampling makes the opaque and transparent passes 5.0 ms slower, partly overlapping); maps 3.1 ms at 540p | ~1.4 ms at 1080p (two maps 1.8 ms; turning shadows off saves 1.4 ms) | **~6 ms at 1080p** | `--benchmark-no-shadows`: 22.1 -> 14.7 ms, 29 -> 45 FPS. Godot's soft filter alone (SoftHigh vs hard, `MARVIN_SCN_CAL=ShadowFilterQuality=0`) is 3.7 ms. Godot renders four maps (two suns x two splits) into an 8192² atlas and clears the whole atlas every frame (0.34 ms); SceneKit renders one 4096 and one 2048 map. |
| 2 | Scene shading without shadows (opaque + transparent passes) | 9.1 ms at 1080p | 5.7 ms (one forward pass) | **~3.4 ms** | Fill-bound: the opaque pass scales 2.5x and the transparent pass 3x from 540p to 1080p. The opaque cost is the ground: without the town it is unchanged (7.6 ms), with the `townNoise` ground materials made constant it drops by 2.2 ms. The transparent pass is 15 draws of town ground overlays (soil, street, trampled sand, red soil) for 6.6 ms with shadows, 3.9 without; without the town 2.7. |
| 3 | Post-processing, depth prepass and frame overhead | ~4.1 ms (depth prepass 1.0, glow 1.0, tonemap + HUD 0.6, uploads 0.5, MSAA depth resolve 0.5, window blit 0.3, colour resolve 0.2) | ~0.6-1.0 ms (uploads 0.5, bloom and final blit; SceneKit has no depth prepass outside its SSAO pass) | **~3 ms** | Metal System Trace per encoder. |
| 4 | Loading freeze at 93 % (`startDirtTrack` on the main thread) | 2.8-3.4 s | 0.75 s | **2-2.6 s** | `--world-build-profile`: facade node flush 2.07 s + parallel mesh preparation 0.51 s for 4,884 nodes; `revealDirtTrack`'s `view.snapshot()` 0.47 s, of which PNG encoding of the discarded image 0.30 s; GC 0.12 s; game code ~0.2 s. SceneKit prepares in the background (`SCNView.prepare` is asynchronous there). |
| 5 | Main-thread CPU (one thread does everything) | 11 ms/frame at 540p: managed 4.1 ms (game tick 1.6, facade flush 2.1-2.5, ...), Godot's scene and render encoding 3.3 ms, Metal driver 1.5 ms; GC pauses ~1.2 ms/frame on average | main 3.8 ms + SceneKit render thread 8.4 ms (in parallel) | headroom 5.7 ms at 60 FPS | `sample` of both processes; the facade flush grows 2.1 -> 2.5 ms over ten minutes (trail chunks). |
| 6 | Garbage collection | 1.6 MB allocated per frame (tick 1.1 MB: effects 0.6, town 0.36; facade node flush 0.48 MB) -> 12-14 gen0 + ~1 gen1 per second, 16-19 ms paused per second | ARC, no collector | ~1 ms/frame, pauses of ~1.4 ms | `godot-render.json` allocation telemetry |
| 7 | Memory | 3.8-3.96 GB (editor) / 3.30-3.77 GB (export); .NET heap ~1 GB, textures 610 MB (540p) / 796 MB (1080p), buffers ~380 MB | 3.23 GB | +0.1-0.7 GB | process RSS, Godot monitors |
| 8 | Texture churn in the dunes | 49 new ImageTextures + 72 MTLTextures per second in `--dune-roam` (DeformableSand height maps, a new texture per patch update as on macOS); city roam: none after the first minute; race 1.3/s | (SceneKit creates the same MTLTextures) | small | `godot-render.json` `flushed` counters; freed only when the GC finalizes the wrappers |

## CPU pass: per-frame facade work, allocation, texture churn and the loading freeze

Done after the measurement above, without changing the look (see "Look and checks" below). Measured with the same runner
on the same Mac at 960 x 540, the unchanged build (a copy of commit c19fd6f) and the new one run back to back: two runs
each for the town roam and the race, one for the dune roam; means per frame of the 45 s runs (`godot-render.json`).

| | city roam before → after | race before → after | dune roam before → after |
|---|---|---|---|
| facade flush (both flushes of a frame) | 1.89 → 1.19 ms | 1.78 → 1.13 ms | 1.55 → 0.93 ms |
| of which the node flush | 1.47 → 0.93 ms | 1.35 → 0.86 ms | 1.16 → 0.69 ms |
| of which the camera sync (transparent sort, shadow fit) | 0.47 → 0.23 ms | 0.48 → 0.23 ms | 0.43 → 0.21 ms |
| main-thread allocation | 1.64 → 0.60 MB | 1.53 → 0.59 MB | 1.60 → 0.71 MB |
| of which the game tick | 1.10 → 0.27 MB | 1.03 → 0.27 MB | 1.15 → 0.44 MB |
| gen0 collections, GC pauses | 12.4/s, 16.9 ms/s → 4.6/s, 6.9 ms/s | 11.6/s, 17.2 ms/s → 4.5/s, 6.5 ms/s | 12.1/s, 14.9 ms/s → 5.4/s, 7.1 ms/s |
| new GPU textures | 1.3/s → 1.3/s | 1.3/s → 1.3/s | 49.4/s → 1.7/s (47.9 updated in place) |
| FPS (vsync-bound) | 59.78, 59.83 → 59.86, 59.81 | 59.32, 59.66 → 59.37, 59.86 | 60.00 → 60.00 |

These 45 s runs were taken before the last change, the transparent sort's world-space centre cache, which matters once
trail chunks pile up (see "Ten minutes"). The game tick's time is unchanged within the run-to-run spread (roam 1.39 →
1.47 ms, race 1.32 → 1.28 ms): it does the same work, with a quarter of the allocation. At 1920 x 1080 the frame rate stays GPU-bound (roam 29.4 → 29.0 FPS, race
32.1 → 34.1 FPS, within the spread between runs) while the flush drops from 2.60 to 1.66 ms (roam) and 2.38 to 1.50 ms
(race). One of the two new race runs had 19 frames over 25 ms in its first 15 s, all waiting for the GPU in the render
submission (no slow tick or flush, another job's GPU run had just ended); the other new and both old runs had none.

**Ten minutes** (603 s city roam, new build): 59.91 FPS, p99 18.4 ms, 2 frames over 25 ms. The facade flush is 1.30 ms
in the first minute, 1.46 ms around minute 5 and 1.20 ms in the last minute (before: 2.11 → 2.53 ms), because the
transparent sort no longer queries Godot for each of the 500 trail chunks that pile up (camera sync 0.25 → 0.35 ms over
the run; a run without that cache went 0.23 → 0.56 ms). Allocation stays at 0.56-0.68 MB per frame and 4.1-5.0 gen0
collections per second; orphan nodes stay at 3,207 (the scenes not shown); the node count grows from 4,870 to 5,836
(the trail chunks, as before).

**Loading** (`--loading-smoke-test`, main-thread stalls of 50 ms or more): the freeze at 93 % went from **2.82 s to
0.31 s** (`startDirtTrack`: the shadow batch, attaching the world and the robots) plus 0.15 s at the reveal (the first
draw of the race world in `revealDirtTrack`'s snapshot); the loading screen keeps drawing in between. Launch to the end
of the check 15.0 → 12.2 s. Done synchronously (`--world-build-profile`), `startDirtTrack` takes 1.41 s instead of
3.36 s: node flush 2.14 → 0.57 s, the snapshot's PNG 0.33 s → 0. First frames after the reveal 73, 47 → 70, 32 ms.

**What changed:**

1. *Facade flush* (`SceneKitRuntime`, `SCNNode`): engine calls only for changes. Nodes outside every scene (the race's
   1,600 pooled particle slots, which SceneKit never draws either) keep their transform and visual changes until they are
   added; that alone removed about 250 transform pushes and 190 visual updates per frame. A node's transform is recomputed
   only when its model values changed (the game reads positions through the `ref` getters, which mark the node), and the
   dirty set is mirrored on the node so those reads skip the dictionary. Mesh instances get cast-shadow, layer,
   transparency and visibility only when they change (geometry rebuilds re-applied them every frame, and a cast-shadow
   write updates the instance's light pairing). The transparent sort reads one global transform per node with cached box
   centres and offsets, tests scenes instead of nodes for being in the tree, and is skipped in the frame's second flush
   when nothing moved. The constraint loop no longer copies the set, the SSAO passes reuse their uniform sets (keyed by
   shader and textures, rebuilt when one is freed), `isNode(_:insideFrustumOf:)` allocates nothing, and a material
   argument set to the bits it already has is not re-sent.
2. *Meshes and snapshots* (`SCNGeometry`, `NSImage`): mesh arrays are compacted with an index array instead of a
   dictionary and without intermediate copies (the same arrays: compared for all 2,937 geometries of the race world, the
   robots and the sandbox), geometry sources and elements are packed with one copy, a new geometry's default material is
   created only when read (per-frame batches replace it at once), and snapshots keep their pixels instead of a PNG
   encode and decode (lossless for the RGBA8 they are).
3. *Loading* (`SceneKitRuntime.PrepareAsync`, `NSImage`): `prepare(_:completionHandler:)` is asynchronous as in
   SceneKit: mesh arrays and procedural textures (the per-pixel conversion and mipmaps) are prepared on worker threads and
   the main thread hands ready nodes to Godot for 10 ms per frame. The town's scanned 2048² textures (30-140 ms each to
   decode, 1.4 s in all, inside single nodes' flushes) start loading on Godot's loader threads when a material names
   them, during the background world build.
4. *Game code* (PORT comments; same values in the same order): debris batches and trail marks reuse their vertex lists
   and write their literals without temporary arrays; `TownResidents` and `TownStreetResidents` replaced the per-frame
   LINQ closures and concatenated lists with loops over reused lists; `RobotCollisions.contact` keeps its axes on the
   stack and `CityCollisionWorld.nearby` uses per-thread scratch lists (`tools/checks` is byte-identical);
   `TownShadowBatch` presizes its weld tables.
5. *Texture churn*: `DeformableSand` keeps one height texture per patch and updates it in place (49 new GPU textures per
   second over the dunes before). `DirtCoating` keeps SceneKit's new texture per upload: Marvin's and R2-D2's coatings
   rely on SceneKit not showing new contents for primitive geometries (PORTING.md, Known deviations), which in-place
   writes would change.
6. *Node growth*: the +3 nodes per second while racing are DirtTrail's chunk nodes, as on macOS (one chunk node and its
   mesh instance per filled chunk, at most 128 chunks per racer), not a leak. A real leak was found: removed nodes are not
   freed in Godot, so every race reset left the old trail chunks behind (100-130 orphan nodes and 150 objects per reset
   after 30 s of racing, measured over four resets). `SCNNode.releaseRemoved()` frees what the Swift code drops: trail
   chunks and sand patches on a reset or eviction and the sandbox's rebuilt floor details; the counts now stay flat.

**Look and checks.** Every capture mode below was run twice on the unchanged build (to know the noise of each image) and
once on the new one: `--facade-test` (all images and measurements.json identical), `--hud-smoke-test`, pinned
`--town-smoke-test`, pinned `--smoke-test` (smoke.json, full-race-trails.json, menu-smoke.json identical),
`--trail-material-smoke-test`, `--debris-smoke-test`, `--dune-contact-test`, `--sandstorm-smoke-test`,
`--character-smoke-test`, `--people-smoke-test`, `--dust-visibility-test`, `--navigation-smoke-test` (pinned grid and
daylight), `--loading-smoke-test`, `--entrance-smoke-test`, `--passage-smoke-test`, pinned `--city-escape-smoke-test` and
`--postrace-smoke-test`, `--visual-regression-test`, `--binary-sky-smoke-test` (daylights from the macOS run),
`--ground-performance-test`, `--mesh-reuse-test`, `--shadow-culling-test`, `--viewport-smoke-test`,
`--weather-reset-test`, `--menu-smoke-test`, `--audio-smoke-test`. Every JSON report of a deterministic mode is
byte-identical (characters, people, street-people, navigation, entrances, passages, preflight, city-escape, postrace,
visual-regression, binary-races, viewport, menu, town and smoke reports, the ground-performance, mesh-reuse and
shadow-culling comparisons, audio.json in a run whose unseeded voice director drew the same count; the mean error of the
comparisons' uncovered-infield views follows the random starting grid and varies between two old runs too); the others differ in timing fields and random draws exactly as
two runs of the unchanged build do. Images: passage (66), visual regression (43), facade, trail, menu and loading are
pixel-identical; the rest are within the unchanged build's own run-to-run spread (largest deterministic difference
0.08/255 in the town smoke, noise 0.08/255); the random modes (character races, dust, weather resets, dirty robots with
their dust plumes) differ as two old runs do. A first version deferred the mesh builds of nodes outside every scene too,
which changed 50 z-fighting pixels in one ground-performance view (resource creation order decides Godot's order among
equal-depth surfaces); it now builds them in the old order and that report is identical again. After the last change
(the sort's centre cache) the facade, town, smoke, trail, people, dust, navigation, entrance, passage, escape, post-race,
visual-regression and ground-performance modes were run once more, with the same result. A temporary check that ran the
old and the new mesh preparation side by side found identical arrays for all 2,937 geometries.

**Not done.** Godot's own scene and render encoding (~3.3 ms) and the Metal driver (~1.5 ms) still run on the main
thread; a separate render thread (Godot's threading model) was not tried. The remaining loading stalls are game code
(`TownShadowBatch` welds 1.8 million vertices, 0.16 s) and the first draw of the race world (0.12-0.17 s).

## Method

**Same drawable, same conditions.** The macOS benchmark asks for a 960 x 540-point window (TownSmoke.swift: a 1080p
drawable on a Retina screen), which is a 960 x 540 drawable on this 1x display, and AppKit keeps titled windows inside
the visible frame, so not even a 1920 x 1080-point window fits. `tools/perf/window-inject.m`, loaded with
`DYLD_INSERT_LIBRARIES` (the macOS game is not modified; its ad-hoc signed binary allows it, as does the Godot editor
with `com.apple.security.cs.allow-dyld-environment-variables`), turns that request into an exact 960 x 540 or
1920 x 1080-point window at the bottom left of the screen, at the floating window level. Godot runs at backing scale 1
(no `MARVIN_BACKING_SCALE`), so both games draw 1x HUDs over a drawable of exactly that size (confirmed by
`benchmark.json`'s `drawableWidth/Height` and the 1920 x 1080 `final-fps.png`). A black opaque backdrop window covers
the rest of the screen: a MarvinSimulator left running on this Mac presented 60 frames per second with 400-500
GPU-ms per second while visible; the macOS game's opaque 1080p window occluded it, Godot's window did not, which made the
first Godot run look worse than it was. With the backdrop it presents nothing in any run, but Metal System Trace still
shows it using 170-220 GPU-ms per second, and WindowServer 10-160, in every run of both games: absolute numbers are
pessimistic, the comparison is fair.

**Scenes.** `--town-benchmark` (TownSmoke.swift and its port: same flags, same 45 s, daylight 0.5) in its default race
mode (four racers on the track, chase camera with an overview every 24 s) and `--city-roam` (Marvin driving a fixed route
through the town). The race world is built during the launch in both (8.7 s from launch to the benchmark's start on
macOS, 12.7 s Godot editor runtime, 12.9 s export).

**What is recorded** (`tools/perf/run-benchmark.py`): the benchmark's own files (`benchmark.json`: rendered-frame
intervals; `timeline.json`: CPU time per tick phase); `metalperftrace listen` (Apple's always-on Metal statistics, no
HUD overhead): presented FPS, on-GPU walltime per frame, frame-on-glass intervals, and the other processes' GPU time;
process RSS and CPU per second; main-thread stalls (`main-stalls.m`, a run-loop timer in both apps); and for Godot
`godot-render.json` (per second: draw calls, nodes, objects, texture/buffer/heap memory, GC counts and pauses, CPU and
allocation per facade flush stage and per tick phase, textures created). **GPU per pass**: a 4 s Metal System Trace
(`xctrace`, 30 s after launch) with `metal-labels.m` loaded, which names every Metal encoder by its attachments
(format, size, MSAA, depth load/clear), draw count and compute pipelines. Godot's Metal driver sets no encoder labels
or debug groups and its RenderingDevice timestamps are zero on Metal, so neither Godot's visual profiler nor the trace's
default "Render Command N" names identify passes. `mst-gpu.py` sums each label's GPU intervals and the union of all of
a process's intervals (vertex, fragment and compute overlap on Apple GPUs). Two GPU numbers appear below:
**GPU busy** (MST union, the work itself) and **on-GPU walltime** (metalperftrace, first to last command buffer of a
frame, which includes waiting behind other processes and between command buffers; Godot's is about 1.6x its busy
time, SceneKit's 1.1-1.2x).
**CPU**: `sample` of all threads of both apps for 8 s, `dotnet-trace` (managed stacks), and the Stopwatch telemetry.

## Frame rate at the same drawable size

45 s per run; two interleaved rounds for macOS and the Godot editor runtime, one for the Godot builds with release C#
and the export. "GPU walltime" is metalperftrace's on-GPU walltime per presented frame; "tick" is the benchmark's CPU
update per tick (p95); "render span" Godot's render submission including the wait for a drawable (SceneKit's is its
callback span).

| build | mode | drawable | runs | FPS | p50 ms | p95 ms | p99 ms | > 25 ms | GPU walltime ms | tick p95 ms | render span p50 ms | RSS MB |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| macOS | city roam | 960x540 | 2 | 59.99 | 16.67 | 17.55 | 18.19 | 0-1 | 8.97 | 3.07 | 1.78 | 3226 |
| Godot editor (Debug C#, optimised) | city roam | 960x540 | 2 | 59.85 | 16.67 | 17.40 | 18.33 | 0 | 15.17 | 3.31 | 12.64 | 3932 |
| Godot editor, ExportRelease C# | city roam | 960x540 | 1 | 59.81 | 16.67 | 17.43 | 18.56 | 2 | 15.39 | 3.31 | 12.72 | 3893 |
| Godot export (release template) | city roam | 960x540 | 1 | 59.81 | 16.68 | 17.38 | 18.87 | 0 | 15.36 | 3.48 | 12.84 | 3301 |
| macOS | race | 960x540 | 2 | 60.00 | 16.67 | 17.61 | 20.26 | 0 | 9.45 | 5.02 | 2.09 | 3224 |
| Godot editor | race | 960x540 | 2 | 59.26 | 16.74 | 18.29 | 19.46 | 0-1 | 15.19 | 3.51 | 13.21 | 3773 |
| Godot editor, ExportRelease C# | race | 960x540 | 1 | 59.83 | 16.70 | 17.50 | 18.75 | 1 | 15.56 | 3.31 | 12.96 | 3913 |
| Godot export | race | 960x540 | 1 | 59.68 | 16.69 | 17.80 | 19.00 | 0 | 15.02 | 3.67 | 13.06 | 3431 |
| macOS | city roam | 1920x1080 | 2 | 60.00 | 16.65 | 17.52 | 18.42 | 0 | 13.16 | 3.23 | 1.80 | 3225 |
| Godot editor | city roam | 1920x1080 | 2 | 29.05 | 34.46 | 38.66 | 42.88 | 1214-1226 | 35.15 | 5.18 | 27.60 | 3838 |
| Godot editor, ExportRelease C# | city roam | 1920x1080 | 1 | 28.94 | 34.68 | 38.72 | 42.77 | 1216 | 35.22 | 5.21 | 27.69 | 3824 |
| Godot export | city roam | 1920x1080 | 1 | 28.98 | 34.52 | 38.81 | 43.06 | 1217 | 35.19 | 5.76 | 27.53 | 3703 |
| macOS | race | 1920x1080 | 2 | 60.00 | 16.66 | 17.49 | 20.30 | 0-1 | 11.99 | 4.53 | 2.10 | 3223 |
| Godot editor | race | 1920x1080 | 2 | 34.79 | 28.19 | 34.47 | 36.07 | 1306-1345 | 29.39 | 3.99 | 22.77 | 3840 |
| Godot editor, ExportRelease C# | race | 1920x1080 | 1 | 34.41 | 28.41 | 34.15 | 36.01 | 1346 | 30.37 | 4.21 | 23.08 | 3859 |
| Godot export | race | 1920x1080 | 1 | 34.27 | 28.52 | 34.51 | 36.45 | 1240 | 29.86 | 5.04 | 22.85 | 3767 |

Release C# vs the editor's Debug assembly: no difference, because `MarvinGodot.csproj` compiles every configuration
with `<Optimize>true</Optimize>`. The exported release build (Godot 4.7.2 release template, no editor, ExportRelease C#)
runs the facade flush 11 % faster (1.87 vs 2.10 ms) and the camera sync twice as fast (0.23 vs 0.5 ms; Godot's editor
binary validates more), and uses 150-630 MB less memory, but the frame rates are the same: at 540p both are
vsync-bound with headroom, at 1080p both are GPU-bound. The earlier PORTING.md figure (31.7-32.1 FPS at 1920 x 1050
with `MARVIN_BACKING_SCALE=2`) was a smaller drawable with the HUD at 2x; this is the full 1920 x 1080 at 1x.

## GPU per pass

Metal System Trace, town roam, 4 s each; GPU busy per frame (ms) by encoder, summed into passes. The labels come from
`metal-labels.m` (attachments, draw counts, pipelines).

| pass | Godot 960x540 | macOS 960x540 | Godot 1920x1080 | macOS 1920x1080 |
|---|---|---|---|---|
| **total GPU busy per frame** (union of all encoders) | **9.73** | **7.91** | **22.08** | **11.03** |
| shadow maps | 2.84 (four maps, D16 8192² atlas) + 0.29 atlas clear | 2.19 (D32F 4096² + 2048²) | 2.71 + 0.34 clear | 1.81 |
| opaque pass (RGBA16F, 2x MSAA, ~405 draws) | 2.97 | 4.13 (one forward pass, opaque + transparent, ~670 draws) | 7.54 | 6.44 |
| transparent pass (15 draws: ground overlays, trails) | 2.22 | (in the forward pass) | 6.60 | (in the forward pass) |
| depth prepass (+ normal/roughness, ~405 draws) | 0.84 | - | 0.96 | - |
| SSAO | 0.58 (depth resolve + SceneKit's kernels, compute) | 2.28 normal/depth pass + 0.6 | 2.55 | 3.17 normal/depth pass + 2.1 |
| glow / bloom | 0.31 | (with SSAO above) | 1.04 | (with SSAO above) |
| tonemap, HUD, resolves, uploads, window blit | 0.40 | 0.25 | 1.68 | 0.60 |

The per-pass sums exceed the union slightly where passes overlap. Toggling features (diagnostics only, they change the
look) separates the costs further (Godot at 1080p, town roam):

| variant | FPS | GPU busy ms/frame | opaque pass | transparent pass | shadow maps |
|---|---|---|---|---|---|
| base | 29.4 | 22.1 | 7.54 | 6.60 | 3.06 |
| `--benchmark-no-shadows` | 45.4 | 14.7 | 5.18 | 3.94 | 0 |
| hard shadow filter (`MARVIN_SCN_CAL=ShadowFilterQuality=0`) | 36.7 | 18.4 | 5.63 | 4.64 | 3.07 |
| `--benchmark-no-ssao` | 31.0 | 20.3 | 7.66 | 6.75 | 3.16 |
| `--benchmark-flat-ground` (`townNoise` materials constant) | 37.6 | 18.0 | 5.35 | 4.62 | 3.02 |
| `--without-town` | 41.0 | 18.4 | 7.62 | 2.67 | 3.14 |

macOS at 1080p: `--benchmark-no-ssao` 11.0 -> 8.8 ms (its SSAO costs 2.2 ms, Godot's port 1.8-2.1 ms);
`--benchmark-no-shadows` 11.0 -> 9.6 ms (1.4 ms). So the 11 ms gap at 1080p is shadows ~6 ms, scene shading ~3.4 ms
and the post chain with the depth prepass ~3 ms (the passes overlap a little, so the parts add up to slightly more).
At 540p the gap is 1.8 ms and SceneKit is relatively heavier in vertex work (2.5 ms of
vertex shading in its forward pass and 1.8 ms in its SSAO pass against Godot's 0.9 and 0.7): Godot is fill-bound,
SceneKit vertex-bound, so Godot's cost grows with the pixel count (x2.3 from 540p to 1080p, SceneKit x1.4).

## CPU per subsystem

960 x 540 town roam (both at 60 FPS). Per frame of 16.7 ms:

| | Godot (one main thread) | macOS |
|---|---|---|
| game tick (physics, opponents, effects, camera, town, audio update) | 1.57 ms mean (physics 0.23, models 0.08, effects 0.60, camera 0.08, town 0.59) | 1.96 ms (physics 0.24, models 0.15, effects 0.62, camera 0.05, town 0.90) |
| facade flush (SceneKit state -> Godot) | 2.1-2.5 ms: node flush 1.6-1.75 (~4.6 trail meshes rebuilt per frame, ~750 nodes flushed), camera sync 0.5-1.1 (shadow fit, transparent sort), constraints 0.14 (2,134 per frame), materials 0.03 | (SceneKit internal) |
| scene processing and render encoding | Godot native ~3.3 ms + Metal driver/IOKit ~1.5 ms, on the main thread | SceneKit render thread 8.4 ms (50 % busy), in parallel with the main thread |
| GC | ~1.2 ms on average (12-14 gen0 + ~1 gen1 collections per second, 16-19 ms paused per second) | - |
| waiting for the next drawable (vsync) | 5.4 ms (32 %) | main thread busy 23 % (3.8 ms), the rest waiting for SceneKit's render lock or idle |
| audio | C# mixer thread 6 % of a core | AVAudioEngine IO thread 39 % of a core (varispeed resampling) |
| process CPU | 59-62 % of one core | 87-98 % |

Godot renders on the main thread (single-threaded render model), so its main thread carries the game, the facade and
the engine: 66 % busy at 60 FPS, 5.7 ms of headroom. The game logic itself is as fast as Swift's. Allocation per frame
on the main thread: 1.6 MB, of which the tick 1.1 MB (effects 0.61: trails, dust and debris rebuild their geometry
sources; town 0.36; physics 0.07; camera 0.06) and the facade's node flush 0.48 MB (new mesh arrays); a gen0
collection every 4-5 frames pauses the main thread about 1.4 ms.

## Memory and node counts over ten minutes

`--town-benchmark --city-roam`, 603 s, 960 x 540, both games:

| | Godot (editor runtime) | macOS |
|---|---|---|
| FPS, p99, frames over 25 ms | 59.92, 18.27 ms, 0 | 60.00, 17.83 ms, 0 |
| resident memory, minute 1 -> 10 | 3,956 -> 3,980 MB | 3,227 -> 3,278 MB |
| nodes | 4,870 -> 5,876 (+3/s in the first minute, +0.4/s in the last): 503 trail chunks | (not exposed) |
| Godot objects | 15.8 k -> 16.7 k, flat after minute 4 | |
| .NET heap | 1.9 GB after loading, 1.05 GB after the first gen2 collection (minute 1), then flat | |
| texture / buffer memory | 610 MB flat / 368 -> 386 MB | |
| facade flush | 2.11 -> 2.53 ms per frame | |
| draw calls, primitives | 1,270-1,480, 4.0-4.5 M (incl. shadow passes), not growing | |
| GC | gen0 12.9-14.2/s, gen1 0.9-1.5/s, gen2 about one per two minutes | |
| ImageTextures created | 112 in minute 1 (loading the robots and the dunes), 0 afterwards | |

The node growth is DirtTrail's chunk nodes, one per filled trail chunk and racer, exactly as DirtTrail.swift creates
SCNNodes: the trails' own diagnostics count 7 -> 510 chunk nodes, and every facade SCNNode with geometry is a Node3D with
a MeshInstance3D child, so 503 chunks are the 1,006 nodes. It stops at 128 chunks per racer (32,768 marks). Nothing
leaks. The earlier report's "+3 nodes/s during races"
is this (race and roam both add 3 nodes/s in the first minute), and its "ImageTexture churn from
DirtCoating/DeformableSand" happens only on sand: `--dune-roam` creates 72 MTLTextures and 49 ImageTextures per second
(DeformableSand's 67 x 67 height map per patch update, a new texture each time as on macOS; DirtCoating's 2 x 1
contact texture), the race 1.3 per second, the city roam none. Each is a new RenderingDevice texture that is freed only
when the GC finalizes the C# wrapper; the dune roam still runs at 60 FPS.

The earlier PORTING.md ten-minute run dropped to 54-55 FPS after three minutes; with the window kept on top over a
black backdrop neither game drops, so that drop was most likely the GPU load of the MarvinSimulator left running in
view (it presents nothing while covered), not the port.

## Loading and transition hitches

**Loading** (`--loading-smoke-test`: main menu -> loading screen -> race world built on a background queue ->
`startDirtTrack` -> `revealDirtTrack`; two runs each, main-thread stalls of 50 ms or more):

| | Godot | macOS |
|---|---|---|
| launch to race shown | 13.1 s | 8.6 s |
| freeze before the first frame (engine, .NET, app launch) | 0.4 + 1.7 s | 1.0 s |
| freeze at 93 % ("Getting ready to race") | **2.79-2.81 s** (playthrough: 2.87 s frame, 3.41 s run-loop gap) | **0.75 s** |
| responsive ticks during loading | 423-431 | 387-390 |

`--world-build-profile` (the same work on the main thread, two runs): building the race world takes 6.7 s (on a
background queue in the game; the progress bar moves at 60 FPS meanwhile) and `startDirtTrack` 3.28-3.29 s:

| part of `startDirtTrack` | time |
|---|---|
| facade flush of 4,884 nodes in `view.prepare` (meshes handed to Godot, instances, materials) | 2.06-2.08 s |
| mesh arrays prepared on all cores (`PrepareMeshes`) | 0.51 s |
| `revealDirtTrack`'s `view.snapshot()`: first draw of the race world 0.11 s, GPU read-back 0.05 s, **PNG encoding of the discarded image 0.29-0.31 s** | 0.47 s |
| GC during it (139 gen0, 47 gen1, 2 gen2) | 0.12 s |
| game code (shadow batch, robots, reset, camera) | ~0.2 s |

The first frames after the reveal take 72-73 ms and 40-44 ms (pipelines and uploads), then 21-25 ms at 1280 x 820.
SceneKit's `SCNView.prepare(_:completionHandler:)` compiles and uploads on a background thread, which is why its
loading screen keeps drawing; the facade's `prepare` is a synchronous `Flush()`.

**Transitions** (`--playthrough` at 1280 x 820, every drawn frame's interval per step, `playthrough-frames.json`):
apart from the loading freeze, every frame over 100 ms belongs to a step that takes a window capture or composites a
smoke image (260-420 ms each: synchronous `RenderIsolated` + PNG); those are the "2-15 fps transitions" of the earlier
report, not gameplay. The gameplay transitions themselves are smooth: intro -> countdown -> race, P/Esc, the race
finish -> overview, ⌘R, ⌘M, menu -> sandbox have worst frames of 19-73 ms. Frame rates at 1280 x 820: race 49-53 FPS,
intro 42, finish overview 36 (GPU-bound views of the whole town), sandbox and menus 60.

## What this means for optimisation (not done here)

In order of payoff at 1080p, each to be checked against the captures (the rule for all optimisations: the look must not
change):

1. Shadows (~6 ms): render only what SceneKit renders (one map per sun instead of two splits each, or the second split
   only when the fixed box needs it, as now, but with a smaller atlas region), stop clearing the full 8192² atlas, and
   reproduce SceneKit's penumbra with fewer taps (Godot's SoftHigh is 3.7 ms more than its hard filter; SceneKit's own
   kernel is a fixed set of taps x `shadowRadius`, PORTING.md "Not resolved").
2. Scene shading (~3.4 ms): the ground (terrain and the transparent town overlays) is shaded with full lighting and
   shadowing per overlay layer; materials that SceneKit shades as `.constant` or whose result is covered could skip
   Godot's light loop (`render_mode unshaded` where the composer already writes the final colour), and the overlays could
   share one lit evaluation.
3. Post chain and prepass (~3 ms): glow, tonemap and the 2D pass at full resolution, the depth prepass, two MSAA
   resolves.
4. Loading freeze (2-2.6 s): hand meshes to Godot over several frames or from a worker thread while the loading screen
   draws, and do not encode a PNG for snapshots whose image is discarded. *Done in the CPU pass (2.8 → 0.3 s).*
5. CPU: a separate render thread (Godot's thread model) would give the main thread back ~5 ms; allocation in the trails,
   dust/debris rebuilds and the node flush drives the GC. *Allocation and the flush done in the CPU pass; the render
   thread not tried.*

## Reproduce

```sh
cd apps/simulator-godot
tools/perf/build-probes.sh                       # window-inject, metal-labels, main-stalls dylibs -> $TMPDIR/marvin-perf
# like-for-like runs (macOS game unmodified; Godot editor runtime; --app export --export-binary PATH for an export)
tools/perf/run-benchmark.py OUT --app macos --size 1920x1080 --mode city-roam
tools/perf/run-benchmark.py OUT --app godot --size 1920x1080 --mode race
tools/perf/summarize-runs.py --label-from-name RUN_DIRS...
# GPU per pass: labelled Metal System Trace 30 s after launch, then per-encoder sums
tools/perf/run-benchmark.py OUT --app godot --size 1920x1080 --labels --trace 30:4
tools/perf/mst-gpu.py OUT/metal-system.trace --process Godot
# CPU: managed stacks (dotnet-trace) and a native sample of all threads
tools/perf/profile-cpu.sh OUT 25 15 --sample -- --app godot --size 960x540
tools/perf/speedscope-top.py OUT/cpu.speedscope.json 40 all
# loading and transitions
tools/perf/run-benchmark.py OUT --app godot --flag=--loading-smoke-test
tools/godot --audio-driver Dummy -- --world-build-profile OUT          # world-build-profile.json: startDirtTrack breakdown
tools/perf/run-benchmark.py OUT --app godot --flag=--playthrough --godot-args="--audio-driver Dummy"   # playthrough-frames.json
# ten minutes
tools/perf/run-benchmark.py OUT --app godot --mode city-roam --seconds 603
# exports (Godot 4.7.2 .NET export templates in ~/Library/Application Support/Godot/export_templates/4.7.2.stable.mono)
mkdir -p build/macos build/windows
tools/godot --headless --export-release "macOS" "build/macos/Marvin Simulator.app"
tools/godot --headless --export-release "Windows Desktop" build/windows/MarvinSimulator.exe
```

`run-benchmark.py` needs `metalperftrace` (macOS 27) and, for `--trace`, Xcode's `xctrace`; `profile-cpu.sh` needs
`dotnet tool install -g dotnet-trace`. Run one GPU-heavy process at a time. The export presets (macOS universal,
ad-hoc signed with the JIT and DYLD entitlements .NET and the probes need; Windows x86_64) are in
`export_presets.cfg`; the export reads the raw `.wav` files like the editor run (`tools/sync-assets.py` keeps them).
