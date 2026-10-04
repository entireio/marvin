using System;
using System.Collections.Generic;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin.Checks;

// Port of Tests/SimulationCoreTests/SandstormChecks.swift.
public partial struct SimulationTests
{
    // PORT: Swift declares this generator inside testSandstorm; C# has no local types.
    private struct SandstormGenerator : RandomNumberGenerator
    {
        public ulong state;
        public SandstormGenerator() { state = 918273; }
        public ulong next()
        {
            unchecked
            {
                state += 0x9e3779b97f4a7c15;
                var z = state;
                z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9;
                z = (z ^ (z >> 27)) * 0x94d049bb133111eb;
                return z ^ (z >> 31);
            }
        }
    }
    public void testSandstorm()
    {
        // PORT: block scope; Swift lets the loop's `storm` coexist with the function-level `storm` below.
        {
            var random = new SandstormGenerator(); var storms = 0; var consecutive = false; var previous = false;
            for (var k = 0; k < 10000; k++)
            {
                var storm = Sandstorm.drawForRace(ref random);
                if (storm) { storms += 1; } consecutive = consecutive || (previous && storm); previous = storm;
            }
            require(900 <= storms && storms <= 1100); require(consecutive);
            print($"Weather draws: {storms}/10000 storms; independent draws permit consecutive storms");
        }
        {
            var clear = new Sandstorm(); var storm = new Sandstorm(enabled: true);
            clear.advance(300); equal(clear.elapsed, 0); equal(clear.wind(0, 0), Double3.zero);
            var p = Sandstorm.drifts[0].center; var early = storm.depth(p.x, p.y);
            storm.advance(180); require(storm.depth(p.x, p.y) > early * 4);
            var maximumError = 0.0;
            // Compare the actual rendered 10 cm triangle interpolation to the
            // analytic collision surface at interior samples, including edges.
            foreach (var drift in Sandstorm.drifts)
            {
                for (var iz = -12; iz <= 12; iz++)
                {
                    for (var ix = -12; ix <= 12; ix++)
                    {
                        var x = drift.center.x + (double)ix * 0.13; var z = drift.center.y + (double)iz * 0.13;
                        var gx = floor(x / 0.10) * 0.10; var gz = floor(z / 0.10) * 0.10; var u = (x - gx) / 0.10; var v = (z - gz) / 0.10;
                        var a = storm.depth(gx, gz); var b = storm.depth(gx + 0.10, gz); var c = storm.depth(gx + 0.10, gz + 0.10); var d = storm.depth(gx, gz + 0.10);
                        var mesh = u >= v ? a * (1 - u) + b * (u - v) + c * v : a * (1 - v) + c * u + d * (v - u);
                        maximumError = max(maximumError, abs(mesh - storm.depth(x, z)));
                        equal(clear.height(x, z), DirtCourse.height(x, z));
                    }
                }
            }
            print($"Maximum drift interpolation error: {description(maximumError)}");
            require(maximumError < 0.005);
            var results = new List<Double3>();
            foreach (var fps in new[] { 30, 60, 120 })
            {
                var slot = DirtCourse.startingGrid[0];
                var player = new Simulation(seed: 0, dirtTrack: true, dirtStartOffset: slot.offset, dirtStartPhase: slot.phase);
                var race = new DirtRace(startPhase: slot.phase); var world = new DirtRacePhysics();
                var rivals = DirtCourse.startingGrid.Skip(1).Select(element => new DirtOpponent(element)).ToArray();
                world.storm = new Sandstorm(enabled: true); race.countDown(3);
                var input = new DriveInput(); input.throttle = 0.6;
                for (var k = 0; k < fps * 8; k++) { world.advance(input, ref player, ref race, rivals, 1 / (double)fps, 1 / (double)fps); }
                near(world.storm.elapsed, 8, 1e-8);
                results.Add(new Double3(player.x, player.z, player.heading));
            }
            foreach (var result in results) { require(Simd.length(result - results[0]) < 0.00001); }
            print($"Storm checks: rendered/collision depth error {description(maximumError)}m; identical 30/60/120 Hz physics");
        }
    }
}
