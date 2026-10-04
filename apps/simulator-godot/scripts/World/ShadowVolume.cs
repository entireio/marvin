using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Marvin.Core;

namespace Marvin;

/// Conservative side-plane test. Deliberately omit near/far rejection: keeping
/// an extra caster is harmless, while clipping a shadow volume is not.
public readonly struct ShadowBounds
{
    public readonly Double3 low, high;
    public readonly Double3[] corners;
    public ShadowBounds(Double3 low, Double3 high)
    {
        this.low = low; this.high = high;
        var result = new List<Double3>();
        foreach (var x in new[] { low.x, high.x }) foreach (var y in new[] { low.y, high.y }) foreach (var z in new[] { low.z, high.z }) result.Add(new Double3(x, y, z));
        corners = result.ToArray();
    }
}
public readonly struct ShadowFrustum
{
    // PORT: simd_float4x4 is the facade's SCNFloat4x4 (what simdWorldTransform returns).
    public readonly SCNFloat4x4 inverse;
    public readonly float tanX, tanY;
    public ShadowFrustum(SCNFloat4x4 inverse, float tanX, float tanY) { this.inverse = inverse; this.tanX = tanX; this.tanY = tanY; }
    public static ShadowFrustum[] cameras(SCNNode node, double aspect)
    {
        if (!(node != null && node.camera is SCNCamera camera && !camera.usesOrthographicProjection
              && double.IsFinite(aspect) && aspect > 0 && camera.fieldOfView > 0 && camera.fieldOfView < 175)) return Array.Empty<ShadowFrustum>();
        // A small extra angular guard only retains additional shadow casters.
        float tangent = (float)Math.Tan((camera.fieldOfView + 1) * Math.PI / 360);
        float x = camera.projectionDirection == SCNCameraProjectionDirection.horizontal ? tangent : tangent * (float)aspect;
        float y = camera.projectionDirection == SCNCameraProjectionDirection.horizontal ? tangent / (float)aspect : tangent;
        return new[] { node.simdWorldTransform }.Select(matrix =>
        {
            for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) if (!float.IsFinite(matrix[i][j])) return (ShadowFrustum?)null;
            return new ShadowFrustum(matrix.Inverse(), x, y);
        }).Where(f => f.HasValue).Select(f => f.Value).ToArray();
    }
    public bool intersects(ShadowBounds bounds)
    {
        bool right = true, left = true, top = true, bottom = true, behind = true;
        foreach (var point in bounds.corners)
        {
            var p = inverse * new Vector4((float)point.x, (float)point.y, (float)point.z, 1);
            if (!(float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z) && float.IsFinite(p.W))) return true;
            right = right && p.X > -p.Z * tanX; left = left && p.X < p.Z * tanX;
            top = top && p.Y > -p.Z * tanY; bottom = bottom && p.Y < p.Z * tanY;
            behind = behind && p.Z > 0;
        }
        return !(right || left || top || bottom || behind);
    }
}
