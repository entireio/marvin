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
/// `--dirt-smoke-test DIR`: the dirt-track part of the composite macOS `--smoke-test` (App.swift smokeTest() from
/// startDirtTrack() on, and TrailRaceSmoke.swift checkFullRaceTrails). Same sequence, cameras and file names:
/// dirt-grid.png, dirt-overview.png, dirt-driving.png, dirt-trails.png, dirt-fence.png, full-race-trails.png and
/// full-race-trails.json; dirt-smoke.json holds the dirt keys of smoke.json (walls, trails, debris, intro).
/// PORT: a separate flag because `--smoke-test` (menu, sandbox, robots, HUD, scores) belongs to the app stream.
/// Robots, their acting/contact/coating checks, the HUD capture and the race scores are not part of this stream.
/// </summary>
public static class DirtTrackSmoke
{
    [GameMode("--dirt-smoke-test")]
    public static Task Run(string dir, SceneTree tree)
    {
        bool passed = false;
        try { passed = smokeTest(new RaceWorldHarness(tree), dir); }
        catch (Exception error) { GD.PrintErr($"Smoke test failed: {error}"); }
        tree.Quit(passed ? 0 : 1);
        return Task.CompletedTask;
    }

    /// `--debris-smoke-test DIR` (App.swift: `dirtWorld.checkDebris()`; prints its results, writes nothing).
    [GameMode("--debris-smoke-test")]
    public static Task RunDebris(string dir, SceneTree tree)
    {
        bool passed = false;
        try { passed = new DirtWorld().checkDebris(); }
        catch (Exception error) { GD.PrintErr($"Debris check failed: {error}"); }
        tree.Quit(passed ? 0 : 1);
        return Task.CompletedTask;
    }

    /// `--race-world-views DIR`: the race-world cameras of the macOS `--town-smoke-test` (TownSmoke.swift), same names
    /// and positions, for checking the service embankment, city-exit apron and walls against reference/mac/town.
    /// PORT: a Godot-only QA mode; the town itself (buildings, grandstands, people) belongs to the town stream.
    /// MARVIN_BUILD_OFF_MAIN=1 builds the DirtWorld on a pool thread first, as LevelLoading.swift does on a global queue.
    [GameMode("--race-world-views")]
    public static async Task RunViews(string dir, SceneTree tree)
    {
        bool passed = false;
        try
        {
            DirtWorld prebuilt = null;
            if (System.Environment.GetEnvironmentVariable("MARVIN_BUILD_OFF_MAIN") == "1")
            {
                var started = DateTime.Now;
                var task = Task.Run(() => new DirtWorld(progress: (fraction, label) => GD.Print($"loading {fraction:0.000} {label}")));
                // Keep the main loop running: Godot answers some of the builder's RenderingServer calls on the main thread.
                while (!task.IsCompleted) await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                prebuilt = task.Result;
                GD.Print($"DirtWorld built off the main thread in {(DateTime.Now - started).TotalSeconds:0.0} s");
            }
            var app = new RaceWorldHarness(tree, prebuilt);
            app.weatherOverride = false;
            app.startDirtTrack(); app.dirtIntro = null; app.race.countDown(dt: 3);
            app.updateRaceWorld(dt: 1.0 / 60);
            var exit = CityExit.point(0, 4.5);
            var gate = CityExit.point(0, -0.5);
            var cameras = new (string, SCNVector3, SCNVector3)[]
            {
                ("town-overview", new SCNVector3(0, 46, -52), new SCNVector3(0, 0, 0)),
                ("town-ramp-ground", new SCNVector3(-7.5, 0.58, -8.9), new SCNVector3(-7.5, 0.26, -12.3)),
                ("town-ramp-side", new SCNVector3(-10.0, 0.85, -10.0), new SCNVector3(-7.5, 0.20, -11.5)),
                ("town-service-access", new SCNVector3(-2, 5.8, -18), new SCNVector3(-7.5, 0.15, -10.8)),
                ("town-repair", new SCNVector3(-3.6, 2.9, -13.0), new SCNVector3(-7.5, 0.95, -7.5)),
                ("race-exit-ramp", new SCNVector3(exit.x, 1.6, exit.y), new SCNVector3(gate.x, 0.3, gate.y)),
            };
            foreach (var (name, eye, target) in cameras)
            {
                app.camera.position = eye;
                app.camera.look(target, up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                app.saveTownFrame(name, dir);
            }
            passed = true;
        }
        catch (Exception error) { GD.PrintErr($"Race world views failed: {error}"); }
        tree.Quit(passed ? 0 : 1);
    }

    /// AppController.advanceRaceFrame(step:raceDelta:advancing:) with no keys held (view.driveInput is idle).
    private static void advanceRaceFrame(RaceWorldHarness app, double step, double raceDelta, bool advancing)
    {
        if (!advancing) return;
        if (app.dirtIntro is double intro)
        {
            app.dirtIntro = intro + step;
            if (app.dirtIntro >= app.dirtIntroDuration) { app.dirtIntro = null; }
        }
        else if (app.isDirtTrack && app.race.countdown > 0) { app.race.countDown(dt: raceDelta); }
        else
        {
            app.advanceRacePhysics(new DriveInput(), dt: step, raceDT: raceDelta);
            if (app.race.finished)
            {
                if (app.dirtOutro == null)
                {
                    app.dirtOutro = 0; app.outroPosition = app.camera.position;
                    var front = app.camera.worldFront;
                    app.outroTarget = new SCNVector3(app.outroPosition.x + front.x * 4.5, app.outroPosition.y + front.y * 4.5, app.outroPosition.z + front.z * 4.5);
                }
                else { app.dirtOutro = min(app.dirtIntroDuration, app.dirtOutro.Value + step); }
            }
        }
    }

    public static bool smokeTest(RaceWorldHarness app, string directory)
    {
        Directory.CreateDirectory(directory);
        app.weatherOverride = false; // smoke runs are clear (applicationDidFinishLaunching)
        app.startDirtTrack();
        bool dirtStartPassed = app.isDirtTrack && app.race.countdown == 3 && app.view.scene == app.dirtWorld.scene && app.simulation.dirtTrack;
        bool introPassed = app.dirtIntro == 0 && abs(app.camera.position.y - 38) < 0.001 && app.race.elapsed == 0;
        app.dirtIntro = app.dirtIntroDuration / 2; app.updateCamera(snap: true);
        bool introMidPassed = app.camera.position.y > 3 && app.camera.position.y < 38;
        app.dirtIntro = null; app.updateCamera(snap: true);
        // (R2-D2 wheels, BB-8/WALL-E motion, race acting, robot contacts and robot close-ups: robots stream.)
        app.updateCamera(snap: true);
        // No racer moves during countdown, pause or inactive frames.
        advanceRaceFrame(app, step: 1.0 / 60, raceDelta: 1.0 / 60, advancing: true);
        bool heldAtStart = app.opponents.All(o => o.simulation.distance == 0 && o.race.elapsed == 0);
        app.race.countDown(dt: 3);
        advanceRaceFrame(app, step: 1.0 / 60, raceDelta: 1.0 / 60, advancing: false);
        bool opponentGatePassed = heldAtStart && app.opponents.All(o => o.simulation.distance == 0 && o.race.elapsed == 0);
        app.saveSnapshot("dirt-grid.png", directory);
        app.race.countDown(dt: 3);
        bool airborneTrailsPassed = true;
        for (int k = 0; k < 240; k++)
        {
            double phase = DirtCourse.phase(app.simulation.x, app.simulation.z);
            var target = DirtCourse.point(phase + 0.055);
            double desired = atan2(target.x - app.simulation.x, target.z - app.simulation.z);
            double error = atan2(sin(desired - app.simulation.heading), cos(desired - app.simulation.heading));
            var input = new DriveInput(); input.throttle = 1; input.boost = true; input.turn = -error * 1.5;
            app.advanceRacePhysics(input, dt: 1.0 / 60, raceDT: 1.0 / 60);
            var beforeTrails = app.dirtWorld.trailCounts;
            app.dirtWorld.update(app.simulation, opponent: app.opponent.simulation, dt: 1.0 / 60, modelScale: app.modelScale, additional: new[] { app.bb8Opponent.simulation, app.wallEOpponent.simulation });
            if (!app.simulation.hasDirtContact) { airborneTrailsPassed = airborneTrailsPassed && app.dirtWorld.trailCounts[0] == beforeTrails[0]; }
            for (int i = 0; i < app.opponents.Length; i++)
            {
                if (app.opponents[i].simulation.hasDirtContact) continue;
                airborneTrailsPassed = airborneTrailsPassed && app.dirtWorld.trailCounts[i + 1] == beforeTrails[i + 1];
            }
        }
        bool opponentPassed = app.opponents.All(o => o.simulation.distance > 10 && o.race.elapsed > 3.9) && opponentGatePassed
            && app.opponent.simulation.distance > 10 && app.opponent.race.elapsed > 3.9;
        // Both brick boundaries must exist, start below the ground and
        // reach their target height wherever bricks are laid; only the
        // gate and service openings may leave short uncovered stretches.
        int trackWalls = 0;
        bool wallPassed = true;
        var wallReport = new Dictionary<string, object>();
        app.dirtWorld.scene.rootNode.enumerateChildNodes((node, _) =>
        {
            if (!(node.name is string name && name.EndsWith("irregular brick track wall") && node.geometry?.sourcesFor(SCNGeometrySourceSemantic.vertex).FirstOrDefault() is SCNGeometrySource source)) return;
            trackWalls += 1;
            var cells = new Dictionary<(int, int), List<Double3>>();
            var bytes = source.data;
            for (int i = 0; i < source.vectorCount; i++)
            {
                var values = Enumerable.Range(0, 3).Select(component =>
                {
                    int offset = source.dataOffset + i * source.dataStride + component * source.bytesPerComponent;
                    return source.bytesPerComponent == 4 ? (double)BitConverter.ToSingle(bytes, offset) : BitConverter.ToDouble(bytes, offset);
                }).ToArray();
                var key = ((int)floor(values[0] / 0.5), (int)floor(values[2] / 0.5));
                if (!cells.TryGetValue(key, out var list)) { list = new List<Double3>(); cells[key] = list; }
                list.Add(new Double3(values[0], values[1], values[2]));
            }
            double side = name.StartsWith("Inner") ? -1.0 : 1.0;
            var samples = DirtCourse.surfacePoints(offset: side * (DirtCourse.fenceOffset + DirtCourse.boundaryWallThickness / 2));
            int covered = 0, floating = 0, @short = 0; double worstShortfall = 0.0, worstFloat = 0.0;
            foreach (var p in samples)
            {
                var cell = ((int)floor(p.x / 0.5), (int)floor(p.y / 0.5));
                var nearby = new List<Double3>();
                for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++) if (cells.TryGetValue((cell.Item1 + dx, cell.Item2 + dz), out var found)) nearby.AddRange(found);
                nearby = nearby.Where(v => Simd.length(new Double2(v.x, v.z) - p) < 0.3).ToList();
                if (nearby.Count == 0) continue;
                double top = nearby.Max(v => v.y), bottom = nearby.Min(v => v.y);
                covered += 1;
                double ground = DirtCourse.height(p.x, p.y);
                double target = side > 0 ? CityExit.wallTop(p) : ground + DirtCourse.postHeight;
                if (bottom > ground) { floating += 1; worstFloat = max(worstFloat, bottom - ground); }
                if (top < target - 0.03) { @short += 1; worstShortfall = max(worstShortfall, target - top); }
            }
            wallPassed = wallPassed && floating == 0 && @short == 0 && covered * 10 >= samples.Length * 9;
            wallReport[name] = new Dictionary<string, object> { ["samples"] = samples.Length, ["covered"] = covered, ["floating"] = floating, ["worstFloat"] = worstFloat, ["short"] = @short, ["worstShortfall"] = worstShortfall };
        });
        wallPassed = wallPassed && trackWalls == 2;
        int raceContactCount = app.racePhysics.contactCount;
        var trailCounts = app.dirtWorld.trailCounts; var racerEmissions = app.dirtWorld.racerEmittedCount.ToArray();
        app.dirtWorld.update(app.simulation, opponent: app.opponent.simulation, dt: 0, modelScale: app.modelScale, additional: new[] { app.bb8Opponent.simulation, app.wallEOpponent.simulation });
        bool effectsPaused = trailCounts.SequenceEqual(app.dirtWorld.trailCounts) && racerEmissions.SequenceEqual(app.dirtWorld.racerEmittedCount);
        Simulation stoppedPlayer = app.simulation, stoppedRival = app.opponent.simulation;
        stoppedPlayer.stop(); stoppedRival.stop();
        var stoppedOthers = new[] { app.bb8Opponent.simulation, app.wallEOpponent.simulation }.Select(state => { var stopped = state; stopped.stop(); return stopped; }).ToArray();
        app.dirtWorld.update(stoppedPlayer, opponent: stoppedRival, dt: 0.1, modelScale: app.modelScale, additional: stoppedOthers);
        bool dirtEffectsPassed = airborneTrailsPassed && effectsPaused && trailCounts.All(c => c > 40)
            && racerEmissions.All(c => c > 20) && trailCounts.SequenceEqual(app.dirtWorld.trailCounts)
            && racerEmissions.SequenceEqual(app.dirtWorld.racerEmittedCount);
        bool dirtPassed = dirtEffectsPassed && wallPassed && opponentPassed && dirtStartPassed && app.dirtWorld.emittedCount > 20 && app.race.elapsed > 3.9
            && DirtCourse.projection(app.simulation.x, app.simulation.z).distance < DirtCourse.fenceOffset;
        foreach (var (mode, name) in new[] { (2, "dirt-overview.png"), (0, "dirt-driving.png") })
        {
            app.cameraMode = mode; app.updateCamera(snap: true);
            app.saveSnapshot(name, directory);
        }
        var trailView = DirtCourse.point(0.18);
        app.camera.position = new SCNVector3(trailView.x + 1.8, 3.8, trailView.z + 2.5);
        app.camera.look(new SCNVector3(trailView.x, 0, trailView.z), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
        app.saveSnapshot("dirt-trails.png", directory);
        // (Dusty drive and robot close-ups: DirtCoating belongs to the robots stream.)
        var hill = DirtCourse.point(0.69 * 2 * Math.PI);
        var hillside = DirtCourse.point(0.64 * 2 * Math.PI, offset: 6);
        app.camera.position = new SCNVector3(hillside.x, 3.6, hillside.z);
        app.camera.look(new SCNVector3(hill.x, 0.9, hill.z), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
        app.saveSnapshot("dirt-fence.png", directory);
        bool fullRaceTrailsPassed = checkFullRaceTrails(app, directory);
        app.reset();
        bool dirtResetPassed = app.opponent.race.elapsed == 0 && app.opponent.simulation.distance == 0 && app.race.laps.Length == 0 && app.race.countdown == 3 && app.dirtWorld.emittedCount == 0 && app.dirtWorld.trailCounts.SequenceEqual(new[] { 0, 0, 0, 0 }) && app.dirtWorld.racerEmittedCount.SequenceEqual(new[] { 0, 0, 0, 0 }) && app.opponents.All(o => o.race.elapsed == 0 && o.simulation.distance == 0);
        bool passed = fullRaceTrailsPassed && introPassed && introMidPassed && dirtPassed && dirtResetPassed;
        var report = new Dictionary<string, object> { ["passed"] = passed, ["fullRaceTrailsPassed"] = fullRaceTrailsPassed, ["raceContactCount"] = raceContactCount, ["dirtEffectsPassed"] = dirtEffectsPassed, ["racerTrailMarks"] = trailCounts.ToList(), ["racerDirtParticles"] = racerEmissions.ToList(), ["opponentPassed"] = opponentPassed, ["wallPassed"] = wallPassed, ["trackWalls"] = wallReport, ["introPassed"] = introPassed && introMidPassed, ["dirtPassed"] = dirtPassed, ["dirtResetPassed"] = dirtResetPassed, ["renderer"] = "Godot / SceneKit facade", ["width"] = 1280, ["height"] = 820 };
        RaceWorldHarness.writeJSON(Path.Combine(directory, "dirt-smoke.json"), report);
        GD.Print($"Dirt smoke test: {(passed ? "PASS" : "FAIL")} · {directory} walls {wallPassed} effects {dirtEffectsPassed} trails [{string.Join(", ", trailCounts)}] particles [{string.Join(", ", racerEmissions)}] full race {fullRaceTrailsPassed}");
        return passed;
    }

    /// TrailRaceSmoke.swift checkFullRaceTrails(at:): a complete three-lap race through the actual collision world and
    /// trail renderer, checking both emission and the visible terrain surface.
    public static bool checkFullRaceTrails(RaceWorldHarness app, string directory)
    {
        app.reset(); app.dirtIntro = null;
        var slot = DirtCourse.startingGrid[3];
        app.simulation = new Simulation(seed: 0, dirtTrack: true, dirtStartOffset: slot.offset, dirtStartPhase: slot.phase);
        app.race = new DirtRace(startPhase: slot.phase); app.race.countDown(dt: 3);
        app.opponent = new DirtOpponent(slot: DirtCourse.startingGrid[0]);
        app.bb8Opponent = new DirtOpponent(slot: DirtCourse.startingGrid[1], laneOffset: 0);
        app.wallEOpponent = new DirtOpponent(slot: DirtCourse.startingGrid[2], laneOffset: -0.65);
        var lane = app.dirtWorld.scene.rootNode.childNode(withName: "Compacted race surface", recursively: false);
        var buried = new List<Dictionary<string, object>>(); var air = new List<Dictionary<string, object>>();
        int groundedMissing = 0, samples = 0, smallHopFrames = 0, airborneMarks = 0;
        var flights = new List<Dictionary<string, object>>(); Simulation? flightStart = null; double flightMax = 0.0;
        var options = new Dictionary<string, object> { [SCNHitTestOption.backFaceCulling] = false };
        for (int frame = 0; frame < 60 * 240; frame++)
        {
            var before = app.simulation; int count = app.dirtWorld.trailCounts[0];
            var input = DirtOpponent.driveInput(app.simulation, laneOffset: 0.1);
            app.advanceRacePhysics(input, dt: 1.0 / 60, raceDT: 1.0 / 60);
            app.dirtWorld.update(app.simulation, opponent: app.opponent.simulation, dt: 1.0 / 60, modelScale: app.modelScale, additional: new[] { app.bb8Opponent.simulation, app.wallEOpponent.simulation });
            var s = app.simulation;
            if (before.hasDirtContact && s.hasDirtContact && hypot(s.x - before.x, s.z - before.z) > 0.07 && app.dirtWorld.trailCounts[0] == count) { groundedMissing += 1; }
            if (s.airborne && s.hasDirtContact) { smallHopFrames += 1; }
            if (!s.hasDirtContact && app.dirtWorld.trailCounts[0] != count) { airborneMarks += 1; }
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
                air.Add(new Dictionary<string, object> { ["time"] = app.race.elapsed, ["phase"] = DirtCourse.phase(s.x, s.z), ["clearance"] = s.groundY - DirtCourse.height(s.x, s.z) });
            }
            if (frame % 5 == 0 && !s.airborne)
            {
                foreach (var side in new[] { -1.0, 1.0 })
                {
                    double lateral = side * 0.262225 * app.modelScale, forward = -0.23 * app.modelScale;
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
                            buried.Add(new Dictionary<string, object> { ["time"] = app.race.elapsed, ["phase"] = DirtCourse.phase(x, z), ["x"] = x, ["z"] = z, ["depth"] = surface - nominal });
                        }
                    }
                }
            }
            if (app.race.finished) break;
        }
        app.cameraMode = 2; app.updateCamera(snap: true);
        app.saveSnapshot("full-race-trails.png", directory);
        var report = new Dictionary<string, object> { ["finished"] = app.race.finished, ["total"] = app.race.elapsed, ["laps"] = app.race.laps.ToList(), ["marks"] = app.dirtWorld.trailCounts.ToList(),
            ["groundedMissingFrames"] = groundedMissing, ["smallHopContactFrames"] = smallHopFrames, ["airborneMarkFrames"] = airborneMarks, ["surfaceSamples"] = samples, ["buriedSamples"] = buried, ["takeoffs"] = air, ["flights"] = flights };
        RaceWorldHarness.writeJSON(Path.Combine(directory, "full-race-trails.json"), report);
        return app.race.finished && samples > 500 && smallHopFrames > 0 && airborneMarks == 0 && groundedMissing == 0 && buried.Count == 0;
    }
}
