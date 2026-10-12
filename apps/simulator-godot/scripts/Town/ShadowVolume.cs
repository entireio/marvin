using System;
using System.Collections.Generic;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

/// Conservative side-plane test. Deliberately omit near/far rejection: keeping
/// an extra caster is harmless, while clipping a shadow volume is not.
public readonly struct ShadowBounds
{
    public readonly Double3 low, high;
    public readonly List<Double3> corners;
    public ShadowBounds(Double3 low, Double3 high)
    {
        this.low = low; this.high = high;
        var result = new List<Double3>();
        foreach (var x in new[] { low.x, high.x }) { foreach (var y in new[] { low.y, high.y }) { foreach (var z in new[] { low.z, high.z }) { result.Add(new Double3(x, y, z)); } } }
        corners = result;
    }
}
public readonly struct ShadowFrustum
{
    public readonly Float4x4 inverse;
    public readonly float tanX, tanY;
    public ShadowFrustum(Float4x4 inverse, float tanX, float tanY) { this.inverse = inverse; this.tanX = tanX; this.tanY = tanY; }
    public static List<ShadowFrustum> cameras(SCNNode node, double aspect)
    {
        if (!(node?.camera is SCNCamera camera && !camera.usesOrthographicProjection
              && double.IsFinite(aspect) && aspect > 0 && camera.fieldOfView > 0 && camera.fieldOfView < 175)) { return new List<ShadowFrustum>(); }
        // A small extra angular guard only retains additional shadow casters.
        var tangent = (float)tan(((double)camera.fieldOfView + 1) * Math.PI / 360);
        var x = camera.projectionDirection == SCNCameraProjectionDirection.horizontal ? tangent : tangent * (float)aspect;
        var y = camera.projectionDirection == SCNCameraProjectionDirection.horizontal ? tangent / (float)aspect : tangent;
        // PORT: node.simdWorldTransform (Float); the facade's worldTransform is SCNMatrix4 (row i = simd column i).
        var w = node.worldTransform;
        var world = new Float4x4(new Float4((float)w.m11, (float)w.m12, (float)w.m13, (float)w.m14), new Float4((float)w.m21, (float)w.m22, (float)w.m23, (float)w.m24),
                                 new Float4((float)w.m31, (float)w.m32, (float)w.m33, (float)w.m34), new Float4((float)w.m41, (float)w.m42, (float)w.m43, (float)w.m44));
        return new[] { world }.Where(matrix => Enumerable.Range(0, 4).All(i => Enumerable.Range(0, 4).All(j => float.IsFinite(matrix[i][j]))))
            .Select(matrix => new ShadowFrustum(inverse: Simd.inverse(matrix), tanX: x, tanY: y)).ToList();
    }
    public bool intersects(ShadowBounds bounds)
    {
        bool right = true, left = true, top = true, bottom = true, behind = true;
        foreach (var point in bounds.corners)
        {
            var p = inverse * new Float4((float)point.x, (float)point.y, (float)point.z, 1);
            if (!Enumerable.Range(0, 4).All(i => float.IsFinite(p[i]))) { return true; }
            right = right && p.x > -p.z * tanX; left = left && p.x < p.z * tanX;
            top = top && p.y > -p.z * tanY; bottom = bottom && p.y < p.z * tanY;
            behind = behind && p.z > 0;
        }
        return !(right || left || top || bottom || behind);
    }
}
