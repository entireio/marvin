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
| `scripts/SceneKit/` | The SceneKit facade (namespace `Marvin.SceneKit`): SceneKit/AppKit-named types over Godot. Port app code against it; see "SceneKit facade" below. |
| `shaders/` | Shared `.gdshader` includes, if any. Shader modifiers stay in the C# files as Godot-language snippets passed to `shaderModifiers` (see the facade section). |
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
apps/simulator-godot/tools/godot -- --facade-test DIR   # SceneKit facade self-test: PNGs + measurements.json, then quits
apps/simulator-godot/tools/godot -- --calibration DIR   # game-like calibration scenes at 1280x820 (see "Calibration against game scenes")
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

**Core helpers (src/Core).** Beyond `Simd` and `SwiftRandom`, `Swift.cs` ports Swift/Darwin behaviour the app also needs: `Swift.format` (exact `String(format:)`, Darwin rounding), `Swift.description` (Swift number/array printing), libm `hypot`, stable `sorted`, `stride`. Measured: Apple's `simd_cross`, quaternion maths and `stride` use fused multiply-add; .NET's `double.Hypot` differs from Darwin's. Never format numbers with `$"{x}"`/`ToString()` for user-visible or report text; use `Swift.format`/`Swift.description` (the current culture would print decimal commas or U+2212 minus signs). Value-type models that hold collections (`DirtRacePhysics`, `PostRaceEscape`, `RacePerformance`, `SandDeformation`) have `Clone()`; call it wherever Swift copies one and mutates either side. Structs with Swift default values have explicit parameterless constructors: never use `default(T)` or `new T[n]` for them.

**Reference numbers.** `reference/simulation-checks-swift.txt` comes from a **debug** Swift build (`swift run SimulationChecks`); an optimized Swift build differs in the last digits. The C# port matches the debug build byte for byte on macOS.

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
| `pivot` (re-measured) | **Rendering** applies the pivot inside the node: world = parent · T · R · S · pivot⁻¹ (a SceneKit render shows the pivot point at the node position). SceneKit's **model API** (`worldTransform`, `convertPosition`, `worldPosition`) instead reports parent · pivot⁻¹ · T · R · S, i.e. `position + R·S·q − pivotTranslation`. The facade reproduces both (`SCNNode.RenderLocal`/`ModelLocal`). |
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
| `.fragment` (post-lighting colour) | No direct hook. The facade composer emulates SceneKit's ordering; translate with the idioms in "Shader modifier translation guide" below. |
| `SCNLight` `.directional` | `DirectionalLight3D`. Measured: SceneKit intensity 1000 = Godot `LightEnergy` 1.0 exactly (same Lambert + GGX response). |
| `.ambient` | `Environment.AmbientLight*` |
| Shadow map size | One directional atlas shared by all directional lights (`SceneKitCalibration.DirectionalShadowAtlas`, 8192); blur and bias are refitted per camera (see "Lights, cameras, scenes, views") |
| `SCNCamera.fieldOfView` (vertical) | `Camera3D.Fov` (vertical with `KeepAspect = Height`); same `Near`/`Far` |
| `wantsHDR`, exposure, bloom | Measured: SceneKit HDR with fixed exposure is a plain linear clamp, so tonemap = Linear; bloom = Glow (calibrated levels). |
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

## SceneKit facade

`scripts/SceneKit/` (namespace `Marvin.SceneKit`, global usings in `GlobalUsings.cs`) re-creates the SceneKit, AppKit-image and Metal API that `Sources/MarvinSimulator` uses, on top of Godot. Port app files **against the facade**, keeping SceneKit names: `SCNNode` is a `Node3D`, `SCNMaterial` compiles to a Godot `ShaderMaterial`, `SCNView` is a `Control`. All "make it look like SceneKit" work lives in the facade, calibrated against SceneKit renders (see "Calibration"). Do not reach around it with Godot nodes in app code unless a `// PORT:` comment explains why.

```csharp
let node = SCNNode(geometry: g); node.position = SCNVector3(1,2,3); node.eulerAngles.y = a; node.castsShadow = false; parent.addChildNode(node)
var node = new SCNNode(g);       node.position = new SCNVector3(1,2,3); node.eulerAngles.y = a; node.castsShadow = false; parent.addChildNode(node);
```

### Swift to C# spelling

| Swift | Facade C# |
|---|---|
| `node.position.y = 1`, `node.eulerAngles.x = -.pi/2`, `eye.scale.y *= 0.9` | Unchanged. `position`, `eulerAngles`, `orientation`, `rotation`, `scale`, `pivot` are `ref` properties. |
| `SCNVector3Zero`, `SCNMatrix4Identity`, `SCNMatrix4MakeTranslation(...)`, `SCNMatrix4MakeScale` | Unchanged (static import of `SCNGlobals`). |
| `CGFloat(x)`, `NSPoint`, `NSSize`, `NSRect` | `(CGFloat)x` (alias of `double`), `NSPoint`/`NSSize`/`NSRect` aliases of `CGPoint`/`CGSize`/`CGRect` with lowercase members (`minX`, `midY`, `width`). |
| `SCNVector3(simd3)` / `SIMD3<Float>(v)` | `new SCNVector3(simd)` / `v.simd`. simd types are aliases (`SCNFloat3`, `SCNQuatF`, `SCNFloat4x4`) defined in `SimdBridge.cs`; switch them to MarvinCore by defining `MARVIN_CORE_SIMD` (see that file). |
| `SCNGeometrySource(vertices:)` / `(normals:)` / `(textureCoordinates:)` | `SCNGeometrySource.vertices(list)` / `.normals(list)` / `.textureCoordinates(list)` (same parameter types, so factories). |
| `SCNGeometrySource(data:semantic:vectorCount:...)` with `values.withUnsafeBytes { Data($0) }` | `new SCNGeometrySource(SCNGeometrySource.Bytes(values), SCNGeometrySourceSemantic.color, n, true, 4, 4, 0, 16)` |
| `SCNGeometrySource(buffer:vertexFormat:...)`, `buffer.contents().bindMemory(to: V.self, ...)` | Same constructor; `buffer.contents<V>()` returns a `Span<V>`. |
| `SCNGeometryElement(indices: [Int32], primitiveType: .triangles)` | `new SCNGeometryElement(indices, SCNGeometryPrimitiveType.triangles)`; `(data:primitiveType:primitiveCount:bytesPerIndex:)` takes `byte[]` (`data[a..b]` for `subdata`). |
| `geometry.sources(for: .vertex)` | `geometry.sourcesFor(SCNGeometrySourceSemantic.vertex)` (C# cannot overload the `sources` property). |
| `geometry.materials = [m]` | `geometry.materials = new() { m };` The getter returns a copy (Swift arrays are values): assign to change. |
| `SCNLevelOfDetail(geometry:worldSpaceDistance:)` / `(geometry:screenSpaceRadius:)` | `new SCNLevelOfDetail(g, worldSpaceDistance: d)` / `SCNLevelOfDetail.withScreenSpaceRadius(g, r)` |
| `SCNBox(width:height:length:chamferRadius:)`, `SCNSphere(radius:)`, `SCNCylinder(radius:height:)`, `SCNPlane(width:height:)`, `SCNShape(path:extrusionDepth:)`, `SCNText(string:extrusionDepth:)` | Same argument order: `new SCNBox(w, h, l, r)`, ... |
| `m.lightingModel = .constant`, `m.transparencyMode = .aOne`, `light.type = .directional` | `SCNMaterial.LightingModel.constant`, `SCNTransparencyMode.aOne`, `SCNLight.LightType.directional` |
| `m.shaderModifiers = [.geometry: a, .surface: b]` | `m.shaderModifiers = new() { [SCNShaderModifierEntryPoint.geometry] = a, [SCNShaderModifierEntryPoint.surface] = b };` snippets in **Godot shading language** (guide below). |
| `m.setValue(x, forKey: "k")`, `NSValue(scnVector3:)`, `NSValue(point:)`, `NSValue(scnMatrix4:)` | `m.setValue(x, "k")`, `NSValue.scnVector3(v)`, `NSValue.point(p)`, `NSValue.scnMatrix4(m)` (also on `SCNGeometry`). |
| `material.copy() as! SCNMaterial`, `geometry.copy() as! SCNGeometry` | `material.copy()`, `geometry.copy()` |
| `NSColor(srgbRed:green:blue:alpha:)`, `(calibratedRed:...)`, `(deviceRed:...)`, `(red:...)`, `(calibratedWhite:alpha:)`, `(white:alpha:)` | `NSColor.srgbRed(r,g,b,a)`, `NSColor.calibratedRed(...)`, `NSColor.deviceRed(...)`, `NSColor.rgb(...)`, `NSColor.calibratedWhite(w,a)`, `NSColor.whiteAlpha(w,a)` |
| `node.convertPosition(p, to: n)` / `(p, from: n)` (also `convertVector`, `convertTransform`, `simdConvert...`) | `convertPosition(p, to: n)` / `convertPositionFrom(p, n)` |
| `node.look(at: t, up: u, localFront: f)` | Unchanged (named arguments). |
| `node.enumerateChildNodes { n, stop in stop.pointee = true }` | `node.enumerateChildNodes((n, stop) => { stop.pointee = true; });` `childNodes(passingTest:)` is `childNodesPassingTest`. |
| `node.boundingBox.max.y`, `geometry.boundingBox = (min, max)` | Unchanged (value tuples). |
| `SCNTransformConstraint.positionConstraint(inWorldSpace: true) { _, _ in p }` | `SCNTransformConstraint.positionConstraint(true, (_, _) => p)` |
| `SCNSceneRendererDelegate`: `renderer(_:updateAtTime:)`, `renderer(_:didRenderScene:atTime:)`, ... | Interface with default methods `rendererUpdateAtTime`, `rendererDidRenderScene`, ...; `view.@delegate = x`. |
| `SCNRenderer(device:options:)`, `renderer.snapshot(atTime:with:antialiasingMode:)` | `new SCNRenderer(null, null)`, `snapshot(t, size, mode)`; returns `NSImage`. `SnapshotImage` returns a Godot `Image`. |
| `NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: ...)`, `bitmap.bitmapData![i] = v` | Named-argument constructor; `bitmapData` is a `byte[]` (row 0 = top). |
| `NSImage(size:)` + `addRepresentation`, `NSImage(contentsOf: url)`, `image.tiffRepresentation`, `NSBitmapImageRep(data:)` | `new NSImage(size)`, `NSImage.contentsOf("res://assets/...")` (null if missing), `tiffRepresentation` (PNG bytes), `NSBitmapImageRep.data(bytes)` |
| `NSGraphicsContext(bitmapImageRep:)`, `color.setFill(); rect.fill()`, `NSBezierPath(...).stroke()`, `NSAttributedString(string:attributes:).draw(in:)`, `image.lockFocus()` | `NSGraphicsContext.bitmapImageRep(rep)`, unchanged drawing calls, `new NSAttributedString(s, new() { [NSAttributedString.Key.font] = f, ... })`, `lockFocus()`/`lockFocusFlipped(true)`. CPU rasteriser with anti-aliasing; text uses Godot's font cache. |
| `NSFont.monospacedSystemFont(ofSize:weight:)`, `NSFont(name:size:)` | `NSFont.monospacedSystemFont(size, NSFont.Weight.semibold)`, `NSFont.named(name, size)` |
| `MTLCreateSystemDefaultDevice()`, `MTLTextureDescriptor.texture2DDescriptor(...)`, `texture.replace(region:mipmapLevel:withBytes:bytesPerRow:)` | Unchanged names; `withBytes` takes the `float[]`/`byte[]`. Textures become Godot `ImageTexture`s. |
| `SCNTransaction.begin()/commit()`, `disableActions` | Unchanged; no implicit animations exist, everything is immediate. |
| `SCNHitTestOption.backFaceCulling.rawValue` | `SCNHitTestOption.backFaceCulling` (string key). |
| `DispatchQueue.main.async { }`, `DispatchQueue.global(qos: .userInitiated).async { }`, `DispatchQueue.main.asyncAfter(deadline: .now() + s, execute: f)` | `DispatchQueue.main.async(() => { })`, `DispatchQueue.global(DispatchQoS.userInitiated).async(() => { })`, `DispatchQueue.main.asyncAfter(DispatchTime.now() + s, f)`. Main blocks run in order on Godot's main thread at the start of the facade's per-frame flush; global blocks run on the .NET thread pool. |
| `CommandLine.arguments.contains("--flag")` | `CommandLine.arguments.Contains("--flag")` (`System.Linq`); the list (a `string[]`) holds the executable, Godot's arguments and the game arguments after `--`. `Environment.GetCommandLineArgs()` is empty inside Godot .NET. |
| `Float.pi` | `Swift.floatPi` (Core): 0x40490FDA, rounded toward zero; `MathF.PI` is one float step larger. `Double.pi` is `Math.PI`. |

### Measured SceneKit semantics reproduced by the facade

Measured with Swift probes on macOS 27 (reproduce with `tools/scenekit-reference/FacadeReference.swift`):

- **eulerAngles**: R = Rz(z)·Ry(y)·Rx(x) (pitch applied first; Godot `EulerOrder.Zyx`). Values read back exactly as written (`eulerAngles.y = 4` reads 4, not a normalised angle); `rotation` reads back as written; whichever of euler/orientation/rotation was written last is authoritative.
- **pivot**: see the table above (render vs model API).
- **Defaults**: `SCNMaterial()` is `.blinn`, diffuse white, specular black, mipFilter `.nearest`, wrap `.clamp`; a new `SCNGeometry` has one default material. `SCNLight()` is omni 1000, `castsShadow` false, shadowRadius 3, shadowBias 1, orthographicScale 1, maximumShadowDistance 100. `SCNCamera()` fov 60 (vertical), zNear 1, zFar 100, `wantsExposureAdaptation` true, bloomThreshold 0.5, bloomBlurRadius 4. `SCNNode` castsShadow true, categoryBitMask 1.
- **Colours**: SceneKit shades in linear extended sRGB. `srgbRed`/`deviceRed`/`white:`/gray-family named colours use the sRGB curve; `calibratedRed` is Generic RGB (gamma 1.8 then a primaries matrix); `calibratedWhite` is gamma 1.8. Light, ambient and fog colours are converted the same way. Diffuse/emission/multiply textures are sRGB-decoded; roughness/metalness/ambientOcclusion/normal textures are raw.
- **Vertex colours** are raw floats that multiply `_surface.diffuse` *before* `.surface` modifiers (a modifier that writes the diffuse replaces them). Alpha premultiplies diffuse: `NSColor` alpha twice for `.constant`/`.lambert`/`.blinn`/`.phong` but **once for `.physicallyBased`**; vertex and texture alpha once for every model. A transparent PBR surface keeps its full specular highlight (glass-like), and `.physicallyBased` materials **ignore `transparency`** in the default `.aOne` mode (measured: opaque result). A geometry's elements are drawn in order with depth writes, so a double-sided transparent box shows only the face drawn first.
- **No normal source**: shaded with flat face normals facing the viewer (measured: a horizontal quad without normals lights exactly like one with +Y normals for either winding, a shared-vertex ridge shades per face). The composer derives them per pixel (`VariantFlags.NoNormals`).
- **Lighting**: directional 1000 lm = irradiance 1 (Lambert, no 1/π) plus GGX specular (F0 0.04, exact height-correlated Smith visibility, no multi-scatter energy compensation). Godot uses an approximate visibility term and energy compensation, so the composer gives PBR materials a matching `light()`. Ambient lights add albedo x intensity/1000 for every lit model, independent of metalness and without specular; `.constant` ignores them. `lightingEnvironment` diffuse and specular follow SceneKit's own roughness responses (fitted); reflections are sharp on smooth materials (the game's sky probe shows a hard horizon line in a chrome sphere) and the reflected intensity scales with f0 (no multi-scatter energy compensation, as for direct light). `.emission` adds after lighting; `.multiply` multiplies the lit colour (`mix(1, m, intensity)`).
- **HDR**: `wantsHDR` with fixed exposure is a linear clamp (emission k renders k, clipped at 1), confirmed on the game-like scenes (suns, emissive patches). Bloom only above `bloomThreshold`; its blur scales with the render size (the same glow levels fit at 400 and 820 pixels high).
- **SSAO** darkens ambient and image-based light only, and much more broadly than Godot's at equal settings (whole faces near the ground, 20-35% at the game's 0.70/1.6). It also darkens rough reflections (metals, roughness >= 0.3) fully but leaves sharp reflections (roughness 0.1) almost untouched.
- **SSAO and `.geometry` modifiers**: a material with a `.geometry` shader modifier gets (almost) no SSAO: SceneKit leaves it out of its SSAO depth/normal pass (an identity modifier on a box keeps 0.994 of its ambient light at the base, 0.853 without). Nearly everything in the town has one (city tint, crowd gait, ground tints), so SceneKit's town shows almost no SSAO (0.70 vs 0: 0.5/255). The composer routes such materials' ambient light around Godot's SSAO (town entrance captures 3.26 -> 2.32/255; calibration race scenes slightly closer). Not reproduced: with a large modified surface behind it, SceneKit darkens every pixel by about 5% of the ambient light.
- **isNode(_:insideFrustumOf:)** tests the node's world-space axis-aligned bounding box (the box around the transformed local bounding box), not the oriented box: a unit box rotated 0.6 rad leaves the frustum at x = 4.64-4.68 (world AABB 4.653, oriented 4.261). Visibility traces of the town people now match SceneKit frame for frame.
- **Fog**: linear in Euclidean view distance, `pow(f, fogDensityExponent)`, mixed in linear space; enabled when `fogEndDistance > fogStartDistance`.
- **Shadows**: forward shadows remove `shadowColor.alpha` of that light. Deferred shadows leave the light itself unshadowed and multiply the **final** colour (all lights, ambient, sky light, emission) by `1 - alpha x shadow`; faces turned away from the light count as shadowed (thin double-sided planes seen from behind darken too), and a large `shadowRadius` self-shadows lit curved/sloped surfaces as a function of the angle to the light only (measured on spheres of 0.15 m and 1 m, map 2048/4096, orthographicScale 2/10: identical): radius 3 none; 8: plateau 0.28 from 37 deg; 16-32: 0.44 from 27 deg. `shadowBias` has no measurable effect (0.001, 1 and 10 identical, forward and deferred). Penumbra (10-90%) of a fixed shadow box is about 1.2 x `shadowRadius` texels of the light's own map in light space (map 4096 over 116 m, radius 3: 11.5 cm on a floor lit at 60 deg). Transparent geometry casts opaque shadows. In the sandbox's deferred setup SceneKit also darkens lit surfaces by 4-6% at grazing view angles (none from above); not reproduced.
- **SCNShape**: curves are flattened with the path's `flatness` (default 0.6, so small curves become chords), overlapping contours are unioned whatever their winding, contained contours become holes, tiny contours vanish (the robot-eye ovals do not render in SceneKit). `extrusionDepth` 0 is a single +Z face.
- **SCNText**: pen starts at x = 0, baseline at y = 1.0 (monospaced system font, sizes 1 and 2).
- **Primitive sources**: SceneKit's primitive geometries expose unit template sources (24 vertices for any box). The facade builds real tessellations instead: chamfered boxes with `chamferSegmentCount` (default 10), `(n+1)²`-vertex spheres, 3-element cylinders (side, top, bottom), measured UV layouts.

### Geometry and mesh conversion

Each `SCNGeometry` becomes one `ArrayMesh` (built lazily, rebuilt when sources or the material grouping change), one surface per run of consecutive elements that draw with the same material, triangles in element order, vertices compacted (SceneKit draws elements in order; Godot's order among one instance's transparent surfaces is undefined, and fewer surfaces mean fewer draw calls). Materials repeat cyclically (`materials[element % count]`, measured). Triangles are re-wound (SceneKit front faces are counter-clockwise, Godot's clockwise). Attributes:

| SceneKit source | Godot attribute | Shader access |
|---|---|---|
| vertex, normal | `VERTEX`, `NORMAL` (missing normals: +Z placeholder in the mesh, flat face normals in `fragment()`) | `VERTEX`, `NORMAL` |
| tangent (or generated from texcoord 0, per-vertex averaged, OpenGL Y+ normal maps) | `TANGENT` (+ sign for `BINORMAL`) | `TANGENT`, `BINORMAL` |
| color | `CUSTOM3` (RGBA float; Godot's `COLOR` attribute is 8-bit and the game stores times and signed slopes there) | `COLOR` (the composer copies `CUSTOM3` into `COLOR` first) |
| texcoord channel 0, 1 | `UV`, `UV2` | `SCN_TEXCOORD0`/`1` |
| texcoord channels 2-7 | `CUSTOM0.xy/.zw`, `CUSTOM1.xy/.zw`, `CUSTOM2.xy/.zw` (RGBA float) | `SCN_TEXCOORD2` ... `SCN_TEXCOORD7` (vertex stage) |

`levelsOfDetail` become one `MeshInstance3D` per level with `VisibilityRangeBegin/End` (screen-space LODs are converted for a 1080-pixel, 48° view). SceneKit measures a `worldSpaceDistance` from the node's origin, Godot a visibility range from the instance's AABB centre (both measured: a level whose geometry sits 8 m off its node origin switches by origin distance in SceneKit), so world-space LOD instances get a custom AABB centred on the node origin that encloses every level. A set `boundingBox` becomes `ArrayMesh.CustomAabb`. `node.opacity` multiplies down the hierarchy into `GeometryInstance3D.Transparency`.

### Shader composer

`ShaderComposer` turns each material (plus the geometry's own `shaderModifiers`/`setValue` arguments, which make a per-geometry variant) into Godot shader code; identical code shares one `Shader`. Variants are keyed by render priority (`renderingOrder`, clamped to -128..127), geometry-owned shading and vertex colours. The generated `fragment()` follows SceneKit's order:

1. `_surface` preparation: diffuse (colour x texture x intensity x vertex colour, premultiplied), roughness/metalness (texture component), emission, ambient occlusion, multiply, normal map applied to `NORMAL` (so modifiers see the mapped normal, as in SceneKit).
2. `.surface` modifiers (geometry's first, then the material's).
3. Lighting composition (direct light for every lit model runs in the generated `light()`, see "Measured SceneKit semantics"): `.constant` writes the colour to `EMISSION` (and `ALBEDO` for deferred shadows) with Godot ambient and reflections zeroed; legacy models set `IRRADIANCE` = SH(lightingEnvironment) + ambient lights. For `.physicallyBased`, at the end of `fragment()` (after `.fragment` modifiers) **all ambient light is routed through `IRRADIANCE`**, which Godot multiplies by AO and SSAO: albedo x (SH diffuse x SceneKit's roughness response x (1 - metalness) + ambient lights) + the occluded share of sky specular (pre-filtered radiance x SceneKit's specular response x an analytic split-sum BRDF, no energy compensation). The unoccluded share (sharp reflections, `smoothstep(0.08, 0.3, roughness)` split) goes through `RADIANCE`. Godot then sees `ALBEDO = 1`, `METALLIC = 0`, `SPECULAR = 2.5` (f0 = f90 = 1, so Godot's environment BRDF x energy compensation is exactly 1 and `RADIANCE` passes through); the material's albedo and metalness reach `light()` through varyings.
4. Transparency: `ALPHA = diffuse alpha x transparency` when SceneKit would blend (`#pragma transparent`, alpha < 1, translucent texture, non-alpha blend mode).
5. Fog: `FOG = vec4(fogColor, SceneKit linear fog)` (Godot's own fog is never used).
6. `.fragment` modifiers.

Render state: `blendMode` -> `blend_mix/add/sub/mul`; `writesToDepthBuffer` -> `depth_draw_opaque/always/never` (transparent SceneKit materials still write depth; depth-writing transparent materials also get `depth_prepass_alpha`, which puts them in Godot's shadow pass, opaque above alpha 0.1, because SceneKit casts their shadows); `specular_occlusion_disabled` (Godot 4.4+ dims sky reflections by ambient luminance, SceneKit does not); `readsFromDepthBuffer = false` -> `depth_test_disabled`, or, with a negative `renderingOrder` (sky dome), the geometry is pushed to the far plane so everything else covers it; `colorBufferWriteMask = []` -> depth-only (`blend_mul` with white); `isDoubleSided`/`cullMode`. `.lambert`/`.blinn`/`.phong` = Lambert diffuse, no specular unless `specular` is set (approximated with GGX).

`.geometry` modifiers live in `vertex()`, which Godot also runs for shadow maps and the depth prepass, so deformed casters (CitizenMotion, dune patches) cast deformed shadows as in SceneKit.

The generated `light()` (all lit models): Lambert diffuse; for PBR, or legacy models with a `specular` colour, GGX with Godot's D and Fresnel, the exact height-correlated Smith visibility and no energy compensation. For the scene's deferred shadow light (`scn_deferred.x` = shadow alpha > 0) it applies SceneKit's deferred model: the light's own term is scaled by `1 - alpha x s` instead of being shadowed, and `SPECULAR_LIGHT -= scn_unlit x alpha x s` darkens what Godot adds outside `light()` (`scn_unlit` = emission + ambient + sky light, a fragment-to-light varying); `s` = max(shadow map, faces turned away from the light, SceneKit's large-kernel self-shadowing from `scn_deferred.yz`). Godot reports the full shadow for deferred lights (`ShadowOpacity` 1).

Scene state reaches every shader through global uniforms declared in `project.godot` (`scn_fog_color`, `scn_fog_range`, `scn_ambient`, `scn_ibl`, `scn_sh0..8`, `scn_deferred` = alpha, self-shadow plateau and onset, and the sampler `scn_radiance`), set by the runtime from the scene shown by the active view. `scn_radiance` (`SCNScene.RadianceTexture`) holds the `lightingEnvironment` resampled to 64 x 32 and convolved with GGX lobes for roughness 0, 0.2 ... 1.0 (six stacked equirect bands, linear float, about 30 ms on 8 threads when the probe changes); SH still provides the diffuse part.

### Shader modifier translation guide

Shader modifiers are written in **Godot shading language**, keeping SceneKit's pragma structure. Translate each MSL snippet when you port its file:

- Keep `#pragma arguments`, `#pragma varyings`, `#pragma declaration`, `#pragma body`, `#pragma transparent`. Text before the first pragma is a declaration; a snippet without pragmas is all body.
- Arguments become uniforms set by `setValue(_:forKey:)`: `float3 x;` -> `vec3 x;`, `float4x4` -> `mat4`, `texture2d<float> t;` -> `sampler2D t : filter_nearest;` (put the sampler state in hints; `NSValue.point` -> `vec2`, numbers -> `float`, `SCNMaterialProperty`/`MTLTexture` -> `sampler2D`).
- Varyings: declare `float x;` and write/read `x` directly (drop `out.` and `in.`). `half` -> `float`.

| SceneKit | Godot (inside the snippet) |
|---|---|
| `_geometry.position` (model space, `.geometry`) | `VERTEX` (vec3; `vec4(VERTEX, 1.0)` where a float4 is needed) |
| `_geometry.normal`, `_geometry.tangent` | `NORMAL`, `TANGENT` |
| `_geometry.color` | `COLOR` (float RGBA) |
| `_geometry.texcoords[n]` | `SCN_TEXCOORDn` (`UV`, `UV2`, `CUSTOM0.xy`, ...) |
| `_surface.diffuse.rgb` / `.a` | `ALBEDO` / `ALPHA` (`ALPHA` only exists when the material is transparent) |
| `_surface.diffuseTexcoord` | `scn_diffuse_texcoord` |
| `_surface.normal`, `_surface.position`, `_surface.view` (view space) | `NORMAL`, `VERTEX`, `VIEW` |
| `_surface.roughness`, `_surface.metalness`, `_surface.ambientOcclusion` | `ROUGHNESS`, `METALLIC`, `AO` |
| `_surface.emission` | `EMISSION` |
| `_surface.selfIllumination` | drop it: it is black by default and does not affect `emission` (measured) |
| `_surface.multiply.rgb` | `scn_multiply` |
| `scn_frame.inverseViewTransform`, `viewTransform`, `projectionTransform` | `INV_VIEW_MATRIX`, `VIEW_MATRIX`, `PROJECTION_MATRIX` |
| `scn_node.modelTransform` | `MODEL_MATRIX` |
| `scn_node.modelViewTransform` | `MODELVIEW_MATRIX` (vertex) / `VIEW_MATRIX * MODEL_MATRIX` (fragment) |
| `scn_node.inverseModelViewTransform` (fragment) | `inverse(MODEL_MATRIX) * INV_VIEW_MATRIX` |
| `u_time`, `scn_frame.time` | `TIME` |
| `discard_fragment()` | `discard` |
| `float2/3/4`, `half4`, `uint2`, `float4x4` | `vec2/3/4`, `vec4`, `uvec2`, `mat4` |
| `atan2(y, x)`, `fmod`, `saturate(x)`, `rsqrt` | `atan(y, x)`, `mod` (floored; equal for non-negative operands), `clamp(x, 0.0, 1.0)`, `inversesqrt` |
| `t.read(uint2(p))`, `t.sample(sampler, uv)` | `texelFetch(t, ivec2(p), 0)`, `texture(t, uv)` (`constexpr sampler(address::clamp_to_zero)` -> multiply by an inside-[0,1] test) |
| `#pragma transparent` | unchanged (the material blends) |

`.fragment` runs after lighting in SceneKit; Godot has no hook there, so use these idioms (the composer runs `.fragment` code last in `fragment()`, after `ALPHA` and `FOG` are set):

| SceneKit `.fragment` | Godot |
|---|---|
| `_output.color.rgb *= a; _output.color.a = a;` (premultiplied alpha, `#pragma transparent`) | `ALPHA = a;` |
| `_output.color = float4(c, 1.0);` on `.constant` (replaces the fogged colour) | `ALBEDO = c; EMISSION = c; FOG = vec4(0.0);` |
| `_output.color.rgb *= k;` (scalar, lit material) | `ALBEDO *= k; EMISSION *= k;` (specular is not scaled) |
| reading the fog amount | `FOG.a` (fog colour `FOG.rgb`) |

**Worked example 1 - BinarySky dome** (`.constant`, `cullMode .front`, no depth read/write, `renderingOrder -10000`):

```
// MSL (.geometry)                          // Godot
#pragma varyings                            #pragma varyings
float3 skyDirection;                        vec3 skyDirection;
#pragma body                                #pragma body
out.skyDirection = _geometry.position.xyz;  skyDirection = VERTEX;
// MSL (.fragment, excerpt)                 // Godot
#pragma arguments                           #pragma arguments
float3 sunA; float2 sunRadii; ...           vec3 sunA; vec2 sunRadii; ...
#pragma body                                #pragma body
float3 d=normalize(in.skyDirection);        vec3 d = normalize(skyDirection);
float3 sky=mix(horizon,zenith,h);           vec3 sky = mix(horizon, zenith, h);
...                                         ...
_output.color=float4(mix(sky,stormTint+sky*0.025,storm*0.97),1.0);
                                            vec3 skyOut = mix(sky, stormTint + sky * 0.025, storm * 0.97);
                                            ALBEDO = skyOut; EMISSION = skyOut; FOG = vec4(0.0);
```
Full text: `FacadeTest.SkyGeometry`/`SkyFragment`. Arguments: `sky.setValue(NSValue.scnVector3(d), "sunA")`, `sky.setValue(NSValue.point(new NSPoint(a, b)), "sunRadii")`, `sky.setValue((float)day, "daylight")`.

**Worked example 2 - TownGround** (terrain `.surface`, overlay idiom):

```
// MSL terrainSurface (excerpt)                                     // Godot
float2 p=(scn_frame.inverseViewTransform*float4(_surface.position,1.0)).xz;   vec2 p = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xz;
float angle=atan2(p.y,p.x);                                         float angle = atan(p.y, p.x);
float grain=dot(_surface.diffuse.rgb,float3(0.299,0.587,0.114));    float grain = dot(ALBEDO, vec3(0.299, 0.587, 0.114));
_surface.diffuse.rgb=mix(soil,dune,desert);                          ALBEDO = mix(soil, dune, desert);
_surface.normal=normalize(_surface.normal+float3(...)*desert);       NORMAL = normalize(NORMAL + vec3(...) * desert);
// patches/streets: .geometry                                       // Godot
out.groundTint=half4(half3(pow(max(_geometry.color.rgb,float3(0)),float3(2.2))),half(_geometry.color.a));
                                                                     groundTint = vec4(pow(max(COLOR.rgb, vec3(0.0)), vec3(2.2)), COLOR.a);
// .surface: _surface.diffuse.rgb=float3(in.groundTint.rgb);        ALBEDO = groundTint.rgb;
// .fragment (#pragma transparent): _output.color.rgb *= a; _output.color.a = a;   ALPHA = groundTint.a;
```
Full text: `FacadeTest.PigmentFunctions`, `TerrainSurface`, `GroundTintGeometry`, `GroundTintFragment`.

**Worked example 3 - DirtTrail ink** (`.constant`, `blendMode .multiply`, `renderingOrder 10`):

```
// .geometry: out.trailDistance=length((scn_node.modelViewTransform*_geometry.position).xyz);
//            trailDistance = length((MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz);
// .surface:  float2 uv=_surface.diffuseTexcoord; ... _surface.diffuse=float4(float3(shade),1.0);
//            vec2 uv = scn_diffuse_texcoord; ... ALBEDO = vec3(shade); ALPHA = 1.0;
// .fragment: if(stormActive>0.5) { ... _output.color=float4(mix(float3(1.0),_surface.diffuse.rgb,visibility),1.0); }
//            if (stormActive > 0.5) { ... vec3 faded = mix(vec3(1.0), ALBEDO, visibility); ALBEDO = faded; EMISSION = faded; FOG = vec4(0.0); }
```
Full text: `FacadeTest.TrailGeometry`, `TrailSurface`, `TrailFragment`. Geometry-owned arguments (DirtCoating, DeformableSand) use `geometry.setValue(...)`; `MTLTexture` contents of an `SCNMaterialProperty` argument are read with `texelFetch`.

### Lights, cameras, scenes, views

- `SCNLight`: directional/omni/spot -> `Light3D` child of the node (energy = intensity/1000, colour linearised then re-encoded, `temperature` tint). Ambient lights feed `scn_ambient`. Shadows: `ShadowOpacity` = shadow alpha (forward) or 1 (deferred, see the composer's `light()`); single orthogonal split for one cascade; `DirectionalShadowMaxDistance` = `orthographicScale` when SceneKit does not adjust the projection, otherwise `min(maximumShadowDistance, max(4 x orthographicScale, 10))`. The directional shadow atlas is 8192 (two 4096-class suns), SoftHigh filter.
- **Per-camera shadow fit** (`SceneKitRuntime.FitShadows`, after each view's camera sync): Godot fits a directional shadow map to the camera frustum, its PCF kernel is `blur x quality radius x texel` and its depth bias is `bias/100 x depth range x blur x quality radius` (Godot source, `light_storage.cpp`), so both change with the camera. The facade recomputes Godot's fit (frustum slice bounding sphere, atlas region per light: 4096 x 8192 for two lights, the coarse axis is used) and sets `ShadowBlur` so the kernel has SceneKit's world size (`0.88 x shadowRadius x 2 x orthographicScale / shadowMapSize` for a fixed box; `shadowRadius` Godot texels for an automatically fitted one), `ShadowBias` = (1 + 0.6 x kernel texels) Godot texels in world units and `ShadowNormalBias` = 2 + 0.8 x kernel texels (no PCF acne on lit slopes, measured on spheres and floors).
- `categoryBitMask`: SceneKit bits are mapped to Godot's 20 render layers on first use. A node the camera mask excludes but a shadow-casting light includes casts `ShadowsOnly` (TownShadowBatch proxies).
- `SCNCamera`: copied each frame into the view's `Camera3D` (vertical FOV with `KeepAspect.Height`; `orthographicScale` is half the height). Linear tonemap, exposure 2^`exposureOffset`, glow for bloom (additive, intensity 0.15 x `bloomIntensity`, levels 1 .. round(log2(`bloomBlurRadius`) - 0.6), independent of resolution), SSAO (`radius`, 4 x `intensity`, power 1.5, ambient and occluded sky light only).
- `SCNScene`: a `World3D` and a hidden host `SubViewport` holding `rootNode`; `background.contents` colour or panorama; `lightingEnvironment` -> SH for the composer (Godot's ambient and reflections are disabled).
- `SCNView` (a `SubViewportContainer`) and `SCNRenderer` render the scene's world with their own camera and environment, so one scene can be shown by several views. `snapshot()` renders synchronously (`RenderingServer.ForceDraw` after forcing transform updates). `projectPoint`/`unprojectPoint` use AppKit coordinates (origin bottom-left, z 0..1). `isNode(_:insideFrustumOf:)` tests the node's bounding box against the view frustum.
- Per frame the runtime (`SceneKitRuntime`, last in `_Process`) runs queued `DispatchQueue.main` blocks, then flushes dirty materials, node transforms/meshes/lights, evaluates constraints (render transform only, like SceneKit's presentation tree), syncs view cameras and scene uniforms.
- Threads: `LevelLoading` builds `DirtWorld` on a global queue and hands it to the main queue; that works unchanged. Godot lets any thread change nodes that are outside the scene tree, so an `SCNScene` created off the main thread stays detached, and changes made off the main thread (dirty nodes, materials, textures, constraints) are parked. The main thread adopts them when it takes the objects over: when a scene is assigned to an `SCNView`/`SCNRenderer`, and when `addChildNode` attaches a subtree built elsewhere. `NSGraphicsContext.current` is per thread, as in AppKit. Do not change objects that are already shown from another thread (SceneKit tolerates it, Godot does not), and do not block the main thread on a builder thread: Godot answers some of the builder's RenderingServer calls (creating a scene's viewport) on the main thread. The facade test builds the same content on the main thread, as a whole scene on a pool thread and as a subtree on a pool thread, and checks the renders are identical.

### Calibration

`tools/godot -- --facade-test DIR` renders the facade scenes (64 px calibration probes, primitives, sky, ground overlays and trails, PBR grid, sign texture) and writes PNGs and `measurements.json`. `tools/scenekit-reference/FacadeReference.swift` renders the same scenes with SceneKit (the shader modifiers there are the game's MSL) and compares:

```sh
apps/simulator-godot/tools/godot -- --facade-test /tmp/facade-godot
swiftc -O apps/simulator-godot/tools/scenekit-reference/FacadeReference.swift -o /tmp/facade-reference
/tmp/facade-reference /tmp/facade-scenekit /tmp/facade-godot   # deltas + compare-*.png (SceneKit | Godot | diff x4)
python3 apps/simulator-godot/tools/scenekit-reference/api_coverage.py
```

Results at this commit (linear pixel values; 8-bit quantisation is about 0.005): of the 111 centre-pixel probes, 103 are exact or within one 8-bit step (constant colours, colour spaces, direct light for dielectrics and metals at every roughness and at grazing view angles, ambient, IBL at normal incidence for every roughness, emission, multiply, vertex colours, fog, HDR clamp, blending, forward shadows, LOD switching (also with the geometry 8 m off its node origin), billboard, texture arguments, material cycling); the rest are `I_pbr_rough0_env255` (0.008), fog and opacity probes at 0.006, four grazing-view IBL probes (within 0.021) and the orthographic constant-material deferred shadow, a SceneKit quirk (below). `SCNView` and `SCNRenderer` output is identical. Visual scenes, mean absolute sRGB difference: sky with bloom 0.18/255, PBR grid under the sky probe 0.22, ground overlays and trails 3.3 (SceneKit shadow acne at a grazing sun), primitives 5.6 (text glyphs, shadow acne), sign texture 5.1 (glyph rasterisation). The output of this run is saved in `reference/calibration/probe-suite/`.

### Calibration against game scenes

All mapping constants live in **`scripts/SceneKit/SceneKitCalibration.cs`**; every field can be overridden for experiments without rebuilding: `MARVIN_SCN_CAL="SsaoIntensityScale=3;ShadowKernelScale=1" tools/godot -- --calibration DIR`.

**Method.** `tools/godot -- --calibration DIR` (`scripts/SceneKit/Calibration.cs`) renders six 1280x820 scenes through the facade's `SCNRenderer`. A Swift twin rendered the same scenes with SceneKit's `SCNRenderer.snapshot` on this Mac (M2, macOS 27). The comparison script reports per-image mean difference, mean luminance, a 4x4 grid of luminance ratios and 5x5 patch values at projected 3D probe points, and writes the images listed below. By instruction the Swift and Python tools are not in the repository; copies are in `reference/calibration/tools/` (gitignored): `CalibrationReference.swift` (scenes; `CAL_ONLY`, `CAL_NOSHADOW`, `CAL_NOSSAO`, `CAL_NOBLOOM` as in Calibration.cs), `compare.py`, `ratio.py`, `probe.py`, `aofit.py`, `bloomfit.py`, and the focused SceneKit experiments `deferred.swift` (self-shadowing), `penumbra.swift`, `pane.swift`/`pane2.swift`/`alpha*.swift` (transparency), `mirror.swift` (reflections) and `viewacne.swift`, each mirrored by a `CAL_EXP` mode of Calibration.cs.

| Scene | Game setup mirrored | Camera |
|---|---|---|
| `race_midday_chase`, `race_midday_overview` | BinarySky.swift at fraction 0.5: sky dome (game MSL / facade translation), two forward-shadow suns (4096/2048, fixed orthographic box 58, radius 3/2, 8 samples, bias 0.6, black shadows, intensity 1550/470, air-mass tints), ambient 190, 64x32 sky probe at 0.75, fog 115-240; HDR, fixed exposure, bloom 0.38/1.2/12, SSAO 0.70/1.6/0.025, 2x MSAA | fov 48, zNear 0.02, zFar 250; chase (0.4, 1.9, 5.6) and the race overview (0, 38, -33) |
| `race_evening_chase` | the same at fraction 0.96 (suns 14 deg and 5 deg high, in view) | (5.5, 1.5, 1.8) looking at the suns |
| `sandbox_orbit`, `sandbox_overview` | World.swift: LDR, ambient 550, deferred sun 1400 (4096, 16 samples, alpha 0.24, orthographicScale 10, max distance 22), fog 15-38, plinth, walls and obstacles, 4x MSAA | orbit (4.27, 3.76, 4.9); overview (0, 11.7, -10) |
| `menu_portrait` | MainMenu.swift: constant `SCNFloor`, ambient 650, deferred key 1100 (2048, radius 32, 32 samples, bias 0.001, alpha 0.18), 4x MSAA | (0, 1.05, 2.25), fov 36 |

Every scene contains Marvin's CAD model with the game's materials (eyes as `SCNShape`, wheel spokes; the static `09_track` belt stands in for `TrackBelt`), PBR spheres (5 roughness x dielectric grey, dielectric red, metal), the Dirt clay with its `.surface` tint, the City plaster/ground/adobe scans (normal and roughness maps), a vertex-tinted house with the `cityTint` modifiers, a raw vertex-colour banner, emissive patches (3.0 and 0.8), TownGround-style overlays (premultiplied `.fragment` idiom), a translucent pane, a pole and a table, and in the race scenes houses and distant posts for fog.

**Results** (mean absolute sRGB difference /255; GD/SK mean linear luminance):

| Scene | Before (facade at fa53b8a) | After |
|---|---|---|
| race_midday_chase | 1.90 (1.001) | 1.14 (0.998) |
| race_midday_overview | 0.69 (1.003) | 0.75 (1.003) |
| race_evening_chase | 1.94 (1.000) | 1.78 (0.993) |
| sandbox_orbit | 2.82 (1.026) | 2.26 (1.023) |
| sandbox_overview | 0.81 (1.005) | 0.33 (1.001) |
| menu_portrait | 0.57 (1.004) | 0.29 (1.001) |

What changed, and the measurement behind each change:

| Area | Finding (SceneKit vs facade before) | Calibrated now |
|---|---|---|
| Deferred shadows | Back faces, thin planes seen from behind and emissive surfaces in shadow were 25-32% too bright; Marvin and the menu spheres 7-9% too bright (large-kernel self-shadowing) | exact model in `light()` (see the composer); back faces now identical; menu 0.57 -> 0.29 |
| Shadow kernel and bias | Godot's bias grew with blur (the race suns' depth bias was about 24 cm; with the radius-32 menu light, sphere shadows vanished entirely); penumbra depended on the camera fit; thin leg shadows blurred twice as wide horizontally (4096 x 8192 regions) | per-camera fit; penumbra 10-90% (Godot vs SceneKit): sun A 11.6 vs 11.5 cm, sun B 15.5 vs 15.8, radius 1 4.0 vs 4.4, radius 8 31.6 vs 32.8, orthographicScale 20 4.1 vs 4.0; no PCF acne on lit floors |
| Transparent casters | the translucent pane cast no shadow | casts as in SceneKit |
| Alpha | PBR `NSColor` alpha was applied twice (pane 40% too dark); `transparency` blended PBR | measured rules per lighting model; pane probe exact |
| SSAO | 2x too weak on faces near the ground; never darkened reflections | intensity scale 4 (per-probe occlusion RMS 0.115 -> 0.049); rough reflections occluded, sharp ones not |
| Sky reflections | SH smeared the probe's horizon in chrome; rough white metal 1.7x too bright (energy compensation) | pre-filtered bands; refitted response; mirror-sphere profiles within about 5% for roughness 0-0.6 away from the silhouette |
| Bloom | halo around the suns up to 7% too dark 12-30 px from the disc at 820 px height; levels scaled with height although SceneKit's do not | intensity 0.15, levels 1-3, resolution independent: sun profile RMS 0.0133 -> 0.0039, V_sky 0.22 -> 0.18 |
| Unchanged, verified | tone mapping (linear clamp), exposure, light energy and Generic RGB light colours (the suns' air-mass tints), ambient, fog curve and colour (fog posts within 1%), vertex colours, clay/scan textures, emissive clamp | |

**Constants** (`SceneKitCalibration`): `LightEnergyPerLumen` 1/1000, `AmbientPerLumen` 1/1000; `TonemapExposure` 1, `TonemapWhite` 1 (Linear); bloom `BloomIntensityScale` 0.15, `BloomLevelOffset` -0.6, `BloomHeightScaling` 0, `BloomHdrScale` 1; SSAO `SsaoIntensityScale` 4, `SsaoRadiusScale` 1, `SsaoPower` 1.5, `SsaoDetail` 0.5, `SsaoHorizon` 0.06, `SsaoSharpness` 0.98; shadows `ShadowKernelScale` 0.88, `ShadowBiasTexels` 1, `ShadowBiasPerKernel` 0.6, `ShadowNormalBias` 2, `ShadowNormalBiasPerKernel` 0.8, `ShadowBlurMin/Max` 0.25/16, `DefaultShadowMapSize` 2048, `DirectionalShadowAtlas` 8192, `ShadowFilterQuality` 4 (SoftHigh, quality radius 3), `DeferredSelfShadowPlateau` 0.44 (`DeferredSelfShadow(radius)`); sky light `IblDiffuse` `1 - 0.1757r + 0.3455r² - 0.3623r³`, `IblSpecular` `0.9358 + 1.1768r - 2.5492r² + 0.7755r³`, `IblBlurScale` 1, `IblBlurPower` 1, `SpecularOcclusionFrom/To` 0.08/0.3.

**Images** (`reference/calibration/`, gitignored): `scenekit/NAME.png` and `godot/NAME.png` (1280x820) plus `scenekit/probes.json`; `compare/compare-NAME.png` (SceneKit over Godot over |difference| x 4), `compare/ab-NAME.png` (half-size side by side), `compare/luma-ratio-NAME.png` (red: Godot brighter, blue: darker, full colour at +-20%), `compare/report.txt` (statistics and probes), `compare/summary.json`; `baseline/compare-NAME.png` and `baseline/summary.json` (before this calibration); `experiments.txt` (SceneKit and facade output of the focused experiments: self-shadowing vs angle, penumbra, transparency per lighting model, reflection profiles vs roughness). NAME is each scene in the table above.

**Residual differences** (looked at in the images): SceneKit's forward-shadow acne stripes on house walls at a grazing sun and its 4-6% deferred darkening of lit surfaces at grazing view angles (sandbox orbit) are not reproduced (Godot is clean); SSAO is broader in SceneKit on large faces (house fronts 0.96 vs 1.00) and Godot's is slightly stronger at some contacts; very rough metals brighten towards the silhouette in SceneKit (roughness 0.8-1.0 at grazing views) but not in Godot; the evening ground glint below the suns is slightly weaker; shadow edges are a little softer in Godot's evening shadows; shadow edges sit about 5 cm further towards the caster (normal offset; penumbra test: -4.8 vs -0.7 cm); Godot's direct-light specular highlight on the r 0.1 spheres is slightly smaller.

### Known deviations

- Image-based specular uses an analytic split-sum environment BRDF: within 0.021 linear of SceneKit at grazing views (up to 20% on rough dielectrics at 80°); very rough metals do not brighten towards the silhouette as in SceneKit. SSAO is not applied to the share of sharp reflections (by design, SceneKit's sharp reflections are barely occluded) and is an approximation of SceneKit's broader SSAO.
- SceneKit's fixed orthographic shadow box (`automaticallyAdjustsShadowProjection = false`) cannot be reproduced; Godot fits the shadow to the camera up to `DirectionalShadowMaxDistance` (shadows end there, not at the box edge), with the penumbra and bias refitted per camera to SceneKit's world size. Godot's normal-offset bias removes the acne SceneKit shows at grazing sun angles and makes shadows leak a few centimetres at contact edges. An automatically fitted SceneKit shadow map is assumed to have Godot's texel size (SceneKit fits tighter in close-ups).
- Deferred shadows assume one deferred shadow-casting light per scene (the game's sandbox and menu); a non-black `shadowColor` is treated as black. SceneKit's large-kernel self-shadowing is reproduced as a fitted function of the angle to the light (measured on spheres). Emission above 1 in an LDR view is darkened before SceneKit's 8-bit clamp in Godot, so very bright emissive surfaces in deferred shadow stay clipped white. SceneKit skips deferred shadows on constant materials under orthographic cameras; the facade does not.
- Reflections come from `lightingEnvironment` resampled to 64 x 32 and pre-filtered into six GGX bands (sharp horizon in chrome as in SceneKit); detailed environment images lose detail beyond that resolution. Diffuse sky light uses order-2 SH.
- Godot samples mipmaps trilinearly and applies one global anisotropy level; SceneKit's default `mipFilter = .nearest` and per-property `maxAnisotropy` are not reproduced. `wrapS != wrapT` is not supported (the game never mixes them).
- `.fragment` modifiers cannot read the lit colour; use the idioms above. Exposure adaptation, vignetting, colour fringe, motion blur and depth of field are not emulated (the game leaves them off).
- Premultiplied `NSBitmapImageRep` texels with colour > alpha (WindblownDust's white-with-density texture) are clamped. SceneKit decodes them as extended sRGB while the premultiplied linear value stays below 1 and wraps chaotically above it (measured over all 256 x 256 colour/alpha pairs; white at alpha 64 renders as premultiplied black, at alpha 128 as premultiplied 0.78 instead of 0.5). The white dust sprite therefore darkens its background 5-30% more in SceneKit than in the facade (single-sprite probe, lambert, lit from either side); the garbage values are not emulated.
- `SCNFloor` reflections, `SCNRenderer.render(withViewport:commandBuffer:passDescriptor:)` and Metal capture APIs are not implemented; `snapshot(atTime:)` does not drive shader `TIME`.
- `presentation` returns the node itself; constraints only move the rendered transform. Nodes removed from a scene and dropped are not freed automatically.
- Global scene uniforms (fog, ambient, IBL) come from the last view rendered in a frame; two visible views showing different scenes at once would share them.
- Text uses the platform fonts Godot finds; glyph metrics differ slightly from CoreText.
- `colorAt(x:y:)?.usingColorSpace(.deviceRGB)` returns the stored sRGB components unchanged. On macOS a snapshot bitmap is tagged sRGB and the conversion goes through the display profile, which lifts mid-tones (measured on this Mac: sRGB 0.826 grey reads 0.860, the clay clod colour (0.525, 0.286, 0.165) reads (0.601, 0.361, 0.216)). Numbers that macOS smoke modes print or store after such a conversion (sun occlusion luminance, rendered clod colours, trail-material ratios) are therefore a little higher there for identical pixels.

## Validation

- **Logic:** `tools/checks` must print the same values as `reference/simulation-checks-swift.txt`, the macOS SimulationChecks output at the same commit. On macOS the numbers should match exactly.
- **Visuals:** compare captures of the same scene, camera and state against the macOS game's captures. Run the macOS game's own capture modes (`'apps/simulator-macos/.build/Marvin Simulator.app/Contents/MacOS/MarvinSimulator' --smoke-test DIR`, `--town-smoke-test DIR` and others; see its README) and add matching capture modes to the Godot game (`tools/godot -- --capture <name> DIR`). Inspect both images side by side. A passing number is never visual acceptance.
- **Behaviour:** port the macOS smoke checks that verify gameplay, and keep their JSON report keys.
- **Random state of a macOS run:** smoke modes draw the starting grid and the daylight at random on every `reset`. The race harness (`scripts/World/RaceWorldHarness.cs`) can pin them so a capture compares 1:1: `MARVIN_GRID_SLOTS=i,j,k,l` (indices into `DirtCourse.startingGrid` for player, R2-D2, BB-8, WALL-E) and `MARVIN_DAYLIGHT_FRACTION` / `MARVIN_DAYLIGHT_PHASE` (comma-separated lists, one entry per reset, the last repeats). The grid of a macOS `--smoke-test` run is the permutation whose 240-frame trail counts reproduce `racerTrailMarks` exactly (the reference run in `reference/mac/smoke` is `0,3,1,2`); its daylights were fitted by rendering candidate fractions and phases against the track and sky pixels (`0.515,0.795` / `2.75,2.065` for that run; town smoke `0.2125` / `4.18`).

## Agent rules

- Do not modify `apps/simulator-macos` (or anything outside `apps/simulator-godot`).
- Do not commit or push. The orchestrator commits.
- Stay inside your assigned files and folders. Report every deviation from the macOS behaviour or look, and anything you could not port.
