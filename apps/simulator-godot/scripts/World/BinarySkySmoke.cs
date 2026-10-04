using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

/// <summary>
/// `--binary-sky-smoke-test DIR` (BinarySkySmoke.swift, AppController.checkBinaryRaces). Same races, cameras, captures
/// (NAME-race.png, NAME-suns.png, sun-visible.png, sun-occluded.png) and binary-races.json keys.
/// PORT: no robots or town (other streams). MARVIN_DAYLIGHT_REFERENCE=path/to/binary-races.json reuses a macOS run's
/// daylight fraction and binary phase per capture so the skies compare 1:1; otherwise they are random as on macOS.
/// </summary>
public static class BinarySkySmoke
{
    [GameMode("--binary-sky-smoke-test")]
    public static Task Run(string dir, SceneTree tree)
    {
        bool passed = checkBinaryRaces(new RaceWorldHarness(tree), dir);
        tree.Quit(passed ? 0 : 1);
        return Task.CompletedTask;
    }

    private static Dictionary<string, (double fraction, double phase)> referenceDaylight()
    {
        var path = System.Environment.GetEnvironmentVariable("MARVIN_DAYLIGHT_REFERENCE");
        var result = new Dictionary<string, (double, double)>();
        if (path == null || !File.Exists(path)) return result;
        var root = Json.ParseString(File.ReadAllText(path)).AsGodotDictionary();
        foreach (var race in root["races"].AsGodotArray())
        {
            var r = race.AsGodotDictionary();
            result[r["time"].AsString()] = (r["daylightFraction"].AsDouble(), r["binaryPhase"].AsDouble());
        }
        return result;
    }

    public static bool checkBinaryRaces(RaceWorldHarness app, string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            bool passed = true; var reports = new List<Dictionary<string, object>>();
            for (int k = 0; k < 2000; k++)
            {
                var s = BinaryDaylight.random();
                passed = passed && s.directions.All(d => d.y > 0 && abs(Simd.length(d) - 1) < 1e-10)
                    && s.separationDegrees > 1.1 && s.separationDegrees < 13;
            }
            app.startDirtTrack();
            bool horizonCapture = System.Environment.GetEnvironmentVariable("MARVIN_HORIZON_CAPTURE") == "1";
            var times = horizonCapture ? new[] { ("sunrise", 0.015, 0.015), ("sunset", 0.985, 0.985) }
                : new[] { ("morning", 0.025, 0.045), ("midday", 0.43, 0.57), ("evening", 0.95, 0.975) };
            var pinned = referenceDaylight();
            foreach (var (name, low, high) in times)
            {
                app.reset(); app.dirtIntro = null; app.race.countDown(dt: 3);
                // Stratified random times exercise the full daylight range.
                var s = new BinaryDaylight(fraction: SwiftRandom.doubleClosed(low, high), phase: horizonCapture ? 0.35 : SwiftRandom.doubleClosed(0.8, 2.0));
                if (pinned.TryGetValue(name, out var reference)) s = new BinaryDaylight(fraction: reference.fraction, phase: reference.phase);
                app.dirtWorld.sky.apply(s);
                for (int i = 0; i < app.dirtWorld.sky.suns.Length; i++)
                {
                    var m = SimdBridge.M(app.dirtWorld.sky.suns[i].simdWorldTransform);
                    var forward = -new Double3(m.m31, m.m32, m.m33);
                    passed = passed && Simd.dot(forward, -s.directions[i]) > 0.99999;
                }
                bool captured = false;
                for (int frame = 0; frame < 40000; frame++)
                {
                    app.advanceRacePhysics(DirtOpponent.driveInput(app.simulation), dt: 1.0 / 60, raceDT: 1.0 / 60);
                    if (frame == 600)
                    {
                        app.updateRaceWorld(dt: 1.0 / 60);
                        app.camera.position = new SCNVector3(0, 38, -33);
                        app.camera.look(SCNVector3Zero, up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                        app.saveTownFrame($"{name}-race", directory);
                        var direction = Simd.normalize(s.directions[0] + s.directions[1]);
                        var horizontal = Simd.normalize(new Double3(direction.x, 0, direction.z));
                        var eye = -horizontal * 27 + new Double3(0, 7.5, 0);
                        app.camera.position = new SCNVector3(eye.x, eye.y, eye.z);
                        var framing = name == "midday" ? direction : Simd.normalize(horizontal + new Double3(0, 0.01, 0));
                        var at = eye + framing * 80;
                        app.camera.look(new SCNVector3(at.x, at.y, at.z), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                        app.saveTownFrame($"{name}-suns", directory);
                        captured = true;
                    }
                    if (new[] { app.race }.Concat(app.opponents.Select(o => o.race)).All(r => r.finished)) break;
                }
                var races = new[] { app.race }.Concat(app.opponents.Select(o => o.race)).ToArray();
                bool finished = races.All(r => r.finished);
                bool stable = app.dirtWorld.sky.daylight.fraction == s.fraction && app.dirtWorld.sky.daylight.phase == s.phase;
                passed = passed && finished && captured && stable;
                var report = new Dictionary<string, object> { ["time"] = name, ["daylightFraction"] = s.fraction, ["binaryPhase"] = s.phase, ["separationDegrees"] = s.separationDegrees, ["elevationsDegrees"] = s.directions.Select(d => asin(d.y) * 180 / Math.PI).ToList(), ["allFinished"] = finished, ["laps"] = races.Select(r => r.laps.Length).ToList(), ["stableDuringRace"] = stable };
                reports.Add(report); GD.Print($"{name}: fraction {Swift.description(s.fraction)} phase {Swift.description(s.phase)} finished {finished} laps [{string.Join(", ", races.Select(r => r.laps.Length))}]");
            }
            double previous = app.dirtWorld.sky.daylight.fraction;
            app.reset();
            passed = passed && previous != app.dirtWorld.sky.daylight.fraction;
            bool occlusion = checkSunOcclusion(directory);
            passed = passed && occlusion;
            RaceWorldHarness.writeJSON(Path.Combine(directory, "binary-races.json"), new Dictionary<string, object> { ["passed"] = passed, ["daylightSamples"] = 2000, ["occlusion"] = occlusion, ["races"] = reports });
            GD.Print($"Binary daylight races: {(passed ? "PASS" : "FAIL")}"); return passed;
        }
        catch (Exception error) { GD.Print($"Binary daylight: {error}"); return false; }
    }
    /// Render a sun directly behind an opaque screen. Its glare must disappear,
    /// including the bloom input, rather than being an always-visible HUD sprite.
    private static bool checkSunOcclusion(string directory)
    {
        SCNScene scene = new SCNScene(); SCNNode camera = new SCNNode();
        var sky = new BinarySky(scene: scene);
        camera.camera = new SCNCamera(); camera.camera.zFar = 250; sky.attach(camera: camera);
        scene.rootNode.addChildNode(camera);
        var s = new BinaryDaylight(fraction: 0.2, phase: 1.2); sky.apply(s);
        var d = s.directions[0];
        camera.look(new SCNVector3(d.x * 20, d.y * 20, d.z * 20), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
        var screen = new SCNNode(new SCNPlane(width: 4, height: 4));
        screen.position = new SCNVector3(d.x * 5, d.y * 5, d.z * 5); screen.look(SCNVector3Zero);
        var material = new SCNMaterial(); material.lightingModel = SCNMaterial.LightingModel.constant; material.diffuse.contents = NSColor.black; material.isDoubleSided = true;
        screen.geometry.materials = new() { material }; scene.rootNode.addChildNode(screen);
        var renderer = new SCNRenderer(device: null, options: null); renderer.scene = scene; renderer.pointOfView = camera;
        var luminance = new List<double>();
        foreach (var hidden in new[] { true, false })
        {
            screen.isHidden = hidden;
            var image = renderer.snapshot(atTime: 0, with: new CGSize(256, 256), antialiasingMode: SCNAntialiasingMode.multisampling4X);
            var bitmap = NSBitmapImageRep.data(image.tiffRepresentation);
            double sum = 0.0;
            for (int y = 120; y < 136; y++)
                for (int x = 120; x < 136; x++)
                {
                    var c = bitmap.colorAt(x, y).usingColorSpace(NSColorSpace.deviceRGB);
                    sum += (c.redComponent + c.greenComponent + c.blueComponent) / 3;
                }
            luminance.Add(sum / 256);
            File.WriteAllBytes(Path.Combine(directory, hidden ? "sun-visible.png" : "sun-occluded.png"), bitmap.representation(NSBitmapImageFileType.png));
        }
        GD.Print($"Sun occlusion luminance: {Swift.description(luminance)}");
        return luminance[0] > 0.6 && luminance[1] < 0.05;
    }
}
