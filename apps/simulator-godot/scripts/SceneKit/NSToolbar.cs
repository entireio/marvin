using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Marvin.SceneKit;

/// <summary>NSToolbarDelegate: the item identifiers and the items (toolbar(_:itemForItemIdentifier:willBeInsertedIntoToolbar:)).</summary>
public interface NSToolbarDelegate
{
    string[] toolbarAllowedItemIdentifiers(NSToolbar toolbar);
    string[] toolbarDefaultItemIdentifiers(NSToolbar toolbar);
    NSToolbarItem toolbar(NSToolbar toolbar, string itemForItemIdentifier, bool willBeInsertedIntoToolbar);
}

/// <summary>
/// NSToolbar: its delegate's default items, shown in the window's unified title bar (NSWindowFrame) while
/// <c>isVisible</c>. Setting <c>isVisible</c> changes the window's content layout rect (28 or 52 points of title bar).
/// </summary>
public sealed class NSToolbar
{
    public enum DisplayMode { @default = 0, iconAndLabel = 1, iconOnly = 2, labelOnly = 3 }
    public readonly string identifier;
    public NSToolbarDelegate @delegate;
    public DisplayMode displayMode = DisplayMode.@default;
    internal NSWindow window;
    private bool _isVisible = true;
    private readonly List<NSToolbarItem> _items = new();
    public NSToolbar(string identifier) { this.identifier = identifier; }
    public IReadOnlyList<NSToolbarItem> items => _items;
    public bool isVisible
    {
        get => _isVisible;
        set { if (_isVisible == value) return; _isVisible = value; window?.toolbarChanged(); }
    }
    internal void loadItems()
    {
        _items.Clear();
        if (@delegate == null) return;
        foreach (var id in @delegate.toolbarDefaultItemIdentifiers(this))
        {
            var item = id is NSToolbarItem.Identifier.flexibleSpace or NSToolbarItem.Identifier.space ? new NSToolbarItem(id) : @delegate.toolbar(this, id, true);
            if (item == null) continue;
            item.toolbar = this;
            _items.Add(item);
        }
    }
    internal void itemChanged() => window?.frameView?.QueueRedraw();
}

/// <summary>NSToolbarItem: label, tooltip, image (an SF Symbol name) and the action called with the item as sender.</summary>
public sealed class NSToolbarItem
{
    public static class Identifier
    {
        public const string flexibleSpace = "NSToolbarFlexibleSpaceItem", space = "NSToolbarSpaceItem";
    }
    public readonly string itemIdentifier;
    internal NSToolbar toolbar;
    private string _label = "";
    private NSImage _image;
    public string toolTip;
    public object target;
    /// <summary>action (Swift: target + selector): called with this item as the sender.</summary>
    public Action<object> action;
    public bool isEnabled = true;
    public NSToolbarItem(string itemIdentifier) { this.itemIdentifier = itemIdentifier; }
    public string label { get => _label; set { _label = value ?? ""; toolbar?.itemChanged(); } }
    public NSImage image { get => _image; set { _image = value; toolbar?.itemChanged(); } }
    internal bool isSpace => itemIdentifier is Identifier.flexibleSpace or Identifier.space;
}

/// <summary>
/// The window's title bar (AppKit's theme frame): drawn over the top of a full-size content view, 28 points high, 52
/// with a visible toolbar. Measured from the macOS game's window style (macOS 13 SDK, tools/scenekit-reference/WindowChrome.swift):
/// an opaque (236, 235, 234) bar; the title centred (13 pt bold, baseline 18) or, with the toolbar, left at x 92 after
/// the traffic lights (15 pt semibold, baseline 30); toolbar items right-aligned, 14 points apart and 15 from the right edge,
/// each its label's width (11 pt) with the icon centred above (icon centre y 21, label baseline 46). Inactive-window
/// colours are measured (title 172, icons and labels 177); active colours are AppKit's label colours (not capturable
/// here). SF Symbols are drawn as vector stand-ins. On macOS the system draws the traffic lights (Godot's
/// extend_to_title) and the title is drawn here; elsewhere the system title bar shows the title and this bar holds
/// the menu bar on the left.
/// </summary>
internal sealed partial class NSWindowFrame : NSView
{
    private readonly NSWindow owner;
    private NSToolbarItem pressed;
    private bool pressedInside;
    private Control menuBarHost;
    private bool wasKey = true;
    internal static readonly bool drawsTitle = OS.GetName() == "macOS";
    public override bool isFlipped => true;

    internal NSWindowFrame(NSWindow owner)
    {
        this.owner = owner;
        Name = "Titlebar";
        MouseFilter = MouseFilterEnum.Stop;
        owner.godotWindow.SizeChanged += updateFrame;
        owner.godotWindow.FocusEntered += () => { QueueRedraw(); owner.@delegate?.windowDidBecomeKey(new Notification("NSWindowDidBecomeKeyNotification", owner)); };
        owner.godotWindow.FocusExited += () => { QueueRedraw(); owner.@delegate?.windowDidResignKey(new Notification("NSWindowDidResignKeyNotification", owner)); };
        updateFrame();
    }

    internal double height => owner.titlebarHeight;
    private bool unified => owner.toolbar is { isVisible: true };
    /// <summary>Draw the inactive window's colours (the AppKit reference captures are of an inactive window).</summary>
    internal bool forceInactive;
    private bool active => !forceInactive && (DisplayServer.GetName() == "headless" || owner.godotWindow.HasFocus());

    private void updateFrame()
    {
        var size = owner.godotWindow.GetVisibleRect().Size;
        frame = new CGRect(0, 0, size.X, height);
        if (menuBarHost != null) layoutMenuBar();
        if (OS.GetName() == "macOS" && DisplayServer.GetName() != "headless")
        {
            // Centre of the first traffic light, measured: (16, 14) in the 28-point bar, (26, 26) with the toolbar.
            var offset = (unified ? new Vector2(26, 26) : new Vector2(16, 14)) * (float)owner.backingScaleFactor;
            DisplayServer.WindowSetWindowButtonsOffset((Vector2I)offset.Round(), (int)owner.godotWindow.GetWindowId());
        }
        QueueRedraw();
    }
    internal void toolbarChanged() => updateFrame();
    internal void needsDisplayTitle() => QueueRedraw();

    internal void installMenuBar(MenuBar menuBar)
    {
        menuBarHost?.QueueFree();
        menuBarHost = new Control { Name = "MenuBarHost", MouseFilter = MouseFilterEnum.Pass };
        AddChild(menuBarHost);
        menuBarHost.AddChild(menuBar);
        layoutMenuBar();
    }
    private void layoutMenuBar()
    {
        var h = (float)Math.Min(height, NSWindow.TitlebarHeight);
        menuBarHost.Position = new Vector2(8, (float)(height - h) / 2);
        menuBarHost.Size = new Vector2(Math.Max(0, Size.X / 2), h);
        if (menuBarHost.GetChildCount() > 0 && menuBarHost.GetChild(0) is Control bar) { bar.Position = Vector2.Zero; bar.Size = menuBarHost.Size; }
    }

    public override void _Process(double delta)
    {
        owner.pollState();
        bool key = active;
        if (key != wasKey) { wasKey = key; QueueRedraw(); }
    }

    // ---- Layout of the toolbar items (right to left)
    private static readonly NSFont labelFont = NSFont.systemFont(11);
    private const double itemGap = 14, rightMargin = 15, flexibleSpaceGap = 1, iconCenterY = 21, labelBaseline = 46, minimumItemWidth = 24;
    private List<(NSToolbarItem item, double center, double width)> itemLayout()
    {
        var list = new List<(NSToolbarItem, double, double)>();
        if (!unified) return list;
        double x = bounds.width - rightMargin;
        foreach (var item in owner.toolbar.items.Reverse())
        {
            if (item.isSpace) { x -= flexibleSpaceGap; continue; }
            double w = Math.Max(minimumItemWidth, Math.Ceiling(item.label.size(new() { [NSAttributedString.Key.font] = labelFont }).width));
            list.Add((item, x - w / 2, w));
            x -= w + itemGap;
        }
        return list;
    }
    /// <summary>The visible items with their centre x and width (for scripted clicks).</summary>
    internal List<(NSToolbarItem item, double center, double width)> itemLayoutForTests() => itemLayout();
    private NSToolbarItem itemAt(CGPoint p)
    {
        foreach (var (item, center, width) in itemLayout())
            if (Math.Abs(p.x - center) <= width / 2 + itemGap / 2 && p.y >= 2 && p.y <= height - 2) return item;
        return null;
    }

    // ---- Drawing
    private static NSColor gray(double v, double a = 1) => NSColor.srgbRed(v / 255, v / 255, v / 255, a);
    public override void draw(CGRect dirtyRect)
    {
        NSColor.srgbRed(236 / 255.0, 235 / 255.0, 234 / 255.0, 1).setFill();
        new NSBezierPath(bounds).fill();
        bool key = active;
        if (drawsTitle && !string.IsNullOrEmpty(owner.title))
        {
            var font = unified ? NSFont.systemFont(15, NSFont.Weight.semibold) : NSFont.boldSystemFont(13);
            var attributes = new Dictionary<NSAttributedString.Key, object> { [NSAttributedString.Key.font] = font, [NSAttributedString.Key.foregroundColor] = key ? NSColor.srgbRed(0, 0, 0, 0.85) : gray(172) };
            var width = owner.title.size(attributes).width;
            double baseline = unified ? 30 : 18;
            double x = unified ? 91 : Math.Round((bounds.width - width) / 2);
            owner.title.draw(new CGPoint(x, baseline - font.ascender), attributes);
        }
        foreach (var (item, center, width) in itemLayout())
        {
            bool down = item == pressed && pressedInside;
            var ink = !key ? gray(177) : down ? NSColor.srgbRed(0, 0, 0, 0.85) : NSColor.srgbRed(0, 0, 0, 0.6);
            if (item.image?.symbolName is string symbol) drawSymbol(symbol, new CGPoint(Math.Round(center), iconCenterY), ink);
            var attributes = new Dictionary<NSAttributedString.Key, object> { [NSAttributedString.Key.font] = labelFont, [NSAttributedString.Key.foregroundColor] = ink };
            var size = item.label.size(attributes);
            item.label.draw(new CGPoint(center - size.width / 2, labelBaseline - labelFont.ascender), attributes);
        }
    }

    /// <summary>Vector stand-ins for the SF Symbols the game's toolbar uses (about 17-point regular glyphs).</summary>
    private static void drawSymbol(string name, CGPoint c, NSColor ink)
    {
        ink.setStroke(); ink.setFill();
        double x = c.x, y = c.y;
        NSBezierPath stroked(double width = 1.5) => new() { lineWidth = width, lineCapStyle = NSBezierPath.LineCapStyle.round, lineJoinStyle = NSBezierPath.LineJoinStyle.round };
        CGPoint P(double dx, double dy) => new(x + dx, y + dy);
        switch (name)
        {
            case "house":
            {
                var roof = stroked(); roof.move(P(-11, -0.5)); roof.line(P(0, -10)); roof.line(P(11, -0.5)); roof.stroke();
                var chimney = stroked(); chimney.move(P(6.5, -5.5)); chimney.line(P(6.5, -8.5)); chimney.stroke();
                var body = stroked(); body.move(P(-7.5, -3)); body.line(P(-7.5, 9.5)); body.line(P(7.5, 9.5)); body.line(P(7.5, -3)); body.stroke();
                NSBezierPath.roundedRect(new CGRect(x - 2.5, y + 3, 5, 6.5), 0.8, 0.8).fill();
                break;
            }
            case "video":
            {
                var body = NSBezierPath.roundedRect(new CGRect(x - 11.5, y - 7, 16, 14), 3, 3); body.lineWidth = 1.5; body.stroke();
                var lens = stroked(); lens.move(P(4.5, -2)); lens.line(P(11.5, -6)); lens.line(P(11.5, 6)); lens.line(P(4.5, 2)); lens.close(); lens.stroke();
                break;
            }
            case "pause.fill":
                NSBezierPath.roundedRect(new CGRect(x - 6, y - 8, 4.5, 16), 1.2, 1.2).fill();
                NSBezierPath.roundedRect(new CGRect(x + 1.5, y - 8, 4.5, 16), 1.2, 1.2).fill();
                break;
            case "play.fill":
            {
                var play = stroked(1); play.move(P(-5, -7.5)); play.line(P(7, 0)); play.line(P(-5, 7.5)); play.close(); play.fill(); play.stroke();
                break;
            }
            case "arrow.counterclockwise":
            {
                double r = 8, cy = y + 1.5;
                var arc = stroked();
                const int n = 40; double from = 80, to = -190;
                for (int i = 0; i <= n; i++)
                {
                    double t = (from + (to - from) * i / n) * Math.PI / 180;
                    var p = new CGPoint(x + r * Math.Cos(t), cy - r * Math.Sin(t));
                    if (i == 0) arc.move(p); else arc.line(p);
                }
                arc.stroke();
                // Arrowhead at the open end (top), pointing counterclockwise (left).
                double a = from * Math.PI / 180;
                var tip = new CGPoint(x + r * Math.Cos(a) - 2.5, cy - r * Math.Sin(a));
                var head = stroked(); head.move(new CGPoint(tip.x + 4, tip.y - 3.8)); head.line(tip); head.line(new CGPoint(tip.x + 4, tip.y + 3.8)); head.stroke();
                break;
            }
            case "keyboard":
            {
                var body = NSBezierPath.roundedRect(new CGRect(x - 12.5, y - 7.5, 25, 15), 2.5, 2.5); body.lineWidth = 1.4; body.stroke();
                for (int row = 0; row < 3; row++)
                    for (int k = 0; k < 9; k++)
                        new NSBezierPath(new CGRect(x - 9.5 + k * 2.3, y - 4.5 + row * 2.6, 1.4, 1.4)).fill();
                var space = stroked(1.4); space.move(P(-5.5, 4.5)); space.line(P(5.5, 4.5)); space.stroke();
                break;
            }
            default:
                NSBezierPath.ovalIn(new CGRect(x - 7, y - 7, 14, 14)).stroke();
                break;
        }
    }

    // ---- Events
    public override void mouseDown(NSEvent @event)
    {
        var p = convertFrom(@event.locationInWindow, null);
        pressed = itemAt(p);
        pressedInside = pressed != null;
        if (pressed != null) { QueueRedraw(); return; }
        if (DisplayServer.GetName() == "headless") return;
        // The title bar moves the window (macOS: the system title bar is hidden by extend_to_title) and zooms on a double click.
        if (OS.GetName() == "macOS")
        {
            var id = (int)owner.godotWindow.GetWindowId();
            if (@event.clickCount >= 2)
                DisplayServer.WindowSetMode(DisplayServer.WindowGetMode(id) == DisplayServer.WindowMode.Maximized ? DisplayServer.WindowMode.Windowed : DisplayServer.WindowMode.Maximized, id);
            else DisplayServer.WindowStartDrag(id);
        }
    }
    public override void mouseDragged(NSEvent @event)
    {
        if (pressed == null) return;
        bool inside = itemAt(convertFrom(@event.locationInWindow, null)) == pressed;
        if (inside != pressedInside) { pressedInside = inside; QueueRedraw(); }
    }
    public override void mouseUp(NSEvent @event)
    {
        var item = pressed;
        bool inside = item != null && itemAt(convertFrom(@event.locationInWindow, null)) == item;
        pressed = null; pressedInside = false; QueueRedraw();
        if (inside && item.isEnabled) item.action?.Invoke(item);
    }
    public override string _GetTooltip(Vector2 atPosition) => itemAt(new CGPoint(atPosition.X, atPosition.Y))?.toolTip ?? "";
}
