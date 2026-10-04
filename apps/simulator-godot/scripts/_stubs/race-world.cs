// STUBS for the race-world-and-effects stream. Owned by other streams; deleted by the
// orchestrator when merging. Only the members DirtWorld and its smoke modes call.
//   TownWorld, TownMesh, TownGround, CityMaterials  -> town stream (TownWorld.swift, TownGround.swift, TownCrowd.swift)
//   R2D2.groundContacts, material(), color()        -> robots stream (R2D2.swift, Robot.swift)
global using static Marvin.RobotGlobals;

using System;
using System.Collections.Generic;
using System.Linq;
using Marvin.Core;

namespace Marvin;

/// <summary>STUB (robots stream, Robot.swift): the free functions `color(_:alpha:)` and `material(_:metal:roughness:)`.</summary>
public static class RobotGlobals
{
    public static NSColor color(uint hex, double alpha = 1) =>
        NSColor.srgbRed(((hex >> 16) & 255) / 255.0, ((hex >> 8) & 255) / 255.0, (hex & 255) / 255.0, alpha);
    public static SCNMaterial material(uint hex, double metal = 0, double roughness = 0.6)
    {
        var m = new SCNMaterial();
        m.lightingModel = SCNMaterial.LightingModel.physicallyBased;
        m.diffuse.contents = color(hex); m.metalness.contents = metal;
        m.roughness.contents = roughness; m.isDoubleSided = true;
        return m;
    }
}

/// <summary>STUB (robots stream, R2D2.swift): wheel contacts only.</summary>
public sealed class R2D2
{
    public static readonly (double x, double z, double width)[] groundContacts =
    {
        (x: 0.213, z: -0.1415, width: 0.120),
        (x: -0.213, z: -0.1415, width: 0.120),
        (x: 0.0, z: 0.1445, width: 0.090),
    };
}

/// <summary>STUB (town stream, TownWorld.swift): an empty town with no collision bodies or people.</summary>
public sealed class TownWorld
{
    public readonly SCNNode root = new SCNNode();
    public Double3[] shadowDirections = Array.Empty<Double3>();
    private bool stormActive;
    public TownWorld(Action<double, string> progress = null)
    {
        root.name = "Town (stub)";
        progress?.Invoke(1, "Town stub");
    }
    public CityCollisionWorld collisionWorld => new CityCollisionWorld(Array.Empty<RobotCollisions.Body>());
    public int population => 0;
    public int visiblePopulation => 0;
    public void setStorm(bool active) { stormActive = active; }
    public void reset() { }
}

/// <summary>STUB (town stream, TownWorld.swift): TownMesh without vertex reuse (same triangles, colours and UVs).</summary>
public sealed class TownMesh
{
    public int materialSlot = 0;
    private readonly List<int>[] groups = { new(), new(), new(), new() };
    public List<CGPoint> wearUV = new();
    public Func<Float3, CGPoint> wearProjector;
    public List<SCNVector3> positions = new(), normals = new();
    public List<CGPoint> uv = new();
    public List<float> colors = new();
    public List<int> indices = new();
    public void triangle(Float3 a, Float3 b, Float3 c, uint color, Float3[] smooth = null)
    {
        var cross = Simd.cross(b - a, c - a);
        if (!(Simd.length_squared(cross) > 1e-12f)) return;
        var n = Simd.normalize(cross);
        int @base = positions.Count;
        var vs = new[] { a, b, c };
        for (int i = 0; i < 3; i++)
        {
            var v = vs[i];
            var normal = smooth?[i] ?? n;
            float contact = 0.83f + 0.17f * Math.Min(1, Math.Max(0, v.y) / 1.2f);
            float shade = (0.94f + 0.06f * Math.Max(0, normal.y)) * contact;
            positions.Add(new SCNVector3(v.x, v.y, v.z)); normals.Add(new SCNVector3(normal.x, normal.y, normal.z));
            var axis = new Float3(Math.Abs(n.x), Math.Abs(n.y), Math.Abs(n.z));
            var tex = axis.y > Math.Max(axis.x, axis.z) ? new Float2(v.x, v.z) : (axis.x > axis.z ? new Float2(v.z, v.y) : new Float2(v.x, v.y));
            uv.Add(new CGPoint(tex.x * 0.48, tex.y * 0.48));
            wearUV.Add(wearProjector?.Invoke(v) ?? new CGPoint(0.0625, 0.0625));
            colors.AddRange(new[] { ((color >> 16) & 255) / 255f * shade, ((color >> 8) & 255) / 255f * shade, (color & 255) / 255f * shade, 1f });
        }
        indices.AddRange(new[] { @base, @base + 1, @base + 2 });
        groups[materialSlot].AddRange(new[] { @base, @base + 1, @base + 2 });
    }
    public SCNGeometry geometry(SCNMaterial material)
    {
        var colorSource = new SCNGeometrySource(SCNGeometrySource.Bytes(colors), SCNGeometrySourceSemantic.color, positions.Count, true, 4, 4, 0, 16);
        var g = new SCNGeometry(new[] { SCNGeometrySource.vertices(positions), SCNGeometrySource.normals(normals), SCNGeometrySource.textureCoordinates(uv), SCNGeometrySource.textureCoordinates(wearUV), colorSource },
            groups.Where(x => x.Count > 0).Select(x => new SCNGeometryElement(x, SCNGeometryPrimitiveType.triangles)));
        var materials = new[] { material, CityMaterials.plaster, CityMaterials.plaster, CityMaterials.plaster };
        g.materials = Enumerable.Range(0, 4).Where(i => groups[i].Count > 0).Select(i => materials[i]).ToList();
        return g;
    }
}

/// <summary>STUB (town stream, TownGround.swift): the terrain pigment modifier and the covered base terrain.</summary>
public static class TownGround
{
    /// The facade's verified translation of TownGround.terrainSurface (PORTING.md, worked example 2).
    public static readonly string terrainSurface = Marvin.SceneKit.FacadeTest.TerrainSurface;
    public static SCNGeometry coveredTerrain(SCNMaterial material)
    {
        var vertices = new List<SCNVector3>(); var uv = new List<CGPoint>(); var normals = new List<SCNVector3>();
        var shaded = new List<int>(); var covered = new List<int>();
        for (int y = -128; y < 128; y += 4)
            for (int x = -128; x < 128; x += 4)
            {
                double nearest = Swift.hypot((double)Math.Max(0, Math.Max(x, -(x + 4))), (double)Math.Max(0, Math.Max(y, -(y + 4))));
                double farthest = Swift.hypot((double)Math.Max(Math.Abs(x), Math.Abs(x + 4)), (double)Math.Max(Math.Abs(y), Math.Abs(y + 4)));
                bool hidden = nearest >= 38 && farthest <= 93;
                int @base = vertices.Count;
                foreach (var (dx, dy) in new[] { (0, 0), (4, 0), (4, 4), (0, 4) })
                {
                    vertices.Add(new SCNVector3(x + dx, y + dy, 0)); normals.Add(new SCNVector3(0, 0, 1));
                    uv.Add(new CGPoint((double)(x + dx + 128) / 256, (double)(128 - y - dy) / 256));
                }
                var indices = new[] { @base, @base + 1, @base + 2, @base, @base + 2, @base + 3 };
                if (hidden) covered.AddRange(indices); else shaded.AddRange(indices);
            }
        var depth = new SCNMaterial(); depth.lightingModel = SCNMaterial.LightingModel.constant;
        depth.colorBufferWriteMask = SCNColorMask.none; depth.writesToDepthBuffer = material.writesToDepthBuffer;
        depth.isDoubleSided = material.isDoubleSided; depth.cullMode = material.cullMode;
        depth.readsFromDepthBuffer = material.readsFromDepthBuffer;
        var geometry = new SCNGeometry(new[] { SCNGeometrySource.vertices(vertices), SCNGeometrySource.normals(normals), SCNGeometrySource.textureCoordinates(uv) },
            new[] { new SCNGeometryElement(shaded, SCNGeometryPrimitiveType.triangles), new SCNGeometryElement(covered, SCNGeometryPrimitiveType.triangles) });
        geometry.materials = new() { material, depth }; return geometry;
    }
}

/// <summary>STUB (town stream, TownCrowd.swift): surfaceNoise (exact) and a plaster material without the wear atlas.</summary>
public static class CityMaterials
{
    public static string asset(string name) => "res://assets/City/" + name;
    public static SCNMaterial scanned(string name, double normal, double metal = 0)
    {
        var m = new SCNMaterial(); m.lightingModel = SCNMaterial.LightingModel.physicallyBased;
        m.diffuse.contents = asset(name + "-base.jpg");
        m.normal.contents = asset(name + "-normal.jpg"); m.normal.intensity = normal;
        m.roughness.contents = asset(name + "-rough.jpg"); m.metalness.contents = metal;
        foreach (var p in new[] { m.diffuse, m.normal, m.roughness })
        {
            p.wrapS = SCNWrapMode.repeat; p.wrapT = SCNWrapMode.repeat; p.mipFilter = SCNFilterMode.linear; p.maxAnisotropy = 4;
        }
        tint(m, "float grain = dot(ALBEDO, vec3(0.2126, 0.7152, 0.0722));\nALBEDO = cityTint * (0.62 + grain * 0.65);");
        return m;
    }
    public static void tint(SCNMaterial m, string surface = "ALBEDO = cityTint;")
    {
        m.shaderModifiers = new()
        {
            [SCNShaderModifierEntryPoint.geometry] = "#pragma varyings\nvec3 cityTint;\n#pragma body\n// Palette bytes are sRGB; PBR surface inputs are linear.\ncityTint = pow(max(COLOR.rgb, vec3(0.0)), vec3(2.2));",
            [SCNShaderModifierEntryPoint.surface] = "#pragma body\n" + surface,
        };
    }
    public static double surfaceNoise(double u, double v, int cells, int seed)
    {
        double x = u * cells, y = v * cells; long ix = (long)Math.Floor(x), iy = (long)Math.Floor(y);
        double fx = x - Math.Floor(x), fy = y - Math.Floor(y), tx = fx * fx * (3 - 2 * fx), ty = fy * fy * (3 - 2 * fy);
        double hash(long a, long b)
        {
            uint n = unchecked((uint)(((a % cells + cells) % cells) * 374761393L + ((b % cells + cells) % cells) * 668265263L + seed * 1274126177L));
            n = unchecked((n ^ (n >> 13)) * 1274126177);
            return ((n ^ (n >> 16)) & 65535) / 65535.0;
        }
        double lo = hash(ix, iy) * (1 - tx) + hash(ix + 1, iy) * tx, hi = hash(ix, iy + 1) * (1 - tx) + hash(ix + 1, iy + 1) * tx;
        return lo * (1 - ty) + hi * ty;
    }
    private static SCNMaterial _plaster;
    public static SCNMaterial plaster
    {
        get
        {
            if (_plaster != null) return _plaster;
            var m = scanned("plaster", 1.0);
            tint(m, "float grain = dot(ALBEDO, vec3(0.2126, 0.7152, 0.0722));\nALBEDO = cityTint * (0.50 + grain * 1.1);");
            return _plaster = m;
        }
    }
}
