using System;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;
using Actor = Marvin.Core.RacePerformance.Actor;

namespace Marvin.Checks;

// Port of Tests/SimulationCoreTests/TownDrivingChecks.swift.
public partial struct SimulationTests
{
    public void testTownSpeedAndGaze()
    {
        foreach (var point in new[] { new Double2(90, 40), new Double2(210, 65) })
        {
            var p = DirtCourse.projection(point.x, point.y);
            var sim = new Simulation(seed: 0, dirtTrack: true, dirtStartOffset: p.offset, dirtStartPhase: p.phase);
            var world = new DirtRacePhysics(); var race = new DirtRace(); race.countDown(3);
            var opponents = DirtCourse.startingGrid.Skip(1).Select(slot => new DirtOpponent(slot)).ToArray();
            var input = new DriveInput(); input.throttle = 1;
            for (var k = 0; k < 240 * 3; k++) { world.advance(input, ref sim, ref race, opponents, 1.0 / 240, 1.0 / 240, robotCollisionsEnabled: false); }
            require(sim.speed > 5.9); require(hypot(sim.velocity.x, sim.velocity.z) > 4);
        }
        foreach (var character in RacePerformance.CharacterAllCases)
        {
            var performance = new RacePerformance(character);
            for (var i = 1; i <= 120; i++)
            {
                performance.update(0, new[] { new Actor(x: 35, z: 12, heading: 1.4, speed: 5, elapsed: (double)i / 60) });
            }
            near(performance.pose.yaw, 0, 0.001);
            var p = DirtCourse.point(CityExit.phase);
            for (var i = 121; i <= 240; i++)
            {
                performance.update(0, new[] { new Actor(x: p.x, z: p.z, heading: DirtCourse.heading(CityExit.phase), speed: 3, elapsed: (double)i / 60) }, followingCourse: false);
            }
            near(performance.pose.yaw, 0, 0.001);
        }
        print("PASS: full drivetrain speed on town/dune sand; neutral curve gaze off-track and during gate departure");
    }
}
