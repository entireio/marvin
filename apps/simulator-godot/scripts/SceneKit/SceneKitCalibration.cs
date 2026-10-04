using System;
using System.Globalization;
using System.Reflection;

namespace Marvin.SceneKit;

/// <summary>
/// Every number that maps SceneKit's look onto Godot, in one place. Values were measured against
/// SceneKit renders of the same scenes (PORTING.md, "Calibration"): 64 px probes from
/// tools/scenekit-reference/FacadeReference.swift and full 1280x820 game-like scenes rendered by
/// `tools/godot -- --calibration DIR` and their SceneKit twins.
///
/// For experiments every field can be overridden without rebuilding:
///   MARVIN_SCN_CAL="SsaoIntensityScale=1.5;ShadowBlurPerRadius=0.4" tools/godot -- --calibration DIR
/// (MARVIN_BLOOM="scale,hdrScale,levelOffset,levelWeight" is still honoured for the bloom fields.)
/// </summary>
internal static class SceneKitCalibration
{
    // ---- Lights (SceneKit lumens -> Godot energy). Measured: directional 1000 = irradiance 1.0 (Lambert, no 1/pi).
    /// <summary>Godot Light3D.LightEnergy per SceneKit lumen.</summary>
    public static double LightEnergyPerLumen = 1.0 / 1000.0;
    /// <summary>SceneKit ambient light 1000 adds albedo x 1.0 (every lit model, independent of metalness).</summary>
    public static double AmbientPerLumen = 1.0 / 1000.0;

    // ---- Tone mapping. SceneKit HDR with fixed exposure (wantsExposureAdaptation = false) is a linear clamp:
    // emission k renders k up to 1.0, also in the game-like scenes (sky, suns, emissive patches).
    public static double TonemapExposure = 1.0;
    public static double TonemapWhite = 1.0;

    // ---- Bloom (SceneKit bloom -> Godot glow, additive). Levels: round(log2(bloomBlurRadius) + BloomLevelOffset - log2(height/400)).
    public static double BloomIntensityScale = 0.15, BloomHdrScale = 1.0, BloomLevelOffset = -0.6, BloomLevelWeight = 1.0, BloomHeightScaling = 0.0;

    // ---- Screen-space ambient occlusion (SceneKit -> Godot SSAO; ambient and IBL only).
    public static double SsaoRadiusScale = 1.0, SsaoIntensityScale = 4.0, SsaoPower = 1.5, SsaoDetail = 0.5, SsaoHorizon = 0.06, SsaoSharpness = 0.98;

    // ---- Shadows.
    // SceneKit's shadowBias has no measurable effect (sphere self-shadowing identical for bias 0.001, 1 and 10, forward
    // and deferred), so Godot's depth bias is a fixed number of Godot shadow texels in world units, recomputed per camera
    // (SceneKitRuntime.FitShadows), and the normal bias (texel-scaled by Godot) removes acne.
    /// <summary>Static fallbacks before the first camera fit: ShadowBias = max(ShadowBiasMin, ShadowBiasPerUnit x shadowBias).</summary>
    public static double ShadowBiasPerUnit = 0.1, ShadowBiasMin = 0.02;
    /// <summary>World depth bias per camera fit, in Godot shadow texels.</summary>
    public static double ShadowBiasTexels = 1.0, ShadowBiasPerKernel = 0.6;
    /// <summary>Godot ShadowNormalBias (Godot's default is 2.0; texel-scaled by Godot).</summary>
    public static double ShadowNormalBias = 2.0, ShadowNormalBiasPerKernel = 0.8;
    /// <summary>PCF kernel: Godot blur x quality radius x Godot texel = ShadowKernelScale x shadowRadius x SceneKit texel (world).
    /// Static fallback before the first fit: blur = shadowRadius x ShadowBlurPerRadius.</summary>
    public static double ShadowKernelScale = 0.88, ShadowBlurPerRadius = 1.0 / 3.0, ShadowBlurMin = 0.25, ShadowBlurMax = 16.0;
    /// <summary>SceneKit's shadow map size when shadowMapSize is zero.</summary>
    public static double DefaultShadowMapSize = 2048;
    /// <summary>Directional shadow atlas (one atlas shared by all directional lights) and its filter quality (0 hard .. 5 ultra).</summary>
    public static int DirectionalShadowAtlas = 8192, ShadowFilterQuality = 4;
    /// <summary>Godot's PCF radius multiplier for ShadowFilterQuality (RendererSceneRenderRD::directional_soft_shadow_filter_set_quality).</summary>
    internal static double ShadowQualityRadius => ShadowFilterQuality switch { 0 => 1.0, 1 => 1.5, 2 => 2.0, 3 => 2.0, 4 => 3.0, _ => 4.0 };

    /// <summary>
    /// SceneKit deferred shadows with a large shadowRadius darken lit curved and sloped surfaces through the kernel's
    /// self-shadowing. Measured on spheres (exp. independent of sphere size, map size and orthographicScale): no effect up to
    /// an onset angle from the light, then rising to a plateau, s = plateau x sqrt((angle - onset) / 28 deg).
    /// shadowRadius 3: none; 8: plateau 0.28 from 37 deg; 16 and 32: plateau 0.44 from 27 deg.
    /// </summary>
    internal static (double plateau, double onsetDegrees) DeferredSelfShadow(double shadowRadius)
    {
        double p = DeferredSelfShadowPlateau * Math.Pow(Math.Clamp((shadowRadius - 3) / 10, 0, 1), 0.65);
        double onset = 27 + 10 * Math.Clamp((16 - shadowRadius) / 8, 0, 1);
        return (p, onset);
    }
    public static double DeferredSelfShadowPlateau = 0.44;

    // ---- Image-based lighting (lightingEnvironment), as polynomials in roughness r (fitted to SceneKit).
    public static double IblDiffuse1 = -0.1757, IblDiffuse2 = 0.3455, IblDiffuse3 = -0.3623;
    public static double IblSpecular0 = 0.9358, IblSpecular1 = 1.1768, IblSpecular2 = -2.5492, IblSpecular3 = 0.7755;
    /// <summary>Share of sky specular that SSAO occludes: smoothstep(From, To, roughness) (SceneKit: roughness 0.1 ~none, 0.3+ full).</summary>
    public static double SpecularOcclusionFrom = 0.08, SpecularOcclusionTo = 0.3;
    /// <summary>Pre-filtered radiance band used for roughness r: clamp(IblBlurScale x r^IblBlurPower, 0, 1) (bands are GGX alpha = band^2).</summary>
    public static double IblBlurScale = 1.0, IblBlurPower = 1.0;

    internal static string Poly(double c1, double c2, double c3, double c0 = 1.0) =>
        $"({F(c0)} + {F(c1)} * scn_r + {F(c2)} * scn_r * scn_r + {F(c3)} * scn_r * scn_r * scn_r)";
    internal static string F(double v) => v.ToString("0.0#######", CultureInfo.InvariantCulture);

    static SceneKitCalibration()
    {
        var bloom = System.Environment.GetEnvironmentVariable("MARVIN_BLOOM");
        if (!string.IsNullOrEmpty(bloom))
        {
            var p = bloom.Split(',');
            double D(int i, double d) => i < p.Length && double.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;
            BloomIntensityScale = D(0, BloomIntensityScale); BloomHdrScale = D(1, BloomHdrScale); BloomLevelOffset = D(2, BloomLevelOffset); BloomLevelWeight = D(3, BloomLevelWeight);
        }
        var o = System.Environment.GetEnvironmentVariable("MARVIN_SCN_CAL");
        if (string.IsNullOrEmpty(o)) return;
        foreach (var item in o.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = item.Split('=', 2);
            var field = typeof(SceneKitCalibration).GetField(kv[0].Trim(), BindingFlags.Public | BindingFlags.Static);
            if (field == null || kv.Length < 2) { Godot.GD.PushWarning($"MARVIN_SCN_CAL: unknown setting '{item}'"); continue; }
            field.SetValue(null, Convert.ChangeType(double.Parse(kv[1], CultureInfo.InvariantCulture), field.FieldType, CultureInfo.InvariantCulture));
        }
    }
}
