using System;
using System.Collections.Generic;
using System.Linq;
using static Marvin.Core.Swift;

namespace Marvin.Core;

/// Authored at the outside of the first right-hand bend. One coordinate frame
/// owns the wall opening, hinge, ramp, clearance reservation and test route.
public static class CityExit
{
    public const double phase = 0.125 * 2 * Math.PI;
    public const double width = 3.2, run = 5.5;
    public static readonly Double2 center = buildCenter();
    private static Double2 buildCenter()
    {
        var p = DirtCourse.point(phase, DirtCourse.fenceOffset + 0.08);
        return new Double2(p.x, p.z);
    }
    public static readonly Double2 tangent = new Double2(sin(DirtCourse.heading(phase)), cos(DirtCourse.heading(phase)));
    public static readonly Double2 outward = new Double2(tangent.y, -tangent.x);
    public static readonly Double2 hinge = center - tangent * width / 2;
    public static readonly double floor = buildFloor();
    private static double buildFloor()
    {
        // [-width/2, 0, width/2].map { ... }.max()!
        var heights = new[] { -width / 2, 0, width / 2 }.Select(along =>
        {
            var p = point(along, 0);
            return DirtCourse.elevation(DirtCourse.phase(p.x, p.y), DirtCourse.width);
        });
        return maxElement(heights).Value;
    }
    public const double gateHeight = 0.95;
    /// Reinforced entry walls meet the gate head, then descend in level brick
    /// courses toward the existing retaining wall over the next few metres.
    public static double wallTop(Double2 p)
    {
        var @base = DirtCourse.elevation(DirtCourse.phase(p.x, p.y), DirtCourse.width) + DirtCourse.postHeight;
        var q = local(p);
        if (!(abs(q.y) < 3.5)) return @base;
        var t = max(0, min(1, (abs(q.x) - 2.8) / 3.2));
        var blend = 1 - t * t * (3 - 2 * t);
        return @base + max(0, floor + 0.035 + gateHeight - @base) * blend;
    }
    public static readonly RobotCollisions.Body[] posts = new[] { -1.0, 1.0 }.Select(side =>
    {
        var p = point(side * (width / 2 + 0.09), 0);
        return new RobotCollisions.Body(position: new Double3(p.x, -0.025, p.y), profile: new RobotCollisions.Profile(mass: 100, halfWidth: 0.085, halfDepth: 0.085, height: floor + gateHeight + 0.16, round: true));
    }).ToArray();
    public static Double2 point(double along, double @out) => center + tangent * along + outward * @out;
    public static Double2 local(Double2 p) { var d = p - center; return new Double2(Simd.dot(d, tangent), Simd.dot(d, outward)); }
    public static bool opening(Double2 p, double clearance = 0)
    {
        var q = local(p);
        return abs(q.x) < width / 2 - clearance && q.y > -1.1 && q.y < run + 1;
    }
    public static bool reserved(Double2 p, double radius)
    {
        var q = local(p);
        return abs(q.x) < 3.5 + radius && q.y > -0.5 - radius && q.y < 10 + radius;
    }
    public static double? rampHeight(Double2 p)
    {
        var q = local(p);
        if (!(abs(q.x) <= 4.001 && q.y > -2.5 && q.y < run)) return null;
        // A level sill supports the straight gate; the original bank varied
        // along its length, leaving a triangular opening beneath the leaf.
        var t = max(0, min(1, (q.y - 0.2) / (run - 0.2)));
        var s = max(0, min(1, (abs(q.x) - (width / 2 + 0.1)) / (4 - width / 2 - 0.1)));
        var across = 1 - s * s * (3 - 2 * s);
        var approach = max(0, min(1, (q.y + 2.5) / 2.3));
        var projection = DirtCourse.projection(p.x, p.y);
        var bank = DirtCourse.elevation(projection.phase, max(-DirtCourse.width, min(DirtCourse.width, projection.offset)));
        // Side shoulders join the existing raised bank, not the town floor.
        // Multiplying the whole bank by `across` cut two deep slots at ±4m.
        var @base = DirtCourse.courseHeight(projection.phase, projection.offset);
        var centerHeight = q.y < 0 ? bank + max(0, floor - bank) * approach * approach * (3 - 2 * approach)
            : (floor + 0.025) * (1 - t * t * (3 - 2 * t)) - 0.025;
        return @base + (centerHeight - @base) * across;
    }
}

/// Motor-driven hinge with acceleration, damping, end stops and obstacle sensing.
/// Blocked motion stalls; the door never teleports or sweeps a robot through a wall.
public struct CityGate
{
    public double angle { get; private set; } = 0.0;
    public double velocity { get; private set; } = 0.0;
    public bool wantsOpen = false;
    public bool blocked { get; private set; } = false;
    public CityGate() { }
    /// PORT: Swift `var body: Body { body(at: angle) }`; C# cannot overload a property and a method, so call <c>body()</c>.
    public readonly RobotCollisions.Body body() => body(angle);
    public readonly RobotCollisions.Body body(double at)
    {
        var angle = at;
        var along = CityExit.tangent * cos(angle) + CityExit.outward * sin(angle);
        var center = CityExit.hinge + along * CityExit.width / 2;
        return new RobotCollisions.Body(position: new Double3(center.x, CityExit.floor + 0.035, center.y), heading: atan2(-along.y, along.x), profile: new RobotCollisions.Profile(mass: 95, halfWidth: CityExit.width / 2, halfDepth: 0.055, height: CityExit.gateHeight));
    }
    public void advance(double dt, IReadOnlyList<RobotCollisions.Body> bodies)
    {
        if (!(dt > 0 && double.IsFinite(dt))) return;
        var duration = min(dt, 0.1);
        var count = max(1, (int)ceil(duration * 240));
        for (var i = 0; i < count; i++) step(duration / (double)count, bodies);
    }
    private void step(double dt, IReadOnlyList<RobotCollisions.Body> bodies)
    {
        var target = wantsOpen ? 100 * Math.PI / 180 : 0;
        var error = target - angle;
        var desired = max(-0.45, min(0.45, error * 2.5));
        velocity += max(-0.7 * dt, min(0.7 * dt, desired - velocity));
        var next = max(0, min(100 * Math.PI / 180, angle + velocity * dt));
        var candidate = body(next);
        blocked = false;
        foreach (var body in bodies)
        {
            var predicted = body; predicted.position += body.velocity * dt;
            if (RobotCollisions.contact(body, candidate) != null || RobotCollisions.contact(predicted, candidate) != null) { blocked = true; break; }
        }
        if (blocked) { velocity = 0; return; }
        angle = next;
        if (abs(error) < 0.0001) { angle = target; velocity = 0; }
    }
}

/// Immutable spatial buckets keep town contacts local even with thousands of
/// authored solids. Render builders supply the same transformed primitives.
public struct CityCollisionWorld
{
    public readonly RobotCollisions.Body[] bodies;
    private readonly Dictionary<Int2, List<int>> buckets;
    private RobotCollisions.Body[] dynamicBodies;
    /// Swift copies the struct and replaces the dynamic array; the bucket index is shared and never mutated.
    public readonly CityCollisionWorld withDynamicBodies(IEnumerable<RobotCollisions.Body> bodies)
    {
        var copy = this; copy.dynamicBodies = bodies.ToArray(); return copy;
    }
    public CityCollisionWorld(IEnumerable<RobotCollisions.Body> bodies)
    {
        this.bodies = bodies.ToArray();
        buckets = new Dictionary<Int2, List<int>>();
        dynamicBodies = Array.Empty<RobotCollisions.Body>();
        for (var i = 0; i < this.bodies.Length; i++)
        {
            var b = this.bodies[i];
            var r = hypot(b.profile.halfWidth, b.profile.halfDepth);
            for (var x = (int)floor((b.position.x - r) / 8); x <= (int)floor((b.position.x + r) / 8); x++)
            {
                for (var z = (int)floor((b.position.z - r) / 8); z <= (int)floor((b.position.z + r) / 8); z++)
                {
                    var key = new Int2(x, z);
                    if (!buckets.TryGetValue(key, out var list)) { list = new List<int>(); buckets[key] = list; }
                    list.Add(i);
                }
            }
        }
    }
    public readonly RobotCollisions.Body[] nearby(RobotCollisions.Body body)
    {
        var r = hypot(body.profile.halfWidth, body.profile.halfDepth) + 0.1;
        var ids = new HashSet<int>();
        for (var x = (int)floor((body.position.x - r) / 8); x <= (int)floor((body.position.x + r) / 8); x++)
        {
            for (var z = (int)floor((body.position.z - r) / 8); z <= (int)floor((body.position.z + r) / 8); z++)
            {
                if (buckets.TryGetValue(new Int2(x, z), out var list)) foreach (var i in list) ids.Add(i);
            }
        }
        var result = new List<RobotCollisions.Body>();
        foreach (var i in sorted(ids))
        {
            var b = bodies[i];
            var reach = r + hypot(b.profile.halfWidth, b.profile.halfDepth);
            if (hypot(body.position.x - b.position.x, body.position.z - b.position.z) < reach) result.Add(b);
        }
        foreach (var b in dynamicBodies)
        {
            if (Simd.distance(body.center, b.center) < r + hypot(b.profile.halfWidth, b.profile.halfDepth)) result.Add(b);
        }
        return result.ToArray();
    }
}
