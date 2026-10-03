# Native Apple GPU profiling

Use an Apple GPU capture or encoder-level report to locate expensive passes
before changing the renderer. Feature-isolation timings are supporting evidence,
not a substitute for this attribution.

## Current host and available tools

The October 3 host is an M2 MacBook Air running macOS 26.6.2, with Xcode 27.0 (27A266a) now installed. Instruments and `xctrace` are
available; select them with `DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer`.
Apple's new `metalperftrace` look-back collection requires macOS 27; do not
assume that its presence in online documentation makes it available here.

The built-in Metal Performance HUD is available. Enable encoder timing and
performance insights, then use **Metal HUD → Generate Performance Report**.
On this host the menu offers 5 seconds, 30 seconds, 1 minute, 5 minutes and
30 minutes. Use consecutive five-minute reports for warm/slow diagnosis, keeping
the continuous native ledger; they do not constitute an uninterrupted ten-minute
Apple presentation trace. The report includes expensive labeled
command buffers/encoders, frame interval distributions, shader compilation and
frame encoding information. Its final-frame tables describe that frame, not
every frame in the ten-minute recording.

```sh
app='apps/simulator-macos/.build/Marvin Simulator.app/Contents/MacOS/MarvinSimulator'
output="$(mktemp -d /tmp/marvin-metal-report.XXXXXX)"
MTL_HUD_ENABLED=1 MTL_HUD_ENCODER_TIMING_ENABLED=1 \
MTL_HUD_INSIGHTS_ENABLED=1 MTL_HUD_REPORT_URL="$output/metal-report.html" \
MARVIN_BENCHMARK_SECONDS=900 MARVIN_DAYLIGHT_FRACTION=0.5 MARVIN_SANDSTORM=0 \
  "$app" --town-benchmark "$output" --city-roam > "$output/native.log" 2>&1
```

The output environment variable selects a destination **file**, not a directory.
Archive that file before generating another report. The variable does **not** start
recording. Verify that the report file exists and its duration/frame coverage
matches the requested recording. Do not label the native callback JSON as an
Apple performance report. GUI automation must have the macOS permissions needed
to operate the report menu; do not bypass a pending authorization.

## Capture-size limit observed on this host

A 630-second full Metal System Trace generated 169 GiB of raw kernel events and
failed to finalize. Its profiling helper consumed substantial CPU during the
drive. That run is not clean cadence evidence. The native ledger and raw trace
are preserved outside the repository. A subsequent ten-second attached capture
saved successfully as a 347 MiB trace and exported CPU, GPU and display tables.
Use short matched warm/slow captures for diagnosis, and keep the full ten-minute
acceptance run uninstrumented. Verify that a trace actually saves and exports
before relying on it.

## Native GPU frame capture

Launch the normal town benchmark with `MTL_CAPTURE_ENABLED=1`,
`--benchmark-gpu-capture`, and optionally `MARVIN_GPU_CAPTURE_AT=30`.
The diagnostic records about 50 ms of native Metal commands to
`native-frame.gputrace` in the benchmark directory. Open the result in Xcode
and verify actual commands and attachments before attributing expensive passes.
The regression gate rejects this diagnostic flag even if cadence happens to pass.

## Detailed trace and frame inspection

After installing a compatible full Xcode, use Instruments' **Metal System Trace**
to correlate CPU submission, GPU execution and display pacing during the actual
slowdown. Use Xcode's Metal debugger on a captured slow frame to inspect pass and
shader cost, texture bandwidth, occupancy, dependencies and resource copies.
Select the Xcode toolchain explicitly when invoking its command-line tools.
List the installed `xctrace` templates before constructing capture commands;
template names and supported options depend on the installed version.

Preserve the trace/report alongside the app revision, binary hash, hardware,
resolution, quality settings, weather, route and native FPS/update ledgers.
Capture warm and slow periods. Change the measured bottleneck, then repeat the
same capture to demonstrate the change. Run final ten-minute clear and storm
acceptance separately without instrumentation overhead or concurrent builds.

## References

- [Apple Metal developer tools](https://developer.apple.com/metal/tools/)
- [Generate Metal HUD performance reports](https://developer.apple.com/documentation/xcode/generating-performance-reports-with-metal-performance-hud)
- [Metal HUD performance insights](https://developer.apple.com/documentation/xcode/gaining-performance-insights-with-metal-performance-hud)
- [WWDC26 trace collection and macOS 27 requirements](https://developer.apple.com/videos/play/wwdc2026/388/)

## Guarded tangent reuse experiment (October 3)

`--benchmark-tangent-reuse` is an opt-in candidate, not the production mesh or
sustained acceptance. It shares identical complete vertices only when their
normal-projected tangent and bitangent directions occupy the same fine bin.
Ill-conditioned frames retain exact gradients; a separate key discriminator
prevents collisions between these two policies. Degenerate, mirrored, stable
scaled, and fallback-boundary fixtures exercise those cases.

On the M2 host, three frozen ABBA comparisons at 1920×1080 with 2× MSAA measured
production medians of 11.769–11.951 ms and candidate medians of 11.592–11.615 ms.
These are owned-command-buffer envelopes, not GPU busy time or presentation.
The candidate removes about 10.4% of production town vertices while retaining
all triangles and attributes. Its maximum observed merged basis angle was
0.001049 degrees across 713,478 tangent/bitangent comparisons.

The frozen comparison changed one pixel by more than 2/255, with a maximum of
3/255. Full-resolution analysis of 27 broader comparisons against the original
triangle soup found isolated larger differences (maximum 12/255); the previous
checker sampling every second pixel missed some. The native checker now visits
every pixel, records its maximum error and compared-pixel count, and includes
magnified street views in clear, low-sun, and storm conditions. Numerical gates
still require manual image review. The expanded 33-view native run passed its pixel-fraction gate and all four
tangent fixtures; every view compared 2,073,600 pixels. Its device-RGB maximum
channel error is recorded separately from PNG byte differences. Neither this
gain nor the image checks prove ten-minute 60 FPS or adequate headroom.

Artifact directories under the external `marvin-town-planning/sustained-fix`
workspace: `tangent-basis-abba`, `tangent-basis-comparison`, and
`tangent-basis-fullpixel`. The two dust shader experiments (`dust-zero-alpha`
and `dust-vertex-tint`) produced identical frozen pixels but no reliable gain
beyond bracket drift, so they remain diagnostics too.
