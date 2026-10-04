using System.Collections.Generic;
using Godot;

namespace Marvin.SceneKit;

/// <summary>
/// Swift's `CommandLine.arguments`: the executable followed by every argument. The macOS game
/// tests flags with `CommandLine.arguments.contains("--flag")` (C#: `.Contains("--flag")`).
/// Godot splits its own arguments from the game's ("--" separator); both are included here, so
/// a game flag such as `--benchmark-no-deformation` is found wherever it was passed.
/// </summary>
public static class CommandLine
{
    private static string[] cached;
    public static IReadOnlyList<string> arguments
    {
        get
        {
            if (cached != null) return cached;
            var list = new List<string> { OS.GetExecutablePath() };
            list.AddRange(OS.GetCmdlineArgs());
            list.AddRange(OS.GetCmdlineUserArgs());
            cached = list.ToArray();
            return cached;
        }
    }
}
