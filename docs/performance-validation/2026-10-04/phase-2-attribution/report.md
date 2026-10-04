# Phase 2 native rendering attribution — resumed October 4, 2026

**PARTIAL.** The Mac was unlocked and visible gameplay recording succeeded, but
neither 600-second export retains an early independent START anchor. The second
attempt retains only START refreshes from 420 seconds onward. GPU preflight has
real commands/resources but unusable replay: the first failed with buffer creation
errors/segmentation fault 11; the device retry has invalid depth data and runtime
issues even at its final command-buffer commit. No dependent matched pair was
collected. Definition-of-done items 1–3 remain missing; CPU correction and hashed
evidence are delivered. These failures are distinct from the earlier locked Mac.

Resumed timebox began **08:21:36 PDT / 15:21:36 UTC**, with a 10:21:36 PDT maximum
and a 10:01:36 PDT collection cutoff. Last collection ended about 09:10:11 PDT.
GPU collection stopped after two preflight attempts; later inspection only checked
the retained capture. Preflight/UI repair took about 21 minutes, slightly over its
20-minute allocation. Two long presentation attempts and one periodic-anchor
repair were bounded; neither is upgraded to validated evidence. No optimization,
performance improvement or visual/audio acceptance is claimed.

## Conditions and preserved quality

Worktree `/Users/thomi/Projects/marvee-perf-phase-2-attribution`, branch
`codex/mos-aster-perf-phase-2-attribution`, directly based on
`e9fcc184dfbe9c425bf8f1b9714d5637c2301a13`. The original checkout is untouched.
Authorized Apple M4 MacBook Air (Mac16,12), 32 GB; macOS 27.0 (26A428), Xcode 27.0
(27A266a). The M4 policy correction and Phase 1 CPU correction are retained.
No inference across GPU generations.

AC power, low-power mode off. Studio Display is the gameplay display: window
{{384,527},{960,540}} points, 2× backing; internal display also connected.
Refresh rates remain UNKNOWN because the retained hardware log did not expose them.
MacBook Air Speakers, stereo/48 kHz; native audioActive=true, without independent
listening acceptance. Scoped idle prevention did not alter lock/security settings.
Both long runs' first resource samples were nominal and final samples fair;
these are initial in-run samples, not a controlled prelaunch cooling measurement.

Native manifests retain **1920×1080, 2× MSAA, AO .7/1.6/.025, 2048/4096 shadows,
full exploration detail and audio**. All 217 packaged resources (178,247,127 bytes)
match Phase 1 exactly. Full population/geometry/effects remain in the source and
native manifests; no quality reduction was implemented. Runtime settings do not
substitute for the missing direct attachment inspection. Only benchmark/capture
instrumentation and documentation changed. Phase 1 clean clear/storm results are
reused: **56.415/56.185 callbacks/s**, not presentation rates.

## Presentation evidence and limits

The repaired 35-second preflight passes: launch, 10-second and 30-second START
markers plus END, matching UUID/PID/boundaries, **20.458 µs** clock-origin spread,
1,920 measured display-start intervals and full 32-second boundary coverage.

The first 603-second attempt (`overview-visible`, UUID B4909AE7-5EA1-494F-9128-4DE7EC96E49A, PID 92801)
retains only END. The second (`overview-periodic`, UUID 474025AE-E908-4B58-84B3-990837481DA2, PID 94717)
retains seven START refreshes at 420–600 seconds plus END. Their **7.292 µs** origin
spread verifies late offset consistency only. Original/early START markers are
absent in both raw os-signpost and derived PointsOfInterestEvents exports. Late
markers retain the original native boundary, but cannot verify its early alignment.
The validator therefore rejects both; it explicitly requires an early emission
within 33 seconds. The loss mechanism is UNKNOWN, not proved to be buffer pressure.

Both exports identify one SCNMetalBackingLayer gameplay layer, have contiguous
frame IDs, nonempty intervals and starts bracketing the inferred 600-second
window. Second layer ID: **529775797888**. Each long run has 604 consecutive
resource seconds, all active, visible, unoccluded and non-minimized. Transition
observers record only start/end, with no foreground, occlusion or lock transition.
Independent UI observations show gameplay. The absent CGSession lock field stays
null/UNKNOWN. Main-queue notification receipt times and one-second samples do not
prove an independent continuous lock history. Largest second-run sample gap:
**1.033 seconds**.

No lost/drop/overflow warning was found in the exported PointsOfInterest OS logs.
That is a narrow check: early anchor loss itself shows incomplete event retention.
There is no independent hardware lost-event detector. Contiguous layer frame IDs
support frame continuity, but do not repair clock alignment.

**Diagnostic statistics only**, using the late clock offset extrapolated back to
the native boundary; not an accepted whole-window presentation measurement.
First attempt: 35,691 display starts / 600 s = **59.485/s**, 295 intervals >25 ms,
worst rolling ten seconds **49.4/s**. Second: 35,357 / 600 s = **58.928/s**, P95
**16.667 ms**, P99 **33.333 ms**, worst **66.667 ms**, **629** >25 ms holds; worst
rolling ten seconds **47.0/s**, starting at measured second 569.

| Minute | Display starts/s | P95 ms | P99 ms | Worst ms | >25 ms holds |
|---|---:|---:|---:|---:|---:|
| 1 | 60.000 | 16.667 | 16.667 | 16.667 | 0 |
| 2 | 59.950 | 16.667 | 16.667 | 33.333 | 3 |
| 3 | 60.000 | 16.667 | 16.667 | 16.668 | 0 |
| 4 | 60.000 | 16.667 | 16.667 | 16.667 | 0 |
| 5 | 60.000 | 16.667 | 16.667 | 16.667 | 0 |
| 6 | 59.233 | 16.667 | 33.333 | 33.333 | 46 |
| 7 | 56.517 | 33.333 | 33.333 | 66.667 | 202 |
| 8 | 58.133 | 16.667 | 33.333 | 50.000 | 109 |
| 9 | 59.150 | 16.667 | 33.333 | 33.333 | 51 |
| 10 | 56.300 | 33.333 | 33.333 | 50.000 | 218 |

Intervals are assigned by display-start time, quantiles use the existing parser,
and rolling windows advance one second. GPU Begin-to-End and CPU Begin-to-Present
exports are elapsed envelopes, not exclusive busy time or CPU execution. These
instrumented runs cannot replace the Phase 1 clean baselines or prove a thermal
cause for the late slowdown.

## GPU preflight diagnostics

Apple's MetalToolchain 27A266a was installed through the official Xcode component
flow. The first queue capture failed replay with segmentation fault 11, buffer
creation failures and 44 additional errors. The repair captures the renderer's
actual device from updateAtTime, before scene rendering, and records actual
presentation-node player/camera transforms, projection, waypoint and callback
uptimes for three willRender callbacks. Those callbacks still require mapping to
complete submitted frames in Xcode; they are not certified native frame IDs.

The device retry contains four command buffers, eight render encoders, four compute
encoders, 208 blit encoders, 2,085 draw calls and six dispatches. Whole-capture
textures/buffers total 704.95 MiB / 1.09 GiB; summary drawable is 1920×1080
BGRA8Unorm_sRGB. These are whole-capture diagnostics, not a corresponding per-frame
inventory. Replay was Debug Workload, Profile-after-replay unchecked; no replay
performance values are accepted. Initial newBufferWithBytes calls and command
buffers 3/4 carry RuntimeIssue; final commit API 13782 also does. Filtering for
present APIs returned none, which is not proof of absent native presentation.

Selected command buffer 3 / render encoder 1 has viewport/scissor 4094×4094,
Depth Texture 5 Clear/Store (64.50 MiB). API 790 submits Triangle/UInt16, 4,356
indices (1,452 triangles), Buffer 885 offset 0, pipeline commonprofile_vert /
commonprofile_frag, GreaterEqual depth with writes. Subsequent examples submit
36 indices against shared Buffer 887 with changing vertex offsets. The depth
viewer warns **“The image contains invalid data.”** These observations establish
encoded draw payload, not pass cost, complete shadow validation, or draw growth.
No generic Render Command ordinal is treated as stable pass identity.

Full color/depth/MSAA/AO/both-shadow inspection, corresponding top-draw/geometry
inventory, actual early/loaded matching and profiling results remain missing.
Acceptance tolerances were declared before capture: positions ≤.10 m, orientations
≤1°, identical route waypoint/camera mode and projection coefficients within 1e-6.
They were not met or tested by a pair. Upload volume, GPU frequency and exclusive
GPU busy counters remain UNKNOWN.

## Corrected Phase 1 CPU interpretation

The earlier singled-out worker omitted nine early drawing workers. The aggregator
selects every non-main thread with observed SceneKit `drawRenderElement:withPass:`
stacks. It sums all sampled weight on those workers and separately sums only
draw-containing stacks. All six source exports match their retained Phase 1 hashes.

| Statistical metric | Early | Loaded |
|---|---:|---:|
| Finalized trace duration, seconds | 11.045617 | 12.329932 |
| Recorded target encoder frame ordinals | 631 | 411 |
| Observed drawing workers | 10 | 1 |
| Aggregate worker sample weight, seconds | 5.856 | 9.986 |
| Draw-inclusive sample weight, seconds | 3.602 | 8.425 |
| Worker weight per trace second | .5302 | .8099 |
| Draw-inclusive weight per trace second | .3261 | .6833 |
| Worker sampled ms per recorded frame | 9.281 | 24.297 |
| Draw-inclusive sampled ms per recorded frame | 5.708 | 20.499 |

The draw-inclusive increase is **2.10× per trace second**, or **3.59× per recorded
frame**, instead of the roughly 18× single-worker comparison. Frame normalization
uses unique encoder ordinals, including possibly partial boundary frames. It is
statistical weight, not measured per-frame encoding latency. The early/loaded
sample-time bounds are .329154–11.045130 / .397366–12.329364 seconds.
`_executeDrawCommand:` weights (3.378/7.966 seconds) are nested within drawing;
do not add them. Parallel worker weights can exceed elapsed time.

`townMS` includes main-thread audio after town work. `physicsMS` includes benchmark
bookkeeping, effect-toggle checks and input setup before physics. Both are elapsed
stage wall spans. Audio IO sampling is separate. The Phase 1 report is corrected
in place with a dated correction preserving the historical evidence limits.

This supports investigating SceneKit submission/drawing work. It identifies no
specific pass, draw, trail cost, upload cost or causal thermal mechanism. Locked
screen, mismatched pose, profiler overhead and device state remain confounds.
Callback cadence, tracked presentation intervals, CPU encoding wall spans, GPU
elapsed envelopes and exclusive GPU busy time remain separate measures.


## Requirement status and next bounded experiment

| Requirement | Status |
|---|---|
| Requested source/worktree and current M4 policy | VERIFIED |
| Build, strict codesign, benchmark execution and unchanged packaged assets | VERIFIED |
| Runtime full quality/population/effects/audio settings | VERIFIED; direct GPU attachments MISSING |
| Display/power/audio output and initial in-run thermal state | VERIFIED; refresh rate/listening acceptance UNKNOWN |
| Short visible overview, early START/END identity and frame coverage | VERIFIED |
| Long visible gameplay, sampled state/transition observers and contiguous frame IDs | VERIFIED |
| Validated 600-second export with early independent START alignment | MISSING in both attempts |
| Per-minute/holds/rolling presentation statistics | DIAGNOSTIC ONLY: clock-alignment limitation above |
| Native GPU command/resource payload | VERIFIED |
| Usable Xcode replay / two accepted pose-matched captures | MISSING: replay preflight fails |
| Full attachments and corresponding pass/top-draw/geometry inventory | MISSING |
| Aggregated workers, duration/frame normalization and stage correction | VERIFIED |
| Upload volume / GPU frequency / exclusive GPU busy time | UNKNOWN |
| Audible/visual parity acceptance | UNKNOWN; no optimization attempted |
| Retained raw failures, reproduction and SHA256 inventories | VERIFIED |

Next bounded experiment: a **20-minute preflight repair on the same M4/Xcode**,
with the unchanged full-quality scene. Test capturing from before SceneKit resource
initialization through a confirmed completed/presented frame, and inspect whether
the observed buffer-creation/runtime errors disappear. In parallel, test an
independent live POI stream started before process launch so an early START event
is preserved and can be checked against the exported trace clock. This is a
capture-lifecycle/clock-transport hypothesis, not a rendering optimization or a
proven diagnosis. Accept neither a written .gputrace nor late-only anchors.
Only after both preflights pass should the declared pose-matched pair investigate
whether submitted draw/geometry increases with normalized CPU drawing weight.

Raw roots: the historical `phase-2-attribution-2026-10-04` and resumed
`phase-2-attribution-resumed-2026-10-04` under
`/Users/thomi/Projects/marvin-town-planning`. See summary.json, cpu-correction.json,
artifacts.json, resumed-artifacts.json and reproduction.md. Historical locked
failure is preserved separately. Current session:
`01a106c0-a5a6-7511-8367-44d0128b05a6`. Git/Entire delivery is verified at handoff.

Supported `entire session attach --agent codex --force` reused this session's
checkpoint `01M43CTKV3S66VSB9SARDJ6NKK`, created at 12:04:41 UTC. It correctly
links the current session but does **not** capture the resumed 15:21 UTC work.
The checkpoint snapshot remains historical; no fresh-transcript claim is made.
