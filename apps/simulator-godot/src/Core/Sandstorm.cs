using System;
using static Marvin.Core.Swift;

namespace Marvin.Core;

/// Deterministic weather state, advanced only by the race's fixed-step clock.
/// Wind is m/s; drift depth is metres. No shared mutable terrain globals.
public struct Sandstorm
{
    /// Independent 10% chance for each race/reset, not every tenth race.
    public static bool drawForRace<R>(ref R generator) where R : RandomNumberGenerator =>
        SwiftRandom.intHalfOpen(0, 10, ref generator) == 0;
    public static bool drawForRace()
    {
        var generator = new SystemRandomNumberGenerator();
        return drawForRace(ref generator);
    }
    public bool enabled;
    public double elapsed { get; private set; } = 0.0;
    public Sandstorm() : this(false) { }
    public Sandstorm(bool enabled) { this.enabled = enabled; }
    public void advance(double dt) { if (enabled) { elapsed += max(0, dt); } }
    public readonly double accumulation => enabled ? min(1, 0.18 + elapsed / 180) : 0;
    public readonly Double3 wind(double x, double z)
    {
        if (!enabled) return Double3.zero;
        var gust = 19 + 4 * sin(elapsed * 0.47 + x * 0.013) + 2 * sin(elapsed * 1.13 + z * 0.02);
        var angle = 0.65 + 0.09 * sin(elapsed * 0.21);
        return new Double3(cos(angle) * gust, 0, sin(angle) * gust);
    }
    public struct Drift
    {
        public readonly Double2 center;
        public readonly double along, across, height;
        internal Drift(Double2 center, double along, double across, double height) { this.center = center; this.along = along; this.across = across; this.height = height; }
    }
    public static readonly Drift[] drifts = buildDrifts();
    private static Drift[] buildDrifts()
    {
        var result = new Drift[17];
        for (var i = 0; i < 17; i++)
        {
            var phase = (double)i * 2 * Math.PI / 17 + 0.037 * sin((double)i * 7);
            var p = DirtCourse.point(phase, (double)(i % 3 - 1) * 0.72);
            result[i] = new Drift(center: new Double2(p.x, p.z), along: 0.75 + (double)(i % 4) * 0.18, across: 1.1 + (double)(i % 5) * 0.27, height: 0.065 + (double)((i * 7) % 9) * 0.01);
        }
        return result;
    }
    /// Compact, asymmetric deposits aligned with prevailing wind. Zero at every
    /// patch boundary, so no vertical skirts or floating sheets are needed.
    public static double deposit(double x, double z)
    {
        if (!(max(abs(x), abs(z)) < 24)) return 0;
        var height = 0.0;
        foreach (var drift in drifts)
        {
            var d = new Double2(x, z) - drift.center;
            var u = (d.x * 0.796 + d.y * 0.605) / drift.along + 0.12 * sin(d.y * 3.1 + drift.center.x);
            var v = (-d.x * 0.605 + d.y * 0.796) / drift.across + 0.10 * sin(d.x * 2.7 + drift.center.y);
            var r = u * u + v * v;
            if (r < 1) { height += drift.height * pow(1 - r, 2) * (1 + 0.3 * u); }
        }
        return height;
    }
    public readonly double depth(double x, double z) => enabled ? accumulation * deposit(x, z) : 0;
    public readonly double height(double x, double z) => DirtCourse.height(x, z) + depth(x, z);
    public readonly Double3 surfaceNormal(double x, double z)
    {
        var e = 0.04;
        return Simd.normalize(new Double3(-(height(x + e, z) - height(x - e, z)) / (2 * e), 1,
                                          -(height(x, z + e) - height(x, z - e)) / (2 * e)));
    }
    public readonly Double3 acceleration(Double3 velocity, double x, double z, RobotCollisions.Profile profile, double shelter = 1)
    {
        if (!enabled) return Double3.zero;
        var relative = wind(x, z) * shelter - new Double3(velocity.x, 0, velocity.z);
        // Quadratic aerodynamic drag / mass. Coefficient is game-tuned, with
        // chassis cross-section and mass retaining their physical relationship.
        var area = 2 * profile.halfWidth * profile.height; // effective drag coefficient 0.55
        return relative * Simd.length(relative) * (0.5 * 1.1 * 0.55 * area / profile.mass);
    }
}
