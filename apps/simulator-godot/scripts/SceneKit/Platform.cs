using System;
using Godot;

namespace Marvin.SceneKit;

/// <summary>
/// Which platform's user-interface conventions the facade follows. On macOS (<see cref="macUI"/>): the global menu bar,
/// Command-key shortcuts, "⌘" in help texts, and a full-size content view under the system's traffic lights
/// (Godot's extend_to_title), with the title drawn by the facade. Elsewhere (Windows): the system title bar above the
/// client area, the menu bar drawn in the facade's title bar, Control-key shortcuts and "Ctrl+" in help texts.
/// Hardware queries (the screen's scale, the thermal state, Darwin libm) follow the real platform, not this.
/// <para>Test hook: <c>MARVIN_PLATFORM_UI=windows</c> makes a Mac follow the Windows conventions, so their code paths
/// (in-window menu bar, Control shortcuts, system title bar) can be run and captured on this Mac.</para>
/// </summary>
public static class Platform
{
    /// <summary>True when the facade follows the macOS conventions (on macOS, unless MARVIN_PLATFORM_UI=windows).</summary>
    public static readonly bool macUI = OS.GetName() == "macOS" &&
        !string.Equals(System.Environment.GetEnvironmentVariable("MARVIN_PLATFORM_UI"), "windows", StringComparison.OrdinalIgnoreCase);
    /// <summary>True when NSApp.mainMenu becomes the system's global menu bar (macOS) rather than a bar in the window.</summary>
    public static bool globalMenu => macUI && NativeMenu.HasFeature(NativeMenu.Feature.GlobalMenu);
}
