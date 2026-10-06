using System.Collections.Generic;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

/// Baked, soft-edged wear attached to actual entrances and walking routes.
/// This is a single static mesh; it adds no per-frame pathfinding or decals.
public static class TownGround
{
    // World-space pigment survives texture mip reduction in the aerial view.
    // The same field covers the central plane and outer terrain tiles.
    // PORT: MSL translated to Godot shading language (PORTING.md, "Shader modifier translation guide").
    // PORT (Godot-only, performance; docs/performance.md, "Ground and overlays"): townNoise reads the four lattice hashes of
    // its cell from noiseTable, which the GPU fills once with the same expression (fract(sin(dot(corner, (127.1, 311.7))) x
    // 43758.5453) for the corners of every cell), instead of evaluating four sines per call. The values are the same, so
    // the ground is too (captures of the town, entrance, navigation, track, dunes and skies unchanged). Without a
    // RenderingDevice (headless) the shaders keep the sines.
    public static string pigmentFunctions => (noiseTable.Value != null ? tableNoise : sineNoise) + pigment;
    private const string sineNoise = @"
float townNoise(vec2 p) {
    vec2 i = floor(p), f = fract(p); f = f * f * (3.0 - 2.0 * f);
    vec4 h = fract(sin(vec4(dot(i, vec2(127.1, 311.7)), dot(i + vec2(1, 0), vec2(127.1, 311.7)), dot(i + vec2(0, 1), vec2(127.1, 311.7)), dot(i + 1.0, vec2(127.1, 311.7)))) * 43758.5453);
    return mix(mix(h.x, h.y, f.x), mix(h.z, h.w, f.x), f.y);
}";
    // Cells -256..255 on both axes (texel = cell + 256). Every call site stays inside: the pigment within 184 m of the
    // centre (terrainSurface, trampledGround), the dune bands over the 4 km horizon plane at 1/43 scale, the streets' wind
    // tongues within 225 m (TownWorld.drawStreets).
    private const string tableNoise = @"
#pragma arguments
sampler2D townNoiseTable : filter_nearest;
#pragma declaration
float townNoise(vec2 p) {
    vec2 i = floor(p), f = fract(p); f = f * f * (3.0 - 2.0 * f);
    vec4 h = texelFetch(townNoiseTable, clamp(ivec2(i) + 256, ivec2(0), ivec2(511)), 0);
    return mix(mix(h.x, h.y, f.x), mix(h.z, h.w, f.x), f.y);
}";
    private const string pigment = @"
vec3 townPigment(vec2 p) {
    vec2 warp = vec2(townNoise(p / 31.0), townNoise(p / 37.0 + 19.0)) * 9.0;
    float broad = townNoise((p + warp) / 22.0), fine = townNoise((p + warp) / 5.5);
    float pale = smoothstep(0.28, 0.72, broad * 0.50 + fine * 0.50);
    vec3 soil = mix(vec3(0.255, 0.208, 0.145), vec3(0.46, 0.36, 0.235), pale);
    return soil * (0.89 + 0.22 * townNoise(p / 2.1 + 7.0));
}";
    // PORT (Godot-only, performance): the Swift modifier's values with less work. radius lies between 95 and 151 m, so
    // desert is exactly 0 within 82 m of the centre and exactly 1 beyond 184 m, where atan and the radius are not needed;
    // the pigment's weight is exactly 0 within 28 m, and it does not show where desert is 1; the dune colour and the ripple
    // only show where desert > 0 (mix(soil, dune, 0) is soil, a zero ripple leaves the normal as it was). Where desert is
    // 1 the colour is the dune colour itself rather than mix(soil, dune, 1), which can differ from it in the last bit.
    public static string terrainSurface => pigmentFunctions + "\n" + @"
#pragma body
vec2 p = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xz;
float radial = length(p);
float desert = radial > 184.0 ? 1.0 : 0.0;
if (radial > 82.0 && radial <= 184.0) {
    float angle = atan(p.y, p.x);
    float radius = 123.0 + 13.0 * sin(3.0 * angle + 0.4) + 9.0 * cos(5.0 * angle - 0.7) + 6.0 * sin(2.0 * angle);
    desert = smoothstep(radius - 12.0, radius + 32.0, length(p));
}
float grain = dot(ALBEDO, vec3(0.299, 0.587, 0.114));
vec3 dune = vec3(0.0);
if (desert > 0.0) {
    float bands = townNoise(p / 43.0);
    dune = vec3(0.64, 0.43, 0.23) * (0.83 + 0.30 * grain) + bands * vec3(0.075, 0.058, 0.029);
}
if (desert < 1.0) {
    vec3 soil = radial > 28.0 ? mix(ALBEDO, townPigment(p) * (1.30 + grain), smoothstep(28.0, 42.0, length(p))) : ALBEDO;
    ALBEDO = desert > 0.0 ? mix(soil, dune, desert) : soil;
} else {
    ALBEDO = dune;
}
float ripple = desert > 0.0 ? sin(p.x * 17.0 + p.y * 5.8 + 1.8 * sin(p.y * 0.41) + sin(p.x * 0.22)) : 0.0;
NORMAL = normalize(NORMAL + vec3(0.022 * ripple, 0.0, 0.008 * ripple) * desert);
";

    /// Binds noiseTable to a material whose shader modifiers use pigmentFunctions (Godot-only, see there).
    public static void useNoiseTable(SCNMaterial material)
    {
        if (noiseTable.Value is SCNMaterialProperty table) { material.setValue(table, "townNoiseTable"); }
    }
    /// townNoise's lattice hashes for cells -256..255, RGBA = the cell's corners (0,0), (1,0), (0,1), (1,1): computed once
    /// by a compute shader on a local RenderingDevice (usable from the world-build thread) with the fragment shaders'
    /// expression, read back and bound as a float texture. Measured on Metal (M2): the same values the fragment shaders
    /// computed (town, entrance, navigation, track, dune and sky captures identical).
    private static readonly System.Lazy<SCNMaterialProperty> noiseTable = new(buildNoiseTable);
    private static SCNMaterialProperty buildNoiseTable()
    {
        const int size = 512;
        var rd = Godot.RenderingServer.CreateLocalRenderingDevice();
        if (rd == null) { return null; }
        const string code = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(rgba32f, set = 0, binding = 0) uniform restrict writeonly image2D table;
void main() {
    ivec2 c = ivec2(gl_GlobalInvocationID.xy);
    vec2 i = vec2(c - ivec2(256));
    vec4 h = fract(sin(vec4(dot(i, vec2(127.1, 311.7)), dot(i + vec2(1, 0), vec2(127.1, 311.7)), dot(i + vec2(0, 1), vec2(127.1, 311.7)), dot(i + 1.0, vec2(127.1, 311.7)))) * 43758.5453);
    imageStore(table, c, h);
}
";
        byte[] bytes = null;
        var spirv = rd.ShaderCompileSpirVFromSource(new Godot.RDShaderSource { Language = Godot.RenderingDevice.ShaderLanguage.Glsl, SourceCompute = code });
        if (string.IsNullOrEmpty(spirv.CompileErrorCompute))
        {
            var shader = rd.ShaderCreateFromSpirV(spirv, "townNoiseTable");
            var pipeline = rd.ComputePipelineCreate(shader);
            var texture = rd.TextureCreate(new Godot.RDTextureFormat
            {
                Format = Godot.RenderingDevice.DataFormat.R32G32B32A32Sfloat, Width = size, Height = size,
                UsageBits = Godot.RenderingDevice.TextureUsageBits.StorageBit | Godot.RenderingDevice.TextureUsageBits.CanCopyFromBit,
            }, new Godot.RDTextureView());
            var uniform = new Godot.RDUniform { UniformType = Godot.RenderingDevice.UniformType.Image, Binding = 0 };
            uniform.AddId(texture);
            var set = rd.UniformSetCreate(new Godot.Collections.Array<Godot.RDUniform> { uniform }, shader, 0);
            long list = rd.ComputeListBegin();
            rd.ComputeListBindComputePipeline(list, pipeline);
            rd.ComputeListBindUniformSet(list, set, 0);
            rd.ComputeListDispatch(list, size / 8, size / 8, 1);
            rd.ComputeListEnd();
            rd.Submit(); rd.Sync();
            bytes = rd.TextureGetData(texture, 0);
            rd.FreeRid(set); rd.FreeRid(texture); rd.FreeRid(pipeline); rd.FreeRid(shader);
        }
        else { Godot.GD.PushError("townNoiseTable: " + spirv.CompileErrorCompute); }
        rd.Free();
        if (bytes == null || bytes.Length != size * size * 16) { return null; }
        var descriptor = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: MTLPixelFormat.rgba32Float, width: size, height: size, mipmapped: false);
        var table = MTLCreateSystemDefaultDevice().makeTexture(descriptor);
        table.replace(MTLRegionMake2D(0, 0, size, size), mipmapLevel: 0, withBytes: bytes, bytesPerRow: size * 16);
        var property = new SCNMaterialProperty(contents: table);
        property.minificationFilter = SCNFilterMode.nearest; property.magnificationFilter = SCNFilterMode.nearest; property.mipFilter = SCNFilterMode.none;
        return property;
    }

    public static SCNNode build(List<TownWorld.PedestrianAccess> access, List<Double2> yards)
    {
        var mesh = new TownMesh();
        void patch(Double2 center, Double2 axis, double width, double depth, int seed, uint ink, float opacity)
        {
            var across = new Double2(axis.y, -axis.x); var segments = 18;
            float height = -0.019f;
            Float3 vertex(double angle, double ring)
            {
                var uneven = 1 + 0.15 * sin(angle * 3 + (double)seed * 0.8) + 0.09 * cos(angle * 5 + (double)seed);
                var p = center + across * (cos(angle) * width * ring * uneven) + axis * (sin(angle) * depth * ring * uneven);
                return new Float3((float)p.x, height, (float)p.y);
            }
            void triangle(Float3 a, Float3 b, Float3 c, float[] alpha)
            {
                var start = mesh.colors.Count / 4; mesh.triangle(a, b, c, ink);
                for (var j = 0; j < 3; j++) { mesh.colors[(start + j) * 4 + 3] = alpha[j]; }
            }
            var middle = new Float3((float)center.x, height, (float)center.y);
            for (var k = 0; k < segments; k++)
            {
                double a = (double)k * 2 * System.Math.PI / (double)segments, b = (double)(k + 1) * 2 * System.Math.PI / (double)segments;
                Float3 ia = vertex(a, 0.38), ib = vertex(b, 0.38), oa = vertex(a, 1), ob = vertex(b, 1);
                triangle(middle, ib, ia, new[] { opacity, opacity * 0.72f, opacity * 0.72f });
                triangle(ia, ib, ob, new[] { opacity * 0.72f, opacity * 0.72f, 0 }); triangle(ia, ob, oa, new[] { opacity * 0.72f, 0, 0 });
            }
        }
        var index = 0;
        foreach (var entry in access)
        {
            // PORT: `for (index,entry) in access.enumerated() where ...`.
            var offset = index; index += 1;
            if (!(max(abs(entry.building.x), abs(entry.building.y)) > 30)) { continue; }
            if (!(entry.route.Count > 3)) { continue; }
            var direction = Simd.normalize(entry.route[min(4, entry.route.Count - 1)] - entry.door);
            // Foot traffic and wind leave broad, irregular tonal transitions,
            // not hard driveway strips or a identical pad around every house.
            if (offset % 3 != 0)
            {
                patch(entry.door + direction * 0.75, direction, 0.9 + (double)(offset % 4) * 0.16, 1.3 + (double)(offset % 5) * 0.22, offset, 0x8e806a, 0.22f);
            }
            if (offset % 4 == 0 && entry.route.Count > 18)
            {
                var p = entry.route[entry.route.Count / 2];
                patch(p, direction, 1.2, 2.1, offset + 73, 0xc2ac87, 0.17f);
            }
        }
        for (var i = 0; i < yards.Count; i++)
        {
            var p = yards[i];
            patch(p, new Double2(cos((double)i), sin((double)i)), 1.4, 1.0, i + 791, i % 3 == 0 ? 0x786753u : 0xbba07bu, i % 3 == 0 ? 0.38f : 0.25f);
        }
        var material = new SCNMaterial(); material.lightingModel = SCNMaterial.LightingModel.physicallyBased;
        material.roughness.contents = 1.0; material.diffuse.contents = NSColor.white;
        material.writesToDepthBuffer = false; material.transparencyMode = SCNTransparencyMode.aOne;
        material.shaderModifiers = new()
        {
            [SCNShaderModifierEntryPoint.geometry] = @"
#pragma varyings
vec4 groundTint;
#pragma body
groundTint = vec4(pow(max(COLOR.rgb, vec3(0.0)), vec3(2.2)), COLOR.a);
",
            [SCNShaderModifierEntryPoint.surface] = "ALBEDO = groundTint.rgb;",
            [SCNShaderModifierEntryPoint.fragment] = @"
#pragma transparent
#pragma body
ALPHA = groundTint.a;
",
        };
        var node = new SCNNode(mesh.geometry(material)); node.castsShadow = false; node.name = "Worn doorway approaches and windblown courtyard sand";
        var root = new SCNNode(); root.name = "Town ground surfaces";
        root.addChildNode(trampledGround()); root.addChildNode(node);
        return root;
    }
    /// Keep the original receiver depth and the transparent town surface. The
    /// fully covered base needs depth, but its expensive pigment/PBR color will
    /// be overwritten. Leave a generous inset from every analytic blend edge.
    public static SCNGeometry coveredTerrain(SCNMaterial material)
    {
        List<SCNVector3> vertices = new(); List<CGPoint> uv = new(); List<SCNVector3> normals = new();
        List<int> shaded = new(), covered = new();
        foreach (var y in strideTo(-128, 128, 4)) { foreach (var x in strideTo(-128, 128, 4)) {
            var nearest = hypot((double)max(0, max(x, -(x + 4))), (double)max(0, max(y, -(y + 4))));
            var farthest = hypot((double)max(abs(x), abs(x + 4)), (double)max(abs(y), abs(y + 4)));
            var hidden = nearest >= 38 && farthest <= 93;
            var @base = vertices.Count;
            foreach (var (dx, dy) in new[] { (0, 0), (4, 0), (4, 4), (0, 4) })
            {
                vertices.Add(new SCNVector3(x + dx, y + dy, 0)); normals.Add(new SCNVector3(0, 0, 1));
                uv.Add(new CGPoint((double)(x + dx + 128) / 256, (double)(128 - y - dy) / 256));
            }
            var indices = new[] { @base, @base + 1, @base + 2, @base, @base + 2, @base + 3 };
            if (hidden) { covered.AddRange(indices); } else { shaded.AddRange(indices); }
        }}
        var depth = new SCNMaterial(); depth.lightingModel = SCNMaterial.LightingModel.constant;
        depth.colorBufferWriteMask = SCNColorMask.none; depth.writesToDepthBuffer = material.writesToDepthBuffer;
        depth.isDoubleSided = material.isDoubleSided; depth.cullMode = material.cullMode;
        depth.readsFromDepthBuffer = material.readsFromDepthBuffer;
        var geometry = new SCNGeometry(new[] { SCNGeometrySource.vertices(vertices), SCNGeometrySource.normals(normals), SCNGeometrySource.textureCoordinates(uv) },
            new[] { new SCNGeometryElement(shaded, SCNGeometryPrimitiveType.triangles), new SCNGeometryElement(covered, SCNGeometryPrimitiveType.triangles) });
        geometry.materials = new() { material, depth }; return geometry;
    }

    private static SCNNode trampledGround()
    {
        var mesh = new TownMesh(); var step = 4;
        foreach (var z in strideTo(-176, 176, step)) { foreach (var x in strideTo(-176, 176, step)) {
            var points = new[] { new Float3((float)x, -0.023f, (float)z), new Float3((float)x, -0.023f, (float)(z + step)), new Float3((float)(x + step), -0.023f, (float)(z + step)), new Float3((float)(x + step), -0.023f, (float)z) };
            var alpha = points.Select(p =>
            {
                var inner = hypot(p.x, p.z);
                var outer = (float)TownFootprint.edgeDistance(new Double2((double)p.x, (double)p.z));
                return min(1f, max(0f, (inner - 28) / 8)) * min(1f, max(0f, (-outer + 5) / 12));
            }).ToArray();
            if (alpha.All(value => value == 0)) { continue; }
            foreach (var ids in new[] { new[] { 0, 1, 2 }, new[] { 0, 2, 3 } })
            {
                var start = mesh.colors.Count / 4;
                mesh.triangle(points[ids[0]], points[ids[1]], points[ids[2]], 0xffffff);
                for (var j = 0; j < 3; j++)
                {
                    var p = points[ids[j]];
                    mesh.colors[(start + j) * 4 + 3] = alpha[ids[j]];
                    mesh.uv[start + j] = new CGPoint((double)p.x / 4, (double)p.z / 4);
                }
            }
        }}
        var m = new SCNMaterial(); m.lightingModel = SCNMaterial.LightingModel.physicallyBased;
        m.diffuse.contents = CityMaterials.asset("ground-base.jpg");
        m.normal.contents = CityMaterials.asset("ground-normal.jpg"); m.normal.intensity = 0.65;
        m.roughness.contents = CityMaterials.asset("ground-rough.jpg");
        foreach (var map in new[] { m.diffuse, m.normal, m.roughness }) { map.wrapS = SCNWrapMode.repeat; map.wrapT = SCNWrapMode.repeat; map.mipFilter = SCNFilterMode.linear; map.maxAnisotropy = 8; }
        m.writesToDepthBuffer = false; m.transparencyMode = SCNTransparencyMode.aOne;
        m.shaderModifiers = new()
        {
            [SCNShaderModifierEntryPoint.geometry] = @"
#pragma varyings
float groundBlend;
#pragma body
groundBlend = COLOR.a;
",
            [SCNShaderModifierEntryPoint.surface] = pigmentFunctions + "\n" + @"
#pragma body
float grain = dot(ALBEDO, vec3(0.2126, 0.7152, 0.0722));
ALBEDO = townPigment((INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xz) * (0.52 + 2.5 * grain);
",
            // `_output.color.rgb *= blend; _output.color.a = blend;` -> `ALPHA = blend;` (PORTING.md).
            // PORT (Godot-only, performance): radius lies between 102 and 158 m, so within 94 m of the centre the outer
            // fade is exactly 1 and atan and the radius are not needed (the same blend).
            [SCNShaderModifierEntryPoint.fragment] = @"
#pragma transparent
#pragma body
vec2 p = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xz;
float radial = length(p);
float blend = smoothstep(28.0, 36.0, radial);
if (radial > 94.0) {
    float angle = atan(p.y, p.x);
    float radius = 130.0 + 13.0 * sin(3.0 * angle + 0.4) + 9.0 * cos(5.0 * angle - 0.7) + 6.0 * sin(2.0 * angle);
    blend = blend * (1.0 - smoothstep(-7.0, 5.0, length(p) - radius));
}
ALPHA = blend;
",
        };
        useNoiseTable(m);
        var node = new SCNNode(mesh.geometry(m)); node.castsShadow = false;
        node.name = "Trampled sand in town, blended out before the circuit and dunes";
        return node;
    }
}
