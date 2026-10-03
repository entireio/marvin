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

Guarded tangent reuse is enabled for the production town mesh following the
33-view comparison and independent Astra Ultra review. It shares identical
complete vertices only when their
normal-projected tangent and bitangent directions occupy the same fine bin.
Ill-conditioned frames retain exact gradients; a separate key discriminator
prevents collisions between these two policies. `--benchmark-exact-tangents`
restores the previous policy for diagnostics. `--benchmark-tangent-reuse` with
`--benchmark-gpu-probe` retains a reference mesh for the frozen ABBA comparison;
normal play neither builds nor retains that duplicate. Degenerate, mirrored, stable
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

The independent Astra Ultra review accepted visual preservation for the tested
views, including the two adjacent 17/255 and 7/255 PNG outliers in the expanded
low-sun infield capture. It explicitly did not accept sustained 60 FPS.

## Sustained result after tangent reuse

The clean clear-weather run of `abe4053` (`tangent-production-clear`) still fails:
603.015 seconds, 54.088 FPS mean, 3,547 callback gaps over 25 ms, 45.3 FPS minimum
rolling ten-second coverage, and CPU update p95 2.866 ms. It drove 1,354.6 m with
full audio, 1920×1080, 2× MSAA, both 4096/2048 shadow maps and exploration detail.
The first two minutes were about 60 FPS; minutes four through ten averaged
50.7–53.3 FPS. Thermal state ended at 1. The final native image showed 44.5 FPS.
This is a failed M2 sustained test, not M4 validation or a presentation trace.

## Static town shadow batching experiment

`MARVIN_GPU_VARIANTS=shadow-batch,shadow-batch,shadow-batch` with the frozen GPU
probe compares shadow-only copies of the static town cells against the ordinary
casters. The copies weld exact local Float positions and keep all 679,992
triangles, winding, transforms and both LODs. Single-sided plaster/adobe/metal
share one material group; double-sided cloth retains another. Camera/light
category masks keep these copies out of the visible and AO passes while allowing
them into the directional shadow maps. Apple documents the separate
[camera/light category filtering](https://developer.apple.com/documentation/scenekit/scnnode/categorybitmask).
An isolated native scene produced identical reference/proxy pixels, while its
missing-shadow control differed in 5,474 pixels.

The first three town ABBA comparisons (`shadow-batch-abba`, M2, nominal thermal
state) measured 11.492–11.542 ms production command-buffer medians versus
11.274–11.326 ms with batching. CPU encoding medians fell from 3.104–3.208 ms to
2.667–2.738 ms. Across 92 cells and their LODs, shadow vertices fell from
1,761,272 to 417,484 and geometry elements from 661 to 338. Each candidate image
changed 28 pixels, at most 5/255; all production blocks were bit-identical.

Run `--ground-performance-test OUTPUT --benchmark-shadow-batch` for the broader
native image comparisons. The opt-in `--benchmark-shadow-batch-live` path routes
TownWorld's authoritative caster decisions to the shadow copies every frame.
`--shadow-culling-test OUTPUT --benchmark-shadow-batch-live` checks the combined
changes, moving caster selection away and back while enabled to detect stale or
duplicate casters. All 33 views passed the numerical gate; 25 were identical,
182 pixels differed overall, and 17 exceeded 5/255 (maximum 53/255). The low-sun
infield pair was viewed at native resolution; independent PNG decoding matched
all reported metrics. Full moving-camera and sustained acceptance remain open.

The full-pixel checker uses matching packed 8-bit bitmap channels when available,
with the previous color-conversion fallback for other encodings. Independent PNG
checks verified all pixels and metrics across the 33 views; this avoids repeated
AppKit color-object allocation without reducing coverage. These measurements do
not prove sustained 60 FPS or display presentation.

The combined three-ABBA run (`shadow-batch-culling-abba`, all thermal state 0)
measured 11.813–11.950 ms production GPU medians versus 11.412–11.448 ms
combined, and 3.107–3.252 ms CPU encoding versus 2.622–2.747 ms. This is a
modest frozen-frame saving, not a sustained-performance pass.

Same-state low-sun infield controls (`shadow-batch-live-repeat`) were bit-identical
within both reference and candidate pairs. Between configurations, 62 pixels
changed, one by more than 5/255, maximum 17/255. Thus the difference is not
explained by instability between consecutive identical snapshots. Robot placement
varies between launches; this run does not invalidate the earlier 53/255 outlier.
The optimization remains opt-in while moving-view validation continues.

Astra Ultra accepted visual preservation for these tested static views after
reviewing the repeats: no perceptible lost detail, missing shadows, seams or
changed silhouettes. The pixel differences are candidate-associated, not random
capture noise. Moving-camera stability and sustained performance were explicitly
not accepted. The live probe snapshots the live configuration before disabling
its owner for frozen ABBA; per-second telemetry uses cached logical caster counts,
while synchronization tests retain actual SceneKit node-state checks.

## Warm-frame follow-up and combined shadow trial

`native-gpu-warm-production` captured a production frame at 285 seconds after
the live HUD had fallen to 52.3 FPS. The 315-second diagnostic ended at thermal
state 1. Xcode replay reported 17.39 ms at its Medium GPU performance setting:
8 render encoders, 4 compute encoders, 67 blit encoders and 2,180 draws. The dust
draw (1,506 indices, pipeline 117) occupied 9.49%; the town-ground overlay draw
(21,786 indices, pipeline 111) occupied 8.15%. These are replay attributions,
not actual presented-frame timestamps or a controlled cold/warm timing ratio.
The raw capture, native ledgers and `xcode-profile-observations.json` are retained.

Three frozen ABBA pairs in `dust-draw-ceiling` measured production GPU medians
11.742–11.904 ms versus 10.719–10.745 ms with dust hidden. CPU encoding remained
about 3.1 ms. This diagnostic only establishes the rendering-cost ceiling; it
excludes live geometry rebuilding, and removing dust is not a permitted fix.

The full-quality opt-in shadow batch + culling run `shadow-live-clear-10min`
completed 603 seconds but still failed: 56.425 FPS mean, 2,145 callback gaps over
25 ms, and 44.3 FPS minimum rolling ten-second coverage. It travelled 1,334.5 m;
thermal state ended at 1. The gate also correctly rejects opt-in diagnostic flags.
This is useful measured progress, not sustained acceptance or M4 validation.

## Low-overhead presentation calibration

Apple's installed **Game Performance Overview** Instruments template successfully
recorded on this macOS 26 host. Its per-frame interval export defines “On Display”
as the interval that the drawable is on display. A separate native CAMetalLayer
calibration app logged public `MTLDrawable.presentedTime` values while rendering
and deliberately skipping selected updates. All 1,071 exported display intervals
matched consecutive valid native presentation timestamps within 42.52 ns. Both
methods identified exactly the same five intervals above 25 ms.

Artifacts: `presentation-calibration-overview/calibration.trace`,
`native-presentations.json`, the per-frame XML exports, and
`calibration-verification.json` under the sustained-fix evidence directory.
This establishes the interval units and semantics for this single-layer test.
Simulator layer identification, long-run completeness, instrument overhead and
GPU active-time semantics still require validation before acceptance integration.
The sustained gate remains fail-closed for unverified presentation evidence.

`overview-town-production` successfully recorded a 315-second full-quality town
drive with Game Performance Overview (M2, 1920×1080, clear, audio on). The long-lived
layer had contiguous layer-local frame IDs. A second layer stopped at 9.735 s,
consistent with the menu lifecycle; direct SCNView attribution remains pending.
Trace minutes 1–3 each contained 3,600 display-start intervals with no >25 ms
gaps. Minute 4 contained 3,109 intervals, including 473 above 25 ms; the remaining
25 seconds contained another 182. Startup is included in trace minute 0 and is
not gameplay acceptance. Native callbacks independently reported 57.865 FPS and
666 gaps >25 ms over the 315-second benchmark.

The long-lived layer's median GPU Begin-to-End envelope rose from about 16.9 ms
in trace minutes 1–3 to 27.0 ms in minute 4. GPU Active Time rose from about
14.7 ms to 26.9 ms; Apple's documentation describes this as associated command
buffers in flight, not exclusive encoder busy time. CPU Begin-to-Present includes
waiting and must not be confused with CPU execution. Native simulation-update
p95 was 2.666 ms. Instruments recorded Nominal→Fair thermal state at 146.143 s.
These establish sustained display misses and a coincident GPU timing increase;
they do not yet isolate all scheduling/thermal causes or prove profiler overhead.

`scripts/rendering/read-game-overview.py` reports each PID/layer separately,
uses adjacent display starts for cadence, and exposes frame-ID continuity and
end-to-next-start gaps. It deliberately leaves overall acceptance false.

The native benchmark now emits `TownBenchmarkStart` and `TownBenchmarkEnd`
Points of Interest, including the run UUID, a fresh marker uptime and the
benchmark boundary uptime separately. Add `--instrument 'Points of Interest'`
to the recording command. `overview-marker-final` verified both events against
the native benchmark UUID and duration: 25.000302208 s, with start/end clock-offset
estimates differing by 3.80 µs. The end marker follows its tick boundary by
1.70 ms; consumers must use the distinct fields rather than assuming coincidence.

The checked-in calibration source is `scripts/rendering/PresentationCalibration.swift`.
Compile it with `xcrun swiftc` and run the resulting binary under the overview
template, passing a native JSON output path. Export the single-run
`metal-perf-overview-layer-per-frame-interval-metric` table; then run
`check-presentation-calibration.py INTERVALS.xml NATIVE.json`. The actual calibration
passes all 1,071 intervals and shows less than 0.7 ns clock-offset spread.
`test-game-overview.py` covers held frames, terminal/only-frame handling, PID/layer
separation, missing/duplicate frame IDs, raw XML references/units, multi-table
rejection and GPU-envelope interpretation. The parser reports terminal display
duration separately from adjacent-start cadence coverage. None of these reports
promotes a short diagnostic run to sustained acceptance.

## Rejected ground hash lookup

`ground-noise-table-probe` had a missed shader-call rewrite in the sand-transition
material and produced visible magenta errors; its apparent speedup is invalid.
The corrected GPU-precomputed RGBA32Float lattice table preserved the original
noise interpolation and matched the frozen native image within three pixels at
1/255, but was slower (about 12.04–12.06 ms versus 11.75–11.92 ms controls).
Two ABBA pairs each for RGBA16Float and RGBA8Unorm in `ground-noise-table-formats`
were also slower: typically about 12.00 ms versus 11.63–11.70 ms production, all
thermal state 0. Half-float changed 161 pixels by at most 1/255; normalized-byte
changed 150,357 pixels by at most 1/255. No format was enabled. The candidate was
removed from the worktree; source, native captures and timing ledgers remain
with the diagnostic artifacts so this failed approach need not be repeated.

The five-minute overview CPU samples also separate sustained costs: in trace
seconds 60–120, the audio IO thread accounted for 16.862 CPU seconds, including
6.777 seconds self time in `Resampler2::ConvertSIMD`. In seconds 240–300, these
were 16.565 and 7.142 seconds respectively. Total sampled CPU across threads
decreased from 55.340 to 52.335 seconds as frame throughput fell. This is not
evidence of a growing simulation-update CPU leak. Raw samples and the cold/warm
aggregation are in `overview-town-production/time-profile.xml` and
`cpu-cold-warm.json`.

## Audio processing isolation (not accepted)

All 161 current source WAVs are mono, while the production graph duplicates
loops into stereo before varispeed and filtering. An opt-in prototype processes
loops in mono and restores stereo balance afterward. Offline render time fell
by roughly 55–60%, but this is not a transparent change: the strengthened native
comparison fails during startup, boosts, moving perspectives and restarts.
The original-versus-original control is bit-identical in all seven scenarios,
including storm, shelter and abrupt camera turns. Expected boost events also
pass, ruling out a silent or missing-event false positive.

Standalone AVAudioEngine probes isolate a further problem even at fixed pan:
changing rate alone agrees within 3.73e-9, but changing player volume at pan
0.65 produces maximum sample errors of 0.00137–0.00143. Centered versions are
bit-identical. Moving balance after DSP changes volume/pan ramp behavior, so
fixed-gain equivalence was insufficient. Removing an always-unity varispeed
also changes latency by approximately 48 samples and leaves an aligned residual;
that shortcut has not been applied to production either.

`audio-layout-control` and `audio-layout-controls-mono` contain the native WAVs,
reports, source snapshots and standalone probes. The candidate remains opt-in,
fails the existing equivalence limits, and has no live performance acceptance.
These signal checks do not claim a listening judgment or sustained 60 FPS.

## AO pass attribution and opacity experiment

Replaying the saved warm frame again in Xcode gave a 16.83 ms diagnostic frame.
Render encoder 3 (20.46%) writes a full-resolution RGBA16Float texture and a
temporary depth attachment. Render encoder 4 (9.95%) is one full-screen triangle
using `scn_ssao_compute`: it reads that RGBA16Float texture as `depthSampler`
and a half-resolution R8Uint texture as `minMaxSampler`. This establishes the
AO preparation/calculation path, rather than inferring it from encoder order.
The ground overlay contributes 5.35% of the frame in encoder 3 alone: draw
10269, 21,786 indices, pipeline 70, `commonprofile_frag (14)`. Xcode exposes
its PBR material source, normal/roughness textures and radial opacity modifier.
Source and bindings alone do not establish which instructions survive compiler
optimization. The observation record is
`native-gpu-warm-production/xcode-ao-pass-observations.json`.

The `ground-surface-opacity` frozen probe moves just that material's opacity
calculation from the fragment modifier to the surface modifier, retaining its
maps and pigment. The initial experiment accidentally matched a street modifier
too and produced visible magenta errors; its apparent speedup is invalid.
The corrected selector changed exactly one material. Two ABBA pairs at thermal
state 0 measured candidate medians 11.676–11.698 ms against production
11.667–11.698 ms: no useful improvement. All production repeat images matched;
the candidate changed 726,769 pixels, by up to 38/255. Both images were viewed.
It remains a diagnostic, not a production change. Raw timings, captures, source
diff and binary hash are in `ground-surface-opacity-corrected`.

Any replacement for the AO path must preserve its lighting contribution,
normal-map detail, contact shading, moving residents and robots, dust, trails,
both suns, storms and camera transitions. Turning it off is only an isolation
test. A custom implementation needs native visual comparison and sustained
timing; the measured ceiling is not proof that a replacement will achieve it.


## Frozen AO input diagnostic (not a gameplay replacement)

`--benchmark-ao-preparation`, together with `--benchmark-gpu-probe`, now writes
an isolated RGBA16Float mapped-normal/coverage texture, stored Depth32Float,
raw GPU/CPU samples, and a viewed PNG. This copies the frozen scene's geometry,
LODs, presentation transforms, maps and shader modifiers, removes lighting nodes,
and replaces the final color with encoded normals. It retains the original
material lighting models: changing them to `.constant` silently drops normal maps.
The original 1.37 ms unlit result is therefore rejected. The corrected mapped
preparation measured 1.64 ms before the effect policy below. None of these
numbers includes AO computation, main rendering, integration or synchronization.

The independent Astra Ultra judge identified two additional input defects:
ordinary multiply trails multiply the encoded normal, and airborne alpha dust
blends billboard normals over an unrelated solid depth. The diagnostic now
suppresses color writes for no-depth-write multiply decals and materials using
the shared `dustTint` modifier. Depth-writing clods remain. Mapped ground dressing
still blends its surface normals. This affects only the cloned diagnostic input;
all impressions, dust, grains and haze remain in the main scene. The policy is
specific to these current materials, not a generic transparency solution.

`AOPreparationFixture.swift` and `check-ao-preparation-fixture.py` test native
pixels against analytic planes, including known tangent-space maps, fractional
coverage, vertex displacement, overlapping mapped dressing, multiply trails,
airborne dust, rotated normals, and perspective reconstruction at 5, 25 and
120 metres with the game's 48-degree, 0.02/250 camera. Unlit and unfiltered
negative controls must reproduce the defects. The source fixture is rendered
before cloning: fresh nodes do not yet have valid presented world transforms.
The checker also verifies the unlit control contains the same visible geometry,
so an empty buffer cannot masquerade as a successful negative control.

Use the actual float projection coefficients when reconstructing reverse-Z
view depth. Rebuilding ideal coefficients from near/far introduced about 12 mm
error at 120 m in this fixture; the actual coefficients reduce maximum error to
0.155 mm. The test covers the game's reverse-Z path, not every SceneKit projection.
Run the fixture independently of controlled game timing:

```sh
swiftc apps/simulator-macos/Sources/MarvinSimulator/AOPreparationProbe.swift scripts/rendering/AOPreparationFixture.swift -o /tmp/marvin-ao-fixture
/tmp/marvin-ao-fixture /tmp/marvin-ao-fixture-output
python3 scripts/rendering/check-ao-preparation-fixture.py /tmp/marvin-ao-fixture-output
```

Evidence is under `ao-preparation-fixture-verified`, `ao-preparation-policy-clear`
and `ao-preparation-policy-storm` in the sustained-fix artifact directory. Both
native town normal captures and the original storm render were viewed. The clear
and storm input passes measured 1.594 and 1.397 ms respectively at thermal state
0, each excluding seven effect materials. They are different poses/populations;
this is not a clear-versus-storm speedup comparison. These are input diagnostics,
not a replacement AO algorithm, visual acceptance, or sustained performance proof.


The Astra Ultra judge accepted the tested frozen clear/storm input diagnostic,
including the overlap policy and analytic fixtures. This does not accept live
synchronization, enabled AO shading or net performance. An AO consumer must
unpremultiply coverage before decoding normals, normalize blended normals, and
reject background/uncovered samples.

### Neutral AO integration controls

A separate `neutral-custom-ao` frozen variant binds a texture containing one to
PBR ambient occlusion with built-in AO disabled. `literal-neutral-ao` appends the
literal multiplier one without a texture. `check-neutral-ao.py` compares both
with `no-ssao`, checks repeated images, and checks restoration of production.
Its default is exact equality; it writes failures as evidence rather than
silently widening the threshold.

The first material-only binding failed: 25,947 pixels changed by up to 37/255,
and restoring production left 23,661 changed pixels concentrated on Marvin.
A follow-up literal control also failed, showing the new texture alone was not
the cause. The judge inspected the captures and identified loss of fine grime
and roughness corresponding to the geometry-owned `DirtCoating` surface shader.
Adding a competing material-owned surface modifier is not a valid composition.
These rejected runs remain in `ao-neutral-binding-clear` and
`ao-neutral-binding-controls`, including the rejected binder source. They are
not performance wins. The next binder keeps the original shader owner and
isolates materials shared across geometry-owned and material-owned surfaces.


The owner-preserving clear run (`ao-neutral-owner-clear`) made the literal
candidate bit-identical to AO-disabled production. The texture candidate changed
one pixel by 1/255. However, restored built-in-AO production still changed 23,531
robot pixels by up to 34/255, while later AO-disabled controls were exact. This
fails the restoration gate; it is not accepted as a complete neutral binding.
Copying all geometry/materials during binding (`ao-neutral-isolated-clear`) did
not fix restoration and added repeat differences. That attempt was archived and
removed; the owner-preserving diagnostic remains for further investigation.
No shader-cache cause is asserted solely from these observations. The next
investigation must compare original/restored AO passes or fresh renderer state,
while preserving the coating's geometry-owned uniforms.

The existing 27 sustained-gate, five Metal-report and seven overview-parser tests
also pass. Their coverage and the input-fixture acceptance do not turn the failed
neutral/restoration checks into a pass. No production AO replacement is enabled.


### Native AO restoration captures and bounded controls

`MARVIN_GPU_CAPTURE_BLOCKS=0,3` with `MTL_CAPTURE_ENABLED=1` captures selected
owned-command-buffer diagnostic blocks to `.gputrace`. Captured runs explicitly
report `timingEligible:false`; they are inspection evidence, not timing samples.
`MARVIN_GPU_FRESH_RENDERER=1` creates a new renderer per block, and
`MARVIN_GPU_RESTORE_AO_FIRST=1` restores camera intensity before restoring shader
modifiers. These switches affect only the frozen diagnostic.

The original/restored clear captures in `ao-restore-capture` were replayed in
Xcode 27.0 without profiling. Render encoder 4, draw 9918 (`scn_ssao_compute`),
binds the full-resolution RGBA16Float depth/normal input (texture 39 before,
44 after) and writes the 960×540 RGBA16Float output (42 before, 37 after).
Exported KTX mip-zero files are byte-identical for both pairs, including signed
half-float values. `scripts/rendering/compare-ao-exports.py` checks format,
dimensions, payload length, finite values, hashes and per-channel differences.
The checked report is `performance-validation/2026-10-03/ao-raw-export-comparison.json`.
This rules out differences in those two buffers. It does not independently test
subsequent filtering, main-pass texture bindings or shader specialization.

The final color still differs by 23,597 pixels, maximum 41/255. Creating fresh
renderers did not fix restoration (22,589 pixels, maximum 40/255). Restoring AO
intensity first also failed (23,999 pixels, maximum 34/255); its restored final
image was inspected. In both latter runs the literal-neutral candidate and all
AO-disabled repeats were exact; the texture-neutral candidate differed by two
pixels at 1/255. Their failed check reports are retained alongside the raw-buffer
report. No tolerance has been relaxed and no optimization is accepted here.

The Astra Ultra judge reviewed the code and evidence and found no concrete
missing Swift restoration operation. Shader specialization or binding remains
an inference, not an established cause. Stop expanding restoration controls:
use independent-process baseline/candidate comparisons for replacement work,
with baseline never installing the candidate, candidate configured once, matched
deterministic state/history, and A/B/B/A process order. Matching state must be
verified, not inferred from the same elapsed wall time. Existing contaminated
post-toggle production images cannot be reference frames. This does not waive
reset/lifecycle testing or full-quality ten-minute clear/storm acceptance.
