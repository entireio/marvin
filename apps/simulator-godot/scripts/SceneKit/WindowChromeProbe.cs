// `tools/godot -- --window-chrome DIR [BACKGROUND.png]`: the facade's title bar in the states that
// tools/scenekit-reference/WindowChrome.swift captures from AppKit (title bar only, unified toolbar, Pause relabelled
// "Resume"), drawn over the same background and with the inactive window's colours, so the two can be compared
// pixel for pixel (window-titlebar.png, window-toolbar.png, window-toolbar-resume.png). The traffic lights are the
// system's and are not part of the Godot capture.
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Godot;

namespace Marvin.SceneKit;

internal sealed class WindowChromeProbe : NSToolbarDelegate
{
    private NSToolbarItem pauseItem;

    [Marvin.GameMode("--window-chrome")]
    public static async Task Run(string dir, SceneTree tree)
    {
        var args = CommandLine.arguments; int i = System.Array.IndexOf(args, "--window-chrome");
        var background = i >= 0 && i + 2 < args.Length && !args[i + 2].StartsWith("--") ? args[i + 2] : null;
        var probe = new WindowChromeProbe();
        var window = new NSWindow(new CGRect(0, 0, 1280, 820),
            NSWindow.StyleMask.titled | NSWindow.StyleMask.closable | NSWindow.StyleMask.miniaturizable | NSWindow.StyleMask.resizable | NSWindow.StyleMask.fullSizeContentView,
            NSWindow.BackingStoreType.buffered, false);
        window.title = "Marvin · Dirt Track";
        var toolbar = new NSToolbar("SimulatorToolbar") { @delegate = probe, displayMode = NSToolbar.DisplayMode.iconAndLabel };
        window.toolbar = toolbar;
        var content = new TextureRect { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale };
        if (background != null && Image.LoadFromFile(background) is Image image && !image.IsEmpty()) content.Texture = ImageTexture.CreateFromImage(image);
        window.contentView = content;
        window.frameView.forceInactive = true;
        async Task capture(string name)
        {
            for (int k = 0; k < 3; k++) await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            await tree.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            tree.Root.GetTexture().GetImage().SavePng(Path.Combine(dir, name));
            GD.Print($"{name}: content layout rect {window.contentLayoutRect.width}x{window.contentLayoutRect.height}");
        }
        toolbar.isVisible = false;
        await capture("window-titlebar.png");
        toolbar.isVisible = true;
        await capture("window-toolbar.png");
        probe.pauseItem.label = "Resume"; probe.pauseItem.image = NSImage.systemSymbolName("play.fill", null);
        await capture("window-toolbar-resume.png");
        foreach (var size in new[] { 13.0, 14.0, 15.0 })
            foreach (var weight in new[] { NSFont.Weight.semibold, NSFont.Weight.bold })
            {
                var font = NSFont.systemFont(size, weight);
                var width = window.title.size(new Dictionary<NSAttributedString.Key, object> { [NSAttributedString.Key.font] = font }).width;
                GD.Print($"title width {size} {weight}: {width:F2}");
            }
        Foundation.exit(0);
    }

    public string[] toolbarAllowedItemIdentifiers(NSToolbar toolbar) => new[] { "menu", NSToolbarItem.Identifier.flexibleSpace, "camera", "pause", "reset", "help" };
    public string[] toolbarDefaultItemIdentifiers(NSToolbar toolbar) => toolbarAllowedItemIdentifiers(toolbar);
    public NSToolbarItem toolbar(NSToolbar toolbar, string itemForItemIdentifier, bool willBeInsertedIntoToolbar)
    {
        var item = new NSToolbarItem(itemForItemIdentifier);
        (string, string) config;
        switch (itemForItemIdentifier)
        {
            case "menu": config = ("Main Menu", "house"); break;
            case "camera": config = ("Camera", "video"); break;
            case "pause": config = ("Pause", "pause.fill"); pauseItem = item; break;
            case "reset": config = ("Reset", "arrow.counterclockwise"); break;
            case "help": config = ("Controls", "keyboard"); break;
            default: return null;
        }
        item.label = config.Item1; item.toolTip = config.Item1;
        item.image = NSImage.systemSymbolName(config.Item2, config.Item1);
        return item;
    }
}
