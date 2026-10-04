using System;
using System.Collections.Generic;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

public partial class SimulatorView : SCNView
{
    public HashSet<ushort> held = new HashSet<ushort>();
    public Action<ushort> onCommand;
    public Action<CGFloat, CGFloat> onOrbit;
    public Action<CGFloat> onZoom;
    public Action onFocusLost;
    public override bool acceptsFirstResponder => true;
    public override void keyDown(NSEvent @event)
    {
        if (@event.modifierFlags.contains(NSEvent.ModifierFlags.command)) { base.keyDown(@event); return; }
        var movement = new HashSet<ushort> { 0, 1, 2, 13, 123, 124, 125, 126, 12, 14, 15, 3, 49 };
        if (movement.Contains(@event.keyCode)) { held.Add(@event.keyCode); }
        else if (!@event.isARepeat) { onCommand?.Invoke(@event.keyCode); }
    }
    public override void keyUp(NSEvent @event) { held.Remove(@event.keyCode); }
    public override void flagsChanged(NSEvent @event)
    {
        if (@event.modifierFlags.contains(NSEvent.ModifierFlags.shift)) { held.Add(56); } else { held.Remove(56); }
    }
    public override bool resignFirstResponder()
    {
        clearInput(); return base.resignFirstResponder();
    }
    public void clearInput() { held.Clear(); onFocusLost?.Invoke(); }
    public override void mouseDown(NSEvent @event) { window?.makeFirstResponder(this); }
    public override void mouseDragged(NSEvent @event) { onOrbit?.Invoke(@event.deltaX, @event.deltaY); }
    public override void rightMouseDragged(NSEvent @event) { onOrbit?.Invoke(@event.deltaX, @event.deltaY); }
    public override void scrollWheel(NSEvent @event) { onZoom?.Invoke(@event.scrollingDeltaY); }
    public DriveInput driveInput
    {
        get
        {
            double down(params ushort[] codes) => codes.Any(code => held.Contains(code)) ? 1 : 0;
            var input = new DriveInput();
            input.throttle = down(13, 126) - down(1, 125);
            input.turn = down(2, 124) - down(0, 123);
            input.headYaw = down(14) - down(12);
            input.headPitch = down(15) - down(3);
            input.boost = held.Contains(56); input.brake = held.Contains(49);
            return input;
        }
    }
}

public partial class HUDView : NSView
{
    public Simulation state = new Simulation();
    public string cameraName = "FOLLOW";
    public int fps = 60;
    public bool helpVisible = true;
    public override bool isFlipped => true;
    public override NSView hitTest(NSPoint point) => null;
    // PORT: color(_:alpha:) is a free function in Robot.swift; this private copy keeps the file independent of that port.
    private static NSColor color(uint hex, CGFloat alpha = 1) =>
        NSColor.srgbRed((CGFloat)((hex >> 16) & 255) / 255, (CGFloat)((hex >> 8) & 255) / 255, (CGFloat)(hex & 255) / 255, alpha);
    private void text(string value, CGFloat x, CGFloat y, CGFloat size = 12,
                      uint ink = 0x304e44, NSFont.Weight weight = NSFont.Weight.regular, bool mono = false)
    {
        var font = mono ? NSFont.monospacedSystemFont(size, weight) : NSFont.systemFont(size, weight);
        value.draw(new NSPoint(x, y), new() { [NSAttributedString.Key.font] = font, [NSAttributedString.Key.foregroundColor] = color(ink) });
    }
    private void card(NSRect rect)
    {
        color(0xf5f6ef, alpha: 0.94).setFill();
        var path = NSBezierPath.roundedRect(rect, 14, 14); path.fill();
        color(0x95ada0, alpha: 0.35).setStroke(); path.lineWidth = 1; path.stroke();
    }
    public override void draw(NSRect dirtyRect)
    {
        CGFloat w = bounds.width, h = bounds.height;
        card(new NSRect(22, 22, 280, 96));
        text("M A R V I N   /   S I M U L A T O R", 40, 38, size: 10, mono: true);
        text("A little room to explore.", 40, 58, size: 21, weight: NSFont.Weight.semibold);
        var status = state.paused ? "PAUSED" : state.contacting ? "OBSTACLE · turn or reverse" : "READY TO ROAM";
        text(status, 40, 91, size: 10, ink: state.contacting ? 0xa56932u : 0x337e69u, mono: true);
        var px = w - 252;
        card(new NSRect(px, 22, 230, 342));
        text("EXPLORATION COURSE", px + 18, 39, size: 10, mono: true);
        text(state.complete ? "Course complete!" : $"Find beacon {state.checkpoint + 1} of 5", px + 18, 60, size: 18, weight: NSFont.Weight.semibold);
        text(state.complete ? "Keep exploring, or reset to go again." : "Drive through the amber ring.", px + 18, 86, size: 11);
        var map = new NSRect(px + 18, 115, 194, 162);
        color(0xe1e8dd).setFill(); NSBezierPath.roundedRect(map, 6, 6).fill();
        NSPoint point(double x, double z) =>
            new NSPoint(map.minX + (CGFloat)((x + 6) / 12) * map.width,
                        map.maxY - (CGFloat)((z + 5) / 10) * map.height);
        foreach (var o in Simulation.obstacles)
        {
            var p = point(o.x - o.width / 2, o.z + o.depth / 2);
            color(0x9aafa2).setFill();
            NSBezierPath.roundedRect(new NSRect(p.x, p.y, o.width / 12 * map.width,
                                                o.depth / 10 * map.height), 2, 2).fill();
        }
        for (int i = 0; i < state.checkpoints.Length; i++)
        {
            var p = state.checkpoints[i];
            var at = point(p.x, p.z);
            color(i < state.checkpoint ? 0x2b8e7fu : i == state.checkpoint ? 0xc88547u : 0xa3b8aeu).setFill();
            NSBezierPath.ovalIn(new NSRect(at.x - 4, at.y - 4, 8, 8)).fill();
        }
        {
            var p = point(state.x, state.z); var angle = state.heading;
            var marker = new NSBezierPath();
            marker.move(new NSPoint(p.x + sin(angle) * 8, p.y - cos(angle) * 8));
            marker.line(new NSPoint(p.x + sin(angle + 2.4) * 6, p.y - cos(angle + 2.4) * 6));
            marker.line(new NSPoint(p.x + sin(angle - 2.4) * 6, p.y - cos(angle - 2.4) * 6));
            marker.close(); color(0x214d40).setFill(); marker.fill();
        }
        text(Swift.format("SPEED   %3.0f mm/s", (state.contacting ? 0 : abs(state.speed)) * 100), px + 18, 292, size: 11, mono: true);
        text(Swift.format("TRAVEL  %.2f m", state.distance / 10), px + 18, 313, size: 11, mono: true);
        text($"{cameraName} CAMERA", px + 18, 336, size: 9, ink: 0x6d8678, mono: true);
        if (helpVisible)
        {
            var columns = new[]
            {
                new[] { ("DRIVE", "W A S D / ↑ ↓ ← →"), ("HEAD", "Q / E · R / F"), ("PAUSE", "P / Esc") },
                new[] { ("BRAKE", "Space"), ("CENTER", "H"), ("RESET", "⌘R") },
                new[] { ("BOOST", "Shift"), ("CAMERA", "C · drag / scroll"), ("HELP", "?") },
            };
            var font = NSFont.monospacedSystemFont(11, NSFont.Weight.regular);
            var keyFont = NSFont.monospacedSystemFont(11, NSFont.Weight.medium);
            NSSize measure(string value, NSFont font) =>
                value.size(new() { [NSAttributedString.Key.font] = font });
            CGFloat padding = 18, labelGap = 12, columnGap = 28, rowGap = 8;
            var labelWidths = columns.Select(column =>
                ceil(column.Select(entry => measure(entry.Item1, font).width).DefaultIfEmpty(0).Max())).ToArray();
            var keyWidths = columns.Select(column =>
                ceil(column.Select(entry => measure(entry.Item2, keyFont).width).DefaultIfEmpty(0).Max())).ToArray();
            var rowHeight = ceil(columns.SelectMany(column => column).Select(entry =>
                max(measure(entry.Item1, font).height, measure(entry.Item2, keyFont).height)).DefaultIfEmpty(0).Max());
            var width = labelWidths.Sum() + keyWidths.Sum()
                + 3 * labelGap + 2 * columnGap + 2 * padding;
            var height = 3 * rowHeight + 2 * rowGap + 2 * padding;
            var panel = new NSRect(22, h - 22 - height, width, height);
            card(panel);
            var x = panel.minX + padding;
            for (int columnIndex = 0; columnIndex < columns.Length; columnIndex++)
            {
                var column = columns[columnIndex];
                for (int rowIndex = 0; rowIndex < column.Length; rowIndex++)
                {
                    var entry = column[rowIndex];
                    var y = panel.minY + padding + (CGFloat)rowIndex * (rowHeight + rowGap);
                    text(entry.Item1, x, y, size: 11, ink: 0x6d8678, mono: true);
                    text(entry.Item2, x + labelWidths[columnIndex] + labelGap, y,
                         size: 11, weight: NSFont.Weight.medium, mono: true);
                }
                x += labelWidths[columnIndex] + labelGap + keyWidths[columnIndex] + columnGap;
            }
        }
        if (state.paused)
        {
            card(new NSRect(w / 2 - 135, h / 2 - 47, 270, 94));
            text("Taking a little break.", w / 2 - 110, h / 2 - 24, size: 21, weight: NSFont.Weight.semibold);
            text("Press P or click Resume to continue", w / 2 - 110, h / 2 + 9, size: 12);
        }
    }
}
