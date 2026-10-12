using System;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

/// A fictional circumbinary world: a 0.20 AU binary seen from ~1 AU,
/// coplanar with the planet's orbit, at 25 degrees north on an equinox.
/// Binary phase is fixed over a short race; planet rotation supplies time of day.
public readonly struct BinaryDaylight
{
    public readonly double fraction;
    public readonly double phase;
    public readonly Double3[] directions;
    public readonly double[] radii;
    public BinaryDaylight(double fraction, double phase)
    {
        radii = new[] { 0.55, 0.38 }.Select(r => r * Math.PI / 180).ToArray();
        this.fraction = max(0.015, min(0.985, fraction)); this.phase = phase;
        var offsets = new[] { atan2(-0.06 * sin(phase), 1 + 0.06 * cos(phase)), atan2(0.14 * sin(phase), 1 - 0.14 * cos(phase)) };
        // Shared daylight: both stellar centers are above the horizon.
        double first = max(-Math.PI / 2 - offsets[0], -Math.PI / 2 - offsets[1]);
        double last = min(Math.PI / 2 - offsets[0], Math.PI / 2 - offsets[1]);
        double h = first + (last - first) * this.fraction, latitude = 25 * Math.PI / 180;
        directions = offsets.Select(d => new Double3(-sin(h + d), cos(latitude) * cos(h + d), -sin(latitude) * cos(h + d))).ToArray();
    }
    public static BinaryDaylight random()
    {
        // Avoid conjunction/eclipses for this race setting: always show a pair.
        double phase = SwiftRandom.doubleClosed(0.35, Math.PI - 0.35) + (SwiftRandom.boolRandom() ? Math.PI : 0);
        return new BinaryDaylight(fraction: SwiftRandom.doubleClosed(0.015, 0.985), phase: phase);
    }
    public double separationDegrees => acos(max(-1, min(1, Simd.dot(directions[0], directions[1])))) * 180 / Math.PI;
}

public sealed class BinarySky
{
    private bool stormActive = false;
    public void setStorm(bool enabled) { stormActive = enabled; apply(daylight); }
    public readonly SCNNode root = new SCNNode();
    private readonly SCNNode dome = new SCNNode();
    private readonly SCNMaterial skyMaterial = new SCNMaterial();
    private readonly SCNNode ambient = new SCNNode();
    public readonly SCNNode[] suns = { new SCNNode(), new SCNNode() };
    public BinaryDaylight daylight { get; private set; } = new BinaryDaylight(fraction: 0.5, phase: 1.2);
    private readonly WeakReference<SCNScene> scene;

    public BinarySky(SCNScene scene)
    {
        this.scene = new WeakReference<SCNScene>(scene); root.name = "Binary daylight";
        var sphere = new SCNSphere(radius: 220); sphere.segmentCount = 48;
        skyMaterial.lightingModel = SCNMaterial.LightingModel.constant; skyMaterial.cullMode = SCNCullMode.front;
        skyMaterial.readsFromDepthBuffer = false; skyMaterial.writesToDepthBuffer = false;
        skyMaterial.shaderModifiers = new()
        {
            [SCNShaderModifierEntryPoint.geometry] = @"
#pragma varyings
vec3 skyDirection;
#pragma body
skyDirection = VERTEX;
",
            [SCNShaderModifierEntryPoint.fragment] = @"
#pragma arguments
vec3 sunA;
vec3 sunB;
vec3 zenith;
vec3 horizon;
vec3 tintA;
vec3 tintB;
vec2 sunRadii;
float daylight;
vec3 duskBand;
float storm;
vec3 stormTint;
#pragma body
vec3 d = normalize(skyDirection);
float height = max(d.y, 0.0);
float h = 1.0 - exp(-height / mix(0.085, 0.35, daylight));
vec3 sky = mix(horizon, zenith, h);
vec3 solarAxis = normalize(sunA + sunB);
float facing = pow(max(0.0, dot(d.xz, solarAxis.xz) / (max(length(d.xz), 0.00001) * max(length(solarAxis.xz), 0.00001))), 3.0);
// A narrow warm horizon beneath cool upper air, with a dusty
// rose transition at dusk. Scattering is strongest toward the suns.
float band = exp(-pow((height - 0.12) / 0.10, 2.0));
sky += duskBand * band * (1.0 - daylight) * (0.35 + 0.65 * facing);
sky += vec3(0.34, 0.095, 0.018) * facing * exp(-height / 0.15) * (1.0 - daylight);
float a = acos(clamp(dot(d, sunA), -1.0, 1.0));
float b = acos(clamp(dot(d, sunB), -1.0, 1.0));
float haze = mix(1.0, 0.35, daylight);
sky += tintA * haze * (0.5 * exp(-a * a / 0.006) + 0.65 * exp(-a / 0.017));
sky += tintB * haze * (0.3 * exp(-b * b / 0.004) + 0.4 * exp(-b / 0.013));
float discA = 1.0 - smoothstep(sunRadii.x * 0.91, sunRadii.x * 1.05, a);
float discB = 1.0 - smoothstep(sunRadii.y * 0.91, sunRadii.y * 1.05, b);
float limbA = sqrt(max(0.0, 1.0 - pow(a / sunRadii.x, 2.0)));
float limbB = sqrt(max(0.0, 1.0 - pow(b / sunRadii.y, 2.0)));
sky = mix(sky, tintA * (3.8 + 2.4 * limbA), discA);
sky = mix(sky, tintB * (2.5 + 1.5 * limbB), discB);
// This is an infinitely distant sky: bypass scene distance fog.
vec3 skyOut = mix(sky, stormTint + sky * 0.025, storm * 0.97);
ALBEDO = skyOut; EMISSION = skyOut; FOG = vec4(0.0);
",
        };
        sphere.materials = new() { skyMaterial }; dome.geometry = sphere; dome.castsShadow = false;
        dome.renderingOrder = -10000; root.addChildNode(dome);
        ambient.light = new SCNLight(); ambient.light.type = SCNLight.LightType.ambient; root.addChildNode(ambient);
        for (int i = 0; i < suns.Length; i++)
        {
            var sun = suns[i];
            sun.name = $"Sun {i + 1}"; sun.light = new SCNLight(); sun.light.type = SCNLight.LightType.directional;
            // Forward shadows attenuate each star separately. The companion
            // lights the other star's shadow instead of painting two dark decals.
            sun.light.castsShadow = true; sun.light.shadowMode = SCNShadowMode.forward;
            int resolution = i == 0 ? 4096 : 2048;
            sun.light.shadowMapSize = new CGSize(resolution, resolution);
            sun.light.automaticallyAdjustsShadowProjection = false;
            sun.light.sampleDistributedShadowMaps = false;
            sun.light.forcesBackFaceCasters = false;
            sun.light.zNear = 0.1; sun.light.zFar = 220;
            sun.light.maximumShadowDistance = 500;
            sun.light.orthographicScale = 58; sun.light.shadowRadius = i == 0 ? 3 : 2;
            sun.light.shadowSampleCount = 8; sun.light.shadowColor = NSColor.black;
            sun.light.shadowBias = 0.6; root.addChildNode(sun);
        }
        scene.rootNode.addChildNode(root);
        apply(daylight);
    }
    /// World-anchored maps are independent of the viewing camera. Only exploration
    /// moves their footprint, in light-space texel increments to avoid shimmer.
    public void updateShadowCenter(Double3 position)
    {
        var center = max(abs(position.x), abs(position.z)) < 28 ? Double3.zero : position;
        foreach (var sun in suns)
        {
            var transform = sun.simdWorldTransform;
            var right = new Double3(transform.X.X, transform.X.Y, transform.X.Z);
            var up = new Double3(transform.Y.X, transform.Y.Y, transform.Y.Z);
            var back = new Double3(transform.Z.X, transform.Z.Y, transform.Z.Z);
            double texel = 116 / sun.light.shadowMapSize.width;
            double snap(double value) => rounded(value / texel) * texel;
            var anchor = right * snap(Simd.dot(center, right)) + up * snap(Simd.dot(center, up)) + back * snap(Simd.dot(center, back));
            var p = anchor + back * 80;
            sun.position = new SCNVector3(p.x, p.y, p.z);
        }
    }
    public void attach(SCNNode camera)
    {
        var weakCamera = new WeakReference<SCNNode>(camera);
        dome.constraints = new() { SCNTransformConstraint.positionConstraint(true, (_, _) => weakCamera.TryGetTarget(out var c) ? c.presentation.worldPosition : SCNVector3Zero) };
        if (camera.camera is not SCNCamera lens) return;
        lens.wantsHDR = true; lens.wantsExposureAdaptation = false;
        // Fixed exposure avoids pumping when a tiny sun enters/leaves frame.
        lens.exposureOffset = 0; lens.bloomIntensity = 0.38;
        lens.bloomThreshold = 1.2; lens.bloomBlurRadius = 12;
    }
    public void apply(BinaryDaylight value)
    {
        daylight = value;
        skyMaterial.setValue((float)(stormActive ? 1 : 0), "storm");
        skyMaterial.setValue(NSValue.scnVector3(new SCNVector3(0.46, 0.29, 0.14)), "stormTint");
        skyMaterial.setValue(NSValue.point(new NSPoint(value.radii[0], value.radii[1])), "sunRadii");
        double elevation = max(0, value.directions.Select(d => d.y).Max());
        double day = min(1, elevation / 0.55);
        float evening = value.fraction > 0.5 ? 1 : 0;
        var lowZenith = new Float3(0.018f, 0.070f, 0.17f) * (1 - evening) + new Float3(0.065f, 0.027f, 0.10f) * evening;
        var lowHorizon = new Float3(0.95f, 0.36f, 0.09f) * (1 - evening) + new Float3(0.85f, 0.19f, 0.035f) * evening;
        var zenith = lowZenith + (new Float3(0.20f, 0.38f, 0.62f) - lowZenith) * (float)day;
        var horizon = lowHorizon + (new Float3(0.65f, 0.72f, 0.75f) - lowHorizon) * (float)day;
        skyMaterial.setValue((float)day, "daylight");
        var band = new Float3(0.04f, 0.055f, 0.06f) * (1 - evening) + new Float3(0.22f, 0.045f, 0.085f) * evening;
        skyMaterial.setValue(NSValue.scnVector3(new SCNVector3(band.x, band.y, band.z)), "duskBand");
        NSColor ink(Float3 p) => NSColor.calibratedRed(p.x, p.y, p.z, 1);
        foreach (var (key, v) in new[] { ("zenith", zenith), ("horizon", horizon) }) skyMaterial.setValue(NSValue.scnVector3(new SCNVector3(v.x, v.y, v.z)), key);
        for (int i = 0; i < 2; i++)
        {
            Double3 d = value.directions[i]; double height = max(0, d.y);
            // Kasten-style air mass approximation; Beer-Lambert extinction
            // removes more blue than red along a long horizon path.
            double altitude = asin(height) * 180 / Math.PI;
            double mass = 1 / (height + 0.50572 * pow(altitude + 6.07995, -1.6364));
            var attenuation = new Double3(exp(-0.035 * mass), exp(-0.070 * mass), exp(-0.15 * mass));
            var intrinsic = i == 0 ? new Double3(1, 0.94, 0.83) : new Double3(1, 0.73, 0.46);
            var rgb = intrinsic * attenuation;
            var tint = new Float3((float)rgb.x, (float)rgb.y, (float)rgb.z);
            suns[i].position = new SCNVector3(d.x * 80, d.y * 80, d.z * 80);
            suns[i].look(SCNVector3Zero, up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
            suns[i].light.color = ink(tint);
            suns[i].light.intensity = (i == 0 ? 1550 : 470) * (stormActive ? 0.20 : 1);
            skyMaterial.setValue(NSValue.scnVector3(new SCNVector3(d.x, d.y, d.z)), i == 0 ? "sunA" : "sunB");
            skyMaterial.setValue(NSValue.scnVector3(new SCNVector3(tint.x, tint.y, tint.z)), i == 0 ? "tintA" : "tintB");
        }
        // Open-sky fill keeps racing surfaces readable in backlight. It is
        // deliberately weaker than direct light, not a camera-facing key light.
        ambient.light.color = ink(new Float3(0.54f, 0.62f, 0.78f)); ambient.light.intensity = 260 - 70 * day;
        if (scene.TryGetTarget(out var s))
        {
            s.fogColor = ink(horizon); s.fogStartDistance = 115; s.fogEndDistance = 240;
            // A diffuse sky probe has no baked sun at the old lighting direction.
            // Directional lights supply the correctly positioned specular highlights.
            s.lightingEnvironment.contents = stormActive ? skyProbe(zenith: new Float3(0.46f, 0.34f, 0.23f), horizon: new Float3(0.68f, 0.50f, 0.32f)) : skyProbe(zenith: zenith, horizon: horizon);
            s.lightingEnvironment.intensity = stormActive ? 0.75 : 0.95 - 0.20 * day;
            if (stormActive)
            {
                s.fogColor = color(0xae865b); s.fogStartDistance = 3; s.fogEndDistance = 65;
                ambient.light.color = color(0xd4b48a); ambient.light.intensity = 320;
            }
        }
    }
    private NSImage skyProbe(Float3 zenith, Float3 horizon)
    {
        int w = 64, h = 32;
        var bitmap = new NSBitmapImageRep(bitmapDataPlanes: null, pixelsWide: w, pixelsHigh: h, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: NSColorSpaceName.deviceRGB, bytesPerRow: w * 4, bitsPerPixel: 32);
        for (int y = 0; y < h; y++)
        {
            float t = (float)pow(max(0, cos((double)y / (h - 1) * Math.PI)), 0.42);
            var rgb = (horizon * (1 - t) + zenith * t) * (y > h / 2 ? 0.55f : 1f);
            for (int x = 0; x < w; x++) { int j = (y * w + x) * 4; for (int c = 0; c < 3; c++) { bitmap.bitmapData[j + c] = (byte)max(0, min(255, rgb[c] * 255)); } bitmap.bitmapData[j + 3] = 255; }
        }
        var image = new NSImage(new NSSize(w, h)); image.addRepresentation(bitmap); return image;
    }
}
