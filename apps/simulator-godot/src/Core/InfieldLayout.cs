using System;
using System.Collections.Generic;
using System.Linq;
using static Marvin.Core.Swift;

namespace Marvin.Core;

/// Shared authored fixtures and seeded salvage: rendering and collision use the
/// same transforms. Seeded placement keeps race resets and tests reproducible.
public static class InfieldLayout
{
    public static readonly Double2[] tentOrigins = { new Double2(-3.8, -9.2), new Double2(-8.5, 3.5) };
    public static readonly Double2[] orangeCorners = { new Double2(-1.6, -1.6), new Double2(-1.6, 1.6), new Double2(1.4, 1.6) };
    public static readonly Double2[] serviceLane = { new Double2(-7.5, -12.5), new Double2(-8.8, -10.8), new Double2(-8.8, -6.2), new Double2(-8.5, -3), new Double2(-8.5, 1.8) };
    public static readonly double[] tentYaws = { 5 * Math.PI / 6, 0 };
    public static Double2 tentPoint(int index, Double2 local)
    {
        var yaw = tentYaws[index];
        return tentOrigins[index] + new Double2(local.x * cos(yaw) + local.y * sin(yaw), -local.x * sin(yaw) + local.y * cos(yaw));
    }
    public static double laneDistance(Double2 p)
    {
        var distances = new double[serviceLane.Length - 1];
        for (var i = 0; i < distances.Length; i++)
        {
            Double2 a = serviceLane[i], b = serviceLane[i + 1];
            var d = b - a;
            var t = max(0, min(1, Simd.dot(p - a, d) / Simd.length_squared(d)));
            distances[i] = Simd.length(p - a - d * t);
        }
        return minElement(distances).Value;
    }

    public struct Part
    {
        public readonly double x, z, yaw, width, depth, height;
        public readonly int kind;
        internal Part(double x, double z, double yaw, double width, double depth, double height, int kind)
        {
            this.x = x; this.z = z; this.yaw = yaw; this.width = width; this.depth = depth; this.height = height; this.kind = kind;
        }
    }
    public static readonly Part[] parts = buildParts();
    private static Part[] buildParts()
    {
        var result = new List<Part>();
        ulong seed = 0x5a17cafe;
        double random() { unchecked { seed = seed * 6364136223846793005 + 1442695040888963407; } return (double)(seed >> 11) / 9007199254740992; }
        for (var n = 0; n < 900; n++)
        {
            double x = -10 + random() * 19, z = -9 + random() * 19;
            var p = DirtCourse.projection(x, z);
            if (!(p.offset < 0 && p.distance > DirtCourse.fenceOffset + 1.3)) continue;
            // Reserve the entrance-to-east-side aisle and both repair footprints.
            if (!(DirtCourse.height(x, z) < -0.02 &&
                  laneDistance(new Double2(x, z)) > 1.35 &&
                  hypot((x - tentOrigins[0].x) / 2.7, (z - tentOrigins[0].y) / 2.7) > 1 &&
                  hypot((x + 8.5) / 3.0, (z - 3.5) / 3.0) > 1)) continue;
            if (!result.All(part => hypot(part.x - x, part.z - z) > 1.45)) continue;
            var kind = result.Count % 3;
            double w = 0.45 + random() * 0.4, d = 0.4 + random() * 0.35;
            result.Add(new Part(x: x, z: z, yaw: random() * 2 * Math.PI, width: w, depth: d, height: kind == 0 ? 0.32 : kind == 1 ? 0.48 : 0.18, kind: kind));
            if (result.Count == 16) break;
        }
        return result.ToArray();
    }
    public struct Canopy
    {
        public readonly Double2[] points;
        public readonly double low, high;
        internal Canopy(Double2[] points, double low, double high) { this.points = points; this.low = low; this.high = high; }
    }
    public static readonly Canopy[] canopies =
    {
        new Canopy(points: orangeCorners.Select(corner => tentPoint(0, corner)).ToArray(), low: 1.97, high: 2.32),
        new Canopy(points: new[] { new Double2(-10.5, 1.5), new Double2(-10.5, 5.5), new Double2(-6.5, 5.5), new Double2(-6.5, 1.5) }, low: 1.97, high: 2.67),
    };
    public static readonly RobotCollisions.Body[] obstacles = buildObstacles();
    private static RobotCollisions.Body[] buildObstacles()
    {
        var result = new List<RobotCollisions.Body>();
        void box(double x, double z, double w, double d, double height, double y = 0, double yaw = 0, bool round = false)
        {
            result.Add(new RobotCollisions.Body(position: new Double3(x, y, z), heading: yaw, profile: new RobotCollisions.Profile(mass: 1, halfWidth: w / 2, halfDepth: d / 2, height: height, round: round)));
        }
        for (var i = 0; i < tentOrigins.Length; i++)
        {
            var origin = tentOrigins[i];
            var first = result.Count;
            var posts = i == 0 ? orangeCorners : new[] { new Double2(-2.0, -2.0), new Double2(-2.0, 2.0), new Double2(2.0, -2.0), new Double2(2.0, 2.0) };
            foreach (var p in posts) { box(origin.x + p.x, origin.y + p.y, 0.07, 0.07, 2.15, 0, 0, true); }
            double x = origin.x + (i == 0 ? -0.1 : 0), z = origin.y + (i == 0 ? 0.35 : 0);
            // Bench legs and elevated top leave genuine clearance beneath it.
            foreach (var zz in new[] { -0.55, 0.85 }) { box(x - 0.92, z + zz, 0.48, 0.09, 0.66); }
            box(x - 0.92, z + 0.15, 0.64, 1.7, 0.12, 0.63);
            box(x + 0.24, z + 0.20, 0.95, 1.3, 0.28);
            box(x + 0.24, z + 0.20, 0.62, 0.62, 0.93, 0.28, 0, true);
            foreach (var side in new[] { -1.0, 1.0 }) { box(x + 0.24 + side * 0.38, z + 0.30, 0.22, 0.40, 0.6, 0.25); }
            box(origin.x + (i == 0 ? -1.625 : 0), origin.y + (i == 0 ? -0.65 : -2.035), 1.85, 0.05, 0.46, 1.51, i == 0 ? -Math.PI / 2 : Math.PI);
            box(x + 1.17, z + 0.91, 0.09, 0.09, 1.6);
            box(x + 0.83, z + 0.91, 0.76, 0.1, 0.1, 1.55);
            box(x + 0.51, z + 0.91, 0.44, 0.44, 0.36, 0.76, 0, true);
            box(x + (i == 0 ? -0.1 : 1.03), z + (i == 0 ? 0.85 : -0.86), 0.54, 0.54, 0.535, 0, 0, true);
            box(x - 0.96, z - 1.06, 0.6, 0.4, 0.4);
            box(x + (i == 0 ? -1.5 : 1.5), z + (i == 0 ? 0.5 : 0.95), 0.32, 0.4, 0.94);
            if (i == 0)
            {
                box(x - 1.54, z + 0.15, 0.08, 1.75, 0.77, 0.665);
                box(x - 0.93, z + 0.93, 0.57, 0.13, 0.43, 0.115);
                box(x - 1.3, z - 0.85, 0.50, 0.50, 0.50, 0.08);
                box(x - 1.42, z - 1.43, 0.39, 0.39, 0.39, 0.08, 0, true);
            }
            // Stationary mechanic occupies the same footprint as the visible figure.
            box(origin.x + (i == 0 ? 0.65 : 0.73), origin.y + (i == 0 ? -0.25 : -0.35), 0.40, 0.4, 0.9, 0.055, 0, true);
            if (i == 0)
            {
                for (var j = first; j < result.Count; j++)
                {
                    var q = tentPoint(i, new Double2(result[j].position.x - origin.x, result[j].position.z - origin.y));
                    var moved = result[j];
                    moved.position.x = q.x; moved.position.z = q.y;
                    moved.heading += tentYaws[i];
                    result[j] = moved;
                }
            }
        }
        foreach (var side in new[] { -1.0, 1.0 }) { var x = DirtCourse.serviceEntryX + side * 1.4; box(x, -12.2, 0.09, 0.09, 0.56, DirtCourse.height(x, -12.2), 0, true); }
        box(DirtCourse.serviceEntryX - 1.85, -12.21, 0.05, 0.05, 0.67, DirtCourse.height(DirtCourse.serviceEntryX - 1.85, -12.21));
        foreach (var p in parts) { box(p.x, p.z, p.width, p.depth, p.height, 0, p.yaw, p.kind == 0); }
        return result.ToArray();
    }
}
