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
/// `--dust-visibility-test DIR` (DustVisibilitySmoke.swift, AppController.checkDustVisibility). Same three drives
/// (clay, town, dunes), camera, 1280 x 800 4x-MSAA renders with the dust batch hidden and shown
/// (NAME-dust-off.png, NAME-dust-on.png) and the same printed measurements.
/// </summary>
public partial class AppController
{
    [GameMode("--dust-visibility-test")]
    public static async Task RunDustVisibilityTest(string dir, SceneTree tree)
    {
        var app = await launchSmoke(tree, dir);
        var passed = app.checkDustVisibility(at: dir);
        exit(passed ? 0 : 1);
    }

    public bool checkDustVisibility(string at)
    {
        var directory = at;
        try
        {
            Directory.CreateDirectory(directory);
            weatherOverride = false; startDirtTrack(); dirtIntro = null; raceHUD.isHidden = true;
            dirtWorld.sky.apply(new BinaryDaylight(fraction: 0.5, phase: 1.2));
            bool passed = true;
            foreach (var (name, point) in new[] { ("clay", new Double2(0, -15)), ("town", new Double2(90, 40)), ("dunes", new Double2(210, 65)) })
            {
                dirtWorld.reset();
                var p = DirtCourse.projection(point.x, point.y);
                simulation = name == "clay" ? new Simulation(dirtTrack: true) : new Simulation(dirtTrack: true, dirtStartOffset: p.offset, dirtStartPhase: p.phase);
                var physics = new DirtRacePhysics(); var localRace = new DirtRace(); localRace.countDown(dt: 3);
                var rivals = DirtCourse.startingGrid.Skip(1).Select(slot => new DirtOpponent(slot: slot)).ToArray();
                var input = new DriveInput(); input.throttle = 0.7;
                for (int k = 0; k < 90; k++)
                {
                    physics.advance(input, ref simulation, ref localRace, rivals, dt: 1.0 / 60, raceDT: 1.0 / 60, robotCollisionsEnabled: false);
                    updateOpponents(); updateRaceWorld(dt: 1.0 / 60);
                }
                var target = new Double3(simulation.x, simulation.groundY + 0.2, simulation.z);
                Double3 forward = new Double3(sin(simulation.heading), 0, cos(simulation.heading)), side = new Double3(cos(simulation.heading), 0, -sin(simulation.heading));
                var eye = target - forward * 3 + side * 2 + new Double3(0, 1.2, 0);
                world.camera.position = new SCNVector3(eye.x, eye.y, eye.z);
                world.camera.look(new SCNVector3(target.x, target.y, target.z), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                var renderer = new SCNRenderer(device: null, options: null); renderer.scene = dirtWorld.scene; renderer.pointOfView = world.camera;
                var images = new List<NSBitmapImageRep>();
                foreach (var visible in new[] { false, true })
                {
                    dirtWorld.diagnosticDust(visible);
                    var image = renderer.snapshot(atTime: 0, with: new CGSize(1280, 800), antialiasingMode: SCNAntialiasingMode.multisampling4X);
                    var bitmap = NSBitmapImageRep.data(image.tiffRepresentation); images.Add(bitmap);
                    File.WriteAllBytes(Path.Combine(directory, $"{name}-dust-{(visible ? "on" : "off")}.png"), bitmap.representation(NSBitmapImageFileType.png));
                }
                int changed = 0; double maximum = 0.0, total = 0.0;
                for (int y = 0; y < 800; y++)
                    for (int x = 0; x < 1280; x++)
                    {
                        var a = images[0].colorAt(x, y).usingColorSpace(NSColorSpace.deviceRGB); var b = images[1].colorAt(x, y).usingColorSpace(NSColorSpace.deviceRGB);
                        double d = max(abs(a.redComponent - b.redComponent), max(abs(a.greenComponent - b.greenComponent), abs(a.blueComponent - b.blueComponent)));
                        if (d > 2.0 / 255) { changed += 1; total += d; }
                        maximum = max(maximum, d);
                    }
                dirtWorld.printDustOpacity();
                GD.Print($"Dust {name}: live {dirtWorld.diagnosticDustCount}, speed {Swift.description(simulation.speed)}, pixels >2/255 {changed}, max contrast {Swift.description(maximum)}, mean changed {Swift.description(total / (double)max(1, changed))}");
                passed = passed && dirtWorld.diagnosticDustCount > 0;
            }
            return passed;
        }
        catch (Exception error) { GD.Print(error.ToString()); return false; }
    }
}
