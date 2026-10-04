using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Marvin.SceneKit;

/// <summary>Notification: the argument of delegate callbacks (name and sending object).</summary>
public sealed class Notification
{
    public readonly string name;
    public readonly object @object;
    public Notification(string name, object @object = null) { this.name = name; this.@object = @object; }
}

/// <summary>NSWindowDelegate: the window notifications the facade delivers (Godot window focus, minimise, resize).</summary>
public interface NSWindowDelegate
{
    void windowDidBecomeKey(Notification notification) { }
    void windowDidResignKey(Notification notification) { }
    void windowDidChangeOcclusionState(Notification notification) { }
    void windowDidMiniaturize(Notification notification) { }
    void windowDidDeminiaturize(Notification notification) { }
    void windowDidResize(Notification notification) { }
}

/// <summary>NSApplicationDelegate: launch, activation (Godot application focus) and termination (Godot close request).</summary>
public interface NSApplicationDelegate
{
    void applicationDidFinishLaunching(Notification notification) { }
    void applicationWillResignActive(Notification notification) { }
    void applicationDidBecomeActive(Notification notification) { }
    void applicationWillTerminate(Notification notification) { }
    bool applicationShouldTerminateAfterLastWindowClosed(NSApplication sender) => false;
}

/// <summary>
/// NSWindow for a Godot Window. The first responder is Godot's focus owner; makeFirstResponder(nil) makes the window
/// itself first responder (no control focused).
///
/// <c>NSWindow(contentRect:styleMask:backing:defer:)</c> adopts the game's (only) Godot window and sizes its client
/// area to the content rect. A titled window gets the facade's title bar (<see cref="NSWindowFrame"/>): with
/// <c>.fullSizeContentView</c> the content view fills the whole client area and the title bar (with the toolbar when one
/// is visible) is drawn over it, as on macOS. On macOS Godot's <c>extend_to_title</c> makes the client area the whole
/// window, so the system draws only the traffic lights over the facade's title bar and the window has the Mac game's
/// 1280 x 820 frame; elsewhere the system title bar sits above a client area of the same size, and the facade title bar
/// holds the menu bar (Windows has no global menu bar). Measured title bar metrics are those of the macOS game, which
/// links against the macOS 13 SDK and so keeps AppKit's pre-macOS 26 window style (tools/scenekit-reference/WindowChrome.swift):
/// a 28-point title bar, 52 points with the unified toolbar.
/// <c>contentLayoutGuide</c> is the client area below the title bar; overlays pinned to it with NSLayoutConstraint follow
/// toolbar visibility and window resizes.
/// </summary>
public sealed class NSWindow
{
    [Flags]
    public enum StyleMask { borderless = 0, titled = 1, closable = 2, miniaturizable = 4, resizable = 8, fullSizeContentView = 1 << 15 }
    public enum BackingStoreType { retained = 0, nonretained = 1, buffered = 2 }
    [Flags]
    public enum OcclusionState { visible = 1 << 1 }

    /// <summary>Title bar height without a toolbar (macOS 13 SDK window style, measured).</summary>
    public const double TitlebarHeight = 28;
    /// <summary>Height of the unified title bar and toolbar (.iconAndLabel, measured).</summary>
    public const double UnifiedToolbarHeight = 52;

    private static readonly Dictionary<ulong, NSWindow> windows = new();
    internal readonly Window godotWindow;
    public StyleMask styleMask;
    public NSWindowDelegate @delegate;
    public NSColor backgroundColor;
    private NSToolbar _toolbar;
    private CGSize _minSize;
    private Control _contentView;
    private bool miniaturized;
    internal NSWindowFrame frameView;
    internal readonly List<NSLayoutConstraint> constraints = new();
    public readonly NSLayoutGuide contentLayoutGuide;

    private NSWindow(Window window)
    {
        godotWindow = window;
        contentLayoutGuide = new NSLayoutGuide(this);
    }
    internal static NSWindow Of(Window window)
    {
        if (window == null) return null;
        ulong id = window.GetInstanceId();
        if (!windows.TryGetValue(id, out var w)) windows[id] = w = new NSWindow(window);
        return w;
    }

    /// <summary>NSWindow(contentRect:styleMask:backing:defer:): the game window (Godot's root window) with this content size.</summary>
    public NSWindow(CGRect contentRect, StyleMask styleMask, BackingStoreType backing, bool @defer)
        : this((Engine.GetMainLoop() as SceneTree)?.Root)
    {
        this.styleMask = styleMask;
        windows[godotWindow.GetInstanceId()] = this;
        if (HasWindow)
        {
            var id = WindowId;
            if (styleMask.HasFlag(StyleMask.fullSizeContentView) && OS.GetName() == "macOS")
                DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.ExtendToTitle, true, id);
            DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.ResizeDisabled, !styleMask.HasFlag(StyleMask.resizable), id);
            // Points, as in AppKit: on a HiDPI screen the window has backingScaleFactor pixels per point and the views
            // keep their point sizes (Godot's content scale). PORT: the SCNView still renders 3D at one pixel per point.
            backingScaleFactor = ScreenScale(id);
            if (backingScaleFactor > 1) godotWindow.ContentScaleFactor = (float)backingScaleFactor;
            DisplayServer.WindowSetSize(Pixels(contentRect.size), id);
        }
        if (styleMask.HasFlag(StyleMask.titled))
        {
            frameView = new NSWindowFrame(this);
            var layer = new CanvasLayer { Name = "NSWindowFrame", Layer = 100 };
            layer.AddChild(frameView);
            godotWindow.AddChild(layer);
        }
        godotWindow.SizeChanged += () => { layoutConstraints(); @delegate?.windowDidResize(new Notification("NSWindowDidResizeNotification", this)); };
    }

    private bool HasWindow => DisplayServer.GetName() != "headless" && godotWindow == (Engine.GetMainLoop() as SceneTree)?.Root;
    private int WindowId => (int)godotWindow.GetWindowId();
    /// <summary>backingScaleFactor: device pixels per point (the screen's scale on macOS, its DPI / 96 elsewhere).</summary>
    public double backingScaleFactor { get; private set; } = 1;
    private static double ScreenScale(int window)
    {
        // Test hook: MARVIN_BACKING_SCALE simulates a HiDPI screen on a 1x display.
        if (double.TryParse(System.Environment.GetEnvironmentVariable("MARVIN_BACKING_SCALE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var forced) && forced >= 1) return forced;
        int screen = DisplayServer.WindowGetCurrentScreen(window);
        double scale = OS.GetName() == "macOS" ? DisplayServer.ScreenGetScale(screen) : DisplayServer.ScreenGetDpi(screen) / 96.0;
        return Math.Clamp(Math.Round(scale * 4) / 4, 1, 3);
    }
    private Vector2I Pixels(CGSize size) => new((int)Math.Round(size.width * backingScaleFactor), (int)Math.Round(size.height * backingScaleFactor));

    public int windowNumber => (int)godotWindow.GetWindowId();
    public string title
    {
        get => godotWindow.Title;
        set { godotWindow.Title = value ?? ""; frameView?.needsDisplayTitle(); }
    }
    public bool isKeyWindow => godotWindow.HasFocus();
    /// <summary>minSize: the smallest client size the window can be resized to.</summary>
    public CGSize minSize
    {
        get => _minSize;
        set { _minSize = value; if (HasWindow) DisplayServer.WindowSetMinSize(Pixels(value), WindowId); }
    }
    /// <summary>frame: the window's position and client size (screen pixels).</summary>
    public CGRect frame
    {
        get
        {
            if (!HasWindow) { var s = godotWindow.Size; return new CGRect(0, 0, s.X, s.Y); }
            var p = DisplayServer.WindowGetPosition(WindowId); var size = DisplayServer.WindowGetSize(WindowId);
            return new CGRect(p.X, p.Y, size.X, size.Y);
        }
    }
    public void setFrame(CGRect frameRect, bool display)
    {
        if (!HasWindow) return;
        DisplayServer.WindowSetPosition(new Vector2I((int)frameRect.minX, (int)frameRect.minY), WindowId);
        DisplayServer.WindowSetSize(new Vector2I((int)frameRect.width, (int)frameRect.height), WindowId);
    }
    /// <summary>center(): centres the window on its screen.</summary>
    public void center()
    {
        if (!HasWindow) return;
        int screen = DisplayServer.WindowGetCurrentScreen(WindowId);
        var area = DisplayServer.ScreenGetUsableRect(screen);
        var size = DisplayServer.WindowGetSize(WindowId);
        DisplayServer.WindowSetPosition(area.Position + (area.Size - size) / 2, WindowId);
    }
    public void makeKeyAndOrderFront(object sender) { if (HasWindow) DisplayServer.WindowMoveToForeground(WindowId); }
    /// <summary>occlusionState: .visible unless the window is minimised (Godot does not report occlusion by other windows).</summary>
    public OcclusionState occlusionState =>
        HasWindow && DisplayServer.WindowGetMode(WindowId) == DisplayServer.WindowMode.Minimized ? 0 : OcclusionState.visible;

    /// <summary>contentView: fills the window (autoresizing width and height).</summary>
    public Control contentView
    {
        get => _contentView;
        set
        {
            if (_contentView == value) return;
            if (_contentView != null && _contentView.GetParent() == godotWindow) godotWindow.RemoveChild(_contentView);
            _contentView = value;
            if (value == null) return;
            if (value.GetParent() is Node old) old.RemoveChild(value);
            godotWindow.AddChild(value);
            // Below the title bar's canvas layer, above anything the app added earlier.
            value.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            // The window's frame rate follows its content view (SCNView.preferredFramesPerSecond; Godot renders all
            // views in one frame loop).
            if (value is SCNView scnView && scnView.preferredFramesPerSecond > 0) Engine.MaxFps = scnView.preferredFramesPerSecond;
            layoutConstraints();
        }
    }

    /// <summary>toolbar: the title bar shows its items while it is visible (see NSToolbar).</summary>
    public NSToolbar toolbar
    {
        get => _toolbar;
        set
        {
            if (_toolbar != null) _toolbar.window = null;
            _toolbar = value;
            if (value != null) { value.window = this; value.loadItems(); }
            toolbarChanged();
        }
    }
    internal void toolbarChanged() { frameView?.toolbarChanged(); layoutConstraints(); }

    /// <summary>Height of the title bar drawn over a full-size content view (0 for untitled windows).</summary>
    internal double titlebarHeight => !styleMask.HasFlag(StyleMask.titled) ? 0 : _toolbar is { isVisible: true } ? UnifiedToolbarHeight : TitlebarHeight;
    /// <summary>contentLayoutRect: the client area below the title bar, in window coordinates (origin bottom left).</summary>
    public CGRect contentLayoutRect
    {
        get
        {
            var s = godotWindow.GetVisibleRect().Size;
            double top = styleMask.HasFlag(StyleMask.fullSizeContentView) ? titlebarHeight : 0;
            return new CGRect(0, 0, s.X, Math.Max(0, s.Y - top));
        }
    }
    /// <summary>Re-applies the active constraints (Auto Layout's pass after the layout guide or a view changed).</summary>
    public void layoutConstraints() => NSLayoutConstraint.Solve(this);

    /// <summary>firstResponder: the focused responder (null when the window itself is first responder).</summary>
    public NSResponder firstResponder => godotWindow.GuiGetFocusOwner() as NSResponder;
    public bool makeFirstResponder(NSResponder responder)
    {
        if (responder == null) { godotWindow.GuiReleaseFocus(); return true; }
        if (responder is not Control control || !responder.acceptsFirstResponder) return false;
        if (godotWindow.GuiGetFocusOwner() == control) return true;
        if (!control.IsInsideTree() || !control.IsVisibleInTree()) return false;
        if (control.FocusMode == Control.FocusModeEnum.None) control.FocusMode = Control.FocusModeEnum.All;
        control.GrabFocus();
        return true;
    }

    /// <summary>Called by the title bar every frame: minimise and occlusion changes for the delegate.</summary>
    internal void pollState()
    {
        bool minimized = occlusionState == 0;
        if (minimized == miniaturized) return;
        miniaturized = minimized;
        if (minimized) @delegate?.windowDidMiniaturize(new Notification("NSWindowDidMiniaturizeNotification", this));
        else @delegate?.windowDidDeminiaturize(new Notification("NSWindowDidDeminiaturizeNotification", this));
        @delegate?.windowDidChangeOcclusionState(new Notification("NSWindowDidChangeOcclusionStateNotification", this));
    }
}

/// <summary>Swift OptionSet spelling for NSWindow.OcclusionState.</summary>
public static class NSWindowOcclusionStateExtensions
{
    public static bool contains(this NSWindow.OcclusionState state, NSWindow.OcclusionState member) => (state & member) == member;
}

/// <summary>NSAppearance: the light (aqua) and dark (darkAqua) appearances.</summary>
public sealed class NSAppearance
{
    public static class Name { public const string aqua = "NSAppearanceNameAqua", darkAqua = "NSAppearanceNameDarkAqua"; }
    public readonly string name;
    private NSAppearance(string name) { this.name = name; }
    /// <summary>NSAppearance(named:).</summary>
    public static NSAppearance named(string name) => name is Name.aqua or Name.darkAqua ? new NSAppearance(name) : null;
    /// <summary>bestMatch(from:): this appearance's name when listed, else the first.</summary>
    public string bestMatch(IEnumerable<string> from) { var names = from.ToList(); return names.Contains(name) ? name : names.FirstOrDefault(); }
    public override bool Equals(object obj) => obj is NSAppearance a && a.name == name;
    public override int GetHashCode() => name.GetHashCode();
}

/// <summary>NSKeyValueObservation: a key-value observation; invalidate() (or dropping the app) stops it.</summary>
public sealed class NSKeyValueObservation
{
    internal Action fire;
    public void invalidate() { fire = null; NSApplication.shared.observations.Remove(this); }
}
[Flags] public enum NSKeyValueObservingOptions { @new = 1, old = 2, initial = 4, prior = 8 }

/// <summary>
/// NSApplication.shared (NSApp). <c>run()</c> (main.swift) adds the delegate to the scene tree when it is a Godot node and
/// calls applicationDidFinishLaunching; the facade then delivers activation (Godot application focus) and termination
/// (Godot close request) to the delegate. <c>mainMenu</c> becomes the macOS global menu bar (Godot NativeMenu) or the
/// in-window menu bar elsewhere; its key equivalents (Command on macOS, Control elsewhere) are handled before the
/// first responder sees the key, as AppKit's performKeyEquivalent.
/// </summary>
public sealed class NSApplication
{
    public enum ActivationPolicy { regular = 0, accessory = 1, prohibited = 2 }
    public static readonly NSApplication shared = new();
    public NSApplicationDelegate @delegate;
    private NSMenu _mainMenu;
    private NSAppearance _appearance;
    private NSImage _applicationIconImage;
    internal readonly List<NSKeyValueObservation> observations = new();
    private bool systemThemeObserved;
    private NSApplication() { }

    public void setActivationPolicy(ActivationPolicy policy) { }
    /// <summary>run(): starts the app (main.swift). PORT: returns at once; Godot's main loop drives it.</summary>
    public void run()
    {
        var tree = Engine.GetMainLoop() as SceneTree;
        NSApplicationHost.Ensure();
        if (@delegate is Node node && node.GetParent() == null) tree?.Root.AddChild(node);
        @delegate?.applicationDidFinishLaunching(new Notification("NSApplicationDidFinishLaunchingNotification", this));
    }
    /// <summary>terminate(_:): applicationWillTerminate, then quits (exit code 0).</summary>
    public void terminate(object sender)
    {
        @delegate?.applicationWillTerminate(new Notification("NSApplicationWillTerminateNotification", this));
        Foundation.exit(0);
    }
    public bool isActive => DisplayServer.WindowIsFocused();
    /// <summary>activate(ignoringOtherApps:).</summary>
    public void activate(bool ignoringOtherApps) { if (DisplayServer.GetName() != "headless") DisplayServer.WindowMoveToForeground(); }

    /// <summary>mainMenu: the menu bar (see NSMenu).</summary>
    public NSMenu mainMenu { get => _mainMenu; set { _mainMenu = value; NSApplicationHost.Ensure().installMenu(value); } }

    /// <summary>applicationIconImage: the Dock / window icon.</summary>
    public NSImage applicationIconImage
    {
        get => _applicationIconImage;
        set { _applicationIconImage = value; if (value?.GodotImage is Image image && DisplayServer.GetName() != "headless") DisplayServer.SetIcon(image); }
    }

    /// <summary>appearance: forced appearance, or nil for the system's (Godot's dark-mode query).</summary>
    public NSAppearance appearance
    {
        get => _appearance;
        set { _appearance = value; appearanceChanged(); }
    }
    public NSAppearance effectiveAppearance => _appearance ?? NSAppearance.named(DisplayServer.IsDarkModeSupported() && DisplayServer.IsDarkMode() ? NSAppearance.Name.darkAqua : NSAppearance.Name.aqua);
    /// <summary>observe(\.effectiveAppearance, options:changeHandler:). PORT: the key path is a string ("effectiveAppearance").</summary>
    public NSKeyValueObservation observe(string keyPath, NSKeyValueObservingOptions options, Action<NSApplication, object> changeHandler)
    {
        if (keyPath != "effectiveAppearance") throw new ArgumentException($"observe: unsupported key path {keyPath}");
        var observation = new NSKeyValueObservation();
        observation.fire = () => changeHandler(this, null);
        observations.Add(observation);
        if (!systemThemeObserved && DisplayServer.GetName() != "headless")
        {
            systemThemeObserved = true;
            DisplayServer.SetSystemThemeChangeCallback(Callable.From(() => { if (_appearance == null) appearanceChanged(); }));
        }
        if (options.HasFlag(NSKeyValueObservingOptions.initial)) observation.fire?.Invoke();
        return observation;
    }
    private void appearanceChanged() { foreach (var o in observations.ToList()) o.fire?.Invoke(); }

    /// <summary>orderFrontStandardAboutPanel(_:): name, version and copyright from the project settings.</summary>
    public void orderFrontStandardAboutPanel(object sender)
    {
        string name = (string)ProjectSettings.GetSetting("application/config/name", "");
        string version = (string)ProjectSettings.GetSetting("application/config/version", "");
        string copyright = (string)ProjectSettings.GetSetting("application/config/copyright", "");
        var alert = new NSAlert { messageText = name, informativeText = $"Version {version}\n\n{copyright}".Trim(), alertStyle = NSAlert.Style.informational };
        alert.runModal();
    }
}

/// <summary>
/// The facade's application node: AppKit's event preprocessing (menu key equivalents before the first responder) and
/// the application delegate's activation and termination callbacks.
/// </summary>
internal partial class NSApplicationHost : Node
{
    private static NSApplicationHost instance;
    private MenuBar menuBar;
    /// <summary>The Godot menu bar of NSApp.mainMenu (for tests).</summary>
    internal static MenuBar currentMenuBar => instance?.menuBar;
    internal static NSApplicationHost Ensure()
    {
        if (instance != null && IsInstanceValid(instance)) return instance;
        instance = new NSApplicationHost { Name = "NSApplication", ProcessMode = ProcessModeEnum.Always };
        (Engine.GetMainLoop() as SceneTree)?.Root.AddChild(instance);
        return instance;
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true } key || NSApplication.shared.mainMenu is not NSMenu menu) return;
        if (NSMenu.performKeyEquivalent(menu, key)) GetViewport().SetInputAsHandled();
    }

    public override void _Notification(int what)
    {
        var app = NSApplication.shared;
        switch ((long)what)
        {
            case NotificationApplicationFocusOut: app.@delegate?.applicationWillResignActive(new Notification("NSApplicationWillResignActiveNotification", app)); break;
            case NotificationApplicationFocusIn: app.@delegate?.applicationDidBecomeActive(new Notification("NSApplicationDidBecomeActiveNotification", app)); break;
            case NotificationWMCloseRequest: app.@delegate?.applicationWillTerminate(new Notification("NSApplicationWillTerminateNotification", app)); break;
        }
    }

    /// <summary>The menu bar: global on macOS (MenuBar.PreferGlobalMenu), else drawn in the window's title bar.</summary>
    internal void installMenu(NSMenu menu)
    {
        menuBar?.QueueFree();
        menuBar = null;
        if (menu == null || DisplayServer.GetName() == "headless") return;
        menuBar = NSMenu.makeMenuBar(menu);
        var window = NSWindow.Of((Engine.GetMainLoop() as SceneTree)?.Root);
        if (window?.frameView != null && !NSMenu.usesGlobalMenu) window.frameView.installMenuBar(menuBar);
        else AddChild(menuBar);
    }
}

/// <summary>NSAlert shown as a Godot AcceptDialog. PORT: runModal() does not block; it returns NSApplication.ModalResponse.alertFirstButtonReturn (1000).</summary>
public sealed class NSAlert
{
    public enum Style { warning = 0, informational = 1, critical = 2 }
    public string messageText = "Alert", informativeText = "";
    public Style alertStyle = Style.warning;
    private readonly List<string> buttons = new();
    public void addButton(string withTitle) => buttons.Add(withTitle);
    /// <summary>beginSheetModal(for:completionHandler:).</summary>
    public void beginSheetModal(NSWindow @for, Action<long> completionHandler = null) => Show(@for?.godotWindow, completionHandler);
    public long runModal() { Show(Engine.GetMainLoop() is SceneTree tree ? tree.Root : null, null); return 1000; }
    private void Show(Window parent, Action<long> completionHandler)
    {
        if (parent == null) return;
        var dialog = new AcceptDialog { Title = messageText, DialogText = string.IsNullOrEmpty(informativeText) ? messageText : messageText + "\n\n" + informativeText, Exclusive = true };
        dialog.OkButtonText = buttons.Count > 0 ? buttons[0] : "OK";
        for (int i = 1; i < buttons.Count; i++) dialog.AddButton(buttons[i], false, (1000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture));
        dialog.Confirmed += () => { completionHandler?.Invoke(1000); dialog.QueueFree(); };
        dialog.CustomAction += action => { completionHandler?.Invoke(long.Parse(action, System.Globalization.CultureInfo.InvariantCulture)); dialog.QueueFree(); };
        dialog.Canceled += () => dialog.QueueFree();
        parent.AddChild(dialog);
        dialog.PopupCentered();
    }
}
