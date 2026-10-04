using System;
using System.Collections.Generic;
using Godot;

namespace Marvin.SceneKit;

/// <summary>NSControl: common text attributes of NSTextField and NSButton.</summary>
public partial class NSControl : NSView
{
    private NSFont _font;
    private NSTextAlignment _alignment = NSTextAlignment.natural;
    public int tag;
    public bool isEnabled = true;
    public NSControl() : this(CGRect.zero) { }
    public NSControl(CGRect frame) : base(frame) { }
    public NSFont font { get => _font; set { _font = value; QueueRedraw(); } }
    public NSTextAlignment alignment { get => _alignment; set { _alignment = value; QueueRedraw(); } }
    /// <summary>NSControl and its cells draw in flipped coordinates.</summary>
    public override bool isFlipped => true;
}

/// <summary>
/// NSTextField. <c>NSTextField(labelWithString:)</c> is <c>NSTextField.labelWithString(s)</c>: a borderless,
/// non-editable, single-line label drawn by its cell with the standard 2-point horizontal text inset, the
/// line's top at the top of the frame, clipped to the frame. Default font: system 13; default colour:
/// labelColor (black at 85%).
/// </summary>
public partial class NSTextField : NSControl
{
    private string _stringValue = "";
    private NSColor _textColor = NSColor.black.withAlphaComponent(0.85);
    public bool isEditable, isSelectable, isBezeled, isBordered, drawsBackground;
    public NSColor backgroundColor;
    public NSLineBreakMode lineBreakMode = NSLineBreakMode.byClipping;
    public NSTextField() : this(CGRect.zero) { }
    public NSTextField(CGRect frame) : base(frame) { }
    /// <summary>NSTextField(labelWithString:).</summary>
    public static NSTextField labelWithString(string stringValue) => new() { _stringValue = stringValue ?? "" };
    public string stringValue { get => _stringValue; set { _stringValue = value ?? ""; QueueRedraw(); } }
    public NSColor textColor { get => _textColor; set { _textColor = value; QueueRedraw(); } }
    public override void draw(CGRect dirtyRect)
    {
        if (drawsBackground && backgroundColor != null) { backgroundColor.setFill(); bounds.fill(); }
        var style = new NSMutableParagraphStyle { alignment = alignment, lineBreakMode = lineBreakMode };
        NSGraphicsContext.saveGraphicsState();
        new NSBezierPath(bounds).addClip();
        // A cell drawing its text over a transparent background gets CoreText's heavier cell smoothing (FontSmoothing).
        var context = NSGraphicsContext.current;
        bool cellText = context?.cellText ?? false;
        if (context != null) context.cellText = !(drawsBackground && backgroundColor != null && backgroundColor.alphaComponent >= 1);
        stringValue.draw(bounds.insetBy(2, 0), new Dictionary<NSAttributedString.Key, object>
        {
            [NSAttributedString.Key.font] = font ?? NSFont.systemFont(NSFont.systemFontSize),
            [NSAttributedString.Key.foregroundColor] = textColor,
            [NSAttributedString.Key.paragraphStyle] = style,
        });
        if (context != null) context.cellText = cellText;
        NSGraphicsContext.restoreGraphicsState();
    }
}

/// <summary>
/// NSButton(title:target:action:) -> <c>new NSButton(title, target, action)</c> with the action as a delegate
/// taking the sender. Borderless buttons (isBordered = false) draw only their layer and title; bordered ones
/// a rounded push-button bezel. The title (attributedTitle, or title in system 13) is centred: horizontally
/// in the bounds, vertically at floor((height - ceil(line height)) / 2) from the top (measured on the
/// game's menu buttons). The action fires when the mouse is released inside the button.
/// </summary>
public partial class NSButton : NSControl
{
    private string _title = "";
    private NSAttributedString _attributedTitle;
    private bool _isBordered = true;
    private bool tracking, highlighted;
    public object target;
    public Action<NSButton> action;
    public NSButton() : this("", null, null) { }
    public NSButton(string title, object target, Action<NSButton> action) : base(CGRect.zero)
    {
        _title = title ?? ""; this.target = target; this.action = action;
        alignment = NSTextAlignment.center;
    }
    public override bool acceptsFirstResponder => true;
    public string title { get => _title; set { _title = value ?? ""; _attributedTitle = null; QueueRedraw(); } }
    public NSAttributedString attributedTitle
    {
        get => _attributedTitle ?? new NSAttributedString(_title, TitleAttributes());
        set { _attributedTitle = value; _title = value?.@string ?? ""; QueueRedraw(); }
    }
    public bool isBordered { get => _isBordered; set { _isBordered = value; QueueRedraw(); } }
    public bool isHighlighted => highlighted;
    private Dictionary<NSAttributedString.Key, object> TitleAttributes() => new()
    {
        [NSAttributedString.Key.font] = font ?? NSFont.systemFont(NSFont.systemFontSize),
        [NSAttributedString.Key.foregroundColor] = NSColor.black.withAlphaComponent(0.85),
    };
    /// <summary>performClick(_:): fires the action.</summary>
    public void performClick(object sender) { if (isEnabled) action?.Invoke(this); }
    public override void draw(CGRect dirtyRect)
    {
        if (_isBordered)
        {
            var bezel = NSBezierPath.roundedRect(bounds.insetBy(0.5, 0.5), 5, 5);
            (highlighted ? NSColor.srgbRed(0.85, 0.85, 0.85, 1) : NSColor.white).setFill(); bezel.fill();
            NSColor.black.withAlphaComponent(0.2).setStroke(); bezel.lineWidth = 1; bezel.stroke();
        }
        var text = attributedTitle;
        var size = text.size();
        double y = Math.Floor((bounds.height - Math.Ceiling(size.height)) / 2);
        double x = alignment switch
        {
            NSTextAlignment.left => 0,
            NSTextAlignment.right => bounds.width - size.width,
            _ => (bounds.width - size.width) / 2,
        };
        // A borderless button's cell draws its title over a transparent background (FontSmoothing cell text).
        var context = NSGraphicsContext.current;
        bool cellText = context?.cellText ?? false;
        if (context != null) context.cellText = !_isBordered;
        text.draw(new CGPoint(x, y));
        if (context != null) context.cellText = cellText;
    }
    public override void mouseDown(NSEvent @event)
    {
        if (!isEnabled) return;
        tracking = true; highlighted = true; QueueRedraw();
    }
    public override void mouseDragged(NSEvent @event)
    {
        if (!tracking) return;
        bool inside = bounds.contains(convertFrom(@event.locationInWindow, from: null));
        if (inside != highlighted) { highlighted = inside; QueueRedraw(); }
    }
    public override void mouseUp(NSEvent @event)
    {
        if (!tracking) return;
        tracking = false;
        bool inside = bounds.contains(convertFrom(@event.locationInWindow, from: null));
        highlighted = false; QueueRedraw();
        if (inside) action?.Invoke(this);
    }
}
