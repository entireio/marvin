using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Marvin.SceneKit;

/// <summary>
/// NSView as a Godot Control. AppKit geometry: <c>frame</c> is in the superview's coordinate system with the
/// origin at the bottom left unless the superview is flipped (the facade maps it to Godot's top-left
/// Position and keeps it when the superview resizes, honouring <c>autoresizingMask</c>); <c>bounds</c> and
/// drawing use the view's own space (y up unless <c>isFlipped</c>). <c>draw(_:)</c> runs from Godot's _Draw
/// with an NSGraphicsContext on the canvas backend (layer background first, then the view's drawing);
/// <c>needsDisplay = true</c> queues a redraw. <c>layout()</c> runs (deferred, before the frame is drawn)
/// after the size changes or <c>needsLayout = true</c>.
/// Events: Godot GUI input becomes NSEvents for keyDown/keyUp/flagsChanged, mouse and scroll methods; key
/// events go to the first responder (Godot's focus owner, see NSWindow.makeFirstResponder) and, like
/// mouse events the default implementations pass on, continue to the superview. <c>hitTest(_:)</c> decides
/// whether the view receives the mouse (returning nil lets clicks through). <c>acceptsFirstResponder</c> maps
/// to the focus mode; focus loss calls <c>resignFirstResponder()</c>. Tracking areas deliver mouseEntered/Exited.
/// <c>cacheDisplay(in:to:)</c> renders the view hierarchy's AppKit drawing into a bitmap through an
/// offscreen viewport (SceneKit content is not captured, as in AppKit).
/// </summary>
public partial class NSView : Control, NSResponder
{
    [Flags] public enum AutoresizingMask { none = 0, minXMargin = 1, width = 2, maxXMargin = 4, minYMargin = 8, height = 16, maxYMargin = 32 }

    private CGRect _frame;
    private CALayer _layer;
    private bool _wantsLayer, _needsLayout = true, layoutQueued;
    private Vector2 lastSize = new(-1, -1);
    private readonly List<NSTrackingArea> _trackingAreas = new();
    internal NSGraphicsCanvas drawing;
    private NSAccessibility.Role accessibilityRole = NSAccessibility.Role.unknown;
    private object accessibilityValue;
    public AutoresizingMask autoresizingMask = AutoresizingMask.none;
    public bool translatesAutoresizingMaskIntoConstraints = true;

    public NSView() : this(CGRect.zero) { }
    public NSView(CGRect frame)
    {
        MouseFilter = MouseFilterEnum.Pass;
        FocusMode = FocusModeEnum.None;
        _frame = frame;
        Size = new Vector2((float)frame.width, (float)frame.height);
        Position = new Vector2((float)frame.minX, (float)frame.minY);
    }

    // ---- Geometry
    /// <summary>frame: in the superview's coordinates (origin bottom left unless the superview is flipped).</summary>
    public CGRect frame
    {
        get => _frame;
        set { _frame = value; AppKitLayout.Apply(this, value); }
    }
    public CGRect bounds => new(0, 0, _frame.width, _frame.height);
    /// <summary>isFlipped: the view's own y axis grows downwards.</summary>
    public virtual bool isFlipped => false;
    public NSView superview => GetParent() as NSView;
    /// <summary>subviews: child NSViews, SCNViews and other controls, back to front.</summary>
    public List<Control> subviews => GetChildren().OfType<Control>().ToList();
    public void addSubview(Control view)
    {
        if (view.GetParent() is Node old) old.RemoveChild(view);
        AddChild(view);
        AppKitLayout.Reapply(view);
    }
    public void removeFromSuperview() => GetParent()?.RemoveChild(this);
    public NSWindow window => IsInsideTree() ? NSWindow.Of(GetWindow()) : null;
    public bool isHidden { get => !Visible; set => Visible = !value; }
    public string toolTip { get => string.IsNullOrEmpty(TooltipText) ? null : TooltipText; set => TooltipText = value ?? ""; }
    public bool wantsLayer
    {
        get => _wantsLayer;
        set { _wantsLayer = value; if (value) _layer ??= new CALayer(this); QueueRedraw(); }
    }
    /// <summary>layer: exists once wantsLayer is set (backgroundColor and cornerRadius are drawn behind draw(_:)).</summary>
    public CALayer layer => _wantsLayer ? _layer : null;

    // ---- Display and layout
    public bool needsDisplay { get => false; set { if (value) QueueRedraw(); } }
    public void display() => QueueRedraw();
    public void displayIfNeeded() { }
    public void setNeedsDisplay(CGRect rect) => QueueRedraw();
    /// <summary>draw(_:): AppKit drawing into NSGraphicsContext.current (the view's canvas).</summary>
    public virtual void draw(CGRect dirtyRect) { }
    public bool needsLayout
    {
        get => _needsLayout;
        set
        {
            _needsLayout = value;
            if (value && !layoutQueued) { layoutQueued = true; Callable.From(DeferredLayout).CallDeferred(); }
        }
    }
    private void DeferredLayout() { layoutQueued = false; if (IsInstanceValid(this)) LayoutSubtree(); }
    /// <summary>layout(): subclasses position their subviews; called when the size changed or needsLayout was set.</summary>
    public virtual void layout() { }
    /// <summary>
    /// layoutSubtreeIfNeeded(): AppKit's layout pass for this view: its own Auto Layout constraints win over a frame
    /// assigned directly (measured on the Mac: the main menu overlay's 900x550 frame reverts to the content layout guide),
    /// then layout() wherever it is needed in the subtree. Display caching (cacheDisplay) and the deferred layout after a
    /// resize lay out the subtree without the constraint pass, as AppKit's display does (the race HUD keeps a 900x550
    /// frame through RaceFinishSmoke's captures).
    /// </summary>
    public void layoutSubtreeIfNeeded()
    {
        if (window is NSWindow w && w.constraints.Count > 0) NSLayoutConstraint.Solve(w, this);
        LayoutSubtree();
    }
    internal void LayoutSubtree()
    {
        if (_needsLayout) { _needsLayout = false; layout(); QueueRedraw(); }
        foreach (var child in GetChildren())
            if (child is NSView v) v.LayoutSubtree();
            else if (child is SCNView s) foreach (var c in s.GetChildren()) if (c is NSView sv) sv.LayoutSubtree();
    }
    /// <summary>resizeSubviews(withOldSize:): applies each subview's autoresizingMask.</summary>
    public virtual void resizeSubviews(CGSize withOldSize)
    {
        foreach (var child in GetChildren()) if (child is NSView v) v.resizeWithOldSuperviewSize(withOldSize);
    }
    public virtual void resizeWithOldSuperviewSize(CGSize oldSize)
    {
        // Auto Layout views ignore their autoresizing mask; the window's constraints place them (NSLayoutConstraint.Solve).
        if (!translatesAutoresizingMaskIntoConstraints) return;
        if (GetParent() is not Control parent) return;
        var f = AppKitLayout.Autoresize(_frame, autoresizingMask, oldSize, new CGSize(parent.Size.X, parent.Size.Y));
        if (f != _frame) frame = f;
    }

    public override void _Draw()
    {
        drawing ??= new NSGraphicsCanvas(GetCanvasItem());
        drawing.Reset();
        NSViewRendering.Paint(this, drawing, Vector2.Zero);
    }
    /// <summary>Layer background (wantsLayer): filled behind the view's own drawing.</summary>
    internal void DrawLayer()
    {
        if (!_wantsLayer || _layer?.backgroundColor is not NSColor background) return;
        background.setFill();
        (_layer.cornerRadius > 0 ? NSBezierPath.roundedRect(bounds, _layer.cornerRadius, _layer.cornerRadius) : new NSBezierPath(bounds)).fill();
    }

    public override void _Notification(int what)
    {
        switch ((long)what)
        {
            case NotificationEnterTree:
                FocusMode = acceptsFirstResponder ? FocusModeEnum.All : FocusModeEnum.None;
                AppKitLayout.Reapply(this);
                needsLayout = true;
                updateTrackingAreas();
                resetCursorRects();
                break;
            case NotificationResized:
                if (Size != lastSize)
                {
                    var old = new CGSize(Math.Max(0, lastSize.X), Math.Max(0, lastSize.Y));
                    lastSize = Size;
                    if (old.width > 0 || old.height > 0) resizeSubviews(old);
                    AppKitLayout.ReapplyChildren(this);
                    // AppKit's layout pass follows the superview's resize: re-solve Auto Layout subviews against the new size.
                    if (IsInsideTree() && GetChildren().Any(c => c is NSView { translatesAutoresizingMaskIntoConstraints: false })) window?.layoutConstraints();
                    needsLayout = true;
                    QueueRedraw();
                    if (IsInsideTree()) { updateTrackingAreas(); resetCursorRects(); }
                }
                break;
            case NotificationFocusEnter: becomeFirstResponder(); break;
            case NotificationFocusExit: resignFirstResponder(); break;
            case NotificationMouseEnter:
                if (_trackingAreas.Any(a => (a.options & NSTrackingArea.Options.mouseEnteredAndExited) != 0)) mouseEntered(TrackingEvent(NSEvent.EventType.mouseEntered));
                break;
            case NotificationMouseExit:
                if (_trackingAreas.Any(a => (a.options & NSTrackingArea.Options.mouseEnteredAndExited) != 0)) mouseExited(TrackingEvent(NSEvent.EventType.mouseExited));
                break;
            case NotificationAccessibilityUpdate:
                var element = GetAccessibilityElement();
                if (!element.IsValid) break;
                if (accessibilityRole != NSAccessibility.Role.unknown) AccessibilityServer.UpdateSetRole(element, NSAccessibility.GodotRole(accessibilityRole));
                if (accessibilityValue is double number) { AccessibilityServer.UpdateSetNumRange(element, 0, 1); AccessibilityServer.UpdateSetNumValue(element, number); }
                break;
        }
    }
    private NSEvent TrackingEvent(NSEvent.EventType type)
    {
        var p = GetGlobalMousePosition();
        float h = GetViewport()?.GetVisibleRect().Size.Y ?? 0;
        return NSEvent.mouseEvent(type, new CGPoint(p.X, h - p.Y), 0, Time.GetTicksUsec() / 1e6, window?.windowNumber ?? 0, null, 0, 0, 0);
    }

    // ---- Coordinates
    /// <summary>Godot local pixel position of a point in the view's own (AppKit) coordinates.</summary>
    internal Vector2 ToGodotLocal(CGPoint p) => new((float)p.x, (float)(isFlipped ? p.y : _frame.height - p.y));
    internal CGPoint ToAppKitLocal(Vector2 p) => new(p.X, isFlipped ? p.Y : _frame.height - p.Y);
    /// <summary>convert(_:to:): to another view's coordinates, or window coordinates (origin bottom left) for nil.</summary>
    public CGPoint convert(CGPoint point, Control to) => AppKitLayout.FromGlobal(GetGlobalTransform() * ToGodotLocal(point), to, this);
    /// <summary>convert(_:from:).</summary>
    public CGPoint convertFrom(CGPoint point, Control from) => ToAppKitLocal(GetGlobalTransform().AffineInverse() * AppKitLayout.ToGlobal(point, from, this));

    // ---- Hit testing
    private static readonly Dictionary<Type, bool> overridesHitTest = new();
    /// <summary>hitTest(_:): point in the superview's coordinates. Returning nil lets the mouse through.</summary>
    public virtual NSView hitTest(CGPoint point) => !isHidden && _frame.contains(point) ? this : null;
    public override bool _HasPoint(Vector2 point)
    {
        var type = GetType();
        if (!overridesHitTest.TryGetValue(type, out var overrides))
            overridesHitTest[type] = overrides = type.GetMethod(nameof(hitTest), new[] { typeof(CGPoint) })?.DeclaringType != typeof(NSView);
        // Godot's C# base _HasPoint returns false instead of the native rectangle test, so test the rectangle here.
        if (!overrides) return point.X >= 0 && point.Y >= 0 && point.X < Size.X && point.Y < Size.Y;
        var local = ToAppKitLocal(point);
        bool superFlipped = GetParent() is NSView s && s.isFlipped;
        // Superview coordinates: frame origin plus the point measured along the superview's y direction.
        double y = superFlipped == isFlipped ? _frame.minY + local.y : _frame.minY + (_frame.height - local.y);
        return hitTest(new CGPoint(_frame.minX + local.x, y)) != null;
    }

    // ---- Responder
    public virtual bool acceptsFirstResponder => false;
    public virtual bool becomeFirstResponder() => true;
    public virtual bool resignFirstResponder() => true;
    public virtual void keyDown(NSEvent @event) => @event.passedToNextResponder = true;
    public virtual void keyUp(NSEvent @event) => @event.passedToNextResponder = true;
    public virtual void flagsChanged(NSEvent @event) => @event.passedToNextResponder = true;
    public virtual void mouseDown(NSEvent @event) => @event.passedToNextResponder = true;
    public virtual void mouseUp(NSEvent @event) => @event.passedToNextResponder = true;
    public virtual void mouseDragged(NSEvent @event) => @event.passedToNextResponder = true;
    public virtual void mouseMoved(NSEvent @event) => @event.passedToNextResponder = true;
    public virtual void mouseEntered(NSEvent @event) => @event.passedToNextResponder = true;
    public virtual void mouseExited(NSEvent @event) => @event.passedToNextResponder = true;
    public virtual void rightMouseDown(NSEvent @event) => @event.passedToNextResponder = true;
    public virtual void rightMouseUp(NSEvent @event) => @event.passedToNextResponder = true;
    public virtual void rightMouseDragged(NSEvent @event) => @event.passedToNextResponder = true;
    public virtual void otherMouseDown(NSEvent @event) => @event.passedToNextResponder = true;
    public virtual void otherMouseUp(NSEvent @event) => @event.passedToNextResponder = true;
    public virtual void otherMouseDragged(NSEvent @event) => @event.passedToNextResponder = true;
    public virtual void scrollWheel(NSEvent @event) => @event.passedToNextResponder = true;
    public virtual void magnify(NSEvent @event) => @event.passedToNextResponder = true;
    public override void _GuiInput(InputEvent @event)
    {
        if (NSEvent.Dispatch(this, this, @event)) AcceptEvent();
    }

    // ---- Tracking areas and cursor rects
    public NSTrackingArea[] trackingAreas => _trackingAreas.ToArray();
    public void addTrackingArea(NSTrackingArea area) => _trackingAreas.Add(area);
    public void removeTrackingArea(NSTrackingArea area) => _trackingAreas.Remove(area);
    /// <summary>updateTrackingAreas(): called when the view enters a window and when its size changes.</summary>
    public virtual void updateTrackingAreas() { }
    public virtual void resetCursorRects() { }
    /// <summary>addCursorRect(_:cursor:). PORT: the cursor applies to the whole view (Godot has one cursor shape per control).</summary>
    public void addCursorRect(CGRect rect, NSCursor cursor) => MouseDefaultCursorShape = cursor.shape;

    // ---- Accessibility
    public void setAccessibilityLabel(string label) => AccessibilityName = label ?? "";
    public void setAccessibilityHelp(string help) => AccessibilityDescription = help ?? "";
    public void setAccessibilityValue(object value) { accessibilityValue = value; QueueAccessibilityUpdate(); }
    public void setAccessibilityRole(NSAccessibility.Role role) { accessibilityRole = role; QueueAccessibilityUpdate(); }

    // ---- Capturing
    /// <summary>bitmapImageRepForCachingDisplay(in:): an RGBA bitmap of the rectangle's size (one pixel per point).</summary>
    public NSBitmapImageRep bitmapImageRepForCachingDisplay(CGRect @in) =>
        new(null, Math.Max(1, (int)Math.Ceiling(@in.width)), Math.Max(1, (int)Math.Ceiling(@in.height)), 8, 4, true, false, NSColorSpaceName.deviceRGB, 0, 32, NSBitmapFormat.alphaNonpremultiplied);
    /// <summary>cacheDisplay(in:to:): draws this view and its subviews (AppKit drawing only) into the bitmap.</summary>
    public void cacheDisplay(CGRect @in, NSBitmapImageRep to) => NSViewRendering.Capture(this, @in, to);

    protected override void Dispose(bool disposing)
    {
        if (disposing) drawing?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>CALayer properties a layer-backed NSView draws: backgroundColor (an NSColor; cgColor is the colour itself) and cornerRadius.</summary>
public sealed class CALayer
{
    private readonly CanvasItem owner;
    private NSColor _backgroundColor;
    private double _cornerRadius;
    internal CALayer(CanvasItem owner) { this.owner = owner; }
    public NSColor backgroundColor { get => _backgroundColor; set { _backgroundColor = value; owner.QueueRedraw(); } }
    public double cornerRadius { get => _cornerRadius; set { _cornerRadius = value; owner.QueueRedraw(); } }
    public bool masksToBounds;
}

/// <summary>NSTrackingArea(rect:options:owner:userInfo:). The facade tracks the whole view (inVisibleRect).</summary>
public sealed class NSTrackingArea
{
    [Flags]
    public enum Options
    {
        mouseEnteredAndExited = 0x01, mouseMoved = 0x02, cursorUpdate = 0x04, activeWhenFirstResponder = 0x10, activeInKeyWindow = 0x20,
        activeInActiveApp = 0x40, activeAlways = 0x80, assumeInside = 0x100, inVisibleRect = 0x200, enabledDuringMouseDrag = 0x400,
    }
    public readonly CGRect rect;
    public readonly Options options;
    public readonly object owner, userInfo;
    public NSTrackingArea(CGRect rect, Options options, object owner, object userInfo) { this.rect = rect; this.options = options; this.owner = owner; this.userInfo = userInfo; }
}

/// <summary>NSAccessibility.Role values the app sets, mapped to Godot's accessibility roles.</summary>
public static class NSAccessibility
{
    public enum Role { unknown, button, staticText, progressIndicator, group, image, textField, slider, checkBox, popUpButton, window }
    internal static AccessibilityServer.AccessibilityRole GodotRole(Role role) => role switch
    {
        Role.button => AccessibilityServer.AccessibilityRole.Button,
        Role.staticText => AccessibilityServer.AccessibilityRole.StaticText,
        Role.progressIndicator => AccessibilityServer.AccessibilityRole.ProgressIndicator,
        Role.group => AccessibilityServer.AccessibilityRole.Container,
        Role.image => AccessibilityServer.AccessibilityRole.Image,
        Role.textField => AccessibilityServer.AccessibilityRole.TextField,
        Role.slider => AccessibilityServer.AccessibilityRole.Slider,
        Role.checkBox => AccessibilityServer.AccessibilityRole.CheckBox,
        Role.window => AccessibilityServer.AccessibilityRole.Window,
        _ => AccessibilityServer.AccessibilityRole.Unknown,
    };
}

/// <summary>AppKit frame bookkeeping shared by NSView and SCNView (children of either).</summary>
internal static class AppKitLayout
{
    private static bool Flipped(Node parent) => parent is NSView v && v.isFlipped;
    /// <summary>Positions a control from an AppKit frame in its parent's coordinates.</summary>
    internal static void Apply(Control child, CGRect frame)
    {
        var parent = child.GetParent() as Control;
        double y = parent == null || Flipped(parent) ? frame.minY : parent.Size.Y - frame.maxY;
        child.Position = new Vector2((float)frame.minX, (float)y);
        child.Size = new Vector2((float)frame.width, (float)frame.height);
    }
    internal static void Reapply(Control child)
    {
        if (child is NSView v) Apply(v, v.frame);
    }
    internal static void ReapplyChildren(Control parent)
    {
        foreach (var child in parent.GetChildren()) if (child is NSView v) Apply(v, v.frame);
    }
    /// <summary>Springs and struts: the superview's size change is shared among the flexible margins and size, in proportion to their current values.</summary>
    internal static CGRect Autoresize(CGRect f, NSView.AutoresizingMask mask, CGSize oldSize, CGSize newSize)
    {
        (double lo, double size) Axis(double lo, double size, double hi, bool flexLo, bool flexSize, bool flexHi, double delta)
        {
            double total = (flexLo ? lo : 0) + (flexSize ? size : 0) + (flexHi ? hi : 0);
            int count = (flexLo ? 1 : 0) + (flexSize ? 1 : 0) + (flexHi ? 1 : 0);
            if (count == 0 || delta == 0) return (lo, size);
            double share(double v) => total > 0 ? delta * v / total : delta / count;
            return (lo + (flexLo ? share(lo) : 0), size + (flexSize ? share(size) : 0));
        }
        var (x, w) = Axis(f.minX, f.width, oldSize.width - f.maxX, mask.HasFlag(NSView.AutoresizingMask.minXMargin), mask.HasFlag(NSView.AutoresizingMask.width), mask.HasFlag(NSView.AutoresizingMask.maxXMargin), newSize.width - oldSize.width);
        var (y, h) = Axis(f.minY, f.height, oldSize.height - f.maxY, mask.HasFlag(NSView.AutoresizingMask.minYMargin), mask.HasFlag(NSView.AutoresizingMask.height), mask.HasFlag(NSView.AutoresizingMask.maxYMargin), newSize.height - oldSize.height);
        return new CGRect(x, y, Math.Max(0, w), Math.Max(0, h));
    }
    /// <summary>A global (viewport) position in another view's AppKit coordinates, or window coordinates for null.</summary>
    internal static CGPoint FromGlobal(Vector2 global, Control to, Control self)
    {
        if (to == null)
        {
            float h = self.GetViewport()?.GetVisibleRect().Size.Y ?? 0;
            return new CGPoint(global.X, h - global.Y);
        }
        var local = to.GetGlobalTransform().AffineInverse() * global;
        return to is NSView v ? v.ToAppKitLocal(local) : new CGPoint(local.X, to.Size.Y - local.Y);
    }
    internal static Vector2 ToGlobal(CGPoint point, Control from, Control self)
    {
        if (from == null)
        {
            float h = self.GetViewport()?.GetVisibleRect().Size.Y ?? 0;
            return new Vector2((float)point.x, (float)(h - point.y));
        }
        var local = from is NSView v ? v.ToGodotLocal(point) : new Vector2((float)point.x, (float)(from.Size.Y - point.y));
        return from.GetGlobalTransform() * local;
    }
}

/// <summary>
/// Paints NSView hierarchies with the canvas backend: a view's own drawing (Paint, used by _Draw) and
/// whole-hierarchy captures for cacheDisplay(in:to:), rendered in an offscreen SubViewport and read back
/// (un-premultiplied: Godot's transparent viewports hold premultiplied colour).
/// </summary>
internal static class NSViewRendering
{
    /// <summary>Draws one view (layer background, then draw(bounds)) with its top-left corner at the canvas offset.</summary>
    internal static void Paint(NSView view, NSGraphicsCanvas canvas, Vector2 offset)
    {
        var size = view.Size;
        var baseTransform = view.isFlipped
            ? new AffineTransform(1, 0, 0, 1, offset.X, offset.Y)
            : new AffineTransform(1, 0, 0, -1, offset.X, offset.Y + size.Y);
        var ctx = new NSGraphicsContext(canvas, view.isFlipped, baseTransform);
        var previous = NSGraphicsContext.current;
        NSGraphicsContext.current = ctx;
        NSGraphicsContext.saveGraphicsState();
        try { view.DrawLayer(); view.draw(view.bounds); }
        catch (Exception e) { GD.PushError($"{view.GetType().Name}.draw failed: {e}"); }
        finally { NSGraphicsContext.restoreGraphicsState(); NSGraphicsContext.current = previous; }
    }
    private static void PaintTree(Control control, NSGraphicsCanvas canvas, Vector2 offset)
    {
        if (!control.Visible) return;
        if (control is NSView view) Paint(view, canvas, offset);
        foreach (var child in control.GetChildren())
            if (child is Control c) PaintTree(c, canvas, offset + c.Position);
    }
    internal static void Capture(NSView view, CGRect rect, NSBitmapImageRep to)
    {
        if (!SceneKitRuntime.OnMainThread) throw new InvalidOperationException("cacheDisplay must run on the main thread");
        view.LayoutSubtree();
        int w = to.pixelsWide, h = to.pixelsHigh;
        var viewport = new SubViewport
        {
            Size = new Vector2I(w, h), TransparentBg = true, Disable3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Once, RenderTargetClearMode = SubViewport.ClearMode.Always,
        };
        var host = SceneKitRuntime.Host as Node ?? (Engine.GetMainLoop() as SceneTree)?.Root;
        host.AddChild(viewport);
        var item = RenderingServer.CanvasItemCreate();
        RenderingServer.CanvasItemSetParent(item, viewport.FindWorld2D().Canvas);
        var canvas = new NSGraphicsCanvas(item);
        try
        {
            // The rectangle is in the view's own coordinates; its top-left corner maps to the bitmap's origin.
            var topLeft = view.ToGodotLocal(new CGPoint(rect.minX, view.isFlipped ? rect.minY : rect.maxY));
            PaintTree(view, canvas, -topLeft);
            RenderingServer.ForceDraw(false, 0.0);
            var image = SceneKitRuntime.ViewportImage(viewport, new Vector2I(w, h));
            if (image.GetFormat() != Image.Format.Rgba8) image.Convert(Image.Format.Rgba8);
            var data = image.GetData();
            bool premultipliedTarget = (to.bitmapFormat & NSBitmapFormat.alphaNonpremultiplied) == 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4, o = y * to.bytesPerRow + x * to.bitsPerPixel / 8;
                    byte a = data[i + 3];
                    for (int k = 0; k < 3; k++)
                    {
                        int v = data[i + k];
                        // The viewport holds premultiplied colour; a non-premultiplied target is divided back.
                        to.bitmapData[o + k] = premultipliedTarget || a == 0 ? (byte)(a == 0 ? 0 : v) : (byte)Math.Min(255, (int)Math.Round(v * 255.0 / a));
                    }
                    if (to.samplesPerPixel > 3) to.bitmapData[o + 3] = a;
                }
            to.version++;
        }
        finally
        {
            canvas.Dispose();
            RenderingServer.FreeRid(item);
            host.RemoveChild(viewport);
            viewport.Free();
        }
    }
}

/// <summary>NSBitmapImageRep drawing into the current context (bitmap.draw(in:)).</summary>
public static class NSBitmapImageRepDrawing
{
    public static void draw(this NSBitmapImageRep rep, CGRect @in) => NSGraphicsContext.current?.DrawImage(rep.cgImage, @in, 1);
}
