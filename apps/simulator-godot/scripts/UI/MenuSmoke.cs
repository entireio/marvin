// The main-menu check of App.swift's tick (smoke branch, menu frame 20) and DrivingAssistSmoke.swift
// (AppController extensions). `--menu-smoke-test DIR` runs it alone; `--smoke-test DIR` runs it before the sandbox.
// Same captures as macOS (main-menu.png, menu-b/r/w/m.png, driving-assist-settings.png) plus Godot-only checks:
// real Godot input through the GUI (hover, click, portrait drag, character keys), main-menu-window.png (the window
// as drawn on screen), driving-assist-settings-900x550.png and menu-smoke.json.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

public partial class AppController
{
    [GameMode("--menu-smoke-test")]
    public static async Task RunMenuSmokeTest(string dir, SceneTree tree)
    {
        var app = await launchSmoke(tree, dir);
        await app.checkMainMenu(at: dir, tree);
        print($"Native menu smoke test: {(app.menuSmokePassed ? "PASS" : "FAIL")} · {dir}");
        exit(app.menuSmokePassed ? 0 : 1);
    }

    /// <summary>
    /// App.swift tick at menu frame 20 (the menu part of the smoke branch): sets menuSmokePassed and ends in the
    /// sandbox (mainMenu.activate()). PORT: asynchronous, because the Godot-only GUI input checks need frames; the
    /// timer is stopped meanwhile (on macOS the whole block runs inside one tick). The menu settings are at their
    /// defaults (all On, as in the Mac captures) during the check and restored afterwards.
    /// </summary>
    public async Task checkMainMenu(string at, SceneTree tree)
    {
        var directory = at;
        FileManager.@default.createDirectory(directory, withIntermediateDirectories: true);
        var settingKeys = new[] { "reduceMenuMotion", "hideKeyboardGuide", "disableRaceRobotCollisions", "disableRaceSteeringAssist", "disableRaceBrakingAssist" };
        var savedSettings = settingKeys.Select(k => UserDefaults.standard.@object(k)).ToArray();
        foreach (var k in settingKeys) UserDefaults.standard.removeObject(k);
        try
        {
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
                UISmoke.Write(image, directory, "main-menu.png");
            }
            menuSmokePassed = !mainMenu.isHidden && hud.isHidden && robot.root.parent == mainMenu.stage.rootNode;
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
            mainMenu.animate(robot, r2d2: r2d2, bb8: bb8, wallE: wallE, dt: 1.0 / 60);
            bool dragPassed = abs(robot.root.eulerAngles.y - initialYaw - 0.8) < 0.001;
            bool responderPassed = window.firstResponder == mainMenu;
            menuSmokePassed = menuSmokePassed && dragPassed && responderPassed;
            portraitMouse(NSEvent.EventType.leftMouseDown, 80);
            portraitMouse(NSEvent.EventType.leftMouseDragged, 0);
            portraitMouse(NSEvent.EventType.leftMouseUp, 0);
            mainMenu.animate(robot, r2d2: r2d2, bb8: bb8, wallE: wallE, dt: 1.0 / 60);
            menuSmokePassed = menuSmokePassed && abs(robot.root.eulerAngles.y - initialYaw) < 0.001;
            await UISmoke.WriteWindow(tree, directory, "main-menu-window.png");
            var shortcutsPassed = new Dictionary<string, bool>();
            foreach (var (shortcut, model) in new[] { ("b", bb8.root), ("r", r2d2.root), ("w", wallE.root), ("m", robot.root) })
            {
                var @event = NSEvent.keyEvent(NSEvent.EventType.keyDown, NSPoint.zero, 0, 0,
                    window.windowNumber, null, shortcut, shortcut, false, 0);
                mainMenu.keyDown(@event);
                mainMenu.animate(robot, r2d2: r2d2, bb8: bb8, wallE: wallE, dt: 1.0 / 60);
                var models = new[] { robot.root, r2d2.root, bb8.root, wallE.root };
                bool passed = model.parent == mainMenu.stage.rootNode
                    && models.Count(m => m.parent == mainMenu.stage.rootNode) == 1;
                portraitMouse(NSEvent.EventType.leftMouseDown, 0);
                portraitMouse(NSEvent.EventType.leftMouseDragged, 40);
                portraitMouse(NSEvent.EventType.leftMouseUp, 40);
                mainMenu.animate(robot, r2d2: r2d2, bb8: bb8, wallE: wallE, dt: 1.0 / 60);
                passed = passed && abs(model.eulerAngles.y - initialYaw - 0.4) < 0.001;
                shortcutsPassed[shortcut] = passed;
                menuSmokePassed = menuSmokePassed && passed;
                UISmoke.Write(mainMenu.portrait.snapshot(), directory, $"menu-{shortcut}.png");
                portraitMouse(NSEvent.EventType.leftMouseDown, 40);
                portraitMouse(NSEvent.EventType.leftMouseDragged, 0);
                portraitMouse(NSEvent.EventType.leftMouseUp, 0);
                mainMenu.animate(robot, r2d2: r2d2, bb8: bb8, wallE: wallE, dt: 0);
            }
            // Real Godot input through the GUI (not in the Mac test, which calls the handlers directly): hovering a
            // button selects it (tracking area), clicking activates it, dragging the portrait rotates the model and
            // the hidden character keys reach the menu as first responder.
            var godotInput = new Dictionary<string, bool>();
            {
                mainMenu.settings = false; mainMenu.selection = 0; mainMenu.refresh();
                mainMenu.layoutSubtreeIfNeeded();
                await frame(tree);
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
                await frame(tree);
                godotInput["hoverSelects"] = mainMenu.selection == 2;
                Button(settingsAt, true); Button(settingsAt, false);
                await frame(tree);
                godotInput["clickActivates"] = mainMenu.settings;
                mainMenu.layoutSubtreeIfNeeded(); await frame(tree);
                var backAt = menuButtons[5].GetGlobalRect().GetCenter();
                Move(backAt, Godot.Vector2.Zero, 0); Button(backAt, true); Button(backAt, false);
                await frame(tree);
                godotInput["clickBack"] = !mainMenu.settings && mainMenu.selection == 2;
                var yawBefore = robot.root.eulerAngles.y;
                var portraitAt = mainMenu.portrait.GetGlobalRect().GetCenter();
                Move(portraitAt, Godot.Vector2.Zero, 0); Button(portraitAt, true);
                Move(portraitAt + new Godot.Vector2(30, 0), new Godot.Vector2(30, 0), MouseButtonMask.Left);
                Move(portraitAt + new Godot.Vector2(50, 0), new Godot.Vector2(20, 0), MouseButtonMask.Left);
                Button(portraitAt + new Godot.Vector2(50, 0), false);
                await frame(tree);
                mainMenu.animate(robot, r2d2: r2d2, bb8: bb8, wallE: wallE, dt: 0);
                godotInput["portraitDrag"] = abs(robot.root.eulerAngles.y - yawBefore - 0.5) < 0.001;
                godotInput["portraitFocusesMenu"] = window.firstResponder == mainMenu;
                Push(new InputEventKey { PhysicalKeycode = Key.R, Keycode = Key.R, KeyLabel = Key.R, Unicode = 'r', Pressed = true });
                Push(new InputEventKey { PhysicalKeycode = Key.R, Keycode = Key.R, KeyLabel = Key.R, Unicode = 'r', Pressed = false });
                await frame(tree);
                godotInput["characterKey"] = mainMenu.character == RacePerformance.Character.r2d2;
                Push(new InputEventKey { PhysicalKeycode = Key.M, Keycode = Key.M, KeyLabel = Key.M, Unicode = 'm', Pressed = true });
                await frame(tree);
                mainMenu.animate(robot, r2d2: r2d2, bb8: bb8, wallE: wallE, dt: 0);
                // Restore the model's yaw for the remaining checks.
                Move(portraitAt, Godot.Vector2.Zero, 0); Button(portraitAt, true);
                Move(portraitAt - new Godot.Vector2(50, 0), new Godot.Vector2(-50, 0), MouseButtonMask.Left);
                Button(portraitAt - new Godot.Vector2(50, 0), false);
                Move(new Godot.Vector2(2, 2), Godot.Vector2.Zero, 0);
                await frame(tree);
                mainMenu.animate(robot, r2d2: r2d2, bb8: bb8, wallE: wallE, dt: 0);
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
            try { assistPassed = checkDrivingAssistSettings(at: directory); }
            catch (Exception e) { GD.PushError(e.ToString()); assistPassed = false; }
            menuSmokePassed = assistPassed && menuSmokePassed;
            mainMenu.selection = 5; mainMenu.activate();
            menuSmokePassed = menuSmokePassed && !mainMenu.settings;
            mainMenu.selection = 0; mainMenu.activate();
            menuSmokePassed = menuSmokePassed && inSandbox && mainMenu.isHidden && !hud.isHidden;
            var report = new Dictionary<string, object>
            {
                ["menuPassed"] = menuSmokePassed, ["dragPassed"] = dragPassed, ["firstResponderPassed"] = responderPassed,
                ["keyboardPassed"] = keyboardPassed, ["drivingAssistPassed"] = assistPassed, ["shortcutsPassed"] = shortcutsPassed,
                ["godotInput"] = godotInput,
            };
            System.IO.File.WriteAllText(URL.fileURLWithPath(directory).appendingPathComponent("menu-smoke.json").path, JSONSerialization.prettyPrintedSortedKeys(report));
        }
        finally
        {
            for (int i = 0; i < settingKeys.Length; i++)
            {
                if (savedSettings[i] != null) UserDefaults.standard.set(savedSettings[i], settingKeys[i]);
                else UserDefaults.standard.removeObject(settingKeys[i]);
            }
        }
    }

    /// <summary>
    /// DrivingAssistSmoke.swift checkDrivingAssistSettings(at:).
    /// PORT: on the Mac, layoutSubtreeIfNeeded re-applies the overlay's Auto Layout constraints, so the 900x550
    /// frame reverts to the 1280x792 content size before the check and the capture. The facade re-solves
    /// constraints only when the content layout guide changes (NSLayoutConstraint.cs), so the frame stays:
    /// the check and driving-assist-settings-900x550.png use the intended 900x550 frame, and
    /// driving-assist-settings.png is captured at the content size like the Mac's.
    /// </summary>
    public bool checkDrivingAssistSettings(string at)
    {
        var directory = at;
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
                UISmoke.Write(small, directory, "driving-assist-settings-900x550.png");
            }
            else passed = false;
            mainMenu.frame = frame;
            mainMenu.layoutSubtreeIfNeeded();
            if (mainMenu.bitmapImageRepForCachingDisplay(mainMenu.bounds) is NSBitmapImageRep bitmap)
            {
                mainMenu.cacheDisplay(mainMenu.bounds, bitmap);
                UISmoke.Write(bitmap, directory, "driving-assist-settings.png");
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
}
