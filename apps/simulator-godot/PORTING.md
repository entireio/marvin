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
| Shadow map size | Project setting `rendering/lights_and_shadows/directional_shadow/size` (one atlas shared by all directional lights) |
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

### Measured SceneKit semantics reproduced by the facade

Measured with Swift probes on macOS 27 (reproduce with `tools/scenekit-reference/FacadeReference.swift`):

- **eulerAngles**: R = Rz(z)·Ry(y)·Rx(x) (pitch applied first; Godot `EulerOrder.Zyx`). Values read back exactly as written (`eulerAngles.y = 4` reads 4, not a normalised angle); `rotation` reads back as written; whichever of euler/orientation/rotation was written last is authoritative.
- **pivot**: see the table above (render vs model API).
- **Defaults**: `SCNMaterial()` is `.blinn`, diffuse white, specular black, mipFilter `.nearest`, wrap `.clamp`; a new `SCNGeometry` has one default material. `SCNLight()` is omni 1000, `castsShadow` false, shadowRadius 3, shadowBias 1, orthographicScale 1, maximumShadowDistance 100. `SCNCamera()` fov 60 (vertical), zNear 1, zFar 100, `wantsExposureAdaptation` true, bloomThreshold 0.5, bloomBlurRadius 4. `SCNNode` castsShadow true, categoryBitMask 1.
- **Colours**: SceneKit shades in linear extended sRGB. `srgbRed`/`deviceRed`/`white:`/gray-family named colours use the sRGB curve; `calibratedRed` is Generic RGB (gamma 1.8 then a primaries matrix); `calibratedWhite` is gamma 1.8. Light, ambient and fog colours are converted the same way. Diffuse/emission/multiply textures are sRGB-decoded; roughness/metalness/ambientOcclusion/normal textures are raw.
- **Vertex colours** are raw floats that multiply `_surface.diffuse` *before* `.surface` modifiers (a modifier that writes the diffuse replaces them). Alpha premultiplies diffuse (`NSColor` alpha twice, vertex/texture alpha once).
- **No normal source**: lit as if the model-space normal were +Z.
- **Lighting**: directional 1000 lm = irradiance 1 (Lambert, no 1/π) plus GGX specular (F0 0.04, exact height-correlated Smith visibility, no multi-scatter energy compensation). Godot uses an approximate visibility term and energy compensation, so the composer gives PBR materials a matching `light()`. Ambient lights add albedo x intensity/1000 for every lit model, independent of metalness and without specular; `.constant` ignores them. `lightingEnvironment` diffuse and specular follow SceneKit's own roughness responses (fitted). `.emission` adds after lighting; `.multiply` multiplies the lit colour (`mix(1, m, intensity)`).
- **HDR**: `wantsHDR` with fixed exposure is a linear clamp (emission k renders k, clipped at 1). Bloom only above `bloomThreshold`, fixed pixel blur radius.
- **Fog**: linear in Euclidean view distance, `pow(f, fogDensityExponent)`, mixed in linear space; enabled when `fogEndDistance > fogStartDistance`.
- **Shadows**: forward shadows remove `shadowColor.alpha` of that light; deferred shadows darken the final colour by alpha (constant materials too, with perspective cameras).
- **SCNShape**: curves are flattened with the path's `flatness` (default 0.6, so small curves become chords), overlapping contours are unioned whatever their winding, contained contours become holes, tiny contours vanish (the robot-eye ovals do not render in SceneKit). `extrusionDepth` 0 is a single +Z face.
- **SCNText**: pen starts at x = 0, baseline at y = 1.0 (monospaced system font, sizes 1 and 2).
- **Primitive sources**: SceneKit's primitive geometries expose unit template sources (24 vertices for any box). The facade builds real tessellations instead: chamfered boxes with `chamferSegmentCount` (default 10), `(n+1)²`-vertex spheres, 3-element cylinders (side, top, bottom), measured UV layouts.

### Geometry and mesh conversion

Each `SCNGeometry` becomes one `ArrayMesh` (built lazily, rebuilt when sources change), one surface per element with its vertices compacted. Materials repeat cyclically (`materials[element % count]`, measured). Triangles are re-wound (SceneKit front faces are counter-clockwise, Godot's clockwise). Attributes:

| SceneKit source | Godot attribute | Shader access |
|---|---|---|
| vertex, normal | `VERTEX`, `NORMAL` (missing normals -> +Z) | `VERTEX`, `NORMAL` |
| tangent (or generated from texcoord 0, per-vertex averaged, OpenGL Y+ normal maps) | `TANGENT` (+ sign for `BINORMAL`) | `TANGENT`, `BINORMAL` |
| color | `CUSTOM3` (RGBA float; Godot's `COLOR` attribute is 8-bit and the game stores times and signed slopes there) | `COLOR` (the composer copies `CUSTOM3` into `COLOR` first) |
| texcoord channel 0, 1 | `UV`, `UV2` | `SCN_TEXCOORD0`/`1` |
| texcoord channels 2-7 | `CUSTOM0.xy/.zw`, `CUSTOM1.xy/.zw`, `CUSTOM2.xy/.zw` (RGBA float) | `SCN_TEXCOORD2` ... `SCN_TEXCOORD7` (vertex stage) |

`levelsOfDetail` become one `MeshInstance3D` per level with `VisibilityRangeBegin/End` (screen-space LODs are converted for a 1080-pixel, 48° view). A set `boundingBox` becomes `ArrayMesh.CustomAabb`. `node.opacity` multiplies down the hierarchy into `GeometryInstance3D.Transparency`.

### Shader composer

`ShaderComposer` turns each material (plus the geometry's own `shaderModifiers`/`setValue` arguments, which make a per-geometry variant) into Godot shader code; identical code shares one `Shader`. Variants are keyed by render priority (`renderingOrder`, clamped to -128..127), geometry-owned shading and vertex colours. The generated `fragment()` follows SceneKit's order:

1. `_surface` preparation: diffuse (colour x texture x intensity x vertex colour, premultiplied), roughness/metalness (texture component), emission, ambient occlusion, multiply, normal map applied to `NORMAL` (so modifiers see the mapped normal, as in SceneKit).
2. `.surface` modifiers (geometry's first, then the material's).
3. Lighting composition (direct light for `.physicallyBased` runs in the generated `light()`, see "Measured SceneKit semantics"): `.constant` writes the colour to `EMISSION` (and `ALBEDO` for deferred shadows) with Godot ambient and reflections zeroed; lit models multiply, set `IRRADIANCE` = SH(lightingEnvironment) x SceneKit's roughness response + ambient lights, `RADIANCE` = SH(reflection) x SceneKit's specular response, and compensate Godot's `(1 - METALLIC)` on ambient lights.
4. Transparency: `ALPHA = diffuse alpha x transparency` when SceneKit would blend (`#pragma transparent`, alpha < 1, translucent texture, non-alpha blend mode).
5. Fog: `FOG = vec4(fogColor, SceneKit linear fog)` (Godot's own fog is never used).
6. `.fragment` modifiers.

Render state: `blendMode` -> `blend_mix/add/sub/mul`; `writesToDepthBuffer` -> `depth_draw_opaque/always/never` (transparent SceneKit materials still write depth); `readsFromDepthBuffer = false` -> `depth_test_disabled`, or, with a negative `renderingOrder` (sky dome), the geometry is pushed to the far plane so everything else covers it; `colorBufferWriteMask = []` -> depth-only (`blend_mul` with white); `isDoubleSided`/`cullMode`. `.lambert`/`.blinn`/`.phong` = Lambert diffuse, no specular unless `specular` is set (approximated with GGX).

`.geometry` modifiers live in `vertex()`, which Godot also runs for shadow maps and the depth prepass, so deformed casters (CitizenMotion, dune patches) cast deformed shadows as in SceneKit.

Scene state reaches every shader through global uniforms declared in `project.godot` (`scn_fog_color`, `scn_fog_range`, `scn_ambient`, `scn_ibl`, `scn_sh0..8`, `scn_deferred`), set by the runtime from the scene shown by the active view.

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

- `SCNLight`: directional/omni/spot -> `Light3D` child of the node (energy = intensity/1000, colour linearised then re-encoded, `temperature` tint). Ambient lights feed `scn_ambient`. Shadows: `ShadowOpacity` = shadow alpha (forward) or the deferred approximation below; `ShadowBias` = 0.1 x `shadowBias`; `ShadowBlur` = `shadowRadius`/3; single orthogonal split for one cascade; `DirectionalShadowMaxDistance` = `orthographicScale` when SceneKit does not adjust the projection, otherwise `min(maximumShadowDistance, max(4 x orthographicScale, 10))`. The directional shadow atlas is 8192 (two 4096-class suns).
- `categoryBitMask`: SceneKit bits are mapped to Godot's 20 render layers on first use. A node the camera mask excludes but a shadow-casting light includes casts `ShadowsOnly` (TownShadowBatch proxies).
- `SCNCamera`: copied each frame into the view's `Camera3D` (vertical FOV with `KeepAspect.Height`; `orthographicScale` is half the height). Linear tonemap, exposure 2^`exposureOffset`, glow for bloom, SSAO (`radius`, 2 x `intensity`, ambient only).
- `SCNScene`: a `World3D` and a hidden host `SubViewport` holding `rootNode`; `background.contents` colour or panorama; `lightingEnvironment` -> SH for the composer (Godot's ambient and reflections are disabled).
- `SCNView` (a `SubViewportContainer`) and `SCNRenderer` render the scene's world with their own camera and environment, so one scene can be shown by several views. `snapshot()` renders synchronously (`RenderingServer.ForceDraw` after forcing transform updates). `projectPoint`/`unprojectPoint` use AppKit coordinates (origin bottom-left, z 0..1). `isNode(_:insideFrustumOf:)` tests the node's bounding box against the view frustum.
- Per frame the runtime (`SceneKitRuntime`, last in `_Process`) flushes dirty materials, node transforms/meshes/lights, evaluates constraints (render transform only, like SceneKit's presentation tree), syncs view cameras and scene uniforms.

### Calibration

`tools/godot -- --facade-test DIR` renders the facade scenes (64 px calibration probes, primitives, sky, ground overlays and trails, PBR grid, sign texture) and writes PNGs and `measurements.json`. `tools/scenekit-reference/FacadeReference.swift` renders the same scenes with SceneKit (the shader modifiers there are the game's MSL) and compares:

```sh
apps/simulator-godot/tools/godot -- --facade-test /tmp/facade-godot
swiftc -O apps/simulator-godot/tools/scenekit-reference/FacadeReference.swift -o /tmp/facade-reference
/tmp/facade-reference /tmp/facade-scenekit /tmp/facade-godot   # deltas + compare-*.png (SceneKit | Godot | diff x4)
python3 apps/simulator-godot/tools/scenekit-reference/api_coverage.py
```

Results at this commit (linear pixel values; 8-bit quantisation is about 0.005): of 107 centre-pixel probes, 95 are identical and 6 more within one 8-bit step (constant colours, colour spaces, direct light for dielectrics and metals at every roughness and at grazing view angles, ambient, IBL at normal incidence, emission, multiply, vertex colours, fog, HDR clamp, blending, forward shadows, LOD switching, billboard, texture arguments, material cycling), the rest are 4 grazing-view IBL probes (within 0.017), the deferred-shadow approximation on a lit floor (0.012) and the orthographic constant-material deferred shadow, a SceneKit quirk (below). `SCNView` and `SCNRenderer` output is identical. Visual scenes, mean absolute sRGB difference: sky with bloom 0.2/255, PBR grid 0.4, ground overlays and trails 3.3 (SceneKit shadow acne at a grazing sun with default bias), primitives 5.6 (text glyphs, shadow softness and acne), sign texture 5.1 (glyph rasterisation). Calibrated constants: light energy = intensity/1000, ambient = intensity/1000, IBL diffuse response `1 - 0.1757r + 0.3455r² - 0.3623r³`, IBL specular response `1 + 0.1473r - 1.3669r² + 0.5496r³`, bloom = Glow additive, intensity 0.4 x `bloomIntensity`, HDR scale 1, levels up to round(log2(radius) - 1.6 - log2(height/400)).

### Known deviations

- Image-based specular at grazing views keeps Godot's split-sum terms: within 0.017 linear of SceneKit (up to 18% on fully rough dielectrics at 80°).
- SceneKit's fixed orthographic shadow box (`automaticallyAdjustsShadowProjection = false`) cannot be reproduced; Godot fits the shadow to the camera up to `DirectionalShadowMaxDistance`. Godot's normal-offset bias removes the shadow acne SceneKit shows at grazing sun angles with its default bias (the game's own captures show none, so the bias mapping was not tuned towards acne).
- Deferred shadows on lit materials are approximated by raising the light's shadow opacity by the ambient/direct ratio (2% off in the probe). SceneKit skips deferred shadows on constant materials under orthographic cameras; the facade does not.
- Reflections come from the order-2 SH of `lightingEnvironment` (exact for the game's procedural gradient probe; blurrier than SceneKit for detailed images).
- Godot samples mipmaps trilinearly and applies one global anisotropy level; SceneKit's default `mipFilter = .nearest` and per-property `maxAnisotropy` are not reproduced. `wrapS != wrapT` is not supported (the game never mixes them).
- `.fragment` modifiers cannot read the lit colour; use the idioms above. Exposure adaptation, vignetting, colour fringe, motion blur and depth of field are not emulated (the game leaves them off).
- Premultiplied `NSBitmapImageRep` texels with colour > alpha (WindblownDust's white-with-density texture) are clamped; SceneKit's result for such invalid data is not reproducible.
- `SCNFloor` reflections, `SCNRenderer.render(withViewport:commandBuffer:passDescriptor:)` and Metal capture APIs are not implemented; `snapshot(atTime:)` does not drive shader `TIME`.
- `presentation` returns the node itself; constraints only move the rendered transform. Nodes removed from a scene and dropped are not freed automatically.
- Global scene uniforms (fog, ambient, IBL) come from the last view rendered in a frame; two visible views showing different scenes at once would share them.
- Text uses the platform fonts Godot finds; glyph metrics differ slightly from CoreText.

## Validation

- **Logic:** `tools/checks` must print the same values as `reference/simulation-checks-swift.txt`, the macOS SimulationChecks output at the same commit. On macOS the numbers should match exactly.
- **Visuals:** compare captures of the same scene, camera and state against the macOS game's captures. Run the macOS game's own capture modes (`'apps/simulator-macos/.build/Marvin Simulator.app/Contents/MacOS/MarvinSimulator' --smoke-test DIR`, `--town-smoke-test DIR` and others; see its README) and add matching capture modes to the Godot game (`tools/godot -- --capture <name> DIR`). Inspect both images side by side. A passing number is never visual acceptance.
- **Behaviour:** port the macOS smoke checks that verify gameplay, and keep their JSON report keys.

## Agent rules

- Do not modify `apps/simulator-macos` (or anything outside `apps/simulator-godot`).
- Do not commit or push. The orchestrator commits.
- Stay inside your assigned files and folders. Report every deviation from the macOS behaviour or look, and anything you could not port.
