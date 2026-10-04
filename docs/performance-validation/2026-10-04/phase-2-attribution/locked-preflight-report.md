# Phase 2 native rendering attribution — October 4, 2026

**PARTIAL.** Presentation preflight failed while the Mac was locked: the finalized
25-second overview retained matching START/END anchors but exported **zero frame
interval rows**. Direct UI checks could not unlock the Mac. The subsequent
10-second telemetry check independently recorded lock=true, appActive=false and
windowOcclusionVisible=false in all 11 samples. Following the task's stopping
rule, no matched GPU pair or 600-second presentation run was collected. GPU replay,
attachments and pass/draw attribution remain unverified. The lock is an observed
condition, not a proven cause of the missing rows.

Collection began at 04:51:11 PDT (11:51:11 UTC), with a 06:51:11 PDT maximum stop.
Dependent collection stopped at 05:00:08 PDT, within the 20-minute preflight bound.
The remaining work corrected CPU interpretation and prepared reproducible evidence.
No performance improvement or visual/audio acceptance is claimed.

## Source and conditions

- Worktree: `/Users/thomi/Projects/marvee-perf-phase-2-attribution`.
  Branch: `codex/mos-aster-perf-phase-2-attribution`, based directly on
  `e9fcc184dfbe9c425bf8f1b9714d5637c2301a13`. The source checkout was clean.
- Current authorized hardware: Apple M4 MacBook Air, Mac16,12, 32 GB. The outdated
  M2-only rule is corrected in this task branch. No cross-generation inference.
- AC power, low-power mode off; main Studio Display and internal display present.
  Telemetry places the 960×540-point window on Studio Display at 2× backing,
  producing the reported 1920×1080 drawable. Visible display placement could not
  be reviewed. Current display refresh rates are UNKNOWN; this hardware log did not expose them.
- MacBook Air Speakers, stereo, 48 kHz. Native manifests report audio active;
  output was not independently heard. Telemetry smoke began nominal and remained
  nominal; the overview's initial resource sample and end were fair.
  No prelaunch cooling/thermal measurement was completed.
- Scoped `caffeinate -di` prevented idle sleep during work; it did not unlock the
  screen. No lock/security setting was weakened. Xcode accessibility text was
  reachable, but did not certify an unlocked desktop; direct UI checks and native
  telemetry establish the failure.
- Both native manifests report 1920×1080, 2× MSAA, full exploration detail,
  2048/4096 shadow maps and AO (.7 intensity, 1.6 radius, .025 bias). These are
  runtime settings, **not inspected GPU attachments**. All 217 packaged resource
  files (178,247,127 bytes) match Phase 1 byte-for-byte. Normal gameplay and assets
  are unchanged. The only executable change is opt-in benchmark visibility logging.
- Phase 1 clean clear/storm 600-second measurements are reused unchanged. Their
  56.415/56.185 callbacks/s remain callback cadence, not presentation rates.

## What the preflight proves

Overview UUID `5904EF09-57F5-4943-A92C-3BAA61D54A6F`, PID 69785, finalized trace
duration 38.270257 seconds. Original START, refreshed START and END survive with
matching UUID/PID and native boundaries. Their independent clock-origin spread
is **32.833 microseconds**. Per-frame interval rows: **0**. Aggregate layer data
and successful export cannot satisfy presentation coverage. No presentation
cadence, percentiles, holds or rolling-window statistics can be computed.

The release build, strict deep codesign verification and 10-second native telemetry
check passed. All telemetry samples report the window non-minimized and logically
visible but occluded, with the session lock flag true. Sampling once per benchmark
second does not prove continuity between samples. An absent OS lock field is
recorded as null/UNKNOWN. Only that field is retained from the session dictionary.

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
| Scoped idle prevention and sampled lock/foreground/occlusion logging | VERIFIED |
| Native quality/settings and unchanged packaged assets | VERIFIED |
| Short overview independent UUID/PID/clock anchors | VERIFIED |
| Short visible overview with gameplay frame rows | MISSING: locked, zero rows |
| Xcode native capture command/resource payload and replay | MISSING: locked UI |
| Actual rendered pose/projection/route matching for two captures | MISSING: collection stopped |
| Direct color/depth/MSAA/AO/both-shadow attachment inspection | MISSING |
| Corresponding pass/top-draw/geometry inventory and replay settings | MISSING |
| Visible 600-second presentation coverage and continuity statistics | MISSING |
| Aggregated workers, window/frame normalization and stage correction | VERIFIED |
| Native upload volume, GPU frequency, exclusive hardware busy counters | UNKNOWN |
| Audible/visual parity acceptance | UNKNOWN: no optimization attempted |
| Raw failure artifacts and reproducible SHA256 inventories | VERIFIED |

The next experiment is the same bounded **unlocked, pose-matched early/loaded
native frame attribution**, after both preflights pass. It should test whether
submitted draws/geometry increase as normalized CPU drawing weight increases.
Predeclare ≤0.10 m player/camera position error, ≤1° heading/view-direction error,
identical route waypoint/camera mode, identical aspect and projection coefficients
within 1e-6. Select actual rendered frames, including presentation-node transforms
and capture timestamps; elapsed 60/210 seconds alone is insufficient. These
tolerances are proposed before future collection, not met in this phase.

The first visible preflight must pass before any sustained recording. A short
GPU capture must replay with commands/resources before collecting the matched
pair. Inspect pass identity through attachments/pipelines/draws and record replay
settings; generic `Render Command 8` numbering is not pass identity. Missing busy,
upload or frequency counters stay UNKNOWN. No optimization is selected without
the missing attribution evidence. A fresh resumed timebox requires manual unlock.

Raw evidence: `/Users/thomi/Projects/marvin-town-planning/phase-2-attribution-2026-10-04`.
See `artifacts.json`, `summary.json`, `cpu-correction.json` and `reproduction.md`.
Current Codex session: `01a106c0-a5a6-7511-8367-44d0128b05a6`. Delivery commit is
`git log -1 --format=%H -- docs/performance-validation/2026-10-04/phase-2-attribution`;
Git and Entire remote verification are reported in the final handoff.
