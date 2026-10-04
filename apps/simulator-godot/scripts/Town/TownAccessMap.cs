using System.Collections.Generic;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

/// Shared, load-time pedestrian reachability. A doorway may face an alley, but
/// that alley must connect to a town street with pedestrian-width clearance.
public sealed class TownAccessMap
{
    private readonly int size = 1320; private readonly double origin = -165.0, step = 0.25;
    private readonly int[] distance;
    public readonly struct Footprint
    {
        public readonly Double2 center; public readonly double width, depth, yaw;
        public Footprint(Double2 center, double width, double depth, double yaw) { this.center = center; this.width = width; this.depth = depth; this.yaw = yaw; }
    }
    // PORT: Swift's labels are (lots:footprints:streets:) with streets required; C# requires optional
    // parameters last, so streets comes first. Call with named arguments, as in Swift.
    public TownAccessMap(List<List<Double2>> streets, List<TownWorld.TownLot> lots = null, List<Footprint> footprints = null)
    {
        lots ??= new List<TownWorld.TownLot>(); footprints ??= new List<Footprint>();
        distance = Enumerable.Repeat(-1, size * size).ToArray();
        var obstacles = footprints.Concat(lots.Select(l => new Footprint(center: new Double2(l.x, l.z), width: l.width, depth: l.depth, yaw: 0))).ToList();
        foreach (var lot in obstacles)
        {
            double c = cos(lot.yaw), s = sin(lot.yaw), halfW = lot.width / 2 + 0.38, halfD = lot.depth / 2 + 0.38;
            var radius = new Double2(abs(c) * halfW + abs(s) * halfD, abs(s) * halfW + abs(c) * halfD);
            Int2 lo = cell(lot.center - radius), hi = cell(lot.center + radius);
            for (var z = max(0, lo.y); z <= min(size - 1, hi.y); z++) { for (var x = max(0, lo.x); x <= min(size - 1, hi.x); x++) {
                var q = new Double2(origin + ((double)x + 0.5) * step, origin + ((double)z + 0.5) * step) - lot.center;
                if (abs(q.x * c - q.y * s) < halfW && abs(q.x * s + q.y * c) < halfD) { distance[z * size + x] = -2; }
            }}
        }
        // The racing surface and infield are not public alley connections.
        {
            Int2 lo = cell(new Double2(-28.0, -28.0)), hi = cell(new Double2(28.0, 28.0));
            for (var z = lo.y; z <= hi.y; z++) { for (var x = lo.x; x <= hi.x; x++) {
                var p = new Double2(origin + ((double)x + 0.5) * step, origin + ((double)z + 0.5) * step);
                var projection = DirtCourse.projection(x: p.x, z: p.y);
                if (projection.offset <= 0 || projection.distance < DirtCourse.fenceOffset + 0.22) { distance[z * size + x] = -2; }
            }}
        }
        var queue = new List<int>(size * size / 3);
        foreach (var path in streets) { foreach (var p in path) {
            Int2 c = cell(p); int i = c.y * size + c.x;
            if (c.x >= 0 && c.x < size && c.y >= 0 && c.y < size && distance[i] == -1) { distance[i] = 0; queue.Add(i); }
        }}
        var head = 0;
        while (head < queue.Count)
        {
            int i = queue[head]; head += 1;
            int x = i % size, z = i / size;
            foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int xx = x + dx, zz = z + dz;
                if (!(xx >= 0 && xx < size && zz >= 0 && zz < size)) { continue; }
                var j = zz * size + xx;
                if (distance[j] == -1) { distance[j] = distance[i] + 1; queue.Add(j); }
            }
        }
    }
    private Int2 cell(Double2 p) => new Int2((int)floor((p.x - origin) / step), (int)floor((p.y - origin) / step));
    public double? streetDistance(Double2 p)
    {
        var c = cell(p);
        if (!(c.x >= 0 && c.x < size && c.y >= 0 && c.y < size)) return null;
        var d = distance[c.y * size + c.x];
        return d >= 0 ? (double)d * step : null;
    }
    /// Follow the shared flood field to a public street, retaining enough
    /// samples to audit the entire corridor against the final collision mesh.
    public List<Double2> routeToStreet(Double2 from)
    {
        var p = from;
        var c = cell(p);
        if (!(c.x >= 0 && c.x < size && c.y >= 0 && c.y < size && distance[c.y * size + c.x] >= 0)) return null;
        var result = new List<Double2> { p };
        for (var step_ = 0; step_ < 3000; step_++)
        {
            var value = distance[c.y * size + c.x];
            result.Add(new Double2(origin + ((double)c.x + 0.5) * step, origin + ((double)c.y + 0.5) * step));
            if (value == 0) { return result; }
            Int2? next = null;
            foreach (var q in new[] { new Int2(c.x + 1, c.y), new Int2(c.x - 1, c.y), new Int2(c.x, c.y + 1), new Int2(c.x, c.y - 1) })
            {
                if (q.x >= 0 && q.x < size && q.y >= 0 && q.y < size && distance[q.y * size + q.x] == value - 1) { next = q; break; }
            }
            if (next == null) { return null; }
            c = next.Value;
        }
        return null;
    }

}
