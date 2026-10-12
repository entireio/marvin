using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using static Marvin.Core.Swift;

namespace Marvin.Core;

/// Closed motocross spline with a start straight, mixed turns and jump sections.
public static class DirtCourse
{
    /// Half the compacted lane width; centerline control points stay unchanged.
    public static readonly (double phase, double offset) playerGrid = (phase: -0.055, offset: -0.72);
    public static readonly (double phase, double offset) opponentGrid = (phase: -0.14, offset: 0.72);
    public static readonly (double phase, double offset)[] startingGrid =
    {
        playerGrid, opponentGrid,
        (phase: -0.225, offset: -0.72), (phase: -0.31, offset: 0.72),
    };

    /// One distinct staggered slot per racer; caller owns the random generator.
    public static (double phase, double offset)[] shuffledGrid<R>(ref R random) where R : RandomNumberGenerator =>
        SwiftRandom.shuffled(startingGrid, ref random);
    public const double width = 1.3 * 1.5;
    public const double bermWidth = 0.35;
    public const double fenceOffset = width + 0.5;
    public const double boundaryWallThickness = 0.15;
    public const double shoulderEdge = fenceOffset + 0.1;
    public const double terrainEdge = shoulderEdge + 0.45;
    // Shared by the visible wall opening, chassis constraints and the baked service-route dirt.
    public const double serviceEntryX = -7.5;
    public const double serviceEntryHalfWidth = 1.25;
    public static bool serviceAccess(double x, double z, double clearance = 0) =>
        abs(x - serviceEntryX) < max(0, serviceEntryHalfWidth - clearance) && z > -15.4 && z < -5.3;
    public const double postHeight = 0.40;
    private static readonly Double2[] controls =
    {
        new(0, -10), new(8, -10), new(12, -6), new(10, 0),
        new(5, 0), new(4, -4), new(-1, -4), new(-2, 3),
        new(6, 5), new(10, 10), new(3, 12), new(-6, 10),
        new(-11, 5), new(-10, -2), new(-10, -9), new(-5, -10),
    };
    public const int sampleCount = 768;
    private static Double2 center(double phase)
    {
        var u = (phase / (2 * Math.PI)) % 1;
        var t = (u < 0 ? u + 1 : u) * (double)controls.Length;
        int i = (int)t, n = controls.Length;
        var f = t - (double)i;
        Double2 a = controls[(i + n - 1) % n], b = controls[i % n], c = controls[(i + 1) % n], d = controls[(i + 2) % n];
        // A periodic B-spline keeps tight bends smooth enough for the full
        // lane and shoulder widths (interpolating splines can fold inside turns).
        var constant = a + b * 4 + c;
        var linear = (-a * 3 + c * 3) * f;
        var quadratic = (a * 3 - b * 6 + c * 3) * (f * f);
        var cubic = (-a + b * 3 - c * 3 + d) * (f * f * f);
        return (constant + linear + quadratic + cubic) * 0.25;
    }
    public static (double x, double z) point(double phase, double offset = 0)
    {
        var p = center(phase);
        var d = center(phase + 0.0001) - center(phase - 0.0001);
        var length = hypot(d.x, d.y);
        return (p.x + d.y / length * offset, p.y - d.x / length * offset);
    }
    /// Trim loops in parallel offsets at tight inside bends. Collapsing the
    /// removed samples to their intersection preserves the centerline and phase
    /// indexing while preventing folded terrain and crossing fence segments.
    public static Double2[] surfacePoints(double offset)
    {
        var points = new Double2[sampleCount + 1];
        for (var i = 0; i < sampleCount; i++)
        {
            var p = point((double)i * 2 * Math.PI / (double)sampleCount, offset);
            points[i] = new Double2(p.x, p.z);
        }
        if (abs(offset) > 1.85)
        {
            static double cross(Double2 a, Double2 b) => a.x * b.y - a.y * b.x;
            for (var i = 0; i < sampleCount - 2; i++)
            {
                var a = points[i];
                var r = points[i + 1] - a;
                if (r.x * r.x + r.y * r.y < 1e-16) continue;
                for (var j = i + 2; j < sampleCount; j++)
                {
                    if (i == 0 && j == sampleCount - 1) continue;
                    var b = points[j];
                    var s = points[(j + 1) % sampleCount] - b;
                    var denominator = cross(r, s);
                    if (abs(denominator) < 1e-12) continue;
                    double t = cross(b - a, s) / denominator, u = cross(b - a, r) / denominator;
                    if (t > 1e-8 && t < 1 - 1e-8 && u > 1e-8 && u < 1 - 1e-8)
                    {
                        var intersection = a + r * t;
                        for (var k = i + 1; k <= j; k++) points[k] = intersection;
                        break;
                    }
                }
            }
        }
        points[sampleCount] = points[0];
        return points;
    }
    public static double heading(double phase)
    {
        Double2 a = center(phase - 0.0001), b = center(phase + 0.0001);
        return atan2(b.x - a.x, b.y - a.y);
    }
    private static readonly Double2[] samples = buildSamples();
    private static Double2[] buildSamples()
    {
        var result = new Double2[sampleCount + 1];
        for (var i = 0; i <= sampleCount; i++) result[i] = center((double)i * 2 * Math.PI / (double)sampleCount);
        return result;
    }
    private readonly struct ProjectionNode
    {
        public readonly Double2 low, high;
        public readonly int start, end, left, right;
        public ProjectionNode(Double2 low, Double2 high, int start, int end, int left, int right)
        {
            this.low = low; this.high = high; this.start = start; this.end = end; this.left = left; this.right = right;
        }
        public double distanceSquared(Double2 p)
        {
            var x = max(0, max(low.x - p.x, p.x - high.x));
            var z = max(0, max(low.y - p.y, p.y - high.y));
            return x * x + z * z;
        }
    }
    // Adjacent spline segments are spatially coherent. A balanced bounds tree
    // rejects whole arcs while retaining the exact original segment projection.
    private static readonly ProjectionNode[] projectionNodes = buildProjectionNodes();
    private static ProjectionNode[] buildProjectionNodes()
    {
        var nodes = new List<ProjectionNode>();
        int build(int start, int end)
        {
            Double2 lo = new Double2(double.PositiveInfinity), hi = new Double2(double.NegativeInfinity);
            for (var i = start; i <= end; i++)
            {
                lo.x = min(lo.x, samples[i].x); lo.y = min(lo.y, samples[i].y);
                hi.x = max(hi.x, samples[i].x); hi.y = max(hi.y, samples[i].y);
            }
            var index = nodes.Count;
            nodes.Add(new ProjectionNode(lo, hi, start, end, -1, -1));
            if (end - start > 12)
            {
                var middle = (start + end) / 2;
                var left = build(start, middle);
                var right = build(middle, end);
                nodes[index] = new ProjectionNode(lo, hi, start, end, left, right);
            }
            return index;
        }
        _ = build(0, sampleCount);
        return nodes.ToArray();
    }
    public static (double phase, double offset, double distance) projection(double x, double z)
    {
        var p = new Double2(x, z);
        double best = double.PositiveInfinity, bestPhase = 0.0, offset = 0.0;
        var bestIndex = sampleCount;
        void search(int index)
        {
            var node = projectionNodes[index];
            // Allow rounding slack in the lower bound, including distant queries.
            if (node.distanceSquared(p) > best + max(1e-12, ulp(best) * 8)) return;
            if (node.left >= 0)
            {
                var leftDistance = projectionNodes[node.left].distanceSquared(p);
                var rightDistance = projectionNodes[node.right].distanceSquared(p);
                if (leftDistance <= rightDistance) { search(node.left); search(node.right); }
                else { search(node.right); search(node.left); }
                return;
            }
            for (var i = node.start; i < node.end; i++)
            {
                Double2 a = samples[i], d = samples[i + 1] - a, v = p - a;
                var lengthSquared = d.x * d.x + d.y * d.y;
                var t = max(0, min(1, (v.x * d.x + v.y * d.y) / lengthSquared));
                var e = p - (a + d * t);
                var distance = e.x * e.x + e.y * e.y;
                // The original ascending scan resolves ties to the first segment.
                if (distance < best || (distance == best && bestIndex < sampleCount && i < bestIndex))
                {
                    best = distance; bestIndex = i;
                    bestPhase = ((double)i + t) * 2 * Math.PI / (double)sampleCount;
                    offset = (e.x * d.y - e.y * d.x) / sqrt(lengthSquared);
                }
            }
        }
        search(0);
        return (bestPhase, offset, sqrt(best));
    }
    public static double phase(double x, double z) => projection(x, z).phase;
    public static bool contains(double x, double z, double margin = 0) => projection(x, z).distance <= width - margin;
    public static double elevation(double phase, double offset = 0)
    {
        var u = ((phase / (2 * Math.PI)) % 1 + 1) % 1;
        double bump(double center, double half, double height)
        {
            var t = abs(u - center) / half;
            return t < 1 ? height * pow(cos(t * Math.PI / 2), 2) : 0;
        }
        // Filled tabletop, rounded rollers, rhythm doubles, raised step-up,
        // then a run of smaller whoops. Features blend into the same mesh.
        var table = max(0, min(1, min((u - 0.025) / 0.015, (0.085 - u) / 0.015))) * 0.70;
        var rollers = bump(0.26, 0.020, 0.24) + bump(0.30, 0.020, 0.32) + bump(0.34, 0.020, 0.24);
        var rhythm = bump(0.49, 0.025, 0.62) + bump(0.54, 0.025, 0.56);
        var hill = bump(0.69, 0.095, 1.30);
        var whoops = 0.0;
        for (var k = 0; k < 6; k++) whoops = whoops + bump(0.82 + (double)k * 0.015, 0.0075, 0.16);
        double h0 = heading(phase - 0.01), h1 = heading(phase + 0.01);
        var turn = atan2(sin(h1 - h0), cos(h1 - h0));
        var bank = min(0.35, abs(turn) * 3) * pow(max(0, offset * (turn > 0 ? -1.0 : 1.0) / width), 2);
        // Broad climbs and descents under the jump features leave the grid flat.
        var terrain = bump(0.17, 0.065, 0.65) + bump(0.40, 0.065, 0.90) + bump(0.91, 0.065, 0.55);
        return table + rollers + rhythm + hill + whoops + terrain + bank;
    }
    public static double surfaceHeight(double phase, double offset)
    {
        var @base = courseHeight(phase, offset);
        if (offset > 0)
        {
            var p = point(phase, offset);
            if (CityExit.rampHeight(new Double2(p.x, p.z)) is double ramp)
            {
                var t = max(0, min(1, (offset - width) / 0.25));
                var blend = t * t * (3 - 2 * t);
                return max(@base, ramp) * (1 - blend) + ramp * blend;
            }
        }
        return @base;
    }
    internal static double courseHeight(double phase, double offset)
    {
        var distance = abs(offset);
        var @base = elevation(phase, max(-width, min(width, offset)));
        if (distance <= width) return @base;
        if (distance <= width + bermWidth)
        {
            var crest = offset > 0 ? 0.10 : 0.055;
            return @base + sin((distance - width) / bermWidth * Math.PI) * crest;
        }
        var location = point(phase, offset);
        if (offset < 0 && serviceHeight(location.x, location.z) is double service) return service;
        return distance <= fenceOffset + boundaryWallThickness ? @base : -0.025;
    }
    private static readonly (double phase, double x, double z, double wallX, double wallZ)[] serviceSections = buildServiceSections();
    private static (double phase, double x, double z, double wallX, double wallZ)[] buildServiceSections()
    {
        var result = new (double phase, double x, double z, double wallX, double wallZ)[752 - 688 + 1];
        for (var i = 688; i <= 752; i++)
        {
            var phase = (double)i / (double)sampleCount * 2 * Math.PI;
            var edge = point(phase, -fenceOffset);
            var wall = point(phase, -fenceOffset - boundaryWallThickness);
            result[i - 688] = (phase: phase, x: edge.x, z: edge.z, wallX: wall.x, wallZ: wall.z);
        }
        return result;
    }
    /// The entrance cross-sections form a continuous field across the yard;
    /// global nearest-course selection would cut a cliff through the shoulder.
    private static double? serviceHeight(double x, double z)
    {
        if (!(abs(x - serviceEntryX) < 3.6 && z > -15.4 && z < -5.3)) return null;
        // X is monotone along this entrance edge. Extend each cross-section
        // toward +Z instead of selecting a different nearest curve branch.
        var first = serviceSections[0];
        var phase = first.phase;
        (double x, double z) edge = (x: first.x, z: first.z), wall = (x: first.wallX, z: first.wallZ);
        for (var i = 0; i < serviceSections.Length - 1; i++)
        {
            var a = serviceSections[i];
            var b = serviceSections[i + 1];
            if (!(x >= a.x)) break;
            var t = max(0, min(1, (x - a.x) / max(0.0001, b.x - a.x)));
            phase = a.phase + (b.phase - a.phase) * t;
            edge = (x, a.z + (b.z - a.z) * t);
            var wt = max(0, min(1, (x - a.wallX) / max(0.0001, b.wallX - a.wallX)));
            wall = (x, a.wallZ + (b.wallZ - a.wallZ) * wt);
            if (x <= b.x) break;
        }
        var distance = z - edge.z;
        if (!(distance > 0)) return null;
        var run = 3.6 + 0.35 * (0.5 + 0.5 * sin(x * 1.3));
        var spread = 3.0 + 0.30 * sin(z * 0.9) + 0.20 * cos(z * 1.7);
        if (!(distance < run && abs(x - serviceEntryX) < spread)) return null;
        var tt = max(0, min(1, distance / run));
        var side = max(0, min(1, (abs(x - serviceEntryX) - serviceEntryHalfWidth) / (spread - serviceEntryHalfWidth)));
        double along = 1 - tt * tt * (3 - 2 * tt), across = 1 - side * side * (3 - 2 * side);
        var @base = elevation(phase, -width);
        var ordinary = z <= wall.z ? @base : -0.025;
        return ordinary + ((@base + 0.025) * along - 0.025 - ordinary) * across;
    }
    /// Project only against the fence. The robot can slide along it and reverse
    /// away; the compacted lane edge is a traction change, not a collision wall.
    public static (double x, double z, bool contact) resolveMove(double x, double z, double heading)
    {
        var p = projection(x, z);
        var tangent = DirtCourse.heading(p.phase);
        var relative = heading - tangent;
        var support = 0.35 * abs(cos(relative)) + 0.33 * abs(sin(relative));
        if (p.offset < 0 && serviceAccess(x, z, support)) return (x, z, false);
        var insideField = p.offset < 0 && p.distance > fenceOffset;
        var limit = fenceOffset + (insideField ? boundaryWallThickness + support + 0.025 : -support - 0.025);
        if (!(insideField ? p.distance < limit : p.distance > limit)) return (x, z, false);
        var corrected = point(p.phase, p.offset < 0 ? -limit : limit);
        return (corrected.x, corrected.z, true);
    }
    public static double traction(double x, double z)
    {
        var distance = projection(x, z).distance;
        var loose = max(0, min(1, (distance - (width - 0.35)) / 0.40));
        return 1 - loose * 0.72;
    }
    public static double height(double x, double z)
    {
        if (max(abs(x), abs(z)) > DesertTerrain.townEdge - 2) return DesertTerrain.height(x, z);
        if (serviceHeight(x, z) is double service) return service;
        var p = projection(x, z);
        return surfaceHeight(p.phase, p.offset);
    }
}

public struct DirtRace
{
    public double countdown { get; private set; } = 3.0;
    public double elapsed { get; private set; } = 0.0;
    /// PORT: Swift [Double] is a value; appends replace this array instead of mutating it,
    /// so copies of a DirtRace never share later laps.
    public double[] laps { get; private set; } = Array.Empty<double>();
    public double progress { get; private set; } = 0.0;
    private double previousPhase = 0.0, lapStart = 0.0;
    public readonly bool finished => laps.Length == 3;
    public double cooldownProgress { get; private set; } = 0.0;
    public readonly bool completedCooldownLap => cooldownProgress >= 2 * Math.PI - 1e-8;
    public readonly double currentLap => elapsed - lapStart;
    public bool wrongWay = false;
    public DirtRace() : this(0.0) { }
    public DirtRace(double startPhase) { previousPhase = startPhase; progress = startPhase; }
    public void countDown(double dt) { countdown = max(0, countdown - max(0, dt)); }
    public void advance(double x, double z, double dt)
    {
        if (!(countdown == 0 && dt > 0 && double.IsFinite(dt))) return;
        var phase = DirtCourse.phase(x, z);
        var delta = atan2(sin(phase - previousPhase), cos(phase - previousPhase));
        previousPhase = phase;
        if (finished)
        {
            if (DirtCourse.projection(x, z).distance < DirtCourse.fenceOffset && abs(delta) < 0.3) { cooldownProgress += delta; }
            return;
        }
        var oldTime = elapsed; elapsed += dt;
        wrongWay = delta < -0.0001;
        // Reject teleports/off-course samples. Signed progress means reversing
        // over the finish or oscillating across it cannot award extra laps.
        if (!(DirtCourse.projection(x, z).distance < DirtCourse.fenceOffset && abs(delta) < 0.3)) return;
        var oldProgress = progress;
        progress += delta;
        var finish = (double)(laps.Length + 1) * 2 * Math.PI;
        if (oldProgress < finish && progress >= finish - 1e-9 && delta > 0)
        {
            var fraction = max(0, min(1, (finish - oldProgress) / delta));
            var crossing = oldTime + dt * fraction;
            laps = laps.Append(crossing - lapStart).ToArray(); lapStart = crossing;
            if (finished) { elapsed = crossing; }
        }
    }
}

public struct DirtScore
{
    /// PORT: Swift `Date` is a Double of seconds since 2001-01-01 00:00 UTC, which is exactly what
    /// JSONEncoder's default (.deferredToDate) strategy writes. The port keeps that Double so a load/save
    /// round trip is bit-exact like Swift's (DateTime's 100 ns ticks shift ~19% of current dates by one ulp)
    /// and any finite JSON date loads, even outside DateTime's range.
    public readonly double timeIntervalSinceReferenceDate;
    /// Swift <c>date</c> as a UTC <see cref="DateTime"/> (rounded to 100 ns ticks; throws outside DateTime's range).
    public readonly DateTime date => DirtScores.referenceDate.AddTicks((long)Math.Round(timeIntervalSinceReferenceDate * TimeSpan.TicksPerSecond));
    public readonly double[] laps;
    public readonly double total
    {
        get { var sum = 0.0; foreach (var lap in laps) sum = sum + lap; return sum; }
    }
    public DirtScore(double[] laps, DateTime? date = null)
        : this(laps, (date ?? DateTime.UtcNow).ToUniversalTime().Subtract(DirtScores.referenceDate).Ticks / (double)TimeSpan.TicksPerSecond) { }
    /// Swift <c>DirtScore(laps:date: Date(timeIntervalSinceReferenceDate:))</c>.
    public DirtScore(double[] laps, double timeIntervalSinceReferenceDate) { this.laps = laps; this.timeIntervalSinceReferenceDate = timeIntervalSinceReferenceDate; }
    public readonly bool valid => laps.Length == 3 && laps.All(lap => double.IsFinite(lap) && lap > 0);
}

/// PORT: Swift takes file URLs; the port takes file-system paths (globalize Godot user:// paths first).
public static class DirtScores
{
    internal static readonly DateTime referenceDate = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static DirtScore[] ranked(IEnumerable<DirtScore> scores) =>
        sorted(scores.Where(score => score.valid), (a, b) => a.total < b.total).Take(10).ToArray();

    public static DirtScore[] load(string path)
    {
        if (!File.Exists(path)) return Array.Empty<DirtScore>();
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var scores = new List<DirtScore>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var seconds = element.GetProperty("date").GetDouble();
            var laps = element.GetProperty("laps").EnumerateArray().Select(lap => lap.GetDouble()).ToArray();
            scores.Add(new DirtScore(laps, seconds));
        }
        return ranked(scores);
    }

    public static void save(IEnumerable<DirtScore> scores, string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var score in ranked(scores))
            {
                writer.WriteStartObject();
                writer.WriteNumber("date", score.timeIntervalSinceReferenceDate);
                writer.WriteStartArray("laps");
                foreach (var lap in score.laps) writer.WriteNumberValue(lap);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        // Atomic replacement, like Data.write(to:options:.atomic).
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temporary, stream.ToArray());
        File.Move(temporary, path, overwrite: true);
    }
}

/// Finishers sort by their frozen crossing time, remaining racers by progress.
/// Original index breaks ties deterministically without inventing finish times.
public static class DirtStandings
{
    public static int[] order(IReadOnlyList<DirtRace> races) =>
        sorted(Enumerable.Range(0, races.Count), (a, b) =>
        {
            if (races[a].finished != races[b].finished) return races[a].finished;
            if (races[a].finished && races[a].elapsed != races[b].elapsed) return races[a].elapsed < races[b].elapsed;
            if (!races[a].finished && races[a].progress != races[b].progress) return races[a].progress > races[b].progress;
            return a < b;
        });
}
