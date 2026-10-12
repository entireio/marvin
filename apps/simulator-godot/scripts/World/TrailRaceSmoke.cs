// Port of Sources/MarvinSimulator/TrailRaceSmoke.swift (an AppController extension).
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

public partial class AppController
{
    /// Drive a complete three-lap race through the actual collision world and
    /// trail renderer, checking both emission and the visible terrain surface.
    public bool checkFullRaceTrails(string at)
    {
        var directory = at;
        reset(null); dirtIntro = null;
        var slot = DirtCourse.startingGrid[3];
        simulation = new Simulation(seed: 0, dirtTrack: true, dirtStartOffset: slot.offset, dirtStartPhase: slot.phase);
        race = new DirtRace(startPhase: slot.phase); race.countDown(dt: 3);
        opponent = new DirtOpponent(slot: DirtCourse.startingGrid[0]);
        bb8Opponent = new DirtOpponent(slot: DirtCourse.startingGrid[1], laneOffset: 0);
        wallEOpponent = new DirtOpponent(slot: DirtCourse.startingGrid[2], laneOffset: -0.65);
        var lane = dirtWorld.scene.rootNode.childNode(withName: "Compacted race surface", recursively: false);
        var buried = new List<Dictionary<string, object>>(); var air = new List<Dictionary<string, object>>();
        int groundedMissing = 0, samples = 0, smallHopFrames = 0, airborneMarks = 0;
        var flights = new List<Dictionary<string, object>>(); Simulation? flightStart = null; double flightMax = 0.0;
        var options = new Dictionary<string, object> { [SCNHitTestOption.backFaceCulling] = false };
        for (int frame = 0; frame < 60 * 240; frame++)
        {
            var before = simulation; int count = dirtWorld.trailCounts[0];
            var input = DirtOpponent.driveInput(simulation, laneOffset: 0.1);
            advanceRacePhysics(input, dt: 1.0 / 60, raceDT: 1.0 / 60);
            dirtWorld.update(simulation, opponent: opponent.simulation, dt: 1.0 / 60, modelScale: robot.modelScale, additional: new[] { bb8Opponent.simulation, wallEOpponent.simulation });
            var s = simulation;
            if (before.hasDirtContact && s.hasDirtContact && hypot(s.x - before.x, s.z - before.z) > 0.07 && dirtWorld.trailCounts[0] == count) { groundedMissing += 1; }
            if (s.airborne && s.hasDirtContact) { smallHopFrames += 1; }
            if (!s.hasDirtContact && dirtWorld.trailCounts[0] != count) { airborneMarks += 1; }
            if (s.airborne)
            {
                if (flightStart == null) { flightStart = before; flightMax = 0; }
                flightMax = max(flightMax, s.groundY - DirtCourse.height(s.x, s.z));
            }
            else if (flightStart is Simulation began)
            {
                flights.Add(new Dictionary<string, object> { ["start"] = began.elapsed, ["duration"] = s.elapsed - began.elapsed, ["distance"] = s.distance - began.distance, ["phase"] = DirtCourse.phase(began.x, began.z), ["maxClearance"] = flightMax });
                flightStart = null;
            }
            if (s.airborne && !before.airborne)
            {
                air.Add(new Dictionary<string, object> { ["time"] = race.elapsed, ["phase"] = DirtCourse.phase(s.x, s.z), ["clearance"] = s.groundY - DirtCourse.height(s.x, s.z) });
            }
            if (frame % 5 == 0 && !s.airborne)
            {
                foreach (var side in new[] { -1.0, 1.0 })
                {
                    double lateral = side * 0.262225 * robot.modelScale, forward = -0.23 * robot.modelScale;
                    double x = s.x + cos(s.heading) * lateral + sin(s.heading) * forward;
                    double z = s.z - sin(s.heading) * lateral + cos(s.heading) * forward;
                    double nominal = DirtCourse.height(x, z) + 0.007;
                    var hits = lane.hitTestWithSegment(new SCNVector3(x, 5, z), new SCNVector3(x, -1, z), options);
                    if (hits.FirstOrDefault() is SCNHitTestResult hit)
                    {
                        samples += 1;
                        double surface = hit.worldCoordinates.y;
                        if (surface > nominal)
                        {
                            buried.Add(new Dictionary<string, object> { ["time"] = race.elapsed, ["phase"] = DirtCourse.phase(x, z), ["x"] = x, ["z"] = z, ["depth"] = surface - nominal });
                        }
                    }
                }
            }
            if (race.finished) break;
        }
        robot.update(simulation); updateOpponents(); cameraMode = 2; updateCamera(snap: true);
        saveSnapshot("full-race-trails.png", directory);
        var report = new Dictionary<string, object> { ["finished"] = race.finished, ["total"] = race.elapsed, ["laps"] = race.laps.ToList(), ["marks"] = dirtWorld.trailCounts.ToList(),
            ["groundedMissingFrames"] = groundedMissing, ["smallHopContactFrames"] = smallHopFrames, ["airborneMarkFrames"] = airborneMarks, ["surfaceSamples"] = samples, ["buriedSamples"] = buried, ["takeoffs"] = air, ["flights"] = flights };
        File.WriteAllText(Path.Combine(directory, "full-race-trails.json"), JSONSerialization.prettyPrintedSortedKeys(report));
        return race.finished && samples > 500 && smallHopFrames > 0 && airborneMarks == 0 && groundedMissing == 0 && buried.Count == 0;
    }
}
