using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

// Port of PostRaceSmoke.swift (an AppController extension, `--postrace-smoke-test DIR`). As on macOS:
// MARVIN_SANDSTORM=1 races in a storm, MARVIN_DAYLIGHT_FRACTION applies BinaryDaylight(fraction, phase 1.2) after the
// start, MARVIN_ROAM_MOVIE=1 also renders the departure and minute-eleven excerpts (JPEG frames). The starting grid is
// the random one of startDirtTrack()'s reset (pin it with MARVIN_GRID_SLOTS, TestPins), so the race outcome, the
// escape routes and every roaming number depend on it.
public partial class AppController
{
    public bool checkPostRaceEscape(string at)
    {
        var directory = at;
        try
        {
            Directory.CreateDirectory(directory);
            weatherOverride = Environment.GetEnvironmentVariable("MARVIN_SANDSTORM") == "1";
            try
            {
                startDirtTrack(); dirtIntro = null; race.countDown(dt: 3);
                if (Environment.GetEnvironmentVariable("MARVIN_DAYLIGHT_FRACTION") is string value && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fraction) && double.IsFinite(fraction))
                {
                    dirtWorld.sky.apply(new BinaryDaylight(fraction: fraction, phase: 1.2));
                }
                if (!(dirtWorld.escapeRoutes.Length == 4)) { print("FAIL: town circuit planning"); return false; }
                var frozen = new double?[4]; bool early = false; double? firstOpen = null; var pauseChecked = false;
                double maxPenetration = 0.0; var cameraLocked = true;
                var peakResidentBlocked = 0.0;
                var photographed = new HashSet<int>(); int? activeStart = null, allDepartedStart = null;
                double roamingSeconds = 0.0, secondsAfterAllDeparted = 0.0;
                var visibleWindows = Enumerable.Range(0, 12).Select(_ => new HashSet<int>()).ToList();
                double[] stationary = new double[4], maxStationary = new double[4];
                var lastPositions = Enumerable.Repeat(Double2.zero, 4).ToArray(); var hudHidden = true;
                double[] travelled = new double[4], lateDistance = new double[4];
                var lateCells = Enumerable.Range(0, 4).Select(_ => new HashSet<(long, long)>()).ToArray();
                var minuteCells = Enumerable.Range(0, 12).Select(_ => Enumerable.Range(0, 4).Select(_ => new HashSet<(long, long)>()).ToList()).ToList();
                var capture = Environment.GetEnvironmentVariable("MARVIN_ROAM_MOVIE") == "1";
                var renderer = new SCNRenderer(view.device, null);
                renderer.scene = dirtWorld.scene; renderer.pointOfView = world.camera;
                var movieFrames = new[] { 0, 0 };
                if (capture)
                {
                    foreach (var name in new[] { "departure", "minute-eleven" }) { Directory.CreateDirectory(Path.Combine(directory, name)); }
                }
                for (var frame = 0; frame < 108000; frame++)
                {
                    advanceRacePhysics(DirtOpponent.driveInput(simulation), dt: 1.0 / 60, raceDT: 1.0 / 60);
                    dirtWorld.town.update(dt: 1.0 / 60, camera: world.camera.position, player: new Double2(simulation.x, simulation.z), robots: robotBodies(), visible: node => view.isNode(node, insideFrustumOf: world.camera));
                    peakResidentBlocked = max(peakResidentBlocked, maxElement(dirtWorld.town.residents?.walkers.Select(w => w.blocked) ?? Enumerable.Empty<double>()) ?? 0);
                    var races = new[] { race }.Concat(opponents.Select(o => o.race)).ToArray();
                    if (racePhysics.escape.active && activeStart == null) { activeStart = frame; }
                    if (racePhysics.escape.complete && allDepartedStart == null) { allDepartedStart = frame; }
                    if (activeStart is int start)
                    {
                        updateDepartureHUD();
                        hudHidden = hudHidden && raceHUD.isHidden && hud.isHidden && window.toolbar?.isVisible == false;
                        var seconds = (double)(frame - start) / 60; var bin = (int)(seconds / 60);
                        roamingSeconds = seconds;
                        while (visibleWindows.Count <= bin)
                        {
                            visibleWindows.Add(new HashSet<int>());
                            minuteCells.Add(Enumerable.Range(0, 4).Select(_ => new HashSet<(long, long)>()).ToList());
                        }
                        if (frame % 60 == 0)
                        {
                            var all = new[] { simulation }.Concat(opponents.Select(o => o.simulation)).ToArray();
                            for (var i = 0; i < all.Length; i++)
                            {
                                var state = all[i];
                                var p = new Double2(state.x, state.z); var distance = Simd.distance(p, lastPositions[i]);
                                if (racePhysics.escape.departed[i])
                                {
                                    travelled[i] += min(10, distance);
                                    minuteCells[bin][i].Add(((long)Math.Floor(p.x / 2), (long)Math.Floor(p.y / 2)));
                                    stationary[i] = distance < 0.08 ? stationary[i] + 1 : 0;
                                    maxStationary[i] = max(maxStationary[i], stationary[i]);
                                    if (distance > 0.25 && max(abs(p.x), abs(p.y)) < 29) { visibleWindows[bin].Add(i); }
                                    if (seconds >= 540)
                                    {
                                        lateDistance[i] += distance;
                                        lateCells[i].Add(((long)Math.Floor(p.x / 2), (long)Math.Floor(p.y / 2)));
                                    }
                                }
                                lastPositions[i] = p;
                            }
                        }
                        if (capture && frame % 6 == 0)
                        {
                            // Render excerpts at normal simulation speed, including a
                            // late pass. Keep the same locked gameplay camera.
                            var clip = seconds < 180 ? 0 : (seconds >= 660 && seconds < 690 ? 1 : -1);
                            if (clip >= 0 || (seconds >= 655 && seconds < 660))
                            {
                                updateOpponents(); updateRaceWorld(dt: 0.1); updateCamera(snap: true);
                                if (clip >= 0)
                                {
                                    var image = renderer.snapshot((double)frame / 60, new CGSize(1280, 720), SCNAntialiasingMode.multisampling4X);
                                    var jpeg = NSBitmapImageRep.data(image.tiffRepresentation)?.representation(NSBitmapImageFileType.jpeg, new() { [NSBitmapImageRep.PropertyKey.compressionFactor] = 0.9 }) ?? throw new IOException("fileWriteUnknown");
                                    var folder = clip == 0 ? "departure" : "minute-eleven";
                                    File.WriteAllBytes(Path.Combine(directory, folder, format("frame-%05d.jpg", movieFrames[clip])), jpeg);
                                    movieFrames[clip] += 1;
                                }
                            }
                        }
                        if (allDepartedStart is int departed)
                        {
                            secondsAfterAllDeparted = (double)(frame - departed) / 60;
                            if (secondsAfterAllDeparted >= 720) { break; }
                        }
                        if ((frame - start) % (60 * 60) == 0)
                        {
                            updateOpponents(); updateRaceWorld(dt: 1.0 / 60); updateCamera(snap: true);
                            saveTownFrame($"town-roaming-minute-{(int)(seconds / 60)}", directory);
                        }
                    }
                    if (race.finished)
                    {
                        updateCamera(snap: true);
                        var before = world.camera.simdTransform;
                        cycleCamera(null); view.onOrbit?.Invoke(120, 80); view.onZoom?.Invoke(50);
                        updateCamera(snap: true);
                        cameraLocked = cameraLocked && cameraMode == 2 && world.camera.simdTransform == before
                            && simdF3(world.camera.simdPosition) == new Float3(0, 38, -33);
                        var escaped = racePhysics.escape.waypoint.Count(w => w >= 3);
                        if (photographed.Add(escaped))
                        {
                            updateOpponents(); updateRaceWorld(dt: 1.0 / 60);
                            saveTownFrame($"postrace-overhead-{escaped}", directory);
                        }
                    }
                    for (var i = 0; i < races.Length; i++) { if (races[i].finished && frozen[i] == null) { frozen[i] = races[i].elapsed; } }
                    if (racePhysics.escape.active)
                    {
                        if (!races.All(r => r.finished && r.completedCooldownLap)) { early = true; }
                        if (firstOpen == null)
                        {
                            firstOpen = (double)frame / 60;
                            simulation.paused = true; double gate = racePhysics.gate.angle, elapsed = simulation.elapsed;
                            advanceRacePhysics(new DriveInput(), dt: 0.1, raceDT: 0.1);
                            pauseChecked = gate == racePhysics.gate.angle && elapsed == simulation.elapsed; simulation.paused = false;
                        }
                        var states = new[] { simulation }.Concat(opponents.Select(o => o.simulation)).ToArray();
                        for (var i = 0; i < states.Length; i++)
                        {
                            if (!(racePhysics.escape.waypoint[i] >= 1)) { continue; }
                            var state = states[i];
                            var body = new RobotCollisions.Body(position: new Double3(state.x, state.groundY, state.z), heading: state.heading, profile: RobotCollisions.profiles[(int)lineup[i]]);
                            var robots = states.Select((other, j) => (other, j)).Where(pair => pair.j != i).Select(pair =>
                                new RobotCollisions.Body(position: new Double3(pair.other.x, pair.other.groundY, pair.other.z), heading: pair.other.heading, profile: RobotCollisions.profiles[(int)lineup[pair.j]]));
                            foreach (var obstacle in dirtWorld.town.collisionWorld.nearby(body).Concat(new[] { racePhysics.gate.body() }).Concat(CityExit.posts).Concat(robots))
                            {
                                if (RobotCollisions.contact(body, obstacle) is RobotCollisions.Contact contact) { maxPenetration = max(maxPenetration, contact.penetration); }
                            }
                        }
                    }
                    if (frame % 6000 == 0)
                    {
                        print($"Postrace {frame / 60}s laps {description(races.Select(r => r.laps.Length))} cooldown {description(races.Select(r => r.cooldownProgress))} waypoints {description(racePhysics.escape.waypoint)}");
                    }
                }
                var finalStates = new[] { simulation }.Concat(opponents.Select(o => o.simulation)).ToArray(); var finalRaces = new[] { race }.Concat(opponents.Select(o => o.race)).ToArray();
                var timesFrozen = Enumerable.Range(0, finalRaces.Length).All(i => frozen[i] == finalRaces[i].elapsed);
                var distinct = racePhysics.escape.routes.Select(r => string.Join(";", r.Select(p => description(p.x) + "," + description(p.y)))).Distinct().Count() > 1;
                var sustained = secondsAfterAllDeparted >= 720 && racePhysics.escape.tours.All(t => t >= 2)
                    && travelled.All(t => t > 400) && maxStationary.All(s => s < 20)
                    && visibleWindows.Take(visibleWindows.Count - 1).Skip(3).All(w => w.Count != 0)
                    && racePhysics.escape.reversals.All(r => r < 20)
                    && minuteCells.Take(minuteCells.Count - 1).Skip(3).All(minute => minute.All(cells => cells.Count > 8))
                    && lateDistance.All(d => d > 100) && lateCells.All(cells => cells.Count > 25);
                var people = dirtWorld.town.residents;
                var peopleMoving = people?.walkers.All(w => racePhysics.storm.enabled && w.index % 11 != 0 ? w.node.isHidden : (w.visits > 0 && w.blocked < 20)) ?? false;
                var passed = peopleMoving && peakResidentBlocked < 20 && (people?.maximumPenetration ?? 1) < 0.005 && sustained && hudHidden && cameraLocked && racePhysics.escape.complete && !early && timesFrozen && pauseChecked && distinct && maxPenetration < 0.005;
                updateOpponents(); updateRaceWorld(dt: 1.0 / 60);
                updateCamera(snap: true);
                saveTownFrame("postrace-town", directory);
                var report = new Dictionary<string, object>
                {
                    ["passed"] = passed, ["peopleMoving"] = peopleMoving, ["peakResidentBlockedSeconds"] = peakResidentBlocked, ["houseEntries"] = people?.entries ?? 0,
                    ["residentVisits"] = people?.walkers.Select(w => w.visits).ToList() ?? new List<int>(), ["residentBlocked"] = people?.walkers.Select(w => w.blocked).ToList() ?? new List<double>(),
                    ["roamingSeconds"] = roamingSeconds, ["secondsAfterAllDeparted"] = secondsAfterAllDeparted, ["lateDistance"] = lateDistance, ["lateUniqueCells"] = lateCells.Select(c => c.Count).ToList(),
                    ["uniqueCellsPerMinute"] = minuteCells.Select(minute => minute.Select(c => c.Count).ToList()).ToList(), ["movieFrames"] = movieFrames, ["reversals"] = racePhysics.escape.reversals,
                    ["yields"] = racePhysics.escape.yields, ["tours"] = racePhysics.escape.tours, ["distanceTravelled"] = travelled, ["maximumStationarySeconds"] = maxStationary,
                    ["visibleRobotsPerMinute"] = visibleWindows.Select(w => w.OrderBy(i => i).ToList()).ToList(), ["hudHidden"] = hudHidden, ["sustained"] = sustained, ["cameraLocked"] = cameraLocked,
                    ["complete"] = racePhysics.escape.complete, ["earlyGate"] = early, ["frozenResults"] = timesFrozen, ["pause"] = pauseChecked, ["differentDestinations"] = distinct,
                    ["maximumPenetration"] = maxPenetration, ["gateOpenTime"] = firstOpen ?? -1, ["waypoints"] = racePhysics.escape.waypoint, ["positions"] = finalStates.Select(s => new List<double> { s.x, s.z }).ToList(),
                    ["routes"] = racePhysics.escape.routes.Select(r => r.Select(p => new List<double> { p.x, p.y }).ToList()).ToList(),
                };
                File.WriteAllText(Path.Combine(directory, "postrace.json"), JSONSerialization.prettyPrintedSortedKeys(report));
                print(JSONSerialization.prettyPrintedSortedKeys(report.Where(kv => kv.Key != "routes").ToDictionary(kv => kv.Key, kv => kv.Value)));
                reset(null); return passed && !racePhysics.escape.active && racePhysics.gate.angle == 0 && !raceHUD.isHidden && window.toolbar?.isVisible == true;
            }
            finally { weatherOverride = null; }
        }
        catch (Exception error) { print(error.ToString()); return false; }
    }

    [GameMode("--postrace-smoke-test")]
    public static async System.Threading.Tasks.Task RunPostRaceSmokeTest(string dir, Godot.SceneTree tree)
    {
        var app = await launchSmoke(tree, dir);
        var passed = app.checkPostRaceEscape(at: dir);
        exit(passed ? 0 : 1);
    }
}
