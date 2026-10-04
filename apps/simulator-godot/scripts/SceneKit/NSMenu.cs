using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Marvin.SceneKit;

/// <summary>
/// NSMenu: the menu bar's menus (NSApp.mainMenu). On macOS the bar becomes the system's global menu bar (Godot's
/// MenuBar with PreferGlobalMenu, NativeMenu underneath; the first menu's items join the application menu); elsewhere
/// it is drawn in the window's title bar. Key equivalents use the Command key on macOS and Control elsewhere (the
/// facade reports Control as <c>.command</c> there, see NSEvent); they are matched before the first responder sees the
/// key, as AppKit's performKeyEquivalent does, so the game never also receives them as driving keys.
/// </summary>
public sealed class NSMenu
{
    public string title;
    private readonly List<NSMenuItem> _items = new();
    internal Action changed;
    public NSMenu() : this("") { }
    public NSMenu(string title) { this.title = title ?? ""; }
    public IReadOnlyList<NSMenuItem> items => _items;
    public void addItem(NSMenuItem item) { item.menu = this; _items.Add(item); changed?.Invoke(); }
    /// <summary>addItem(withTitle:action:keyEquivalent:).</summary>
    public NSMenuItem addItem(string withTitle, Action<object> action, string keyEquivalent)
    {
        var item = new NSMenuItem(withTitle, action, keyEquivalent);
        addItem(item);
        return item;
    }

    /// <summary>performKeyEquivalent(with:): runs the item whose key equivalent and modifiers match the key event.</summary>
    public bool performKeyEquivalent(NSEvent with)
    {
        if (with == null || with.type != NSEvent.EventType.keyDown || !with.modifierFlags.contains(NSEvent.ModifierFlags.command)) return false;
        var characters = new List<string>();
        if (!string.IsNullOrEmpty(with.charactersIgnoringModifiers)) characters.Add(with.charactersIgnoringModifiers.ToLowerInvariant());
        var item = Find(this, characters, with.modifierFlags);
        if (item == null) return false;
        item.action?.Invoke(item);
        return true;
    }

    /// <summary>True when the menu bar is the system's global menu (macOS) rather than drawn in the window.</summary>
    internal static bool usesGlobalMenu => NativeMenu.HasFeature(NativeMenu.Feature.GlobalMenu);

    /// <summary>performKeyEquivalent: the first enabled item, depth first, whose key equivalent and modifiers match.</summary>
    internal static bool performKeyEquivalent(NSMenu menu, InputEventKey key)
    {
        var flags = NSEvent.ModifiersOf(key);
        if (!flags.contains(NSEvent.ModifierFlags.command)) return false;
        var candidates = KeyCharacters(key);
        if (candidates.Count == 0) return false;
        var item = Find(menu, candidates, flags);
        if (item == null) return false;
        item.action?.Invoke(item);
        return true;
    }
    private static NSMenuItem Find(NSMenu menu, List<string> characters, NSEvent.ModifierFlags flags)
    {
        foreach (var item in menu.items)
        {
            if (item.submenu != null) { if (Find(item.submenu, characters, flags) is NSMenuItem found) return found; continue; }
            if (!item.isEnabled || string.IsNullOrEmpty(item.keyEquivalent)) continue;
            // An upper-case key equivalent implies Shift (AppKit).
            var equivalent = item.keyEquivalent;
            var mask = item.keyEquivalentModifierMask;
            if (equivalent.ToLowerInvariant() != equivalent) { mask |= NSEvent.ModifierFlags.shift; equivalent = equivalent.ToLowerInvariant(); }
            var relevant = NSEvent.ModifierFlags.command | NSEvent.ModifierFlags.shift | NSEvent.ModifierFlags.option | NSEvent.ModifierFlags.control;
            if ((flags & relevant) != (mask & relevant)) continue;
            if (characters.Contains(equivalent)) return item;
        }
        return null;
    }
    /// <summary>The characters a key event can match: the layout's unshifted character and the physical key's US character.</summary>
    private static List<string> KeyCharacters(InputEventKey key)
    {
        var list = new List<string>();
        void add(Key k)
        {
            long code = (long)k;
            if (code >= 0x20 && code < 0x110000) { var s = char.ConvertFromUtf32((int)code).ToLowerInvariant(); if (!list.Contains(s)) list.Add(s); }
        }
        add(key.KeyLabel); add(key.Keycode); add(key.PhysicalKeycode);
        return list;
    }

    /// <summary>The Godot menu bar for a main menu: one PopupMenu per top-level item with a submenu.</summary>
    internal static MenuBar makeMenuBar(NSMenu main)
    {
        var bar = new MenuBar { Name = "MenuBar", PreferGlobalMenu = true, Flat = true };
        bar.AddThemeFontSizeOverride("font_size", 13);
        bar.AddThemeColorOverride("font_color", new Color(0.15f, 0.15f, 0.15f));
        bar.AddThemeColorOverride("font_hover_color", new Color(0.05f, 0.05f, 0.05f));
        bar.AddThemeColorOverride("font_pressed_color", new Color(1, 1, 1));
        var hover = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.08f) }; hover.SetCornerRadiusAll(4); hover.SetContentMarginAll(6);
        var pressedBox = new StyleBoxFlat { BgColor = new Color(0.04f, 0.4f, 0.85f) }; pressedBox.SetCornerRadiusAll(4); pressedBox.SetContentMarginAll(6);
        var normal = new StyleBoxEmpty(); normal.SetContentMarginAll(6);
        foreach (var (name, box) in new (string, StyleBox)[] { ("normal", normal), ("hover", hover), ("pressed", pressedBox), ("hover_pressed", pressedBox), ("focus", new StyleBoxEmpty()) })
            bar.AddThemeStyleboxOverride(name, box);
        bool first = true;
        foreach (var top in main.items)
        {
            if (top.submenu == null) continue;
            var popup = new PopupMenu();
            // The application menu (the first, untitled menu) is the app's name; on macOS its items join the system application menu.
            string title = !string.IsNullOrEmpty(top.submenu.title) ? top.submenu.title : !string.IsNullOrEmpty(top.title) ? top.title : (string)ProjectSettings.GetSetting("application/config/name", "App");
            popup.Name = title;
            if (first && usesGlobalMenu) popup.SystemMenuId = NativeMenu.SystemMenus.ApplicationMenuId;
            StylePopup(popup);
            fill(popup, top.submenu, first && usesGlobalMenu);
            top.submenu.changed = () => { popup.Clear(); fill(popup, top.submenu, false); };
            bar.AddChild(popup);
            bar.SetMenuTitle(bar.GetMenuCount() - 1, title);
            first = false;
        }
        return bar;
    }
    private static void fill(PopupMenu popup, NSMenu menu, bool applicationMenu)
    {
        var items = menu.items.ToList();
        popup.IdPressed += id => { if (id >= 0 && id < items.Count) items[(int)id].action?.Invoke(items[(int)id]); };
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            // The system application menu already has Quit (Command-Q) and its own separators.
            if (applicationMenu && (item.isSeparatorItem || item.keyEquivalent == "q")) continue;
            if (item.isSeparatorItem) { popup.AddSeparator(); continue; }
            var accelerator = Accelerator(item);
            int index = popup.ItemCount;
            if (item.isCheckable) popup.AddCheckItem(item.title, i, accelerator); else popup.AddItem(item.title, i, accelerator);
            popup.SetItemChecked(index, item.state == NSControl.StateValue.on);
            popup.SetItemDisabled(index, !item.isEnabled);
            item.bind(popup, index);
        }
    }
    private static Key Accelerator(NSMenuItem item)
    {
        if (string.IsNullOrEmpty(item.keyEquivalent)) return Key.None;
        var c = item.keyEquivalent[0];
        Key key = char.ToUpperInvariant(c) switch { >= 'A' and <= 'Z' and var u => (Key)u, >= '0' and <= '9' and var d => (Key)d, _ => Key.None };
        if (key == Key.None) return Key.None;
        var mask = OS.GetName() == "macOS" ? KeyModifierMask.MaskMeta : KeyModifierMask.MaskCtrl;
        if (char.IsUpper(c)) mask |= KeyModifierMask.MaskShift;
        return (Key)((long)key | (long)mask);
    }
    private static void StylePopup(PopupMenu popup)
    {
        var panel = new StyleBoxFlat { BgColor = new Color(0.97f, 0.97f, 0.97f), BorderColor = new Color(0, 0, 0, 0.18f) };
        panel.SetBorderWidthAll(1); panel.SetCornerRadiusAll(6); panel.SetContentMarginAll(5);
        var hover = new StyleBoxFlat { BgColor = new Color(0.04f, 0.4f, 0.85f) }; hover.SetCornerRadiusAll(4);
        popup.AddThemeStyleboxOverride("panel", panel);
        popup.AddThemeStyleboxOverride("hover", hover);
        popup.AddThemeColorOverride("font_color", new Color(0.12f, 0.12f, 0.12f));
        popup.AddThemeColorOverride("font_hover_color", new Color(1, 1, 1));
        popup.AddThemeColorOverride("font_accelerator_color", new Color(0.45f, 0.45f, 0.45f));
        popup.AddThemeColorOverride("font_disabled_color", new Color(0.6f, 0.6f, 0.6f));
        popup.AddThemeFontSizeOverride("font_size", 13);
    }
}

/// <summary>NSMenuItem: title, action (called with the item as sender), key equivalent, state (a check mark), submenu.</summary>
public sealed class NSMenuItem
{
    private string _title = "";
    private NSControl.StateValue _state = NSControl.StateValue.off;
    internal NSMenu menu;
    public NSMenu submenu;
    public object target;
    /// <summary>action (Swift: target + selector): called with this item as the sender.</summary>
    public Action<object> action;
    public string keyEquivalent = "";
    public NSEvent.ModifierFlags keyEquivalentModifierMask = NSEvent.ModifierFlags.command;
    public bool isEnabled = true;
    public bool isSeparatorItem { get; private set; }
    public int tag;
    public string toolTip;
    /// <summary>Set once the item's state was assigned (a checkable item in the Godot menu).</summary>
    internal bool isCheckable;
    private PopupMenu popup;
    private int popupIndex = -1;

    public NSMenuItem() { }
    /// <summary>NSMenuItem(title:action:keyEquivalent:).</summary>
    public NSMenuItem(string title, Action<object> action, string keyEquivalent) { _title = title ?? ""; this.action = action; this.keyEquivalent = keyEquivalent ?? ""; }
    /// <summary>NSMenuItem.separator().</summary>
    public static NSMenuItem separator() => new() { isSeparatorItem = true };
    public string title { get => _title; set { _title = value ?? ""; if (popup != null && IsInstanceValid(popup)) popup.SetItemText(popupIndex, _title); } }
    public NSControl.StateValue state
    {
        get => _state;
        set
        {
            _state = value; isCheckable = true;
            if (popup != null && GodotObject.IsInstanceValid(popup) && popupIndex >= 0)
            {
                if (!popup.IsItemCheckable(popupIndex)) popup.SetItemAsCheckable(popupIndex, true);
                popup.SetItemChecked(popupIndex, value == NSControl.StateValue.on);
            }
        }
    }
    internal void bind(PopupMenu popup, int index) { this.popup = popup; popupIndex = index; }
    private static bool IsInstanceValid(GodotObject o) => GodotObject.IsInstanceValid(o);
}

/// <summary>
/// The command key as the game's help texts show it: "⌘" on macOS, "Ctrl+" elsewhere (key equivalents use Control
/// there). PORT: the Swift strings spell "⌘R"; the port writes <c>$"{KeyEquivalent.command}R"</c>.
/// </summary>
public static class KeyEquivalent
{
    public static readonly string command = OS.GetName() == "macOS" ? "⌘" : "Ctrl+";
}
