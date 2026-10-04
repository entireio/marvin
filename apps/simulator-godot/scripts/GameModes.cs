using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Godot;

namespace Marvin;

/// Marks a static method `Task Run(string dir, SceneTree tree)` (or `void Run(...)`) as a command-line
/// mode, e.g. [GameMode("--town-smoke-test")]. Main runs it when the flag is passed after "--":
///   tools/godot -- --town-smoke-test captures/town
/// Mirror the macOS flag names and output file names so captures can be compared side by side.
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class GameModeAttribute : Attribute
{
    public string Flag { get; }
    public GameModeAttribute(string flag) { Flag = flag; }
}

public static class GameModes
{
    static Dictionary<string, MethodInfo> modes;

    public static IReadOnlyDictionary<string, MethodInfo> All
    {
        get
        {
            if (modes != null) return modes;
            modes = new Dictionary<string, MethodInfo>();
            foreach (var type in typeof(GameModes).Assembly.GetTypes())
                foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    foreach (var attribute in method.GetCustomAttributes<GameModeAttribute>())
                    {
                        if (modes.ContainsKey(attribute.Flag)) throw new InvalidOperationException($"duplicate game mode {attribute.Flag}");
                        modes[attribute.Flag] = method;
                    }
            return modes;
        }
    }

    /// Resolves a mode's output directory like the macOS app: relative paths are relative to the working directory.
    public static string OutputDirectory(string arg, string fallback)
    {
        string dir = string.IsNullOrEmpty(arg) || arg.StartsWith("--") ? fallback : arg;
        // The modes write with System.IO, which does not know Godot's user:// (it would create a "user:" folder).
        if (dir.StartsWith("user://")) dir = ProjectSettings.GlobalizePath(dir);
        if (!dir.StartsWith("res://") && !dir.StartsWith("user://") && !System.IO.Path.IsPathRooted(dir))
            dir = System.IO.Path.GetFullPath(dir, System.IO.Directory.GetCurrentDirectory());
        if (!dir.StartsWith("res://") && !dir.StartsWith("user://")) System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    /// Runs the first registered mode found in args. Returns false when no mode flag is present.
    public static async Task<bool> TryRun(string[] args, SceneTree tree)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (!All.TryGetValue(args[i], out var method)) continue;
            string dir = OutputDirectory(i + 1 < args.Length ? args[i + 1] : null, "user://" + args[i].TrimStart('-'));
            var result = method.Invoke(null, new object[] { dir, tree });
            if (result is Task task) await task;
            return true;
        }
        return false;
    }
}
