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
    // The camera-boom queries AppController.updateCamera calls, ported verbatim from TownWorld.swift. With the stub's
    // empty collision world and no roof bounds only InfieldLayout.obstacles and the terrain constrain the boom.
    private readonly List<(Double3, Double3)> cameraBounds = new();
    public SCNVector3 cameraPivot(Double3 position, double chassisHeight)
    {
        var head = new SCNVector3(position.x, position.y + chassisHeight + 0.18, position.z);
        double rise = Math.Min(0.72, cameraRoom(head, range: 0.9));
        var raised = new SCNVector3(position.x, head.y + rise, position.z);
        return clearCamera(head, raised);
    }
    public double cameraRoom(SCNVector3 point, double range = 9)
    {
        var a = new Double3(point.x, point.y, point.z);
        double radius = Math.Max(0.1, (range + 0.2) / Math.Sqrt(2));
        var query = new RobotCollisions.Body(position: a, profile: new RobotCollisions.Profile(mass: 1, halfWidth: radius, halfDepth: radius, height: 1));
        double room = double.PositiveInfinity;
        foreach (var body in collisionWorld.nearby(query).Concat(InfieldLayout.obstacles))
        {
            if (body.profile.mass == 70) continue;
            var d = a - body.position; double c = Math.Cos(body.heading), s = Math.Sin(body.heading); var p = body.profile;
            var q = new Double3(Math.Abs(c * d.x - s * d.z) - p.halfWidth, Math.Max(-d.y, d.y - p.height), Math.Abs(s * d.x + c * d.z) - p.halfDepth);
            room = Math.Min(room, Simd.length(Simd.max(q, Double3.zero)));
        }
        foreach (var (low, high) in cameraBounds) room = Math.Min(room, Simd.length(Simd.max(Simd.max(low - a, a - high), Double3.zero)));
        return Math.Max(0, room - 0.16);
    }
    public SCNVector3 terrainCamera(SCNVector3 from, SCNVector3 to)
    {
        var pivot = from; var desired = to;
        if (!(Math.Max(Math.Abs(pivot.x), Math.Abs(pivot.z)) > DesertTerrain.townEdge)) return desired;
        var result = desired;
        for (int i = 1; i <= 64; i++)
        {
            double t = (double)i / 64, x = pivot.x + (desired.x - pivot.x) * t, z = pivot.z + (desired.z - pivot.z) * t;
            double needed = (DirtCourse.height(x, z) + 0.25 - pivot.y * (1 - t)) / t;
            result.y = Math.Max(result.y, needed);
        }
        return result;
    }
    public SCNVector3 clearCamera(SCNVector3 from, SCNVector3 to)
    {
        var a = new Double3(from.x, from.y, from.z);
        Double3 b = new Double3(to.x, to.y, to.z), delta = b - a;
        double limit = 1.0;
        void clip(Double3 origin, Double3 direction, Double3 low, Double3 high)
        {
            Double3 lo = low - new Double3(0.14, 0.14, 0.14), hi = high + new Double3(0.14, 0.14, 0.14);
            double enter = 0.0, leave = 1.0;
            for (int axis = 0; axis < 3; axis++)
            {
                if (Math.Abs(direction[axis]) < 1e-8)
                {
                    if (origin[axis] < lo[axis] || origin[axis] > hi[axis]) return;
                }
                else
                {
                    double t0 = (lo[axis] - origin[axis]) / direction[axis], t1 = (hi[axis] - origin[axis]) / direction[axis];
                    enter = Math.Max(enter, Math.Min(t0, t1)); leave = Math.Min(leave, Math.Max(t0, t1));
                    if (enter > leave) return;
                }
            }
            limit = Math.Min(limit, Math.Max(0, enter - 0.01 / Math.Max(0.01, Simd.length(delta))));
        }
        var middle = (a + b) / 2; double radius = Math.Max(0.1, Simd.length(delta) / 2 + 0.2);
        var query = new RobotCollisions.Body(position: middle, profile: new RobotCollisions.Profile(mass: 1, halfWidth: radius, halfDepth: radius, height: 1));
        foreach (var body in collisionWorld.nearby(query).Concat(InfieldLayout.obstacles))
        {
            if (body.profile.mass == 70) continue;
            double c = Math.Cos(body.heading), s = Math.Sin(body.heading);
            Double3 local(Double3 p) => new Double3(c * p.x - s * p.z, p.y, s * p.x + c * p.z);
            var p = body.profile;
            clip(local(a - body.position), local(delta), new Double3(-p.halfWidth, 0, -p.halfDepth), new Double3(p.halfWidth, p.height, p.halfDepth));
        }
        foreach (var (low, high) in cameraBounds) clip(a, delta, low, high);
        int steps = Math.Max(1, (int)Math.Ceiling(Simd.length(delta) * limit / 0.20));
        for (int i = 1; i <= steps; i++)
        {
            double t = limit * (double)i / (double)steps; var p = a + delta * t;
            if (p.y < DirtCourse.height(p.x, p.z) + 0.18)
            {
                limit = limit * (double)(i - 1) / (double)steps; break;
            }
        }
        var result = a + delta * limit;
        return new SCNVector3(result.x, result.y, result.z);
    }
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
