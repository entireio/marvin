// Godot-only tool: finds the random state of the race captures of a macOS --character-smoke-test run.
//
// `tools/godot -- --fit-character-race DIR MACDIR [brwm]` (MACDIR holds the macOS b-race.png, r-race.png, w-race.png and
// m-race.png). PlayableCharacterSmoke's race capture follows a startDirtTrack() whose reset(_:) draws the starting grid
// and the daylight at random, so the port can only reproduce it 1:1 once both are known. For each character the tool
// drives the smoke's 120 race frames from every grid permutation (daylight fixed), keeps the grid whose view is closest
// to the macOS capture (mean sRGB difference of the whole 1280 x 820 view; the camera follows the player, so the
// player's slot moves the whole picture and the rivals' slots move the robots), then scans and refines the daylight
// (fraction, phase) on that state, scores the five next-best grids again under that daylight (grids that differ only
// in the rivals' slots score alike under a wrong sun) and refines the daylight for the winner. It writes fit.json and
// prints the pins for the whole run in TestPins' format: two dirt resets per
// character (the captured one and the reset after it), in the smoke's order b, r, w, m.
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
    [GameMode("--fit-character-race")]
    public static async Task RunFitCharacterRace(string dir, SceneTree tree)
    {
        var args = CommandLine.arguments; int at = Array.IndexOf(args, "--fit-character-race");
        var macDir = at >= 0 && at + 2 < args.Length && !args[at + 2].StartsWith("--") ? args[at + 2]
            : ProjectSettings.GlobalizePath("res://reference/mac/character");
        var app = await launchSmoke(tree, dir);
        var permutations = new List<string>();
        void permute(List<int> prefix, List<int> rest)
        {
            if (rest.Count == 0) { permutations.Add(string.Join(",", prefix)); return; }
            foreach (var r in rest.ToList()) permute(prefix.Append(r).ToList(), rest.Where(x => x != r).ToList());
        }
        permute(new List<int>(), new List<int> { 0, 1, 2, 3 });
        var results = new Dictionary<string, object>();
        var grids = new List<string>(); var fractions = new List<string>(); var phases = new List<string>();
        // Optional third argument: the characters to fit (default "brwm", the smoke's order).
        var keys = at >= 0 && at + 3 < args.Length && !args[at + 3].StartsWith("--") ? args[at + 3] : "brwm";
        foreach (var key in "brwm".Where(c => keys.Contains(c)).Select(c => c.ToString()))
        {
            var mac = Image.LoadFromFile(Path.Combine(macDir, key + "-race.png")); mac.Convert(Image.Format.Rgb8);
            int w = mac.GetWidth(), h = mac.GetHeight();
            var macBytes = mac.GetData();
            double score()
            {
                var image = app.view.snapshot().GodotImage;
                image.Convert(Image.Format.Rgb8);
                if (image.GetWidth() != w || image.GetHeight() != h) image.Resize(w, h, Image.Interpolation.Bilinear);
                var data = image.GetData();
                double sum = 0;
                for (int i = 0; i < data.Length; i++) sum += Math.Abs(data[i] - macBytes[i]);
                return sum / data.Length;
            }
            void race(string grid)
            {
                System.Environment.SetEnvironmentVariable("MARVIN_GRID_SLOTS", grid);
                System.Environment.SetEnvironmentVariable("MARVIN_DAYLIGHT_FRACTION", "0.5");
                System.Environment.SetEnvironmentVariable("MARVIN_DAYLIGHT_PHASE", "1.2");
                TestPins.restart();
                app.reset(null);
                app.dirtIntro = null; app.race.countDown(dt: 3);
                for (int i = 0; i < 120; i++)
                {
                    var drive = DirtOpponent.driveInput(app.simulation);
                    app.advanceRacePhysics(drive, dt: 1.0 / 60, raceDT: 1.0 / 60);
                    app.updateOpponents(); app.updateRaceWorld(dt: 1.0 / 60);
                }
                app.updateCamera(snap: true);
            }
            // The smoke's character selection: the hidden menu key, then the race.
            app.showMainMenu(null);
            var @event = NSEvent.keyEvent(NSEvent.EventType.keyDown, NSPoint.zero, 0, 0, app.window.windowNumber, null, key, key, false, 0);
            app.mainMenu.keyDown(@event);
            app.mainMenu.animate(app.robot, r2d2: app.r2d2, bb8: app.bb8, wallE: app.wallE, dt: 0);
            app.startDirtTrack();
            var scan = new List<(string grid, double score)>();
            foreach (var grid in permutations)
            {
                race(grid);
                var s = score();
                GD.Print($"{key} grid {grid}: {s:F2}");
                scan.Add((grid, s));
            }
            scan.Sort((a, b) => a.score.CompareTo(b.score));
            string bestGrid = scan[0].grid; double bestScore = double.MaxValue;
            race(bestGrid);
            double bestFraction = 0.5, bestPhase = 1.2;
            void consider(double f, double p)
            {
                app.dirtWorld.sky.apply(new BinaryDaylight(fraction: f, phase: p));
                var s = score();
                if (s < bestScore) { bestScore = s; bestFraction = f; bestPhase = p; }
            }
            void refine()
            {
                foreach (var (df, dp) in new[] { (0.025, Math.PI / 24), (0.008, Math.PI / 72), (0.0025, Math.PI / 216) })
                {
                    double f0 = bestFraction, p0 = bestPhase;
                    for (int i = -3; i <= 3; i++)
                        for (int j = -3; j <= 3; j++)
                            consider(Math.Clamp(f0 + i * df, 0.015, 0.985), p0 + j * dp);
                }
            }
            for (double f = 0.025; f <= 0.98; f += 0.05)
                for (double p = 0.35; p < 2 * Math.PI; p += Math.PI / 12)
                {
                    if (p > Math.PI - 0.35 && p < Math.PI + 0.35 || p > 2 * Math.PI - 0.35) continue;
                    consider(f, p);
                }
            refine();
            // With a wrong daylight, grids that differ only in the rivals' slots score alike: score the closest
            // grids again under the fitted daylight and refine the daylight for the winner.
            foreach (var (grid, _) in scan.Skip(1).Take(5).ToList())
            {
                race(grid);
                app.dirtWorld.sky.apply(new BinaryDaylight(fraction: bestFraction, phase: bestPhase));
                var s = score();
                GD.Print($"{key} grid {grid} at the fitted daylight: {s:F2} (best {bestGrid}: {bestScore:F2})");
                if (s < bestScore) { bestScore = s; bestGrid = grid; }
            }
            race(bestGrid); bestScore = double.MaxValue; consider(bestFraction, bestPhase); refine();
            GD.Print($"{key}: grid {bestGrid} fraction {bestFraction:F4} phase {bestPhase:F4}: {bestScore:F2}/255");
            results[key] = new Dictionary<string, object> { ["grid"] = bestGrid, ["fraction"] = bestFraction, ["phase"] = bestPhase, ["score"] = bestScore };
            // The captured reset, then the reset after the capture (any state).
            grids.Add(bestGrid); grids.Add(bestGrid);
            var fs = bestFraction.ToString("R", CultureInfo.InvariantCulture); var ps = bestPhase.ToString("R", CultureInfo.InvariantCulture);
            fractions.Add(fs); fractions.Add(fs); phases.Add(ps); phases.Add(ps);
        }
        results["reference"] = macDir;
        File.WriteAllText(Path.Combine(dir, "fit.json"), JSONSerialization.prettyPrintedSortedKeys(results));
        print($"Character race fit: MARVIN_GRID_SLOTS=\"{string.Join(";", grids)}\" MARVIN_DAYLIGHT_FRACTION={string.Join(",", fractions)} MARVIN_DAYLIGHT_PHASE={string.Join(",", phases)}");
        exit(0);
    }
}
