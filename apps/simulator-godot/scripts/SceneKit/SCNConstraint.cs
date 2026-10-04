using System;
using Godot;

namespace Marvin.SceneKit;

[Flags]
public enum SCNBillboardAxis { X = 1, Y = 2, Z = 4, all = 7 }

/// <summary>
/// SCNConstraint. Constraints change only the rendered (Godot) transform, never the
/// node's model values, like SceneKit's presentation tree. Evaluated every frame
/// with the active view's point of view.
/// </summary>
public abstract class SCNConstraint
{
    public double influenceFactor = 1;
    public bool isEnabled = true;
    public bool isIncremental = true;
    /// <summary>Returns the constrained world (render) matrix.</summary>
    internal abstract SCNMatrix4 Apply(SCNNode node, SCNMatrix4 world, SCNNode camera);
}

/// <summary>SCNBillboardConstraint: orients the node like the camera (its +Z faces the viewer).</summary>
public sealed class SCNBillboardConstraint : SCNConstraint
{
    public SCNBillboardAxis freeAxes = SCNBillboardAxis.all;
    internal override SCNMatrix4 Apply(SCNNode node, SCNMatrix4 world, SCNNode camera)
    {
        if (camera == null) return world;
        var (pos, _, scale) = SCNNode.Decompose(world);
        var cam = camera.RenderWorld();
        SCNVector4 q;
        if (freeAxes == SCNBillboardAxis.Y)
        {
            var toCam = cam.Column(3) - pos;
            double yaw = Math.Atan2(toCam.x, toCam.z);
            q = SCNNode.QuaternionFromEuler(new SCNVector3(0, yaw, 0));
        }
        else q = SCNNode.QuaternionFromMatrix(cam);
        return SCNMatrix4.Mul(SCNMatrix4.Translation(pos.x, pos.y, pos.z), SCNMatrix4.Mul(SCNMatrix4.Rotation(q), SCNMatrix4.Scale(scale.x, scale.y, scale.z)));
    }
}

/// <summary>SCNTransformConstraint (and its position/orientation factories).</summary>
public sealed class SCNTransformConstraint : SCNConstraint
{
    private readonly bool worldSpace;
    private readonly Func<SCNNode, SCNMatrix4, SCNMatrix4> transformBlock;
    private readonly Func<SCNNode, SCNVector3, SCNVector3> positionBlock;
    private readonly Func<SCNNode, SCNVector4, SCNVector4> orientationBlock;

    /// <summary>SCNTransformConstraint(inWorldSpace:with:).</summary>
    public SCNTransformConstraint(bool inWorldSpace, Func<SCNNode, SCNMatrix4, SCNMatrix4> with) { worldSpace = inWorldSpace; transformBlock = with; }
    private SCNTransformConstraint(bool inWorldSpace, Func<SCNNode, SCNVector3, SCNVector3> position, Func<SCNNode, SCNVector4, SCNVector4> orientation)
    { worldSpace = inWorldSpace; positionBlock = position; orientationBlock = orientation; }
    /// <summary>SCNTransformConstraint.positionConstraint(inWorldSpace:with:) - Swift trailing closure `{ node, position in ... }`.</summary>
    public static SCNTransformConstraint positionConstraint(bool inWorldSpace, Func<SCNNode, SCNVector3, SCNVector3> with) => new(inWorldSpace, with, null);
    public static SCNTransformConstraint orientationConstraint(bool inWorldSpace, Func<SCNNode, SCNVector4, SCNVector4> with) => new(inWorldSpace, null, with);

    internal override SCNMatrix4 Apply(SCNNode node, SCNMatrix4 world, SCNNode camera)
    {
        var parent = node.parent?.RenderWorld() ?? SCNMatrix4.Identity;
        var local = SCNMatrix4.Mul(SCNMatrix4.Inverse(parent), world);
        var current = worldSpace ? world : local;
        SCNMatrix4 result = current;
        if (transformBlock != null) result = transformBlock(node, current);
        else
        {
            var (p, q, s) = SCNNode.Decompose(current);
            if (positionBlock != null) p = positionBlock(node, p);
            if (orientationBlock != null) q = SCNNode.Normalize(orientationBlock(node, q));
            result = SCNMatrix4.Mul(SCNMatrix4.Translation(p.x, p.y, p.z), SCNMatrix4.Mul(SCNMatrix4.Rotation(q), SCNMatrix4.Scale(s.x, s.y, s.z)));
        }
        return worldSpace ? result : SCNMatrix4.Mul(parent, result);
    }
}

/// <summary>SCNLookAtConstraint: points the node's local front (-Z) at the target.</summary>
public sealed class SCNLookAtConstraint : SCNConstraint
{
    public SCNNode target;
    public bool isGimbalLockEnabled;
    public SCNVector3 localFront = new(0, 0, -1), worldUp = new(0, 1, 0);
    public SCNLookAtConstraint(SCNNode target) { this.target = target; }
    internal override SCNMatrix4 Apply(SCNNode node, SCNMatrix4 world, SCNNode camera)
    {
        if (target == null) return world;
        var (pos, _, scale) = SCNNode.Decompose(world);
        var d = (target.RenderWorld().Column(3) - pos).Normalized();
        var up = isGimbalLockEnabled ? worldUp : worldUp;
        var r = SCNVector3.Cross(d, up).Normalized(); var u = SCNVector3.Cross(r, d);
        var basis = new SCNMatrix4(r.x, r.y, r.z, 0, u.x, u.y, u.z, 0, -d.x, -d.y, -d.z, 0, 0, 0, 0, 1);
        var q = SCNNode.QuaternionFromMatrix(basis);
        return SCNMatrix4.Mul(SCNMatrix4.Translation(pos.x, pos.y, pos.z), SCNMatrix4.Mul(SCNMatrix4.Rotation(q), SCNMatrix4.Scale(scale.x, scale.y, scale.z)));
    }
}
