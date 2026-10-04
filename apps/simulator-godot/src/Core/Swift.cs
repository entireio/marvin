using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Marvin.Core;

/// <summary>
/// PORT: Swift standard library and Darwin semantics that differ from .NET defaults.
/// Use with <c>using static Marvin.Core.Swift;</c> so ported code reads like the Swift
/// (<c>max(0, min(1, x))</c>, <c>hypot(dx, dz)</c>, <c>sin(a)</c>).
/// <list type="bullet">
/// <item><c>max(x, y)</c> is Swift's generic <c>y &gt;= x ? y : x</c> and <c>min(x, y)</c> is
/// <c>y &lt; x ? y : x</c>. They differ from <see cref="Math.Max(double,double)"/> for NaN and
/// signed zeros, so <see cref="Math.Max(double,double)"/>/<see cref="Math.Min(double,double)"/> must not be used.</item>
/// <item><c>hypot</c> calls the platform C library (Darwin libm on macOS, the same function Swift
/// calls). .NET's <see cref="double.Hypot"/> is a managed implementation that can differ in the
/// last bit; it is only the fallback when libm cannot be loaded.</item>
/// <item>Transcendentals forward to <see cref="Math"/>/<see cref="MathF"/>, which call the C runtime
/// (Darwin libm on macOS, the same as Swift).</item>
/// <item><c>sorted</c> is stable like Swift's sort; <see cref="Array.Sort(Array)"/> is not.</item>
/// <item><c>minBy</c>/<c>maxBy</c> follow <c>Sequence.min(by:)</c>/<c>max(by:)</c>: the first
/// minimum and the last maximum win.</item>
/// </list>
/// </summary>
public static class Swift
{
    public const double pi = Math.PI;
    /// <summary>
    /// Swift <c>Float.pi</c>: pi rounded toward zero, 0x40490FDA = 3.1415925 (measured). <c>MathF.PI</c> is the
    /// nearest float, 0x40490FDB, one ulp larger, so <c>Float.pi</c> arithmetic must use this constant.
    /// (<c>Double.pi</c> equals <c>Math.PI</c>: the nearest double is already below pi.)
    /// </summary>
    public static readonly float floatPi = BitConverter.Int32BitsToSingle(0x40490FDA);

    // MARK: Comparable max/min (Swift.max / Swift.min)

    public static double max(double x, double y) => y >= x ? y : x;
    public static double min(double x, double y) => y < x ? y : x;
    public static float max(float x, float y) => y >= x ? y : x;
    public static float min(float x, float y) => y < x ? y : x;
    public static int max(int x, int y) => y >= x ? y : x;
    public static int min(int x, int y) => y < x ? y : x;
    public static long max(long x, long y) => y >= x ? y : x;
    public static long min(long x, long y) => y < x ? y : x;

    public static double max(double x, double y, double z, params double[] rest)
    {
        var maxValue = max(max(x, y), z);
        foreach (var value in rest) if (value >= maxValue) maxValue = value;
        return maxValue;
    }
    public static double min(double x, double y, double z, params double[] rest)
    {
        var minValue = min(min(x, y), z);
        foreach (var value in rest) if (value < minValue) minValue = value;
        return minValue;
    }
    public static float max(float x, float y, float z, params float[] rest)
    {
        var maxValue = max(max(x, y), z);
        foreach (var value in rest) if (value >= maxValue) maxValue = value;
        return maxValue;
    }
    public static float min(float x, float y, float z, params float[] rest)
    {
        var minValue = min(min(x, y), z);
        foreach (var value in rest) if (value < minValue) minValue = value;
        return minValue;
    }
    public static int max(int x, int y, int z, params int[] rest)
    {
        var maxValue = max(max(x, y), z);
        foreach (var value in rest) if (value >= maxValue) maxValue = value;
        return maxValue;
    }
    public static int min(int x, int y, int z, params int[] rest)
    {
        var minValue = min(min(x, y), z);
        foreach (var value in rest) if (value < minValue) minValue = value;
        return minValue;
    }

    // MARK: Darwin math

    public static double abs(double x) => Math.Abs(x);
    public static float abs(float x) => MathF.Abs(x);
    public static int abs(int x) => Math.Abs(x);
    public static long abs(long x) => Math.Abs(x);
    public static double sqrt(double x) => Math.Sqrt(x);
    public static float sqrt(float x) => MathF.Sqrt(x);
    public static double sin(double x) => Math.Sin(x);
    public static float sin(float x) => MathF.Sin(x);
    public static double cos(double x) => Math.Cos(x);
    public static float cos(float x) => MathF.Cos(x);
    public static double tan(double x) => Math.Tan(x);
    public static float tan(float x) => MathF.Tan(x);
    public static double asin(double x) => Math.Asin(x);
    public static double acos(double x) => Math.Acos(x);
    public static float acos(float x) => MathF.Acos(x);
    public static double atan(double x) => Math.Atan(x);
    public static float atan(float x) => MathF.Atan(x);
    public static double atan2(double y, double x) => Math.Atan2(y, x);
    public static float atan2(float y, float x) => MathF.Atan2(y, x);
    public static double exp(double x) => Math.Exp(x);
    public static float exp(float x) => MathF.Exp(x);
    public static double log(double x) => Math.Log(x);
    public static float log(float x) => MathF.Log(x);
    public static double pow(double x, double y) => Math.Pow(x, y);
    public static float pow(float x, float y) => MathF.Pow(x, y);
    public static double floor(double x) => Math.Floor(x);
    public static float floor(float x) => MathF.Floor(x);
    public static double ceil(double x) => Math.Ceiling(x);
    public static float ceil(float x) => MathF.Ceiling(x);
    public static double fmod(double x, double y) => x % y;
    public static double fma(double x, double y, double z) => Math.FusedMultiplyAdd(x, y, z);
    public static float fma(float x, float y, float z) => MathF.FusedMultiplyAdd(x, y, z);

    /// <summary>Swift <c>x.rounded()</c>: round half away from zero.</summary>
    public static double rounded(double x) => Math.Round(x, MidpointRounding.AwayFromZero);
    /// <summary>Swift <c>x.rounded()</c> on Float.</summary>
    public static float rounded(float x) => MathF.Round(x, MidpointRounding.AwayFromZero);

    /// <summary>Darwin <c>hypot</c> (libm). Falls back to <see cref="double.Hypot"/> if libm is unavailable.</summary>
    public static unsafe double hypot(double x, double y)
    {
        var f = Libm.hypot;
        return f != null ? f(x, y) : double.Hypot(x, y);
    }

    /// <summary>Darwin <c>hypotf</c> (libm). Falls back to <see cref="float.Hypot"/> if libm is unavailable.</summary>
    public static unsafe float hypot(float x, float y)
    {
        var f = Libm.hypotf;
        return f != null ? f(x, y) : float.Hypot(x, y);
    }

    /// <summary>True when <see cref="hypot(double,double)"/> uses the platform C library.</summary>
    public static unsafe bool usesNativeHypot => Libm.hypot != null;

    /// <summary>Swift <c>Double.ulp</c> (NaN for non-finite values; subnormals give the least subnormal).</summary>
    public static double ulp(double x)
    {
        if (!double.IsFinite(x)) return double.NaN;
        if (double.IsNormal(x))
        {
            var bits = BitConverter.DoubleToInt64Bits(x) & BitConverter.DoubleToInt64Bits(double.PositiveInfinity);
            return BitConverter.Int64BitsToDouble(bits) * 2.220446049250313e-16 /* 2^-52 */;
        }
        return 2.2250738585072014e-308 * 2.220446049250313e-16 /* 2^-52 */;
    }

    /// <summary>Swift <c>Float.ulp</c>.</summary>
    public static float ulp(float x)
    {
        if (!float.IsFinite(x)) return float.NaN;
        if (float.IsNormal(x))
        {
            var bits = BitConverter.SingleToInt32Bits(x) & BitConverter.SingleToInt32Bits(float.PositiveInfinity);
            return BitConverter.Int32BitsToSingle(bits) * 1.1920929e-07f /* 2^-23 */;
        }
        return 1.17549435e-38f * 1.1920929e-07f /* 2^-23 */;
    }

    // MARK: Preconditions

    /// <summary>Swift <c>precondition</c>. Swift traps; the port throws.</summary>
    public static void precondition(bool condition, string message = "Precondition failed")
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>Swift <c>preconditionFailure</c>. Use as <c>throw preconditionFailure("...")</c>.</summary>
    public static Exception preconditionFailure(string message) => new InvalidOperationException(message);

    // MARK: Sequence algorithms with Swift semantics

    /// <summary>Swift <c>sorted(by:)</c>: a stable sort ordered by <paramref name="areInIncreasingOrder"/>.</summary>
    public static T[] sorted<T>(IEnumerable<T> source, Func<T, T, bool> areInIncreasingOrder)
    {
        var items = new List<T>(source).ToArray();
        if (items.Length < 2) return items;
        var buffer = new T[items.Length];
        MergeSort(items, buffer, 0, items.Length, areInIncreasingOrder);
        return items;
    }

    /// <summary>Swift <c>sorted()</c> for integers (ascending).</summary>
    public static int[] sorted(IEnumerable<int> source) => sorted(source, (a, b) => a < b);

    private static void MergeSort<T>(T[] items, T[] buffer, int start, int end, Func<T, T, bool> less)
    {
        if (end - start < 2) return;
        if (end - start <= 12)
        {
            // Stable insertion sort: move an element left only past strictly greater ones.
            for (var i = start + 1; i < end; i++)
            {
                var value = items[i];
                var j = i - 1;
                while (j >= start && less(value, items[j])) { items[j + 1] = items[j]; j--; }
                items[j + 1] = value;
            }
            return;
        }
        var middle = start + (end - start) / 2;
        MergeSort(items, buffer, start, middle, less);
        MergeSort(items, buffer, middle, end, less);
        int a = start, b = middle, k = start;
        while (a < middle && b < end)
        {
            // Take from the right run only when it is strictly smaller: stable.
            if (less(items[b], items[a])) buffer[k++] = items[b++];
            else buffer[k++] = items[a++];
        }
        while (a < middle) buffer[k++] = items[a++];
        while (b < end) buffer[k++] = items[b++];
        Array.Copy(buffer, start, items, start, end - start);
    }

    /// <summary>Swift <c>min(by:)</c>: the first element no later element is ordered before.</summary>
    public static bool minBy<T>(IEnumerable<T> source, Func<T, T, bool> areInIncreasingOrder, out T result)
    {
        using var it = source.GetEnumerator();
        if (!it.MoveNext()) { result = default; return false; }
        result = it.Current;
        while (it.MoveNext())
        {
            var e = it.Current;
            if (areInIncreasingOrder(e, result)) result = e;
        }
        return true;
    }

    /// <summary>Swift <c>max(by:)</c>: replaces the result whenever it is ordered before a later element.</summary>
    public static bool maxBy<T>(IEnumerable<T> source, Func<T, T, bool> areInIncreasingOrder, out T result)
    {
        using var it = source.GetEnumerator();
        if (!it.MoveNext()) { result = default; return false; }
        result = it.Current;
        while (it.MoveNext())
        {
            var e = it.Current;
            if (areInIncreasingOrder(result, e)) result = e;
        }
        return true;
    }

    /// <summary>Swift <c>[Double].min()</c>; null when empty.</summary>
    public static double? minElement(IEnumerable<double> source) =>
        minBy(source, (a, b) => a < b, out var r) ? r : null;

    /// <summary>Swift <c>[Double].max()</c>; null when empty.</summary>
    public static double? maxElement(IEnumerable<double> source) =>
        maxBy(source, (a, b) => a < b, out var r) ? r : null;

    // MARK: stride (Swift StrideTo / StrideThrough)

    /// <summary>
    /// Swift <c>stride(from:through:by:)</c> for Double: value k is
    /// <c>start.addingProduct(Double(k), stride)</c>, a fused multiply-add (Stride.swift), and the end
    /// is included only when hit exactly.
    /// </summary>
    public static IEnumerable<double> stride(double from, double through, double by)
    {
        long index = 0;
        var value = from;
        var didReturnEnd = false;
        while (true)
        {
            var result = value;
            if (by > 0 ? result >= through : result <= through)
            {
                if (result == through && !didReturnEnd) { didReturnEnd = true; yield return result; continue; }
                yield break;
            }
            index += 1;
            value = Math.FusedMultiplyAdd((double)index, by, from);
            yield return result;
        }
    }

    /// <summary>Swift <c>stride(from:to:by:)</c> for Double (end excluded).</summary>
    public static IEnumerable<double> strideTo(double from, double to, double by)
    {
        long index = 0;
        var value = from;
        while (by > 0 ? value < to : value > to)
        {
            yield return value;
            index += 1;
            value = Math.FusedMultiplyAdd((double)index, by, from);
        }
    }

    /// <summary>Swift <c>stride(from:through:by:)</c> for Int.</summary>
    public static IEnumerable<int> stride(int from, int through, int by)
    {
        for (var value = from; by > 0 ? value <= through : value >= through; value += by) yield return value;
    }

    /// <summary>Swift <c>stride(from:to:by:)</c> for Int.</summary>
    public static IEnumerable<int> strideTo(int from, int to, int by)
    {
        for (var value = from; by > 0 ? value < to : value > to; value += by) yield return value;
    }

    // MARK: Swift `description` formatting (print / string interpolation)

    /// <summary>Swift <c>Double.description</c>, e.g. <c>1.0</c>, <c>0.0001</c>, <c>1e-05</c>, <c>1e+16</c>, <c>inf</c>, <c>nan</c>.</summary>
    public static string description(double value)
    {
        // PORT: Swift prints "-nan" for a NaN with the sign bit set. .NET's double.NaN constant has the sign
        // bit set while Swift's .nan does not (and arm64 arithmetic produces positive NaNs), so every NaN
        // prints as "nan" here.
        if (double.IsNaN(value)) return "nan";
        if (double.IsInfinity(value)) return value < 0 ? "-inf" : "inf";
        if (value == 0) return BitConverter.DoubleToInt64Bits(value) < 0 ? "-0.0" : "0.0";
        // Shortest round-trip digits (same contract as SwiftDtoa), reformatted to Swift's layout.
        ShortestDigits(value.ToString("R", CultureInfo.InvariantCulture), out var digits, out var exponent);
        var exponential = exponent < -3 || Math.Abs(value) > 9007199254740992.0 /* 2^53 */;
        return Format(value < 0, digits, exponent, exponential);
    }

    /// <summary>Swift <c>Float.description</c>.</summary>
    public static string description(float value)
    {
        if (float.IsNaN(value)) return "nan";
        if (float.IsInfinity(value)) return value < 0 ? "-inf" : "inf";
        if (value == 0) return BitConverter.SingleToInt32Bits(value) < 0 ? "-0.0" : "0.0";
        ShortestDigits(value.ToString("R", CultureInfo.InvariantCulture), out var digits, out var exponent);
        var exponential = exponent < -3 || MathF.Abs(value) > 16777216f /* 2^24 */;
        return Format(value < 0, digits, exponent, exponential);
    }

    /// <summary>Swift <c>[Double].description</c>, e.g. <c>[1.0, 2.5]</c>.</summary>
    public static string description(IEnumerable<double> values)
    {
        var sb = new StringBuilder("[");
        var first = true;
        foreach (var v in values) { if (!first) sb.Append(", "); sb.Append(description(v)); first = false; }
        return sb.Append(']').ToString();
    }

    /// <summary>Swift <c>[Int].description</c>, e.g. <c>[1, 2]</c>.</summary>
    public static string description(IEnumerable<int> values)
    {
        // Invariant culture: Swift always prints an ASCII minus; .NET's sv-SE, nb-NO, fi-FI, ... print U+2212.
        var sb = new StringBuilder("[");
        var first = true;
        foreach (var v in values) { if (!first) sb.Append(", "); sb.Append(v.ToString(CultureInfo.InvariantCulture)); first = false; }
        return sb.Append(']').ToString();
    }

    // MARK: Foundation String(format:) (printf conversions used by the game)

    /// <summary>
    /// Foundation <c>String(format:)</c> for the conversions the game uses: <c>%d %i %ld %lld %u</c>,
    /// <c>%f %F</c>, <c>%s %@</c> and <c>%%</c>, with the flags <c>- + space 0</c>, a width and a
    /// precision (e.g. <c>%.3f</c>, <c>%05.2f</c>, <c>%3.0f</c>, <c>%02d</c>). <c>%f</c> is formatted like Darwin
    /// printf: the exact binary value rounded half-to-even (.NET's "F" format rounds differently).
    /// </summary>
    public static string format(string format, params object[] args)
    {
        var sb = new StringBuilder();
        var next = 0;
        for (var i = 0; i < format.Length; i++)
        {
            var ch = format[i];
            if (ch != '%') { sb.Append(ch); continue; }
            i++;
            if (i >= format.Length) throw new FormatException("Incomplete format specifier");
            if (format[i] == '%') { sb.Append('%'); continue; }
            bool left = false, plus = false, space = false, zero = false;
            for (; i < format.Length && "-+ 0#".IndexOf(format[i]) >= 0; i++)
            {
                switch (format[i]) { case '-': left = true; break; case '+': plus = true; break; case ' ': space = true; break; case '0': zero = true; break; }
            }
            var width = 0;
            for (; i < format.Length && char.IsDigit(format[i]); i++) width = width * 10 + (format[i] - '0');
            var precision = -1;
            if (i < format.Length && format[i] == '.')
            {
                precision = 0;
                for (i++; i < format.Length && char.IsDigit(format[i]); i++) precision = precision * 10 + (format[i] - '0');
            }
            while (i < format.Length && (format[i] == 'l' || format[i] == 'h' || format[i] == 'q' || format[i] == 'z')) i++;
            if (i >= format.Length) throw new FormatException("Incomplete format specifier");
            var conversion = format[i];
            var arg = args[next++];
            string body;
            var negative = false;
            var numeric = true;
            switch (conversion)
            {
                case 'd': case 'i': case 'u':
                {
                    var value = Convert.ToInt64(arg, CultureInfo.InvariantCulture);
                    negative = value < 0;
                    body = negative ? ((ulong)(-(value + 1)) + 1).ToString(CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);
                    if (precision >= 0) { body = body.PadLeft(precision, '0'); if (precision == 0 && value == 0) body = ""; zero = false; }
                    break;
                }
                case 'f': case 'F':
                {
                    var value = Convert.ToDouble(arg, CultureInfo.InvariantCulture);
                    negative = BitConverter.DoubleToInt64Bits(value) < 0 && !double.IsNaN(value);
                    if (double.IsNaN(value)) { body = conversion == 'F' ? "NAN" : "nan"; zero = false; }
                    else if (double.IsInfinity(value)) { body = conversion == 'F' ? "INF" : "inf"; zero = false; }
                    else body = fixedDigits(Math.Abs(value), precision < 0 ? 6 : precision);
                    break;
                }
                case 's': case '@':
                    body = arg is double d ? description(d) : arg is float f ? description(f) : Convert.ToString(arg, CultureInfo.InvariantCulture) ?? "(null)";
                    if (precision >= 0 && conversion == 's' && body.Length > precision) body = body.Substring(0, precision);
                    numeric = false;
                    break;
                default:
                    throw new FormatException($"Unsupported conversion %{conversion}");
            }
            var sign = numeric ? (negative ? "-" : plus ? "+" : space ? " " : "") : "";
            var padding = width - sign.Length - body.Length;
            if (padding <= 0) sb.Append(sign).Append(body);
            else if (left) sb.Append(sign).Append(body).Append(' ', padding);
            else if (zero && numeric) sb.Append(sign).Append('0', padding).Append(body);
            else sb.Append(' ', padding).Append(sign).Append(body);
        }
        return sb.ToString();
    }

    /// <summary>Digits of a finite, non-negative double with <paramref name="precision"/> decimals, rounded half-to-even from its exact binary value.</summary>
    private static string fixedDigits(double magnitude, int precision)
    {
        var bits = BitConverter.DoubleToInt64Bits(magnitude);
        var exponentBits = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xFFFFFFFFFFFFFL;
        int exponent;
        if (exponentBits == 0) exponent = -1074;
        else { mantissa |= 1L << 52; exponent = exponentBits - 1075; }
        System.Numerics.BigInteger numerator = mantissa, denominator = System.Numerics.BigInteger.One;
        if (exponent > 0) numerator <<= exponent; else denominator <<= -exponent;
        numerator *= System.Numerics.BigInteger.Pow(10, precision);
        var quotient = System.Numerics.BigInteger.DivRem(numerator, denominator, out var remainder);
        var twice = remainder * 2;
        if (twice > denominator || (twice == denominator && !quotient.IsEven)) quotient += 1;
        var digits = quotient.ToString(CultureInfo.InvariantCulture).PadLeft(precision + 1, '0');
        return precision == 0 ? digits : digits.Substring(0, digits.Length - precision) + "." + digits.Substring(digits.Length - precision);
    }

    /// <summary>Decimal digits without sign/point, and exponent e such that value = 0.d1d2... x 10^e.</summary>
    private static void ShortestDigits(string roundTrip, out string digits, out int exponent)
    {
        var s = roundTrip.TrimStart('-');
        var e = 0;
        var ePos = s.IndexOfAny(new[] { 'E', 'e' });
        if (ePos >= 0) { e = int.Parse(s.Substring(ePos + 1), CultureInfo.InvariantCulture); s = s.Substring(0, ePos); }
        var dot = s.IndexOf('.');
        var intPart = dot >= 0 ? s.Substring(0, dot) : s;
        var fracPart = dot >= 0 ? s.Substring(dot + 1) : "";
        var all = intPart + fracPart;
        var pointPos = intPart.Length + e; // digits before the decimal point
        var lead = 0;
        while (lead < all.Length - 1 && all[lead] == '0') { lead++; pointPos--; }
        all = all.Substring(lead).TrimEnd('0');
        if (all.Length == 0) all = "0";
        digits = all;
        exponent = pointPos;
    }

    private static string Format(bool negative, string digits, int exponent, bool exponential)
    {
        var sb = new StringBuilder();
        if (negative) sb.Append('-');
        if (exponential)
        {
            sb.Append(digits[0]);
            if (digits.Length > 1) sb.Append('.').Append(digits, 1, digits.Length - 1);
            var e = exponent - 1;
            sb.Append('e').Append(e < 0 ? '-' : '+');
            var magnitude = Math.Abs(e).ToString(CultureInfo.InvariantCulture);
            if (magnitude.Length < 2) sb.Append('0');
            sb.Append(magnitude);
        }
        else if (exponent <= 0)
        {
            sb.Append("0.").Append('0', -exponent).Append(digits);
        }
        else if (exponent >= digits.Length)
        {
            sb.Append(digits).Append('0', exponent - digits.Length).Append(".0");
        }
        else
        {
            sb.Append(digits, 0, exponent).Append('.').Append(digits, exponent, digits.Length - exponent);
        }
        return sb.ToString();
    }

    // MARK: libm binding

    private static unsafe class Libm
    {
        internal static readonly delegate* unmanaged[SuppressGCTransition]<double, double, double> hypot;
        internal static readonly delegate* unmanaged[SuppressGCTransition]<float, float, float> hypotf;

        static Libm()
        {
            string[] candidates = OperatingSystem.IsMacOS() || OperatingSystem.IsIOS()
                ? new[] { "/usr/lib/libSystem.B.dylib", "libm.dylib" }
                : OperatingSystem.IsWindows()
                    ? new[] { "ucrtbase.dll", "msvcrt.dll" }
                    : new[] { "libm.so.6", "libm.so" };
            foreach (var name in candidates)
            {
                if (!NativeLibrary.TryLoad(name, out var library)) continue;
                if (NativeLibrary.TryGetExport(library, "hypot", out var p) || NativeLibrary.TryGetExport(library, "_hypot", out p))
                    hypot = (delegate* unmanaged[SuppressGCTransition]<double, double, double>)p;
                if (NativeLibrary.TryGetExport(library, "hypotf", out var q) || NativeLibrary.TryGetExport(library, "_hypotf", out q))
                    hypotf = (delegate* unmanaged[SuppressGCTransition]<float, float, float>)q;
                if (hypot != null) break;
            }
        }
    }
}
