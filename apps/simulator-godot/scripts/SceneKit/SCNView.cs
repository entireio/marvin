using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Marvin.SceneKit;

public enum SCNAntialiasingMode { none = 0, multisampling2X = 1, multisampling4X = 2, multisampling8X = 3, multisampling16X = 4 }
public enum SCNRenderingAPI { metal = 0, openGLLegacy = 1, openGLCore32 = 2, openGLCore41 = 3 }
[Flags]
public enum SCNDebugOptions { none = 0, showPhysicsShapes = 1, showBoundingBoxes = 2, showLightInfluences = 4, showLightExtents = 8, showPhysicsFields = 16, showWireframe = 32, renderAsWireframe = 64, showSkeletons = 128, showCreases = 256, showConstraints = 512, showCameras = 1024 }

/// <summary>SCNSceneRenderer protocol (SCNView, SCNRenderer).</summary>
public interface SCNSceneRenderer
{
    SCNScene scene { get; set; }
    SCNNode pointOfView { get; set; }
    double sceneTime { get; set; }
    bool isNode(SCNNode node, SCNNode insideFrustumOf);
    List<SCNNode> nodesInsideFrustum(SCNNode of);
    SCNVector3 projectPoint(SCNVector3 point);
    SCNVector3 unprojectPoint(SCNVector3 point);
}

/// <summary>
/// SCNSceneRendererDelegate. Swift overloads `renderer(_:updateAtTime:)` etc. share one
/// name; in C# each callback has its own name (all optional).
/// </summary>
public interface SCNSceneRendererDelegate
{
    void rendererUpdateAtTime(SCNSceneRenderer renderer, double time) { }
    void rendererDidApplyAnimationsAtTime(SCNSceneRenderer renderer, double time) { }
    void rendererDidSimulatePhysicsAtTime(SCNSceneRenderer renderer, double time) { }
    void rendererDidApplyConstraintsAtTime(SCNSceneRenderer renderer, double time) { }
    void rendererWillRenderScene(SCNSceneRenderer renderer, SCNScene scene, double time) { }
    void rendererDidRenderScene(SCNSceneRenderer renderer, SCNScene scene, double time) { }
}

/// <summary>Viewport + camera + environment that renders an SCNScene from a point of view.</summary>
internal sealed class ViewRig
{
    internal readonly SubViewport viewport;
    internal readonly Camera3D camera = new() { Current = true };
    private readonly Godot.Environment env = new();
    private readonly CameraAttributesPractical attributes = new();
    internal SCNScene scene;
    internal SCNNode pointOfView;
    internal NSColor backgroundColor = NSColor.black;
    private SCNNode defaultCameraNode;

    internal ViewRig(SubViewport viewport)
    {
        this.viewport = viewport;
        viewport.OwnWorld3D = false;
        viewport.HandleInputLocally = false;
        viewport.PositionalShadowAtlasSize = 4096;
        viewport.AddChild(camera);
        camera.Environment = env;
        camera.Attributes = attributes;
        env.FogEnabled = false;
        env.AmbientLightSource = Godot.Environment.AmbientSource.Disabled;
        env.ReflectedLightSource = Godot.Environment.ReflectionSource.Disabled;
    }
    internal void SetScene(SCNScene s)
    {
        s?.EnsureAttached();
        scene = s;
        viewport.World3D = s?.World;
    }
    internal SCNNode EffectivePointOfView
    {
        get
        {
            if (pointOfView?.camera != null) return pointOfView;
            SCNNode found = null;
            scene?.rootNode.enumerateHierarchy((n, stop) => { if (n.camera != null) { found = n; stop.pointee = true; } });
            if (found != null) return found;
            defaultCameraNode ??= new SCNNode { camera = new SCNCamera() };
            defaultCameraNode.position = new SCNVector3(0, 0, 15);
            return defaultCameraNode;
        }
    }
    internal double Aspect => viewport.Size.Y > 0 ? (double)viewport.Size.X / viewport.Size.Y : 1;

    internal void Sync()
    {
        if (scene == null) return;
        var pov = EffectivePointOfView;
        var world = pov.RenderWorld().ToGodot();
        world.Basis = world.Basis.Orthonormalized();
        camera.Transform = world;
        pov.camera.ApplyLens(camera);
        pov.camera.ApplyEnvironment(env, attributes, viewport.Size.Y);
        SceneKitRuntime.FitShadows(scene, camera, viewport.Size);
        // Background.
        var bg = scene.background.contents;
        if (bg is NSImage or string)
        {
            env.BackgroundMode = Godot.Environment.BGMode.Sky;
            env.Sky = scene.SkyFor(bg, scene.background.intensity);
        }
        else
        {
            env.BackgroundMode = Godot.Environment.BGMode.Color;
            env.BackgroundColor = (bg as NSColor ?? backgroundColor ?? NSColor.black).GodotSrgb;
        }
        // Image-based lighting (diffuse and specular) is evaluated by the composer from the SH of
        // lightingEnvironment (IRRADIANCE/RADIANCE), so Godot's own ambient and reflections stay off.
        env.ReflectedLightSource = Godot.Environment.ReflectionSource.Disabled;
        env.AmbientLightSource = Godot.Environment.AmbientSource.Disabled;
        SceneKitRuntime.ActiveScene = scene;
        SceneKitRuntime.ActiveCamera = pov;
        SceneKitRuntime.SetActiveLdr(!pov.camera.wantsHDR);
        int mask = pov.camera.categoryBitMask;
        if (SceneKitRuntime.CameraMask != mask) { SceneKitRuntime.CameraMask = mask; SceneKitRuntime.MasksChanged(); }
    }

    internal SCNMatrix4 ViewProjection(SCNNode pov, double aspect) =>
        SCNMatrix4.Mul(pov.camera.ProjectionFor(aspect), SCNMatrix4.Inverse(pov.RenderWorld()));

    /// <summary>
    /// isNode(_:insideFrustumOf:). Measured: SceneKit tests the node's WORLD-SPACE axis-aligned bounding box
    /// (the box enclosing the transformed local bounding box), not the oriented box: a unit box rotated by
    /// 0.6 rad about y at depth 5 (fov 48, aspect 1280/820) leaves the frustum at x = 4.64-4.68 (world AABB:
    /// 4.653; oriented box: 4.261); unrotated it leaves at 4.30-4.35 (both: 4.32).
    /// </summary>
    internal bool IsNodeInFrustum(SCNNode node, SCNNode pov, double aspect)
    {
        if (pov?.camera == null || node == null) return false;
        var vp = ViewProjection(pov, aspect);
        var (mn, mx) = node.boundingBox;
        var world = node.RenderWorld();
        double lx = double.MaxValue, ly = double.MaxValue, lz = double.MaxValue, hx = double.MinValue, hy = double.MinValue, hz = double.MinValue;
        for (int i = 0; i < 8; i++)
        {
            double bx = (i & 1) != 0 ? mx.x : mn.x, by = (i & 2) != 0 ? mx.y : mn.y, bz = (i & 4) != 0 ? mx.z : mn.z;
            double wx = world[0, 0] * bx + world[1, 0] * by + world[2, 0] * bz + world[3, 0];
            double wy = world[0, 1] * bx + world[1, 1] * by + world[2, 1] * bz + world[3, 1];
            double wz = world[0, 2] * bx + world[1, 2] * by + world[2, 2] * bz + world[3, 2];
            lx = Math.Min(lx, wx); ly = Math.Min(ly, wy); lz = Math.Min(lz, wz); hx = Math.Max(hx, wx); hy = Math.Max(hy, wy); hz = Math.Max(hz, wz);
        }
        var corners = new (double x, double y, double z, double w)[8];
        for (int i = 0; i < 8; i++)
        {
            var p = new SCNVector3((i & 1) != 0 ? hx : lx, (i & 2) != 0 ? hy : ly, (i & 4) != 0 ? hz : lz);
            corners[i] = (vp[0, 0] * p.x + vp[1, 0] * p.y + vp[2, 0] * p.z + vp[3, 0],
                          vp[0, 1] * p.x + vp[1, 1] * p.y + vp[2, 1] * p.z + vp[3, 1],
                          vp[0, 2] * p.x + vp[1, 2] * p.y + vp[2, 2] * p.z + vp[3, 2],
                          vp[0, 3] * p.x + vp[1, 3] * p.y + vp[2, 3] * p.z + vp[3, 3]);
        }
        bool Out(Func<(double x, double y, double z, double w), bool> outside) => corners.All(outside);
        return !(Out(c => c.x < -c.w) || Out(c => c.x > c.w) || Out(c => c.y < -c.w) || Out(c => c.y > c.w) || Out(c => c.z < -c.w) || Out(c => c.z > c.w));
    }
    /// <summary>projectPoint: x,y in view points with the origin at the bottom left (AppKit), z depth 0 (near) .. 1 (far).</summary>
    internal SCNVector3 Project(SCNVector3 p, double width, double height)
    {
        var pov = EffectivePointOfView;
        var vp = ViewProjection(pov, width / Math.Max(height, 1));
        double x = vp[0, 0] * p.x + vp[1, 0] * p.y + vp[2, 0] * p.z + vp[3, 0];
        double y = vp[0, 1] * p.x + vp[1, 1] * p.y + vp[2, 1] * p.z + vp[3, 1];
        double z = vp[0, 2] * p.x + vp[1, 2] * p.y + vp[2, 2] * p.z + vp[3, 2];
        double w = vp[0, 3] * p.x + vp[1, 3] * p.y + vp[2, 3] * p.z + vp[3, 3];
        if (Math.Abs(w) < 1e-12) w = 1e-12;
        return new SCNVector3((x / w * 0.5 + 0.5) * width, (y / w * 0.5 + 0.5) * height, z / w * 0.5 + 0.5);
    }
    internal SCNVector3 Unproject(SCNVector3 p, double width, double height)
    {
        var pov = EffectivePointOfView;
        var inv = SCNMatrix4.Inverse(ViewProjection(pov, width / Math.Max(height, 1)));
        double nx = p.x / width * 2 - 1, ny = p.y / height * 2 - 1, nz = p.z * 2 - 1;
        double x = inv[0, 0] * nx + inv[1, 0] * ny + inv[2, 0] * nz + inv[3, 0];
        double y = inv[0, 1] * nx + inv[1, 1] * ny + inv[2, 1] * nz + inv[3, 1];
        double z = inv[0, 2] * nx + inv[1, 2] * ny + inv[2, 2] * nz + inv[3, 2];
        double w = inv[0, 3] * nx + inv[1, 3] * ny + inv[2, 3] * nz + inv[3, 3];
        return new SCNVector3(x / w, y / w, z / w);
    }
    internal static Viewport.Msaa Msaa(SCNAntialiasingMode mode) => mode switch
    {
        SCNAntialiasingMode.multisampling2X => Viewport.Msaa.Msaa2X,
        SCNAntialiasingMode.multisampling4X => Viewport.Msaa.Msaa4X,
        SCNAntialiasingMode.multisampling8X or SCNAntialiasingMode.multisampling16X => Viewport.Msaa.Msaa8X,
        _ => Viewport.Msaa.Disabled,
    };
}

/// <summary>
/// SCNView: a Control (SubViewportContainer) that renders an SCNScene. Position and
/// size it like any Godot control; `frame`/`bounds` use Godot pixel coordinates.
/// </summary>
public partial class SCNView : SubViewportContainer, SCNSceneRenderer
{
    internal readonly ViewRig rig;
    private SCNAntialiasingMode _antialiasingMode = SCNAntialiasingMode.multisampling4X;
    private bool _rendersContinuously;
    public int preferredFramesPerSecond = 60;
    public bool isPlaying, loops, autoenablesDefaultLighting, allowsCameraControl, showsStatistics, isJitteringEnabled, isTemporalAntialiasingEnabled;
    public SCNDebugOptions debugOptions;
    public SCNSceneRendererDelegate @delegate;
    public double sceneTime { get; set; }
    public MTLDevice device => MTLDevice.Shared;
    public bool usesReverseZ = true;
    /// <summary>renderingAPI: always .metal for compatibility with code that reports it.</summary>
    public SCNRenderingAPI renderingAPI => SCNRenderingAPI.metal;

    public SCNView()
    {
        SceneKitRuntime.EnsureStarted();
        Stretch = true;
        var vp = new SubViewport { Name = "SCNViewport", RenderTargetUpdateMode = SubViewport.UpdateMode.Always, Msaa3D = Viewport.Msaa.Msaa4X };
        AddChild(vp);
        rig = new ViewRig(vp);
        SceneKitRuntime.views.Add(new WeakReference<SCNView>(this));
    }
    public SCNView(CGRect frame) : this() { this.frame = frame; }

    public SCNScene scene { get => rig.scene; set => rig.SetScene(value); }
    public SCNNode pointOfView { get => rig.pointOfView; set => rig.pointOfView = value; }
    public SCNAntialiasingMode antialiasingMode { get => _antialiasingMode; set { _antialiasingMode = value; rig.viewport.Msaa3D = ViewRig.Msaa(value); } }
    /// <summary>rendersContinuously: the facade always renders every frame while visible; false is stored only.</summary>
    public bool rendersContinuously { get => _rendersContinuously; set => _rendersContinuously = value; }
    public NSColor backgroundColor { get => rig.backgroundColor; set => rig.backgroundColor = value; }
    /// <summary>frame in Godot pixels (origin top-left).</summary>
    public CGRect frame
    {
        get => new(Position.X, Position.Y, Size.X, Size.Y);
        set { Position = new Vector2((float)value.origin.x, (float)value.origin.y); Size = new Vector2((float)value.size.width, (float)value.size.height); }
    }
    public CGRect bounds => new(0, 0, Size.X, Size.Y);
    /// <summary>The renderer's own Godot camera (for Godot-side effects only).</summary>
    public Camera3D GodotCamera => rig.camera;
    public SubViewport GodotViewport => rig.viewport;

    internal void SyncCamera() { if (rig.scene != null && IsVisibleInTree()) rig.Sync(); }
    internal void CallDelegateUpdate() { if (@delegate != null) { @delegate.rendererUpdateAtTime(this, SceneKitRuntime.SceneTime); @delegate.rendererDidApplyAnimationsAtTime(this, SceneKitRuntime.SceneTime); @delegate.rendererDidSimulatePhysicsAtTime(this, SceneKitRuntime.SceneTime); } }
    internal void CallDelegateWillRender() { if (@delegate != null && scene != null) { @delegate.rendererDidApplyConstraintsAtTime(this, SceneKitRuntime.SceneTime); @delegate.rendererWillRenderScene(this, scene, SceneKitRuntime.SceneTime); } }
    internal void CallDelegateDidRender() { if (@delegate != null && scene != null) @delegate.rendererDidRenderScene(this, scene, SceneKitRuntime.SceneTime); }

    /// <summary>snapshot(): renders now and returns the view's image.</summary>
    public NSImage snapshot()
    {
        // The container resizes its SubViewport on a deferred notification; apply the size now.
        var size = new Vector2I(Math.Max(1, (int)Size.X / Math.Max(1, StretchShrink)), Math.Max(1, (int)Size.Y / Math.Max(1, StretchShrink)));
        if (rig.viewport.Size != size) rig.viewport.Size = size;
        rig.Sync();
        SceneKitRuntime.RenderNow(rig.viewport);
        var img = rig.viewport.GetTexture().GetImage();
        return NSImage.data(img.SavePngToBuffer());
    }
    /// <summary>prepare(_:completionHandler:): shaders compile on first use in Godot; completes immediately.</summary>
    public void prepare(object[] objects, Action<bool> completionHandler) { SceneKitRuntime.Flush(); completionHandler?.Invoke(true); }
    public bool prepare(object @object, Func<bool> shouldAbortBlock) { SceneKitRuntime.Flush(); return true; }
    public bool isNode(SCNNode node, SCNNode insideFrustumOf) => rig.IsNodeInFrustum(node, insideFrustumOf, rig.Aspect);
    public List<SCNNode> nodesInsideFrustum(SCNNode of)
    {
        var list = new List<SCNNode>();
        scene?.rootNode.enumerateChildNodes((n, _) => { if (n.geometry != null && isNode(n, of)) list.Add(n); });
        return list;
    }
    public SCNVector3 projectPoint(SCNVector3 point) => rig.Project(point, Size.X, Size.Y);
    public SCNVector3 unprojectPoint(SCNVector3 point) => rig.Unproject(point, Size.X, Size.Y);
    public SCNFloat3 projectPoint(SCNFloat3 point) => projectPoint(new SCNVector3(point)).simd;
    /// <summary>hitTest(_:options:): point in view coordinates (origin bottom-left), nearest hits first.</summary>
    public List<SCNHitTestResult> hitTest(CGPoint point, Dictionary<string, object> options = null)
    {
        if (scene == null) return new List<SCNHitTestResult>();
        var a = unprojectPoint(new SCNVector3(point.x, point.y, 0));
        var b = unprojectPoint(new SCNVector3(point.x, point.y, 1));
        return scene.rootNode.hitTestWithSegment(a, b, options);
    }
}

/// <summary>SCNRenderer: offscreen rendering of a scene (snapshot(atTime:with:antialiasingMode:)).</summary>
public sealed class SCNRenderer : SCNSceneRenderer
{
    private readonly ViewRig rig;
    public double sceneTime { get; set; }
    public bool usesReverseZ = true;
    public bool autoenablesDefaultLighting, isJitteringEnabled;
    public SCNSceneRendererDelegate @delegate;
    public MTLDevice device => MTLDevice.Shared;

    /// <summary>SCNRenderer(device:options:).</summary>
    public SCNRenderer(MTLDevice device, Dictionary<string, object> options)
    {
        SceneKitRuntime.EnsureStarted();
        var vp = new SubViewport { Name = "SCNRenderer", RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled, Size = new Vector2I(64, 64) };
        SceneKitRuntime.AddRendererViewport(vp);
        rig = new ViewRig(vp);
    }
    public SCNScene scene { get => rig.scene; set => rig.SetScene(value); }
    public SCNNode pointOfView { get => rig.pointOfView; set => rig.pointOfView = value; }
    public NSColor backgroundColor { get => rig.backgroundColor; set => rig.backgroundColor = value; }

    /// <summary>snapshot(atTime:with:antialiasingMode:). PORT: atTime does not drive shader time (Godot TIME).</summary>
    public NSImage snapshot(double atTime, CGSize with, SCNAntialiasingMode antialiasingMode)
    {
        var img = SnapshotImage(new Vector2I((int)Math.Round(with.width), (int)Math.Round(with.height)), antialiasingMode);
        return NSImage.data(img.SavePngToBuffer());
    }
    /// <summary>Facade helper: the rendered Godot image (sRGB, RGBA8).</summary>
    public Image SnapshotImage(Vector2I size, SCNAntialiasingMode antialiasingMode)
    {
        rig.viewport.Size = size;
        rig.viewport.Msaa3D = ViewRig.Msaa(antialiasingMode);
        rig.viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        rig.Sync();
        SceneKitRuntime.RenderNow();
        rig.Sync();
        rig.viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        RenderingServer.ForceDraw(false, 0.0);
        return rig.viewport.GetTexture().GetImage();
    }
    /// <summary>render(withViewport:commandBuffer:passDescriptor:) is Metal-specific. PORT: unsupported (diagnostics only).</summary>
    public void render(CGRect withViewport, object commandBuffer, object passDescriptor) => throw new NotSupportedException("SCNRenderer.render(withViewport:commandBuffer:passDescriptor:) has no Godot equivalent");
    public bool isNode(SCNNode node, SCNNode insideFrustumOf) => rig.IsNodeInFrustum(node, insideFrustumOf, rig.Aspect);
    public List<SCNNode> nodesInsideFrustum(SCNNode of)
    {
        var list = new List<SCNNode>();
        scene?.rootNode.enumerateChildNodes((n, _) => { if (n.geometry != null && isNode(n, of)) list.Add(n); });
        return list;
    }
    public SCNVector3 projectPoint(SCNVector3 point) => rig.Project(point, rig.viewport.Size.X, rig.viewport.Size.Y);
    public SCNVector3 unprojectPoint(SCNVector3 point) => rig.Unproject(point, rig.viewport.Size.X, rig.viewport.Size.Y);
    public void prepare(object[] objects, Action<bool> completionHandler) { SceneKitRuntime.Flush(); completionHandler?.Invoke(true); }
    /// <summary>prepare(_:shouldAbortBlock:) (SCNSceneRenderer), as SCNView's: flushes pending changes; Godot compiles on first draw.</summary>
    public bool prepare(object @object, Func<bool> shouldAbortBlock) { SceneKitRuntime.Flush(); return true; }
}
