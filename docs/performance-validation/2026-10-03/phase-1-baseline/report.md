# Mos Aster phase 1 baseline — 2026-10-03

**Partial.** Two independent uninstrumented 600-second native runs completed at production quality on the **M4 MacBook Air explicitly authorized by the user**. This is an M4 baseline, with no M2 extrapolation. The first Apple presentation recording finalized and exported useful gameplay-layer intervals, but retained only the end POI. A repeat finalized with zero per-frame rows. Short Metal traces finalized and exported; a GPU capture bundle was written, but Xcode replay, attachment inspection, and draw-cost export remain unverified because the Mac locked. Missing mandatory evidence prevents completion; slow cadence by itself does not.

Work began 17:15:16 UTC. The 120-minute stop is 19:15:16 UTC. Setup/build and both clean runs fit the first 35 minutes; presentation collection/export and a bounded diagnostic marker attempt consumed additional time. Long Metal finalization and the locked UI prevented the remaining Xcode inspection. No dedicated Audio System Trace or further full-run retry was attempted.

## Provenance and unchanged quality

- Branch: `codex/mos-aster-perf-phase-1-baseline`; isolated checkout `/Users/thomi/Projects/marvee-perf-phase-1-baseline`.
- Reviewed source: `057f52ecbad7e4d33047004b09ad1ed68bafe1c5`. The performance-experiments branch was not used as source.
- Clean release executable SHA256: `3485a9a014c7aad8ecfa7650f161cc15dd2c3f8af99a6d9fe68ca183cf184ab3`. Archived outside Git as `clean-baseline-MarvinSimulator`.
- Bundle assets: 217 files, 178,247,127 bytes; sorted inventory SHA256 `522ae099997b9dbc45800b9a0cf5b9d8c8dd5c5d846986e5c924a772c8e53b2c`. LFS fsck and strict deep signature verification passed.
- Diagnostic release executable SHA256: `ee87d94bdb1ce49e92af94c1df8753c16f358c115690d9834dd5fcf4c9143631`. The only source change adds benchmark-only AO reporting and an opt-in repeat start signpost at 10 seconds. No normal gameplay path, optimization, asset, geometry, population, effect, audio, or detail change. All resource hashes match the clean build. See diagnostic-build.json.
- Hardware: Apple M4, MacBook Air Mac16,12, 32 GB, 10 CPU cores (4 performance/6 efficiency). macOS 27.0 (26A428), Xcode/xctrace 27.0 (27A266a), `/Applications/Xcode.app/Contents/Developer`.
- AC power, low power mode off; dual 60 Hz displays (main Studio Display and internal Color LCD; configuration sampled after collection, app display placement not independently logged). Built-in stereo MacBook Air Speakers, 48,000 Hz. Audio was active in both clean manifests. Audio output correctness was not independently listened to.
- Native manifests confirm **1920×1080**, **2× MSAA**, full exploration detail, and **2048/4096** shadow maps. AO defaults remain intensity .7, radius 1.6, bias .025; later diagnostic runtime reporting confirms these within Float precision. Actual GPU attachment inspection is pending.
- Town manifest unchanged: 497 buildings, 282 people, 34 walkers, 492 accessible compounds, 92 cells; production storm policy naturally lowers visible population. No reduced-population override was used.

Clean launches used an `env -i` whitelist (HOME, USER, TMPDIR, PATH, DEVELOPER_DIR). Runner set benchmark duration 603, daylight .5, storm 0/1. No GPU HUD, capture layer, Instruments, benchmark effect toggle, or other simulator was active. Start thermal samples were nominal; storm followed about 2.5 minutes of cooling. Defender/WindowServer and other normal desktop workloads remained present and were recorded, so this is not an idle-machine laboratory result. Low-overhead process CPU/RSS and system swap polling ran every 15 seconds; its overhead was not separately calibrated. Clear polling began about 51 seconds after launch, storm about 24 seconds.

The screen locked at **11:16:09 AM PDT**, after the clean runs and first overview. Those runs precede the observed lock event. Continuous foreground/occlusion telemetry was not collected; this limits visibility certification. The second overview and all detailed captures were locked-screen diagnostics. The saved native FPS images were inspected. They do not prove continuous on-screen presentation. Future runs should use scoped idle prevention and independently record visibility.

## Clean-run results

These rates count **SceneKit didRenderScene callbacks**, not actual presentations. Windows are `[startUptime+3, startUptime+603)`, exactly 600 seconds. Callback-end timestamps select intervals; boundary-crossing gaps are retained conservatively. P95/P99 use floor((n−1)q). Rolling ten-second windows step one second. CPU update timing is elapsed time inside the simulation update, excluding SceneKit rendering and audio IO thread execution. All ledgers are finite, monotonic, and cover both boundaries; interval deltas agree with timestamps.

| Metric | Clear | Storm |
|---|---:|---:|
| Callbacks/s | 56.415 | 56.185 |
| P95 callback interval, ms | 25.607 | 24.273 |
| P99 callback interval, ms | 33.819 | 33.968 |
| Worst callback interval, ms | 85.873 | 1023.054 |
| Intervals >25 ms | 1896.000 | 1468.000 |
| Worst rolling 10-second callbacks/s | 44.100 | 45.100 |
| CPU update P95, ms | 3.928 | 4.632 |
| CPU update P99, ms | 4.924 | 5.539 |
| Worst CPU update, ms | 16.096 | 125.258 |

Clear UUID `744EA611-2A4F-4476-B3DF-EE7FA1DA884A`, 10:25:59–10:36:02 PDT. Storm UUID `972948C7-F94C-41ED-BCAE-6A3E3E46AE40`, 10:39:26–10:49:29 PDT. Both native processes exited successfully. Both sustained performance gates failed cadence criteria. That is useful slow-baseline evidence. Storm also contains a 1,023-ms callback gap around elapsed 395 seconds, with adjacent update gaps; it was retained rather than filtered. CPU, OS scheduling, presentation/lifecycle or GPU origin is unresolved.

### Clear per-minute measured window

| Minute | Callbacks/s | P95 ms | P99 ms | Worst ms | >25 ms | Update P95 ms |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 60.000 | 17.482 | 18.144 | 26.971 | 1 | 2.261 |
| 2 | 60.000 | 17.333 | 17.764 | 27.674 | 1 | 2.109 |
| 3 | 55.917 | 27.465 | 33.823 | 42.531 | 235 | 4.000 |
| 4 | 50.983 | 33.615 | 35.114 | 46.757 | 554 | 4.447 |
| 5 | 52.683 | 29.280 | 34.204 | 42.541 | 441 | 4.206 |
| 6 | 57.250 | 23.837 | 28.634 | 34.820 | 115 | 3.855 |
| 7 | 58.000 | 22.944 | 33.120 | 35.102 | 107 | 3.581 |
| 8 | 58.600 | 21.057 | 32.178 | 34.919 | 76 | 3.411 |
| 9 | 55.283 | 25.302 | 34.804 | 85.873 | 184 | 4.162 |
| 10 | 55.433 | 25.194 | 31.957 | 36.933 | 182 | 4.243 |

### Storm per-minute measured window

| Minute | Callbacks/s | P95 ms | P99 ms | Worst ms | >25 ms | Update P95 ms |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 59.967 | 17.578 | 17.999 | 32.225 | 1 | 2.012 |
| 2 | 59.017 | 19.264 | 27.026 | 44.468 | 53 | 4.064 |
| 3 | 48.900 | 32.198 | 36.368 | 57.056 | 357 | 5.494 |
| 4 | 53.317 | 31.440 | 34.909 | 46.622 | 254 | 4.668 |
| 5 | 54.283 | 25.751 | 34.237 | 39.902 | 220 | 5.005 |
| 6 | 54.100 | 26.700 | 34.190 | 49.352 | 230 | 4.731 |
| 7 | 55.583 | 28.198 | 34.408 | 1023.054 | 189 | 3.758 |
| 8 | 59.300 | 17.711 | 32.591 | 35.144 | 44 | 2.919 |
| 9 | 59.500 | 17.796 | 20.341 | 35.215 | 30 | 3.143 |
| 10 | 57.883 | 21.421 | 33.289 | 44.345 | 90 | 4.177 |

Thermal history: clear nominal→fair at elapsed **83.010 s**; storm at **76.008 s**, remaining fair through completion. No serious/critical state observed. This state is coarse and does not prove clock throttling. Worst rolling windows begin at benchmark elapsed 211 s (clear) and 265 s (storm).

Clear process RSS first/last/peak was 2305.9/2372.8/2375.8 MiB; storm 2772.1/2316.1/2772.1 MiB. Sampled system swap was zero throughout. Process CPU percent medians were 99.5/103.5% (100% is one core). These counters neither measure GPU pressure nor exclude transient memory pressure. Native trail resources grew from zero to roughly 32,000 quads per racer and about 128 geometry nodes per racer. `visibleNodes`/`visibleQuads = -1` means unavailable, so stored trail counts cannot be treated as submitted geometry. Both shadow batch topology and all resource histories are retained in JSON.

## Apple presentation evidence

`overview-clear.trace` finalized (exit 0, duration 617.814 seconds) and all requested exports succeeded. Native PID **62417**, UUID **EA272D91-5ECD-422A-A4BE-63BAB34A3CE5**. Layer **524709308096**, `SCNMetalBackingLayer: MarvinSimulator.SimulatorView`, distinguishes gameplay from the menu layer. Native drawable dimensions were 1920×1080. Layer frame IDs are contiguous and display starts bracket the inferred 600-second window. No independent dropped-event detector or GPU attachment-dimension inspection was completed.

Only `TownBenchmarkEnd` survived the POI export (also checked all signposts). Its marker uptime is 117697.571189583; benchmark end boundary 117697.567536666. The single retained anchor maps the measured window to trace seconds **16.144147459–616.144147459**; display starts span 14.342000666–617.440798958. This is explicit single-anchor alignment, not independent start/end clock verification. **The required start marker check remains unresolved.**

In this instrumented run, adjacent On Display starts show P95 **33.333250 ms**, P99 **33.333333 ms**, worst **66.666583 ms**, **3009** gaps above 25 ms, worst rolling ten-second display cadence **43.9/s**. Per-minute rates: 59.983, 60.000, 60.000, 59.967, 55.183, 51.700, 49.167, 49.517, 50.283, 53.567. These are Apple's tracked layer display intervals; they are not clean-run callback rates or direct physical panel scanout measurements.

GPU Begin-to-End median/P95/P99: **15.736/18.352/19.581 ms**. CPU Begin-to-Present median/P95/P99: **17.790/29.227/34.592 ms**. These are elapsed envelopes with waiting/overlap; neither is exclusive GPU busy time or CPU execution. No exclusive hardware GPU busy counter was established.

A 25-second diagnostic marker test retained start, refreshed start, and end. The subsequent 603-second `overview-verified.trace` again retained only end and exported **zero per-frame interval rows**, despite aggregate layer and CPU data. It finalized successfully but failed per-frame collection. The repeat began after the automatic lock, a plausible confound that does not establish why rows were missing. The refresh is opt-in benchmark tooling and is not claimed as a fix. `read-game-overview.py` remains diagnostic-only; no acceptance claim is derived from its overallComplete field.

## Short detailed evidence and rendering-cost observation

Both `metal-early.trace` and `metal-loaded.trace` finalized and exported GPU intervals, encoders, submissions, allocation, thermal, performance-state, and CPU sample tables. Same diagnostic process PID **65595**, UUID **C30A7E43-1A40-4041-9C5E-5444F580E777**, unchanged resources/quality. Requested elapsed windows were 60 and 210 seconds; actual trace wall starts were 11:33:53.211 and 11:36:29.029 PDT, durations 11.046/12.330 seconds. Recording startup latency and finalization are preserved in metal-captures.json; early finalization took about 147 seconds, loaded about 63 seconds. All these timings are excluded from clean measurements.

The deterministic route is approximately phase-matched: loaded elapsed 210 pose is within 3.73 cm of an early pose at elapsed 60.596; Actual trace starts map approximately to elapsed 62.739 and 218.557 seconds (stable wall/Mach offset assumed), so those windows only partially overlap in route phase. The requested-pose comparison does not establish a matched capture; heading and resource/camera states are not exactly aligned. Locked screen, profiling overhead, temperature, and frame rate remain confounds. Device thermal exports show nominal early and fair loaded. GPU performance-state intervals report early Medium 8.411 s, Maximum .817 s, Minimum .127 s; loaded Minimum 7.492 s and Medium .995 s. These intervals are marked induced by device conditions and do not sum to a full-window busy counter. They provide coarse state evidence, not clocks or thermal-causation proof; see metal-device-state.json.

| Diagnostic observation | Early | Loaded |
|---|---:|---:|
| Per-frame summed CPU encoder wall median | 6.026 ms | 23.214 ms |
| Per-frame summed CPU encoder wall P95 | 7.679 ms | 32.028 ms |
| Render Command 8 CPU encode median | 2.083 ms | 8.590 ms |
| Render Command 2 CPU encode median | 1.633 ms | 6.058 ms |
| Metal currentAllocatedSize median | 1,950,400,512 B | 1,965,342,720 B |
| Recorded depth-zero GPU-channel interval union | 7.691 s | 6.406 s |
| Audio IO thread sampled CPU weight | 2.443 s | 2.394 s |

**Render Command 8 is the measured rendering target:** it has the largest aggregate recorded vertex/fragment interval durations in both short windows and the largest median CPU encoding span. Early vertex/fragment summed channel intervals were 2.568/2.500 seconds; loaded 1.814/2.365 seconds across fewer frames. Overlapping/nested channel durations must not be added into exclusive GPU busy time. A recorded depth-zero union is activity visible in the trace, not a hardware busy-cycle counter. Generic SceneKit encoder labels do not establish that Render Command 8 is trails, shadows, AO, or a specific draw.

CPU samples include substantial audio resampling in both windows. One unnamed rendering worker (tid 0x3511e7) had 0.683→9.986 sampled CPU seconds; The worker is attributable to SceneKit drawing: inclusive samples in `drawRenderElement:withPass:` were 0.462→8.425 seconds and `_executeDrawCommand:` 0.441→7.966 seconds. These are nested inclusive weights, not additive exclusive CPU costs. Main-thread sample weights were 1.439→2.744 seconds. These samples are statistical and include trace overhead; no audio optimization is selected.

`gpu-loaded/native-frame.gputrace` was written at elapsed 212 with Metal capture enabled from launch, UUID **BEE8A04E-C25C-4FF4-B873-8A022EA30EE4**. The application reported capture stopped and finished normally. The bundle is only about 2.5 MB and has capture/index/store/metadata files but no independently verified command/resource payload; this reinforces the need for replay verification. The directory and file hashes are retained. **It is not certified usable:** the locked Mac prevented opening/replaying in Xcode, checking 2× color/depth attachments and both shadows/AO, inspecting costly draws, and exporting replay evidence. A bundle alone is insufficient. Do not substitute historical Xcode captures for this run.

## Hypotheses and one next measurement

Tentative evidence rank, without causal attribution:

1. **Thermal limitation:** nominal→fair precedes slower clean intervals; the short loaded window shifts from mostly Medium to mostly Minimum GPU performance state. Locked-screen/recording effects and missing frequency/power-limit/busy counters prevent thermal attribution. Clean cadence sometimes recovers while fair, so coarse thermal state alone cannot explain all pacing.
2. **Accumulating rendering work:** stored trail geometry and upload counts rise; Render Command 8 dominates measured channels/encoding. Missing visible/submitted counts and draw identity prevent assigning that cost to trails or separating it from reduced performance state.
3. **Resource pressure:** weaker support. Clear RSS grows modestly, storm RSS falls, no swap is recorded, and Metal allocation rises only about 15 MB across the short windows. These observations do not rule out transient pressure or a non-memory resource limit.

The next action is **one unlocked, pose-matched early/loaded rendering attribution capture**, not a gameplay optimization. Target Render Command 8's CPU encoding and vertex/fragment intervals. The discriminating mechanism is growth in submitted geometry/draws/uploads versus reduced GPU performance state or additional OS/profiler work. Use the same diagnostic binary, fresh deterministic clear process, daylight .5, original 1920×1080/2× MSAA/AO/shadows/population/audio, AC power and nominal start. Keep the app visibly foreground with scoped `caffeinate -di`, and record lock/occlusion state. Attach near 60/210 seconds but compare **matched pose/heading and actual trace-window timestamps**, not only requested elapsed time. Export encoder IDs, draw/triangle/upload counters and GPU performance state/frequency/busy counters if supported. Replay corresponding GPU frames in Xcode and identify Render Command 8's attachments and top draws; record replay performance setting. Increasing submitted counts at comparable performance state would support accumulating work; stable counts with reduced clocks would support thermal limitation. Neither outcome should be inferred from elapsed envelopes alone.

Before treating that capture as full phase-1 evidence, repair the missing presentation proof in the same collection workflow: validate a short visible overview with explicit per-frame metrics enabled and a POI stream retained from before launch, then retain both UUID-matched start/end marker and boundary uptimes over the full 600 seconds. Require one gameplay layer, native/captured dimensions, consistent independent clock anchors, contiguous coverage, and nonempty exported rows. Preserve quality and production behavior; save all raw artifacts outside Git. No comprehensive importer rewrite is proposed.

## Handoff and reproduction

See reproduction.md for executed commands and helpers. Compact native summaries include all minute distributions, update stages, rolling-window locations, thermal transitions and endpoint resources. metal.json preserves bounded diagnostic aggregates. artifacts.json references large raw files by absolute path and SHA256, using sorted file inventories for trace directories. Raw root: `/Users/thomi/Projects/marvin-town-planning/phase-1-baseline-2026-10-03`.

The delivery commit is the latest commit touching this directory, obtainable with `git log -1 --format=%H -- docs/performance-validation/2026-10-03/phase-1-baseline`; its parent/base and diagnostic patch are recorded above. The current Codex session is `01a102c3-12e2-7113-af4a-331e3e624e61`. The first Entire checkpoint `01M41HZX37Z276V8CJRMSB6N9V` was newly created from this session and linked to the delivery commit using supported `entire session attach --agent codex --force`; initial hooks had not tracked the session because it started from a different repository. Remote verification is recorded in the final handoff after pushing, to avoid a self-referential report hash.

Subsequent attachment reused that existing checkpoint and did **not** recapture the transcript after the final Metal-state/reporting amendments. The snapshot creation time is 18:56:33 UTC; this limitation and its verified remote Git object are retained in provenance.json. The final delivery commit trailer identifies the supported session attachment; verify with `git log -1 --format=%B` and `entire checkpoint explain CHECKPOINT --json`. Both Git and checkpoint remote hashes must match local refs.

Outstanding mandatory evidence: independently checked start/end presentation anchors, direct captured attachment checks, usable Xcode GPU replay and costly draw/pass export, and unlocked exactly matched detailed captures. Foreground continuity was not independently logged. This phase remains **partial** regardless of failed or passed performance gates.
