using System;
using System.Collections.Generic;
using Godot;

namespace Marvin.SceneKit;

public enum SCNShadowMode { forward = 0, deferred = 1, modulated = 2 }

/// <summary>
/// SCNLight. Directional/omni/spot lights become Godot Light3D nodes under the
/// owning SCNNode; ambient lights feed the composer's scn_ambient uniform.
/// Defaults are SceneKit's (measured): omni, intensity 1000, temperature 6500,
/// shadowRadius 3, shadowBias 1, zNear 1, zFar 100, orthographicScale 1,
/// maximumShadowDistance 100, shadowCascadeCount 1, spotOuterAngle 45.
/// </summary>
public sealed class SCNLight
{
    /// <summary>SCNLight.LightType.</summary>
    public enum LightType { ambient, omni, directional, spot, IES, probe, area }

    private LightType _type = LightType.omni;
    private object _color = NSColor.white, _shadowColor = NSColor.black;
    private double _intensity = 1000, _temperature = 6500, _shadowRadius = 3, _shadowBias = 1, _zNear = 1, _zFar = 100,
        _orthographicScale = 1, _maximumShadowDistance = 100, _attenuationStartDistance, _attenuationEndDistance,
        _attenuationFalloffExponent = 2, _spotInnerAngle, _spotOuterAngle = 45, _shadowCascadeSplittingFactor = 0.15;
    private bool _castsShadow, _automaticallyAdjustsShadowProjection = true, _sampleDistributedShadowMaps, _forcesBackFaceCasters;
    private int _shadowSampleCount, _shadowCascadeCount = 1, _categoryBitMask = -1;
    private CGSize _shadowMapSize = CGSize.zero;
    private SCNShadowMode _shadowMode = SCNShadowMode.forward;
    internal readonly HashSet<SCNNode> users = new();
    internal int version;
    public string name;

    public LightType type { get => _type; set { _type = value; Changed(); } }
    /// <summary>color: NSColor (or CGColor) contents.</summary>
    public object color { get => _color; set { _color = value; Changed(); } }
    public double intensity { get => _intensity; set { _intensity = value; Changed(); } }
    public double temperature { get => _temperature; set { _temperature = value; Changed(); } }
    public bool castsShadow { get => _castsShadow; set { _castsShadow = value; Changed(); } }
    public double shadowRadius { get => _shadowRadius; set { _shadowRadius = value; Changed(); } }
    public int shadowSampleCount { get => _shadowSampleCount; set { _shadowSampleCount = value; Changed(); } }
    public CGSize shadowMapSize { get => _shadowMapSize; set { _shadowMapSize = value; Changed(); } }
    public double shadowBias { get => _shadowBias; set { _shadowBias = value; Changed(); } }
    public object shadowColor { get => _shadowColor; set { _shadowColor = value; Changed(); } }
    public SCNShadowMode shadowMode { get => _shadowMode; set { _shadowMode = value; Changed(); } }
    public double zNear { get => _zNear; set { _zNear = value; Changed(); } }
    public double zFar { get => _zFar; set { _zFar = value; Changed(); } }
    public double orthographicScale { get => _orthographicScale; set { _orthographicScale = value; Changed(); } }
    public bool automaticallyAdjustsShadowProjection { get => _automaticallyAdjustsShadowProjection; set { _automaticallyAdjustsShadowProjection = value; Changed(); } }
    public double maximumShadowDistance { get => _maximumShadowDistance; set { _maximumShadowDistance = value; Changed(); } }
    public int shadowCascadeCount { get => _shadowCascadeCount; set { _shadowCascadeCount = value; Changed(); } }
    public double shadowCascadeSplittingFactor { get => _shadowCascadeSplittingFactor; set { _shadowCascadeSplittingFactor = value; Changed(); } }
    public bool sampleDistributedShadowMaps { get => _sampleDistributedShadowMaps; set { _sampleDistributedShadowMaps = value; Changed(); } }
    public bool forcesBackFaceCasters { get => _forcesBackFaceCasters; set { _forcesBackFaceCasters = value; Changed(); } }
    public int categoryBitMask { get => _categoryBitMask; set { _categoryBitMask = value; Changed(); SceneKitRuntime.MasksChanged(); } }
    public double attenuationStartDistance { get => _attenuationStartDistance; set { _attenuationStartDistance = value; Changed(); } }
    public double attenuationEndDistance { get => _attenuationEndDistance; set { _attenuationEndDistance = value; Changed(); } }
    public double attenuationFalloffExponent { get => _attenuationFalloffExponent; set { _attenuationFalloffExponent = value; Changed(); } }
    public double spotInnerAngle { get => _spotInnerAngle; set { _spotInnerAngle = value; Changed(); } }
    public double spotOuterAngle { get => _spotOuterAngle; set { _spotOuterAngle = value; Changed(); } }

    private void Changed()
    {
        version++;
        foreach (var n in users) n.MarkLightDirty();
        SceneKitRuntime.SceneStateDirty();
    }

    internal NSColor Color => _color as NSColor ?? NSColor.white;
    internal NSColor ShadowColor => _shadowColor as NSColor ?? NSColor.black;
    /// <summary>Linear light colour x intensity in SceneKit's "irradiance" units (1000 lm = 1.0), with colour temperature.</summary>
    internal Vector3 LinearRadiance
    {
        get
        {
            var (r, g, b) = Color.LinearRGB;
            var t = TemperatureTint(_temperature);
            double k = _intensity / 1000.0;
            return new Vector3((float)(r * t.X * k), (float)(g * t.Y * k), (float)(b * t.Z * k));
        }
    }
    /// <summary>Linear RGB of a black body at the given temperature, normalised to 6500 K = (1,1,1).</summary>
    internal static Vector3 TemperatureTint(double kelvin)
    {
        if (Math.Abs(kelvin - 6500) < 1) return Vector3.One;
        static Vector3 Kelvin(double k)
        {
            // Tanner Helland's fit, in sRGB, converted to linear.
            double t = k / 100, r, g, b;
            r = t <= 66 ? 255 : 329.698727446 * Math.Pow(t - 60, -0.1332047592);
            g = t <= 66 ? 99.4708025861 * Math.Log(t) - 161.1195681661 : 288.1221695283 * Math.Pow(t - 60, -0.0755148492);
            b = t >= 66 ? 255 : t <= 19 ? 0 : 138.5177312231 * Math.Log(t - 10) - 305.0447927307;
            return new Vector3((float)NSColor.SrgbToLinear(Math.Clamp(r, 0, 255) / 255), (float)NSColor.SrgbToLinear(Math.Clamp(g, 0, 255) / 255), (float)NSColor.SrgbToLinear(Math.Clamp(b, 0, 255) / 255));
        }
        var c = Kelvin(kelvin); var w = Kelvin(6500);
        return new Vector3(c.X / w.X, c.Y / w.Y, c.Z / w.Z);
    }

    /// <summary>
    /// Per-camera shadow parameters (see SceneKitRuntime.FitShadows). texel: Godot's shadow texel for this camera (world);
    /// depthRange: Godot's light-space depth range (bias scale). SceneKit's kernel is shadowRadius texels of its own map:
    /// 2 x orthographicScale / shadowMapSize when the projection is fixed; an automatically fitted SceneKit map is assumed
    /// to have Godot's texel size.
    /// </summary>
    internal void FitShadow(DirectionalLight3D light, double texel, double depthRange)
    {
        double qr = SceneKitCalibration.ShadowQualityRadius;
        double skTexel = !_automaticallyAdjustsShadowProjection
            ? 2 * _orthographicScale / (_shadowMapSize.width > 0 ? _shadowMapSize.width : SceneKitCalibration.DefaultShadowMapSize)
            : texel;
        double kernel = _shadowRadius * skTexel * SceneKitCalibration.ShadowKernelScale;
        double blur = Math.Clamp(kernel / (qr * texel), SceneKitCalibration.ShadowBlurMin, SceneKitCalibration.ShadowBlurMax);
        double kernelTexels = blur * qr;
        double biasWorld = (SceneKitCalibration.ShadowBiasTexels + SceneKitCalibration.ShadowBiasPerKernel * kernelTexels) * texel;
        double bias = biasWorld * 100 / Math.Max(1e-6, depthRange * blur * qr);
        if (Math.Abs(light.ShadowBlur - blur) > 1e-4) light.ShadowBlur = (float)blur;
        if (Math.Abs(light.ShadowBias - bias) > 1e-6) light.ShadowBias = (float)bias;
        // Normal offset (Godot texels) grows with the kernel so Godot's PCF does not self-shadow lit slopes; SceneKit's
        // own large-kernel self-shadowing is reproduced analytically for deferred lights (ShaderComposer light()).
        double normalBias = SceneKitCalibration.ShadowNormalBias + SceneKitCalibration.ShadowNormalBiasPerKernel * kernelTexels;
        if (Math.Abs(light.ShadowNormalBias - normalBias) > 1e-4) light.ShadowNormalBias = (float)normalBias;
    }

    /// <summary>Creates or updates the Godot light node for this light (null for ambient lights).</summary>
    internal Light3D Sync(Light3D existing, double sceneAmbientIntensity)
    {
        Light3D light = _type switch
        {
            LightType.directional => existing as DirectionalLight3D ?? new DirectionalLight3D(),
            LightType.spot => existing as SpotLight3D ?? new SpotLight3D(),
            LightType.omni => existing as OmniLight3D ?? new OmniLight3D(),
            _ => null,
        };
        if (light == null) return null;
        var rad = LinearRadiance;
        float energy = Math.Max(rad.X, Math.Max(rad.Y, rad.Z));
        var lin = energy > 0 ? rad / energy : Vector3.One;
        light.LightColor = new Color((float)NSColor.LinearToSrgb(lin.X), (float)NSColor.LinearToSrgb(lin.Y), (float)NSColor.LinearToSrgb(lin.Z));
        light.LightEnergy = (float)(energy * 1000 * SceneKitCalibration.LightEnergyPerLumen);
        light.LightSpecular = 1.0f;
        light.LightCullMask = SceneKitRuntime.GodotLayers(_categoryBitMask);
        light.ShadowCasterMask = SceneKitRuntime.GodotLayers(_categoryBitMask);
        light.ShadowEnabled = _castsShadow;
        var shadow = ShadowColor;
        double alpha = Math.Clamp(shadow.alphaComponent, 0, 1);
        // Deferred SceneKit shadows multiply the final colour by 1 - alpha x shadow. The composer's light()
        // does that from ATTENUATION (scn_deferred.x = alpha), so Godot must report the full shadow.
        if (_shadowMode == SCNShadowMode.deferred) alpha = 1;
        light.ShadowOpacity = (float)alpha;
        light.ShadowBias = (float)Math.Max(SceneKitCalibration.ShadowBiasMin, SceneKitCalibration.ShadowBiasPerUnit * _shadowBias);
        light.ShadowNormalBias = (float)SceneKitCalibration.ShadowNormalBias;
        light.ShadowBlur = (float)Math.Clamp(_shadowRadius * SceneKitCalibration.ShadowBlurPerRadius, SceneKitCalibration.ShadowBlurMin, SceneKitCalibration.ShadowBlurMax);
        if (light is DirectionalLight3D d)
        {
            d.DirectionalShadowMode = _shadowCascadeCount >= 4 ? DirectionalLight3D.ShadowMode.Parallel4Splits
                : _shadowCascadeCount >= 2 ? DirectionalLight3D.ShadowMode.Parallel2Splits : DirectionalLight3D.ShadowMode.Orthogonal;
            // SceneKit fits a fixed orthographic map (orthographicScale = half extent) when it does not
            // adjust the projection automatically; Godot always fits the camera frustum up to a distance.
            double maxDistance = _automaticallyAdjustsShadowProjection
                ? Math.Min(_maximumShadowDistance, Math.Max(_orthographicScale * 4, 10))
                : Math.Min(_maximumShadowDistance, _orthographicScale);
            d.DirectionalShadowMaxDistance = (float)Math.Max(1, maxDistance);
            d.DirectionalShadowFadeStart = 1.0f;
            d.SkyMode = DirectionalLight3D.SkyModeEnum.LightOnly;
        }
        else if (light is OmniLight3D o)
        {
            o.OmniRange = (float)(_attenuationEndDistance > 0 ? _attenuationEndDistance : 4096);
            o.OmniAttenuation = (float)(_attenuationEndDistance > 0 ? _attenuationFalloffExponent : 0);
        }
        else if (light is SpotLight3D s)
        {
            s.SpotRange = (float)(_attenuationEndDistance > 0 ? _attenuationEndDistance : 4096);
            s.SpotAttenuation = (float)(_attenuationEndDistance > 0 ? _attenuationFalloffExponent : 0);
            s.SpotAngle = (float)(_spotOuterAngle / 2);
            s.SpotAngleAttenuation = (float)(_spotInnerAngle >= _spotOuterAngle ? 0.01 : 1.0);
        }
        return light;
    }
}
