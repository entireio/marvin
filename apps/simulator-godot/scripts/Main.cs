using Godot;

public partial class Main : Node3D
{
    public override void _Ready()
    {
        GD.Print($"Marvin Godot skeleton: {Engine.GetVersionInfo()["string"]}, renderer {RenderingServer.GetCurrentRenderingDriverName()}");
        GetTree().Quit();
    }
}
