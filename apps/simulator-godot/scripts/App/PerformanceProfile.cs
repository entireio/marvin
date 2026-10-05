// Godot-only performance diagnostics (no macOS counterpart).
//
// `--world-build-profile DIR`: builds the race world (DirtWorld, as LevelLoading does on a global queue) on the main
// thread and records the wall-clock time of each loading stage (the progress labels), garbage collections and the
// first frames after the world is shown (Godot compiles shaders and uploads meshes on first use). Writes
// world-build-profile.json.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace Marvin;

/// <summary>
/// Godot-only render telemetry for --town-benchmark (godot-render.json next to the macOS files): per second, the 3D
/// view's measured GPU and CPU render time (RenderingServer viewport timestamps), the frame's draw calls, objects and
/// primitives, Godot's process time and the node count. SceneKit has no equivalent API (the macOS evidence used the
/// Metal HUD and Xcode GPU traces).
/// </summary>
public sealed class GodotFrameTelemetry
{
    private readonly SubViewport viewport;
    private readonly List<Dictionary<string, object>> seconds = new();
    private readonly List<double> gpu = new(), cpu = new(), process = new(), flush = new(), dispatch = new(), meshes = new();
    private readonly List<double> secondGpu = new(), secondCpu = new(), secondProcess = new(), secondFlush = new();
    private long drawCalls, objects, primitives, shadowDrawCalls, shadowPrimitives, visibleDrawCalls, visiblePrimitives; private int frames, current = -1;
    // Facade CPU per Flush stage (FrameProfile), per second and over the run, and the resource counters.
    private readonly double[] secondStages = new double[FrameProfile.FlushStages.Length], runStages = new double[FrameProfile.FlushStages.Length];
    private readonly double[] secondStageBytes = new double[FrameProfile.FlushStages.Length], runStageBytes = new double[FrameProfile.FlushStages.Length];
    // Allocation: main thread per frame (all of it: game tick, facade, Godot callbacks) and the whole process.
    private long secondTickBytes, runTickBytes;
    private long lastThreadBytes = -1, lastProcessBytes = -1, secondThreadBytes, secondProcessBytes, runThreadBytes, runProcessBytes;
    private readonly List<double> setup = new(), secondSetup = new();
    private long secondMaterials, secondNodes, secondConstraints, secondMetalTextures, secondImageCreated, secondImageUpdates, secondImageBytes;
    private int runFrames;
    private int gc0, gc1, gc2; private TimeSpan gcPause;
    public GodotFrameTelemetry(SubViewport viewport)
    {
        this.viewport = viewport;
        RenderingServer.ViewportSetMeasureRenderTime(viewport.GetViewportRid(), true);
        ResetGc();
    }
    private void ResetGc()
    {
        gc0 = GC.CollectionCount(0); gc1 = GC.CollectionCount(1); gc2 = GC.CollectionCount(2); gcPause = GC.GetTotalPauseDuration();
    }
    static double Percentile(List<double> values, double p) { if (values.Count == 0) return 0; var s = values.OrderBy(v => v).ToList(); return s[Math.Min(s.Count - 1, (int)((s.Count - 1) * p))]; }
    private void Close()
    {
        if (current < 0 || frames == 0) return;
        seconds.Add(new Dictionary<string, object>
        {
            ["second"] = current, ["frames"] = frames,
            ["gpuMS"] = new Dictionary<string, object> { ["mean"] = secondGpu.Average(), ["max"] = secondGpu.Max() },
            ["cpuRenderMS"] = new Dictionary<string, object> { ["mean"] = secondCpu.Average(), ["max"] = secondCpu.Max() },
            ["processMS"] = new Dictionary<string, object> { ["mean"] = secondProcess.Average(), ["max"] = secondProcess.Max() },
            ["flushMS"] = new Dictionary<string, object> { ["mean"] = secondFlush.Average(), ["max"] = secondFlush.Max() },
            ["drawCalls"] = drawCalls / frames, ["objects"] = objects / frames, ["primitives"] = primitives / frames,
            ["view"] = new Dictionary<string, object> { ["visibleDrawCalls"] = visibleDrawCalls / frames, ["visiblePrimitives"] = visiblePrimitives / frames, ["shadowDrawCalls"] = shadowDrawCalls / frames, ["shadowPrimitives"] = shadowPrimitives / frames },
            ["nodes"] = (long)Performance.GetMonitor(Performance.Monitor.ObjectNodeCount),
            ["videoMemoryMB"] = Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576.0,
            // Memory and object counts at the end of the second (Godot monitors, .NET GC, process working set).
            ["memory"] = new Dictionary<string, object>
            {
                ["objects"] = (long)Performance.GetMonitor(Performance.Monitor.ObjectCount),
                ["resources"] = (long)Performance.GetMonitor(Performance.Monitor.ObjectResourceCount),
                ["orphanNodes"] = (long)Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount),
                ["textureMB"] = Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed) / 1048576.0,
                ["bufferMB"] = Performance.GetMonitor(Performance.Monitor.RenderBufferMemUsed) / 1048576.0,
                ["staticMB"] = Performance.GetMonitor(Performance.Monitor.MemoryStatic) / 1048576.0,
                ["managedHeapMB"] = GC.GetTotalMemory(false) / 1048576.0,
                ["workingSetMB"] = System.Environment.WorkingSet / 1048576.0,
                ["gcCollections"] = new[] { GC.CollectionCount(0) - gc0, GC.CollectionCount(1) - gc1, GC.CollectionCount(2) - gc2 },
                ["gcPauseMS"] = (GC.GetTotalPauseDuration() - gcPause).TotalMilliseconds,
                ["metalTexturesTotal"] = FrameProfile.TotalMetalTexturesCreated, ["imageTexturesTotal"] = FrameProfile.TotalImageTexturesCreated,
            },
            // Facade CPU per frame (mean ms per Flush stage) and what it flushed per second.
            ["flushStagesMS"] = Enumerable.Range(0, secondStages.Length).ToDictionary(i => FrameProfile.FlushStages[i], i => (object)(secondStages[i] / frames)),
            ["allocationKBPerFrame"] = new Dictionary<string, object>
            {
                ["mainThread"] = secondThreadBytes / 1024.0 / frames, ["process"] = secondProcessBytes / 1024.0 / frames, ["benchmarkTick"] = secondTickBytes / 1024.0 / frames,
                ["tickPhases"] = Enumerable.Range(0, 6).ToDictionary(i => TickPhases[i], i => (object)(secondPhaseBytes[i] / 1024.0 / frames)),
                ["flushStages"] = Enumerable.Range(0, secondStageBytes.Length).ToDictionary(i => FrameProfile.FlushStages[i], i => (object)(secondStageBytes[i] / 1024.0 / frames)),
            },
            ["frameSetupCpuMS"] = secondSetup.Count == 0 ? 0 : secondSetup.Average(),
            ["flushed"] = new Dictionary<string, object>
            {
                ["materials"] = secondMaterials, ["nodes"] = secondNodes, ["constraintsPerFrame"] = (double)secondConstraints / frames,
                ["metalTexturesCreated"] = secondMetalTextures, ["imageTexturesCreated"] = secondImageCreated,
                ["imageTextureUpdates"] = secondImageUpdates, ["imageTextureMB"] = secondImageBytes / 1048576.0,
            },
        });
        secondGpu.Clear(); secondCpu.Clear(); secondProcess.Clear(); secondFlush.Clear(); drawCalls = objects = primitives = 0; shadowDrawCalls = shadowPrimitives = visibleDrawCalls = visiblePrimitives = 0; frames = 0;
        Array.Clear(secondStages); Array.Clear(secondStageBytes); secondThreadBytes = secondProcessBytes = secondTickBytes = 0; Array.Clear(secondPhaseBytes); secondSetup.Clear(); ResetGc();
        secondMaterials = secondNodes = secondConstraints = secondMetalTextures = secondImageCreated = secondImageUpdates = secondImageBytes = 0;
    }
    /// <summary>Takes the facade counters of the frame just processed (FrameProfile) and resets them.</summary>
    private void TakeProfile(bool keep)
    {
        if (keep)
        {
            for (int i = 0; i < secondStages.Length; i++) { double ms = FrameProfile.Milliseconds(FrameProfile.FlushTicks[i]); secondStages[i] += ms; runStages[i] += ms; }
            for (int i = 0; i < secondStageBytes.Length; i++) { secondStageBytes[i] += FrameProfile.FlushBytes[i]; runStageBytes[i] += FrameProfile.FlushBytes[i]; }
            long threadBytes = GC.GetAllocatedBytesForCurrentThread(), processBytes = GC.GetTotalAllocatedBytes(false);
            if (lastThreadBytes >= 0) { secondThreadBytes += threadBytes - lastThreadBytes; runThreadBytes += threadBytes - lastThreadBytes; secondProcessBytes += processBytes - lastProcessBytes; runProcessBytes += processBytes - lastProcessBytes; }
            lastThreadBytes = threadBytes; lastProcessBytes = processBytes;
            secondMaterials += FrameProfile.MaterialsFlushed; secondNodes += FrameProfile.NodesFlushed; secondConstraints += FrameProfile.ConstraintsEvaluated;
            secondMetalTextures += FrameProfile.MetalTexturesCreated; secondImageCreated += FrameProfile.ImageTexturesCreated;
            secondImageUpdates += FrameProfile.ImageTextureUpdates; secondImageBytes += FrameProfile.ImageTextureBytes;
            runFrames++;
        }
        else { lastThreadBytes = lastProcessBytes = -1; }
        FrameProfile.Reset();
    }
    /// <summary>Called once per tick with the benchmark's elapsed time (samples taken during the 3 s warm-up are dropped).</summary>
    /// <summary>Tick phases of tickTownBenchmark whose allocations the benchmark reports (timeline update columns + audio).</summary>
    public static readonly string[] TickPhases = { "physics", "models", "effects", "camera", "town", "audio" };
    private readonly double[] secondPhaseBytes = new double[6], runPhaseBytes = new double[6];
    public void sample(double elapsed, long[] tickPhaseBytes = null)
    {
        long tickBytes = tickPhaseBytes?.Sum() ?? 0;
        if (elapsed < 3) { gpu.Clear(); cpu.Clear(); process.Clear(); flush.Clear(); dispatch.Clear(); meshes.Clear(); secondFlush.Clear(); seconds.Clear(); current = -1; frames = 0; secondGpu.Clear(); secondCpu.Clear(); secondProcess.Clear(); drawCalls = objects = primitives = 0; TakeProfile(false); Array.Clear(runStages); Array.Clear(runStageBytes); runThreadBytes = runProcessBytes = runTickBytes = 0; Array.Clear(runPhaseBytes); runFrames = 0; setup.Clear(); return; }
        int second = (int)elapsed;
        if (second != current) { Close(); current = second; }
        TakeProfile(true);
        secondTickBytes += tickBytes; runTickBytes += tickBytes;
        if (tickPhaseBytes != null) for (int i = 0; i < 6; i++) { secondPhaseBytes[i] += tickPhaseBytes[i]; runPhaseBytes[i] += tickPhaseBytes[i]; }
        double frameSetup = RenderingServer.GetFrameSetupTimeCpu(); setup.Add(frameSetup); secondSetup.Add(frameSetup);
        var rid = viewport.GetViewportRid();
        double g = RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid), c = RenderingServer.ViewportGetMeasuredRenderTimeCpu(rid);
        double p = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000;
        gpu.Add(g); cpu.Add(c); process.Add(p); secondGpu.Add(g); secondCpu.Add(c); secondProcess.Add(p);
        // The previous frame's facade flush (the tick runs before this frame's flush).
        flush.Add(SceneKitRuntime.LastFlushMS); secondFlush.Add(SceneKitRuntime.LastFlushMS);
        dispatch.Add(SceneKitRuntime.LastDispatchMS); meshes.Add(SceneKitRuntime.LastMeshesBuilt);
        drawCalls += (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame);
        objects += (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalObjectsInFrame);
        primitives += (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalPrimitivesInFrame);
        visibleDrawCalls += RenderingServer.ViewportGetRenderInfo(rid, RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.DrawCallsInFrame);
        visiblePrimitives += RenderingServer.ViewportGetRenderInfo(rid, RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.PrimitivesInFrame);
        shadowDrawCalls += RenderingServer.ViewportGetRenderInfo(rid, RenderingServer.ViewportRenderInfoType.Shadow, RenderingServer.ViewportRenderInfo.DrawCallsInFrame);
        shadowPrimitives += RenderingServer.ViewportGetRenderInfo(rid, RenderingServer.ViewportRenderInfoType.Shadow, RenderingServer.ViewportRenderInfo.PrimitivesInFrame);
        frames += 1;
    }
    public Dictionary<string, object> report()
    {
        Close();
        Dictionary<string, object> stats(List<double> v) => new() { ["p50"] = Percentile(v, 0.5), ["p95"] = Percentile(v, 0.95), ["p99"] = Percentile(v, 0.99), ["max"] = v.Count == 0 ? 0 : v.Max(), ["mean"] = v.Count == 0 ? 0 : v.Average() };
        return new Dictionary<string, object>
        {
            ["metric"] = "Godot RenderingServer viewport render-time measurement of the 3D view (GPU timestamps and CPU submission) per tick; process time of Godot's frame",
            ["gpuMS"] = stats(gpu), ["cpuRenderMS"] = stats(cpu), ["processMS"] = stats(process), ["seconds"] = seconds,
            ["flushMS"] = stats(flush), ["dispatchMS"] = stats(dispatch), ["meshesBuiltPerFrame"] = stats(meshes),
            ["flushStagesMS"] = Enumerable.Range(0, runStages.Length).ToDictionary(i => FrameProfile.FlushStages[i], i => (object)(runFrames == 0 ? 0 : runStages[i] / runFrames)),
            ["frameSetupCpuMS"] = stats(setup),
            ["allocationKBPerFrame"] = new Dictionary<string, object>
            {
                ["mainThread"] = runFrames == 0 ? 0 : runThreadBytes / 1024.0 / runFrames, ["process"] = runFrames == 0 ? 0 : runProcessBytes / 1024.0 / runFrames,
                ["benchmarkTick"] = runFrames == 0 ? 0 : runTickBytes / 1024.0 / runFrames,
                ["tickPhases"] = Enumerable.Range(0, 6).ToDictionary(i => TickPhases[i], i => (object)(runFrames == 0 ? 0 : runPhaseBytes[i] / 1024.0 / runFrames)),
                ["flushStages"] = Enumerable.Range(0, runStageBytes.Length).ToDictionary(i => FrameProfile.FlushStages[i], i => (object)(runFrames == 0 ? 0 : runStageBytes[i] / 1024.0 / runFrames)),
            },
        };
    }
}

public partial class AppController
{
    [GameMode("--world-build-profile")]
    public static async Task RunWorldBuildProfile(string dir, SceneTree tree)
    {
        var app = await launchSmoke(tree, dir);
        // MARVIN_PROFILE_DELAY=s: idle before and after the build, so a sampling profiler (dotnet-trace collect -p PID)
        // can attach first and stop (with its method rundown) while the process still runs.
        double delay = double.TryParse(System.Environment.GetEnvironmentVariable("MARVIN_PROFILE_DELAY"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
        if (delay > 0) { GD.Print($"PID {OS.GetProcessId()}: waiting {delay} s"); await tree.ToSignal(tree.CreateTimer(delay), SceneTreeTimer.SignalName.Timeout); }
        var stages = new List<Dictionary<string, object>>();
        var watch = Stopwatch.StartNew();
        double last = 0; string label = "Starting"; double fraction = 0;
        int gc0 = System.GC.CollectionCount(0), gc2 = System.GC.CollectionCount(2);
        var pause0 = System.GC.GetTotalPauseDuration();
        void record(double f, string next)
        {
            double now = watch.Elapsed.TotalMilliseconds;
            if (next == label && stages.Count > 0) { fraction = f; return; }
            stages.Add(new Dictionary<string, object> { ["label"] = label, ["fromFraction"] = fraction, ["ms"] = now - last });
            last = now; label = next; fraction = f;
        }
        var world = new DirtWorld(progress: (f, l) => record(f, l));
        record(1, "done");
        double buildMs = watch.Elapsed.TotalMilliseconds;
        var gc = new Dictionary<string, object>
        {
            ["gen0"] = System.GC.CollectionCount(0) - gc0, ["gen2"] = System.GC.CollectionCount(2) - gc2,
            ["pauseMs"] = (System.GC.GetTotalPauseDuration() - pause0).TotalMilliseconds,
            ["heapMB"] = System.GC.GetTotalMemory(false) / 1048576.0,
        };
        app.cachedDirtWorld = world;
        // What startDirtTrack spends its time on (the loading screen's freeze at 93 %): the facade's Flush stages (view.prepare
        // and the snapshot's flush), SCNView.snapshot (revealDirtTrack: first draw of the race world, read-back, PNG), GC,
        // and the rest (game code and Godot calls outside the flush).
        FrameProfile.Reset(); FrameProfile.ResetSnapshots();
        int startGc0 = System.GC.CollectionCount(0), startGc1 = System.GC.CollectionCount(1), startGc2 = System.GC.CollectionCount(2);
        var startPause = System.GC.GetTotalPauseDuration();
        var start = Stopwatch.StartNew();
        app.startDirtTrack();
        double startMs = start.Elapsed.TotalMilliseconds;
        var startBreakdown = new Dictionary<string, object>
        {
            ["flushStagesMs"] = Enumerable.Range(0, FrameProfile.FlushStages.Length).ToDictionary(i => FrameProfile.FlushStages[i], i => (object)FrameProfile.Milliseconds(FrameProfile.FlushTicks[i])),
            ["nodesFlushed"] = FrameProfile.NodesFlushed, ["materialsFlushed"] = FrameProfile.MaterialsFlushed,
            ["snapshots"] = FrameProfile.Snapshots, ["snapshotRenderMs"] = FrameProfile.Milliseconds(FrameProfile.SnapshotRenderTicks),
            ["snapshotReadbackMs"] = FrameProfile.Milliseconds(FrameProfile.SnapshotReadbackTicks), ["snapshotPngMs"] = FrameProfile.Milliseconds(FrameProfile.SnapshotEncodeTicks),
            ["gc"] = new[] { System.GC.CollectionCount(0) - startGc0, System.GC.CollectionCount(1) - startGc1, System.GC.CollectionCount(2) - startGc2 },
            ["gcPauseMs"] = (System.GC.GetTotalPauseDuration() - startPause).TotalMilliseconds,
        };
        var frames = new List<double>();
        var frameFlush = new List<double>();
        app.timer = new Marvin.SceneKit.Timer(1.0 / 60);
        var frameWatch = Stopwatch.StartNew();
        FrameProfile.Reset();
        for (int i = 0; i < 60; i++)
        {
            await frame(tree);
            frames.Add(frameWatch.Elapsed.TotalMilliseconds); frameWatch.Restart();
            frameFlush.Add(Enumerable.Range(0, FrameProfile.FlushStages.Length).Sum(k => FrameProfile.Milliseconds(FrameProfile.FlushTicks[k]))); FrameProfile.Reset();
        }
        var report = new Dictionary<string, object>
        {
            ["stages"] = stages, ["buildMs"] = buildMs, ["gc"] = gc, ["startDirtTrackMs"] = startMs, ["firstFramesMs"] = frames,
            ["startDirtTrackBreakdown"] = startBreakdown, ["firstFramesFlushMs"] = frameFlush,
            ["engine"] = app.benchmarkEngineReport(),
        };
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "world-build-profile.json"), JSONSerialization.prettyPrintedSortedKeys(report));
        foreach (var s in stages) GD.Print($"{s["ms"],9:F1} ms  {s["label"]}");
        GD.Print($"build {buildMs:F0} ms, startDirtTrack {startMs:F0} ms, first frames {string.Join(" ", frames.Take(8).Select(f => f.ToString("F0")))} ms");
        GD.Print("startDirtTrack breakdown: " + JSONSerialization.prettyPrintedSortedKeys(startBreakdown));
        if (delay > 0) { await tree.ToSignal(tree.CreateTimer(delay), SceneTreeTimer.SignalName.Timeout); }
        Foundation.exit(0);
    }
}
