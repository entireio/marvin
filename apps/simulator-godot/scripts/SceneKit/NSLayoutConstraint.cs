using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Marvin.SceneKit;

/// <summary>NSLayoutGuide: a window's contentLayoutGuide (the client area below the title bar and toolbar).</summary>
public sealed class NSLayoutGuide
{
    internal readonly NSWindow window;
    internal NSLayoutGuide(NSWindow window) { this.window = window; }
    /// <summary>frame in window coordinates (origin bottom left).</summary>
    public CGRect frame => window.contentLayoutRect;
}

/// <summary>
/// NSLayoutConstraint for pinning views: equalities between an item's edge, size or centre attribute and another
/// item's (a view or a layout guide), <c>item.a = toItem.b x multiplier + constant</c>. Active constraints are solved
/// per item and axis (two of leading/trailing/width/centerX, likewise vertically) whenever the window's layout guide
/// changes (toolbar visibility, window size) and on activation; the result is the view's frame. PORT: this is the
/// subset of Auto Layout the app uses (installContentOverlay); inequalities, priorities and solving across items are
/// not implemented, and frames assigned directly stay until the next guide change (AppKit re-solves at its next
/// layout pass).
/// </summary>
public sealed class NSLayoutConstraint
{
    public enum Attribute { notAnAttribute = 0, left = 1, right = 2, top = 3, bottom = 4, leading = 5, trailing = 6, width = 7, height = 8, centerX = 9, centerY = 10 }
    public enum Relation { lessThanOrEqual = -1, equal = 0, greaterThanOrEqual = 1 }

    public readonly object firstItem, secondItem;
    public readonly Attribute firstAttribute, secondAttribute;
    public readonly Relation relation;
    public readonly double multiplier;
    public double constant;
    private bool _isActive;

    /// <summary>NSLayoutConstraint(item:attribute:relatedBy:toItem:attribute:multiplier:constant:).</summary>
    public NSLayoutConstraint(object item, Attribute attribute, Relation relatedBy, object toItem, Attribute attribute2, double multiplier, double constant)
    {
        if (relatedBy != Relation.equal) throw new NotSupportedException("NSLayoutConstraint: only .equal is implemented");
        firstItem = item; firstAttribute = attribute; relation = relatedBy; secondItem = toItem; secondAttribute = attribute2;
        this.multiplier = multiplier; this.constant = constant;
    }

    public bool isActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            var window = Window(this);
            if (window == null) return;
            if (value) window.constraints.Add(this); else window.constraints.Remove(this);
            Solve(window);
        }
    }
    /// <summary>NSLayoutConstraint.activate(_:).</summary>
    public static void activate(IEnumerable<NSLayoutConstraint> constraints) { foreach (var c in constraints) c.isActive = true; }
    public static void deactivate(IEnumerable<NSLayoutConstraint> constraints) { foreach (var c in constraints) c.isActive = false; }

    private static NSWindow Window(NSLayoutConstraint c) =>
        (c.secondItem as NSLayoutGuide)?.window ?? (c.firstItem as NSLayoutGuide)?.window
        ?? (c.firstItem is Control a && a.IsInsideTree() ? NSWindow.Of(a.GetWindow()) : null)
        ?? (c.secondItem is Control b && b.IsInsideTree() ? NSWindow.Of(b.GetWindow()) : null);

    /// <summary>An item's rectangle in window coordinates (origin bottom left).</summary>
    private static CGRect? WindowRect(object item, NSWindow window)
    {
        switch (item)
        {
            case NSLayoutGuide guide: return guide.frame;
            case Control control when control.IsInsideTree():
                var r = control.GetGlobalRect();
                var h = window.godotWindow.GetVisibleRect().Size.Y;
                return new CGRect(r.Position.X, h - r.End.Y, r.Size.X, r.Size.Y);
            default: return null;
        }
    }
    private static double Value(CGRect r, Attribute a) => a switch
    {
        Attribute.left or Attribute.leading => r.minX,
        Attribute.right or Attribute.trailing => r.maxX,
        Attribute.bottom => r.minY,
        Attribute.top => r.maxY,
        Attribute.width => r.width,
        Attribute.height => r.height,
        Attribute.centerX => r.midX,
        Attribute.centerY => r.midY,
        _ => 0,
    };

    /// <summary>Solves the window's active constraints and assigns the constrained views' frames (only <paramref name="only"/>'s
    /// when given: a view's layoutSubtreeIfNeeded re-applies its own constraints, measured on the Mac: a frame assigned
    /// to the main menu overlay reverts to the content layout guide).</summary>
    internal static void Solve(NSWindow window, Control only = null)
    {
        foreach (var group in window.constraints.Where(c => c.firstItem is Control && (only == null || c.firstItem == only)).GroupBy(c => (Control)c.firstItem).ToList())
        {
            var view = group.Key;
            if (!GodotObject.IsInstanceValid(view) || !view.IsInsideTree()) continue;
            var values = new Dictionary<Attribute, double>();
            foreach (var c in group)
            {
                double target = c.constant;
                if (c.secondItem != null && c.secondAttribute != Attribute.notAnAttribute)
                {
                    if (WindowRect(c.secondItem, window) is not CGRect other) continue;
                    target += Value(other, c.secondAttribute) * c.multiplier;
                }
                var a = c.firstAttribute switch { Attribute.leading => Attribute.left, Attribute.trailing => Attribute.right, _ => c.firstAttribute };
                values[a] = target;
            }
            if (!Axis(values, Attribute.left, Attribute.right, Attribute.width, Attribute.centerX, out double x, out double w)) continue;
            if (!Axis(values, Attribute.bottom, Attribute.top, Attribute.height, Attribute.centerY, out double y, out double hgt)) continue;
            // Window rectangle -> the superview's AppKit coordinates.
            var windowRect = new CGRect(x, y, w, hgt);
            var parent = view.GetParent() as Control;
            CGRect frame;
            if (parent == null) frame = windowRect;
            else
            {
                var bottomLeft = parent is NSView nsParent ? nsParent.convertFrom(new CGPoint(windowRect.minX, windowRect.minY), null)
                    : parent is SCNView scnParent ? scnParent.convertFrom(new CGPoint(windowRect.minX, windowRect.minY), null) : new CGPoint(windowRect.minX, windowRect.minY);
                bool flipped = parent is NSView { isFlipped: true };
                frame = new CGRect(bottomLeft.x, flipped ? bottomLeft.y - hgt : bottomLeft.y, w, hgt);
            }
            switch (view)
            {
                case NSView v: if (v.frame != frame) v.frame = frame; break;
                case SCNView s: s.frame = new CGRect(frame.minX, (parent?.Size.Y ?? 0) - frame.maxY, frame.width, frame.height); break;
            }
        }
    }
    private static bool Axis(Dictionary<Attribute, double> v, Attribute lo, Attribute hi, Attribute size, Attribute center, out double origin, out double length)
    {
        origin = length = 0;
        if (v.TryGetValue(lo, out var a) && v.TryGetValue(hi, out var b)) { origin = a; length = b - a; return true; }
        if (v.TryGetValue(lo, out a) && v.TryGetValue(size, out var s)) { origin = a; length = s; return true; }
        if (v.TryGetValue(hi, out b) && v.TryGetValue(size, out s)) { origin = b - s; length = s; return true; }
        if (v.TryGetValue(center, out var m) && v.TryGetValue(size, out s)) { origin = m - s / 2; length = s; return true; }
        return false;
    }
}
