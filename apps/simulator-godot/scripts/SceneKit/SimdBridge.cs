// simd types used by the SceneKit facade's simd* members (simdPosition,
// simdOrientation, simdTransform, simdWorldTransform, ...).
//
// Swift's SIMD3<Float>, simd_quatf and simd_float4x4 are ported by MarvinCore
// (Float3, QuatF, Float4x4). Until MarvinCore provides them, the facade uses
// Godot's float types through these aliases. Facade code never touches the
// aliased types directly: it goes through SimdBridge, so switching the aliases
// to MarvinCore is a change to this one file:
//
//   1. Define MARVIN_CORE_SIMD in MarvinGodot.csproj (<DefineConstants>).
//   2. Check the member names used in the #if MARVIN_CORE_SIMD branch below
//      against MarvinCore (x/y/z/w fields, QuatF(ix,iy,iz,r), Float4x4 columns).
//
// The rest of the facade (and every ported app file) then sees MarvinCore types.
#if MARVIN_CORE_SIMD
global using SCNFloat3 = Marvin.Core.Float3;
global using SCNFloat4 = Marvin.Core.Float4;
global using SCNQuatF = Marvin.Core.QuatF;
global using SCNFloat4x4 = Marvin.Core.Float4x4;
global using SCNDouble3 = Marvin.Core.Double3;
#else
global using SCNFloat3 = Godot.Vector3;
global using SCNFloat4 = Godot.Vector4;
global using SCNQuatF = Godot.Quaternion;
global using SCNFloat4x4 = Godot.Projection;
global using SCNDouble3 = Godot.Vector3;
#endif

using Godot;

namespace Marvin.SceneKit;

/// <summary>Conversions between facade value types and the simd alias types.</summary>
public static class SimdBridge
{
#if MARVIN_CORE_SIMD
    public static SCNFloat3 F3(double x, double y, double z) => new SCNFloat3((float)x, (float)y, (float)z);
    public static (double x, double y, double z) Get(SCNFloat3 v) => (v.x, v.y, v.z);
    public static SCNFloat4 F4(double x, double y, double z, double w) => new SCNFloat4((float)x, (float)y, (float)z, (float)w);
    public static (double x, double y, double z, double w) Get(SCNFloat4 v) => (v.x, v.y, v.z, v.w);
    public static SCNQuatF Q(double x, double y, double z, double w) => new SCNQuatF((float)x, (float)y, (float)z, (float)w);
    public static (double x, double y, double z, double w) Get(SCNQuatF q) => (q.vector.x, q.vector.y, q.vector.z, q.vector.w);
    public static SCNFloat4x4 M(SCNMatrix4 m) => new SCNFloat4x4(
        F4(m.m11, m.m12, m.m13, m.m14), F4(m.m21, m.m22, m.m23, m.m24),
        F4(m.m31, m.m32, m.m33, m.m34), F4(m.m41, m.m42, m.m43, m.m44));
    public static SCNMatrix4 M(SCNFloat4x4 m) => new SCNMatrix4(
        m.columns.Item1.x, m.columns.Item1.y, m.columns.Item1.z, m.columns.Item1.w,
        m.columns.Item2.x, m.columns.Item2.y, m.columns.Item2.z, m.columns.Item2.w,
        m.columns.Item3.x, m.columns.Item3.y, m.columns.Item3.z, m.columns.Item3.w,
        m.columns.Item4.x, m.columns.Item4.y, m.columns.Item4.z, m.columns.Item4.w);
    public static SCNDouble3 D3(double x, double y, double z) => new SCNDouble3(x, y, z);
    public static (double x, double y, double z) Get(SCNDouble3 v) => (v.x, v.y, v.z);
#else
    public static SCNFloat3 F3(double x, double y, double z) => new((float)x, (float)y, (float)z);
    public static (double x, double y, double z) Get(SCNFloat3 v) => (v.X, v.Y, v.Z);
    public static SCNFloat4 F4(double x, double y, double z, double w) => new((float)x, (float)y, (float)z, (float)w);
    public static (double x, double y, double z, double w) Get(SCNFloat4 v) => (v.X, v.Y, v.Z, v.W);
    public static SCNQuatF Q(double x, double y, double z, double w) => new((float)x, (float)y, (float)z, (float)w);
    public static (double x, double y, double z, double w) Get(SCNQuatF q) => (q.X, q.Y, q.Z, q.W);
    /// <summary>SCNMatrix4 (row i = simd column i) to a column-major 4x4.</summary>
    public static SCNFloat4x4 M(SCNMatrix4 m) => new(
        new Vector4((float)m.m11, (float)m.m12, (float)m.m13, (float)m.m14),
        new Vector4((float)m.m21, (float)m.m22, (float)m.m23, (float)m.m24),
        new Vector4((float)m.m31, (float)m.m32, (float)m.m33, (float)m.m34),
        new Vector4((float)m.m41, (float)m.m42, (float)m.m43, (float)m.m44));
    public static SCNMatrix4 M(SCNFloat4x4 m) => new(
        m.X.X, m.X.Y, m.X.Z, m.X.W, m.Y.X, m.Y.Y, m.Y.Z, m.Y.W,
        m.Z.X, m.Z.Y, m.Z.Z, m.Z.W, m.W.X, m.W.Y, m.W.Z, m.W.W);
#endif
}
