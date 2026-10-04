using System;
using System.Collections.Generic;
using Godot;

namespace Marvin.SceneKit;

/// <summary>
/// NSEvent: keyboard, mouse and scroll events in AppKit terms, built from Godot input events.
/// keyCode is the macOS virtual key code of the physical key (PORTING.md: NSEvent.keyCode ->
/// InputEventKey.PhysicalKeycode), so layout-independent key handling (W A S D, arrows) matches the
/// Mac; characters come from the keyboard layout. The Command modifier is the Control key except on macOS
/// (<see cref="commandIsControlKey"/>). Mouse locations are window coordinates (origin at the
/// window's bottom left); deltaX/deltaY are in points with y growing downwards, as in AppKit.
/// </summary>
public sealed class NSEvent
{
    public enum EventType
    {
        leftMouseDown = 1, leftMouseUp = 2, rightMouseDown = 3, rightMouseUp = 4, mouseMoved = 5, leftMouseDragged = 6,
        rightMouseDragged = 7, mouseEntered = 8, mouseExited = 9, keyDown = 10, keyUp = 11, flagsChanged = 12,
        scrollWheel = 22, otherMouseDown = 25, otherMouseUp = 26, otherMouseDragged = 27, magnify = 30,
    }
    [Flags]
    public enum ModifierFlags
    {
        capsLock = 1 << 16, shift = 1 << 17, control = 1 << 18, option = 1 << 19, command = 1 << 20,
        numericPad = 1 << 21, help = 1 << 22, function = 1 << 23, deviceIndependentFlagsMask = 0x7fff0000,
    }

    public EventType type { get; private set; }
    public ModifierFlags modifierFlags { get; private set; }
    public double timestamp { get; private set; }
    public int windowNumber { get; private set; }
    public ushort keyCode { get; private set; }
    public bool isARepeat { get; private set; }
    public string characters { get; private set; }
    public string charactersIgnoringModifiers { get; private set; }
    public CGPoint locationInWindow { get; private set; }
    public double deltaX { get; private set; }
    public double deltaY { get; private set; }
    public double scrollingDeltaX { get; private set; }
    public double scrollingDeltaY { get; private set; }
    public bool hasPreciseScrollingDeltas { get; private set; }
    public int clickCount { get; private set; }
    public float pressure { get; private set; }
    public int eventNumber { get; private set; }
    public double magnification { get; private set; }
    /// <summary>Set by NSResponder default implementations: the event continues up the responder chain (Godot propagates it to the parent control).</summary>
    internal bool passedToNextResponder;

    private NSEvent() { }

    /// <summary>NSEvent.keyEvent(with:location:modifierFlags:timestamp:windowNumber:context:characters:charactersIgnoringModifiers:isARepeat:keyCode:).</summary>
    public static NSEvent keyEvent(EventType with, CGPoint location, ModifierFlags modifierFlags, double timestamp, int windowNumber, object context,
        string characters, string charactersIgnoringModifiers, bool isARepeat, ushort keyCode) => new()
    {
        type = with, locationInWindow = location, modifierFlags = modifierFlags, timestamp = timestamp, windowNumber = windowNumber,
        characters = characters, charactersIgnoringModifiers = charactersIgnoringModifiers, isARepeat = isARepeat, keyCode = keyCode,
    };
    /// <summary>NSEvent.mouseEvent(with:location:modifierFlags:timestamp:windowNumber:context:eventNumber:clickCount:pressure:).</summary>
    public static NSEvent mouseEvent(EventType with, CGPoint location, ModifierFlags modifierFlags, double timestamp, int windowNumber, object context,
        int eventNumber, int clickCount, float pressure) => new()
    {
        type = with, locationInWindow = location, modifierFlags = modifierFlags, timestamp = timestamp, windowNumber = windowNumber,
        eventNumber = eventNumber, clickCount = clickCount, pressure = pressure,
    };

    // ---- Godot input -> NSEvent
    private static readonly Dictionary<Key, ushort> keyCodes = new()
    {
        [Key.A] = 0, [Key.S] = 1, [Key.D] = 2, [Key.F] = 3, [Key.H] = 4, [Key.G] = 5, [Key.Z] = 6, [Key.X] = 7, [Key.C] = 8, [Key.V] = 9,
        [Key.Section] = 10, [Key.B] = 11, [Key.Q] = 12, [Key.W] = 13, [Key.E] = 14, [Key.R] = 15, [Key.Y] = 16, [Key.T] = 17,
        [Key.Key1] = 18, [Key.Key2] = 19, [Key.Key3] = 20, [Key.Key4] = 21, [Key.Key6] = 22, [Key.Key5] = 23, [Key.Equal] = 24,
        [Key.Key9] = 25, [Key.Key7] = 26, [Key.Minus] = 27, [Key.Key8] = 28, [Key.Key0] = 29, [Key.Bracketright] = 30, [Key.O] = 31,
        [Key.U] = 32, [Key.Bracketleft] = 33, [Key.I] = 34, [Key.P] = 35, [Key.Enter] = 36, [Key.L] = 37, [Key.J] = 38,
        [Key.Apostrophe] = 39, [Key.K] = 40, [Key.Semicolon] = 41, [Key.Backslash] = 42, [Key.Comma] = 43, [Key.Slash] = 44,
        [Key.N] = 45, [Key.M] = 46, [Key.Period] = 47, [Key.Tab] = 48, [Key.Space] = 49, [Key.Quoteleft] = 50, [Key.Backspace] = 51,
        [Key.Escape] = 53, [Key.Meta] = 55, [Key.Shift] = 56, [Key.Capslock] = 57, [Key.Alt] = 58, [Key.Ctrl] = 59,
        [Key.KpPeriod] = 65, [Key.KpMultiply] = 67, [Key.KpAdd] = 69, [Key.Clear] = 71, [Key.KpDivide] = 75, [Key.KpEnter] = 76,
        [Key.KpSubtract] = 78, [Key.Kp0] = 82, [Key.Kp1] = 83, [Key.Kp2] = 84, [Key.Kp3] = 85, [Key.Kp4] = 86, [Key.Kp5] = 87,
        [Key.Kp6] = 88, [Key.Kp7] = 89, [Key.Kp8] = 91, [Key.Kp9] = 92, [Key.F5] = 96, [Key.F6] = 97, [Key.F7] = 98, [Key.F3] = 99,
        [Key.F8] = 100, [Key.F9] = 101, [Key.F11] = 103, [Key.F13] = 105, [Key.F14] = 107, [Key.F10] = 109, [Key.F12] = 111,
        [Key.F15] = 113, [Key.Help] = 114, [Key.Home] = 115, [Key.Pageup] = 116, [Key.Delete] = 117, [Key.F4] = 118, [Key.End] = 119,
        [Key.F2] = 120, [Key.Pagedown] = 121, [Key.F1] = 122, [Key.Left] = 123, [Key.Right] = 124, [Key.Down] = 125, [Key.Up] = 126,
    };
    /// <summary>macOS virtual key code of a physical Godot key (0xFFFF when unmapped).</summary>
    public static ushort KeyCodeFor(Key physical) => keyCodes.TryGetValue(physical, out var code) ? code : (ushort)0xFFFF;

    /// <summary>
    /// Whether the Command key is the keyboard's Control key: true except on macOS. AppKit's Command shortcuts (menu key
    /// equivalents, the views' "modifierFlags.contains(.command)" checks) become Control shortcuts on Windows and Linux,
    /// where the Windows/Super key is reported as <c>.control</c> instead.
    /// </summary>
    public static readonly bool commandIsControlKey = OS.GetName() != "macOS";
    private static ModifierFlags Modifiers(InputEventWithModifiers e)
    {
        ModifierFlags f = 0;
        if (e.ShiftPressed) f |= ModifierFlags.shift;
        if (e.CtrlPressed) f |= commandIsControlKey ? ModifierFlags.command : ModifierFlags.control;
        if (e.AltPressed) f |= ModifierFlags.option;
        if (e.MetaPressed) f |= commandIsControlKey ? ModifierFlags.control : ModifierFlags.command;
        return f;
    }
    /// <summary>The AppKit modifier flags of a Godot key event (after the key's own press or release).</summary>
    internal static ModifierFlags ModifiersOf(InputEventWithModifiers e) => Modifiers(e);
    private static CGPoint WindowLocation(Control control, Vector2 viewportPosition)
    {
        float height = control.GetViewport() is Viewport v ? v.GetVisibleRect().Size.Y : 0;
        return new CGPoint(viewportPosition.X, height - viewportPosition.Y);
    }
    private static int WindowNumber(Control control) => control.GetWindow() is Window w ? (int)w.GetWindowId() : 0;

    /// <summary>
    /// Delivers a Godot GUI event to an AppKit responder (NSView or SCNView). Returns true when the
    /// responder handled it; events its default implementation passes on are left for Godot to
    /// propagate to the parent control (AppKit's nextResponder chain).
    /// </summary>
    internal static bool Dispatch(NSResponder responder, Control control, InputEvent input)
    {
        NSEvent e;
        switch (input)
        {
            case InputEventKey key:
            {
                var physical = key.PhysicalKeycode != Key.None ? key.PhysicalKeycode : key.Keycode;
                var flags = Modifiers(key);
                bool modifierKey = physical is Key.Shift or Key.Ctrl or Key.Alt or Key.Meta or Key.Capslock;
                if (modifierKey)
                {
                    // flagsChanged reports the state after the change.
                    var changed = physical switch
                    {
                        Key.Shift => ModifierFlags.shift, Key.Alt => ModifierFlags.option,
                        Key.Ctrl => commandIsControlKey ? ModifierFlags.command : ModifierFlags.control,
                        Key.Meta => commandIsControlKey ? ModifierFlags.control : ModifierFlags.command,
                        _ => ModifierFlags.capsLock,
                    };
                    flags = key.Pressed ? flags | changed : flags & ~changed;
                }
                string chars = key.Unicode != 0 ? char.ConvertFromUtf32((int)key.Unicode) : "";
                string ignoring = key.KeyLabel != Key.None && (long)key.KeyLabel < 0x110000 && (long)key.KeyLabel >= 0x20 ? char.ConvertFromUtf32((int)key.KeyLabel).ToLowerInvariant() : chars;
                if ((flags & ModifierFlags.shift) != 0 && ignoring.Length > 0 && chars.Length > 0) ignoring = chars;
                e = keyEvent(modifierKey ? EventType.flagsChanged : key.Pressed ? EventType.keyDown : EventType.keyUp, CGPoint.zero, flags,
                    Time.GetTicksUsec() / 1e6, WindowNumber(control), null, chars, ignoring, key.Echo, KeyCodeFor(physical));
                if (modifierKey) responder.flagsChanged(e);
                else if (key.Pressed) responder.keyDown(e);
                else responder.keyUp(e);
                return !e.passedToNextResponder;
            }
            case InputEventMouseButton button:
            {                var location = WindowLocation(control, button.GlobalPosition);
                if (button.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown or MouseButton.WheelLeft or MouseButton.WheelRight)
                {
                    if (!button.Pressed) return false;
                    double amount = button.Factor > 0 ? button.Factor : 1;
                    e = new NSEvent { type = EventType.scrollWheel, locationInWindow = location, modifierFlags = Modifiers(button), timestamp = Time.GetTicksUsec() / 1e6, windowNumber = WindowNumber(control) };
                    e.scrollingDeltaY = button.ButtonIndex == MouseButton.WheelUp ? amount : button.ButtonIndex == MouseButton.WheelDown ? -amount : 0;
                    e.scrollingDeltaX = button.ButtonIndex == MouseButton.WheelLeft ? amount : button.ButtonIndex == MouseButton.WheelRight ? -amount : 0;
                    e.deltaY = e.scrollingDeltaY; e.deltaX = e.scrollingDeltaX;
                    responder.scrollWheel(e);
                    return !e.passedToNextResponder;
                }
                var type = (button.ButtonIndex, button.Pressed) switch
                {
                    (MouseButton.Left, true) => EventType.leftMouseDown, (MouseButton.Left, false) => EventType.leftMouseUp,
                    (MouseButton.Right, true) => EventType.rightMouseDown, (MouseButton.Right, false) => EventType.rightMouseUp,
                    (_, true) => EventType.otherMouseDown, _ => EventType.otherMouseUp,
                };
                e = mouseEvent(type, location, Modifiers(button), Time.GetTicksUsec() / 1e6, WindowNumber(control), null, 0, button.DoubleClick ? 2 : 1, button.Pressed ? 1 : 0);
                switch (type)
                {
                    case EventType.leftMouseDown: responder.mouseDown(e); break;
                    case EventType.leftMouseUp: responder.mouseUp(e); break;
                    case EventType.rightMouseDown: responder.rightMouseDown(e); break;
                    case EventType.rightMouseUp: responder.rightMouseUp(e); break;
                    case EventType.otherMouseDown: responder.otherMouseDown(e); break;
                    default: responder.otherMouseUp(e); break;
                }
                return !e.passedToNextResponder;
            }
            case InputEventMouseMotion motion:
            {
                var mask = motion.ButtonMask;
                var type = (mask & MouseButtonMask.Left) != 0 ? EventType.leftMouseDragged
                    : (mask & MouseButtonMask.Right) != 0 ? EventType.rightMouseDragged
                    : mask != 0 ? EventType.otherMouseDragged : EventType.mouseMoved;
                e = mouseEvent(type, WindowLocation(control, motion.GlobalPosition), Modifiers(motion), Time.GetTicksUsec() / 1e6, WindowNumber(control), null, 0, 0, mask != 0 ? 1 : 0);
                e.deltaX = motion.Relative.X; e.deltaY = motion.Relative.Y;
                switch (type)
                {
                    case EventType.leftMouseDragged: responder.mouseDragged(e); break;
                    case EventType.rightMouseDragged: responder.rightMouseDragged(e); break;
                    case EventType.otherMouseDragged: responder.otherMouseDragged(e); break;
                    default: responder.mouseMoved(e); break;
                }
                return !e.passedToNextResponder;
            }
            case InputEventPanGesture pan:
            {
                // Trackpad scrolling. Godot's macOS backend reports precise scrolling deltas x 0.03 with the sign reversed.
                e = new NSEvent
                {
                    type = EventType.scrollWheel, locationInWindow = WindowLocation(control, pan.Position + control.GetGlobalRect().Position), modifierFlags = Modifiers(pan),
                    timestamp = Time.GetTicksUsec() / 1e6, windowNumber = WindowNumber(control), hasPreciseScrollingDeltas = true,
                    scrollingDeltaX = -pan.Delta.X / 0.03, scrollingDeltaY = -pan.Delta.Y / 0.03,
                };
                e.deltaX = e.scrollingDeltaX * 0.1; e.deltaY = e.scrollingDeltaY * 0.1;
                responder.scrollWheel(e);
                return !e.passedToNextResponder;
            }
            case InputEventMagnifyGesture magnify:
            {
                e = new NSEvent { type = EventType.magnify, magnification = magnify.Factor - 1, modifierFlags = Modifiers(magnify), timestamp = Time.GetTicksUsec() / 1e6, windowNumber = WindowNumber(control) };
                responder.magnify(e);
                return !e.passedToNextResponder;
            }
        }
        return false;
    }
}

/// <summary>Swift OptionSet spellings for NSEvent.ModifierFlags: contains, intersection, isEmpty.</summary>
public static class NSEventModifierFlagsExtensions
{
    public static bool contains(this NSEvent.ModifierFlags flags, NSEvent.ModifierFlags member) => (flags & member) == member;
    public static NSEvent.ModifierFlags intersection(this NSEvent.ModifierFlags flags, NSEvent.ModifierFlags other) => flags & other;
    public static NSEvent.ModifierFlags union(this NSEvent.ModifierFlags flags, NSEvent.ModifierFlags other) => flags | other;
    public static bool isEmpty(this NSEvent.ModifierFlags flags) => flags == 0;
}

/// <summary>
/// NSResponder: the event and first-responder methods shared by NSView and SCNView (C# cannot give
/// SCNView, a SubViewportContainer, the NSView base class). Default implementations pass the event to
/// the next responder.
/// </summary>
public interface NSResponder
{
    bool acceptsFirstResponder { get; }
    bool becomeFirstResponder();
    bool resignFirstResponder();
    void keyDown(NSEvent @event);
    void keyUp(NSEvent @event);
    void flagsChanged(NSEvent @event);
    void mouseDown(NSEvent @event);
    void mouseUp(NSEvent @event);
    void mouseDragged(NSEvent @event);
    void mouseMoved(NSEvent @event);
    void mouseEntered(NSEvent @event);
    void mouseExited(NSEvent @event);
    void rightMouseDown(NSEvent @event);
    void rightMouseUp(NSEvent @event);
    void rightMouseDragged(NSEvent @event);
    void otherMouseDown(NSEvent @event);
    void otherMouseUp(NSEvent @event);
    void otherMouseDragged(NSEvent @event);
    void scrollWheel(NSEvent @event);
    void magnify(NSEvent @event);
}

/// <summary>NSCursor: the system cursors, mapped to Godot cursor shapes (openHand is Godot's CanDrop, closedHand its Drag).</summary>
public sealed class NSCursor
{
    internal readonly Control.CursorShape shape;
    private NSCursor(Control.CursorShape shape) { this.shape = shape; }
    public static readonly NSCursor arrow = new(Control.CursorShape.Arrow);
    public static readonly NSCursor iBeam = new(Control.CursorShape.Ibeam);
    public static readonly NSCursor pointingHand = new(Control.CursorShape.PointingHand);
    public static readonly NSCursor openHand = new(Control.CursorShape.CanDrop);
    public static readonly NSCursor closedHand = new(Control.CursorShape.Drag);
    public static readonly NSCursor crosshair = new(Control.CursorShape.Cross);
    public static readonly NSCursor operationNotAllowed = new(Control.CursorShape.Forbidden);
    public static readonly NSCursor resizeLeftRight = new(Control.CursorShape.Hsize);
    public static readonly NSCursor resizeUpDown = new(Control.CursorShape.Vsize);
    public void set() => Input.SetDefaultCursorShape((Input.CursorShape)shape);
}
