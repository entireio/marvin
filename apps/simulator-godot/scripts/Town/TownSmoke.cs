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
// (`startTownBenchmark(at:)`, `tickTownBenchmark(now:dt:)`; AppController extensions). Random state, as on macOS:
// reset() shuffles the starting grid and applies BinaryDaylight.random(), so the town-smoke captures use a random sun;
// the test pins of reset(_:) (TestPins in AppModes.cs: MARVIN_TOWN_GRID="a,b,c,d", MARVIN_TOWN_DAYLIGHT="fraction,phase")
// fix them for 1:1 comparison with a macOS run.
//
// The benchmark writes the macOS files (benchmark.json, timeline.json, resources.json, displayed-fps.json,
// final-fps.png) with the same keys, so scripts/rendering/check-sustained-performance.py and analyze-frame-timeline.py
// read Godot runs. PORT: the os_signpost markers (Instruments points of interest), BenchmarkGPUCapture (Xcode GPU trace)
// and the Apple-GPU diagnostics run after the drive (--benchmark-marvin-visibility-audit, --benchmark-visibility-audit,
// --benchmark-gpu-probe: MarvinVisibilityAudit, TownVisibilityAudit, LoadedGPUProbe) are not ported; those flags only
// print a note. The 1920x1080 drawable needs a 2x backing scale (960x540 points), as on a Retina Mac: on a 1x screen
// run with MARVIN_BACKING_SCALE=2.

/// Render callback cadence is recorded separately from simulation callbacks.
/// This is not a GPU timestamp or a substitute for Instruments presentation data.
/// PORT: the facade calls the delegate from Godot's frame loop: updateAtTime, didApplyAnimations and didSimulatePhysics
/// when it flushes the scene before drawing, didApplyConstraints and willRenderScene right before RenderingServer
/// draws the frame (frame_pre_draw), didRenderScene after the frame was submitted (frame_post_draw), so
/// renderCallbackSpanMS is the CPU time of Godot's render submission (with vsync this includes waiting for a drawable).
public sealed class TownFrameMeter : SCNSceneRendererDelegate
{
    public FrameRateHUD fpsHUD;
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
            if (previous is double p) { intervals.Add(now - p); frames.Add(new[] { now, (now - p) * 1000, (now - frameStart) * 1000, (now - cycleStart) * 1000, (animationsEnd - cycleStart) * 1000, (physicsEnd - animationsEnd) * 1000, (constraintsEnd - physicsEnd) * 1000, (frameStart - constraintsEnd) * 1000 }); }
            previous = now;
        }
        finally { @lock.unlock(); }
    }
    public void reset() { @lock.@lock(); collecting = true; intervals = new(); frames = new(); previous = null; @lock.unlock(); }
    public List<double[]> timeline() { @lock.@lock(); try { return new List<double[]>(frames); } finally { @lock.unlock(); } } // a copy: Swift arrays are values
    public Dictionary<string, object> report()
    {
        @lock.@lock(); var values = intervals.ToList(); @lock.unlock();
        var sorted = values.OrderBy(v => v).ToList();
        double percentile(double p) => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)((double)(sorted.Count - 1) * p))] * 1000;
        return new Dictionary<string, object>
        {
            ["samples"] = values.Count, ["meanFPS"] = values.Count == 0 ? 0 : (double)values.Count / values.Aggregate(0.0, (x, y) => x + y),
            ["p50MS"] = percentile(0.5), ["p95MS"] = percentile(0.95), ["p99MS"] = percentile(0.99),
            ["over25MS"] = values.Count(v => v > 0.025), ["over50MS"] = values.Count(v => v > 0.050),
            ["metric"] = "SceneKit didRenderScene wall-clock intervals (Godot: RenderingServer frame_post_draw); not GPU or display presentation timing",
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

    [GameMode("--town-smoke-test")]
    public static async System.Threading.Tasks.Task RunTownSmokeTest(string dir, Godot.SceneTree tree)
    {
        var app = await launchSmoke(tree, dir);
        var passed = app.checkTown(at: dir);
        print($"Town smoke: {(passed ? "PASS" : "FAIL")} · {dir}");
        exit(passed ? 0 : 1);
    }

    /// `--town-benchmark DIR` (App.swift tick: startTownBenchmark at menu frame 20, then tickTownBenchmark on every tick
    /// until MARVIN_BENCHMARK_SECONDS (default 45, at least 10) have passed; it writes the reports and exits). The timer
    /// keeps running (or the display link with --benchmark-display-link).
    [GameMode("--town-benchmark")]
    public static async System.Threading.Tasks.Task RunTownBenchmark(string dir, Godot.SceneTree tree)
    {
        var app = launch(tree, dir);
        while (app.menuSmokeFrames < 20) { await frame(tree); }
        app.startTownBenchmark(at: URL.fileURLWithPath(dir));
    }

    private static string environmentValue(string name) => ProcessInfo.processInfo.environment.TryGetValue(name, out var value) ? value : null;

    public void startTownBenchmark(URL at)
    {
        var directory = at;
        weatherOverride = environmentValue("MARVIN_SANDSTORM") == "1";
        try { FileManager.@default.createDirectory(directory, withIntermediateDirectories: true); } catch (Exception) { }
        startDirtTrack(); dirtIntro = null; race.countDown(dt: 3);
        if (environmentValue("MARVIN_DAYLIGHT_FRACTION") is string text && double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fraction) && double.IsFinite(fraction))
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
        var args = CommandLine.arguments;
        if (args.Contains("--city-roam"))
        {
            simulation = new Simulation(dirtTrack: true, dirtStartOffset: DirtCourse.fenceOffset + 8.08, dirtStartPhase: CityExit.phase);
            racePhysics.gate.wantsOpen = true;
            var current = new Double2(simulation.x, simulation.z);
            var planner = new TownEscapeRoute(city: dirtWorld.town.collisionWorld, origin: current);
            foreach (var destination in new[] { new Double2(29, -10), new Double2(33, 3), new Double2(35, 14), new Double2(36, 28) })
            {
                if (planner.route(current, destination) is Double2[] route) { townBenchmarkRoute.AddRange(route); current = route[^1]; }
            }
            townBenchmarkRoute.AddRange(townBenchmarkRoute.AsEnumerable().Reverse().ToList());
        }
        if (args.Contains("--dune-roam"))
        {
            var p = DirtCourse.projection(x: 175, z: 57);
            simulation = new Simulation(dirtTrack: true, dirtStartOffset: p.distance, dirtStartPhase: p.phase);
            townBenchmarkRoute = new List<Double2> { new Double2(205, 65), new Double2(178, 57) };
        }
        if (args.Contains("--postrace-roam"))
        {
            var roamingFrames = 0;
            for (var i = 0; i < 54000; i++)
            {
                advanceRacePhysics(DirtOpponent.driveInput(simulation), dt: 1.0 / 60, raceDT: 1.0 / 60);
                if (racePhysics.escape.complete) { roamingFrames += 1; }
                if (roamingFrames >= 60 * 120) { break; }
            }
            if (!racePhysics.escape.complete) { throw new InvalidOperationException("Post-race benchmark did not reach town roaming"); }
            updateDepartureHUD();
        }
        dirtWorld.town.explorationDetailEnabled = !args.Contains("--benchmark-simple-town");
        updateOpponents();
        // Explicit 960x540 points at 2x backing gives the target 1080p drawable.
        window.minSize = new NSSize(640, 400);
        window.setContentSize(new NSSize(960, 540));
        if (args.Contains("--benchmark-msaa2")) { view.antialiasingMode = SCNAntialiasingMode.multisampling2X; }
        if (args.Contains("--benchmark-no-shadows"))
        {
            dirtWorld.scene.rootNode.enumerateChildNodes((node, _) => { if (node.light != null) node.light.castsShadow = false; });
        }
        if (args.Contains("--benchmark-no-ssao")) { world.camera.camera.screenSpaceAmbientOcclusionIntensity = 0; }
        if (args.Contains("--benchmark-no-bloom")) { world.camera.camera.bloomIntensity = 0; }
        if (args.Contains("--benchmark-flat-ground"))
        {
            dirtWorld.scene.rootNode.enumerateChildNodes((node, _) =>
            {
                foreach (var material in node.geometry?.materials ?? new List<SCNMaterial>())
                {
                    if ((material.shaderModifiers ?? new Dictionary<SCNShaderModifierEntryPoint, string>()).Values.Any(v => v.Contains("townNoise")))
                    {
                        material.shaderModifiers = null; material.lightingModel = SCNMaterial.LightingModel.constant;
                    }
                }
            });
        }
        // Godot-only measurement hook: MARVIN_BENCHMARK_ON_TOP=1 keeps the window above other windows. Godot (like AppKit)
        // stops drawing a window that is fully covered, so on a shared desktop another app's window would pause the
        // rendered-frame ledger while the timer keeps ticking.
        if (environmentValue("MARVIN_BENCHMARK_ON_TOP") == "1") { window.setAlwaysOnTop(true); }
        view.@delegate = townMeter;
        townBenchmarkStart = ProcessInfo.processInfo.systemUptime;
        // PORT: os_signpost(.event, "TownBenchmarkStart") (Instruments points of interest) has no Godot equivalent.
        var backing = view.convertToBacking(view.bounds);
        var identity = new Dictionary<string, object> { ["runID"] = townBenchmarkRunID, ["startUptime"] = townBenchmarkStart.Value, ["gpuDevice"] = view.device?.name ?? "Unavailable", ["resolution"] = new[] { backing.width, backing.height } };
        FileHandle.standardOutput.write(Encoding.UTF8.GetBytes("MARVIN_BENCHMARK_ID " + JSONSerialization.@string(identity, JSONSerialization.WritingOptions.sortedKeys) + "\n"));
        frameRateHUD.resetSamples(); frameRateHUD.isHidden = false;
        townMeter.fpsHUD = frameRateHUD; townBenchmarkHUDSamples = new();
        frameRateHUD.onSample = (now, fps) =>
        {
            if (!(townBenchmarkStart is double start && now - start >= 3.5)) { return; }
            townBenchmarkHUDSamples.Add(new Dictionary<string, object> { ["elapsedSeconds"] = now - start, ["fps"] = fps, ["text"] = frameRateHUD.displayedText });
            if (townBenchmarkHUDSamples.Count % 120 == 0)
            {
                var status = Swift.format("Town drive %.0f s · %@\n", now - start, frameRateHUD.displayedText);
                FileHandle.standardOutput.write(Encoding.UTF8.GetBytes(status));
            }
        };
        townMeter.reset(); townBenchmarkCPU = new();
        townBenchmarkDirectory = directory;
        godotTelemetry = new GodotFrameTelemetry(view.GodotViewport);
        dirtWorld.town.root.isHidden = args.Contains("--without-town");
    }

    /// Godot-only render telemetry of the benchmark (godot-render.json).
    public GodotFrameTelemetry godotTelemetry;

    public void tickTownBenchmark(double now, double dt)
    {
        if (!(townBenchmarkStart is double start && townBenchmarkDirectory is URL directory)) { return; }
        var elapsed = now - start;
        // PORT: benchmarkGPUCapture.update(elapsed:device:directory:) (Xcode GPU trace) is not ported.
        if (elapsed < 3) { townMeter.reset(); townBenchmarkCPU = new(); townBenchmarkTimeline = new(); }
        var begin = ProcessInfo.processInfo.systemUptime;
        // Godot-only telemetry (godot-render.json): bytes the main thread allocates per phase of the tick.
        long allocationBegin = GC.GetAllocatedBytesForCurrentThread();
        var allocation = new long[6];
        var args = CommandLine.arguments;
        var isolateTrails = args.Contains("--benchmark-isolate-trails");
        var trailsHidden = isolateTrails && ((elapsed >= 300 && elapsed < 330) || (elapsed >= 480 && elapsed < 510));
        if (isolateTrails) { dirtWorld.setBenchmarkTrailsHidden(trailsHidden); }
        var lastSecond = townBenchmarkResourceSamples.Count > 0 && townBenchmarkResourceSamples[^1]["second"] is int s ? s : -1;
        if ((int)elapsed >= lastSecond + 1)
        {
            townBenchmarkResourceSamples.Add(new Dictionary<string, object> { ["second"] = (int)elapsed, ["uptime"] = now, ["thermalState"] = (int)ProcessInfo.processInfo.thermalState, ["trailsHidden"] = trailsHidden, ["shadowCasters"] = dirtWorld.town.shadowCasterCount, ["trails"] = dirtWorld.trailDiagnostics(), ["shadowBatch"] = dirtWorld.town.shadowBatchTelemetry });
        }
        var input = DirtOpponent.driveInput(simulation);
        if (townBenchmarkRoute.Count > 0)
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
        allocation[0] = GC.GetAllocatedBytesForCurrentThread();
        updateOpponents();
        var modelsEnd = ProcessInfo.processInfo.systemUptime;
        allocation[1] = GC.GetAllocatedBytesForCurrentThread();
        updateRaceWorld(dt: dt);
        var effectsEnd = ProcessInfo.processInfo.systemUptime;
        allocation[2] = GC.GetAllocatedBytesForCurrentThread();
        var aerial = !raceCameraLocked && townBenchmarkRoute.Count == 0 && !args.Contains("--benchmark-chase-only") && elapsed % 24 > 18;
        if (aerial)
        {
            world.camera.position = new SCNVector3(0, 42, -44); world.camera.look(at: new SCNVector3(0, 0, 0), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
        }
        else { updateCamera(snap: false, dt: dt); }
        var detailPlayer = new Double2(simulation.x, simulation.z);
        if (args.Contains("--outer-town-survey"))
        {
            var fraction = 0.10 + 0.85 * min(1, elapsed / 45);
            detailPlayer = dirtWorld.town.explorationSurveyPoint(fraction);
            dirtWorld.sky.updateShadowCenter(new Double3(detailPlayer.x, 0, detailPlayer.y));
            var ahead = dirtWorld.town.explorationSurveyPoint(min(1, fraction + 0.025));
            world.camera.position = new SCNVector3(detailPlayer.x, 1.5, detailPlayer.y);
            world.camera.look(at: new SCNVector3(ahead.x, 1.5, ahead.y), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
        }
        var cameraEnd = ProcessInfo.processInfo.systemUptime;
        allocation[3] = GC.GetAllocatedBytesForCurrentThread();
        if (!dirtWorld.town.root.isHidden)
        {
            dirtWorld.town.update(dt: dt, camera: world.camera.position, player: detailPlayer, robots: robotBodies(),
                visible: node => view.isNode(node, insideFrustumOf: world.camera), shadowCamera: world.camera, viewportAspect: (double)(view.bounds.width / view.bounds.height));
        }
        allocation[4] = GC.GetAllocatedBytesForCurrentThread();
        updateRaceAudio(dt: dt, advancing: true);
        var finish = ProcessInfo.processInfo.systemUptime;
        allocation[5] = GC.GetAllocatedBytesForCurrentThread();
        townBenchmarkCPU.Add(finish - begin);
        for (int i = 5; i > 0; i--) allocation[i] -= allocation[i - 1];
        allocation[0] -= allocationBegin;
        godotTelemetry?.sample(elapsed, allocation);
        townBenchmarkTimeline.Add(new[] { now, elapsed, dt * 1000, (physicsEnd - begin) * 1000, (modelsEnd - physicsEnd) * 1000, (effectsEnd - modelsEnd) * 1000, (cameraEnd - effectsEnd) * 1000, (finish - cameraEnd) * 1000, (finish - begin) * 1000, simulation.x, simulation.z, aerial ? 1 : 0 });
        var duration = double.TryParse(environmentValue("MARVIN_BENCHMARK_SECONDS") ?? "45", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? seconds : 45;
        if (elapsed >= max(10, duration))
        {
            // PORT: os_signpost(.event, "TownBenchmarkEnd") is not ported.
            timer?.invalidate(); view.@delegate = null;
            frameDisplayLink?.invalidate();
            var report = townMeter.report();
            var cpu = townBenchmarkCPU.OrderBy(v => v).ToList();
            report["cpuUpdateP95MS"] = cpu.Count == 0 ? 0 : cpu[(int)((double)(cpu.Count - 1) * 0.95)] * 1000;
            report["durationSeconds"] = elapsed; report["townEnabled"] = !dirtWorld.town.root.isHidden;
            report["postRaceRoaming"] = racePhysics.escape.active;
            report["town"] = dirtWorld.town.statistics;
            report["activeExplorationCells"] = dirtWorld.town.activeExplorationCells;
            report["sandstorm"] = racePhysics.storm.enabled;
            report["visiblePeople"] = dirtWorld.town.visiblePopulation;
            report["residentUpdates"] = (object)dirtWorld.town.residents?.updateStatistics ?? new Dictionary<string, int>();
            report["audioActive"] = raceAudio?.active ?? false;
            report["updateDriver"] = frameDisplayLink == null ? "timer" : "display-link";
            report["expressionsPlayed"] = raceAudio?.expressionCount ?? 0;
            report["metalRenderer"] = view.renderingAPI == SCNRenderingAPI.metal;
            report["gpuDevice"] = view.device?.name ?? "Unavailable";
            report["daylightFraction"] = dirtWorld.sky.daylight.fraction;
            report["sunElevationsDegrees"] = dirtWorld.sky.daylight.directions.Select(d => asin(d.y) * 180 / Math.PI).ToList();
            report["drawableWidth"] = view.convertToBacking(view.bounds).width;
            report["drawableHeight"] = view.convertToBacking(view.bounds).height;
            report["simulationSeconds"] = race.elapsed; report["laps"] = race.laps.Count();
            var shadowWidths = new List<int>();
            dirtWorld.scene.rootNode.enumerateChildNodes((node, _) =>
            {
                if (node.light is SCNLight light && light.castsShadow) { shadowWidths.Add((int)light.shadowMapSize.width); }
            });
            report["quality"] = new Dictionary<string, object> { ["msaaSamples"] = view.antialiasingMode == SCNAntialiasingMode.none ? 1 : (1 << (int)view.antialiasingMode), ["shadowMapWidths"] = shadowWidths.OrderBy(w => w).ToList(), ["explorationDetail"] = dirtWorld.town.explorationDetailEnabled };
            report["shadowBatch"] = dirtWorld.town.shadowBatchTelemetry;
            report["thermalState"] = (int)ProcessInfo.processInfo.thermalState;
            // PORT: CommandLine.arguments as the macOS app sees them (executable and game arguments), without Godot's
            // own engine arguments, so check-sustained-performance.py's flag rules apply unchanged.
            report["benchmarkArguments"] = CommandLine.gameArguments;
            report["benchmarkRunID"] = townBenchmarkRunID;
            report["startUptime"] = start;
            report["endWallTime"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            // Godot-only: the engine, renderer and C# build that produced the run.
            report["godot"] = benchmarkEngineReport();
            try
            {
                var timeline = new Dictionary<string, object> { ["renderColumns"] = new[] { "uptime", "intervalMS", "renderCallbackSpanMS", "rendererCycleMS", "sceneAnimationMS", "scenePhysicsMS", "sceneConstraintsMS", "preRenderMS" }, ["renderFrames"] = townMeter.timeline(), ["updateColumns"] = new[] { "uptime", "elapsed", "tickMS", "physicsMS", "modelsMS", "effectsMS", "cameraMS", "townMS", "totalMS", "x", "z", "aerial" }, ["updates"] = townBenchmarkTimeline };
                JSONSerialization.data(timeline, JSONSerialization.WritingOptions.none).write(directory.appendingPathComponent("timeline.json"));
                JSONSerialization.data(report, JSONSerialization.WritingOptions.prettyPrinted | JSONSerialization.WritingOptions.sortedKeys).write(directory.appendingPathComponent("benchmark.json"));
                JSONSerialization.data(townBenchmarkResourceSamples, JSONSerialization.WritingOptions.prettyPrinted | JSONSerialization.WritingOptions.sortedKeys).write(directory.appendingPathComponent("resources.json"));
                JSONSerialization.data(townBenchmarkHUDSamples, JSONSerialization.WritingOptions.prettyPrinted | JSONSerialization.WritingOptions.sortedKeys).write(directory.appendingPathComponent("displayed-fps.json"));
                // Godot-only: GPU and render telemetry of the 3D view (no macOS counterpart).
                if (godotTelemetry != null) JSONSerialization.data(godotTelemetry.report(), JSONSerialization.WritingOptions.prettyPrinted | JSONSerialization.WritingOptions.sortedKeys).write(directory.appendingPathComponent("godot-render.json"));
                // SCNView snapshots omit AppKit overlays; draw the actual live
                // counter over the native scene at its unchanged view position.
                // PORT: the facade caches the counter over a transparent base and draws it at its frame (same pixels).
                var image = new NSImage(view.bounds.size); image.lockFocus();
                view.snapshot().draw(view.bounds);
                var counter = frameRateHUD.bitmapImageRepForCachingDisplay(frameRateHUD.bounds);
                frameRateHUD.cacheDisplay(frameRateHUD.bounds, counter);
                counter.draw(frameRateHUD.frame);
                image.unlockFocus();
                if (NSBitmapImageRep.data(image.tiffRepresentation)?.representation(NSBitmapImageFileType.png) is byte[] png)
                {
                    png.write(directory.appendingPathComponent("final-fps.png"));
                }
                foreach (var flag in new[] { "--benchmark-marvin-visibility-audit", "--benchmark-visibility-audit", "--benchmark-gpu-probe" })
                {
                    if (args.Contains(flag)) { print($"{flag}: Apple GPU diagnostic, not ported to Godot"); }
                }
                print($"Town benchmark complete · {directory.path}");
                exit(0);
            }
            catch (Exception error) { print(error.ToString()); exit(1); }
        }
    }

    /// Godot-only benchmark context: engine version, rendering driver and method, adapter, the C# build configuration
    /// and whether the engine binary is a debug (editor) build.
    public Dictionary<string, object> benchmarkEngineReport()
    {
        var version = Godot.Engine.GetVersionInfo();
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        bool optimized = typeof(AppController).Assembly.GetCustomAttributes(typeof(System.Diagnostics.DebuggableAttribute), false)
            .OfType<System.Diagnostics.DebuggableAttribute>().All(a => !a.IsJITOptimizerDisabled);
        return new Dictionary<string, object>
        {
            ["version"] = (string)version["string"],
            ["renderingDriver"] = Godot.RenderingServer.GetCurrentRenderingDriverName(),
            ["renderingMethod"] = Godot.RenderingServer.GetCurrentRenderingMethod(),
            ["videoAdapter"] = Godot.RenderingServer.GetVideoAdapterName(),
            ["engineDebugBuild"] = Godot.OS.IsDebugBuild(),
            ["engineEditorBuild"] = Godot.OS.HasFeature("editor"),
            ["csharpConfiguration"] = configuration,
            ["csharpOptimized"] = optimized,
            ["backingScaleFactor"] = window?.backingScaleFactor ?? 1,
        };
    }
}
