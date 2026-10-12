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
    /// <summary>The constrained nodes of the flush in progress (a copy: a constraint may change the set).</summary>
    private static readonly List<SCNNode> constrainedBuffer = new();
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

    /// <summary>True when nothing is drawn: Godot's dummy renderer (`--headless`), whose viewport textures hold no image.</summary>
    internal static bool Headless => headless ??= DisplayServer.GetName() == "headless" || RenderingServer.GetCurrentRenderingMethod() == "dummy";
    private static bool? headless;
    private static bool headlessNoted;

    /// <summary>A viewport's rendered image. Headless (the dummy renderer) there is none: a transparent black RGBA8 image of
    /// the viewport's size stands in, so game modes still run their logic and write their reports (with blank captures and
    /// whatever their pixel checks make of them). GPU-less CI runs the logic modes this way
    /// (.github/workflows/godot-windows.yml). A rendering driver is unaffected.</summary>
    internal static Image ViewportImage(Viewport viewport, Vector2I size)
    {
        if (!Headless) return viewport.GetTexture().GetImage();
        if (!headlessNoted) { headlessNoted = true; GD.Print("SceneKit facade: headless (dummy renderer), snapshots and captures are blank"); }
        return Image.CreateEmpty(Math.Max(1, size.X), Math.Max(1, size.Y), false, Image.Format.Rgba8);
    }

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
        if (!SceneKitCalibration.GlowBicubicUpscale) RenderingServer.EnvironmentGlowSetUseBicubicUpscale(false);
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
        TransformsChanged();
    }
    public override void _Process(double delta)
    {
        long begin = System.Diagnostics.Stopwatch.GetTimestamp();
        SceneTime += delta; DispatchQueue.Drain();
        long drained = System.Diagnostics.Stopwatch.GetTimestamp();
        int meshes = SCNGeometry.MeshesBuilt;
        Flush();
        long end = System.Diagnostics.Stopwatch.GetTimestamp();
        LastDispatchMS = (drained - begin) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        LastFlushMS = (end - drained) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        LastMeshesBuilt = SCNGeometry.MeshesBuilt - meshes;
    }
    /// <summary>Diagnostics for the frame just processed (Godot-only benchmark telemetry): time in queued main-queue blocks,
    /// in Flush (dirty materials, nodes, meshes, constraints, views) and the Godot meshes it built.</summary>
    internal static double LastDispatchMS, LastFlushMS;
    internal static int LastMeshesBuilt;

    // ---- Shutdown (Godot-only)
    // Swift deallocates a SceneKit node once nothing references it (ARC); Godot keeps a node until it is freed, and frees
    // at exit only the nodes in the scene tree. The game drops nodes outside every scene (the TownWorld that
    // --town-statistics builds and never shows, the race's 1,600 pooled particle slots, which are never added to a scene,
    // nodes removed from their parent), so they leaked at exit with their MeshInstance3D and Light3D children: Godot then
    // reported "N RID allocations of type 'RendererSceneCull::Instance' were leaked at exit" and freed them itself, after
    // the meshes and materials they show (RenderingServerDefault::_finish deletes the storages before the scene cull). In
    // builds without DEBUG_ENABLED, the release export templates, an instance's destructor then erases itself from its
    // mesh's freed Dependency (DependencyTracker::clear in ~Instance): a use after free that ended the exported Windows
    // build with 0xC0000374 or 0xC0000005 at every quit after the town was built (PORTING.md, "Release builds and
    // Windows"; --exit-leak-probe shows it with one MeshInstance3D). So the facade tracks the nodes it makes and, when
    // Godot quits, frees those outside the scene tree while every server is still alive, after the background work that
    // builds Godot objects and the audio render threads have stopped and the views' SSAO GPU resources are released
    // (SCNSsaoEffect.ReleaseAll; the uniform sets Godot's UniformSetCacheRD keeps for them would otherwise be freed after
    // the cache itself, a second use after free, in every build that renders with RenderingDevice).
    //
    // Weak GC handles rather than WeakReference objects: no finalizable object per node (the race makes nodes as it runs).
    // A node's managed object lives as long as the Godot node (its script instance holds it), so a handle whose target
    // is gone or disposed belongs to a freed node and is dropped when the list has doubled.
    private static readonly object trackedGate = new();
    private static readonly List<System.Runtime.InteropServices.GCHandle> trackedNodes = new();
    private static int trackedPruneAt = 4096, trackedTotal;
    private static bool shutDown;
    /// <summary>Any thread: remembers a node the facade made (SCNNode, SCNView, NSView) for Shutdown.</summary>
    internal static void TrackNode(Node node)
    {
        lock (trackedGate)
        {
            trackedNodes.Add(System.Runtime.InteropServices.GCHandle.Alloc(node, System.Runtime.InteropServices.GCHandleType.Weak));
            trackedTotal++;
            if (trackedNodes.Count < trackedPruneAt) return;
            trackedNodes.RemoveAll(h => { if (h.Target is Node n && IsInstanceValid(n)) return false; h.Free(); return true; });
            trackedPruneAt = Math.Max(4096, trackedNodes.Count * 2);
        }
    }
    /// <summary>The runtime node stays at the root of the scene tree for the whole run: it leaves the tree only when Godot
    /// quits (SceneTree finalization, whatever ended the run: SceneTree.Quit, exit(), the window's close button).</summary>
    public override void _ExitTree() => Shutdown();
    /// <summary>Main thread, once, when Godot quits: see "Shutdown" above.</summary>
    internal static void Shutdown()
    {
        if (shutDown || !OnMainThread) return;
        shutDown = true;
        // Nothing may touch Godot from another thread while it shuts down: worlds built on DispatchQueue.global (a quit
        // during the loading screen), the background mesh and texture preparation, the audio engines' render threads.
        bool idle = DispatchQueue.WaitForGlobalBlocks(TimeSpan.FromSeconds(60));
        foreach (var p in preparations) Wait(p.work);
        SCNGeometry.WaitForBackgroundWork();
        AVAudioEngine.StopRenderThreads();
        int effects = SCNSsaoEffect.ReleaseAll();
        int freed = FreeDetachedNodes();
        if (OS.IsStdOutVerbose()) GD.Print($"SceneKit facade: shutdown, {freed} detached node trees freed ({trackedTotal} nodes made), {effects} SSAO views released{(idle ? "" : ", background blocks still running")}");
    }
    private static void Wait(System.Threading.Tasks.Task task)
    {
        try { task?.Wait(); } catch (AggregateException) { } // a failed preparation already reported itself
    }
    /// <summary>Frees every tracked node that is outside the scene tree, with the subtree it heads (its topmost
    /// ancestor); nodes in the tree are freed by Godot with the root.</summary>
    private static int FreeDetachedNodes()
    {
        if ((Engine.GetMainLoop() as SceneTree)?.Root is not Window root) return 0;
        var nodes = new List<Node>();
        lock (trackedGate)
        {
            foreach (var h in trackedNodes) { if (h.Target is Node n) nodes.Add(n); h.Free(); }
            trackedNodes.Clear();
        }
        int freed = 0;
        foreach (var node in nodes)
        {
            if (!IsInstanceValid(node)) continue;
            Node top = node;
            for (var parent = top.GetParent(); parent != null; parent = parent.GetParent()) top = parent;
            if (top == root) continue;
            top.Free();
            freed++;
        }
        return freed;
    }

    internal static void AttachHost(SubViewport host)
    {
        EnsureStarted();
        if (instance.IsInsideTree()) instance.AddChild(host);
        else pendingHosts.Add(host);
        TransformsChanged(); // the scene's nodes enter the tree
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
        // node.dirtyFlags mirrors the node's entry in dirtyNodes: the transform getters mark their node on every access
        // (the race's particle loop reads positions thousands of times per frame), so skip the dictionary once queued.
        int queued = node.dirtyFlags;
        if ((queued | flags) == queued) return;
        node.dirtyFlags = queued | flags;
        dirtyNodes[node] = queued | flags;
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
    internal static void SceneStateDirty() { sceneStateDirty = true; System.Threading.Interlocked.Increment(ref lightEpoch); }

    // ---- Lighting features of a scene's shaders (Godot-only, performance; the images are those of the full shaders).
    // The composer's light() carries SceneKit's deferred shadows and the LDR clamp (both behind uniform branches) and is
    // inlined by Godot into its omni and spot light loops too; a .constant material runs Godot's light loop only for
    // deferred shadows. Scenes that never use a feature get shaders without its code (ShaderComposer.VariantFlags
    // Forward, DirectionalOnly): the race and town show no deferred shadows, are drawn HDR and have no omni or spot
    // lights. A scene's features only grow: they start from its lights when its first node is built, and a view that
    // syncs a scene with a feature its shaders lack (an LDR camera, a deferred or positional light added later) adds the
    // feature and rebuilds the scene's nodes before that view draws (ObserveShading). 1080p frozen views: town -2.0 ms, race start -1.8 ms GPU per frame with the composer's
    // other changes (docs/performance.md, "Shading and post").
    internal const int ShadingDeferred = 1, ShadingLdr = 2, ShadingPositional = 4;
    /// <summary>Incremented by every light or scene state change (SceneStateDirty), so scenes rescan their lights.</summary>
    private static int lightEpoch;
    /// <summary>A scene gained a feature in this flush: its nodes were marked and are rebuilt before drawing.</summary>
    private static bool shadingGrew;
    private static int ScanLights(SCNScene scene)
    {
        int features = 0;
        scene.rootNode.enumerateHierarchy((n, _) =>
        {
            if (n.light is not SCNLight l) return;
            if (l.type is SCNLight.LightType.omni or SCNLight.LightType.spot) features |= ShadingPositional;
            else if (l.type == SCNLight.LightType.directional && l.castsShadow && l.shadowMode == SCNShadowMode.deferred) features |= ShadingDeferred;
        });
        return features;
    }
    /// <summary>The shader variant flags for nodes of <paramref name="scene"/> (none outside every scene: full shaders).</summary>
    internal static ShaderComposer.VariantFlags ShadingVariant(SCNScene scene)
    {
        if (scene == null) return ShaderComposer.VariantFlags.None;
        if (scene.shadingFeatures < 0) { scene.shadingScanEpoch = lightEpoch; scene.shadingFeatures = ScanLights(scene); }
        var flags = ShaderComposer.VariantFlags.None;
        if ((scene.shadingFeatures & (ShadingDeferred | ShadingLdr)) == 0) flags |= ShaderComposer.VariantFlags.Forward;
        if ((scene.shadingFeatures & ShadingPositional) == 0) flags |= ShaderComposer.VariantFlags.DirectionalOnly;
        return flags | DetailVariant;
    }
    /// <summary>The graphics-detail shading variant (look changes; none at Graphics detail Max): SceneKitCalibration
    /// .SimpleSkyReflection and SimpleGroundLighting (ShaderComposer.VariantFlags.SimpleSky, SimpleGround).</summary>
    internal static ShaderComposer.VariantFlags DetailVariant =>
        (SceneKitCalibration.SimpleSkyReflection ? ShaderComposer.VariantFlags.SimpleSky : 0)
        | (SceneKitCalibration.SimpleGroundLighting ? ShaderComposer.VariantFlags.SimpleGround : 0);
    /// <summary>Incremented when DetailVariant or SceneKitCalibration.LodDistanceScale changes at run time
    /// (GraphicsDetailChanged): every scene rebuilds its nodes with the new shaders and LOD distances before it is drawn
    /// next (ObserveShading) or prepared (PrepareAsync).</summary>
    internal static int DetailEpoch;
    /// <summary>What the scenes' nodes were last built with: the detail shading variant and the LOD distance scale.</summary>
    private static (ShaderComposer.VariantFlags, double) builtDetail = (DetailVariant, SceneKitCalibration.LodDistanceScale);
    /// <summary>A view is about to draw <paramref name="scene"/> (ldr: its camera renders without HDR): adds the features the
    /// scene's shaders lack and marks its nodes for rebuilding (Flush and RenderIsolated flush them before drawing).</summary>
    internal static void ObserveShading(SCNScene scene, bool ldr)
    {
        if (scene == null) return;
        if (ObserveDetail(scene)) shadingGrew = true;
        int observed = ldr ? ShadingLdr : 0;
        if (scene.shadingScanEpoch != lightEpoch || scene.shadingFeatures < 0) { scene.shadingScanEpoch = lightEpoch; observed |= ScanLights(scene); }
        if (scene.shadingFeatures < 0) { scene.shadingFeatures = observed; return; }
        if ((observed & ~scene.shadingFeatures) == 0) return;
        var before = ShadingVariant(scene);
        scene.shadingFeatures |= observed;
        // Only a change of the variant needs new shaders (the menu's LDR camera adds nothing to its deferred light's).
        if (ShadingVariant(scene) == before) return;
        scene.rootNode.MarkSubtree(SCNNode.DirtyGeometry);
        shadingGrew = true;
    }
    /// <summary>The graphics-detail shading changed since this scene's nodes were built (DetailEpoch): marks them for
    /// rebuilding with the new shaders. Called before a view draws the scene (ObserveShading) and when a scene is prepared
    /// (PrepareAsync: the race world cached from an earlier race rebuilds behind the loading screen, spread over frames).</summary>
    private static bool ObserveDetail(SCNScene scene)
    {
        if (scene.detailEpochSeen == DetailEpoch) return false;
        scene.detailEpochSeen = DetailEpoch;
        scene.rootNode.MarkSubtree(SCNNode.DirtyGeometry);
        return true;
    }
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

    /// <summary>Applies pending SceneKit changes to Godot before every frame. A scene being prepared asynchronously
    /// (<see cref="PrepareAsync"/>) is flushed over several frames.</summary>
    public static void Flush() => Flush(complete: false);
    /// <summary>Applies all pending SceneKit changes now, including all of a scene being prepared (snapshots, synchronous
    /// prepare, SCNTransaction.flush).</summary>
    internal static void FlushAll() => Flush(complete: true);
    private static void Flush(bool complete)
    {
        if (flushing || isolating || !OnMainThread) return;
        flushing = true;
        completeFlush = complete;
        try
        {
            // FrameProfile: per-stage CPU time for the benchmark telemetry (measurement only).
            var t = FrameProfile.Now;
            foreach (var view in LiveViews()) view.scene?.EnsureAttached();
            foreach (var view in LiveViews()) if (view.IsVisibleInTree()) view.CallDelegateUpdate();
            FrameProfile.Add(FrameProfile.Attach, t); t = FrameProfile.Now;
            SCNGeometry.RecheckTangents();
            FrameProfile.Add(FrameProfile.Tangents, t); t = FrameProfile.Now;
            ApplyMasks(null);
            FrameProfile.Add(FrameProfile.Masks, t);
            FlushDirty();
            t = FrameProfile.Now;
            if (dirtyTextures.Count > 0) { foreach (var t2 in dirtyTextures) t2.Refresh(); dirtyTextures.Clear(); }
            FrameProfile.Add(FrameProfile.Textures, t); t = FrameProfile.Now;
            constrainedBuffer.Clear(); constrainedBuffer.AddRange(constrained);
            foreach (var node in constrainedBuffer) ApplyConstraints(node);
            FrameProfile.ConstraintsEvaluated += constrainedBuffer.Count;
            constrainedBuffer.Clear();
            FrameProfile.Add(FrameProfile.Constraints, t); t = FrameProfile.Now;
            foreach (var view in LiveViews()) view.SyncCamera();
            // A view's scene needs a lighting feature its shaders lack (ObserveShading): rebuild its nodes before drawing.
            if (shadingGrew) { shadingGrew = false; FlushDirty(); }
            FrameProfile.Add(FrameProfile.Cameras, t); t = FrameProfile.Now;
            if (ActiveScene != null && (sceneStateDirty || ActiveScene != uniformsScene || ActiveScene.stateVersion != uniformsSceneVersion))
                UpdateSceneUniforms(ActiveScene);
            FrameProfile.Add(FrameProfile.SceneUniforms, t); t = FrameProfile.Now;
            foreach (var view in LiveViews()) if (view.IsVisibleInTree()) view.CallDelegateWillRender();
            FrameProfile.Add(FrameProfile.WillRender, t);
        }
        finally { flushing = false; completeFlush = false; }
        // Every node of the scenes being prepared reached Godot: SceneKit's completion handlers run now (after the flush,
        // so a handler may flush or snapshot itself).
        if (preparations.Count > 0 && preparingNodes.Count == 0)
        {
            var done = preparations.ToArray(); preparations.Clear();
            foreach (var p in done) p.completion?.Invoke(true);
        }
    }

    // ---- SCNView.prepare(_:completionHandler:)
    // SceneKit prepares a scene on a background thread and calls the handler when it is done, so the game's loading screen
    // keeps drawing meanwhile (macOS: 0.75 s at "Getting ready to race"). The facade's preparation is the scene's first
    // flush (meshes, material variants, textures), which froze the loading screen for 2.8 s. Now the CPU half (mesh arrays
    // and mipmapped procedural textures, SCNGeometry.PrepareMesh and NSImage.PrepareTexture) runs on worker threads, and
    // each frame the main thread hands the nodes whose arrays are ready to Godot for at most PrepareBudgetMs, in order; the
    // handler runs once all of them are flushed. Nothing draws the scene meanwhile (the game shows its view in the
    // handler), and a snapshot or a synchronous prepare flushes everything at once. Other scenes flush every frame as usual.
    private sealed class Preparation
    {
        internal HashSet<SCNScene> scenes; internal Action<bool> completion;
        internal System.Threading.Tasks.Task work;
    }
    private static readonly List<Preparation> preparations = new();
    /// <summary>Dirty nodes of scenes being prepared that a frame's budget left for the next frames.</summary>
    private static readonly Dictionary<SCNNode, int> preparingNodes = new();
    private static bool completeFlush;
    private const double PrepareBudgetMs = 10;
    private static ulong prepareFrame = ulong.MaxValue;
    private static long prepareSpentTicks;
    internal static void PrepareAsync(IEnumerable<SCNScene> scenes, Action<bool> completion)
    {
        var set = new HashSet<SCNScene>();
        foreach (var s in scenes) if (s != null) { s.EnsureAttached(); ObserveDetail(s); set.Add(s); }
        if (set.Count == 0 || !OnMainThread) { FlushAll(); completion?.Invoke(true); return; }
        preparations.Add(new Preparation { scenes = set, completion = completion });
    }
    private static Preparation PreparationOf(SCNNode node)
    {
        var scene = node.sceneOwner;
        if (scene == null) return null;
        foreach (var p in preparations) if (p.scenes.Contains(scene)) return p;
        return null;
    }
    /// <summary>Flushes the dirty nodes: all of them, except that nodes of scenes being prepared are flushed only when their
    /// arrays are ready and while this frame's budget lasts; the rest wait in preparingNodes.</summary>
    private static void FlushNodes(KeyValuePair<SCNNode, int>[] nodes)
    {
        if (preparations.Count == 0 || completeFlush)
        {
            // Everything now: the background preparation finishes first (its results are used, nothing is computed twice).
            foreach (var p in preparations) p.work?.Wait();
            FlushNodeBatch(nodes);
            return;
        }
        var regular = new List<KeyValuePair<SCNNode, int>>();
        var preparing = new List<KeyValuePair<SCNNode, int>>();
        foreach (var entry in nodes) (PreparationOf(entry.Key) != null ? preparing : regular).Add(entry);
        if (regular.Count > 0) FlushNodeBatch(regular.ToArray());
        if (preparing.Count == 0) return;
        foreach (var p in preparations) if (p.work == null) p.work = StartPreparation(preparing.Where(e => PreparationOf(e.Key) == p));
        bool allPrepared = preparations.All(p => p.work.IsCompleted);
        ulong frame = Engine.GetProcessFrames();
        if (frame != prepareFrame) { prepareFrame = frame; prepareSpentTicks = 0; }
        long budget = (long)(PrepareBudgetMs / 1000 * System.Diagnostics.Stopwatch.Frequency);
        int at = 0, flushed = 0;
        for (; at < preparing.Count && prepareSpentTicks < budget; at++)
        {
            var (node, flags) = preparing[at];
            if (!allPrepared && !Prepared(node, flags)) break; // keep the order: the workers prepare in this order too
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            if (GodotObject.IsInstanceValid(node)) { var t = FrameProfile.Now; node.Flush(flags); FrameProfile.Add(FrameProfile.Nodes, t); }
            FrameProfile.NodesFlushed++; flushed++;
            prepareSpentTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
        }
        for (; at < preparing.Count; at++)
        {
            var (node, flags) = preparing[at];
            preparingNodes[node] = preparingNodes.TryGetValue(node, out var f) ? f | flags : flags;
        }
    }
    /// <summary>The geometries a node's flush builds (its own and its levels of detail).</summary>
    private static IEnumerable<SCNGeometry> BuiltGeometries(SCNNode node, int flags)
    {
        if ((flags & SCNNode.DirtyGeometry) == 0 || node.sceneOwner == null || node.geometry is not SCNGeometry g) yield break;
        yield return g;
        if (g.levelsOfDetail is SCNLevelOfDetail[] lods) foreach (var l in lods) if (l?.geometry != null) yield return l.geometry;
    }
    /// <summary>Main thread: starts preparing the meshes and procedural textures of these nodes on worker threads, in node
    /// order (each node's textures, then its geometries).</summary>
    private static System.Threading.Tasks.Task StartPreparation(IEnumerable<KeyValuePair<SCNNode, int>> nodes)
    {
        var items = new List<object>();
        var seen = new HashSet<object>();
        var images = new HashSet<NSImage>();
        foreach (var (node, flags) in nodes)
            foreach (var g in BuiltGeometries(node, flags))
            {
                g.EnsureBuilt(); // primitives build their sources on the main thread
                images.Clear();
                foreach (var m in g.MaterialList) m?.CollectImages(images);
                SCNMaterial.CollectArgumentImages(g.arguments, images);
                foreach (var image in images) if (image.NeedsTexture && seen.Add(image)) items.Add(image);
                if (g.NeedsMeshBuild && seen.Add(g)) items.Add(g);
            }
        if (items.Count == 0) return System.Threading.Tasks.Task.CompletedTask;
        return System.Threading.Tasks.Task.Run(() => System.Threading.Tasks.Parallel.ForEach(
            System.Collections.Concurrent.Partitioner.Create(items, System.Collections.Concurrent.EnumerablePartitionerOptions.NoBuffering),
            item => { if (item is NSImage image) image.PrepareTexture(); else ((SCNGeometry)item).PrepareMesh(); }));
    }
    /// <summary>A node's flush would only hand prepared arrays and images to Godot.</summary>
    private static bool Prepared(SCNNode node, int flags)
    {
        var images = new HashSet<NSImage>();
        foreach (var g in BuiltGeometries(node, flags))
        {
            if (!g.MeshReady) return false;
            foreach (var m in g.MaterialList) m?.CollectImages(images);
            SCNMaterial.CollectArgumentImages(g.arguments, images);
        }
        foreach (var image in images) if (image.NeedsTexture) return false;
        return true;
    }
    private static void FlushNodeBatch(KeyValuePair<SCNNode, int>[] nodes)
    {
        var t = FrameProfile.Now;
        PrepareMeshes(nodes);
        FrameProfile.Add(FrameProfile.PrepareMeshes, t); t = FrameProfile.Now;
        foreach (var (node, flags) in nodes) if (GodotObject.IsInstanceValid(node)) node.Flush(flags);
        FrameProfile.NodesFlushed += nodes.Length;
        FrameProfile.Add(FrameProfile.Nodes, t);
    }
    /// <summary>
    /// When a flush rebuilds many meshes (a scene shown for the first time: the race world has thousands), their vertex
    /// and index arrays are computed on all cores first (SCNGeometry.PrepareMesh); the nodes' flush then only hands them to
    /// Godot. Same arrays as the serial path, so the meshes are identical. Small flushes (the per-frame batches) stay serial.
    /// </summary>
    private static void PrepareMeshes(KeyValuePair<SCNNode, int>[] nodes)
    {
        const int MinimumGeometries = 16;
        int candidates = 0;
        foreach (var (node, flags) in nodes) if ((flags & SCNNode.DirtyGeometry) != 0 && node.geometry != null) candidates++;
        if (candidates < MinimumGeometries) return;
        var geometries = new HashSet<SCNGeometry>();
        foreach (var (node, flags) in nodes)
        {
            if ((flags & SCNNode.DirtyGeometry) == 0 || node.geometry is not SCNGeometry g || !GodotObject.IsInstanceValid(node)) continue;
            geometries.Add(g);
            if (g.levelsOfDetail is SCNLevelOfDetail[] lods) foreach (var l in lods) if (l?.geometry != null) geometries.Add(l.geometry);
        }
        var work = new List<SCNGeometry>();
        var images = new HashSet<NSImage>();
        foreach (var g in geometries)
        {
            g.EnsureBuilt(); // primitives build their sources on the main thread
            if (g.NeedsMeshBuild) work.Add(g);
            foreach (var m in g.MaterialList) m?.CollectImages(images);
            SCNMaterial.CollectArgumentImages(g.arguments, images);
        }
        // The procedural textures these nodes' materials bind are converted and mipmapped on all cores too (NSImage.
        // PrepareTexture); binding them in the node flush then only uploads them (the race world's took 1.7 s serially).
        images.RemoveWhere(image => !image.NeedsTexture);
        if (images.Count > 1) System.Threading.Tasks.Parallel.ForEach(images, image => image.PrepareTexture());
        if (work.Count < MinimumGeometries) return;
        System.Threading.Tasks.Parallel.ForEach(work, g => g.PrepareMesh());
    }

    private static void PostDraw()
    {
        if (isolating) return;
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

    private static readonly string[] ShNames = { "scn_sh0", "scn_sh1", "scn_sh2", "scn_sh3", "scn_sh4", "scn_sh5", "scn_sh6", "scn_sh7", "scn_sh8" };
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
        ShaderNames.SetGlobal(ShaderNames.Of("scn_fog_color"), new Vector4(fogLinear.R, fogLinear.G, fogLinear.B, 1));
        ShaderNames.SetGlobal(ShaderNames.Of("scn_fog_range"), new Vector4((float)scene.fogStartDistance, (float)scene.fogEndDistance, (float)Math.Max(1e-3, scene.fogDensityExponent), fogOn ? 1 : 0));
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
        ShaderNames.SetGlobal(ShaderNames.Of("scn_ambient"), new Vector4(ambient.X, ambient.Y, ambient.Z, 1));
        var (selfPlateau, selfOnset) = SceneKitCalibration.DeferredSelfShadow(deferredRadius);
        ShaderNames.SetGlobal(ShaderNames.Of("scn_deferred"), new Vector4((float)deferredAlpha, (float)selfPlateau, (float)selfOnset, 0));
        bool ibl = scene.HasLightingEnvironment;
        // scn_ibl.z: the view renders LDR (SceneKit's 8-bit target; the composer's light() clamps each draw to 1).
        ShaderNames.SetGlobal(ShaderNames.Of("scn_ibl"), new Vector4(ibl ? (float)scene.lightingEnvironment.intensity : 0, ibl ? 1 : 0, activeLdr ? 1 : 0, 0));
        if (ibl) ShaderNames.SetGlobal(ShaderNames.Of("scn_radiance"), scene.RadianceTexture());
        var sh = scene.IrradianceSH();
        for (int i = 0; i < 9; i++) ShaderNames.SetGlobal(ShaderNames.Of(ShNames[i]), new Vector4((float)sh[i * 3], (float)sh[i * 3 + 1], (float)sh[i * 3 + 2], 0));
    }

    private static void ApplyConstraints(SCNNode node)
    {
        // A hidden node is not drawn, and constraints only move the rendered transform: evaluate them once it shows
        // again (in the flush that shows it). The race's 1,067 billboarded dust slots stay hidden (their particles are
        // drawn as one batch) and each cost engine round trips every frame before.
        if (!GodotObject.IsInstanceValid(node) || node.HiddenInHierarchy) return;
        // A node outside every scene is not in Godot's tree either (managed test first: the race's pooled particle slots).
        if (node.constraints == null || node.sceneOwner == null || !node.IsInsideTree()) return;
        var world = node.RenderWorld();
        foreach (var c in node.constraints)
            if (c.isEnabled) world = c.Apply(node, world, ActiveCamera);
        var parentWorld = node.parent?.RenderWorld() ?? SCNMatrix4.Identity;
        node.Transform = SCNMatrix4.Mul(SCNMatrix4.Inverse(parentWorld), world).ToGodot(); node.transformStamp = StampTransform();
        node.constraintsApplied = true;
    }

    /// <summary>Renders all viewports now. Must be called on the main thread.</summary>
    internal static void RenderNow(Node extra = null)
    {
        FlushAll();
        // Godot defers Node3D transform notifications to the end of the frame; apply them now.
        if (instance != null && instance.IsInsideTree()) ForceTransforms(instance);
        if (extra != null && extra.IsInsideTree()) ForceTransforms(extra);
        RenderingServer.ForceDraw(false, 0.0);
    }

    private static bool isolating;
    /// <summary>
    /// Renders one view's or renderer's viewport now (snapshot()), with its own scene's global uniforms (fog, ambient,
    /// sky light, deferred shadows, shadow boxes). The other views do not render in this draw: they would overwrite the
    /// scene uniforms with their own scene's (a renderer showing a QA scene while the window's view shows the race, as
    /// in VisualRegressionSmoke), and SceneKit's offscreen snapshot does not draw them either. The views' delegates are
    /// not called for these draws (they report the window's frames only). <paramref name="sync"/> applies the rig's camera
    /// and environment before each draw.
    /// </summary>
    /// <summary>Re-applies node visibility and shadow casting after a camera or light mask change (MasksChanged).</summary>
    private static void ApplyMasks(SCNScene extra)
    {
        if (!masksDirty) return;
        masksDirty = false;
        foreach (var view in LiveViews()) if (view.scene != null) view.scene.rootNode.MarkSubtree(SCNNode.DirtyVisual | SCNNode.DirtyLight);
        extra?.rootNode.MarkSubtree(SCNNode.DirtyVisual | SCNNode.DirtyLight);
    }
    private static void FlushDirty()
    {
        // Nodes of a scene being prepared that an earlier frame's budget left over (FlushNodes) are due again.
        if (preparingNodes.Count > 0)
        {
            foreach (var (node, flags) in preparingNodes) NodeDirty(node, flags);
            preparingNodes.Clear();
        }
        for (int pass = 0; pass < 4 && (dirtyMaterials.Count > 0 || dirtyNodes.Count > 0); pass++)
        {
            if (dirtyMaterials.Count > 0)
            {
                var t = FrameProfile.Now;
                var mats = dirtyMaterials.ToArray(); dirtyMaterials.Clear();
                foreach (var m in mats) m.Flush();
                FrameProfile.MaterialsFlushed += mats.Length;
                FrameProfile.Add(FrameProfile.Materials, t);
            }
            if (dirtyNodes.Count > 0)
            {
                var nodes = dirtyNodes.ToArray(); dirtyNodes.Clear();
                foreach (var (node, _) in nodes) node.dirtyFlags = 0;
                FlushNodes(nodes);
            }
        }
    }
    internal static void RenderIsolated(SubViewport target, Action sync, int draws = 1, SCNScene scene = null)
    {
        FlushAll();
        var paused = new List<(SubViewport viewport, SubViewport.UpdateMode mode)>();
        isolating = true;
        try
        {
            foreach (var view in LiveViews())
            {
                var vp = view.rig.viewport;
                if (vp != target && vp.RenderTargetUpdateMode != SubViewport.UpdateMode.Disabled) { paused.Add((vp, vp.RenderTargetUpdateMode)); vp.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled; }
            }
            for (int i = 0; i < draws; i++)
            {
                sync();
                // A camera with another categoryBitMask than the window's (the town's shadow-batch proxies are hidden
                // from the race camera only) changes which nodes are visible: apply that before drawing, not at the
                // next frame (VisualRegressionSmoke's first track close-up after its QA scene showed the proxies white).
                if (masksDirty) { ApplyMasks(scene); completeFlush = true; try { FlushDirty(); } finally { completeFlush = false; } }
                // The renderer's scene needs a lighting feature its shaders lack (an LDR camera, ObserveShading): rebuild first.
                if (shadingGrew) { shadingGrew = false; completeFlush = true; try { FlushDirty(); } finally { completeFlush = false; } }
                if (ActiveScene != null) UpdateSceneUniforms(ActiveScene);
                // Godot defers Node3D transform notifications to the end of the frame; apply them now.
                if (instance != null && instance.IsInsideTree()) ForceTransforms(instance);
                if (target.IsInsideTree()) ForceTransforms(target);
                if (target.RenderTargetUpdateMode != SubViewport.UpdateMode.Always) target.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
                RenderingServer.ForceDraw(false, 0.0);
            }
        }
        finally
        {
            foreach (var (vp, mode) in paused) if (GodotObject.IsInstanceValid(vp)) vp.RenderTargetUpdateMode = mode;
            isolating = false;
            // The next frame's Flush restores the uniforms of the scene the window shows.
            sceneStateDirty = true;
        }
    }
    private static void ForceTransforms(Node node)
    {
        if (node is Node3D spatial) spatial.ForceUpdateTransform();
        foreach (var child in node.GetChildren(true)) ForceTransforms(child);
    }

    // ---- Directional shadows fitted per camera
    private static readonly HashSet<SCNNode> shadowLights = new();
    private static readonly Projection[] shadowBoxes = new Projection[2];
    /// <summary>The lights whose Godot specular amount carries their box index (FitShadows).</summary>
    private static readonly List<SCNNode> boxLights = new();
    private static Vector4 shadowSlope;
    internal static void RegisterShadowLight(SCNNode node, bool on) { if (on) shadowLights.Add(node); else shadowLights.Remove(node); }

    // ---- Order of transparent geometry.
    // Measured (Swift probe): SceneKit draws transparent geometry back to front by the view-space depth of its world bounding
    // box centre (a box whose centre is 13 m deep but 29.5 m to the side is drawn after one 15 m deep on the axis), ties in
    // scene-graph order. Godot sorts by the Euclidean distance from the camera to the AABB centre, so large overlays came out
    // in another order: the town's street ribbon (centre 94.6 m away, 55 m deep) was drawn over the trampled-sand overlay
    // (96.6 m away, 35 m deep), which SceneKit draws last and which hides the streets there (--dust-visibility-test town
    // view 14% too dark). Each view therefore sets every transparent instance's sorting offset to distance - depth.
    private static readonly HashSet<SCNNode> ssaoSkyNodes = new();
    internal static void RegisterSsaoSky(SCNNode node, bool on) { if (on) ssaoSkyNodes.Add(node); else ssaoSkyNodes.Remove(node); }
    /// <summary>Whether a sky drawn behind everything with a .geometry modifier is visible in this scene (SCNSsao: its
    /// pixels are at view z +1 in SceneKit's SSAO pass).</summary>
    internal static bool HasSsaoSky(SCNScene scene)
    {
        if (scene == null || ssaoSkyNodes.Count == 0) return false;
        ssaoSkyNodes.RemoveWhere(node => !GodotObject.IsInstanceValid(node));
        foreach (var node in ssaoSkyNodes)
            if (node.IsInsideTree() && !node.HiddenInHierarchy && scene.rootNode.IsAncestorOf(node)) return true;
        return false;
    }
    private static readonly HashSet<SCNNode> transparentNodes = new();
    internal static void RegisterTransparent(SCNNode node, bool on) { if (on) transparentNodes.Add(node); else transparentNodes.Remove(node); }
    private static readonly Predicate<SCNNode> Freed = node => !GodotObject.IsInstanceValid(node);
    /// <summary>Nodes whose geometry has Godot mesh LODs (SCNGeometry.godotAutomaticLevelsOfDetail): whether they get a
    /// shadow-only twin depends on SceneKitCalibration.MeshLodForCamera (SCNNode.RebuildMeshes).</summary>
    private static readonly HashSet<SCNNode> meshLodNodes = new();
    internal static void RegisterMeshLod(SCNNode node, bool on) { if (on) meshLodNodes.Add(node); else meshLodNodes.Remove(node); }
    /// <summary>
    /// Applies SceneKitCalibration's shadow configuration after it changed at run time (the game's Shadow quality setting,
    /// ShadowQualitySetting.apply; main thread): Godot's shadow filter quality now, and the mesh-LOD nodes' shadow-only
    /// twins (MeshLodForCamera) at the next flush. The splits and biases follow at every view's next camera sync
    /// (FitShadows runs on each one), so the next frame renders as a launch with that configuration would.
    /// </summary>
    internal static void ShadowConfigurationChanged()
    {
        RenderingServer.DirectionalSoftShadowFilterSetQuality((RenderingServer.ShadowQuality)SceneKitCalibration.ShadowFilterQuality);
        RenderingServer.PositionalSoftShadowFilterSetQuality((RenderingServer.ShadowQuality)SceneKitCalibration.ShadowFilterQuality);
        meshLodNodes.RemoveWhere(Freed);
        foreach (var node in meshLodNodes) node.MarkGeometryDirty();
    }
    /// <summary>
    /// Applies SceneKitCalibration's graphics-detail fields after they changed at run time (the game's Graphics detail
    /// setting, GraphicsDetailSetting.apply; main thread): every view's render scale, upscaler, MSAA and anisotropy now,
    /// Godot's glow upsampling now, the robots' small-part mesh LODs at the next flush (SCNGeometry
    /// .AutomaticLodMinTrianglesChanged) and the shading variant of every scene before it is drawn next (DetailEpoch,
    /// ObserveShading); the SSAO switch is read by each view's effect as it draws.
    /// </summary>
    internal static void GraphicsDetailChanged()
    {
        RenderingServer.EnvironmentGlowSetUseBicubicUpscale(SceneKitCalibration.GlowBicubicUpscale);
        foreach (var view in LiveViews()) view.ApplyRenderScaling();
        var detail = (DetailVariant, SceneKitCalibration.LodDistanceScale);
        if (detail != builtDetail) { builtDetail = detail; DetailEpoch++; }
        int lodMin = SceneKitCalibration.MeshLodMinTriangles;
        if (lodMinTrianglesBuilt != lodMin) SCNGeometry.AutomaticLodMinTrianglesChanged(lodMinTrianglesBuilt, lodMin);
        lodMinTrianglesBuilt = lodMin;
    }
    /// <summary>The MeshLodMinTriangles the robots' built meshes were made with (GraphicsDetailChanged).</summary>
    private static int lodMinTrianglesBuilt = SceneKitCalibration.MeshLodMinTriangles;
    /// <summary>Set whenever a Godot transform, the node hierarchy, a node's visibility or the set of transparent instances
    /// changes: the offsets depend on nothing else but the camera, so a sort for the same camera with nothing changed since
    /// (the second flush of a frame) would set the same offsets and is skipped.</summary>
    private static bool transformsChanged = true;
    private static Transform3D lastSortCamera;
    private static bool lastSortOrthographic;
    internal static void TransformsChanged() => transformsChanged = true;
    /// <summary>Stamps of Godot transform changes: a node's transformStamp is the stamp of its last transform push or
    /// reparenting (SCNNode.SortTransparent caches world-space box centres until its chain gets a newer stamp).</summary>
    private static int transformStamp;
    internal static int TransformStamp => transformStamp;
    internal static int StampTransform() { transformsChanged = true; return System.Threading.Interlocked.Increment(ref transformStamp); }
    internal static void SortTransparent(Transform3D camera, bool orthographic)
    {
        if (!transformsChanged && camera == lastSortCamera && orthographic == lastSortOrthographic) return;
        transformsChanged = false; lastSortCamera = camera; lastSortOrthographic = orthographic;
        var eye = camera.Origin; var forward = -camera.Basis.Z.Normalized();
        transparentNodes.RemoveWhere(Freed);
        // A node is in Godot's tree exactly when its scene's host viewport is (the facade mirrors the SceneKit hierarchy
        // under the scene's root node): one engine call per scene instead of one per node.
        SCNScene lastScene = null; bool sceneInTree = false;
        foreach (var node in transparentNodes)
        {
            var scene = node.sceneOwner;
            if (scene == null) continue;
            if (scene != lastScene) { lastScene = scene; sceneInTree = scene.host.IsInsideTree(); }
            if (!sceneInTree || node.HiddenInHierarchy) continue;
            node.SortTransparent(eye, forward, orthographic);
        }
    }

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
        // The composer's light() recognises a box's light by its Godot specular amount: 2 + the box index (1 for every
        // other light; SCNLight.Sync sets 1). Lights that carried an index and no longer own a box (another scene's suns
        // when this view is synced last) get 1 again, so a view never clips a light by a box that is not its own (as when
        // the boxes were matched by direction).
        var owners = new List<SCNNode>(2);
        foreach (var n in lights)
            if (!n.light.automaticallyAdjustsShadowProjection && boxCount < boxes.Length)
            {
                slopes[boxCount] = SceneKitCalibration.ShadowSlopeBiasTexels * n.light.SceneKitShadowTexel;
                n.SetShadowBoxId(2 + boxCount);
                owners.Add(n);
                boxes[boxCount++] = n.light.ShadowBox(n.RenderWorld());
            }
        foreach (var n in boxLights)
            if (!owners.Contains(n) && GodotObject.IsInstanceValid(n)) n.SetShadowBoxId(1);
        boxLights.Clear(); boxLights.AddRange(owners);
        for (int i = 0; i < boxes.Length; i++)
        {
            if (shadowBoxes[i] == boxes[i]) continue;
            shadowBoxes[i] = boxes[i];
            ShaderNames.SetGlobal(ShaderNames.Of(i == 0 ? "scn_shadow_box0" : "scn_shadow_box1"), boxes[i]);
        }
        var slope = new Vector4((float)slopes[0], (float)slopes[1], (float)SceneKitCalibration.ShadowSlopeMax, 0);
        if (shadowSlope != slope) { shadowSlope = slope; RenderingServer.GlobalShaderParameterSet("scn_shadow_slope", slope); }
        if (lights.Count == 0) return;
        int splitH = 1, splitV = 1;
        while (splitH * splitV < lights.Count) { if (splitH == splitV) splitH <<= 1; else splitV <<= 1; }
        int atlas = SceneKitCalibration.DirectionalShadowAtlas;
        double regionW = atlas / splitH, regionH = atlas / splitV, textureSize = Math.Max(regionW, regionH);
        double aspect = viewportSize.Y > 0 ? (double)viewportSize.X / viewportSize.Y : 1;
        double largestBoxMap = 0;
        foreach (var n in lights)
            if (!n.light.automaticallyAdjustsShadowProjection) largestBoxMap = Math.Max(largestBoxMap, n.light.shadowMapSize.width);
        foreach (var n in lights)
        {
            var light = n.GodotLight as DirectionalLight3D;
            double near = camera.Near, far = camera.Far;
            bool ortho = camera.Projection == Camera3D.ProjectionType.Orthogonal;
            bool keepWidth = camera.KeepAspect == Camera3D.KeepAspectEnum.Width;
            double rW = regionW, rH = regionH, tSize = textureSize, normalBiasScale = double.NaN;
            if (!n.light.automaticallyAdjustsShadowProjection && n.light.shadowCascadeCount <= 1 && !ortho && SceneKitCalibration.NearShadowSplits)
            {
                // The hard filter's near splits (SceneKitCalibration.HardShadowSplit1): fixed split distances so Godot's
                // texel snapping keeps every split but the last stable while the camera moves, the last one out to the
                // farthest point of the box in view, rounded up in steps (the composer clips every split to the box).
                double first = n.light.FixedShadowDistance;
                double tv = Math.Tan(camera.Fov * Math.PI / 360);
                double tx = keepWidth ? tv : tv * aspect, ty = keepWidth ? tv / aspect : tv;
                double cap = Math.Min(n.light.maximumShadowDistance, far);
                double need = Math.Min(n.light.ShadowBoxFarDepth(n.RenderWorld(), camera.Transform, tx, ty, near, far), cap);
                bool secondary = n.light.shadowMapSize.width < largestBoxMap;
                var (splits, ends) = NearSplitDistances(near, first, need, cap, secondary);
                double end = ends[^1];
                var mode = splits == 4 ? DirectionalLight3D.ShadowMode.Parallel4Splits
                    : splits == 2 ? DirectionalLight3D.ShadowMode.Parallel2Splits : DirectionalLight3D.ShadowMode.Orthogonal;
                if (light.DirectionalShadowMode != mode) light.DirectionalShadowMode = mode;
                if (light.DirectionalShadowMaxDistance != (float)end) light.DirectionalShadowMaxDistance = (float)end;
                if (splits > 1)
                {
                    float Ratio(int i) => (float)Math.Clamp((ends[i] - near) / (end - near), 0.001, 0.999);
                    if (light.DirectionalShadowSplit1 != Ratio(0)) light.DirectionalShadowSplit1 = Ratio(0);
                    if (splits == 4)
                    {
                        if (light.DirectionalShadowSplit2 != Ratio(1)) light.DirectionalShadowSplit2 = Ratio(1);
                        if (light.DirectionalShadowSplit3 != Ratio(2)) light.DirectionalShadowSplit3 = Ratio(2);
                    }
                    if (light.DirectionalShadowBlendSplits) light.DirectionalShadowBlendSplits = false;
                }
                // Godot halves the light's atlas region per axis for four splits, vertically for two.
                if (splits == 4) { rW /= 2; rH /= 2; } else if (splits == 2) rH /= 2;
                tSize = Math.Max(rW, rH);
                // Godot's normal bias is in texels of the split's longer side (2 x radius / tSize); scale it so its world
                // offset stays the calibrated number of the split's coarser texels (across the light), as with the
                // orthogonal fit (whose coarser side is 4096 texels per sun).
                normalBiasScale = (tSize / Math.Min(rW, rH)) / (textureSize / Math.Min(regionW, regionH));
                far = ends[0];
            }
            else if (!n.light.automaticallyAdjustsShadowProjection && n.light.shadowCascadeCount <= 1)
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
            if (double.IsNaN(normalBiasScale)) normalBiasScale = Math.Pow(tSize / textureSize, SceneKitCalibration.SplitNormalBiasExponent);
            n.light.FitShadow(light, texel, 2 * radius + light.DirectionalShadowPancakeSize, normalBiasScale);
        }
    }

    /// <summary>
    /// Split ends (metres from the camera) of a fixed-box light under the hard filter's near splits: HardShadowSplit1,
    /// HardShadowSplit2 and the calibrated fit (orthographicScale), then the farthest view depth of the box (need) rounded
    /// up to first x HardShadowFarStep^k and capped at the camera's far plane or maximumShadowDistance. Godot has one, two
    /// or four splits: three become four with a boundary halfway through the last one. A secondary light (a smaller
    /// SceneKit map than the scene's largest fixed box) gets HardShadowSecondarySplits.
    /// </summary>
    internal static (int splits, double[] ends) NearSplitDistances(double near, double first, double need, double cap, bool secondary)
    {
        double end = first;
        if (need > first * 1.05)
        {
            double step = Math.Max(1.01, SceneKitCalibration.HardShadowFarStep);
            end = first * Math.Pow(step, Math.Ceiling(Math.Log(need / first) / Math.Log(step) - 1e-9));
        }
        end = Math.Max(Math.Min(end, cap), near + 0.01);
        int count = secondary ? SceneKitCalibration.HardShadowSecondarySplits : SceneKitCalibration.HardShadowSplits;
        var wanted = count >= 4 ? new[] { SceneKitCalibration.HardShadowSplit1, SceneKitCalibration.HardShadowSplit2, first }
            : count >= 2 ? new[] { secondary && SceneKitCalibration.HardShadowSecondaryTwoSplit > 0 ? SceneKitCalibration.HardShadowSecondaryTwoSplit : SceneKitCalibration.HardShadowTwoSplit }
            : Array.Empty<double>();
        var ends = new List<double>();
        foreach (var d in wanted)
            if (d > near * 1.05 && d * 1.05 < end && (ends.Count == 0 || d > ends[^1] * 1.05)) ends.Add(d);
        if (ends.Count == 2) ends.Add((ends[1] + end) / 2);
        ends.Add(end);
        return (ends.Count, ends.ToArray());
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
    public static void flush() => SceneKitRuntime.FlushAll();
    public static void @lock() { }
    public static void unlock() { }
    public static bool disableActions { get; set; }
    public static double animationDuration { get; set; }
    public static Action completionBlock { get; set; }
}
