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
/// photo frames, cameras (1440 x 900, 4x MSAA), file names and pixel checks; dune-contact.json keeps the macOS keys.
/// MARVIN_DUNE_LIVE and MARVIN_DUNE_MOVIE are supported as on macOS. PORT: live mode only paces the loop; it has no
/// TownFrameMeter (renderCadence) and does not resize the window.
/// </summary>
public partial class AppController
{
    [GameMode("--dune-contact-test")]
    public static async Task RunDuneContactTest(string dir, SceneTree tree)
    {
        var app = await launchSmoke(tree, dir);
        var passed = app.checkDuneContacts(at: dir);
        exit(passed ? 0 : 1);
    }

    public bool checkDuneContacts(string at)
    {
        var directory = at;
        try
        {
            Directory.CreateDirectory(directory);
            weatherOverride = false;
            try
            {
                startDirtTrack(); dirtIntro = null; setRaceControlsHidden(true);
                dirtWorld.sky.apply(new BinaryDaylight(fraction: 0.35, phase: 1.2));
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
                var renderer = new SCNRenderer(device: null, options: null); renderer.scene = dirtWorld.scene; renderer.pointOfView = world.camera;
                var cpu = new List<double>(); int maximumPatches = 0; double settleBefore = 0.0, settleAfter = 0.0;
                int contactPixels = 0, contactTop = 900, bodyPixels = 0;
                void photo(string name, Simulation state, double angle, double height = 0.7, double distance = 1.5)
                {
                    if (live) return;
                    var p = new Double2(state.x, state.z); var eye = p + new Double2(sin(angle), cos(angle)) * distance;
                    double ground = state.terrainHeight(eye.x, eye.y);
                    world.camera.position = new SCNVector3(eye.x, max(state.groundY + height, ground + 0.20), eye.y);
                    world.camera.look(new SCNVector3(state.x, state.groundY + 0.22, state.z), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                    dirtWorld.sky.updateShadowCenter(new Double3(state.x, state.groundY, state.z));
                    var image = renderer.snapshot(atTime: state.elapsed, with: new CGSize(1440, 900), antialiasingMode: SCNAntialiasingMode.multisampling4X);
                    var bitmap = NSBitmapImageRep.data(image.tiffRepresentation);
                    File.WriteAllBytes(Path.Combine(directory, name + ".png"), bitmap.representation(NSBitmapImageFileType.png));
                }
                NSBitmapImageRep load(string name) => NSBitmapImageRep.data(File.ReadAllBytes(Path.Combine(directory, name)));
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
                        physics[i].sand = dirtWorld.duneSand.field;
                        physics[i].advance(input, ref states[i], ref races[i], rivals[i], dt: 1.0 / 60, raceDT: 1.0 / 60, robotCollisionsEnabled: false);
                        updateModel(characters[i], state: states[i]);
                    }
                    dirtWorld.update(states[0], opponent: states[1], dt: 1.0 / 60, modelScale: robot.modelScale, additional: new[] { states[2], states[3] });
                    cpu.Add(start.Elapsed.TotalMilliseconds); maximumPatches = Math.Max(maximumPatches, dirtWorld.duneSand.patchCount);
                    if (frame == 660) { photo("wall-e-diagonal", states[3], angle: states[3].heading + 1.2); }
                    if (frame == 820)
                    {
                        photo("wall-e-stopped", states[3], angle: states[3].heading - 1.1, height: 0.4);
                        if (!live)
                        {
                            wallE.dirtCoating.diagnosticContact(false);
                            photo("wall-e-coating-off", states[3], angle: states[3].heading - 1.1, height: 0.4);
                            wallE.dirtCoating.diagnosticContact(true);
                            var on = load("wall-e-stopped.png"); var off = load("wall-e-coating-off.png");
                            for (int y = 0; y < on.pixelsHigh; y += 3)
                            {
                                for (int x = 0; x < on.pixelsWide; x += 3)
                                {
                                    NSColor a = on.colorAt(x, y).usingColorSpace(NSColorSpace.deviceRGB), b = off.colorAt(x, y).usingColorSpace(NSColorSpace.deviceRGB);
                                    if (max(abs(a.redComponent - b.redComponent), max(abs(a.greenComponent - b.greenComponent), abs(a.blueComponent - b.blueComponent))) > 0.02)
                                    {
                                        contactPixels += 1; contactTop = Math.Min(contactTop, y);
                                    }
                                }
                            }
                        }
                    }
                    if (frame == 1079) { photo("wall-e-reverse", states[3], angle: states[3].heading + 2.4); }
                    if (frame == 1259) { photo("wall-e-pivot", states[3], angle: states[3].heading + 1.4, height: 0.45); }
                    if (frame == 1560) { settleBefore = dirtWorld.duneSand.field.tiles.Values.Sum(t => t.delta.Sum(v => (double)abs(v))); }
                    if (frame == 1799)
                    {
                        settleAfter = dirtWorld.duneSand.field.tiles.Values.Sum(t => t.delta.Sum(v => (double)abs(v)));
                        for (int i = 0; i < states.Length; i++) { photo($"{characters[i].displayName()}-contact", states[i], angle: states[i].heading + 1.2); }
                        foreach (var (i, angle) in new[] { 0.0, 1.5, 3.0, 4.5 }.Select((a, i) => (i, a))) { photo($"wall-e-orbit-{i}", states[3], angle: angle, height: 0.35); }
                        photo("four-robots-dunes", states[1], angle: 2.5, height: 9, distance: 13);
                        if (!live)
                        {
                            var body = load("Marvin-contact.png");
                            // The fixed camera puts Marvin's opaque head here. A
                            // missing CAD texture-coordinate stream used to leave
                            // only eyes and belts visible without a shader error.
                            for (int y = 250; y < 430; y += 3)
                            {
                                for (int x = 550; x < 1000; x += 3)
                                {
                                    var c = body.colorAt(x, y).usingColorSpace(NSColorSpace.deviceRGB);
                                    if (c.redComponent * 0.30 + c.greenComponent * 0.59 + c.blueComponent * 0.11 < 0.5) { bodyPixels += 1; }
                                }
                            }
                        }
                    }
                    if (movie && frame >= 480 && frame < 960 && frame % 3 == 0)
                    {
                        var state = states[3]; double angle = state.heading + 1.15;
                        var eye = new Double2(state.x, state.z) + new Double2(sin(angle), cos(angle)) * 1.7;
                        world.camera.position = new SCNVector3(eye.x, max(state.groundY + 0.6, state.terrainHeight(eye.x, eye.y) + 0.2), eye.y);
                        world.camera.look(new SCNVector3(state.x, state.groundY + 0.25, state.z), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                        dirtWorld.sky.updateShadowCenter(new Double3(state.x, state.groundY, state.z));
                        var image = renderer.snapshot(atTime: time, with: new CGSize(1280, 720), antialiasingMode: SCNAntialiasingMode.multisampling4X);
                        var bitmap = NSBitmapImageRep.data(image.tiffRepresentation);
                        File.WriteAllBytes(Path.Combine(directory, "movie", Swift.format("frame-%04d.jpg", (frame - 480) / 3)), bitmap.representation(NSBitmapImageFileType.jpeg));
                    }
                    if (live)
                    {
                        var state = states[3];
                        world.camera.position = new SCNVector3(state.x + 3, state.groundY + 2.5, state.z + 5);
                        world.camera.look(new SCNVector3(state.x, state.groundY + 0.25, state.z), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                        dirtWorld.sky.updateShadowCenter(new Double3(state.x, state.groundY, state.z));
                        double wait = max(0, (double)(frame + 1) / 60 - liveStart.Elapsed.TotalSeconds);
                        if (wait > 0) System.Threading.Thread.Sleep(TimeSpan.FromSeconds(wait));
                    }
                    if (frame % 300 == 0) { GD.Print($"Dune contact {(int)time}s: patches {maximumPatches}, displaced {Swift.description(dirtWorld.duneSand.field.displacedVolume)}"); }
                }
                double pausedClock = dirtWorld.duneSand.field.clock; int pausedUploads = dirtWorld.duneSand.updates;
                dirtWorld.update(states[0], opponent: states[1], dt: 0, modelScale: robot.modelScale, additional: new[] { states[2], states[3] });
                bool pausePassed = pausedClock == dirtWorld.duneSand.field.clock && pausedUploads == dirtWorld.duneSand.updates;
                var heights = dirtWorld.duneSand.field.tiles.Values.SelectMany(t => t.delta).ToArray();
                float minimum = heights.Length > 0 ? heights.Min() : 0, maximum = heights.Length > 0 ? heights.Max() : 0;
                var sortedCPU = cpu.OrderBy(v => v).ToArray();
                var report = new Dictionary<string, object> { ["seconds"] = 30, ["maximumPatches"] = maximumPatches, ["minimumOffset"] = minimum, ["maximumOffset"] = maximum, ["displacedVolume"] = dirtWorld.duneSand.field.displacedVolume, ["heightTextureUploads"] = dirtWorld.duneSand.updates, ["cpuUpdateP95MS"] = sortedCPU[(int)((double)sortedCPU.Length * 0.95)], ["settleBefore"] = settleBefore, ["settleAfter"] = settleAfter, ["positions"] = states.Select(s => new[] { s.x, s.groundY, s.z }).ToList(), ["pausePassed"] = pausePassed, ["opaqueMarvinBodySamples"] = bodyPixels, ["contactCoatingChangedSamples"] = contactPixels, ["contactCoatingTopPixel"] = contactTop, ["finite"] = heights.All(float.IsFinite), ["emissions"] = dirtWorld.racerEmittedCount.ToList() };
                File.WriteAllText(Path.Combine(directory, "dune-contact.json"), JSONSerialization.prettyPrintedSortedKeys(report));
                GD.Print($"Dune contact: patches {maximumPatches}, offsets {Swift.description(minimum)}..{Swift.description(maximum)}, uploads {dirtWorld.duneSand.updates}, emissions [{string.Join(", ", dirtWorld.racerEmittedCount)}]");
                bool passed = pausePassed && (live || (contactPixels > 20 && contactTop > 400 && bodyPixels > 1500)) && minimum < -0.01 && maximum > 0.005 && maximumPatches <= SandDeformation.capacity && heights.All(h => float.IsFinite(h) && h > -0.14 && h < 0.11);
                dirtWorld.reset();
                return passed && dirtWorld.duneSand.patchCount == 0 && dirtWorld.duneSand.field.tiles.Count == 0;
            }
            finally { weatherOverride = null; }
        }
        catch (Exception error) { GD.Print($"Dune contact: {error}"); return false; }
    }
}
