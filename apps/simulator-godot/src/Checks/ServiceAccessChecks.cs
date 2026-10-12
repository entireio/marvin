using System;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin.Checks;

// Port of Tests/SimulationCoreTests/ServiceAccessChecks.swift.
public partial struct SimulationTests
{
    public void testServiceAccess()
    {
        // Enter and leave along the same visible opening, with every playable chassis.
        for (var step = 0; step <= 40; step++)
        {
            var z = -14.5 + (double)step * 0.1; var x = DirtCourse.serviceEntryX;
            foreach (var heading in new[] { 0.0, Math.PI })
            {
                var move = DirtCourse.resolveMove(x, z, heading);
                require(!move.contact); near(move.x, x, 1e-9); near(move.z, z, 1e-9);
                foreach (var profile in RobotCollisions.profiles)
                {
                    var bodies = new[] { new RobotCollisions.Body(position: new Double3(x, DirtCourse.height(x, z), z), heading: heading, profile: profile) };
                    _ = RobotCollisions.resolve(bodies, terrain: true, betweenRobots: false);
                    near(bodies[0].position.x, x, 1e-9); near(bodies[0].position.z, z, 1e-9);
                }
            }
        }
        // Walk the driving corridor at centimetre resolution. A natural access
        // slope must be continuous and below a 1:4 grade in either direction.
        foreach (var x in new[] { DirtCourse.serviceEntryX - 0.7, DirtCourse.serviceEntryX, DirtCourse.serviceEntryX + 0.7 })
        {
            var previous = DirtCourse.height(x, -12.2);
            for (var i = 1; i <= 400; i++)
            {
                var z = -12.2 + (double)i * 0.01; var h = DirtCourse.height(x, z);
                require(abs(h - previous) < 0.0025);
                previous = h;
            }
            near(previous, -0.025, 0.001);
        }
        // Once through, movement around the repair apron must not snap back to the course.
        foreach (var x in new[] { -9.0, -7.5, -6.0 })
        {
            var p = DirtCourse.projection(x, -8);
            require(p.offset < 0 && p.distance > DirtCourse.fenceOffset + 0.5);
            var move = DirtCourse.resolveMove(x, -8, 0);
            require(!move.contact); near(move.x, x, 1e-9); near(move.z, -8, 1e-9);
        }
        // The opening is only on the inner fence. Adjacent brick wall still collides.
        foreach (var x in new[] { -10.0, -5.0 })
        {
            var phase = DirtCourse.projection(x, -15).phase;
            var p = DirtCourse.point(phase, offset: -DirtCourse.fenceOffset);
            require(DirtCourse.resolveMove(p.x, p.z, 0).contact);
            var fieldSide = DirtCourse.point(phase, offset: -DirtCourse.fenceOffset - 0.08);
            var blocked = DirtCourse.resolveMove(fieldSide.x, fieldSide.z, 0);
            require(blocked.contact);
            require(DirtCourse.projection(blocked.x, blocked.z).distance > DirtCourse.fenceOffset);
            var bodies = new[] { new RobotCollisions.Body(position: new Double3(fieldSide.x, 0, fieldSide.z), profile: RobotCollisions.profiles[3]) };
            _ = RobotCollisions.resolve(bodies, terrain: true, betweenRobots: false);
            require(bodies[0].contacted);
            require(DirtCourse.projection(bodies[0].position.x, bodies[0].position.z).distance > DirtCourse.fenceOffset + DirtCourse.boundaryWallThickness);
        }
        // PORT: block scope; Swift lets this function-level `phase` coexist with the loop's `phase`.
        {
            var phase = DirtCourse.projection(DirtCourse.serviceEntryX, -15).phase;
            var outer = DirtCourse.point(phase, offset: DirtCourse.fenceOffset);
            require(DirtCourse.resolveMove(outer.x, outer.z, 0).contact);
            require(!DirtCourse.serviceAccess(DirtCourse.serviceEntryX + 1.0, -12, clearance: 0.4));
        }
        print("PASS: service opening admits all four chassis in both directions; adjacent and outer fences remain solid");
    }
}
