using System;
using System.Collections.Generic;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin.Checks;

// Port of Tests/SimulationCoreTests/NavigationChecks.swift.
public partial struct SimulationTests
{
    public void testNavigationMap()
    {
        require(RaceMapRegion.at(Double2.zero) == RaceMapRegion.course);
        require(RaceMapRegion.at(CityExit.point(0, -1)) == RaceMapRegion.course);
        require(RaceMapRegion.at(CityExit.point(0, 2)) == RaceMapRegion.town);
        require(RaceMapRegion.at(new Double2(100, 30)) == RaceMapRegion.town);
        require(RaceMapRegion.at(new Double2(180, 50)) == RaceMapRegion.dunes);
        var boundary = TownFootprint.radius(0);
        require(RaceMapRegion.at(new Double2(boundary, 0), previous: RaceMapRegion.town) == RaceMapRegion.town);
        require(RaceMapRegion.at(new Double2(boundary, 0), previous: RaceMapRegion.dunes) == RaceMapRegion.dunes);
        require(RaceMapRegion.at(new Double2(boundary - 2, 0), previous: RaceMapRegion.dunes) == RaceMapRegion.town);
        require(RaceMapRegion.at(CityExit.point(0, -1), previous: RaceMapRegion.town) == RaceMapRegion.course);
        var ends = new List<Double3>();
        foreach (var fps in new[] { 30.0, 60, 120 })
        {
            var motion = new OverviewMotion(); motion.reset(new Double3(0, 38, -33), Double3.zero);
            for (var k = 0; k < (int)fps; k++) { motion.advance(new Double3(25, 41, -18), new Double3(25, 3, 15), 1 / fps); }
            ends.Add(motion.eye);
            require(Simd.distance(motion.eye, new Double3(25, 41, -18)) < 0.6);
            near(Simd.distance(motion.eye - motion.aim, new Double3(0, 38, -33)), 0, 1e-9);
        }
        require(Simd.distance(ends[0], ends[2]) < 1e-9);
        print("Navigation: COURSE/TOWN/DUNES both directions, boundary hysteresis, smooth 30/60/120 Hz overview: PASS");
    }
}
