using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Godot;

namespace Marvin.SceneKit;

/// <summary>
/// Foundation/AppKit free functions and globals the app code uses unqualified
/// (global using static): <c>NSApp</c>, <c>print</c>, <c>exit</c>.
/// </summary>
public static class Foundation
{
    /// <summary>NSApp: the shared NSApplication.</summary>
    public static NSApplication NSApp => NSApplication.shared;
    /// <summary>Swift print(_:): items separated by spaces, to stdout (Godot's log).</summary>
    public static void print(params object[] items) => GD.Print(string.Join(" ", items.Select(i => Convert.ToString(i, CultureInfo.InvariantCulture))));
    /// <summary>
    /// exit(_:). PORT: Godot quits at the end of the current frame (SceneTree.Quit), so statements after
    /// exit() still run until the caller returns; the process exit code is the one passed here.
    /// </summary>
    public static void exit(int code)
    {
        if (Engine.GetMainLoop() is SceneTree tree) tree.Quit(code);
        else System.Environment.Exit(code);
    }
}

/// <summary>ProcessInfo.processInfo: systemUptime (seconds, monotonic), arguments, environment.</summary>
public sealed class ProcessInfo
{
    public static readonly ProcessInfo processInfo = new();
    public double systemUptime => Time.GetTicksUsec() / 1e6;
    public string[] arguments => CommandLine.arguments;
    public Dictionary<string, string> environment
    {
        get
        {
            var result = new Dictionary<string, string>();
            foreach (System.Collections.DictionaryEntry e in System.Environment.GetEnvironmentVariables()) result[(string)e.Key] = (string)e.Value;
            return result;
        }
    }
}

/// <summary>CommandLine.arguments: the executable followed by the game arguments (those after "--").</summary>
public static class CommandLine
{
    public static string[] arguments => new[] { OS.GetExecutablePath() }.Concat(OS.GetCmdlineUserArgs()).ToArray();
}

/// <summary>NSLock (lock/unlock). <c>lock</c> is a C# keyword: <c>sampleLock.@lock()</c>.</summary>
public sealed class NSLock
{
    private readonly object gate = new();
    public void @lock() => Monitor.Enter(gate);
    public void unlock() => Monitor.Exit(gate);
    public bool @try() => Monitor.TryEnter(gate);
}

/// <summary>
/// UserDefaults.standard, persisted with a Godot ConfigFile at user://UserDefaults.cfg (PORTING.md:
/// UserDefaults -> user://). Swift accessors that are C# keywords keep their names with @:
/// <c>UserDefaults.standard.@bool("key")</c>, <c>.@object("key")</c>, <c>.@double("key")</c>, <c>.@string("key")</c>;
/// <c>set(value, "key")</c>, <c>removeObject("key")</c>. Missing keys read as false/0/null like Foundation.
/// </summary>
public sealed class UserDefaults
{
    private const string Section = "defaults";
    public static readonly UserDefaults standard = new("user://UserDefaults.cfg");
    private readonly string path;
    private readonly object gate = new();
    private ConfigFile file;
    private UserDefaults(string path) { this.path = path; }
    private ConfigFile File
    {
        get
        {
            if (file != null) return file;
            file = new ConfigFile();
            if (FileAccess.FileExists(path)) file.Load(path);
            return file;
        }
    }
    public object @object(string forKey)
    {
        lock (gate)
        {
            if (!File.HasSectionKey(Section, forKey)) return null;
            var v = File.GetValue(Section, forKey);
            return v.VariantType switch
            {
                Variant.Type.Bool => v.AsBool(),
                Variant.Type.Int => v.AsInt64(),
                Variant.Type.Float => v.AsDouble(),
                Variant.Type.String => v.AsString(),
                _ => v.Obj,
            };
        }
    }
    public bool @bool(string forKey) => @object(forKey) switch
    {
        bool b => b,
        long i => i != 0,
        double d => d != 0,
        string s => s.Equals("YES", StringComparison.OrdinalIgnoreCase) || s.Equals("true", StringComparison.OrdinalIgnoreCase) || (int.TryParse(s, out var n) && n != 0),
        _ => false,
    };
    public double @double(string forKey) => @object(forKey) switch { bool b => b ? 1 : 0, long i => i, double d => d, string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0, _ => 0 };
    public long integer(string forKey) => (long)@double(forKey);
    public string @string(string forKey) => @object(forKey) switch { string s => s, null => null, var o => Convert.ToString(o, CultureInfo.InvariantCulture) };
    public void set(object value, string forKey)
    {
        if (value == null) { removeObject(forKey); return; }
        lock (gate)
        {
            Variant v = value switch
            {
                bool b => b, int i => i, long l => l, double d => d, float f => f, string s => s,
                _ => Convert.ToString(value, CultureInfo.InvariantCulture),
            };
            File.SetValue(Section, forKey, v);
            File.Save(path);
        }
    }
    public void removeObject(string forKey)
    {
        lock (gate)
        {
            if (!File.HasSectionKey(Section, forKey)) return;
            File.EraseSectionKey(Section, forKey);
            File.Save(path);
        }
    }
    public bool synchronize() { lock (gate) return File.Save(path) == Error.Ok; }
}

/// <summary>A file URL (only what the app uses): URL(fileURLWithPath:) and appendingPathComponent.</summary>
public readonly struct URL
{
    public readonly string path;
    private URL(string path) { this.path = path; }
    /// <summary>URL(fileURLWithPath:) / URL(fileURLWithPath:isDirectory:). user:// and res:// paths are globalized.</summary>
    public static URL fileURLWithPath(string path, bool isDirectory = false) =>
        new(path.StartsWith("user://") || path.StartsWith("res://") ? ProjectSettings.GlobalizePath(path) : path);
    public URL appendingPathComponent(string component) => new(System.IO.Path.Combine(path, component));
    public URL deletingLastPathComponent() => new(System.IO.Path.GetDirectoryName(path) ?? path);
    public string lastPathComponent => System.IO.Path.GetFileName(path);
    public override string ToString() => "file://" + path;
}

/// <summary>FileManager.default (<c>FileManager.@default</c>): directory creation and existence checks.</summary>
public sealed class FileManager
{
    public static readonly FileManager @default = new();
    public void createDirectory(string atPath, bool withIntermediateDirectories) => System.IO.Directory.CreateDirectory(atPath);
    public void createDirectory(URL at, bool withIntermediateDirectories) => System.IO.Directory.CreateDirectory(at.path);
    public bool fileExists(string atPath) => System.IO.File.Exists(atPath) || System.IO.Directory.Exists(atPath);
    public void removeItem(URL at) { if (System.IO.Directory.Exists(at.path)) System.IO.Directory.Delete(at.path, true); else System.IO.File.Delete(at.path); }
}

/// <summary>Data writing and NSString helpers.</summary>
public static class FoundationExtensions
{
    /// <summary>Data.write(to:) for the byte arrays the facade uses as Data.</summary>
    public static void write(this byte[] data, URL to) => System.IO.File.WriteAllBytes(to.path, data);
    /// <summary>
    /// NSString.padding(toLength:withPad:startingAt:): truncates to the length, or pads with the pad
    /// string repeated from startingAt.
    /// </summary>
    public static string padding(this string value, int toLength, string withPad, int startingAt)
    {
        if (value.Length >= toLength) return value.Substring(0, toLength);
        var result = new System.Text.StringBuilder(value, toLength);
        for (int i = 0; result.Length < toLength; i++) result.Append(withPad[(startingAt + i) % withPad.Length]);
        return result.ToString();
    }
}
