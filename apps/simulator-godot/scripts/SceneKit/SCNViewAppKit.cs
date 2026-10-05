using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Marvin.SceneKit;

/// <summary>
/// SCNView's NSView/NSResponder side (SCNView is an NSView subclass in AppKit; in the facade it is a
/// SubViewportContainer, so these members mirror NSView's): input as NSEvents (see NSEvent.Dispatch),
/// first responder, overlay subviews with AppKit frames, cursor rects, coordinate conversion.
/// SCNView accepts first responder by default, like AppKit's. <c>frame</c> stays in Godot pixels (see SCNView.cs).
/// </summary>
public partial class SCNView : NSResponder
{
    private Vector2 lastAppKitSize = new(-1, -1);
    public virtual bool acceptsFirstResponder => true;
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

    public NSWindow window => IsInsideTree() ? NSWindow.Of(GetWindow()) : null;
    public NSView superview => GetParent() as NSView;
    public List<Control> subviews => GetChildren().OfType<Control>().ToList();
    public void addSubview(Control view)
    {
        if (view.GetParent() is Node old) old.RemoveChild(view);
        AddChild(view);
        AppKitLayout.Reapply(view);
    }
    public void removeFromSuperview() => GetParent()?.RemoveChild(this);
    public bool isHidden { get => !Visible; set => Visible = !value; }
    public string toolTip { get => string.IsNullOrEmpty(TooltipText) ? null : TooltipText; set => TooltipText = value ?? ""; }
    public bool needsDisplay { get => false; set { if (value) QueueRedraw(); } }
    public void displayIfNeeded() { }
    public virtual void resetCursorRects() { }
    /// <summary>addCursorRect(_:cursor:). PORT: the cursor applies to the whole view.</summary>
    public void addCursorRect(CGRect rect, NSCursor cursor) => MouseDefaultCursorShape = cursor.shape;
    /// <summary>convert(_:to:): a point in the view's coordinates (origin bottom left) to another view's, or to window coordinates for nil.</summary>
    public CGPoint convert(CGPoint point, Control to) => AppKitLayout.FromGlobal(GetGlobalTransform() * new Vector2((float)point.x, Size.Y - (float)point.y), to, this);
    public CGPoint convertFrom(CGPoint point, Control from)
    {
        var local = GetGlobalTransform().AffineInverse() * AppKitLayout.ToGlobal(point, from, this);
        return new CGPoint(local.X, Size.Y - local.Y);
    }
    /// <summary>
    /// convertToBacking(_:): points to backing-store pixels (the window's backingScaleFactor). PORT: the 3D view itself
    /// renders at one pixel per point (PORTING.md, window chrome), so on a HiDPI screen this is larger than the 3D render.
    /// </summary>
    public CGRect convertToBacking(CGRect rect)
    {
        var scale = window?.backingScaleFactor ?? 1;
        return new CGRect(rect.minX * scale, rect.minY * scale, rect.width * scale, rect.height * scale);
    }

    public override void _Notification(int what)
    {
        switch ((long)what)
        {
            case NotificationEnterTree:
                FocusMode = acceptsFirstResponder ? FocusModeEnum.All : FocusModeEnum.None;
                resetCursorRects();
                break;
            case NotificationResized:
                if (Size != lastAppKitSize)
                {
                    var old = new CGSize(Math.Max(0, lastAppKitSize.X), Math.Max(0, lastAppKitSize.Y));
                    lastAppKitSize = Size;
                    if (old.width > 0 || old.height > 0) foreach (var child in GetChildren()) if (child is NSView v) v.resizeWithOldSuperviewSize(old);
                    AppKitLayout.ReapplyChildren(this);
                    // AppKit's layout pass follows the superview's resize: re-solve Auto Layout subviews (the overlays pinned to
                    // the content layout guide) against the new size. The window's own resize handler ran before this view had
                    // its new size and placed them for the old one.
                    if (IsInsideTree() && GetChildren().Any(c => c is NSView { translatesAutoresizingMaskIntoConstraints: false })) window?.layoutConstraints();
                    if (IsInsideTree()) resetCursorRects();
                }
                break;
            case NotificationFocusEnter: becomeFirstResponder(); break;
            case NotificationFocusExit: resignFirstResponder(); break;
        }
    }
}
