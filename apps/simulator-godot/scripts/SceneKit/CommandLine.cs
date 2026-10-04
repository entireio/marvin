using System;

namespace Marvin.SceneKit;

/// <summary>
/// Swift's <c>CommandLine.arguments</c>: the executable followed by the game's own arguments.
/// Godot game arguments follow "--" (<c>tools/godot -- --town-smoke-test DIR</c>), so this is the
/// executable path plus <c>OS.GetCmdlineUserArgs()</c>, the same list the macOS app sees after its
/// executable. Measured: <c>Environment.GetCommandLineArgs()</c> is empty inside Godot .NET, so it
/// cannot stand in for Swift's arguments. Write <c>CommandLine.arguments.Contains("--flag")</c>.
/// </summary>
public static class CommandLine
{
    private static readonly Lazy<string[]> _arguments = new(() =>
    {
        var user = Godot.OS.GetCmdlineUserArgs();
        var result = new string[user.Length + 1];
        result[0] = Godot.OS.GetExecutablePath();
        Array.Copy(user, 0, result, 1, user.Length);
        return result;
    });
    public static string[] arguments => _arguments.Value;
}
