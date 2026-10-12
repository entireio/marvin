using System;
using System.Collections.Generic;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

public sealed partial class DirtWorld
{
    public void updateGate(CityGate gate)
    {
        var b = gate.body();
        cityGateNode.position = new SCNVector3(b.position.x, b.position.y, b.position.z);
        cityGateNode.eulerAngles.y = (CGFloat)b.heading;
    }
    public void addCityExit(SCNMaterial clay, SCNMaterial earth)
    {
        var metal = material(0x60615a, roughness: 0.79);
        metal.metalness.contents = 0.72;
        var rusty = material(0x805443, roughness: 0.94);
        SCNNode piece(double w, double h, double d, double x, double y, double z, SCNMaterial m)
        {
            var g = new SCNBox(width: w, height: h, length: d, chamferRadius: 0.012);
            g.materials = new() { m }; var node = new SCNNode(g);
            node.position = new SCNVector3(x, y, z); cityGateNode.addChildNode(node); return node;
        }
        _ = piece(CityExit.width, CityExit.gateHeight, 0.085, 0, CityExit.gateHeight / 2, 0, metal);
        foreach (var x in new[] { -1.48, -0.5, 0.5, 1.48 }) _ = piece(0.065, CityExit.gateHeight, 0.11, x, CityExit.gateHeight / 2, 0, rusty);
        foreach (var y in new[] { 0.045, CityExit.gateHeight - 0.045 }) _ = piece(CityExit.width, 0.09, 0.12, 0, y, 0, rusty);
        var brace = piece(3.14, 0.065, 0.12, 0, 0.475, 0, rusty); brace.eulerAngles.z = 0.24;
        foreach (var x in new[] { -1.47, 1.47 })
            foreach (var y in new[] { 0.14, 0.81 })
            {
                _ = piece(0.065, 0.065, 0.135, x, y, 0, metal);
            }
        foreach (var x in stride(from: -0.7, through: 0.7, by: 0.2))
        {
            _ = piece(0.14, 0.11, 0.09, x, 0.67, 0.012, material((long)(x * 10) % 4 == 0 ? 0xb6a064u : 0x454541u, roughness: 0.9));
        }
        cityGateNode.name = "Motor-driven metal city gate"; scene.rootNode.addChildNode(cityGateNode);
        foreach (var side in new[] { -1.0, 1.0 })
        {
            var p = CityExit.point(side * (CityExit.width / 2 + 0.09), 0);
            var post = new SCNCylinder(radius: 0.085, height: CityExit.floor + CityExit.gateHeight + 0.16);
            post.radialSegmentCount = 12; post.materials = new() { rusty };
            var node = new SCNNode(post); node.position = new SCNVector3(p.x, (CityExit.floor + CityExit.gateHeight + 0.11) / 2, p.y);
            scene.rootNode.addChildNode(node);
        }
        updateGate(new CityGate());
        // Match the track's actual offset-curve vertices at the inner edge.
        // A rectangular overlap grid cut across the raised lane and exposed
        // sliver triangles. This apron begins inside the retaining masonry.
        int nx = 96, nz = 100, start = (int)((double)DirtCourse.sampleCount * 0.125) - 48;
        var outlines = Enumerable.Range(0, nz + 1).Select(j => DirtCourse.surfacePoints(offset: DirtCourse.fenceOffset + (double)j * 0.08)).ToArray();
        var v = new List<SCNVector3>(); var n = new List<SCNVector3>(); var uv = new List<CGPoint>(); var clayUV = new List<CGPoint>(); var colors = new List<float>(); var indices = new List<int>();
        double heightAt(Double2 q)
        {
            double h = DirtCourse.height(q.x, q.y), t = max(0, min(1, (h + 0.025) / 0.035));
            return h - 0.008 * (1 - t * t * (3 - 2 * t));
        }
        for (int j = 0; j <= nz; j++)
            for (int i = 0; i <= nx; i++)
            {
                var point = outlines[j][(start + i + DirtCourse.sampleCount) % DirtCourse.sampleCount];
                var p = new Double2(point.x, point.y); var local = CityExit.local(p); double along = local.x, @out = local.y;
                double height = heightAt(p);
                v.Add(new SCNVector3(p.x, height, p.y)); uv.Add(new CGPoint(p.x / 4, -p.y / 4));
                clayUV.Add(new CGPoint((double)(start + i) / DirtCourse.sampleCount, (DirtCourse.fenceOffset + (double)j * 0.08 + DirtCourse.width) / (2 * DirtCourse.width)));
                double epsilon = 0.025;
                var normal = Simd.normalize(new Double3(heightAt(p - new Double2(epsilon, 0)) - heightAt(p + new Double2(epsilon, 0)), epsilon * 2, heightAt(p - new Double2(0, epsilon)) - heightAt(p + new Double2(0, epsilon))));
                n.Add(new SCNVector3(normal.x, normal.y, normal.z));
                double fade = max(0, min(1, (height + 0.025) / 0.06)), run = max(0, min(1, @out / CityExit.run));
                double noise = CityMaterials.surfaceNoise(p.x / 12, p.y / 12, cells: 9, seed: 327);
                double distance = (double)j * 0.08, shoulder = min(1, distance / 0.6), blend = shoulder * shoulder * (3 - 2 * shoulder);
                double red = max(0, min(1, (1 - run) * (1 - min(1, abs(along) / 4) * blend) + run * (1 - run) * (noise - 0.5) * 0.7));
                colors.AddRange(new[] { (float)(red * fade * fade * (3 - 2 * fade)), 0, 0, 1 });
            }
        for (int j = 0; j < nz; j++)
            for (int i = 0; i < nx; i++)
            {
                int a = j * (nx + 1) + i, b = a + 1, c = a + (nx + 1), d = c + 1;
                indices.AddRange(new[] { a, b, c, b, d, c });
            }
        var border = Enumerable.Range(0, nx + 1).Concat(Enumerable.Range(1, nz).Select(k => k * (nx + 1) + nx)).Concat(Enumerable.Range(0, nx).Reverse().Select(k => nz * (nx + 1) + k)).Concat(Enumerable.Range(1, nz - 1).Reverse().Select(k => k * (nx + 1))).ToArray();
        int bottom = v.Count;
        foreach (var i in border) { var p = v[i]; v.Add(new SCNVector3(p.x, -0.1, p.z)); n.Add(new SCNVector3(0, -1, 0)); uv.Add(uv[i]); clayUV.Add(clayUV[i]); colors.AddRange(new[] { 0f, 0, 0, 1 }); }
        for (int i = 0; i < border.Length; i++) { int j = (i + 1) % border.Length; indices.AddRange(new[] { border[i], bottom + i, border[j], border[j], bottom + i, bottom + j }); }
        for (int i = 1; i < border.Length - 1; i++) indices.AddRange(new[] { bottom, bottom + i, bottom + i + 1 });
        var sources = new[] { SCNGeometrySource.vertices(v), SCNGeometrySource.normals(n), SCNGeometrySource.textureCoordinates(uv) };
        var elements = new[] { new SCNGeometryElement(indices, SCNGeometryPrimitiveType.triangles) };
        var @base = new SCNGeometry(sources, elements); var sand = earth.copy();
        sand.diffuse.contentsTransform = SCNMatrix4Identity; @base.materials = new() { sand };
        var baseNode = new SCNNode(@base); baseNode.name = "Filled outside turn one city ramp"; baseNode.castsShadow = false; scene.rootNode.addChildNode(baseNode);
        var tint = new SCNGeometrySource(SCNGeometrySource.Bytes(colors), SCNGeometrySourceSemantic.color, v.Count, true, 4, 4, 0, 16);
        var overlay = new SCNGeometry(new[] { sources[0], sources[1], SCNGeometrySource.textureCoordinates(clayUV), tint }, new[] { new SCNGeometryElement(indices.Take(nx * nz * 6).ToList(), SCNGeometryPrimitiveType.triangles) }); var pigment = clay.copy();
        var modifiers = pigment.shaderModifiers ?? new();
        modifiers[SCNShaderModifierEntryPoint.geometry] = "#pragma varyings\nfloat redSoil;\n#pragma body\nredSoil = COLOR.r;";
        // `_output.color.rgb *= a; _output.color.a = a;` (premultiplied, #pragma transparent) -> `ALPHA = a;` (PORTING.md).
        modifiers[SCNShaderModifierEntryPoint.fragment] = "#pragma transparent\n#pragma body\nALPHA = redSoil;";
        pigment.shaderModifiers = modifiers; pigment.transparencyMode = SCNTransparencyMode.aOne; pigment.writesToDepthBuffer = false; overlay.materials = new() { pigment };
        var top = new SCNNode(overlay); top.position.y = 0.0002; top.castsShadow = false; scene.rootNode.addChildNode(top);
    }
}
