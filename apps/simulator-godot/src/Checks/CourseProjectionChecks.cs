using System;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin.Checks;

// Port of Tests/SimulationCoreTests/CourseProjectionChecks.swift.
public partial struct SimulationTests
{
    public void testExactCourseProjection()
    {
        var count = DirtCourse.sampleCount;
        var points = Enumerable.Range(0, count + 1).Select(i =>
        {
            var p = DirtCourse.point((double)i * 2 * Math.PI / (double)count);
            return new Double2(p.x, p.z);
        }).ToArray();
        // Independent exhaustive oracle: same contract as the pre-optimization
        // implementation, including the first-segment tie rule at shared vertices.
        (double, double, double) exhaustive(Double2 p)
        {
            double best = double.PositiveInfinity, phase = 0.0, offset = 0.0;
            for (var i = 0; i < count; i++)
            {
                Double2 a = points[i], d = points[i + 1] - a, v = p - a;
                var length = d.x * d.x + d.y * d.y;
                var t = max(0, min(1, (v.x * d.x + v.y * d.y) / length));
                var e = p - (a + d * t); var distance = e.x * e.x + e.y * e.y;
                if (distance < best)
                {
                    best = distance; phase = ((double)i + t) * 2 * Math.PI / (double)count;
                    offset = (e.x * d.y - e.y * d.x) / sqrt(length);
                }
            }
            return (phase, offset, sqrt(best));
        }
        void check(Double2 p)
        {
            var expected = exhaustive(p); var actual = DirtCourse.projection(p.x, p.y);
            near(actual.phase, expected.Item1, 1e-12);
            near(actual.offset, expected.Item2, 1e-10);
            near(actual.distance, expected.Item3, 1e-10);
        }
        foreach (var p in points) { check(p); }
        for (var i = 0; i < count; i++)
        {
            var phase = ((double)i + 0.5) * 2 * Math.PI / (double)count;
            foreach (var offset in new[] { -DirtCourse.terrainEdge, -DirtCourse.width, 0, DirtCourse.width, DirtCourse.terrainEdge })
            {
                var p = DirtCourse.point(phase, offset); check(new Double2(p.x, p.z));
            }
        }
        // Includes interiors, tight competing arcs, shoulders and distant city lots.
        for (var z = -70; z <= 70; z++) { for (var x = -70; x <= 70; x++) { check(new Double2((double)x * 0.47, (double)z * 0.47)); } }
        foreach (var z in new[] { -150.0, 0, 150 }) { foreach (var x in new[] { -150.0, 0, 150 }) { check(new Double2(x, z)); } }
        // Overflow-sized positions keep the old no-finite-candidate result.
        foreach (var x in new[] { -1e200, 1e200 })
        {
            foreach (var z in new[] { -1e200, 1e200 })
            {
                var result = DirtCourse.projection(x, z);
                equal(result.phase, 0); equal(result.offset, 0); require(double.IsInfinity(result.distance));
            }
        }
        print("PASS: exact spatial projection agrees with exhaustive scan at 24,499 queries plus overflow fallback");
    }
}
