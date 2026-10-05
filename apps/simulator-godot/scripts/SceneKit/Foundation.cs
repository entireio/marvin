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
    /// <summary>NSLog(_:): the message on stderr (Godot's error log), as Foundation logs it.</summary>
    public static void NSLog(string message) => GD.PrintErr(message);
}

/// <summary>
/// JSONSerialization.data(withJSONObject:options:[.prettyPrinted, .sortedKeys]), as macOS writes the smoke reports:
/// two-space indentation, <c>"key" : value</c>, no trailing newline, empty containers as <c>{\n\n}</c> / <c>[\n\n]</c>;
/// numbers as NSNumber prints them (integers and integral doubles without a fraction, other doubles %.17g); strings
/// with <c>/</c> escaped; keys in <c>.sortedKeys</c> order, which compares [.numeric, .caseInsensitive, .forcedOrdering]
/// (measured: "signs" sorts before "signTextFits"). Values: dictionaries (IDictionary), sequences, strings, bools,
/// numbers and null.
/// </summary>
public static class JSONSerialization
{
    [Flags] public enum WritingOptions { none = 0, prettyPrinted = 1, sortedKeys = 2 }
    /// <summary>The report text with [.prettyPrinted, .sortedKeys] (the options most smoke reports use).</summary>
    public static string prettyPrintedSortedKeys(object withJSONObject) => @string(withJSONObject, WritingOptions.prettyPrinted | WritingOptions.sortedKeys);
    /// <summary>
    /// The JSON text for these options. Without <c>.prettyPrinted</c> the text is compact (<c>{"a":1,"b":[2,3]}</c>, as
    /// Foundation writes it); without <c>.sortedKeys</c> keys keep the dictionary's insertion order. PORT: Swift
    /// dictionaries have no defined order, so unsorted output was not deterministic on macOS either.
    /// </summary>
    public static string @string(object withJSONObject, WritingOptions options)
    {
        var sb = new System.Text.StringBuilder(); write(sb, withJSONObject, 0, options); return sb.ToString();
    }
    /// <summary>The text without .prettyPrinted (<c>{"key":value}</c>), keys sorted: <c>@string(x, .sortedKeys)</c>.</summary>
    public static string compact(object withJSONObject) => @string(withJSONObject, WritingOptions.sortedKeys);
    /// <summary>JSONSerialization.data(withJSONObject:options:) (UTF-8); write it with <c>.write(to: url)</c>. PORT: the
    /// default here is [.prettyPrinted, .sortedKeys] (Swift's is []); pass <c>WritingOptions.none</c> for compact text.</summary>
    public static byte[] data(object withJSONObject, WritingOptions options = WritingOptions.prettyPrinted | WritingOptions.sortedKeys) =>
        System.Text.Encoding.UTF8.GetBytes(@string(withJSONObject, options));

    private static void write(System.Text.StringBuilder sb, object value, int depth, WritingOptions options)
    {
        string pad(int d) => new string(' ', d * 2);
        bool pretty = options.HasFlag(WritingOptions.prettyPrinted);
        switch (value)
        {
            case null: sb.Append("null"); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case string s: quote(sb, s); break;
            case int or long or uint or short or ulong or ushort or byte: sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
            case float f: sb.Append(number(f)); break;
            case double d: sb.Append(number(d)); break;
            case Godot.Variant v: write(sb, v.Obj, depth, options); break;
            case System.Collections.IDictionary dictionary:
            {
                var names = dictionary.Keys.Cast<object>().Select(k => Convert.ToString(k, CultureInfo.InvariantCulture));
                var keys = (options.HasFlag(WritingOptions.sortedKeys) ? names.OrderBy(k => k, keyOrder) : names).ToList();
                var byName = dictionary.Keys.Cast<object>().ToDictionary(k => Convert.ToString(k, CultureInfo.InvariantCulture), k => dictionary[k]);
                if (!pretty)
                {
                    sb.Append('{');
                    for (var i = 0; i < keys.Count; i++) { if (i > 0) sb.Append(','); quote(sb, keys[i]); sb.Append(':'); write(sb, byName[keys[i]], depth + 1, options); }
                    sb.Append('}');
                    break;
                }
                if (keys.Count == 0) { sb.Append("{\n\n").Append(pad(depth)).Append('}'); break; }
                sb.Append("{\n");
                for (var i = 0; i < keys.Count; i++)
                {
                    sb.Append(pad(depth + 1)); quote(sb, keys[i]); sb.Append(" : ");
                    write(sb, byName[keys[i]], depth + 1, options);
                    sb.Append(i + 1 < keys.Count ? ",\n" : "\n");
                }
                sb.Append(pad(depth)).Append('}');
                break;
            }
            case System.Collections.IEnumerable sequence:
            {
                if (!pretty)
                {
                    sb.Append('[');
                    bool first = true;
                    foreach (var item in sequence) { if (!first) sb.Append(','); first = false; write(sb, item, depth + 1, options); }
                    sb.Append(']');
                    break;
                }
                var items = sequence.Cast<object>().ToList();
                if (items.Count == 0) { sb.Append("[\n\n").Append(pad(depth)).Append(']'); break; }
                sb.Append("[\n");
                for (var i = 0; i < items.Count; i++)
                {
                    sb.Append(pad(depth + 1)); write(sb, items[i], depth + 1, options);
                    sb.Append(i + 1 < items.Count ? ",\n" : "\n");
                }
                sb.Append(pad(depth)).Append(']');
                break;
            }
            default: quote(sb, Convert.ToString(value, CultureInfo.InvariantCulture)); break;
        }
    }
    private static void quote(System.Text.StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '/': sb.Append("\\/"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
    /// <summary>printf("%.17g"), as NSNumber's JSON description; integral values print without a fraction.</summary>
    private static string number(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) throw new System.IO.InvalidDataException("JSON cannot encode a non-finite number");
        if (d == Math.Floor(d) && Math.Abs(d) < 1e15) return ((long)d).ToString(CultureInfo.InvariantCulture);
        // 17 significant digits, then %g's choice between fixed and exponential notation, trailing zeros removed.
        string e = d.ToString("E16", CultureInfo.InvariantCulture);
        int ePos = e.IndexOf('E');
        int exponent = int.Parse(e.Substring(ePos + 1), CultureInfo.InvariantCulture);
        bool negative = e[0] == '-';
        string digits = e.Substring(negative ? 1 : 0, ePos - (negative ? 1 : 0)).Replace(".", "");
        string result;
        if (exponent < -4 || exponent >= 17)
        {
            string mantissa = (digits.Substring(0, 1) + "." + digits.Substring(1)).TrimEnd('0').TrimEnd('.');
            result = mantissa + "e" + (exponent < 0 ? "-" : "+") + Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture);
        }
        else if (exponent < 0) { result = ("0." + new string('0', -exponent - 1) + digits).TrimEnd('0').TrimEnd('.'); }
        else
        {
            string integer = digits.Substring(0, exponent + 1), fraction = digits.Substring(exponent + 1);
            result = (integer + "." + fraction).TrimEnd('0').TrimEnd('.');
        }
        return negative ? "-" + result : result;
    }
    private static readonly Comparer<string> keyOrder = Comparer<string>.Create((a, b) =>
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) { i++; }
                while (j < b.Length && char.IsAsciiDigit(b[j])) { j++; }
                var order = decimal.Parse(a[si..i], CultureInfo.InvariantCulture).CompareTo(decimal.Parse(b[sj..j], CultureInfo.InvariantCulture));
                if (order != 0) { return order; }
                continue;
            }
            var c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
            if (c != 0) { return c; }
            i++; j++;
        }
        var length = (a.Length - i).CompareTo(b.Length - j);
        return length != 0 ? length : string.CompareOrdinal(a, b);
    });
}

/// <summary>ProcessInfo.processInfo: systemUptime (seconds, monotonic), arguments (CommandLine.cs), environment.</summary>
public sealed class ProcessInfo
{
    public static readonly ProcessInfo processInfo = new();
    /// <summary>systemUptime. PORT: Godot's monotonic clock (seconds since the engine started), not the time since boot;
    /// only differences and values from the same run are meaningful, as the game uses them.</summary>
    public double systemUptime => Time.GetTicksUsec() / 1e6;
    public string[] arguments => CommandLine.arguments;
    public enum ThermalState { nominal = 0, fair = 1, serious = 2, critical = 3 }
    /// <summary>thermalState: [NSProcessInfo processInfo].thermalState on macOS (through the Objective-C runtime).
    /// PORT: other platforms report .nominal (no portable equivalent).</summary>
    public ThermalState thermalState
    {
        get
        {
            if (!OperatingSystem.IsMacOS()) return ThermalState.nominal;
            try
            {
                var info = ObjC.msgSend(ObjC.objc_getClass("NSProcessInfo"), ObjC.sel_registerName("processInfo"));
                return info == IntPtr.Zero ? ThermalState.nominal : (ThermalState)(long)ObjC.msgSend(info, ObjC.sel_registerName("thermalState"));
            }
            catch (Exception) { return ThermalState.nominal; }
        }
    }
    private static class ObjC
    {
        [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib")] internal static extern IntPtr objc_getClass(string name);
        [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib")] internal static extern IntPtr sel_registerName(string name);
        [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static extern IntPtr msgSend(IntPtr receiver, IntPtr selector);
    }
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

/// <summary>
/// Foundation Timer, for the app's 1/60 s tick. PORT (PORTING.md: Timer -> _Process): the owner calls its tick from
/// Godot's per-frame <c>_Process</c> while <c>isValid</c>; <c>invalidate()</c> stops it, as in Foundation.
/// </summary>
public sealed class Timer
{
    public readonly double timeInterval;
    public bool isValid { get; private set; } = true;
    public Timer(double timeInterval) { this.timeInterval = timeInterval; }
    public void invalidate() => isValid = false;
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

/// <summary>FileHandle.standardOutput: <c>write(_ data:)</c> puts UTF-8 bytes on stdout unchanged (no added newline).</summary>
public sealed class FileHandle
{
    public static readonly FileHandle standardOutput = new();
    private FileHandle() { }
    public void write(byte[] data) => GD.PrintRaw(System.Text.Encoding.UTF8.GetString(data));
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
