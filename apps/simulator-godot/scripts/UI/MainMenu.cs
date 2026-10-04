using System;
using System.Collections.Generic;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;
using Character = Marvin.Core.RacePerformance.Character;

namespace Marvin;

// PORT: Swift `private final class` (file scope); Godot classes must be partial, so it is internal.
internal sealed partial class PortraitView : SCNView
{
    public Action<CGFloat> onRotate;
    private CGFloat? dragX;
    public override bool acceptsFirstResponder => false;
    public override void resetCursorRects() { addCursorRect(bounds, NSCursor.openHand); }
    public override void mouseDown(NSEvent @event)
    {
        window?.makeFirstResponder(superview);
        dragX = @event.locationInWindow.x;
    }
    public override void mouseDragged(NSEvent @event)
    {
        if (dragX is not CGFloat previousX) return;
        var x = @event.locationInWindow.x;
        onRotate?.Invoke(x - previousX);
        dragX = x;
    }
    public override void mouseUp(NSEvent @event) { dragX = null; }
}

// PORT: Swift `private final class` (file scope); Godot classes must be partial, so it is internal.
internal sealed partial class MenuButton : NSButton
{
    public Action onHover;
    public MenuButton() : this("", null, null) { }
    public MenuButton(string title, object target, Action<NSButton> action) : base(title, target, action) { }
    public override bool acceptsFirstResponder => false;
    public override void updateTrackingAreas()
    {
        base.updateTrackingAreas();
        foreach (var area in trackingAreas) removeTrackingArea(area);
        addTrackingArea(new NSTrackingArea(NSRect.zero, NSTrackingArea.Options.inVisibleRect | NSTrackingArea.Options.mouseEnteredAndExited | NSTrackingArea.Options.activeInKeyWindow, this, null));
    }
    public override void mouseEntered(NSEvent @event) { onHover?.Invoke(); }
}

/// A native AppKit menu alongside a separate, live SceneKit portrait.
public partial class MainMenuView : NSView
{
    public Character character { get; private set; } = Character.marvin;
    public readonly SCNView portrait = new PortraitView(); public readonly SCNScene stage = new SCNScene(); public readonly SCNNode camera = new SCNNode();
    public readonly NSTextField title = NSTextField.labelWithString("Marvin"), subtitle = NSTextField.labelWithString("Beep, boop... just some fun.");
    public readonly NSTextField hint = NSTextField.labelWithString("↑ ↓ to choose   ·   Return to select");
    private readonly List<MenuButton> buttons = new List<MenuButton>();
    public Action onSandbox;
    public Action onDirtTrack;
    private int optionCount => settings ? 6 : 4;
    public bool settings = false;
    public int selection = 0;
    public bool idleAnimation
    {
        get => !UserDefaults.standard.@bool("reduceMenuMotion");
        set => UserDefaults.standard.set(!value, "reduceMenuMotion");
    }
    public bool showGuide
    {
        get => !UserDefaults.standard.@bool("hideKeyboardGuide");
        set => UserDefaults.standard.set(!value, "hideKeyboardGuide");
    }
    public bool raceRobotCollisions
    {
        get => !UserDefaults.standard.@bool("disableRaceRobotCollisions");
        set => UserDefaults.standard.set(!value, "disableRaceRobotCollisions");
    }
    public bool steeringAssist
    {
        get => !UserDefaults.standard.@bool("disableRaceSteeringAssist");
        set => UserDefaults.standard.set(!value, "disableRaceSteeringAssist");
    }
    public bool brakingAssist
    {
        get => !UserDefaults.standard.@bool("disableRaceBrakingAssist");
        set => UserDefaults.standard.set(!value, "disableRaceBrakingAssist");
    }
    public DirtDrivingAssists raceAssists => new DirtDrivingAssists(steering: steeringAssist, braking: brakingAssist);
    private double clock = 0.0, nextLook = 1.8, nextBlink = 2.7, blinkStart = -10.0;
    private double yaw = -0.30, pitch = 0.0, targetYaw = -0.30, targetPitch = 0.0;
    private CGFloat modelYaw = 0.35;
    public override bool acceptsFirstResponder => true;
    // PORT: color(_:alpha:) is a free function in Robot.swift; this private copy keeps the file independent of that port.
    private static NSColor color(uint hex, CGFloat alpha = 1) =>
        NSColor.srgbRed((CGFloat)((hex >> 16) & 255) / 255, (CGFloat)((hex >> 8) & 255) / 255, (CGFloat)(hex & 255) / 255, alpha);

    public MainMenuView() : this(NSRect.zero) { }
    public MainMenuView(NSRect frame) : base(frame)
    {
        wantsLayer = true; layer.backgroundColor = color(0xf2f1e9).cgColor;
        portrait.scene = stage; portrait.pointOfView = camera;
        portrait.antialiasingMode = SCNAntialiasingMode.multisampling4X;
        portrait.rendersContinuously = true; portrait.preferredFramesPerSecond = 60;
        portrait.toolTip = "Drag left or right to rotate.";
        if (portrait is PortraitView portraitView) portraitView.onRotate = delta =>
        {
            if (!IsInstanceValid(this)) return;
            modelYaw = (modelYaw + delta * 0.01) % (2 * Math.PI);
        };
        stage.background.contents = color(0xf2f1e9);
        camera.camera = new SCNCamera(); camera.camera.fieldOfView = 36;
        camera.position = new SCNVector3(0, 1.05, 2.25);
        camera.look(at: new SCNVector3(0, 0.40, 0), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
        stage.rootNode.addChildNode(camera);
        var ambient = new SCNNode(); ambient.light = new SCNLight(); ambient.light.type = SCNLight.LightType.ambient;
        ambient.light.intensity = 650; stage.rootNode.addChildNode(ambient);
        var key = new SCNNode(); key.light = new SCNLight(); key.light.type = SCNLight.LightType.directional;
        key.light.intensity = 1100; key.eulerAngles = new SCNVector3(-1.15, -0.6, 0);
        // Deferred shadows also shade the constant-color backdrop, preserving
        // its seamless match to the native menu while grounding the model.
        key.light.castsShadow = true; key.light.shadowMode = SCNShadowMode.deferred;
        key.light.shadowMapSize = new CGSize(2048, 2048);
        key.light.orthographicScale = 2;
        key.light.shadowSampleCount = 32; key.light.shadowRadius = 32;
        key.light.shadowBias = 0.001;
        key.light.shadowColor = NSColor.black.withAlphaComponent(0.18);
        stage.rootNode.addChildNode(key);
        var floor = new SCNFloor(); floor.reflectivity = 0;
        var surface = new SCNMaterial(); surface.diffuse.contents = color(0xf2f1e9); surface.lightingModel = SCNMaterial.LightingModel.constant;
        floor.materials = new() { surface }; stage.rootNode.addChildNode(new SCNNode(floor));
        addSubview(portrait);
        title.font = NSFont.systemFont(58, NSFont.Weight.bold);
        subtitle.font = NSFont.systemFont(18, NSFont.Weight.regular);
        hint.font = NSFont.systemFont(12);
        foreach (var label in new[] { title, subtitle, hint }) { label.textColor = color(0x304e44); addSubview(label); }
        foreach (var index in Enumerable.Range(0, 6))
        {
            var button = new MenuButton("", this, activateButton);
            button.tag = index; button.isBordered = false; button.wantsLayer = true;
            button.layer.cornerRadius = 14;
            button.onHover = () => { if (!IsInstanceValid(this)) return; selection = index; refresh(); };
            addSubview(button); buttons.Add(button);
        }
        refresh();
    }
    public override void layout()
    {
        base.layout();
        CGFloat split = bounds.width * 0.55, width = min(360, bounds.width - split - 50);
        portrait.frame = new NSRect(0, 0, split, bounds.height);
        var top = bounds.height / 2 + (settings ? 240 : 205);
        title.frame = new NSRect(split, top - 72, width, 76);
        subtitle.frame = new NSRect(split + 3, top - 108, width, 30);
        for (int i = 0; i < buttons.Count; i++)
        {
            var button = buttons[i];
            button.frame = new NSRect(split, top - (198) - (CGFloat)i * (settings ? 54 : 72), width, settings ? 46 : 58);
        }
        hint.frame = new NSRect(split + 3, max(4, top - (settings ? 504 : 458)), width, 24);
    }
    public void refresh()
    {
        title.stringValue = settings ? "Settings" : "Marvin";
        subtitle.stringValue = settings ? "Race assists still need your input." : "Beep, boop... just some fun.";
        var names = settings ? new[] { $"Idle animation: {(idleAnimation ? "On" : "Off")}", $"Keyboard guide: {(showGuide ? "On" : "Off")}", $"Robot collisions: {(raceRobotCollisions ? "On" : "Off")}", $"Steering assist: {(steeringAssist ? "On" : "Off")}", $"Braking assist: {(brakingAssist ? "On" : "Off")}", "Back" } : new[] { "Sandbox", "Dirt Track", "Settings", "Quit" };
        for (int i = 0; i < buttons.Count; i++)
        {
            var button = buttons[i];
            button.isHidden = i >= names.Length;
            if (!(i < names.Length)) continue;
            button.title = names[i];
            var tips = new Dictionary<int, string>
            {
                [2] = "Dirt Track only. Turn off to let racers pass through one another.",
                [3] = "Dirt Track only. Gently smooths steering corrections while preserving your chosen direction. You remain in control.",
                [4] = "Dirt Track only. Slows before tight corners and preserves turning grip. Space always applies full braking.",
            };
            button.toolTip = settings ? tips.GetValueOrDefault(i) : null;
            button.setAccessibilityHelp(button.toolTip);
            button.attributedTitle = new NSAttributedString(names[i], new() { [NSAttributedString.Key.font] = NSFont.systemFont(21, NSFont.Weight.medium), [NSAttributedString.Key.foregroundColor] = i == selection ? NSColor.white : color(0x304e44) });
            button.layer.backgroundColor = (i == selection ? color(0x304e44) : color(0xe5e9df)).cgColor;
            button.setAccessibilityLabel(names[i]);
        }
    }
    private void activateButton(NSButton sender) { selection = sender.tag; activate(); }
    public void activate()
    {
        if (settings)
        {
            switch (selection)
            {
                case 0: idleAnimation = !idleAnimation; break;
                case 1: showGuide = !showGuide; break;
                case 2: raceRobotCollisions = !raceRobotCollisions; break;
                case 3: steeringAssist = !steeringAssist; break;
                case 4: brakingAssist = !brakingAssist; break;
                default: settings = false; selection = 2; break;
            }
        }
        else
        {
            switch (selection)
            {
                case 0: onSandbox?.Invoke(); break;
                case 1: onDirtTrack?.Invoke(); break;
                case 2: settings = true; selection = 0; break;
                default: NSApp.terminate(null); break;
            }
        }
        needsLayout = true; refresh(); if (!isHidden) { window?.makeFirstResponder(this); }
    }
    public override void keyDown(NSEvent @event)
    {
        if (@event.modifierFlags.contains(NSEvent.ModifierFlags.command)) { base.keyDown(@event); return; }
        if (!settings && @event.modifierFlags.intersection(NSEvent.ModifierFlags.control | NSEvent.ModifierFlags.option).isEmpty() &&
            new Dictionary<string, Character> { ["m"] = Character.marvin, ["b"] = Character.bb8, ["r"] = Character.r2d2, ["w"] = Character.wallE }
                .TryGetValue(@event.charactersIgnoringModifiers?.ToLowerInvariant() ?? "", out var chosen))
        {
            character = chosen;
            return;
        }
        switch (@event.keyCode)
        {
            case 125: case 124: selection = (selection + 1) % optionCount; refresh(); break;
            case 126: case 123: selection = (selection + optionCount - 1) % optionCount; refresh(); break;
            case 36: case 76: case 49: if (!@event.isARepeat) { activate(); } break;
            case 53: settings = false; selection = 0; needsLayout = true; refresh(); break;
            default: break;
        }
    }
    public override void mouseDown(NSEvent @event) { window?.makeFirstResponder(this); }
    public void animate(Robot robot, R2D2 r2d2, ImportedRacer bb8, ImportedRacer wallE, double dt)
    {
        clock += dt;
        if (clock >= nextLook)
        {
            targetYaw = SwiftRandom.doubleClosed(-0.65, 0.25);
            targetPitch = SwiftRandom.doubleClosed(-0.07, 0.12);
            nextLook = clock + SwiftRandom.doubleClosed(2.0, 5.5);
        }
        if (clock >= nextBlink) { blinkStart = clock; nextBlink = clock + SwiftRandom.doubleClosed(2.5, 6.0); }
        var blend = 1 - exp(-dt * 3);
        yaw += (targetYaw - yaw) * blend; pitch += (targetPitch - pitch) * blend;
        var models = new Dictionary<Character, SCNNode> { [Character.marvin] = robot.root, [Character.bb8] = bb8.root, [Character.r2d2] = r2d2.root, [Character.wallE] = wallE.root };
        var selected = models[character];
        foreach (var node in models.Values)
        {
            if (node != selected && node.parent == stage.rootNode) { node.removeFromParentNode(); }
        }
        if (selected.parent != stage.rootNode) { stage.rootNode.addChildNode(selected); }
        // WALL-E's tracks and hands need more room when viewed from the side.
        camera.position = new SCNVector3(0, 1.05, character == Character.wallE ? 2.85 : 2.25);
        camera.look(at: new SCNVector3(0, 0.40, 0), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
        var pose = new RacePerformance.Pose();
        pose.yaw = idleAnimation ? yaw : -0.30;
        pose.pitch = idleAnimation ? pitch : 0;
        switch (character)
        {
            case Character.marvin: break;
            case Character.r2d2: r2d2.update(new Simulation()); r2d2.applyExpression(pose); break;
            case Character.bb8: bb8.update(new Simulation()); bb8.applyExpression(pose, heading: 0); break;
            case Character.wallE: wallE.update(new Simulation()); wallE.applyExpression(pose, heading: 0); break;
        }
        selected.position = SCNVector3Zero; selected.eulerAngles = new SCNVector3(0, modelYaw, 0);
        robot.yawNode.eulerAngles.y = (CGFloat)(idleAnimation ? yaw : -0.30);
        robot.pitchNode.eulerAngles.x = (CGFloat)(idleAnimation ? -pitch : 0);
        var blink = max(0, 1 - abs((clock - blinkStart) / 0.11 - 1));
        foreach (var eye in robot.eyes) { eye.scale.y = idleAnimation ? (CGFloat)(1 - blink * 0.92) : 1; }
    }
}
