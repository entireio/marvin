# Apple renderer migration study

This is an opt-in prototype, not a switch of the playable game's renderer.
The normal app still uses SceneKit and its tested SimulationCore.

## Scope and controls

`--renderer-study OUTPUT` runs the SceneKit reference. Add `--realitykit` to
run the RealityKit adapter (macOS 15+). Both select the same grandstand block,
retain the track and four existing robot models, and use the same fixed starting
grid, fixed-step physics replay and moving camera. All selected geometry uses its
near LOD in both backends. Dust/trails are not updated in either study. The normal
full-city benchmark remains a separate, representative gameplay measurement.

The adapter converts SceneKit vertex/index buffers to RealityKit LowLevelMesh,
including colors, normals, UVs, material parts and generated tangent bases.
It ports the city's vertex tint shader, plaster/cloth/metal scans, clay color
shader, image-based lighting, directional light and per-node shadow eligibility.
It corrects AppKit texture orientation and texture tiling. Only moving entities
receive transform updates. SceneKit's view has its scene detached during the
RealityKit run so scene changes cannot trigger a second renderer behind it.

The adapter intentionally reuses the existing source assets rather than creating
a different, simpler demonstration. It is not a production asset pipeline:
SceneKit source objects stay in memory, runtime shader compilation reads public
headers from the installed Apple SDK, and lighting units/tone mapping/shadow
filtering differ. Packed material channels, sign emission, the clay multiply map,
and every robot-specific material effect are not yet fully matched. Consequently
performance differences are directional evidence, not a controlled claim that
one engine will render the finished game faster.

## Tool availability and measurements

Host: M2 MacBook Air, macOS 26.6.2. Command Line Tools are installed; full Xcode,
`xctrace` and the `metalperftrace` executable are unavailable. The newer SDK's
manual describes metalperftrace, but that does not make the executable available.
No Instruments Metal System Trace or GPU-debugger inspection is claimed.

The fallback uses `MTL_HUD_ENABLED=1 MTL_HUD_LOG_ENABLED=1`, collecting Apple's
HUD-reported GPU times and presentation intervals. Console HUD logging is legacy;
use metalperftrace when a matching tool installation is available. The summarizer
uses the last 35 seconds of each 45-second run to exclude setup and shader warmup.
Its sample distributions can contain repeated logged entries, so they are not
unique-frame counts or a substitute for a trace. HUD overhead remains enabled in
both measurements. App update counts are never reported as RealityKit FPS.

The first HUD-enabled RealityKit attempt crashed in
`HUDMTLLayerTracking safeAreaInsets` after replacing the window's content view.
The harness now retains the stopped SceneKit view in the window hierarchy while
removing its scene. Interrupted runs are excluded. This is a test-tool integration
issue observed on this OS, not evidence that game physics crashed.

### Full-city baseline

45 seconds, 1920×1080, all scenery and normal LOD enabled. SceneKit callback mean
59.97/sec, CPU update p95 2.52 ms. The final 35 seconds of HUD logs report GPU
median 13.72 ms, p95 15.43 ms, p99 16.02 ms. Presentation interval median/p95/p99
16.67 ms, maximum 33.33 ms. This leaves little GPU headroom at the expensive tail
of a 16.67 ms frame budget despite the approximately 60 FPS average.

Raw artifacts: `../marvin-town-planning/apple-renderer-baseline/`.

## Reproduction

Build with `apps/simulator-macos/build-app.sh`. Run each command separately; do
not compile, capture GPU frames, or run another rendering workload concurrently
with a timing measurement. The Android emulator was stopped before these runs.

```sh
app='apps/simulator-macos/.build/Marvin Simulator.app/Contents/MacOS/MarvinSimulator'
MTL_HUD_ENABLED=1 MTL_HUD_LOG_ENABLED=1 MARVIN_STUDY_SECONDS=45 \
  "$app" --renderer-study /tmp/marvin-study-scn > /tmp/marvin-study-scn.log 2>&1
MTL_HUD_ENABLED=1 MTL_HUD_LOG_ENABLED=1 MARVIN_STUDY_SECONDS=45 \
  "$app" --renderer-study /tmp/marvin-study-rk --realitykit > /tmp/marvin-study-rk.log 2>&1
python3 scripts/rendering/summarize-metal-hud.py /tmp/marvin-study-scn.log /tmp/marvin-study-scn/hud.json
python3 scripts/rendering/summarize-metal-hud.py /tmp/marvin-study-rk.log /tmp/marvin-study-rk/hud.json
```

For a separate capture attempt, add `--gpu-capture`, set `MTL_CAPTURE_ENABLED=1`,
and use a fresh output directory. Its timings must not be mixed into benchmark
results. Capture availability or failure is recorded in `study.json`.

## Migration gates

1. Keep the present game as the reference until the prototype matches sign
   legibility, skin/cloth colors, normal maps, sun shadows and racer materials.
2. Replace this conversion bridge with shared renderer-independent mesh/material
   descriptors or authored USD assets. Keep SimulationCore and its tests intact.
3. Restore equivalent distance LOD, then evaluate RealityKit mesh instancing for
   repeated building modules and crowd models (`MeshInstancesComponent` requires
   macOS 26; retain batching as the older-OS fallback). Existing static batches are the
   comparison baseline, not an instancing implementation.
4. Move presentation updates to the renderer/display clock while retaining fixed
   simulation steps. Verify pause, input latency and camera behavior independently.
5. Profile full races with Instruments and GPU captures on the minimum supported
   Mac, including a thermally sustained run. Target GPU p95 below 12–13 ms at
   1080p as a project headroom goal, not an Apple-mandated threshold.
6. Evaluate dynamic resolution/MetalFX only if full-scene measurements show a GPU
   bottleneck after batching/LOD. It is not enabled by this prototype, nor is it
   assumed to be a one-line switch in RealityKit.

The prototype requires macOS 15 for LowLevelMesh. The playable app still targets
macOS 13. A production migration therefore needs an explicit OS support decision
or a compatible alternative asset path.

## Apple references

- [SceneKit migration guidance](https://developer.apple.com/videos/play/wwdc2025/288/)
- [Game graphics performance guidance](https://developer.apple.com/documentation/metal/improving-your-games-graphics-performance-and-settings)
- [Metal HUD](https://developer.apple.com/videos/play/tech-talks/110339/)
- [RealityKit custom materials](https://developer.apple.com/documentation/realitykit/modifying-realitykit-rendering-using-custom-materials)

## Paired result and decision

Both runs completed 45 seconds at 1920×1080 with 863 meshes, 3,023,194 triangles,
and exactly 2,700 simulation steps; both ended at nominal thermal state (0).
The table reports distributions from the final 35 seconds of legacy HUD logs.

| Metric | SceneKit block | RealityKit block |
| --- | ---: | ---: |
| GPU median | 12.68 ms | 13.14 ms |
| GPU p95 | 16.29 ms | 15.47 ms |
| GPU p99 | 16.70 ms | 16.43 ms |
| Presentation interval median / p95 / p99 | 16.67 ms | 16.67 ms |
| Maximum logged presentation interval | 33.33 ms | 33.33 ms |
| Reported process memory peak | 1,438 MB | 2,325 MB |
| Reported Metal allocations peak | 2,455 MB | 3,448 MB |

Memory figures must not be interpreted as intrinsic engine overhead: the bridge
retains SceneKit source meshes and assets while also allocating RealityKit
resources. It is intentionally unsuitable for a production memory comparison.
Neither a small p95 improvement nor a small median regression establishes an
engine win from one short trial. Differences in shading and shadow behavior
further limit that inference. No automatic quality improvement is demonstrated.

The paired screenshots show matching composition, signs, people and silhouettes.
RealityKit remains darker, with different shadow softness, texture response and
missing sign emission. The corrected signs are upright and readable. These are
migration tasks, not reasons to replace the current game immediately.

**Decision: retain SceneKit as the playable reference; pursue a staged RealityKit
migration only behind the comparison flag until the gates above are met.** The
prototype proves that the same city assets and SimulationCore can run through
RealityKit. It does not yet justify making it the default renderer. Before adding
more city geometry, spend the next graphics iteration on lighting/material
parity, source-asset separation, equivalent LOD and instancing, then measure the
complete city. MetalFX remains deferred; this result does not justify it yet.

Artifacts (outside the repository):
- `../marvin-town-planning/renderer-study-scenekit/`: screenshot, study metadata, HUD summary.
- `../marvin-town-planning/renderer-study-realitykit/`: matching RealityKit artifacts.
- `../marvin-town-planning/apple-renderer-baseline/`: full-city baseline.

## Validation

The release app builds and signs successfully. All 29 SimulationChecks pass,
including the independent course-projection oracle and 30/60/120 Hz race checks.
The normal game's native town smoke passes layout clearance, city coverage,
street network, camera obstruction, crowd pause, reset and sign-text fitting.
Both comparison backends write screenshots and matching mesh/triangle/replay
counts. `git diff --check` passes.

GPU captures are collected in separate short runs, never in the timing runs.
The SceneKit capture contains approximately 711 MB of resources and command data,
but has not been opened in Xcode's GPU debugger here. An initial RealityKit
capture against the default Metal device produced an empty trace; merely writing
a `.gputrace` directory is not considered a successful renderer capture. The
harness now selects the actual device exposed by the renderer's Metal layer and
reports empty/unavailable captures explicitly.

The retry against the exposed RealityKit layer device also produced no command
data. RealityKit GPU frame capture and Instruments inspection therefore remain
unverified and require a full Xcode setup/attached capture workflow. The HUD
results above come from completed independent runs and remain available. The
capture diagnostic report is in
`../marvin-town-planning/renderer-capture-realitykit-device/study.json`.

## October 3: intermittent town frame pacing

On the current M2 MacBook Air (24 GB), two 120-second town drives at
1920×1080 with audio enabled reproduced occasional 33 ms render intervals.
The timer-driven runs missed two frames at midday and three at a lower sun
angle; matching display-link trials missed one each. CPU update P95 remained
about 2.8 ms. These small counts suggest a pacing improvement, not proof that
the timer caused every hitch or that 60 FPS is guaranteed.

The view’s display link at 60 Hz is available on macOS 14 and later through
`--display-link-updates` (or the existing `--benchmark-display-link`). The timer
remains the default and the driver for deterministic smoke fixtures.
`--benchmark-timer` explicitly forces it for comparisons. No shadow, geometry,
population or material quality was reduced. The benchmark reports its driver.

An additional 45-second diagnostic run used Apple’s Metal HUD logging. After
excluding startup, its log included a 33.33 ms presentation interval paired
with 13.49 ms GPU time; GPU time elsewhere reached 16.43 ms. This establishes
a displayed hitch and tight GPU headroom, but not a unique cause. HUD logs
contain duplicate batches/samples and must not be blindly counted as frames.
A short shadows-disabled run had no missed frame; that is only an isolation
lead, not permission to remove shadows. System stack sampling did not return
a report and was stopped; the installed Command Line Tools lack Instruments.

[Machine-readable comparisons](performance-validation/2026-10-03/investigation.json)
retain the limited diagnostic conclusions. Full callback timelines and Metal
logs remain under `/Users/thomi/Projects/marvin-town-planning/fps-debug/` and
`/tmp/marvin-metal-native.log`. These M2 measurements do not establish M4
performance, especially at a different drawable resolution.

The subsequent new-default validation ran for 120 seconds and again missed
two frames: 59.98285 FPS, P99 17.491 ms, worst 33.816 ms. This did **not**
reproduce a reliable reduction in missed frames. The clock change aligns updates
with display cadence; it must not be described as a verified stutter cure.

The independent Astra Ultra judge recommended retaining the timer default until
there is a repeatable benefit. That recommendation was applied. Visibility
changes now reset the elapsed-time anchor and stop hidden audio; termination
invalidates either driver. The normal-clock native lifecycle check
(`--display-link-lifecycle-check OUTPUT_DIRECTORY`) passes all 13 checks across
menu, asynchronous loading, pause/resume and actual minimize/restore. The
restored frame was inspected. This closes a display-link suspension hazard;
the reported intermittent town stutter remains unresolved.

### Live FPS overlay

Gameplay now shows a small bottom-right FPS readout, including manual town
exploration. It counts SceneKit render callbacks over half-second windows,
independent of the simulation timer, and clears old samples across window
visibility changes. It passes pointer input through and follows the existing
HUD hiding during automated postrace departures. This is render callback rate,
not GPU time or display-presentation timing. Native lifecycle validation also
checks that the FPS value is live and that the overlay does not intercept input.

### Ten-minute drive with the visible counter

A 603-second automated town drive (three-second warm-up plus ten measured
minutes) on the M2 MacBook Air, 24 GB, at 1920×1080 used the timer default,
clear midday lighting, normal shadows/detail, audio and the visible FPS overlay.
The benchmark now forwards renderer callbacks to the actual overlay and records
the value/text published by it, rather than reconstructing an approximation.
It collected 1,176 counter updates while travelling 1,339.7 m.

The readout did **not** stay at 60: its range was **40.7–60.3 FPS**. Overall
render callback rate was **57.23 FPS**. Minutes 1–4 averaged about 60; minutes
6–10 averaged 56.42, 55.83, 54.23, 52.73 and 53.60 FPS respectively. There were
1,662 render intervals above 25 ms, one above 50 ms, and a worst interval of
52.26 ms. The final captured readout was 57.9 FPS.

CPU update P95 remained 2.63 ms overall, and the final thermal state was fair
(raw value 1). Neither observation alone identifies the cause. The increasing
slowdown is materially worse than the short trials suggested; it must not be
dismissed as only isolated frame misses. This is an M2 result, not an M4 test.

[Recorded counter values and summary](performance-validation/2026-10-03/ten-minute-drive/summary.json)
are retained alongside the benchmark report. Full frame/update timelines and the
viewed native scene plus live-overlay capture are in
`/Users/thomi/Projects/marvin-town-planning/fps-ten-minutes/`.

### M2 profiling and production shadow geometry

The current performance target is solely the workspace M2 MacBook Air. Apple
Metal System Trace, attached for ten seconds after a warmed-up clear town drive,
separates recorded GPU Active intervals from the command-buffer envelope. Across
472 interior frames, the union of the app's Vertex/Fragment/Compute intervals
had median 13.658 ms and P99 15.637 ms; the envelope median was 16.580 ms.
Channels overlap and must not be added. These are instrumented diagnostics, not
exclusive GPU cycles or ten-minute acceptance. The preceding correlated capture
also exposed GPU-capture-layer CPU overhead before its frame capture began.
No thermal-frequency cause has been established.

Normal gameplay now enables the previously opt-in town shadow mesh. It retains
679,992 triangles, winding, sidedness and LOD geometry while welding exact equal
positions and merging opaque shadow material groups: 1,761,272 input vertices
become 417,484 and 661 geometry elements become 338. Camera-visible geometry,
material detail and the 4096/2048 shadow maps are unchanged. Unsupported geometry
keeps the original shadow path rather than terminating the game.

`--benchmark-shadow-batch-reference` selects the original shadow path. Independent
ground/mesh/shadow comparison tools retain control of their own reference pairs;
`--benchmark-shadow-batch-live` exercises production activation in those tools.
The release build, all 33 live reference comparisons (midday, low sun and storm),
caster synchronization, and default/reference 15-second activation checks passed.
All three native comparison contact sheets were inspected. Small rasterization
differences remain; the largest channel difference was 29/255 at a shadow edge.
This is not pixel identity or a moving-camera flicker acceptance result.

This is a partial optimization, not a sustained-60-FPS fix. Earlier isolated
measurements showed a small encoding/GPU saving; no new controlled ten-minute
clear/storm improvement or complete headroom result is claimed here.

Retained reports: [visual comparisons](performance-validation/2026-10-03/shadow-production/comparison.json),
[activation and quality configuration](performance-validation/2026-10-03/shadow-production/activation.json),
and [prior-build GPU diagnostic](performance-validation/2026-10-03/shadow-production/prior-build-gpu-diagnostic.json).
Full Instruments traces, native images, frame joins and independent audits remain
under `/Users/thomi/Projects/marvin-town-planning/sustained-fix/metal-warm-current/`
and `shadow-production-validation/` in that same artifact root.


### October 3: geometry attribution, still diagnostic

The frozen native visibility audit now inventories the initialized renderer and
checks exact pixel contribution by removing camera-visible town cells while
retaining independent shadow proxies. Baseline repeats, per-cell restoration and
combined restoration are checked; this does not establish a future-frame culling
rule. Two street viewpoints found no entire in-frustum architecture cell with
zero contribution. The corrected inventory's Town-cell frustum set agrees with
the removal audit (23 cells). Near/base primitive inventory at that view:
Marvin hierarchy 992,079; crowd cells 117,147; architecture 109,424. These counts
are not GPU submissions or selected LOD counts.

A release native M2 diagnostic at 1920×1080, 2× MSAA, clear midday, after a
15-second town drive used frozen SCNRenderer owned command buffers. Each ABBA
block retains 60 samples after 30 warmup frames. The explicit
`MARVIN_GPU_PRODUCTION_SHADOW_BATCH=1` retains the production shadow-mesh path
in these comparisons. Subsequent probes default to the live shadow path; explicit
`0` selects the historical reference. No frame capture was active; thermal state was nominal.

- Removing Marvin: baseline block median GPU envelopes 11.507/11.454 ms versus
  9.501/9.509 ms; CPU encoding 2.723/2.718 versus 1.491/1.492 ms. This is a
  net removal effect (including shadows, AO and newly exposed background), not
  an acceptable optimization or isolated vertex cost. CPU encoding is elapsed
  wall time.
- Single-sided Marvin materials: no repeatable timing gain and 23,494 changed
  pixels (maximum channel delta 40/255); rejected as an optimization.
- Removing motors, servos, bearings, battery and axis mount: baseline medians
  11.526/11.465 ms versus 11.143/11.143 ms. Removal changed 25 pixels (max 9),
  but baseline restoration changed 45 pixels (max 9). This is **not** proof of
  invisibility; retain all parts pending reliable multi-pose enclosure/appearance
  validation. No gameplay visibility or material default was changed.

Measurements precede remote wall-fix commit `1c8bb11` and use `db1be0f` plus
uncommitted diagnostic code and existing opt-in prototypes. The internal-parts
run includes a binary hash. Raw timing samples and the corrected inventory are
in [geometry attribution](performance-validation/2026-10-03/geometry-attribution/).
Local images remain under `marvin-town-planning/sustained-fix/` in the matching
run directories. These short frozen tests do not prove live presentation,
sustained 60 FPS, or the 12.5 ms P99 headroom goal. Next investigate actual CAD
pass costs and safe hidden geometry before accepting any removal.

Post-merge release build and native default-path verification passed: with no
shadow-path environment override, the report records productionShadowBatch=true.
Its ABBA GPU medians were 11.316/11.260 ms baseline versus 9.361/9.358 ms with
Marvin removed, consistent with the earlier net removal result.


### October 3: direct CAD enclosure and controlled index experiments

Following the user's correction, geometry is analyzed before using full-scene
rendering as regression evidence. Native Rhino and tessellated STEP audits found
open/nonmanifold exterior meshes, so global point-in-solid classification is not
valid without additional proof. Bounding boxes and sampled rays are insufficient.
One direct neutral-CAD ray reaches Motor_Left from below; this disproves complete
enclosure of that whole part in the exported CAD (procedural runtime belts are
not part of this ray test).

Exact Float32 position+normal comparison found 6,114 same-winding duplicate
triangles. A stronger enclosure certificate identifies 27,756 triangles wholly
inside two retained convex `Body` components: Motor_Left 13,412; Motor_Right
12,518; Servo_Head 1,614; 01_body 212. All 504 enclosing Body triangles remain.
The exact rational/integer proof checks closed topology, convex halfspaces and
at least 0.01 mm clearance. These parts share the same rigid parent and opaque
materials. Astra Ultra reviewed the corrected exact-representability and margin
predicates. This is geometric containment, not pixel or performance acceptance.

Native diagnostic comparisons use clear midday town, M2, 1920×1080, 2× MSAA,
production shadow batching, 15 seconds of driving followed by frozen ABBA blocks.
Each block retains 60 GPU/encoding samples after 30 warmup frames. Runtime source
geometry and original/candidate index hashes bind every loaded candidate to its
offline verification manifest. An original-index geometry reconstruction control
separates reconstruction effects from changed index content.

- Original-index reconstruction: exact images in all three experiments.
- Vertex-cache index reorder: 11.142/11.162 ms versus bracketing original
  11.324/11.333 ms GPU medians. 780 pixels changed, maximum channel delta 10/255;
  unchanged triangle tuples/winding do not imply identical depth-tie pixels.
- Exact duplicate removal: no consistent GPU benefit (11.600/11.514 versus
  11.497/11.517 ms); 16 pixels changed by at most 1/255. Not promoted.
- Certified enclosed triangles: 11.368/11.364 versus 11.416/11.436 ms;
  about 0.06 ms difference is too small to establish sustained benefit.
  74 pixels changed by at most 4/255. Not promoted.

The candidate images were inspected. All restored production images match their
initial references exactly in these three tests. Raw timing samples and pixel
counts are in [CAD geometry evidence](performance-validation/2026-10-03/cad-geometry/).
No production geometry changed, no ten-minute acceptance result is claimed, and
all earlier full-quality sustained/headroom requirements remain open.

Next larger attribution target: Marvin's 224 separate shoe/rib geometry nodes.
They contain 325,248 triangles; an exact-pose batching experiment must preserve
those triangles and the coating's original rest coordinates, then measure actual
draw, encoding and GPU changes before attempting animated batching.

The reusable verifier is `scripts/rendering/check-cad-enclosure.py` (Python 3.10+
and NumPy). Run:

```sh
python3 scripts/rendering/check-cad-enclosure.py --self-test --output /tmp/marvin-enclosure
```

It derives the shells directly from current
runtime assets, writes an exact containment certificate and original triangle
indices, and never modifies the assets. Negative fixtures cover open/nonconvex
shells, winding/duplicate failures, boundary/insufficient clearance, a triangle
whose centroid is inside but a vertex is outside, and oblique planes with unequal
non-unit coordinate scales. The saved run passed and reproduces 27,756 triangles.
Astra Ultra approved the repeatable geometric proof for an isolated candidate;
runtime opacity and relative transforms remain explicitly reviewed conditions.
Source hashes bind evidence to files but do not enforce those conditions.

The frozen tread batching prototype (`MARVIN_GPU_VARIANTS=track-batch-control,track-batch`
with `--benchmark-gpu-probe`) retained all 325,248 triangles and 263,424 vertices,
combining 224 shoe/rib nodes into four batches. Original-geometry reconstruction
controls were pixel-identical. CPU encoding medians fell from about 2.7 ms to
1.705/1.706 ms, but GPU medians were 11.612/11.594 ms versus bracketing production
11.620/11.496 ms: no established GPU improvement. Candidate images changed 2,365
pixels by at most 6/255; restoration changed 30 pixels by at most 4/255, so strict
restoration equivalence failed. The saved candidate image was inspected. No
animated batching or production change is accepted from this experiment. Raw
samples, image comparison and the exact binary hash are saved beside the CAD
reports. CPU submission savings remain a separate possible follow-up.

A follow-up corrected the prototype's world-coordinate round trip by composing
shoe/rib local transforms directly. The rebuilt native run confirms zero spurious
shoe X displacement. CPU encoding remains about 1.71–1.73 ms versus 2.71–2.75 ms;
GPU medians are 11.573/11.612 ms versus bracketing 11.425/11.465 ms, so this test
regresses GPU time slightly. It changed 2,226 pixels by at most 7/255; copy controls
and early restored baselines differed by two pixels at 1/255, and final restoration
by 35 pixels at up to 7/255. Exact visual controls therefore still fail. The native
candidate image was inspected. The committed prototype uses the corrected local
transforms, remains diagnostic-only, and is not a validated gameplay optimization.

### October 3: captured ground-texture controls

The next experiment targets texture traffic, preserving the original diffuse RGB
and linear roughness bytes at every mip. Xcode exports supplied all twelve levels;
diffuse levels 0–5 and roughness levels 0–4 match the capture's corresponding raw
texture resources byte for byte. Smaller levels have no independent raw reference.
The opt-in `GroundTexturePackProbe` checks asset/shader hashes, component masks,
sampler properties and original opaque alpha before loading explicit Metal textures.
It does not alter normal gameplay.

The initial native controls **failed before packing was attempted**. At the frozen
15-second clear-town pose, replacing just diffuse with the explicit original bytes
changed about 961,000 pixels (maximum channel delta 26/255); adding the explicit
roughness texture produced the same image. The captures were inspected: ground
near the track fade darkens, while distant ground remains unchanged. These are
not valid equivalent-rendering timing comparisons and no speedup is accepted.

A new production/control Metal capture identified the cause. Main ground draw
12375 uses pipeline 105 / library 32 in production and pipeline 123 / library 43
with explicit diffuse. Both use `USE_PBR_TRANSPARENCY`, but only the original URL
binding uses `DIFFUSE_PREMULTIPLIED`. In the captured `prepareForPBR`, absence of
that flag multiplies albedo by diffuse alpha again. Original vertex alpha is
nonunit near the track transition. Explicit byte identity therefore does not
preserve SceneKit's material interpretation. Shader flags, raw samples and control
comparisons are in [ground texture evidence](performance-validation/2026-10-03/ground-texture-controls/).

Any follow-up must first preserve that material interpretation and pass the
explicit-texture and restoration controls. Aliasing two material properties to
one texture does not itself prove removal of a texture fetch. No texture packing,
quality reduction or sustained-performance acceptance follows from this test.
