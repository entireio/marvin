// Godot-only game mode: plays the game through its real input path and captures every stage.
//
// `tools/godot --audio-driver Dummy -- --playthrough DIR` launches the normal app (no smoke directory: wall-clock
// ticks, focus gating, the loading screen and race audio, exactly as `tools/godot` without arguments) and drives it
// with synthesized Godot input events: keys reach the first responder through NSEvent like a player's, Command (Control
// outside macOS) shortcuts go through the menu bar's key equivalents, and toolbar buttons are clicked with the mouse.
// Route: main menu -> keyboard selection of Dirt Track -> loading -> intro fly-in -> countdown -> pause/resume -> three
// laps with DirtOpponent's driving logic steering the player through the W A S D / Shift keys -> finish and results
// -> post-race departure -> Command-R restart -> Command-M main menu -> Sandbox (keys, mouse orbit and zoom, toolbar
// Camera/Pause/Controls/Main Menu, Command-1, Esc, H, ?). It writes a window screenshot per stage (NN-name.png, the
// whole window: 3D view, overlays and title bar) and, at the matching moments, the macOS composite smoke captures
// race-start.png, race-pause.png, dirt-hud.png, race-finish-live.png and race-results.png (RaceFinishSmoke's 900 x 550
// composite of the view snapshot and the race HUD), plus playthrough.json with every step's result.
//
// Test hooks, as the macOS smoke modes have them: the weather is clear (weatherOverride), scores go to
// DIR/test-scores.json (scoreDirectory), the five menu settings and the mute setting are reset to their defaults and
// restored afterwards, and `active` is kept true so another app taking focus does not pause the run. The grid and
// daylight can be pinned with MARVIN_GRID_SLOTS / MARVIN_DAYLIGHT_FRACTION / MARVIN_DAYLIGHT_PHASE (TestPins).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

public partial class AppController
{
    [GameMode("--playthrough")]
    public static async Task RunPlaythrough(string dir, SceneTree tree)
    {
        var run = new Playthrough(dir, tree);
        bool passed;
        try { passed = await run.play(); }
        catch (Exception error) { GD.PrintErr($"Playthrough failed: {error}"); run.note("exception", error.ToString()); passed = false; }
        finally { run.restoreSettings(); }
        run.writeReport(passed);
        print($"Playthrough: {(passed ? "PASS" : "FAIL")} · {dir}");
        exit(passed ? 0 : 1);
    }
}

/// <summary>The --playthrough driver (see the file comment).</summary>
public sealed class Playthrough
{
    private readonly string directory;
    private readonly SceneTree tree;
    private AppController app;
    private readonly Dictionary<string, object> report = new();
    private readonly List<object> steps = new();
    private int shotIndex = 0;
    private readonly double started = Time.GetTicksMsec() / 1000.0;
    private static readonly string[] settingKeys = { "reduceMenuMotion", "hideKeyboardGuide", "disableRaceRobotCollisions", "disableRaceSteeringAssist", "disableRaceBrakingAssist", "raceSoundMuted" };
    private readonly object[] savedSettings = new object[settingKeys.Length];
    private bool keepActive = true;

    public Playthrough(string directory, SceneTree tree) { this.directory = directory; this.tree = tree; }

    // ---- Report
    public void note(string key, object value) => report[key] = value;
    private bool check(string name, bool passed, object detail = null)
    {
        stage = name;
        var entry = new Dictionary<string, object> { ["step"] = name, ["passed"] = passed, ["time"] = Math.Round(Time.GetTicksMsec() / 1000.0 - started, 2) };
        if (detail != null) entry["detail"] = detail;
        steps.Add(entry);
        GD.Print($"[playthrough] {(passed ? "ok  " : "FAIL")} {name}{(detail != null ? " · " + Convert.ToString(detail, System.Globalization.CultureInfo.InvariantCulture) : "")}");
        return passed;
    }
    public void writeReport(bool passed)
    {
        report["passed"] = passed;
        report["steps"] = steps;
        report["telemetry"] = telemetry;
        report["platform"] = OS.GetName();
        report["commandKey"] = KeyEquivalent.command;
        try { File.WriteAllText(Path.Combine(directory, "playthrough.json"), JSONSerialization.prettyPrintedSortedKeys(report)); }
        catch (Exception error) { GD.PrintErr($"playthrough.json: {error.Message}"); }
    }
    public void restoreSettings()
    {
        for (int i = 0; i < settingKeys.Length; i++)
        {
            if (savedSettings[i] != null) UserDefaults.standard.set(savedSettings[i], settingKeys[i]);
            else UserDefaults.standard.removeObject(settingKeys[i]);
        }
    }

    // ---- Frames and input
    private readonly List<object> telemetry = new();
    private double nextSample;
    private async Task frame()
    {
        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        if (keepActive && app != null) app.active = true;
        var now = Time.GetTicksMsec() / 1000.0;
        if (now >= nextSample)
        {
            nextSample = now + 2;
            telemetry.Add(new Dictionary<string, object>
            {
                ["t"] = Math.Round(now - started, 1), ["stage"] = stage, ["fps"] = Performance.GetMonitor(Performance.Monitor.TimeFps),
                ["processMs"] = Math.Round(Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000, 1),
                ["nodes"] = Performance.GetMonitor(Performance.Monitor.ObjectNodeCount), ["objects"] = Performance.GetMonitor(Performance.Monitor.ObjectCount),
                ["drawCalls"] = Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame), ["renderObjects"] = Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame),
                ["primitives"] = Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame), ["videoMB"] = Math.Round(Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576, 1),
                ["raceElapsed"] = app == null ? 0 : Math.Round(app.race.elapsed, 1),
            });
        }
    }
    private string stage = "launch";
    private async Task frames(int n) { for (int i = 0; i < n; i++) await frame(); }
    private async Task seconds(double s) { var end = Time.GetTicksMsec() / 1000.0 + s; while (Time.GetTicksMsec() / 1000.0 < end) await frame(); }
    private async Task<bool> waitFor(string what, Func<bool> condition, double timeout, Func<Task> eachFrame = null)
    {
        var end = Time.GetTicksMsec() / 1000.0 + timeout;
        while (!condition())
        {
            if (Time.GetTicksMsec() / 1000.0 > end) { check($"wait: {what}", false, $"timed out after {timeout} s"); return false; }
            if (eachFrame != null) await eachFrame(); else await frame();
        }
        return true;
    }

    private static readonly bool mac = OS.GetName() == "macOS";
    /// <summary>A physical key press or release as Godot delivers it (layout-independent physical code, US key label).</summary>
    private static void key(Key physical, bool pressed, bool command = false, bool shift = false)
    {
        var e = new InputEventKey { Pressed = pressed, PhysicalKeycode = physical, Keycode = physical, KeyLabel = physical, ShiftPressed = shift };
        if (command) { if (mac) e.MetaPressed = true; else e.CtrlPressed = true; }
        if (physical == Key.Shift) e.ShiftPressed = pressed;
        if (!command && (long)physical >= (long)Key.A && (long)physical <= (long)Key.Z) e.Unicode = (long)physical + (shift ? 0 : 32);
        else if (!command && physical is Key.Slash) e.Unicode = shift ? '?' : '/';
        else if (!command && physical is Key.Space) e.Unicode = ' ';
        Input.ParseInputEvent(e);
    }
    private async Task tap(Key physical, bool command = false, bool shift = false)
    {
        if (command) { var mod = new InputEventKey { Pressed = true, PhysicalKeycode = mac ? Key.Meta : Key.Ctrl, Keycode = mac ? Key.Meta : Key.Ctrl, MetaPressed = mac, CtrlPressed = !mac }; Input.ParseInputEvent(mod); }
        key(physical, true, command, shift); await frame();
        key(physical, false, command, shift);
        if (command) { var mod = new InputEventKey { Pressed = false, PhysicalKeycode = mac ? Key.Meta : Key.Ctrl, Keycode = mac ? Key.Meta : Key.Ctrl }; Input.ParseInputEvent(mod); }
        await frame();
    }
    private static void mouseMove(Vector2 position, MouseButtonMask mask, Vector2 relative)
    {
        Input.ParseInputEvent(new InputEventMouseMotion { Position = position, GlobalPosition = position, ButtonMask = mask, Relative = relative });
    }
    private static void mouseButton(Vector2 position, MouseButton button, bool pressed)
    {
        Input.ParseInputEvent(new InputEventMouseButton
        {
            Position = position, GlobalPosition = position, ButtonIndex = button, Pressed = pressed,
            ButtonMask = pressed ? (button == MouseButton.Left ? MouseButtonMask.Left : button == MouseButton.Right ? MouseButtonMask.Right : 0) : 0,
        });
    }
    private async Task click(Vector2 position)
    {
        mouseMove(position, 0, Vector2.Zero); await frame();
        mouseButton(position, MouseButton.Left, true); await frame();
        mouseButton(position, MouseButton.Left, false); await frames(2);
    }
    private async Task drag(Vector2 from, Vector2 delta, int steps = 10)
    {
        mouseMove(from, 0, Vector2.Zero); await frame();
        mouseButton(from, MouseButton.Left, true); await frame();
        for (int i = 1; i <= steps; i++) { mouseMove(from + delta * i / steps, MouseButtonMask.Left, delta / steps); await frame(); }
        mouseButton(from + delta, MouseButton.Left, false); await frames(2);
    }
    private async Task scroll(Vector2 at, bool up, int notches)
    {
        for (int i = 0; i < notches; i++)
        {
            var b = up ? MouseButton.WheelUp : MouseButton.WheelDown;
            Input.ParseInputEvent(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = b, Pressed = true, Factor = 1 });
            Input.ParseInputEvent(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = b, Pressed = false, Factor = 1 });
            await frame();
        }
    }

    // ---- Captures
    /// <summary>The whole window as the player sees it (the root viewport after this frame is drawn).</summary>
    private async Task shot(string name)
    {
        await tree.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var image = tree.Root.GetTexture().GetImage();
        shotIndex += 1;
        var file = $"{shotIndex:00}-{name}.png";
        image.SavePng(Path.Combine(directory, file));
        check($"capture {file}", image.GetWidth() > 0, $"{image.GetWidth()}x{image.GetHeight()}");
    }
    /// <summary>RaceFinishSmoke's 900 x 550 composite (view snapshot with the race HUD drawn over it).</summary>
    private void composite(string name)
    {
        var frame = app.raceHUD.frame;
        try
        {
            app.raceHUD.frame = new NSRect(0, 0, 900, 550);
            app.raceHUD.race = app.race;
            var image = new NSImage(app.raceHUD.bounds.size);
            image.lockFocus();
            app.view.snapshot().draw(app.raceHUD.bounds);
            var overlay = app.raceHUD.bitmapImageRepForCachingDisplay(app.raceHUD.bounds);
            app.raceHUD.cacheDisplay(app.raceHUD.bounds, overlay);
            overlay.draw(app.raceHUD.bounds);
            image.unlockFocus();
            var png = NSBitmapImageRep.data(image.tiffRepresentation)?.representation(NSBitmapImageFileType.png);
            File.WriteAllBytes(Path.Combine(directory, name), png);
            check($"composite {name}", png != null);
        }
        finally { app.raceHUD.frame = frame; app.raceHUD.needsDisplay = true; }
    }
    /// <summary>The smoke test's dirt-hud.png: the top-left 900 x 550 of the race HUD at its window size.</summary>
    private void hudCapture(string name)
    {
        var rect = new NSRect(0, 0, 900, 550);
        var bitmap = app.raceHUD.bitmapImageRepForCachingDisplay(rect);
        app.raceHUD.cacheDisplay(rect, bitmap);
        File.WriteAllBytes(Path.Combine(directory, name), bitmap.representation(NSBitmapImageFileType.png));
        check($"hud {name}", true, $"HUD {app.raceHUD.bounds.width}x{app.raceHUD.bounds.height}");
    }

    // ---- The player's driver: the keyboard driver of the macOS assist checks (DrivingAssistChecks.swift): every 150 ms
    // it aims at the centre line 0.075 rad ahead, holds W, steers with A or D outside a 0.08 rad dead band and brakes
    // with Space above 3 m/s in bends (5.5 m/s elsewhere). Those decisions become held keys, as a player's would.
    private readonly HashSet<Key> held = new();
    private double lastDecision = -1;
    private int steering; private bool braking;
    private void hold(Key k, bool down)
    {
        bool isDown = held.Contains(k);
        // Re-press a key the view lost (focus changes clear its held keys), as a player would keep holding it.
        bool viewHas = app.view.held.Contains(NSEvent.KeyCodeFor(k));
        if (down && (!isDown || !viewHas)) { key(k, true); held.Add(k); }
        else if (!down && isDown) { key(k, false); held.Remove(k); }
    }
    private void releaseAll() { foreach (var k in held.ToList()) key(k, false); held.Clear(); steering = 0; braking = false; lastDecision = -1; }
    private void drive()
    {
        var now = Time.GetTicksMsec() / 1000.0;
        if (lastDecision < 0 || now - lastDecision >= 0.15)
        {
            lastDecision = now;
            var state = app.simulation;
            var at = DirtCourse.phase(state.x, state.z); var target = DirtCourse.point(at + 0.075);
            var desired = atan2(target.x - state.x, target.z - state.z);
            var error = atan2(sin(desired - state.heading), cos(desired - state.heading));
            var delta = DirtCourse.heading(at + 0.15) - DirtCourse.heading(at);
            var bend = abs(atan2(sin(delta), cos(delta)));
            steering = abs(error) < 0.08 ? 0 : error > 0 ? -1 : 1;
            braking = state.groundSpeed > (bend > 0.4 ? 3.0 : 5.5);
        }
        hold(Key.W, true);
        hold(Key.D, steering > 0); hold(Key.A, steering < 0);
        hold(Key.Space, braking);
    }

    // ---- The route
    public async Task<bool> play()
    {
        Directory.CreateDirectory(directory);
        for (int i = 0; i < settingKeys.Length; i++) { savedSettings[i] = UserDefaults.standard.@object(settingKeys[i]); UserDefaults.standard.removeObject(settingKeys[i]); }
        var scoreFile = Path.Combine(directory, "test-scores.json");
        if (File.Exists(scoreFile)) File.Delete(scoreFile);

        app = AppController.launch(tree);
        app.weatherOverride = System.Environment.GetEnvironmentVariable("MARVIN_PLAYTHROUGH_STORM") == "1";
        app.scoreDirectory = directory;
        bool ok = true;
        await seconds(1.0);
        ok &= check("main menu shown", !app.mainMenu.isHidden && !app.inSandbox && app.window.firstResponder == app.mainMenu && app.window.toolbar?.isVisible == false,
            $"title '{app.window.title}', layout {app.window.contentLayoutRect.width}x{app.window.contentLayoutRect.height}, menu {app.mainMenu.frame.width}x{app.mainMenu.frame.height}");
        await shot("main-menu");

        // Keyboard: Down selects Dirt Track, Return starts loading the race.
        await tap(Key.Down);
        ok &= check("Down selects Dirt Track", app.mainMenu.selection == 1);
        await shot("menu-dirt-track-selected");
        await tap(Key.Enter);
        ok &= check("Return starts loading", app.isLoadingDirt || app.inSandbox);
        if (await waitFor("loading progress 45 %", () => !app.isLoadingDirt || (app.loadingView.history.Count > 0 && app.loadingView.history[^1] >= 0.45), 60))
            await shot("loading");
        var loadingStarted = Time.GetTicksMsec() / 1000.0;
        ok &= await waitFor("race revealed", () => !app.isLoadingDirt && app.inSandbox && app.isDirtTrack && !app.view.isHidden, 120);
        ok &= check("race loaded", app.isDirtTrack && !app.raceHUD.isHidden && app.window.toolbar?.isVisible == true,
            $"{Math.Round(Time.GetTicksMsec() / 1000.0 - loadingStarted, 1)} s, {app.loadingView.history.Count} progress reports, {app.loadingHeartbeats} responsive ticks, title '{app.window.title}', HUD {app.raceHUD.frame.width}x{app.raceHUD.frame.height}");
        ok &= check("view is first responder", app.window.firstResponder == app.view);

        // Intro fly-in, then the countdown.
        if (await waitFor("intro half way", () => app.dirtIntro == null || app.dirtIntro >= app.dirtIntroDuration / 2, 10))
            await shot("intro");
        ok &= await waitFor("intro finished", () => app.dirtIntro == null, 10);
        ok &= check("countdown follows the intro", app.race.countdown > 2.5, $"countdown {Math.Round(app.race.countdown, 2)}");
        await shot("countdown");
        composite("race-start.png");

        // Pause with P, resume with Esc (both are App.swift's pause commands).
        await tap(Key.P);
        ok &= check("P pauses", app.simulation.paused && app.raceHUD.paused);
        var pausedCountdown = app.race.countdown;
        await seconds(0.5);
        ok &= check("countdown frozen while paused", app.race.countdown == pausedCountdown);
        await shot("paused");
        composite("race-pause.png");
        await tap(Key.Escape);
        ok &= check("Esc resumes", !app.simulation.paused);

        // Race: hold the keys DirtOpponent's logic asks for, every frame.
        ok &= await waitFor("race start", () => app.race.countdown <= 0, 10);
        var raceStart = Time.GetTicksMsec() / 1000.0;
        async Task driving() { if (app.dirtIntro == null && app.race.countdown <= 0 && !app.race.finished && !app.simulation.paused) drive(); else releaseAll(); await frame(); }
        await waitFor("4 s of racing", () => app.race.elapsed >= 4, 15, driving);
        ok &= check("player moves under keyboard input", app.simulation.distance > 3, $"distance {Math.Round(app.simulation.distance, 2)} m");
        hudCapture("dirt-hud.png");
        await shot("racing");
        // Camera: C cycles follow -> orbit -> overview -> follow.
        int mode0 = app.cameraMode;
        await tap(Key.C); int mode1 = app.cameraMode;
        await waitFor("orbit camera", () => app.race.elapsed >= 6, 10, driving);
        await shot("camera-orbit");
        await tap(Key.C); int mode2 = app.cameraMode;
        await waitFor("overview camera", () => app.race.elapsed >= 9, 10, driving);
        await shot("camera-overview");
        await tap(Key.C); int mode3 = app.cameraMode;
        ok &= check("C cycles the camera", mode0 == 0 && mode1 == 1 && mode2 == 2 && mode3 == 0, $"{mode0} {mode1} {mode2} {mode3}");
        // Toggle the keyboard guide with "?" twice.
        bool help = app.raceHUD.helpVisible;
        await tap(Key.Slash, shift: true); bool helpToggled = app.raceHUD.helpVisible != help;
        await tap(Key.Slash, shift: true);
        ok &= check("? toggles the keyboard guide", helpToggled && app.raceHUD.helpVisible == help);

        for (int lap = 1; lap <= 3; lap++)
        {
            int target = lap;
            ok &= await waitFor($"lap {lap}", () => app.race.laps.Length >= target, 120, driving);
            check($"lap {lap} complete", app.race.laps.Length >= target, app.race.laps.Length >= target ? $"{Math.Round(app.race.laps[target - 1], 2)} s, position {position()}" : null);
            if (lap == 1) await shot("lap-1");
        }
        releaseAll();
        ok &= check("race finished", app.race.finished, $"total {Math.Round(app.race.laps.Sum(), 2)} s, wall {Math.Round(Time.GetTicksMsec() / 1000.0 - raceStart, 1)} s");
        ok &= await waitFor("outro fly-out", () => app.dirtOutro >= app.dirtIntroDuration, 10);
        await frames(2);
        ok &= check("camera locked on the overview", app.raceCameraLocked && Math.Abs(app.world.camera.position.y - 38) < 0.001);
        await shot("finish");
        composite("race-finish-live.png");
        int scoreCount = File.Exists(Path.Combine(directory, "test-scores.json")) ? DirtScores.load(Path.Combine(directory, "test-scores.json")).Length : 0;
        ok &= check("score saved", scoreCount == 1 && app.scores.Length == 1, $"{scoreCount} stored");
        if (await waitFor("all robots finished", () => app.opponents.All(o => o.race.finished), 240))
        {
            await frames(2);
            await shot("results");
            composite("race-results.png");
        }
        // Post-race: the robots leave through town while the HUD steps aside.
        await waitFor("post-race departure", () => app.racePhysics.escape.active, 60);
        await seconds(3);
        ok &= check("post-race departure hides the race controls", !app.racePhysics.escape.active || (app.raceHUD.isHidden && app.window.toolbar?.isVisible == false), $"escape {app.racePhysics.escape.active}");
        await shot("post-race");

        // Command-R: race again (menu key equivalent, Control-R outside macOS).
        await tap(Key.R, command: true);
        ok &= check($"{KeyEquivalent.command}R restarts the race", !app.race.finished && app.race.countdown > 2.5 && app.race.laps.Length == 0 && !app.raceHUD.isHidden && app.window.toolbar?.isVisible == true,
            $"countdown {Math.Round(app.race.countdown, 2)}, laps {app.race.laps.Length}");
        ok &= check($"{KeyEquivalent.command}R is not a driving key", app.simulation.pitch == 0 && !app.view.held.Contains(15));
        await shot("race-again");

        // Command-M: main menu.
        await tap(Key.M, command: true);
        ok &= check($"{KeyEquivalent.command}M shows the main menu", !app.inSandbox && !app.mainMenu.isHidden && app.raceHUD.isHidden && app.window.toolbar?.isVisible == false);
        await shot("menu-again");

        // Sandbox through the keyboard (selection 0 is Sandbox).
        await tap(Key.Enter);
        ok &= check("Return starts the sandbox", app.inSandbox && !app.isDirtTrack && !app.hud.isHidden && app.window.toolbar?.isVisible == true,
            $"HUD {app.hud.frame.width}x{app.hud.frame.height}");
        await seconds(0.5);
        await shot("sandbox");
        key(Key.W, true); await seconds(1.0); key(Key.W, false);
        key(Key.D, true); await seconds(0.5); key(Key.D, false);
        await frames(2);
        ok &= check("W and D drive the sandbox robot", app.simulation.distance > 0.3 && Math.Abs(app.simulation.heading) > 0.1, $"distance {Math.Round(app.simulation.distance, 2)}, heading {Math.Round(app.simulation.heading, 2)}");
        await shot("sandbox-driven");
        // Mouse: drag orbits (ORBIT camera), scroll zooms.
        var center = new Vector2(640, 500);
        double yaw = app.orbitYaw, distance = app.cameraDistance;
        await drag(center, new Vector2(120, 30));
        ok &= check("mouse drag orbits the camera", app.cameraMode == 1 && Math.Abs(app.orbitYaw - yaw - 120 * 0.008) < 0.05, $"yaw {Math.Round(yaw, 3)} -> {Math.Round(app.orbitYaw, 3)}");
        await scroll(center, up: false, notches: 3);
        ok &= check("scrolling zooms", app.cameraDistance != distance, $"distance {distance} -> {Math.Round(app.cameraDistance, 3)}");
        await seconds(0.3);
        await shot("sandbox-orbit");
        // Toolbar buttons with the mouse.
        var items = toolbarCenters();
        note("toolbarItems", items.ToDictionary(p => p.Key, p => (object)new[] { Math.Round(p.Value.X, 1), Math.Round(p.Value.Y, 1) }));
        int before = app.cameraMode;
        await click(items["camera"]);
        ok &= check("toolbar Camera cycles the camera", app.cameraMode == (before + 1) % 3, $"{before} -> {app.cameraMode}");
        await click(items["pause"]);
        ok &= check("toolbar Pause pauses", app.simulation.paused && app.pauseItem.label == "Resume");
        await seconds(0.3);
        await shot("sandbox-paused");
        await click(items["pause"]);
        ok &= check("toolbar Resume resumes", !app.simulation.paused && app.pauseItem.label == "Pause");
        bool guide = app.hud.helpVisible;
        await click(items["help"]);
        ok &= check("toolbar Controls toggles the guide", app.hud.helpVisible != guide);
        await click(items["help"]);
        // Command-1 cycles the camera, Esc pauses, H centres the head.
        before = app.cameraMode;
        await tap(Key.Key1, command: true);
        ok &= check($"{KeyEquivalent.command}1 cycles the camera", app.cameraMode == (before + 1) % 3);
        await tap(Key.Escape);
        ok &= check("Esc pauses the sandbox", app.simulation.paused);
        await tap(Key.Escape);
        key(Key.Q, true); await seconds(0.4); key(Key.Q, false); await frame();
        bool headTurned = app.simulation.yaw != 0;
        await tap(Key.H); await seconds(0.6);
        ok &= check("Q turns the head and H centres it", headTurned && Math.Abs(app.simulation.yaw) < Math.Abs(0.5), $"yaw {Math.Round(app.simulation.yaw, 3)}");
        await click(items["reset"]);
        ok &= check("toolbar Reset resets the sandbox", app.simulation.distance == 0);
        await shot("sandbox-reset");
        await click(items["menu"]);
        ok &= check("toolbar Main Menu returns to the menu", !app.inSandbox && !app.mainMenu.isHidden);
        await seconds(0.3);
        await shot("menu-final");
        ok &= checkMenuBar();
        keepActive = false;
        return ok;
    }

    /// <summary>The menu bar's menus and items (global on macOS) and the mute item chosen as from the menu.</summary>
    private bool checkMenuBar()
    {
        var bar = NSApplicationHost.currentMenuBar;
        if (bar == null) return check("menu bar installed", false);
        var menus = new Dictionary<string, object>();
        PopupMenu simulation = null;
        foreach (var popup in bar.GetChildren().OfType<PopupMenu>())
        {
            var items = new List<object>();
            for (int i = 0; i < popup.ItemCount; i++)
                items.Add(popup.IsItemSeparator(i) ? "-" : popup.GetItemText(i) + (popup.GetItemAccelerator(i) != Key.None ? "  " + OS.GetKeycodeString(popup.GetItemAccelerator(i)) : "") + (popup.IsItemCheckable(i) ? (popup.IsItemChecked(i) ? "  [x]" : "  [ ]") : ""));
            menus[popup.Name] = items;
            if (popup.Name == "Simulation") simulation = popup;
        }
        note("menuBar", menus);
        note("globalMenu", bar.IsNativeMenu());
        if (bar.IsNativeMenu())
        {
            var main = NativeMenu.GetSystemMenu(NativeMenu.SystemMenus.MainMenuId);
            var titles = new List<object>();
            for (int i = 0; i < NativeMenu.GetItemCount(main); i++) titles.Add(NativeMenu.GetItemText(main, i));
            note("globalMenuTitles", titles);
        }
        if (simulation == null) return check("Simulation menu", false);
        int index = Enumerable.Range(0, simulation.ItemCount).FirstOrDefault(i => simulation.GetItemText(i) == "Mute race sound", -1);
        if (index < 0) return check("Mute race sound item", false);
        bool muted = app.raceSoundMuted;
        simulation.EmitSignal(PopupMenu.SignalName.IdPressed, simulation.GetItemId(index));
        bool toggled = app.raceSoundMuted != muted && simulation.IsItemChecked(index) == app.raceSoundMuted && UserDefaults.standard.@bool("raceSoundMuted") == app.raceSoundMuted;
        simulation.EmitSignal(PopupMenu.SignalName.IdPressed, simulation.GetItemId(index));
        return check("menu bar: Mute race sound toggles", toggled && app.raceSoundMuted == muted, $"global {bar.IsNativeMenu()}");
    }

    private int position()
    {
        var races = new[] { app.race }.Concat(app.opponents.Select(o => o.race)).ToArray();
        return Array.IndexOf(DirtStandings.order(races), 0) + 1;
    }
    /// <summary>The toolbar items' centres in window pixels (the title bar's layout).</summary>
    private Dictionary<string, Vector2> toolbarCenters()
    {
        var result = new Dictionary<string, Vector2>();
        var bar = app.window.frameView;
        foreach (var (item, centerX, _) in bar.itemLayoutForTests())
            result[item.itemIdentifier] = bar.GetGlobalRect().Position + new Vector2((float)centerX, 26);
        return result;
    }
}
