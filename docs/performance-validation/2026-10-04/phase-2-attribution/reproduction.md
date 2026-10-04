# Phase 2 reproduction

Run from `/Users/thomi/Projects/marvee-perf-phase-2-attribution`. Historical locked
preflight commands/results remain in `locked-preflight-reproduction.md` and
`locked-preflight-report.md`. Never overwrite an attempt.

Build: `DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer CONFIGURATION=release apps/simulator-macos/build-app.sh`.
Verify: `codesign --verify --deep --strict 'apps/simulator-macos/.build/Marvin Simulator.app'`.
Hardware/power: `system_profiler SPHardwareDataType SPDisplaysDataType SPAudioDataType`,
`pmset -g custom`, `pmset -g batt`. Raw hardware identifiers stay outside Git.
The scoped `caffeinate -di -t 7000` assertion is terminated at handoff.

## Presentation

The retained options explicitly enable perFrameMetrics, disable shader compilation
metrics, and set Points of Interest excludeOSLogs=false. Time Profiler defaults
are retained. No GPU capture runs concurrently with presentation collection.

```sh
PHASE2_RAW=/Users/thomi/Projects/marvin-town-planning/phase-2-attribution-resumed-2026-10-04
PHASE2_OPTIONS=/Users/thomi/Projects/marvin-town-planning/phase-2-attribution-2026-10-04/recording-options.json
PHASE2_APP="$PWD/apps/simulator-macos/.build/Marvin Simulator.app/Contents/MacOS/MarvinSimulator"
env -i HOME="$HOME" USER="$USER" TMPDIR="${TMPDIR:-/tmp}" \
  PATH=/usr/bin:/bin:/usr/sbin:/sbin:/opt/homebrew/bin \
  DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer \
  xcrun xctrace record --template 'Game Performance Overview' \
  --instrument 'Points of Interest' --recording-options "$PHASE2_OPTIONS" \
  --time-limit 660s --output "$PHASE2_RAW/overview-periodic.trace" \
  --target-stdout "$PHASE2_RAW/overview-periodic-native.log" \
  --env MARVIN_BENCHMARK_SECONDS=603 --env MARVIN_DAYLIGHT_FRACTION=0.5 \
  --env MARVIN_SANDSTORM=0 --env MARVIN_BENCHMARK_POI_REFRESH=1 \
  --env MARVIN_BENCHMARK_VISIBILITY=1 \
  --launch -- "$PHASE2_APP" --town-benchmark "$PHASE2_RAW/overview-periodic" --city-roam
```

Short attempts used 35 seconds, a 65-second trace limit and prefixes
preflight-visible / preflight-periodic. The first sustained run used overview-visible:
its launch/10-second START markers did not survive export. The bounded repair
refreshes START at 10 seconds and every 30 seconds, preserving the original start
boundary and separately recording emission uptime. Only instrumented runs enable
this. The exact periodic-anchor binary is retained outside Git.

Finalize immediately, export TOC (`xcrun xctrace export --input TRACE --toc --output XML`),
then export each schema through
`--xpath '/trace-toc/run[@number="1"]/data/table[@schema="SCHEMA"]'`.
Schemas/suffixes: os-signpost/poi,
metal-perf-overview-layer-per-frame-interval-metric/intervals,
metal-perf-overview-layer-count-metric/layer-count, os-log/os-log.

```sh
python3 docs/performance-validation/2026-10-04/phase-2-attribution/validate-overview.py \
  "$PHASE2_RAW" overview-periodic "$PHASE2_RAW/overview-periodic"
```

The validator uses 600 seconds after three seconds of warmup. Pass 32 as its fourth
argument for a 35-second preflight. Required: one gameplay layer, matching UUID/PID
and native boundaries, an early START emission within 33 seconds of native start,
END, clock-origin spread <100 microseconds, contiguous frame IDs and both boundaries
bracketed. Late START refreshes alone cannot pass. Rolling windows step one second;
intervals are assigned by their start time.

Inspect resources.json and session-events.json with independent UI observations.
Absent OS lock field is null/UNKNOWN. Notifications record main-queue receipt time;
they are not an independent hardware lost-event detector.

## GPU preflight

Use the same whitelisted environment with MTL_CAPTURE_ENABLED=1,
MARVIN_GPU_CAPTURE_AT=10, MARVIN_BENCHMARK_SECONDS=25, visibility logging,
daylight=.5 and sandstorm=0. Launch with --town-benchmark OUTPUT --city-roam
--benchmark-gpu-capture. Actual outputs: gpu-preflight and gpu-preflight-device.
The first captured the renderer queue from willRender. The repair captures the
actual renderer device from updateAtTime and records three renderer callbacks.
Xcode must establish the complete submitted frames corresponding to callbacks.

Open native-frame.gputrace in Xcode through native UI. First replay reported
segmentation fault 11 and buffer creation failures. Apple MetalToolchain 27A266a
was installed through the official component flow (`xcodebuild -downloadComponent
MetalToolchain`); its log is retained. The device retry's depth viewer reported
invalid data. JSON diagnostics retain observed whole-capture counts and selected
draw details, not a matched per-frame inventory or native GPU cost.

No matched pair is accepted without usable replay and complete frame mapping.
Declared tolerances: player/camera positions ≤0.10 m, orientations ≤1°, identical
route waypoint/camera mode and projection coefficients within 1e-6. Trigger margins
are .07 m/.6°. Future pair requests use comma-separated MARVIN_GPU_CAPTURE_AT and
MARVIN_GPU_CAPTURE_MATCH=1. Requested elapsed times alone never prove matching.
Inspect attachments, pipelines, corresponding passes, top draws, submitted geometry
and replay performance settings.

## CPU and hashes

```sh
python3 docs/performance-validation/2026-10-04/phase-2-attribution/aggregate-cpu.py \
  /Users/thomi/Projects/marvin-town-planning/phase-1-baseline-2026-10-03 \
  /tmp/phase2-cpu-correction.json
```

Compare cpu-correction.json. All six source XML exports match retained Phase 1
hashes. Nested draw/execute weights must not be added. artifacts.json identifies
the historical raw inventory; resumed-artifacts.json identifies the resumed one.
Each inventory lists relative paths, byte lengths and streamed SHA256s, excluding
itself. Rehash the inventory and listed files. Raw traces, ledgers, binary, hardware
output and asset inventory stay outside Git. Missing mandatory evidence means PARTIAL.
