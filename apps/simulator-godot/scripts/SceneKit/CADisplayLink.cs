using System;
using System.Collections.Generic;
using Godot;

namespace Marvin.SceneKit;

/// <summary>CAFrameRateRange(minimum:maximum:preferred:).</summary>
public struct CAFrameRateRange
{
    public float minimum, maximum, preferred;
    public CAFrameRateRange(float minimum, float maximum, float preferred) { this.minimum = minimum; this.maximum = maximum; this.preferred = preferred; }
}

/// <summary>RunLoop.main and its modes, for <c>link.add(to: .main, forMode: .common)</c>.</summary>
public sealed class RunLoop
{
    public enum Mode { common, @default }
    public static readonly RunLoop main = new();
    private RunLoop() { }
}

/// <summary>
/// CADisplayLink from <c>NSView.displayLink(target:selector:)</c> (macOS 14): calls its selector once per displayed frame
/// while the view's window is on screen. PORT: Godot's frame loop is the display-synchronised loop (vsync, capped by
/// <c>Engine.MaxFps</c>), so the link fires from a facade node at the start of every Godot frame in which the window is
/// not minimised (AppKit pauses a view's display link while the view is off screen). <c>preferredFrameRateRange</c>
/// caps the frame rate at its preferred value. <c>timestamp</c> is the frame's start (ProcessInfo.systemUptime) and
/// <c>targetTimestamp</c> one frame duration later.
/// </summary>
public sealed class CADisplayLink
{
    private readonly WeakReference<Control> view;
    private readonly Action<object> selector;
    private CAFrameRateRange _range;
    public bool isPaused;
    public double timestamp { get; private set; }
    public double targetTimestamp { get; private set; }
    public double duration => 1.0 / Math.Max(1, Engine.MaxFps > 0 ? Engine.MaxFps : DisplayServer.ScreenGetRefreshRate());
    internal bool valid = true;

    internal CADisplayLink(Control view, Action<object> selector)
    {
        this.view = new WeakReference<Control>(view);
        this.selector = selector;
    }
    public CAFrameRateRange preferredFrameRateRange
    {
        get => _range;
        set { _range = value; if (value.preferred > 0) Engine.MaxFps = (int)Math.Round(value.preferred); }
    }
    /// <summary>add(to:forMode:): starts calling the selector every frame.</summary>
    public void add(RunLoop to, RunLoop.Mode forMode) => DisplayLinkHost.Ensure().links.Add(this);
    /// <summary>invalidate(): stops the link for good.</summary>
    public void invalidate() { valid = false; DisplayLinkHost.Remove(this); }

    internal void fire(double now)
    {
        if (!valid || isPaused) return;
        if (!view.TryGetTarget(out var v) || !GodotObject.IsInstanceValid(v) || !v.IsInsideTree()) return;
        if (NSWindow.Of(v.GetWindow()) is NSWindow window)
        {
            // AppKit delivers windowDidDeminiaturize before the display link resumes: let the delegate see the state
            // change first (the game resets its clock there, so the hidden interval is not caught up).
            window.pollState();
            if (!window.occlusionState.contains(NSWindow.OcclusionState.visible)) return;
        }
        timestamp = now; targetTimestamp = now + duration;
        selector(this);
    }
}

/// <summary>Fires the active display links at the start of each frame (before the app's and the runtime's _Process).</summary>
internal partial class DisplayLinkHost : Node
{
    private static DisplayLinkHost instance;
    internal readonly List<CADisplayLink> links = new();
    internal static DisplayLinkHost Ensure()
    {
        if (instance != null && IsInstanceValid(instance)) return instance;
        instance = new DisplayLinkHost { Name = "CADisplayLink", ProcessPriority = int.MinValue, ProcessMode = ProcessModeEnum.Always };
        (Engine.GetMainLoop() as SceneTree)?.Root.AddChild(instance);
        return instance;
    }
    internal static void Remove(CADisplayLink link) { if (instance != null && IsInstanceValid(instance)) instance.links.Remove(link); }
    public override void _Process(double delta)
    {
        double now = ProcessInfo.processInfo.systemUptime;
        foreach (var link in links.ToArray()) link.fire(now);
    }
}
