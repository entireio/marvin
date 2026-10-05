// Port of Sources/MarvinSimulator/BB8StormSmoke.swift (an AppController extension).
//
// `--storm-race-test DIR`: a storm race for each of the 24 starting-grid permutations, driven by the race controller
// with the AI drive input, must finish for all four racers without BB-8 leaving the course through the city exit
// before the post-race escape. Same files as macOS: storm-grids.json on success; escape.json and
// premature-exit.png when BB-8 escapes early.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

public partial class AppController
{
    [GameMode("--storm-race-test")]
    public static async Task RunStormRaceTest(string dir, Godot.SceneTree tree)
    {
        var app = await launchSmoke(tree, dir);
        var passed = app.checkStormRace(at: dir);
        exit(passed ? 0 : 1);
    }

    public bool checkStormRace(string at)
    {
        var directory = at;
        try
        {
            Directory.CreateDirectory(directory);
            weatherOverride = true;
            try
            {
                startDirtTrack(); dirtIntro = null; raceHUD.isHidden = true;
                var results = new List<Dictionary<string, object>>();
                for (int a = 0; a < 4; a++)
                {
                    for (int b = 0; b < 4; b++)
                    {
                        if (b == a) { continue; }
                        for (int c = 0; c < 4; c++)
                        {
                            if (c == a || c == b) { continue; }
                            int d = Enumerable.Range(0, 4).First(x => x != a && x != b && x != c); var order = new[] { a, b, c, d };
                            var slots = order.Select(i => DirtCourse.startingGrid[i]).ToArray();
                            simulation = new Simulation(seed: 0, dirtTrack: true, dirtStartOffset: slots[0].offset, dirtStartPhase: slots[0].phase, character: playerCharacter);
                            opponent = new DirtOpponent(slot: slots[1]); bb8Opponent = new DirtOpponent(slot: slots[2], laneOffset: 0); wallEOpponent = new DirtOpponent(slot: slots[3], laneOffset: -0.65);
                            race = new DirtRace(startPhase: slots[0].phase); race.countDown(dt: 3);
                            racePhysics = new DirtRacePhysics(characters: lineup, townRoutes: dirtWorld.escapeRoutes);
                            racePhysics.storm = new Sandstorm(enabled: true); dirtWorld.town.reset();
                            var history = new List<Dictionary<string, object>>(); var finished = false;
                            for (int frame = 0; frame < 18000; frame++)
                            {
                                advanceRacePhysics(DirtOpponent.driveInput(simulation), dt: 1.0 / 60, raceDT: 1.0 / 60);
                                var bb = bb8Opponent.simulation; var q = CityExit.local(new Double2(bb.x, bb.z));
                                history.Add(new Dictionary<string, object> { ["seconds"] = (double)frame / 60, ["x"] = bb.x, ["z"] = bb.z, ["y"] = bb.groundY, ["vx"] = bb.velocity.x, ["vy"] = bb.velocity.y, ["vz"] = bb.velocity.z, ["along"] = q.x, ["out"] = q.y, ["heading"] = bb.heading, ["contact"] = bb.contacting, ["shelter"] = bb.windShelter });
                                if (history.Count > 120) { history.RemoveAt(0); }
                                var p = DirtCourse.projection(bb.x, bb.z);
                                if (!racePhysics.escape.active && p.offset > 0 && p.distance > DirtCourse.fenceOffset + 0.75)
                                {
                                    var report = new Dictionary<string, object> { ["grid"] = order, ["lineup"] = lineup.Select(character => (int)character).ToArray(), ["gateAngle"] = racePhysics.gate.angle, ["history"] = history };
                                    File.WriteAllText(Path.Combine(directory, "escape.json"), JSONSerialization.prettyPrintedSortedKeys(report));
                                    // PORT: Swift prints `history.suffix(12)` with its Dictionary description (unordered keys);
                                    // the same entries are printed here as JSON.
                                    print($"BB8 ESCAPED grid {Swift.description(order.Select(i => (int)i))} at {Swift.description((double)frame / 60)}, gate {Swift.description(racePhysics.gate.angle)}, last {JSONSerialization.prettyPrintedSortedKeys(history.Skip(Math.Max(0, history.Count - 12)).ToList())}"); Console.Out.Flush();
                                    updateOpponents(); updateRaceWorld(dt: 1.0 / 60);
                                    world.camera.position = new SCNVector3(bb.x + 3, bb.groundY + 2, bb.z - 2); world.camera.look(at: new SCNVector3(bb.x, bb.groundY + 0.2, bb.z), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                                    saveTownFrame("premature-exit", directory);
                                    return false;
                                }
                                if (race.finished && opponents.All(o => o.race.finished))
                                {
                                    finished = true;
                                    results.Add(new Dictionary<string, object> { ["grid"] = order, ["seconds"] = (double)frame / 60, ["laps"] = new[] { race }.Concat(opponents.Select(o => o.race)).Select(r => r.laps.Length).ToArray() });
                                    print($"Storm grid {Swift.description(order.Select(i => (int)i))} finished at {Swift.description((double)frame / 60)}"); Console.Out.Flush();
                                    break;
                                }
                            }
                            if (!finished) { print($"Storm grid {Swift.description(order.Select(i => (int)i))} TIMEOUT: {Swift.description(new[] { race }.Concat(opponents.Select(o => o.race)).Select(r => r.laps.Length))}"); return false; }
                        }
                    }
                }
                File.WriteAllText(Path.Combine(directory, "storm-grids.json"), JSONSerialization.prettyPrintedSortedKeys(new Dictionary<string, object> { ["passed"] = true, ["grids"] = results }));
                return true;
            }
            finally { weatherOverride = null; }
        }
        catch (Exception error) { print(error.ToString()); return false; }
    }
}
