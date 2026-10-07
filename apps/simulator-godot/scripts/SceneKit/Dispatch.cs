using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace Marvin.SceneKit;

/// <summary>DispatchQoS.QoSClass (only used to pick the global queue; all map to the .NET thread pool).</summary>
public enum DispatchQoS { background, utility, @default, userInitiated, userInteractive, unspecified }

/// <summary>DispatchTime: <c>.now() + seconds</c>.</summary>
public readonly struct DispatchTime
{
    internal readonly double seconds;
    private DispatchTime(double seconds) { this.seconds = seconds; }
    public static DispatchTime now() => new(Time.GetTicksUsec() / 1e6);
    public static DispatchTime operator +(DispatchTime t, double seconds) => new(t.seconds + seconds);
}

/// <summary>
/// The Dispatch calls the game uses. <c>DispatchQueue.main.async</c> runs the block on Godot's main
/// thread before the facade's next flush (from SceneKitRuntime._Process, i.e. once per frame, in order);
/// <c>DispatchQueue.global(qos:).async</c> runs it on the .NET thread pool. Objects built on a global
/// queue follow the facade's threading rules (see SceneKitRuntime: built off the tree, adopted when the
/// main thread shows the scene or attaches the subtree).
/// </summary>
public sealed class DispatchQueue
{
    private readonly bool isMain;
    private DispatchQueue(bool isMain) { this.isMain = isMain; }
    public static readonly DispatchQueue main = new(true);
    private static readonly DispatchQueue globalQueue = new(false);
    public static DispatchQueue global(DispatchQoS qos = DispatchQoS.@default) => globalQueue;

    private static readonly ConcurrentQueue<Action> mainBlocks = new();
    private static readonly List<(double at, long order, Action block)> timed = new();
    private static long timedOrder;

    public void async(Action execute)
    {
        if (isMain) { SceneKitRuntime.EnsureStartedFromAnyThread(); mainBlocks.Enqueue(execute); }
        else Task.Run(() => RunGlobal(execute));
    }
    public void asyncAfter(DispatchTime deadline, Action execute)
    {
        if (!isMain) { Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, deadline.seconds - DispatchTime.now().seconds))); RunGlobal(execute); }); return; }
        SceneKitRuntime.EnsureStartedFromAnyThread();
        lock (timed) timed.Add((deadline.seconds, timedOrder++, execute));
    }

    // Blocks of the global queues that are running (Godot-only): the world builds create Godot objects, which must not
    // happen while Godot shuts down. SceneKitRuntime.Shutdown closes the global queues (blocks that have not started yet
    // never run) and waits for the running ones.
    private static readonly object globalGate = new();
    private static int globalBlocks;
    private static bool globalClosed;
    private static void RunGlobal(Action block)
    {
        lock (globalGate) { if (globalClosed) return; globalBlocks++; }
        try { Run(block); }
        finally { lock (globalGate) { if (--globalBlocks == 0) System.Threading.Monitor.PulseAll(globalGate); } }
    }
    /// <summary>Main thread, when Godot quits: closes the global queues and waits until no block of theirs is running, at
    /// most <paramref name="timeout"/>; false when some still are. Blocks they queue on the main queue are not run.</summary>
    internal static bool WaitForGlobalBlocks(TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        lock (globalGate)
        {
            globalClosed = true;
            while (globalBlocks > 0)
            {
                var left = end - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !System.Threading.Monitor.Wait(globalGate, left)) return globalBlocks == 0;
            }
            return true;
        }
    }

    /// <summary>Main thread, once per frame: runs the blocks queued for the main queue.</summary>
    internal static void Drain()
    {
        // Blocks enqueued while draining run next frame (like a run loop turn).
        for (int n = mainBlocks.Count; n > 0 && mainBlocks.TryDequeue(out var block); n--) Run(block);
        List<Action> due = null;
        lock (timed)
        {
            if (timed.Count == 0) return;
            double now = DispatchTime.now().seconds;
            timed.Sort((a, b) => a.at != b.at ? a.at.CompareTo(b.at) : a.order.CompareTo(b.order));
            int i = 0;
            while (i < timed.Count && timed[i].at <= now) (due ??= new()).Add(timed[i++].block);
            timed.RemoveRange(0, i);
        }
        if (due != null) foreach (var block in due) Run(block);
    }

    private static void Run(Action block)
    {
        try { block(); }
        catch (Exception e) { GD.PushError($"Dispatch block failed: {e}"); }
    }
}
