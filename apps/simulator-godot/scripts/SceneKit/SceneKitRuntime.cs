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
        if (instance != null) return;
        // Runs after every other node's _Process; Godot then applies the transform notifications.
        instance = new SceneKitRuntime { Name = "SceneKitRuntime", ProcessPriority = int.MaxValue, ProcessMode = ProcessModeEnum.Always };
        // The composer's global uniforms (scn_fog_color, scn_ambient, scn_sh0..8, ...) are declared in
        // project.godot [shader_globals]; adding them at runtime is editor-only in Godot.
        // SceneKit uses 4096 shadow maps for the race sun; give Godot's shared atlas room for two such lights.
        RenderingServer.DirectionalShadowAtlasSetSize(8192, true);
        RenderingServer.DirectionalSoftShadowFilterSetQuality(RenderingServer.ShadowQuality.SoftHigh);
        RenderingServer.FramePreDraw += Flush;
        RenderingServer.FramePostDraw += PostDraw;
        if (Engine.GetMainLoop() is SceneTree tree)
            tree.Root.CallDeferred(Node.MethodName.AddChild, instance);
    }
    public override void _Ready()
    {
        foreach (var h in pendingHosts) if (h.GetParent() == null) AddChild(h);
        pendingHosts.Clear();
    }
    public override void _Process(double delta) { SceneTime += delta; Flush(); }

    internal static void AttachHost(SubViewport host)
    {
        EnsureStarted();
        if (instance.IsInsideTree()) instance.AddChild(host);
        else pendingHosts.Add(host);
    }

    internal static void NodeDirty(SCNNode node, int flags)
    {
        if (dirtyNodes.TryGetValue(node, out var f)) { if ((f | flags) != f) dirtyNodes[node] = f | flags; }
        else dirtyNodes[node] = flags;
    }
    internal static void MaterialDirty(MaterialGpu m) => dirtyMaterials.Add(m);
    private static readonly HashSet<MTLTexture> dirtyTextures = new();
    internal static void TextureDirty(MTLTexture t) => dirtyTextures.Add(t);
    internal static void SceneStateDirty() => sceneStateDirty = true;
    internal static void MasksChanged() { masksDirty = true; sceneStateDirty = true; }
    internal static void ConstraintsChanged(SCNNode node, bool has) { if (has) constrained.Add(node); else { constrained.Remove(node); NodeDirty(node, SCNNode.DirtyTransform); } }

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
        if (flushing) return;
        flushing = true;
        try
        {
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
        double deferredAlpha = 0, deferredOpacity = 1;
        double ambientIntensity = 0;
        scene.rootNode.enumerateHierarchy((n, _) =>
        {
            if (n.light is not SCNLight l || n.isHidden) return;
            if (l.type == SCNLight.LightType.ambient) { ambient += l.LinearRadiance * (float)(ShaderComposer.AmbientPerLumen * 1000); ambientIntensity += l.intensity; }
            else if (l.castsShadow)
            {
                shadowMask |= l.categoryBitMask;
                if (l.shadowMode == SCNShadowMode.deferred && deferredAlpha == 0) deferredAlpha = Math.Clamp(l.ShadowColor.alphaComponent, 0, 1);
            }
        });
        scene.rootNode.enumerateHierarchy((n, _) =>
        {
            if (n.light is { castsShadow: true, shadowMode: SCNShadowMode.deferred } l && deferredOpacity == 1)
            {
                double direct = Math.Max(l.intensity, 1e-3);
                deferredOpacity = Math.Min(1, deferredAlpha * (direct + ambientIntensity) / direct);
            }
        });
        if (ShadowLightMask != (shadowMask == 0 ? -1 : shadowMask)) { ShadowLightMask = shadowMask == 0 ? -1 : shadowMask; masksDirty = true; }
        RenderingServer.GlobalShaderParameterSet("scn_ambient", new Vector4(ambient.X, ambient.Y, ambient.Z, 1));
        RenderingServer.GlobalShaderParameterSet("scn_deferred", new Vector4((float)(deferredOpacity > 0 ? deferredAlpha / deferredOpacity : 0), 0, 0, 0));
        bool ibl = scene.HasLightingEnvironment;
        RenderingServer.GlobalShaderParameterSet("scn_ibl", new Vector4(ibl ? (float)scene.lightingEnvironment.intensity : 0, ibl ? 1 : 0, 0, 0));
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
