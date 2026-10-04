using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using static Marvin.Core.Swift;

namespace Marvin;

// PORT: Swift `private final class` (file scope); Godot classes must be partial, so it is internal.
internal sealed partial class LoadingProgressBar : NSView
{
    private double _value = 0.0;
    public double value { get => _value; set { _value = value; needsDisplay = true; setAccessibilityValue(value); } }
    public override void draw(NSRect dirtyRect)
    {
        NSColor.calibratedWhite(0.22, 1).setFill();
        NSBezierPath.roundedRect(bounds, 4, 4).fill();
        NSColor.calibratedRed(0.88, 0.69, 0.41, 1).setFill();
        var fill = new NSRect(0, 0, bounds.width * value, bounds.height);
        NSBezierPath.roundedRect(fill, 4, 4).fill();
    }
}

public sealed partial class LevelLoadingView : NSView
{
    private readonly NSTextField title = NSTextField.labelWithString("MOS ASTER");
    private readonly NSTextField subtitle = NSTextField.labelWithString("Preparing the dirt race");
    private readonly NSTextField stage = NSTextField.labelWithString("");
    private readonly NSTextField percentage = NSTextField.labelWithString("0%");
    private readonly LoadingProgressBar bar = new LoadingProgressBar();
    public List<double> history { get; private set; } = new List<double>();
    public override bool acceptsFirstResponder => true;
    public override void keyDown(NSEvent @event) { }
    public LevelLoadingView() : this(NSRect.zero) { }
    public LevelLoadingView(NSRect frame) : base(frame)
    {
        wantsLayer = true; layer.backgroundColor = NSColor.calibratedRed(0.085, 0.105, 0.12, 1).cgColor;
        foreach (var label in new[] { title, subtitle, stage, percentage }) { addSubview(label); label.alignment = NSTextAlignment.center; label.textColor = NSColor.calibratedWhite(0.85, 1); }
        title.font = NSFont.systemFont(34, NSFont.Weight.bold); title.textColor = NSColor.calibratedRed(0.88, 0.69, 0.41, 1);
        subtitle.font = NSFont.systemFont(16, NSFont.Weight.medium); stage.font = NSFont.systemFont(13); percentage.font = NSFont.monospacedDigitSystemFont(13, NSFont.Weight.medium);
        bar.setAccessibilityRole(NSAccessibility.Role.progressIndicator); bar.setAccessibilityLabel("Race loading progress"); addSubview(bar);
    }
    public override void layout()
    {
        base.layout(); var width = min(440, bounds.width - 64); var x = (bounds.width - width) / 2; var y = bounds.height / 2;
        title.frame = new NSRect(x, y + 54, width, 44);
        subtitle.frame = new NSRect(x, y + 22, width, 24);
        bar.frame = new NSRect(x, y - 17, width, 8);
        stage.frame = new NSRect(x, y - 47, width, 22);
        percentage.frame = new NSRect(x, y - 73, width, 20);
    }
    public void begin() { history = new List<double>(); update(0, "Preparing your race"); }
    public void update(double progress, string text)
    {
        var value = max(history.Count > 0 ? history[^1] : 0, min(1, progress)); history.Add(value);
        bar.value = value; percentage.stringValue = $"{((long)(value * 100)).ToString(CultureInfo.InvariantCulture)}%"; stage.stringValue = text;
    }
}

public partial class AppController
{
    public void loadDirtTrack()
    {
        if (isLoadingDirt) return;
        isLoadingDirt = true; loadingHeartbeats = 0; view.clearInput();
        installContentOverlay(loadingView); loadingView.begin(); window.makeFirstResponder(loadingView);
        if (window.toolbar != null) window.toolbar.isVisible = false; mainMenu.portrait.rendersContinuously = false;
        if (cachedDirtWorld != null)
        {
            DispatchQueue.main.async(() => startDirtTrack()); return;
        }
        DispatchQueue.global(DispatchQoS.userInitiated).async(() =>
        {
            var built = new DirtWorld(progress: (fraction, label) =>
            {
                DispatchQueue.main.async(() =>
                {
                    loadingView.update(fraction, label);
                    captureLoadingCheckIfNeeded(fraction);
                });
            });
            DispatchQueue.main.async(() => { cachedDirtWorld = built; startDirtTrack(); });
        });
    }
    public void failDirtLoading()
    {
        isLoadingDirt = false; loadingView.removeFromSuperview(); showMainMenu(null);
        var alert = new NSAlert(); alert.messageText = "The race could not finish loading."; alert.informativeText = "Please try again."; alert.beginSheetModal(window);
    }
    public void captureLoadingCheckIfNeeded(double progress)
    {
        if (!(CommandLine.arguments.Contains("--loading-smoke-test") && smokeDirectory is string directory &&
              progress >= 0.49 && progress < 0.51)) return;
        loadingView.layoutSubtreeIfNeeded(); loadingView.displayIfNeeded();
        if (loadingView.bitmapImageRepForCachingDisplay(loadingView.bounds) is NSBitmapImageRep bitmap)
        {
            loadingView.cacheDisplay(loadingView.bounds, bitmap);
            try { FileManager.@default.createDirectory(directory, withIntermediateDirectories: true); } catch (Exception) { }
            try { bitmap.representation(NSBitmapImageFileType.png, new())?.write(URL.fileURLWithPath(directory).appendingPathComponent("loading.png")); } catch (Exception) { }
        }
    }
    public void finishLoadingCheckIfNeeded()
    {
        if (!CommandLine.arguments.Contains("--loading-smoke-test")) return;
        var h = loadingView.history;
        var passed = h.FirstOrDefault(-1) == 0 && h.LastOrDefault(-1) == 1 && h.Zip(h.Skip(1)).All(pair => pair.First <= pair.Second) && h.Count > 20 && loadingHeartbeats > 5 && !view.isHidden && inSandbox && !isLoadingDirt;
        print($"Loading: {(passed ? "PASS" : "FAIL")}, {h.Count.ToString(CultureInfo.InvariantCulture)} progress reports, {loadingHeartbeats.ToString(CultureInfo.InvariantCulture)} responsive ticks");
        exit(passed ? 0 : 1);
    }
}
