using Godot;

public partial class Main : Node3D
{
    public override async void _Ready()
    {
        var args = OS.GetCmdlineUserArgs();
        int facade = System.Array.IndexOf(args, "--facade-test");
        if (facade >= 0)
        {
            // SceneKit facade self-test (scripts/SceneKit/FacadeTest.cs).
            string dir = facade + 1 < args.Length ? args[facade + 1] : "user://facade-test";
            if (!dir.StartsWith("res://") && !dir.StartsWith("user://") && !System.IO.Path.IsPathRooted(dir))
                dir = System.IO.Path.GetFullPath(dir, System.IO.Directory.GetCurrentDirectory());
            Marvin.SceneKit.SceneKitRuntime.EnsureStarted();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Marvin.SceneKit.FacadeTest.Run(dir, GetTree());
            return;
        }
        GD.Print($"Marvin Godot skeleton: {Engine.GetVersionInfo()["string"]}, renderer {RenderingServer.GetCurrentRenderingDriverName()}");
        GetTree().Quit();
    }
}
