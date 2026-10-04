using System;
using System.Collections.Generic;
using Godot;

namespace Marvin.SceneKit;

/// <summary>
/// Swift's <c>CommandLine.arguments</c>: the executable followed by every argument. The macOS game
/// tests flags with <c>CommandLine.arguments.contains("--flag")</c> (C#: <c>.Contains("--flag")</c>).
/// Godot game arguments follow "--" (<c>tools/godot -- --town-smoke-test DIR</c>); Godot's own
/// arguments are included before them, so a game flag such as <c>--benchmark-no-deformation</c> is
/// found wherever it was passed, and a flag's value still follows the flag. Measured:
/// <c>Environment.GetCommandLineArgs()</c> is empty inside Godot .NET, so it cannot stand in for
/// Swift's arguments.
/// </summary>
public static class CommandLine
{
    private static readonly Lazy<string[]> _arguments = new(() =>
    {
        var list = new List<string> { OS.GetExecutablePath() };
        list.AddRange(OS.GetCmdlineArgs());
        list.AddRange(OS.GetCmdlineUserArgs());
        return list.ToArray();
    });
    public static string[] arguments => _arguments.Value;
}
