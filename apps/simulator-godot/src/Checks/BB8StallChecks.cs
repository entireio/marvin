using System;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin.Checks;

// Port of Tests/SimulationCoreTests/BB8StallChecks.swift.
public partial struct SimulationTests
{
    public void testStormLanding()
    {
        // Captured failure: BB-8 descended at 4.3 m/s while carrying 10.7 m/s
        // horizontally. The old slope-velocity assignment launched it again.
        foreach (var normal in new[] { new Double3(0, 1, 0), new Double3(-0.45, 1, 0), new Double3(0.3, 1, -0.5) })
        {
            var n = Simd.normalize(normal);
            foreach (var incoming in new[] { new Double3(10.7, -4.3, 1.5), new Double3(0, -8, 0), n * 3 })
            {
                var landed = RobotCollisions.landingVelocity(incoming, normal);
                require(Simd.length_squared(landed) <= Simd.length_squared(incoming) + 1e-10);
                require(Simd.dot(landed, n) >= (-1e-10));
                var tangent = incoming - n * Simd.dot(incoming, n);
                require(Simd.length((landed - n * Simd.dot(landed, n)) - tangent) < 1e-10);
                if (Simd.dot(incoming, n) > 0) { require(Simd.length(landed - incoming) < 1e-10); }
            }
        }
        var boosts = 0; var slowdowns = 0;
        for (var i = 0; i < 120; i++)
        {
            var phase = (double)i * 2 * Math.PI / 120;
            var clear = new Simulation(seed: 0, dirtTrack: true, dirtStartPhase: phase);
            if (DirtOpponent.driveInput(clear).boost) { boosts += 1; }
            var early = clear; early.storm = new Sandstorm(enabled: true);
            var late = early; late.storm.advance(180);
            var a = DirtOpponent.driveInput(early); var b = DirtOpponent.driveInput(late);
            require(!a.boost && !b.boost && b.throttle <= 5.2 / 6 + 1e-10);
            require(b.throttle <= a.throttle + 1e-10);
            if (b.throttle < a.throttle - 0.01) { slowdowns += 1; }
        }
        require(boosts > 0 && slowdowns > 0);
        print("PASS: terrain landings do not add kinetic energy; storm AI previews accumulating drifts; clear-weather boost remains available");
    }
}
