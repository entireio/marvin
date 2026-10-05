using System;
using System.Collections.Generic;
using Marvin.Core;

namespace Marvin.Checks;

/// <summary>
/// Godot-port only (`tools/checks --portable-math [millions]`): checks that the managed stand-ins for Darwin libm
/// (<see cref="Swift.portableHypot(double,double)"/>, <see cref="Swift.portableHypot(float,float)"/>), which the
/// game uses on Windows, return the same bits as Darwin's hypot and hypotf on this Mac. Needs macOS (it calls
/// libSystem); elsewhere it reports that there is nothing to compare against.
/// </summary>
public static class PortableMathChecks
{
    public static int Run(string[] args)
    {
        var native = Swift.nativeHypotForChecks();
        if (native == null)
        {
            Console.WriteLine("portable-math: Darwin libm is not available on this platform; nothing to compare against (run on macOS).");
            return 0;
        }
        var (hypot, hypotf) = native.Value;
        int index = Array.IndexOf(args, "--portable-math");
        double millions = index >= 0 && index + 1 < args.Length && double.TryParse(args[index + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : 4;
        int perRegime = Math.Max(1000, (int)(millions * 1_000_000 / 8));
        var random = new Random(20261005);
        bool passed = true;

        double Uniform(double lo, double hi) => lo + (hi - lo) * random.NextDouble();
        double Sign() => random.Next(2) == 0 ? 1 : -1;
        double LogUniform(double lo, double hi) => Sign() * Math.Pow(10, Uniform(Math.Log10(lo), Math.Log10(hi)));
        double AnyFinite() { while (true) { var v = BitConverter.Int64BitsToDouble(random.NextInt64()); if (double.IsFinite(v)) return v; } }
        double Subnormal() => Sign() * BitConverter.Int64BitsToDouble(random.NextInt64(1, 1L << 52));
        double Exponent(int lo, int hi) => Sign() * Math.ScaleB(1 + random.NextDouble(), random.Next(lo, hi));

        var doubleRegimes = new List<(string name, Func<(double, double)> make)>
        {
            ("game range (|x|, |y| <= 500 m)", () => (Uniform(-500, 500), Uniform(-500, 500))),
            ("magnitudes 1e-8 ... 1e3", () => (LogUniform(1e-8, 1e3), LogUniform(1e-8, 1e3))),
            ("near-equal magnitudes", () => { var x = LogUniform(1e-3, 1e3); return (x, x * (1 + Uniform(-1e-3, 1e-3)) * Sign()); }),
            ("integers and halves", () => (random.Next(-2000, 2001) * 0.5, random.Next(-2000, 2001) * 0.5)),
            ("any finite doubles", () => (AnyFinite(), AnyFinite())),
            ("subnormals and tiny", () => random.Next(3) switch { 0 => (Subnormal(), Subnormal()), 1 => (Subnormal(), Exponent(-1022, -900)), _ => (Exponent(-1074, -1000), Exponent(-1074, -1000)) }),
            ("scaling thresholds 2^+-500, 2^+-511", () => { int e = new[] { 500, -500, 511, -511, 499, -501 }[random.Next(6)]; return (Exponent(e - 1, e + 2), Exponent(e - 60, e + 2)); }),
            ("overflow boundary", () => (Exponent(1020, 1024), Exponent(990, 1024))),
        };
        foreach (var (name, make) in doubleRegimes)
        {
            long mismatches = 0; string example = null;
            for (int i = 0; i < perRegime; i++)
            {
                var (x, y) = make();
                double expected = hypot(x, y), actual = Swift.portableHypot(x, y);
                if (BitConverter.DoubleToInt64Bits(expected) != BitConverter.DoubleToInt64Bits(actual) && !(double.IsNaN(expected) && double.IsNaN(actual)))
                {
                    mismatches++;
                    example ??= $"hypot({x:R}, {y:R}) = {expected:R} (Darwin), {actual:R} (portable)";
                }
            }
            Report($"hypot, {name}", perRegime, mismatches, example, ref passed);
        }
        var specials = new[] { 0.0, -0.0, double.Epsilon, -double.Epsilon, double.MinValue, double.MaxValue, 1.0, -1.0, 3.0, 4.0, double.PositiveInfinity, double.NegativeInfinity, double.NaN, Math.ScaleB(1, -1022), Math.ScaleB(1, 1023), Math.ScaleB(1, 511), Math.ScaleB(1, 512), Math.ScaleB(1, -511), Math.ScaleB(1, 500), Math.ScaleB(1, -500) };
        {
            long mismatches = 0; string example = null;
            foreach (var x in specials) foreach (var y in specials)
            {
                double expected = hypot(x, y), actual = Swift.portableHypot(x, y);
                if (BitConverter.DoubleToInt64Bits(expected) != BitConverter.DoubleToInt64Bits(actual) && !(double.IsNaN(expected) && double.IsNaN(actual)))
                {
                    mismatches++;
                    example ??= $"hypot({x:R}, {y:R}) = {expected:R} (Darwin), {actual:R} (portable)";
                }
            }
            Report("hypot, special values (zeros, infinities, NaN, extremes)", specials.Length * specials.Length, mismatches, example, ref passed);
        }
        // What the portable function replaces: .NET's managed Hypot (the earlier fallback).
        {
            long differs = 0;
            for (int i = 0; i < perRegime; i++) { double x = Uniform(-500, 500), y = Uniform(-500, 500); if (hypot(x, y) != double.Hypot(x, y)) differs++; }
            Console.WriteLine($"portable-math: for reference, .NET double.Hypot differs from Darwin in {differs} of {perRegime} game-range calls ({100.0 * differs / perRegime:F2} %)");
        }

        float FloatAnyFinite() { while (true) { var v = BitConverter.Int32BitsToSingle(random.Next(int.MinValue, int.MaxValue)); if (float.IsFinite(v)) return v; } }
        var floatRegimes = new List<(string name, Func<(float, float)> make)>
        {
            ("game range", () => ((float)Uniform(-500, 500), (float)Uniform(-500, 500))),
            ("magnitudes 1e-6 ... 1e3", () => ((float)LogUniform(1e-6, 1e3), (float)LogUniform(1e-6, 1e3))),
            ("near-equal magnitudes", () => { var x = (float)LogUniform(1e-3, 1e3); return (x, (float)(x * (1 + Uniform(-1e-3, 1e-3)))); }),
            ("any finite floats", () => (FloatAnyFinite(), FloatAnyFinite())),
        };
        foreach (var (name, make) in floatRegimes)
        {
            long mismatches = 0; string example = null;
            for (int i = 0; i < perRegime; i++)
            {
                var (x, y) = make();
                float expected = hypotf(x, y), actual = Swift.portableHypot(x, y);
                if (BitConverter.SingleToInt32Bits(expected) != BitConverter.SingleToInt32Bits(actual) && !(float.IsNaN(expected) && float.IsNaN(actual)))
                {
                    mismatches++;
                    example ??= $"hypotf({x:R}, {y:R}) = {expected:R} (Darwin), {actual:R} (portable)";
                }
            }
            Report($"hypotf, {name}", perRegime, mismatches, example, ref passed);
        }
        var floatSpecials = new[] { 0f, -0f, float.Epsilon, float.MaxValue, float.MinValue, 1f, 3f, 4f, float.PositiveInfinity, float.NegativeInfinity, float.NaN };
        {
            long mismatches = 0; string example = null;
            foreach (var x in floatSpecials) foreach (var y in floatSpecials)
            {
                float expected = hypotf(x, y), actual = Swift.portableHypot(x, y);
                if (BitConverter.SingleToInt32Bits(expected) != BitConverter.SingleToInt32Bits(actual) && !(float.IsNaN(expected) && float.IsNaN(actual)))
                {
                    mismatches++;
                    example ??= $"hypotf({x:R}, {y:R}) = {expected:R} (Darwin), {actual:R} (portable)";
                }
            }
            Report("hypotf, special values", floatSpecials.Length * floatSpecials.Length, mismatches, example, ref passed);
        }
        Console.WriteLine(passed ? "PASS: portable hypot/hypotf are bit-identical to Darwin libm" : "FAIL: portable hypot/hypotf differ from Darwin libm");
        return passed ? 0 : 1;
    }

    private static void Report(string what, long count, long mismatches, string example, ref bool passed)
    {
        Console.WriteLine($"portable-math: {what}: {count - mismatches} of {count} identical" + (example != null ? $"; first difference {example}" : ""));
        if (mismatches != 0) passed = false;
    }
}
