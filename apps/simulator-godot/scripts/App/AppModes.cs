// How the game modes run on the AppController (PORT: main.swift and the smoke branch of App.swift's tick).
//
// On macOS every smoke flag starts the normal app; tick() shows the main menu for 20 frames, invalidates the
// timer and runs the flag's check (App.swift), then exits. Here each flag is one game mode (GameModes.cs) whose
// entry point does the same through `launchSmoke`: the AppController is created with the mode's directory as
// its smokeDirectory, ticks 20 main-menu frames and stops its timer; the entry point then runs the check.
using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Marvin.Core;

namespace Marvin;

public partial class AppController
{
    /// main.swift: NSApplication with this delegate; run() adds the controller to the scene tree and calls
    /// applicationDidFinishLaunching (robots, window, toolbar, content view, overlays, menu bar, main menu), after which
    /// the app ticks from Godot's frame loop.
    public static AppController launch(Godot.SceneTree tree, string smokeDirectory = null)
    {
        var app = NSApplication.shared;
        app.setActivationPolicy(NSApplication.ActivationPolicy.regular);
        var controller = new AppController(smokeDirectory) { Name = "AppController" };
        app.@delegate = controller;
        app.run();
        return controller;
    }

    /// `launch`, then App.swift's 20 main-menu ticks before a smoke check, after which tick() stops
    /// (`timer?.invalidate()` precedes every check there).
    public static async Task<AppController> launchSmoke(Godot.SceneTree tree, string smokeDirectory)
    {
        var app = launch(tree, smokeDirectory);
        while (app.menuSmokeFrames < 20) { await frame(tree); }
        app.timer?.invalidate();
        return app;
    }

    public static async Task frame(Godot.SceneTree tree) => await tree.ToSignal(tree, Godot.SceneTree.SignalName.ProcessFrame);

    /// `--loading-smoke-test DIR` (App.swift tick: `loadDirtTrack()` at menu frame 20; LevelLoading.swift): the race
    /// world builds on a global queue while tick() keeps counting heartbeats; loading.png at 49 % and the PASS/FAIL line
    /// come from captureLoadingCheckIfNeeded and finishLoadingCheckIfNeeded, which exits.
    [GameMode("--loading-smoke-test")]
    public static async Task RunLoadingSmokeTest(string dir, Godot.SceneTree tree)
    {
        var app = launch(tree, dir);
        while (app.menuSmokeFrames < 20) { await frame(tree); }
        app.loadDirtTrack();
        // The timer keeps running (heartbeats); revealDirtTrack -> finishLoadingCheckIfNeeded exits.
        while (true) { await frame(tree); }
    }

    /// view.snapshot() written as NAME (a PNG), as the smoke checks' `png.write(to:)`.
    public void saveSnapshot(string file, string directory)
    {
        var bitmap = NSBitmapImageRep.data(view.snapshot().tiffRepresentation);
        var png = bitmap?.representation(NSBitmapImageFileType.png) ?? throw new System.IO.IOException("fileWriteUnknown");
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory, file), png);
    }
}

/// <summary>
/// PORT: test pins for comparing a capture with one particular macOS run. reset(_:) draws the starting grid and the
/// daylight at random (as on macOS); these environment variables fix them:
/// <list type="bullet">
/// <item><c>MARVIN_GRID_SLOTS=i,j,k,l</c> (or <c>MARVIN_TOWN_GRID</c>): indices into DirtCourse.startingGrid for the
/// player, R2-D2, BB-8 and WALL-E; several grids separated by ";" pin one reset each (the last repeats).</item>
/// <item><c>MARVIN_DAYLIGHT_FRACTION</c> / <c>MARVIN_DAYLIGHT_PHASE</c>: comma-separated lists, one entry per reset (the
/// last repeats), so a mode that resets several times can reproduce each daylight of a run;
/// <c>MARVIN_TOWN_DAYLIGHT=fraction,phase</c> pins one daylight for every reset.</item>
/// </list>
/// </summary>
public static class TestPins
{
    private static int gridCount = 0;
    public static (double phase, double offset)[] gridSlots()
    {
        var value = Environment.GetEnvironmentVariable("MARVIN_GRID_SLOTS") ?? Environment.GetEnvironmentVariable("MARVIN_TOWN_GRID");
        if (string.IsNullOrWhiteSpace(value)) { return null; }
        var grids = value.Split(';');
        var order = grids[Math.Min(gridCount, grids.Length - 1)];
        gridCount += 1;
        return order.Split(',').Select(s => DirtCourse.startingGrid[int.Parse(s.Trim(), CultureInfo.InvariantCulture)]).ToArray();
    }
    /// <summary>Starts the per-reset lists again (fitting tools that reset many times).</summary>
    public static void restart() { gridCount = 0; daylightCount = 0; }

    private static int daylightCount = 0;
    public static BinaryDaylight? daylight()
    {
        if (Environment.GetEnvironmentVariable("MARVIN_TOWN_DAYLIGHT") is string town && !string.IsNullOrWhiteSpace(town))
        {
            var values = town.Split(',').Select(v => double.Parse(v.Trim(), CultureInfo.InvariantCulture)).ToArray();
            return new BinaryDaylight(fraction: values[0], phase: values.Length > 1 ? values[1] : 1.2);
        }
        string pick(string name)
        {
            var list = Environment.GetEnvironmentVariable(name)?.Split(',');
            return list == null ? null : list[Math.Min(daylightCount, list.Length - 1)].Trim();
        }
        var f = pick("MARVIN_DAYLIGHT_FRACTION");
        if (f == null || !double.TryParse(f, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction) || !double.IsFinite(fraction)) { return null; }
        var p = pick("MARVIN_DAYLIGHT_PHASE");
        double phase = p != null && double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 1.2;
        daylightCount += 1;
        return new BinaryDaylight(fraction: fraction, phase: phase);
    }
}
