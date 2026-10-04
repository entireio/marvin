using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

// Port of PeopleSmoke.swift (an AppController extension): the
// native regression of TownResidents, TownStreetResidents and the crowd's GPU gait. 600 simulated seconds
// with the survey daylight (0.48, phase 1.2), captures of street walkers, spectators and walking residents,
// people.json / street-people.json, then camera-cut, pause, storm-shelter and robot-avoidance checks.
// MARVIN_YIELD_RECOVERY_TEST, MARVIN_PEOPLE_OFFSCREEN, MARVIN_PEOPLE_MOVIE and MARVIN_STREET_MOVIE work as on macOS.
public partial class AppController
{
    public bool checkTownPeople(string at)
    {
        var directory = at;
        try
        {
            Directory.CreateDirectory(directory);
            weatherOverride = false;
            try
            {
                startDirtTrack(); dirtIntro = null; raceHUD.isHidden = true; if (window.toolbar != null) { window.toolbar.isVisible = false; }
                dirtWorld.sky.apply(new BinaryDaylight(fraction: 0.48, phase: 1.2));
                if (Environment.GetEnvironmentVariable("MARVIN_YIELD_RECOVERY_TEST") == "1" && dirtWorld.town.residents is TownResidents recovering)
                {
                    var result = recovering.checkYieldCornerRecovery();
                    File.WriteAllText(Path.Combine(directory, "yield-recovery.json"), JSONSerialization.prettyPrintedSortedKeys(result));
                    Godot.GD.Print($"Yield recovery: {JSONSerialization.prettyPrintedSortedKeys(result).Replace("\n", " ")}");
                    return result.TryGetValue("passed", out var passedValue) && passedValue is bool yes && yes;
                }
                if (!(dirtWorld.town.residents is TownResidents residents && residents.walkers.Count >= 6 && residents.connections >= 4)) { return false; }
                if (!(dirtWorld.town.streetResidents is TownStreetResidents street && street.walkers.Count >= 12)) { return false; }
                var streetPositions = new List<List<double>>(); var streetStopped = new double[street.walkers.Count];
                var longestStreetStop = 0.0;
                var offscreen = Environment.GetEnvironmentVariable("MARVIN_PEOPLE_OFFSCREEN") == "1";
                Func<SCNNode, bool> visibility = node => !offscreen && view.isNode(node, insideFrustumOf: world.camera);
                var streetMovie = Environment.GetEnvironmentVariable("MARVIN_STREET_MOVIE") == "1";
                var movie = Environment.GetEnvironmentVariable("MARVIN_PEOPLE_MOVIE") == "1";
                var renderer = new SCNRenderer(view.device, null);
                renderer.scene = dirtWorld.scene; renderer.pointOfView = world.camera;
                var movieFrames = new[] { 0, 0, 0 };
                var names = new[] { "resident-trip", "spectator-reactions", "town-life" };
                if (movie) { foreach (var name in names) { Directory.CreateDirectory(Path.Combine(directory, name)); } }
                if (streetMovie) { Directory.CreateDirectory(Path.Combine(directory, "street-movie")); }
                void writeMovieFrame(string folder, int index, double frame)
                {
                    var image = renderer.snapshot(atTime: frame / 60, with: new CGSize(1280, 720), antialiasingMode: SCNAntialiasingMode.multisampling4X);
                    var tiff = image?.tiffRepresentation; var jpeg = tiff == null ? null : NSBitmapImageRep.data(tiff)?.representation(NSBitmapImageFileType.jpeg);
                    if (jpeg == null) { throw new IOException("fileWriteUnknown"); }
                    File.WriteAllBytes(Path.Combine(directory, folder, string.Format(System.Globalization.CultureInfo.InvariantCulture, "frame-{0:00000}.jpg", index)), jpeg);
                }
                for (var frame = 0; frame < 36000; frame++)
                {
                    var previousStreet = street.walkers.Select(w => w.position).ToList();
                    dirtWorld.town.update(dt: 1.0 / 60, camera: world.camera.position, player: new Double2((double)world.camera.position.x, (double)world.camera.position.z), visible: visibility);
                    for (var i = 0; i < street.walkers.Count; i++)
                    {
                        streetStopped[i] = Simd.distance(previousStreet[i], street.walkers[i].position) < 0.0001 ? streetStopped[i] + 1.0 / 60 : 0;
                        longestStreetStop = max(longestStreetStop, streetStopped[i]);
                    }
                    if (new[] { 120, 480, 900, 1260, 1800, 2160 }.Contains(frame))
                    {
                        var district = frame < 900 ? 0 : (frame < 1800 ? 1 : 2);
                        var w = street.walkers[district * street.walkers.Count / 3]; var p = w.path[w.start];
                        world.camera.position = new SCNVector3(p.x + 4, 2.0, p.y + 5);
                        world.camera.look(at: new SCNVector3(p.x, 0.6, p.y), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                        saveTownFrame($"street-{district}-{frame}", directory);
                        streetPositions.Add(new List<double> { (double)frame, w.position.x, w.position.y });
                    }
                    if (new[] { 0, 180, 360 }.Contains(frame))
                    {
                        var z = DirtCourse.point(0).z;
                        world.camera.position = new SCNVector3(-4.4, 2.6, z - 0.5);
                        world.camera.look(at: new SCNVector3(-4.4, 2.35, z - 4.4), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                        saveTownFrame($"spectators-{frame}", directory);
                    }
                    if (frame % 120 == 0 && frame < 2400 && residents.walkers.FirstOrDefault(w => !w.node.isHidden) is TownResidents.Walker walker)
                    {
                        // PORT: SceneKit stores node positions as Float; the facade keeps doubles.
                        var stored = walker.node.position; var p = new SCNVector3((float)stored.x, (float)stored.y, (float)stored.z);
                        world.camera.position = new SCNVector3(p.x + 1.1, p.y + 1.1, p.z + 2.0);
                        world.camera.look(at: new SCNVector3(p.x, p.y + 0.55, p.z), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                        saveTownFrame($"walking-{frame}", directory);
                    }
                    if (movie && frame % 3 == 0)
                    {
                        var clip = frame < 2400 ? 0 : (frame >= 2400 && frame < 3120 ? 1 : (frame >= 3600 && frame < 4320 ? 2 : -1));
                        if (clip >= 0)
                        {
                            if (clip == 0)
                            {
                                var w = residents.walkers[min(3, residents.walkers.Count - 1)]; var p = w.position;
                                world.camera.position = new SCNVector3(p.x + 1.5, 1.5, p.y + 2.8);
                                world.camera.look(at: new SCNVector3(p.x, 0.55, p.y), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                            } else if (clip == 1)
                            {
                                var z = DirtCourse.point(0).z;
                                world.camera.position = new SCNVector3(-4.4, 2.6, z - 0.5); world.camera.look(at: new SCNVector3(-4.4, 2.35, z - 4.4), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                            } else
                            {
                                world.camera.position = new SCNVector3(0, 38, -33); world.camera.look(at: SCNVector3Zero, up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                            }
                            writeMovieFrame(names[clip], movieFrames[clip], frame);
                            movieFrames[clip] += 1;
                        }
                    }
                    if (streetMovie && frame >= 120 && frame < 600 && frame % 3 == 0)
                    {
                        var w = street.walkers[0]; var p = w.path[w.start];
                        world.camera.position = new SCNVector3(p.x + 4, 2, p.y + 5);
                        world.camera.look(at: new SCNVector3(p.x, 0.6, p.y), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                        writeMovieFrame("street-movie", (frame - 120) / 3, frame);
                    }
                    if (frame % 3600 == 0)
                    {
                        Godot.GD.Print($"Residents {frame / 60}s: entries {residents.entries}, exits {residents.exits}, visits {description(residents.walkers.Select(w => w.visits))}, blocked {description(residents.walkers.Select(w => (int)w.blocked))}");
                        Console.Out.Flush();
                    }
                }
                for (var i = 0; i < street.walkers.Count; i++)
                {
                    if (!(streetStopped[i] > 15)) { continue; }
                    var w = street.walkers[i];
                    Godot.GD.Print($"Stuck street walker {i}: position {w.position}, target {w.target} {w.path[w.target]}, heading {description(w.heading)}, direction {w.direction}, blocked {description(w.blocked)}, wait {description(w.wait)}, endpoints {w.path[0]} {w.path[^1]}");
                    var probe = new RobotCollisions.Body(position: new Double3(w.position.x, 0, w.position.y), profile: new RobotCollisions.Profile(mass: 70, halfWidth: 1, halfDepth: 1, height: 1.05));
                    Godot.GD.Print($"Nearby: [{string.Join(", ", dirtWorld.town.collisionWorld.nearby(probe).Select(b => $"Body(position: {b.position}, heading: {description(b.heading)}, mass: {description(b.profile.mass)})"))}]");
                }
                var streetReport = new Dictionary<string, object> { ["count"] = street.walkers.Count, ["distance"] = street.walkers.Select(w => w.distance).ToList(), ["longestStop"] = longestStreetStop, ["positions"] = streetPositions, ["maximumPenetration"] = street.maximumPenetration, ["poseUpdates"] = street.poseUpdates, ["navigationUpdates"] = street.navigationUpdates };
                File.WriteAllText(Path.Combine(directory, "street-people.json"), JSONSerialization.prettyPrintedSortedKeys(streetReport));
                var streetRatePassed = !offscreen || (street.poseUpdates == 0 && street.navigationUpdates < 36000 * street.walkers.Count / 2);
                var streetPassed = streetRatePassed && street.walkers.All(w => w.distance > 30) && street.maximumPenetration < 0.005 && longestStreetStop < 15;
                Godot.GD.Print($"Street residents: count {street.walkers.Count}, longestStop {description(longestStreetStop)}, maximumPenetration {description(street.maximumPenetration)}, poseUpdates {street.poseUpdates}, navigationUpdates {street.navigationUpdates}, passed {(streetPassed ? "true" : "false")}");
                var report = new Dictionary<string, object>
                {
                    ["offscreen"] = offscreen, ["updateStatistics"] = residents.updateStatistics, ["seconds"] = 600, ["movieFrames"] = movieFrames.ToList(), ["entries"] = residents.entries, ["firstEntries"] = residents.walkers.Select(w => w.firstEntry).ToList(), ["exits"] = residents.exits,
                    ["doors"] = residents.doors.Count, ["routes"] = residents.connections, ["visits"] = residents.walkers.Select(w => w.visits).ToList(), ["distance"] = residents.walkers.Select(w => w.distance).ToList(), ["blocked"] = residents.walkers.Select(w => w.blocked).ToList(),
                    ["waypoints"] = residents.walkers.Select(w => w.waypoint).ToList(), ["paths"] = residents.walkers.Select(w => w.path.Select(p => new List<double> { p.x, p.y }).ToList()).ToList(), ["positions"] = residents.walkers.Select(w => new List<double> { w.position.x, w.position.y }).ToList(), ["maximumPenetration"] = residents.maximumPenetration,
                };
                File.WriteAllText(Path.Combine(directory, "people.json"), JSONSerialization.prettyPrintedSortedKeys(report));
                Godot.GD.Print(JSONSerialization.prettyPrintedSortedKeys(report.Where(pair => pair.Key != "paths").ToDictionary(pair => pair.Key, pair => pair.Value)).Replace("\n", " "));
                world.camera.position = new SCNVector3(0, 38, -33); world.camera.look(at: SCNVector3Zero, up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                saveTownFrame("town-people-overhead", directory);
                var moving = residents.walkers.All(w => w.visits > 0 && w.distance > 20) && residents.maximumPenetration < 0.005;
                var ratePassed = !offscreen || (residents.poseUpdates == 0 && residents.navigationUpdates < 36000 * residents.walkers.Count / 2 && residents.coarseUpdates > 0);
                // A camera cut must wake poses on the next update, without resetting
                // distance-driven gait or teleporting the resident to catch up.
                var previous = residents.walkers.Select(w => w.position).ToList(); var poses = residents.poseUpdates;
                residents.update(dt: 1.0 / 60, robots: new List<RobotCollisions.Body>(), visible: _ => true);
                var woke = residents.poseUpdates > poses && previous.Zip(residents.walkers).All(pair => Simd.distance(pair.First, pair.Second.position) < 0.1);
                var phasePassed = residents.walkers.Where(w => !w.indoors).All(w =>
                {
                    if (!(w.materials.FirstOrDefault()?.value("walkCycle") is double cycle)) { return false; }
                    return abs(cycle - w.distance / 0.24 * Math.PI) < 0.001;
                });
                var positions = residents.walkers.Select(w => w.position).ToList();
                dirtWorld.town.update(dt: 0, camera: world.camera.position, player: Double2.zero);
                var paused = positions.SequenceEqual(residents.walkers.Select(w => w.position));
                dirtWorld.town.setStorm(true);
                for (var k = 0; k < 1200; k++) { dirtWorld.town.update(dt: 1.0 / 60, camera: world.camera.position, player: new Double2((double)world.camera.position.x, (double)world.camera.position.z), visible: visibility); }
                var sheltered = residents.visible <= 2 && dirtWorld.town.visiblePopulation < 30;
                dirtWorld.town.setStorm(false); dirtWorld.town.reset();
                street.reset();
                var pedestrian = street.walkers[0]; var block = pedestrian.path[min(pedestrian.path.Count - 1, pedestrian.start + 20)];
                var parkedRobot = new RobotCollisions.Body(position: new Double3(block.x, 0, block.y), profile: new RobotCollisions.Profile(mass: 30, halfWidth: 0.45, halfDepth: 0.45, height: 0.8));
                for (var k = 0; k < 1800; k++) { street.update(dt: 1.0 / 60, obstacles: new List<RobotCollisions.Body> { parkedRobot }, visible: _ => true); }
                var avoidedRobot = street.maximumPenetration < 0.005 && pedestrian.distance > 5;
                Godot.GD.Print($"Street robot avoidance: {(avoidedRobot ? "true" : "false")}, distance {description(pedestrian.distance)}, penetration {description(street.maximumPenetration)}");
                return avoidedRobot && streetPassed && moving && ratePassed && woke && phasePassed && paused && sheltered && residents.walkers.All(w => w.node.isHidden && w.distance == 0);
            }
            finally { weatherOverride = null; }
        }
        catch (Exception error) { Godot.GD.Print(error.ToString()); return false; }
    }

    [GameMode("--people-smoke-test")]
    public static async System.Threading.Tasks.Task RunPeopleSmokeTest(string dir, Godot.SceneTree tree)
    {
        var app = await launchSmoke(tree, dir);
        var passed = app.checkTownPeople(at: dir);
        print($"People smoke: {(passed ? "PASS" : "FAIL")} · {dir}");
        exit(passed ? 0 : 1);
    }
}
