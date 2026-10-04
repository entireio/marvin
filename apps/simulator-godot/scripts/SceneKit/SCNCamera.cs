using System;
using System.Collections.Generic;
using Godot;

namespace Marvin.SceneKit;

public enum SCNCameraProjectionDirection { vertical = 0, horizontal = 1 }

/// <summary>
/// SCNCamera. The facade does not create a Godot camera per SCNCamera: each
/// SCNView/SCNRenderer owns a Camera3D that copies the point-of-view node's
/// world transform and these lens settings every frame.
/// Defaults are SceneKit's (measured): fieldOfView 60 (vertical), zNear 1, zFar 100,
/// orthographicScale 1 (half height), wantsExposureAdaptation true, bloomThreshold 0.5,
/// bloomBlurRadius 4, SSAO radius 5 / bias 0.03 / depth 0.2 / normal 0.3.
/// </summary>
public sealed class SCNCamera
{
    public string name;
    public double fieldOfView = 60;
    public SCNCameraProjectionDirection projectionDirection = SCNCameraProjectionDirection.vertical;
    public double focalLength = 50, sensorHeight = 24;
    public double zNear = 1, zFar = 100;
    public bool automaticallyAdjustsZRange;
    public bool usesOrthographicProjection;
    public double orthographicScale = 1;
    public SCNMatrix4? projectionTransformOverride;
    private int _categoryBitMask = -1;
    public int categoryBitMask { get => _categoryBitMask; set { _categoryBitMask = value; SceneKitRuntime.MasksChanged(); } }

    // HDR and exposure.
    public bool wantsHDR;
    public double exposureOffset;
    public double averageGray = 0.18, whitePoint = 1;
    public double minimumExposure = -15, maximumExposure = 15;
    public bool wantsExposureAdaptation = true;
    public double exposureAdaptationBrighteningSpeedFactor = 0.4, exposureAdaptationDarkeningSpeedFactor = 0.6;
    // Bloom.
    public double bloomIntensity, bloomThreshold = 0.5, bloomBlurRadius = 4;
    public int bloomIterationCount = 1;
    public double bloomIterationSpread;
    // Screen-space ambient occlusion.
    public double screenSpaceAmbientOcclusionIntensity, screenSpaceAmbientOcclusionRadius = 5, screenSpaceAmbientOcclusionBias = 0.03,
        screenSpaceAmbientOcclusionDepthThreshold = 0.2, screenSpaceAmbientOcclusionNormalThreshold = 0.3;
    // Colour and lens effects.
    public double contrast, saturation = 1, vignettingPower, vignettingIntensity = 1, colorFringeStrength, colorFringeIntensity = 1,
        motionBlurIntensity, grainIntensity, grainScale = 1, whiteBalanceTemperature, whiteBalanceTint;
    public bool grainIsColored;
    public bool wantsDepthOfField;
    public double focusDistance = 2.5, fStop = 5.6;
    public int apertureBladeCount = 6;
    public object colorGrading;

    /// <summary>projectionTransform: the projection for a square viewport (SceneKit returns the last used aspect).</summary>
    public SCNMatrix4 projectionTransform
    {
        get => projectionTransformOverride ?? ProjectionFor(1.0);
        set => projectionTransformOverride = value;
    }
    /// <summary>OpenGL-style projection (right-handed, -Z forward, depth -1..1) for the given aspect ratio.</summary>
    internal SCNMatrix4 ProjectionFor(double aspect)
    {
        if (projectionTransformOverride.HasValue) return projectionTransformOverride.Value;
        double n = zNear, f = zFar;
        if (usesOrthographicProjection)
        {
            double h = orthographicScale, w = h * aspect;
            if (projectionDirection == SCNCameraProjectionDirection.horizontal) { w = orthographicScale; h = w / aspect; }
            return new SCNMatrix4(1 / w, 0, 0, 0, 0, 1 / h, 0, 0, 0, 0, -2 / (f - n), 0, 0, 0, -(f + n) / (f - n), 1);
        }
        double t = Math.Tan(fieldOfView * Math.PI / 360);
        double sy = projectionDirection == SCNCameraProjectionDirection.vertical ? 1 / t : aspect / t;
        double sx = sy / aspect;
        return new SCNMatrix4(sx, 0, 0, 0, 0, sy, 0, 0, 0, 0, -(f + n) / (f - n), -1, 0, 0, -2 * f * n / (f - n), 0);
    }

    /// <summary>Copies the lens into a Godot camera.</summary>
    internal void ApplyLens(Camera3D cam)
    {
        cam.Near = (float)Math.Max(1e-4, zNear);
        cam.Far = (float)Math.Max(zNear + 1e-3, zFar);
        cam.KeepAspect = projectionDirection == SCNCameraProjectionDirection.vertical ? Camera3D.KeepAspectEnum.Height : Camera3D.KeepAspectEnum.Width;
        if (usesOrthographicProjection)
        {
            cam.Projection = Camera3D.ProjectionType.Orthogonal;
            cam.Size = (float)(orthographicScale * 2); // measured: orthographicScale is half the visible height
        }
        else
        {
            cam.Projection = Camera3D.ProjectionType.Perspective;
            cam.Fov = (float)Math.Clamp(fieldOfView, 1, 179);
        }
        cam.CullMask = 0xFFFFF;
    }

    /// <summary>
    /// Camera-dependent environment settings. SceneKit's HDR pipeline with fixed
    /// exposure is a plain linear clamp (measured: emission k renders as k up to 1.0),
    /// so tone mapping is Linear in both HDR and LDR; exposureOffset scales exposure.
    /// </summary>
    internal void ApplyEnvironment(Godot.Environment env, CameraAttributesPractical attributes, double viewportHeight = 400)
    {
        env.TonemapMode = Godot.Environment.ToneMapper.Linear;
        env.TonemapExposure = (float)SceneKitCalibration.TonemapExposure;
        env.TonemapWhite = (float)SceneKitCalibration.TonemapWhite;
        attributes.ExposureMultiplier = (float)(wantsHDR ? Math.Pow(2, exposureOffset) : 1.0);
        attributes.AutoExposureEnabled = false; // PORT: SceneKit exposure adaptation is not emulated (the game disables it).
        bool bloom = wantsHDR && bloomIntensity > 0;
        env.GlowEnabled = bloom;
        if (bloom)
        {
            env.GlowIntensity = (float)(bloomIntensity * SceneKitCalibration.BloomIntensityScale);
            env.GlowStrength = 1.0f;
            env.GlowBloom = 0.0f;
            env.GlowHdrThreshold = (float)bloomThreshold;
            env.GlowHdrScale = (float)SceneKitCalibration.BloomHdrScale;
            env.GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Additive;
            // Blur radius -> Godot glow levels (each level doubles the blur footprint).
            // SceneKit's bloom blur is a fixed pixel radius (measured at 400 and 800 px heights); Godot's glow
            // levels scale with the viewport, so fewer levels are used for taller viewports.
            double heightScale = SceneKitCalibration.BloomHeightScaling * Math.Log2(Math.Max(1, viewportHeight) / 400.0);
            int top = Math.Clamp((int)Math.Round(Math.Log2(Math.Max(1, bloomBlurRadius)) + SceneKitCalibration.BloomLevelOffset - heightScale), 1, 7);
            for (int i = 1; i <= 7; i++) env.SetGlowLevel(i - 1, i <= top ? (float)SceneKitCalibration.BloomLevelWeight : 0.0f);
        }
        bool ssao = screenSpaceAmbientOcclusionIntensity > 0;
        env.SsaoEnabled = ssao;
        if (ssao)
        {
            env.SsaoRadius = (float)(screenSpaceAmbientOcclusionRadius * SceneKitCalibration.SsaoRadiusScale);
            env.SsaoIntensity = (float)(screenSpaceAmbientOcclusionIntensity * SceneKitCalibration.SsaoIntensityScale);
            env.SsaoPower = (float)SceneKitCalibration.SsaoPower;
            env.SsaoDetail = (float)SceneKitCalibration.SsaoDetail;
            env.SsaoHorizon = (float)SceneKitCalibration.SsaoHorizon;
            env.SsaoSharpness = (float)SceneKitCalibration.SsaoSharpness;
            env.SsaoLightAffect = 0.0f; // SceneKit SSAO attenuates ambient and IBL only
            env.SsaoAOChannelAffect = 0.0f;
        }
        bool adjust = contrast != 0 || saturation != 1;
        env.AdjustmentEnabled = adjust;
        if (adjust)
        {
            env.AdjustmentContrast = (float)(1 + contrast);
            env.AdjustmentSaturation = (float)saturation;
            env.AdjustmentBrightness = 1.0f;
        }
    }
}
