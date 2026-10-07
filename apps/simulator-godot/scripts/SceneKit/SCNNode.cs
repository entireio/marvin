using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Marvin.SceneKit;

public enum SCNMovabilityHint { fixedHint = 0, movable = 1 }

/// <summary>enumerateChildNodes stop flag (Swift: UnsafeMutablePointer&lt;ObjCBool&gt;, `stop.pointee = true`).</summary>
public sealed class SCNStop { public bool pointee; }

/// <summary>
/// SCNNode as a Godot Node3D.
///
/// Transform members (position, eulerAngles, orientation, rotation, scale, pivot)
/// are `ref` properties so Swift code such as `node.position.y = 1` and
/// `node.eulerAngles.x = -.pi/2` ports unchanged. The facade keeps SceneKit's
/// model values (euler angles read back exactly as written, like SceneKit) and
/// pushes them to the Godot transform once per frame.
///
/// Measured SceneKit semantics:
/// - eulerAngles (x pitch, y yaw, z roll): R = Rz * Ry * Rx (pitch applied first) = Godot EulerOrder.Zyx.
/// - Rendering applies the pivot inside the node: world = parent * T * R * S * pivot⁻¹.
/// - The model-space API (transform excludes pivot; worldTransform, convertPosition,
///   worldPosition) applies it outside: parent * pivot⁻¹ * T * R * S. The facade
///   reproduces both (they differ only for nodes with a pivot).
/// </summary>
public partial class SCNNode : Node3D
{
    // ---- SceneKit model state
    private SCNVector3 _position, _scale = new(1, 1, 1);
    private SCNVector3 _euler, _eulerSeen;
    private SCNVector4 _orientation = new(0, 0, 0, 1), _orientationSeen = new(0, 0, 0, 1);
    private SCNVector4 _rotation = new(0, 0, 0, 0), _rotationSeen = new(0, 0, 0, 0);
    private SCNVector4 _quat = new(0, 0, 0, 1);
    private SCNMatrix4 _pivot = SCNMatrix4.Identity;
    private SCNNode _parent;
    private readonly List<SCNNode> _children = new();
    private SCNGeometry _geometry;
    private SCNLight _light;
    private SCNCamera _camera;
    private bool _isHidden;
    private double _opacity = 1;
    private bool _castsShadow = true;
    private int _categoryBitMask = 1;
    private int _renderingOrder;
    private List<SCNConstraint> _constraints;
    public string name;
    public SCNMovabilityHint movabilityHint;
    /// <summary>Scene this node is attached to (set on the scene's root node and propagated).</summary>
    internal SCNScene sceneOwner;

    public SCNNode() { SceneKitRuntime.EnsureStarted(); SceneKitRuntime.TrackNode(this); }
    public SCNNode(SCNGeometry geometry) : this() { this.geometry = geometry; }

    // =====================================================================
    // Transform (ref properties; see class summary)
    public ref SCNVector3 position { get { Quantize(); Touch(); return ref _position; } }
    public ref SCNVector3 scale { get { Quantize(); Touch(); return ref _scale; } }
    public ref SCNVector3 eulerAngles { get { SyncRotation(); Touch(); return ref _euler; } }
    public ref SCNVector4 orientation { get { SyncRotation(); Touch(); return ref _orientation; } }
    public ref SCNVector4 rotation { get { SyncRotation(); Touch(); return ref _rotation; } }
    public ref SCNMatrix4 pivot { get { Quantize(); Touch(); return ref _pivot; } }

    private void Touch() => SceneKitRuntime.NodeDirty(this, DirtyTransform);

    // Measured (Swift probe on macOS 27): SceneKit keeps a node's position, scale, eulerAngles, orientation, rotation,
    // pivot, transform and opacity in single precision, and answers world-space queries (worldPosition,
    // convertPosition) in single precision: position 0.1 reads back 0.10000000149011612, eulerAngles.y = 0.7 reads
    // 0.699999988079071 (4 reads 4), a Double scale of 0.5555555555555555 reads 0.5555555820465088 (so the smoke
    // test's marvinToR2D2HeightRatio is 0.5555555673827447 on macOS). The ref properties cannot round a write, so
    // every read path rounds the stored values first (Quantize); the rotation is derived from the rounded values.
    private static double F(double v) => (float)v;
    private static SCNVector3 F(SCNVector3 v) => new(F(v.x), F(v.y), F(v.z));
    private static SCNVector4 F(SCNVector4 v) => new(F(v.x), F(v.y), F(v.z), F(v.w));
    private static SCNMatrix4 F(SCNMatrix4 m) => new(F(m.m11), F(m.m12), F(m.m13), F(m.m14), F(m.m21), F(m.m22), F(m.m23), F(m.m24),
        F(m.m31), F(m.m32), F(m.m33), F(m.m34), F(m.m41), F(m.m42), F(m.m43), F(m.m44));
    private void Quantize()
    {
        _position = F(_position); _scale = F(_scale);
        _euler = F(_euler); _orientation = F(_orientation); _rotation = F(_rotation);
        if (!_pivot.IsIdentity) _pivot = F(_pivot);
    }

    private void SyncRotation()
    {
        Quantize();
        if (_euler != _eulerSeen)
        {
            _quat = QuaternionFromEuler(_euler);
            _orientation = F(_quat);
            _rotation = F(AxisAngleFromQuaternion(_quat));
        }
        else if (_orientation != _orientationSeen)
        {
            _quat = Normalize(_orientation);
            _euler = F(EulerFromQuaternion(_quat));
            _rotation = F(AxisAngleFromQuaternion(_quat));
        }
        else if (_rotation != _rotationSeen)
        {
            _quat = QuaternionFromAxisAngle(_rotation);
            _euler = F(EulerFromQuaternion(_quat));
            _orientation = F(_quat);
        }
        _eulerSeen = _euler; _orientationSeen = _orientation; _rotationSeen = _rotation;
    }

    /// <summary>transform: T * R * S (excludes the pivot, as SceneKit's model API).</summary>
    public SCNMatrix4 transform
    {
        get { SyncRotation(); return F(TRS()); }
        set
        {
            var (p, q, s) = Decompose(value);
            _position = p; _scale = s; _orientation = q; SyncRotation(); Touch();
        }
    }
    private SCNMatrix4 TRS() =>
        SCNMatrix4.Mul(SCNMatrix4.Translation(_position.x, _position.y, _position.z),
            SCNMatrix4.Mul(SCNMatrix4.Rotation(_quat), SCNMatrix4.Scale(_scale.x, _scale.y, _scale.z)));
    /// <summary>Local matrix of the model API (pivot applied in parent space, measured quirk).</summary>
    internal SCNMatrix4 ModelLocal() { SyncRotation(); return _pivot.IsIdentity ? TRS() : SCNMatrix4.Mul(SCNMatrix4.Inverse(_pivot), TRS()); }
    /// <summary>Local matrix used for rendering (pivot applied inside the node).</summary>
    internal SCNMatrix4 RenderLocal() { SyncRotation(); return _pivot.IsIdentity ? TRS() : SCNMatrix4.Mul(TRS(), SCNMatrix4.Inverse(_pivot)); }
    internal SCNMatrix4 ModelWorld() => _parent == null ? ModelLocal() : SCNMatrix4.Mul(_parent.ModelWorld(), ModelLocal());
    internal SCNMatrix4 RenderWorld() => _parent == null ? RenderLocal() : SCNMatrix4.Mul(_parent.RenderWorld(), RenderLocal());

    public SCNMatrix4 worldTransform
    {
        get => F(ModelWorld());
        set => transform = _parent == null ? SCNMatrix4.Mul(_pivot, value) : SCNMatrix4.Mul(_pivot, SCNMatrix4.Mul(SCNMatrix4.Inverse(_parent.ModelWorld()), value));
    }
    public SCNVector3 worldPosition
    {
        get => F(ModelWorld().Column(3));
        set
        {
            var parentWorld = _parent == null ? SCNMatrix4.Identity : _parent.ModelWorld();
            var local = SCNMatrix4.Inverse(SCNMatrix4.Mul(parentWorld, SCNMatrix4.Inverse(_pivot))).TransformPoint(value);
            position = local;
        }
    }
    public SCNVector4 worldOrientation
    {
        get => F(QuaternionFromMatrix(ModelWorld()));
        set
        {
            var parentRot = _parent == null ? new SCNVector4(0, 0, 0, 1) : QuaternionFromMatrix(_parent.ModelWorld());
            orientation = QMul(QConj(parentRot), Normalize(value));
        }
    }
    public SCNVector3 worldFront => F(ModelWorld().TransformVector(new SCNVector3(0, 0, -1)).Normalized());
    public SCNVector3 worldUp => F(ModelWorld().TransformVector(new SCNVector3(0, 1, 0)).Normalized());
    public SCNVector3 worldRight => F(ModelWorld().TransformVector(new SCNVector3(1, 0, 0)).Normalized());
    public static SCNVector3 localFront => new(0, 0, -1);
    public static SCNVector3 localUp => new(0, 1, 0);
    public static SCNVector3 localRight => new(1, 0, 0);

    // ---- simd variants (types aliased in SimdBridge.cs)
    public SCNFloat3 simdPosition { get { Quantize(); return _position.simd; } set => position = new SCNVector3(value); }
    public SCNFloat3 simdScale { get { Quantize(); return _scale.simd; } set => scale = new SCNVector3(value); }
    public SCNFloat3 simdEulerAngles { get { SyncRotation(); return _euler.simd; } set => eulerAngles = new SCNVector3(value); }
    public SCNQuatF simdOrientation
    {
        get { SyncRotation(); return SimdBridge.Q(_orientation.x, _orientation.y, _orientation.z, _orientation.w); }
        set { var (x, y, z, w) = SimdBridge.Get(value); orientation = new SCNVector4(x, y, z, w); }
    }
    public SCNFloat4 simdRotation { get { SyncRotation(); return _rotation.simd; } set => rotation = new SCNVector4(value); }
    public SCNFloat4x4 simdTransform { get => SimdBridge.M(transform); set => transform = SimdBridge.M(value); }
    public SCNFloat4x4 simdPivot { get { Quantize(); return SimdBridge.M(_pivot); } set => pivot = SimdBridge.M(value); }
    public SCNFloat4x4 simdWorldTransform { get => SimdBridge.M(worldTransform); set => worldTransform = SimdBridge.M(value); }
    public SCNFloat3 simdWorldPosition { get => worldPosition.simd; set => worldPosition = new SCNVector3(value); }
    public SCNQuatF simdWorldOrientation
    {
        get { var q = worldOrientation; return SimdBridge.Q(q.x, q.y, q.z, q.w); }
        set { var (x, y, z, w) = SimdBridge.Get(value); worldOrientation = new SCNVector4(x, y, z, w); }
    }
    public SCNFloat3 simdWorldFront => worldFront.simd;
    public SCNFloat3 simdWorldUp => worldUp.simd;
    public SCNFloat3 simdWorldRight => worldRight.simd;
    public static SCNFloat3 simdLocalFront => localFront.simd;
    public static SCNFloat3 simdLocalUp => localUp.simd;
    public static SCNFloat3 simdLocalRight => localRight.simd;

    // ---- coordinate conversion. Swift `convertX(_:from:)` is `convertXFrom(_, node)` here (same types as `to:`).
    public SCNVector3 convertPosition(SCNVector3 position, SCNNode to) =>
        F(to == null ? ModelWorld().TransformPoint(position) : SCNMatrix4.Inverse(to.ModelWorld()).TransformPoint(ModelWorld().TransformPoint(position)));
    public SCNVector3 convertPositionFrom(SCNVector3 position, SCNNode from) =>
        F(SCNMatrix4.Inverse(ModelWorld()).TransformPoint(from == null ? position : from.ModelWorld().TransformPoint(position)));
    public SCNVector3 convertVector(SCNVector3 vector, SCNNode to) =>
        F(to == null ? ModelWorld().TransformVector(vector) : SCNMatrix4.Inverse(to.ModelWorld()).TransformVector(ModelWorld().TransformVector(vector)));
    public SCNVector3 convertVectorFrom(SCNVector3 vector, SCNNode from) =>
        F(SCNMatrix4.Inverse(ModelWorld()).TransformVector(from == null ? vector : from.ModelWorld().TransformVector(vector)));
    public SCNMatrix4 convertTransform(SCNMatrix4 transform, SCNNode to) =>
        F(to == null ? SCNMatrix4.Mul(ModelWorld(), transform) : SCNMatrix4.Mul(SCNMatrix4.Inverse(to.ModelWorld()), SCNMatrix4.Mul(ModelWorld(), transform)));
    public SCNMatrix4 convertTransformFrom(SCNMatrix4 transform, SCNNode from) =>
        F(SCNMatrix4.Mul(SCNMatrix4.Inverse(ModelWorld()), from == null ? transform : SCNMatrix4.Mul(from.ModelWorld(), transform)));
    public SCNFloat3 simdConvertPosition(SCNFloat3 position, SCNNode to) => convertPosition(new SCNVector3(position), to).simd;
    public SCNFloat3 simdConvertPositionFrom(SCNFloat3 position, SCNNode from) => convertPositionFrom(new SCNVector3(position), from).simd;
    public SCNFloat3 simdConvertVector(SCNFloat3 vector, SCNNode to) => convertVector(new SCNVector3(vector), to).simd;
    public SCNFloat3 simdConvertVectorFrom(SCNFloat3 vector, SCNNode from) => convertVectorFrom(new SCNVector3(vector), from).simd;
    public SCNFloat4x4 simdConvertTransform(SCNFloat4x4 transform, SCNNode to) => SimdBridge.M(convertTransform(SimdBridge.M(transform), to));
    public SCNFloat4x4 simdConvertTransformFrom(SCNFloat4x4 transform, SCNNode from) => SimdBridge.M(convertTransformFrom(SimdBridge.M(transform), from));

    // ---- look(at:)
    /// <summary>look(at:): SceneKit uses the node's current worldUp as the up vector (and localFront -Z), so a
    /// camera that was pitched keeps a rolled horizon after a second look(at:) (measured: BB8MotionSmoke captures).</summary>
    public void look(SCNVector3 at) => look(at, worldUp, new SCNVector3(0, 0, -1));
    /// <summary>look(at:up:localFront:): rotates the node so localFront points at the world-space target.</summary>
    public void look(SCNVector3 at, SCNVector3 up, SCNVector3 localFront)
    {
        var eye = worldPosition;
        // Measured (Swift probe, macOS 27): SceneKit takes the target in Float and leaves the orientation unchanged
        // when it lies within 1e-5 of the node's world position on every axis ((8e-6, 8e-6, 8e-6) keeps it, 1.05e-5
        // on one axis turns the node). EntranceSmoke's market-vendor camera, clipped onto its own aim point, keeps
        // the previous capture's orientation on macOS.
        double dx = (float)at.x - (float)eye.x, dy = (float)at.y - (float)eye.y, dz = (float)at.z - (float)eye.z;
        if (Math.Abs(dx) < 1e-5 && Math.Abs(dy) < 1e-5 && Math.Abs(dz) < 1e-5) return;
        var d = (at - eye).Normalized();
        if (d.Length < 1e-12) return;
        var f = localFront.Normalized();
        var lu = new SCNVector3(0, 1, 0);
        if (Math.Abs(SCNVector3.Dot(lu, f)) > 0.999) lu = new SCNVector3(0, 0, 1);
        var ul = (lu - f * SCNVector3.Dot(lu, f)).Normalized();
        var rl = SCNVector3.Cross(f, ul);
        var wu = up.Normalized();
        if (Math.Abs(SCNVector3.Dot(wu, d)) > 0.9999) wu = Math.Abs(d.y) < 0.9 ? new SCNVector3(0, 1, 0) : new SCNVector3(0, 0, 1);
        var uw = (wu - d * SCNVector3.Dot(wu, d)).Normalized();
        var rw = SCNVector3.Cross(d, uw);
        // R maps (rl, ul, f) to (rw, uw, d): R = W * L^T.
        var W = new SCNMatrix4(rw.x, rw.y, rw.z, 0, uw.x, uw.y, uw.z, 0, d.x, d.y, d.z, 0, 0, 0, 0, 1);
        var L = new SCNMatrix4(rl.x, ul.x, f.x, 0, rl.y, ul.y, f.y, 0, rl.z, ul.z, f.z, 0, 0, 0, 0, 1);
        var world = QuaternionFromMatrix(SCNMatrix4.Mul(W, L));
        worldOrientation = world;
    }
    public void simdLook(SCNFloat3 at) => look(new SCNVector3(at));
    public void simdLook(SCNFloat3 at, SCNFloat3 up, SCNFloat3 localFront) => look(new SCNVector3(at), new SCNVector3(up), new SCNVector3(localFront));
    public void localTranslate(SCNVector3 by) { SyncRotation(); var d = SCNMatrix4.Rotation(_quat).TransformVector(by); position = _position + d; }
    public void localRotate(SCNVector4 by) { SyncRotation(); orientation = QMul(_quat, Normalize(by)); }

    // =====================================================================
    // Hierarchy
    public SCNNode parent => _parent;
    /// <summary>childNodes (a copy, like Swift's array value).</summary>
    public List<SCNNode> childNodes => new(_children);
    /// <summary>The children without a copy (facade internals that only read them).</summary>
    internal IReadOnlyList<SCNNode> ChildrenView => _children;
    public void addChildNode(SCNNode child)
    {
        if (child == null || child == this) return;
        child.removeFromParentNode();
        child._parent = this;
        _children.Add(child);
        var gp = child.GetParent();
        if (gp != null) gp.RemoveChild(child);
        AddChild(child);
        child.SetSceneOwner(sceneOwner);
        child.Touch();
        child.transformStamp = SceneKitRuntime.StampTransform(); // the subtree's global transforms changed
        SceneKitRuntime.Adopt(child); // a subtree built on another thread is handed over here
    }
    public void insertChildNode(SCNNode child, int at)
    {
        addChildNode(child);
        _children.Remove(child);
        at = Math.Clamp(at, 0, _children.Count);
        _children.Insert(at, child);
        MoveChild(child, at);
    }
    public void replaceChildNode(SCNNode oldChild, SCNNode with)
    {
        int i = _children.IndexOf(oldChild);
        if (i < 0) return;
        oldChild.removeFromParentNode();
        insertChildNode(with, i);
    }
    public void removeFromParentNode()
    {
        if (_parent == null)
        {
            if (sceneOwner != null && sceneOwner.rootNode == this) return;
            GetParent()?.RemoveChild(this);
            return;
        }
        _parent._children.Remove(this);
        _parent.RemoveChild(this);
        _parent = null;
        SetSceneOwner(null);
        transformStamp = SceneKitRuntime.StampTransform();
    }
    /// <summary>
    /// PORT helper (no SceneKit counterpart): Swift deallocates a node that was removed from its parent once the game drops
    /// its last reference (ARC), while a Godot node, its mesh instances and their meshes stay allocated until freed.
    /// Game code calls this where the Swift code removes a node and drops it for good (trail chunks and sand patches on a
    /// reset, the sandbox's rebuilt floor details); before, every race reset left its trail chunks behind as orphan
    /// nodes. Removes the node, releases its geometry and light, and frees it with its subtree at the end of the frame;
    /// neither may be used afterwards.
    /// </summary>
    public void releaseRemoved()
    {
        removeFromParentNode();
        enumerateHierarchy((n, _) => { n.constraints = null; n.geometry = null; n.light = null; });
        if (SceneKitRuntime.OnMainThread) QueueFree(); else Callable.From(QueueFree).CallDeferred();
    }
    internal void SetSceneOwner(SCNScene scene)
    {
        if (sceneOwner == scene) return;
        sceneOwner = scene;
        // Changes made while the node was outside every scene reach Godot now (see Flush).
        if (scene != null && deferredFlags != 0) { int flags = deferredFlags; deferredFlags = 0; SceneKitRuntime.NodeDirty(this, flags); }
        if (_light != null) SceneKitRuntime.SceneStateDirty();
        if (scene != null) _geometry?.SceneChanged();
        // Another scene may need other shaders (SceneKitRuntime.ShadingVariant): rebuild the node's material variants. Off the
        // main thread (a subtree built by a builder thread), or while the scene's features are not determined yet (they are
        // read when its first node is built, after the code building it ran), the rebuild is requested anyway: it is idempotent.
        if (scene != null && _geometry != null && (!SceneKitRuntime.OnMainThread || scene.shadingFeatures < 0 || SceneKitRuntime.ShadingVariant(scene) != builtShading))
            SceneKitRuntime.NodeDirty(this, DirtyGeometry);
        foreach (var c in _children) c.SetSceneOwner(scene);
    }
    public SCNNode childNode(string withName, bool recursively)
    {
        foreach (var c in _children)
        {
            if (c.name == withName) return c;
            if (recursively) { var r = c.childNode(withName, true); if (r != null) return r; }
        }
        return null;
    }
    /// <summary>childNodes(passingTest:).</summary>
    public List<SCNNode> childNodesPassingTest(Func<SCNNode, SCNStop, bool> passingTest)
    {
        var result = new List<SCNNode>();
        var stop = new SCNStop();
        void Walk(SCNNode n)
        {
            foreach (var c in n._children.ToArray())
            {
                if (stop.pointee) return;
                if (passingTest(c, stop)) result.Add(c);
                Walk(c);
            }
        }
        Walk(this);
        return result;
    }
    /// <summary>enumerateChildNodes { node, stop in ... } - depth-first, pre-order, excluding self.</summary>
    public void enumerateChildNodes(Action<SCNNode, SCNStop> block)
    {
        var stop = new SCNStop();
        void Walk(SCNNode n)
        {
            foreach (var c in n._children.ToArray())
            {
                if (stop.pointee) return;
                block(c, stop);
                if (stop.pointee) return;
                Walk(c);
            }
        }
        Walk(this);
    }
    /// <summary>enumerateHierarchy: like enumerateChildNodes but includes self first.</summary>
    public void enumerateHierarchy(Action<SCNNode, SCNStop> block)
    {
        var stop = new SCNStop();
        block(this, stop);
        if (!stop.pointee) enumerateChildNodes((n, s) => { block(n, s); if (s.pointee) stop.pointee = true; });
    }
    /// <summary>presentation: the facade has no separate render tree; returns the node itself.</summary>
    public SCNNode presentation => this;

    /// <summary>clone(): copies the node tree; geometry, light and camera are shared (SceneKit semantics).</summary>
    public SCNNode clone()
    {
        SyncRotation();
        var n = new SCNNode
        {
            name = name, movabilityHint = movabilityHint,
        };
        n._position = _position; n._scale = _scale; n._euler = _euler; n._orientation = _orientation; n._rotation = _rotation;
        n._eulerSeen = _eulerSeen; n._orientationSeen = _orientationSeen; n._rotationSeen = _rotationSeen; n._quat = _quat; n._pivot = _pivot;
        n.geometry = _geometry; n.light = _light; n.camera = _camera;
        n.isHidden = _isHidden; n.opacity = _opacity; n.castsShadow = _castsShadow; n.categoryBitMask = _categoryBitMask;
        n.renderingOrder = _renderingOrder;
        if (_constraints != null) n.constraints = new List<SCNConstraint>(_constraints);
        foreach (var c in _children) n.addChildNode(c.clone());
        n.Touch();
        return n;
    }
    /// <summary>flattenedClone(): PORT: returns a regular clone (not merged into one geometry).</summary>
    public SCNNode flattenedClone() => clone();

    // =====================================================================
    // Attributes
    public SCNGeometry geometry
    {
        get => _geometry;
        set
        {
            if (_geometry == value) return;
            _geometry?.users.Remove(this);
            _geometry = value;
            _geometry?.users.Add(this);
            MarkGeometryDirty();
        }
    }
    public SCNLight light
    {
        get => _light;
        set
        {
            if (_light == value) return;
            _light?.users.Remove(this);
            _light = value;
            _light?.users.Add(this);
            MarkLightDirty();
            SceneKitRuntime.SceneStateDirty();
        }
    }
    public SCNCamera camera { get => _camera; set => _camera = value; }
    public bool isHidden { get => _isHidden; set { if (_isHidden != value) { _isHidden = value; SceneKitRuntime.NodeDirty(this, DirtyVisual); if (_light != null) SceneKitRuntime.SceneStateDirty(); } } }
    /// <summary>opacity. Measured: SceneKit stores it in single precision (CGFloat 0.48 reads back 0.47999998927116394).</summary>
    public double opacity { get => _opacity; set { double v = (float)value; if (_opacity != v) { _opacity = v; MarkSubtree(DirtyVisual); } } }
    public bool castsShadow { get => _castsShadow; set { if (_castsShadow != value) { _castsShadow = value; SceneKitRuntime.NodeDirty(this, DirtyVisual); } } }
    public int categoryBitMask { get => _categoryBitMask; set { if (_categoryBitMask != value) { _categoryBitMask = value; SceneKitRuntime.NodeDirty(this, DirtyVisual); } } }
    public int renderingOrder { get => _renderingOrder; set { if (_renderingOrder != value) { _renderingOrder = value; SceneKitRuntime.NodeDirty(this, DirtyGeometry); } } }
    public List<SCNConstraint> constraints
    {
        get => _constraints;
        set { _constraints = value == null || value.Count == 0 ? null : new List<SCNConstraint>(value); SceneKitRuntime.ConstraintsChanged(this, _constraints != null); }
    }

    /// <summary>boundingBox: own geometry plus children, in this node's coordinates.</summary>
    public (SCNVector3 min, SCNVector3 max) boundingBox
    {
        get
        {
            var acc = new BoundsAccumulator();
            AccumulateBounds(SCNMatrix4.Identity, ref acc, true);
            return acc.Any ? (acc.Min, acc.Max) : (SCNVector3.Zero, SCNVector3.Zero);
        }
        set { if (_geometry != null) _geometry.boundingBox = value; }
    }
    public (SCNVector3 center, double radius) boundingSphere
    {
        get
        {
            var (mn, mx) = boundingBox;
            var c = new SCNVector3((mn.x + mx.x) / 2, (mn.y + mx.y) / 2, (mn.z + mx.z) / 2);
            return (c, (mx - c).Length);
        }
    }
    internal struct BoundsAccumulator
    {
        public SCNVector3 Min, Max; public bool Any;
        public void Add(SCNVector3 p)
        {
            if (!Any) { Min = Max = p; Any = true; return; }
            Min = new SCNVector3(Math.Min(Min.x, p.x), Math.Min(Min.y, p.y), Math.Min(Min.z, p.z));
            Max = new SCNVector3(Math.Max(Max.x, p.x), Math.Max(Max.y, p.y), Math.Max(Max.z, p.z));
        }
    }
    internal void AccumulateBounds(SCNMatrix4 toTarget, ref BoundsAccumulator acc, bool self)
    {
        if (_geometry != null)
        {
            var (mn, mx) = _geometry.boundingBox;
            for (int i = 0; i < 8; i++)
                acc.Add(toTarget.TransformPoint(new SCNVector3((i & 1) != 0 ? mx.x : mn.x, (i & 2) != 0 ? mx.y : mn.y, (i & 4) != 0 ? mx.z : mn.z)));
        }
        foreach (var c in _children) c.AccumulateBounds(SCNMatrix4.Mul(toTarget, c.ModelLocal()), ref acc, false);
    }

    // =====================================================================
    // Godot side
    internal const int DirtyTransform = 1, DirtyGeometry = 2, DirtyVisual = 4, DirtyLight = 8;
    private Transform3D? appliedTransform;
    /// <summary>The model values appliedTransform was computed from (see Flush).</summary>
    private SCNVector3 appliedPosition, appliedScale;
    private SCNVector4 appliedQuat;
    private SCNMatrix4 appliedPivot;
    /// <summary>Set when a constraint moved the Godot transform away from the model value.</summary>
    internal bool constraintsApplied;
    /// <summary>The flags this node is queued with in the runtime's dirty set (main thread; 0 when not queued), so the
    /// transform getters, which mark the node on every access, skip the dictionary once it is queued.</summary>
    internal int dirtyFlags;
    /// <summary>Transform and visual changes flushed while the node was outside every scene (sceneOwner null): nothing shows
    /// such a node, so they reach Godot when it is added to a scene (SetSceneOwner). The race's 1,600 pooled particle slots
    /// are never added to the scene (their particles are drawn as batches) and pushed hundreds of transforms per frame.</summary>
    private int deferredFlags;
    private readonly List<MeshInstance3D> meshes = new();
    /// <summary>The shading variant flags of the scene the meshes were last built for (SceneKitRuntime.ShadingVariant).</summary>
    private ShaderComposer.VariantFlags builtShading;
    /// <summary>Shadow-only instance of a geometry with Godot mesh LODs (SCNGeometry.godotAutomaticLevelsOfDetail): it casts
    /// the node's shadows from the levels of detail while meshes[0] draws the full mesh for the camera.</summary>
    private MeshInstance3D shadowTwin;
    /// <summary>LOD bias that keeps Godot at the full-detail level whatever the distance (screen error x bias).</summary>
    private const float FullDetailLodBias = 1e20f;
    private Light3D godotLight;
    internal Light3D GodotLight => godotLight;

    internal void MarkGeometryDirty() => SceneKitRuntime.NodeDirty(this, DirtyGeometry);
    internal void MarkLightDirty() => SceneKitRuntime.NodeDirty(this, DirtyLight);
    internal void MarkSubtree(int flags)
    {
        SceneKitRuntime.NodeDirty(this, flags);
        foreach (var c in _children) c.MarkSubtree(flags);
    }
    internal double EffectiveOpacity => _parent == null ? _opacity : _parent.EffectiveOpacity * _opacity;

    /// <summary>Pushes SceneKit state to Godot. Called once per frame for dirty nodes.</summary>
    internal void Flush(int flags)
    {
        if (sceneOwner == null)
        {
            // Outside every scene: the transform and the visual state wait (deferredFlags). Meshes, material variants and
            // lights are still built now, in the same order as before: the order Godot resources are created in decides
            // its draw order among surfaces of equal depth (deferring them changed a few z-fighting pixels).
            deferredFlags |= flags & (DirtyTransform | DirtyVisual);
            if ((flags & DirtyGeometry) != 0) { RebuildMeshes(); deferredFlags |= DirtyVisual; }
            if ((flags & DirtyLight) != 0) SyncLight();
            return;
        }
        if ((flags & DirtyTransform) != 0)
        {
            // Getters mark nodes dirty too; only push real changes to Godot. The transform is a function of the model
            // values, so when they equal those of the last push (most nodes the game only read) it is not recomputed.
            SyncRotation();
            if (!appliedTransform.HasValue || constraintsApplied || _position != appliedPosition || _quat != appliedQuat || _scale != appliedScale || !_pivot.Equals(appliedPivot))
            {
                var t = RenderLocal().ToGodot();
                if (!appliedTransform.HasValue || appliedTransform.Value != t || constraintsApplied) { Transform = t; appliedTransform = t; constraintsApplied = false; transformStamp = SceneKitRuntime.StampTransform(); }
                appliedPosition = _position; appliedQuat = _quat; appliedScale = _scale; appliedPivot = _pivot;
            }
        }
        if ((flags & DirtyGeometry) != 0) RebuildMeshes();
        if ((flags & (DirtyGeometry | DirtyVisual)) != 0) ApplyVisuals();
        if ((flags & DirtyLight) != 0) SyncLight();
        if ((flags & (DirtyVisual | DirtyTransform)) != 0)
        {
            // Only push changes: setting Node3D.Visible costs an engine call even when the value is unchanged.
            bool visible = !_isHidden;
            if (appliedVisible != visible) { Visible = visible; appliedVisible = visible; SceneKitRuntime.TransformsChanged(); }
        }
    }
    private bool? appliedVisible;
    /// <summary>The node or one of its ancestors is hidden (SceneKit's isHidden, without engine calls).</summary>
    internal bool HiddenInHierarchy
    {
        get { for (var n = this; n != null; n = n._parent) if (n._isHidden) return true; return false; }
    }

    private void RebuildMeshes()
    {
        var levels = new List<(SCNGeometry g, double from, double to)>();
        if (_geometry != null)
        {
            var lods = (_geometry.levelsOfDetail ?? Array.Empty<SCNLevelOfDetail>()).Where(l => l?.geometry != null).ToList();
            var distances = lods.Select(LodDistance).ToList();
            levels.Add((_geometry, 0, distances.Count > 0 ? distances[0] : 0));
            for (int i = 0; i < lods.Count; i++) levels.Add((lods[i].geometry, distances[i], i + 1 < lods.Count ? distances[i + 1] : 0));
        }
        while (meshes.Count > levels.Count) { var mi = meshes[^1]; meshes.RemoveAt(meshes.Count - 1); RemoveChild(mi); mi.QueueFree(); }
        while (meshes.Count < levels.Count) { var mi = new MeshInstance3D(); AddChild(mi, false, InternalMode.Front); meshes.Add(mi); appliedVisuals = null; }
        sortCenters.Clear(); sortWorldCenters.Clear(); while (sortOffsets.Count > meshes.Count) sortOffsets.RemoveAt(sortOffsets.Count - 1);
        while (sortOffsets.Count < meshes.Count) sortOffsets.Add(0); // Godot's default sorting offset of a new instance
        // Measured (Swift probe): SceneKit measures a worldSpaceDistance LOD from the node's origin, Godot's visibility
        // range from the instance's AABB centre. Centre the instances' culling box on the node origin (a symmetric box
        // enclosing every level), so the switch happens where SceneKit's does (DesertWorld's dune tiles have their
        // origin at a corner, 45 m from their centre). Screen-space LODs keep the geometric centre.
        Aabb? lodBounds = null;
        if (levels.Count > 1 && _geometry.levelsOfDetail.All(l => l?.geometry == null || l.screenSpaceRadius <= 0))
        {
            var extent = Vector3.Zero;
            foreach (var (g, _, _) in levels)
            {
                var box = g.GodotMesh.GetAabb();
                extent = extent.Max(box.Position.Abs()).Max(box.End.Abs());
            }
            lodBounds = new Aabb(-extent, extent * 2);
        }
        bool transparent = false, ssaoSky = false;
        var levelMeshes = new ArrayMesh[levels.Count];
        // The lighting features the scene's shaders need (SceneKitRuntime.ShadingVariant): full shaders outside every scene.
        builtShading = SceneKitRuntime.ShadingVariant(sceneOwner);
        for (int i = 0; i < levels.Count; i++)
        {
            var (g, from, to) = levels[i];
            var mi = meshes[i];
            var mesh = g.GodotMesh;
            levelMeshes[i] = mesh;
            if (mi.Mesh != mesh) mi.Mesh = mesh;
            mi.CustomAabb = lodBounds ?? new Aabb();
            mi.VisibilityRangeBegin = (float)from;
            mi.VisibilityRangeEnd = (float)to;
            var mats = g.MaterialList;
            var geometryFlags = ShaderComposer.VariantFlags.None;
            if (g.HasColors) geometryFlags |= ShaderComposer.VariantFlags.VertexColors;
            if (!g.HasNormals) geometryFlags |= ShaderComposer.VariantFlags.NoNormals;
            int surfaces = mesh.GetSurfaceCount();
            for (int s = 0; s < surfaces; s++)
            {
                var material = mats.Count == 0 ? SceneKitRuntime.DefaultMaterial : mats[g.surfaceElements[s] % mats.Count];
                var flags = geometryFlags | builtShading;
                if (!material.readsFromDepthBuffer && _renderingOrder < 0) flags |= ShaderComposer.VariantFlags.Background;
                mi.SetSurfaceOverrideMaterial(s, material.gpu.Variant(g, _renderingOrder, flags));
                transparent |= material.gpu.IsTransparentVariant(g, _renderingOrder, flags);
                ssaoSky |= material.gpu.IsSsaoSkyVariant(g, _renderingOrder, flags);
            }
            // Godot-only: the geometry's per-instance shader arguments (SCNGeometry.InstanceArguments).
            if (g.InstanceArguments.Count > 0) g.ApplyInstanceArguments(mi);
        }
        if (transparent)
        {
            // The culling box centre of each instance for the transparent sort (SortTransparent): the custom box when one
            // is set, else the mesh's own box (a Godot mesh does not change after it is built).
            for (int i = 0; i < meshes.Count; i++)
            {
                var box = lodBounds is Aabb b && b.Size != Vector3.Zero ? b : levelMeshes[i].GetAabb();
                sortCenters.Add(box.Position + box.Size * 0.5f);
            }
        }
        SceneKitRuntime.RegisterTransparent(this, transparent && meshes.Count > 0); SceneKitRuntime.TransformsChanged();
        SceneKitRuntime.RegisterSsaoSky(this, ssaoSky && meshes.Count > 0);
        // Godot-only (SCNGeometry.godotAutomaticLevelsOfDetail): the camera keeps the full mesh, a shadow-only twin with the
        // same mesh and materials casts from the levels of detail. Registered so a run-time change of MeshLodForCamera
        // (SceneKitRuntime.ShadowConfigurationChanged) rebuilds the node.
        bool meshLods = levels.Count == 1 && _geometry.godotAutomaticLevelsOfDetail && _geometry.MeshHasLods;
        SceneKitRuntime.RegisterMeshLod(this, meshLods);
        bool twin = meshLods && !SceneKitCalibration.MeshLodForCamera;
        if (twin)
        {
            if (shadowTwin == null) { shadowTwin = new MeshInstance3D(); AddChild(shadowTwin, false, InternalMode.Front); appliedVisuals = null; }
            var primary = meshes[0];
            if (shadowTwin.Mesh != primary.Mesh) shadowTwin.Mesh = primary.Mesh;
            for (int s = 0; s < primary.Mesh.GetSurfaceCount(); s++) shadowTwin.SetSurfaceOverrideMaterial(s, primary.GetSurfaceOverrideMaterial(s));
            if (_geometry.InstanceArguments.Count > 0) _geometry.ApplyInstanceArguments(shadowTwin);
            primary.LodBias = FullDetailLodBias;
        }
        else if (shadowTwin != null)
        {
            RemoveChild(shadowTwin); shadowTwin.QueueFree(); shadowTwin = null; appliedVisuals = null;
            foreach (var mi in meshes) mi.LodBias = 1;
        }
    }
    /// <summary>The Godot instances of this node's geometry (one per level of detail).</summary>
    internal IReadOnlyList<MeshInstance3D> MeshInstances => meshes;
    /// <summary>Per instance: the local culling-box centre (filled for transparent nodes) and the sorting offset last set.</summary>
    private readonly List<Vector3> sortCenters = new();
    private readonly List<float> sortOffsets = new();
    /// <summary>The centres in world space and the transform stamp they were computed at.</summary>
    private readonly List<Vector3> sortWorldCenters = new();
    private int sortStamp = -1;
    /// <summary>The stamp of this node's last Godot transform push or reparenting (SceneKitRuntime.StampTransform).</summary>
    internal int transformStamp;
    private bool ChainUnchangedSince(int stamp)
    {
        if (stamp < 0) return false;
        for (var n = this; n != null; n = n._parent) if (n.transformStamp > stamp) return false;
        return true;
    }
    /// <summary>
    /// SceneKit's transparent draw order for this node's instances (see SceneKitRuntime.SortTransparent): Godot sorts by
    /// |centre - eye| - SortingOffset, so the offset is distance - view depth of the box centre. The instances have an
    /// identity local transform, so their global transform is the node's (one engine call per node); the offset last set
    /// is remembered instead of read back.
    /// </summary>
    internal void SortTransparent(Vector3 eye, Vector3 forward, bool orthographic)
    {
        if (sortCenters.Count != meshes.Count || meshes.Count == 0) return;
        // World-space centres, recomputed only when this node or an ancestor got another Godot transform or parent since
        // (most transparent nodes, the trail chunks and ground overlays, never move; 500 chunks after ten minutes).
        if (sortWorldCenters.Count != meshes.Count || !ChainUnchangedSince(sortStamp))
        {
            sortStamp = SceneKitRuntime.TransformStamp;
            var global = GlobalTransform;
            sortWorldCenters.Clear();
            foreach (var center in sortCenters) sortWorldCenters.Add(global * center);
        }
        for (int i = 0; i < meshes.Count; i++)
        {
            var d = sortWorldCenters[i] - eye;
            // Godot: perspective depth = |centre - eye| - offset; orthographic: distance of the nearest box corner to
            // the near plane - offset (left as Godot has it).
            float offset = orthographic ? 0 : d.Length() - d.Dot(forward);
            if (Math.Abs(sortOffsets[i] - offset) > 0.01f) { meshes[i].SortingOffset = offset; sortOffsets[i] = offset; }
        }
    }
    private static double LodDistance(SCNLevelOfDetail l)
    {
        // Godot-only graphics detail: SceneKitCalibration.LodDistanceScale (1 at Max) moves every switch closer.
        double scale = SceneKitCalibration.LodDistanceScale;
        if (l.screenSpaceRadius <= 0) return scale == 1 ? l.worldSpaceDistance : l.worldSpaceDistance * scale;
        // PORT: screen-space LODs are converted for a 1080-pixel-high, 48° viewport.
        var r = l.geometry.boundingSphere.radius;
        var d = r * 1080 / (2 * Math.Tan(48 * Math.PI / 360) * l.screenSpaceRadius);
        return scale == 1 ? d : d * scale;
    }

    /// <summary>The visual state last pushed to every instance in <see cref="meshes"/> (null after an instance was added).</summary>
    private VisualState? appliedVisuals;
    private readonly record struct VisualState(GeometryInstance3D.ShadowCastingSetting Cast, uint Layers, float Transparency, bool Visible);
    private void ApplyVisuals()
    {
        double opacity = EffectiveOpacity;
        bool visibleToCamera = (_categoryBitMask & SceneKitRuntime.CameraMask) != 0;
        bool lightsSeeIt = (_categoryBitMask & SceneKitRuntime.ShadowLightMask) != 0;
        var cast = !_castsShadow || !lightsSeeIt ? GeometryInstance3D.ShadowCastingSetting.Off
            : visibleToCamera ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;
        uint layers = SceneKitRuntime.GodotLayers(_categoryBitMask);
        var state = new VisualState(cast, layers == 0 ? 1u : layers, (float)Math.Clamp(1 - opacity, 0, 1),
            visibleToCamera || cast == GeometryInstance3D.ShadowCastingSetting.ShadowsOnly);
        // Only push changes: each setter is an engine call (a shadow-casting change also updates the instance's light
        // pairing), and geometry rebuilds re-apply the same state to the same instances every frame.
        if (appliedVisuals is VisualState applied)
        {
            if (applied == state) return;
            foreach (var mi in meshes)
            {
                if (applied.Cast != state.Cast) mi.CastShadow = state.Cast;
                if (applied.Layers != state.Layers) mi.Layers = state.Layers;
                if (applied.Transparency != state.Transparency) mi.Transparency = state.Transparency;
                if (applied.Visible != state.Visible) mi.Visible = state.Visible;
            }
        }
        else
        {
            foreach (var mi in meshes)
            {
                mi.CastShadow = state.Cast;
                mi.Layers = state.Layers;
                mi.Transparency = state.Transparency;
                mi.Visible = state.Visible;
            }
        }
        if (shadowTwin != null)
        {
            // The full mesh draws for the camera, the twin casts the shadow (a geometry only the lights see keeps casting
            // from the full mesh). Re-applied whenever the state changed (the loop above may have reset meshes[0]).
            bool split = state.Cast == GeometryInstance3D.ShadowCastingSetting.On;
            if (split) meshes[0].CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            shadowTwin.CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;
            shadowTwin.Layers = state.Layers;
            shadowTwin.Transparency = state.Transparency;
            shadowTwin.Visible = split;
        }
        appliedVisuals = state;
    }

    /// <summary>The Godot light's specular amount: 2 + the index of the fixed shadow box this light owns, else 1 (the
    /// composer's light() recognises a box's light by it; SceneKitRuntime.FitShadows).</summary>
    private float shadowBoxId = 1;
    internal void SetShadowBoxId(float id)
    {
        shadowBoxId = id;
        if (godotLight != null && godotLight.LightSpecular != id) godotLight.LightSpecular = id;
    }
    private void SyncLight()
    {
        var updated = _light?.Sync(godotLight, SceneKitRuntime.AmbientIntensityFor(sceneOwner));
        if (updated != null && updated.LightSpecular != shadowBoxId) updated.LightSpecular = shadowBoxId;
        SceneKitRuntime.RegisterShadowLight(this, updated is DirectionalLight3D && _light.castsShadow);
        if (updated != godotLight)
        {
            if (godotLight != null) { RemoveChild(godotLight); godotLight.QueueFree(); }
            godotLight = updated;
            SceneKitRuntime.RegisterShadowLight(this, godotLight is DirectionalLight3D && _light.castsShadow);
            if (godotLight != null) AddChild(godotLight, false, InternalMode.Front);
        }
    }

    // =====================================================================
    // Hit testing (SCNHitTestResult)
    /// <summary>hitTestWithSegment(from:to:options:) on this node's geometry and children (local coordinates).</summary>
    public List<SCNHitTestResult> hitTestWithSegment(SCNVector3 from, SCNVector3 to, Dictionary<string, object> options = null)
    {
        var world = ModelWorld();
        return SCNHitTest.Run(this, world.TransformPoint(from), world.TransformPoint(to), options, renderTransforms: false);
    }

    // =====================================================================
    // Rotation maths (SceneKit conventions)
    internal static SCNVector4 QMul(SCNVector4 a, SCNVector4 b) => new(
        a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
        a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
        a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
        a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);
    internal static SCNVector4 QConj(SCNVector4 q) => new(-q.x, -q.y, -q.z, q.w);
    internal static SCNVector4 Normalize(SCNVector4 q)
    {
        double l = Math.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        return l > 0 ? new SCNVector4(q.x / l, q.y / l, q.z / l, q.w / l) : new SCNVector4(0, 0, 0, 1);
    }
    /// <summary>R = Rz(z) * Ry(y) * Rx(x) (measured).</summary>
    internal static SCNVector4 QuaternionFromEuler(SCNVector3 e)
    {
        var qx = new SCNVector4(Math.Sin(e.x / 2), 0, 0, Math.Cos(e.x / 2));
        var qy = new SCNVector4(0, Math.Sin(e.y / 2), 0, Math.Cos(e.y / 2));
        var qz = new SCNVector4(0, 0, Math.Sin(e.z / 2), Math.Cos(e.z / 2));
        return QMul(qz, QMul(qy, qx));
    }
    internal static SCNVector3 EulerFromQuaternion(SCNVector4 q)
    {
        var m = SCNMatrix4.Rotation(q);
        double r31 = m[0, 2], r32 = m[1, 2], r33 = m[2, 2], r21 = m[0, 1], r11 = m[0, 0];
        double y = Math.Asin(Math.Clamp(-r31, -1, 1));
        if (Math.Abs(r31) < 0.9999999)
            return new SCNVector3(Math.Atan2(r32, r33), y, Math.Atan2(r21, r11));
        // Gimbal lock: put everything into x.
        return new SCNVector3(Math.Atan2(-m[2, 1], m[1, 1]), y, 0);
    }
    internal static SCNVector4 QuaternionFromAxisAngle(SCNVector4 r)
    {
        double l = Math.Sqrt(r.x * r.x + r.y * r.y + r.z * r.z);
        if (l < 1e-12) return new SCNVector4(0, 0, 0, 1);
        double s = Math.Sin(r.w / 2) / l;
        return new SCNVector4(r.x * s, r.y * s, r.z * s, Math.Cos(r.w / 2));
    }
    internal static SCNVector4 AxisAngleFromQuaternion(SCNVector4 q)
    {
        q = Normalize(q);
        double angle = 2 * Math.Acos(Math.Clamp(q.w, -1, 1));
        double s = Math.Sqrt(Math.Max(0, 1 - q.w * q.w));
        if (s < 1e-9) return new SCNVector4(0, 0, 0, 0);
        return new SCNVector4(q.x / s, q.y / s, q.z / s, angle);
    }
    internal static SCNVector4 QuaternionFromMatrix(SCNMatrix4 m)
    {
        var c0 = m.Column(0).Normalized(); var c1 = m.Column(1).Normalized(); var c2 = m.Column(2).Normalized();
        double m00 = c0.x, m10 = c0.y, m20 = c0.z, m01 = c1.x, m11 = c1.y, m21 = c1.z, m02 = c2.x, m12 = c2.y, m22 = c2.z;
        double tr = m00 + m11 + m22;
        double x, y, z, w;
        if (tr > 0) { double s = Math.Sqrt(tr + 1) * 2; w = 0.25 * s; x = (m21 - m12) / s; y = (m02 - m20) / s; z = (m10 - m01) / s; }
        else if (m00 > m11 && m00 > m22) { double s = Math.Sqrt(1 + m00 - m11 - m22) * 2; w = (m21 - m12) / s; x = 0.25 * s; y = (m01 + m10) / s; z = (m02 + m20) / s; }
        else if (m11 > m22) { double s = Math.Sqrt(1 + m11 - m00 - m22) * 2; w = (m02 - m20) / s; x = (m01 + m10) / s; y = 0.25 * s; z = (m12 + m21) / s; }
        else { double s = Math.Sqrt(1 + m22 - m00 - m11) * 2; w = (m10 - m01) / s; x = (m02 + m20) / s; y = (m12 + m21) / s; z = 0.25 * s; }
        return Normalize(new SCNVector4(x, y, z, w));
    }
    internal static (SCNVector3 position, SCNVector4 orientation, SCNVector3 scale) Decompose(SCNMatrix4 m)
    {
        var c0 = m.Column(0); var c1 = m.Column(1); var c2 = m.Column(2);
        double sx = c0.Length, sy = c1.Length, sz = c2.Length;
        if (SCNVector3.Dot(SCNVector3.Cross(c0, c1), c2) < 0) sx = -sx;
        return (m.Column(3), QuaternionFromMatrix(new SCNMatrix4(
            c0.x / sx, c0.y / sx, c0.z / sx, 0, c1.x / sy, c1.y / sy, c1.z / sy, 0, c2.x / sz, c2.y / sz, c2.z / sz, 0, 0, 0, 0, 1)), new SCNVector3(sx, sy, sz));
    }
}

/// <summary>SCNHitTestOption keys.</summary>
public static class SCNHitTestOption
{
    public const string backFaceCulling = "backFaceCulling", firstFoundOnly = "firstFoundOnly", ignoreHiddenNodes = "ignoreHiddenNodes", searchMode = "searchMode", categoryBitMask = "categoryBitMask";
}

/// <summary>SCNHitTestResult.</summary>
public sealed class SCNHitTestResult
{
    public readonly SCNNode node;
    public readonly SCNVector3 localCoordinates, worldCoordinates, localNormal, worldNormal;
    public readonly int faceIndex, geometryIndex;
    internal readonly double distance;
    internal SCNHitTestResult(SCNNode node, SCNVector3 local, SCNVector3 world, SCNVector3 localNormal, SCNVector3 worldNormal, int face, int element, double distance)
    {
        this.node = node; localCoordinates = local; worldCoordinates = world; this.localNormal = localNormal; this.worldNormal = worldNormal;
        faceIndex = face; geometryIndex = element; this.distance = distance;
    }
    public SCNFloat3 simdWorldCoordinates => worldCoordinates.simd;
    public SCNFloat3 simdLocalCoordinates => localCoordinates.simd;
}
