using System;
using System.Collections.Generic;
using static Marvin.Core.Swift;

namespace Marvin.Core;

/// Upright rigid bodies: three-axis translation and yaw inertia. Terrain owns
/// pitch/roll; this is deliberately not a six-axis tipping/suspension simulation.
public static class RobotCollisions
{
    public struct Profile
    {
        public readonly double mass, halfWidth, halfDepth, height;
        public readonly bool round;
        public Profile(double mass, double halfWidth, double halfDepth, double height, bool round = false)
        {
            precondition(mass > 0 && halfWidth > 0 && halfDepth > 0 && height > 0);
            this.mass = mass; this.halfWidth = halfWidth; this.halfDepth = halfDepth; this.height = height; this.round = round;
        }
        internal readonly double inertia => round ? mass * halfWidth * halfWidth / 2 : mass * (halfWidth * halfWidth + halfDepth * halfDepth) / 3;
    }
    // Footprint bounds measured from the scaled meshes. Masses are explicit game
    // tuning in kg, not claims about movie props or Marvin's physical hardware.
    public static readonly Profile[] profiles =
    {
        new Profile(mass: 18, halfWidth: 0.262, halfDepth: 0.28, height: 0.492),
        new Profile(mass: 55, halfWidth: 0.288, halfDepth: 0.283, height: 0.885),
        new Profile(mass: 12, halfWidth: 0.174, halfDepth: 0.174, height: 0.50, round: true),
        new Profile(mass: 85, halfWidth: 0.398, halfDepth: 0.246, height: 0.833),
    };
    public struct Body
    {
        public Double3 position, velocity;
        public double heading, angularVelocity;
        public readonly Profile profile;
        public bool contacted = false;
        // PORT: Swift's labels are (position:velocity:heading:angularVelocity:profile:) with profile required;
        // C# requires optional parameters last, so call with named arguments.
        public Body(Double3 position, Profile profile, Double3 velocity = default, double heading = 0, double angularVelocity = 0)
        {
            this.position = position; this.velocity = velocity; this.heading = heading; this.angularVelocity = angularVelocity; this.profile = profile;
        }
        internal readonly Double2 center => new Double2(position.x, position.z);
        internal readonly Double2 lateral => new Double2(cos(heading), -sin(heading));
        internal readonly Double2 forward => new Double2(sin(heading), cos(heading));
        internal readonly double extent(Double2 axis) =>
            profile.round ? profile.halfWidth : abs(Simd.dot(lateral, axis)) * profile.halfWidth + abs(Simd.dot(forward, axis)) * profile.halfDepth;
        internal readonly Double3 contactVelocity(Double3 at)
        {
            var r = at - position;
            return velocity + new Double3(angularVelocity * r.z, 0, -angularVelocity * r.x);
        }
        internal void impulse(Double3 impulse, Double3 at)
        {
            velocity += impulse / profile.mass;
            var r = at - position;
            angularVelocity += (r.z * impulse.x - r.x * impulse.z) / profile.inertia;
        }
    }
    public struct Contact
    {
        public readonly Double3 normal, point;
        public readonly double penetration;
        internal Contact(Double3 normal, Double3 point, double penetration) { this.normal = normal; this.point = point; this.penetration = penetration; }
    }
    /// Inelastic terrain impact: remove velocity into the surface, retaining
    /// tangential motion without creating kinetic energy or a rebound impulse.
    public static Double3 landingVelocity(Double3 velocity, Double3 normal)
    {
        var n = Simd.normalize(normal);
        return velocity - n * min(0, Simd.dot(velocity, n));
    }
    public static Contact? contact(Body a, Body b)
    {
        var low = max(a.position.y, b.position.y);
        var high = min(a.position.y + a.profile.height, b.position.y + b.profile.height);
        if (!(high > low)) return null;
        var delta = b.center - a.center;
        // PORT: the axes (at most four) and box corners live on the stack instead of in a List and arrays: contact runs
        // thousands of times per frame (race physics against the town, residents' swept steps). Same values, same order;
        // the nearest corner is the first minimal one, as Swift's min(by:).
        Span<Double2> axes = stackalloc Double2[4];
        int axisCount = 0;
        if (!a.profile.round) { axes[axisCount++] = a.lateral; axes[axisCount++] = a.forward; }
        if (!b.profile.round) { axes[axisCount++] = b.lateral; axes[axisCount++] = b.forward; }
        if (a.profile.round && b.profile.round)
        {
            axisCount = 0;
            axes[axisCount++] = Simd.length_squared(delta) > 1e-12 ? Simd.normalize(delta) : new Double2(1, 0);
        }
        else if (a.profile.round || b.profile.round)
        {
            var box = a.profile.round ? b : a;
            var circle = a.profile.round ? a : b;
            var nearest = default(Double2);
            var k = 0;
            for (var xi = 0; xi < 2; xi++)
                for (var zi = 0; zi < 2; zi++)
                {
                    double x = xi == 0 ? -1.0 : 1, z = zi == 0 ? -1.0 : 1;
                    var corner = box.center + box.lateral * x * box.profile.halfWidth + box.forward * z * box.profile.halfDepth;
                    if (k++ == 0 || Simd.length_squared(corner - circle.center) < Simd.length_squared(nearest - circle.center)) nearest = corner;
                }
            var direction = nearest - circle.center;
            if (Simd.length_squared(direction) > 1e-12) { axes[axisCount++] = Simd.normalize(direction); }
        }
        double depth = double.PositiveInfinity;
        var n = new Double2(1, 0);
        for (var i = 0; i < axisCount; i++)
        {
            var axis = axes[i];
            var overlap = a.extent(axis) + b.extent(axis) - abs(Simd.dot(delta, axis));
            if (!(overlap > 0)) return null;
            if (overlap < depth) { depth = overlap; n = Simd.dot(delta, axis) >= 0 ? axis : -axis; }
        }
        var verticalDepth = min(a.position.y + a.profile.height - b.position.y, b.position.y + b.profile.height - a.position.y);
        if (verticalDepth < depth)
        {
            var sign = b.position.y + b.profile.height / 2 >= a.position.y + a.profile.height / 2 ? 1.0 : -1.0;
            return new Contact(normal: new Double3(0, sign, 0), point: new Double3((a.position.x + b.position.x) / 2, (low + high) / 2, (a.position.z + b.position.z) / 2), penetration: verticalDepth);
        }
        var tangent = new Double2(-n.y, n.x);
        var minT = max(Simd.dot(a.center, tangent) - a.extent(tangent), Simd.dot(b.center, tangent) - b.extent(tangent));
        var maxT = min(Simd.dot(a.center, tangent) + a.extent(tangent), Simd.dot(b.center, tangent) + b.extent(tangent));
        // Circle contact lies on its radial normal; box faces use the overlap
        // midpoint, preventing artificial spin in a symmetric head-on impact.
        var t = a.profile.round ? Simd.dot(a.center, tangent) : b.profile.round ? Simd.dot(b.center, tangent) : (minT + maxT) / 2;
        var along = (Simd.dot(a.center, n) + a.extent(n) + Simd.dot(b.center, n) - b.extent(n)) / 2;
        var p = n * along + tangent * t;
        return new Contact(normal: new Double3(n.x, 0, n.y), point: new Double3(p.x, (low + high) / 2, p.y), penetration: depth);
    }
    /// Convex, elevated canopy envelope. SAT uses actual footprint edges, so
    /// the missing triangular corner remains traversable at every height.
    public static Contact? canopyContact(Body body, Double2[] footprint, double low, double high)
    {
        if (!(body.position.y < high && body.position.y + body.profile.height > low)) return null;
        var axes = body.profile.round ? new List<Double2>() : new List<Double2> { body.lateral, body.forward };
        for (var i = 0; i < footprint.Length; i++)
        {
            var d = footprint[(i + 1) % footprint.Length] - footprint[i];
            axes.Add(Simd.normalize(new Double2(-d.y, d.x)));
            if (body.profile.round)
            {
                var v = footprint[i] - body.center;
                if (Simd.length_squared(v) > 1e-10) { axes.Add(Simd.normalize(v)); }
            }
        }
        double depth = double.PositiveInfinity;
        var normal = Double3.zero;
        foreach (var axis in axes)
        {
            var values = new double[footprint.Length];
            for (var i = 0; i < footprint.Length; i++) values[i] = Simd.dot(footprint[i], axis);
            double c = Simd.dot(body.center, axis), extent = body.extent(axis);
            double left = c + extent - minElement(values).Value, right = maxElement(values).Value - c + extent;
            if (!(left > 0 && right > 0)) return null;
            if (min(left, right) < depth)
            {
                depth = min(left, right); var n = axis * (left < right ? 1.0 : -1.0);
                normal = new Double3(n.x, 0, n.y);
            }
        }
        double below = body.position.y + body.profile.height - low, above = high - body.position.y;
        if (min(below, above) < depth) { depth = min(below, above); normal = new Double3(0, below < above ? 1 : -1, 0); }
        return new Contact(normal: normal, point: body.position, penetration: depth);
    }

    private static double effectiveMass(Body body, Double3 at, Double3 axis)
    {
        Double3 r = at - body.position;
        var lever = r.z * axis.x - r.x * axis.z;
        return 1 / body.profile.mass + lever * lever / body.profile.inertia;
    }
    /// Low restitution for robot shells/rubber, with Coulomb contact friction.
    /// Positional correction is separate from velocity so overlap adds no energy.
    /// PORT: Swift `inout [Body]`; the C# array is mutated in place. `storm` defaults to a clear Sandstorm().
    public static int resolve(Body[] bodies, bool terrain = false, bool betweenRobots = true, CityGate? gate = null, CityCollisionWorld? city = null, Double3[] previousPositions = null, Sandstorm? storm = null, SandDeformation sand = null)
    {
        var weather = storm ?? new Sandstorm();
        var pairs = new HashSet<int>();
        for (var iteration = 0; iteration < 24; iteration++)
        {
            var worstOverlap = 0.0;
            if (betweenRobots)
            {
                for (var i = 0; i < bodies.Length; i++)
                {
                    for (var j = 0; j < bodies.Length; j++)
                    {
                        if (!(j > i)) continue;
                        if (!(contact(bodies[i], bodies[j]) is Contact c)) continue;
                        worstOverlap = max(worstOverlap, c.penetration);
                        pairs.Add(i * bodies.Length + j);
                        bodies[i].contacted = true; bodies[j].contacted = true;
                        var relative = bodies[j].contactVelocity(c.point) - bodies[i].contactVelocity(c.point);
                        var closing = Simd.dot(relative, c.normal);
                        if (closing < 0)
                        {
                            var restitution = iteration == 0 && closing < -0.5 ? 0.08 : 0;
                            var inverseMass = effectiveMass(bodies[i], c.point, c.normal) + effectiveMass(bodies[j], c.point, c.normal);
                            var normalImpulse = -(1 + restitution) * closing / inverseMass;
                            bodies[i].impulse(-c.normal * normalImpulse, c.point); bodies[j].impulse(c.normal * normalImpulse, c.point);
                            var after = bodies[j].contactVelocity(c.point) - bodies[i].contactVelocity(c.point);
                            var sliding = after - c.normal * Simd.dot(after, c.normal);
                            var length = Simd.length(sliding);
                            if (length > 1e-9)
                            {
                                var tangent = sliding / length;
                                var denominator = effectiveMass(bodies[i], c.point, tangent) + effectiveMass(bodies[j], c.point, tangent);
                                var friction = min(length / denominator, 0.45 * normalImpulse);
                                bodies[i].impulse(tangent * friction, c.point); bodies[j].impulse(-tangent * friction, c.point);
                            }
                        }
                        double inverseA = 1 / bodies[i].profile.mass, inverseB = 1 / bodies[j].profile.mass;
                        var correction = max(0, c.penetration - 0.0002) * 0.8 / (inverseA + inverseB);
                        bodies[i].position -= c.normal * correction * inverseA;
                        bodies[j].position += c.normal * correction * inverseB;
                    }
                }
            }
            if (terrain)
            {
                for (var i = 0; i < bodies.Length; i++)
                {
                    constrainToCourse(ref bodies[i], gate != null, previousPositions != null ? previousPositions[i] : null, weather, sand);
                    // Swift iterates InfieldLayout.obstacles + gate.body + CityExit.posts + city.nearby(bodies[i]),
                    // a sequence built before the loop; the nearby query therefore uses the constrained body.
                    var nearby = city is CityCollisionWorld world ? world.nearby(bodies[i]) : null;
                    foreach (var obstacle in InfieldLayout.obstacles) collideStatic(bodies, i, obstacle, ref worstOverlap);
                    if (gate is CityGate g)
                    {
                        collideStatic(bodies, i, g.body(), ref worstOverlap);
                        foreach (var post in CityExit.posts) collideStatic(bodies, i, post, ref worstOverlap);
                    }
                    if (nearby != null) foreach (var obstacle in nearby) collideStatic(bodies, i, obstacle, ref worstOverlap);
                    foreach (var canopy in InfieldLayout.canopies)
                    {
                        if (!(canopyContact(bodies[i], canopy.points, canopy.low, canopy.high) is Contact c)) continue;
                        worstOverlap = max(worstOverlap, c.penetration); bodies[i].contacted = true;
                        bodies[i].position -= c.normal * (c.penetration + 0.00001);
                        var inward = Simd.dot(bodies[i].velocity, c.normal);
                        if (inward > 0) { bodies[i].velocity -= c.normal * inward; }
                    }
                }
            }
            if (worstOverlap < 0.00025) break;
        }
        return pairs.Count;
    }
    private static void collideStatic(Body[] bodies, int i, Body obstacle, ref double worstOverlap)
    {
        var delta = bodies[i].center - obstacle.center;
        var radius = hypot(bodies[i].profile.halfWidth, bodies[i].profile.halfDepth) + hypot(obstacle.profile.halfWidth, obstacle.profile.halfDepth);
        if (!(Simd.length_squared(delta) < radius * radius)) return;
        if (!(contact(bodies[i], obstacle) is Contact c)) return;
        worstOverlap = max(worstOverlap, c.penetration);
        bodies[i].contacted = true;
        // Immovable props: remove inward velocity, retaining slide and
        // reverse control; positional correction cannot inject energy.
        bodies[i].position -= c.normal * (c.penetration + 0.00001);
        var inward = Simd.dot(bodies[i].velocity, c.normal);
        if (inward > 0) { bodies[i].velocity -= c.normal * inward; }
    }
    private static void constrainToCourse(ref Body body, bool escape, Double3? previous, Sandstorm storm, SandDeformation sand)
    {
        var projection = DirtCourse.projection(body.position.x, body.position.z);
        var heading = DirtCourse.heading(projection.phase);
        var outward = new Double2(cos(heading), -sin(heading)) * (projection.offset < 0 ? -1.0 : 1.0);
        var prior = previous is Double3 prev ? DirtCourse.projection(prev.x, prev.z) : projection;
        var insideField = projection.offset < 0 ? projection.distance > DirtCourse.fenceOffset : escape && prior.offset > 0 && prior.distance > DirtCourse.fenceOffset;
        var support = body.extent(outward) + 0.025;
        var limit = DirtCourse.fenceOffset + (insideField ? DirtCourse.boundaryWallThickness + support : -support);
        var serviceAccess = projection.offset < 0 && DirtCourse.serviceAccess(body.position.x, body.position.z, body.extent(new Double2(1, 0)));
        var exitAccess = escape && projection.offset > 0 && CityExit.opening(body.center, body.extent(CityExit.tangent));
        if ((insideField ? projection.distance < limit : projection.distance > limit) && !serviceAccess && !exitAccess)
        {
            var point = DirtCourse.point(projection.phase, projection.offset < 0 ? -limit : limit);
            body.position.x = point.x; body.position.z = point.z;
            var normal = new Double3(outward.x, 0, outward.y) * (insideField ? -1.0 : 1.0);
            var speed = Simd.dot(body.velocity, normal);
            if (speed > 0) { body.velocity -= normal * speed; }
            body.contacted = true;
        }
        var floor = storm.height(body.position.x, body.position.z) + (sand?.supportOffset(body.position.x, body.position.z, body.heading, body.profile) ?? 0);
        if (body.position.y < floor)
        {
            body.position.y = floor;
            if (body.velocity.y < 0) { body.velocity.y = 0; }
        }
    }
}
