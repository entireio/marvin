// Port of Sources/MarvinSimulator/TrackBelt.swift.
using System.Collections.Generic;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

/// Moving shoes over a continuous rubber carcass, sized to the widened CAD belt.
public sealed class TrackBelt
{
    public readonly SCNNode node = new SCNNode();
    public readonly bool left;
    public readonly SCNNode[] shoes;
    public TrackBelt(double x, SCNMaterial rubber)
    {
        left = x > 0; // Marvin faces +Z; anatomical left is +X.
        node.position.x = (CGFloat)x;
        var width = 0.165324;
        var count = 56;
        var pitch = TrackLoop.circumference / (double)count;
        var shoe = new SCNBox(width, 0.008, pitch * 0.80, 0.001);
        shoe.materials = new() { rubber };
        shoes = Enumerable.Range(0, count).Select(_ => new SCNNode(shoe)).ToArray();
        for (int i = 0; i < shoes.Length; i++)
        {
            var shoeNode = shoes[i];
            // Offset center ribs break up the broad tread, making its motion legible.
            var rib = new SCNBox(width * 0.38, 0.003, pitch * 0.42, 0.0007);
            rib.materials = new() { material(0x2b3432, roughness: 0.95) };
            var ribNode = new SCNNode(rib);
            ribNode.position = new SCNVector3(i % 2 == 0 ? -width * 0.23 : width * 0.23, 0.004, 0);
            shoeNode.addChildNode(ribNode);
            node.addChildNode(shoeNode);
        }
        // A watertight ring beneath the shoes; its outer surface is inward of
        // the tread so the belt is solid without a stationary tread overlay.
        List<SCNVector3> vertices = new(), normals = new(); List<int> indices = new();
        Float3[] ring(int i)
        {
            var p = TrackLoop.sample((double)i / 160 * TrackLoop.circumference);
            var outward = new Float3(0, (float)cos(p.angle), (float)sin(p.angle));
            var center = new Float3(0, (float)p.y, (float)p.z);
            return new[] { center + new Float3(-(float)(width / 2), 0, 0) - outward * 0.004f,
                           center + new Float3((float)(width / 2), 0, 0) - outward * 0.004f,
                           center + new Float3((float)(width / 2), 0, 0) - outward * 0.013f,
                           center + new Float3(-(float)(width / 2), 0, 0) - outward * 0.013f };
        }
        for (int i = 0; i < 160; i++)
        {
            Float3[] a = ring(i), b = ring(i + 1);
            for (int j = 0; j < 4; j++)
            {
                var k = (j + 1) % 4;
                var points = new[] { a[j], b[j], b[k], a[k] };
                var normal = Simd.normalize(Simd.cross(points[1] - points[0], points[2] - points[0]));
                var start = vertices.Count;
                foreach (var point in points) { vertices.Add(new SCNVector3(point.x, point.y, point.z)); normals.Add(new SCNVector3(normal.x, normal.y, normal.z)); }
                indices.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
            }
        }
        var mesh = new SCNGeometry(new[] { SCNGeometrySource.vertices(vertices), SCNGeometrySource.normals(normals) },
                                   new[] { new SCNGeometryElement(indices, SCNGeometryPrimitiveType.triangles) });
        mesh.materials = new() { rubber };
        node.addChildNode(new SCNNode(mesh));
        update(travel: 0);
    }
    public void update(double travel)
    {
        for (int i = 0; i < shoes.Length; i++)
        {
            var shoe = shoes[i];
            var p = TrackLoop.sample(travel + (double)i / (double)shoes.Length * TrackLoop.circumference);
            shoe.position = new SCNVector3(0, p.y, p.z);
            shoe.eulerAngles.x = (CGFloat)p.angle;
        }
    }
}
