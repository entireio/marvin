using System.Globalization;
using Godot;

public partial class Main : Node3D
{
    public override async void _Ready()
    {
        // Report and HUD text must not depend on the user's locale (see PORTING.md).
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Marvin.SceneKit.SceneKitRuntime.EnsureStarted();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (await Marvin.GameModes.TryRun(OS.GetCmdlineUserArgs(), GetTree())) return;
        GD.Print($"Marvin Godot: {Engine.GetVersionInfo()["string"]}, renderer {RenderingServer.GetCurrentRenderingDriverName()}");
        GetTree().Quit();
    }
}
