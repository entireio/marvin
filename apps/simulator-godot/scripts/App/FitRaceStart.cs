// Godot-only tool: finds the random state of a macOS race-start capture so the port can reproduce it.
//
// `tools/godot -- --fit-race-start DIR [MAC.png]` (default reference/mac/smoke/race-start.png). App.swift's reset(_:)
// draws the starting grid and the daylight at random, so a macOS capture taken after a reset (RaceFinishSmoke's
// race-start.png and race-pause.png, the playthrough's countdown) can only be compared 1:1 once both are known. The
// tool resets the race for every grid permutation and then for a grid of daylights (fraction, phase), renders the
// view as RaceFinishSmoke composites it (900 x 550, the race HUD over the view) and scores the mean sRGB difference;
// it prints the best grid and daylight in TestPins' format and writes fit.json and the best render. Fitted for
// reference/mac/smoke/race-start.png (the third reset of that --smoke-test run): grid 3,1,2,0, daylight 0.8305 / 2.5753
// (1.40/255 for the composite; the phase is loosely constrained, 2.66 scores 1.42).
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
    [GameMode("--fit-race-start")]
    public static async Task RunFitRaceStart(string dir, SceneTree tree)
    {
        var args = CommandLine.arguments; int at = Array.IndexOf(args, "--fit-race-start");
        var macPath = at >= 0 && at + 2 < args.Length && !args[at + 2].StartsWith("--") ? args[at + 2]
            : ProjectSettings.GlobalizePath("res://reference/mac/smoke/race-start.png");
        var mac = Image.LoadFromFile(macPath); mac.Convert(Image.Format.Rgb8);
        int w = mac.GetWidth(), h = mac.GetHeight();
        var app = await launchSmoke(tree, dir);
        app.startDirtTrack(); app.dirtIntro = null;
        var macBytes = mac.GetData();
        // The candidate as RaceFinishSmoke composites it: the view scaled to the HUD's 900 x 550 frame with the race
        // HUD over it (its panels, the countdown backdrop and the course map's racer dots depend on the grid).
        double score(Image view)
        {
            var frame = app.raceHUD.frame;
            app.raceHUD.frame = new NSRect(0, 0, w, h);
            app.raceHUD.race = app.race; app.raceHUD.introducing = false;
            var hud = app.raceHUD.bitmapImageRepForCachingDisplay(app.raceHUD.bounds);
            app.raceHUD.cacheDisplay(app.raceHUD.bounds, hud);
            app.raceHUD.frame = frame;
            var data = view.GetData();
            double sum = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int o = y * hud.bytesPerRow + x * 4, i = (y * w + x) * 3;
                    double a = hud.bitmapData[o + 3] / 255.0;
                    for (int k = 0; k < 3; k++) sum += Math.Abs(data[i + k] * (1 - a) + hud.bitmapData[o + k] * a - macBytes[i + k]);
                }
            return sum / (w * h * 3);
        }
        Image render(string grid, double fraction, double phase)
        {
            System.Environment.SetEnvironmentVariable("MARVIN_GRID_SLOTS", grid);
            System.Environment.SetEnvironmentVariable("MARVIN_DAYLIGHT_FRACTION", fraction.ToString("R", CultureInfo.InvariantCulture));
            System.Environment.SetEnvironmentVariable("MARVIN_DAYLIGHT_PHASE", phase.ToString("R", CultureInfo.InvariantCulture));
            TestPins.restart();
            app.reset(null);
            app.robot.update(app.simulation); app.updateOpponents();
            var image = app.view.snapshot().GodotImage;
            image.Convert(Image.Format.Rgb8);
            image.Resize(w, h, Image.Interpolation.Bilinear);
            return image;
        }
        var permutations = new List<string>();
        void permute(List<int> prefix, List<int> rest)
        {
            if (rest.Count == 0) { permutations.Add(string.Join(",", prefix)); return; }
            foreach (var r in rest.ToList()) permute(prefix.Append(r).ToList(), rest.Where(x => x != r).ToList());
        }
        permute(new List<int>(), new List<int> { 0, 1, 2, 3 });
        var results = new Dictionary<string, object>();
        string bestGrid = null; double bestScore = double.MaxValue;
        foreach (var grid in permutations)
        {
            var s = score(render(grid, 0.5, 1.2));
            GD.Print($"grid {grid}: {s:F2}");
            if (s < bestScore) { bestScore = s; bestGrid = grid; }
        }
        results["gridScan"] = bestScore;
        double bestFraction = 0.5, bestPhase = 1.2; bestScore = double.MaxValue;
        void consider(double f, double p)
        {
            var s = score(render(bestGrid, f, p));
            if (s < bestScore) { bestScore = s; bestFraction = f; bestPhase = p; }
        }
        for (double f = 0.025; f <= 0.98; f += 0.05)
            for (double p = 0.35; p < 2 * Math.PI; p += Math.PI / 12)
            {
                if (p > Math.PI - 0.35 && p < Math.PI + 0.35 || p > 2 * Math.PI - 0.35) continue;
                consider(f, p);
            }
        GD.Print($"coarse: fraction {bestFraction:F3} phase {bestPhase:F3}: {bestScore:F2}");
        foreach (var (df, dp) in new[] { (0.025, Math.PI / 24), (0.008, Math.PI / 72), (0.0025, Math.PI / 216) })
        {
            double f0 = bestFraction, p0 = bestPhase;
            for (int i = -3; i <= 3; i++)
                for (int j = -3; j <= 3; j++)
                    consider(Math.Clamp(f0 + i * df, 0.015, 0.985), p0 + j * dp);
            GD.Print($"refined: fraction {bestFraction:F4} phase {bestPhase:F4}: {bestScore:F2}");
        }
        var best = render(bestGrid, bestFraction, bestPhase);
        best.SavePng(Path.Combine(dir, "fit-race-start-view.png"));
        results["grid"] = bestGrid; results["fraction"] = bestFraction; results["phase"] = bestPhase; results["score"] = bestScore; results["reference"] = macPath;
        File.WriteAllText(Path.Combine(dir, "fit.json"), JSONSerialization.prettyPrintedSortedKeys(results));
        print($"Race start fit: MARVIN_GRID_SLOTS={bestGrid} MARVIN_DAYLIGHT_FRACTION={bestFraction.ToString("R", CultureInfo.InvariantCulture)} MARVIN_DAYLIGHT_PHASE={bestPhase.ToString("R", CultureInfo.InvariantCulture)} · {bestScore:F2}/255");
        exit(0);
    }
}
