using System;
using static Marvin.Core.Swift;

namespace Marvin.Core;

/// The neck's lower circular sections share X=0, Z=-1.886 mm in the CAD.
/// Yaw must use that center, while retaining the inferred pitch pivot.
public static class HeadRig
{
    public static readonly Double3 yawPivot = new Double3(0, 0.30, -0.01886);
    public static readonly Double3 pitchPivot = new Double3(0, 0.56, 0);

    public static Double3 rotate(Double3 v, double yaw, double pitch)
    {
        var y = cos(pitch) * v.y + sin(pitch) * v.z;
        var z = -sin(pitch) * v.y + cos(pitch) * v.z;
        return new Double3(cos(yaw) * v.x + sin(yaw) * z, y, -sin(yaw) * v.x + cos(yaw) * z);
    }

    public static Double3 headPoint(Double3 point, double yaw, double pitch)
    {
        var tilted = rotate(point - pitchPivot, 0, pitch) + pitchPivot;
        return rotate(tilted - yawPivot, yaw, 0) + yawPivot;
    }
}
