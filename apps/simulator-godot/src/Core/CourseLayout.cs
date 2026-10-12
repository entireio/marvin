using System;
using System.Collections.Generic;
using static Marvin.Core.Swift;

namespace Marvin.Core;

public struct Checkpoint : IEquatable<Checkpoint>
{
    public readonly double x, z;
    public Checkpoint(double x, double z) { this.x = x; this.z = z; }
    /// Swift synthesized Equatable: member-wise <c>==</c> (IEEE).
    public static bool operator ==(Checkpoint a, Checkpoint b) => a.x == b.x && a.z == b.z;
    public static bool operator !=(Checkpoint a, Checkpoint b) => !(a == b);
    public readonly bool Equals(Checkpoint other) => this == other;
    public override readonly bool Equals(object obj) => obj is Checkpoint other && this == other;
    public override readonly int GetHashCode() => HashCode.Combine(x, z);
}

public static class CourseLayout
{
    public const int count = 5;
    public const double ringRadius = 0.54, pipeRadius = 0.018, pulseScale = 1.06;
    public const double clearance = 0.15;
    public static readonly double outerRadius = (ringRadius + pipeRadius) * pulseScale;
    public static readonly Obstacle launch = new Obstacle(0, -2.6, 1.35, 1.25, 0);

    public static bool isClear(Checkpoint point, IReadOnlyList<Checkpoint> placed)
    {
        var radius = outerRadius + clearance;
        if (!(abs(point.x) + radius < Simulation.halfWidth &&
              abs(point.z) + radius < Simulation.halfDepth)) return false;
        bool overlaps(Obstacle box)
        {
            var dx = max(0, abs(point.x - box.x) - box.width / 2);
            var dz = max(0, abs(point.z - box.z) - box.depth / 2);
            return hypot(dx, dz) <= radius;
        }
        foreach (var box in Simulation.obstacles) if (overlaps(box)) return false;
        if (overlaps(launch)) return false;
        foreach (var other in placed)
        {
            if (!(hypot(point.x - other.x, point.z - other.z) > 2 * outerRadius + clearance)) return false;
        }
        return true;
    }

    public static Checkpoint[] generate(ulong seed)
    {
        var random = new SeededRandom(seed);
        // Jittered candidates cover the arena, then shuffle before selecting.
        // Bounded work avoids an unbounded rejection loop on the main thread.
        var candidates = new List<Checkpoint>();
        for (var x = -13; x <= 13; x++)
        {
            for (var z = -10; z <= 10; z++)
            {
                var cx = (double)x * 0.4 + SwiftRandom.doubleClosed(-0.15, 0.15, ref random);
                var cz = (double)z * 0.4 + SwiftRandom.doubleClosed(-0.15, 0.15, ref random);
                candidates.Add(new Checkpoint(cx, cz));
            }
        }
        SwiftRandom.shuffle(candidates, ref random);
        var placed = new List<Checkpoint>();
        foreach (var point in candidates)
        {
            if (!isClear(point, placed)) continue;
            placed.Add(point);
            if (placed.Count == count) return placed.ToArray();
        }
        throw preconditionFailure("Arena has insufficient room for five clear rings");
    }

    private struct SeededRandom : RandomNumberGenerator
    {
        public ulong state;
        public SeededRandom(ulong state) { this.state = state; }
        public ulong next()
        {
            unchecked
            {
                state += 0x9e3779b97f4a7c15;
                var value = state;
                value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9;
                value = (value ^ (value >> 27)) * 0x94d049bb133111eb;
                return value ^ (value >> 31);
            }
        }
    }
}
