using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

// Port of TownSmoke.swift: TownFrameMeter, `checkTown(at:)`, `saveTownFrame(_:at:)` and the town benchmark
// (`startTownBenchmark(at:)`, `tickTownBenchmark(now:dt:)`; AppController extensions).
// Random state, as on macOS: reset() shuffles the starting grid and applies BinaryDaylight.random(),
// so the town-smoke captures use a random sun; the test pins of reset(_:) (TestPins in AppModes.cs:
// MARVIN_TOWN_GRID="a,b,c,d", MARVIN_TOWN_DAYLIGHT="fraction,phase") fix them for 1:1 comparison with a macOS run.
// PORT (town benchmark): the os_signpost markers, BenchmarkGPUCapture (Metal GPU capture), the display-link driver and
// the optional --benchmark-marvin-visibility-audit, --benchmark-visibility-audit and --benchmark-gpu-probe passes
// (MarvinVisibilityAudit.swift, TownVisibilityAudit.swift, LoadedGPUProbe.swift) are not ported; the render
// intervals are Godot frame callbacks (SceneKitRuntime's post-draw), and thermalState is always nominal.

/// Render callback cadence is recorded separately from simulation callbacks.
/// This is not a GPU timestamp or a substitute for Instruments presentation data.
public sealed class TownFrameMeter : SCNSceneRendererDelegate
{
    public FrameRateHUD fpsHUD; // weak in Swift
    private readonly NSLock @lock = new NSLock();
    private double? previous;
    private List<double> intervals = new();
    private bool collecting = false;
    private double frameStart = 0.0, cycleStart = 0.0, animationsEnd = 0.0, physicsEnd = 0.0, constraintsEnd = 0.0;
    public void rendererDidApplyAnimationsAtTime(SCNSceneRenderer renderer, double time)
    {
        @lock.@lock(); animationsEnd = ProcessInfo.processInfo.systemUptime; @lock.unlock();
    }
    public void rendererDidSimulatePhysicsAtTime(SCNSceneRenderer renderer, double time)
    {
        @lock.@lock(); physicsEnd = ProcessInfo.processInfo.systemUptime; @lock.unlock();
    }
    public void rendererDidApplyConstraintsAtTime(SCNSceneRenderer renderer, double time)
    {
        @lock.@lock(); constraintsEnd = ProcessInfo.processInfo.systemUptime; @lock.unlock();
    }
    public void rendererUpdateAtTime(SCNSceneRenderer renderer, double time)
    {
        @lock.@lock(); cycleStart = ProcessInfo.processInfo.systemUptime; @lock.unlock();
    }
    private List<double[]> frames = new();
    public void rendererWillRenderScene(SCNSceneRenderer renderer, SCNScene scene, double time)
    {
        @lock.@lock(); frameStart = ProcessInfo.processInfo.systemUptime; @lock.unlock();
    }
    public void rendererDidRenderScene(SCNSceneRenderer renderer, SCNScene scene, double time)
    {
        fpsHUD?.rendererDidRenderScene(renderer, scene, time);
        var now = ProcessInfo.processInfo.systemUptime;
        @lock.@lock();
        try
        {
            if (!collecting) { return; }
            if (previous is double last)
            {
                intervals.Add(now - last);
                frames.Add(new[] { now, (now - last) * 1000, (now - frameStart) * 1000, (now - cycleStart) * 1000, (animationsEnd - cycleStart) * 1000, (physicsEnd - animationsEnd) * 1000, (constraintsEnd - physicsEnd) * 1000, (frameStart - constraintsEnd) * 1000 });
            }
            previous = now;
        }
        finally { @lock.unlock(); }
    }
    public void reset() { @lock.@lock(); collecting = true; intervals = new(); frames = new(); previous = null; @lock.unlock(); }
    public List<double[]> timeline() { @lock.@lock(); try { return new List<double[]>(frames); } finally { @lock.unlock(); } }
    public Dictionary<string, object> report()
    {
        @lock.@lock(); var values = new List<double>(intervals); @lock.unlock();
        var sorted = Swift.sorted(values, (a, b) => a < b);
        double percentile(double p) => sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)((double)(sorted.Length - 1) * p))] * 1000;
        return new Dictionary<string, object>
        {
            ["samples"] = values.Count, ["meanFPS"] = values.Count == 0 ? 0 : (double)values.Count / values.Aggregate(0.0, (a, b) => a + b),
            ["p50MS"] = percentile(0.5), ["p95MS"] = percentile(0.95), ["p99MS"] = percentile(0.99),
            ["over25MS"] = values.Count(v => v > 0.025), ["over50MS"] = values.Count(v => v > 0.050),
            ["metric"] = "SceneKit didRenderScene wall-clock intervals; not GPU or display presentation timing",
        };
    }
}

public partial class AppController
{
    public sealed class RenderAuditException : Exception { public RenderAuditException(string message) : base(message) { } }

    public void saveTownFrame(string name, string directory)
    {
        var tiff = view.snapshot()?.tiffRepresentation;
        var bitmap = tiff == null ? null : NSBitmapImageRep.data(tiff);
        var png = bitmap?.representation(NSBitmapImageFileType.png);
        if (png == null) { throw new IOException("fileWriteUnknown"); }
        File.WriteAllBytes(Path.Combine(directory, name + ".png"), png);
        var magenta = 0;
        for (var y = 0; y < bitmap.pixelsHigh; y += 8)
        {
            for (var x = 0; x < bitmap.pixelsWide; x += 8)
            {
                if (bitmap.colorAt(x, y)?.usingColorSpace(NSColorSpace.deviceRGB) is NSColor c && c.redComponent > 0.9 && c.blueComponent > 0.9 && c.greenComponent < 0.15) { magenta += 1; }
            }
        }
        if (!(magenta < 20)) { throw new RenderAuditException($"Shader failure colour in {name}: {magenta} sampled pixels"); }
    }

    public bool checkTown(string at)
    {
        var directory = at;
        try
        {
            Directory.CreateDirectory(directory);
            startDirtTrack(); dirtIntro = null; race.countDown(dt: 3);
            var valid = dirtWorld.town.validate();
            SCNVector3 target = new SCNVector3(0, 1, -15), blocked = new SCNVector3(0, 1, -22);
            var safe = dirtWorld.town.clearCamera(from: target, to: blocked);
            var cameraPassed = safe.z > blocked.z + 1 && safe.z < target.z;
            var crowd = dirtWorld.town.residents?.walkers.Select(w => w.node).ToList() ?? new List<SCNNode>();
            var before = crowd.Select(n => n.transform).ToList();
            dirtWorld.town.update(dt: 0, camera: world.camera.position, player: Double2.zero);
            var pausePassed = before.Zip(crowd).All(pair => pair.First == pair.Second.transform);
            raceHUD.isHidden = true;
            for (var frame = 0; frame < 90; frame++)
            {
                advanceRacePhysics(DirtOpponent.driveInput(simulation), dt: 1.0 / 60, raceDT: 1.0 / 60);
                updateOpponents(); updateRaceWorld(dt: 1.0 / 60);
                dirtWorld.town.update(dt: 1.0 / 60, camera: new SCNVector3(0, 5, -12), player: new Double2(simulation.x, simulation.z), robots: robotBodies(),
                    visible: node => view.isNode(node, insideFrustumOf: world.camera), shadowCamera: world.camera, viewportAspect: (double)(view.bounds.width / view.bounds.height));
            }
            var cameras = new List<(string, SCNVector3, SCNVector3)> {
                ("town-overview", new SCNVector3(0, 46, -52), new SCNVector3(0, 0, 0)),
                ("town-grandstand", new SCNVector3(5, 4.8, -10), new SCNVector3(0, 1.9, -20.5)),
                ("town-citizens", new SCNVector3(-6.0, 1.35, -28.1), new SCNVector3(-6.7, 0.65, -25.4)),
                ("town-spectators", new SCNVector3(3.3, 2.5, DirtCourse.point(0).z - 1.5), new SCNVector3(3, 2.03, DirtCourse.point(0).z - 4.18)),
                ("town-market", new SCNVector3(-14, 2.6, -27.8), new SCNVector3(0, 1.3, -24.5)),
                ("town-ramp-ground", new SCNVector3(-7.5, 0.58, -8.9), new SCNVector3(-7.5, 0.26, -12.3)),
                ("town-ramp-side", new SCNVector3(-10.0, 0.85, -10.0), new SCNVector3(-7.5, 0.20, -11.5)),
                ("town-service-access", new SCNVector3(-2, 5.8, -18), new SCNVector3(-7.5, 0.15, -10.8)),
                ("town-repair", new SCNVector3(-3.6, 2.9, -13.0), new SCNVector3(-7.5, 0.95, -7.5)),
                ("town-outskirts", new SCNVector3(-46, 13, -26), new SCNVector3(-21, 3, -3)),
                ("town-spaceport", new SCNVector3(17, 9, 0), new SCNVector3(34, 1.5, 13)),
                ("town-skyline", new SCNVector3(-10, 13, 19), new SCNVector3(10, 5, 43)),
                ("town-game-overview", new SCNVector3(0, 38, -33), new SCNVector3(0, 0, 0)) };
            foreach (var (name, eye, aim) in cameras)
            {
                world.camera.position = eye; world.camera.look(at: aim, up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                _ = view.prepare(dirtWorld.scene, shouldAbortBlock: null);
                saveTownFrame(name, directory);
            }
            updateCamera(snap: true);
            saveTownFrame("town-racing", directory);
            // Inspect from just ahead of Marvin's head, at its actual eye height.
            var forward = new Double2(sin(simulation.heading), cos(simulation.heading));
            var robotEye = new SCNVector3(simulation.x + forward.x * 0.34, simulation.groundY + 0.46, simulation.z + forward.y * 0.34);
            world.camera.position = robotEye;
            world.camera.look(at: new SCNVector3(simulation.x + forward.x * 12, simulation.groundY + 0.46, simulation.z + forward.y * 12), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
            saveTownFrame("town-robot-pov", directory);
            var detailsPassed = true;
            foreach (var fraction in new[] { 0.25, 0.60, 0.90 })
            {
                Double2 p = dirtWorld.town.explorationSurveyPoint(fraction), ahead = dirtWorld.town.explorationSurveyPoint(fraction + 0.03);
                world.camera.position = new SCNVector3(p.x, 1.5, p.y);
                world.camera.look(at: new SCNVector3(ahead.x, 1.5, ahead.y), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                foreach (var enabled in new[] { false, true })
                {
                    dirtWorld.town.explorationDetailEnabled = enabled;
                    dirtWorld.town.updateExplorationDetail(camera: world.camera.position, player: p);
                    detailsPassed = detailsPassed && (enabled ? dirtWorld.town.activeExplorationCells > 0 : dirtWorld.town.activeExplorationCells == 0);
                    saveTownFrame($"outer-{(int)(fraction * 100)}-{(enabled ? "detailed" : "simple")}", directory);
                }
            }
            dirtWorld.town.updateExplorationDetail(camera: new SCNVector3(0, 38, -33), player: new Double2(90, 45));
            detailsPassed = detailsPassed && dirtWorld.town.activeExplorationCells == 0;
            dirtWorld.town.updateExplorationDetail(camera: new SCNVector3(25, 2, -10), player: Double2.zero);
            detailsPassed = detailsPassed && dirtWorld.town.activeExplorationCells == 0;
            var count = dirtWorld.town.statistics;
            var children = dirtWorld.town.root.childNodes.Count;
            reset(null);
            var after = dirtWorld.town.statistics;
            var resetPassed = after.Count == count.Count && count.All(pair => after.TryGetValue(pair.Key, out var value) && value == pair.Value) && dirtWorld.town.root.childNodes.Count == children && race.countdown == 3;
            var passed = valid && resetPassed && cameraPassed && pausePassed && detailsPassed;
            var report = new Dictionary<string, object>
            {
                ["passed"] = passed, ["explorationDetailPassed"] = detailsPassed, ["layoutClearancePassed"] = valid, ["cityCoveragePassed"] = dirtWorld.town.cityCoveragePassed, ["streetNetworkPassed"] = dirtWorld.town.streetNetworkPassed, ["resetPassed"] = resetPassed, ["cameraObstructionPassed"] = cameraPassed, ["crowdPausePassed"] = pausePassed,
                ["town"] = count, ["images"] = cameras.Select(c => c.Item1).Concat(new[] { "town-racing", "town-robot-pov" }).ToList(),
            };
            File.WriteAllText(Path.Combine(directory, "town-smoke.json"), JSONSerialization.prettyPrintedSortedKeys(report));
            return passed;
        }
        catch (Exception error) { Godot.GD.Print($"Town smoke: {error}"); return false; }
    }

    public void startTownBenchmark(string at)
    {
        var directory = at;
        weatherOverride = Environment.GetEnvironmentVariable("MARVIN_SANDSTORM") == "1";
        try { Directory.CreateDirectory(directory); } catch (Exception) { }
        startDirtTrack(); dirtIntro = null; race.countDown(dt: 3);
        if (Environment.GetEnvironmentVariable("MARVIN_DAYLIGHT_FRACTION") is string text && double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fraction) && double.IsFinite(fraction))
        {
            dirtWorld.sky.apply(new BinaryDaylight(fraction: fraction, phase: 1.2));
        }
        raceHUD.isHidden = true;
        simulation = new Simulation(dirtTrack: true, dirtStartOffset: DirtCourse.startingGrid[0].offset, dirtStartPhase: DirtCourse.startingGrid[0].phase);
        opponent = new DirtOpponent(slot: DirtCourse.startingGrid[1]);
        bb8Opponent = new DirtOpponent(slot: DirtCourse.startingGrid[2], laneOffset: 0);
        wallEOpponent = new DirtOpponent(slot: DirtCourse.startingGrid[3], laneOffset: -0.65);
        race = new DirtRace(startPhase: DirtCourse.startingGrid[0].phase); race.countDown(dt: 3);
        racePhysics = new DirtRacePhysics(characters: lineup, townRoutes: dirtWorld.escapeRoutes);
        racePhysics.storm = new Sandstorm(enabled: weatherOverride ?? false);
        if (CommandLine.arguments.Contains("--city-roam"))
        {
            simulation = new Simulation(dirtTrack: true, dirtStartOffset: DirtCourse.fenceOffset + 8.08, dirtStartPhase: CityExit.phase);
            racePhysics.gate.wantsOpen = true;
            var current = new Double2(simulation.x, simulation.z);
            var planner = new TownEscapeRoute(city: dirtWorld.town.collisionWorld, origin: current);
            foreach (var destination in new[] { new Double2(29, -10), new Double2(33, 3), new Double2(35, 14), new Double2(36, 28) })
            {
                if (planner.route(current, destination) is Double2[] route) { townBenchmarkRoute.AddRange(route); current = route[route.Length - 1]; }
            }
            townBenchmarkRoute.AddRange(Enumerable.Reverse(townBenchmarkRoute).ToList());
        }
        if (CommandLine.arguments.Contains("--dune-roam"))
        {
            var p = DirtCourse.projection(x: 175, z: 57);
            simulation = new Simulation(dirtTrack: true, dirtStartOffset: p.distance, dirtStartPhase: p.phase);
            townBenchmarkRoute = new List<Double2> { new Double2(205, 65), new Double2(178, 57) };
        }
        if (CommandLine.arguments.Contains("--postrace-roam"))
        {
            var roamingFrames = 0;
            for (var i = 0; i < 54000; i++)
            {
                advanceRacePhysics(DirtOpponent.driveInput(simulation), dt: 1.0 / 60, raceDT: 1.0 / 60);
                if (racePhysics.escape.complete) { roamingFrames += 1; }
                if (roamingFrames >= 60 * 120) { break; }
            }
            precondition(racePhysics.escape.complete, "Post-race benchmark did not reach town roaming");
            updateDepartureHUD();
        }
        dirtWorld.town.explorationDetailEnabled = !CommandLine.arguments.Contains("--benchmark-simple-town");
        updateOpponents();
        // Explicit 960x540 points at 2x backing gives the target 1080p drawable.
        window.minSize = new NSSize(640, 400);
        window.setContentSize(new NSSize(960, 540));
        if (CommandLine.arguments.Contains("--benchmark-msaa2")) { view.antialiasingMode = SCNAntialiasingMode.multisampling2X; }
        if (CommandLine.arguments.Contains("--benchmark-no-shadows"))
        {
            dirtWorld.scene.rootNode.enumerateChildNodes((node, _) => { if (node.light != null) { node.light.castsShadow = false; } });
        }
        if (CommandLine.arguments.Contains("--benchmark-no-ssao") && world.camera.camera != null) { world.camera.camera.screenSpaceAmbientOcclusionIntensity = 0; }
        if (CommandLine.arguments.Contains("--benchmark-no-bloom") && world.camera.camera != null) { world.camera.camera.bloomIntensity = 0; }
        if (CommandLine.arguments.Contains("--benchmark-flat-ground"))
        {
            dirtWorld.scene.rootNode.enumerateChildNodes((node, _) =>
            {
                foreach (var material in node.geometry?.materials ?? new List<SCNMaterial>())
                {
                    if (!(material.shaderModifiers ?? new Dictionary<SCNShaderModifierEntryPoint, string>()).Values.Any(v => v.Contains("townNoise"))) { continue; }
                    material.shaderModifiers = null; material.lightingModel = SCNMaterial.LightingModel.constant;
                }
            });
        }
        view.@delegate = townMeter;
        townBenchmarkStart = ProcessInfo.processInfo.systemUptime;
        // PORT: os_signpost(.event, log: townBenchmarkTraceLog, name: "TownBenchmarkStart", ...) is not ported (Instruments).
        var backing = view.convertToBacking(view.bounds);
        var identity = new Dictionary<string, object> { ["runID"] = townBenchmarkRunID, ["startUptime"] = townBenchmarkStart.Value, ["gpuDevice"] = view.device?.name ?? "Unavailable", ["resolution"] = new List<double> { backing.width, backing.height } };
        print("MARVIN_BENCHMARK_ID " + JSONSerialization.compact(identity));
        frameRateHUD.resetSamples(); frameRateHUD.isHidden = false;
        townMeter.fpsHUD = frameRateHUD; townBenchmarkHUDSamples = new();
        frameRateHUD.onSample = (now, fps) =>
        {
            if (!(townBenchmarkStart is double start && now - start >= 3.5)) { return; }
            townBenchmarkHUDSamples.Add(new Dictionary<string, object> { ["elapsedSeconds"] = now - start, ["fps"] = fps, ["text"] = frameRateHUD.displayedText });
            if (townBenchmarkHUDSamples.Count % 120 == 0)
            {
                print(format("Town drive %.0f s · %@", now - start, frameRateHUD.displayedText));
            }
        };
        townMeter.reset(); townBenchmarkCPU = new();
        townBenchmarkDirectory = URL.fileURLWithPath(directory);
        dirtWorld.town.root.isHidden = CommandLine.arguments.Contains("--without-town");
    }
    public void tickTownBenchmark(double now, double dt)
    {
        if (!(townBenchmarkStart is double start && townBenchmarkDirectory is URL directoryURL)) { return; }
        var directory = directoryURL.path;
        var elapsed = now - start;
        // PORT: benchmarkGPUCapture.update(elapsed:device:directory:) (Metal GPU capture) is not ported.
        if (elapsed < 3) { townMeter.reset(); townBenchmarkCPU = new(); townBenchmarkTimeline = new(); }
        var begin = ProcessInfo.processInfo.systemUptime;
        var isolateTrails = CommandLine.arguments.Contains("--benchmark-isolate-trails");
        var trailsHidden = isolateTrails && ((elapsed >= 300 && elapsed < 330) || (elapsed >= 480 && elapsed < 510));
        if (isolateTrails) { dirtWorld.setBenchmarkTrailsHidden(trailsHidden); }
        var lastSecond = townBenchmarkResourceSamples.Count > 0 && townBenchmarkResourceSamples[townBenchmarkResourceSamples.Count - 1]["second"] is int second ? second : -1;
        if ((int)elapsed >= lastSecond + 1)
        {
            townBenchmarkResourceSamples.Add(new Dictionary<string, object>
            {
                ["second"] = (int)elapsed, ["uptime"] = now, ["thermalState"] = (int)ProcessInfo.processInfo.thermalState, ["trailsHidden"] = trailsHidden,
                ["shadowCasters"] = dirtWorld.town.shadowCasterCount, ["trails"] = dirtWorld.trailDiagnostics(), ["shadowBatch"] = dirtWorld.town.shadowBatchTelemetry,
            });
        }
        var input = DirtOpponent.driveInput(simulation);
        if (townBenchmarkRoute.Count != 0)
        {
            var target = townBenchmarkRoute[townBenchmarkWaypoint];
            if (hypot(target.x - simulation.x, target.y - simulation.z) < 0.24)
            {
                townBenchmarkWaypoint = (townBenchmarkWaypoint + 1) % townBenchmarkRoute.Count;
                target = townBenchmarkRoute[townBenchmarkWaypoint];
            }
            var error = atan2(sin(atan2(target.x - simulation.x, target.y - simulation.z) - simulation.heading), cos(atan2(target.x - simulation.x, target.y - simulation.z) - simulation.heading));
            input = new DriveInput(); input.turn = max(-1, min(1, -error * 2.5)); input.throttle = abs(error) < 0.22 ? 0.5 : 0;
        }
        advanceRacePhysics(input, dt: dt, raceDT: dt);
        var physicsEnd = ProcessInfo.processInfo.systemUptime;
        updateOpponents();
        var modelsEnd = ProcessInfo.processInfo.systemUptime;
        updateRaceWorld(dt: dt);
        var effectsEnd = ProcessInfo.processInfo.systemUptime;
        var aerial = !raceCameraLocked && townBenchmarkRoute.Count == 0 && !CommandLine.arguments.Contains("--benchmark-chase-only") && elapsed % 24 > 18;
        if (aerial)
        {
            world.camera.position = new SCNVector3(0, 42, -44); world.camera.look(at: new SCNVector3(0, 0, 0), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
        }
        else { updateCamera(snap: false, dt: dt); }
        var detailPlayer = new Double2(simulation.x, simulation.z);
        if (CommandLine.arguments.Contains("--outer-town-survey"))
        {
            var fraction = 0.10 + 0.85 * min(1, elapsed / 45);
            detailPlayer = dirtWorld.town.explorationSurveyPoint(fraction);
            dirtWorld.sky.updateShadowCenter(new Double3(detailPlayer.x, 0, detailPlayer.y));
            var ahead = dirtWorld.town.explorationSurveyPoint(min(1, fraction + 0.025));
            world.camera.position = new SCNVector3(detailPlayer.x, 1.5, detailPlayer.y);
            world.camera.look(at: new SCNVector3(ahead.x, 1.5, ahead.y), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
        }
        var cameraEnd = ProcessInfo.processInfo.systemUptime;
        if (!dirtWorld.town.root.isHidden)
        {
            dirtWorld.town.update(dt: dt, camera: world.camera.position, player: detailPlayer, robots: robotBodies(),
                visible: node => view.isNode(node, insideFrustumOf: world.camera), shadowCamera: world.camera, viewportAspect: (double)(view.bounds.width / view.bounds.height));
        }
        updateRaceAudio(dt: dt, advancing: true);
        var finish = ProcessInfo.processInfo.systemUptime;
        townBenchmarkCPU.Add(finish - begin);
        townBenchmarkTimeline.Add(new[] { now, elapsed, dt * 1000, (physicsEnd - begin) * 1000, (modelsEnd - physicsEnd) * 1000, (effectsEnd - modelsEnd) * 1000, (cameraEnd - effectsEnd) * 1000, (finish - cameraEnd) * 1000, (finish - begin) * 1000, simulation.x, simulation.z, aerial ? 1 : 0 });
        var duration = double.TryParse(Environment.GetEnvironmentVariable("MARVIN_BENCHMARK_SECONDS") ?? "45", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? seconds : 45;
        if (!(elapsed >= max(10, duration))) { return; }
        // PORT: os_signpost(.event, log: townBenchmarkTraceLog, name: "TownBenchmarkEnd", ...) is not ported.
        timer?.invalidate(); view.@delegate = null;
        // PORT: (frameDisplayLink as? CADisplayLink)?.invalidate(): no display link.
        var report = townMeter.report();
        var cpu = Swift.sorted(townBenchmarkCPU, (a, b) => a < b);
        report["cpuUpdateP95MS"] = cpu.Length == 0 ? 0 : cpu[(int)((double)(cpu.Length - 1) * 0.95)] * 1000;
        report["durationSeconds"] = elapsed; report["townEnabled"] = !dirtWorld.town.root.isHidden;
        report["postRaceRoaming"] = racePhysics.escape.active;
        report["town"] = dirtWorld.town.statistics;
        report["activeExplorationCells"] = dirtWorld.town.activeExplorationCells;
        report["sandstorm"] = racePhysics.storm.enabled;
        report["visiblePeople"] = dirtWorld.town.visiblePopulation;
        report["residentUpdates"] = dirtWorld.town.residents?.updateStatistics ?? new Dictionary<string, int>();
        report["audioActive"] = raceAudio?.active ?? false;
        report["updateDriver"] = frameDisplayLink == null ? "timer" : "display-link";
        report["expressionsPlayed"] = raceAudio?.expressionCount ?? 0;
        report["metalRenderer"] = view.renderingAPI == SCNRenderingAPI.metal;
        report["gpuDevice"] = view.device?.name ?? "Unavailable";
        report["daylightFraction"] = dirtWorld.sky.daylight.fraction;
        report["sunElevationsDegrees"] = dirtWorld.sky.daylight.directions.Select(d => Math.Asin(d.y) * 180 / Math.PI).ToList();
        var drawable = view.convertToBacking(view.bounds);
        report["drawableWidth"] = drawable.width;
        report["drawableHeight"] = drawable.height;
        report["simulationSeconds"] = race.elapsed; report["laps"] = race.laps.Length;
        var shadowWidths = new List<int>();
        dirtWorld.scene.rootNode.enumerateChildNodes((node, _) =>
        {
            if (node.light is SCNLight light && light.castsShadow) { shadowWidths.Add((int)light.shadowMapSize.width); }
        });
        report["quality"] = new Dictionary<string, object> { ["msaaSamples"] = view.antialiasingMode == SCNAntialiasingMode.none ? 1 : (1 << (int)view.antialiasingMode), ["shadowMapWidths"] = shadowWidths.OrderBy(w => w).ToList(), ["explorationDetail"] = dirtWorld.town.explorationDetailEnabled };
        report["shadowBatch"] = dirtWorld.town.shadowBatchTelemetry;
        report["thermalState"] = (int)ProcessInfo.processInfo.thermalState;
        report["benchmarkArguments"] = CommandLine.arguments;
        report["benchmarkRunID"] = townBenchmarkRunID;
        report["startUptime"] = start;
        report["endWallTime"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        try
        {
            var timeline = new Dictionary<string, object>
            {
                ["renderColumns"] = new[] { "uptime", "intervalMS", "renderCallbackSpanMS", "rendererCycleMS", "sceneAnimationMS", "scenePhysicsMS", "sceneConstraintsMS", "preRenderMS" }, ["renderFrames"] = townMeter.timeline(),
                ["updateColumns"] = new[] { "uptime", "elapsed", "tickMS", "physicsMS", "modelsMS", "effectsMS", "cameraMS", "townMS", "totalMS", "x", "z", "aerial" }, ["updates"] = townBenchmarkTimeline,
            };
            File.WriteAllText(Path.Combine(directory, "timeline.json"), JSONSerialization.compact(timeline));
            File.WriteAllText(Path.Combine(directory, "benchmark.json"), JSONSerialization.prettyPrintedSortedKeys(report));
            File.WriteAllText(Path.Combine(directory, "resources.json"), JSONSerialization.prettyPrintedSortedKeys(townBenchmarkResourceSamples));
            File.WriteAllText(Path.Combine(directory, "displayed-fps.json"), JSONSerialization.prettyPrintedSortedKeys(townBenchmarkHUDSamples));
            // SCNView snapshots omit AppKit overlays; draw the actual live
            // counter over the native scene at its unchanged view position.
            var image = new NSImage(view.bounds.size); image.lockFocus();
            view.snapshot().draw(view.bounds);
            // PORT: Swift concatenates a translation to the HUD's frame origin and calls frameRateHUD.draw(_:); the facade's
            // views draw through cacheDisplay (over a transparent base), so the HUD is cached and drawn at its frame.
            var overlay = frameRateHUD.bitmapImageRepForCachingDisplay(frameRateHUD.bounds);
            frameRateHUD.cacheDisplay(frameRateHUD.bounds, overlay);
            overlay.draw(frameRateHUD.frame);
            image.unlockFocus();
            var png = NSBitmapImageRep.data(image.tiffRepresentation)?.representation(NSBitmapImageFileType.png);
            if (png != null) { File.WriteAllBytes(Path.Combine(directory, "final-fps.png"), png); }
            // PORT: --benchmark-marvin-visibility-audit, --benchmark-visibility-audit and --benchmark-gpu-probe are not ported.
            print($"Town benchmark complete · {directory}");
            exit(0);
        }
        catch (Exception error) { print(error.ToString()); exit(1); }
    }

    /// `--town-benchmark DIR` (App.swift tick: `startTownBenchmark(at:)` at main-menu frame 20). Unlike the checks the
    /// timer keeps running: every later tick() runs tickTownBenchmark(now:dt:) with wall-clock time until it exits.
    [GameMode("--town-benchmark")]
    public static async System.Threading.Tasks.Task RunTownBenchmark(string dir, Godot.SceneTree tree)
    {
        var app = launch(tree, dir);
        while (app.menuSmokeFrames < 20) { await frame(tree); }
        app.startTownBenchmark(at: dir);
        while (true) { await frame(tree); }
    }

    [GameMode("--town-smoke-test")]
    public static async System.Threading.Tasks.Task RunTownSmokeTest(string dir, Godot.SceneTree tree)
    {
        var app = await launchSmoke(tree, dir);
        var passed = app.checkTown(at: dir);
        print($"Town smoke: {(passed ? "PASS" : "FAIL")} · {dir}");
        exit(passed ? 0 : 1);
    }
}
