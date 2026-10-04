using System;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin.Checks;

// Port of Tests/SimulationCoreTests/SandDeformationChecks.swift.
public partial struct SimulationTests
{
    public void testSandDeformation()
    {
        var field = new SandDeformation();
        var p = DirtCourse.projection(211, 71);
        var state = new Simulation(dirtTrack: true, dirtStartOffset: p.offset, dirtStartPhase: p.phase, character: RacePerformance.Character.wallE);
        var physics = new DirtRacePhysics(characters: new[] { RacePerformance.Character.wallE, RacePerformance.Character.marvin, RacePerformance.Character.r2d2, RacePerformance.Character.bb8 }); physics.sand = field;
        var race = new DirtRace(); race.countDown(3);
        var rivals = Enumerable.Range(1, 3).Select(i => new DirtOpponent(DirtCourse.startingGrid[i])).ToArray();
        var contacts = new[] { new SandDeformation.Contact(x: 0.30, z: 0, width: 0.196, length: 0.44), new SandDeformation.Contact(x: -0.30, z: 0, width: 0.196, length: 0.44) };
        field.contactLayouts[3] = contacts;
        var lowest = 0.0; var highest = 0.0;
        for (var frame = 0; frame < 900; frame++)
        {
            var input = new DriveInput();
            if (frame < 180)
            {
                var error = atan2(sin(0.72 - state.heading), cos(0.72 - state.heading));
                input.turn = max(-1, min(1, -error * 2.5));
            }
            else if (frame < 660) { input.throttle = 0.6; }
            else { input.brake = true; }
            physics.advance(input, ref state, ref race, rivals, 1.0 / 60, 1.0 / 60, robotCollisionsEnabled: false);
            if (frame % 2 == 0)
            {
                field.begin(1.0 / 30, new[] { new Double2(state.x, state.z) });
                field.stamp(state, contacts, 1.0 / 30); field.settle(1.0 / 30);
            }
            require(double.IsFinite(state.groundY) && double.IsFinite(state.bodyRoll));
            require(state.groundY >= state.supportHeight - 0.015);
        }
        foreach (var tile in field.tiles.Values)
        {
            foreach (var value in tile.delta)
            {
                // Swift compares the Float with Float literals.
                require(float.IsFinite(value) && value > -0.14f && value < 0.11f);
                lowest = min(lowest, (double)value); highest = max(highest, (double)value);
            }
        }
        require(lowest < -0.01 && highest > 0.005 && field.displacedVolume > 0);
        // The CPU sampler uses exactly the rendered triangle diagonal. Shared
        // tile edges must be continuous even when a footprint crosses them.
        foreach (var key in field.tiles.Keys)
        {
            for (var j = 0; j < 64; j += 7)
            {
                int ix = key.x * 64 + 63, iz = key.z * 64 + j; double u = 0.37, v = 0.76;
                double a = (double)field.vertex(ix, iz), b = (double)field.vertex(ix + 1, iz), c = (double)field.vertex(ix, iz + 1), d = (double)field.vertex(ix + 1, iz + 1);
                near(field.offset(((double)ix + u) * SandDeformation.step, ((double)iz + v) * SandDeformation.step), d + (c - d) * (1 - u) + (b - d) * (1 - v), 1e-7);
                var x = (double)(key.x + 1) * 4; var z = (double)iz * SandDeformation.step;
                near(field.offset(x - 1e-7, z), field.offset(x + 1e-7, z), 0.000001);
                _ = a;
            }
        }
        // Snapshot uploaded to Metal matches the collision sampler, including
        // neighboring tiles used for boundary normals.
        foreach (var key in field.tiles.Keys)
        {
            var grid = field.grid(key);
            for (var j = -1; j <= 65; j += 3)
            {
                for (var i = -1; i <= 65; i += 3)
                {
                    near((double)grid[(j + 1) * 67 + i + 1], (double)field.vertex(key.x * 64 + i, key.z * 64 + j), 0);
                }
            }
        }
        var orientation = state.duneOrientation;
        var up = orientation.act(new Double3(0, 1, 0)); var forward = orientation.act(new Double3(0, 0, 1)); var right = orientation.act(new Double3(1, 0, 0));
        near(Simd.length(up), 1, 1e-9);
        near(Simd.dot(up, forward), 0, 1e-9);
        near(Simd.dot(up, right), 0, 1e-9);
        // Drive the cache beyond capacity while protecting the original robot.
        // Eviction must never remove the terrain supporting a current racer.
        var protectedPoint = new Double2(state.x, state.z); var protectedKey = SandDeformation.key(state.x, state.z);
        require(field.tiles.ContainsKey(protectedKey));
        for (var i = 0; i < 180; i++)
        {
            // PORT: Swift shadows the function-level `p` here; C# needs a distinct name.
            var visitorProjection = DirtCourse.projection((i % 2 == 0 ? 1.0 : -1.0) * (220 + (double)(i % 40) * 10), 80 + (double)(i / 40) * 12);
            var visitor = new Simulation(dirtTrack: true, dirtStartOffset: visitorProjection.offset, dirtStartPhase: visitorProjection.phase, character: RacePerformance.Character.wallE);
            visitor.sand = field;
            field.begin(1.0 / 30, new[] { protectedPoint, new Double2(visitor.x, visitor.z) });
            field.stamp(visitor, contacts, 1.0 / 30);
            require(field.tiles.Count <= SandDeformation.capacity && field.tiles.ContainsKey(protectedKey));
        }
        print($"Sand cache: {field.tiles.Count} tiles, {field.topologyVersion} allocations");
        require(field.tiles.Count == SandDeformation.capacity && field.topologyVersion > SandDeformation.capacity * 2);
        // No modifications to race/town soil; resetting removes the shared data.
        near(field.offset(0, 0), 0, 0);
        require(field.tiles.Count <= SandDeformation.capacity);
        field.reset(); require(field.tiles.Count == 0); near(field.offset(state.x, state.z), 0, 0);
        print($"Dune deformation: PASS, cut {description(lowest)}m, bank {description(highest)}m; contact, GPU snapshots, seams, cache eviction and reset");
    }
}
