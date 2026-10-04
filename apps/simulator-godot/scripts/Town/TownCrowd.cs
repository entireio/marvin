using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

// PORT: TownCrowd.swift: CityMaterials, TownCrowd and its inspection extension. Shader modifiers are
// translated to Godot shading language (PORTING.md, "Shader modifier translation guide").

/// Shared scanned surfaces. All maps are bundled; there is no network work at runtime.
public static class CityMaterials
{
    // PORT: Swift returns a bundle URL; the facade loads res:// paths.
    public static string asset(string name) => "res://assets/City/" + name;
    public static SCNMaterial scanned(string name, CGFloat normal, CGFloat metal = 0)
    {
        var m = new SCNMaterial(); m.lightingModel = SCNMaterial.LightingModel.physicallyBased;
        m.diffuse.contents = asset(name + "-base.jpg");
        m.normal.contents = asset(name + "-normal.jpg"); m.normal.intensity = normal;
        m.roughness.contents = asset(name + "-rough.jpg"); m.metalness.contents = metal;
        foreach (var p in new[] { m.diffuse, m.normal, m.roughness })
        {
            p.wrapS = SCNWrapMode.repeat; p.wrapT = SCNWrapMode.repeat; p.mipFilter = SCNFilterMode.linear; p.maxAnisotropy = 4;
        }
        // Scans supply spatial variation; vertex colors supply district/outfit palettes.
        tint(m, surface: @"
float grain = dot(ALBEDO, vec3(0.2126, 0.7152, 0.0722));
ALBEDO = cityTint * (0.62 + grain * 0.65);
");
        return m;
    }
    // PORT: MSL `_surface.diffuse.rgb=float3(in.cityTint);` -> `ALBEDO = cityTint;`. SceneKit declares the varying
    // as half3; Godot's varyings are 32-bit floats (precision only).
    public static void tint(SCNMaterial m, string surface = "ALBEDO = cityTint;")
    {
        m.shaderModifiers = new()
        {
            [SCNShaderModifierEntryPoint.geometry] = @"#pragma varyings
vec3 cityTint;
#pragma body
// Palette bytes are sRGB; PBR surface inputs are linear.
cityTint = pow(max(COLOR.rgb, vec3(0.0)), vec3(2.2));",
            [SCNShaderModifierEntryPoint.surface] = "#pragma body\n" + surface,
        };
    }
    /// Periodic value noise baked only during asset preparation, never in a fragment shader.
    public static double surfaceNoise(double u, double v, long cells, long seed)
    {
        double x = u * (double)cells, y = v * (double)cells; long ix = (long)floor(x), iy = (long)floor(y);
        double fx = x - floor(x), fy = y - floor(y), tx = fx * fx * (3 - 2 * fx), ty = fy * fy * (3 - 2 * fy);
        double hash(long a, long b)
        {
            var n = unchecked((uint)(((a % cells + cells) % cells) * 374761393L + ((b % cells + cells) % cells) * 668265263L + seed * 1274126177L));
            n = unchecked((n ^ (n >> 13)) * 1274126177u);
            return (double)((n ^ (n >> 16)) & 65535) / 65535;
        }
        double lo = hash(ix, iy) * (1 - tx) + hash(ix + 1, iy) * tx, hi = hash(ix, iy + 1) * (1 - tx) + hash(ix + 1, iy + 1) * tx;
        return lo * (1 - ty) + hi * ty;
    }
    /// An atlas of maintenance histories, with gravity-aligned and localized masks.
    /// Cell zero is neutral for non-building geometry. Each other cell has its own seed.
    private static NSImage plasterWear()
    {
        int tileSize = 128, size = tileSize * 8;
        var bitmap = new NSBitmapImageRep(bitmapDataPlanes: null, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: NSColorSpaceName.deviceRGB, bytesPerRow: size * 4, bitsPerPixel: 32);
        var pixels = bitmap.bitmapData;
        for (var tile = 0; tile < 64; tile++)
        {
            int history = tile / 16, seed = tile * 197 + 31;
            var centerX = 0.20 + (double)((tile * 17) % 61) / 100;
            var baseHeight = 0.12 + (double)((tile * 13) % 24) / 100;
            for (var y = 0; y < tileSize; y++) { for (var x = 0; x < tileSize; x++) {
                double u = (double)x / (double)(tileSize - 1), v = 1 - (double)y / (double)(tileSize - 1);
                var macro = surfaceNoise(u, v, cells: 5, seed: seed);
                var fine = surfaceNoise(u, v, cells: 27, seed: seed + 17);
                var warp = (macro - 0.5) * 0.14 + (fine - 0.5) * 0.045;
                var @base = max(0, 1 - v / (baseHeight + warp));
                var level = 1.0;
                if (tile > 0) { level = 0.955 + (macro - 0.5) * 0.12 + (fine - 0.5) * 0.035; }
                if (history > 0)
                {
                    var exposure = history == 1 ? 0.19 : (history == 2 ? 0.10 : 0.30);
                    level -= @base * exposure * (0.65 + macro * 0.55);
                }
                if (history == 2)
                {
                    // Uneven resurfacing: one repaired patch, not a scatter of identical stains.
                    double radiusX = 0.13 + (double)(tile % 5) * 0.022, radiusY = 0.15 + (double)(tile % 3) * 0.04;
                    var cy = 0.25 + (double)(tile % 4) * 0.12;
                    var boundary = pow(abs((u - centerX) / radiusX), 4) + pow(abs((v - cy) / radiusY), 4) + warp * 12;
                    var patch = max(0, min(1, (1.1 - boundary) * 5));
                    level = level * (1 - patch) + patch * (tile % 2 == 0 ? 0.995 : 0.85);
                }
                if (history == 3)
                {
                    // A narrow runoff/soot mark tied to the roof edge, fading before ground.
                    var drift = centerX + (macro - 0.5) * 0.065;
                    var stain = exp(-pow((u - drift) / (0.025 + (1 - v) * 0.035), 2))
                        * max(0, min(1, (v - 0.24) * 1.6)) * (0.65 + fine * 0.35);
                    level -= stain * 0.21;
                }
                var i = ((tile / 8 * tileSize + y) * size + tile % 8 * tileSize + x) * 4;
                pixels[i] = (byte)(255 * max(0.60, min(1, level)));
                pixels[i + 1] = (byte)(255 * max(0.60, min(1, level - (1 - level) * 0.09)));
                pixels[i + 2] = (byte)(255 * max(0.60, min(1, level - (1 - level) * 0.19)));
                pixels[i + 3] = 255;
            }}
        }
        var image = new NSImage(new NSSize(size, size)); image.addRepresentation(bitmap); return image;
    }
    // PORT: Swift `static let`s are lazy and thread-safe; Lazy<T> keeps that (surfaceNoise or asset must not
    // build every material).
    private static readonly Lazy<SCNMaterial> _plaster = new(() =>
    {
        var m = scanned("plaster", normal: 1.0);
        tint(m, surface: @"
float grain = dot(ALBEDO, vec3(0.2126, 0.7152, 0.0722));
ALBEDO = cityTint * (0.50 + grain * 1.1);
");
        m.multiply.contents = plasterWear(); m.multiply.mappingChannel = 1;
        m.multiply.wrapS = SCNWrapMode.clamp; m.multiply.wrapT = SCNWrapMode.clamp;
        m.multiply.mipFilter = SCNFilterMode.linear; m.multiply.maxAnisotropy = 4;
        return m;
    });
    public static SCNMaterial plaster => _plaster.Value;
    private static readonly Lazy<SCNMaterial> _adobe = new(() =>
    {
        var m = scanned("adobe", normal: 0.85);
        tint(m, surface: @"
float grain = dot(ALBEDO, vec3(0.2126, 0.7152, 0.0722));
ALBEDO = cityTint * (0.60 + grain * 1.25);
");
        m.multiply.contents = plasterWear(); m.multiply.mappingChannel = 1;
        m.multiply.wrapS = SCNWrapMode.clamp; m.multiply.wrapT = SCNWrapMode.clamp; m.multiply.mipFilter = SCNFilterMode.linear;
        return m;
    });
    public static SCNMaterial adobe => _adobe.Value;
    private static readonly Lazy<SCNMaterial> _cloth = new(() =>
    {
        var m = scanned("cloth", normal: 0.28); m.isDoubleSided = true;
        tint(m, surface: "float grain = dot(ALBEDO, vec3(0.2126, 0.7152, 0.0722)); ALBEDO = cityTint * (0.82 + grain * 0.22);");
        return m;
    });
    public static SCNMaterial cloth => _cloth.Value;
    private static readonly Lazy<SCNMaterial> _crowdCloth = new(() =>
    {
        var m = cloth.copy(); m.isDoubleSided = true; return m;
    });
    public static SCNMaterial crowdCloth => _crowdCloth.Value;
    private static readonly Lazy<SCNMaterial> _metal = new(() => scanned("metal", normal: 0.55, metal: 0.55));
    public static SCNMaterial metal => _metal.Value;
    private static readonly Lazy<SCNMaterial> _skin = new(() =>
    {
        var m = new SCNMaterial(); m.lightingModel = SCNMaterial.LightingModel.physicallyBased;
        m.diffuse.contents = NSColor.white; m.roughness.contents = 0.64; tint(m);
        return m;
    });
    public static SCNMaterial skin => _skin.Value;
    private static readonly Lazy<SCNMaterial> _leather = new(() =>
    {
        var m = new SCNMaterial(); m.lightingModel = SCNMaterial.LightingModel.physicallyBased;
        m.diffuse.contents = NSColor.white; m.roughness.contents = 0.78; tint(m);
        return m;
    });
    public static SCNMaterial leather => _leather.Value;
}

/// Indexed authored humans, batched by eight-meter cells, with independent crowd LOD.
/// Spectator reactions stay batched; only walking residents use individual nodes.
public sealed class TownCrowd
{
    private sealed class Model
    {
        public readonly List<float[]> vertices; public readonly int[] indices; public readonly bool[] arms; public readonly float[] armTops;
        // PORT: Swift's Decodable init(from:); the JSON holds `vertices` ([[Float]]) and `indices` ([Int32]).
        public Model(List<float[]> vertices, int[] indices)
        {
            this.vertices = vertices; this.indices = indices;
            // Clothing islands identify sleeves and hands at every authored LOD.
            // Position-only masks accidentally include a seated person's skirt.
            var parent = Enumerable.Range(0, vertices.Count).ToArray();
            int root(int input) { var i = input; while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
            for (var i = 0; i < indices.Length; i += 3)
            {
                int a = indices[i], b = indices[i + 1], d = indices[i + 2];
                if (vertices[a][8] == 0 || vertices[a][8] == 1) { parent[root(b)] = root(a); parent[root(d)] = root(a); }
            }
            var bounds = new Dictionary<int, Float2>();
            for (var i = 0; i < vertices.Count; i++)
            {
                if (!(vertices[i][8] == 0 || vertices[i][8] == 1)) { continue; }
                var r = root(i); var x = vertices[i][0]; var old = bounds.TryGetValue(r, out var found) ? found : new Float2(x, x);
                bounds[r] = new Float2(min(old.x, x), max(old.y, x));
            }
            var armFlags = Enumerable.Range(0, vertices.Count).Select(i =>
            {
                if (!bounds.TryGetValue(root(i), out var b)) { return false; } return b.x > 0.07f || b.y < -0.07f;
            }).ToArray();
            arms = armFlags;
            var tops = new float[] { 0, 0 };
            for (var i = 0; i < vertices.Count; i++)
            {
                if (!(armFlags[i] && vertices[i][8] == 1)) { continue; }
                var side = vertices[i][0] < 0 ? 0 : 1; tops[side] = max(tops[side], vertices[i][1]);
            }
            armTops = vertices.Select(v => tops[v[0] < 0 ? 0 : 1]).ToArray();
        }
    }
    private sealed class Batch
    {
        public List<SCNVector3> vertices = new(), normals = new(); public List<CGPoint> uv = new(); public List<float> colors = new();
        public List<int>[] groups = { new(), new(), new() };
        public List<CGPoint>[] motion = Enumerable.Range(0, 7).Select(_ => new List<CGPoint>()).ToArray();
        public void add(Model model, Float3 at, float yaw, int index, bool seated, Activity activity = Activity.ordinary)
        {
            var position = at;
            float c = cos(yaw), s = sin(yaw); var @base = vertices.Count;
            uint[] outfits = { 0x746354, 0x566866, 0xa99b81, 0x6d6a53, 0x5b6266, 0x907451, 0x82756b, 0xb8ab91 };
            uint[] skins = { 0xb68b70, 0x835c48, 0xc2a084, 0x9c755c, 0xa37c61, 0xc4a591 };
            // Independent deterministic hashes prevent matching outfit/skin/pose stripes.
            var seed = unchecked((uint)((long)index * 747796405L + 2891336453L));
            var mix = (int)((seed ^ (seed >> 16)) & 0x7fffffff);
            uint[] palette = { skins[(mix / 7) % 6], outfits[mix % 8], outfits[(mix / 13) % 8], 0x433c34, outfits[(mix / 23) % 8], 0xc1b8a7, 0x342b25, 0x8a8170 };
            var scale = new Float3(0.90f + (float)(mix % 19) * 0.012f, 0.95f + (float)((mix / 19) % 11) * 0.01f, 0.94f + (float)((mix / 209) % 13) * 0.012f);
            var turn = (float)((mix / 2717) % 17 - 8) * (activity == Activity.ordinary ? 0.055f : 0.012f);
            var neck = seated ? 0.51f : 0.875f;
            var lean = (float)((mix / 31) % 9 - 4) * 0.013f;
            (Float3, Float3) posed(Float3 p, Float3 normal)
            {
                Float3 v = p, n = normal;
                float weight = min(1, max(0, (p.y - neck) / 0.045f)), a = turn * weight;
                float cc = cos(a), ss = sin(a);
                v.x = p.x * cc + p.z * ss; v.z = -p.x * ss + p.z * cc;
                n.x = normal.x * cc + normal.z * ss; n.z = -normal.x * ss + normal.z * cc;
                v.z += max(0, p.y - (seated ? 0.10f : 0.46f)) * lean; n.y -= lean * n.z;
                v *= scale; n = Simd.normalize(n / scale);
                return (position + new Float3(v.x * c + v.z * s, v.y, -v.x * s + v.z * c), new Float3(n.x * c + n.z * s, n.y, -n.x * s + n.z * c));
            }
            void motionData(Float3 p, int semantic, bool armIsland = false, float armTop = 0)
            {
                var arm = armIsland ? min(1, max(0, (armTop - p.y) / 0.075f)) : 0;
                motion[0].Add(new CGPoint((double)position.x, (double)position.z));
                motion[1].Add(new CGPoint((double)yaw, (double)position.y));
                motion[2].Add(new CGPoint((double)(mix % 1000) * 0.071, (double)(neck * scale.y)));
                motion[3].Add(new CGPoint((double)arm, p.x < 0 ? -1 : 1));
                motion[4].Add(new CGPoint((double)(0.53f * scale.y), (double)(0.28f * scale.y)));
                motion[5].Add(new CGPoint(seated ? 1 : (double)(int)activity, (double)semantic));
                motion[6].Add(new CGPoint((double)(0.12f * scale.x), (double)(armTop * scale.y)));
            }
            for (var vertexIndex = 0; vertexIndex < model.vertices.Count; vertexIndex++)
            {
                var v = model.vertices[vertexIndex];
                motionData(new Float3(v[0], v[1], v[2]), semantic: (int)v[8], armIsland: model.arms[vertexIndex], armTop: model.armTops[vertexIndex]);
                var (p, n) = posed(new Float3(v[0], v[1], v[2]), new Float3(v[3], v[4], v[5]));
                vertices.Add(new SCNVector3(p.x, p.y, p.z)); normals.Add(new SCNVector3(n.x, n.y, n.z));
                uv.Add(new CGPoint((double)v[6] * 12, (double)v[7] * 12));
                var color = palette[(int)v[8]];
                colors.Add((float)((color >> 16) & 255) / 255); colors.Add((float)((color >> 8) & 255) / 255); colors.Add((float)(color & 255) / 255); colors.Add(1);
            }
            for (var i = 0; i < model.indices.Length; i += 3)
            {
                var semantic = (int)model.vertices[model.indices[i]][8];
                var slot = (semantic == 1 || semantic == 2 || semantic == 4) ? 1 : (semantic == 0 || semantic == 5 ? 0 : 2);
                for (var k = i; k < i + 3; k++) { groups[slot].Add(model.indices[k] + @base); }
            }
            // A light shoulder mantle changes selected silhouettes. It is batched
            // with the existing cloth material and costs only 32 triangles/person.
            if (mix % 5 == 0)
            {
                var ink = outfits[(mix / 29) % 8]; var top = seated ? 0.49f : 0.86f;
                Float3 point(int ring, int k)
                {
                    // PORT: Float.pi is Swift.floatPi (rounded toward zero; MathF.PI is one ulp larger).
                    var a = floatPi + (float)k * floatPi / 8;
                    var radius = ring == 0 ? 0.155f : (ring == 1 ? 0.213f : 0.226f);
                    return new Float3(cos(a) * radius, top - (float)ring * 0.135f, -0.005f + sin(a) * radius * 0.73f);
                }
                for (var ring = 0; ring < 2; ring++) { for (var k = 0; k < 8; k++) {
                    foreach (var points in new[] { new[] { point(ring, k), point(ring + 1, k), point(ring + 1, k + 1) }, new[] { point(ring, k), point(ring + 1, k + 1), point(ring, k + 1) } })
                    {
                        var normal = Simd.normalize(Simd.cross(points[1] - points[0], points[2] - points[0]));
                        var start = vertices.Count;
                        foreach (var q in points)
                        {
                            var (v, n) = posed(q, normal);
                            motionData(q, semantic: 1);
                            vertices.Add(new SCNVector3(v.x, v.y, v.z)); normals.Add(new SCNVector3(n.x, n.y, n.z)); uv.Add(new CGPoint((double)q.x * 18, (double)q.y * 18));
                            colors.Add((float)((ink >> 16) & 255) / 255); colors.Add((float)((ink >> 8) & 255) / 255); colors.Add((float)(ink & 255) / 255); colors.Add(1);
                        }
                        groups[1].Add(start); groups[1].Add(start + 1); groups[1].Add(start + 2);
                    }
                }}
            }
        }
        public int triangles => groups.Aggregate(0, (sum, group) => sum + group.Count / 3);
        public SCNGeometry geometry(List<SCNMaterial> materials = null)
        {
            materials ??= CityMaterialsArray.shared;
            var color = new SCNGeometrySource(SCNGeometrySource.Bytes(colors), SCNGeometrySourceSemantic.color, vertices.Count, true, 4, 4, 0, 16);
            var g = new SCNGeometry(new[] { SCNGeometrySource.vertices(vertices), SCNGeometrySource.normals(normals), SCNGeometrySource.textureCoordinates(uv), color }.Concat(motion.Select(m => SCNGeometrySource.textureCoordinates(m))),
                groups.Select(group => new SCNGeometryElement(group, SCNGeometryPrimitiveType.triangles)));
            g.materials = materials;
            // Shader-deformed hands and feet may extend beyond the bind pose.
            var bounds = g.boundingBox;
            g.boundingBox = (new SCNVector3(bounds.min.x - 0.35, bounds.min.y - 0.12, bounds.min.z - 0.35),
                             new SCNVector3(bounds.max.x + 0.35, bounds.max.y + 0.2, bounds.max.z + 0.35));
            return g;
        }
    }
    public enum Activity { ordinary = 0, conversation = -1, waiting = -2, trading = -3 }
    private static class CityMaterialsArray
    {
        private static readonly Lazy<List<SCNMaterial>> _shared = new(() => CitizenMotion.materials());
        public static List<SCNMaterial> shared => _shared.Value;
    }
    private sealed class Cell
    {
        public readonly Float3 origin; public readonly Batch[] lod; public readonly bool stays;
        public Cell(Float3 origin, Batch[] lod, bool stays) { this.origin = origin; this.lod = lod; this.stays = stays; }
    }
    private List<(SCNNode, bool)> weatherNodes = new();
    public int stormPopulation { get; private set; } = 0;
    public static bool staysOutside(double x, double z, int index) => abs((long)index * 17 + (long)(x * 13) + (long)(z * 7)) % 31 == 0;
    public void update(double time) { foreach (var material in CityMaterialsArray.shared) { material.setValue((float)time, "crowdTime"); } }
    public void setStorm(bool active) { foreach (var (node, stays) in weatherNodes) { node.isHidden = active && !stays; } }
    private Dictionary<string, List<Model>> models = new();
    private Dictionary<string, Cell> cells = new();
    public int triangles { get; private set; } = 0;
    public int farTriangles { get; private set; } = 0;
    public int cellCount { get; private set; } = 0;
    private int modelCount = 0;
    public bool valid => modelCount == 12 && cellCount > 0 && triangles < 2_100_000 && farTriangles < 115_000;
    private sealed class ModelJson { public List<float[]> vertices { get; set; } public int[] indices { get; set; } }
    public TownCrowd()
    {
        try
        {
            var bytes = Godot.FileAccess.GetFileAsBytes(CityMaterials.asset("crowd.json"));
            if (bytes == null || bytes.Length == 0) { throw new System.IO.FileNotFoundException(CityMaterials.asset("crowd.json")); }
            var decoded = JsonSerializer.Deserialize<Dictionary<string, List<ModelJson>>>(bytes);
            models = decoded.ToDictionary(pair => pair.Key, pair => pair.Value.Select(m => new Model(m.vertices, m.indices)).ToList());
        }
        catch (Exception error) { System.Diagnostics.Debug.Fail($"Missing or invalid bundled crowd: {error}"); Godot.GD.PrintErr($"Missing or invalid bundled crowd: {error}"); }
        modelCount = models.Count;
    }
    public SCNNode add(double x, double y, double z, double yaw, int index, bool seated, bool animated, bool shelter = false, Activity activity = Activity.ordinary)
    {
        var key = $"{(index % 2 == 0 ? "male" : "female")}-{(seated ? "sit" : "stand")}-{(index / 2) % 3}";
        if (!models.TryGetValue(key, out var model)) { return null; }
        var stays = !shelter && TownCrowd.staysOutside(x: x, z: z, index: index);
        if (stays && !animated) { stormPopulation += 1; }
        var position = new Float3((float)x, (float)(!seated && y < 0.1 ? -0.03 : y), (float)z);
        if (animated)
        {
            var lod = Enumerable.Range(0, 3).Select(_ => new Batch()).ToArray();
            for (var i = 0; i < 3; i++) { lod[i].add(model[i], at: Float3.zero, yaw: 0, index: index, seated: seated, activity: activity); }
            var materials = CitizenMotion.materials();
            var near = lod[0].geometry(materials: materials);
            near.levelsOfDetail = new[] { new SCNLevelOfDetail(geometry: lod[1].geometry(materials: materials), worldSpaceDistance: 8), new SCNLevelOfDetail(geometry: lod[2].geometry(materials: materials), worldSpaceDistance: 24) };
            var node = new SCNNode(near); node.position = new SCNVector3(position.x, position.y, position.z); node.eulerAngles.y = (CGFloat)yaw;
            node.castsShadow = false;
            triangles += lod[0].triangles; farTriangles += lod[2].triangles;
            return node;
        }
        long ix = (long)floor(x / 8), iz = (long)floor(z / 8); var cellKey = $"{ix},{iz},{(stays ? "true" : "false")}";
        if (!cells.ContainsKey(cellKey)) { cells[cellKey] = new Cell(origin: new Float3((float)(ix * 8 + 4), 0, (float)(iz * 8 + 4)), lod: Enumerable.Range(0, 3).Select(_ => new Batch()).ToArray(), stays: stays); }
        var cell = cells[cellKey];
        for (var i = 0; i < 3; i++) { cell.lod[i].add(model[i], at: position - cell.origin, yaw: (float)yaw, index: index, seated: seated, activity: activity); }
        return null;
    }
    public void finish(SCNNode into)
    {
        var root = into;
        foreach (var key in cells.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList())
        {
            var cell = cells[key]; var near = cell.lod[0].geometry();
            near.levelsOfDetail = new[] { new SCNLevelOfDetail(geometry: cell.lod[1].geometry(), worldSpaceDistance: 10), new SCNLevelOfDetail(geometry: cell.lod[2].geometry(), worldSpaceDistance: 26) };
            var node = new SCNNode(near); node.position = new SCNVector3(cell.origin.x, cell.origin.y, cell.origin.z);
            node.name = $"Crowd cell {key}"; node.castsShadow = false; root.addChildNode(node); weatherNodes.Add((node, cell.stays));
            triangles += cell.lod[0].triangles; farTriangles += cell.lod[2].triangles;
        }
        cellCount = cells.Count;
        cells.Clear(); models.Clear();
    }

    // Render every authored variant and LOD in the native regression capture.
    public List<(string, SCNNode)> inspectionFigures() =>
        models.Keys.OrderBy(k => k, StringComparer.Ordinal).SelectMany(key =>
            Enumerable.Range(0, 3).Select(lod =>
            {
                var batch = new Batch(); batch.add(models[key][lod], at: Float3.zero, yaw: 0, index: 1, seated: key.Contains("sit"));
                return ($"{key}-lod{lod}", new SCNNode(batch.geometry()));
            })).ToList();
}
