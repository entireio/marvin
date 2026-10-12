using System;
using System.Collections.Generic;
using System.Linq;
using static Marvin.Core.Swift;

namespace Marvin.Core;

/// Conservative town navigation with footprint clearance and line-of-sight simplification.
public struct TownEscapeRoute
{
    internal readonly CityCollisionWorld city;
    internal readonly Double2 origin;
    public TownEscapeRoute(CityCollisionWorld city, Double2 origin) { this.city = city; this.origin = origin; }
    /// PORT: Swift returns nil when no reachable cell is close enough; C# returns null.
    public readonly Double2[] route(Double2 start, Double2 goal, bool rounded = false, double clearance = 0.53, double goalTolerance = 3, IReadOnlyList<RobotCollisions.Body> avoiding = null, double cellSize = 0.5)
    {
        var origin = this.origin;
        var city = this.city;
        var avoid = avoiding ?? Array.Empty<RobotCollisions.Body>();
        Int2 cell(Double2 p) { var d = (p - origin) / cellSize; return new Int2((int)Swift.rounded(d.x), (int)Swift.rounded(d.y)); }
        Double2 point(Int2 c) => origin + new Double2((double)c.x, (double)c.y) * cellSize;
        bool free(Double2 p)
        {
            var projection = DirtCourse.projection(p.x, p.y);
            if (!(projection.offset > 0 && projection.distance > DirtCourse.fenceOffset + 0.8)) return false;
            var body = new RobotCollisions.Body(position: new Double3(p.x, DirtCourse.height(p.x, p.y), p.y), profile: new RobotCollisions.Profile(mass: 18, halfWidth: clearance, halfDepth: clearance, height: 1.0, round: true));
            foreach (var obstacle in city.nearby(body)) if (RobotCollisions.contact(body, obstacle) != null) return false;
            foreach (var obstacle in avoid) if (RobotCollisions.contact(body, obstacle) != null) return false;
            return true;
        }
        Int2 source = cell(start), target = cell(goal);
        var parents = new Dictionary<Int2, Int2> { [source] = source };
        var queue = new List<Int2> { source };
        var head = 0;
        var best = source;
        var bestDistance = double.PositiveInfinity;
        var cache = new Dictionary<Int2, bool>();
        Int2[] directions = { new Int2(1, 0), new Int2(-1, 0), new Int2(0, 1), new Int2(0, -1) };
        while (head < queue.Count)
        {
            var c = queue[head]; head += 1;
            var distance = Simd.distance(point(c), goal);
            if (distance < bestDistance) { best = c; bestDistance = distance; }
            if (c == target) break;
            foreach (var d in directions)
            {
                var next = c + d;
                if (!(abs(next.x) < (int)(50 / cellSize) && abs(next.y) < (int)(50 / cellSize) && !parents.ContainsKey(next))) continue;
                var available = cache.TryGetValue(next, out var cached) ? cached : free(point(next)); cache[next] = available;
                if (available) { parents[next] = c; queue.Add(next); }
            }
        }
        if (!(bestDistance < goalTolerance)) { Console.WriteLine($"Town route nearest reachable point is {description(bestDistance)}m from {goal}"); return null; }
        var path = new List<Double2> { point(best) };
        var cursor = best;
        while (cursor != source) { cursor = parents[cursor]; path.Add(point(cursor)); }
        path.Reverse();
        bool clear(Double2 a, Double2 b)
        {
            var count = max(1, (int)ceil(Simd.distance(a, b) / 0.15));
            for (var k = 0; k <= count; k++) if (!free(a + (b - a) * (double)k / (double)count)) return false;
            return true;
        }
        var result = new List<Double2> { start };
        var i = 0;
        while (i < path.Count - 1)
        {
            var next = i + 1;
            for (var j = i + 1; j < path.Count; j++) { if (clear(result[result.Count - 1], path[j])) { next = j; } else { break; } }
            result.Add(path[next]); i = next;
        }
        if (!(rounded && result.Count > 2)) return result.ToArray();
        // PORT: Swift shadows the `rounded` parameter with this local.
        var roundedPath = new List<Double2> { result[0] };
        for (var k = 1; k < result.Count - 1; k++)
        {
            Double2 a = result[k - 1], b = result[k], c = result[k + 1];
            var trim = min(1.4, min(Simd.distance(a, b), Simd.distance(b, c)) * 0.35);
            Double2 entry = b + Simd.normalize(a - b) * trim, exit = b + Simd.normalize(c - b) * trim;
            var curve = new Double2[9];
            for (var step = 0; step <= 8; step++)
            {
                var t = (double)step / 8; curve[step] = entry * (1 - t) * (1 - t) + b * 2 * t * (1 - t) + exit * t * t;
            }
            var curveClear = true;
            for (var s = 0; s < curve.Length - 1; s++) if (!clear(curve[s], curve[s + 1])) { curveClear = false; break; }
            if (curveClear) { roundedPath.AddRange(curve); }
            else { roundedPath.Add(b); }
        }
        roundedPath.Add(result[result.Count - 1]);
        for (var s = 0; s < roundedPath.Count - 1; s++) if (!clear(roundedPath[s], roundedPath[s + 1])) return result.ToArray();
        return roundedPath.ToArray();
    }
}
