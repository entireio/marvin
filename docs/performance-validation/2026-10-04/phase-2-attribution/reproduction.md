# Reproduction and collection boundary

Run from `/Users/thomi/Projects/marvee-perf-phase-2-attribution`. Use new raw paths
for every retry; never overwrite the retained Phase 1 or Phase 2 failures.

## Commands executed

Build (both baseline and telemetry versions), then verify:

```sh
DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer CONFIGURATION=release apps/simulator-macos/build-app.sh
codesign --verify --deep --strict 'apps/simulator-macos/.build/Marvin Simulator.app'
```

Hardware/power: `system_profiler SPHardwareDataType SPDisplaysDataType SPAudioDataType`,
`pmset -g custom`, `pmset -g batt`. Raw hardware output stays outside Git because
it includes hardware identifiers. Scoped idle assertion: `caffeinate -di -t 7000`,
terminated at handoff. It does not unlock a locked Mac.

The short overview used the base-commit release binary (SHA256 in summary.json).
The retained recording-options.json enables Metal Performance Overview
`perFrameMetrics` and disables its shader-compilation metrics. Points of Interest
`excludeOSLogs=false`; default Time Profiler options were retained.

```sh
PHASE2_RAW=/Users/thomi/Projects/marvin-town-planning/phase-2-attribution-2026-10-04
PHASE2_APP="$PWD/apps/simulator-macos/.build/Marvin Simulator.app/Contents/MacOS/MarvinSimulator"
env -i HOME="$HOME" USER="$USER" TMPDIR="${TMPDIR:-/tmp}" \
  PATH=/usr/bin:/bin:/usr/sbin:/sbin:/opt/homebrew/bin \
  DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer \
  xcrun xctrace record --template 'Game Performance Overview' \
  --instrument 'Points of Interest' --recording-options "$PHASE2_RAW/recording-options.json" \
  --time-limit 55s --output "$PHASE2_RAW/preflight-overview.trace" \
  --target-stdout "$PHASE2_RAW/preflight-overview-native.log" \
  --env MARVIN_BENCHMARK_SECONDS=25 --env MARVIN_DAYLIGHT_FRACTION=0.5 \
  --env MARVIN_SANDSTORM=0 --env MARVIN_BENCHMARK_POI_REFRESH=1 \
  --launch -- "$PHASE2_APP" --town-benchmark "$PHASE2_RAW/preflight-overview" --city-roam
```

Export TOC and tables through `xcrun xctrace export --input TRACE --toc --output XML`,
and `--xpath '/trace-toc/run[@number="1"]/data/table[@schema="SCHEMA"]'`.
Executed schemas: `os-signpost`, `metal-perf-overview-layer-count-metric`,
`metal-perf-overview-layer-per-frame-interval-metric`. Exports succeeded; the last
contains zero rows. preflight-validation.json retains unique resolved signposts,
UUID/PID/boundary checks and clock origins. Zero rows make preflight fail.

After rebuilding with visibility logging, the separate native smoke command used
the same whitelisted environment, `MARVIN_BENCHMARK_SECONDS=10`, daylight=.5,
sandstorm=0 and `MARVIN_BENCHMARK_VISIBILITY=1`, then launched the binary with
`--town-benchmark "$PHASE2_RAW/visibility-smoke" --city-roam`. Inspect
resources.json `visibility` per sample, and benchmark.json quality/audio settings.
This check is diagnostic only and does not repair presentation collection.

## CPU correction and hashes

```sh
python3 docs/performance-validation/2026-10-04/phase-2-attribution/aggregate-cpu.py \
  /Users/thomi/Projects/marvin-town-planning/phase-1-baseline-2026-10-03 \
  /tmp/phase2-cpu-correction.json
```

Compare with committed cpu-correction.json. Its six inputs record current SHA256s;
phase1-input-hash-check.json compares each with the original Phase 1 artifacts.json.
Worker selection, duration, frame count and nested-weight limitations are explicit.

artifacts.json identifies the external artifact-inventory.json by SHA256. Each
entry in that inventory contains a raw-root-relative file path, byte length and
SHA256. Rehash each listed file and the inventory itself; the inventory excludes
its own path. Raw traces, native ledgers/logs, hardware output and the exact
telemetry binary remain outside Git. The packaged-resource inventory matches all
Phase 1 entries byte-for-byte.

## Commands intentionally not executed

No matched GPU pair, long overview or new detailed Metal traces. After manual
unlock, use a fresh output directory and preflight/timebox. Enable visibility
logging on every collection run and require active, unoccluded gameplay throughout
the accepted window. Sampled flags alone do not rule out between-sample changes.
Record lock/foreground transitions independently where possible.

Reuse the Phase 1 native GPU capture entrypoint and overview validator only after
their preflights pass. Native GPU capture requires `MTL_CAPTURE_ENABLED=1` and
`--benchmark-gpu-capture`; a .gputrace directory is insufficient. Record actual
rendered pose/projection and prove the report's proposed tolerances before accepting
the pair. Record attachment/pipeline/draw identity and Xcode replay settings.
Then collect/finalize/export a clear 603-second benchmark (600 measured seconds
after 3-second warmup), retaining both independent anchors, one identified gameplay
layer, boundary bracketing, nonempty rows and continuity evidence. Stop new
collection at minute 100 and deliver PARTIAL for any missing mandatory evidence.
