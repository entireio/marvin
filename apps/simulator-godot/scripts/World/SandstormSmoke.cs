using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

/// <summary>
/// `--sandstorm-smoke-test DIR` (SandstormSmoke.swift, AppController.checkSandstorm). Same storm race, capture frame,
/// cameras, files (storm-overview.png, storm-driving.png, storm-drifts.png, storm-spectators.png, optional
/// MARVIN_STORM_MOVIE frames) and sandstorm.json keys. PORT: the town is a stub (no people or colliders), so
/// visiblePeople/normalPopulation are 0 and the population/collider checks cannot pass; robots are not rendered.
/// </summary>
public static class SandstormSmoke
{
    [GameMode("--sandstorm-smoke-test")]
    public static Task Run(string dir, SceneTree tree)
    {
        bool passed = checkSandstorm(new RaceWorldHarness(tree), dir);
        tree.Quit(passed ? 0 : 1);
        return Task.CompletedTask;
    }

    public static bool checkSandstorm(RaceWorldHarness app, string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            app.weatherOverride = true;
            try
            {
                app.startDirtTrack(); app.dirtIntro = null; app.race.countDown(dt: 3);
                app.dirtWorld.sky.apply(new BinaryDaylight(fraction: 0.55, phase: 1.2));
                int population = app.dirtWorld.town.visiblePopulation;
                bool movie = System.Environment.GetEnvironmentVariable("MARVIN_STORM_MOVIE") == "1";
                var renderer = new SCNRenderer(device: null, options: null);
                renderer.scene = app.dirtWorld.scene; renderer.pointOfView = app.camera;
                double floorError = 0.0, maximumDepth = 0.0; bool captured = false;
                for (int frame = 0; frame < 54000; frame++)
                {
                    app.advanceRacePhysics(DirtOpponent.driveInput(app.simulation), dt: 1.0 / 60, raceDT: 1.0 / 60);
                    var states = new[] { app.simulation }.Concat(app.opponents.Select(o => o.simulation));
                    foreach (var state in states)
                    {
                        floorError = max(floorError, state.terrainHeight(state.x, state.z) - state.groundY);
                        maximumDepth = max(maximumDepth, state.storm.depth(state.x, state.z));
                    }
                    // Update effects at 30 Hz during this offline physics test.
                    if (frame % 2 == 0) { app.updateCamera(snap: false); app.updateRaceWorld(dt: 1.0 / 30); }
                    if (movie && frame >= 1800 && frame < 2700 && frame % 2 == 0)
                    {
                        app.cameraMode = 0; app.updateCamera(snap: false);
                        var image = renderer.snapshot(atTime: (double)frame / 60, with: new CGSize(1280, 720), antialiasingMode: SCNAntialiasingMode.multisampling4X);
                        var jpeg = NSBitmapImageRep.data(image.tiffRepresentation).representation(NSBitmapImageFileType.jpeg);
                        File.WriteAllBytes(Path.Combine(directory, Swift.format("frame-%05d.jpg", (frame - 1800) / 2)), jpeg);
                    }
                    if (frame == 7200)
                    {
                        app.cameraMode = 2; app.updateCamera(snap: true);
                        app.saveTownFrame("storm-overview", directory);
                        app.cameraMode = 0; app.updateCamera(snap: true);
                        app.saveTownFrame("storm-driving", directory);
                        var p = Sandstorm.drifts[0].center;
                        app.camera.position = new SCNVector3(p.x + 2, 1.0, p.y - 3);
                        app.camera.look(new SCNVector3(p.x, 0.2, p.y), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                        app.saveTownFrame("storm-drifts", directory);
                        app.camera.position = new SCNVector3(3, 3.5, DirtCourse.point(0).z + 2);
                        app.camera.look(new SCNVector3(0, 1.8, DirtCourse.point(0).z - 5), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                        app.saveTownFrame("storm-spectators", directory);
                        captured = true;
                    }
                    if (frame % 3600 == 0) { GD.Print($"Storm {frame / 60}s laps [{string.Join(", ", new[] { app.race }.Concat(app.opponents.Select(o => o.race)).Select(r => r.laps.Length))}] depth {Swift.description(maximumDepth)}"); }
                    if (captured && new[] { app.race }.Concat(app.opponents.Select(o => o.race)).All(r => r.finished)) break;
                }
                bool finished = new[] { app.race }.Concat(app.opponents.Select(o => o.race)).All(r => r.finished);
                double before = app.racePhysics.storm.elapsed;
                app.simulation.paused = true; app.advanceRacePhysics(new DriveInput(), dt: 1, raceDT: 1);
                bool paused = app.racePhysics.storm.elapsed == before; app.simulation.paused = false;
                var wind = app.racePhysics.storm.wind(0, 0); var direction = Simd.normalize(wind); var profile = RobotCollisions.profiles[0];
                double head = Simd.length(app.racePhysics.storm.acceleration(velocity: -direction * 5, x: 0, z: 0, profile: profile));
                double tail = Simd.length(app.racePhysics.storm.acceleration(velocity: direction * 5, x: 0, z: 0, profile: profile));
                double sheltered = Simd.length(app.racePhysics.storm.acceleration(velocity: Double3.zero, x: 0, z: 0, profile: profile, shelter: 0.25));
                double exposed = Simd.length(app.racePhysics.storm.acceleration(velocity: Double3.zero, x: 0, z: 0, profile: profile));
                int stormBodies = app.dirtWorld.town.collisionWorld.bodies.Length;
                app.weatherOverride = false; app.reset();
                bool restored = app.dirtWorld.town.visiblePopulation > app.dirtWorld.town.population / 2 && !app.racePhysics.storm.enabled && app.racePhysics.storm.elapsed == 0;
                bool absentCollisions = stormBodies < app.dirtWorld.town.collisionWorld.bodies.Length;
                bool passed = finished && captured && paused && restored && absentCollisions && population < 20 && population > 0 && floorError < 0.003 && maximumDepth > 0.025 && head > tail * 1.5 && sheltered < exposed * 0.2;
                var report = new Dictionary<string, object> { ["passed"] = passed, ["allFinished"] = finished, ["visiblePeople"] = population, ["normalPopulation"] = app.dirtWorld.town.population, ["maximumGroundPenetration"] = floorError, ["maximumDrivenDriftDepth"] = maximumDepth, ["pause"] = paused, ["clearReset"] = restored, ["absentPeopleCollidersRemoved"] = absentCollisions, ["headwindAcceleration"] = head, ["tailwindAcceleration"] = tail, ["exposedWindAcceleration"] = exposed, ["shelteredWindAcceleration"] = sheltered };
                RaceWorldHarness.writeJSON(Path.Combine(directory, "sandstorm.json"), report);
                GD.Print($"Sandstorm: {(passed ? "PASS" : "FAIL")} finished {finished} paused {paused} depth {Swift.description(maximumDepth)} penetration {Swift.description(floorError)} head {Swift.description(head)} tail {Swift.description(tail)}");
                return passed;
            }
            finally { app.weatherOverride = null; }
        }
        catch (Exception error) { GD.Print($"Sandstorm check: {error}"); return false; }
    }
}
