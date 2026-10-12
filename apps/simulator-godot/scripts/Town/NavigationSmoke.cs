using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

// Port of NavigationSmoke.swift (an AppController extension). The daylight is fixed (0.5, phase 1.2) and the weather
// clear; the rivals keep the random grid of startDirtTrack()'s reset (pin it with MARVIN_GRID_SLOTS, TestPins).
// MARVIN_AUDIO_CAPTURE=1 renders the offline race audio of the drive into navigation-audio.wav and
// audio-timeline.json, as on macOS.
public partial class AppController
{
    public bool checkNavigation(string at)
    {
        var directory = at;
        try
        {
            Directory.CreateDirectory(directory);
            weatherOverride = false;
            RaceAudio navigationAudio = null; var recordAudio = false;
            var audioTimeline = new List<Dictionary<string, object>>();
            try
            {
                startDirtTrack(); dirtIntro = null; race.countDown(dt: 3);
                dirtWorld.sky.apply(new BinaryDaylight(fraction: 0.5, phase: 1.2));
                simulation = new Simulation(dirtTrack: true, dirtStartPhase: CityExit.phase);
                race = new DirtRace(startPhase: CityExit.phase); race.countDown(dt: 3);
                recordAudio = Environment.GetEnvironmentVariable("MARVIN_AUDIO_CAPTURE") == "1";
                navigationAudio = recordAudio ? new RaceAudio(resources: resources, offline: true) : null;
                var audioFormat = new AVAudioFormat(standardFormatWithSampleRate: 48000, channels: 2);
                var audioBuffer = new AVAudioPCMBuffer(pcmFormat: audioFormat, frameCapacity: 800);
                var audioFile = recordAudio ? new AVAudioFile(forWriting: Path.Combine(directory, "navigation-audio.wav"), settings: audioFormat.settings) : null;
                cameraMode = 0; updateCamera(snap: true);
                void key(string character, ushort code)
                {
                    var e = NSEvent.keyEvent(NSEvent.EventType.keyDown, NSPoint.zero, 0, 0, window.windowNumber, null, character, character, false, code);
                    view.keyDown(e); view.keyUp(e);
                }
                key("c", 8); key("c", 8); key("g", 5);
                bool checkingCameraSwitch = false, switchesPassed = true;
                int frames = 0; double maxCameraStep = 0.0, maxFollowError = 0.0; var transitionFrame = 0;
                var regions = new List<string> { RaceMapRegion.course.rawValue }; var route = new List<Double2>(); var captures = new List<string>();
                var city = dirtWorld.town.collisionWorld;
                print($"Town layout: [{string.Join(", ", dirtWorld.town.statistics.Select(kv => $"\"{kv.Key}\": {kv.Value}"))}]");
                var renderer = new SCNRenderer(view.device, null); renderer.scene = dirtWorld.scene; renderer.pointOfView = world.camera;
                void capture(string name, bool hud = true)
                {
                    raceHUD.race = race; raceHUD.opponents = opponents; raceHUD.x = simulation.x; raceHUD.z = simulation.z; raceHUD.heading = simulation.heading; raceHUD.introducing = false; raceHUD.helpVisible = false;
                    dirtWorld.town.update(dt: 0, camera: world.camera.position, player: new Double2(simulation.x, simulation.z));
                    var size = new NSSize(1280, 820); var old = raceHUD.frame;
                    raceHUD.frame = new NSRect(NSPoint.zero, size);
                    NSImage image = new NSImage(size), background = renderer.snapshot((double)frames / 60, size, SCNAntialiasingMode.multisampling4X);
                    // PORT: Swift draws into a flipped focus (lockFocusFlipped(true)) and calls raceHUD.draw(_:) directly; the
                    // facade's views draw through cacheDisplay, which renders the HUD over a transparent base, so the overlay is
                    // cached and drawn over the snapshot (as RaceFinishSmoke.cs; same pixels).
                    image.lockFocus();
                    background.draw(new NSRect(NSPoint.zero, size), NSRect.zero, NSCompositingOperation.copy, 1, respectFlipped: true, hints: null);
                    if (hud)
                    {
                        var overlay = raceHUD.bitmapImageRepForCachingDisplay(raceHUD.bounds);
                        raceHUD.cacheDisplay(raceHUD.bounds, overlay);
                        overlay.draw(raceHUD.bounds);
                    }
                    image.unlockFocus(); raceHUD.frame = old;
                    var bitmap = NSBitmapImageRep.data(image.tiffRepresentation);
                    File.WriteAllBytes(Path.Combine(directory, name + ".png"), bitmap.representation(NSBitmapImageFileType.png));
                    captures.Add(name);
                }
                void verifyTownImpressions(string location)
                {
                    var pose = world.camera.transform;
                    SCNCamera camera = world.camera.camera; bool ortho = camera.usesOrthographicProjection; var scale = camera.orthographicScale;
                    SCNNode receiver = null;
                    try
                    {
                        camera.usesOrthographicProjection = true; camera.orthographicScale = 3;
                        world.camera.position = new SCNVector3(simulation.x, simulation.groundY + 12, simulation.z);
                        world.camera.look(at: new SCNVector3(simulation.x, simulation.groundY, simulation.z), up: new SCNVector3(0, 0, -1), localFront: new SCNVector3(0, 0, -1));
                        var roots = dirtWorld.scene.rootNode.childNodesPassingTest((node, _) => node.name == "Surface-aware ground impressions");
                        NSBitmapImageRep shot(bool visible, int order)
                        {
                            foreach (var root in roots) { root.isHidden = !visible; foreach (var child in root.childNodes) { child.renderingOrder = order; } }
                            return NSBitmapImageRep.data(renderer.snapshot((double)frames / 60, new CGSize(800, 800), SCNAntialiasingMode.multisampling4X).tiffRepresentation);
                        }
                        NSBitmapImageRep @base = shot(false, 10), old = shot(true, 0), @fixed = shot(true, 10);
                        int oldPixels = 0, fixedPixels = 0;
                        for (var y = 0; y < 800; y++)
                        {
                            for (var x = 0; x < 800; x++)
                            {
                                var a = @base.colorAt(x, y).usingColorSpace(NSColorSpace.deviceRGB);
                                foreach (var (index, b) in new[] { old, @fixed }.Select((b, i) => (i, b)))
                                {
                                    var c = b.colorAt(x, y).usingColorSpace(NSColorSpace.deviceRGB);
                                    if (a.redComponent - c.redComponent > 0.015 && a.greenComponent - c.greenComponent > 0.015)
                                    {
                                        if (index == 0) { oldPixels += 1; } else { fixedPixels += 1; }
                                    }
                                }
                            }
                        }
                        foreach (var (name, bitmap) in new[] { ("town-treads-default-order", old), ("town-treads-after", @fixed) })
                        {
                            File.WriteAllBytes(Path.Combine(directory, name + "-" + location + ".png"), bitmap.representation(NSBitmapImageFileType.png));
                        }
                        print($"Rendered town impressions {location}: default order {oldPixels}, corrected {fixedPixels} visible pixels");
                        if (!(fixedPixels > 100)) { throw new InvalidOperationException("Town impressions invisible"); }
                        // Reproduce the raised plaza receiver that previously buried
                        // impressions. Test final rendered pixels, not generated geometry.
                        receiver = new SCNNode(new SCNPlane(4, 4));
                        receiver.eulerAngles.x = -(CGFloat)Math.PI / 2;
                        receiver.position = new SCNVector3(simulation.x, 0.003, simulation.z);
                        var surface = new SCNMaterial(); surface.lightingModel = SCNMaterial.LightingModel.constant; surface.diffuse.contents = color(0xab9679);
                        receiver.geometry.materials = new() { surface }; dirtWorld.scene.rootNode.addChildNode(receiver);
                        NSBitmapImageRep clean = shot(false, 10), imprinted = shot(true, 10);
                        var receiverPixels = 0;
                        for (var y = 240; y < 560; y++)
                        {
                            for (var x = 240; x < 560; x++)
                            {
                                NSColor a = clean.colorAt(x, y).usingColorSpace(NSColorSpace.deviceRGB), b = imprinted.colorAt(x, y).usingColorSpace(NSColorSpace.deviceRGB);
                                if (a.redComponent - b.redComponent > 0.015 && a.greenComponent - b.greenComponent > 0.015) { receiverPixels += 1; }
                            }
                        }
                        print($"Raised town surface impressions {location}: {receiverPixels} visible pixels");
                        if (!(receiverPixels > 100)) { throw new InvalidOperationException("Town surface buried impressions"); }
                    }
                    finally
                    {
                        receiver?.removeFromParentNode();
                        world.camera.transform = pose; camera.usesOrthographicProjection = ortho; camera.orthographicScale = scale;
                    }
                }
                void streetCapture(string name)
                {
                    var pose = world.camera.transform; var motion = overviewMotion; var aim = cameraAim; var mode = cameraMode;
                    cameraMode = 0; updateCamera(snap: true);
                    capture(name);
                    world.camera.transform = pose; overviewMotion = motion; cameraAim = aim; cameraMode = mode;
                }
                void step(DriveInput? given = null)
                {
                    var input = given ?? new DriveInput();
                    var old = simdF3(world.camera.simdPosition); var region = raceHUD.mapRegion;
                    advanceRacePhysics(input, dt: 1.0 / 60, raceDT: 1.0 / 60);
                    updateOpponents(); updateRaceWorld(dt: 1.0 / 60); updateCamera(snap: false);
                    if (navigationAudio is RaceAudio audio)
                    {
                        var front = simdF3(world.camera.simdWorldFront);
                        audio.update(states: new[] { simulation }.Concat(opponents.Select(o => o.simulation)).ToArray(), lineup: lineup, zones: dirtWorld.town.spectatorSoundZones,
                                     heading: atan2((double)front.x, (double)front.z), storm: racePhysics.storm.enabled, racing: true, dt: 1.0 / 60,
                                     finished: race.finished, escaping: racePhysics.escape.active, progress: new[] { race }.Concat(opponents.Select(o => o.race)).Select(r => r.progress).ToArray(), town: dirtWorld.town.soundZones);
                        if (audio.engine.renderOffline(800, to: audioBuffer) != AVAudioEngineManualRenderingStatus.success) { throw new IOException("fileWriteUnknown"); }
                        audioFile?.write(from: audioBuffer);
                        if (frames % 60 == 0)
                        {
                            var mix = audio.lastMix;
                            audioTimeline.Add(new Dictionary<string, object>
                            {
                                ["second"] = frames / 60, ["x"] = simulation.x, ["z"] = simulation.z, ["speed"] = simulation.groundSpeed,
                                ["region"] = raceHUD.mapRegion.rawValue, ["motorGain"] = mix[0].gain, ["marketGain"] = mix[25].gain,
                                ["workshopGain"] = mix[26].gain, ["cantinaGain"] = mix[27].gain,
                            });
                        }
                    }
                    frames += 1;
                    if (frames > 180 && !checkingCameraSwitch) { maxCameraStep = max(maxCameraStep, (double)Simd.distance(old, simdF3(world.camera.simdPosition))); }
                    if (raceHUD.mapRegion != region)
                    {
                        transitionFrame = frames; regions.Add(raceHUD.mapRegion.rawValue);
                        print($"Navigation {regions[regions.Count - 1]}: {description(simulation.x)}, {description(simulation.z)}");
                        capture($"transition-{regions.Count}-{regions[regions.Count - 1]}");
                    }
                    if (!checkingCameraSwitch && frames - transitionFrame > 120 && raceHUD.mapRegion != RaceMapRegion.course)
                    {
                        maxFollowError = max(maxFollowError, Simd.distance(overviewMotion.aim, new Double3(simulation.x, simulation.groundY + 0.35, simulation.z)));
                    }
                    if (frames % 1200 == 0) { print($"Navigation simulated {frames / 60}s"); }
                }
                for (var i = 0; i < 600; i++) { step(); }
                if (!(cameraMode == 2)) { return false; }
                capture("01-course");
                var cameraPose = world.camera.transform; var far = world.camera.camera.zFar;
                double fogStart = dirtWorld.scene.fogStartDistance, fogEnd = dirtWorld.scene.fogEndDistance;
                world.camera.camera.zFar = 800; dirtWorld.scene.fogStartDistance = 500; dirtWorld.scene.fogEndDistance = 800;
                world.camera.position = new SCNVector3(0, 280, -180);
                world.camera.look(at: SCNVector3Zero, up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                capture("00-whole-town", hud: false);
                world.camera.transform = cameraPose; world.camera.camera.zFar = far;
                dirtWorld.scene.fogStartDistance = fogStart; dirtWorld.scene.fogEndDistance = fogEnd;
                bool drive(Double2 target)
                {
                    for (var i = 0; i < 5400; i++)
                    {
                        var d = target - new Double2(simulation.x, simulation.z); var distance = Simd.length(d);
                        if (distance < 0.25) { return true; }
                        var error = atan2(sin(atan2(d.x, d.y) - simulation.heading), cos(atan2(d.x, d.y) - simulation.heading));
                        var input = new DriveInput(); input.turn = max(-1, min(1, -error * 2.5)); input.throttle = abs(error) < 0.22 ? min(0.65, distance * 0.7) : 0;
                        step(input);
                    }
                    var body = new RobotCollisions.Body(position: new Double3(simulation.x, simulation.groundY, simulation.z), heading: simulation.heading, profile: RobotCollisions.profiles[(int)lineup[0]]);
                    print($"Navigation stuck: {description(simulation.x)},{description(simulation.z)} target SIMD2<Double>({description(target.x)}, {description(target.y)}), heading {description(simulation.heading)}, obstacles {dirtWorld.town.collisionWorld.nearby(body).Length}");
                    streetCapture("stuck-street"); capture("stuck-overview"); return false;
                }
                foreach (var p in new[] { CityExit.point(0, -1.1), CityExit.point(0, 2.5), CityExit.point(0, 8) }) { if (!drive(p)) { return false; } }
                capture("02-town-near-course");
                var apron = new Double2(simulation.x, simulation.z);
                var cursor = apron;
                foreach (var destination in new[] { new Double2(29.0, -10.0), new Double2(33, 3), new Double2(35, 14), new Double2(36, 30), new Double2(60, 48), new Double2(82, 44), new Double2(105, 42), new Double2(128, 47), new Double2(150, 52), new Double2(170, 56), new Double2(207, 65) })
                {
                    if (!(new TownEscapeRoute(city: city, origin: cursor).route(cursor, destination) is Double2[] leg)) { return false; }
                    foreach (var p in leg.Skip(1)) { if (!drive(p)) { return false; } route.Add(p); }
                    cursor = new Double2(simulation.x, simulation.z);
                    if (destination.x == 105) { capture("03-town"); verifyTownImpressions("outer-town"); }
                    if (destination.x == 60) { verifyTownImpressions("street"); }
                    if (new[] { 60.0, 105, 150 }.Contains(destination.x)) { streetCapture($"street-out-{(long)destination.x}"); }
                }
                for (var i = 0; i < 180; i++) { var input = new DriveInput(); input.brake = true; step(input); }
                capture("04-dunes");
                void verifyCameraCycle()
                {
                    checkingCameraSwitch = true;
                    key("c", 8); key("c", 8);
                    var before = simdF3(world.camera.simdPosition);
                    key("c", 8);
                    var entryStep = Simd.distance(before, simdF3(world.camera.simdPosition));
                    switchesPassed = switchesPassed && cameraMode == 2 && entryStep < 1;
                    for (var i = 0; i < 240; i++) { var input = new DriveInput(); input.brake = true; step(input); }
                    var targetY = max(simulation.groundY + 0.35, raceHUD.mapRegion == RaceMapRegion.dunes ? DirtCourse.height(x: simulation.x, z: simulation.z) + 0.35 : simulation.groundY + 0.35);
                    var error = Simd.distance(overviewMotion.aim, new Double3(simulation.x, targetY, simulation.z));
                    print($"Camera cycle {raceHUD.mapRegion.rawValue}: entry step {description(entryStep)}, settled error {description(error)}");
                    switchesPassed = switchesPassed && error < 0.05;
                    checkingCameraSwitch = false;
                }
                verifyCameraCycle();
                capture("04b-dunes-camera-cycle");
                // The same physical route in reverse exercises both re-entry boundaries.
                var returnShot = false;
                foreach (var p in Enumerable.Reverse(route.Take(route.Count - 1)).ToList())
                {
                    if (!drive(p)) { return false; }
                    if (!returnShot && simulation.x < 100) { streetCapture("street-return"); returnShot = true; }
                }
                if (!drive(apron)) { return false; }
                verifyCameraCycle();
                capture("05-town-return");
                foreach (var p in new[] { CityExit.point(0, 2.5), CityExit.point(0, -1.1) }) { if (!drive(p)) { return false; } }
                for (var i = 0; i < 180; i++) { var input = new DriveInput(); input.brake = true; step(input); }
                capture("06-course-return");
                var passed = switchesPassed && regions.SequenceEqual(new[] { "COURSE", "TOWN", "DUNES", "TOWN", "COURSE" }) && cameraMode == 2 && maxCameraStep < 1.2 && maxFollowError < 1.5 && Simd.length(overviewMotion.aim) < 0.1;
                var report = new Dictionary<string, object>
                {
                    ["passed"] = passed, ["cameraCyclesOutsidePassed"] = switchesPassed, ["regions"] = regions, ["simulatedSeconds"] = (double)frames / 60, ["maximumCameraStepMetres"] = maxCameraStep,
                    ["maximumFollowErrorMetres"] = maxFollowError, ["finalCourseAim"] = new List<double> { overviewMotion.aim.x, overviewMotion.aim.y, overviewMotion.aim.z }, ["mapPanelWidth"] = 270, ["captures"] = captures,
                };
                File.WriteAllText(Path.Combine(directory, "navigation.json"), JSONSerialization.prettyPrintedSortedKeys(report));
                print(JSONSerialization.prettyPrintedSortedKeys(report)); return passed;
            }
            finally
            {
                weatherOverride = null;
                navigationAudio?.stop();
                if (recordAudio) { File.WriteAllText(Path.Combine(directory, "audio-timeline.json"), JSONSerialization.prettyPrintedSortedKeys(audioTimeline)); }
            }
        }
        catch (Exception error) { print($"Navigation: {error}"); return false; }
    }

    [GameMode("--navigation-smoke-test")]
    public static async System.Threading.Tasks.Task RunNavigationSmokeTest(string dir, Godot.SceneTree tree)
    {
        var app = await launchSmoke(tree, dir);
        var passed = app.checkNavigation(at: dir);
        exit(passed ? 0 : 1);
    }
}
