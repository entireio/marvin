using System;
using System.Collections.Generic;
using System.Linq;
using static Marvin.Core.Swift;

namespace Marvin.Core;

/// Shortest visibility-graph route around conservative robot-clearance polygons.
/// Circumscribed corner arcs leave room for Marvin's circular collision footprint.
public static class CourseRoute
{
    private static Checkpoint[][] barriers
    {
        get
        {
            var r = Simulation.radius / cos(Math.PI / 8) + 0.002;
            var corners = new (double sx, double sz, double degrees)[] { (1.0, 1.0, 0.0), (-1.0, 1.0, 90.0), (-1.0, -1.0, 180.0), (1.0, -1.0, 270.0) };
            return Simulation.obstacles.Select(box =>
                corners.SelectMany(corner => Enumerable.Range(0, 3).Select(step =>
                {
                    var a = (corner.degrees + (double)step * 45) * Math.PI / 180;
                    return new Checkpoint(box.x + corner.sx * box.width / 2 + r * cos(a), box.z + corner.sz * box.depth / 2 + r * sin(a));
                })).ToArray()).ToArray();
        }
    }
    private static bool clear(Checkpoint a, Checkpoint b, Checkpoint[][] barriers)
    {
        // Clip the segment against the interior half-planes of each convex polygon.
        foreach (var polygon in barriers)
        {
            double lower = 0.0, upper = 1.0;
            for (var i = 0; i < polygon.Length; i++)
            {
                Checkpoint p = polygon[i], q = polygon[(i + 1) % polygon.Length];
                double ex = q.x - p.x, ez = q.z - p.z;
                var start = ex * (a.z - p.z) - ez * (a.x - p.x) - 1e-9;
                var delta = ex * (b.z - a.z) - ez * (b.x - a.x);
                if (abs(delta) < 1e-12)
                {
                    if (start <= 0) { upper = -1; break; }
                }
                else if (delta > 0) { lower = max(lower, -start / delta); }
                else { upper = min(upper, -start / delta); }
            }
            if (lower < upper) return false;
        }
        return true;
    }
    public static Checkpoint[] path(Checkpoint start, Checkpoint goal)
    {
        var polygons = barriers;
        var corners = polygons.SelectMany(polygon => polygon).Where(c =>
            abs(c.x) < Simulation.halfWidth - Simulation.radius && abs(c.z) < Simulation.halfDepth - Simulation.radius);
        var points = new[] { start, goal }.Concat(corners).ToArray();
        var distance = Enumerable.Repeat(double.PositiveInfinity, points.Length).ToArray();
        var previous = Enumerable.Repeat(-1, points.Length).ToArray();
        var visited = new HashSet<int>();
        distance[0] = 0;
        while (minBy(Enumerable.Range(0, points.Length).Where(i => !visited.Contains(i)), (i, j) => distance[i] < distance[j], out var u))
        {
            if (!double.IsFinite(distance[u])) break;
            if (u == 1)
            {
                var route = new List<Checkpoint> { goal };
                var index = 1;
                while (previous[index] >= 0) { index = previous[index]; route.Add(points[index]); }
                route.Reverse();
                return route.ToArray();
            }
            visited.Add(u);
            for (var v = 0; v < points.Length; v++)
            {
                if (!(!visited.Contains(v) && clear(points[u], points[v], polygons))) continue;
                var next = distance[u] + hypot(points[v].x - points[u].x, points[v].z - points[u].z);
                if (next < distance[v]) { distance[v] = next; previous[v] = u; }
            }
        }
        // The fixed arena and generated checkpoints always have a route.
        throw preconditionFailure("Checkpoint is unreachable");
    }
    public static double labelYaw(Checkpoint start, Checkpoint goal)
    {
        var route = path(start, goal);
        var approach = route[route.Length - 2];
        return atan2(approach.x - goal.x, approach.z - goal.z);
    }
}
