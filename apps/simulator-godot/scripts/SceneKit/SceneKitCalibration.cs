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
///   MARVIN_SCN_CAL="ShadowKernelScale=1;ShadowBlurPerRadius=0.4" tools/godot -- --calibration DIR
/// (MARVIN_BLOOM="scale,hdrScale,levelOffset,levelWeight" is still honoured for the bloom fields.)
/// The game trades two small look changes for GPU time (hard shadow filtering, mesh LODs for the robots' camera
/// images); MARVIN_SCN_CAL=Exact restores the exact-SceneKit configuration (ApplyExact), and so does the game's
/// Settings screen (Shadow quality "Exact", ShadowQualitySetting) unless MARVIN_SCN_CAL is set.
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

    // ---- Screen-space ambient occlusion: SceneKit's own algorithm, ported from its Metal kernels (SCNSsao.cs); no constants.

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
    /// <summary>
    /// SceneKit's caster-side slope-scaled bias for fixed shadow boxes (ShaderComposer.ShadowSlopeBias), in SceneKit
    /// shadow texels (2 x orthographicScale / shadowMapSize) per unit tangent of the surface's angle to the light, and the
    /// largest tangent used. 2.6 texels (7.4 cm for the race sun) is about the PCF kernel's radius, so a casting ground
    /// leaves the walls standing on it lit, as in SceneKit (GroundBiasProbe test A). Casters without normals (the town's
    /// shadow proxies) get none.
    /// </summary>
    public static double ShadowSlopeBiasTexels = 2.6, ShadowSlopeMax = 20.0;
    /// <summary>PCF kernel: Godot blur x quality radius x Godot texel = ShadowKernelScale x shadowRadius x SceneKit texel (world).
    /// Static fallback before the first fit: blur = shadowRadius x ShadowBlurPerRadius.</summary>
    public static double ShadowKernelScale = 0.88, ShadowBlurPerRadius = 1.0 / 3.0, ShadowBlurMin = 0.25, ShadowBlurMax = 16.0;
    /// <summary>
    /// Fixed shadow boxes that reach beyond orthographicScale from the camera get a second Godot split (SceneKitRuntime.FitShadows).
    /// Godot's normal-bias texel doubles there (2 x radius / the halved region); the normal bias is scaled by
    /// (split shadow size / orthogonal shadow size) ^ SplitNormalBiasExponent (1 keeps the world offset of the calibrated fit).
    /// </summary>
    public static double SplitNormalBiasExponent = 1.0;
    /// <summary>SceneKit's shadow map size when shadowMapSize is zero.</summary>
    public static double DefaultShadowMapSize = 2048;
    /// <summary>
    /// Directional shadow atlas (one atlas shared by all directional lights) and its filter quality (0 hard .. 5 ultra).
    /// The game uses the hard filter (0: one bilinear depth comparison, no PCF kernel), about 4 ms less GPU time per
    /// 1080p frame than SoftHigh (4: 16 taps, quality radius 3), whose kernel reproduces SceneKit's penumbra width; hard
    /// shadow edges are about one Godot texel wide instead (docs/performance.md, "Hard shadows"). The exact-SceneKit
    /// look is MARVIN_SCN_CAL=Exact (ApplyExact).
    /// </summary>
    public static int DirectionalShadowAtlas = 8192, ShadowFilterQuality = 0;
    /// <summary>Godot's PCF radius multiplier for ShadowFilterQuality (RendererSceneRenderRD::directional_soft_shadow_filter_set_quality).</summary>
    internal static double ShadowQualityRadius => ShadowFilterQuality switch { 0 => 1.0, 1 => 1.5, 2 => 2.0, 3 => 2.0, 4 => 3.0, _ => 4.0 };
    /// <summary>The hard filter has no kernel (ShadowFilterQuality 0).</summary>
    internal static bool HardShadows => ShadowFilterQuality == 0;
    /// <summary>
    /// Biases of the hard filter, in Godot shadow texels of the camera's fit (SCNLight.FitShadow): the world depth bias and
    /// Godot's ShadowNormalBias. The soft filter's biases grow with the kernel (ShadowBiasPerKernel, ShadowNormalBiasPerKernel)
    /// so its taps do not self-shadow lit slopes; one bilinear comparison needs no kernel term, and the kernel-sized normal
    /// offset moved hard shadow edges towards their casters as much as soft ones (the race sun at 10 degrees: 24 cm,
    /// SceneKit 8 cm; with these biases 7 cm, CAL_EXP=penumbra CAL_PENUMBRA_ELEV=1). Measured with --ground-bias-probe
    /// (no wall self-shadowing at 0-85 degrees, sunlit walls on a casting ground clean, thin plates cast from gaps of
    /// 8 / 16 / 4 / 2 cm where SceneKit's half-shadow gap is 7.5 / 15 / 4.8 / 2.4 cm) and the game's captures: a normal bias
    /// of 2 (Godot's default) was closest to macOS of 1, 2, 4 and 6; depth biases of 0.5 to 2 texels gave identical probe values.
    /// </summary>
    public static double HardShadowBiasTexels = 1.0, HardShadowNormalBias = 2.0;
    /// <summary>
    /// Near splits of the fixed-box suns under the hard filter (SceneKitRuntime.FitShadows, NearSplitDistances). With
    /// HardShadowSplits = 4 (the default), Godot's four splits end HardShadowSplit1 and HardShadowSplit2 metres from the
    /// camera, at the calibrated fit (orthographicScale) and at the box's farthest view depth, rounded up in steps of
    /// HardShadowFarStep so the last split's texels stay put between steps (the composer clips every split to the box).
    /// With the race camera at 1080p the texels are 1.2 / 2.8 / 5.3 / 14 cm across and half that along the light's y axis
    /// (Godot gives four splits a quarter of the light's 4096 x 8192 atlas region each); the soft filter's fit had 2.9 cm
    /// up to 58 m and 7 cm beyond. A first split ending at 8 m (0.8 cm texels) let thin parts of the robots shade
    /// their own feet and domes, which SceneKit's coarser, filtered map does not, and moved the close-ups further from macOS. Split distances are fixed in metres, so Godot's texel snapping keeps the first three
    /// splits from crawling while the camera moves. With 2, one split ends HardShadowTwoSplit metres from the camera
    /// (HardShadowSecondaryTwoSplit for the secondary light, when set). A secondary light (a smaller SceneKit map than the
    /// scene's largest fixed box: the second sun) has HardShadowSecondarySplits. HardShadowSplit1 = 0 keeps the soft
    /// filter's fit (one split up to orthographicScale, a second to the box's farthest view depth).
    /// Measured in docs/performance.md ("Near shadow splits").
    /// </summary>
    public static double HardShadowSplit1 = 12, HardShadowSplit2 = 30, HardShadowTwoSplit = 15, HardShadowSecondaryTwoSplit = 0, HardShadowFarStep = 1.25;
    public static int HardShadowSplits = 4, HardShadowSecondarySplits = 4;
    internal static bool NearShadowSplits => HardShadows && HardShadowSplit1 > 0;
    /// <summary>
    /// The race world's flat base terrain (DirtWorld's "Town base terrain": 256 m square at y = -0.025, under every other
    /// surface) casts SceneKit shadows that no visible surface receives: the race, town, sky, dune, entrance and
    /// ground-performance captures are identical with and without its casting (apart from their random state). Godot fills
    /// every split of both suns with it: 1.6 ms per 1080p frame at the race start, 1.4 ms in the town. It casts only in
    /// the exact configuration (docs/performance.md, "Near shadow splits").
    /// </summary>
    public static bool BaseTerrainCastsShadow = false;

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

    // ---- Godot mesh LODs of the robots (SCNGeometry.godotAutomaticLevelsOfDetail): a level is used while its geometric
    // error stays below this many pixels of the camera's view (Viewport.MeshLodThreshold of every SCNView and SCNRenderer),
    // by the shadow maps and, with MeshLodForCamera, by the camera. Measured on the robots: drawn by the camera too, Godot's default of 1 pixel
    // changed the close-ups by 0.03/255 on average and half a pixel by 0.01 (town view: 2.2 and 2.4 M primitives per frame
    // instead of 3.8 M); as shadow casters only, half a pixel changes them by at most 0.003/255.
    public static double MeshLodThreshold = 0.5;
    /// <summary>Let the camera draw the levels too (no shadow-only twin, the game's default): 0.5-1 ms less GPU time per
    /// 1080p frame than shadows alone, but the robots' silhouettes move by up to MeshLodThreshold pixels. False draws the
    /// full meshes for the camera, as SceneKit does (MARVIN_SCN_CAL=Exact or "MeshLodForCamera=0").</summary>
    public static bool MeshLodForCamera = true;

    /// <summary>
    /// The exact-SceneKit configuration: SoftHigh shadow filtering with the soft biases (penumbrae of SceneKit's width) and
    /// its split fit, the robots' full meshes for the camera and the base terrain's shadow, at the GPU cost measured in
    /// docs/performance.md ("Hard shadows").
    /// MARVIN_SCN_CAL=Exact applies it; settings after it in the list still override it ("Exact;ShadowNormalBias=3").
    /// The game's Settings screen selects it as Shadow quality "Exact" (ShadowQualitySetting).
    /// </summary>
    public static void ApplyExact()
    {
        ShadowFilterQuality = 4;
        MeshLodForCamera = false;
        BaseTerrainCastsShadow = true;
    }
    /// <summary>
    /// The default configuration, the reverse of ApplyExact: the hard filter with its near splits, the robots' mesh LODs
    /// for the camera, no base terrain shadow (Shadow quality "Fast" on the game's Settings screen).
    /// </summary>
    public static void ApplyFast()
    {
        ShadowFilterQuality = 0;
        MeshLodForCamera = true;
        BaseTerrainCastsShadow = false;
    }
    /// <summary>Whether MARVIN_SCN_CAL is set: tests and experiments choose the configuration, and the game's Shadow quality
    /// setting leaves it alone (ShadowQualitySetting.apply).</summary>
    internal static readonly bool Overridden = !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("MARVIN_SCN_CAL"));

    // ---- Direct specular (GGX in the composer's light()).
    // Measured (tools/scenekit-reference/robots/SpecularHighlight.swift: highlight profiles of roughness 0 .. 0.2 spheres
    // under 0.01 .. 1000 lm suns, zoomed in at 0.0005 rad per pixel and whole at 0.007): SceneKit keeps the highlight's full
    // energy as roughness goes to 0 (D is not clamped to the half-float maximum; Godot's 65504 clamp lost the highlight below
    // roughness 0.04 and all of it at 0), and widens the lobe with the shading normal's screen-space variation:
    // alpha^2 + SpecularAntialiasing x (|dN/dx|^2 + |dN/dy|^2) reproduces both scales (a roughness-0 sphere 145 px in radius
    // has the highlight of roughness ~0.07; zoomed in, of ~0.018); a flat roughness-0 plane reflects no direct light.
    public static double SpecularAntialiasing = 0.25;

    // ---- Image-based lighting (lightingEnvironment), as polynomials in roughness r (fitted to SceneKit).
    public static double IblDiffuse1 = -0.1757, IblDiffuse2 = 0.3455, IblDiffuse3 = -0.3623;
    public static double IblSpecular0 = 0.9358, IblSpecular1 = 1.1768, IblSpecular2 = -2.5492, IblSpecular3 = 0.7755;
    /// <summary>Pre-filtered radiance band used for roughness r: clamp(IblBlurScale x r^IblBlurPower, 0, 1) (bands are GGX alpha = band^2).</summary>
    public static double IblBlurScale = 1.0, IblBlurPower = 1.0;

    // ---- Text (CoreText vs FreeType), measured with tools/scenekit-reference/TextCalibration.swift against
    // `tools/godot -- --text-calibration DIR` (28 system/monospaced fonts x 5 greys x plain/cell; FontSmoothing).
    /// <summary>
    /// CoreText's font smoothing dilates glyphs; as a FreeType stem gain (pixels) it is
    /// min(FontSmoothingCap, point size x (Black + (White - Black) x f(L))) + FontSmoothingCell for control cells
    /// (NSTextField/NSButton text over a transparent background), with f(L) = clamp((L - Floor) / (1 - Floor), 0, 1)^Power
    /// of the text colour's linear luminance L. Measured: black 0.14 px at 10 pt, 0.29 at 21 pt, 0.62 at 44 pt;
    /// white 0.37 px at 10 pt, 0.71 from about 21 pt; the cap 0.71 px holds for every colour from 58 pt; cells add
    /// 0.31 px at every size and colour. Fit: mean ink error 1.3 % (max 5 %) over the 280 cases.
    /// FontDilationRise x stem gain adjusts the cap top through a vertical outline scale (fitted on the rows' vertical
    /// ink profiles: error 0.0320 at -0.25, 0.0331 at -0.15, 0.0375 at 0).
    /// </summary>
    public static double FontSmoothingBlack = 0.014, FontSmoothingWhite = 0.034, FontSmoothingCap = 0.71, FontSmoothingCell = 0.31;
    public static double FontSmoothingLuminanceFloor = 0.06, FontSmoothingLuminancePower = 0.7;
    public static double FontDilationRise = -0.25;
    /// <summary>
    /// Glyphs are drawn this much further left per pixel of stem gain: FreeType's emboldening keeps left edges and
    /// widens to the right (ink centroid +0.25 x gain), CoreText dilates about symmetrically. 0.125 fits CoreText's
    /// smoothed glyph positions best after Godot's quarter-pixel rounding (mean |offset| 0.07 px over 4 fonts x 20
    /// positions; 0.08 with 0, 0.15 with 0.25).
    /// </summary>
    public static double FontEmboldenShift = 0.125;

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
        // Items are separated by ';' or ','; numbers use '.'. "Exact" is the named exact-SceneKit preset.
        foreach (var item in o.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (item.Trim().Equals("Exact", StringComparison.OrdinalIgnoreCase)) { ApplyExact(); continue; }
            var kv = item.Split('=', 2);
            var field = typeof(SceneKitCalibration).GetField(kv[0].Trim(), BindingFlags.Public | BindingFlags.Static);
            if (field == null || kv.Length < 2) { Godot.GD.PushWarning($"MARVIN_SCN_CAL: unknown setting '{item}'"); continue; }
            field.SetValue(null, Convert.ChangeType(double.Parse(kv[1], CultureInfo.InvariantCulture), field.FieldType, CultureInfo.InvariantCulture));
        }
    }
}
