// Godot-only: the Settings screen's "Graphics detail" row (MainMenuView), next to Shadow quality. The macOS Settings screen
// has no such control: SceneKit renders the game at 60 FPS on the Mac's GPU, and the Godot port offers lighter levels for
// GPUs that cannot (PORTING.md, "Known deviations"; docs/performance.md, "Graphics detail").
//   Max (the default): the renderer as it was before the setting.
//   High, Medium, Low: presets of SceneKitCalibration's graphics-detail fields (Preset below), chosen by measurement at
//   1920 x 1080 on an M2: High holds 60 FPS with Shadow quality Fast at full resolution (no SSAO and four small savings),
//   Medium with Exact (High's savings, the 3D view at 0.6 of the resolution with temporal upscaling: MetalFX temporal on
//   macOS, FSR 2 on Windows), Low leaves a large margin (half resolution and further savings). The HUD and menus are not
//   part of the 3D view and stay at full resolution.
// Shadow quality (ShadowQualitySetting) is a separate, independent setting: no level changes the shadow configuration.
// The choice is stored with the other settings (UserDefaults, user://UserDefaults.cfg) and applies at once, in the menu
// and in a race or sandbox entered afterwards (GraphicsDetailSetting.apply). The game launched without a game-mode flag
// starts with the stored choice (Main.cs); game modes (smoke tests, benchmarks, probes, the playthrough) keep Max.
// MARVIN_GRAPHICS_DETAIL=Low|Medium|High|Max selects a level for any launch instead (tests and measurements), and
// MARVIN_SCN_CAL entries that name a graphics-detail field override the level's value for that field (experiments).
using System;

namespace Marvin;

public static class GraphicsDetailSetting
{
    public enum Level { Low = 0, Medium = 1, High = 2, Max = 3 }

    /// <summary>The UserDefaults key: "Low", "Medium", "High" or "Max" (absent or unknown: Max).</summary>
    public const string key = "graphicsDetail";

    /// <summary>The stored choice (Max by default).</summary>
    public static Level stored
    {
        get => Parse(UserDefaults.standard.@string(key)) ?? Level.Max;
        set => UserDefaults.standard.set(value.ToString(), key);
    }

    /// <summary>The level MARVIN_GRAPHICS_DETAIL selects, if any (tests and measurements; it overrides the stored choice).</summary>
    public static Level? environment => Parse(System.Environment.GetEnvironmentVariable("MARVIN_GRAPHICS_DETAIL"));

    /// <summary>The level in use (Max until apply ran).</summary>
    public static Level current { get; private set; } = Level.Max;

    /// <summary>The next level when the Settings row is activated: Max, High, Medium, Low, Max ...</summary>
    public static Level Next(Level level) => level == Level.Low ? Level.Max : (Level)((int)level - 1);

    public static Level? Parse(string text) => Enum.TryParse<Level>(text?.Trim() ?? "", true, out var level) && Enum.IsDefined(level) ? level : null;

    /// <summary>
    /// The graphics-detail fields of each level (the field defaults are Max). Chosen by measurement at 1920 x 1080 on an M2
    /// (docs/performance.md, "Graphics detail"): High holds 60 FPS with Shadow quality Fast with the smallest look change
    /// found, Medium with Exact, Low leaves a large margin for weaker GPUs. Each level contains the one above it.
    /// </summary>
    public static void Preset(Level level)
    {
        // Max: the renderer as before.
        SceneKitCalibration.RenderScale = 1.0;
        SceneKitCalibration.RenderUpscaler = 0;
        SceneKitCalibration.AnisotropicFiltering = -1;
        SceneKitCalibration.SsaoEnabled = true;
        SceneKitCalibration.GlowBicubicUpscale = true;
        SceneKitCalibration.MeshLodMinTriangles = 2048;
        SceneKitCalibration.SimpleSkyReflection = false;
        SceneKitCalibration.SimpleGroundLighting = false;
        SceneKitCalibration.LodDistanceScale = 1.0;
        SceneKitCalibration.MeshLodThreshold = 0.5;
        if (level == Level.Max) return;
        // High, and every level below it: full resolution, without SceneKit's SSAO (it darkens the robots and the track
        // walls; the town's .geometry-modified materials receive almost none of it), the robots' small parts with mesh
        // LODs (MeshLodMinTriangles 256), one bilinear tap per glow level, the sky reflection of everything but the robots
        // from the sky light's spherical harmonics, 2x instead of 4x anisotropic filtering.
        SceneKitCalibration.SsaoEnabled = false;
        SceneKitCalibration.MeshLodMinTriangles = 256;
        SceneKitCalibration.GlowBicubicUpscale = false;
        SceneKitCalibration.SimpleSkyReflection = true;
        SceneKitCalibration.AnisotropicFiltering = 1;
        if (level == Level.High) return;
        // Medium, and Low: the 3D view at 0.6 of the drawable's width and height with temporal upscaling (MetalFX
        // temporal or FSR 2; MSAA off), the ground's overlay layers lit with diffuse light only.
        SceneKitCalibration.RenderScale = 0.6;
        SceneKitCalibration.SimpleGroundLighting = true;
        if (level == Level.Medium) return;
        // Low: half the drawable's width and height, no anisotropic filtering, SceneKit's levels of detail switching at
        // 0.6 of their distances, the robots' mesh LODs at up to 2 pixels of error.
        SceneKitCalibration.RenderScale = 0.5;
        SceneKitCalibration.AnisotropicFiltering = 0;
        SceneKitCalibration.LodDistanceScale = 0.6;
        SceneKitCalibration.MeshLodThreshold = 2;
    }

    /// <summary>
    /// Applies a level (main thread): its preset, then MARVIN_SCN_CAL's entries for graphics-detail fields, then the
    /// facade's run-time changes (SceneKitRuntime.GraphicsDetailChanged: the views' render scale and upscaler, the glow,
    /// the robots' small-part mesh LODs). Without an argument: MARVIN_GRAPHICS_DETAIL when set, else the stored choice
    /// (the game launched normally; game modes call nothing and keep Max unless MARVIN_GRAPHICS_DETAIL is set, see Main.cs).
    /// </summary>
    public static void apply(Level? level = null)
    {
        var chosen = level ?? environment ?? stored;
        if (environment is Level forced) chosen = forced;
        Preset(chosen);
        SceneKitCalibration.ReapplyOverrides(SceneKitCalibration.GraphicsDetailFields);
        current = chosen;
        SceneKitRuntime.GraphicsDetailChanged();
    }

    /// <summary>Whether the graphics-detail fields in use are the ones <paramref name="level"/> selects (with MARVIN_SCN_CAL's
    /// overrides; always true while MARVIN_GRAPHICS_DETAIL chooses the level).</summary>
    public static bool applied(Level level)
    {
        if (environment is Level) return true;
        var fields = System.Array.ConvertAll(SceneKitCalibration.GraphicsDetailFields, n => typeof(SceneKitCalibration).GetField(n));
        var saved = System.Array.ConvertAll(fields, f => f.GetValue(null));
        var now = SceneKitCalibration.GraphicsDetailSignature();
        Preset(level);
        SceneKitCalibration.ReapplyOverrides(SceneKitCalibration.GraphicsDetailFields);
        var expected = SceneKitCalibration.GraphicsDetailSignature();
        for (int i = 0; i < fields.Length; i++) fields[i].SetValue(null, saved[i]);
        return current == level && now == expected;
    }
}
