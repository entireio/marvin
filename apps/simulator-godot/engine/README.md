# Patched Godot 4.7.2 for the Marvin port

SceneKit-like soft directional shadows at close to hard-shadow cost need changes inside Godot's renderer, which the
game cannot make from C#. This folder holds a small patch series against the **4.7.2-stable** tag, a script that builds
the patched .NET editor and export templates on macOS (Windows templates cross-compiled), and the plan for using it
from the SceneKit facade. Every change is behind a project setting or a new RenderingServer call: with nothing set, the
patched engine renders exactly like stock 4.7.2, and the facade keeps working on the stock engine.

Status: drafted, reviewed against the 4.7.2 source, and checked to apply in order to 4.7.2-stable (`git apply` into a
scratch index; the series is `git format-patch` output of those commits). **Not compiled and not measured yet** (other
GPU benchmarks were running on this Mac); see "Open points".

| | |
|---|---|
| `patches/` | `git format-patch` series, applied in order with `git am` (`build-engine.sh source`) |
| `build-engine.sh` | prerequisites, source + patches, macOS editor with .NET glue and assemblies, macOS and Windows templates, install |
| `build/` | gitignored: logs, the SCons venv, the packaged `.tpz` |
| `.gdignore` | keeps Godot's importer out of this folder |

## Why the soft filter costs ~4 ms (from the 4.7.2 source)

Measured before (`docs/performance.md`): at 1080p on the M2 the two shadowed suns cost about 7.4 ms of GPU per frame
with the soft filter (SoftHigh), SceneKit about 1.4-1.8 ms. Godot's soft filter alone is ~4 ms more than its hard
filter, and SoftMedium's 8 taps were not measurably cheaper than SoftHigh's 16 (one frozen-view comparison inside the
±0.6 ms run spread; point 6). The hard filter cut the opaque pass by 2.3-2.8 ms and the race's transparent pass from
3.75 to 1.19 ms (per-pass numbers partly affected by mislabelled encoders; point 6). Paths are relative to
`servers/rendering/` in the Godot source.

1. **The soft PCF is the hard filter plus a per-pixel rotation and N taps, inlined into the heaviest shader.**
   `renderer_rd/shaders/scene_forward_lights_inc.glsl:312-337` (`sample_directional_pcf_shadow`): with
   `sc_directional_soft_shadow_samples() == 0` (hard) it returns one `textureProj` (hardware 2x2 bilinear comparison,
   317-319). Otherwise it hashes `gl_FragCoord` (`quick_hash`, 305-310), takes `sin` and `cos`, builds a `mat2`
   (321-327) and loops over N taps, each `pos + shadow_pixel_size * (disk_rotation * kernel[i].xy)` from the scene UBO
   (331-334). Sample counts and radii per quality: `renderer_rd/renderer_scene_render_rd.cpp:1219-1263`
   (Hard 0, SoftVeryLow 1, SoftLow 4, SoftMedium 8 / radius 2, SoftHigh 16 / radius 3, SoftUltra 32 / radius 4;
   `get_vogel_disk`, 47-56). The facade's exact configuration uses SoftHigh: 16 comparisons per sun, 32 per lit fragment.
2. **Where it runs.** `renderer_rd/shaders/forward_clustered/scene_forward_clustered.glsl`: the material's fragment code
   is pasted at 1351 (`#CODE : FRAGMENT`); the directional shadow loop comes after it (2292-2553, "Do shadow and lighting
   in two passes to reduce register pressure", 2294), looping over up to 8 lights (2328), selecting a split by view
   depth (2454-2485), sampling (2489) and packing 8 bits per light into `shadow0/1` (2548-2552) for the lighting loop
   (2571-2693). Every material output, the ambient and reflection terms and the facade's composed `light()` varyings are
   live across the shadow loop. The facade's `light()` is already occupancy-limited (removing it whole saves 1.6 ms,
   removing any part of it nothing). The soft path adds the rotation matrix, the hash and the unrolled taps' coordinates
   and results to that live set; a lower occupancy tier for the whole shader would explain why the cost appears in every
   lit fragment of both color passes. The hard path's single `textureProj` adds almost nothing. The loop's bound is a
   specialization constant, so the compiler knows the trip count when it builds the pipeline and unrolls the loop:
   specialized pipelines carry no loop hint (`scene_forward_lights_inc.glsl:10-16`), and on Metal no hint could stop
   it, because SPIRV-Cross drops loop controls when it writes MSL (`thirdparty/spirv-cross/spirv_msl.cpp`,
   `CompilerMSL::emit_block_hints` is empty). The function is also inlined twice per light (main split and blend split).
   (Hypothesis from the measurements and the code; the build stage confirms it with Xcode's shader profiler.)
3. **Overdraw multiplies the taps.** The transparent pass shades every layer (the town's 15 ground-overlay draws, dust),
   each with both suns' kernels; the opaque pass has no overdraw (depth prepass, equal test).
4. **Not involved here:** PCSS (`sample_directional_soft_shadow`, `scene_forward_lights_inc.glsl:410-453`, blocker search
   + filter, two loops) runs only for lights with `light_angular_distance > 0` (`scene_forward_clustered.glsl:2356`;
   `sc_use_directional_soft_shadows`, set in `storage_rd/light_storage.cpp:788-797` only when a light has an angular
   size and blur). Split blending (2491-2525) samples twice in the blend band; the facade turns it off. Branch divergence
   is limited to split boundaries (the light loop is uniform). Specialization constants (`sc_packed_0`, bits 20-25 for
   the directional sample count, `forward_clustered/scene_forward_clustered_inc.glsl:63-123`) make each quality its own
   pipeline; until a pipeline is compiled the ubershader runs with every path and `[[dont_unroll]]`.
5. **The maps.** Every frame `render_forward_clustered.cpp:1569-1574` clears the whole directional atlas in a render pass
   of its own (8192² at 16 bits: 0.34 ms measured), then each split of each light is its own render pass with
   load/store actions on the atlas (`_render_shadow_end`, 2898-2906; `_render_shadow_pass` 2637-2672 divides the
   light's region per split). On Metal a partial render area keeps `LoadActionLoad` and sets `renderTargetWidth/Height`
   to the region's far corner (`drivers/metal/metal3_objects.cpp:1075,1160-1174`;
   `drivers/metal/metal_objects_shared.h`, `configureDescriptor`), so a split in the lower right quarter loads and stores
   the whole atlas; a pass with 16 draws still cost 0.4-0.5 ms. The facade renders 2-4 splits per sun
   (`SceneKitRuntime.FitShadows`), each covered with ground from edge to edge, because Godot fits every directional map
   to the camera (`renderer_scene_cull.cpp:2146-2365`: per split the bounding sphere of the frustum slice, its extent
   snapped at 2315; two lights get 4096 x 8192 regions, `storage_rd/light_storage.cpp:2789-2809`), where SceneKit renders
   one fixed 4096 map and one 2048 map per frame.
6. **How firm the evidence is.** "8 taps cost the same as 16" rests on one comparison of the frozen town view's total:
   20.7 and 20.9 ms with SoftMedium against 20.1-21.3 ms with SoftHigh (`docs/performance.md`, "Tried and dropped"),
   inside the ±0.6-0.7 ms run spread. A cost that grows with the taps is therefore not excluded. A rough estimate (not
   measured): 1080p x (the opaque layer plus about two transparent ground layers in the town) x 2 suns x 16 taps is
   about 200 M bilinear depth comparisons per frame, or 1.5-2 ms if the M2 takes about 110 G of them per second (an
   assumption: Apple does not publish the M2's texture rate). The per-pixel rotation also scatters each 2x2 quad's
   taps over different texels, so texture-cache locality is a second candidate, and the fixed kernel fixes it too. The
   per-pass split (transparent pass 3.75 -> 1.19 ms in the race) is softer than the totals: the after traces filed
   0.5-0.9 transparent encoders per frame under other labels. The robust number is the total: 4.5-5 ms for the hard
   filter. The three candidates are rotation state, unrolled-loop register pressure and sampling throughput or
   locality. The build stage separates them with kernel modes 1 and 3 (below) and with 4 against 8 fixed taps.
7. **What SceneKit runs.** The Metal source SceneKit compiles for the game's materials was captured by
   `reference/calibration/tools/fullgame/srcspy` (common profile, `ComputeSoftShadow`, the path for forward shadows with
   `shadowSampleCount` > 1):

   ```metal
   float3 center_uv = lightScreen.xyz / lightScreen.w;
   float3 scale_uv  = float3(shadowRadius, shadowRadius, reverseZ ? shadowRadius * center_uv.z : shadowRadius / lightScreen.w);
   for (int i = 0; i < sampleCount; i++)   // light.shadowSampleCount, a uniform
       totalAccum += shadow2D(shadow_sampler, shadowMap, center_uv + shadowKernel[i].xyz * scale_uv); // sample_compare, filter::linear
   shadow = totalAccum / float(sampleCount);
   ```

   That is a fixed kernel (no per-pixel rotation, no noise), one bilinear comparison per tap, a loop over a runtime
   sample count that the compiler cannot unroll, and a per-tap depth offset (the kernel's z times `shadowRadius` times
   the receiver depth in reverse Z), which works like a bias that grows with the tap's distance from the centre. The
   kernel values (`shadowKernel`, a constant buffer) are not in the source. Patch 0003's modes 1 and 3 have this shape.
   What still differs: the tap positions, until SceneKit's are read out (validation plan, step 4), and the per-tap depth
   offset, for which the facade's kernel-proportional bias (`ShadowBiasPerKernel`) stands in.

## The patches

| patch | switch (default = stock) | what it changes |
|---|---|---|
| `0001` version tag | always | `version.py` `module_config = ".marvin"`: the engine reports `4.7.2.stable.marvin.mono` and uses `export_templates/4.7.2.stable.marvin.mono`. The status stays `stable`, so the C# packages stay `Godot.NET.Sdk`/`GodotSharp` 4.7.2 from nuget.org. |
| `0002` one render pass for the directional atlas | `rendering/lights_and_shadows/directional_shadow/single_render_pass` (bool, false) | Forward+ draws all directional splits in one draw list on the atlas: `DRAW_CLEAR_DEPTH` over the whole atlas (a load-action clear), per-split viewport and scissor, one store. Removes the separate 0.34 ms clear and the per-split load/store. Same image. |
| `0003` fixed-kernel directional PCF | `rendering/lights_and_shadows/directional_shadow/soft_shadow_filter_kernel` (0 Rotated, 1 Fixed, 2 Fixed With Early Out, 3 Fixed With Runtime Loop) | `sample_directional_pcf_shadow_fixed`: the same taps for every pixel (point-symmetric rings of four, outermost ring first, `get_fixed_ring_disk`), each a hardware bilinear comparison; no hash, no sin/cos, no rotation matrix, no noise. Mode 2 takes the outer ring first and stops when its four taps agree. Mode 3 is mode 1's image with the sample count read from the kernel (the first tap's w) instead of the specialization constant, so the loop is not unrolled, as in SceneKit; it tests the register-pressure explanation. Sample count and radius still from `soft_shadow_filter_quality` and `shadow_blur`. A 2-bit specialization constant (`packed_1` bits 6-7), so the other paths compile out. Read when `directional_soft_shadow_filter_set_quality()` is called; renderers without the fixed path (Mobile) keep the rotated Vogel disk. |
| `0004` fixed shadow box | `RenderingServer.light_directional_set_shadow_fixed_box(light, enable, extent, z_near, z_far, resolution)` | One orthographic map anchored to the light: `|x|, |y| <= extent` around its origin, `z_near..z_far` along -Z, centre snapped to whole texels, casters culled to the box, map a `resolution` square at the origin of the light's atlas region (4096 and 2048 in an 8192 atlas). Shaders (Forward+, Mobile, volumetric fog) leave receivers outside the box unshadowed; the fixed kernel's taps are clamped to the map. The map rect travels in the former `pad` of `DirectionalLightData` (layout unchanged). |

Design choices for a small, maintainable series:

- No new enum values and no new `Light3D`/`DirectionalLight3D` properties: existing scenes, GDExtensions and the C#
  bindings keep their meaning, the Compatibility renderer needs no changes (the base `RendererLightStorage` gets a
  default no-op implementation), and `light_directional_get_shadow_mode()` simply reports `ORTHOGONAL` for a fixed box,
  so the atlas layout, the light culler and the debug views follow without changes.
- Shaders are written for the RenderingDevice path that already serves Metal (SPIRV-Cross), Vulkan and D3D12
  (SPIR-V to DXIL through Mesa NIR): plain GLSL 450, the new kernel reuses the existing UBO kernel array and
  `sampler2DShadow` comparisons, the box rect is unpacked with integer ALU (no `unpackUnorm2x16`).
- What is not covered: the Mobile renderer keeps the rotated kernel and the separate clear; PCSS lights
  (`light_angular_distance > 0`) and the transmittance lookup are not clipped to a fixed box; the rotated kernel can
  read a few texels beyond a fixed box near its edge, and the hard filter's bilinear footprint half a texel. Beyond a
  box's map lies cleared atlas (lit) or, where it meets the next light's region, that light's map; this only touches
  receivers at the box's edge (`extent` from the light's centre, 58 m for the race suns).

Upstream: 0002 and 0003 are self-contained candidates (a render-pass optimisation and a filter option). 0004 would
need `DirectionalLight3D` properties and an editor gizmo upstream; here the RenderingServer call is enough.

## Building

```sh
apps/simulator-godot/engine/build-engine.sh all     # or step by step:
apps/simulator-godot/engine/build-engine.sh deps    # SCons in engine/build/venv, llvm-mingw, checks
apps/simulator-godot/engine/build-engine.sh source  # ~/src/godot-4.7.2-marvin, branch marvin-4.7.2 = tag + patches/
apps/simulator-godot/engine/build-engine.sh deps    # again: Mesa NIR + Agility SDK (D3D12), AccessKit, WinRT into $GODOT_SRC/bin/build_deps
apps/simulator-godot/engine/build-engine.sh editor
apps/simulator-godot/engine/build-engine.sh templates-macos
apps/simulator-godot/engine/build-engine.sh templates-windows
apps/simulator-godot/engine/build-engine.sh install
apps/simulator-godot/engine/build-engine.sh verify
```

- **Do not build while GPU benchmarks run** on this Mac: the compile uses every core (`JOBS` limits it). A full build
  is roughly an hour per target class on the M2 (editor, 2 x 2 macOS templates, 2 Windows templates).
- **Editor** (`editor`): `scons platform=macos target=editor arch=arm64 production=yes lto=none module_mono_enabled=yes`
  (Metal; `vulkan=yes` when MoltenVK is installed), then `bin/godot.macos.editor.arm64.mono --headless
  --generate-mono-glue modules/mono/glue`, then `modules/mono/build_scripts/build_assemblies.py --godot-output-dir=bin
  --godot-platform=macos` (GodotSharp, GodotSharpEditor, GodotTools, GodotPlugins), then the same SCons line with
  `generate_bundle=yes` (Godot's bundler: `bin/godot_macos_editor_mono.app` with `Contents/Resources/GodotSharp`).
  `install` copies it to `~/Applications/GodotMarvin/Godot_mono.app` and signs it ad hoc (no hardened runtime, so .NET
  can JIT). The app keeps the name `Godot_mono.app` so `tools/perf/run-benchmark.py --wait-idle` still recognises it.
- **macOS templates** (`templates-macos`): `target=template_release` and `template_debug` for `arm64` and `x86_64`, then
  `generate_bundle=yes` (lipo + `macos_template.app` zipped as `bin/godot_macos_mono.zip`, installed as `macos.zip`).
  Godot's Metal driver is arm64-only, so the Intel slice needs MoltenVK (Vulkan SDK:
  `misc/scripts/install_vulkan_sdk_macos.sh`, needs `jq`); without it use `MACOS_ARCHS=arm64` (Apple Silicon only).
- **Windows templates** (`templates-windows`, cross-compiled on the Mac): llvm-mingw (latest `ucrt-macos-universal`
  release, `deps` downloads it to `~/src/llvm-mingw`), D3D12 dependencies from Godot's own
  `misc/scripts/install_d3d12_sdk_windows.py --mingw_prefix=...` (Mesa NIR `mesa-x86_64-llvm`, Agility SDK, PIX into
  `$GODOT_SRC/bin/build_deps`, found by `platform/windows/detect.py`), then
  `scons platform=windows target=template_release|template_debug arch=x86_64 production=yes module_mono_enabled=yes
  use_mingw=yes use_llvm=yes mingw_prefix=~/src/llvm-mingw d3d12=yes vulkan=yes`. Output
  `bin/godot.windows.template_*.x86_64.llvm.mono.exe` and `.console.exe`, installed as `windows_*_x86_64[_console].exe`.
  The .NET module loads `hostfxr` at run time, so no Windows .NET packs are needed to build. `MINGW_PREFIX` is
  exported as well, because `detect.py`'s `can_build()` runs before the SCons options are read and only looks at the
  environment and `PATH`. AccessKit is linked statically. The stock 4.7.2 template set ships no Agility SDK DLLs
  (`D3D12Core.*.dll`), and this set ships none either, so exports use the system's D3D12 as before. The official
  templates come from Godot's build containers, not from llvm-mingw on macOS, so validation step 5 on a Windows PC
  matters.
- **Install** (`install`): templates go to `~/Library/Application Support/Godot/export_templates/4.7.2.stable.marvin.mono/`
  (`version.txt` = `4.7.2.stable.marvin.mono`, `icudt_godot.dat` copied from the stock set) and are packed as
  `engine/build/Godot_v4.7.2-marvin_mono_export_templates.tpz` for other machines. The stock editor and the stock
  `4.7.2.stable.mono` templates are never touched.

**C# packages.** The project keeps `Godot.NET.Sdk/4.7.2` and restores the stock `GodotSharp` 4.7.2 from nuget.org. The
patches add API only and never change an existing method's signature (method binds are looked up by name and hash), and
the facade calls the new API dynamically, so the stock bindings work with the patched engine. The editor's own
`GodotSharp/Api` is built from the patched source; its NuGet packages are deliberately not pushed to any feed
(`build-engine.sh` deletes `GodotSharp/Tools/nupkgs`), so they can never shadow the stock 4.7.2 packages in
`~/.nuget/packages`.

**Changing the patches.** After `source`, `~/src/godot-4.7.2-marvin` is on branch `marvin-4.7.2` with one commit per
patch on top of the tag. Edit there, fold fixes into the right commit (`git commit --fixup` + `git rebase --autosquash
4.7.2-stable`), rebuild, and regenerate the series with
`git -C ~/src/godot-4.7.2-marvin -c format.signature="4.7.2-stable + Marvin patches" format-patch -o
apps/simulator-godot/engine/patches 4.7.2-stable` (delete the old files first: format-patch shortens long names, so
they change). `source` compares the branch with engine/patches by content (`git patch-id`). If they differ, it
rebuilds the branch from the tag. It stops instead of dropping a commit that is not in engine/patches. For a newer
Godot, rebase the branch onto the new tag and change `GODOT_TAG`/`TEMPLATE_VERSION` in `build-engine.sh` and the
version check in its `source` step.

## Selecting the engine

`tools/env.sh`: `MARVIN_ENGINE=marvin` sets `GODOT` to `~/Applications/GodotMarvin/Godot_mono.app/Contents/MacOS/Godot`
(or `$MARVIN_ENGINE_APP`); without it, or with `MARVIN_ENGINE=stock`, the stock editor. An explicit `GODOT` wins.
`tools/godot`, `tools/export` and `tools/perf/run-benchmark.py` (which reads `GODOT`) follow it:

```sh
MARVIN_ENGINE=marvin apps/simulator-godot/tools/godot                 # play on the patched engine
MARVIN_ENGINE=marvin apps/simulator-godot/tools/export windows --zip  # templates from export_templates/4.7.2.stable.marvin.mono
```

`tools/export` derives the template folder from `$GODOT --version` (`4.7.2.stable.marvin.mono.custom_build.<hash>`
-> `4.7.2.stable.marvin.mono`). Once the patched engine is validated, the default can flip by changing one line in
`tools/env.sh`.

## Facade integration (not done yet; feature-detected)

Detection at start-up (`SceneKitRuntime.EnsureStarted`), so the stock engine keeps today's behaviour:

```csharp
var rs = Engine.GetSingleton("RenderingServer");
bool engineBox = rs.HasMethod("light_directional_set_shadow_fixed_box");
bool engineKernel = ProjectSettings.HasSetting("rendering/lights_and_shadows/directional_shadow/soft_shadow_filter_kernel");
bool engineOnePass = ProjectSettings.HasSetting("rendering/lights_and_shadows/directional_shadow/single_render_pass");
// Never put these keys in project.godot: the stock engine would then report them too.
if (engineOnePass) ProjectSettings.SetSetting("rendering/lights_and_shadows/directional_shadow/single_render_pass", true);
if (engineKernel && !SceneKitCalibration.HardShadows)
{
    // 1, 2 (early out) or 3 (runtime loop), whichever measures fastest. Set it before the quality call: the kernel mode
    // is read inside DirectionalSoftShadowFilterSetQuality (today's call is SceneKitRuntime.cs:46).
    ProjectSettings.SetSetting("rendering/lights_and_shadows/directional_shadow/soft_shadow_filter_kernel", SceneKitCalibration.ShadowKernelMode);
    RenderingServer.DirectionalSoftShadowFilterSetQuality(RenderingServer.ShadowQuality.SoftMedium); // 8 taps = the suns' shadowSampleCount
}
```

1. **`SCNLight.Sync`**: a directional light with `automaticallyAdjustsShadowProjection == false` (the binary suns) calls
   `rs.Call("light_directional_set_shadow_fixed_box", godotLight.GetBase(), true, orthographicScale, zNear, zFar,
   (int)shadowMapSize.width)` and sets `DirectionalShadowMode = Orthogonal`, `DirectionalShadowPancakeSize = 0`
   (SceneKit ignores casters in front of `zNear`), blend splits off; turning the box off again passes `false`. Lights
   with an automatic projection keep Godot's camera fit.
2. **`SceneKitRuntime.FitShadows`**: box lights skip the split emulation entirely (`NearSplitDistances`, the second
   split, `ShadowBoxFarDepth`, the region/normal-bias corrections). Their texel is SceneKit's own
   (`SceneKitShadowTexel`, 2.83 / 5.66 cm), the same for every camera, so `FitShadow(light, SceneKitShadowTexel,
   zFar - zNear, normalBiasScale: 1)` runs once per change instead of per camera: blur = `ShadowKernelScale x
   shadowRadius / qr` (the kernel is then exactly `shadowRadius` texels of the light's own map, SceneKit's semantics),
   bias and normal bias as calibrated. The box's texel snapping duplicates the game's own `updateShadowCenter` snapping
   (harmless: the anchor is already on the grid).
3. **Composer**: keep `scn_shadow_box0/1` (the shadow-pass slope bias identifies the light by them). The receiver clip in
   `light()` becomes redundant with the engine's clip; it can stay (measured free) or be compiled out when `engineBox`.
4. **Calibration constants** (`SceneKitCalibration`): `ShadowKernelMode` (0 rotated, 1 fixed, 2 fixed + early out) and
   a refit `ShadowKernelScale` for the ring kernel (the rotated Vogel disk needed 0.88 for SceneKit's 10-90 % penumbra
   of 1.2 x `shadowRadius` texels); `ShadowBiasPerKernel`/`ShadowNormalBiasPerKernel` re-checked with
   `--ground-bias-probe`. Overridable through `MARVIN_SCN_CAL` like the others (`EngineShadowBox=0` to A/B the box).
5. **Graphics Detail levels** (low / medium / high / max; max is today's look). Only 0002 renders the same image, so
   it can be on at every level, max included. 0003 and 0004 change the image: ring taps instead of the rotated Vogel
   disk, and SceneKit's box texels instead of the camera-fitted splits. A level can only switch to them after the
   full-game comparison against macOS comes out at least as close as that level does today. Until then max keeps
   SoftHigh + split emulation (+ 0002), and the levels below can take the fixed kernel + boxes first. The hard levels can
   use the boxes too (two passes instead of up to eight, texels fixed at SceneKit's size) or keep the near splits for
   finer close-up texels. Early out (mode 2) is a candidate for the lower levels only: with SceneKit's kernel (radius 3,
   about 2.6 Godot texels after `ShadowKernelScale`) the outer four taps sit about 3 texels (8-9 cm) apart, so robot
   legs, antennae and posts thinner than that can lose their shadow. `BaseTerrainCastsShadow` stays a per-level choice (in
   a box map the flat base terrain fills the whole 4096 map and shadows nothing visible).

## Validation plan (build stage)

1. Build; `--headless --version`; the facade test, calibration scenes and probes on the patched engine with every new
   switch off must be byte-identical to the stock engine (`--calibration`, `--shadow-box-probe`, `--ground-bias-probe`,
   `--shadow-motion-probe`, the deterministic smoke and town captures).
2. `single_render_pass` alone: identical captures; Metal System Trace: the 0.34 ms clear gone, shadow encoders merged.
3. Fixed kernel: Xcode GPU capture of the race frame, shader profiler on the composed opaque material: registers and
   occupancy for hard / rotated SoftHigh / rotated SoftMedium / fixed (1) / fixed + early out (2) / fixed, runtime
   loop (3), and fixed SoftLow (4 taps) against fixed SoftMedium (8). This separates the three candidates of cause 6:
   1 against rotated is the rotation state and the cache locality, 3 against 1 is the unrolled loop's register
   pressure, 4 against 8 taps is sampling throughput. Then `tools/perf/run-benchmark.py --wait-idle` race and city roam
   at 1920 x 1080, interleaved against the hard default. Target: within ~1 ms of the hard filter. If no mode gets
   there, the larger follow-ups in "Open points" are next.
4. Fixed box: `--shadow-box-probe` (SceneKit's box semantics, 0.0-0.4/255 before), `CAL_EXP=penumbra` to refit the
   kernel scale, `--ground-bias-probe`, `--shadow-motion-probe` (texels must not move with the camera at all now), the
   full-game comparison against macOS (`PORTING.md`, "Full-game comparison"). Read SceneKit's `shadowKernel` values
   (a Metal frame capture of the SceneKit reference, `pen2.swift`, shows the constant buffer bound to the fragment
   function; or a `texspy`-style probe that copies it). If they are far from the rings, use SceneKit's own taps as the
   fixed kernel (a small follow-up to 0003: `get_fixed_ring_disk` would take them from a project setting), including
   their z offsets if the bias probes need them.
5. Windows export on a Windows PC (D3D12 and the Vulkan fallback), `tools/export-verify.py`.

## Open points

- Everything here is uncompiled. The C++ follows the surrounding 4.7.2 code (`FUNC6`, `GLOBAL_GET_CACHED`,
  `draw_list_set_viewport`/`draw_list_enable_scissor`, `Frustum(Vector<Plane>)`), the shaders were not run through
  glslang; expect small compile fixes.
- Whether the fixed kernel gets within ~1 ms of the hard filter is the open measurement. If register pressure in the
  composed shader is the cause, the next steps are (a) computing the directional shadows before the material code when
  the material does not write `LIGHT_VERTEX` or `NORMAL` (shorter live ranges), and (b) a screen-space shadow mask
  from the depth prepass for the opaque pass (one texel fetch per light; transparent surfaces keep the fixed kernel).
  Both are larger patches.
- SceneKit's filter code is known (point 7 above), its tap values are not (the `shadowKernel` constant buffer). Until
  they are read out, the ring kernel matches SceneKit's penumbra width after calibration, not tap for tap.
- `single_render_pass` on Metal still stores the whole 8192² atlas once per frame (Godot's Metal driver treats any
  partial render area as load/store). With fixed boxes only 6144 x 4096 of it is used; a smaller atlas or an
  origin-anchored render area (a Metal driver change) would save that store too.
- The macOS x86_64 template slice needs the Vulkan SDK (MoltenVK) on the build machine.
- **Upgrades.** Every Godot update means rebasing the series, rebuilding the editor and all four templates (about an
  hour per target class on the M2) and repeating validation steps 1-4. 0001 and 0002 are small and rebase easily.
  0003 and 0004 touch code that upstream reworks often: the directional shadow block of `scene_forward_clustered.glsl`
  and `scene_forward_mobile.glsl`, the spec-constant bit layout (0003 takes `packed_1` bits 6-7; any new upstream bit
  there collides), `DirectionalLightData` (0004 takes its `pad` words), `_light_instance_setup_directional_shadow` and
  the light culler. Expect a patch release (4.7.x) to apply with offsets and a minor release (4.8) to need a manual
  rebase of 0003/0004 of a few hours, plus a full re-validation, because the look depends on them. Moving to a newer
  Godot also means changing the csproj's `Godot.NET.Sdk` version, `GODOT_TAG`/`TEMPLATE_VERSION` in
  `build-engine.sh`, and the version check in its `source` step. Upstreaming 0002 (no API) and 0003 (a project
  setting) would cut this to 0004.
