using System;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin.Checks;

// Port of Tests/SimulationCoreTests/InfieldCollisionChecks.swift.
public partial struct SimulationTests
{
    public void testInfieldObstacles()
    {
        require(InfieldLayout.parts.Length >= 6);
        // Largest and smallest chassis can travel from the opening past the
        // clipped corner. No giant tent bounding box may close this aisle.
        var route = InfieldLayout.serviceLane;
        foreach (var profile in RobotCollisions.profiles)
        {
            for (var i = 1; i < route.Length; i++)
            {
                var delta = route[i] - route[i - 1]; var heading = atan2(delta.x, delta.y);
                for (var step = 0; step <= 40; step++)
                {
                    var p = route[i - 1] + delta * (double)step / 40;
                    var bodies = new[] { new RobotCollisions.Body(position: new Double3(p.x, DirtCourse.height(p.x, p.y), p.y), heading: heading, profile: profile) };
                    _ = RobotCollisions.resolve(bodies, terrain: true, betweenRobots: false);
                    if (abs(bodies[0].position.x - p.x) > 0.001 || abs(bodies[0].position.z - p.y) > 0.001) { print($"Blocked route: {p}, heading {description(heading)}, result {bodies[0].position}, chassis {description(profile.halfWidth)}"); }
                    near(bodies[0].position.x, p.x, 0.001);
                    near(bodies[0].position.z, p.y, 0.001);
                }
            }
            // Turning and reversing must fit too, not just facing each segment.
            foreach (var p in route)
            {
                for (var turn = 0; turn < 24; turn++)
                {
                    var bodies = new[] { new RobotCollisions.Body(position: new Double3(p.x, DirtCourse.height(p.x, p.y), p.y), heading: (double)turn * Math.PI / 12, profile: profile) };
                    _ = RobotCollisions.resolve(bodies, terrain: true, betweenRobots: false);
                    near(bodies[0].position.x, p.x, 0.001);
                    near(bodies[0].position.z, p.y, 0.001);
                }
            }
            // Continuous 240 Hz boosted approaches: no tunneling through salvage,
            // including the low metal panels. Static contacts remain enabled when
            // robot-to-robot collisions are disabled.
            foreach (var part in InfieldLayout.parts)
            {
                var obstacle = InfieldLayout.obstacles[InfieldLayout.obstacles.Length - InfieldLayout.parts.Length + Array.FindIndex(InfieldLayout.parts, candidate => candidate.x == part.x && candidate.z == part.z)];
                var axis = new Double2(cos(part.yaw), -sin(part.yaw));
                var start = new Double2(part.x, part.z) + axis * (part.width / 2 + 0.8);
                var bodies = new[] { new RobotCollisions.Body(position: new Double3(start.x, 0, start.y), heading: part.yaw, profile: profile) };
                var touched = false;
                for (var k = 0; k < 65; k++)
                {
                    bodies[0].velocity = new Double3(-axis.x * 8, 0, -axis.y * 8);
                    bodies[0].position += bodies[0].velocity / 240;
                    _ = RobotCollisions.resolve(bodies, terrain: true, betweenRobots: false);
                    touched = touched || bodies[0].contacted;
                    if (RobotCollisions.contact(bodies[0], obstacle) is RobotCollisions.Contact c) { require(c.penetration < 0.001); }
                }
                require(touched);
                var final = new Double2(bodies[0].position.x - part.x, bodies[0].position.z - part.z);
                require(Simd.dot(final, axis) > 0);
            }
        }
        // PORT: block scope; Swift lets this function-level `profile` coexist with the loop's `profile`.
        {
            var canopy = InfieldLayout.canopies[0];
            var profile = RobotCollisions.profiles[0];
            require(minElement(canopy.points.Select(point => point.x)).Value > DirtCourse.serviceEntryX + DirtCourse.serviceEntryHalfWidth + 1.5);
            foreach (var (local, y, expected) in new (Double2, double, bool)[] { (new Double2(-1.1, 0.3), 0.0, false), (new Double2(1.2, -1.3), 2.0, false), (new Double2(-1.1, 0.3), 2.0, true) })
            {
                var p = InfieldLayout.tentPoint(0, local);
                var body = new RobotCollisions.Body(position: new Double3(p.x, y, p.y), profile: profile);
                require((RobotCollisions.canopyContact(body, canopy.points, canopy.low, canopy.high) != null) == expected);
            }
            // A narrow pole must stop an approaching robot but allow backing away.
            var pole = InfieldLayout.obstacles.First(obstacle => abs(obstacle.position.x + 6.5) < 0.001 && abs(obstacle.position.z - 1.5) < 0.001);
            var probe = new[] { new RobotCollisions.Body(position: pole.position + new Double3(0.29, 0, 0), velocity: new Double3(-8, 0, 0), profile: profile) };
            _ = RobotCollisions.resolve(probe, terrain: true, betweenRobots: false);
            require(probe[0].contacted);
            require(RobotCollisions.contact(probe[0], pole) == null);
            var before = probe[0].position;
            probe[0].velocity = new Double3(1, 0, 0); probe[0].position.x += 0.05;
            _ = RobotCollisions.resolve(probe, terrain: true, betweenRobots: false);
            require(probe[0].position.x > before.x);
        }
        print($"PASS: triangular tent aisle, overhead clearance, and boosted salvage collisions for all four chassis; {InfieldLayout.parts.Length} seeded parts");
    }
}
