# Current M4 collection runbook

The user explicitly authorized the current M4 after the initial M2 access blocker.
Clean-run commands below have now been executed; trace/capture commands were executed, with validation limitations in report.md.
Paths below use the isolated worktree on the current host.

## Preflight and build

```sh
cd /Users/thomi/Projects/marvee-perf-phase-1-baseline
git rev-parse HEAD
git status --short
system_profiler SPHardwareDataType SPSoftwareDataType SPAudioDataType SPDisplaysDataType
sysctl -n machdep.cpu.brand_string
# Verify the authorized current Apple M4 MacBook Air (Mac16,12, 32 GB).
xcode-select -p
DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer xcodebuild -version
DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer xcrun xctrace list templates
DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer CONFIGURATION=release apps/simulator-macos/build-app.sh
codesign --verify --deep --strict 'apps/simulator-macos/.build/Marvin Simulator.app'
shasum -a 256 'apps/simulator-macos/.build/Marvin Simulator.app/Contents/MacOS/MarvinSimulator'
```

Record target hardware/OS/Xcode, binary hash, resource tree inventory/hash, exact
commit/status, power/battery/low-power mode, display refresh/backing/lid state,
audio device/channels/sample rate, and all overrides. Do not commit hardware serial
numbers. Finish builds and heavy analysis before measurements. Check for competing
simulators and workloads; do not silently terminate unrelated applications.

Start each clean run in a fresh process with the same power/display/audio setup
and settled nominal thermal state. Record the settling duration and observed
background workload; if conditions cannot be controlled, document the deviation.
Use clear/storm population policy unchanged, clear midday fraction 0.5, existing
timer default, and existing deterministic city route. Verify route motion/state
and weather in native ledgers rather than inferring them from command arguments.

## Separate clean runs

Use new output paths. Each command runs independently; wait for completion and
return to the controlled starting condition before starting the next.

```sh
RAW=/Users/thomi/Projects/marvin-town-planning/phase-1-baseline-2026-10-03
mkdir -p "$RAW"
env -i HOME="$HOME" USER="$USER" TMPDIR="${TMPDIR:-/tmp}" \
  PATH=/usr/bin:/bin:/usr/sbin:/sbin:/opt/homebrew/bin \
  DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer \
  python3 scripts/rendering/run-sustained-performance.py "$RAW/clean-clear" --daylight 0.5
```

Then, after restoring controlled conditions:

```sh
env -i HOME="$HOME" USER="$USER" TMPDIR="${TMPDIR:-/tmp}" \
  PATH=/usr/bin:/bin:/usr/sbin:/sbin:/opt/homebrew/bin \
  DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer \
  python3 scripts/rendering/run-sustained-performance.py "$RAW/clean-storm" --daylight 0.5 --storm
```

An exit of 1 from the cadence gate can be a valid slow baseline. App failure,
missing ledgers, wrong hardware/quality or incomplete coverage is invalid collection.
For each weather, inspect `benchmark.json`, `timeline.json`, `resources.json`,
`displayed-fps.json`, `sustained-gate.json`, the log, and the saved image. Select
exactly `[startUptime+3,startUptime+603]`. Compute ten per-minute callback rates and
interval P95/P99/max/>25-ms counts; compute worst ten-second cadence with sliding
windows and state their step/boundary rule. Summarize update total/stage timing from
the same window and thermal/trail/caster history from resources. The existing
`analyze-frame-timeline.py` supplies stage/gap diagnostics; its whole-ledger summary
does not replace measured-window/per-minute analysis. Review boundary-straddling
intervals and complete ledger timestamps so clipping cannot hide a missed frame.

Run the retained reporting helper after completion:

```sh
python3 docs/performance-validation/2026-10-03/phase-1-baseline/summarize-baseline.py "$RAW/clean-clear"
python3 docs/performance-validation/2026-10-03/phase-1-baseline/summarize-baseline.py "$RAW/clean-storm"
```

`sample-resources.py PID OUTPUT_JSON` records OS CPU/RSS/swap every 15 seconds
while the process exists. Start it just after launch; record the starting offset
and polling overhead limitation. Both clean runs used it.

No HUD, capture layer, Instruments, concurrent builds or other simulator should be
active during these clean runs. Add available low-overhead OS counters only with
their method/overhead documented; native resources do not include RSS or frequency.

## Independent presentation recording

```sh
APP="$PWD/apps/simulator-macos/.build/Marvin Simulator.app/Contents/MacOS/MarvinSimulator"
env -i HOME="$HOME" USER="$USER" TMPDIR="${TMPDIR:-/tmp}" \
  PATH=/usr/bin:/bin:/usr/sbin:/sbin:/opt/homebrew/bin \
  DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer \
  xcrun xctrace record --template 'Game Performance Overview' \
  --instrument 'Points of Interest' --time-limit 660s \
  --output "$RAW/overview-clear.trace" \
  --target-stdout "$RAW/overview-clear-native.log" \
  --env MARVIN_BENCHMARK_SECONDS=603 --env MARVIN_DAYLIGHT_FRACTION=0.5 \
  --env MARVIN_SANDSTORM=0 \
  --launch -- "$APP" --town-benchmark "$RAW/overview-clear" --city-roam
xcrun xctrace export --input "$RAW/overview-clear.trace" --toc --output "$RAW/overview-toc.xml"
xcrun xctrace export --input "$RAW/overview-clear.trace" \
  --xpath '/trace-toc/run[@number="1"]/data/table[@schema="metal-perf-overview-layer-per-frame-interval-metric"]' \
  --output "$RAW/overview-intervals.xml"
python3 scripts/rendering/read-game-overview.py "$RAW/overview-intervals.xml" \
  --output "$RAW/overview-diagnostic.json"
```

Run exports with the selected Xcode environment as above. 660 seconds allows
startup margin; **both benchmark markers and whole measured-window coverage are
mandatory**, irrespective of recording duration. Inspect TOC for POI and CPU sample
schema names actually present, then export those exact tables with the same XPath
pattern. Confirm the finalized trace opens in Instruments. Resolve process and
SCNView/CAMetalLayer identity, rule out menu/other layers, verify native and captured
1920×1080 attachments, UUID, marker/boundary offsets, contiguous display frame IDs
and events spanning both boundaries. Record lost-event/coverage and attribution
limitations. Do not interpret parser `overallComplete:false` as performance failure.
Keep this instrumented run's timing separate from clean-run results.

## Short detailed captures, then separate GPU capture

In a new clear diagnostic process, launch the same route with
`MARVIN_BENCHMARK_SECONDS=240`, `MARVIN_DAYLIGHT_FRACTION=0.5`, and
`MARVIN_SANDSTORM=0` in the clean environment above. Do **not** enable the Metal
capture layer. Use its `MARVIN_BENCHMARK_ID` start uptime/UUID and PID to attach near
elapsed 60 and 210 seconds (select comparable route/view states):

```sh
# PID must be the verified diagnostic process; attach only while it is running.
xcrun xctrace record --template 'Metal System Trace' --instrument 'Time Profiler' --attach "$PID" \
  --time-limit 10s --output "$RAW/metal-early.trace"
# Later, near the verified loaded window, independently:
xcrun xctrace record --template 'Metal System Trace' --instrument 'Time Profiler' --attach "$PID" \
  --time-limit 10s --output "$RAW/metal-loaded.trace"
xcrun xctrace export --input "$RAW/metal-early.trace" --toc --output "$RAW/metal-early-toc.xml"
xcrun xctrace export --input "$RAW/metal-loaded.trace" --toc --output "$RAW/metal-loaded-toc.xml"
```

Use actual TOC schemas to export GPU intervals, submissions/display data, resources
and available CPU samples. Require finalized/openable traces and successful exports.
Do not collect a ten-minute full Metal System Trace; historical collection grew
to 169 GiB and failed finalization. These are diagnostic timings, not clean cadence.

For the GPU capture use another new process/output directory:

```sh
env -i HOME="$HOME" USER="$USER" TMPDIR="${TMPDIR:-/tmp}" \
  PATH=/usr/bin:/bin:/usr/sbin:/sbin:/opt/homebrew/bin \
  DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer \
  MARVIN_BENCHMARK_SECONDS=240 MARVIN_DAYLIGHT_FRACTION=0.5 MARVIN_SANDSTORM=0 \
  MTL_CAPTURE_ENABLED=1 MARVIN_GPU_CAPTURE_AT=212 \
  "$APP" --town-benchmark "$RAW/gpu-loaded" --city-roam --benchmark-gpu-capture \
  > "$RAW/gpu-loaded-native.log" 2>&1
open -a Xcode "$RAW/gpu-loaded/native-frame.gputrace"
```

Verify nonempty commands/resources, replay, encoder/pass attachments, sample count,
AO and both shadow maps. Inspect expensive passes/draws and export relevant images,
textures or supported reports. Record Xcode replay performance setting and exact
pass/draw identities. A directory or “capture stopped” message is insufficient.
Capture-layer overhead can exist before frame capture begins; exclude the entire
capture run from clean measurements.

Hash raw files and deterministic trace-bundle inventories outside Git. Commit
only compact summaries, provenance and report. A slow valid baseline may complete
this phase; missing mandatory evidence must remain partial/blocked. Stop at the
task's 120-minute deadline and list remaining evidence explicitly.

## Diagnostic build, exports and limitations

The clean runs and first overview used the original reviewed commit and clean binary hash in report.md. The current branch adds only benchmark AO reporting and an opt-in refreshed start POI. Rebuild with the release command above to reproduce the diagnostic version; both builds had identical resource inventories. For the 25-second marker test and full overview repeat, add `--env MARVIN_BENCHMARK_POI_REFRESH=1`. The short marker test worked; the full repeat did not retain start and exported zero frame rows. Do not assume this flag repairs collection.

The actual Metal collector is retained as `collect-metal.py`; it waits on native startUptime and attaches at 60/210 seconds in a 240-second run. Run once against fresh output directories (paths in the helper are this worktree/raw root). The exports and analysis are reproducible with `export-metal.py` and `analyze-metal.py`. They require finalized raw traces at the referenced paths. `inspect-presentation.py RAW overview RAW/overview-clear` checks POI UUID/clock, gameplay layer and 600-second coverage using the existing diagnostic reader. Its false identityAndCoverageVerified flag is deliberate when start POI is absent.

All exact capture commands, request/finish uptimes and environment are in metal-captures.json. Artifact directory hashes refer to sorted path/byte/file-hash JSON inventories, not an opaque filesystem directory hash. Original raw files remain outside Git. The run-specific helper scripts may need root/path substitution for a new checkout; never overwrite old evidence.

The screen locked automatically at 11:16:09 PDT. Future captures require manual unlock, app foreground visibility, and scoped idle prevention (`caffeinate -di` for the collection duration, stop afterward). This assertion was **not** used for today's measurements. Retain independent visibility and both POI anchors. A full repeat, Xcode replay/pass inspection and replay exports are outstanding; commands shown do not imply those checks succeeded.
