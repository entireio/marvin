using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

/// <summary>
/// Capture and smoke modes for the AppKit UI ports, mirroring the macOS game's native checks:
/// - --menu-smoke-test DIR: the menu part of AppController.tick's smoke branch (main-menu.png, portrait drag,
///   hidden B/R/W/M keys with menu-b/r/w/m.png, keyboard navigation into Settings) and
///   DrivingAssistSmoke.checkDrivingAssistSettings (driving-assist-settings.png); prints
///   "Native menu smoke test: PASS/FAIL".
/// - --hud-smoke-test DIR: the HUD captures of the composite --smoke-test with synthetic race state
///   (dirt-hud.png, race-finish-live.png, race-results.png, race-start.png, race-pause.png), the loading
///   screen of --loading-smoke-test (loading.png), plus views the Mac never captures (sandbox HUD, FPS HUD,
///   storm warning, town departure, TOWN/DUNES maps) and SimulatorView input checks (hud-smoke.json).
///   tools/scenekit-reference/ui-reference/build.sh renders the same states with the macOS game's own AppKit files.
/// Window content is 1280x792 points, the macOS window's content layout area below the title bar.
/// </summary>
public static class UISmoke
{
    internal const double ContentWidth = 1280, ContentHeight = 792, WindowHeight = 820;
    internal const string Resources = "res://assets/";

    internal static async Task Frame(SceneTree tree) => await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
    /// <summary>A window content view (AppKit window coordinates) hosting overlays like the Mac window's.</summary>
    internal static NSView Content(SceneTree tree)
    {
        var content = new NSView(new NSRect(0, 0, ContentWidth, WindowHeight));
        tree.Root.AddChild(content);
        return content;
    }
    internal static void Write(NSBitmapImageRep bitmap, string dir, string name)
    {
        var png = bitmap.representation(NSBitmapImageFileType.png, new());
        png.write(URL.fileURLWithPath(dir).appendingPathComponent(name));
    }
    internal static void Write(NSImage image, string dir, string name)
    {
        if (image.tiffRepresentation is byte[] tiff && NSBitmapImageRep.data(tiff) is NSBitmapImageRep bitmap) Write(bitmap, dir, name);
    }
    /// <summary>The live window as drawn on screen (Godot _Draw path and SCNViews), for checking the real views against the captures.</summary>
    internal static async Task WriteWindow(SceneTree tree, string dir, string name)
    {
        await Frame(tree);
        await tree.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var image = tree.Root.GetTexture().GetImage();
        image.SavePng(URL.fileURLWithPath(dir).appendingPathComponent(name).path);
    }

    // ---------------------------------------------------------------- menu
    [GameMode("--menu-smoke-test")]
    public static async Task RunMenu(string dir, SceneTree tree)
    {
        FileManager.@default.createDirectory(dir, withIntermediateDirectories: true);
        // The Mac captures were taken with the settings at their defaults (all On).
        var settingKeys = new[] { "reduceMenuMotion", "hideKeyboardGuide", "disableRaceRobotCollisions", "disableRaceSteeringAssist", "disableRaceBrakingAssist" };
        var savedSettings = settingKeys.Select(k => UserDefaults.standard.@object(k)).ToArray();
        foreach (var k in settingKeys) UserDefaults.standard.removeObject(k);
        bool menuSmokePassed = false;
        try
        {
            var content = Content(tree);
            var mainMenu = new MainMenuView(NSRect.zero);
            mainMenu.frame = new NSRect(0, 0, ContentWidth, ContentHeight);
            content.addSubview(mainMenu);
            var robot = new Robot(Resources); var r2d2 = new R2D2(Resources);
            var bb8 = new ImportedRacer(ImportedRacer.Kind.bb8, Resources); var wallE = new ImportedRacer(ImportedRacer.Kind.wallE, Resources);
            bool inSandbox = false;
            mainMenu.onSandbox = () => { inSandbox = true; mainMenu.isHidden = true; };
            // showMainMenu(nil)
            mainMenu.portrait.rendersContinuously = true;
            mainMenu.isHidden = false;
            mainMenu.settings = false; mainMenu.selection = 0; mainMenu.refresh();
            mainMenu.stage.rootNode.addChildNode(robot.root);
            mainMenu.animate(robot, r2d2, bb8, wallE, dt: 0);
            await Frame(tree);
            var window = mainMenu.window;
            window.makeFirstResponder(mainMenu);
            // tick(): the menu animates every frame; the check runs on frame 20.
            double lastTime = ProcessInfo.processInfo.systemUptime;
            for (int menuSmokeFrames = 1; menuSmokeFrames <= 20; menuSmokeFrames++)
            {
                await Frame(tree);
                var now = ProcessInfo.processInfo.systemUptime;
                var dt = min(max(0, now - lastTime), 0.1); lastTime = now;
                mainMenu.animate(robot, r2d2, bb8, wallE, dt: dt);
            }
            // Capture the native controls and composite the Metal portrait for visual QA.
            mainMenu.layoutSubtreeIfNeeded();
            if (mainMenu.bitmapImageRepForCachingDisplay(mainMenu.bounds) is NSBitmapImageRep bitmap)
            {
                mainMenu.cacheDisplay(mainMenu.bounds, bitmap);
                var image = new NSImage(mainMenu.bounds.size);
                image.lockFocus();
                bitmap.draw(mainMenu.bounds);
                mainMenu.portrait.snapshot().draw(mainMenu.portrait.frame);
                image.unlockFocus();
                Write(image, dir, "main-menu.png");
            }
            menuSmokePassed = !mainMenu.isHidden && robot.root.parent == mainMenu.stage.rootNode;
            // Exercise portrait dragging through its native mouse handlers.
            var initialYaw = robot.root.eulerAngles.y;
            var portraitPoint = mainMenu.portrait.convert(new NSPoint(150, 200), to: null);
            void portraitMouse(NSEvent.EventType type, CGFloat offset)
            {
                var @event = NSEvent.mouseEvent(type, new NSPoint(portraitPoint.x + offset, portraitPoint.y),
                    0, 0, window.windowNumber, null, 0, 1, 1);
                switch (type)
                {
                    case NSEvent.EventType.leftMouseDown: mainMenu.portrait.mouseDown(@event); break;
                    case NSEvent.EventType.leftMouseDragged: mainMenu.portrait.mouseDragged(@event); break;
                    default: mainMenu.portrait.mouseUp(@event); break;
                }
            }
            portraitMouse(NSEvent.EventType.leftMouseDown, 0);
            portraitMouse(NSEvent.EventType.leftMouseDragged, 80);
            portraitMouse(NSEvent.EventType.leftMouseUp, 80);
            mainMenu.animate(robot, r2d2, bb8, wallE, dt: 1.0 / 60);
            bool dragPassed = abs(robot.root.eulerAngles.y - initialYaw - 0.8) < 0.001;
            bool responderPassed = window.firstResponder == mainMenu;
            menuSmokePassed = menuSmokePassed && dragPassed && responderPassed;
            portraitMouse(NSEvent.EventType.leftMouseDown, 80);
            portraitMouse(NSEvent.EventType.leftMouseDragged, 0);
            portraitMouse(NSEvent.EventType.leftMouseUp, 0);
            mainMenu.animate(robot, r2d2, bb8, wallE, dt: 1.0 / 60);
            menuSmokePassed = menuSmokePassed && abs(robot.root.eulerAngles.y - initialYaw) < 0.001;
            await WriteWindow(tree, dir, "main-menu-window.png");
            var shortcutsPassed = new Dictionary<string, bool>();
            foreach (var (shortcut, model) in new[] { ("b", bb8.root), ("r", r2d2.root), ("w", wallE.root), ("m", robot.root) })
            {
                var @event = NSEvent.keyEvent(NSEvent.EventType.keyDown, NSPoint.zero, 0, 0,
                    window.windowNumber, null, shortcut, shortcut, false, 0);
                mainMenu.keyDown(@event);
                mainMenu.animate(robot, r2d2, bb8, wallE, dt: 1.0 / 60);
                var models = new[] { robot.root, r2d2.root, bb8.root, wallE.root };
                bool passed = model.parent == mainMenu.stage.rootNode
                    && models.Count(m => m.parent == mainMenu.stage.rootNode) == 1;
                portraitMouse(NSEvent.EventType.leftMouseDown, 0);
                portraitMouse(NSEvent.EventType.leftMouseDragged, 40);
                portraitMouse(NSEvent.EventType.leftMouseUp, 40);
                mainMenu.animate(robot, r2d2, bb8, wallE, dt: 1.0 / 60);
                passed = passed && abs(model.eulerAngles.y - initialYaw - 0.4) < 0.001;
                shortcutsPassed[shortcut] = passed;
                menuSmokePassed = menuSmokePassed && passed;
                Write(mainMenu.portrait.snapshot(), dir, $"menu-{shortcut}.png");
                portraitMouse(NSEvent.EventType.leftMouseDown, 40);
                portraitMouse(NSEvent.EventType.leftMouseDragged, 0);
                portraitMouse(NSEvent.EventType.leftMouseUp, 0);
                mainMenu.animate(robot, r2d2, bb8, wallE, dt: 0);
            }
            // Real Godot input through the GUI (not in the Mac test, which calls the handlers directly): hovering a
            // button selects it (tracking area), clicking activates it, dragging the portrait rotates the model and
            // the hidden character keys reach the menu as first responder.
            var godotInput = new Dictionary<string, bool>();
            {
                mainMenu.settings = false; mainMenu.selection = 0; mainMenu.refresh();
                mainMenu.layoutSubtreeIfNeeded();
                await Frame(tree);
                var menuButtons = mainMenu.subviews.OfType<NSButton>().ToList();
                void Push(InputEvent e) => tree.Root.PushInput(e);
                void Move(Godot.Vector2 p, Godot.Vector2 relative, MouseButtonMask mask) =>
                    Push(new InputEventMouseMotion { Position = p, GlobalPosition = p, Relative = relative, ButtonMask = mask });
                void Button(Godot.Vector2 p, bool pressed) =>
                    Push(new InputEventMouseButton { Position = p, GlobalPosition = p, ButtonIndex = MouseButton.Left, Pressed = pressed, ButtonMask = pressed ? MouseButtonMask.Left : 0 });
                // Godot only tracks hover while the OS cursor is in the window; declare it inside (the test pushes events).
                tree.Root.Notification((int)Node.NotificationWMMouseEnter);
                var settingsAt = menuButtons[2].GetGlobalRect().GetCenter();
                Move(settingsAt - new Godot.Vector2(0, 40), Godot.Vector2.Zero, 0); Move(settingsAt, new Godot.Vector2(0, 40), 0);
                await Frame(tree);
                godotInput["hoverSelects"] = mainMenu.selection == 2;
                Button(settingsAt, true); Button(settingsAt, false);
                await Frame(tree);
                godotInput["clickActivates"] = mainMenu.settings;
                mainMenu.layoutSubtreeIfNeeded(); await Frame(tree);
                var backAt = menuButtons[5].GetGlobalRect().GetCenter();
                Move(backAt, Godot.Vector2.Zero, 0); Button(backAt, true); Button(backAt, false);
                await Frame(tree);
                godotInput["clickBack"] = !mainMenu.settings && mainMenu.selection == 2;
                var yawBefore = robot.root.eulerAngles.y;
                var portraitAt = mainMenu.portrait.GetGlobalRect().GetCenter();
                Move(portraitAt, Godot.Vector2.Zero, 0); Button(portraitAt, true);
                Move(portraitAt + new Godot.Vector2(30, 0), new Godot.Vector2(30, 0), MouseButtonMask.Left);
                Move(portraitAt + new Godot.Vector2(50, 0), new Godot.Vector2(20, 0), MouseButtonMask.Left);
                Button(portraitAt + new Godot.Vector2(50, 0), false);
                await Frame(tree);
                mainMenu.animate(robot, r2d2, bb8, wallE, dt: 0);
                godotInput["portraitDrag"] = abs(robot.root.eulerAngles.y - yawBefore - 0.5) < 0.001;
                godotInput["portraitFocusesMenu"] = window.firstResponder == mainMenu;
                Push(new InputEventKey { PhysicalKeycode = Key.R, Keycode = Key.R, KeyLabel = Key.R, Unicode = 'r', Pressed = true });
                Push(new InputEventKey { PhysicalKeycode = Key.R, Keycode = Key.R, KeyLabel = Key.R, Unicode = 'r', Pressed = false });
                await Frame(tree);
                godotInput["characterKey"] = mainMenu.character == RacePerformance.Character.r2d2;
                Push(new InputEventKey { PhysicalKeycode = Key.M, Keycode = Key.M, KeyLabel = Key.M, Unicode = 'm', Pressed = true });
                await Frame(tree);
                mainMenu.animate(robot, r2d2, bb8, wallE, dt: 0);
                // Restore the model's yaw for the remaining checks.
                Move(portraitAt, Godot.Vector2.Zero, 0); Button(portraitAt, true);
                Move(portraitAt - new Godot.Vector2(50, 0), new Godot.Vector2(-50, 0), MouseButtonMask.Left);
                Button(portraitAt - new Godot.Vector2(50, 0), false);
                Move(new Godot.Vector2(2, 2), Godot.Vector2.Zero, 0);
                await Frame(tree);
                mainMenu.animate(robot, r2d2, bb8, wallE, dt: 0);
            }
            bool godotInputPassed = godotInput.Values.All(v => v);
            menuSmokePassed = menuSmokePassed && godotInputPassed;
            var down = NSEvent.keyEvent(NSEvent.EventType.keyDown, NSPoint.zero, 0, 0,
                window.windowNumber, null, "", "", false, 125);
            var enter = NSEvent.keyEvent(NSEvent.EventType.keyDown, NSPoint.zero, 0, 0,
                window.windowNumber, null, "", "", false, 36);
            // Mouse hover while the window appears may change selection.
            // Establish the keyboard test's starting item explicitly.
            mainMenu.settings = false; mainMenu.selection = 0; mainMenu.refresh();
            mainMenu.keyDown(down); mainMenu.keyDown(down); mainMenu.keyDown(enter);
            bool keyboardPassed = mainMenu.settings;
            menuSmokePassed = menuSmokePassed && keyboardPassed;
            bool assistPassed;
            try { assistPassed = checkDrivingAssistSettings(mainMenu, dir); }
            catch (Exception e) { GD.PushError(e.ToString()); assistPassed = false; }
            menuSmokePassed = assistPassed && menuSmokePassed;
            mainMenu.selection = 5; mainMenu.activate();
            menuSmokePassed = menuSmokePassed && !mainMenu.settings;
            mainMenu.selection = 0; mainMenu.activate();
            menuSmokePassed = menuSmokePassed && inSandbox && mainMenu.isHidden;
            var shortcuts = new Godot.Collections.Dictionary();
            foreach (var (shortcut, passed) in shortcutsPassed) shortcuts[shortcut] = passed;
            var godotInputReport = new Godot.Collections.Dictionary();
            foreach (var (check, passed) in godotInput) godotInputReport[check] = passed;
            var report = new Godot.Collections.Dictionary
            {
                ["menuPassed"] = menuSmokePassed, ["dragPassed"] = dragPassed, ["firstResponderPassed"] = responderPassed,
                ["keyboardPassed"] = keyboardPassed, ["drivingAssistPassed"] = assistPassed, ["shortcutsPassed"] = shortcuts,
                ["godotInput"] = godotInputReport,
            };
            System.IO.File.WriteAllText(URL.fileURLWithPath(dir).appendingPathComponent("menu-smoke.json").path, Json.Stringify(report, "  ", sortKeys: true));
            // PORT: SCNNodes removed from a scene are not freed automatically (PORTING.md); free the models the menu detached.
            foreach (var root in new[] { robot.root, r2d2.root, bb8.root, wallE.root }) if (root.GetParent() == null) root.Free();
        }
        finally
        {
            for (int i = 0; i < settingKeys.Length; i++)
            {
                if (savedSettings[i] != null) UserDefaults.standard.set(savedSettings[i], settingKeys[i]);
                else UserDefaults.standard.removeObject(settingKeys[i]);
            }
        }
        print($"Native menu smoke test: {(menuSmokePassed ? "PASS" : "FAIL")} · {dir}");
        exit(menuSmokePassed ? 0 : 1);
    }

    /// <summary>
    /// DrivingAssistSmoke.checkDrivingAssistSettings, on the menu instead of AppController.
    /// PORT: on the Mac, layoutSubtreeIfNeeded re-applies the overlay's Auto Layout constraints, so the 900x550
    /// frame reverts to the 1280x792 content size before the check and the capture. Here there are no
    /// constraints: the check and driving-assist-settings-900x550.png use the intended 900x550 frame, and
    /// driving-assist-settings.png is captured at the content size like the Mac's.
    /// </summary>
    internal static bool checkDrivingAssistSettings(MainMenuView mainMenu, string directory)
    {
        var keys = new[] { "disableRaceSteeringAssist", "disableRaceBrakingAssist" };
        var saved = keys.Select(k => UserDefaults.standard.@object(k)).ToArray();
        var frame = mainMenu.frame;
        try
        {
            foreach (var k in keys) UserDefaults.standard.removeObject(k);
            var passed = mainMenu.steeringAssist && mainMenu.brakingAssist;
            mainMenu.selection = 3; mainMenu.activate();
            passed = passed && !mainMenu.steeringAssist && mainMenu.brakingAssist;
            mainMenu.selection = 4; mainMenu.activate();
            passed = passed && !mainMenu.steeringAssist && !mainMenu.brakingAssist;
            mainMenu.selection = 3; mainMenu.activate();
            passed = passed && mainMenu.steeringAssist && !mainMenu.brakingAssist;
            // A fresh settings view must read the persisted, independent choices.
            var reopened = new MainMenuView(NSRect.zero);
            passed = passed && reopened.steeringAssist && !reopened.brakingAssist;
            reopened.Free();
            mainMenu.selection = 4; mainMenu.activate();
            mainMenu.frame = new NSRect(0, 0, 900, 550);
            mainMenu.layoutSubtreeIfNeeded();
            var buttons = mainMenu.subviews.OfType<NSButton>().Where(b => !b.isHidden).ToList();
            passed = passed && buttons.Count == 6 && buttons.All(b => mainMenu.bounds.contains(b.frame));
            if (mainMenu.bitmapImageRepForCachingDisplay(mainMenu.bounds) is NSBitmapImageRep small)
            {
                mainMenu.cacheDisplay(mainMenu.bounds, small);
                Write(small, directory, "driving-assist-settings-900x550.png");
            }
            else passed = false;
            mainMenu.frame = frame;
            mainMenu.layoutSubtreeIfNeeded();
            if (mainMenu.bitmapImageRepForCachingDisplay(mainMenu.bounds) is NSBitmapImageRep bitmap)
            {
                mainMenu.cacheDisplay(mainMenu.bounds, bitmap);
                Write(bitmap, directory, "driving-assist-settings.png");
            }
            else passed = false;
            return passed;
        }
        finally
        {
            for (int i = 0; i < keys.Length; i++)
            {
                if (saved[i] != null) UserDefaults.standard.set(saved[i], keys[i]);
                else UserDefaults.standard.removeObject(keys[i]);
            }
            mainMenu.frame = frame; mainMenu.refresh(); mainMenu.needsLayout = true;
        }
    }

    // ---------------------------------------------------------------- HUD
    /// <summary>Synthetic DirtRace (test scaffolding): sets the state a finished or running race would have.</summary>
    internal static DirtRace SyntheticRace(double elapsed, double[] laps, double countdown = 0, double? progress = null)
    {
        object boxed = new DirtRace();
        var type = typeof(DirtRace);
        void set(string name, object value) => type.GetProperty(name).GetSetMethod(true).Invoke(boxed, new[] { value });
        set("countdown", countdown);
        set("elapsed", elapsed);
        set("laps", laps);
        set("progress", progress ?? 2 * Math.PI * laps.Length);
        type.GetField("lapStart", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(boxed, laps.Sum());
        return (DirtRace)boxed;
    }
    /// <summary>Three finished laps whose best is `best` and whose sum is `total`.</summary>
    private static double[] Laps(double total, double best) => new[] { total - 2 * best - 0.6, best + 0.6, best };

    private static async Task<NSBitmapImageRep> CaptureHUD(RaceHUD hud, NSRect rect, SceneTree tree)
    {
        hud.needsDisplay = true;
        await Frame(tree);
        var bitmap = hud.bitmapImageRepForCachingDisplay(rect);
        hud.cacheDisplay(rect, bitmap);
        return bitmap;
    }

    [GameMode("--hud-smoke-test")]
    public static async Task RunHUD(string dir, SceneTree tree)
    {
        FileManager.@default.createDirectory(dir, withIntermediateDirectories: true);
        var report = new Godot.Collections.Dictionary();
        var content = Content(tree);
        var raceHUD = new RaceHUD();
        content.addSubview(raceHUD);
        raceHUD.frame = new NSRect(0, 0, ContentWidth, ContentHeight);
        raceHUD.helpVisible = true;

        // dirt-hud.png: the smoke test's 240-frame autopilot drive after the countdown (elapsed 4.00 s), everyone on
        // lap 1, the player in front. reset(nil) shuffles the grid with SystemRandomNumberGenerator; the slots below
        // (player, R2-D2, BB-8, WALL-E) are the draw of the Mac reference run, identified from where its race-start
        // map shows the player (this drive's end): of the 24 orders only this one puts the marker and its heading
        // line on the Mac's pixels (IoU 0.79; the next best 0.51).
        {
            var slots = new[] { DirtCourse.startingGrid[0], DirtCourse.startingGrid[3], DirtCourse.startingGrid[1], DirtCourse.startingGrid[2] };
            var simulation = new Simulation(dirtTrack: true, dirtStartOffset: slots[0].offset, dirtStartPhase: slots[0].phase);
            var race = new DirtRace(startPhase: slots[0].phase); race.countDown(3);
            var opponents = new[] { new DirtOpponent(slots[1]), new DirtOpponent(slots[2], laneOffset: 0), new DirtOpponent(slots[3], laneOffset: -0.65) };
            // advanceRacePhysics: the shared 240 Hz race physics with the menu's default assists and robot collisions.
            // PORT: the dune sand field and the town collision world (DirtWorld) are left out; neither acts on the
            // start straight in these four seconds.
            var racePhysics = new DirtRacePhysics();
            for (int frame = 0; frame < 240; frame++)
            {
                var phase = DirtCourse.phase(simulation.x, simulation.z);
                var target = DirtCourse.point(phase + 0.055);
                var desired = atan2(target.x - simulation.x, target.z - simulation.z);
                var error = atan2(sin(desired - simulation.heading), cos(desired - simulation.heading));
                var input = new DriveInput(); input.throttle = 1; input.boost = true; input.turn = -error * 1.5;
                racePhysics.advance(input, ref simulation, ref race, opponents, 1.0 / 60, 1.0 / 60,
                    robotCollisionsEnabled: true, assists: new DirtDrivingAssists(steering: true, braking: true));
            }
            raceHUD.opponents = opponents; raceHUD.race = race;
            raceHUD.x = simulation.x; raceHUD.z = simulation.z; raceHUD.heading = simulation.heading;
            raceHUD.introducing = false; raceHUD.paused = false;
            report["dirtHudPosition"] = raceHUD.position;
            report["dirtHudElapsed"] = race.elapsed;
            // SCNView.snapshot excludes AppKit subviews; capture the HUD
            // separately for layout QA at the minimum supported window size.
            // PORT: on the Mac the overlay's constraints keep it at the 1280x792 content size while the bitmap is
            // 900x550, so dirt-hud.png is the top-left 900x550 of the full-size HUD; dirt-hud-900x550.png is the
            // HUD laid out at 900x550.
            Write(await CaptureHUD(raceHUD, new NSRect(0, 0, 900, 550), tree), dir, "dirt-hud.png");
            raceHUD.frame = new NSRect(0, 0, 900, 550);
            Write(await CaptureHUD(raceHUD, raceHUD.bounds, tree), dir, "dirt-hud-900x550.png");
            raceHUD.frame = new NSRect(0, 0, ContentWidth, ContentHeight);
            Write(await CaptureHUD(raceHUD, raceHUD.bounds, tree), dir, "dirt-hud-1280x792.png");
        }

        // RaceFinishSmoke captures (900x550): the classification while rivals finish, the final results,
        // then the next race's start countdown and its pause overlay. Values are the Mac capture's.
        raceHUD.frame = new NSRect(0, 0, 900, 550);
        var wallE = SyntheticRace(89.18, Laps(89.18, 27.12));
        var marvin = SyntheticRace(90.94, Laps(90.94, 27.55));
        var r2d2 = SyntheticRace(93.28, Laps(93.28, 27.49));
        var bb8Live = SyntheticRace(66.10, new[] { 34.86, 31.24 }, progress: 2 * Math.PI * 2.4);
        var bb8 = SyntheticRace(96.85, Laps(96.85, 27.54));
        DirtOpponent Rival((double phase, double offset) slot, DirtRace race, double laneOffset = 0.65)
        {
            var o = new DirtOpponent(slot, laneOffset); o.race = race; return o;
        }
        raceHUD.race = marvin; raceHUD.introducing = false; raceHUD.paused = false; raceHUD.escaping = false;
        raceHUD.opponents = new[] { Rival(DirtCourse.startingGrid[1], r2d2), Rival(DirtCourse.startingGrid[2], bb8Live, 0), Rival(DirtCourse.startingGrid[3], wallE, -0.65) };
        Write(await CaptureHUD(raceHUD, raceHUD.bounds, tree), dir, "race-finish-live.png");
        raceHUD.opponents = new[] { Rival(DirtCourse.startingGrid[1], r2d2), Rival(DirtCourse.startingGrid[2], bb8, 0), Rival(DirtCourse.startingGrid[3], wallE, -0.65) };
        Write(await CaptureHUD(raceHUD, raceHUD.bounds, tree), dir, "race-results.png");
        // reset(nil): a new countdown with the player on the last grid slot (POSITION 4 / 4) and WALL-E, R2-D2, BB-8
        // ahead of it, as in the Mac capture. RaceFinishSmoke's capture() does not update the HUD's player position,
        // so the map still shows where the 240-frame drive of the dirt-hud capture ended.
        {
            var slots = new[] { DirtCourse.startingGrid[3], DirtCourse.startingGrid[1], DirtCourse.startingGrid[2], DirtCourse.startingGrid[0] };
            raceHUD.race = new DirtRace(startPhase: slots[0].phase);
            raceHUD.opponents = new[] { new DirtOpponent(slots[1]), new DirtOpponent(slots[2], laneOffset: 0), new DirtOpponent(slots[3], laneOffset: -0.65) };
            raceHUD.scores = Array.Empty<DirtScore>();
            Write(await CaptureHUD(raceHUD, raceHUD.bounds, tree), dir, "race-start.png");
            raceHUD.paused = true;
            Write(await CaptureHUD(raceHUD, raceHUD.bounds, tree), dir, "race-pause.png");
            raceHUD.paused = false;
            report["raceStartPosition"] = raceHUD.position;
        }
        // Views without a Mac capture: high scores, the storm warning, the town departure and the exploration maps.
        {
            raceHUD.scores = new[] { new DirtScore(Laps(88.4, 27.0), 0), new DirtScore(Laps(90.94, 27.55), 0), new DirtScore(Laps(95.1, 29.2), 0) };
            raceHUD.saveError = null;
            Write(await CaptureHUD(raceHUD, raceHUD.bounds, tree), dir, "race-scores.png");
            raceHUD.stormSelected = true; raceHUD.introducing = true;
            Write(await CaptureHUD(raceHUD, raceHUD.bounds, tree), dir, "race-storm-warning.png");
            raceHUD.introducing = false;
            Write(await CaptureHUD(raceHUD, raceHUD.bounds, tree), dir, "race-storm-start.png");
            raceHUD.stormSelected = false;
            raceHUD.race = marvin;
            raceHUD.opponents = new[] { Rival(DirtCourse.startingGrid[1], r2d2), Rival(DirtCourse.startingGrid[2], bb8, 0), Rival(DirtCourse.startingGrid[3], wallE, -0.65) };
            raceHUD.escaping = true; raceHUD.escapeComplete = false;
            Write(await CaptureHUD(raceHUD, raceHUD.bounds, tree), dir, "race-departure.png");
            raceHUD.escapeComplete = true;
            Write(await CaptureHUD(raceHUD, raceHUD.bounds, tree), dir, "race-departure-complete.png");
            raceHUD.escaping = false;
            raceHUD.race = new DirtRace(); raceHUD.race.countDown(3);
            raceHUD.opponents = new[] { new DirtOpponent(DirtCourse.startingGrid[1]), new DirtOpponent(DirtCourse.startingGrid[2], laneOffset: 0), new DirtOpponent(DirtCourse.startingGrid[3], laneOffset: -0.65) };
            raceHUD.configureNavigationMap(new TownWorld());
            raceHUD.mapRegion = RaceMapRegion.town; raceHUD.x = 72; raceHUD.z = -96; raceHUD.heading = 0.7;
            Write(await CaptureHUD(raceHUD, raceHUD.bounds, tree), dir, "race-map-town.png");
            raceHUD.mapRegion = RaceMapRegion.dunes; raceHUD.x = 260; raceHUD.z = 180; raceHUD.heading = -2.2;
            Write(await CaptureHUD(raceHUD, raceHUD.bounds, tree), dir, "race-map-dunes.png");
            raceHUD.mapRegion = RaceMapRegion.course;
        }
        // Live drawing: the race-start state on screen, and the cost of one RaceHUD.draw on the canvas backend.
        {
            var slots = new[] { DirtCourse.startingGrid[3], DirtCourse.startingGrid[1], DirtCourse.startingGrid[2], DirtCourse.startingGrid[0] };
            raceHUD.race = new DirtRace(startPhase: slots[0].phase);
            raceHUD.opponents = new[] { new DirtOpponent(slots[1]), new DirtOpponent(slots[2], laneOffset: 0), new DirtOpponent(slots[3], laneOffset: -0.65) };
            raceHUD.frame = new NSRect(0, 0, ContentWidth, ContentHeight);
            raceHUD.needsDisplay = true;
            await WriteWindow(tree, dir, "race-start-window.png");
            var canvas = new NSGraphicsCanvas(raceHUD.GetCanvasItem());
            var watch = System.Diagnostics.Stopwatch.StartNew();
            const int draws = 200;
            for (int i = 0; i < draws; i++) { canvas.Reset(); NSViewRendering.Paint(raceHUD, canvas, Godot.Vector2.Zero); }
            report["raceHudDrawMs"] = watch.Elapsed.TotalMilliseconds / draws;
            canvas.Dispose();
            raceHUD.needsDisplay = true;
        }
        raceHUD.removeFromSuperview(); raceHUD.QueueFree();

        // loading.png: LevelLoadingView at the Mac capture's progress (49%, "Preparing the sand and racecourse").
        {
            var loadingView = new LevelLoadingView();
            content.addSubview(loadingView);
            loadingView.frame = new NSRect(0, 0, ContentWidth, ContentHeight);
            loadingView.begin();
            loadingView.update(0.4947, "Preparing the sand and racecourse");
            await Frame(tree);
            loadingView.layoutSubtreeIfNeeded(); loadingView.displayIfNeeded();
            var bitmap = loadingView.bitmapImageRepForCachingDisplay(loadingView.bounds);
            loadingView.cacheDisplay(loadingView.bounds, bitmap);
            Write(bitmap, dir, "loading.png");
            loadingView.update(0.3, "regression");
            report["loadingMonotonic"] = loadingView.history.Zip(loadingView.history.Skip(1)).All(pair => pair.First <= pair.Second);
            loadingView.removeFromSuperview(); loadingView.QueueFree();
        }

        // Sandbox HUD (HUDView) and the FPS readout, which the Mac smoke tests never capture.
        {
            var view = new SimulatorView();
            content.AddChild(view);
            view.frame = new NSRect(0, 0, ContentWidth, WindowHeight);
            var hud = new HUDView();
            view.addSubview(hud);
            hud.frame = new NSRect(0, 0, ContentWidth, ContentHeight);
            // Seed 0: the exploration course's beacons are seeded (the game uses a random seed per sandbox run).
            var state = new Simulation(seed: 0);
            var input = new DriveInput(); input.throttle = 1; input.turn = 0.4;
            for (int i = 0; i < 40; i++) state.advance(input, 1.0 / 60);
            hud.state = state; hud.cameraName = "ORBIT";
            hud.needsDisplay = true; await Frame(tree);
            var bitmap = hud.bitmapImageRepForCachingDisplay(hud.bounds);
            hud.cacheDisplay(hud.bounds, bitmap);
            Write(bitmap, dir, "sandbox-hud.png");
            state.paused = true; hud.state = state;
            bitmap = hud.bitmapImageRepForCachingDisplay(hud.bounds);
            hud.cacheDisplay(hud.bounds, bitmap);
            Write(bitmap, dir, "sandbox-hud-paused.png");

            var fps = new FrameRateHUD();
            view.addSubview(fps);
            fps.frame = new NSRect(0, 0, ContentWidth, ContentHeight);
            var scene = new SCNScene();
            var camera = new SCNNode { camera = new SCNCamera() };
            scene.rootNode.addChildNode(camera);
            view.scene = scene; view.pointOfView = camera;
            view.@delegate = fps;
            fps.resetSamples();
            bitmap = fps.bitmapImageRepForCachingDisplay(fps.bounds);
            fps.cacheDisplay(fps.bounds, bitmap);
            Write(bitmap, dir, "fps-hud-empty.png");
            var started = ProcessInfo.processInfo.systemUptime;
            while (fps.framesPerSecond == null && ProcessInfo.processInfo.systemUptime - started < 5) await Frame(tree);
            report["fpsSample"] = fps.framesPerSecond ?? -1;
            bitmap = fps.bitmapImageRepForCachingDisplay(fps.bounds);
            fps.cacheDisplay(fps.bounds, bitmap);
            Write(bitmap, dir, "fps-hud.png");

            // SimulatorView input (the composite smoke test's boost/steering, focus and command checks).
            await WriteWindow(tree, dir, "sandbox-window.png");
            var inputs = SimulatorInputChecks(view, tree);
            foreach (var (k, v) in inputs) report[k] = v;
            // Godot mouse input: clicks and drags over the HUD's cards reach the SimulatorView beneath (HUDView and
            // FrameRateHUD return nil from hitTest), making it first responder and orbiting; the wheel zooms.
            {
                CGFloat orbitX = 0, orbitY = 0, zoom = 0;
                view.onOrbit = (dx, dy) => { orbitX += dx; orbitY += dy; };
                view.onZoom = delta => zoom += delta;
                var overCard = hud.GetGlobalRect().Position + new Godot.Vector2(100, 60);
                void Push(InputEvent e) => tree.Root.PushInput(e);
                Push(new InputEventMouseMotion { Position = overCard, GlobalPosition = overCard });
                Push(new InputEventMouseButton { Position = overCard, GlobalPosition = overCard, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left });
                Push(new InputEventMouseMotion { Position = overCard + new Godot.Vector2(12, 5), GlobalPosition = overCard + new Godot.Vector2(12, 5), Relative = new Godot.Vector2(12, 5), ButtonMask = MouseButtonMask.Left });
                Push(new InputEventMouseButton { Position = overCard + new Godot.Vector2(12, 5), GlobalPosition = overCard + new Godot.Vector2(12, 5), ButtonIndex = MouseButton.Left, Pressed = false });
                Push(new InputEventMouseButton { Position = overCard, GlobalPosition = overCard, ButtonIndex = MouseButton.WheelDown, Pressed = true, Factor = 1 });
                await Frame(tree);
                report["godotMouseThroughHUD"] = view.window?.firstResponder == view && orbitX == 12 && orbitY == 5 && zoom == -1;
            }
            view.removeFromSuperview(); view.QueueFree();
        }
        bool passed = report["boostSteeringPassed"].AsBool() && report["focusPassed"].AsBool() && report["godotKeyRoutingPassed"].AsBool()
            && report["pausePassed"].AsBool() && report["brakePassed"].AsBool() && report["headPassed"].AsBool()
            && report["commandPassed"].AsBool() && report["godotMouseThroughHUD"].AsBool()
            && report["loadingMonotonic"].AsBool() && report["dirtHudElapsed"].AsDouble() > 3.99;
        report["passed"] = passed;
        System.IO.File.WriteAllText(URL.fileURLWithPath(dir).appendingPathComponent("hud-smoke.json").path, Json.Stringify(report, "  ", sortKeys: true));
        print($"HUD smoke test: {(passed ? "PASS" : "FAIL")} · {dir}");
        exit(passed ? 0 : 1);
    }

    /// <summary>SimulatorView keyboard handling: NSEvent-level checks from the Mac smoke test, then real Godot key events.</summary>
    private static Dictionary<string, bool> SimulatorInputChecks(SimulatorView view, SceneTree tree)
    {
        var result = new Dictionary<string, bool>();
        void key(ushort code, bool down, NSEvent.ModifierFlags modifiers = 0)
        {
            var @event = NSEvent.keyEvent(down ? NSEvent.EventType.keyDown : NSEvent.EventType.keyUp, NSPoint.zero,
                modifiers, ProcessInfo.processInfo.systemUptime, 0, null, "", "", false, code);
            if (down) { view.keyDown(@event); } else { view.keyUp(@event); }
        }
        // The composite smoke test's sandbox drive (frames 30-89 W, 90-119 D, cleared at 120, checks at 150), each
        // frame advancing the sandbox simulation with the view's drive input.
        var simulation = new Simulation(seed: 0);
        for (int smokeFrames = 1; smokeFrames < 150; smokeFrames++)
        {
            if (smokeFrames >= 30 && smokeFrames < 90) key(13, down: true);
            if (smokeFrames >= 90 && smokeFrames < 120) { key(13, down: false); key(2, down: true); }
            if (smokeFrames == 120) view.clearInput();
            simulation.advance(view.driveInput, 1.0 / 60);
        }
        var boostInputPassed = true;
        foreach (var (forward, turn) in new (ushort, ushort)[] { (13, 0), (13, 2), (126, 123), (126, 124) })
        {
            view.clearInput();
            var shift = NSEvent.keyEvent(NSEvent.EventType.flagsChanged, NSPoint.zero,
                NSEvent.ModifierFlags.shift, 0, 0, null, "", "", false, 56);
            view.flagsChanged(shift);
            key(forward, down: true, modifiers: NSEvent.ModifierFlags.shift); key(turn, down: true, modifiers: NSEvent.ModifierFlags.shift);
            var input = view.driveInput;
            var sample = new Simulation(dirtTrack: true);
            var startHeading = sample.heading;
            sample.advance(input, 0.1); sample.advance(input, 0.1);
            var angle = atan2(sin(sample.heading - startHeading), cos(sample.heading - startHeading));
            boostInputPassed = boostInputPassed && input.boost && input.throttle == 1
                && abs(input.turn) == 1 && angle * input.turn < -0.04;
            key(turn, down: false, modifiers: NSEvent.ModifierFlags.shift);
            boostInputPassed = boostInputPassed && view.driveInput.turn == 0 && view.driveInput.boost;
        }
        result["boostSteeringPassed"] = boostInputPassed;
        view.clearInput();
        // PORT: AppController.togglePause (app stream): simulation.paused.toggle(); view.clearInput().
        void togglePause() { simulation.paused = !simulation.paused; view.clearInput(); }
        togglePause();
        var elapsed = simulation.elapsed;
        key(13, down: true);
        simulation.advance(view.driveInput, 0.1);
        result["pausePassed"] = simulation.paused && simulation.elapsed == elapsed;
        togglePause();
        key(13, down: true); key(49, down: true);
        simulation.advance(view.driveInput, 0.1);
        result["brakePassed"] = simulation.speed == 0;
        view.clearInput();
        result["focusPassed"] = view.held.Count == 0 && simulation.speed == 0;
        key(14, down: true); key(15, down: true);
        simulation.advance(view.driveInput, 0.1);
        result["headPassed"] = simulation.yaw < 0 && simulation.pitch > 0;
        view.clearInput();
        ushort command = 0xFFFF;
        view.onCommand = code => command = code;
        key(8, down: true);
        bool commandPassed = command == 8 && !view.held.Contains(8);
        view.clearInput();
        commandPassed = commandPassed && view.held.Count == 0;
        // Godot events through _GuiInput: physical W and Shift become keyCode 13 and flag 56, P a command.
        var w = new InputEventKey { PhysicalKeycode = Key.W, Keycode = Key.W, Pressed = true };
        var shiftKey = new InputEventKey { PhysicalKeycode = Key.Shift, Keycode = Key.Shift, Pressed = true, ShiftPressed = true };
        var p = new InputEventKey { PhysicalKeycode = Key.P, Keycode = Key.P, Pressed = true, Unicode = 'p' };
        view._GuiInput(w); view._GuiInput(shiftKey);
        bool routed = view.held.Contains(13) && view.held.Contains(56) && view.driveInput.throttle == 1 && view.driveInput.boost;
        view._GuiInput(new InputEventKey { PhysicalKeycode = Key.W, Keycode = Key.W, Pressed = false });
        view._GuiInput(new InputEventKey { PhysicalKeycode = Key.Shift, Keycode = Key.Shift, Pressed = false });
        routed = routed && view.held.Count == 0;
        command = 0xFFFF; view._GuiInput(p);
        routed = routed && command == 35;
        result["commandPassed"] = commandPassed;
        result["godotKeyRoutingPassed"] = routed;
        view.clearInput();
        return result;
    }
}
