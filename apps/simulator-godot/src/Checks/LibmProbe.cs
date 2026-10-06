using System;
using System.Collections.Generic;
using System.IO;
using Marvin.Core;

namespace Marvin.Checks;

/// <summary>
/// Godot-port only (`tools/checks --libm-probe DIR [COUNT]`): the C library functions the simulation calls through
/// .NET's Math and MathF (Darwin libm on macOS, the Universal C Runtime on Windows, glibc on Linux), evaluated for
/// COUNT deterministic arguments each in the ranges the game uses. Writes DIR/NAME.bin per function: for every call the
/// argument bits and the result bits, little-endian (doubles 8 bytes, floats 4). Comparing two platforms' files
/// (tools/ci/libm-report.py, .github/workflows/godot-windows.yml) shows which functions round differently from
/// Darwin's, how often and by how many ulps: the source of SimulationChecks' numeric differences off macOS.
/// sqrt is correctly rounded everywhere (a control); hypot is Swift.hypot (Darwin libm on macOS, the portable
/// reproduction elsewhere), so it must match too.
/// </summary>
public static class LibmProbe
{
    public static int Run(string[] args)
    {
        int index = Array.IndexOf(args, "--libm-probe");
        string dir = index + 1 < args.Length && !args[index + 1].StartsWith("--") ? args[index + 1] : "libm-probe";
        int count = index + 2 < args.Length && int.TryParse(args[index + 2], out var n) ? n : 65536;
        Directory.CreateDirectory(dir);
        var random = new Random(20261007);
        double U(double lo, double hi) => lo + (hi - lo) * random.NextDouble();
        // Arguments come from exact arithmetic only (no libm), so every platform evaluates the same ones.
        double LogU(int lo, int hi) => Math.ScaleB(1 + random.NextDouble(), random.Next(lo, hi));
        double Angle() => random.Next(4) == 0 ? U(-1000, 1000) : U(-8 * Math.PI, 8 * Math.PI);

        var doubles = new List<(string name, int arity, Func<double[]> make, Func<double[], double> f)>
        {
            ("sin", 1, () => new[] { Angle() }, a => Math.Sin(a[0])),
            ("cos", 1, () => new[] { Angle() }, a => Math.Cos(a[0])),
            ("tan", 1, () => new[] { U(-1.55, 1.55) }, a => Math.Tan(a[0])),
            ("asin", 1, () => new[] { U(-1, 1) }, a => Math.Asin(a[0])),
            ("acos", 1, () => new[] { U(-1, 1) }, a => Math.Acos(a[0])),
            ("atan", 1, () => new[] { random.Next(2) == 0 ? U(-2, 2) : LogU(-10, 10) * (random.Next(2) * 2 - 1) }, a => Math.Atan(a[0])),
            ("atan2", 2, () => new[] { U(-500, 500), U(-500, 500) }, a => Math.Atan2(a[0], a[1])),
            ("exp", 1, () => new[] { U(-30, 10) }, a => Math.Exp(a[0])),
            ("log", 1, () => new[] { LogU(-20, 20) }, a => Math.Log(a[0])),
            ("pow", 2, () => new[] { U(0, 10), U(-4, 4) }, a => Math.Pow(a[0], a[1])),
            ("sqrt", 1, () => new[] { LogU(-20, 20) }, a => Math.Sqrt(a[0])),
            ("hypot", 2, () => new[] { U(-500, 500), U(-500, 500) }, a => Swift.hypot(a[0], a[1])),
        };
        foreach (var (name, arity, make, f) in doubles)
        {
            using var w = new BinaryWriter(File.Create(Path.Combine(dir, name + ".bin")));
            for (int i = 0; i < count; i++)
            {
                var a = make();
                foreach (var x in a) w.Write(BitConverter.DoubleToInt64Bits(x));
                w.Write(BitConverter.DoubleToInt64Bits(f(a)));
            }
        }
        float F(double lo, double hi) => (float)U(lo, hi);
        var floats = new List<(string name, int arity, Func<float[]> make, Func<float[], float> f)>
        {
            ("sinf", 1, () => new[] { (float)Angle() }, a => MathF.Sin(a[0])),
            ("cosf", 1, () => new[] { (float)Angle() }, a => MathF.Cos(a[0])),
            ("tanf", 1, () => new[] { F(-1.55, 1.55) }, a => MathF.Tan(a[0])),
            ("acosf", 1, () => new[] { F(-1, 1) }, a => MathF.Acos(a[0])),
            ("atanf", 1, () => new[] { F(-20, 20) }, a => MathF.Atan(a[0])),
            ("atan2f", 2, () => new[] { F(-500, 500), F(-500, 500) }, a => MathF.Atan2(a[0], a[1])),
            ("expf", 1, () => new[] { F(-30, 10) }, a => MathF.Exp(a[0])),
            ("logf", 1, () => new[] { (float)LogU(-20, 20) }, a => MathF.Log(a[0])),
            ("powf", 2, () => new[] { F(0, 10), F(-4, 4) }, a => MathF.Pow(a[0], a[1])),
            ("sqrtf", 1, () => new[] { (float)LogU(-20, 20) }, a => MathF.Sqrt(a[0])),
        };
        foreach (var (name, arity, make, f) in floats)
        {
            using var w = new BinaryWriter(File.Create(Path.Combine(dir, name + ".bin")));
            for (int i = 0; i < count; i++)
            {
                var a = make();
                foreach (var x in a) w.Write(BitConverter.SingleToInt32Bits(x));
                w.Write(BitConverter.SingleToInt32Bits(f(a)));
            }
        }
        Console.WriteLine($"libm-probe: {doubles.Count + floats.Count} functions x {count} calls in {dir} ({System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");
        return 0;
    }
}
