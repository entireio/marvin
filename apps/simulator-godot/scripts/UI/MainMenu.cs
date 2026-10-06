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
    /// PORT: Godot-only, called after the Shadow quality row changed the stored choice (ShadowQualitySetting).
    public Action onShadowQuality;
    /// PORT: Godot-only, called after the Graphics detail row changed the stored choice (GraphicsDetailSetting).
    public Action onGraphicsDetail;
    // PORT: eight settings rows on Godot (the Shadow quality and Graphics detail rows before Back); macOS has six.
    private int optionCount => settings ? 8 : 4;
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
    /// PORT: Godot-only setting, the macOS Settings screen has no such row: Exact (soft shadows as in SceneKit) or Fast
    /// (hard shadows, the default), stored with the other settings (ShadowQualitySetting).
    public bool exactShadows
    {
        get => ShadowQualitySetting.exact;
        set => ShadowQualitySetting.exact = value;
    }
    /// PORT: Godot-only setting, the macOS Settings screen has no such row: Max (the default, full resolution), High,
    /// Medium or Low, stored with the other settings (GraphicsDetailSetting).
    public GraphicsDetailSetting.Level graphicsDetail
    {
        get => GraphicsDetailSetting.stored;
        set => GraphicsDetailSetting.stored = value;
    }
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
        foreach (var index in Enumerable.Range(0, 8))
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
        // PORT: the seventh and eighth settings rows (Shadow quality, Graphics detail) go below the Mac's six at the same
        // spacing, the hint below them. Where that would reach the bottom edge (the 900 x 550 QA frame, the 900 x 612
        // content of the smallest window), the gap under the subtitle first narrows to the main menu's, then the rows move
        // closer together.
        CGFloat first = 198, spacing = settings ? 54 : 72, height = settings ? 46 : 58;
        int last = optionCount - 1;
        if (settings)
        {
            var excess = 40 - (top - first - last * spacing);
            if (excess > 0)
            {
                first -= min(12.0, excess);
                spacing = min(spacing, max(30.0, Math.Floor((top - first - 40) / last)));
                height = spacing - 8;
            }
        }
        for (int i = 0; i < buttons.Count; i++)
        {
            var button = buttons[i];
            button.frame = new NSRect(split, top - first - (CGFloat)i * spacing, width, height);
        }
        hint.frame = new NSRect(split + 3, max(4, settings ? top - first - last * spacing - 36 : top - 458), width, 24);
    }
    public void refresh()
    {
        title.stringValue = settings ? "Settings" : "Marvin";
        subtitle.stringValue = settings ? "Race assists still need your input." : "Beep, boop... just some fun.";
        var names = settings ? new[] { $"Idle animation: {(idleAnimation ? "On" : "Off")}", $"Keyboard guide: {(showGuide ? "On" : "Off")}", $"Robot collisions: {(raceRobotCollisions ? "On" : "Off")}", $"Steering assist: {(steeringAssist ? "On" : "Off")}", $"Braking assist: {(brakingAssist ? "On" : "Off")}", $"Shadow quality: {(exactShadows ? "Exact" : "Fast")}", $"Graphics detail: {graphicsDetail}", "Back" } : new[] { "Sandbox", "Dirt Track", "Settings", "Quit" };
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
                [5] = "Exact draws soft shadows, as the macOS game does. Fast draws hard shadows for a higher frame rate. Applies immediately.",
                [6] = "Max draws everything as the macOS game does. High leaves out the ambient occlusion and simplifies small details; Medium and Low also draw the 3D view at a lower resolution, upscaled. Lower levels give a higher frame rate. Applies immediately.",
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
                case 5: exactShadows = !exactShadows; onShadowQuality?.Invoke(); break;
                case 6: graphicsDetail = GraphicsDetailSetting.Next(graphicsDetail); onGraphicsDetail?.Invoke(); break;
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
