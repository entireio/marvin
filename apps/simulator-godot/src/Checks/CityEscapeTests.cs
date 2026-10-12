using System;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin.Checks;

// Port of Tests/SimulationCoreTests/CityEscapeTests.swift.
public partial struct SimulationTests
{
    public void testCityEscape()
    {
        var h = 1.0 / 240;
        foreach (var side in new[] { -1.0, 1.0 })
        {
            require(CityExit.wallTop(CityExit.point(side * 2, 0)) >= CityExit.floor + CityExit.gateHeight + 0.035 - 0.00001);
        }
        // The straight leaf needs a level sill across its full width and depth.
        foreach (var along in stride(-CityExit.width / 2, CityExit.width / 2, 0.025))
        {
            foreach (var @out in new[] { -0.055, 0, 0.055 })
            {
                var p = CityExit.point(along, @out);
                near(DirtCourse.height(p.x, p.y), CityExit.floor, 0.003);
            }
        }
        // Regress the two deep slots where the outside ramp meets the shoulder.
        foreach (var side in new[] { -1.0, 1.0 })
        {
            foreach (var @out in new[] { -1.2, -1.0 })
            {
                double? previous = null;
                foreach (var along in stride(3.8, 4.2, 0.005))
                {
                    var p = CityExit.point(side * along, @out); var height = DirtCourse.height(p.x, p.y);
                    if (previous is double prior) { require(abs(height - prior) < 0.025); }
                    previous = height;
                }
            }
        }
        // The service shoulder crosses a nearest-track Voronoi boundary.
        // Its local earth field must remain continuous across that boundary.
        foreach (var z in stride(-11.05, -8.05, 0.025))
        {
            foreach (var x in stride(-10.6, -8.3, 0.005))
            {
                if (DirtCourse.projection(x, z).distance < 2.8) continue;
                require(abs(DirtCourse.height(x + 0.005, z) - DirtCourse.height(x, z)) < 0.01);
            }
        }
        var gate = new CityGate();
        gate.wantsOpen = true;
        var previousSpeed = 0.0;
        for (var k = 0; k < 2400; k++)
        {
            gate.advance(h, Array.Empty<RobotCollisions.Body>());
            require(gate.angle >= 0 && gate.angle <= 100 * Math.PI / 180);
            require(abs(gate.velocity) <= 0.450001);
            require(abs(gate.velocity - previousSpeed) <= 0.7 * h + 0.001 || gate.velocity == 0);
            previousSpeed = gate.velocity;
            var body = gate.body();
            foreach (var t in stride(-1.6, 1.6, 0.1))
            {
                var p = new Double2(body.position.x, body.position.z) + new Double2(cos(body.heading), -sin(body.heading)) * t;
                require(body.position.y > DirtCourse.height(p.x, p.y) - 0.01);
            }
        }
        near(gate.angle, 100 * Math.PI / 180, 0.0001);
        print($"City exit center {CityExit.center}, sill {description(CityExit.floor)}");
        // Closed gate and either jamb stop all four boosted chassis. Open gate
        // lets them descend and return without an invisible course boundary.
        foreach (var profile in RobotCollisions.profiles)
        {
            foreach (var open in new[] { false, true })
            {
                var p = CityExit.point(0, -0.95);
                var bodies = new[] { new RobotCollisions.Body(position: new Double3(p.x, DirtCourse.height(p.x, p.y), p.y), heading: atan2(CityExit.outward.x, CityExit.outward.y), profile: profile) };
                var door = open ? gate : new CityGate();
                var furthest = -100.0;
                for (var k = 0; k < 480; k++)
                {
                    bodies[0].velocity = new Double3(CityExit.outward.x * 12, 0, CityExit.outward.y * 12);
                    bodies[0].position += bodies[0].velocity * h;
                    bodies[0].position.y = DirtCourse.height(bodies[0].position.x, bodies[0].position.z);
                    _ = RobotCollisions.resolve(bodies, terrain: true, gate: door);
                    furthest = max(furthest, CityExit.local(new Double2(bodies[0].position.x, bodies[0].position.z)).y);
                    require(RobotCollisions.contact(bodies[0], door.body()) == null);
                }
                require(open ? furthest > 8 : furthest < 0);
                if (open)
                {
                    for (var k = 0; k < 480; k++)
                    {
                        bodies[0].velocity = new Double3(-CityExit.outward.x * 12, 0, -CityExit.outward.y * 12);
                        bodies[0].position += bodies[0].velocity * h;
                        bodies[0].position.y = DirtCourse.height(bodies[0].position.x, bodies[0].position.z);
                        _ = RobotCollisions.resolve(bodies, terrain: true, gate: door);
                    }
                    require(CityExit.local(new Double2(bodies[0].position.x, bodies[0].position.z)).y < -0.7);
                }
            }
        }
        // Stop a closing gate in its swing and resume smoothly when clear.
        var blocker = new RobotCollisions.Body(position: gate.body(0.8).position, profile: RobotCollisions.profiles[0]);
        gate.wantsOpen = false;
        for (var k = 0; k < 2000; k++) { gate.advance(h, new[] { blocker }); }
        require(gate.blocked && gate.angle > 0.8);
        require(RobotCollisions.contact(gate.body(), blocker) == null);
        gate.wantsOpen = true;
        for (var k = 0; k < 2400; k++) { gate.advance(h, new[] { blocker }); }
        near(gate.angle, 100 * Math.PI / 180, 0.001);
        gate.wantsOpen = false;
        for (var k = 0; k < 2400; k++) { gate.advance(h, Array.Empty<RobotCollisions.Body>()); }
        near(gate.angle, 0, 0.0001);
        // Grade and continuity through the center driving corridor.
        foreach (var along in new[] { -0.8, 0.0, 0.8 })
        {
            double? last = null;
            foreach (var @out in stride(0.15, 6, 0.01))
            {
                var p = CityExit.point(along, @out); var height = DirtCourse.height(p.x, p.y);
                if (last is double prior) { require(abs(height - prior) < 0.0025); }
                last = height;
            }
        }
        // A collision impulse must not push a racer through a non-gate wall.
        var wallPhase = CityExit.phase + 0.4;
        var before = DirtCourse.point(wallPhase, offset: DirtCourse.fenceOffset - 0.35);
        var beyond = DirtCourse.point(wallPhase, offset: DirtCourse.fenceOffset + 0.1);
        var pushed = new[] { new RobotCollisions.Body(position: new Double3(beyond.x, 0, beyond.z), profile: RobotCollisions.profiles[0]) };
        _ = RobotCollisions.resolve(pushed, terrain: true, gate: gate, previousPositions: new[] { new Double3(before.x, 0, before.z) });
        require(DirtCourse.projection(pushed[0].position.x, pushed[0].position.z).distance < DirtCourse.fenceOffset);
        // PORT: block scope; Swift lets this function-level `body` coexist with the loop's `body` above.
        {
            // Spatial query must include solids straddling bucket boundaries.
            var fixture = new RobotCollisions.Body(position: new Double3(8, 0, 8), profile: new RobotCollisions.Profile(mass: 1, halfWidth: 2, halfDepth: 2, height: 2));
            var city = new CityCollisionWorld(new[] { fixture });
            var body = new RobotCollisions.Body(position: new Double3(5.9, 0, 8), profile: RobotCollisions.profiles[0]);
            require(city.nearby(body).Length == 1);
            // Updating a pedestrian must move its collision proxy, without stale
            // bucket entries or rebuilding the immutable architectural index.
            var pedestrian = new RobotCollisions.Body(position: new Double3(6, 0, 8), profile: new RobotCollisions.Profile(mass: 70, halfWidth: 0.17, halfDepth: 0.17, height: 1.05, round: true));
            var occupied = city.withDynamicBodies(new[] { pedestrian });
            require(occupied.nearby(body).Length == 2);
            pedestrian.position = new Double3(32, 0, 8);
            var moved = city.withDynamicBodies(new[] { pedestrian });
            require(moved.nearby(body).Length == 1);
            require(moved.nearby(pedestrian).Length == 1);
            require(city.nearby(pedestrian).Length == 0);
            // A reverse recovery behind a corner must select the clear earlier leg,
            // not resume lookahead through the building toward the old waypoint.
            var corner = new CityCollisionWorld(new[] { new RobotCollisions.Body(position: new Double3(62, 0, 60), profile: new RobotCollisions.Profile(mass: 100, halfWidth: 0.2, halfDepth: 1, height: 3)) });
            var route = new[] { Double2.zero, Double2.zero, Double2.zero, new Double2(60, 62), new Double2(64, 62), new Double2(64, 60) };
            require(PostRaceEscape.recoveryWaypoint(route, 5, new Double2(60, 60), corner) == 3);
            require(PostRaceEscape.recoveryWaypoint(route.Take(3).Append(new Double2(64, 60)).ToArray(), 3, new Double2(60, 60), corner) == null);
        }
    }
}
