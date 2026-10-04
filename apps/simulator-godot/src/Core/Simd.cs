using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace Marvin.Core;

// PORT: Apple simd (<simd/simd.h> as imported into Swift) for the types the macOS game uses:
// SIMD2/3/4<Double> -> Double2/3/4, SIMD2/3/4<Float> -> Float2/3/4, SIMD2<Int> -> Int2,
// simd_quatd/simd_quatf -> QuatD/QuatF, simd_double3x3 -> Double3x3, simd_float4x4 -> Float4x4.
//
// Lane-wise operators match Swift's SIMD operators (one IEEE operation per lane, no FMA;
// vector / scalar divides each lane, never multiplies by a reciprocal). Swift's SIMD prefix
// minus is measured to behave as `0 - v` (-SIMD2(0, 1) == (+0.0, -1.0)), so it is ported that way.
//
// Functions follow Apple's header bodies as compiled into Swift on arm64. Measured against
// Swift 6.4 (both -Onone and -O), Swift imports these C inline functions with clang's default
// fp-contract=on, so some of them use fused multiply-add:
//   simd_dot        lane products summed left to right (4 lanes: (x+z)+(y+w), simd_reduce_add)
//   simd_length     sqrt(dot(x,x));  simd_distance = length(x-y)
//   simd_normalize  x * (1/sqrt(dot(x,x)))   (Float: NEON rsqrt estimate + 2 Newton steps)
//   simd_cross      (x.zxy*y - x*y.zxy).zxy, each lane fma(a, b, -(c*d))       [FMA, measured]
//   quaternion *    #pragma STDC FP_CONTRACT ON in the header                    [FMA, measured]
//   simd_act        arm64 path: q * (v * conj(q)), both products fused           [FMA, measured]
//   simd_quaternion(double3x3) plain arithmetic                                   [measured]
//   simd_slerp      A*q0 + B*q1 contracted to fma(A, q0, B*q1)                   [measured]
//   Float header paths call sinf/cosf/atan2f (tgmath-style overloads)            [measured]
//   simd_mix        x + t*(y-x) -> fma(t, y-x, x)                                 [header, fp-contract=on]
//   matrix * vector c0*v.x, then fma(c1, v.y, r), fma(c2, v.z, r), ...             [header simd_muladd]
//   simd_min/max    fmin/fmax lanes (NaN-ignoring, -0 < +0) -> double.MinNumber/MaxNumber

/// <summary>SIMD2&lt;Double&gt;.</summary>
public struct Double2 : IEquatable<Double2>
{
    public double x, y;
    public Double2(double x, double y) { this.x = x; this.y = y; }
    /// <summary>Swift <c>SIMD2(repeating:)</c>.</summary>
    public Double2(double repeating) { x = repeating; y = repeating; }
    /// <summary>Swift <c>SIMD2&lt;Double&gt;(floatVector)</c>.</summary>
    public Double2(Float2 v) { x = v.x; y = v.y; }
    public static Double2 zero => default;
    public double this[int i]
    {
        readonly get => i switch { 0 => x, 1 => y, _ => throw new IndexOutOfRangeException() };
        set { switch (i) { case 0: x = value; break; case 1: y = value; break; default: throw new IndexOutOfRangeException(); } }
    }
    public static Double2 operator +(Double2 a, Double2 b) => new(a.x + b.x, a.y + b.y);
    public static Double2 operator -(Double2 a, Double2 b) => new(a.x - b.x, a.y - b.y);
    public static Double2 operator *(Double2 a, Double2 b) => new(a.x * b.x, a.y * b.y);
    public static Double2 operator /(Double2 a, Double2 b) => new(a.x / b.x, a.y / b.y);
    /// <summary>Swift SIMD prefix minus: measured to behave as <c>0 - a</c> per lane, so a +0 lane stays +0.</summary>
    public static Double2 operator -(Double2 a) => new(0 - a.x, 0 - a.y);
    public static Double2 operator +(Double2 a, double s) => new(a.x + s, a.y + s);
    public static Double2 operator -(Double2 a, double s) => new(a.x - s, a.y - s);
    public static Double2 operator *(Double2 a, double s) => new(a.x * s, a.y * s);
    public static Double2 operator /(Double2 a, double s) => new(a.x / s, a.y / s);
    public static Double2 operator +(double s, Double2 a) => new(s + a.x, s + a.y);
    public static Double2 operator -(double s, Double2 a) => new(s - a.x, s - a.y);
    public static Double2 operator *(double s, Double2 a) => new(s * a.x, s * a.y);
    public static Double2 operator /(double s, Double2 a) => new(s / a.x, s / a.y);
    /// <summary>Swift SIMD <c>==</c>: every lane compares equal (IEEE, so NaN != NaN).</summary>
    public static bool operator ==(Double2 a, Double2 b) => a.x == b.x && a.y == b.y;
    public static bool operator !=(Double2 a, Double2 b) => !(a == b);
    public readonly bool Equals(Double2 o) => x.Equals(o.x) && y.Equals(o.y);
    public override readonly bool Equals(object obj) => obj is Double2 o && Equals(o);
    public override readonly int GetHashCode() => HashCode.Combine(x, y);
    /// <summary>Swift description, e.g. <c>SIMD2&lt;Double&gt;(1.0, -2.5)</c>.</summary>
    public override readonly string ToString() => $"SIMD2<Double>({Swift.description(x)}, {Swift.description(y)})";
}

/// <summary>SIMD3&lt;Double&gt;.</summary>
public struct Double3 : IEquatable<Double3>
{
    public double x, y, z;
    public Double3(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
    public Double3(double repeating) { x = repeating; y = repeating; z = repeating; }
    public Double3(Double2 xy, double z) { x = xy.x; y = xy.y; this.z = z; }
    public Double3(Float3 v) { x = v.x; y = v.y; z = v.z; }
    public static Double3 zero => default;
    public readonly Double2 xy => new(x, y);
    public readonly Double2 xz => new(x, z);
    public double this[int i]
    {
        readonly get => i switch { 0 => x, 1 => y, 2 => z, _ => throw new IndexOutOfRangeException() };
        set { switch (i) { case 0: x = value; break; case 1: y = value; break; case 2: z = value; break; default: throw new IndexOutOfRangeException(); } }
    }
    public static Double3 operator +(Double3 a, Double3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
    public static Double3 operator -(Double3 a, Double3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
    public static Double3 operator *(Double3 a, Double3 b) => new(a.x * b.x, a.y * b.y, a.z * b.z);
    public static Double3 operator /(Double3 a, Double3 b) => new(a.x / b.x, a.y / b.y, a.z / b.z);
    /// <summary>Swift SIMD prefix minus: <c>0 - a</c> per lane (see <see cref="Double2"/>).</summary>
    public static Double3 operator -(Double3 a) => new(0 - a.x, 0 - a.y, 0 - a.z);
    public static Double3 operator +(Double3 a, double s) => new(a.x + s, a.y + s, a.z + s);
    public static Double3 operator -(Double3 a, double s) => new(a.x - s, a.y - s, a.z - s);
    public static Double3 operator *(Double3 a, double s) => new(a.x * s, a.y * s, a.z * s);
    public static Double3 operator /(Double3 a, double s) => new(a.x / s, a.y / s, a.z / s);
    public static Double3 operator +(double s, Double3 a) => new(s + a.x, s + a.y, s + a.z);
    public static Double3 operator -(double s, Double3 a) => new(s - a.x, s - a.y, s - a.z);
    public static Double3 operator *(double s, Double3 a) => new(s * a.x, s * a.y, s * a.z);
    public static Double3 operator /(double s, Double3 a) => new(s / a.x, s / a.y, s / a.z);
    public static bool operator ==(Double3 a, Double3 b) => a.x == b.x && a.y == b.y && a.z == b.z;
    public static bool operator !=(Double3 a, Double3 b) => !(a == b);
    public readonly bool Equals(Double3 o) => x.Equals(o.x) && y.Equals(o.y) && z.Equals(o.z);
    public override readonly bool Equals(object obj) => obj is Double3 o && Equals(o);
    public override readonly int GetHashCode() => HashCode.Combine(x, y, z);
    public override readonly string ToString() => $"SIMD3<Double>({Swift.description(x)}, {Swift.description(y)}, {Swift.description(z)})";
}

/// <summary>SIMD4&lt;Double&gt;.</summary>
public struct Double4 : IEquatable<Double4>
{
    public double x, y, z, w;
    public Double4(double x, double y, double z, double w) { this.x = x; this.y = y; this.z = z; this.w = w; }
    public Double4(double repeating) { x = repeating; y = repeating; z = repeating; w = repeating; }
    public Double4(Double3 xyz, double w) { x = xyz.x; y = xyz.y; z = xyz.z; this.w = w; }
    public Double4(Float4 v) { x = v.x; y = v.y; z = v.z; w = v.w; }
    public static Double4 zero => default;
    public readonly Double3 xyz => new(x, y, z);
    public double this[int i]
    {
        readonly get => i switch { 0 => x, 1 => y, 2 => z, 3 => w, _ => throw new IndexOutOfRangeException() };
        set { switch (i) { case 0: x = value; break; case 1: y = value; break; case 2: z = value; break; case 3: w = value; break; default: throw new IndexOutOfRangeException(); } }
    }
    public static Double4 operator +(Double4 a, Double4 b) => new(a.x + b.x, a.y + b.y, a.z + b.z, a.w + b.w);
    public static Double4 operator -(Double4 a, Double4 b) => new(a.x - b.x, a.y - b.y, a.z - b.z, a.w - b.w);
    public static Double4 operator *(Double4 a, Double4 b) => new(a.x * b.x, a.y * b.y, a.z * b.z, a.w * b.w);
    public static Double4 operator /(Double4 a, Double4 b) => new(a.x / b.x, a.y / b.y, a.z / b.z, a.w / b.w);
    public static Double4 operator -(Double4 a) => new(0 - a.x, 0 - a.y, 0 - a.z, 0 - a.w);
    public static Double4 operator +(Double4 a, double s) => new(a.x + s, a.y + s, a.z + s, a.w + s);
    public static Double4 operator -(Double4 a, double s) => new(a.x - s, a.y - s, a.z - s, a.w - s);
    public static Double4 operator *(Double4 a, double s) => new(a.x * s, a.y * s, a.z * s, a.w * s);
    public static Double4 operator /(Double4 a, double s) => new(a.x / s, a.y / s, a.z / s, a.w / s);
    public static Double4 operator *(double s, Double4 a) => new(s * a.x, s * a.y, s * a.z, s * a.w);
    public static bool operator ==(Double4 a, Double4 b) => a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;
    public static bool operator !=(Double4 a, Double4 b) => !(a == b);
    public readonly bool Equals(Double4 o) => x.Equals(o.x) && y.Equals(o.y) && z.Equals(o.z) && w.Equals(o.w);
    public override readonly bool Equals(object obj) => obj is Double4 o && Equals(o);
    public override readonly int GetHashCode() => HashCode.Combine(x, y, z, w);
    public override readonly string ToString() => $"SIMD4<Double>({Swift.description(x)}, {Swift.description(y)}, {Swift.description(z)}, {Swift.description(w)})";
}

/// <summary>SIMD2&lt;Float&gt;.</summary>
public struct Float2 : IEquatable<Float2>
{
    public float x, y;
    public Float2(float x, float y) { this.x = x; this.y = y; }
    public Float2(float repeating) { x = repeating; y = repeating; }
    /// <summary>Swift <c>SIMD2&lt;Float&gt;(doubleVector)</c> (round each lane to nearest).</summary>
    public Float2(Double2 v) { x = (float)v.x; y = (float)v.y; }
    public static Float2 zero => default;
    public float this[int i]
    {
        readonly get => i switch { 0 => x, 1 => y, _ => throw new IndexOutOfRangeException() };
        set { switch (i) { case 0: x = value; break; case 1: y = value; break; default: throw new IndexOutOfRangeException(); } }
    }
    public static Float2 operator +(Float2 a, Float2 b) => new(a.x + b.x, a.y + b.y);
    public static Float2 operator -(Float2 a, Float2 b) => new(a.x - b.x, a.y - b.y);
    public static Float2 operator *(Float2 a, Float2 b) => new(a.x * b.x, a.y * b.y);
    public static Float2 operator /(Float2 a, Float2 b) => new(a.x / b.x, a.y / b.y);
    public static Float2 operator -(Float2 a) => new(0 - a.x, 0 - a.y);
    public static Float2 operator +(Float2 a, float s) => new(a.x + s, a.y + s);
    public static Float2 operator -(Float2 a, float s) => new(a.x - s, a.y - s);
    public static Float2 operator *(Float2 a, float s) => new(a.x * s, a.y * s);
    public static Float2 operator /(Float2 a, float s) => new(a.x / s, a.y / s);
    public static Float2 operator *(float s, Float2 a) => new(s * a.x, s * a.y);
    public static bool operator ==(Float2 a, Float2 b) => a.x == b.x && a.y == b.y;
    public static bool operator !=(Float2 a, Float2 b) => !(a == b);
    public readonly bool Equals(Float2 o) => x.Equals(o.x) && y.Equals(o.y);
    public override readonly bool Equals(object obj) => obj is Float2 o && Equals(o);
    public override readonly int GetHashCode() => HashCode.Combine(x, y);
    public override readonly string ToString() => $"SIMD2<Float>({Swift.description(x)}, {Swift.description(y)})";
}

/// <summary>SIMD3&lt;Float&gt;.</summary>
public struct Float3 : IEquatable<Float3>
{
    public float x, y, z;
    public Float3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    public Float3(float repeating) { x = repeating; y = repeating; z = repeating; }
    public Float3(Float2 xy, float z) { x = xy.x; y = xy.y; this.z = z; }
    public Float3(Double3 v) { x = (float)v.x; y = (float)v.y; z = (float)v.z; }
    public static Float3 zero => default;
    public readonly Float2 xy => new(x, y);
    public readonly Float2 xz => new(x, z);
    public float this[int i]
    {
        readonly get => i switch { 0 => x, 1 => y, 2 => z, _ => throw new IndexOutOfRangeException() };
        set { switch (i) { case 0: x = value; break; case 1: y = value; break; case 2: z = value; break; default: throw new IndexOutOfRangeException(); } }
    }
    public static Float3 operator +(Float3 a, Float3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
    public static Float3 operator -(Float3 a, Float3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
    public static Float3 operator *(Float3 a, Float3 b) => new(a.x * b.x, a.y * b.y, a.z * b.z);
    public static Float3 operator /(Float3 a, Float3 b) => new(a.x / b.x, a.y / b.y, a.z / b.z);
    public static Float3 operator -(Float3 a) => new(0 - a.x, 0 - a.y, 0 - a.z);
    public static Float3 operator +(Float3 a, float s) => new(a.x + s, a.y + s, a.z + s);
    public static Float3 operator -(Float3 a, float s) => new(a.x - s, a.y - s, a.z - s);
    public static Float3 operator *(Float3 a, float s) => new(a.x * s, a.y * s, a.z * s);
    public static Float3 operator /(Float3 a, float s) => new(a.x / s, a.y / s, a.z / s);
    public static Float3 operator *(float s, Float3 a) => new(s * a.x, s * a.y, s * a.z);
    public static bool operator ==(Float3 a, Float3 b) => a.x == b.x && a.y == b.y && a.z == b.z;
    public static bool operator !=(Float3 a, Float3 b) => !(a == b);
    public readonly bool Equals(Float3 o) => x.Equals(o.x) && y.Equals(o.y) && z.Equals(o.z);
    public override readonly bool Equals(object obj) => obj is Float3 o && Equals(o);
    public override readonly int GetHashCode() => HashCode.Combine(x, y, z);
    public override readonly string ToString() => $"SIMD3<Float>({Swift.description(x)}, {Swift.description(y)}, {Swift.description(z)})";
}

/// <summary>SIMD4&lt;Float&gt;.</summary>
public struct Float4 : IEquatable<Float4>
{
    public float x, y, z, w;
    public Float4(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
    public Float4(float repeating) { x = repeating; y = repeating; z = repeating; w = repeating; }
    public Float4(Float3 xyz, float w) { x = xyz.x; y = xyz.y; z = xyz.z; this.w = w; }
    public Float4(Double4 v) { x = (float)v.x; y = (float)v.y; z = (float)v.z; w = (float)v.w; }
    public static Float4 zero => default;
    public readonly Float3 xyz => new(x, y, z);
    public float this[int i]
    {
        readonly get => i switch { 0 => x, 1 => y, 2 => z, 3 => w, _ => throw new IndexOutOfRangeException() };
        set { switch (i) { case 0: x = value; break; case 1: y = value; break; case 2: z = value; break; case 3: w = value; break; default: throw new IndexOutOfRangeException(); } }
    }
    public static Float4 operator +(Float4 a, Float4 b) => new(a.x + b.x, a.y + b.y, a.z + b.z, a.w + b.w);
    public static Float4 operator -(Float4 a, Float4 b) => new(a.x - b.x, a.y - b.y, a.z - b.z, a.w - b.w);
    public static Float4 operator *(Float4 a, Float4 b) => new(a.x * b.x, a.y * b.y, a.z * b.z, a.w * b.w);
    public static Float4 operator /(Float4 a, Float4 b) => new(a.x / b.x, a.y / b.y, a.z / b.z, a.w / b.w);
    public static Float4 operator -(Float4 a) => new(0 - a.x, 0 - a.y, 0 - a.z, 0 - a.w);
    public static Float4 operator +(Float4 a, float s) => new(a.x + s, a.y + s, a.z + s, a.w + s);
    public static Float4 operator -(Float4 a, float s) => new(a.x - s, a.y - s, a.z - s, a.w - s);
    public static Float4 operator *(Float4 a, float s) => new(a.x * s, a.y * s, a.z * s, a.w * s);
    public static Float4 operator /(Float4 a, float s) => new(a.x / s, a.y / s, a.z / s, a.w / s);
    public static Float4 operator *(float s, Float4 a) => new(s * a.x, s * a.y, s * a.z, s * a.w);
    public static bool operator ==(Float4 a, Float4 b) => a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;
    public static bool operator !=(Float4 a, Float4 b) => !(a == b);
    public readonly bool Equals(Float4 o) => x.Equals(o.x) && y.Equals(o.y) && z.Equals(o.z) && w.Equals(o.w);
    public override readonly bool Equals(object obj) => obj is Float4 o && Equals(o);
    public override readonly int GetHashCode() => HashCode.Combine(x, y, z, w);
    public override readonly string ToString() => $"SIMD4<Float>({Swift.description(x)}, {Swift.description(y)}, {Swift.description(z)}, {Swift.description(w)})";
}

/// <summary>
/// SIMD2&lt;Int&gt;. PORT: Swift's Int lanes are 64-bit; every use in the game is a small grid
/// coordinate, so the lanes are <c>int</c>. <c>+</c> is unchecked (wrapping), matching Swift <c>&amp;+</c>.
/// </summary>
public struct Int2 : IEquatable<Int2>
{
    public int x, y;
    public Int2(int x, int y) { this.x = x; this.y = y; }
    public Int2(int repeating) { x = repeating; y = repeating; }
    public static Int2 zero => default;
    public int this[int i]
    {
        readonly get => i switch { 0 => x, 1 => y, _ => throw new IndexOutOfRangeException() };
        set { switch (i) { case 0: x = value; break; case 1: y = value; break; default: throw new IndexOutOfRangeException(); } }
    }
    public static Int2 operator +(Int2 a, Int2 b) => new(unchecked(a.x + b.x), unchecked(a.y + b.y));
    public static Int2 operator -(Int2 a, Int2 b) => new(unchecked(a.x - b.x), unchecked(a.y - b.y));
    public static Int2 operator *(Int2 a, Int2 b) => new(unchecked(a.x * b.x), unchecked(a.y * b.y));
    public static bool operator ==(Int2 a, Int2 b) => a.x == b.x && a.y == b.y;
    public static bool operator !=(Int2 a, Int2 b) => !(a == b);
    public readonly bool Equals(Int2 o) => x == o.x && y == o.y;
    public override readonly bool Equals(object obj) => obj is Int2 o && Equals(o);
    public override readonly int GetHashCode() => HashCode.Combine(x, y);
    /// <summary>Swift description, e.g. <c>SIMD2&lt;Int&gt;(-3, 4)</c>. Invariant: some .NET cultures print U+2212 for minus.</summary>
    public override readonly string ToString() => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"SIMD2<Int>({x}, {y})");
}

/// <summary>simd_double3x3 (column major). Swift <c>simd_double3x3(columns:(a,b,c))</c> is <c>new Double3x3(a, b, c)</c>.</summary>
public struct Double3x3 : IEquatable<Double3x3>
{
    public Double3 column0, column1, column2;
    public Double3x3(Double3 column0, Double3 column1, Double3 column2) { this.column0 = column0; this.column1 = column1; this.column2 = column2; }
    /// <summary>Diagonal matrix (Swift <c>simd_double3x3(diagonal:)</c>).</summary>
    public Double3x3(Double3 diagonal) { column0 = new Double3(diagonal.x, 0, 0); column1 = new Double3(0, diagonal.y, 0); column2 = new Double3(0, 0, diagonal.z); }
    public static Double3x3 identity => new(new Double3(1, 0, 0), new Double3(0, 1, 0), new Double3(0, 0, 1));
    /// <summary>Column <paramref name="i"/> (Swift <c>columns.i</c>).</summary>
    public Double3 this[int i]
    {
        readonly get => i switch { 0 => column0, 1 => column1, 2 => column2, _ => throw new IndexOutOfRangeException() };
        set { switch (i) { case 0: column0 = value; break; case 1: column1 = value; break; case 2: column2 = value; break; default: throw new IndexOutOfRangeException(); } }
    }
    /// <summary>Element at column <paramref name="column"/>, row <paramref name="row"/> (C <c>m.columns[column][row]</c>).</summary>
    public readonly double this[int column, int row] => this[column][row];
    /// <summary>simd_mul(matrix, vector): c0*v.x, then fused multiply-adds (simd_muladd).</summary>
    public static Double3 operator *(Double3x3 m, Double3 v)
    {
        var r = m.column0 * v.x;
        r = Simd.muladd(m.column1, v.y, r);
        r = Simd.muladd(m.column2, v.z, r);
        return r;
    }
    public static Double3x3 operator *(Double3x3 a, Double3x3 b) => new(a * b.column0, a * b.column1, a * b.column2);
    public static bool operator ==(Double3x3 a, Double3x3 b) => a.column0 == b.column0 && a.column1 == b.column1 && a.column2 == b.column2;
    public static bool operator !=(Double3x3 a, Double3x3 b) => !(a == b);
    public readonly bool Equals(Double3x3 o) => column0.Equals(o.column0) && column1.Equals(o.column1) && column2.Equals(o.column2);
    public override readonly bool Equals(object obj) => obj is Double3x3 o && Equals(o);
    public override readonly int GetHashCode() => HashCode.Combine(column0, column1, column2);
}

/// <summary>simd_float4x4 (column major). Swift <c>simd_float4x4(columns:(a,b,c,d))</c> is <c>new Float4x4(a, b, c, d)</c>.</summary>
public struct Float4x4 : IEquatable<Float4x4>
{
    public Float4 column0, column1, column2, column3;
    public Float4x4(Float4 column0, Float4 column1, Float4 column2, Float4 column3) { this.column0 = column0; this.column1 = column1; this.column2 = column2; this.column3 = column3; }
    public Float4x4(Float4 diagonal) { column0 = new Float4(diagonal.x, 0, 0, 0); column1 = new Float4(0, diagonal.y, 0, 0); column2 = new Float4(0, 0, diagonal.z, 0); column3 = new Float4(0, 0, 0, diagonal.w); }
    /// <summary><c>matrix_identity_float4x4</c>.</summary>
    public static Float4x4 identity => new(new Float4(1, 0, 0, 0), new Float4(0, 1, 0, 0), new Float4(0, 0, 1, 0), new Float4(0, 0, 0, 1));
    public Float4 this[int i]
    {
        readonly get => i switch { 0 => column0, 1 => column1, 2 => column2, 3 => column3, _ => throw new IndexOutOfRangeException() };
        set { switch (i) { case 0: column0 = value; break; case 1: column1 = value; break; case 2: column2 = value; break; case 3: column3 = value; break; default: throw new IndexOutOfRangeException(); } }
    }
    public readonly float this[int column, int row] => this[column][row];
    /// <summary>simd_mul(matrix, vector): c0*v.x, then fused multiply-adds (simd_muladd).</summary>
    public static Float4 operator *(Float4x4 m, Float4 v)
    {
        var r = m.column0 * v.x;
        r = Simd.muladd(m.column1, v.y, r);
        r = Simd.muladd(m.column2, v.z, r);
        r = Simd.muladd(m.column3, v.w, r);
        return r;
    }
    public static Float4x4 operator *(Float4x4 a, Float4x4 b) => new(a * b.column0, a * b.column1, a * b.column2, a * b.column3);
    public readonly Float4x4 transpose => new(
        new Float4(column0.x, column1.x, column2.x, column3.x), new Float4(column0.y, column1.y, column2.y, column3.y),
        new Float4(column0.z, column1.z, column2.z, column3.z), new Float4(column0.w, column1.w, column2.w, column3.w));
    public static bool operator ==(Float4x4 a, Float4x4 b) => a.column0 == b.column0 && a.column1 == b.column1 && a.column2 == b.column2 && a.column3 == b.column3;
    public static bool operator !=(Float4x4 a, Float4x4 b) => !(a == b);
    public readonly bool Equals(Float4x4 o) => column0.Equals(o.column0) && column1.Equals(o.column1) && column2.Equals(o.column2) && column3.Equals(o.column3);
    public override readonly bool Equals(object obj) => obj is Float4x4 o && Equals(o);
    public override readonly int GetHashCode() => HashCode.Combine(column0, column1, column2, column3);
}

/// <summary>simd_quatd: imaginary part in vector.xyz, real part in vector.w.</summary>
public struct QuatD : IEquatable<QuatD>
{
    public Double4 vector;
    /// <summary>Swift <c>simd_quatd(ix:iy:iz:r:)</c>.</summary>
    public QuatD(double ix, double iy, double iz, double r) { vector = new Double4(ix, iy, iz, r); }
    /// <summary>Swift <c>simd_quatd(vector:)</c>.</summary>
    public QuatD(Double4 vector) { this.vector = vector; }
    /// <summary>Swift <c>simd_quatd(real:imag:)</c>. PORT: a factory, because a (double, Double3) constructor is the angle-axis form.</summary>
    public static QuatD realImag(double real, Double3 imag) => new(new Double4(imag, real));
    /// <summary>Swift <c>simd_quatd(angle:axis:)</c>: <c>(sin(angle/2)*axis, cos(angle/2))</c>; the axis is not normalized.</summary>
    public static QuatD angleAxis(double angle, Double3 axis)
    {
        var s = Math.Sin(angle / 2);
        return new QuatD(new Double4(s * axis.x, s * axis.y, s * axis.z, Math.Cos(angle / 2)));
    }
    /// <summary>Swift <c>simd_quatd(angle:axis:)</c>.</summary>
    public QuatD(double angle, Double3 axis) { this = angleAxis(angle, axis); }
    /// <summary>Swift <c>simd_quatd(_ rotationMatrix: simd_double3x3)</c> (Apple's trace/branch formula).</summary>
    public QuatD(Double3x3 matrix)
    {
        double m(int c, int r) => matrix[c, r];
        var trace = m(0, 0) + m(1, 1) + m(2, 2);
        if (trace >= 0.0)
        {
            var r = 2 * Math.Sqrt(1 + trace);
            var rinv = 1 / r;
            vector = new Double4(rinv * (m(1, 2) - m(2, 1)), rinv * (m(2, 0) - m(0, 2)), rinv * (m(0, 1) - m(1, 0)), r / 4);
        }
        else if (m(0, 0) >= m(1, 1) && m(0, 0) >= m(2, 2))
        {
            var r = 2 * Math.Sqrt(1 - m(1, 1) - m(2, 2) + m(0, 0));
            var rinv = 1 / r;
            vector = new Double4(r / 4, rinv * (m(0, 1) + m(1, 0)), rinv * (m(0, 2) + m(2, 0)), rinv * (m(1, 2) - m(2, 1)));
        }
        else if (m(1, 1) >= m(2, 2))
        {
            var r = 2 * Math.Sqrt(1 - m(0, 0) - m(2, 2) + m(1, 1));
            var rinv = 1 / r;
            vector = new Double4(rinv * (m(0, 1) + m(1, 0)), r / 4, rinv * (m(1, 2) + m(2, 1)), rinv * (m(2, 0) - m(0, 2)));
        }
        else
        {
            var r = 2 * Math.Sqrt(1 - m(0, 0) - m(1, 1) + m(2, 2));
            var rinv = 1 / r;
            vector = new Double4(rinv * (m(0, 2) + m(2, 0)), rinv * (m(1, 2) + m(2, 1)), r / 4, rinv * (m(0, 1) - m(1, 0)));
        }
    }
    /// <summary>Swift <c>simd_quatd(from:to:)</c>.</summary>
    public QuatD(Double3 from, Double3 to)
    {
        static QuatD reduced(Double3 f, Double3 t)
        {
            var half = Simd.normalize(f + t);
            return realImag(Simd.dot(f, half), Simd.cross(f, half));
        }
        if (Simd.dot(from, to) >= 0) { this = reduced(from, to); return; }
        var h = Simd.normalize(from) + Simd.normalize(to);
        if (Simd.length_squared(h) <= 4.930380657631324e-32 /* 2^-104 */)
        {
            var a = Simd.abs(from);
            if (a.x <= a.y && a.x <= a.z) this = realImag(0, Simd.normalize(Simd.cross(from, new Double3(1, 0, 0))));
            else if (a.y <= a.z) this = realImag(0, Simd.normalize(Simd.cross(from, new Double3(0, 1, 0))));
            else this = realImag(0, Simd.normalize(Simd.cross(from, new Double3(0, 0, 1))));
            return;
        }
        h = Simd.normalize(h);
        this = reduced(from, h) * reduced(h, to);
    }
    public static QuatD identity => new(0, 0, 0, 1);
    public readonly double real => vector.w;
    public readonly Double3 imag => vector.xyz;
    public readonly double angle => 2 * Math.Atan2(Simd.length(vector.xyz), vector.w);
    public readonly Double3 axis => Simd.normalize(vector.xyz);
    public readonly double length => Simd.length(vector);
    public readonly QuatD conjugate => new(vector * new Double4(-1, -1, -1, 1));
    public readonly QuatD inverse => new(conjugate.vector * (1 / Simd.length_squared(vector)));
    public readonly QuatD normalized
    {
        get
        {
            var ls = Simd.length_squared(vector);
            if (ls == 0) return identity;
            return new QuatD(vector * (1 / Math.Sqrt(ls)));
        }
    }
    /// <summary>simd_mul(p, q), compiled with FP_CONTRACT ON (measured bit-exact against Swift).</summary>
    public static QuatD operator *(QuatD p, QuatD q)
    {
        Double4 P = p.vector, Q = q.vector;
        var A = new Double4(Q.w, -Q.z, Q.y, -Q.x);
        var B = new Double4(Q.z, Q.w, -Q.x, -Q.y);
        var C = new Double4(-Q.y, Q.x, Q.w, -Q.z);
        static double lane(Double4 P, double a, double b, double c, double d) =>
            Math.FusedMultiplyAdd(P.x, a, P.y * b) + Math.FusedMultiplyAdd(P.z, c, P.w * d);
        return new QuatD(new Double4(lane(P, A.x, B.x, C.x, Q.x), lane(P, A.y, B.y, C.y, Q.y), lane(P, A.z, B.z, C.z, Q.z), lane(P, A.w, B.w, C.w, Q.w)));
    }
    public static QuatD operator *(QuatD q, double a) => new(a * q.vector);
    public static QuatD operator *(double a, QuatD q) => new(a * q.vector);
    public static QuatD operator +(QuatD p, QuatD q) => new(p.vector + q.vector);
    public static QuatD operator -(QuatD p, QuatD q) => new(p.vector - q.vector);
    /// <summary>simd_negate: a true sign flip of every lane (C negation, unlike Swift SIMD prefix minus).</summary>
    public static QuatD operator -(QuatD q) => new(new Double4(-q.vector.x, -q.vector.y, -q.vector.z, -q.vector.w));
    public static QuatD operator /(QuatD p, QuatD q) => p * q.inverse;
    /// <summary>_simd_mul_vq: v * q with FP_CONTRACT ON (arm64 implementation detail of simd_act).</summary>
    private static QuatD mulVQ(Double3 v, QuatD q)
    {
        var Q = q.vector;
        var A = new Double4(Q.w, -Q.z, Q.y, -Q.x);
        var B = new Double4(Q.z, Q.w, -Q.x, -Q.y);
        var C = new Double4(-Q.y, Q.x, Q.w, -Q.z);
        static double lane(Double3 v, double a, double b, double c) =>
            Math.FusedMultiplyAdd(v.z, c, Math.FusedMultiplyAdd(v.x, a, v.y * b));
        return new QuatD(new Double4(lane(v, A.x, B.x, C.x), lane(v, A.y, B.y, C.y), lane(v, A.z, B.z, C.z), lane(v, A.w, B.w, C.w)));
    }
    /// <summary>simd_act(q, v): rotates <paramref name="v"/> (arm64 formula, measured bit-exact against Swift).</summary>
    public readonly Double3 act(Double3 v) => (this * mulVQ(v, conjugate)).vector.xyz;
    public static bool operator ==(QuatD a, QuatD b) => a.vector == b.vector;
    public static bool operator !=(QuatD a, QuatD b) => a.vector != b.vector;
    public readonly bool Equals(QuatD o) => vector.Equals(o.vector);
    public override readonly bool Equals(object obj) => obj is QuatD o && Equals(o);
    public override readonly int GetHashCode() => vector.GetHashCode();
    public override readonly string ToString() => $"simd_quatd(real: {Swift.description(vector.w)}, imag: {imag})";
}

/// <summary>simd_quatf: imaginary part in vector.xyz, real part in vector.w.</summary>
public struct QuatF : IEquatable<QuatF>
{
    public Float4 vector;
    public QuatF(float ix, float iy, float iz, float r) { vector = new Float4(ix, iy, iz, r); }
    public QuatF(Float4 vector) { this.vector = vector; }
    /// <summary>Swift <c>simd_quatf(real:imag:)</c> (factory; see <see cref="QuatD.realImag"/>).</summary>
    public static QuatF realImag(float real, Float3 imag) => new(new Float4(imag, real));
    /// <summary>Swift <c>simd_quatf(_ q: simd_quatd)</c> (rounds each lane).</summary>
    public QuatF(QuatD q) { vector = new Float4(q.vector); }
    /// <summary>
    /// Swift <c>simd_quatf(angle:axis:)</c>: <c>(sinf(angle/2)*axis, cosf(angle/2))</c>. The header's float
    /// paths resolve to the single-precision libm functions (measured against Swift).
    /// </summary>
    public QuatF(float angle, Float3 axis)
    {
        var s = MathF.Sin(angle / 2);
        vector = new Float4(s * axis.x, s * axis.y, s * axis.z, MathF.Cos(angle / 2));
    }
    public QuatF(Float3 from, Float3 to)
    {
        static QuatF reduced(Float3 f, Float3 t)
        {
            var half = Simd.normalize(f + t);
            return realImag(Simd.dot(f, half), Simd.cross(f, half));
        }
        if (Simd.dot(from, to) >= 0) { this = reduced(from, to); return; }
        var h = Simd.normalize(from) + Simd.normalize(to);
        if (Simd.length_squared(h) <= 1.4210855e-14f /* 2^-46 */)
        {
            var a = Simd.abs(from);
            if (a.x <= a.y && a.x <= a.z) this = realImag(0, Simd.normalize(Simd.cross(from, new Float3(1, 0, 0))));
            else if (a.y <= a.z) this = realImag(0, Simd.normalize(Simd.cross(from, new Float3(0, 1, 0))));
            else this = realImag(0, Simd.normalize(Simd.cross(from, new Float3(0, 0, 1))));
            return;
        }
        h = Simd.normalize(h);
        this = reduced(from, h) * reduced(h, to);
    }
    /// <summary>Swift <c>simd_quatf(_ rotationMatrix: simd_float4x4)</c> (Apple's trace/branch formula, simd_recip(float)).</summary>
    public QuatF(Float4x4 matrix)
    {
        float m(int c, int r) => matrix[c, r];
        var trace = m(0, 0) + m(1, 1) + m(2, 2);
        if (trace >= 0.0)
        {
            var r = 2 * MathF.Sqrt(1 + trace);
            var rinv = Simd.recip(r);
            vector = new Float4(rinv * (m(1, 2) - m(2, 1)), rinv * (m(2, 0) - m(0, 2)), rinv * (m(0, 1) - m(1, 0)), r / 4);
        }
        else if (m(0, 0) >= m(1, 1) && m(0, 0) >= m(2, 2))
        {
            var r = 2 * MathF.Sqrt(1 - m(1, 1) - m(2, 2) + m(0, 0));
            var rinv = Simd.recip(r);
            vector = new Float4(r / 4, rinv * (m(0, 1) + m(1, 0)), rinv * (m(0, 2) + m(2, 0)), rinv * (m(1, 2) - m(2, 1)));
        }
        else if (m(1, 1) >= m(2, 2))
        {
            var r = 2 * MathF.Sqrt(1 - m(0, 0) - m(2, 2) + m(1, 1));
            var rinv = Simd.recip(r);
            vector = new Float4(rinv * (m(0, 1) + m(1, 0)), r / 4, rinv * (m(1, 2) + m(2, 1)), rinv * (m(2, 0) - m(0, 2)));
        }
        else
        {
            var r = 2 * MathF.Sqrt(1 - m(0, 0) - m(1, 1) + m(2, 2));
            var rinv = Simd.recip(r);
            vector = new Float4(rinv * (m(0, 2) + m(2, 0)), rinv * (m(1, 2) + m(2, 1)), r / 4, rinv * (m(0, 1) - m(1, 0)));
        }
    }
    public static QuatF identity => new(0, 0, 0, 1);
    public readonly float real => vector.w;
    public readonly Float3 imag => vector.xyz;
    public readonly float angle => 2 * MathF.Atan2(Simd.length(vector.xyz), vector.w);
    public readonly Float3 axis => Simd.normalize(vector.xyz);
    public readonly float length => Simd.length(vector);
    public readonly QuatF conjugate => new(vector * new Float4(-1, -1, -1, 1));
    public readonly QuatF inverse => new(conjugate.vector * Simd.recip(Simd.length_squared(vector)));
    public readonly QuatF normalized
    {
        get
        {
            var ls = Simd.length_squared(vector);
            if (ls == 0) return identity;
            return new QuatF(vector * Simd.rsqrt(ls));
        }
    }
    public static QuatF operator *(QuatF p, QuatF q)
    {
        Float4 P = p.vector, Q = q.vector;
        var A = new Float4(Q.w, -Q.z, Q.y, -Q.x);
        var B = new Float4(Q.z, Q.w, -Q.x, -Q.y);
        var C = new Float4(-Q.y, Q.x, Q.w, -Q.z);
        static float lane(Float4 P, float a, float b, float c, float d) =>
            MathF.FusedMultiplyAdd(P.x, a, P.y * b) + MathF.FusedMultiplyAdd(P.z, c, P.w * d);
        return new QuatF(new Float4(lane(P, A.x, B.x, C.x, Q.x), lane(P, A.y, B.y, C.y, Q.y), lane(P, A.z, B.z, C.z, Q.z), lane(P, A.w, B.w, C.w, Q.w)));
    }
    public static QuatF operator *(QuatF q, float a) => new(a * q.vector);
    public static QuatF operator *(float a, QuatF q) => new(a * q.vector);
    public static QuatF operator +(QuatF p, QuatF q) => new(p.vector + q.vector);
    public static QuatF operator -(QuatF p, QuatF q) => new(p.vector - q.vector);
    public static QuatF operator -(QuatF q) => new(new Float4(-q.vector.x, -q.vector.y, -q.vector.z, -q.vector.w));
    public static QuatF operator /(QuatF p, QuatF q) => p * q.inverse;
    private static QuatF mulVQ(Float3 v, QuatF q)
    {
        var Q = q.vector;
        var A = new Float4(Q.w, -Q.z, Q.y, -Q.x);
        var B = new Float4(Q.z, Q.w, -Q.x, -Q.y);
        var C = new Float4(-Q.y, Q.x, Q.w, -Q.z);
        static float lane(Float3 v, float a, float b, float c) =>
            MathF.FusedMultiplyAdd(v.z, c, MathF.FusedMultiplyAdd(v.x, a, v.y * b));
        return new QuatF(new Float4(lane(v, A.x, B.x, C.x), lane(v, A.y, B.y, C.y), lane(v, A.z, B.z, C.z), lane(v, A.w, B.w, C.w)));
    }
    public readonly Float3 act(Float3 v) => (this * mulVQ(v, conjugate)).vector.xyz;
    public static bool operator ==(QuatF a, QuatF b) => a.vector == b.vector;
    public static bool operator !=(QuatF a, QuatF b) => a.vector != b.vector;
    public readonly bool Equals(QuatF o) => vector.Equals(o.vector);
    public override readonly bool Equals(object obj) => obj is QuatF o && Equals(o);
    public override readonly int GetHashCode() => vector.GetHashCode();
    public override readonly string ToString() => $"simd_quatf(real: {Swift.description(vector.w)}, imag: {imag})";
}

/// <summary>Apple simd free functions: <c>simd_dot</c> is <c>Simd.dot</c>, <c>simd_length_squared</c> is <c>Simd.length_squared</c>, etc.</summary>
public static class Simd
{
    // MARK: dot / length / distance / normalize (Double)

    public static double dot(Double2 a, Double2 b) => a.x * b.x + a.y * b.y;
    public static double dot(Double3 a, Double3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
    /// <summary>simd_reduce_add(double4) is reduce_add(lo + hi): (x+z) + (y+w).</summary>
    public static double dot(Double4 a, Double4 b) { var p = a * b; return (p.x + p.z) + (p.y + p.w); }
    public static double dot(QuatD a, QuatD b) => dot(a.vector, b.vector);
    public static double length_squared(Double2 v) => dot(v, v);
    public static double length_squared(Double3 v) => dot(v, v);
    public static double length_squared(Double4 v) => dot(v, v);
    public static double length(Double2 v) => Math.Sqrt(length_squared(v));
    public static double length(Double3 v) => Math.Sqrt(length_squared(v));
    public static double length(Double4 v) => Math.Sqrt(length_squared(v));
    public static double length(QuatD q) => length(q.vector);
    public static double distance(Double2 a, Double2 b) => length(a - b);
    public static double distance(Double3 a, Double3 b) => length(a - b);
    public static double distance(Double4 a, Double4 b) => length(a - b);
    public static double distance_squared(Double2 a, Double2 b) => length_squared(a - b);
    public static double distance_squared(Double3 a, Double3 b) => length_squared(a - b);
    public static double distance_squared(Double4 a, Double4 b) => length_squared(a - b);
    public static Double2 normalize(Double2 v) => v * (1 / Math.Sqrt(length_squared(v)));
    public static Double3 normalize(Double3 v) => v * (1 / Math.Sqrt(length_squared(v)));
    public static Double4 normalize(Double4 v) => v * (1 / Math.Sqrt(length_squared(v)));
    public static QuatD normalize(QuatD q) => q.normalized;

    /// <summary>simd_cross: (x.zxy*y - x*y.zxy).zxy with each lane contracted to fma(a, b, -(c*d)) (measured).</summary>
    public static Double3 cross(Double3 a, Double3 b) => new(
        Math.FusedMultiplyAdd(a.y, b.z, -(a.z * b.y)),
        Math.FusedMultiplyAdd(a.z, b.x, -(a.x * b.z)),
        Math.FusedMultiplyAdd(a.x, b.y, -(a.y * b.x)));
    /// <summary>simd_cross for 2-vectors: (0, 0, x.x*y.y - x.y*y.x), contracted.</summary>
    public static Double3 cross(Double2 a, Double2 b) => new(0, 0, Math.FusedMultiplyAdd(a.x, b.y, -(a.y * b.x)));

    // MARK: dot / length / distance / normalize (Float)

    public static float dot(Float2 a, Float2 b) => a.x * b.x + a.y * b.y;
    public static float dot(Float3 a, Float3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
    public static float dot(Float4 a, Float4 b) { var p = a * b; return (p.x + p.z) + (p.y + p.w); }
    public static float dot(QuatF a, QuatF b) => dot(a.vector, b.vector);
    public static float length_squared(Float2 v) => dot(v, v);
    public static float length_squared(Float3 v) => dot(v, v);
    public static float length_squared(Float4 v) => dot(v, v);
    public static float length(Float2 v) => MathF.Sqrt(length_squared(v));
    public static float length(Float3 v) => MathF.Sqrt(length_squared(v));
    public static float length(Float4 v) => MathF.Sqrt(length_squared(v));
    public static float length(QuatF q) => length(q.vector);
    public static float distance(Float2 a, Float2 b) => length(a - b);
    public static float distance(Float3 a, Float3 b) => length(a - b);
    public static float distance(Float4 a, Float4 b) => length(a - b);
    public static float distance_squared(Float2 a, Float2 b) => length_squared(a - b);
    public static float distance_squared(Float3 a, Float3 b) => length_squared(a - b);
    public static float distance_squared(Float4 a, Float4 b) => length_squared(a - b);
    public static Float2 normalize(Float2 v) => v * rsqrt(length_squared(v));
    public static Float3 normalize(Float3 v) => v * rsqrt(length_squared(v));
    public static Float4 normalize(Float4 v) => v * rsqrt(length_squared(v));
    public static QuatF normalize(QuatF q) => q.normalized;
    public static Float3 cross(Float3 a, Float3 b) => new(
        MathF.FusedMultiplyAdd(a.y, b.z, -(a.z * b.y)),
        MathF.FusedMultiplyAdd(a.z, b.x, -(a.x * b.z)),
        MathF.FusedMultiplyAdd(a.x, b.y, -(a.y * b.x)));
    public static Float3 cross(Float2 a, Float2 b) => new(0, 0, MathF.FusedMultiplyAdd(a.x, b.y, -(a.y * b.x)));

    /// <summary>
    /// simd_precise_rsqrt(float) on ARM NEON: FRSQRTE estimate refined by two FRSQRTS steps.
    /// PORT: reproduced with AdvSimd on arm64; other CPUs use 1/sqrtf, which can differ in the last bit.
    /// </summary>
    public static float rsqrt(float x)
    {
        if (AdvSimd.IsSupported)
        {
            var v = Vector64.Create(x);
            var r = AdvSimd.ReciprocalSquareRootEstimate(v);
            r = AdvSimd.Multiply(r, AdvSimd.ReciprocalSquareRootStep(v, AdvSimd.Multiply(r, r)));
            r = AdvSimd.Multiply(r, AdvSimd.ReciprocalSquareRootStep(v, AdvSimd.Multiply(r, r)));
            return r.ToScalar();
        }
        return 1 / MathF.Sqrt(x);
    }
    /// <summary>simd_precise_recip(float) on ARM NEON: FRECPE estimate refined by two FRECPS steps (see <see cref="rsqrt(float)"/>).</summary>
    public static float recip(float x)
    {
        if (AdvSimd.IsSupported)
        {
            var v = Vector64.Create(x);
            var r = AdvSimd.ReciprocalEstimate(v);
            r = AdvSimd.Multiply(r, AdvSimd.ReciprocalStep(v, r));
            r = AdvSimd.Multiply(r, AdvSimd.ReciprocalStep(v, r));
            return r.ToScalar();
        }
        return 1 / x;
    }
    /// <summary>simd_precise_rsqrt(double) = 1/sqrt(x).</summary>
    public static double rsqrt(double x) => 1 / Math.Sqrt(x);
    /// <summary>simd_precise_recip(double) = 1/x.</summary>
    public static double recip(double x) => 1 / x;

    // MARK: lane-wise min / max / clamp / abs / mix

    public static Double2 min(Double2 a, Double2 b) => new(double.MinNumber(a.x, b.x), double.MinNumber(a.y, b.y));
    public static Double3 min(Double3 a, Double3 b) => new(double.MinNumber(a.x, b.x), double.MinNumber(a.y, b.y), double.MinNumber(a.z, b.z));
    public static Double4 min(Double4 a, Double4 b) => new(double.MinNumber(a.x, b.x), double.MinNumber(a.y, b.y), double.MinNumber(a.z, b.z), double.MinNumber(a.w, b.w));
    public static Double2 max(Double2 a, Double2 b) => new(double.MaxNumber(a.x, b.x), double.MaxNumber(a.y, b.y));
    public static Double3 max(Double3 a, Double3 b) => new(double.MaxNumber(a.x, b.x), double.MaxNumber(a.y, b.y), double.MaxNumber(a.z, b.z));
    public static Double4 max(Double4 a, Double4 b) => new(double.MaxNumber(a.x, b.x), double.MaxNumber(a.y, b.y), double.MaxNumber(a.z, b.z), double.MaxNumber(a.w, b.w));
    public static Float2 min(Float2 a, Float2 b) => new(float.MinNumber(a.x, b.x), float.MinNumber(a.y, b.y));
    public static Float3 min(Float3 a, Float3 b) => new(float.MinNumber(a.x, b.x), float.MinNumber(a.y, b.y), float.MinNumber(a.z, b.z));
    public static Float4 min(Float4 a, Float4 b) => new(float.MinNumber(a.x, b.x), float.MinNumber(a.y, b.y), float.MinNumber(a.z, b.z), float.MinNumber(a.w, b.w));
    public static Float2 max(Float2 a, Float2 b) => new(float.MaxNumber(a.x, b.x), float.MaxNumber(a.y, b.y));
    public static Float3 max(Float3 a, Float3 b) => new(float.MaxNumber(a.x, b.x), float.MaxNumber(a.y, b.y), float.MaxNumber(a.z, b.z));
    public static Float4 max(Float4 a, Float4 b) => new(float.MaxNumber(a.x, b.x), float.MaxNumber(a.y, b.y), float.MaxNumber(a.z, b.z), float.MaxNumber(a.w, b.w));
    public static Double2 clamp(Double2 x, Double2 lo, Double2 hi) => min(max(x, lo), hi);
    public static Double3 clamp(Double3 x, Double3 lo, Double3 hi) => min(max(x, lo), hi);
    public static Float2 clamp(Float2 x, Float2 lo, Float2 hi) => min(max(x, lo), hi);
    public static Float3 clamp(Float3 x, Float3 lo, Float3 hi) => min(max(x, lo), hi);
    public static double clamp(double x, double lo, double hi) => double.MinNumber(double.MaxNumber(x, lo), hi);
    public static float clamp(float x, float lo, float hi) => float.MinNumber(float.MaxNumber(x, lo), hi);
    public static Double2 abs(Double2 v) => new(Math.Abs(v.x), Math.Abs(v.y));
    public static Double3 abs(Double3 v) => new(Math.Abs(v.x), Math.Abs(v.y), Math.Abs(v.z));
    public static Float2 abs(Float2 v) => new(MathF.Abs(v.x), MathF.Abs(v.y));
    public static Float3 abs(Float3 v) => new(MathF.Abs(v.x), MathF.Abs(v.y), MathF.Abs(v.z));
    /// <summary>simd_mix: x + t*(y-x), contracted to fma(t, y-x, x) per lane.</summary>
    public static double mix(double x, double y, double t) => Math.FusedMultiplyAdd(t, y - x, x);
    public static float mix(float x, float y, float t) => MathF.FusedMultiplyAdd(t, y - x, x);
    public static Double2 mix(Double2 x, Double2 y, Double2 t) => new(mix(x.x, y.x, t.x), mix(x.y, y.y, t.y));
    public static Double3 mix(Double3 x, Double3 y, Double3 t) => new(mix(x.x, y.x, t.x), mix(x.y, y.y, t.y), mix(x.z, y.z, t.z));
    public static Double2 mix(Double2 x, Double2 y, double t) => mix(x, y, new Double2(t));
    public static Double3 mix(Double3 x, Double3 y, double t) => mix(x, y, new Double3(t));
    public static Float2 mix(Float2 x, Float2 y, Float2 t) => new(mix(x.x, y.x, t.x), mix(x.y, y.y, t.y));
    public static Float3 mix(Float3 x, Float3 y, Float3 t) => new(mix(x.x, y.x, t.x), mix(x.y, y.y, t.y), mix(x.z, y.z, t.z));
    public static Float4 mix(Float4 x, Float4 y, Float4 t) => new(mix(x.x, y.x, t.x), mix(x.y, y.y, t.y), mix(x.z, y.z, t.z), mix(x.w, y.w, t.w));
    public static Float3 mix(Float3 x, Float3 y, float t) => mix(x, y, new Float3(t));

    /// <summary>simd_muladd(x, y, z) = x*y + z with FP_CONTRACT ON (lane-wise fma).</summary>
    public static Double3 muladd(Double3 x, double y, Double3 z) => new(Math.FusedMultiplyAdd(x.x, y, z.x), Math.FusedMultiplyAdd(x.y, y, z.y), Math.FusedMultiplyAdd(x.z, y, z.z));
    public static Float4 muladd(Float4 x, float y, Float4 z) => new(MathF.FusedMultiplyAdd(x.x, y, z.x), MathF.FusedMultiplyAdd(x.y, y, z.y), MathF.FusedMultiplyAdd(x.z, y, z.z), MathF.FusedMultiplyAdd(x.w, y, z.w));

    // MARK: quaternions

    public static Double3 act(QuatD q, Double3 v) => q.act(v);
    public static Float3 act(QuatF q, Float3 v) => q.act(v);
    public static QuatD mul(QuatD p, QuatD q) => p * q;
    public static QuatF mul(QuatF p, QuatF q) => p * q;
    public static QuatD inverse(QuatD q) => q.inverse;
    public static QuatF inverse(QuatF q) => q.inverse;
    public static QuatD conjugate(QuatD q) => q.conjugate;
    public static QuatF conjugate(QuatF q) => q.conjugate;

    private static double sinc(double x) => x == 0 ? 1 : Math.Sin(x) / x;
    private static float sinc(float x) => x == 0 ? 1 : MathF.Sin(x) / x;
    private static QuatD slerpInternal(QuatD q0, QuatD q1, double t)
    {
        var s = 1 - t;
        var a = 2 * Math.Atan2(length(q0.vector - q1.vector), length(q0.vector + q1.vector));
        var r = recip(sinc(a));
        // A*q0 + B*q1 with fp-contract=on: fma(A, q0, B*q1) per lane.
        double A = sinc(s * a) * r * s, B = sinc(t * a) * r * t;
        Double4 p = q0.vector, q = q1.vector;
        return new QuatD(new Double4(Math.FusedMultiplyAdd(A, p.x, B * q.x), Math.FusedMultiplyAdd(A, p.y, B * q.y), Math.FusedMultiplyAdd(A, p.z, B * q.z), Math.FusedMultiplyAdd(A, p.w, B * q.w))).normalized;
    }
    private static QuatF slerpInternal(QuatF q0, QuatF q1, float t)
    {
        var s = 1 - t;
        var a = 2 * MathF.Atan2(length(q0.vector - q1.vector), length(q0.vector + q1.vector));
        var r = recip(sinc(a));
        float A = sinc(s * a) * r * s, B = sinc(t * a) * r * t;
        Float4 p = q0.vector, q = q1.vector;
        return new QuatF(new Float4(MathF.FusedMultiplyAdd(A, p.x, B * q.x), MathF.FusedMultiplyAdd(A, p.y, B * q.y), MathF.FusedMultiplyAdd(A, p.z, B * q.z), MathF.FusedMultiplyAdd(A, p.w, B * q.w))).normalized;
    }
    /// <summary>simd_slerp (shortest arc).</summary>
    public static QuatD slerp(QuatD q0, QuatD q1, double t) => dot(q0, q1) >= 0 ? slerpInternal(q0, q1, t) : slerpInternal(q0, -q1, t);
    /// <summary>simd_slerp (shortest arc).</summary>
    public static QuatF slerp(QuatF q0, QuatF q1, float t) => dot(q0, q1) >= 0 ? slerpInternal(q0, q1, t) : slerpInternal(q0, -q1, t);

    // MARK: matrices

    /// <summary>
    /// simd_inverse(float4x4). PORT: Apple calls the external __invert_f4; this is a cofactor
    /// inverse in float and can differ from it in the last bits.
    /// </summary>
    public static Float4x4 inverse(Float4x4 m)
    {
        float a00 = m.column0.x, a01 = m.column1.x, a02 = m.column2.x, a03 = m.column3.x;
        float a10 = m.column0.y, a11 = m.column1.y, a12 = m.column2.y, a13 = m.column3.y;
        float a20 = m.column0.z, a21 = m.column1.z, a22 = m.column2.z, a23 = m.column3.z;
        float a30 = m.column0.w, a31 = m.column1.w, a32 = m.column2.w, a33 = m.column3.w;
        float b00 = a00 * a11 - a01 * a10, b01 = a00 * a12 - a02 * a10, b02 = a00 * a13 - a03 * a10;
        float b03 = a01 * a12 - a02 * a11, b04 = a01 * a13 - a03 * a11, b05 = a02 * a13 - a03 * a12;
        float b06 = a20 * a31 - a21 * a30, b07 = a20 * a32 - a22 * a30, b08 = a20 * a33 - a23 * a30;
        float b09 = a21 * a32 - a22 * a31, b10 = a21 * a33 - a23 * a31, b11 = a22 * a33 - a23 * a32;
        var det = b00 * b11 - b01 * b10 + b02 * b09 + b03 * b08 - b04 * b07 + b05 * b06;
        var inv = 1 / det;
        // Row-major inverse entries r[row][col].
        float r00 = (a11 * b11 - a12 * b10 + a13 * b09) * inv, r01 = (-a01 * b11 + a02 * b10 - a03 * b09) * inv;
        float r02 = (a31 * b05 - a32 * b04 + a33 * b03) * inv, r03 = (-a21 * b05 + a22 * b04 - a23 * b03) * inv;
        float r10 = (-a10 * b11 + a12 * b08 - a13 * b07) * inv, r11 = (a00 * b11 - a02 * b08 + a03 * b07) * inv;
        float r12 = (-a30 * b05 + a32 * b02 - a33 * b01) * inv, r13 = (a20 * b05 - a22 * b02 + a23 * b01) * inv;
        float r20 = (a10 * b10 - a11 * b08 + a13 * b06) * inv, r21 = (-a00 * b10 + a01 * b08 - a03 * b06) * inv;
        float r22 = (a30 * b04 - a31 * b02 + a33 * b00) * inv, r23 = (-a20 * b04 + a21 * b02 - a23 * b00) * inv;
        float r30 = (-a10 * b09 + a11 * b07 - a12 * b06) * inv, r31 = (a00 * b09 - a01 * b07 + a02 * b06) * inv;
        float r32 = (-a30 * b03 + a31 * b01 - a32 * b00) * inv, r33 = (a20 * b03 - a21 * b01 + a22 * b00) * inv;
        return new Float4x4(new Float4(r00, r10, r20, r30), new Float4(r01, r11, r21, r31), new Float4(r02, r12, r22, r32), new Float4(r03, r13, r23, r33));
    }
    public static Float4x4 transpose(Float4x4 m) => m.transpose;
    public static Float4 mul(Float4x4 m, Float4 v) => m * v;
    public static Float4x4 mul(Float4x4 a, Float4x4 b) => a * b;
    public static Double3 mul(Double3x3 m, Double3 v) => m * v;
}
