// Godot-only tool: finds the random daylight of a macOS town capture so a check can be rerun 1:1.
//
// `tools/godot -- --fit-daylight DIR MAC.png EX,EY,EZ TX,TY,TZ` starts the dirt track as the town checks
// do (startDirtTrack(), clear weather), puts the camera at EYE looking at TARGET, renders the view for a grid of
// daylights (fraction, phase) and then refines the best one, scoring the mean sRGB difference to MAC.png (the 3D view,
// as saveTownFrame captures it). Robots and people may stand elsewhere than in the macOS run; the sky, the shading and
// the shadows of the town decide the score. It prints the result in TestPins' format (MARVIN_DAYLIGHT_FRACTION /
// MARVIN_DAYLIGHT_PHASE pin reset(_:)'s random daylight) and writes fit-daylight.json and the best render.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Marvin.Core;

namespace Marvin;

public partial class AppController
{
    [GameMode("--fit-daylight")]
    public static async Task RunFitDaylight(string dir, SceneTree tree)
    {
        var args = CommandLine.arguments; int at = Array.IndexOf(args, "--fit-daylight");
        if (!(at >= 0 && at + 4 < args.Length)) { print("usage: --fit-daylight DIR MAC.png EX,EY,EZ TX,TY,TZ"); exit(2); return; }
        var macPath = args[at + 2];
        double[] vector(string text) => text.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        double[] eye = vector(args[at + 3]), target = vector(args[at + 4]);
        var mac = Image.LoadFromFile(macPath); mac.Convert(Image.Format.Rgb8);
        int w = mac.GetWidth(), h = mac.GetHeight();
        var macBytes = mac.GetData();
        var app = await launchSmoke(tree, dir);
        app.weatherOverride = false;
        app.startDirtTrack(); app.dirtIntro = null; app.race.countDown(dt: 3); app.setRaceControlsHidden(true);
        Image render(double fraction, double phase)
        {
            app.dirtWorld.sky.apply(new BinaryDaylight(fraction: fraction, phase: phase));
            app.world.camera.position = new SCNVector3(eye[0], eye[1], eye[2]);
            app.world.camera.look(at: new SCNVector3(target[0], target[1], target[2]), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
            app.dirtWorld.town.update(dt: 0, camera: app.world.camera.position, player: new Double2(app.simulation.x, app.simulation.z));
            var image = app.view.snapshot().GodotImage;
            image.Convert(Image.Format.Rgb8);
            if (image.GetWidth() != w || image.GetHeight() != h) { image.Resize(w, h, Image.Interpolation.Bilinear); }
            return image;
        }
        double score(Image view)
        {
            var data = view.GetData(); double sum = 0;
            for (int i = 0; i < data.Length; i++) { sum += Math.Abs(data[i] - macBytes[i]); }
            return sum / data.Length;
        }
        double bestFraction = 0.5, bestPhase = 1.2, bestScore = double.MaxValue;
        void consider(double f, double p)
        {
            var s = score(render(f, p));
            if (s < bestScore) { bestScore = s; bestFraction = f; bestPhase = p; }
        }
        for (double f = 0.025; f <= 0.98; f += 0.05)
        {
            for (double p = 0.35; p < 2 * Math.PI; p += Math.PI / 12)
            {
                if (p > Math.PI - 0.35 && p < Math.PI + 0.35 || p > 2 * Math.PI - 0.35) { continue; }
                consider(f, p);
            }
        }
        print($"coarse: fraction {bestFraction:F3} phase {bestPhase:F3}: {bestScore:F2}");
        foreach (var (df, dp) in new[] { (0.025, Math.PI / 24), (0.008, Math.PI / 72), (0.0025, Math.PI / 216) })
        {
            double f0 = bestFraction, p0 = bestPhase;
            for (int i = -3; i <= 3; i++)
            {
                for (int j = -3; j <= 3; j++) { consider(Math.Clamp(f0 + i * df, 0.015, 0.985), p0 + j * dp); }
            }
            print($"refined: fraction {bestFraction:F4} phase {bestPhase:F4}: {bestScore:F2}");
        }
        render(bestFraction, bestPhase).SavePng(Path.Combine(dir, "fit-daylight-view.png"));
        var results = new Dictionary<string, object> { ["fraction"] = bestFraction, ["phase"] = bestPhase, ["score"] = bestScore, ["reference"] = macPath, ["eye"] = eye, ["target"] = target };
        File.WriteAllText(Path.Combine(dir, "fit-daylight.json"), JSONSerialization.prettyPrintedSortedKeys(results));
        print($"Daylight fit: MARVIN_DAYLIGHT_FRACTION={bestFraction.ToString("R", CultureInfo.InvariantCulture)} MARVIN_DAYLIGHT_PHASE={bestPhase.ToString("R", CultureInfo.InvariantCulture)} · {bestScore:F2}/255");
        exit(0);
    }
}
