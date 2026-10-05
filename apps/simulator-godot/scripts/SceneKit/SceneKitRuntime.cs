using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Marvin.SceneKit;

/// <summary>
/// Facade runtime: one node in the scene tree that hosts SCNScene worlds and
/// SCNRenderer viewports, and once per frame (RenderingServer.FramePreDraw)
/// flushes SceneKit state to Godot: materials, node transforms/meshes/lights,
/// constraints, view cameras/environments and the composer's global uniforms.
/// </summary>
public partial class SceneKitRuntime : Node
{
    private static SceneKitRuntime instance;
    private static readonly Dictionary<SCNNode, int> dirtyNodes = new();
    private static readonly HashSet<MaterialGpu> dirtyMaterials = new();
    private static readonly HashSet<SCNNode> constrained = new();
    private static readonly List<SubViewport> pendingHosts = new();
    internal static readonly List<WeakReference<SCNView>> views = new();
    private static bool sceneStateDirty = true, masksDirty;
    private static bool flushing;
    /// <summary>True while Flush() pushes state to Godot for the frame (or snapshot) about to be drawn.</summary>
    internal static bool Flushing => flushing;
    internal static int CameraMask = -1, ShadowLightMask = -1;
    internal static SCNNode ActiveCamera;
    internal static SCNScene ActiveScene;
    private static readonly Dictionary<int, int> layerOfBit = new();
    private static SCNMaterial defaultMaterial;
    internal static SCNMaterial DefaultMaterial => defaultMaterial ??= new SCNMaterial();
    internal static double SceneTime;

    /// <summary>Creates the runtime node (deferred add to the scene tree root) and the global shader uniforms.</summary>
    public static void EnsureStarted()
    {
        if (instance != null || !OnMainThread) return;
        // Runs after every other node's _Process; Godot then applies the transform notifications.
        instance = new SceneKitRuntime { Name = "SceneKitRuntime", ProcessPriority = int.MaxValue, ProcessMode = ProcessModeEnum.Always };
        // The composer's global uniforms (scn_fog_color, scn_ambient, scn_sh0..8, ...) are declared in
        // project.godot [shader_globals]; adding them at runtime is editor-only in Godot.
        // SceneKit uses 4096 shadow maps for the race sun; give Godot's shared atlas room for two such lights.
        RenderingServer.DirectionalShadowAtlasSetSize(SceneKitCalibration.DirectionalShadowAtlas, true);
        RenderingServer.DirectionalSoftShadowFilterSetQuality((RenderingServer.ShadowQuality)SceneKitCalibration.ShadowFilterQuality);
        RenderingServer.PositionalSoftShadowFilterSetQuality((RenderingServer.ShadowQuality)SceneKitCalibration.ShadowFilterQuality);
        RenderingServer.FramePreDraw += Flush;
        RenderingServer.FramePostDraw += PostDraw;
        if (Engine.GetMainLoop() is SceneTree tree)
            tree.Root.CallDeferred(Node.MethodName.AddChild, instance);
    }
    internal static void EnsureStartedFromAnyThread()
    {
        if (instance != null) return;
        if (OnMainThread) EnsureStarted();
        else Callable.From(EnsureStarted).CallDeferred();
    }
    public override void _Ready()
    {
        foreach (var h in pendingHosts) if (h.GetParent() == null) AddChild(h);
        pendingHosts.Clear();
    }
    public override void _Process(double delta) { SceneTime += delta; DispatchQueue.Drain(); Flush(); }

    internal static void AttachHost(SubViewport host)
    {
        EnsureStarted();
        if (instance.IsInsideTree()) instance.AddChild(host);
        else pendingHosts.Add(host);
    }

    // ---- Threads
    // SceneKit objects may be built on a background queue (the game builds DirtWorld that way) and handed
    // to the main thread. The facade follows that model: Godot nodes outside the scene tree may be
    // changed from any thread, so a scene created off the main thread stays detached, and changes made
    // off the main thread are parked here instead of entering the per-frame dirty sets. The main thread
    // adopts them when it takes the objects over: when a scene is shown (SCNView/SCNRenderer.scene) and
    // when addChildNode attaches a subtree. Everything else in the facade is main-thread only, like Godot.
    private static int mainThreadId = -1;
    /// <summary>True on Godot's main thread (cheap: compares managed thread ids after the first call there).</summary>
    internal static bool OnMainThread
    {
        get
        {
            int id = System.Environment.CurrentManagedThreadId;
            if (id == mainThreadId) return true;
            if (mainThreadId != -1) return false;
            if (OS.GetThreadCallerId() != OS.GetMainThreadId()) return false;
            mainThreadId = id;
            return true;
        }
    }
    private static readonly object parkGate = new();
    private static readonly Dictionary<SCNNode, int> parkedNodes = new();
    private static readonly HashSet<MaterialGpu> parkedMaterials = new();
    private static readonly HashSet<MTLTexture> parkedTextures = new();
    private static volatile bool anyParked;
    private const int ParkedConstrain = 1 << 29, ParkedUnconstrain = 1 << 30;

    /// <summary>Main thread: takes over the changes other threads parked for a subtree (and all parked materials and textures).</summary>
    internal static void Adopt(SCNNode subtree)
    {
        if (!anyParked || !OnMainThread) return;
        var nodes = new List<(SCNNode node, int flags)>();
        MaterialGpu[] mats;
        MTLTexture[] texs;
        lock (parkGate)
        {
            subtree?.enumerateHierarchy((n, _) => { if (parkedNodes.Remove(n, out var f)) nodes.Add((n, f)); });
            mats = parkedMaterials.ToArray(); parkedMaterials.Clear();
            texs = parkedTextures.ToArray(); parkedTextures.Clear();
            anyParked = parkedNodes.Count > 0;
        }
        foreach (var m in mats) dirtyMaterials.Add(m);
        foreach (var t in texs) dirtyTextures.Add(t);
        foreach (var (n, f) in nodes)
        {
            if ((f & ParkedConstrain) != 0) constrained.Add(n);
            if ((f & ParkedUnconstrain) != 0) constrained.Remove(n);
            int flags = f & ~(ParkedConstrain | ParkedUnconstrain);
            if (flags != 0) NodeDirty(n, flags);
        }
        sceneStateDirty = true;
    }
    private static void Park(SCNNode node, int flags)
    {
        lock (parkGate)
        {
            parkedNodes.TryGetValue(node, out var f);
            if ((flags & ParkedConstrain) != 0) f &= ~ParkedUnconstrain;
            if ((flags & ParkedUnconstrain) != 0) f &= ~ParkedConstrain;
            parkedNodes[node] = f | flags;
            anyParked = true;
        }
    }

    internal static void NodeDirty(SCNNode node, int flags)
    {
        if (!OnMainThread) { Park(node, flags); return; }
        if (dirtyNodes.TryGetValue(node, out var f)) { if ((f | flags) != f) dirtyNodes[node] = f | flags; }
        else dirtyNodes[node] = flags;
    }
    internal static void MaterialDirty(MaterialGpu m)
    {
        if (OnMainThread) { dirtyMaterials.Add(m); return; }
        lock (parkGate) { parkedMaterials.Add(m); anyParked = true; }
    }
    private static readonly HashSet<MTLTexture> dirtyTextures = new();
    internal static void TextureDirty(MTLTexture t)
    {
        if (OnMainThread) { dirtyTextures.Add(t); return; }
        lock (parkGate) { parkedTextures.Add(t); anyParked = true; }
    }
    internal static void SceneStateDirty() => sceneStateDirty = true;
    private static bool activeLdr;
    /// <summary>The view being synced renders without HDR (SCNCamera.wantsHDR false).</summary>
    internal static void SetActiveLdr(bool ldr) { if (activeLdr != ldr) { activeLdr = ldr; sceneStateDirty = true; } }
    internal static void MasksChanged() { masksDirty = true; sceneStateDirty = true; }
    internal static void ConstraintsChanged(SCNNode node, bool has)
    {
        if (!OnMainThread) { Park(node, has ? ParkedConstrain : ParkedUnconstrain | SCNNode.DirtyTransform); return; }
        if (has) constrained.Add(node); else { constrained.Remove(node); NodeDirty(node, SCNNode.DirtyTransform); }
    }

    /// <summary>Maps a SceneKit category bit mask to Godot's 20 render layers (bits allocated on first use).</summary>
    internal static uint GodotLayers(int mask)
    {
        if (mask == -1) return 0xFFFFF;
        uint result = 0;
        for (int bit = 0; bit < 32; bit++)
        {
            if ((mask & (1 << bit)) == 0) continue;
            if (!layerOfBit.TryGetValue(bit, out int layer))
            {
                if (layerOfBit.Count >= 20) continue;
                layer = bit == 0 ? 0 : Enumerable.Range(1, 19).First(l => !layerOfBit.ContainsValue(l));
                if (bit == 0 && layerOfBit.ContainsValue(0)) layer = Enumerable.Range(1, 19).First(l => !layerOfBit.ContainsValue(l));
                layerOfBit[bit] = layer;
            }
            result |= 1u << layer;
        }
        return result;
    }

    internal static double AmbientIntensityFor(SCNScene scene)
    {
        if (scene == null) return 0;
        double total = 0;
        scene.rootNode.enumerateHierarchy((n, _) => { if (n.light is { type: SCNLight.LightType.ambient } l && !n.isHidden) total += l.intensity; });
        return total;
    }

    /// <summary>Applies all pending SceneKit changes to Godot. Called before every frame and by snapshot().</summary>
    public static void Flush()
    {
        if (flushing || !OnMainThread) return;
        flushing = true;
        try
        {
            foreach (var view in LiveViews()) view.scene?.EnsureAttached();
            foreach (var view in LiveViews()) if (view.IsVisibleInTree()) view.CallDelegateUpdate();
            if (masksDirty)
            {
                masksDirty = false;
                foreach (var view in LiveViews()) if (view.scene != null) view.scene.rootNode.MarkSubtree(SCNNode.DirtyVisual | SCNNode.DirtyLight);
            }
            for (int pass = 0; pass < 4 && (dirtyMaterials.Count > 0 || dirtyNodes.Count > 0); pass++)
            {
                if (dirtyMaterials.Count > 0)
                {
                    var mats = dirtyMaterials.ToArray(); dirtyMaterials.Clear();
                    foreach (var m in mats) m.Flush();
                }
                if (dirtyNodes.Count > 0)
                {
                    var nodes = dirtyNodes.ToArray(); dirtyNodes.Clear();
                    foreach (var (node, flags) in nodes) if (GodotObject.IsInstanceValid(node)) node.Flush(flags);
                }
            }
            if (dirtyTextures.Count > 0) { foreach (var t in dirtyTextures) t.Refresh(); dirtyTextures.Clear(); }
            foreach (var node in constrained.ToArray()) ApplyConstraints(node);
            foreach (var view in LiveViews()) view.SyncCamera();
            if (ActiveScene != null && (sceneStateDirty || ActiveScene != uniformsScene || ActiveScene.stateVersion != uniformsSceneVersion))
                UpdateSceneUniforms(ActiveScene);
            foreach (var view in LiveViews()) if (view.IsVisibleInTree()) view.CallDelegateWillRender();
        }
        finally { flushing = false; }
    }
    private static void PostDraw()
    {
        foreach (var view in LiveViews()) if (view.IsVisibleInTree()) view.CallDelegateDidRender();
    }
    /// <summary>Views that still exist (freed views are dropped from the list).</summary>
    private static List<SCNView> LiveViews()
    {
        var live = new List<SCNView>();
        for (int i = views.Count - 1; i >= 0; i--)
        {
            if (views[i].TryGetTarget(out var view) && GodotObject.IsInstanceValid(view) && !view.IsQueuedForDeletion()) live.Add(view);
            else views.RemoveAt(i);
        }
        live.Reverse();
        return live;
    }

    private static int uniformsSceneVersion = -1;
    private static SCNScene uniformsScene;
    /// <summary>Scene-wide state the composer reads from global uniforms (fog, ambient lights, IBL SH, deferred shadows).</summary>
    internal static void UpdateSceneUniforms(SCNScene scene)
    {
        if (scene == null) return;
        sceneStateDirty = false;
        uniformsScene = scene; uniformsSceneVersion = scene.stateVersion;
        var fog = scene.fogColor as NSColor ?? NSColor.white;
        bool fogOn = scene.fogEndDistance > 0 && scene.fogEndDistance > scene.fogStartDistance;
        var fogLinear = fog.GodotLinear;
        RenderingServer.GlobalShaderParameterSet("scn_fog_color", new Vector4(fogLinear.R, fogLinear.G, fogLinear.B, 1));
        RenderingServer.GlobalShaderParameterSet("scn_fog_range", new Vector4((float)scene.fogStartDistance, (float)scene.fogEndDistance, (float)Math.Max(1e-3, scene.fogDensityExponent), fogOn ? 1 : 0));
        var ambient = Vector3.Zero;
        int shadowMask = 0;
        double deferredAlpha = 0, deferredRadius = 0;
        double ambientIntensity = 0;
        scene.rootNode.enumerateHierarchy((n, _) =>
        {
            if (n.light is not SCNLight l || n.isHidden) return;
            if (l.type == SCNLight.LightType.ambient) { ambient += l.LinearRadiance * (float)(SceneKitCalibration.AmbientPerLumen * 1000); ambientIntensity += l.intensity; }
            else if (l.castsShadow)
            {
                shadowMask |= l.categoryBitMask;
                if (l.shadowMode == SCNShadowMode.deferred && deferredAlpha == 0) { deferredAlpha = Math.Clamp(l.ShadowColor.alphaComponent, 0, 1); deferredRadius = l.shadowRadius; }
            }
        });
        if (ShadowLightMask != (shadowMask == 0 ? -1 : shadowMask)) { ShadowLightMask = shadowMask == 0 ? -1 : shadowMask; masksDirty = true; }
        RenderingServer.GlobalShaderParameterSet("scn_ambient", new Vector4(ambient.X, ambient.Y, ambient.Z, 1));
        var (selfPlateau, selfOnset) = SceneKitCalibration.DeferredSelfShadow(deferredRadius);
        RenderingServer.GlobalShaderParameterSet("scn_deferred", new Vector4((float)deferredAlpha, (float)selfPlateau, (float)selfOnset, 0));
        bool ibl = scene.HasLightingEnvironment;
        // scn_ibl.z: the view renders LDR (SceneKit's 8-bit target; the composer's light() clamps each draw to 1).
        RenderingServer.GlobalShaderParameterSet("scn_ibl", new Vector4(ibl ? (float)scene.lightingEnvironment.intensity : 0, ibl ? 1 : 0, activeLdr ? 1 : 0, 0));
        if (ibl) RenderingServer.GlobalShaderParameterSet("scn_radiance", scene.RadianceTexture());
        var sh = scene.IrradianceSH();
        for (int i = 0; i < 9; i++) RenderingServer.GlobalShaderParameterSet($"scn_sh{i}", new Vector4((float)sh[i * 3], (float)sh[i * 3 + 1], (float)sh[i * 3 + 2], 0));
    }

    private static void ApplyConstraints(SCNNode node)
    {
        if (!GodotObject.IsInstanceValid(node) || node.constraints == null || !node.IsInsideTree()) return;
        var world = node.RenderWorld();
        foreach (var c in node.constraints)
            if (c.isEnabled) world = c.Apply(node, world, ActiveCamera);
        var parentWorld = node.parent?.RenderWorld() ?? SCNMatrix4.Identity;
        node.Transform = SCNMatrix4.Mul(SCNMatrix4.Inverse(parentWorld), world).ToGodot();
        node.constraintsApplied = true;
    }

    /// <summary>Renders all viewports now (used by snapshot()). Must be called on the main thread.</summary>
    internal static void RenderNow(Node extra = null)
    {
        Flush();
        // Godot defers Node3D transform notifications to the end of the frame; apply them now.
        if (instance != null && instance.IsInsideTree()) ForceTransforms(instance);
        if (extra != null && extra.IsInsideTree()) ForceTransforms(extra);
        RenderingServer.ForceDraw(false, 0.0);
    }
    private static void ForceTransforms(Node node)
    {
        if (node is Node3D spatial) spatial.ForceUpdateTransform();
        foreach (var child in node.GetChildren(true)) ForceTransforms(child);
    }

    // ---- Directional shadows fitted per camera
    private static readonly HashSet<SCNNode> shadowLights = new();
    private static readonly Projection[] shadowBoxes = new Projection[2];
    private static Vector4 shadowSlope;
    internal static void RegisterShadowLight(SCNNode node, bool on) { if (on) shadowLights.Add(node); else shadowLights.Remove(node); }

    /// <summary>
    /// Godot fits a directional shadow map to the camera frustum (up to DirectionalShadowMaxDistance), and both its
    /// PCF kernel (blur x quality radius x texel) and its depth bias (bias x blur x quality radius x depth range) scale
    /// with that fit. SceneKit's kernel is shadowRadius texels of its own map. Called after a view's camera is synced:
    /// recomputes Godot's fit like RendererSceneCull and sets ShadowBlur/ShadowBias so the penumbra and the bias
    /// have SceneKit's world size for this camera.
    /// </summary>
    internal static void FitShadows(SCNScene scene, Camera3D camera, Vector2I viewportSize)
    {
        shadowLights.RemoveWhere(n => !GodotObject.IsInstanceValid(n));
        var lights = new List<SCNNode>();
        foreach (var n in shadowLights)
            if (GodotObject.IsInstanceValid(n) && n.sceneOwner == scene && n.GodotLight is DirectionalLight3D && n.IsVisibleInTree()) lights.Add(n);
        // SceneKit's fixed shadow boxes (automaticallyAdjustsShadowProjection = false): the composer's light() leaves
        // receivers outside them unshadowed. Up to two such lights (the game's binary suns); further ones use Godot's fit.
        var boxes = new Projection[2];
        var slopes = new double[2];
        int boxCount = 0;
        foreach (var n in lights)
            if (!n.light.automaticallyAdjustsShadowProjection && boxCount < boxes.Length)
            {
                slopes[boxCount] = SceneKitCalibration.ShadowSlopeBiasTexels * n.light.SceneKitShadowTexel;
                boxes[boxCount++] = n.light.ShadowBox(n.RenderWorld());
            }
        for (int i = 0; i < boxes.Length; i++)
        {
            if (shadowBoxes[i] == boxes[i]) continue;
            shadowBoxes[i] = boxes[i];
            RenderingServer.GlobalShaderParameterSet(i == 0 ? "scn_shadow_box0" : "scn_shadow_box1", boxes[i]);
        }
        var slope = new Vector4((float)slopes[0], (float)slopes[1], (float)SceneKitCalibration.ShadowSlopeMax, 0);
        if (shadowSlope != slope) { shadowSlope = slope; RenderingServer.GlobalShaderParameterSet("scn_shadow_slope", slope); }
        if (lights.Count == 0) return;
        int splitH = 1, splitV = 1;
        while (splitH * splitV < lights.Count) { if (splitH == splitV) splitH <<= 1; else splitV <<= 1; }
        int atlas = SceneKitCalibration.DirectionalShadowAtlas;
        double regionW = atlas / splitH, regionH = atlas / splitV, textureSize = Math.Max(regionW, regionH);
        double aspect = viewportSize.Y > 0 ? (double)viewportSize.X / viewportSize.Y : 1;
        foreach (var n in lights)
        {
            var light = n.GodotLight as DirectionalLight3D;
            double near = camera.Near, far = camera.Far;
            bool ortho = camera.Projection == Camera3D.ProjectionType.Orthogonal;
            bool keepWidth = camera.KeepAspect == Camera3D.KeepAspectEnum.Width;
            double rW = regionW, rH = regionH, tSize = textureSize;
            if (!n.light.automaticallyAdjustsShadowProjection && n.light.shadowCascadeCount <= 1)
            {
                // SceneKit's fixed box shadows everything inside it, however far from the camera; Godot's map ends at
                // DirectionalShadowMaxDistance. Keep the calibrated fit (orthographicScale) as the first split and add a
                // second one out to the farthest point of the box in view (the composer clips both to the box).
                double first = n.light.FixedShadowDistance, need = 0;
                if (!ortho)
                {
                    double tv = Math.Tan(camera.Fov * Math.PI / 360);
                    double tx = keepWidth ? tv : tv * aspect, ty = keepWidth ? tv / aspect : tv;
                    need = Math.Min(Math.Min(n.light.ShadowBoxFarDepth(n.RenderWorld(), camera.Transform, tx, ty, near, far), n.light.maximumShadowDistance), far);
                }
                if (need > first * 1.05)
                {
                    if (light.DirectionalShadowMode != DirectionalLight3D.ShadowMode.Parallel2Splits) light.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits;
                    float split1 = (float)Math.Clamp((first - near) / (need - near), 0.01, 0.99);
                    if (light.DirectionalShadowMaxDistance != (float)need) light.DirectionalShadowMaxDistance = (float)need;
                    if (light.DirectionalShadowSplit1 != split1) light.DirectionalShadowSplit1 = split1;
                    if (light.DirectionalShadowBlendSplits) light.DirectionalShadowBlendSplits = false;
                    rH /= 2; tSize = Math.Max(rW, rH); // Godot halves the light's atlas region for two splits
                }
                else
                {
                    if (light.DirectionalShadowMode != DirectionalLight3D.ShadowMode.Orthogonal) light.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Orthogonal;
                    if (light.DirectionalShadowMaxDistance != (float)first) light.DirectionalShadowMaxDistance = (float)first;
                }
                if (!ortho) far = Math.Min(far, first);
            }
            else if (!ortho && light.DirectionalShadowMaxDistance > 0) far = Math.Min(far, light.DirectionalShadowMaxDistance);
            far = Math.Max(far, near + 0.001);
            // Frustum slice endpoints in camera space, bounding sphere (RendererSceneCull::_light_instance_setup_directional_shadow).
            // Half extents along the kept axis (Camera3D.KeepAspect), the other axis follows the aspect ratio.
            double hn, hf;
            if (ortho) { hn = hf = camera.Size / 2; }
            else { double t = Math.Tan(camera.Fov * Math.PI / 360); hn = near * t; hf = far * t; }
            double ax = keepWidth ? 1 : aspect, ay = keepWidth ? 1 / aspect : 1;
            var pts = new List<Vector3>();
            foreach (var (h, z) in new[] { (hn, near), (hf, far) })
                foreach (var sx in new[] { -1, 1 })
                    foreach (var sy in new[] { -1, 1 })
                        pts.Add(new Vector3((float)(sx * h * ax), (float)(sy * h * ay), (float)-z));
            var center = Vector3.Zero; foreach (var p in pts) center += p; center /= pts.Count;
            double radius = 0; foreach (var p in pts) radius = Math.Max(radius, center.DistanceTo(p));
            radius *= tSize / (tSize - 2.0);
            // Godot's PCF kernel is isotropic in atlas pixels; with two or more lights a region is not square
            // (e.g. 4096 x 8192), so size the kernel from the coarser axis, which is horizontal in light space and thus
            // runs across the shadows of thin vertical casters (legs, posts, robot parts).
            double texel = 2 * radius / Math.Min(rW, rH);
            n.light.FitShadow(light, texel, 2 * radius + light.DirectionalShadowPancakeSize, Math.Pow(tSize / textureSize, SceneKitCalibration.SplitNormalBiasExponent));
        }
    }

    internal static Node Host => instance;
    internal static void AddRendererViewport(SubViewport vp)
    {
        EnsureStarted();
        if (instance.IsInsideTree()) instance.AddChild(vp); else pendingHosts.Add(vp);
    }
}

/// <summary>SCNTransaction: implicit animations do not exist in the facade, so every change is immediate.</summary>
public static class SCNTransaction
{
    public static void begin() { }
    public static void commit() { }
    public static void flush() => SceneKitRuntime.Flush();
    public static void @lock() { }
    public static void unlock() { }
    public static bool disableActions { get; set; }
    public static double animationDuration { get; set; }
    public static Action completionBlock { get; set; }
}
