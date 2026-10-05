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
    private static readonly Lazy<string[]> _userArguments = new(() =>
    {
        var user = OS.GetCmdlineUserArgs();
        // An exported build also takes the game's flags without "--": Windows PowerShell drops a bare "--" before a
        // native program, and Godot passes arguments it does not know through to the game.
        return user.Length > 0 || OS.HasFeature("editor") ? user : OS.GetCmdlineArgs();
    });
    /// <summary>The game's arguments: those after "--" (<c>tools/godot -- --smoke-test DIR</c>); in an exported build
    /// all arguments when none follow "--" (<c>MarvinSimulator.console.exe --smoke-test DIR</c>).</summary>
    public static string[] userArguments => _userArguments.Value;
    /// <summary>The game's own command line as the macOS app receives it: the executable and the game arguments
    /// (after "--"), without Godot's engine arguments. For reports that record the invocation (benchmarkArguments).</summary>
    public static string[] gameArguments
    {
        get
        {
            var list = new List<string> { OS.GetExecutablePath() };
            list.AddRange(userArguments);
            return list.ToArray();
        }
    }
}
