# Godot port performance: the real gap to the macOS game

Measured on 2026-10-05 on this Mac: Mac mini M2 (8-core CPU, 10-core GPU), 16 GB, macOS 27, a 1x 1920 x 1080 display.
Both games ran the same scenes at the **same drawable sizes**, one GPU-heavy process at a time, back to back and
interleaved (macOS, Godot, macOS, Godot ...). The macOS game is the build in `apps/simulator-macos/.build` (gameplay
sources as of 1c8bb11; the later commits only add diagnostics); the Godot port is this branch with the measurement
tooling of `tools/perf` (commit 6a9f018 and the telemetry committed with this document). Raw runs are in the session
scratchpad, not in the repository; every number below can be reproduced with the commands at the end.

Three passes followed on the same day, each on its own branch from c19fd6f: the CPU pass and the GPU optimisation pass
(sections below) and the release builds (PORTING.md, "Release builds and Windows"). "All passes merged" measures the
branch with all three replayed onto it against c19fd6f, like for like. After that, the game switched to hard shadows and
mesh LODs for the robots' camera images, two small look changes accepted for frame rate at 1080p ("Hard shadows and
camera mesh LODs"); every measurement before that section is of the exact-SceneKit configuration, which
`MARVIN_SCN_CAL=Exact` still selects. On 2026-10-06 the hard filter got finer splits next to the camera ("Near shadow
splits"), paid for by no longer casting the race world's flat base terrain. The two configurations are now a setting
(Settings > Shadow quality: Fast, the default, or Exact; PORTING.md, "Known deviations"), which applies to the game
launched normally; game modes, the benchmarks included, ignore the stored choice and measure Fast unless
`MARVIN_SCN_CAL` says otherwise. Later that day the procedural ground and its overlays were made cheaper without changing
a pixel's worth of the look ("Ground and overlays").

## Summary

- **Ground and overlays** (2026-10-06; section below, like for like against eb73a04 built from its own tree): the
  town's world-space value noise reads its lattice hashes from a table the GPU fills once with the same expression
  instead of evaluating four sines per call, and the ground's shader modifiers skip work whose result is exactly known
  (`atan`, the pigment, the dune colour, the wind tongues and a texture sample where their weight is exactly 0 or 1).
  At 1920 x 1080 (editor runtime, interleaved rounds) the town roam runs at 48.6 instead of 46.2 FPS in the default
  Fast configuration (GPU walltime 22.2 -> 21.0 ms per frame) and at 35.7 instead of 34.5 in Exact; the race at 52.9
  instead of 52.3 in Fast (GPU busy 17.6 -> 16.9 ms). The Exact race shows no measurable gain: 41.6 against 42.4 FPS over
  five rounds whose spreads overlap (39.7-43.2 and 41.4-43.4), with the same GPU busy time in its traces and no loss in
  the frozen race views; the moving race's content depends on the frame timing.
  The captures and reports are unchanged (in 119 of 388 captures single pixels one 8-bit step off, at most 0.0018 % of
  a capture: last-bit differences of the reordered arithmetic). Most of the town's remaining ground cost is lighting:
  every overlay layer is lit in full with both
  suns' shadows, and in a street-level town view the trampled sand alone costs about 3.5 ms and the streets 1.8 ms of the
  transparent pass.

- **Shading and post** (2026-10-06; section below, like for like against eb73a04, two interleaved rounds): the
  composer's `light()` was inlined by Godot into its omni and spot light loops, which never run in the game, and carried
  deferred-shadow and LDR code the race world never uses; scenes now get shaders without what they lack, the sky-light
  lookup uses polynomials instead of Metal's `atan2`/`acos`, and `light()` finds a light's shadow box by an id. At 1920
  x 1080 the city roam runs at 51.6 instead of 46.4 FPS and the race at 56.7 instead of 52.2 (GPU walltime 22.0 -> 19.7
  and 19.3 -> 17.3 ms per frame); `MARVIN_SCN_CAL=Exact` 34.6 -> 37.9 and 40.0 -> 47.8 FPS. The look is unchanged:
  images identical or a few pixels one 8-bit level off, reports byte-identical. The post chain, the depth prepass and
  Godot's MSAA resolves were measured and left (Godot's two depth resolves, 0.6 ms, are redundant here but unconditional
  in the engine).
- **Near shadow splits** (2026-10-06; section below, like for like against the earlier hard-shadow fit on one build):
  under the hard filter the suns' shadow maps have four splits ending 12, 30 and 58 m from the camera and at the far end
  of the suns' box, so the texels next to the camera are 1.2 x 0.6 cm instead of 2.9 cm and do not crawl while the
  camera moves; the race world's flat base terrain no longer casts (it shadows nothing visible), which pays for the extra
  splits. At 1920 x 1080 the race runs at 52.1 instead of 51.1 FPS and the town roam at 46.2 instead of 45.0, 0.5 and
  0.7 ms less GPU walltime per frame (measured after the MarvinSimulator that shared the GPU in the earlier sections had
  exited), on one build with the earlier fit selected. Against 38818a3 itself (the commit before) the roam's gain
  reproduces (45.1 -> 46.2 FPS) but the moving race's does not: 53.4 -> 52.5 FPS over four interleaved rounds, within
  their spread (section below). Close views lose the earlier fit's blurred 2.9 cm blocks; the texels next to the camera
  are 2.4-4.8 times smaller but hard-edged, so where a pixel is a millimetre or two (race-light and dune-contact close-ups,
  a walker's feet, the base of a wall) they still show as small crisp stair-steps. Edges are sharper than SceneKit's
  soft penumbrae, so the full-game comparison moves from 1.98 to 2.06/255 from macOS. `MARVIN_SCN_CAL=Exact` is
  unchanged.
- **Hard shadows and camera mesh LODs** (the default since ef7b4e6; section below, like for like against 3cb6567, the
  exact-SceneKit configuration): at 1920 x 1080 the town roam runs at 39.2 FPS instead of 29.7 and the race at 46.9
  instead of 34.0 (editor runtime; the export 40.3 / 46.8 against 30.2 / 35.4), with 5.3-5.8 ms less GPU work per frame
  (busy 22.9 -> 17.6 ms roam, 21.9 -> 16.1 ms race) and 1,000 instead of 1,250 (roam) and 230 instead of 1,260 (race)
  frames over 25 ms per 45 s. At 960 x 540 both hold 60 FPS, with about 1 ms less GPU walltime per frame. The look
  changes where shadows are seen close up: hard edges one Godot texel wide (1.3-2 cm in the race views) instead of
  SceneKit's 11.5-16 cm penumbrae, so close views show the shadow map's texels as stair-steps; the full-game comparison
  moves from 1.77 to 1.96/255 from macOS on average. `MARVIN_SCN_CAL=Exact` restores the exact look and its cost.
- **All passes merged, like for like against c19fd6f** (section below; the look is unchanged: captures identical,
  within 0.005/255 or within the random modes' own spread): at 1920 x 1080 the race runs at 37.2 FPS instead of 34.4 and the town roam at 29.9 instead of 28.6 (editor runtime;
  the export 37.2 / 30.0 against 35.6 / 29.4), with 1.2 ms less GPU work per frame (busy 22.2 -> 21.0 ms race,
  23.5 -> 22.7 ms roam) and 4.2 / 3.3 M primitives instead of 5.3 / 4.2 M. At 960 x 540 both builds hold 60 FPS; the
  merged one has a slightly lower p99 (17.9-18.8 ms against 18.1-19.0), a facade flush of 1.2-1.4 ms instead of
  1.6-2.0, 0.6 MB of allocation per frame instead of 1.5-1.7 and 7-9 ms of GC pauses per second instead of 17-19. Loading:
  launch to the end of the loading check 13.0 -> 10.8 s, the freeze at 93 % 2.5-2.7 s -> 0.3 s plus 0.1 s at the
  reveal. Ten minutes of city roam: 59.97 FPS, 2 frames over 25 ms, flush flat at 1.2-1.6 ms, no orphan nodes.
  The gap to macOS at 1080p (60 FPS, 11 ms GPU) is still mostly Godot's renderer: shadows, scene shading and the post chain.

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
- **GPU optimisation pass** (section below; the look unchanged): the SSAO port reads its depth mips from a one-channel
  texture (bit-identical, 0.75 ms less per 1080p frame) and the robots' CAD meshes cast their shadows from mesh LODs
  (camera images unchanged). The race at 1080p now runs at about 36 FPS (33 before) with 3 ms less GPU work per frame,
  the town roam at 29.7 (29.1). Most of what remains is Godot's renderer itself: the soft-shadow path (~4 ms over a hard
  filter, whatever the tap count), its fixed passes, and per-fragment occupancy in the composed materials.
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

Measured with the exact-SceneKit configuration (SoftHigh shadows); the game's default hard filter removes most of the
sampling cost in row 1 (about 4.5-5 ms at 1080p, "Hard shadows and camera mesh LODs").

| # | Cost | Godot | macOS | Gap | Evidence |
|---|---|---|---|---|---|
| 1 | Directional shadows (two suns): maps + sampling | 7.4 ms GPU/frame at 1080p (maps 3.1 incl. the atlas clear; sampling makes the opaque and transparent passes 5.0 ms slower, partly overlapping); maps 3.1 ms at 540p | ~1.4 ms at 1080p (two maps 1.8 ms; turning shadows off saves 1.4 ms) | **~6 ms at 1080p** | `--benchmark-no-shadows`: 22.1 -> 14.7 ms, 29 -> 45 FPS. Godot's soft filter alone (SoftHigh vs hard, `MARVIN_SCN_CAL=ShadowFilterQuality=0`) is 3.7 ms. Godot renders four maps (two suns x two splits) into an 8192² atlas and clears the whole atlas every frame (0.34 ms); SceneKit renders one 4096 and one 2048 map. |
| 2 | Scene shading without shadows (opaque + transparent passes) | 9.1 ms at 1080p | 5.7 ms (one forward pass) | **~3.4 ms** | Fill-bound: the opaque pass scales 2.5x and the transparent pass 3x from 540p to 1080p. The opaque cost is the ground: without the town it is unchanged (7.6 ms), with the `townNoise` ground materials made constant it drops by 2.2 ms. The transparent pass is 15 draws of town ground overlays (soil, street, trampled sand, red soil) for 6.6 ms with shadows, 3.9 without; without the town 2.7. Since "Ground and overlays" the noise costs no sines and the ground skips work with an exactly known result. |
| 3 | Post-processing, depth prepass and frame overhead | ~4.1 ms (depth prepass 1.0, glow 1.0, tonemap + HUD 0.6, uploads 0.5, MSAA depth resolve 0.5, window blit 0.3, colour resolve 0.2) | ~0.6-1.0 ms (uploads 0.5, bloom and final blit; SceneKit has no depth prepass outside its SSAO pass) | **~3 ms** | Metal System Trace per encoder. |
| 4 | Loading freeze at 93 % (`startDirtTrack` on the main thread) | 2.8-3.4 s | 0.75 s | **2-2.6 s** | `--world-build-profile`: facade node flush 2.07 s + parallel mesh preparation 0.51 s for 4,884 nodes; `revealDirtTrack`'s `view.snapshot()` 0.47 s, of which PNG encoding of the discarded image 0.30 s; GC 0.12 s; game code ~0.2 s. SceneKit prepares in the background (`SCNView.prepare` is asynchronous there). |
| 5 | Main-thread CPU (one thread does everything) | 11 ms/frame at 540p: managed 4.1 ms (game tick 1.6, facade flush 2.1-2.5, ...), Godot's scene and render encoding 3.3 ms, Metal driver 1.5 ms; GC pauses ~1.2 ms/frame on average | main 3.8 ms + SceneKit render thread 8.4 ms (in parallel) | headroom 5.7 ms at 60 FPS | `sample` of both processes; the facade flush grows 2.1 -> 2.5 ms over ten minutes (trail chunks). |
| 6 | Garbage collection | 1.6 MB allocated per frame (tick 1.1 MB: effects 0.6, town 0.36; facade node flush 0.48 MB) -> 12-14 gen0 + ~1 gen1 per second, 16-19 ms paused per second | ARC, no collector | ~1 ms/frame, pauses of ~1.4 ms | `godot-render.json` allocation telemetry |
| 7 | Memory | 3.8-3.96 GB (editor) / 3.30-3.77 GB (export); .NET heap ~1 GB, textures 610 MB (540p) / 796 MB (1080p), buffers ~380 MB | 3.23 GB | +0.1-0.7 GB | process RSS, Godot monitors |
| 8 | Texture churn in the dunes | 49 new ImageTextures + 72 MTLTextures per second in `--dune-roam` (DeformableSand height maps, a new texture per patch update as on macOS); city roam: none after the first minute; race 1.3/s | (SceneKit creates the same MTLTextures) | small | `godot-render.json` `flushed` counters; freed only when the GC finalizes the wrappers |

## Ground and overlays: the same pixels with less work

Measured on 2026-10-06 on the same Mac, editor runtime at 1920 x 1080, the default Fast configuration unless a row says
Exact. Two other worktrees ran their own GPU benchmarks on the Mac throughout; every run waited for an idle GPU
(`run-benchmark.py --wait-idle`) and the per-pass numbers come from traces without other GPU work in them.

The opaque pass shades the town's base terrain, the dune tiles and the 4 km horizon plane with
`TownGround.terrainSurface`: world-space value noise (`townNoise`, four `fract(sin(...) * 43758.5453)` lattice hashes per
call), `townPigment` (five noise calls) and the desert boundary (`atan` and three sines). Over it the transparent pass
draws the town's ground overlays one layer at a time, each lit in full with both suns' shadows: the trampled sand
(`townPigment` over three anisotropically filtered textures), the streets (wind tongues from `townNoise`), the doorway
patches, the robots' trails and two red-soil aprons at the track.

**Where the time goes** (diagnostics that change the look; the city roam frozen at 16 s (`MARVIN_BENCHMARK_FREEZE=16`),
a street-level view in which the ground fills about two thirds of the screen and the trampled sand is drawn after the
streets; GPU per pass from a labelled Metal System Trace 21-25 s after the start; one or two runs per variant, interleaved
with runs of the unchanged build):

| variant, frozen town view | opaque pass ms | transparent pass ms | GPU busy ms | FPS |
|---|---|---|---|---|
| unchanged shader code (eb73a04), five runs | 5.08-5.16 | 7.85-8.47 | 18.98-19.55 | 47.3-48.2 |
| trampled sand hidden | 5.11, 5.12 | 4.46, 4.98 | 15.54, 16.07 | 56.7, 55.4 |
| streets hidden | 5.13 | 6.52 | 17.63 | 51.6 |
| doorway patches hidden; trails hidden | 5.09; 5.12 | 8.15; 8.10 | 19.15; 19.16 | 48.0; 47.7 |
| trampled sand, streets, patches and trails hidden | 5.09 | 2.57 | 13.80 | 58.6 |
| trampled sand unlit (`.constant`) | 5.14 | 6.74 | 17.85 | 51.2 |
| trampled sand without its three textures; without anisotropic filtering | 5.11; 5.12 | 7.17; 7.51 | 18.22; 18.59 | 49.9; 48.5 |
| trampled sand without its pigment | 5.08, 5.12 | 7.82, 7.41 | 18.88, 18.46 | 48.6, 49.1 |
| every `townNoise` hash without its sines (wrong values) | 4.92, 4.91 | 7.64, 7.55 | 18.65, 18.42 | 48.6, 49.3 |

In this view the trampled sand costs about 3.5 ms (its lighting 1.6, its textures 1.1, of which anisotropic filtering
0.8, its pigment 0.1-1.0 in two noisy runs) and the streets 1.8 ms; the noise's sines are 0.5-1.1 ms of GPU busy (0.3-1.0 ms
of walltime) over both passes. The red-soil aprons
cost nothing measurable there (each hidden: within the runs' spread). Of the 2.6 ms left in the transparent pass with
the four layers hidden, most is the pass itself (it loads and stores the 2x MSAA colour and depth targets) and the dust.

**Kept** (commit 9e00938; the look unchanged, see below):

- **The noise's lattice hashes from a table.** `townNoise` reads the four hashes of its cell from a 512 x 512 RGBA32F
  texture (cells -256..255; every call site stays inside: the pigment within 184 m of the centre, the dune bands over
  the horizon plane at 1/43 scale, the wind tongues within 225 m), which a compute shader on a local RenderingDevice
  fills once with the shaders' own expression while the world is built (5.3 ms). On Metal the values are bit for bit
  the ones the fragment shaders computed: the town captures, where the pigment covers most of the ground, are
  identical. Without a RenderingDevice (headless) the shaders keep the sines.
- **Exact early-outs.** The desert boundary's radius lies between 95 and 151 m (the overlays' outer fade: 102 to
  158 m), so `desert` is exactly 0 within 82 m of the centre and exactly 1 beyond 184 m, where `atan` and the radius are
  not needed; the pigment's weight is exactly 0 within 28 m and it is hidden where `desert` is 1 (the dunes and the
  horizon plane), the dune colour and the ripple only show where `desert` > 0; the trampled sand's outer fade is exactly 1
  within 94 m; a street's alpha is exactly its vertex alpha within 83 m (no `atan`, no wind tongues); the infield
  deposit's texture is sampled only inside its 60 m square (outside, `scn_inside` made it 0). Where `desert` is 1 the
  colour is the dune colour itself rather than `mix(soil, dune, 1)`, which can differ in the last bit.

Frozen views, the two steps on one build with the original shader text as the baseline (two interleaved rounds; FPS,
GPU walltime per frame from `metalperftrace`, opaque / transparent pass and GPU busy per frame from the trace):

| frozen view | unchanged | early-outs | early-outs + hash table |
|---|---|---|---|
| town (city roam at 16 s) | 48.14, 48.23 FPS; 21.21, 21.10 ms; 5.13 / 8.08, 5.08 / 7.85; busy 19.17, 18.98 | 49.09, 49.05 FPS; 20.76, 20.69 ms; 4.91 / 7.69, 4.86 / 7.86; busy 18.57, 18.70 | 50.10, 50.09 FPS; 20.32, 20.25 ms; 4.60 / 7.65, 4.62 / 7.41; busy 18.21, 18.03 |
| race start (1 s; track, walls and robots, no town ground) | 56.62, 55.85 FPS; 17.81, 18.00 ms | 56.61, 56.51 FPS; 17.81, 17.80 ms | 56.44, 56.35 FPS; 17.83, 17.85 ms |

So the town view gains about 0.9 ms of GPU walltime per frame (+1.9 FPS): 0.5 ms in the opaque pass (base terrain) and
0.4 ms in the transparent one (trampled sand, streets); the deposit's early-out came after these runs. The race start shows
no ground that changes.

Moving benchmarks, eb73a04 built from its own tree (a copy of this tree with the four changed files restored) against
this commit, interleaved (A, B, A, B), 45 s each, labelled trace 20-24 s after the start:

| 1920 x 1080, editor runtime | eb73a04 | this commit |
|---|---|---|
| city roam, Fast (two rounds) | 46.11, 46.21 FPS; walltime 22.25, 22.09 ms; busy 20.71, 20.34 ms (opaque 5.72, 5.62 / transparent 8.17, 8.20) | 48.66, 48.46 FPS; 21.01, 21.02 ms; busy 19.35, 19.08 ms (5.09, 5.08 / 7.76, 7.59) |
| race, Fast (four rounds) | 52.59, 51.59, 52.80, 52.32 FPS; 19.09, 19.47, 19.08, 19.22 ms; busy 17.48, 18.39, 17.29, 17.32 ms | 53.16, 52.93, 53.42, 51.91 FPS; 19.07, 19.06, 18.77, 19.36 ms; busy 16.75, 17.08, 16.85, 17.00 ms |
| city roam, Exact (two rounds) | 34.56, 34.49 FPS; 29.54, 29.63 ms; busy 27.25, 27.43 ms | 35.68, 35.76 FPS; 28.61, 28.56 ms; busy 26.17, 25.95 ms |
| race, Exact (five rounds) | 42.04, 42.62, 43.36, 42.41, 41.43 FPS; 24.25, 23.95, 23.46, 24.12, 24.57 ms; busy 22.32, 22.60, 21.38, 21.72, 23.00 ms | 40.79, 39.74, 41.46, 43.15, 42.77 FPS; 25.05, 25.72, 24.73, 23.59, 23.80 ms; busy 21.91, 22.73, 23.61, 21.82, 21.77 ms |
| race, Exact, grid pinned (`MARVIN_GRID_SLOTS=0,3,1,2`, two rounds) | 42.06, 41.51 FPS; 24.35, 24.51 ms; busy 22.12, 22.48 ms | 40.22, 39.77 FPS; 25.39, 25.79 ms; busy 23.93, 24.73 ms |
| town view frozen at 16 s, Fast (two rounds) | 48.26, 48.25 FPS; 21.00, 20.98 ms; opaque 5.12, 5.16 / transparent 7.65, 7.60 ms | 49.70, 50.00 FPS; 20.40, 20.17 ms; 4.65, 4.69 / 7.69, 7.30 ms |
| race overview frozen at 19 s (the whole town from above), grid pinned, Fast | busy 20.19 ms (opaque 9.82 / transparent 3.50) | busy 19.21 ms (9.46 / 3.24) |
| the same, Exact (two rounds) | busy 26.12, 26.21 ms (14.09, 14.15 / 4.96, 5.02) | busy 25.39, 25.15 ms (13.53, 13.55 / 4.66, 4.71) |
| race start frozen at 1 s, grid pinned, Exact (two rounds) | busy 19.99, 20.39 ms | busy 20.26, 20.37 ms |

(One round-1 run of each build, eb73a04's race and this commit's roam, shared the GPU with another worktree's benchmark
that started at the same moment (about 1,000 GPU-ms per second of another Godot in `metalperftrace`) and was repeated;
the runner now backs off before starting and repeats such runs.)

- **Fast**: the town roam gains 2.4 FPS (46.2 -> 48.6) and 1.15 ms of GPU walltime per frame (busy -1.3 ms: opaque
  -0.6, transparent -0.5); the race 0.5 FPS (52.3 -> 52.9) and 0.7 ms of GPU busy per frame, the walltime only 0.15 ms
  (the chase views show little town ground; the race overview, all town, 1.0 ms less busy).
- **Exact**: the town roam gains 1.2 FPS (34.5 -> 35.7) and 1.0 ms of walltime. The race does not gain measurably: 41.6
  against 42.4 FPS on average over five unpinned rounds whose spreads overlap, 40.0 against 41.8 in the two pinned ones;
  the GPU busy time in its traces is the same on average (22.4 against 22.2 ms), and the frozen race views that compare
  one frame show no loss (the start the same, the overview 0.9 ms less). The race's physics advances by each frame's dt,
  so the moving race's content already differs between two runs of one build (draw calls per second differ from the first
  seconds, also with the grid pinned), more so between builds with different frame rates. Variants run to look for a
  cause (the sines instead of the table, the table only for the coarse noise scales, the table only in the opaque
  terrain; one or two runs each) gave 39.4-43.5 FPS in the pinned Exact race without a pattern, and the same GPU time as
  this commit in the frozen views.

**Tried and dropped:**

- **Skipping the streets under the trampled sand.** Where the transparent sort draws the streets before the trampled
  sand (about half of all view directions: their bounding-box centres are 25 m apart) the trampled sand's alpha is
  exactly 1 within 36-94 m of the centre and covers them completely; SceneKit draws them the same way. Discarding those
  street fragments (a test of the sort order with a 1 m margin and of the radius in the street shader) saved 0.8 ms of
  the frozen town view's transparent pass (busy 18.42, 18.34 -> 17.58, 17.39 ms; walltime 20.44, 20.37 -> 19.97,
  20.02 ms; 49.8 -> 50.8 FPS). Not kept: it is exact only where the trampled sand really reaches every sample, which the
  street shader cannot see: an opaque surface between the two layers (they are 7 mm apart; a robot's tread a few
  millimetres in the ground, a wall's base) or a depth-writing transparent surface sorted between them (WALL-E's eyes)
  hides the trampled sand but not the street, and the trampled mesh ends at the town's edge. Making it exact needs the
  opaque depth per MSAA sample (Godot's depth texture holds their mean), the facade's sorted list per view and a
  coverage mask, for about 0.4 ms of walltime in half of the town's frames.
- **A baked pigment texture** (`townPigment` sampled from a world-space texture instead of evaluated): bilinear
  interpolation of the 2.1 m value noise differs from the analytic field, so not look-identical; the hash table gives
  most of the gain exactly.
- **Dropping the red-soil aprons' fully transparent triangles** (15,186 of the city ramp apron's 19,200 triangles have
  zero alpha at all three vertices, so dropping them with the bounding box kept would be exact): the aprons cost nothing
  measurable in the town view, so not pursued.
- **The trampled sand in the opaque pass, or a wider depth-only region of the base terrain under it**: not exact. The
  transparent sort decides whether the streets show above or below the trampled sand, and the depth-only base terrain
  is outside SceneKit's SSAO pass, so moving its border changes the occlusion of what stands on it.

**Look and checks.** The unchanged build and this commit, each run from its own tree with the same pins (town and
entrance `MARVIN_GRID_SLOTS=0,3,1,2 MARVIN_TOWN_DAYLIGHT=0.2125,4.18`; navigation `2,1,0,3`; the smoke test's reference
pins; visual regression `1,3,0,2`; binary sky `MARVIN_DAYLIGHT_REFERENCE`; post-race and city escape as in PORTING.md;
passage `0,3,1,2`), the unchanged build twice for the noise: 14 capture modes, 388 captures (town 21, entrance 38,
navigation 20, smoke 36, dune contact 14, ground performance 68, visual regression 43, binary sky 8, dust 6, post-race 21,
city escape 10, people 29, passage 66, menu 8). 307 of them render identically in the unchanged build's two runs; of
those, 188 are identical after the change and 119 (town, entrance, navigation, dunes, ground performance, dust, city
escape, people and passage views) differ in single pixels by one 8-bit step, at most 0.0018 % of a capture (mean
0.0000/255): the last-bit differences of the restructured shader code (the compiler fuses the reordered arithmetic
differently, and where `desert` is 1 the dune colour replaces `mix(soil, dune, 1)`). The other 81 captures hold random
state (crowd, dust, dirt coatings, racer contacts) and differ from the unchanged build about as much as its two runs
differ from each other (each mode's mean within 0.00-0.06/255 of that spread; the largest, dust 0.24 against 0.19). Every
deterministic JSON report is byte-identical (town-smoke, the three entrance reports, navigation, smoke, full-race-trails,
menu-smoke, visual-regression, binary-races, postrace, city-escape, people, street-people, passages, preflight);
dune-contact.json differs in `cpuUpdateP95MS` (a timing), the ground-performance comparison's image errors by up to
1e-8 and the smoke test's test-scores.json in its random scores, as between two runs of the unchanged build.
`tools/checks` is byte-identical to `reference/simulation-checks-swift.txt`.

## Shading and post: lighting-feature variants, sky-light lookup, shadow-box test

Measured on 2026-10-06 on the same Mac, on top of the shadow quality setting (eb73a04). Three exact changes to the
composer make the opaque and transparent passes cheaper; the post chain, the depth prepass and the MSAA resolves were
measured and left as they are. The look is unchanged ("Look and checks" below).

**What changed.**

- **Lighting-feature variants** (`ShaderComposer.VariantFlags.Forward` and `DirectionalOnly`, chosen per scene by
  `SceneKitRuntime.ShadingVariant`; PORTING.md, "Shader composer"). Godot inlines the composer's `light()` into its
  directional, omni and spot light loops, and the deferred-shadow and LDR-clamp code sits behind uniform branches, so
  every material carried code the race and the town never run: the game has no omni or spot lights, and the race world
  has no deferred shadows and an HDR camera. The GPU allocates registers for that code all the same. A scene without
  omni and spot lights now gets `light()` for directional lights only; a scene without deferred shadows and LDR cameras
  gets neither branch nor the `scn_unlit` and `scn_fog_amount` varyings they read. A scene's features only grow, and a
  view about to draw a scene with a feature its shaders lack (an LDR snapshot camera, a deferred light added later)
  rebuilds the scene's nodes first, so every scene renders exactly what the full shaders render. The menu and the
  sandbox, with deferred shadows and LDR cameras, keep the full shaders.
- **Sky-light lookup** (`scn_env_radiance`): the equirect coordinates of the reflection direction came from Metal's
  `atan2` and `acos`. Minimax polynomials (six terms each: `atan` on [0, 1] with the octants folded, `acos` as
  sqrt(1-x) x P(x)) are within 1.7e-6 and 6.4e-7 rad, 2e-5 and 7e-6 of a texel of the 64 x 32 radiance bands. The band
  lookup's `pow(r, 1.0)` (the calibrated blur power is 1) is folded to `r`.
- **Shadow-box test**: `light()` found a light's fixed shadow box by comparing the light's direction with both boxes' z
  rows, in every fragment and for every light. `FitShadows` now gives each box's Godot light the specular amount 2 + the
  box index (every other light 1; SceneKit lights have no specular amount, and `light()` no longer multiplies by it),
  and `light()` transforms the fragment by that light's box only.

**Method.** One fixed frame each, as in the GPU optimisation pass: the town roam frozen at 16 s and the race frozen 1 s
after its start (`MARVIN_BENCHMARK_FREEZE`), 1920 x 1080, editor runtime, a labelled Metal System Trace 21-25 s after
the benchmark start, GPU busy per frame and pass (`gpu-passes.py`). Diagnostics change the look and only isolate a cost.
Three other agents' Godot runs shared the Mac all morning: every run waited for an idle GPU (`run-benchmark.py
--wait-idle`), and runs whose trace showed other GPU work were repeated. `--wait-idle` now recognises other game
processes by their executable (`ps` comm): matching whole command lines (`pgrep -f`) also matched the other runners' own
`pgrep` and shell polling loops, which hold the pattern verbatim, so three runners waiting for an idle GPU kept one
another waiting with no game running (seen for minutes at a time). The same build varies by 0.4 ms in the town
view (transparent pass 7.70-8.17 ms over five runs) and by 0.03 ms at the race start (three runs). Then 45 s moving
benchmarks, interleaved against a build of eb73a04 from its own tree (base, this change, base, this change), Fast and
Exact (`MARVIN_SCN_CAL=Exact`), with traces 20-24 s after the start.

GPU busy per frame in ms (single runs unless a count is given; "..." adds to the row above):

| change | town view: GPU busy | opaque | transparent | race start: GPU busy | opaque | transparent | |
|---|---|---|---|---|---|---|---|
| base (HEAD eb73a04) (5 runs) | 19.06 | 5.14 | 7.92 | 15.86 | 7.70 | 0.87 |  |
| diagnostic: plain Lambert `light()` | 17.03 | 4.51 | 6.39 |  |  |  | changes the look |
| diagnostic: no sky-light reflection | 17.42 | 4.26 | 7.07 |  |  |  | changes the look |
| diagnostic: sky lookup with linear u, v (fetches kept) | 18.22 | 4.74 | 7.45 |  |  |  | changes the look |
| diagnostic: sky lookup without its two fetches | 18.69 | 5.05 | 7.55 |  |  |  | changes the look |
| diagnostic: no direct specular | 17.65 | 4.59 | 7.18 |  |  |  | changes the look |
| diagnostic: Forward without the shadow-box test | 17.13 | 4.84 | 6.44 |  |  |  | changes the look |
| polynomial atan / acos | 18.33 | 4.82 | 7.42 |  |  |  | exact |
| DirectionalOnly | 17.58 | 4.83 | 6.77 |  |  |  | exact |
| Forward | 18.71 | 5.06 | 7.63 |  |  |  | exact |
| `.constant` unshaded in forward scenes | 19.12 | 5.15 | 8.05 | 15.85 | 7.72 | 0.75 | exact, dropped |
| Forward + DirectionalOnly + unshaded constants | 17.55 | 4.74 | 6.92 | 16.08 | 7.63 | 0.89 | exact |
| ... + box id | 17.00 | 4.81 | 6.12 | 14.91 | 6.86 | 0.67 | exact |
| ... + polynomials, `pow` folded | 17.10 | 4.48 | 6.59 | 14.12 | 6.31 | 0.51 | exact |
| ... + box id + polynomials | 16.72 | 4.44 | 6.27 | 14.10 | 6.30 | 0.58 | exact |
| **kept** (variants, box id, polynomials, fold) | 17.12 | 4.41 | 6.78 | 14.49 | 6.32 | 0.79 | exact |

The cost sat in register pressure, not in arithmetic, as the GPU optimisation pass suspected: removing `light()` from
the omni and spot loops, which never run, saves about as much as replacing it with a plain Lambert term (a diagnostic)
did, most of it in the transparent pass, where the town's 15 ground overlays are lit layer by layer. The sky-light
lookup's two trigonometric functions alone cost about 0.4 ms in the opaque pass and as much in the transparent pass
(linear coordinates as a diagnostic), its two fetches 0.1 ms; the polynomials recover most of that. The parts do not add
up: in the race-start view the variants alone gain nothing and the variants with the polynomials 1.7 ms. What counts is
whether a shader drops below a register threshold, so every part was measured together with the others before it was
kept.

**45 s at 1920 x 1080** (editor runtime, two interleaved rounds; FPS, p99 and frames over 25 ms from `benchmark.json`,
GPU walltime per frame from `metalperftrace`, GPU busy and passes from the trace):

| scene, configuration | build | FPS | p99 ms | frames > 25 ms | GPU walltime ms/frame | GPU busy ms/frame | opaque | transparent |
|---|---|---|---|---|---|---|---|---|
| city roam, Fast | eb73a04 | 46.46, 46.40 | 25.9, 27.2 | 43, 59 | 22.06, 22.02 | 20.20, 20.34 | 5.66, 5.68 | 7.99, 8.13 |
| city roam, Fast | this change | 51.71, 51.55 | 23.4, 23.3 | 4, 3 | 19.68, 19.77 | 17.94, 18.13 | 4.94, 4.99 | 6.42, 6.52 |
| city roam, Exact | eb73a04 | 34.62, 34.50 | 36.1, 37.4 | 1331, 1331 | 29.60, 30.11 | 27.09, 27.35 | 8.13, 8.24 | 12.02, 12.10 |
| city roam, Exact | this change | 38.21, 37.49 | 32.1, 33.3 | 1223, 1300 | 26.79, 27.42 | 24.49, 24.78 | 7.09, 7.39 | 10.49, 10.07 |
| race, Fast | eb73a04 | 52.59, 51.77 | 23.2, 23.1 | 2, 2 | 19.10, 19.56 | 17.12, 19.05 | 8.09, 8.07 | 2.09, 3.82 |
| race, Fast | this change | 57.38, 56.10 | 21.7, 21.7 | 2, 1 | 16.87, 17.79 | 14.73, 16.93 | 6.19, 6.58 | 1.65, 3.38 |
| race, Exact | eb73a04 | 40.27, 39.67 | 29.8, 34.5 | 786, 785 | 25.46, 25.83 | 21.87, 21.36 | 10.68, 10.99 | 4.32, 3.18 |
| race, Exact | this change | 48.01, 47.67 | 26.3, 26.0 | 273, 285 | 21.02, 21.24 | 18.96, 19.10 | 9.39, 9.18 | 2.61, 2.78 |

- City roam, Fast: FPS 46.43 -> 51.63; walltime 22.04 -> 19.72 (-2.32); busy 20.27 -> 18.04 (-2.23); opaque 5.67 ->
  4.97; transparent 8.06 -> 6.47 (means of the runs above).
- City roam, Exact: FPS 34.56 -> 37.85; walltime 29.85 -> 27.11 (-2.75); busy 27.22 -> 24.63 (-2.59); opaque 8.18 ->
  7.24; transparent 12.06 -> 10.28 (means of the runs above).
- Race, Fast: FPS 52.18 -> 56.74; walltime 19.33 -> 17.33 (-2.00); busy 18.08 -> 15.83 (-2.26); opaque 8.08 -> 6.38;
  transparent 2.96 -> 2.51 (means of the runs above).
- Race, Exact: FPS 39.97 -> 47.84; walltime 25.65 -> 21.13 (-4.52); busy 21.62 -> 19.03 (-2.59); opaque 10.84 -> 9.29;
  transparent 3.75 -> 2.70 (means of the runs above).

**Post chain, prepass and resolves: measured, not changed.** Per pass in the town view: the tonemap pass 0.65 ms (0.36
without glow), the root viewport's 2D pass that draws the 3D view's texture 0.19 ms, the window blit 0.37 ms, the glow's
compute passes about 0.11 ms (in one encoder with the final MSAA depth resolve, 0.41 ms), the MSAA depth resolve between
the opaque and transparent passes 0.21 ms, the depth prepass 0.68-0.72 ms (1.45-1.48 ms at the race start).

- **Glow**: with the bloom off (diagnostic) the glow's compute passes disappear and the tonemap pass takes 0.36 instead
  of 0.65 ms, so the glow costs about 0.4 ms, 0.29 ms of it in the tonemap pass, which samples three levels bicubically
  (four bilinear taps each). Bilinear upsampling (`SceneKitCalibration.GlowBicubicUpscale = false`, Godot's
  `EnvironmentGlowSetUseBicubicUpscale`) takes the tonemap pass to 0.50 ms. Measured as an option (the default stays
  bicubic in this change): in the town view the frame takes 16.95 instead of 17.12 ms with it (tonemap and 2D 0.71
  instead of 0.85 ms). The glow then differs only where it is visible at all: around the robots' highlights in the smoke
  test's close-ups and acting views by up to 5/255 in 0.03-0.4 % of the pixels (at most 0.002/255 on average), and by
  one level in 0.08 % of the binary sky's midday-suns view; over the smoke and sky sets 0.11 and 0.054/255 from the
  bicubic build against 0.11 and 0.071/255 between two runs of eb73a04. Computing the glow at a lower resolution than
  Godot does is not possible from the game: its first level is already half resolution, and the levels and the tonemap
  pass that composites them are inside the engine.
- **MSAA resolves**: Godot 4.7.2 resolves the MSAA depth after the opaque pass whenever a compositor effect asks for the
  normal-roughness buffer (the SSAO port reads it before the opaque pass), and resolves colour and depth again at the
  end of every frame whether anything reads the depth or not (`render_forward_clustered.cpp`). Both depth resolves (0.21
  + 0.41 ms) are redundant for this game, but every other way to get the normal-roughness buffer (Godot's own SSAO, SSR,
  SDFGI, a material that reads `NORMAL_ROUGHNESS_TEXTURE`) triggers the same resolve: removing them needs an engine
  change.
- **Depth prepass**: Godot 4.7.2 draws every opaque material in its depth prepass and has no per-material switch; the
  SSAO port reads the prepass's depth and normals before the opaque pass, and the opaque pass relies on its depth test.
  Turning it off (`rendering/driver/depth_prepass/enable`) also takes away the resolved depth the SSAO effect reads
  before the opaque pass. Its fragment work is small (the composer's code is dead there apart from the SSAO normal and
  tag): the cost is the vertex and rasterisation work of about 437 draws.
- **Tonemap, 2D pass, window blit**: full-screen passes bound by memory bandwidth (the tonemap reads the 16 MB RGBA16F
  colour buffer and writes 8 MB). Rendering the 3D view straight into the root viewport would save the 2D pass's copy of
  the view's texture (about 0.19 ms), but the root canvas draws the window's own backgrounds under the content view and
  snapshots must leave the HUD out; not done.

**Tried and dropped**: `.constant` materials unshaded in forward scenes (their `light()` only applies deferred shadows;
exact): the town view 19.12 against 19.06 ms, the race start 15.85 against 15.86 ms, no gain. Not measured on their own
(the GPU time went to the combinations): folding the roughness polynomials of constant-roughness materials into
uniforms, and recomputing the world position in `light()` instead of passing it from `fragment()`.

**Look and checks.** Captures of eb73a04 and of this change, interleaved mode by mode (one Godot at a time): the visual
regression test (pinned grid `1,3,0,2`), the smoke test (the reference pins), the town smoke test (pinned), the binary
sky (the macOS daylights), the menu, the robot close-ups in the sandbox and the race light, the facade test and the
calibration scenes, 196 images; a second run of eb73a04 for the visual regression, smoke, town and sky sets gives the
run-to-run noise. Every image that the two eb73a04 runs render identically, and every image of the deterministic
close-up, menu, facade-test and calibration sets, is identical with this change or differs in at most 11 pixels by one
8-bit level (at most 4 outside the race-light close-ups; the polynomial coordinates move a few sky-light lookups across
a bilinear weight step): 32 of 43 visual-regression images, all menu, facade-test and 4 of 6 calibration images, 21 of
31 sandbox-light and 7 of 31 race-light close-ups are pixel-identical. Images with random state (the sandbox course,
dirt coatings, dust, crowds) differ from eb73a04 as much as two eb73a04 runs do (all 196: 0.029/255 on average, between
the two eb73a04 runs 0.043; town 0.0067 against 0.0071, sky 0.048 against 0.071, smoke 0.14 against 0.11 with the
sandbox course's random rings in sandbox-contact). The reports are byte-identical: smoke.json, full-race-trails.json,
menu-smoke.json (the Shadow quality row's live switch included), visual-regression.json (its robot shadow samples count
rendered pixels), town-smoke.json, binary-races.json and the facade test's measurements.json. `tools/checks` is
byte-identical to the Swift reference. The playthrough passes 63 of 63 steps with frame times per stage like eb73a04's;
`--loading-smoke-test` passes.

## Near shadow splits: finer hard shadows next to the camera

Measured on 2026-10-06 on the same Mac. The hard filter's shadow edges are one Godot texel wide. With the split fit made
for the soft filter (one split from the camera out to `orthographicScale`, 58 m for the race suns, over a 4096-texel
side, and a second one out to the farthest point of the sun's box in view), that texel was 2-2.9 cm next to the camera,
and close views showed it: Marvin's head cast a stair-stepped band across its body in the race-grid close-ups, the
robots' shadows on the ground were saw-toothed, the race-light robot close-ups and the dune contact views showed blocks
tens of pixels wide, and where the two suns' shadows meet both edges were stair-stepped. Now the fixed-box suns get four
splits under the hard filter (`SceneKitRuntime.NearSplitDistances`, `SceneKitCalibration.HardShadowSplit1` and
`HardShadowSplit2`), and the flat base terrain under the race world no longer casts (it shadows nothing visible, see
"Cost"), which pays for them. `MARVIN_SCN_CAL=Exact` keeps the earlier fit and the terrain's shadow; its captures are
pixel for pixel those of the build before (calibration, menu, both close-up sets, visual regression and the deterministic
smoke and town captures; the others differ only where two runs of one build do).

| split | ends at | texel, race camera at 1080p (across / along the light's y axis) | soft filter's fit (before) |
|---|---|---|---|
| 1 | 12 m | 1.2 / 0.61 cm | 2.9 cm (one split to 58 m, 4096 x 4096 texels) |
| 2 | 30 m | 2.8 / 1.4 cm | 2.9 cm |
| 3 | 58 m (`orthographicScale`) | 5.3 / 2.7 cm | 2.9 cm |
| 4 | the box's farthest view depth, rounded up to 58 m x 1.25^k | 14 / 7.0 cm at 150 m | 7.0 cm |

Godot gives each of four splits a quarter of the light's 4096 x 8192 atlas region (2048 x 4096), so the texels are twice
as long across the light as along its y axis; on the ground that axis is stretched by 1 / sin(elevation), which evens
them out at a 30 degree sun. The split distances are fixed in metres, so Godot's own stabilisation (each split's square
light-space extent comes from the bounding sphere of its slice of the view frustum, which only depends on the lens, and
is snapped to whole texels) keeps the first three splits from crawling while the camera moves or turns. Only the last
split follows the view; its end is rounded up in steps of 25 %, so its texels stay put between steps (before, the second
split's end followed the box's farthest view depth every frame). A first split ending 8 m from the camera (0.8 cm texels,
second split to 24 m) was tried first: it let thin parts of the robots shade their own feet and domes where SceneKit's
coarser, filtered map does not (R2-D2's feet in the race light, a dome's rim at a 15 degree sun) and put the close-ups
further from macOS than 12 m does (race-light close-ups 2.29 against 2.23/255, dune contact 3.08 against 3.00, visual
regression 1.23 against 1.20, smoke 2.62 against 2.58). A larger normal bias (3 texels instead of 2) did not help near
and moved the far shadows (binary sky 1.15 -> 1.21/255).

**Cost.** Like for like on one build, the earlier fit selected with `MARVIN_SCN_CAL="HardShadowSplit1=0;
BaseTerrainCastsShadow=1"` (the same code path as before), editor runtime at 1920 x 1080, GPU walltime per frame from
`metalperftrace` (the number that sets the frame rate while the GPU is the limit; trace busy times overlap passes and
moved by up to ±0.7 ms between runs). The user's MarvinSimulator (PID 68407), which shared the GPU in every earlier
measurement, exited on its own at 00:29; the rows below ran without it unless they say otherwise, so their absolute
frame rates are higher than in the sections below.

| 1920 x 1080, editor runtime | earlier fit | near splits, base terrain not casting (the default) |
|---|---|---|
| race, 45 s (two interleaved rounds) | 51.92, 50.32 FPS; 19.45, 20.13 ms | 52.99, 51.26 FPS; 18.97, 19.60 ms |
| city roam, 45 s (two interleaved rounds) | 45.05, 44.97 FPS; 22.86, 22.83 ms | 46.23, 46.24 FPS; 22.11, 22.11 ms |
| race start, frozen 1 s after the start (`MARVIN_BENCHMARK_FREEZE=1`) | 18.48 ms | 18.01 ms |
| town view, frozen at 16 s | 21.54 ms | 21.25 ms |

So the new default is 0.5 ms (race) and 0.7 ms (roam) of GPU walltime per frame faster than the earlier fit: +1.0 and
+1.2 FPS. `MARVIN_SCN_CAL=Exact` on the same build: race 40.5 FPS (25.39 ms), city roam 34.4 FPS (29.71 ms). By pass
(labelled traces 20-24 s after the start, `gpu-passes.py`), the shadow maps cost 2.41 instead of 2.29 ms per frame in the
race and 2.12 instead of 2.47 in the roam; the other passes are the same within the runs' spread.

**Against the commit before** (a separate check: 38818a3 built from its own tree next to this one, the same runner,
`--wait-idle`, no other game process alive in any run, editor runtime at 1920 x 1080, interleaved B A A B; GPU walltime
from `metalperftrace`):

| 1920 x 1080, editor runtime | 38818a3 | near splits (233da48) |
|---|---|---|
| city roam, 45 s (two rounds) | 45.00, 45.14 FPS; 22.69, 22.65 ms | 46.17, 46.20 FPS; 22.12, 22.06 ms |
| race, 45 s (four rounds) | 53.13, 53.96, 54.08, 52.29 FPS; 18.98, 18.66, 18.73, 19.42 ms | 53.06, 51.76, 53.58, 51.77 FPS; 18.96, 19.49, 18.90, 19.57 ms |
| race start frozen at 1 s (28 s runs) | 18.31, 18.45 ms | 18.02, 17.82 ms |
| aerial view frozen at 20 s | 19.33, 19.30 ms | 19.19, 20.01 ms |
| town view frozen at 16 s | 21.58, 21.50 ms | 21.08, 21.22 ms |

The roam (-0.58 ms, +1.1 FPS) and the frozen race start and town view (-0.46 and -0.39 ms) reproduce the gain above. The
moving race does not: 53.4 -> 52.5 FPS and 18.95 -> 19.23 ms on average (its chase phases 18.32 -> 18.67 ms), within
the 1.8 FPS spread of each build's four rounds, so the race is about as fast as before rather than 1 FPS faster. The
roam's trace puts the shadow maps at 2.08 instead of 2.51 ms per frame; in the frozen race start they are 0.17 ms
cheaper while the opaque pass is 0.2-0.35 ms dearer.

Where a split's cost goes: it is what the split fills. In the race-start view, sun A's first split took 0.69 ms of
fragment and 0.60 ms of vertex time with the earlier fit and 1.26 / 0.61 ms when it ended 15 m from the camera instead
of 58 m: a split next to the camera is covered by the ground from edge to edge (4096 x 4096 texels per split with two
splits, 2048 x 4096 with four), while the earlier fit's splits were partly empty. The flat base terrain (`Town base
terrain`, 256 m square at y = -0.025, under every other surface) filled every split of both suns; without it casting, the
race start took 16.81 instead of 18.42 ms and the town view 20.39 instead of 21.80 ms with the earlier fit. It shadows
nothing visible (it can only shade what lies below it, and nothing below it is seen): the visual regression, town,
binary sky, dune contact, ground performance and entrance captures are identical with and without its casting, apart
from the modes' random state. It casts only in the exact configuration (`BaseTerrainCastsShadow`).

Configurations measured on the way (frozen views; differences against the earlier fit in interleaved runs, the
first column while the MarvinSimulator still ran):

| configuration, base terrain casting | town view | race start | near texel | why not |
|---|---|---|---|---|
| four splits per sun (8 / 24 / 58 m / box) | +1.12 ms; without the MarvinSimulator +0.94 | +1.41 ms | 0.81 / 0.41 cm | over the budget of +0.5 ms; with the terrain not casting -0.70 / -0.31 ms |
| sun A four splits, sun B the earlier fit | +0.44 ms | | 0.81 cm (sun A), 2.9 cm (sun B) | sun B's lighter band stays blocky; the moving race +1.2 ms (sun B split at 24 m) |
| two splits per sun, the first ending 15 / 24 / 32 m | +0.09 ms (15 m) | +1.07 / +0.87 / +0.50 ms | 0.76 / 1.2 / 1.6 cm | 15-58 m coarser than before (7.3 cm instead of 2.9) and over the budget in the race; at 32 m only 1.8 times finer near |
| sun A two splits at 15 m, sun B the earlier fit | +0.24 ms | | 0.76 cm (sun A) | as above, and sun B blocky |
| coarser robot shadow LODs (LOD bias 0.5 / 0.25), earlier fit | | -0.30 / -0.18 ms | | triangles are not what a split costs |
| four splits with the robots hidden (`MARVIN_BENCHMARK_HIDE`) | | 16.60 -> 18.07 ms | | the robots are not what the extra splits cost either |

**Stability.** `tools/godot -- --shadow-motion-probe DIR` (scripts/World/ShadowMotionProbe.cs) renders the race suns over a
flat ground with shadow-only casters, seen straight down through the race camera's lens from 3, 14, 40 and 85 m (the
first to the fourth split), and moves the camera by exactly 5 pixel footprints per frame along x or z for 16 frames, so
each frame equals the previous one shifted by 5 pixels wherever the texels stayed put. Share of shadow-edge pixels that
changed by more than 3/255 between consecutive frames, mean of 15 frame pairs (what remains is mostly short lines where
the boxes touch the ground, with both fits):

| height (split) | earlier fit | near splits |
|---|---|---|
| 3 m (1), along x / z | 8.6 % / 12.0 % (of fewer edge pixels: the 2.9 cm ramps are wide) | 1.1 % / 0.8 % |
| 14 m (2) | 0.4 % / 0.8 % | 0.4 % / 0.2 % |
| 40 m (3) | 0.3 % / 0.3 % | 0.0 % / 0.3 % |
| 85 m (4) | 0.1 % / 0.3 % | 0.5 % / 0.4 % |
| 85 m, the suns' boxes moved 2 m per frame along the light (the box's farthest view depth changes, as for a chase camera) | 61 % in every frame (16 shadow distances in 16 frames: the last split crawls) | 6.8 % (one step: 97 % in that frame, 0.4 % in the others) |

In the race world (the same probe with a pinned grid at a low sun: the chase camera moved 2 cm per frame and a view 6 m
higher moved 10 cm per frame, each frame also rendered without the suns' shadows so their ratio is the shadow factor
alone) the temporal second difference of the shadow factor is the same for both fits, 0.001-0.008 per pixel near and up
to 0.029 in the far view's lower rows, where shadow edges cross several pixels per frame.

Driven race (a separate check: the race at a fixed 1/60 s step with the AI's input and the chase camera, grid and sun
pinned as above, 30 s): the last split's end changed in 44 frames for sun A and 37 for sun B (five and six distinct
ends), against every frame with the earlier fit. The steps have no hysteresis, so near a step the end can go back and
forth (sun B 72.5 -> 58 -> 72.5 m within four frames, sun A 90.6 -> 72.5 -> 90.6 m within 43). At driving speed
(7.5 cm per frame on average) a frame with a step changes no more shadow pixels than its neighbours do, and the temporal
second difference of the shadow factor per image band is the same for both fits; the frames looked at showed no seam at
the split ends.

**Look against macOS.** The same 21 capture modes, pins and macOS sets as the hard-shadow comparison (PORTING.md,
"Full-game comparison"), 467 captures compared like for like with that run of the earlier fit: the mean |sRGB
difference| from macOS goes from 1.98 to 2.06/255, and 213 captures change by less than 0.05/255. The earlier fit's
blurred 2.9 cm blocks next to the camera are gone (`reference/compare/11-near-splits-close-range.png`, macOS | earlier
fit | near splits): Marvin's head shadow crosses its body as a clean line and the robots' shadows on the ground follow
their outlines more closely. The texels still show where a pixel is a millimetre or two: in the race-light and dune
contact close-ups the 1.2 x 0.6 cm texels are crisp stair-steps 4-12 pixels wide (before, blurred blocks 2.4-4.8
times larger), and a walker's feet, the base of a wall next to the camera (entrance household-2) and the track
close-ups' ground shadows keep 1-3 pixel steps. What the per-pixel
difference counts against them is that the edges are now as sharp as the texels are fine. SceneKit's penumbrae are
11.5-16 cm wide (its 2.83 cm texels under a 3-texel kernel), so a crisp edge differs from them over a wider band than the
earlier fit's 2.9 cm bilinear ramp did, and the finer map resolves thin casters that SceneKit's filtered map blurs away:
R2-D2's feet and WALL-E's tread plates are shaded more, and a robot dome right in front of the camera at a 15 degree sun
shows blotchy dark self-shadowing below its rim, fainter with the earlier fit and absent on macOS (visual regression's
shadow-angle-15, +0.95). Per area: dirt track 2.25 -> 2.38 (the track
close-ups +0.1-0.5), dunes 1.82 -> 1.89 (WALL-E's contact views +0.4-0.6), town 2.22 -> 2.31 (the overviews, whose far
split now has 14 cm texels across the light instead of 7, +0.2-0.4), post-race and city escape 2.08 -> 2.19, robots
close up 1.64 -> 1.75 (race-light close-ups 2.01 -> 2.23), sky 1.10 -> 1.14, calibration scenes 1.01 -> 1.04; menu,
sandbox and sandstorm only within their runs' own spread (their lights have no fixed box). Of the reports that count
shadow pixels, `visual-regression.json` counts 732 robot shadow samples (earlier fit 569, macOS 598; per robot
502 / 582 / 140 / 467, macOS 515 / 552 / 147 / 450) and `dune-contact.json` 1,054 changed coating samples (earlier 1,094,
macOS 1,448) and 4,455 opaque Marvin body samples (4,431; macOS 4,282); both checks pass.

**Bias.** The hard filter's biases are unchanged in Godot texels (`HardShadowBiasTexels` 1, `HardShadowNormalBias` 2;
with four splits the normal bias stays one of the split's coarser texels, as before), so in world units they shrink with
the near texels. `--ground-bias-probe` gives the same values for its sunlit walls standing on a casting ground (A) and
its walls at 0-85 degrees to the light (D): no acne. Its orthographic plate tests (B, C) keep the earlier fit; a new test
E puts the plate under the game's 4096 sun at 20 degrees through the race camera's lens 3, 14 and 40 m away (the first,
second and third split): its shadow is half there at a gap of about 7, 9 and 13.5 cm (the earlier fit 8.5, 9.7 and
10.7 cm; SceneKit 7.5 cm; the exact configuration 14-15 cm), so thin casters next to the camera detach from their shadows
about as in SceneKit. The penumbra probe (`CAL_EXP=penumbra CAL_PENUMBRA_ELEV=1`, a box edge seen close up) gives edges
0.29 / 0.54 / 0.72 / 1.31 cm wide, 0.5 / 1.0 / 1.65 / 2.65 cm towards the caster at 60 / 35 / 20 / 10 degrees (earlier
fit: 1.35 / 1.98 / 3.32 / 6.64 cm wide, 1.8 / 2.7 / 5.8 / 6.8 cm towards the caster; SceneKit sun A: penumbrae 11.5-58 cm
wide, 0.7 / -0.1 / 6.1 / 7.8 cm towards the caster).

## Hard shadows and camera mesh LODs: before and after

The user accepted two small look changes for frame rate at 1920 x 1080, both measured earlier as diagnostics and options
(sections below): Godot's **hard shadow filter** instead of SoftHigh (`SceneKitCalibration.ShadowFilterQuality` 4 -> 0)
and the robots' **mesh LODs for the camera too** (`MeshLodForCamera` false -> true; before, only their shadows were cast
from the LODs). Both are the default for the game and every mode since ef7b4e6. `MARVIN_SCN_CAL=Exact` (or
`MARVIN_SCN_CAL="ShadowFilterQuality=4;MeshLodForCamera=0"`) selects the exact-SceneKit configuration again, in the editor
runtime and in the exported builds; the probes and capture modes give the same values with it as the build before.

**Method.** As in "All passes merged": `tools/perf/run-benchmark.py --wait-idle`, 45 s per run, daylight 0.5, drawables of
exactly 960 x 540 and 1920 x 1080 over the black backdrop, the editor runtime and an exported release build. "Before" is a
worktree of 3cb6567 (the merged branch, exact-SceneKit look), built and exported next to this branch, measured by its own
copy of the same runner. Two interleaved rounds (before editor, after editor, before export, after export) per scene and
size, plus the after build with `MARVIN_SCN_CAL=Exact` at 1080p (one run each). The user's MarvinSimulator (PID 68407) ran
throughout, covered by the backdrop and presenting nothing; the traces show it using 186-219 GPU-ms per second in every
1080p run and WindowServer 72-163, so absolute numbers are pessimistic, and both builds ran under the same conditions.
One run saw another game process start during it (before export, roam, round 1: 29.86 FPS against 30.48 in round 2). The
first before-editor roam run at 1080p was lost when the disk filled up (Instruments leaves about 1 GB of raw kernel trace
per recording in the temporary directory; `run-benchmark.py` now deletes it) and was repeated as a third interleaved pair
(before, after) at the end. The 1080p runs recorded a labelled Metal System Trace: 20-24 s after the benchmark start in
the editor runtime, 31-35 s after launch for the exports (about 19.5-23.5 s after their start: a release export does not
flush its stdout per line, so the runner cannot see the start line while it runs).

| scene | drawable | build | FPS | p99 ms | frames > 25 ms | GPU walltime ms/frame | GPU busy ms/frame | primitives M/frame | facade flush ms | tick p95 ms |
|---|---|---|---|---|---|---|---|---|---|---|
| city roam | 960 x 540 | before editor | 59.88, 59.90 | 18.2, 18.3 | 1, 0 | 14.9, 15.1 | - | 3.2 | 1.31, 1.34 | 2.43, 2.34 |
| city roam | 960 x 540 | after editor | 60.00, 60.00 | 17.9, 17.9 | 0, 0 | 13.8, 13.8 | - | 2.7 | 1.24, 1.34 | 2.49, 2.57 |
| city roam | 960 x 540 | before export | 59.87, 59.93 | 18.4, 18.1 | 0, 0 | 15.3, 15.1 | - | 3.2 | 1.23, 1.24 | 2.56, 2.59 |
| city roam | 960 x 540 | after export | 60.00, 60.00 | 17.9, 18.0 | 0, 1 | 14.2, 13.7 | - | 2.7 | 1.22, 1.29 | 2.72, 2.60 |
| race | 960 x 540 | before editor | 59.97, 59.96 | 17.9, 18.2 | 0, 0 | 15.1, 15.1 | - | 4.1-4.2 | 1.24, 1.30 | 2.01, 2.10 |
| race | 960 x 540 | after editor | 60.00, 60.00 | 17.8, 17.8 | 0, 0 | 14.2, 14.0 | - | 3.5-3.6 | 1.32, 1.30 | 2.13, 1.93 |
| race | 960 x 540 | before export | 60.00, 59.87 | 17.9, 18.6 | 0, 1 | 14.9, 15.3 | - | 4.1-4.2 | 1.19, 1.19 | 2.28, 2.05 |
| race | 960 x 540 | after export | 59.98, 59.97 | 17.8, 17.7 | 1, 1 | 14.1, 13.9 | - | 3.5-3.6 | 1.24, 1.18 | 2.51, 2.28 |
| city roam | 1920 x 1080 | before editor | 29.61, 29.70 | 43.0, 43.6 | 1242, 1247 | 34.5, 34.5 | 23.4, 22.4 | 3.3 | 1.49, 1.63 | 3.52, 3.70 |
| city roam | 1920 x 1080 | after editor | 38.92, 39.01, 39.73 | 31.4, 31.5, 30.8 | 1024, 1000, 929 | 26.3, 26.4, 26.0 | 18.0, 17.8, 17.2 | 2.9 | 1.42, 1.39, 1.39 | 2.55, 2.85, 2.86 |
| city roam | 1920 x 1080 | after editor, `Exact` | 30.89 | 42.6 | 1297 | 33.1 | 22.5 | 3.3 | 1.63 | 3.57 |
| city roam | 1920 x 1080 | before export | 29.86, 30.48 | 42.7, 42.5 | 1254, 1279 | 34.4, 33.7 | 21.4, 22.1 | 3.3 | 1.46, 1.48 | 3.82, 3.79 |
| city roam | 1920 x 1080 | after export | 39.81, 40.85 | 30.5, 31.2 | 920, 657 | 25.8, 25.1 | 17.1, 17.2 | 2.9 | 1.29, 1.27 | 2.91, 2.84 |
| race | 1920 x 1080 | before editor | 34.58, 33.43 | 35.1, 36.2 | 1292, 1237 | 29.5, 30.6 | 21.5, 22.3 | 4.2-4.3 | 1.50, 1.55 | 2.63, 2.60 |
| race | 1920 x 1080 | after editor | 46.56, 47.23 | 27.1, 27.1 | 223, 234 | 22.1, 21.7 | 16.1, 16.1 | 3.6 | 1.30, 1.36 | 2.06, 2.24 |
| race | 1920 x 1080 | after editor, `Exact` | 36.61 | 34.6 | 1109 | 28.0 | 20.0 | 4.2 | 1.46 | 2.79 |
| race | 1920 x 1080 | before export | 34.40, 36.34 | 36.9, 33.9 | 1171, 1214 | 29.9, 28.2 | 23.2, 22.1 | 4.3 | 1.41, 1.25 | 2.94, 2.58 |
| race | 1920 x 1080 | after export | 47.65, 45.86 | 26.9, 28.1 | 143, 254 | 21.5, 22.3 | 17.2, 17.1 | 3.8 | 1.21, 1.28 | 1.97, 2.19 |

(The before-editor roam runs at 1080p are rounds 2 and 3; the after-editor ones rounds 1-3.)

- **1920 x 1080**: still GPU-bound, but much less so. Means: town roam 29.7 -> 39.2 FPS in the editor runtime (+32 %) and
  30.2 -> 40.3 in the export; race 34.0 -> 46.9 (+38 %) and 35.4 -> 46.8. GPU busy per frame roam 22.9 -> 17.6 ms, race
  21.9 -> 16.1 ms (export 21.8 -> 17.2 and 22.6 -> 17.2); GPU walltime per frame roam 34.5 -> 26.2 ms, race 30.1 -> 21.9 ms.
  Frames over 25 ms: roam 1,242-1,279 -> 657-1,024 per 45 s (every roam frame took longer than 25 ms before), race
  1,171-1,292 -> 143-254; p99 roam 42.5-43.6 -> 30.5-31.5 ms, race 33.9-36.9 -> 26.9-28.1 ms. The after build with
  `MARVIN_SCN_CAL=Exact` runs like the before build (roam 30.9 FPS, 22.5 ms busy; race 36.6 FPS, 20.0 ms), so the gain is
  the two settings. 60 FPS at 1080p is still out of reach: the median frame takes 24-26 ms in the roam and 20-22 ms in the
  race (before: 33-34 and 27-30 ms).
- **960 x 540**: both builds vsync-bound at 59.9-60.0 FPS; the after build has about 1 ms less GPU walltime per frame
  (13.7-14.2 against 14.9-15.3 ms), a p99 0.1-0.9 ms lower and 15 % fewer primitives. CPU numbers (facade flush, tick,
  allocation, GC) are unchanged within the run-to-run spread.
- **Editor runtime vs export**: the same within the spread, as before.

GPU busy per pass at 1080p (editor runtime, mean of the traces of each build: before rounds 2-3 (roam) and 1-2 (race),
after rounds 1-3 and 1-2; `tools/perf/gpu-passes.py`):

| pass | roam before | roam after | race before | race after |
|---|---|---|---|---|
| **total GPU busy per frame** | **22.89** | **17.64** | **21.91** | **16.10** |
| opaque pass | 8.13 | 5.82 | 10.30 | 7.52 |
| transparent pass | 7.08 | 4.31 | 3.75 | 1.19 |
| depth prepass | 1.27 | 1.01 | 1.48 | 1.19 |
| shadow maps (+ atlas clear) | 2.58 + 0.34 | 2.59 + 0.35 | 2.32 + 0.34 | 2.23 + 0.35 |
| depth/normal resolve + SSAO | 2.01 | 2.00 | 2.10 | 2.08 |
| resolves, glow, tonemap and 2D, window blit, uploads | 2.71 | 2.79 | 2.70 | 2.68 |

The exports' traces show the same split (roam 21.8 -> 17.1 ms, opaque 7.9 -> 5.7, transparent 6.2 -> 4.1; race 22.6 ->
17.2 ms, opaque 10.9 -> 7.8, transparent 3.8 -> 2.0). The hard filter's single depth comparison removes the soft
filter's cost from every lit fragment of the opaque and transparent passes (2.3-2.8 ms each; Instruments filed some
transparent encoders of the after traces under other labels, 0.5-0.9 encoders per frame, so the totals are the robust
numbers); the camera's mesh LODs make the depth prepass 0.26-0.29 ms shorter and take some of the opaque pass. The shadow
maps cost the same: they were already cast from the LODs. Hard filter alone (`MARVIN_SCN_CAL=MeshLodForCamera=0`, one
run each against an after run right after it): roam 38.8 FPS / 17.95 ms busy against 40.4 / 17.21, race 45.3 / 17.11
against 45.7 / 16.26, so of the 5.3-5.8 ms the hard filter is about 4.5-5 ms and the camera's mesh LODs 0.75-0.85 ms
(depth prepass 0.24-0.28 ms, opaque pass 0.3-0.4 ms; primitives per frame 3.3 -> 2.9 M and 4.25 -> 3.7 M).

**Bias retune for the hard filter.** The soft filter's biases grow with its kernel (depth bias 1 + 0.6 x kernel texels,
normal bias 2 + 0.8 x kernel texels, about 3 and 5-6 Godot texels for the race suns) so that its 16 taps do not reach
into lit slopes. The hard filter takes one bilinear depth comparison, so with those biases it only moved its sharp edges
as far towards their casters as the soft ones (`MARVIN_SCN_CAL=ShadowFilterQuality=0` on the earlier build). It now has
its own biases without the kernel terms (`HardShadowBiasTexels` 1, `HardShadowNormalBias` 2, Godot's default normal
bias). Probes (`CAL_EXP=penumbra CAL_PENUMBRA_ELEV=1`, a box edge on a floor under the race suns' fixed boxes; SceneKit
from `reference/calibration/tools/fullgame/pen2.swift`, run again on this Mac), penumbra 10-90 % / edge offset in cm
(negative: towards the caster):

| sun elevation | SceneKit sun A / sun B | exact (SoftHigh) | hard, soft biases | hard, retuned (default) |
|---|---|---|---|---|
| 60 degrees | 11.5 / -0.7, 15.8 / -4.4 | 11.7 / -3.5, 15.8 / -4.0 | 1.3 / -3.6, 1.3 / -4.1 | 1.3 / -1.8 (both suns) |
| 35 degrees | 17.5 / 0.1, 24.1 / -5.6 | 17.8 / -7.0, 23.9 / -8.9 | 2.0 / -7.0, 2.0 / -8.4 | 2.0 / -2.7 |
| 20 degrees | 29.1 / -6.1, 40.1 / 0.9 | 30.2 / -13.8, 40.1 / -16.7 | 3.3 / -14.0, 3.4 / -16.7 | 3.3 / -5.8 |
| 10 degrees | 58.2 / -7.8, 79.7 / -10.2 | 60.4 / -24.7, 78.7 / -29.2 | 6.7 / -23.8, 6.7 / -29.5 | 6.6 / -6.8 |

`--ground-bias-probe` with the retuned biases: no self-shadowing of lit walls at 0-85 degrees to the light (D), sunlit
walls standing on a casting ground lit down to 2 mm at 5-35 degrees (A), and a 1 cm plate casts its shadow from a gap of
8 / 16 / 4 / 2 cm for SceneKit texels of 2.83 / 5.66 / 1.42 / 0.49 cm (C; SceneKit's half-shadow gaps 7.5 / 15 / 4.8 /
2.4 cm; exact 15 / 24 / 8 / 3 cm, the hard filter with the soft biases 16 / 26 / 8 / 4 cm). Depth biases of 0.5, 1 and 2
texels gave identical probe values. On the game's captures (16 capture modes, 429 images, mean |sRGB difference| from
macOS) normal biases of 1 / 2 / 4 / 6 Godot texels gave 1.98 / 1.97 / 2.02 / 2.14 /255 (exact: 1.78): larger offsets
light the robots' tread plates more as SceneKit does, but move every shadow edge further from where SceneKit puts it.

**Look against macOS** (the full-game comparison, PORTING.md "Full-game comparison": the same 21 capture modes, pins and
macOS sets as the merged comparison, 473 captures): the mean |sRGB difference| from macOS goes from 1.77 to 1.96/255;
139 captures change by less than 0.05/255. What changed, by area: hard one-texel shadow edges everywhere, stair-stepped
where the camera is close (race-light robot close-ups at about 1 mm per pixel show the 2 cm shadow texels as blocks:
1.50 -> 2.01/255, worst walle-gear 3.32 -> 5.37; dune contact close-ups 2.08 -> 2.66; walls next to the camera at a
grazing angle); the weaker sun's shadow as a lighter band beside the dark core where SceneKit's penumbrae blend the two
suns' shadows; crisp outlines of the menu's and sandbox's faint deferred shadows (menu 0.53 -> 0.51). No acne in the captures
looked at close up (track and town walls, roofs, dunes, robots, sandbox, menu): the saw-tooth edges along walls at
grazing angles are the shadow texels (identical with normal biases 1 and 2). Robot silhouettes from the mesh LODs move by up to half a pixel. Of the reports that count
rendered pixels, visual regression's robot shadow samples move towards macOS (512 -> 569, macOS 598) and dune contact's
changed coating samples away from it (1,319 -> 1,094, macOS 1,448); every check gives the merged build's result.
`MARVIN_SCN_CAL=Exact` renders the merged build's captures pixel for pixel (calibration, menu, close-ups, visual
regression, the deterministic smoke and town captures), in the editor runtime and in the export.

## All passes merged: before and after

The GPU, CPU and release passes were developed on separate branches from c19fd6f and replayed onto this branch one
commit at a time (17 commits, 0436006..4c0875a). Two files were changed by both the GPU and the CPU pass and were merged
by hand, keeping both: `SCNNode.ApplyVisuals` pushes only changed visual state (CPU) and then re-applies the robots'
shadow-only twin (GPU), whose creation or removal resets the pushed state; `SCNSsaoEffect` keeps its cached uniform
sets (CPU), now with a fourth binding for the one-channel depth chain (GPU).

**Method.** As in the measurement above: `tools/perf/run-benchmark.py --wait-idle`, 45 s per run, daylight 0.5, a
drawable of exactly 960 x 540 or 1920 x 1080 over the black backdrop, the editor runtime (C# compiled optimised) and an
exported release build (release template, `ExportRelease` C#). "c19fd6f" is a worktree of that commit, built and
exported next to this branch and run by the same runner script. The builds were interleaved (c19fd6f editor, merged
editor, c19fd6f export, merged export) in two rounds; the table gives both runs. No other game process ran (one run
recorded two short-lived game processes of another job); the user's MarvinSimulator, covered by the backdrop and
presenting nothing, still used 190-215 GPU-ms per second and WindowServer 95-155 in every trace. The 1080p editor runs
recorded a labelled Metal System Trace 20-24 s into the benchmark ("GPU busy"; the export runs have none:
`--trace-from-start` did not see the benchmark's start line in the export's log while it ran). Facade flush, allocation, GC and
primitives are means over the benchmark from `godot-render.json`; "tick p95" is the benchmark's CPU update per tick.

| scene | drawable | build | FPS | p99 ms | frames > 25 ms | GPU walltime ms/frame | GPU busy ms/frame | facade flush ms | allocation MB/frame | GC pauses ms/s | primitives M/frame | tick p95 ms |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| city roam | 960 x 540 | c19fd6f editor | 59.86, 59.86 | 18.5, 18.1 | 0, 0 | 15.2, 15.3 | - | 2.04, 2.02 | 1.60, 1.61 | 17.6, 17.3 | 4.2, 4.2 | 3.26, 3.27 |
| city roam | 960 x 540 | merged editor | 59.90, 59.88 | 18.3, 18.4 | 0, 0 | 15.0, 15.1 | - | 1.33, 1.37 | 0.60, 0.60 | 7.1, 7.9 | 3.2, 3.2 | 2.39, 2.49 |
| city roam | 960 x 540 | c19fd6f export | 59.79, 59.88 | 19.0, 18.1 | 1, 0 | 15.5, 15.3 | - | 1.82, 1.85 | 1.72, 1.70 | 18.9, 19.1 | 4.2, 4.2 | 3.50, 3.57 |
| city roam | 960 x 540 | merged export | 59.91, 59.93 | 18.0, 18.1 | 1, 1 | 15.2, 15.2 | - | 1.26, 1.24 | 0.61, 0.61 | 8.2, 8.2 | 3.2, 3.2 | 2.62, 2.65 |
| race | 960 x 540 | c19fd6f editor | 59.71, 59.87 | 18.6, 18.8 | 0, 0 | 15.7, 15.1 | - | 1.92, 2.02 | 1.57, 1.67 | 17.0, 16.6 | 5.3, 5.0 | 3.26, 3.11 |
| race | 960 x 540 | merged editor | 60.00, 60.00 | 17.9, 18.0 | 0, 2 | 15.2, 15.0 | - | 1.28, 1.33 | 0.61, 0.60 | 7.5, 7.5 | 4.1, 3.9 | 2.11, 2.22 |
| race | 960 x 540 | c19fd6f export | 59.68, 59.85 | 19.0, 18.7 | 0, 0 | 15.2, 15.5 | - | 1.60, 1.68 | 1.54, 1.65 | 17.5, 18.7 | 5.3, 5.3 | 3.65, 3.56 |
| race | 960 x 540 | merged export | 59.86, 59.82 | 18.3, 18.8 | 0, 1 | 15.3, 15.3 | - | 1.21, 1.21 | 0.65, 0.63 | 9.2, 8.4 | 4.1, 4.1 | 2.13, 2.16 |
| city roam | 1920 x 1080 | c19fd6f editor | 28.58, 28.67 | 43.9, 44.1 | 1201, 1205 | 35.7, 35.6 | 23.4, 23.5 | 2.57, 2.56 | 1.96, 1.97 | 8.6, 10.0 | 4.2, 4.2 | 4.05, 4.34 |
| city roam | 1920 x 1080 | merged editor | 29.60, 30.21 | 41.9, 42.5 | 1243, 1268 | 34.5, 34.1 | 22.5, 22.9 | 1.55, 1.60 | 0.68, 0.68 | 3.2, 3.5 | 3.3, 3.3 | 3.75, 3.67 |
| city roam | 1920 x 1080 | c19fd6f export | 29.72, 29.02 | 41.5, 43.4 | 1248, 1219 | 34.5, 35.2 | - | 2.57, 2.54 | 2.12, 2.11 | 13.1, 12.5 | 4.2, 4.2 | 5.08, 5.33 |
| city roam | 1920 x 1080 | merged export | 29.74, 30.26 | 41.9, 41.4 | 1249, 1271 | 34.5, 33.9 | - | 1.63, 1.64 | 0.69, 0.69 | 4.2, 4.4 | 3.3, 3.3 | 4.38, 4.26 |
| race | 1920 x 1080 | c19fd6f editor | 33.39, 35.33 | 36.8, 37.1 | 1323, 1308 | 30.5, 28.9 | 22.4, 21.9 | 2.41, 2.23 | 1.91, 1.72 | 9.4, 9.9 | 5.3, 5.4 | 3.19, 3.36 |
| race | 1920 x 1080 | merged editor | 36.35, 37.95 | 34.6, 34.0 | 1158, 946 | 28.2, 27.1 | 20.6, 21.3 | 1.45, 1.49 | 0.62, 0.64 | 3.3, 3.9 | 4.3, 4.1 | 2.42, 2.71 |
| race | 1920 x 1080 | c19fd6f export | 36.14, 35.15 | 34.6, 36.4 | 1285, 1292 | 28.3, 29.1 | - | 2.12, 2.11 | 1.96, 1.95 | 12.7, 12.0 | 5.3, 5.4 | 4.00, 3.89 |
| race | 1920 x 1080 | merged export | 35.53, 38.78 | 34.0, 33.5 | 1213, 818 | 28.9, 26.4 | - | 1.48, 1.35 | 0.68, 0.63 | 4.7, 4.6 | 4.2, 4.2 | 3.08, 2.89 |

- **960 x 540**: both builds are vsync-bound at 59.7-60.0 FPS. The merged build's p99 is the same in the editor's
  city roam and 0.3-0.8 ms lower in the other three pairs, its GPU walltime up to 0.3 ms lower. On the CPU side the
  facade flush is 0.4-0.7 ms shorter, the main thread allocates 0.6 MB per frame instead of 1.5-1.7, the GC pauses
  7-9 ms per second instead of 17-19, and the tick's p95 is 0.8-1.5 ms lower.
- **1920 x 1080**: GPU-bound in both. Means of the two runs: race 34.4 -> 37.2 FPS in the editor runtime (+8 %) and
  35.6 -> 37.2 in the export; town roam 28.6 -> 29.9 (+4.5 %) and 29.4 -> 30.0. GPU walltime per frame race 29.7 ->
  27.7 ms, roam 35.7 -> 34.3 ms; GPU busy race 22.2 -> 21.0 ms, roam 23.5 -> 22.7 ms. In the roam every frame of
  both builds takes more than 25 ms, so the count follows the frame rate; in the race it falls from 1,285-1,323 to
  818-1,213. Within a round the merged build was ahead in every pair but the first race export pair (36.14 against
  35.53 FPS).
- **Editor runtime vs export**: within the run-to-run spread of each other after the merge, as before it. Resident
  memory varies by several hundred MB between runs of the same build (3.2-4.0 GB) and shows no difference between the
  builds.

GPU busy per pass at 1080p (editor runtime, the two traces' mean, `tools/perf/gpu-passes.py`):

| pass | city roam c19fd6f | city roam merged | race c19fd6f | race merged |
|---|---|---|---|---|
| **total GPU busy per frame** | **23.47** | **22.67** | **22.15** | **20.95** |
| shadow maps (+ atlas clear) | 3.07 + 0.35 | 2.53 + 0.34 | 2.92 + 0.35 | 2.34 + 0.33 |
| depth/normal resolve + SSAO (one compute encoder) | 2.68 | 1.99 | 2.90 | 2.10 |
| opaque pass | 8.20 | 8.02 | 10.31 | 10.46 |
| transparent pass | 6.45 | 6.88 | 2.83 | 2.46 |
| depth prepass | 1.26 | 1.27 | 1.46 | 1.55 |
| resolves, glow, tonemap and 2D, window blit, uploads | 2.89 | 2.88 | 2.78 | 2.81 |

The two GPU changes show where expected: the SSAO step is 0.7-0.8 ms shorter (one-channel depth mips) and the shadow
maps 0.55-0.6 ms (robot casters from mesh LODs); the opaque and transparent passes move by up to 0.7 ms between the
two traces of one build, so the frame totals differ by less than the sum of the two.

**Loading** (`--loading-smoke-test` through the same runner, two runs per build; `--world-build-profile`, two runs):

| | c19fd6f editor / export | merged editor / export |
|---|---|---|
| launch to the end of the loading check | 13.0-13.1 s / 13.0-13.1 s | 10.8-10.9 s / 10.8-10.9 s |
| freeze at 93 % (main-thread stall) | 2.73 s / 2.52 s (the other run's stall log missed it) | 0.30-0.31 s + 0.10-0.11 s at the reveal / 0.26-0.27 s + 0.10-0.11 s |
| freeze at launch (engine, .NET, app launch) | 1.66-1.71 s + 0.36-0.41 s | 1.85-1.89 s + 0.38-0.40 s |
| responsive ticks while loading | 422, 422 / 439, 446 | 438, 447 / 450, 453 |
| world build (background queue) | 6.79-6.84 s | 6.18-6.29 s |
| `startDirtTrack` run synchronously | 3.23-3.25 s (node flush 2.05-2.06 s, discarded PNG 0.30-0.33 s) | 1.40-1.43 s (node flush 0.56-0.58 s, no PNG) |
| first frames after the reveal | 74-81, 41-43 ms | 72-73, 30-32 ms |

The launch freeze is 0.15-0.2 s longer in the merged build, in all four runs. Most likely this is the GPU pass's mesh
LOD generation for the robots' CAD meshes (meshoptimizer over 695,000 triangles for Marvin alone, done when the robots'
meshes are first prepared at launch); not verified separately.

**Ten minutes** (merged editor runtime, 603 s city roam, 960 x 540): 59.97 FPS, p50 16.65, p95 17.56, p99 18.09 ms,
2 frames over 25 ms, none over 50 ms (c19fd6f in the measurement above: 59.92 FPS, p99 18.27 ms, 0 over 25 ms). Facade
flush 1.38 ms in the first minute, 1.55 ms around minute 5, 1.19 ms in the last (c19fd6f: 2.11 -> 2.53 ms); allocation
0.56-0.69 MB per frame, 4.2-5.2 gen0 collections per second; orphan nodes 3,207 throughout; nodes 4,905 -> 5,847 (the
trail chunks); resident memory 3.73-3.85 GB, texture memory flat at 691 MB, the .NET heap 1.8 GB after loading and
1.06-1.07 GB from minute 5.

**Look and checks** (the rule for every optimisation: the look must not change):

- `tools/checks` is byte-identical to `reference/simulation-checks-swift.txt`, also with `MARVIN_PORTABLE_MATH=1`;
  `tools/checks --portable-math` passes.
- Every game mode was run on the merged editor runtime (49 runs, PORTING.md "App and game modes"): all pass except
  `--town-departure-movie`, which fails on macOS too (departure.json identical to the earlier run). The playthrough
  passes 63 of 63 steps; the fit tools find the same pins as before.
- 21 capture modes (477 images, the full-game comparison's pins) were run on c19fd6f and on the merged build. 246
  images are pixel-identical and 143 differ by less than 0.005/255 in the mean (at most 4,200 pixels by an 8-bit step
  or two, where the robots' own shadows fall: they are now cast from mesh LODs). The other 88 hold random state (dust,
  dirt coatings, the sandbox course, crowd and racer contacts) and differ from c19fd6f no more than c19fd6f differs
  from the earlier comparison run of the same code (largest: town robot POV 1.16 against 1.22, sandbox contact 1.10
  against 1.10, ground-performance midday infield 0.89 against 0.61, character sandbox 0.40-0.89 against 0.08-0.72).
  Every JSON report of a deterministic mode is byte-identical to c19fd6f's. Two reports differ as expected: `visual-regression.json` counts 512 robot shadow
  samples instead of 516 (per robot 500/537/150/422 instead of 501/538/153/420; the robots cast from mesh LODs; macOS
  598; the check passes), and `dune-contact.json`'s `cpuUpdateP95MS` is a timing (3.77 -> 3.38 ms).
- Two modes draw their starting grid at random and gave an unusual draw in the first merged run: the sandstorm
  (Marvin a lap behind at 120 s, storm-driving.png 30/255 from macOS) and the navigation drive (a rival's contact at
  the gate). Pinned to the same grids, the merged build reproduces c19fd6f exactly (sandstorm.json identical for two
  grids, captures 0.00/255; navigation.json identical for two grids, one of them the macOS run's 2,1,0,3, captures
  0.08-0.09/255), and a second unpinned merged sandstorm run gave the usual result, as both unpinned c19fd6f runs did. The exported merged build passes the same checks (PORTING.md, "Release builds and Windows").

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

## GPU optimisation pass (after the measurement above)

The rule: the look must not change. Every change was checked with 14 capture modes (249 images: facade test,
calibration scenes, shadow probes, robot close-ups in both lights, menu, the pinned smoke and town tests, binary sky,
dunes, navigation, visual regression, sandstorm) against two runs of the unchanged build, which also give the noise of
the random modes, and against the macOS captures in `reference/mac`.

**Method.** One GPU-heavy process at a time on a machine shared with other jobs: `run-benchmark.py --wait-idle` waits
for other Godot or Marvin Simulator processes to end, and every run records the game processes alive during it and the
other GPU work in its trace (`run.json` `contention`, `traceGPU.contention`). For A/B comparisons of one frame,
`MARVIN_BENCHMARK_FREEZE=16` stops the town roam at the same view after 16 s and keeps drawing it; a labelled Metal
System Trace 21-25 s after the benchmark started (`--trace 21:4 --trace-from-start`) gives the GPU time per pass
(`tools/perf/gpu-passes.py`). The same build varies by about ±0.7 ms in total GPU busy per frame between runs (other
processes' GPU work interleaves with Godot's, and Instruments sometimes files an encoder under another label), so
totals come from several interleaved runs and small changes are judged by their own pass. `MARVIN_BENCHMARK_HIDE`
hides nodes by name to cost a class of geometry. The race's frozen view is not repeatable (the rivals' positions after
12 s depend on the frame timing), so race results come from the 45 s benchmark.

### Kept

| change | GPU (1920 x 1080) | look |
|---|---|---|
| **SSAO depth mips in one channel** (`SCNSsao.cs`): the depth/normal pass also writes the half-precision depth alone into an R16F texture with SceneKit's box-filtered mips; the checkerboard index and the first mip come from it in one pass, the SAO taps and the upsampling read it. Before, all four channels of the RGBA16F texture were mip-mapped although the kernels read only the depth beyond mip 0. | depth/normal resolve + SSAO compute encoder 2.58-2.63 -> 1.81-1.92 ms per frame (frozen town view, 3 runs each); 2.67-2.73 -> 2.02-2.04 ms in the moving town roam | bit-identical (the same half-precision values): every deterministic capture is byte-identical, the random ones differ from the old build no more than two runs of the old build |
| **Robot shadow casters from mesh LODs** (`SCNGeometry.godotAutomaticLevelsOfDetail`, set by `DirtCoating.install` for every robot part): the robots' CAD and scanned meshes (Marvin alone 695,000 triangles in 23 parts) get Godot's own screen-space mesh LODs (meshoptimizer, as for imported scenes); a shadow-only twin instance casts from them while the camera and the depth prepass keep the full mesh (`SCNNode.RebuildMeshes`). A level casts only while its error stays under 0.5 pixel of the camera's view (`SceneKitCalibration.MeshLodThreshold`), far below the shadow maps' 2-6 cm texels. | shadow maps 3.0 -> 2.6 ms (town roam), 2.7 -> 2.4 ms (race); primitives per frame 4.2 -> 3.3 M and 5.2 -> 4.2 M; frame totals within the run-to-run spread (table below) | camera images unchanged: close-ups 0.0000-0.0006/255 (robot shadows), visual regression 0.0025/255; its shadow-angle check counts 512 shadow samples as before (macOS 598) |

Results, 45 s benchmark at 1920 x 1080 with the moving camera (two interleaved runs per build; FPS from
`benchmark.json`, GPU busy from the labelled trace at 20-24 s, GPU walltime per frame from `metalperftrace`):

| build | town roam FPS | GPU busy ms/frame | GPU walltime | race FPS | GPU busy | GPU walltime | primitives/frame (roam, race) |
|---|---|---|---|---|---|---|---|
| before (c19fd6f) | 29.66, 28.58 | 23.74, 24.40 | 34.3, 35.5 | 33.40, 32.22 | 24.22, 23.64 | 30.6, 31.7 | 4.2 M, 5.3 M |
| SSAO change | 29.45, 29.40 | 22.07, 22.77 | 34.7, 34.7 | 36.42, 35.69 | 20.83, 21.48 | 28.0, 28.7 | 4.2 M, 5.2 M |
| final (+ robot shadow LODs) | 29.76, 29.60 | 22.72, 22.61 | 34.3, 34.4 | 35.78, 37.06 | 21.01, 20.79 | 28.6, 27.5 | 3.3 M, 4.2 M |
| option `MeshLodForCamera=1` (LODs for the camera too, 0.5 px) | 30.74, 30.22 | 21.64, 21.73 | 33.4, 33.8 | 37.86, 35.97 | 19.77, 20.86 | 27.0, 28.3 | 2.9 M, 3.7 M |

So at 1080p the pass gives the race about 3.5 FPS (32.8 -> 36.4 on average) and 3 ms of GPU work per frame (busy
23.9 -> 20.9, walltime 31.2 -> 28.1), the town roam 1.4 ms of GPU work (24.1 -> 22.7) but only 0.6 FPS (29.1 -> 29.7):
the GPU is shared (a MarvinSimulator left running and WindowServer used 270-350 GPU-ms per second in every run), and
the roam's walltime per frame fell only from 34.9 to 34.4 ms. At 960 x 540 both hold 60 FPS (town roam 59.62 before,
59.76 now; GPU busy 10.3 -> 9.8 ms, walltime 15.6 -> 14.8 ms; shadow maps 3.1 -> 2.6 ms).

On the frozen town view the total GPU busy per frame went from 21.7 and 22.7 ms (before) to 20.6-21.7 (SSAO change,
three runs) and 19.3 and 19.3 (final), with the shadow maps at 1.6-2.2 ms instead of 2.6-2.7.

The option `SceneKitCalibration.MeshLodForCamera` (off) lets the camera draw the levels too, about another 0.5-1 ms per
1080p frame, but then the robots' silhouettes move by up to half a pixel: close-ups 0.011/255 on average (0.031 at
Godot's default of 1 pixel), visual regression 0.025/255, dunes 0.018/255, all slightly further from macOS, and
DuneContact's `opaqueMarvinBodySamples` falls from 4,422 to 4,282 (macOS 4,428). Every check still passes, but the
camera image is no longer the same, so it stays off.

### Tried and dropped

Each on the frozen town view, against several runs of the build without it:

- **8 PCF taps instead of 16** (`MARVIN_SCN_CAL=ShadowFilterQuality=3`, SoftMedium; the suns' SceneKit
  `shadowSampleCount` is 8): 20.7 and 20.9 ms against 20.1-21.3, no gain. The opaque pass costs the same with 8 or 16
  taps but 1.9 ms less with Godot's hard filter (one tap; about 4 ms less in all): the cost is apparently the soft path
  itself (its register use), not the number of taps, so matching SceneKit's sample count saves nothing.
- **Shared-memory bilateral blur** for the SSAO: the SSAO encoder is unchanged (1.92-2.0 ms).
- **The shadow-box test once per fragment** instead of once per light in the composer's `light()`, and **discarding
  fragments whose alpha is exactly 0** on blended overlays that write no depth (both exact): no measurable change.
- **One sky-radiance fetch when the roughness falls on a band** (exact) and **polynomial atan/acos** for the
  sky-light lookup: the opaque pass 0.1-0.2 ms lower, within the run-to-run spread.
- **A fused SSAO kernel** (depth/normal, index and first mip in one dispatch through shared memory): as fast as the kept
  version but not bit-exact (half rounding of the shared values; robot close-ups 0.04-0.12/255), so the kept version
  reads the stored depth back in a second dispatch.

### Where the remaining time goes

Diagnostics on the frozen town view (they change the look; Godot 4.7.2's renderer cannot be changed from the game):

- **Shadows ~7 ms**: no shadows 20.9 -> 14.2 ms. The soft filter is about 4 ms of it (above); the maps 2.6 ms plus 0.35 ms
  to clear the whole 8192² atlas every frame, and every pass loads and stores the atlas (a pass with 16 draws still costs
  0.4-0.5 ms).
- **The composer's `light()`**: a plain Lambert `light()` saves 1.6 ms in the opaque pass, but removing any single part
  (the deferred-shadow and LDR branches, the specular term, the shadow-box test) saves nothing: an occupancy limit, not
  arithmetic.
- **Sky light**: dropping the radiance lookups (atan, acos, two fetches) saves 0.7 ms.
- **Robots**: besides the triangles now handled, every robot part is its own draw with its own material (DirtCoating's
  per-geometry arguments) in the prepass, the colour pass and each shadow map: about 760 draw calls per frame.
- **Ground overlays**: of the transparent pass (~6 ms), the trampled sand is 1.7 ms, the streets 1.2, the doorway
  patches 0.2 (fully lit, with both suns' soft shadows, as SceneKit draws them).
- **Godot's fixed passes**: depth prepass ~0.9 ms, the MSAA depth resolve between the opaque and transparent passes
  0.25-0.35 ms (Godot resolves whenever a compositor effect asks for the normal-roughness buffer, which the SSAO port
  needs), final depth resolve + glow 0.6-0.8, tonemap and 2D 0.6-0.7, window blit 0.3. The "Blit Command 0" of the
  earlier report is Godot's per-frame uploads (15 small buffer copies) and a 1.9 MB buffer fill (the cluster buffer),
  0.2-0.4 ms.

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

## What this means for optimisation

In order of payoff at 1080p, each to be checked against the captures (the rule for all optimisations: the look must not
change). The GPU optimisation pass above did the SSAO mips and the robots' shadow casters; what it found for the rest
(the hard filter and the camera mesh LODs that followed are look changes, accepted for frame rate; "Hard shadows and
camera mesh LODs"):

1. Shadows (~6 ms): render only what SceneKit renders (one map per sun instead of two splits each, or the second split
   only when the fixed box needs it, as now, but with a smaller atlas region), stop clearing the full 8192² atlas, and
   reproduce SceneKit's penumbra with fewer taps (Godot's SoftHigh is 3.7 ms more than its hard filter; SceneKit's own
   kernel is a fixed set of taps x `shadowRadius`, PORTING.md "Not resolved"). Measured since: fewer taps do not help
   (SoftMedium's 8 taps cost what SoftHigh's 16 do), the atlas clear and the per-pass atlas load/store are inside
   Godot's renderer, and Godot fits its maps to the camera (no fixed box per light), so this needs either a custom
   Godot build or shadow maps rendered by the facade itself. The game now uses the hard filter (about 4.5-5 ms less);
   the maps themselves (2.2-2.6 ms plus the 0.35 ms atlas clear) are unchanged.
2. The robots' draw calls (~760 per 1080p frame in the town roam): every robot part is its own draw with its own
   material (DirtCoating's per-geometry arguments) in the prepass, the colour pass and each shadow map. Merging the
   parts that move together and share a material would need the coating's per-part `dirtToBody` folded into the
   vertices.
3. Scene shading (~3.4 ms): the ground (terrain and the transparent town overlays) is shaded with full lighting and
   shadowing per overlay layer; materials that SceneKit shades as `.constant` or whose result is covered could skip
   Godot's light loop (`render_mode unshaded` where the composer already writes the final colour), and the overlays could
   share one lit evaluation. Measured since: the composed `light()` and the sky-light lookups cost 1.6 and 0.7 ms
   in the opaque pass, but only when removed whole (an occupancy limit); exact restructurings of single parts gained
   nothing measurable. The trampled sand and the streets are 1.7 and 1.2 ms of the transparent pass. *Ground and
   overlays (2026-10-06): the noise's sines and the work with an exactly known result are gone (about 0.9 ms of GPU
   walltime in a street-level town view); with hard shadows the trampled sand is still 3.5 ms (lighting 1.6, textures
   1.1) and the streets 1.8 ms of such a view's transparent pass, because every layer is lit in full. What is left needs
   either a look change (one lit evaluation for the stacked layers, fewer anisotropic taps) or a renderer that can skip
   a layer the next one covers completely (see "Tried and dropped" there).*
   *Shading and post (2026-10-06): the occupancy limit was `light()` inlined into Godot's omni and spot loops, which
   never run, and Metal's `atan2`/`acos` in the sky-light lookup; 2.0-2.3 ms less per 1080p frame in the default
   configuration.*
4. Post chain and prepass (~3 ms): glow, tonemap and the 2D pass at full resolution, the depth prepass, two MSAA
   resolves. All inside Godot's renderer; the depth resolve between the opaque and transparent passes runs because the
   SSAO port asks for the normal-roughness buffer. *Measured again in "Shading and post": the two depth resolves
   (0.21 + 0.41 ms) and the 2D pass's copy of the view (0.19 ms) are the only redundant work, and only an engine change
   (or drawing the 3D view into the root viewport) removes them; bilinear glow upsampling saves 0.15 ms.*
5. Loading freeze (2-2.6 s): hand meshes to Godot over several frames or from a worker thread while the loading screen
   draws, and do not encode a PNG for snapshots whose image is discarded. *Done in the CPU pass (2.8 → 0.3 s).*
6. CPU: a separate render thread (Godot's thread model) would give the main thread back ~5 ms; allocation in the trails,
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
# an export does not flush its stdout per line: trace it from launch (its benchmark starts 11-12 s after launch)
tools/perf/run-benchmark.py OUT --app export --export-binary "build/macos/Marvin Simulator.app/Contents/MacOS/Marvin Simulator" \
    --size 1920x1080 --labels --trace 31:4
tools/perf/mst-gpu.py OUT/metal-system.trace --process Godot
# A/B of one fixed frame (GPU optimisation pass): wait for an idle GPU, freeze the town roam after 16 s, trace 21-25 s
# after the benchmark start (the run analyses its own trace into OUT/mst-gpu.json), then compare runs pass by pass
MARVIN_PERF_IGNORE_PIDS=<pid of a game left running> tools/perf/run-benchmark.py OUT --app godot --size 1920x1080 \
    --mode city-roam --seconds 28 --labels --trace 21:4 --trace-from-start --wait-idle --env MARVIN_BENCHMARK_FREEZE=16
tools/perf/gpu-passes.py OUT_A OUT_B ...
# one fixed race frame (the start, the robots next to the chase camera) for A/B: freeze 1 s after the start
tools/perf/run-benchmark.py OUT --app godot --size 1920x1080 --mode race --seconds 28 --labels --trace 21:4 \
    --trace-from-start --wait-idle --env MARVIN_BENCHMARK_FREEZE=1
# the cost of one ground layer in the frozen town view (a look change, diagnostics only; "Ground and overlays")
tools/perf/run-benchmark.py OUT --app godot --size 1920x1080 --mode city-roam --seconds 28 --labels --trace 21:4 \
    --trace-from-start --wait-idle --env MARVIN_BENCHMARK_FREEZE=16 "--env=MARVIN_BENCHMARK_HIDE=Trampled sand"
# before/after of a shader change without a worktree: a copy of this tree with the changed files restored from the old
# commit (cp -cR apps/simulator-godot OLD; git show OLD_COMMIT:apps/simulator-godot/FILE > OLD/FILE; OLD/tools/build),
# then OLD/tools/perf/run-benchmark.py and this tree's, interleaved
# the earlier hard-shadow fit for A/B on one build (and the near splits' stability probe)
tools/perf/run-benchmark.py OUT ... --env "MARVIN_SCN_CAL=HardShadowSplit1=0;BaseTerrainCastsShadow=1"
tools/godot -- --shadow-motion-probe OUT        # MARVIN_SHADOW_MOTION=exact|race|casters
# diagnostics: --env MARVIN_BENCHMARK_HIDE="Marvin CAD assembly;R2-D2 · ;BB-8 · ;WALL-E · " (no robots),
# --env MARVIN_SCN_CAL=Exact (the exact-SceneKit look: SoftHigh shadows, full robot meshes for the camera; the game's
# default is hard shadows and camera mesh LODs, and its Settings > Shadow quality does not reach game modes),
# -- --benchmark-no-shadows, -- --benchmark-no-ssao
# before/after of a look option on one build: the same runs with and without --env MARVIN_SCN_CAL=..., interleaved
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
tools/export                                     # build/macos/Marvin Simulator.app, build/windows, checked by export-verify.py
# before/after of a merge ("All passes merged"): a worktree of the old commit next to this one, assets and import
# cache cloned, built and exported there; this tree's tools/perf copied in, so the same runner measures both
git worktree add --detach BASE c19fd6f && cp -cR assets BASE/apps/simulator-godot/ && cp -cR .godot/imported .godot/editor BASE/apps/simulator-godot/.godot/
cp tools/perf/*.py tools/perf/*.m BASE/apps/simulator-godot/tools/perf/ && (cd BASE/apps/simulator-godot && tools/build && \
    tools/godot --headless --export-release "macOS" "$PWD/build/macos/Marvin Simulator.app")
BASE/apps/simulator-godot/tools/perf/run-benchmark.py OUT_BASE --app godot --size 1920x1080 --mode race --wait-idle   # then this tree's, interleaved
```

`run-benchmark.py` needs `metalperftrace` (macOS 27) and, for `--trace`, Xcode's `xctrace`; `profile-cpu.sh` needs
`dotnet tool install -g dotnet-trace`. Run one GPU-heavy process at a time. The export presets (macOS universal,
ad-hoc signed with the JIT and DYLD entitlements .NET and the probes need; Windows x86_64) are in
`export_presets.cfg`; the export reads the raw `.wav` files like the editor run (`tools/sync-assets.py` keeps them).
