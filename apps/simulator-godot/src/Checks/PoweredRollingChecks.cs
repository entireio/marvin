using System;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin.Checks;

// Port of Tests/SimulationCoreTests/PoweredRollingChecks.swift.
public partial struct SimulationTests
{
    public void testPoweredRolling()
    {
        foreach (var character in RacePerformance.CharacterAllCases)
        {
            var input = new DriveInput(); input.throttle = 1;
            var sandbox = new Simulation(seed: 0, character: character);
            for (var k = 0; k < 600; k++) { sandbox.advance(input, 1.0 / 60); }
            require(sandbox.contacting);
            var blocked = sandbox;
            sandbox.advance(input, 0.1);
            near(sandbox.x, blocked.x, 1e-9); near(sandbox.z, blocked.z, 1e-9);
            require(sandbox.leftTravel > blocked.leftTravel && sandbox.rightTravel > blocked.rightTravel);
            require(sandbox.rollingTravel.y > blocked.rollingTravel.y);
            input.brake = true;
            var braking = sandbox;
            sandbox.advance(input, 0.1);
            equal(sandbox.leftTravel, braking.leftTravel); equal(sandbox.rollingTravel, braking.rollingTravel);
            sandbox.reset(); equal(sandbox.rollingTravel, Double2.zero); equal(sandbox.leftTravel, 0);
        }
        // PORT: block scope; Swift lets this function-level `input` coexist with the loop's `input`.
        {
            var input = new DriveInput(); input.throttle = 1;
            var fence = new Simulation(seed: 0, dirtTrack: true, dirtStartPhase: 0.6);
            var fenceSamples = 0;
            for (var k = 0; k < 600; k++)
            {
                var before = fence;
                fence.advance(input, 1.0 / 60);
                if (fence.contacting)
                {
                    fenceSamples += 1;
                    require(fence.leftTravel > before.leftTravel && fence.rightTravel > before.rightTravel);
                    var delta = fence.rollingTravel - before.rollingTravel;
                    require(hypot(delta.x, delta.y) > 0);
                }
            }
            require(fenceSamples > 30);
            var jumper = new DirtOpponent(); var airborneSamples = 0;
            for (var k = 0; k < 600; k++)
            {
                var before = jumper.simulation;
                jumper.advance(1.0 / 60, 1.0 / 60);
                var after = jumper.simulation;
                if (before.airborne && after.airborne)
                {
                    airborneSamples += 1;
                    require(after.leftTravel != before.leftTravel && after.rightTravel != before.rightTravel);
                    var delta = after.rollingTravel - before.rollingTravel;
                    require(hypot(delta.x, delta.y) > 0);
                }
            }
            require(airborneSamples > 10);
        }
        print("PASS: powered rolling at sandbox barriers, fences, and during jumps; brake/reset preserved");
    }
}
