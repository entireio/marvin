using System.Diagnostics;

namespace Marvin.SceneKit;

/// <summary>
/// Godot-only CPU and resource counters of the facade (no SceneKit counterpart), read by the benchmark telemetry
/// (GodotFrameTelemetry, godot-render.json). Measurement only: nothing here changes what is drawn. Timers are
/// Stopwatch ticks accumulated over every Flush() of a frame (the runtime's _Process flush and the frame_pre_draw
/// flush); the telemetry reads and resets them once per benchmark tick.
/// </summary>
internal static class FrameProfile
{
    /// <summary>Stages of SceneKitRuntime.Flush in order.</summary>
    internal static readonly string[] FlushStages =
    {
        "attachAndDelegateUpdate", "tangents", "masks", "materials", "prepareMeshes", "nodes", "textures", "constraints",
        "cameras", "sceneUniforms", "delegateWillRender",
    };
    internal const int Attach = 0, Tangents = 1, Masks = 2, Materials = 3, PrepareMeshes = 4, Nodes = 5, Textures = 6,
        Constraints = 7, Cameras = 8, SceneUniforms = 9, WillRender = 10;
    internal static readonly long[] FlushTicks = new long[FlushStages.Length];
    /// <summary>Counts since the last reset: materials and nodes flushed, constraints evaluated, MTLTextures created,
    /// ImageTextures created and updated (uploads) with their bytes.</summary>
    internal static long MaterialsFlushed, NodesFlushed, ConstraintsEvaluated, MetalTexturesCreated, ImageTexturesCreated,
        ImageTextureUpdates, ImageTextureBytes;
    /// <summary>Running totals (never reset).</summary>
    internal static long TotalMetalTexturesCreated, TotalImageTexturesCreated;

    internal static long Now => Stopwatch.GetTimestamp();
    internal static void Add(int stage, long since) => FlushTicks[stage] += Stopwatch.GetTimestamp() - since;
    internal static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    internal static void Reset()
    {
        System.Array.Clear(FlushTicks);
        MaterialsFlushed = NodesFlushed = ConstraintsEvaluated = MetalTexturesCreated = ImageTexturesCreated = ImageTextureUpdates = ImageTextureBytes = 0;
    }
}
