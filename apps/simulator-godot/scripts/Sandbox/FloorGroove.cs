// Port of Sources/MarvinSimulator/FloorGroove.swift.
using System.Collections.Generic;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

/// V-shaped channel: both lips meet y=0 and the center lies below the floor.
public static class FloorGroove
{
    public const double depth = 0.012;
    public static double innerRadius => CourseLayout.ringRadius - CourseLayout.pipeRadius;
    public static double outerRadius => CourseLayout.ringRadius + CourseLayout.pipeRadius;

    public static SCNGeometry geometry()
    {
        var profile = new[] { (innerRadius, 0.0), (CourseLayout.ringRadius, -depth), (outerRadius, 0.0) };
        List<SCNVector3> vertices = new(), normals = new(); List<int> indices = new();
        for (int band = 0; band < 2; band++)
        {
            var (r0, y0) = profile[band]; var (r1, y1) = profile[band + 1];
            for (int i = 0; i <= 128; i++)
            {
                var angle = (double)i * 2 * System.Math.PI / 128;
                var normal = Simd.normalize(new Double3(-(y1 - y0) * cos(angle), r1 - r0, -(y1 - y0) * sin(angle)));
                foreach (var (radius, y) in new[] { profile[band], profile[band + 1] })
                {
                    vertices.Add(new SCNVector3(radius * cos(angle), y, radius * sin(angle)));
                    normals.Add(new SCNVector3(normal.x, normal.y, normal.z));
                }
                if (i < 128)
                {
                    var a = band * 258 + i * 2;
                    indices.AddRange(new[] { a, a + 2, a + 1, a + 1, a + 2, a + 3 });
                }
            }
        }
        return new SCNGeometry(new[] { SCNGeometrySource.vertices(vertices), SCNGeometrySource.normals(normals) },
                               new[] { new SCNGeometryElement(indices, SCNGeometryPrimitiveType.triangles) });
    }
}
