using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

/// <summary>
/// `--dune-contact-test DIR` (DuneContactSmoke.swift, AppController.checkDuneContacts). Same four drivers, inputs,
/// photo frames, cameras (1440 x 900, 4x MSAA) and file names; dune-contact.json keeps the macOS keys.
/// PORT: robot models (and WALL-E's dirt coating) belong to the robots stream: the photos show the deformed sand,
/// tread marks and debris without the robots; wall-e-coating-off.png is not written and the coating/body pixel
/// measurements are reported as -1. MARVIN_DUNE_LIVE and MARVIN_DUNE_MOVIE are supported as on macOS
/// (live mode only paces the loop; it has no frame meter).
/// </summary>
public static class DuneContactSmoke
{
    private static readonly string[] displayNames = { "Marvin", "R2-D2", "BB-8", "WALL-E" }; // PlayerCharacter.swift displayName

    [GameMode("--dune-contact-test")]
    public static Task Run(string dir, SceneTree tree)
    {
        bool passed = checkDuneContacts(new RaceWorldHarness(tree), dir);
        tree.Quit(passed ? 0 : 1);
        return Task.CompletedTask;
    }

    public static bool checkDuneContacts(RaceWorldHarness app, string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            app.weatherOverride = false;
            try
            {
                app.startDirtTrack(); app.dirtIntro = null;
                app.dirtWorld.sky.apply(new BinaryDaylight(fraction: 0.35, phase: 1.2));
                bool live = System.Environment.GetEnvironmentVariable("MARVIN_DUNE_LIVE") == "1";
                bool movie = System.Environment.GetEnvironmentVariable("MARVIN_DUNE_MOVIE") == "1";
                if (movie) Directory.CreateDirectory(Path.Combine(directory, "movie"));
                var liveStart = Stopwatch.StartNew();
                var characters = RacePerformance.CharacterAllCases;
                var states = characters.Select((character, i) =>
                {
                    var p = DirtCourse.projection(211, 62 + (double)i * 3);
                    return new Simulation(dirtTrack: true, dirtStartOffset: p.offset, dirtStartPhase: p.phase, character: character);
                }).ToArray();
                var physics = characters.Select(character => new DirtRacePhysics(characters: new[] { character }.Concat(characters.Where(c => c != character)).ToArray())).ToArray();
                var races = characters.Select(_ => new DirtRace()).ToArray();
                var rivals = characters.Select(character =>
                    characters.Where(c => c != character).Select((c, i) => new DirtOpponent(slot: DirtCourse.startingGrid[i + 1])).ToArray()).ToArray();
                for (int i = 0; i < races.Length; i++) races[i].countDown(dt: 3);
                var renderer = new SCNRenderer(device: null, options: null); renderer.scene = app.dirtWorld.scene; renderer.pointOfView = app.camera;
                var cpu = new List<double>(); int maximumPatches = 0; double settleBefore = 0.0, settleAfter = 0.0;
                int contactPixels = -1, contactTop = -1, bodyPixels = -1; // PORT: needs the robot models (see summary)
                void photo(string name, Simulation state, double angle, double height = 0.7, double distance = 1.5)
                {
                    if (live) return;
                    var p = new Double2(state.x, state.z); var eye = p + new Double2(sin(angle), cos(angle)) * distance;
                    double ground = state.terrainHeight(eye.x, eye.y);
                    app.camera.position = new SCNVector3(eye.x, max(state.groundY + height, ground + 0.20), eye.y);
                    app.camera.look(new SCNVector3(state.x, state.groundY + 0.22, state.z), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                    app.dirtWorld.sky.updateShadowCenter(new Double3(state.x, state.groundY, state.z));
                    var image = renderer.snapshot(atTime: state.elapsed, with: new CGSize(1440, 900), antialiasingMode: SCNAntialiasingMode.multisampling4X);
                    var bitmap = NSBitmapImageRep.data(image.tiffRepresentation);
                    File.WriteAllBytes(Path.Combine(directory, name + ".png"), bitmap.representation(NSBitmapImageFileType.png));
                }
                for (int frame = 0; frame < 1800; frame++)
                {
                    double time = (double)frame / 60; var start = Stopwatch.StartNew();
                    for (int i = 0; i < states.Length; i++)
                    {
                        var input = new DriveInput();
                        if (time < 3)
                        {
                            double error = atan2(sin(0.72 - states[i].heading), cos(0.72 - states[i].heading));
                            input.turn = max(-1, min(1, -error * 2.5));
                        }
                        else if (time < 11) { input.throttle = 0.6; }
                        else if (time < 14) { input.brake = true; }
                        else if (time < 18) { input.throttle = -0.45; }
                        else if (time < 21) { input.turn = 0.45; }
                        else if (time < 26) { input.throttle = 0.55; }
                        else { input.brake = true; }
                        physics[i].sand = app.dirtWorld.duneSand.field;
                        physics[i].advance(input, ref states[i], ref races[i], rivals[i], dt: 1.0 / 60, raceDT: 1.0 / 60, robotCollisionsEnabled: false);
                    }
                    app.dirtWorld.update(states[0], opponent: states[1], dt: 1.0 / 60, modelScale: app.modelScale, additional: new[] { states[2], states[3] });
                    cpu.Add(start.Elapsed.TotalMilliseconds); maximumPatches = Math.Max(maximumPatches, app.dirtWorld.duneSand.patchCount);
                    if (frame == 660) { photo("wall-e-diagonal", states[3], angle: states[3].heading + 1.2); }
                    if (frame == 820)
                    {
                        photo("wall-e-stopped", states[3], angle: states[3].heading - 1.1, height: 0.4);
                        // PORT: wallE.dirtCoating.diagnosticContact(false) / wall-e-coating-off.png need the WALL-E model.
                    }
                    if (frame == 1079) { photo("wall-e-reverse", states[3], angle: states[3].heading + 2.4); }
                    if (frame == 1259) { photo("wall-e-pivot", states[3], angle: states[3].heading + 1.4, height: 0.45); }
                    if (frame == 1560) { settleBefore = app.dirtWorld.duneSand.field.tiles.Values.Sum(t => t.delta.Sum(v => (double)abs(v))); }
                    if (frame == 1799)
                    {
                        settleAfter = app.dirtWorld.duneSand.field.tiles.Values.Sum(t => t.delta.Sum(v => (double)abs(v)));
                        for (int i = 0; i < states.Length; i++) { photo($"{displayNames[(int)characters[i]]}-contact", states[i], angle: states[i].heading + 1.2); }
                        foreach (var (i, angle) in new[] { 0.0, 1.5, 3.0, 4.5 }.Select((a, i) => (i, a))) { photo($"wall-e-orbit-{i}", states[3], angle: angle, height: 0.35); }
                        photo("four-robots-dunes", states[1], angle: 2.5, height: 9, distance: 13);
                    }
                    if (movie && frame >= 480 && frame < 960 && frame % 3 == 0)
                    {
                        var state = states[3]; double angle = state.heading + 1.15;
                        var eye = new Double2(state.x, state.z) + new Double2(sin(angle), cos(angle)) * 1.7;
                        app.camera.position = new SCNVector3(eye.x, max(state.groundY + 0.6, state.terrainHeight(eye.x, eye.y) + 0.2), eye.y);
                        app.camera.look(new SCNVector3(state.x, state.groundY + 0.25, state.z), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                        app.dirtWorld.sky.updateShadowCenter(new Double3(state.x, state.groundY, state.z));
                        var image = renderer.snapshot(atTime: time, with: new CGSize(1280, 720), antialiasingMode: SCNAntialiasingMode.multisampling4X);
                        var bitmap = NSBitmapImageRep.data(image.tiffRepresentation);
                        File.WriteAllBytes(Path.Combine(directory, "movie", Swift.format("frame-%04d.jpg", (frame - 480) / 3)), bitmap.representation(NSBitmapImageFileType.jpeg));
                    }
                    if (live)
                    {
                        var state = states[3];
                        app.camera.position = new SCNVector3(state.x + 3, state.groundY + 2.5, state.z + 5);
                        app.camera.look(new SCNVector3(state.x, state.groundY + 0.25, state.z), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                        app.dirtWorld.sky.updateShadowCenter(new Double3(state.x, state.groundY, state.z));
                        double wait = max(0, (double)(frame + 1) / 60 - liveStart.Elapsed.TotalSeconds);
                        if (wait > 0) System.Threading.Thread.Sleep(TimeSpan.FromSeconds(wait));
                    }
                    if (frame % 300 == 0) { GD.Print($"Dune contact {(int)time}s: patches {maximumPatches}, displaced {Swift.description(app.dirtWorld.duneSand.field.displacedVolume)}"); }
                }
                double pausedClock = app.dirtWorld.duneSand.field.clock; int pausedUploads = app.dirtWorld.duneSand.updates;
                app.dirtWorld.update(states[0], opponent: states[1], dt: 0, modelScale: app.modelScale, additional: new[] { states[2], states[3] });
                bool pausePassed = pausedClock == app.dirtWorld.duneSand.field.clock && pausedUploads == app.dirtWorld.duneSand.updates;
                var heights = app.dirtWorld.duneSand.field.tiles.Values.SelectMany(t => t.delta).ToArray();
                float minimum = heights.Length > 0 ? heights.Min() : 0, maximum = heights.Length > 0 ? heights.Max() : 0;
                var sortedCPU = cpu.OrderBy(v => v).ToArray();
                var report = new Dictionary<string, object> { ["seconds"] = 30, ["maximumPatches"] = maximumPatches, ["minimumOffset"] = minimum, ["maximumOffset"] = maximum, ["displacedVolume"] = app.dirtWorld.duneSand.field.displacedVolume, ["heightTextureUploads"] = app.dirtWorld.duneSand.updates, ["cpuUpdateP95MS"] = sortedCPU[(int)((double)sortedCPU.Length * 0.95)], ["settleBefore"] = settleBefore, ["settleAfter"] = settleAfter, ["positions"] = states.Select(s => new[] { s.x, s.groundY, s.z }).ToList(), ["pausePassed"] = pausePassed, ["opaqueMarvinBodySamples"] = bodyPixels, ["contactCoatingChangedSamples"] = contactPixels, ["contactCoatingTopPixel"] = contactTop, ["finite"] = heights.All(float.IsFinite), ["emissions"] = app.dirtWorld.racerEmittedCount.ToList() };
                RaceWorldHarness.writeJSON(Path.Combine(directory, "dune-contact.json"), report);
                GD.Print($"Dune contact: patches {maximumPatches}, offsets {Swift.description(minimum)}..{Swift.description(maximum)}, uploads {app.dirtWorld.duneSand.updates}, emissions [{string.Join(", ", app.dirtWorld.racerEmittedCount)}]");
                // PORT: the coating and body-pixel conditions need the robot models and are left out.
                bool passed = pausePassed && minimum < -0.01 && maximum > 0.005 && maximumPatches <= SandDeformation.capacity && heights.All(h => float.IsFinite(h) && h > -0.14 && h < 0.11);
                app.dirtWorld.reset();
                return passed && app.dirtWorld.duneSand.patchCount == 0 && app.dirtWorld.duneSand.field.tiles.Count == 0;
            }
            finally { app.weatherOverride = null; }
        }
        catch (Exception error) { GD.Print($"Dune contact: {error}"); return false; }
    }
}
