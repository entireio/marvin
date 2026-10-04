using System;
using Marvin.Core;

namespace Marvin;

/// Render-callback rate, independent of the simulation's update clock.
public partial class FrameRateHUD : NSView, SCNSceneRendererDelegate
{
    private readonly NSLock sampleLock = new NSLock();
    private double? sampleStart;
    private int frames = 0;
    public double? framesPerSecond { get; private set; }
    public Action<double, double?> onSample;
    public string displayedText => framesPerSecond is double fps ? Swift.format("%.1f FPS", fps) : "— FPS";
    public override NSView hitTest(NSPoint point) => null;

    public void resetSamples()
    {
        sampleLock.@lock(); sampleStart = null; frames = 0; sampleLock.unlock();
        framesPerSecond = null; needsDisplay = true;
    }

    public void rendererDidRenderScene(SCNSceneRenderer renderer, SCNScene scene, double time)
    {
        var now = ProcessInfo.processInfo.systemUptime;
        sampleLock.@lock();
        if (sampleStart is not double start) { sampleStart = now; sampleLock.unlock(); return; }
        frames += 1;
        var elapsed = now - start;
        if (!(elapsed >= 0.5)) { sampleLock.unlock(); return; }
        double? fps = elapsed < 2 ? (double)frames / elapsed : null;
        sampleStart = now; frames = 0; sampleLock.unlock();
        DispatchQueue.main.async(() =>
        {
            if (!IsInstanceValid(this)) return;
            framesPerSecond = fps; needsDisplay = true;
            onSample?.Invoke(now, fps);
        });
    }

    public override void draw(NSRect dirtyRect)
    {
        var label = displayedText;
        var attributes = new System.Collections.Generic.Dictionary<NSAttributedString.Key, object>
        {
            [NSAttributedString.Key.font] = NSFont.monospacedDigitSystemFont(13, NSFont.Weight.semibold),
            [NSAttributedString.Key.foregroundColor] = NSColor.calibratedWhite(0.97, 1),
        };
        var size = label.size(attributes);
        var panel = new NSRect(bounds.width - 132, 22, 110, 32);
        NSColor.calibratedRed(0.15, 0.19, 0.16, 0.90).setFill();
        NSBezierPath.roundedRect(panel, 9, 9).fill();
        label.draw(new NSPoint(panel.midX - size.width / 2, panel.midY - size.height / 2), attributes);
    }
}
