# Marvin Simulator: Godot port

A faithful port of the macOS game in `apps/simulator-macos` to Godot 4.7 (.NET/C#), so it runs on Windows as well as macOS. The goal is that it looks and plays as close to the macOS game as possible.

**The macOS game is the reference, not a dependency.** Never modify anything under `apps/simulator-macos`. Read it, run it, capture it, and compare against it.

## Layout

| Path | Contents |
|---|---|
| `project.godot`, `MarvinGodot.csproj` | Godot project (Forward+, C#). Assembly `MarvinGodot`. |
| `src/Core/` | `MarvinCore`: a C# port of `Sources/SimulationCore`. Pure C#, no Godot types, double precision. One `.cs` file per Swift file, same file names. |
| `src/Checks/` | `MarvinChecks`: a console port of `Tests/SimulationCoreTests` (SimulationChecks). Runs without Godot. |
| `scripts/` | Godot-side C# for the app target, mirroring `Sources/MarvinSimulator` file by file (e.g. `scripts/World/DirtWorld.cs`, `scripts/Town/TownWorld.cs`). |
| `shaders/` | `.gdshader` ports of the SceneKit shader modifiers. |
| `scenes/` | Godot scenes. `Main.tscn` is the entry point. |
| `assets/` | **Gitignored.** Runtime assets copied from the macOS game by `tools/sync-assets.py`. |
| `reference/` | **Gitignored.** Reference outputs from the macOS game (SimulationChecks output, captures). |
| `tools/` | `env.sh`, `godot`, `build`, `checks`, `sync-assets.py`. |

## Build and run

```sh
apps/simulator-godot/tools/sync-assets.py      # once, and after macOS assets change
apps/simulator-godot/tools/build               # dotnet build (Godot assembly + MarvinCore)
apps/simulator-godot/tools/checks              # SimulationChecks port (no Godot)
apps/simulator-godot/tools/godot --headless --import   # first time: import assets
apps/simulator-godot/tools/godot               # run the game
apps/simulator-godot/tools/godot -- <game args> # game arguments go after "--" (OS.GetCmdlineUserArgs)
```

`tools/env.sh` sets `DOTNET_ROOT` (`~/.dotnet`) and `GODOT` (`~/Applications/Godot_mono.app`). In this shell, `cat` and `ls` are aliased to missing tools: use `command cat`, `command ls`, or the Read and Write tools. Use `gtimeout` (GNU coreutils) to bound any Godot run.

## Translation rules

**Mirror the Swift code.** Port file by file and keep every type, member and function name **verbatim**, including Swift's camelCase member names. Do not restructure, "improve" or simplify behaviour. If something cannot be mirrored, note the deviation in a `// PORT:` comment.

**Numbers.**
- Swift `Double` → `double`, `Float` → `float`, `CGFloat` → `double`.
- Swift `Int` is 64-bit. Use `long` wherever values can exceed 32 bits: hashes, seeds, RNG state, products of indices. Plain `int` is fine for small counts and indices.
- Wrapping operators `&+ &- &*` → C#'s default unchecked arithmetic on the same width. Bit shifts on unsigned types → the same unsigned C# types.
- `x.rounded()` is round-half-away-from-zero: `Math.Round(x, MidpointRounding.AwayFromZero)`. C#'s default `Math.Round` is banker's rounding and is **wrong** here. `.rounded(.down)` → `Math.Floor`, `.rounded(.up)` → `Math.Ceiling`, `.rounded(.towardZero)` → `Math.Truncate`.
- `Int(x)` truncates toward zero: `(long)x`. `truncatingRemainder(dividingBy:)` → `%` on doubles. `%` on integers is truncated in both languages.
- `Float` maths uses the float functions (`MathF.Sin`), `Double` maths uses `Math.Sin`. On macOS .NET calls the same Darwin libm as Swift, so results should be bit-identical. Keep operation order exactly as written; do not use FMA.

**simd.** Core provides `Double2`, `Double3`, `Double4`, `Float2`, `Float3`, `Float4`, `QuatD`, `QuatF`, `Double3x3` and `Float4x4`, plus a static `Simd` class mirroring Apple's functions (`Simd.dot`, `Simd.length`, `Simd.length_squared`, `Simd.distance`, `Simd.normalize`, `Simd.cross`, `Simd.min`, `Simd.max`, `Simd.mix`, `Simd.clamp`, quaternion `act` and `slerp`). They follow Apple's header formulas:
- `dot` sums the lane products left to right;
- `length = sqrt(dot(x, x))`;
- `normalize(x) = x * (1 / sqrt(dot(x, x)))`;
- `distance = length(x - y)`.

**Value semantics.** Swift structs **and Swift arrays/dictionaries** are values; copying one and mutating the copy never affects the original. C# arrays and `List<T>` are references. Where Swift copies a struct that holds arrays (for example `var stopped = state; stopped.stop()`) and then mutates either side, the C# port must deep-copy, or the mutation leaks. Port value-type models as C# `struct`s with explicit `Clone()`/copy where they hold collections, and audit every copy site.

**Randomness.** Seeded generators (custom `RandomNumberGenerator` structs, LCGs, hashes) must give identical sequences. Swift's `Double.random(in:using:)`, `Float.random(in:using:)`, `Int.random(in:using:)`, `Bool.random(using:)` and `shuffle(using:)` / `shuffled(using:)` / `randomElement(using:)` have specific stdlib algorithms. Port them **exactly**, from the Swift stdlib source, into `Marvin.Core.SwiftRandom`. Unseeded calls (`SystemRandomNumberGenerator`, plain `.random(in:)`) may use `System.Random.Shared`.

**Collections.** Swift `Dictionary`/`Set` iteration order is unspecified. Where Swift code depends on iteration order, the result was already nondeterministic: keep the C# behaviour deterministic and note it.

## SceneKit → Godot

Coordinate systems match: right-handed, +Y up, cameras look down −Z, metres.

| SceneKit | Godot |
|---|---|
| `SCNNode` without geometry | `Node3D` |
| `SCNNode` with geometry | `MeshInstance3D` (or a `Node3D` with a `MeshInstance3D` child when the node also has children with their own transforms) |
| `position`, `scale` | `Position`, `Scale` |
| `eulerAngles` (pitch, yaw, roll) | Set `RotationOrder = EulerOrder.Zyx` and `Rotation = euler`, or build `Basis.FromEuler(euler, EulerOrder.Zyx)`. **Measured:** SceneKit's matrix equals Godot's ZYX basis. Godot's default YXZ order is wrong. |
| `simdOrientation` / quaternion | `Quaternion` (x, y, z, w) |
| `pivot` (translation-only, measured) | A point q maps to `position + R·q − pivotTranslation`. Used only in TownShadowBatch and World.swift. Re-measure with a SceneKit script for anything else. |
| `isHidden` | `Visible = false` |
| `opacity` | `GeometryInstance3D.Transparency = 1 - opacity` |
| `castsShadow` | `CastShadow` |
| Shadow-only proxy via `categoryBitMask` | `CastShadow = ShadowCastingSetting.ShadowsOnly` |
| `renderingOrder` | `Material.RenderPriority` (sorting among transparents); opaque order is by depth |
| `SCNLevelOfDetail(worldSpaceDistance:)` | One `MeshInstance3D` per level with `VisibilityRangeBegin`/`End` |
| `SCNGeometrySource`/`Element` | `ArrayMesh.AddSurfaceFromArrays`. Godot has `UV`, `UV2` and `CUSTOM0`-`CUSTOM3` (up to 4 floats each, `ArrayFormat.FormatCustomRgbaFloat`) for extra channels, plus `Color`. |
| Triangle winding | **SceneKit front faces are counter-clockwise, Godot's are clockwise.** Reverse each triangle's index order when converting (or the faces are culled). |
| `SCNBox`/`SCNSphere`/`SCNCylinder`/`SCNPlane`/`SCNFloor` | `BoxMesh`/`SphereMesh`/`CylinderMesh`/`PlaneMesh`, or generate the same tessellation in code. Match segment counts and chamfers. |
| `SCNMaterial` `.physicallyBased` | `StandardMaterial3D` (metallic/roughness) or a `ShaderMaterial` |
| `.lambert` / `.constant` | Diffuse-only shading / `ShadingMode.Unshaded` |
| `diffuse` colour (sRGB `NSColor`) | Albedo `Color` (Godot colours are sRGB; it linearises them) |
| Shader modifier `.geometry` | `vertex()` in a `.gdshader` |
| `.surface` | `fragment()` (set ALBEDO, ROUGHNESS, METALLIC, NORMAL_MAP, EMISSION, ...) |
| `.fragment` (post-lighting colour) | No direct hook. Reproduce the effect inside `fragment()`/`light()`, or `render_mode unshaded` with manual lighting. Choose whichever matches the captures best and document it. |
| `SCNLight` `.directional` | `DirectionalLight3D`. SceneKit intensity 1000 ≈ Godot `LightEnergy` 1.0 (calibrate against captures). |
| `.ambient` | `Environment.AmbientLight*` |
| Shadow map size | Project setting `rendering/lights_and_shadows/directional_shadow/size` (one atlas shared by all directional lights) |
| `SCNCamera.fieldOfView` (vertical) | `Camera3D.Fov` (vertical with `KeepAspect = Height`); same `Near`/`Far` |
| `wantsHDR`, exposure, bloom | `Environment` tonemap (Linear or Filmic, chosen by capture comparison), exposure, Glow |
| `screenSpaceAmbientOcclusion*` | `Environment.Ssao*` |
| Scene fog (linear start/end) | `Environment` fog in depth mode (`FogMode = Depth`, begin/end, curve 1.0 for linear) |
| `lightingEnvironment` (IBL image) | `Environment` sky (`PanoramaSkyMaterial` or a sky shader), ambient and reflections from the sky |
| `background` colour | `Environment.BackgroundMode = Color` |
| `SCNView.antialiasingMode` | `Viewport.Msaa3D` |
| AppKit views, Quartz drawing | `Control` nodes; immediate-mode drawing in `_Draw()` |
| SF Pro / SF Mono | `SystemFont` with `FontNames` `["SF Pro Text", "SF Pro", ".AppleSystemUIFont"]` / `["SF Mono", "Menlo"]`; on Windows it falls back to Segoe UI / Consolas |
| `NSEvent.keyCode` | `InputEventKey.PhysicalKeycode` |
| `Timer` 1/60 s tick | `_Process(delta)`, with the same dt clamping and pause rules |
| UserDefaults / Application Support | `user://` (`ConfigFile` for settings, JSON for scores) |
| GCD background world build | `Task.Run` for data generation; node-tree changes on the main thread |
| AVAudioEngine | Godot audio buses and `AudioStreamPlayer`s (or an `AudioStreamGenerator` mixer), keeping gains, rates, pans and filters |

**Assets.** Load from `res://assets/...`. Marvin's `geometry.bin` and the JSON meshes are raw files read with `FileAccess`. Textures are imported with mipmaps (sync writes `.import` files).

## Validation

- **Logic:** `tools/checks` must print the same values as `reference/simulation-checks-swift.txt`, the macOS SimulationChecks output at the same commit. On macOS the numbers should match exactly.
- **Visuals:** compare captures of the same scene, camera and state against the macOS game's captures. Run the macOS game's own capture modes (`'apps/simulator-macos/.build/Marvin Simulator.app/Contents/MacOS/MarvinSimulator' --smoke-test DIR`, `--town-smoke-test DIR` and others; see its README) and add matching capture modes to the Godot game (`tools/godot -- --capture <name> DIR`). Inspect both images side by side. A passing number is never visual acceptance.
- **Behaviour:** port the macOS smoke checks that verify gameplay, and keep their JSON report keys.

## Agent rules

- Do not modify `apps/simulator-macos` (or anything outside `apps/simulator-godot`).
- Do not commit or push. The orchestrator commits.
- Stay inside your assigned files and folders. Report every deviation from the macOS behaviour or look, and anything you could not port.
