using System;
using System.Collections.Generic;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

/// Bounded windblown sand: two batches and one shared-height drift mesh.
public sealed class SandstormWorld
{
    public readonly SCNNode root = new SCNNode();
    private readonly SCNNode drifts = new SCNNode();
    private readonly SCNMaterial driftMaterial = new SCNMaterial();
    private readonly SCNNode grains = new SCNNode(), haze = new SCNNode();
    private Float3[] particles = Array.Empty<Float3>();
    private readonly int count = 960;
    public SandstormWorld(NSImage texture)
    {
        root.name = "Sandstorm"; root.isHidden = true;
        driftMaterial.lightingModel = SCNMaterial.LightingModel.physicallyBased; driftMaterial.diffuse.contents = texture;
        driftMaterial.diffuse.wrapS = SCNWrapMode.repeat; driftMaterial.diffuse.wrapT = SCNWrapMode.repeat;
        driftMaterial.roughness.contents = 1;
        driftMaterial.shaderModifiers = new()
        {
            [SCNShaderModifierEntryPoint.geometry] = @"
#pragma arguments
float deposition;
#pragma varyings
float driftDepth;
#pragma body
driftDepth = COLOR.r * deposition;
VERTEX.y += driftDepth;
NORMAL = normalize(NORMAL + vec3(-COLOR.g, 0.0, -COLOR.b) * deposition);
",
            [SCNShaderModifierEntryPoint.surface] = @"
#pragma transparent
#pragma body
float grain = dot(ALBEDO, vec3(0.333));
ALBEDO = vec3(0.57, 0.38, 0.23) * (0.82 + grain * 0.5); ALPHA = 1.0;
",
            [SCNShaderModifierEntryPoint.fragment] = @"
#pragma transparent
#pragma body
if (driftDepth < 0.0004) discard;
float coverage = smoothstep(0.0004, 0.025, driftDepth);
ALPHA = coverage;
",
        };
        driftMaterial.setValue(0.18f, "deposition");
        var vertices = new List<SCNVector3>(); var normals = new List<SCNVector3>(); var uv = new List<CGPoint>(); var colors = new List<float>(); var indices = new List<int>();
        double step = 0.10;
        for (int iz = -232; iz < 232; iz++)
            for (int ix = -232; ix < 232; ix++)
            {
                double x = (double)ix * step, z = (double)iz * step;
                var points = new[] { (x, z), (x + step, z), (x + step, z + step), (x, z + step) };
                var depths = points.Select(p => Sandstorm.deposit(p.Item1, p.Item2)).ToArray();
                if (!(depths.Max() > 0.00001)) continue;
                int @base = vertices.Count;
                for (int i = 0; i < points.Length; i++)
                {
                    var p = points[i];
                    double e = 0.03;
                    vertices.Add(new SCNVector3(p.Item1, DirtCourse.height(p.Item1, p.Item2) + 0.002, p.Item2));
                    double nx = (DirtCourse.height(p.Item1 - e, p.Item2) - DirtCourse.height(p.Item1 + e, p.Item2)) / (2 * e);
                    double nz = (DirtCourse.height(p.Item1, p.Item2 - e) - DirtCourse.height(p.Item1, p.Item2 + e)) / (2 * e);
                    var n = Simd.normalize(new Double3(nx, 1, nz));
                    normals.Add(new SCNVector3(n.x, n.y, n.z)); uv.Add(new CGPoint(p.Item1 / 2, p.Item2 / 2));
                    colors.AddRange(new[] { (float)depths[i], (float)((Sandstorm.deposit(p.Item1 + e, p.Item2) - Sandstorm.deposit(p.Item1 - e, p.Item2)) / (2 * e)), (float)((Sandstorm.deposit(p.Item1, p.Item2 + e) - Sandstorm.deposit(p.Item1, p.Item2 - e)) / (2 * e)), 1 });
                }
                indices.AddRange(new[] { @base, @base + 2, @base + 1, @base, @base + 3, @base + 2 });
            }
        var data = SCNGeometrySource.Bytes(colors);
        var colorSource = new SCNGeometrySource(data, SCNGeometrySourceSemantic.color, vertices.Count, true, 4, 4, 0, 16);
        var mesh = new SCNGeometry(new[] { SCNGeometrySource.vertices(vertices), SCNGeometrySource.normals(normals), SCNGeometrySource.textureCoordinates(uv), colorSource }, new[] { new SCNGeometryElement(indices, SCNGeometryPrimitiveType.triangles) });
        mesh.materials = new() { driftMaterial }; drifts.geometry = mesh; drifts.castsShadow = false; root.addChildNode(drifts);
        foreach (var (node, isHaze) in new[] { (grains, false), (haze, true) })
        {
            var mat = new SCNMaterial(); mat.lightingModel = SCNMaterial.LightingModel.constant; mat.diffuse.contents = color(0xd5b17c);
            mat.isDoubleSided = true; mat.writesToDepthBuffer = false;
            if (isHaze)
            {
                mat.diffuse.contents = WindblownDust.texture();
            }
            WindblownDust.configure(mat);
            var placeholder = new SCNPlane(width: 0, height: 0); placeholder.materials = new() { mat }; node.geometry = placeholder; node.castsShadow = false; root.addChildNode(node);
        }
        reset();
    }
    public void reset()
    {
        particles = Enumerable.Range(0, count).Select(i => new Float3((float)((i * 37) % 101) / 101 * 64 - 32, (float)((i * 31) % 97) / 97 * 15 - 5, (float)((i * 47) % 103) / 103 * 64 - 32)).ToArray();
    }
    public void update(Sandstorm storm, SCNNode camera, double dt)
    {
        root.isHidden = !storm.enabled;
        if (!(storm.enabled && dt > 0)) return;
        driftMaterial.setValue((float)storm.accumulation, "deposition");
        var e = camera.simdWorldPosition; var eye = new Float3(e.X, e.Y, e.Z); var transform = camera.simdWorldTransform;
        var right = new Float3(transform.X.X, transform.X.Y, transform.X.Z);
        var up = new Float3(transform.Y.X, transform.Y.Y, transform.Y.Z);
        var w = storm.wind(eye.x, eye.z); var wind = new Float3((float)w.x, 0, (float)w.z);
        for (int i = 0; i < particles.Length; i++)
        {
            particles[i] += wind * (float)dt;
            // Positions are camera-relative only when recycled; all visible
            // motion between recycling events is world-space wind advection.
            foreach (var axis in new[] { 0, 2 }) { float d = particles[i][axis] - eye[axis]; if (abs(d) > 32) { particles[i][axis] = eye[axis] + (d > 0 ? -31.9f : 31.9f); } }
            if (i % 12 == 0)
            {
                particles[i].y = (float)DirtCourse.height(particles[i].x, particles[i].z) + 0.3f + (float)(i % 7) * 0.12f;
            }
            else if (abs(particles[i].y - eye.y) > 10) { particles[i].y = eye.y + (float)(i % 17) - 6; }
        }
        foreach (var isHaze in new[] { false, true })
        {
            var vertices = new List<SCNVector3>(); var uv = new List<CGPoint>(); var colors = new List<float>(); var indices = new List<int>();
            for (int i = 0; i < particles.Length; i++)
            {
                if ((i % 12 == 0) != isHaze) continue;
                var p = particles[i]; int @base = vertices.Count;
                var across = isHaze ? right * (float)(1.8 + (double)(i % 7) * 0.2) : right * 0.012f;
                var along = isHaze ? up * 0.32f : Simd.normalize(wind) * 0.13f;
                float distance = Simd.length(p - eye);
                float alpha = (isHaze ? 0.28f : 0.34f) * min(1, max(0, (32 - distance) / 8));
                foreach (var (x, y) in new[] { (-1.0, -1.0), (1.0, -1.0), (1.0, 1.0), (-1.0, 1.0) })
                {
                    var v = p + across * (float)x + along * (float)y;
                    vertices.Add(new SCNVector3(v.x, v.y, v.z)); uv.Add(new CGPoint((x + 1) / 2, (y + 1) / 2));
                    colors.AddRange(isHaze ? new[] { 0.68f, 0.49f, 0.29f, alpha } : new[] { 1f, 1f, 1f, alpha });
                }
                indices.AddRange(new[] { @base, @base + 1, @base + 2, @base, @base + 2, @base + 3 });
            }
            var source = new SCNGeometrySource(SCNGeometrySource.Bytes(colors), SCNGeometrySourceSemantic.color, vertices.Count, true, 4, 4, 0, 16);
            var mesh = new SCNGeometry(new[] { SCNGeometrySource.vertices(vertices), SCNGeometrySource.textureCoordinates(uv), source }, new[] { new SCNGeometryElement(indices, SCNGeometryPrimitiveType.triangles) });
            var node = isHaze ? haze : grains; mesh.materials = node.geometry.materials; node.geometry = mesh;
        }
    }
}
