using System;
using System.Collections.Generic;
using static Marvin.Core.Swift;

namespace Marvin.Core;

/// Conservative collision envelopes measured from the assembled CAD meshes.
/// Uses oriented-box / axis-aligned-box separating axes, in 100 mm scene units.
/// The neck joint is intentionally excluded; the head shells must clear the base.
public static class HeadClearance
{
    private readonly struct Box
    {
        public readonly Double3 lo, hi;
        public Box(Double3 lo, Double3 hi) { this.lo = lo; this.hi = hi; }
        public Double3 center => (lo + hi) / 2;
        public Double3 half => (hi - lo) / 2;
    }
    private static readonly Box[] heads =
    {
        new Box(lo: new Double3(-0.3358, 0.4038, -0.3114), hi: new Double3(0.3362, 0.6313, 0.3332)),
        new Box(lo: new Double3(-0.3485, 0.4315, -0.3949), hi: new Double3(0.3489, 0.7066, 0.3452)),
        new Box(lo: new Double3(-0.3354, 0.4146, 0.2533), hi: new Double3(0.3354, 0.6948, 0.3451)),
        new Box(lo: new Double3(-0.2958, 0.4843, -0.4017), hi: new Double3(0.2961, 0.6568, -0.3676)),
    };
    private static readonly Box[] bodies =
    {
        new Box(lo: new Double3(-0.2805, 0.0184, -0.3944), hi: new Double3(0.2805, 0.299, 0.3357)),
        new Box(lo: new Double3(0.0813, 0.0201, -0.3954), hi: new Double3(0.3771, 0.300, 0.3368)),
        new Box(lo: new Double3(-0.3771, 0.0201, -0.3954), hi: new Double3(-0.0813, 0.300, 0.3368)),
    };
    public static bool isClear(double yaw, double pitch)
    {
        if (!(double.IsFinite(yaw) && double.IsFinite(pitch))) return false;
        Double3 rotate(Double3 v) => HeadRig.rotate(v, yaw, pitch);
        Double3[] world = { new Double3(1, 0, 0), new Double3(0, 1, 0), new Double3(0, 0, 1) };
        var axes = new Double3[3];
        for (var i = 0; i < 3; i++) axes[i] = rotate(world[i]);
        var candidates = new List<Double3>(15);
        candidates.AddRange(world);
        candidates.AddRange(axes);
        foreach (var a in world) foreach (var axis in axes) candidates.Add(Simd.cross(a, axis));
        foreach (var head in heads)
        {
            var center = HeadRig.headPoint(head.center, yaw, pitch);
            // Half-millimeter clearance, including the physical button height.
            var headHalf = head.half + new Double3(0.005);
            foreach (var body in bodies)
            {
                var delta = center - body.center;
                var separated = false;
                foreach (var candidate in candidates)
                {
                    var length = Simd.length(candidate);
                    if (length < 1e-9) continue;
                    var axis = candidate / length;
                    var a = abs(axis.x) * body.half.x + abs(axis.y) * body.half.y + abs(axis.z) * body.half.z;
                    var b = 0.0;
                    for (var k = 0; k < 3; k++) b = b + abs(Simd.dot(axis, axes[k])) * headHalf[k];
                    if (abs(Simd.dot(delta, axis)) > a + b) { separated = true; break; }
                }
                if (!separated) return false;
            }
        }
        return true;
    }
}
