using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace Marvin.SceneKit;

/// <summary>
/// `tools/godot -- --text-calibration DIR`: the text matrix of tools/scenekit-reference/TextCalibration.swift drawn
/// through the facade's AppKit views, one PNG per font (rows top to bottom, each ceil(1.5 x size) + 12 px high):
/// raw black and white (font smoothing off), plain NSString.draw(at:) over the view's own fill in sRGB greys
/// 0, 0.25, 0.5, 0.75, 1 (black background for greys from 0.5, white below), the same greys as NSTextField labels
/// over a layer-backed parent, then (Godot only) black plain text at fixed stem gains 0 ... 1.5 px for the fit.
/// `python3 tools/scenekit-reference/text_calibration.py MACDIR GODOTDIR` compares the ink and fits FontSmoothing.
/// </summary>
internal static partial class TextCalibration
{
    internal readonly record struct Spec(string name, bool mono, double size, NSFont.Weight weight);
    internal static readonly Spec[] Specs =
    {
        new("sys80b", false, 80, NSFont.Weight.bold), new("sys58b", false, 58, NSFont.Weight.bold),
        new("sys44b", false, 44, NSFont.Weight.bold), new("sys34b", false, 34, NSFont.Weight.bold),
        new("sys30b", false, 30, NSFont.Weight.bold), new("sys26s", false, 26, NSFont.Weight.semibold),
        new("sys21m", false, 21, NSFont.Weight.medium), new("sys18r", false, 18, NSFont.Weight.regular),
        new("sys17r", false, 17, NSFont.Weight.regular), new("sys16b", false, 16, NSFont.Weight.bold),
        new("sys16m", false, 16, NSFont.Weight.medium), new("sys13m", false, 13, NSFont.Weight.medium),
        new("sys13r", false, 13, NSFont.Weight.regular), new("sys12s", false, 12, NSFont.Weight.semibold),
        new("sys12m", false, 12, NSFont.Weight.medium), new("sys12r", false, 12, NSFont.Weight.regular),
        new("sys11b", false, 11, NSFont.Weight.bold), new("sys11r", false, 11, NSFont.Weight.regular),
        new("sys10b", false, 10, NSFont.Weight.bold),
        new("mono23b", true, 23, NSFont.Weight.bold), new("mono18b", true, 18, NSFont.Weight.bold),
        new("mono17b", true, 17, NSFont.Weight.bold), new("mono16r", true, 16, NSFont.Weight.regular),
        new("mono14r", true, 14, NSFont.Weight.regular), new("mono13r", true, 13, NSFont.Weight.regular),
        new("mono12r", true, 12, NSFont.Weight.regular), new("mono12b", true, 12, NSFont.Weight.bold),
        new("mono9b", true, 9, NSFont.Weight.bold),
    };
    internal const string Text = "Race 1:29.18";
    internal static readonly double[] Greys = { 0, 0.25, 0.5, 0.75, 1 };
    internal static readonly double[] Gains = { 0, 0.25, 0.5, 0.75, 1.0, 1.5 };

    private sealed partial class TextView : NSView
    {
        public NSFont font; public NSColor fg, bg; public bool smooth = true; public double? gain;
        public TextView(CGRect frame) : base(frame) { }
        public override bool isFlipped => true;
        public override void draw(CGRect dirtyRect)
        {
            var context = NSGraphicsContext.current;
            context.shouldSmoothFonts = smooth; context.stemGainOverride = gain;
            bg.setFill(); bounds.fill();
            Text.draw(new CGPoint(8, 6), new Dictionary<NSAttributedString.Key, object> { [NSAttributedString.Key.font] = font, [NSAttributedString.Key.foregroundColor] = fg });
            context.shouldSmoothFonts = true; context.stemGainOverride = null;
        }
    }

    private static NSColor Grey(double v) => NSColor.srgbRed(v, v, v, 1);

    /// <summary>Each string drawn with draw(at:) at x = 20 + 0.05 k (k = 0...19), one 70 px row each.</summary>
    private sealed partial class PositionView : NSView
    {
        public NSFont font; public string text; public bool smooth = true;
        public PositionView(CGRect frame) : base(frame) { }
        public override bool isFlipped => true;
        public override void draw(CGRect dirtyRect)
        {
            NSGraphicsContext.current.shouldSmoothFonts = smooth;
            NSColor.black.setFill(); bounds.fill();
            for (int k = 0; k < 20; k++)
                text.draw(new CGPoint(20 + 0.05 * k, 10 + k * 70), new Dictionary<NSAttributedString.Key, object> { [NSAttributedString.Key.font] = font, [NSAttributedString.Key.foregroundColor] = NSColor.white });
        }
    }
    internal static (string name, NSFont font, string text)[] PositionCases() => new[]
    {
        ("mono12r", NSFont.monospacedSystemFont(12, NSFont.Weight.regular), "H"), ("sys44b", NSFont.systemFont(44, NSFont.Weight.bold), "Paused"),
        ("sys17r", NSFont.systemFont(17, NSFont.Weight.regular), "Get ready"), ("sys21m", NSFont.systemFont(21, NSFont.Weight.medium), "l"),
    };

    /// <summary>(key, font, string): the game's literal HUD/menu strings in their fonts (TextCalibration.swift widthCases).</summary>
    internal static List<(string key, NSFont font, string text)> WidthCases()
    {
        NSFont sys(double s, NSFont.Weight w) => NSFont.systemFont(s, w);
        NSFont mono(double s, NSFont.Weight w) => NSFont.monospacedSystemFont(s, w);
        var b = NSFont.Weight.bold; var r = NSFont.Weight.regular; var m = NSFont.Weight.medium; var sb = NSFont.Weight.semibold;
        return new()
        {
            ("sys44b Paused", sys(44, b), "Paused"), ("sys80b 3", sys(80, b), "3"),
            ("sys30b Race finished", sys(30, b), "Race finished"), ("sys17r Get ready to race", sys(17, r), "Get ready to race"),
            ("sys17r The race is waiting for you.", sys(17, r), "The race is waiting for you."),
            ("sys12s TAKE A BREATHER", sys(12, sb), "TAKE A BREATHER"), ("sys12s DIRT TRACK · 3 LAPS", sys(12, sb), "DIRT TRACK · 3 LAPS"),
            ("sys13m WASD", sys(13, m), "WASD / ↑↓←→  Drive    ·    Shift  Boost"), ("sys13m P / Esc", sys(13, m), "P / Esc  Resume    ·    ⌘R  Restart"),
            ("sys26s A storm is coming...", sys(26, sb), "A storm is coming..."), ("sys21m Sandbox", sys(21, m), "Sandbox"),
            ("sys21m Dirt Track", sys(21, m), "Dirt Track"), ("sys58b Marvin", sys(58, b), "Marvin"),
            ("sys18r Beep", sys(18, r), "Beep, boop... just some fun."), ("sys12r hint", sys(12, r), "↑ ↓ to choose   ·   Return to select"),
            ("sys11b FINAL CLASSIFICATION", sys(11, b), "FINAL CLASSIFICATION"), ("sys16b WALL-E", sys(16, b), "WALL-E"),
            ("sys16m Marvin", sys(16, m), "Marvin"), ("sys12r Autopilot", sys(12, r), "Autopilot · Enjoy the cooldown lap"),
            ("sys12m Race again", sys(12, m), "⌘R  Race again    ·    Main Menu to leave"), ("sys10b BEST LAP", sys(10, b), "BEST LAP"),
            ("sys16b OFF TO TOWN", sys(16, b), "OFF TO TOWN"), ("sys13m 1.  Marvin", sys(13, m), "1.  Marvin"),
            ("sys21s A little room", sys(21, sb), "A little room to explore."), ("sys18s Find beacon", sys(18, sb), "Find beacon 1 of 5"),
            ("sys11r Drive through", sys(11, r), "Drive through the amber ring."), ("sys34b MOS ASTER", sys(34, b), "MOS ASTER"),
            ("sys16m Preparing", sys(16, m), "Preparing the dirt race"), ("sys13r stage", sys(13, r), "Preparing the sand and racecourse"),
            ("mono23b DIRT TRACK", mono(23, b), "DIRT TRACK"), ("mono16r CURRENT", mono(16, r), "CURRENT  0:04.00"),
            ("mono13r standings", mono(13, r), "Marvin  LAP 1 / 3"), ("mono12r help", mono(12, r), "DRIVE W A S D / arrows   BOOST Shift   BRAKE Space"),
            ("mono11m keys", mono(11, m), "W A S D / ↑ ↓ ← →"), ("mono9b START", mono(9, b), "START / FINISH"),
            ("mono14r time", mono(14, r), "1:29.18"), ("mono17b LOCAL", mono(17, b), "LOCAL HIGH SCORES"),
            ("monodigit13s 59.9 FPS", NSFont.monospacedDigitSystemFont(13, sb), "59.9 FPS"),
            ("monodigit13s — FPS", NSFont.monospacedDigitSystemFont(13, sb), "— FPS"),
        };
    }

    [Marvin.GameMode("--text-calibration")]
    public static async Task Run(string dir, SceneTree tree)
    {
        System.IO.Directory.CreateDirectory(dir);
        var cases = new List<(string kind, double value)> { ("raw", 0), ("raw", 1) };
        cases.AddRange(Greys.Select(g => ("plain", g)));
        cases.AddRange(Greys.Select(g => ("label", g)));
        cases.AddRange(Gains.Select(g => ("gain", g)));
        foreach (var spec in Specs)
        {
            var font = spec.mono ? NSFont.monospacedSystemFont(spec.size, spec.weight) : NSFont.systemFont(spec.size, spec.weight);
            double rowH = Math.Ceiling(spec.size * 1.5) + 12, width = Math.Ceiling(spec.size * 0.62 * Text.Length) + 24;
            double height = rowH * cases.Count;
            var root = new NSView(new CGRect(0, 0, width, height)) { wantsLayer = true };
            root.layer.backgroundColor = NSColor.gray;
            tree.Root.AddChild(root);
            for (int i = 0; i < cases.Count; i++)
            {
                var (kind, value) = cases[i];
                double g = kind == "gain" ? 0 : value;
                var bg = g >= 0.5 ? Grey(0) : Grey(1);
                var frame = new CGRect(0, height - rowH * (i + 1), width, rowH);
                if (kind == "label")
                {
                    var parent = new NSView(frame) { wantsLayer = true };
                    parent.layer.backgroundColor = bg;
                    root.addSubview(parent);
                    var label = NSTextField.labelWithString(Text);
                    label.font = font; label.textColor = Grey(g);
                    label.frame = new CGRect(6, 0, width - 12, rowH - 6);
                    parent.addSubview(label);
                }
                else
                {
                    root.addSubview(new TextView(frame) { font = font, fg = Grey(g), bg = bg, smooth = kind != "raw", gain = kind == "gain" ? value : null });
                }
            }
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            root.layoutSubtreeIfNeeded();
            var bitmap = root.bitmapImageRepForCachingDisplay(root.bounds);
            root.cacheDisplay(root.bounds, bitmap);
            bitmap.representation(NSBitmapImageFileType.png, new()).write(URL.fileURLWithPath(dir).appendingPathComponent(spec.name + ".png"));
            root.QueueFree();
        }
        // Horizontal glyph positioning (TextCalibration.swift positions-*.png).
        foreach (var (name, font, text) in PositionCases())
        foreach (var smooth in new[] { true, false })
        {
            var view = new PositionView(new CGRect(0, 0, 400, 1420)) { font = font, text = text, smooth = smooth };
            tree.Root.AddChild(view);
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            var bitmap = view.bitmapImageRepForCachingDisplay(view.bounds);
            view.cacheDisplay(view.bounds, bitmap);
            bitmap.representation(NSBitmapImageFileType.png, new()).write(URL.fileURLWithPath(dir).appendingPathComponent($"positions-{(smooth ? "" : "raw-")}{name}.png"));
            view.QueueFree();
        }
        // String widths and heights (AppKit layout inputs), as TextCalibration.swift's widths.json.
        var sizes = new Godot.Collections.Dictionary();
        foreach (var (key, font, text) in WidthCases())
        {
            var size = text.size(new Dictionary<NSAttributedString.Key, object> { [NSAttributedString.Key.font] = font });
            sizes[key] = new Godot.Collections.Array { size.width, size.height };
        }
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "widths.json"), Json.Stringify(sizes, "  ", sortKeys: true));
        Foundation.print($"Text calibration: {Specs.Length} fonts, {sizes.Count} widths · {dir}");
        Foundation.exit(0);
    }
}
