# Sustained town performance repair

Target: 600 measured seconds of continuous town driving at 1920×1080, with
production geometry, textures, shadows, population, effects, audio and draw
distances unchanged. Available hardware is Apple M2 / 24 GB, not the user's M4.

1. Reproduce and attribute: collect frame ledger, actual live counter, CPU stages,
   trail retained/visible geometry, thermal timeline, and Metal HUD GPU samples.
   Diagnostic trail hide windows retain and continue generating all geometry.
   These runs cannot count as production acceptance.
2. Optimize observed work while preserving rendered detail. First candidates are
   redundant trail geometry uploads and submission of many archived trail chunks.
   Preserve exact contact positions, attributes, storm age and chunk eviction.
3. Test retained history and rollover, unchanged-geometry updates, storm fading,
   overlap/occlusion and terrain contact with native images. Test the performance
   gate against actual failing evidence and synthetic missing/slow data.
4. Run full production ten-minute clear and storm routes. Publish full/minute/
   rolling statistics and actual counter values, with warmup excluded. Require
   frame coverage at 60 Hz and zero callback gaps over 25 ms. This is explicitly
   callback evidence, not proof of display presentation. A fixed low frame rate
   must not pass simply because each interval is below 25 ms.
5. Measure GPU headroom separately: proposed p99 <=12.5 ms, maximum below the
   16.667 ms frame budget, including loaded final minutes. HUD sample distributions
   are not unique-frame accounting. Obtain Astra Ultra review before delivery.

No ten-minute test guarantees every future hardware/OS/workload. Preserve this
route as a repeatable regression gate; do not report a short average as sustained
60 FPS or imply that M2 measurements validate an M4.

## First diagnostic and rejected prototype

**Invalid for causal attribution (see instrumentation correction below).** The first
instrumented 603-second diagnostic also declined. Thermal state moved
from nominal to fair at178seconds; rendering remained near60until roughly273s.
CPU-update p95 stayed around3.4ms and render-cycle p95 around9ms. Trail geometry
increased from25,302quads/101nodes after the first minute to112,432quads/441nodes.
Hiding trails at300–330seconds did not restore60FPS (54.66); hiding them at480–510
improved that window to57.26 but still failed. Different route positions and
thermal history limit direct window comparisons. Trail drawing contributes
work, but these observations do not establish it as the sole cause.

The first ground prototype moved full-alpha town cells into the opaque pass.
Although its opacity classification was mathematically valid, native comparison
FAILED:12.5%of street-view pixels changed by more than5/255, with a visible
partition seam. It was rejected. The independent judge identified changed
transparent layer ordering as the strongest explanation. The replacement keeps
visible town ground in its original pass and uses depth-only base terrain under
conservatively fully covered cells. It must pass native comparison before use.

Runtime inspection also established that production Dirt Track uses2xMSAA;
sandbox uses4x. Benchmark reports now encode actual sample count, not SceneKit's
enum raw value. Existing2x is preserved; no antialiasing reduction is authorized.

## Instrumentation correction — do not use the first profiles for acceptance

The new per-second trail visibility probe itself caused main-thread stalls. It
queried every trail node's SceneKit geometry and frustum visibility. In the first
63-second run without Metal HUD, 35 update gaps exceeded 100 ms, starting exactly
after integer-second samples (for example 28.007 → 28.119 seconds). The 603-second
instrumented run lost 208 seconds through the simulation's 100 ms delta clamp.
The original noninstrumented ten-minute failure had no such lost time.

Normal resource telemetry now reads cached mark/node/upload counts only, without
SceneKit geometry access or frustum queries. Its cost is included in the measured
update span. Earlier attribution of these additional stalls to Metal HUD alone
was not justified. Preserve the failed profiles, but rerun all timing comparisons
with nonintrusive instrumentation before drawing optimization conclusions.

The covered-base native comparison passed eight clear/low-sun views; street
views were effectively pixel-identical. UV orientation, uncovered ground, storms
and lowest production-camera views remain required checks. Performance benefit
is still unproven. Shadow-volume culling is currently an explicit experimental
benchmark option, not the production default.


## GPU metric interpretation

Apple's [Metal HUD documentation](https://developer.apple.com/documentation/xcode/monitoring-your-metal-apps-graphics-performance)
distinguishes command-buffer elapsed GPU time from encoder activity: idle gaps
between encoders can inflate the former. The legacy log contains command-buffer
elapsed time, not an established measurement of exclusive GPU busy time. Treat
its proposed 12.5 ms gate as provisional; do not claim quantified rendering
headroom from that field alone. Encoder counter timing or equivalent native
measurement is required to substantiate workload headroom. The runner defaults
to a normal run with all HUD environment variables removed; `--gpu-hud` selects
a separate profiling run. A normal callback pass alone cannot be overall success.

Short cached-telemetry validation: normal 30 measured seconds recorded 1,799
updates, no update gaps above 100 ms, no render gaps above 25 ms, and approximately
60 FPS. This validates the probe repair only. Conservative shadow culling remains
opt-in: after removing the synchronizing camera-presentation getter, CPU p95
returned to about 3 ms, but the short legacy GPU comparison showed no substantial
benefit. Do not enable it without visual acceptance and measured benefit.


## Corrected full production run — still failed

`production-clear-cached`: Apple M2, 1920×1080, production 2× MSAA, both original
shadow map sizes, normal detail/audio, clear midday, 603.017 seconds, no Metal
HUD. Mean callback rate 53.024 FPS; 4,185 gaps above25ms inside the measured
600seconds; minimum rolling10second rate42.7FPS; distance1384.58m. CPU update
p952.907ms. No lost simulation time from100ms clamps. The first three minutes
were60FPS, later minutes fell to46–55FPS. This is a valid failure and the current
changes are insufficient. The original and new runs had different thermal
histories; do not claim an isolated optimization regression from their difference.

Ground comparison now passes27views, including clear/low-sun/storm, uncovered
infield, both blend boundaries, grazing camera, aerial, reversed view and LOD
boundary. SCNPlane UV orientation is checked against its generated source.
Shadow-culling comparison passes the same27views with zero sampled pixels above
5/255 difference and actual caster reduction. Known native inside/outside probes
and a49-box frustum oracle passed. Culling remains opt-in pending benefit and
moving-camera validation.

The independent judge rejected using legacy HUD first-frame-number plus sample
offset as a unique presentation ledger: overlapping batches in the same captured
log conflict. The gate now requires typed encoder evidence for headroom and does
not mark overall completion without presentation evidence. Normal runner exit
status covers cadence/motion only; its JSON separately reports overall incomplete.


## Loaded isolation and exact vertex reuse

A corrected363second diagnostic retained full trail history and hid it only at
300–330seconds. Callback FPS for270–300/300–330/330–360seconds was48.53/44.33/45.83.
This moving-route comparison did not restore60FPS. The fixed-pose ABBA GPU probe
also did not show a repeatable large trail saving once baseline drift was
considered. A speculative trail-page batching prototype was removed before
building or enabling it. Do not attribute the slowdown to trail count alone.

The owned-command-buffer probe now copies live attachment formats and reverse-Z,
sets the correct depth clear, preserves2×MSAA, uses a frozen scene, and brackets
each isolated variant with production blocks. Its resolved capture was viewed
alongside the live capture after correcting linear/sRGB readback and vertical
orientation; both show the same complete scene. At the loaded pose, disabling
SSAO saved roughly4.5–5ms and disabling shadows roughly5ms. Shadow-volume culling
saved under1ms. These are diagnostic GPU envelopes with different duty cycle and
pipelining, not certified production headroom or presentation measurements.

Exact vertex-attribute reuse initially reduced2,592,774vertices to1,637,060 but
normal-map tangent generation changed a small number of pixels. A stricter key
also preserves per-face texture gradients; it retains2,495,307vertices. Native
expanded indexed-attribute comparison and all27strict image comparisons passed.
All original triangles, material groups and normal/texture/color attributes
remain. The reduced vertex count alone is not a sustained-performance result.

Regression suite:22tests passed. Tests reject the actual ten-minute failures,
missing/slow frame ledgers, long simulation gaps, false FPS counters, diagnostic
quality modes, insufficient driving, mismatched GPU evidence and legacy GPU
metric types. Overall completion remains unverified until presentation and
headroom evidence accompany sustained cadence success.
