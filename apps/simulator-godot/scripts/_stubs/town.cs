// STUBS (stream "town"): minimal stand-ins for types owned by other streams, with only the members
// that the town files (TownWorld.cs, TownGround.cs, TownShadowBatch.cs, TownSigns.cs, TownAccessMap.cs,
// TownCrowd.cs, TownResidents.cs, TownStreetResidents.cs, TownSmoke.cs, EntranceSmoke.cs) call.
// The orchestrator deletes this file when merging; the real ports replace every type here.
using System;
using System.Collections.Generic;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

// ShadowVolume.swift
public readonly struct ShadowBounds
{
    public readonly Double3 low, high;
    public readonly List<Double3> corners;
    public ShadowBounds(Double3 low, Double3 high)
    {
        this.low = low; this.high = high;
        var result = new List<Double3>();
        foreach (var x in new[] { low.x, high.x }) foreach (var y in new[] { low.y, high.y }) foreach (var z in new[] { low.z, high.z }) result.Add(new Double3(x, y, z));
        corners = result;
    }
}
public readonly struct ShadowFrustum
{
    public static List<ShadowFrustum> cameras(SCNNode node, double aspect) => new();
    public bool intersects(ShadowBounds bounds) => true;
}

// RaceAudio.swift
public struct SpectatorSoundZone
{
    public Double2 position;
    public int people;
    public int stormPeople;
    public SpectatorSoundZone(Double2 position, int people, int stormPeople) { this.position = position; this.people = people; this.stormPeople = stormPeople; }
}
public struct TownSoundZone
{
    public enum Kind { market, workshop, cantina }
    public readonly Double2 position; public readonly Kind kind;
    public double activity;
    public bool infieldRepair;
    public TownSoundZone(Double2 position, Kind kind, double activity = 1, bool infieldRepair = false)
    {
        this.position = position; this.kind = kind; this.activity = activity; this.infieldRepair = infieldRepair;
    }
}

// BinarySky.swift (sky stream). The town captures (TownSmoke.cs, EntranceSmoke.cs) need the race
// lighting, so this stub mirrors BinarySky.swift line for line: the sky dome (the facade's translation
// of its shader modifiers, FacadeTest.SkyGeometry/SkyFragment), two forward-shadow suns with air-mass
// tints, ambient fill, fog and the 64x32 sky probe.
public readonly struct BinaryDaylight
{
    public readonly double fraction, phase;
    public readonly List<Double3> directions;
    public List<double> radii => new List<double> { 0.55, 0.38 }.ConvertAll(r => r * Math.PI / 180);
    public BinaryDaylight(double fraction, double phase)
    {
        this.fraction = max(0.015, min(0.985, fraction)); this.phase = phase;
        var offsets = new[] { atan2(-0.06 * sin(phase), 1 + 0.06 * cos(phase)), atan2(0.14 * sin(phase), 1 - 0.14 * cos(phase)) };
        // Shared daylight: both stellar centers are above the horizon.
        var first = max(-Math.PI / 2 - offsets[0], -Math.PI / 2 - offsets[1]);
        var last = min(Math.PI / 2 - offsets[0], Math.PI / 2 - offsets[1]);
        double h = first + (last - first) * this.fraction, latitude = 25 * Math.PI / 180;
        directions = new List<double>(offsets).ConvertAll(d => new Double3(-sin(h + d), cos(latitude) * cos(h + d), -sin(latitude) * cos(h + d)));
    }
    public static BinaryDaylight random()
    {
        // Avoid conjunction/eclipses for this race setting: always show a pair.
        var phase = 0.35 + System.Random.Shared.NextDouble() * (Math.PI - 0.70) + (System.Random.Shared.Next(2) == 1 ? Math.PI : 0);
        return new BinaryDaylight(fraction: 0.015 + System.Random.Shared.NextDouble() * 0.97, phase: phase);
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
    public readonly List<SCNNode> suns = new() { new SCNNode(), new SCNNode() };
    public BinaryDaylight daylight { get; private set; } = new BinaryDaylight(fraction: 0.5, phase: 1.2);
    private readonly WeakReference<SCNScene> scene;
    private static NSColor color(uint hex, double alpha = 1) => NSColor.srgbRed((double)((hex >> 16) & 255) / 255, (double)((hex >> 8) & 255) / 255, (double)(hex & 255) / 255, alpha);
    public BinarySky(SCNScene scene)
    {
        this.scene = new WeakReference<SCNScene>(scene); root.name = "Binary daylight";
        var sphere = new SCNSphere(220); sphere.segmentCount = 48;
        skyMaterial.lightingModel = SCNMaterial.LightingModel.constant; skyMaterial.cullMode = SCNCullMode.front;
        skyMaterial.readsFromDepthBuffer = false; skyMaterial.writesToDepthBuffer = false;
        skyMaterial.shaderModifiers = new() { [SCNShaderModifierEntryPoint.geometry] = FacadeTest.SkyGeometry, [SCNShaderModifierEntryPoint.fragment] = FacadeTest.SkyFragment };
        sphere.materials = new() { skyMaterial }; dome.geometry = sphere; dome.castsShadow = false;
        dome.renderingOrder = -10000; root.addChildNode(dome);
        ambient.light = new SCNLight(); ambient.light.type = SCNLight.LightType.ambient; root.addChildNode(ambient);
        for (var i = 0; i < suns.Count; i++)
        {
            var sun = suns[i];
            sun.name = $"Sun {i + 1}"; sun.light = new SCNLight(); sun.light.type = SCNLight.LightType.directional;
            // Forward shadows attenuate each star separately.
            sun.light.castsShadow = true; sun.light.shadowMode = SCNShadowMode.forward;
            var resolution = i == 0 ? 4096 : 2048;
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
            var transform = sun.worldTransform;
            var right = new Double3(transform.m11, transform.m12, transform.m13);
            var up = new Double3(transform.m21, transform.m22, transform.m23);
            var back = new Double3(transform.m31, transform.m32, transform.m33);
            var texel = 116 / (double)sun.light.shadowMapSize.width;
            double snap(double value) => rounded(value / texel) * texel;
            var anchor = right * snap(Simd.dot(center, right)) + up * snap(Simd.dot(center, up)) + back * snap(Simd.dot(center, back));
            var p = anchor + back * 80; sun.position = new SCNVector3(p.x, p.y, p.z);
        }
    }
    public void attach(SCNNode camera)
    {
        var weak = new WeakReference<SCNNode>(camera);
        dome.constraints = new() { SCNTransformConstraint.positionConstraint(true, (_, _) => weak.TryGetTarget(out var c) ? c.presentation.worldPosition : SCNVector3Zero) };
        if (camera.camera is not SCNCamera lens) { return; }
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
        var elevation = max(0, value.directions.Max(d => d.y));
        var day = min(1, elevation / 0.55);
        var evening = (float)(value.fraction > 0.5 ? 1 : 0);
        var lowZenith = new Float3(0.018f, 0.070f, 0.17f) * (1 - evening) + new Float3(0.065f, 0.027f, 0.10f) * evening;
        var lowHorizon = new Float3(0.95f, 0.36f, 0.09f) * (1 - evening) + new Float3(0.85f, 0.19f, 0.035f) * evening;
        var zenith = lowZenith + (new Float3(0.20f, 0.38f, 0.62f) - lowZenith) * (float)day;
        var horizon = lowHorizon + (new Float3(0.65f, 0.72f, 0.75f) - lowHorizon) * (float)day;
        skyMaterial.setValue((float)day, "daylight");
        var band = new Float3(0.04f, 0.055f, 0.06f) * (1 - evening) + new Float3(0.22f, 0.045f, 0.085f) * evening;
        skyMaterial.setValue(NSValue.scnVector3(new SCNVector3(band.x, band.y, band.z)), "duskBand");
        NSColor ink(Float3 p) => NSColor.calibratedRed((CGFloat)p.x, (CGFloat)p.y, (CGFloat)p.z, 1);
        foreach (var (key, v) in new[] { ("zenith", zenith), ("horizon", horizon) }) { skyMaterial.setValue(NSValue.scnVector3(new SCNVector3(v.x, v.y, v.z)), key); }
        for (var i = 0; i < 2; i++)
        {
            Double3 d = value.directions[i]; var height = max(0, d.y);
            // Kasten-style air mass approximation; Beer-Lambert extinction
            // removes more blue than red along a long horizon path.
            var altitude = asin(height) * 180 / Math.PI;
            var mass = 1 / (height + 0.50572 * pow(altitude + 6.07995, -1.6364));
            var attenuation = new Double3(exp(-0.035 * mass), exp(-0.070 * mass), exp(-0.15 * mass));
            var intrinsic = i == 0 ? new Double3(1, 0.94, 0.83) : new Double3(1, 0.73, 0.46);
            var rgb = intrinsic * attenuation;
            var tint = new Float3((float)rgb.x, (float)rgb.y, (float)rgb.z);
            var position = d * 80; suns[i].position = new SCNVector3(position.x, position.y, position.z);
            suns[i].look(at: SCNVector3Zero, up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
            suns[i].light.color = ink(tint);
            suns[i].light.intensity = (i == 0 ? 1550 : 470) * (stormActive ? 0.20 : 1);
            skyMaterial.setValue(NSValue.scnVector3(new SCNVector3(d.x, d.y, d.z)), i == 0 ? "sunA" : "sunB");
            skyMaterial.setValue(NSValue.scnVector3(new SCNVector3(tint.x, tint.y, tint.z)), i == 0 ? "tintA" : "tintB");
        }
        // Open-sky fill keeps racing surfaces readable in backlight.
        ambient.light.color = ink(new Float3(0.54f, 0.62f, 0.78f)); ambient.light.intensity = 260 - 70 * day;
        if (!scene.TryGetTarget(out var target)) { return; }
        target.fogColor = ink(horizon); target.fogStartDistance = 115; target.fogEndDistance = 240;
        // A diffuse sky probe has no baked sun at the old lighting direction.
        target.lightingEnvironment.contents = stormActive ? skyProbe(zenith: new Float3(0.46f, 0.34f, 0.23f), horizon: new Float3(0.68f, 0.50f, 0.32f)) : skyProbe(zenith: zenith, horizon: horizon);
        target.lightingEnvironment.intensity = stormActive ? 0.75 : 0.95 - 0.20 * day;
        if (stormActive)
        {
            target.fogColor = color(0xae865b); target.fogStartDistance = 3; target.fogEndDistance = 65;
            ambient.light.color = color(0xd4b48a); ambient.light.intensity = 320;
        }
    }
    private NSImage skyProbe(Float3 zenith, Float3 horizon)
    {
        int w = 64, h = 32;
        var bitmap = new NSBitmapImageRep(bitmapDataPlanes: null, pixelsWide: w, pixelsHigh: h, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: NSColorSpaceName.deviceRGB, bytesPerRow: w * 4, bitsPerPixel: 32);
        for (var y = 0; y < h; y++)
        {
            var t = (float)pow(max(0, cos((double)y / (double)(h - 1) * Math.PI)), 0.42);
            var rgb = (horizon * (1 - t) + zenith * t) * (y > h / 2 ? 0.55f : 1);
            for (var x = 0; x < w; x++) { var j = (y * w + x) * 4; for (var c = 0; c < 3; c++) { bitmap.bitmapData[j + c] = (byte)max(0, min(255, rgb[c] * 255)); } bitmap.bitmapData[j + 3] = 255; }
        }
        var image = new NSImage(new NSSize(w, h)); image.addRepresentation(bitmap); return image;
    }
}

// DeformableSand.swift (dune stream): only the field the race physics reads.
public sealed class DeformableSand
{
    public SandDeformation field => null;
    public void reset() { }
}

// DirtWorld.swift (world stream). Only what the town captures need: the scene with BinarySky, the town,
// the escape routes for DirtRacePhysics, and the ground below the town (DirtWorld's "Town base terrain":
// TownGround.coveredTerrain with the packed-earth material and TownGround.terrainSurface, plus
// DesertWorld's flat 4 km horizon plane). The race track, its walls and gate, the dunes, dust, trails and
// the sandstorm visuals are NOT built here.
public sealed class DirtWorld
{
    public readonly SCNScene scene = new SCNScene();
    public readonly DeformableSand duneSand = new DeformableSand();
    public BinarySky sky { get; private set; }
    public readonly TownWorld town;
    public readonly Double2[][] escapeRoutes;
    public WeakReference<SCNNode> cameraReference;
    public SCNNode camera { get => cameraReference != null && cameraReference.TryGetTarget(out var c) ? c : null; set => cameraReference = value == null ? null : new WeakReference<SCNNode>(value); }
    private static NSColor color(uint hex, double alpha = 1) => NSColor.srgbRed((double)((hex >> 16) & 255) / 255, (double)((hex >> 8) & 255) / 255, (double)(hex & 255) / 255, alpha);
    private static SCNMaterial material(uint hex, double metal = 0, double roughness = 0.6)
    {
        var m = new SCNMaterial(); m.lightingModel = SCNMaterial.LightingModel.physicallyBased;
        m.diffuse.contents = color(hex); m.metalness.contents = metal; m.roughness.contents = roughness; m.isDoubleSided = true;
        return m;
    }
    private NSImage packedEarthTexture()
    {
        var size = 512;
        var bitmap = new NSBitmapImageRep(bitmapDataPlanes: null, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: NSColorSpaceName.deviceRGB, bytesPerRow: size * 4, bitsPerPixel: 32);
        var bytes = bitmap.bitmapData;
        for (var y = 0; y < size; y++) { for (var x = 0; x < size; x++) {
            var seed = unchecked((uint)((long)x * 374761393L + (long)y * 668265263L));
            seed = unchecked((seed ^ (seed >> 13)) * 1274126177u);
            var grain = (double)((seed ^ (seed >> 16)) & 255) / 255;
            double u = (double)x / (double)size, v = (double)y / (double)size;
            var soil = (CityMaterials.surfaceNoise(u, v, cells: 5, seed: 11) - 0.5) * 7 + (CityMaterials.surfaceNoise(u, v, cells: 21, seed: 31) - 0.5) * 5;
            var pebble = grain > 0.984 ? -19.0 : 0.0;
            var value = soil + (grain - 0.5) * 16 + pebble;
            var i = (y * size + x) * 4;
            bytes[i] = (byte)(157 + value); bytes[i + 1] = (byte)(140 + value); bytes[i + 2] = (byte)(115 + value); bytes[i + 3] = 255;
        }}
        var image = new NSImage(new NSSize(size, size)); image.addRepresentation(bitmap); return image;
    }
    public DirtWorld(Action<double, string> progress = null)
    {
        town = new TownWorld(progress: (fraction, label) => progress?.Invoke(0.05 + fraction * 0.43, label));
        progress?.Invoke(0.49, "Planning routes through town");
        escapeRoutes = PostRaceEscape.makeRoutes(city: town.collisionWorld);
        sky = new BinarySky(scene: scene);
        var earth = material(0x827656, roughness: 1);
        earth.diffuse.contents = packedEarthTexture();
        earth.normal.contents = null;
        foreach (var channel in new[] { earth.diffuse, earth.normal })
        {
            channel.wrapS = SCNWrapMode.repeat; channel.wrapT = SCNWrapMode.repeat;
            channel.contentsTransform = SCNMatrix4MakeScale(64, 64, 1);
        }
        var terrain = new SCNNode(TownGround.coveredTerrain(earth)); terrain.name = "Town base terrain"; terrain.eulerAngles.x = -Math.PI / 2; terrain.position.y = -0.025;
        scene.rootNode.addChildNode(terrain);
        // DesertWorld.addDesertTerrain: the earth shares the dunes' terrain shading, and a flat plane fills
        // beyond the (not built) dune field.
        var sand = earth.copy();
        sand.diffuse.contentsTransform = SCNMatrix4Identity;
        sand.roughness.contents = 0.94;
        sand.shaderModifiers = new() { [SCNShaderModifierEntryPoint.surface] = TownGround.terrainSurface };
        earth.shaderModifiers = sand.shaderModifiers;
        var horizon = new SCNNode(new SCNPlane(4000, 4000));
        horizon.geometry.materials = new() { earth }; horizon.eulerAngles.x = -Math.PI / 2; horizon.position.y = -0.05;
        horizon.castsShadow = false; scene.rootNode.addChildNode(horizon);
        scene.rootNode.addChildNode(town.root);
    }
    public Sandstorm storm { get; private set; } = new Sandstorm();
    /// DirtWorld.configureStorm without the sandstorm visuals (SandstormWorld is not built here).
    public void configureStorm(Sandstorm value) { storm = value; town.setStorm(value.enabled); sky.setStorm(value.enabled); }
    public void reset() { town.reset(); duneSand.reset(); }
    public void updateGate(CityGate gate) { }
}
