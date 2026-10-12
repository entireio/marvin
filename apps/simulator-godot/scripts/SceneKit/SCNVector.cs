using System;
using Godot;

namespace Marvin.SceneKit;

/// <summary>CGPoint (NSPoint). CGFloat is double on macOS.</summary>
public struct CGPoint : IEquatable<CGPoint>
{
    public double x, y;
    public CGPoint(double x, double y) { this.x = x; this.y = y; }
    public static readonly CGPoint zero = new(0, 0);
    public bool Equals(CGPoint o) => x == o.x && y == o.y;
    public override bool Equals(object o) => o is CGPoint p && Equals(p);
    public override int GetHashCode() => HashCode.Combine(x, y);
    public static bool operator ==(CGPoint a, CGPoint b) => a.Equals(b);
    public static bool operator !=(CGPoint a, CGPoint b) => !a.Equals(b);
    public override string ToString() => $"({x}, {y})";
    public Vector2 ToGodot() => new((float)x, (float)y);
}

/// <summary>CGSize (NSSize).</summary>
public struct CGSize : IEquatable<CGSize>
{
    public double width, height;
    public CGSize(double width, double height) { this.width = width; this.height = height; }
    public static readonly CGSize zero = new(0, 0);
    public bool Equals(CGSize o) => width == o.width && height == o.height;
    public override bool Equals(object o) => o is CGSize s && Equals(s);
    public override int GetHashCode() => HashCode.Combine(width, height);
    public static bool operator ==(CGSize a, CGSize b) => a.Equals(b);
    public static bool operator !=(CGSize a, CGSize b) => !a.Equals(b);
    public override string ToString() => $"({width}, {height})";
}

/// <summary>CGRect (NSRect). AppKit coordinates: origin is the lower-left corner.</summary>
public struct CGRect : IEquatable<CGRect>
{
    public CGPoint origin;
    public CGSize size;
    public CGRect(double x, double y, double width, double height) { origin = new CGPoint(x, y); size = new CGSize(width, height); }
    public CGRect(CGPoint origin, CGSize size) { this.origin = origin; this.size = size; }
    public static readonly CGRect zero = new(0, 0, 0, 0);
    public double width => size.width;
    public double height => size.height;
    public double minX => Math.Min(origin.x, origin.x + size.width);
    public double minY => Math.Min(origin.y, origin.y + size.height);
    public double maxX => Math.Max(origin.x, origin.x + size.width);
    public double maxY => Math.Max(origin.y, origin.y + size.height);
    public double midX => origin.x + size.width / 2;
    public double midY => origin.y + size.height / 2;
    public CGRect insetBy(double dx, double dy) => new(origin.x + dx, origin.y + dy, size.width - 2 * dx, size.height - 2 * dy);
    public bool contains(CGPoint p) => p.x >= minX && p.x < maxX && p.y >= minY && p.y < maxY;
    public bool Equals(CGRect o) => origin == o.origin && size == o.size;
    public override bool Equals(object o) => o is CGRect r && Equals(r);
    public override int GetHashCode() => HashCode.Combine(origin, size);
    public static bool operator ==(CGRect a, CGRect b) => a.Equals(b);
    public static bool operator !=(CGRect a, CGRect b) => !a.Equals(b);
    public override string ToString() => $"({origin.x}, {origin.y}, {size.width}, {size.height})";
}

/// <summary>SCNVector3. Components are CGFloat (double) on macOS.</summary>
public struct SCNVector3 : IEquatable<SCNVector3>
{
    public double x, y, z;
    public SCNVector3(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
    /// <summary>SCNVector3(SIMD3&lt;Float&gt;) - the facade's simd float vector.</summary>
    public SCNVector3(SCNFloat3 v) { var (a, b, c) = SimdBridge.Get(v); x = a; y = b; z = c; }
#if MARVIN_CORE_SIMD
    /// <summary>SCNVector3(SIMD3&lt;Double&gt;).</summary>
    public SCNVector3(SCNDouble3 v) { var (a, b, c) = SimdBridge.Get(v); x = a; y = b; z = c; }
    public static implicit operator SCNDouble3(SCNVector3 v) => SimdBridge.D3(v.x, v.y, v.z);
#endif
    public static readonly SCNVector3 Zero = new(0, 0, 0);
    public bool Equals(SCNVector3 o) => x == o.x && y == o.y && z == o.z;
    public override bool Equals(object o) => o is SCNVector3 v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(x, y, z);
    public static bool operator ==(SCNVector3 a, SCNVector3 b) => a.Equals(b);
    public static bool operator !=(SCNVector3 a, SCNVector3 b) => !a.Equals(b);
    public override string ToString() => $"SCNVector3(x: {x}, y: {y}, z: {z})";

    // Conversions (not SceneKit API; SceneKit has no arithmetic on SCNVector3).
    public Vector3 ToGodot() => new((float)x, (float)y, (float)z);
    public static SCNVector3 FromGodot(Vector3 v) => new(v.X, v.Y, v.Z);
    public SCNFloat3 simd => SimdBridge.F3(x, y, z);
    /// <summary>Generic conversion hook: SCNVector3 to any (x,y,z) constructor, e.g. MarvinCore's Double3.</summary>
    public T To<T>(Func<double, double, double, T> make) => make(x, y, z);
    internal double Length => Math.Sqrt(x * x + y * y + z * z);
    public static SCNVector3 operator +(SCNVector3 a, SCNVector3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
    public static SCNVector3 operator -(SCNVector3 a, SCNVector3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
    public static SCNVector3 operator *(SCNVector3 a, double s) => new(a.x * s, a.y * s, a.z * s);
    internal static double Dot(SCNVector3 a, SCNVector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
    internal static SCNVector3 Cross(SCNVector3 a, SCNVector3 b) => new(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
    internal SCNVector3 Normalized() { var l = Length; return l > 0 ? new(x / l, y / l, z / l) : this; }
}

/// <summary>SCNVector4 (also SCNQuaternion: x,y,z imaginary, w real; or axis-angle for rotation).</summary>
public struct SCNVector4 : IEquatable<SCNVector4>
{
    public double x, y, z, w;
    public SCNVector4(double x, double y, double z, double w) { this.x = x; this.y = y; this.z = z; this.w = w; }
    public SCNVector4(SCNFloat4 v) { var (a, b, c, d) = SimdBridge.Get(v); x = a; y = b; z = c; w = d; }
    public static readonly SCNVector4 Zero = new(0, 0, 0, 0);
    public bool Equals(SCNVector4 o) => x == o.x && y == o.y && z == o.z && w == o.w;
    public override bool Equals(object o) => o is SCNVector4 v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(x, y, z, w);
    public static bool operator ==(SCNVector4 a, SCNVector4 b) => a.Equals(b);
    public static bool operator !=(SCNVector4 a, SCNVector4 b) => !a.Equals(b);
    public override string ToString() => $"SCNVector4(x: {x}, y: {y}, z: {z}, w: {w})";
    public Vector4 ToGodot() => new((float)x, (float)y, (float)z, (float)w);
    public SCNFloat4 simd => SimdBridge.F4(x, y, z, w);
    public T To<T>(Func<double, double, double, double, T> make) => make(x, y, z, w);
}

/// <summary>
/// SCNMatrix4. Field m{i}{j}: SceneKit's row-major naming, where row i is the
/// i-th column of the equivalent column-vector (simd) matrix. m41,m42,m43 is the
/// translation. All facade matrix maths uses column vectors: (A*B)*p applies B first.
/// </summary>
public struct SCNMatrix4 : IEquatable<SCNMatrix4>
{
    public double m11, m12, m13, m14, m21, m22, m23, m24, m31, m32, m33, m34, m41, m42, m43, m44;
    public SCNMatrix4(double m11, double m12, double m13, double m14, double m21, double m22, double m23, double m24,
                      double m31, double m32, double m33, double m34, double m41, double m42, double m43, double m44)
    {
        this.m11 = m11; this.m12 = m12; this.m13 = m13; this.m14 = m14;
        this.m21 = m21; this.m22 = m22; this.m23 = m23; this.m24 = m24;
        this.m31 = m31; this.m32 = m32; this.m33 = m33; this.m34 = m34;
        this.m41 = m41; this.m42 = m42; this.m43 = m43; this.m44 = m44;
    }
    /// <summary>SCNMatrix4(simd_float4x4).</summary>
    public SCNMatrix4(SCNFloat4x4 m) { this = SimdBridge.M(m); }
    public static readonly SCNMatrix4 Identity = new(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);

    /// <summary>Element at column c, row r of the column-vector matrix.</summary>
    internal double this[int c, int r]
    {
        readonly get => (c * 4 + r) switch
        {
            0 => m11, 1 => m12, 2 => m13, 3 => m14, 4 => m21, 5 => m22, 6 => m23, 7 => m24,
            8 => m31, 9 => m32, 10 => m33, 11 => m34, 12 => m41, 13 => m42, 14 => m43, _ => m44,
        };
        set
        {
            switch (c * 4 + r)
            {
                case 0: m11 = value; break; case 1: m12 = value; break; case 2: m13 = value; break; case 3: m14 = value; break;
                case 4: m21 = value; break; case 5: m22 = value; break; case 6: m23 = value; break; case 7: m24 = value; break;
                case 8: m31 = value; break; case 9: m32 = value; break; case 10: m33 = value; break; case 11: m34 = value; break;
                case 12: m41 = value; break; case 13: m42 = value; break; case 14: m43 = value; break; default: m44 = value; break;
            }
        }
    }
    /// <summary>Column-vector product a*b (b is applied first).</summary>
    internal static SCNMatrix4 Mul(in SCNMatrix4 a, in SCNMatrix4 b)
    {
        var r = new SCNMatrix4();
        for (int c = 0; c < 4; c++)
            for (int row = 0; row < 4; row++)
                r[c, row] = a[0, row] * b[c, 0] + a[1, row] * b[c, 1] + a[2, row] * b[c, 2] + a[3, row] * b[c, 3];
        return r;
    }
    internal readonly SCNVector3 TransformPoint(SCNVector3 p) => new(
        this[0, 0] * p.x + this[1, 0] * p.y + this[2, 0] * p.z + this[3, 0],
        this[0, 1] * p.x + this[1, 1] * p.y + this[2, 1] * p.z + this[3, 1],
        this[0, 2] * p.x + this[1, 2] * p.y + this[2, 2] * p.z + this[3, 2]);
    internal readonly SCNVector3 TransformVector(SCNVector3 p) => new(
        this[0, 0] * p.x + this[1, 0] * p.y + this[2, 0] * p.z,
        this[0, 1] * p.x + this[1, 1] * p.y + this[2, 1] * p.z,
        this[0, 2] * p.x + this[1, 2] * p.y + this[2, 2] * p.z);
    internal static SCNMatrix4 Inverse(in SCNMatrix4 m)
    {
        var a = new double[16];
        for (int i = 0; i < 16; i++) a[i] = m[i / 4, i % 4];
        var inv = new double[16];
        inv[0] = a[5] * a[10] * a[15] - a[5] * a[11] * a[14] - a[9] * a[6] * a[15] + a[9] * a[7] * a[14] + a[13] * a[6] * a[11] - a[13] * a[7] * a[10];
        inv[4] = -a[4] * a[10] * a[15] + a[4] * a[11] * a[14] + a[8] * a[6] * a[15] - a[8] * a[7] * a[14] - a[12] * a[6] * a[11] + a[12] * a[7] * a[10];
        inv[8] = a[4] * a[9] * a[15] - a[4] * a[11] * a[13] - a[8] * a[5] * a[15] + a[8] * a[7] * a[13] + a[12] * a[5] * a[11] - a[12] * a[7] * a[9];
        inv[12] = -a[4] * a[9] * a[14] + a[4] * a[10] * a[13] + a[8] * a[5] * a[14] - a[8] * a[6] * a[13] - a[12] * a[5] * a[10] + a[12] * a[6] * a[9];
        inv[1] = -a[1] * a[10] * a[15] + a[1] * a[11] * a[14] + a[9] * a[2] * a[15] - a[9] * a[3] * a[14] - a[13] * a[2] * a[11] + a[13] * a[3] * a[10];
        inv[5] = a[0] * a[10] * a[15] - a[0] * a[11] * a[14] - a[8] * a[2] * a[15] + a[8] * a[3] * a[14] + a[12] * a[2] * a[11] - a[12] * a[3] * a[10];
        inv[9] = -a[0] * a[9] * a[15] + a[0] * a[11] * a[13] + a[8] * a[1] * a[15] - a[8] * a[3] * a[13] - a[12] * a[1] * a[11] + a[12] * a[3] * a[9];
        inv[13] = a[0] * a[9] * a[14] - a[0] * a[10] * a[13] - a[8] * a[1] * a[14] + a[8] * a[2] * a[13] + a[12] * a[1] * a[10] - a[12] * a[2] * a[9];
        inv[2] = a[1] * a[6] * a[15] - a[1] * a[7] * a[14] - a[5] * a[2] * a[15] + a[5] * a[3] * a[14] + a[13] * a[2] * a[7] - a[13] * a[3] * a[6];
        inv[6] = -a[0] * a[6] * a[15] + a[0] * a[7] * a[14] + a[4] * a[2] * a[15] - a[4] * a[3] * a[14] - a[12] * a[2] * a[7] + a[12] * a[3] * a[6];
        inv[10] = a[0] * a[5] * a[15] - a[0] * a[7] * a[13] - a[4] * a[1] * a[15] + a[4] * a[3] * a[13] + a[12] * a[1] * a[7] - a[12] * a[3] * a[5];
        inv[14] = -a[0] * a[5] * a[14] + a[0] * a[6] * a[13] + a[4] * a[1] * a[14] - a[4] * a[2] * a[13] - a[12] * a[1] * a[6] + a[12] * a[2] * a[5];
        inv[3] = -a[1] * a[6] * a[11] + a[1] * a[7] * a[10] + a[5] * a[2] * a[11] - a[5] * a[3] * a[10] - a[9] * a[2] * a[7] + a[9] * a[3] * a[6];
        inv[7] = a[0] * a[6] * a[11] - a[0] * a[7] * a[10] - a[4] * a[2] * a[11] + a[4] * a[3] * a[10] + a[8] * a[2] * a[7] - a[8] * a[3] * a[6];
        inv[11] = -a[0] * a[5] * a[11] + a[0] * a[7] * a[9] + a[4] * a[1] * a[11] - a[4] * a[3] * a[9] - a[8] * a[1] * a[7] + a[8] * a[3] * a[5];
        inv[15] = a[0] * a[5] * a[10] - a[0] * a[6] * a[9] - a[4] * a[1] * a[10] + a[4] * a[2] * a[9] + a[8] * a[1] * a[6] - a[8] * a[2] * a[5];
        double det = a[0] * inv[0] + a[1] * inv[4] + a[2] * inv[8] + a[3] * inv[12];
        if (det == 0) return m;
        det = 1.0 / det;
        var r = new SCNMatrix4();
        for (int i = 0; i < 16; i++) r[i / 4, i % 4] = inv[i] * det;
        return r;
    }
    internal static SCNMatrix4 Translation(double x, double y, double z) => new(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, x, y, z, 1);
    internal static SCNMatrix4 Scale(double x, double y, double z) => new(x, 0, 0, 0, 0, y, 0, 0, 0, 0, z, 0, 0, 0, 0, 1);
    /// <summary>Rotation matrix of a unit quaternion (x,y,z,w).</summary>
    internal static SCNMatrix4 Rotation(SCNVector4 q)
    {
        double x = q.x, y = q.y, z = q.z, w = q.w;
        double xx = x * x, yy = y * y, zz = z * z, xy = x * y, xz = x * z, yz = y * z, wx = w * x, wy = w * y, wz = w * z;
        return new SCNMatrix4(
            1 - 2 * (yy + zz), 2 * (xy + wz), 2 * (xz - wy), 0,
            2 * (xy - wz), 1 - 2 * (xx + zz), 2 * (yz + wx), 0,
            2 * (xz + wy), 2 * (yz - wx), 1 - 2 * (xx + yy), 0,
            0, 0, 0, 1);
    }
    internal readonly SCNVector3 Column(int c) => new(this[c, 0], this[c, 1], this[c, 2]);
    internal readonly bool IsIdentity => Equals(Identity);

    public readonly bool Equals(SCNMatrix4 o) =>
        m11 == o.m11 && m12 == o.m12 && m13 == o.m13 && m14 == o.m14 && m21 == o.m21 && m22 == o.m22 && m23 == o.m23 && m24 == o.m24 &&
        m31 == o.m31 && m32 == o.m32 && m33 == o.m33 && m34 == o.m34 && m41 == o.m41 && m42 == o.m42 && m43 == o.m43 && m44 == o.m44;
    public override bool Equals(object o) => o is SCNMatrix4 m && Equals(m);
    public override int GetHashCode() => HashCode.Combine(HashCode.Combine(m11, m12, m13, m21, m22, m23), HashCode.Combine(m31, m32, m33, m41, m42, m43));
    public static bool operator ==(SCNMatrix4 a, SCNMatrix4 b) => a.Equals(b);
    public static bool operator !=(SCNMatrix4 a, SCNMatrix4 b) => !a.Equals(b);
    public override string ToString() => $"SCNMatrix4([{m11} {m12} {m13} {m14}] [{m21} {m22} {m23} {m24}] [{m31} {m32} {m33} {m34}] [{m41} {m42} {m43} {m44}])";

    /// <summary>Affine part as a Godot Transform3D (Basis columns = rows 1-3, origin = row 4).</summary>
    public Transform3D ToGodot() => new(
        new Basis(new Vector3((float)m11, (float)m12, (float)m13), new Vector3((float)m21, (float)m22, (float)m23), new Vector3((float)m31, (float)m32, (float)m33)),
        new Vector3((float)m41, (float)m42, (float)m43));
    public static SCNMatrix4 FromGodot(Transform3D t) => new(
        t.Basis.Column0.X, t.Basis.Column0.Y, t.Basis.Column0.Z, 0,
        t.Basis.Column1.X, t.Basis.Column1.Y, t.Basis.Column1.Z, 0,
        t.Basis.Column2.X, t.Basis.Column2.Y, t.Basis.Column2.Z, 0,
        t.Origin.X, t.Origin.Y, t.Origin.Z, 1);
    public Projection ToGodotProjection() => new(
        new Vector4((float)m11, (float)m12, (float)m13, (float)m14), new Vector4((float)m21, (float)m22, (float)m23, (float)m24),
        new Vector4((float)m31, (float)m32, (float)m33, (float)m34), new Vector4((float)m41, (float)m42, (float)m43, (float)m44));
}

/// <summary>SceneKit free functions and constants. Imported with `using static` (see GlobalUsings.cs).</summary>
public static class SCNGlobals
{
    public static readonly SCNVector3 SCNVector3Zero = SCNVector3.Zero;
    public static readonly SCNVector4 SCNVector4Zero = SCNVector4.Zero;
    public static readonly SCNMatrix4 SCNMatrix4Identity = SCNMatrix4.Identity;
    public static SCNVector3 SCNVector3Make(double x, double y, double z) => new(x, y, z);
    public static SCNVector4 SCNVector4Make(double x, double y, double z, double w) => new(x, y, z, w);
    public static bool SCNVector3EqualToVector3(SCNVector3 a, SCNVector3 b) => a == b;
    public static bool SCNVector4EqualToVector4(SCNVector4 a, SCNVector4 b) => a == b;
    public static bool SCNMatrix4EqualToMatrix4(SCNMatrix4 a, SCNMatrix4 b) => a == b;
    public static bool SCNMatrix4IsIdentity(SCNMatrix4 m) => m.IsIdentity;
    public static SCNMatrix4 SCNMatrix4MakeTranslation(double tx, double ty, double tz) => SCNMatrix4.Translation(tx, ty, tz);
    public static SCNMatrix4 SCNMatrix4MakeScale(double sx, double sy, double sz) => SCNMatrix4.Scale(sx, sy, sz);
    public static SCNMatrix4 SCNMatrix4MakeRotation(double angle, double x, double y, double z) =>
        SCNMatrix4.Rotation(SCNNode.QuaternionFromAxisAngle(new SCNVector4(x, y, z, angle)));
    /// <summary>SCNMatrix4Mult(a, b): a then b (row-vector convention) = b*a in column vectors.</summary>
    public static SCNMatrix4 SCNMatrix4Mult(SCNMatrix4 a, SCNMatrix4 b) => SCNMatrix4.Mul(b, a);
    public static SCNMatrix4 SCNMatrix4Invert(SCNMatrix4 m) => SCNMatrix4.Inverse(m);
    /// <summary>SCNMatrix4Translate(m, t): m followed by a translation.</summary>
    public static SCNMatrix4 SCNMatrix4Translate(SCNMatrix4 m, double tx, double ty, double tz) => SCNMatrix4.Mul(SCNMatrix4.Translation(tx, ty, tz), m);
    public static SCNMatrix4 SCNMatrix4Scale(SCNMatrix4 m, double sx, double sy, double sz) => SCNMatrix4.Mul(SCNMatrix4.Scale(sx, sy, sz), m);
    public static SCNMatrix4 SCNMatrix4Rotate(SCNMatrix4 m, double angle, double x, double y, double z) => SCNMatrix4.Mul(SCNMatrix4MakeRotation(angle, x, y, z), m);
    public static SCNFloat4x4 SCNMatrix4ToMat4(SCNMatrix4 m) => SimdBridge.M(m);
    public static SCNMatrix4 SCNMatrix4FromMat4(SCNFloat4x4 m) => SimdBridge.M(m);
    public static SCNFloat3 SCNVector3ToFloat3(SCNVector3 v) => v.simd;
    public static SCNVector3 SCNVector3FromFloat3(SCNFloat3 v) => new(v);
    public static SCNFloat4 SCNVector4ToFloat4(SCNVector4 v) => v.simd;
    public static SCNVector4 SCNVector4FromFloat4(SCNFloat4 v) => new(v);

    // Metal entry points used by the app.
    public static MTLDevice MTLCreateSystemDefaultDevice() => MTLDevice.Shared;
    public static MTLRegion MTLRegionMake2D(int x, int y, int width, int height) => new(new MTLOrigin(x, y, 0), new MTLSize(width, height, 1));
    public static MTLClearColor MTLClearColorMake(double red, double green, double blue, double alpha) => new(red, green, blue, alpha);
}
