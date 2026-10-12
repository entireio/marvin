using System;
using System.Collections.Generic;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;
using Body = Marvin.Core.RobotCollisions.Body;

namespace Marvin.Checks;

// Port of Tests/SimulationCoreTests/RobotCollisionChecks.swift.
public partial struct SimulationTests
{
    public void testOptionalRobotCollisions()
    {
        // Three coincident AI racers drive through a stationary Marvin when
        // contacts are off; the same setup must produce contact by default.
        foreach (var enabled in new[] { false, true })
        {
            var player = new Simulation(seed: 0, dirtTrack: true, dirtStartPhase: -0.03);
            var start = new Double2(player.x, player.z);
            var race = new DirtRace(startPhase: -0.03); var physics = new DirtRacePhysics();
            var rivals = Enumerable.Range(0, 3).Select(_ => new DirtOpponent((phase: -0.09, offset: 0.0), laneOffset: 0)).ToArray();
            race.countDown(3);
            var input = new DriveInput(); input.brake = true;
            var passedThrough = new[] { false, false, false };
            for (var k = 0; k < 120; k++)
            {
                physics.advance(input, ref player, ref race, rivals, 1.0 / 60, 1.0 / 60,
                    robotCollisionsEnabled: enabled);
                for (var i = 0; i < rivals.Length; i++)
                {
                    var state = rivals[i].simulation;
                    var a = new RobotCollisions.Body(position: new Double3(player.x, player.groundY, player.z), heading: player.heading, profile: RobotCollisions.profiles[0]);
                    var b = new RobotCollisions.Body(position: new Double3(state.x, state.groundY, state.z), heading: state.heading, profile: RobotCollisions.profiles[i + 1]);
                    if ((RobotCollisions.contact(a, b)?.penetration ?? 0) > 0.1) { passedThrough[i] = true; }
                }
            }
            if (enabled) { require(physics.contactCount > 0); }
            else
            {
                equal(physics.contactCount, 0); require(passedThrough.All(passed => passed));
                equal(new Double2(player.x, player.z), start);
                require(rivals.All(rival => rival.race.progress > race.progress && rival.simulation.distance > 4));
                near(rivals[0].simulation.x, rivals[1].simulation.x, 1e-9);
                near(rivals[1].simulation.z, rivals[2].simulation.z, 1e-9);
            }
            near(race.elapsed, 2, 1e-9);
        }
        // Turning off robot contacts must retain the fence and ground support.
        var point = DirtCourse.point(0, offset: DirtCourse.fenceOffset + 0.2);
        var bodies = new[] { new RobotCollisions.Body(position: new Double3(point.x, -2, point.z), profile: RobotCollisions.profiles[0]) };
        equal(RobotCollisions.resolve(bodies, terrain: true, betweenRobots: false), 0);
        require(DirtCourse.projection(bodies[0].position.x, bodies[0].position.z).distance < DirtCourse.fenceOffset);
        require(bodies[0].position.y >= DirtCourse.height(bodies[0].position.x, bodies[0].position.z));
    }

    public void testCollisionRecovery()
    {
        foreach (var phase in new[] { 0.0, 2.0, 5.0 })
        {
            foreach (var direction in new[] { -1.0, 1.0 })
            {
                var p = DirtCourse.point(phase); var heading = DirtCourse.heading(phase);
                var body = new RobotCollisions.Body(position: new Double3(p.x, 0, p.z), heading: heading + direction * 2.8,
                    angularVelocity: direction * 9, profile: RobotCollisions.profiles[0]);
                var original = body.position;
                var recovery = new CollisionRecovery(); body.contacted = true;
                for (var k = 0; k < 720; k++)
                {
                    recovery.advance(ref body, 1.0 / 240);
                    body.contacted = false; body.heading += body.angularVelocity / 240;
                }
                var error = atan2(sin(body.heading - heading), cos(body.heading - heading));
                require(abs(error) < 0.03); require(abs(body.angularVelocity) < 0.1);
                equal(body.position, original); equal(body.velocity, Double3.zero);
            }
        }
        // PORT: block scope; Swift lets this function-level `recovery` coexist with the loop's `recovery`.
        {
            var untouched = new RobotCollisions.Body(position: Double3.zero, heading: 1, angularVelocity: 2, profile: RobotCollisions.profiles[0]);
            var recovery = new CollisionRecovery(); recovery.advance(ref untouched, 1.0 / 60);
            equal(untouched.angularVelocity, 2);
        }
        equal(DirtStandings.order(new[] { new DirtRace(startPhase: 0), new DirtRace(startPhase: 1), new DirtRace(startPhase: 1) }), new[] { 1, 2, 0 });
    }

    public void testRobotCollisionImpulses()
    {
        var light = new RobotCollisions.Profile(mass: 12, halfWidth: 0.25, halfDepth: 0.25, height: 0.5);
        var heavy = new RobotCollisions.Profile(mass: 60, halfWidth: 0.25, halfDepth: 0.25, height: 0.8);
        static Double3 momentum(Body[] bodies)
        {
            var total = Double3.zero;
            foreach (var body in bodies) { total = total + body.velocity * body.profile.mass; }
            return total;
        }
        static double energy(Body[] bodies)
        {
            var total = 0.0;
            foreach (var b in bodies)
            {
                var inertia = b.profile.round ? b.profile.mass * b.profile.halfWidth * b.profile.halfWidth / 2 : b.profile.mass * (pow(b.profile.halfWidth, 2) + pow(b.profile.halfDepth, 2)) / 3;
                total = total + 0.5 * b.profile.mass * Simd.length_squared(b.velocity) + 0.5 * inertia * b.angularVelocity * b.angularVelocity;
            }
            return total;
        }
        foreach (var profile in new[] { light, heavy })
        {
            var bodies = new[] { new Body(position: new Double3(0, 0, -0.24), velocity: new Double3(0, 0, 6), profile: light), new Body(position: new Double3(0, 0, 0.24), profile: profile) };
            var before = momentum(bodies); var initialEnergy = energy(bodies);
            require(RobotCollisions.resolve(bodies) > 0);
            near(Simd.length(momentum(bodies) - before), 0, 1e-9);
            require(energy(bodies) <= initialEnergy + 1e-8);
            require(bodies[1].velocity.z > bodies[0].velocity.z);
            near(bodies[0].angularVelocity, 0, 1e-9); near(bodies[1].angularVelocity, 0, 1e-9);
            require((RobotCollisions.contact(bodies[0], bodies[1])?.penetration ?? 0) < 0.0003);
            if (profile.mass == heavy.mass) { require(bodies[1].velocity.z < 1.2); }
        }
        // PORT: block scope; Swift lets these function-level names coexist with the loop's.
        {
            // Off-center contact transfers angular momentum and dissipates energy.
            var glance = new[] { new Body(position: new Double3(0.28, 0, -0.24), velocity: new Double3(2, 0, 6), profile: light), new Body(position: new Double3(0, 0, 0.24), profile: heavy) };
            var before = momentum(glance); var initialEnergy = energy(glance);
            RobotCollisions.resolve(glance);
            near(Simd.length(momentum(glance) - before), 0, 1e-8);
            require(energy(glance) <= initialEnergy + 1e-8);
            require(abs(glance[0].angularVelocity) + abs(glance[1].angularVelocity) > 0.1);
            // Separated vertical intervals pass overhead; landing transfers vertical momentum.
            var high = new[] { new Body(position: new Double3(0, 1, 0), velocity: new Double3(0, 0, 8), profile: light), new Body(position: Double3.zero, profile: heavy) };
            equal(RobotCollisions.resolve(high), 0);
            high[0].position.y = 0.79; high[0].velocity = new Double3(0, -2, 0);
            var verticalBefore = momentum(high);
            require(RobotCollisions.resolve(high) > 0);
            near(Simd.length(momentum(high) - verticalBefore), 0, 1e-8);
            require(high[0].position.y >= 0.795);
            // Rotation changes box corners; rounded BB-8 uses circle/box corner distance.
            var rotated = new Body(position: Double3.zero, heading: Math.PI / 4, profile: heavy);
            var bb = new Body(position: new Double3(0.44, 0, 0), profile: RobotCollisions.profiles[2]);
            require(RobotCollisions.contact(rotated, bb) != null);
            require(RobotCollisions.contact(new Body(position: Double3.zero, profile: heavy), new Body(position: new Double3(0.42, 0, 0.42), profile: RobotCollisions.profiles[2])) == null);
            // Initially coincident centers remain finite and are separated deterministically.
            var overlap = new[] { new Body(position: Double3.zero, profile: light), new Body(position: Double3.zero, profile: heavy) };
            RobotCollisions.resolve(overlap);
            require(overlap.All(body => double.IsFinite(body.position.x) && double.IsFinite(body.velocity.z)));
            require((RobotCollisions.contact(overlap[0], overlap[1])?.penetration ?? 0) < 0.0003);
            // Opposing boosted motion cannot tunnel at the shared physics step.
            var fast = new[] { new Body(position: new Double3(0, 0, -1), velocity: new Double3(0, 0, 12), profile: light), new Body(position: new Double3(0, 0, 1), velocity: new Double3(0, 0, -12), profile: heavy) };
            var hits = 0;
            for (var k = 0; k < 120; k++)
            {
                for (var i = 0; i < fast.Length; i++) { fast[i].position += fast[i].velocity / 240; }
                hits += RobotCollisions.resolve(fast);
            }
            require(hits > 0); require(fast[0].position.z < fast[1].position.z);
        }
    }

    public void testCollisionPileupsAndClock()
    {
        var bodies = RobotCollisions.profiles.Select((profile, i) =>
        {
            var p = DirtCourse.point(0, offset: 1.0 + (double)i * 0.32);
            return new RobotCollisions.Body(position: new Double3(p.x, DirtCourse.height(p.x, p.z), p.z), heading: DirtCourse.heading(0), profile: profile);
        }).ToArray();
        for (var k = 0; k < 10; k++) { RobotCollisions.resolve(bodies, terrain: true); }
        for (var i = 0; i < bodies.Length; i++)
        {
            for (var j = 0; j < bodies.Length; j++)
            {
                if (!(j > i)) continue;
                require((RobotCollisions.contact(bodies[i], bodies[j])?.penetration ?? 0) < 0.002);
            }
        }
        require(bodies.All(body => DirtCourse.projection(body.position.x, body.position.z).distance < DirtCourse.fenceOffset && Simd.length(body.velocity) < 1e-8));
        static (Simulation, DirtRace, DirtOpponent[]) setup()
        {
            var slot = DirtCourse.playerGrid;
            var race = new DirtRace(startPhase: slot.phase); race.countDown(3);
            return (new Simulation(seed: 0, dirtTrack: true, dirtStartOffset: slot.offset, dirtStartPhase: slot.phase), race,
                DirtCourse.startingGrid.Skip(1).Select((element, offset) => new DirtOpponent(element, laneOffset: new[] { 0.65, 0, -0.65 }[offset])).ToArray());
        }
        var endings = new List<Simulation[]>();
        foreach (var fps in new[] { 10.0, 30.0, 60.0, 120.0 })
        {
            var (player, race, rivals) = setup(); var world = new DirtRacePhysics(); var input = new DriveInput();
            input.throttle = 1; input.boost = true; input.turn = 0.08;
            for (var k = 0; k < (int)(fps * 4); k++) { world.advance(input, ref player, ref race, rivals, 1 / fps, 1 / fps); }
            endings.Add(new[] { player }.Concat(rivals.Select(rival => rival.simulation)).ToArray());
            var before = player; var clock = race.elapsed;
            world.advance(input, ref player, ref race, rivals, double.NaN, 1);
            world.advance(input, ref player, ref race, rivals, 1, double.PositiveInfinity);
            equal(player.x, before.x); equal(race.elapsed, clock);
        }
        foreach (var states in endings.Skip(1))
        {
            for (var k = 0; k < Math.Min(endings[0].Length, states.Length); k++)
            {
                var a = endings[0][k]; var b = states[k];
                near(a.x, b.x, 1e-7); near(a.z, b.z, 1e-7);
                near(a.heading, b.heading, 1e-7);
                near(Simd.length(a.velocity - b.velocity), 0, 1e-7);
            }
        }
        // PORT: block scope; Swift lets these function-level names coexist with the loop's.
        {
            var (player, race, rivals) = setup(); var world = new DirtRacePhysics(); var input = new DriveInput();
            input.throttle = 1;
            for (var k = 0; k < 30; k++) { world.advance(input, ref player, ref race, rivals, 1.0 / 60, 1.0 / 60); }
            var moving = hypot(player.velocity.x, player.velocity.z);
            input.brake = true;
            world.advance(input, ref player, ref race, rivals, 1.0 / 60, 1.0 / 60);
            require(hypot(player.velocity.x, player.velocity.z) > 0.1);
            require(hypot(player.velocity.x, player.velocity.z) < moving);
        }
    }

    public void testCoupledRacePhysics()
    {
        var totals = new List<double>();
        foreach (var fps in new[] { 30.0, 60.0, 120.0 })
        {
            var slot = DirtCourse.startingGrid[3];
            var player = new Simulation(seed: 0, dirtTrack: true, dirtStartOffset: slot.offset, dirtStartPhase: slot.phase);
            var race = new DirtRace(startPhase: slot.phase); var world = new DirtRacePhysics();
            var rivals = DirtCourse.startingGrid.Take(3).Select((element, offset) => new DirtOpponent(element, laneOffset: new[] { 0.65, 0, -0.65 }[offset])).ToArray();
            var input = new DriveInput(); input.throttle = 1;
            world.advance(input, ref player, ref race, rivals, 1 / fps, 1 / fps);
            equal(player.distance, 0); race.countDown(3);
            for (var k = 0; k < (int)(fps * 180); k++)
            {
                var phase = DirtCourse.phase(player.x, player.z); var target = DirtCourse.point(phase + 0.05, offset: 0.1);
                var desired = atan2(target.x - player.x, target.z - player.z);
                var error = atan2(sin(desired - player.heading), cos(desired - player.heading));
                input.throttle = max(0.15, 1 - abs(error) * 1.5); input.turn = -error * 3; input.boost = abs(error) < 0.08;
                world.advance(input, ref player, ref race, rivals, 1 / fps, 1 / fps);
                var states = new[] { player }.Concat(rivals.Select(rival => rival.simulation)).ToArray();
                require(states.All(state => double.IsFinite(state.x) && double.IsFinite(state.z) && double.IsFinite(state.velocity.x) && DirtCourse.projection(state.x, state.z).distance < DirtCourse.fenceOffset));
                if (race.finished) break;
            }
            print($"Coupled race at {description(fps)} fps: {description(race.elapsed)}, laps {race.laps.Length}, contacts {world.contactCount}");
            require(race.finished); require(world.contactCount > 0);
            totals.Add(race.elapsed);
            if (fps == 60)
            {
                var finishTime = race.elapsed; var laps = race.laps; var traveled = player.distance;
                // Player input is ignored on cooldown; unfinished opponents still finish.
                var hostile = new DriveInput(); hostile.brake = true; hostile.turn = 1; hostile.throttle = -1;
                for (var k = 0; k < 60 * 90; k++)
                {
                    world.advance(hostile, ref player, ref race, rivals, 1 / fps, 1 / fps);
                    if (rivals.All(rival => rival.race.finished) && player.distance > traveled + 10) break;
                }
                require(rivals.All(rival => rival.race.finished));
                require(player.distance > traveled + 10);
                equal(race.elapsed, finishTime); equal(race.laps, laps);
                var distances = rivals.Select(rival => rival.simulation.distance).ToArray(); var times = rivals.Select(rival => rival.race.elapsed).ToArray();
                for (var k = 0; k < 60 * 4; k++) { world.advance(hostile, ref player, ref race, rivals, 1 / fps, 1 / fps); }
                for (var i = 0; i < rivals.Length; i++) { require(rivals[i].simulation.distance > distances[i] + 0.1); equal(rivals[i].race.elapsed, times[i]); }
                var ranks = DirtStandings.order(new[] { race }.Concat(rivals.Select(rival => rival.race)).ToArray());
                var results = new[] { race }.Concat(rivals.Select(rival => rival.race)).ToArray();
                require(ranks.Zip(ranks.Skip(1)).All(pair => results[pair.First].elapsed <= results[pair.Second].elapsed));
            }
            var x = player.x; var time = player.elapsed;
            player.paused = true;
            world.advance(input, ref player, ref race, rivals, 0.1, 0.1);
            equal(player.x, x); equal(player.elapsed, time);
            player.reset(); world = new DirtRacePhysics();
            equal(player.velocity, Double3.zero); equal(player.angularVelocity, 0); equal(world.contactCount, 0);
        }
        // Player steering is sampled per displayed frame, so tolerate a small
        // difference in this interactive-controller test. Fixed-input determinism
        // is covered separately by the integration checks.
        require(maxElement(totals).Value - minElement(totals).Value < 12);
    }
}
