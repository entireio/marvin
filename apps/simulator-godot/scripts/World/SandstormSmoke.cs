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
/// MARVIN_STORM_MOVIE frames) and sandstorm.json keys. `--weather-reset-test DIR` is checkWeatherReset: 102 resets
/// through the menu bar's Command-R key equivalent (reset-storm/reset-clear town frames, overlay-*.png, weather-reset.json).
/// </summary>
public partial class AppController
{
    [GameMode("--weather-reset-test")]
    public static async Task RunWeatherResetTest(string dir, SceneTree tree)
    {
        var app = await launchSmoke(tree, dir);
        var passed = app.checkWeatherReset(at: dir);
        exit(passed ? 0 : 1);
    }

    /// Exercise the real Command-R menu dispatch, not a direct reset call.
    public bool checkWeatherReset(string at)
    {
        var directory = at;
        try
        {
            Directory.CreateDirectory(directory);
            weatherOverride = null;
            try
            {
                startDirtTrack(); dirtIntro = null;
                var draws = new List<bool>(); bool handled = true, synchronized = true;
                for (int i = 0; i < 102; i++)
                {
                    // First verify both outcomes deterministically, then use the
                    // production system RNG for 100 independent race restarts.
                    weatherOverride = i < 2 ? i == 0 : null;
                    var @event = NSEvent.keyEvent(NSEvent.EventType.keyDown, NSPoint.zero, NSEvent.ModifierFlags.command,
                        ProcessInfo.processInfo.systemUptime, window.windowNumber,
                        null, "r", "r", false, 15);
                    handled = (NSApp.mainMenu?.performKeyEquivalent(with: @event) ?? false) && handled;
                    var storm = racePhysics.storm.enabled;
                    synchronized = synchronized && dirtWorld.storm.enabled == storm
                        && window.title.Contains("Sandstorm") == storm
                        && (storm ? dirtWorld.town.visiblePopulation < 20 : dirtWorld.town.visiblePopulation > 20);
                    if (i < 2) { synchronized = synchronized && storm == (i == 0); }
                    else { draws.Add(storm); }
                    if (i == 0 || i == 1)
                    {
                        updateOpponents(); updateRaceWorld(dt: 1.0 / 60); updateCamera(snap: true);
                        saveTownFrame(i == 0 ? "reset-storm" : "reset-clear", directory);
                        raceHUD.race = race; raceHUD.paused = false;
                        foreach (var intro in new[] { true, false })
                        {
                            raceHUD.introducing = intro;
                            var image = new NSImage(raceHUD.bounds.size);
                            image.lockFocus(); view.snapshot().draw(raceHUD.bounds);
                            // PORT: AppKit draws raceHUD.draw(_:) through a flipped context; the facade caches the
                            // HUD over a transparent base and draws it over the snapshot (same pixels, RaceFinishSmoke).
                            var overlay = raceHUD.bitmapImageRepForCachingDisplay(raceHUD.bounds);
                            raceHUD.cacheDisplay(raceHUD.bounds, overlay);
                            overlay.draw(raceHUD.bounds);
                            image.unlockFocus();
                            if (NSBitmapImageRep.data(image.tiffRepresentation)?.representation(NSBitmapImageFileType.png) is byte[] png)
                            {
                                File.WriteAllBytes(Path.Combine(directory, $"overlay-{(storm ? "storm" : "clear")}-{(intro ? "intro" : "countdown")}.png"), png);
                            }
                            synchronized = synchronized && raceHUD.stormWarningVisible == storm;
                        }
                        raceHUD.race.countDown(dt: 3);
                        synchronized = synchronized && !raceHUD.stormWarningVisible;
                    }
                }
                var report = new Dictionary<string, object>
                {
                    ["passed"] = handled && synchronized, ["commandRHandled"] = handled,
                    ["weatherSynchronized"] = synchronized, ["randomResets"] = draws.Count,
                    ["storms"] = draws.Count(d => d), ["draws"] = draws,
                };
                File.WriteAllText(Path.Combine(directory, "weather-reset.json"), JSONSerialization.prettyPrintedSortedKeys(report));
                print(JSONSerialization.prettyPrintedSortedKeys(report)); return handled && synchronized;
            }
            finally { weatherOverride = null; }
        }
        catch (Exception error) { print(error.ToString()); return false; }
    }

    [GameMode("--sandstorm-smoke-test")]
    public static async Task RunSandstormSmokeTest(string dir, SceneTree tree)
    {
        var app = await launchSmoke(tree, dir);
        var passed = app.checkSandstorm(at: dir);
        exit(passed ? 0 : 1);
    }

    public bool checkSandstorm(string at)
    {
        var directory = at;
        try
        {
            Directory.CreateDirectory(directory);
            weatherOverride = true;
            try
            {
                startDirtTrack(); dirtIntro = null; race.countDown(dt: 3); raceHUD.isHidden = true;
                dirtWorld.sky.apply(new BinaryDaylight(fraction: 0.55, phase: 1.2));
                int population = dirtWorld.town.visiblePopulation;
                bool movie = System.Environment.GetEnvironmentVariable("MARVIN_STORM_MOVIE") == "1";
                var renderer = new SCNRenderer(device: null, options: null);
                renderer.scene = dirtWorld.scene; renderer.pointOfView = world.camera;
                double floorError = 0.0, maximumDepth = 0.0; bool captured = false;
                for (int frame = 0; frame < 54000; frame++)
                {
                    advanceRacePhysics(DirtOpponent.driveInput(simulation), dt: 1.0 / 60, raceDT: 1.0 / 60);
                    var states = new[] { simulation }.Concat(opponents.Select(o => o.simulation));
                    foreach (var state in states)
                    {
                        floorError = max(floorError, state.terrainHeight(state.x, state.z) - state.groundY);
                        maximumDepth = max(maximumDepth, state.storm.depth(state.x, state.z));
                    }
                    // Update effects at 30 Hz during this offline physics test.
                    if (frame % 2 == 0) { updateCamera(snap: false); updatePlayerModel(); updateOpponents(); updateRaceWorld(dt: 1.0 / 30); }
                    if (movie && frame >= 1800 && frame < 2700 && frame % 2 == 0)
                    {
                        cameraMode = 0; updateCamera(snap: false);
                        var image = renderer.snapshot(atTime: (double)frame / 60, with: new CGSize(1280, 720), antialiasingMode: SCNAntialiasingMode.multisampling4X);
                        var jpeg = NSBitmapImageRep.data(image.tiffRepresentation).representation(NSBitmapImageFileType.jpeg);
                        File.WriteAllBytes(Path.Combine(directory, Swift.format("frame-%05d.jpg", (frame - 1800) / 2)), jpeg);
                    }
                    if (frame == 7200)
                    {
                        cameraMode = 2; updateCamera(snap: true);
                        saveTownFrame("storm-overview", directory);
                        cameraMode = 0; updateCamera(snap: true);
                        saveTownFrame("storm-driving", directory);
                        var p = Sandstorm.drifts[0].center;
                        world.camera.position = new SCNVector3(p.x + 2, 1.0, p.y - 3);
                        world.camera.look(new SCNVector3(p.x, 0.2, p.y), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                        saveTownFrame("storm-drifts", directory);
                        world.camera.position = new SCNVector3(3, 3.5, DirtCourse.point(0).z + 2);
                        world.camera.look(new SCNVector3(0, 1.8, DirtCourse.point(0).z - 5), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                        saveTownFrame("storm-spectators", directory);
                        captured = true;
                    }
                    if (frame % 3600 == 0) { GD.Print($"Storm {frame / 60}s laps [{string.Join(", ", new[] { race }.Concat(opponents.Select(o => o.race)).Select(r => r.laps.Length))}] depth {Swift.description(maximumDepth)}"); }
                    if (captured && new[] { race }.Concat(opponents.Select(o => o.race)).All(r => r.finished)) break;
                }
                bool finished = new[] { race }.Concat(opponents.Select(o => o.race)).All(r => r.finished);
                double before = racePhysics.storm.elapsed;
                simulation.paused = true; advanceRacePhysics(new DriveInput(), dt: 1, raceDT: 1);
                bool paused = racePhysics.storm.elapsed == before; simulation.paused = false;
                var wind = racePhysics.storm.wind(0, 0); var direction = Simd.normalize(wind); var profile = RobotCollisions.profiles[0];
                double head = Simd.length(racePhysics.storm.acceleration(velocity: -direction * 5, x: 0, z: 0, profile: profile));
                double tail = Simd.length(racePhysics.storm.acceleration(velocity: direction * 5, x: 0, z: 0, profile: profile));
                double sheltered = Simd.length(racePhysics.storm.acceleration(velocity: Double3.zero, x: 0, z: 0, profile: profile, shelter: 0.25));
                double exposed = Simd.length(racePhysics.storm.acceleration(velocity: Double3.zero, x: 0, z: 0, profile: profile));
                int stormBodies = dirtWorld.town.collisionWorld.bodies.Length;
                weatherOverride = false; reset(null);
                bool restored = dirtWorld.town.visiblePopulation > dirtWorld.town.population / 2 && !racePhysics.storm.enabled && racePhysics.storm.elapsed == 0;
                bool absentCollisions = stormBodies < dirtWorld.town.collisionWorld.bodies.Length;
                bool passed = finished && captured && paused && restored && absentCollisions && population < 20 && population > 0 && floorError < 0.003 && maximumDepth > 0.025 && head > tail * 1.5 && sheltered < exposed * 0.2;
                var report = new Dictionary<string, object> { ["passed"] = passed, ["allFinished"] = finished, ["visiblePeople"] = population, ["normalPopulation"] = dirtWorld.town.population, ["maximumGroundPenetration"] = floorError, ["maximumDrivenDriftDepth"] = maximumDepth, ["pause"] = paused, ["clearReset"] = restored, ["absentPeopleCollidersRemoved"] = absentCollisions, ["headwindAcceleration"] = head, ["tailwindAcceleration"] = tail, ["exposedWindAcceleration"] = exposed, ["shelteredWindAcceleration"] = sheltered };
                File.WriteAllText(Path.Combine(directory, "sandstorm.json"), JSONSerialization.prettyPrintedSortedKeys(report));
                GD.Print($"Sandstorm: {(passed ? "PASS" : "FAIL")} finished {finished} paused {paused} depth {Swift.description(maximumDepth)} penetration {Swift.description(floorError)} head {Swift.description(head)} tail {Swift.description(tail)}");
                return passed;
            }
            finally { weatherOverride = null; }
        }
        catch (Exception error) { GD.Print($"Sandstorm check: {error}"); return false; }
    }
}
