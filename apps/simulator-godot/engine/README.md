# Patched Godot 4.7.2 for the Marvin port

SceneKit-like soft directional shadows at close to hard-shadow cost need changes inside Godot's renderer, which the
game cannot make from C#. This folder holds a small patch series against the **4.7.2-stable** tag, a script that builds
the patched .NET editor and export templates on macOS (Windows templates cross-compiled), and the plan for using it
from the SceneKit facade. Every change is behind a project setting or a new RenderingServer call: with nothing set, the
patched engine renders exactly like stock 4.7.2, and the facade keeps working on the stock engine.

Status: drafted and checked to apply cleanly to 4.7.2-stable (`patch -p1`, file for file against the tag). **Not compiled
and not measured yet** (other GPU benchmarks were running on this Mac); see "Open points".

| | |
|---|---|
| `patches/` | `git format-patch` series, applied in order with `git am` (`build-engine.sh source`) |
| `build-engine.sh` | prerequisites, source + patches, macOS editor with .NET glue and assemblies, macOS and Windows templates, install |
| `build/` | gitignored: logs, the SCons venv, the packaged `.tpz` |
| `.gdignore` | keeps Godot's importer out of this folder |

## Why the soft filter costs ~4 ms (from the 4.7.2 source)

Measured before (`docs/performance.md`): at 1080p on the M2 the two shadowed suns cost about 7.4 ms of GPU per frame
with the soft filter (SoftHigh), SceneKit about 1.4-1.8 ms. Godot's soft filter alone is ~4 ms more than its hard
filter, and SoftMedium's 8 taps cost the same as SoftHigh's 16 (frozen town view, within the ±0.6 ms run spread). The
hard filter cut the opaque pass by 2.3-2.8 ms and the race's transparent pass from 3.75 to 1.19 ms. Paths are relative
to `servers/rendering/` in the Godot source.

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
   and results to that live set (specialized pipelines do not annotate the loop, `scene_forward_lights_inc.glsl:10-16`,
   so the driver unrolls it); a lower occupancy tier for the whole shader explains why 8 and 16 taps cost the same and
   why the cost appears in every lit fragment of both color passes. The hard path's single `textureProj` adds almost
   nothing. (Hypothesis from the measurements and the code; the build stage confirms it with Xcode's shader profiler.)
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

## The patches

| patch | switch (default = stock) | what it changes |
|---|---|---|
| `0001` version tag | always | `version.py` `module_config = ".marvin"`: the engine reports `4.7.2.stable.marvin.mono` and uses `export_templates/4.7.2.stable.marvin.mono`. The status stays `stable`, so the C# packages stay `Godot.NET.Sdk`/`GodotSharp` 4.7.2 from nuget.org. |
| `0002` one render pass for the directional atlas | `rendering/lights_and_shadows/directional_shadow/single_render_pass` (bool, false) | Forward+ draws all directional splits in one draw list on the atlas: `DRAW_CLEAR_DEPTH` over the whole atlas (a load-action clear), per-split viewport and scissor, one store. Removes the separate 0.34 ms clear and the per-split load/store. Same image. |
| `0003` fixed-kernel directional PCF | `rendering/lights_and_shadows/directional_shadow/soft_shadow_filter_kernel` (0 Rotated, 1 Fixed, 2 Fixed With Early Out) | `sample_directional_pcf_shadow_fixed`: the same taps for every pixel (point-symmetric rings of four, outermost ring first, `get_fixed_ring_disk`), each a hardware bilinear comparison; no hash, no sin/cos, no rotation matrix, no noise. Mode 2 takes the outer ring first and stops when its four taps agree. Sample count and radius still from `soft_shadow_filter_quality` and `shadow_blur`. A 2-bit specialization constant (`packed_1` bits 6-7), so the other path compiles out. Read when `directional_soft_shadow_filter_set_quality()` is called. |
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
  (`light_angular_distance > 0`) are not clipped to a fixed box; the rotated kernel can read a few texels beyond a
  fixed box near its edge.

Upstream: 0002 and 0003 are self-contained candidates (a render-pass optimisation and a filter option). 0004 would
need `DirectionalLight3D` properties and an editor gizmo upstream; here the RenderingServer call is enough.

## Building

```sh
apps/simulator-godot/engine/build-engine.sh all     # or step by step:
apps/simulator-godot/engine/build-engine.sh deps    # SCons in engine/build/venv, llvm-mingw, checks
apps/simulator-godot/engine/build-engine.sh source  # ~/src/godot-4.7.2-marvin, branch marvin-4.7.2 = tag + git am patches/
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
  The .NET module loads `hostfxr` at run time, so no Windows .NET packs are needed to build.
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
`git -C ~/src/godot-4.7.2-marvin format-patch -o apps/simulator-godot/engine/patches 4.7.2-stable` (delete the old files
first). For a newer Godot, rebase the branch onto the new tag and change `GODOT_TAG`/`TEMPLATE_VERSION` in
`build-engine.sh` and the version check in its `source` step.

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
    ProjectSettings.SetSetting("rendering/lights_and_shadows/directional_shadow/soft_shadow_filter_kernel", SceneKitCalibration.ShadowKernelMode); // 1, or 2 (early out)
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
5. **Graphics Detail levels**: whichever levels want soft shadows use, on the patched engine, the fixed kernel + boxes
   instead of SoftHigh + split emulation; the hard levels can use the boxes too (two passes instead of up to eight,
   texels fixed at SceneKit's size) or keep the near splits for finer close-up texels. `BaseTerrainCastsShadow` stays a
   per-level choice (in a box map the flat base terrain fills the whole 4096 map and shadows nothing visible).

## Validation plan (build stage)

1. Build; `--headless --version`; the facade test, calibration scenes and probes on the patched engine with every new
   switch off must be byte-identical to the stock engine (`--calibration`, `--shadow-box-probe`, `--ground-bias-probe`,
   `--shadow-motion-probe`, the deterministic smoke and town captures).
2. `single_render_pass` alone: identical captures; Metal System Trace: the 0.34 ms clear gone, shadow encoders merged.
3. Fixed kernel: Xcode GPU capture of the race frame, shader profiler on the composed opaque material: registers and
   occupancy for hard / rotated SoftHigh / rotated SoftMedium / fixed / fixed + early out (confirms or refutes cause 2).
   Then `tools/perf/run-benchmark.py --wait-idle` race and city roam at 1920 x 1080, interleaved against the hard
   default. Target: within ~1 ms of the hard filter.
4. Fixed box: `--shadow-box-probe` (SceneKit's box semantics, 0.0-0.4/255 before), `CAL_EXP=penumbra` to refit the
   kernel scale, `--ground-bias-probe`, `--shadow-motion-probe` (texels must not move with the camera at all now), the
   full-game comparison against macOS (`PORTING.md`, "Full-game comparison").
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
- SceneKit's exact kernel offsets are unknown (its `shadowKernel` uniform); the ring kernel matches its penumbra width
  after calibration, not tap for tap.
- `single_render_pass` on Metal still stores the whole 8192² atlas once per frame (Godot's Metal driver treats any
  partial render area as load/store). With fixed boxes only 6144 x 4096 of it is used; a smaller atlas or an
  origin-anchored render area (a Metal driver change) would save that store too.
- The macOS x86_64 template slice needs the Vulkan SDK (MoltenVK) on the build machine.
