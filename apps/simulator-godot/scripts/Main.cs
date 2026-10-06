using System.Globalization;
using Godot;

/// <summary>
/// The entry scene (scenes/Main.tscn). Without a game-mode flag this is main.swift: the app with AppController as
/// the NSApplication's delegate, opening on the main menu (`tools/godot`). A flag after "--" runs that game mode
/// instead (GameModes, e.g. `tools/godot -- --smoke-test DIR`); an exported build also takes it without "--"
/// (CommandLine.userArguments).
/// </summary>
public partial class Main : Node
{
    public override async void _Ready()
    {
        // Report and HUD text must not depend on the user's locale (see PORTING.md).
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Marvin.SceneKit.SceneKitRuntime.EnsureStarted();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (await Marvin.GameModes.TryRun(Marvin.SceneKit.CommandLine.userArguments, GetTree())) return;
        GD.Print($"Marvin Godot: {Engine.GetVersionInfo()["string"]}, renderer {RenderingServer.GetCurrentRenderingDriverName()}");
        // The game starts with the Settings screen's stored Shadow quality (game modes keep the default; MARVIN_SCN_CAL,
        // when set, overrides both).
        Marvin.ShadowQualitySetting.apply();
        Marvin.AppController.launch(GetTree());
    }
}
