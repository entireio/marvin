using System;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin.Checks;

// Port of Tests/SimulationCoreTests/PostRaceChecks.swift.
public partial struct SimulationTests
{
    public void testCooldownLap()
    {
        var race = new DirtRace(); race.countDown(3);
        void visit(double phase)
        {
            var p = DirtCourse.point(phase); race.advance(p.x, p.z, 0.01);
        }
        for (var i = 1; i <= 3000; i++) { visit((double)i * 6 * Math.PI / 3000); }
        require(race.finished && !race.completedCooldownLap);
        var time = race.elapsed; var laps = race.laps; var progress = race.progress;
        // Oscillating backwards and forwards cannot satisfy the extra lap.
        for (var k = 0; k < 4; k++)
        {
            for (var i = 1; i <= 100; i++) { visit(6 * Math.PI - (double)i * 0.002); }
            for (var i = 1; i <= 100; i++) { visit(6 * Math.PI - 0.2 + (double)i * 0.002); }
        }
        require(!race.completedCooldownLap);
        for (var i = 1; i <= 999; i++) { visit(6 * Math.PI + (double)i * 2 * Math.PI / 1000); }
        require(!race.completedCooldownLap);
        visit(8 * Math.PI + 0.00001);
        require(race.completedCooldownLap);
        equal(race.elapsed, time); equal(race.laps, laps); equal(race.progress, progress);
        // A long sample across the map is not lap progress.
        var teleported = new DirtRace(); teleported.countDown(3);
        for (var i = 1; i <= 3000; i++) { var p = DirtCourse.point((double)i * 6 * Math.PI / 3000); teleported.advance(p.x, p.z, 0.01); }
        var before = teleported.cooldownProgress;
        // PORT: block scope; Swift lets this function-level `p` coexist with the loop's `p`.
        {
            var p = DirtCourse.point(Math.PI); teleported.advance(p.x, p.z, 1);
        }
        near(teleported.cooldownProgress, before, 1e-9);
        print("Cooldown: PASS (full extra lap, reverse rejection, frozen results)");
    }
}
