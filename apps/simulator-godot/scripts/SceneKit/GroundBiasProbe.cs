using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Marvin.SceneKit;

/// <summary>
/// Facade probe for SceneKit's shadow bias, mirroring the SceneKit probes that measured it (Swift copies in
/// reference/calibration/tools: groundbias.swift, biasscale.swift, selfshadow.swift; the game's sun: fixed box,
/// orthographicScale 58, map 4096, radius 3, 8 samples, bias 0.6, black shadows, a sun at elevation e):
/// A: a sunlit 1 m wall standing on a shadow-casting 8 m ground plane; the wall face's brightness at heights 2 mm to
///    20 cm with the ground casting and not casting. SceneKit: at most 0.004 darker at 2 mm (e 5-35 degrees). The
///    facade before its caster slope bias: 0.20 darker at 2 mm, up to 5-8 cm (Godot's PCF taps saw the ground).
/// B: a 1 cm plate at height g above the casting ground; the ground's brightness inside the plate's shadow beyond its far
///    edge. SceneKit's shadow is half there at g = 6.5-10 cm (e 8-35 degrees).
/// C: B at 20 degrees for several maps, radii and box sizes, normalised (0 lit, 1 shadowed). SceneKit's half-shadow gap
///    is about 1.3 cm + 2.2 of its texels (texel 0.49 / 1.42 / 2.83 / 5.66 cm: 2.4 / 4.8 / 7.5 / 15 cm).
/// D: a lit wall at 0-85 degrees to the light and the ground: self-shadowing (SceneKit at most 0.007 at 75-85 degrees).
///   tools/godot -- --ground-bias-probe DIR      prints the tables and writes DIR/wall-eN.png, DIR/plate-e8-gG.png
/// </summary>
public static class GroundBiasProbe
{
    [Marvin.GameMode("--ground-bias-probe")]
    public static void Run(string dir, SceneTree tree)
    {
        foreach (var elevation in new[] { 5.0, 8.0, 12.0, 20.0, 35.0 })
        {
            var rows = new List<string>();
            foreach (var casts in new[] { true, false })
            {
                var scene = new SCNScene();
                Ground(scene, casts);
                var wall = new SCNNode(new SCNBox(2, 1, 0.4, 0)) { position = new SCNVector3(0, 0.5, 0.2) };
                wall.geometry.materials = new() { Lambert(0.8) }; scene.rootNode.addChildNode(wall);
                Sun(scene, elevation * Math.PI / 180);
                var camera = new SCNNode { camera = new SCNCamera { fieldOfView = 4.58, zNear = 0.1, zFar = 50 } };
                camera.position = new SCNVector3(0, 0.2, -5); camera.eulerAngles.y = Math.PI;
                var image = Snapshot(scene, camera, 400);
                if (casts) image.SavePng(System.IO.Path.Combine(dir, $"wall-e{(int)elevation}.png"));
                var values = new[] { 0.002, 0.005, 0.01, 0.02, 0.03, 0.05, 0.08, 0.12, 0.2 }.Select(h =>
                {
                    int row = (int)(200 + (0.2 - h) * 1000); double sum = 0;
                    for (int x = 150; x < 250; x++) sum += image.GetPixel(x, row).R;
                    return (sum / 100).ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                });
                rows.Add((casts ? "cast " : "none ") + string.Join(" ", values));
            }
            GD.Print($"A e={(int)elevation} h[2mm,5mm,1cm,2cm,3cm,5cm,8cm,12cm,20cm]:\n  " + string.Join("\n  ", rows));
        }
        foreach (var elevation in new[] { 5.0, 8.0, 12.0, 20.0, 35.0 })
        {
            var line = new List<string>();
            foreach (var g in new[] { 0.005, 0.01, 0.02, 0.04, 0.08, 0.16, 0.32 })
            {
                var scene = new SCNScene();
                Ground(scene, true);
                var plate = new SCNNode(new SCNBox(1, 0.01, 1, 0)) { position = new SCNVector3(0, g + 0.005, 0) };
                plate.geometry.materials = new() { Lambert(0.8) }; scene.rootNode.addChildNode(plate);
                Sun(scene, elevation * Math.PI / 180);
                double shift = g / Math.Tan(elevation * Math.PI / 180), sampleZ = 0.5 + Math.Min(shift, 1.0) * 0.5;
                var camera = new SCNNode { camera = new SCNCamera { usesOrthographicProjection = true, orthographicScale = 1.5, zNear = 0.1, zFar = 50 } };
                camera.position = new SCNVector3(0, 10, sampleZ); camera.eulerAngles.x = -Math.PI / 2;
                var image = Snapshot(scene, camera, 300);
                double sum = 0; for (int x = 140; x < 160; x++) sum += image.GetPixel(x, 150).R;
                line.Add($"g{g:F3}:{sum / 20:F3}".Replace(',', '.'));
                if (elevation == 8) image.SavePng(System.IO.Path.Combine(dir, $"plate-e8-g{g.ToString(System.Globalization.CultureInfo.InvariantCulture)}.png"));
            }
            GD.Print($"B e={(int)elevation} " + string.Join(" ", line));
        }
        // C: the plate at 20 degrees for several maps, radii and box sizes, normalised between lit (0) and shadowed (1).
        foreach (var (map, radius, scale) in new[] { (4096.0, 3.0, 58.0), (2048, 2, 58), (2048, 3, 58), (4096, 1, 58), (4096, 8, 58), (4096, 3, 29), (4096, 3, 10) })
        {
            double e = 20 * Math.PI / 180;
            double lit = PlateValue(e, -1, map, radius, scale), dark = PlateValue(e, 0.34, map, radius, scale);
            var line = new[] { 0.0, 0.01, 0.02, 0.04, 0.06, 0.08, 0.10, 0.12, 0.16, 0.20, 0.26 }.Select(g =>
                $"{g:F2}:{(lit - PlateValue(e, g, map, radius, scale)) / Math.Max(1e-6, lit - dark):F2}".Replace(',', '.'));
            GD.Print($"C map {map} radius {radius} scale {scale} lit {lit:F3} dark {dark:F3}: " + string.Join(" ", line));
        }
        // D: self-shadowing of lit surfaces: a 2 x 2 m wall floating 1 m above the ground, turned by phi; the wall face with
        // the wall casting / not casting, and the ground in front of it with the ground casting / not casting.
        // SceneKit: at most 0.007 darker at phi 75-85, the ground 0.001 at 8 degrees.
        foreach (var elevation in new[] { 8.0, 20.0, 35.0 })
        {
            double e = elevation * Math.PI / 180;
            var line = new[] { 0.0, 30, 60, 75, 85 }.Select(phi =>
                $"phi{phi:F0}:{SelfShadowValue(e, phi * Math.PI / 180, true, true, false):F3}/{SelfShadowValue(e, phi * Math.PI / 180, false, true, false):F3}".Replace(',', '.'));
            GD.Print($"D e={elevation:F0} wall " + string.Join(" ", line) + $"  ground {SelfShadowValue(e, 0, true, true, true):F3}/{SelfShadowValue(e, 0, true, false, true):F3}".Replace(',', '.'));
        }
        tree.Quit();
    }

    private static double SelfShadowValue(double e, double phi, bool wallCasts, bool groundCasts, bool sampleGround)
    {
        var scene = new SCNScene();
        Ground(scene, groundCasts);
        var wall = new SCNNode(new SCNBox(2, 2, 0.2, 0)) { position = new SCNVector3(0, 2, 0) };
        wall.eulerAngles.y = phi; wall.geometry.materials = new() { Lambert(0.8) }; wall.castsShadow = wallCasts;
        scene.rootNode.addChildNode(wall);
        Sun(scene, e);
        var camera = new SCNNode { camera = new SCNCamera { zNear = 0.1, zFar = 100, fieldOfView = 10 } };
        if (sampleGround) { camera.position = new SCNVector3(0, 3, -6); camera.eulerAngles = new SCNVector3(-0.46, Math.PI, 0); }
        else { camera.position = new SCNVector3(-Math.Sin(phi) * 8, 2, -Math.Cos(phi) * 8); camera.eulerAngles.y = Math.PI + phi; }
        var image = Snapshot(scene, camera, 200);
        double sum = 0;
        for (int y = 80; y < 120; y++) for (int x = 80; x < 120; x++) sum += image.GetPixel(x, y).R;
        return sum / 1600;
    }

    private static double PlateValue(double e, double g, double map, double radius, double scale)
    {
        var scene = new SCNScene();
        Ground(scene, true);
        if (g >= 0)
        {
            var plate = new SCNNode(new SCNBox(1, 0.01, 1, 0)) { position = new SCNVector3(0, g + 0.005, 0) };
            plate.geometry.materials = new() { Lambert(0.8) }; scene.rootNode.addChildNode(plate);
        }
        Sun(scene, e, map, radius, scale);
        double shift = Math.Max(0, g) / Math.Tan(e), z = 0.5 + Math.Min(shift, 1.0) * 0.5;
        var camera = new SCNNode { camera = new SCNCamera { usesOrthographicProjection = true, orthographicScale = 1.5, zNear = 0.1, zFar = 50 } };
        camera.position = new SCNVector3(0, 10, z); camera.eulerAngles.x = -Math.PI / 2;
        var image = Snapshot(scene, camera, 300);
        double sum = 0; for (int x = 140; x < 160; x++) sum += image.GetPixel(x, 150).R;
        return sum / 20;
    }

    private static SCNMaterial Lambert(double white) { var m = new SCNMaterial { lightingModel = SCNMaterial.LightingModel.lambert }; m.diffuse.contents = NSColor.whiteAlpha(white, 1); return m; }
    private static void Ground(SCNScene scene, bool casts)
    {
        var ground = new SCNNode(new SCNPlane(8, 8)); ground.eulerAngles.x = -Math.PI / 2;
        ground.geometry.materials = new() { Lambert(0.6) }; ground.castsShadow = casts;
        scene.rootNode.addChildNode(ground);
    }
    private static void Sun(SCNScene scene, double elevation, double map = 4096, double radius = 3, double scale = 58)
    {
        var light = new SCNLight { type = SCNLight.LightType.directional, intensity = 1000 };
        light.castsShadow = true; light.shadowMode = SCNShadowMode.forward; light.shadowMapSize = new CGSize(map, map);
        light.automaticallyAdjustsShadowProjection = false; light.zNear = 0.1; light.zFar = 220;
        light.orthographicScale = scale; light.shadowRadius = radius; light.shadowSampleCount = 8;
        light.shadowColor = NSColor.black; light.shadowBias = 0.6;
        // Light travels towards +z, descending at `elevation`: it lights faces whose normal is -z.
        var node = new SCNNode { light = light };
        node.eulerAngles = new SCNVector3(-elevation, Math.PI, 0);
        node.position = new SCNVector3(0, Math.Sin(elevation) * 80, -Math.Cos(elevation) * 80);
        scene.rootNode.addChildNode(node);
        scene.rootNode.addChildNode(new SCNNode { light = new SCNLight { type = SCNLight.LightType.ambient, intensity = 150 } });
    }
    private static Image Snapshot(SCNScene scene, SCNNode camera, int size)
    {
        scene.rootNode.addChildNode(camera);
        var renderer = new SCNRenderer(null, null) { scene = scene, pointOfView = camera };
        renderer.SnapshotImage(new Vector2I(size, size), SCNAntialiasingMode.none);
        var image = renderer.SnapshotImage(new Vector2I(size, size), SCNAntialiasingMode.none);
        image.Convert(Image.Format.Rgba8);
        return image;
    }
}
