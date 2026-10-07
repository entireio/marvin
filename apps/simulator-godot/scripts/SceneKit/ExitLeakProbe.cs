using Godot;

namespace Marvin.SceneKit;

/// <summary>
/// Godot-only diagnostic: the smallest program that shows the engine fault behind the Windows export's crash at exit
/// (PORTING.md, "Release builds and Windows"). One MeshInstance3D with a one-triangle ArrayMesh, never added to the scene
/// tree and never freed, then quit: Godot 4.7.2's RenderingServer frees the leaked mesh (and the Dependency it keeps of
/// the instances that show it) before the leaked instance, and in builds without DEBUG_ENABLED (the release export
/// templates) the instance's destructor then erases itself from that freed Dependency (DependencyTracker::clear), a use
/// after free: 0xC0000374 or 0xC0000005 on Windows, SIGBUS under MallocScribble on macOS. Debug templates and the editor
/// unlink the instances in ~Dependency and are unaffected.
///   MarvinSimulator --headless --exit-leak-probe DIR                       leaks the instance (the fault in a release build)
///   MARVIN_EXIT_PROBE=free MarvinSimulator --headless --exit-leak-probe DIR   frees it first (exits 0)
/// Writes DIR/exit-leak-probe.json ({"freed": bool}) before quitting.
/// </summary>
public static class ExitLeakProbe
{
    [Marvin.GameMode("--exit-leak-probe")]
    public static void Run(string dir, SceneTree tree)
    {
        bool free = System.Environment.GetEnvironmentVariable("MARVIN_EXIT_PROBE") == "free";
        var mesh = new ArrayMesh();
        using (var arrays = new Godot.Collections.Array())
        {
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) };
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        }
        var instance = new MeshInstance3D { Mesh = mesh };
        if (free) instance.Free();
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "exit-leak-probe.json"), $"{{\"freed\": {(free ? "true" : "false")}}}\n");
        GD.Print($"exit-leak-probe: a MeshInstance3D outside the tree, {(free ? "freed" : "leaked")}; quitting");
        tree.Quit();
    }
}
