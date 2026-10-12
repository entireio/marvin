using System;
using static Marvin.Core.Swift;

namespace Marvin.Core;

/// Shared navigation boundary, with a small dead band to avoid flickering at
/// the wall or the town edge. The infield belongs to the course enclosure.
/// PORT: Swift `enum RaceMapRegion: String` with a static `at`. C# enums cannot carry string raw
/// values or static methods, so this is a value type with the three cases as static members
/// (<c>RaceMapRegion.course</c>, ...) and <c>rawValue</c>. Compare with <c>==</c>; switch on <c>rawValue</c>.
public readonly struct RaceMapRegion : IEquatable<RaceMapRegion>
{
    public readonly string rawValue;
    private RaceMapRegion(string rawValue) { this.rawValue = rawValue; }
    public static readonly RaceMapRegion course = new RaceMapRegion("COURSE");
    public static readonly RaceMapRegion town = new RaceMapRegion("TOWN");
    public static readonly RaceMapRegion dunes = new RaceMapRegion("DUNES");
    public static readonly RaceMapRegion[] allCases = { course, town, dunes };
    /// Swift <c>RaceMapRegion(rawValue:)</c>; null when the string is not a case.
    public static RaceMapRegion? fromRawValue(string rawValue) =>
        rawValue == "COURSE" ? course : rawValue == "TOWN" ? town : rawValue == "DUNES" ? dunes : null;
    private static class Enclosure
    {
        internal static readonly Double2[] points = DirtCourse.surfacePoints(DirtCourse.fenceOffset);
    }
    public static RaceMapRegion at(Double2 p) => at(p, course);
    public static RaceMapRegion at(Double2 p, RaceMapRegion previous)
    {
        var enclosure = Enclosure.points;
        var edge = max(abs(p.x), abs(p.y));
        if (TownFootprint.edgeDistance(p) > (previous == dunes ? -1 : 1)) return dunes;
        if (edge > 35) return town;
        var inside = false;
        var distance = double.PositiveInfinity;
        for (var i = 0; i < enclosure.Length; i++)
        {
            Double2 a = enclosure[i], b = enclosure[(i + 1) % enclosure.Length];
            var d = b - a;
            var t = max(0, min(1, Simd.dot(p - a, d) / max(1e-12, Simd.dot(d, d))));
            distance = min(distance, Simd.length(p - a - d * t));
            if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x) { inside = !inside; }
        }
        if (distance < 0.18) return previous == course ? course : town;
        return inside ? course : town;
    }
    public static bool operator ==(RaceMapRegion a, RaceMapRegion b) => a.rawValue == b.rawValue;
    public static bool operator !=(RaceMapRegion a, RaceMapRegion b) => a.rawValue != b.rawValue;
    public bool Equals(RaceMapRegion other) => rawValue == other.rawValue;
    public override bool Equals(object obj) => obj is RaceMapRegion other && Equals(other);
    public override int GetHashCode() => rawValue?.GetHashCode() ?? 0;
    /// Swift prints an enum case by name: <c>course</c>, <c>town</c>, <c>dunes</c>.
    public override string ToString() => rawValue?.ToLowerInvariant() ?? "";
}

/// Critically damped camera motion: continuous position and velocity when a
/// boundary changes the destination, independent of rendering frame rate.
public struct OverviewMotion
{
    // PORT: Swift `public private(set) var`; fields so they can be passed by ref to `step`.
    public Double3 eye = Double3.zero, aim = Double3.zero;
    private Double3 eyeVelocity = Double3.zero, aimVelocity = Double3.zero;
    public OverviewMotion() { }
    public void reset(Double3 eye, Double3 aim)
    {
        this.eye = eye; this.aim = aim; eyeVelocity = Double3.zero; aimVelocity = Double3.zero;
    }
    public void advance(Double3 eye, Double3 aim, double dt)
    {
        Double3 goalEye = eye, goalAim = aim;
        double h = max(0, min(0.1, dt)), omega = 8.0, decay = exp(-omega * h);
        void step(ref Double3 p, ref Double3 v, Double3 goal)
        {
            var delta = p - goal;
            var t = (v + omega * delta) * h;
            p = goal + (delta + t) * decay; v = (v - omega * t) * decay;
        }
        step(ref this.eye, ref eyeVelocity, goalEye); step(ref this.aim, ref aimVelocity, goalAim);
    }
}

/// Irregular urban apron shared by town generation and navigation. The dune
/// heightfield remains level underneath every foundation and exit route.
public static class TownFootprint
{
    public static double radius(double angle) =>
        130 + 13 * sin(3 * angle + 0.4) + 9 * cos(5 * angle - 0.7) + 6 * sin(2 * angle);
    public static double edgeDistance(Double2 p) => Simd.length(p) - radius(atan2(p.y, p.x));
}
