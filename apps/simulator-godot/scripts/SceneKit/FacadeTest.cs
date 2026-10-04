using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;

namespace Marvin.SceneKit;

/// <summary>
/// `tools/godot -- --facade-test DIR`: renders scenes built only with the SceneKit
/// facade, writes PNGs and measurements.json, then quits. The same scenes are
/// rendered by SceneKit with tools/scenekit-reference/FacadeReference.swift, so the
/// two outputs can be compared pixel by pixel (that script also builds side-by-side
/// images). Keep both files in step: each scene has the same name and construction.
/// </summary>
public static class FacadeTest
{
    private static readonly SCNRenderer renderer = new(null, null);
    private static readonly Dictionary<string, double[]> measurements = new();
    private static string dir;

    public static void Run(string outputDirectory, SceneTree tree)
    {
        dir = outputDirectory;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        DirAccess.MakeDirRecursiveAbsolute(dir);
        GD.Print($"facade-test: writing to {dir}");
        Calibration();
        VisualPrimitives();
        VisualSky();
        VisualGround();
        VisualMaterials();
        VisualSign();
        VisualTransforms();
        var json = new StringBuilder("{\n");
        json.Append(string.Join(",\n", measurements.Select(kv => $"  \"{kv.Key}\": [{string.Join(", ", kv.Value.Select(v => v.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)))}]")));
        json.Append("\n}\n");
        using (var f = FileAccess.Open(dir.PathJoin("measurements.json"), FileAccess.ModeFlags.Write)) f.StoreString(json.ToString());
        GD.Print($"facade-test: {measurements.Count} measurements, {ShaderComposer.CompiledShaders} composed shaders");
        tree.Quit();
    }

    // ---- helpers mirroring FacadeReference.swift
    private static NSColor color(uint hex, double alpha = 1) =>
        NSColor.srgbRed(((hex >> 16) & 255) / 255.0, ((hex >> 8) & 255) / 255.0, (hex & 255) / 255.0, alpha);
    private static NSColor gray(double v) => NSColor.srgbRed(v, v, v, 1);
    private static SCNMaterial mat(SCNMaterial.LightingModel model, object diffuse = null, double rough = 1, double metal = 0)
    {
        var m = new SCNMaterial { lightingModel = model };
        m.diffuse.contents = diffuse ?? NSColor.white; m.roughness.contents = rough; m.metalness.contents = metal;
        return m;
    }
    private static SCNNode light(SCNScene scene, SCNLight.LightType type, double intensity, SCNVector3 euler = default, NSColor color = null)
    {
        var n = new SCNNode { light = new SCNLight() };
        n.light.type = type; n.light.intensity = intensity; n.light.color = color ?? NSColor.white; n.eulerAngles = euler;
        scene.rootNode.addChildNode(n);
        return n;
    }
    private static NSImage uniformImage(byte v, int w = 64, int h = 32)
    {
        var bitmap = new NSBitmapImageRep(bitmapDataPlanes: null, pixelsWide: w, pixelsHigh: h, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: NSColorSpaceName.deviceRGB, bytesPerRow: w * 4, bitsPerPixel: 32);
        for (int i = 0; i < w * h; i++) { bitmap.bitmapData[i * 4] = v; bitmap.bitmapData[i * 4 + 1] = v; bitmap.bitmapData[i * 4 + 2] = v; bitmap.bitmapData[i * 4 + 3] = 255; }
        var image = new NSImage(new NSSize(w, h)); image.addRepresentation(bitmap); return image;
    }
    private static (SCNScene, SCNNode) flatScene(SCNMaterial material, SCNGeometry geometry = null, bool hdr = false, Action<SCNScene> configure = null)
    {
        var scene = new SCNScene(); scene.background.contents = NSColor.black;
        var plane = geometry ?? new SCNPlane(4, 4);
        plane.materials = new() { material };
        scene.rootNode.addChildNode(new SCNNode(plane));
        var cam = new SCNNode { camera = new SCNCamera() };
        cam.camera.usesOrthographicProjection = true; cam.camera.orthographicScale = 1;
        cam.camera.wantsHDR = hdr; cam.camera.wantsExposureAdaptation = false;
        cam.position = new SCNVector3(0, 0, 5); scene.rootNode.addChildNode(cam);
        configure?.Invoke(scene);
        return (scene, cam);
    }
    /// <summary>MARVIN_FACADE_SCALE (default 1) scales the visual scenes (not the 64 px calibration renders).</summary>
    private static readonly int visualScale = int.TryParse(System.Environment.GetEnvironmentVariable("MARVIN_FACADE_SCALE"), out var s) && s > 0 ? s : 1;
    private static Image render(SCNScene scene, SCNNode camera, int w, int h, SCNAntialiasingMode aa = SCNAntialiasingMode.none)
    {
        if (w >= 320) { w *= visualScale; h *= visualScale; }
        renderer.scene = scene; renderer.pointOfView = camera;
        return renderer.SnapshotImage(new Vector2I(w, h), aa);
    }
    private static double lin(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    /// <summary>Records the linear value of the pixel at (x, y) (top-left origin) and returns it.</summary>
    private static double[] measure(string name, Image img, int x = -1, int y = -1)
    {
        if (x < 0) x = img.GetWidth() / 2;
        if (y < 0) y = img.GetHeight() / 2;
        var c = img.GetPixel(x, y);
        var v = new[] { lin(c.R), lin(c.G), lin(c.B) };
        measurements[name] = v;
        return v;
    }
    private static void cal(string name, (SCNScene, SCNNode) s) => measure(name, render(s.Item1, s.Item2, 64, 64));
    private static void save(string name, Image img) => img.SavePng(dir.PathJoin(name + ".png"));
    private static SCNGeometry coloredPlane(float[] c, bool normals = true)
    {
        var v = new[] { new SCNVector3(-2, -2, 0), new SCNVector3(2, -2, 0), new SCNVector3(2, 2, 0), new SCNVector3(-2, 2, 0) };
        var colors = c.Concat(c).Concat(c).Concat(c).ToArray();
        var cs = new SCNGeometrySource(SCNGeometrySource.Bytes(colors), SCNGeometrySourceSemantic.color, 4, true, 4, 4, 0, 16);
        var sources = new List<SCNGeometrySource> { SCNGeometrySource.vertices(v), cs };
        if (normals) sources.Add(SCNGeometrySource.normals(Enumerable.Repeat(new SCNVector3(0, 0, 1), 4).ToArray()));
        return new SCNGeometry(sources, new[] { new SCNGeometryElement(new[] { 0, 1, 2, 0, 2, 3 }, SCNGeometryPrimitiveType.triangles) });
    }

    // =====================================================================
    private static void Calibration()
    {
        cal("C_constant_srgb_0.5", flatScene(mat(SCNMaterial.LightingModel.constant, gray(0.5))));
        cal("C_constant_calibrated_0.5", flatScene(mat(SCNMaterial.LightingModel.constant, NSColor.calibratedRed(0.5, 0.5, 0.5, 1))));
        { var m = mat(SCNMaterial.LightingModel.constant, gray(0.5)); m.emission.contents = gray(0.25); cal("C_constant_plus_emission", flatScene(m)); }
        { var m = mat(SCNMaterial.LightingModel.constant, gray(0.5)); cal("C_constant_ambient_ignored", flatScene(m, configure: s => light(s, SCNLight.LightType.ambient, 1000))); }
        { var m = mat(SCNMaterial.LightingModel.constant, gray(0.5)); m.multiply.contents = gray(0.5); cal("C_constant_multiply", flatScene(m)); }
        foreach (var (model, tag) in new[] { (SCNMaterial.LightingModel.physicallyBased, "pbr"), (SCNMaterial.LightingModel.lambert, "lambert"), (SCNMaterial.LightingModel.blinn, "blinn") })
        {
            cal($"L_{tag}_dir1000", flatScene(mat(model, gray(0.5)), configure: s => light(s, SCNLight.LightType.directional, 1000)));
            cal($"L_{tag}_dir500", flatScene(mat(model, gray(0.5)), configure: s => light(s, SCNLight.LightType.directional, 500)));
            cal($"L_{tag}_dir1000_60deg", flatScene(mat(model, gray(0.5)), configure: s => light(s, SCNLight.LightType.directional, 1000, new SCNVector3(Math.PI / 3, 0, 0))));
        }
        cal("L_pbr_diffuse0.25_dir1000", flatScene(mat(SCNMaterial.LightingModel.physicallyBased, gray(0.25)), configure: s => light(s, SCNLight.LightType.directional, 1000)));
        cal("L_pbr_calibrated_light", flatScene(mat(SCNMaterial.LightingModel.physicallyBased, gray(0.5)), configure: s => light(s, SCNLight.LightType.directional, 1000, color: NSColor.calibratedRed(0.5, 0.5, 0.5, 1))));
        foreach (var rough in new[] { 0.0, 0.3, 0.6, 1.0 })
        {
            cal($"L_pbr_rough{rough}_dir1000", flatScene(mat(SCNMaterial.LightingModel.physicallyBased, gray(0.5), rough, 0), configure: s => light(s, SCNLight.LightType.directional, 1000)));
            cal($"L_pbr_metal_rough{rough}_dir1000", flatScene(mat(SCNMaterial.LightingModel.physicallyBased, gray(0.5), rough, 1), configure: s => light(s, SCNLight.LightType.directional, 1000)));
            cal($"L_pbr_rough{rough}_dir1000_60deg", flatScene(mat(SCNMaterial.LightingModel.physicallyBased, gray(0.5), rough, 0), configure: s => light(s, SCNLight.LightType.directional, 1000, new SCNVector3(Math.PI / 3, 0, 0))));
            cal($"I_pbr_rough{rough}_env255", flatScene(mat(SCNMaterial.LightingModel.physicallyBased, gray(0.5), rough, 0), configure: s => { s.lightingEnvironment.contents = uniformImage(255); }));
            cal($"I_pbr_metal_rough{rough}_env128", flatScene(mat(SCNMaterial.LightingModel.physicallyBased, gray(0.5), rough, 1), configure: s => { s.lightingEnvironment.contents = uniformImage(128); }));
        }
        cal("I_pbr_env255_intensity0.5", flatScene(mat(SCNMaterial.LightingModel.physicallyBased, gray(0.5)), configure: s => { s.lightingEnvironment.contents = uniformImage(255); s.lightingEnvironment.intensity = 0.5; }));
        cal("I_lambert_env255", flatScene(mat(SCNMaterial.LightingModel.lambert, gray(0.5)), configure: s => { s.lightingEnvironment.contents = uniformImage(255); }));
        cal("A_pbr_ambient1000", flatScene(mat(SCNMaterial.LightingModel.physicallyBased, gray(0.5)), configure: s => light(s, SCNLight.LightType.ambient, 1000)));
        cal("A_pbr_ambient500", flatScene(mat(SCNMaterial.LightingModel.physicallyBased, gray(0.5)), configure: s => light(s, SCNLight.LightType.ambient, 500)));
        cal("A_pbr_metal_ambient300", flatScene(mat(SCNMaterial.LightingModel.physicallyBased, gray(0.5), 0.3, 1), configure: s => light(s, SCNLight.LightType.ambient, 300)));
        { var m = mat(SCNMaterial.LightingModel.physicallyBased, NSColor.black); m.emission.contents = gray(0.5); cal("E_pbr_emission", flatScene(m)); }
        { var m = mat(SCNMaterial.LightingModel.physicallyBased, NSColor.black); m.emission.contents = gray(0.5); m.emission.intensity = 0.5; cal("E_pbr_emission_intensity0.5", flatScene(m)); }
        { var m = mat(SCNMaterial.LightingModel.physicallyBased, NSColor.white); m.multiply.contents = gray(0.5); cal("M_pbr_multiply", flatScene(m, configure: s => light(s, SCNLight.LightType.directional, 1000))); }
        { var m = mat(SCNMaterial.LightingModel.physicallyBased, NSColor.white); m.multiply.contents = gray(0.5); m.multiply.intensity = 0.35; cal("M_pbr_multiply_intensity0.35", flatScene(m, configure: s => light(s, SCNLight.LightType.directional, 1000))); }
        cal("V_constant_vertexcolor", flatScene(mat(SCNMaterial.LightingModel.constant), coloredPlane(new[] { 0.5f, 0.2f, 0.1f, 1f })));
        cal("V_pbr_vertexcolor_ambient", flatScene(mat(SCNMaterial.LightingModel.physicallyBased), coloredPlane(new[] { 0.5f, 0.2f, 0.1f, 1f }), configure: s => light(s, SCNLight.LightType.ambient, 1000)));
        cal("N_pbr_no_normals", flatScene(mat(SCNMaterial.LightingModel.physicallyBased, gray(0.5)), coloredPlane(new[] { 1f, 0f, 0f, 1f }, false), configure: s => { light(s, SCNLight.LightType.directional, 1000); light(s, SCNLight.LightType.ambient, 300); }));
        foreach (var k in new[] { 0.1, 0.5, 0.75, 1.5 })
        {
            var m = mat(SCNMaterial.LightingModel.constant, NSColor.black); m.emission.contents = NSColor.white; m.emission.intensity = k;
            cal($"H_hdr_emission{k}", flatScene(m, hdr: true));
        }
        // Fog: perspective camera, plane at distance 15.
        foreach (var (start, end, exponent, fogColor, tag) in new[] { (10.0, 20.0, 1.0, NSColor.black, "F_fog_10_20"), (10.0, 20.0, 2.0, NSColor.black, "F_fog_10_20_exp2"), (10.0, 20.0, 1.0, gray(0.5), "F_fog_10_20_gray") })
        {
            var scene = new SCNScene(); scene.background.contents = NSColor.black;
            var plane = new SCNPlane(60, 60); plane.materials = new() { mat(SCNMaterial.LightingModel.constant, NSColor.white) };
            var pn = new SCNNode(plane); pn.position = new SCNVector3(0, 0, -10); scene.rootNode.addChildNode(pn);
            scene.fogStartDistance = start; scene.fogEndDistance = end; scene.fogDensityExponent = exponent; scene.fogColor = fogColor;
            var cam = new SCNNode { camera = new SCNCamera() }; cam.camera.fieldOfView = 90; cam.camera.zFar = 200; cam.position = new SCNVector3(0, 0, 5); scene.rootNode.addChildNode(cam);
            var img = render(scene, cam, 65, 65);
            measure(tag, img, 32, 32);
            measure(tag + "_corner", img, 0, 0);
        }
        // Blending.
        { var m = mat(SCNMaterial.LightingModel.constant, NSColor.srgbRed(1, 1, 1, 0.5)); cal("Y_color_alpha0.5_over_red", flatScene(m, configure: s => s.background.contents = NSColor.srgbRed(0.5, 0, 0, 1))); }
        { var m = mat(SCNMaterial.LightingModel.constant, NSColor.white); m.transparency = 0.5; cal("Y_transparency0.5_over_red", flatScene(m, configure: s => s.background.contents = NSColor.srgbRed(0.5, 0, 0, 1))); }
        { var m = mat(SCNMaterial.LightingModel.constant, NSColor.white); cal("Y_opacity0.5_over_red", flatScene(m, configure: s => { s.background.contents = NSColor.srgbRed(0.5, 0, 0, 1); s.rootNode.childNodes[0].opacity = 0.5; })); }
        { var m = mat(SCNMaterial.LightingModel.constant, gray(0.5)); m.blendMode = SCNBlendMode.multiply; cal("Y_multiply_blend", flatScene(m, configure: s => s.background.contents = gray(0.8))); }
        {
            var m = mat(SCNMaterial.LightingModel.constant, NSColor.white); m.transparencyMode = SCNTransparencyMode.aOne;
            m.shaderModifiers = new() { [SCNShaderModifierEntryPoint.fragment] = "#pragma transparent\n#pragma body\nALPHA = 0.25;" };
            cal("Y_fragment_alpha_idiom", flatScene(m, configure: s => s.background.contents = NSColor.srgbRed(0.5, 0, 0, 1)));
        }
        // Level of detail: red near geometry, green LOD from 10 m.
        foreach (var (distance, tag) in new[] { (6.0, "D_lod_near"), (14.0, "D_lod_far") })
        {
            var scene = new SCNScene(); scene.background.contents = NSColor.black;
            var near = new SCNBox(2, 2, 2, 0); near.materials = new() { mat(SCNMaterial.LightingModel.constant, NSColor.red) };
            var far = new SCNBox(2, 2, 2, 0); far.materials = new() { mat(SCNMaterial.LightingModel.constant, NSColor.green) };
            near.levelsOfDetail = new[] { new SCNLevelOfDetail(far, worldSpaceDistance: 10) };
            scene.rootNode.addChildNode(new SCNNode(near));
            var cam = new SCNNode { camera = new SCNCamera() }; cam.position = new SCNVector3(0, 0, distance); scene.rootNode.addChildNode(cam);
            cal(tag, (scene, cam));
        }
        // Billboard: a plane rotated away from the camera still faces it.
        {
            var scene = new SCNScene(); scene.background.contents = NSColor.black;
            var plane = new SCNPlane(1, 1); plane.materials = new() { mat(SCNMaterial.LightingModel.constant, gray(0.5)) };
            var n = new SCNNode(plane); n.eulerAngles.y = Math.PI / 2; n.constraints = new() { new SCNBillboardConstraint() };
            scene.rootNode.addChildNode(n);
            var cam = new SCNNode { camera = new SCNCamera() }; cam.position = new SCNVector3(3, 1, 3); cam.look(SCNVector3Zero, new SCNVector3(0, 1, 0), new SCNVector3(0, 0, -1));
            scene.rootNode.addChildNode(cam);
            cal("B_billboard", (scene, cam));
        }
        // Geometry-level shader modifier reading an r32Float MTLTexture argument (DeformableSand pattern).
        {
            var device = MTLCreateSystemDefaultDevice();
            var descriptor = MTLTextureDescriptor.texture2DDescriptor(MTLPixelFormat.r32Float, 2, 1, false);
            var texture = device.makeTexture(descriptor);
            texture.replace(MTLRegionMake2D(0, 0, 2, 1), 0, new float[] { 0.25f, 0.75f }, 8);
            var heights = new SCNMaterialProperty(); heights.contents = texture;
            var plane = new SCNPlane(4, 4);
            plane.materials = new() { mat(SCNMaterial.LightingModel.constant, NSColor.white) };
            plane.shaderModifiers = new() { [SCNShaderModifierEntryPoint.surface] = "#pragma arguments\nsampler2D duneHeights : filter_nearest;\n#pragma body\nALBEDO = vec3(texelFetch(duneHeights, ivec2(1, 0), 0).r, texelFetch(duneHeights, ivec2(0, 0), 0).r, 0.0);" };
            plane.setValue(heights, "duneHeights");
            var scene = new SCNScene(); scene.background.contents = NSColor.black;
            scene.rootNode.addChildNode(new SCNNode(plane));
            var cam = new SCNNode { camera = new SCNCamera() }; cam.camera.usesOrthographicProjection = true; cam.position = new SCNVector3(0, 0, 5); scene.rootNode.addChildNode(cam);
            cal("G_texture_argument", (scene, cam));
        }
        // Materials repeat cyclically over elements: cylinder [side, top, bottom] with two materials.
        {
            var cyl = new SCNCylinder(1.5, 1); cyl.materials = new() { mat(SCNMaterial.LightingModel.constant, NSColor.red), mat(SCNMaterial.LightingModel.constant, NSColor.green) };
            var scene = new SCNScene(); scene.background.contents = NSColor.black;
            scene.rootNode.addChildNode(new SCNNode(cyl));
            foreach (var (y, tag) in new[] { (5.0, "K_cycle_top"), (-5.0, "K_cycle_bottom") })
            {
                var cam = new SCNNode { camera = new SCNCamera() }; cam.camera.usesOrthographicProjection = true;
                cam.position = new SCNVector3(0, y, 0); cam.eulerAngles.x = y > 0 ? -Math.PI / 2 : Math.PI / 2; scene.rootNode.addChildNode(cam);
                cal(tag, (scene, cam));
            }
        }
        // Shadows: sun at 45 degrees, box above floor, top-down orthographic camera.
        foreach (var (mode, alpha, constantFloor, tag) in new[] {
            (SCNShadowMode.forward, 1.0, false, "S_forward_alpha1"), (SCNShadowMode.forward, 0.5, false, "S_forward_alpha0.5"),
            (SCNShadowMode.deferred, 0.24, false, "S_deferred_alpha0.24"), (SCNShadowMode.deferred, 0.24, true, "S_deferred_alpha0.24_constant") })
        {
            var scene = new SCNScene(); scene.background.contents = NSColor.black;
            var floor = new SCNPlane(8, 8); floor.materials = new() { mat(constantFloor ? SCNMaterial.LightingModel.constant : SCNMaterial.LightingModel.physicallyBased, gray(0.8)) };
            var fn = new SCNNode(floor); fn.eulerAngles.x = -Math.PI / 2; scene.rootNode.addChildNode(fn);
            var box = new SCNBox(1, 1, 1, 0); box.materials = new() { mat(SCNMaterial.LightingModel.physicallyBased) };
            var bn = new SCNNode(box); bn.position = new SCNVector3(0, 1, 0); scene.rootNode.addChildNode(bn);
            var sun = light(scene, SCNLight.LightType.directional, 1000, new SCNVector3(-Math.PI / 4, 0, 0));
            sun.light.castsShadow = true; sun.light.shadowMode = mode; sun.light.shadowColor = NSColor.black.withAlphaComponent(alpha); sun.light.orthographicScale = 5;
            light(scene, SCNLight.LightType.ambient, 300);
            var cam = new SCNNode { camera = new SCNCamera() }; cam.camera.usesOrthographicProjection = true; cam.camera.orthographicScale = 4;
            cam.position = new SCNVector3(0, 10, 0); cam.eulerAngles.x = -Math.PI / 2; scene.rootNode.addChildNode(cam);
            var img = render(scene, cam, 64, 64);
            measure(tag + "_lit", img, 32, 4);
            measure(tag + "_shadow", img, 32, 22);
            save(tag, img);
        }
    }

    // =====================================================================
    private static SCNMaterial material(uint hex, double metal = 0, double roughness = 0.6)
    {
        var m = new SCNMaterial { lightingModel = SCNMaterial.LightingModel.physicallyBased };
        m.diffuse.contents = color(hex); m.metalness.contents = metal; m.roughness.contents = roughness; m.isDoubleSided = true;
        return m;
    }

    /// <summary>Primitive tessellation, transforms, shadows and the sandbox-style lighting.</summary>
    private static void VisualPrimitives()
    {
        var scene = new SCNScene();
        scene.background.contents = color(0xdbe4df);
        scene.fogColor = color(0xdbe4df); scene.fogStartDistance = 15; scene.fogEndDistance = 38;
        var camera = new SCNNode { camera = new SCNCamera() };
        camera.camera.fieldOfView = 48; camera.camera.zNear = 0.02; camera.camera.zFar = 80;
        camera.position = new SCNVector3(0, 3.2, 6.5);
        camera.look(new SCNVector3(0, 0.4, 0), new SCNVector3(0, 1, 0), new SCNVector3(0, 0, -1));
        scene.rootNode.addChildNode(camera);
        var ambient = light(scene, SCNLight.LightType.ambient, 550, color: color(0xe7f4ff));
        var sun = light(scene, SCNLight.LightType.directional, 1400, new SCNVector3(-0.85, -0.45, -0.25), color(0xfff1df));
        sun.light.castsShadow = true; sun.light.shadowMode = SCNShadowMode.deferred;
        sun.light.shadowMapSize = new CGSize(4096, 4096); sun.light.shadowSampleCount = 16;
        sun.light.shadowColor = NSColor.black.withAlphaComponent(0.24); sun.light.orthographicScale = 10; sun.light.maximumShadowDistance = 22;
        SCNNode box(double x, double y, double z, double w, double h, double d, SCNMaterial m, double radius = 0)
        {
            var g = new SCNBox(w, h, d, radius); g.materials = new() { m };
            var n = new SCNNode(g); n.position = new SCNVector3(x, y, z); scene.rootNode.addChildNode(n); return n;
        }
        box(0, -0.175, 0, 12.5, 0.3, 10.5, material(0x927455, roughness: 0.95), 0.14);
        box(0, -0.05, 0, 12, 0.05, 10, material(0xb49470, roughness: 0.98), 0.04);
        box(-1.6, 0.4, 0, 0.8, 0.8, 0.8, material(0x96b4a7), 0.07);
        box(1.6, 0.25, 0.8, 0.6, 0.5, 1.2, material(0xd2b59a), 0.07).eulerAngles.y = 0.4;
        var sphere = new SCNSphere(0.45); sphere.materials = new() { material(0xc3c7c9, 0.8, 0.65) };
        var sn = new SCNNode(sphere); sn.position = new SCNVector3(0, 0.45, 0.4); scene.rootNode.addChildNode(sn);
        var cyl = new SCNCylinder(0.3, 0.9); cyl.radialSegmentCount = 48; cyl.materials = new() { material(0x2b8e7f) };
        var cn = new SCNNode(cyl); cn.position = new SCNVector3(0.2, 0.45, -1.4); scene.rootNode.addChildNode(cn);
        var wheel = new SCNCylinder(0.25, 0.12); wheel.materials = new() { material(0x555653, roughness: 0.94) };
        var wn = new SCNNode(wheel); wn.position = new SCNVector3(-1.6, 0.25, 1.3); wn.eulerAngles.z = Math.PI / 2; scene.rootNode.addChildNode(wn);
        // SCNShape arrow (DirtWorld) and a ring with holes (World floor).
        var path = new NSBezierPath(); path.move(new NSPoint(-0.15, -0.12));
        path.line(new NSPoint(0, 0.15)); path.line(new NSPoint(0.15, -0.12)); path.line(new NSPoint(0, -0.02)); path.close();
        var arrowShape = new SCNShape(path, 0); arrowShape.materials = new() { material(0xd6ba85) };
        var arrow = new SCNNode(arrowShape); arrow.scale = new SCNVector3(3, 3, 3); arrow.position = new SCNVector3(1.4, 0.006, 2.2);
        arrow.eulerAngles = new SCNVector3(-Math.PI / 2, 0.6, 0); scene.rootNode.addChildNode(arrow);
        var holes = new NSBezierPath(new NSRect(-1, -0.6, 2, 1.2)); holes.windingRule = NSBezierPath.WindingRule.evenOdd;
        for (int k = 0; k < 2; k++)
        {
            double cx = -0.45 + 0.9 * k;
            for (int i = 0; i < 128; i++)
            {
                double a = i * 2 * Math.PI / 128;
                var p = new NSPoint(cx + 0.35 * Math.Cos(a), 0.35 * Math.Sin(a));
                if (i == 0) holes.move(p); else holes.line(p);
            }
            holes.close();
        }
        var holeShape = new SCNShape(holes, 0); holeShape.materials = new() { material(0xa48766, roughness: 0.98) };
        var hn = new SCNNode(holeShape); hn.position = new SCNVector3(-0.5, 0.003, 2.6); hn.eulerAngles.x = -Math.PI / 2; scene.rootNode.addChildNode(hn);
        // Extruded shape.
        var star = new NSBezierPath();
        for (int i = 0; i < 10; i++) { double a = Math.PI / 2 + i * Math.PI / 5, r = i % 2 == 0 ? 0.35 : 0.15; var p = new NSPoint(r * Math.Cos(a), r * Math.Sin(a)); if (i == 0) star.move(p); else star.line(p); }
        star.close();
        var starShape = new SCNShape(star, 0.1); starShape.materials = new() { material(0xc83a24, roughness: 0.45) };
        var stn = new SCNNode(starShape); stn.position = new SCNVector3(2.6, 0.5, -0.6); stn.eulerAngles.y = -0.5; scene.rootNode.addChildNode(stn);
        // SCNText label laid flat and centred with a pivot (World.swift beacon labels).
        var text = new SCNText("12", 0); text.font = NSFont.monospacedSystemFont(1, NSFont.Weight.semibold); text.flatness = 0.2;
        text.materials = new() { material(0x4e756a) };
        var label = new SCNNode(text);
        var bounds = text.boundingBox;
        double height = bounds.max.y - bounds.min.y, scale = 0.36 / height;
        label.scale = new SCNVector3(scale, scale, scale);
        label.pivot = SCNMatrix4MakeTranslation((bounds.min.x + bounds.max.x) / 2, (bounds.min.y + bounds.max.y) / 2, 0);
        label.eulerAngles.x = -Math.PI / 2; label.position = new SCNVector3(-0.4, 0.0008, 1.4); label.castsShadow = false;
        scene.rootNode.addChildNode(label);
        // Child transforms: a hub with spokes rotating with its parent.
        var hub = new SCNNode(); hub.position = new SCNVector3(2.4, 0.6, 1.2); hub.eulerAngles = new SCNVector3(0.3, -0.7, 0.2);
        for (int i = 0; i < 5; i++)
        {
            var spoke = new SCNBox(0.04, 0.5, 0.08, 0.01); spoke.materials = new() { material(0x8b969e, 0.75, 0.38) };
            var s = new SCNNode(spoke); s.eulerAngles.x = i * 2 * Math.PI / 5; s.position = new SCNVector3(0, Math.Cos(i * 2 * Math.PI / 5) * 0.2, Math.Sin(i * 2 * Math.PI / 5) * 0.2);
            hub.addChildNode(s);
        }
        scene.rootNode.addChildNode(hub);
        var offscreen = render(scene, camera, 640, 400, SCNAntialiasingMode.multisampling4X);
        save("V_primitives", offscreen);
        // The same scene through an on-screen SCNView (Control) must match SCNRenderer.
        var view = new SCNView(new CGRect(0, 0, 640 * visualScale, 400 * visualScale));
        ((SceneTree)Engine.GetMainLoop()).Root.AddChild(view);
        view.scene = scene; view.pointOfView = camera; view.antialiasingMode = SCNAntialiasingMode.multisampling4X;
        var viewPng = view.snapshot().tiffRepresentation;
        System.IO.File.WriteAllBytes(dir.PathJoin("W_scnview.png"), viewPng);
        var viewImage = NSBitmapImageRep.data(viewPng);
        double diff = 0;
        for (int y = 0; y < offscreen.GetHeight(); y += 4)
            for (int x = 0; x < offscreen.GetWidth(); x += 4)
            {
                var a = offscreen.GetPixel(x, y); var (r, g, b, _) = viewImage.Sample(x, y);
                diff += Math.Abs(a.R - r) + Math.Abs(a.G - g) + Math.Abs(a.B - b);
            }
        measurements["W_scnview_vs_scnrenderer_mean_abs"] = new[] { diff / (3.0 * (offscreen.GetWidth() / 4) * (offscreen.GetHeight() / 4)) };
        view.QueueFree();
        _ = ambient;
    }

    /// <summary>BinarySky sky dome (worked example 1 in PORTING.md), fog and HDR bloom.</summary>
    private static void VisualSky()
    {
        var scene = new SCNScene();
        var camera = new SCNNode { camera = new SCNCamera() };
        camera.camera.fieldOfView = 48; camera.camera.zNear = 0.02; camera.camera.zFar = 250;
        camera.camera.wantsHDR = true; camera.camera.wantsExposureAdaptation = false; camera.camera.exposureOffset = 0;
        camera.camera.bloomIntensity = 0.38; camera.camera.bloomThreshold = 1.2; camera.camera.bloomBlurRadius = 12;
        camera.position = new SCNVector3(0, 2, 6);
        scene.rootNode.addChildNode(camera);
        var sky = new SCNMaterial { lightingModel = SCNMaterial.LightingModel.constant, cullMode = SCNCullMode.front, readsFromDepthBuffer = false, writesToDepthBuffer = false };
        sky.shaderModifiers = new()
        {
            [SCNShaderModifierEntryPoint.geometry] = SkyGeometry,
            [SCNShaderModifierEntryPoint.fragment] = SkyFragment,
        };
        var sphere = new SCNSphere(220); sphere.segmentCount = 48; sphere.materials = new() { sky };
        var dome = new SCNNode(sphere); dome.castsShadow = false; dome.renderingOrder = -10000;
        dome.constraints = new() { SCNTransformConstraint.positionConstraint(true, (_, _) => camera.presentation.worldPosition) };
        scene.rootNode.addChildNode(dome);
        // Low evening suns (BinarySky palette for day = 0.25, evening).
        double day = 0.25; double evening = 1;
        Vector3 lowZenith = new Vector3(0.065f, 0.027f, 0.10f), lowHorizon = new Vector3(0.85f, 0.19f, 0.035f);
        var zenith = lowZenith + (new Vector3(0.20f, 0.38f, 0.62f) - lowZenith) * (float)day;
        var horizon = lowHorizon + (new Vector3(0.65f, 0.72f, 0.75f) - lowHorizon) * (float)day;
        var sunA = new SCNVector3(-0.55, 0.14, -0.82).Normalized(); var sunB = new SCNVector3(-0.62, 0.10, -0.78).Normalized();
        sky.setValue(NSValue.scnVector3(sunA), "sunA"); sky.setValue(NSValue.scnVector3(sunB), "sunB");
        sky.setValue(NSValue.scnVector3(SCNVector3.FromGodot(zenith)), "zenith"); sky.setValue(NSValue.scnVector3(SCNVector3.FromGodot(horizon)), "horizon");
        sky.setValue(NSValue.scnVector3(new SCNVector3(1, 0.62, 0.30)), "tintA"); sky.setValue(NSValue.scnVector3(new SCNVector3(1, 0.42, 0.14)), "tintB");
        sky.setValue(NSValue.point(new NSPoint(0.55 * Math.PI / 180 * 6, 0.38 * Math.PI / 180 * 6)), "sunRadii");
        sky.setValue((float)day, "daylight"); sky.setValue(NSValue.scnVector3(new SCNVector3(0.22, 0.045, 0.085)), "duskBand");
        sky.setValue(0f, "storm"); sky.setValue(NSValue.scnVector3(new SCNVector3(0.46, 0.29, 0.14)), "stormTint");
        _ = evening;
        camera.look(new SCNVector3(sunA.x * 20, 1.5, sunA.z * 20), new SCNVector3(0, 1, 0), new SCNVector3(0, 0, -1));
        // Ground with fog toward the horizon colour.
        scene.fogColor = NSColor.calibratedRed(horizon.X, horizon.Y, horizon.Z, 1); scene.fogStartDistance = 15; scene.fogEndDistance = 120;
        var ground = new SCNPlane(400, 400); ground.materials = new() { material(0x827656, roughness: 1) };
        var gn = new SCNNode(ground); gn.eulerAngles.x = -Math.PI / 2; scene.rootNode.addChildNode(gn);
        for (int i = 0; i < 12; i++)
        {
            var post = new SCNBox(0.4, 2.5, 0.4, 0.05); post.materials = new() { material(0x805443, roughness: 0.94) };
            var pn = new SCNNode(post); pn.position = new SCNVector3(-4 + (i % 4) * 2.5 - 8, 1.25, -6 - (i / 4) * 18 - 4); scene.rootNode.addChildNode(pn);
        }
        var s1 = light(scene, SCNLight.LightType.directional, 1550, color: NSColor.calibratedRed(1, 0.62, 0.30, 1));
        s1.position = new SCNVector3(sunA.x * 80, sunA.y * 80, sunA.z * 80); s1.look(SCNVector3Zero, new SCNVector3(0, 1, 0), new SCNVector3(0, 0, -1));
        light(scene, SCNLight.LightType.ambient, 200, color: NSColor.calibratedRed(0.54, 0.62, 0.78, 1));
        save("V_sky", render(scene, camera, 640, 400, SCNAntialiasingMode.multisampling4X));
    }
    internal const string SkyGeometry = @"
#pragma varyings
vec3 skyDirection;
#pragma body
skyDirection = VERTEX;
";
    internal const string SkyFragment = @"
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
";

    /// <summary>TownGround overlays (worked example 2) and DirtTrail ink decals (worked example 3) on a terrain plane.</summary>
    private static void VisualGround()
    {
        var scene = new SCNScene(); scene.background.contents = color(0xb9c5ca);
        var camera = new SCNNode { camera = new SCNCamera() };
        camera.camera.fieldOfView = 48; camera.camera.zNear = 0.02; camera.camera.zFar = 250;
        camera.position = new SCNVector3(30, 9, 30); camera.look(new SCNVector3(36, 0, 18), new SCNVector3(0, 1, 0), new SCNVector3(0, 0, -1));
        scene.rootNode.addChildNode(camera);
        light(scene, SCNLight.LightType.ambient, 260, color: NSColor.calibratedRed(0.54, 0.62, 0.78, 1));
        var sun = light(scene, SCNLight.LightType.directional, 1550, color: NSColor.calibratedRed(1, 0.94, 0.83, 1));
        sun.position = new SCNVector3(-40, 60, 30); sun.look(new SCNVector3(30, 0, 20), new SCNVector3(0, 1, 0), new SCNVector3(0, 0, -1));
        sun.light.castsShadow = true; sun.light.shadowMode = SCNShadowMode.forward; sun.light.automaticallyAdjustsShadowProjection = false; sun.light.orthographicScale = 58;
        var earth = material(0x827656, roughness: 1);
        earth.diffuse.contents = packedEarthTexture();
        earth.diffuse.wrapS = SCNWrapMode.repeat; earth.diffuse.wrapT = SCNWrapMode.repeat;
        earth.diffuse.contentsTransform = SCNMatrix4MakeScale(64, 64, 1);
        earth.shaderModifiers = new() { [SCNShaderModifierEntryPoint.surface] = TerrainSurface };
        // TownGround.coveredTerrain layout: 4 m quads over +-128 m in the XY plane (the node is rotated -pi/2).
        var gv = new List<SCNVector3>(); var guv = new List<CGPoint>(); var gnrm = new List<SCNVector3>(); var gidx = new List<int>();
        for (int y = -128; y < 128; y += 4)
            for (int x = -128; x < 128; x += 4)
            {
                int b = gv.Count;
                foreach (var (dx, dy) in new[] { (0, 0), (4, 0), (4, 4), (0, 4) })
                {
                    gv.Add(new SCNVector3(x + dx, y + dy, 0)); gnrm.Add(new SCNVector3(0, 0, 1));
                    guv.Add(new CGPoint((double)(x + dx + 128) / 256, (double)(128 - y - dy) / 256));
                }
                gidx.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
            }
        var ground = new SCNGeometry(new[] { SCNGeometrySource.vertices(gv), SCNGeometrySource.normals(gnrm), SCNGeometrySource.textureCoordinates(guv) },
            new[] { new SCNGeometryElement(gidx, SCNGeometryPrimitiveType.triangles) });
        ground.materials = new() { earth };
        var gn = new SCNNode(ground); gn.eulerAngles.x = -Math.PI / 2; gn.position = new SCNVector3(0, -0.025, 0); scene.rootNode.addChildNode(gn);
        // Overlay quad with alpha from vertex colours, premultiplied idiom in .fragment.
        var overlay = new SCNMaterial { lightingModel = SCNMaterial.LightingModel.physicallyBased };
        overlay.roughness.contents = 1.0; overlay.diffuse.contents = NSColor.white;
        overlay.writesToDepthBuffer = false; overlay.transparencyMode = SCNTransparencyMode.aOne;
        overlay.shaderModifiers = new()
        {
            [SCNShaderModifierEntryPoint.geometry] = GroundTintGeometry,
            [SCNShaderModifierEntryPoint.surface] = "ALBEDO = groundTint.rgb;",
            [SCNShaderModifierEntryPoint.fragment] = GroundTintFragment,
        };
        var verts = new List<SCNVector3>(); var colors = new List<float>(); var idx = new List<int>();
        for (int k = 0; k < 6; k++)
        {
            double cx = 30 + k * 2.2, cz = 20 - k * 0.8;
            int center = verts.Count; verts.Add(new SCNVector3(cx, -0.019, cz)); colors.AddRange(new[] { 0x8e / 255f, 0x80 / 255f, 0x6a / 255f, 0.6f });
            for (int i = 0; i < 18; i++)
            {
                double a = i * 2 * Math.PI / 18;
                verts.Add(new SCNVector3(cx + Math.Cos(a) * 1.1, -0.019, cz + Math.Sin(a) * 0.8)); colors.AddRange(new[] { 0x8e / 255f, 0x80 / 255f, 0x6a / 255f, 0f });
            }
            for (int i = 0; i < 18; i++) { idx.Add(center); idx.Add(center + 1 + (i + 1) % 18); idx.Add(center + 1 + i); }
        }
        var tint = new SCNGeometrySource(SCNGeometrySource.Bytes(colors), SCNGeometrySourceSemantic.color, verts.Count, true, 4, 4, 0, 16);
        var patches = new SCNGeometry(new[] { SCNGeometrySource.vertices(verts), SCNGeometrySource.normals(Enumerable.Repeat(new SCNVector3(0, 1, 0), verts.Count).ToArray()), tint },
            new[] { new SCNGeometryElement(idx, SCNGeometryPrimitiveType.triangles) });
        patches.materials = new() { overlay };
        var pn = new SCNNode(patches); pn.castsShadow = false; scene.rootNode.addChildNode(pn);
        // Tread marks: constant multiply decals with renderingOrder 10.
        var ink = material(0xffffff, roughness: 1);
        ink.lightingModel = SCNMaterial.LightingModel.constant; ink.blendMode = SCNBlendMode.multiply;
        ink.shaderModifiers = new()
        {
            [SCNShaderModifierEntryPoint.geometry] = TrailGeometry,
            [SCNShaderModifierEntryPoint.surface] = TrailSurface,
            [SCNShaderModifierEntryPoint.fragment] = TrailFragment,
        };
        ink.setValue(0f, "stormTime"); ink.setValue(0f, "stormActive");
        ink.isDoubleSided = true; ink.writesToDepthBuffer = false;
        var tv = new List<SCNVector3>(); var tuv = new List<CGPoint>(); var tcol = new List<float>(); var tidx = new List<int>();
        for (int i = 0; i < 120; i++)
        {
            double t = i * 0.065, x = 33 + t, z = 22 + Math.Sin(t * 0.8) * 1.5;
            foreach (var side in new[] { -0.3, 0.3 })
            {
                int baseIndex = tv.Count;
                foreach (var (sx, sz) in new[] { (-1.0, -1.0), (1.0, -1.0), (1.0, 1.0), (-1.0, 1.0) })
                {
                    tv.Add(new SCNVector3(x + sz * 0.021, 0.007 - 0.025, z + side + sx * 0.08));
                    tuv.Add(new CGPoint((sx + 1) / 2, (sz + 1) / 2)); tcol.AddRange(new[] { 0.24f, 0f, 0f, 1f });
                }
                tidx.AddRange(new[] { baseIndex, baseIndex + 2, baseIndex + 1, baseIndex, baseIndex + 3, baseIndex + 2 });
            }
        }
        var trail = new SCNGeometry(new[] { SCNGeometrySource.vertices(tv), SCNGeometrySource.textureCoordinates(tuv),
            SCNGeometrySource.normals(Enumerable.Repeat(new SCNVector3(0, 1, 0), tv.Count).ToArray()),
            new SCNGeometrySource(SCNGeometrySource.Bytes(tcol), SCNGeometrySourceSemantic.color, tv.Count, true, 4, 4, 0, 16) },
            new[] { new SCNGeometryElement(tidx, SCNGeometryPrimitiveType.triangles) });
        trail.materials = new() { ink };
        var tn = new SCNNode(trail); tn.castsShadow = false; tn.renderingOrder = 10; scene.rootNode.addChildNode(tn);
        var rock = new SCNBox(1.2, 1.2, 1.2, 0.1); rock.materials = new() { material(0xa28a70) };
        var rn = new SCNNode(rock); rn.position = new SCNVector3(37, 0.6, 19); rn.eulerAngles.y = 0.5; scene.rootNode.addChildNode(rn);
        save("V_ground", render(scene, camera, 640, 400, SCNAntialiasingMode.multisampling4X));
    }
    internal const string PigmentFunctions = @"
float townNoise(vec2 p) {
    vec2 i = floor(p), f = fract(p); f = f * f * (3.0 - 2.0 * f);
    vec4 h = fract(sin(vec4(dot(i, vec2(127.1, 311.7)), dot(i + vec2(1, 0), vec2(127.1, 311.7)), dot(i + vec2(0, 1), vec2(127.1, 311.7)), dot(i + 1.0, vec2(127.1, 311.7)))) * 43758.5453);
    return mix(mix(h.x, h.y, f.x), mix(h.z, h.w, f.x), f.y);
}
vec3 townPigment(vec2 p) {
    vec2 warp = vec2(townNoise(p / 31.0), townNoise(p / 37.0 + 19.0)) * 9.0;
    float broad = townNoise((p + warp) / 22.0), fine = townNoise((p + warp) / 5.5);
    float pale = smoothstep(0.28, 0.72, broad * 0.50 + fine * 0.50);
    vec3 soil = mix(vec3(0.255, 0.208, 0.145), vec3(0.46, 0.36, 0.235), pale);
    return soil * (0.89 + 0.22 * townNoise(p / 2.1 + 7.0));
}
";
    internal const string TerrainSurface = PigmentFunctions + @"
#pragma body
vec2 p = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xz;
float angle = atan(p.y, p.x);
float radius = 123.0 + 13.0 * sin(3.0 * angle + 0.4) + 9.0 * cos(5.0 * angle - 0.7) + 6.0 * sin(2.0 * angle);
float desert = smoothstep(radius - 12.0, radius + 32.0, length(p));
float grain = dot(ALBEDO, vec3(0.299, 0.587, 0.114));
float bands = townNoise(p / 43.0);
vec3 dune = vec3(0.64, 0.43, 0.23) * (0.83 + 0.30 * grain) + bands * vec3(0.075, 0.058, 0.029);
vec3 soil = mix(ALBEDO, townPigment(p) * (1.30 + grain), smoothstep(28.0, 42.0, length(p)));
ALBEDO = mix(soil, dune, desert);
float ripple = sin(p.x * 17.0 + p.y * 5.8 + 1.8 * sin(p.y * 0.41) + sin(p.x * 0.22));
NORMAL = normalize(NORMAL + vec3(0.022 * ripple, 0.0, 0.008 * ripple) * desert);
";
    internal const string GroundTintGeometry = @"
#pragma varyings
vec4 groundTint;
#pragma body
groundTint = vec4(pow(max(COLOR.rgb, vec3(0.0)), vec3(2.2)), COLOR.a);
";
    internal const string GroundTintFragment = @"
#pragma transparent
#pragma body
ALPHA = groundTint.a;
";
    internal const string TrailGeometry = @"
#pragma varyings
float impressionStrength;
float trailBorn;
float trailDistance;
#pragma body
impressionStrength = COLOR.r;
trailBorn = COLOR.g;
trailDistance = length((MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz);
";
    internal const string TrailSurface = @"
#pragma transparent
#pragma body
vec2 uv = scn_diffuse_texcoord;
float across = smoothstep(0.0, 0.15, uv.x) * (1.0 - smoothstep(0.85, 1.0, uv.x));
float along = smoothstep(0.0, 0.24, uv.y) * (1.0 - smoothstep(0.65, 1.0, uv.y));
float shade = 1.0 - impressionStrength * across * along;
ALBEDO = vec3(shade); ALPHA = 1.0;
";
    internal const string TrailFragment = @"
#pragma arguments
float stormTime;
float stormActive;
#pragma body
if (stormActive > 0.5) {
    float age = max(0.0, stormTime - trailBorn);
    float visibility = exp(-age / 10.0) * (1.0 - smoothstep(3.0, 65.0, trailDistance));
    // Multiply decals must fade toward white, not toward brown fog.
    vec3 faded = mix(vec3(1.0), ALBEDO, visibility);
    ALBEDO = faded; EMISSION = faded; FOG = vec4(0.0);
}
";
    private static NSImage packedEarthTexture()
    {
        int size = 512;
        var bitmap = new NSBitmapImageRep(bitmapDataPlanes: null, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: NSColorSpaceName.deviceRGB, bytesPerRow: size * 4, bitsPerPixel: 32);
        var bytes = bitmap.bitmapData;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                uint seed = unchecked((uint)(x * 374761393 + y * 668265263));
                seed = unchecked((seed ^ (seed >> 13)) * 1274126177);
                double grain = ((seed ^ (seed >> 16)) & 255) / 255.0;
                double u = (double)x / size, v = (double)y / size;
                double soil = (SurfaceNoise(u, v, 5, 11) - 0.5) * 7 + (SurfaceNoise(u, v, 21, 31) - 0.5) * 5;
                double pebble = grain > 0.984 ? -19.0 : 0.0;
                double value = soil + (grain - 0.5) * 16 + pebble;
                int i = (y * size + x) * 4;
                bytes[i] = (byte)(157 + value); bytes[i + 1] = (byte)(140 + value); bytes[i + 2] = (byte)(115 + value); bytes[i + 3] = 255;
            }
        var image = new NSImage(new NSSize(size, size)); image.addRepresentation(bitmap); return image;
    }
    private static double SurfaceNoise(double u, double v, int cells, int seed)
    {
        double x = u * cells, y = v * cells; int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        double fx = x - Math.Floor(x), fy = y - Math.Floor(y), tx = fx * fx * (3 - 2 * fx), ty = fy * fy * (3 - 2 * fy);
        double hash(int a, int b)
        {
            uint n = unchecked((uint)(((a % cells + cells) % cells) * 374761393 + ((b % cells + cells) % cells) * 668265263 + seed * 1274126177));
            n = unchecked((n ^ (n >> 13)) * 1274126177);
            return ((n ^ (n >> 16)) & 65535) / 65535.0;
        }
        double lo = hash(ix, iy) * (1 - tx) + hash(ix + 1, iy) * tx, hi = hash(ix, iy + 1) * (1 - tx) + hash(ix + 1, iy + 1) * tx;
        return lo * (1 - ty) + hi * ty;
    }

    /// <summary>PBR response grid under the BinarySky IBL probe plus a directional sun.</summary>
    private static void VisualMaterials()
    {
        var scene = new SCNScene(); scene.background.contents = color(0x30343a);
        var camera = new SCNNode { camera = new SCNCamera() }; camera.camera.fieldOfView = 36;
        camera.position = new SCNVector3(0, 0, 9); scene.rootNode.addChildNode(camera);
        scene.lightingEnvironment.contents = skyProbe(new Vector3(0.20f, 0.38f, 0.62f), new Vector3(0.65f, 0.72f, 0.75f));
        scene.lightingEnvironment.intensity = 0.95;
        var sun = light(scene, SCNLight.LightType.directional, 1550, new SCNVector3(-0.7, 0.6, 0), NSColor.calibratedRed(1, 0.94, 0.83, 1));
        light(scene, SCNLight.LightType.ambient, 200, color: NSColor.calibratedRed(0.54, 0.62, 0.78, 1));
        _ = sun;
        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 3; j++)
            {
                var s = new SCNSphere(0.42); s.segmentCount = 48;
                s.materials = new() { material(0xc3c7c9, j * 0.5, 0.1 + i * 0.2) };
                var n = new SCNNode(s); n.position = new SCNVector3(-2.2 + i * 1.1, 1.1 - j * 1.1, 0); scene.rootNode.addChildNode(n);
            }
        save("V_materials", render(scene, camera, 640, 400, SCNAntialiasingMode.multisampling4X));
    }
    private static NSImage skyProbe(Vector3 zenith, Vector3 horizon)
    {
        int w = 64, h = 32;
        var bitmap = new NSBitmapImageRep(bitmapDataPlanes: null, pixelsWide: w, pixelsHigh: h, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: NSColorSpaceName.deviceRGB, bytesPerRow: w * 4, bitsPerPixel: 32);
        for (int y = 0; y < h; y++)
        {
            float t = (float)Math.Pow(Math.Max(0, Math.Cos((double)y / (h - 1) * Math.PI)), 0.42);
            var rgb = (horizon * (1 - t) + zenith * t) * (y > h / 2 ? 0.55f : 1f);
            for (int x = 0; x < w; x++)
            {
                int j = (y * w + x) * 4;
                for (int c = 0; c < 3; c++) bitmap.bitmapData[j + c] = (byte)Math.Max(0, Math.Min(255, rgb[c] * 255));
                bitmap.bitmapData[j + 3] = 255;
            }
        }
        var image = new NSImage(new NSSize(w, h)); image.addRepresentation(bitmap); return image;
    }

    /// <summary>TownSigns.image(...) ported line by line: AppKit drawing and text into an NSBitmapImageRep.</summary>
    private static void VisualSign()
    {
        var bitmap = SignImage("DROID EXCHANGE", "REPAIRS  /  REBUILT MOTORS", "PARTS · TRACKS · POWER CELLS", "08", 6.8 / 0.95, 0xb98450);
        System.IO.File.WriteAllBytes(dir.PathJoin("V_sign.png"), bitmap.representation(NSBitmapImageFileType.png));
    }
    private static NSBitmapImageRep SignImage(string title, string eyebrow, string footer, string badge, double aspect, uint accent)
    {
        int w = 1024, h = (int)Math.Round(1024 / aspect, MidpointRounding.AwayFromZero);
        var bitmap = new NSBitmapImageRep(bitmapDataPlanes: null, pixelsWide: w, pixelsHigh: h, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: NSColorSpaceName.deviceRGB, bytesPerRow: w * 4, bitsPerPixel: 32);
        NSGraphicsContext.saveGraphicsState(); NSGraphicsContext.current = NSGraphicsContext.bitmapImageRep(bitmap);
        double W = w, H = h;
        NSColor color(uint rgb) => NSColor.calibratedRed(((rgb >> 16) & 255) / 255.0, ((rgb >> 8) & 255) / 255.0, (rgb & 255) / 255.0, 1);
        color(0x263c3c).setFill(); new NSRect(0, 0, W, H).fill();
        color(accent).setFill(); new NSRect(0, H - 10, W, 10).fill();
        color(0xb8aa8c).withAlphaComponent(0.45).setStroke();
        var outline = new NSBezierPath(new NSRect(12, 12, W - 24, H - 31)); outline.lineWidth = 2; outline.stroke();
        double badgeW = H * 0.55, left = badgeW + H * 0.15, right = W - H * 0.10;
        color(accent).setFill(); new NSRect(H * 0.10, H * 0.25, badgeW, H * 0.49).fill();
        void text(string value, NSRect rect, double size, uint ink, NSFont.Weight weight, double tracking = 0)
        {
            var paragraph = new NSMutableParagraphStyle(); paragraph.alignment = NSTextAlignment.center; paragraph.lineBreakMode = NSLineBreakMode.byClipping;
            Dictionary<NSAttributedString.Key, object> attributes() => new()
            {
                [NSAttributedString.Key.font] = NSFont.named(weight == NSFont.Weight.bold ? "AvenirNextCondensed-DemiBold" : "AvenirNextCondensed-Medium", size),
                [NSAttributedString.Key.foregroundColor] = color(ink), [NSAttributedString.Key.paragraphStyle] = paragraph, [NSAttributedString.Key.kern] = tracking,
            };
            while (size > 8)
            {
                var m = new NSAttributedString(value, attributes()).size();
                if (m.width <= rect.width && m.height <= rect.height) break;
                size -= 1;
            }
            var str = new NSAttributedString(value, attributes()); var measured = str.size();
            str.draw(new NSRect(rect.minX, rect.midY - measured.height / 2, rect.width, measured.height + 1));
        }
        text(badge, new NSRect(H * 0.10, H * 0.25, badgeW, H * 0.49), H * 0.30, 0x263c3c, NSFont.Weight.bold);
        double x = left + H * 0.08, tw = right - x;
        text(eyebrow, new NSRect(x, H * 0.73, tw, H * 0.16), H * 0.105, 0xc2b69c, NSFont.Weight.medium, 2.6);
        text(title, new NSRect(x, H * 0.29, tw, H * 0.43), H * 0.34, 0xf1e1bc, NSFont.Weight.bold, 1.5);
        color(accent).withAlphaComponent(0.70).setFill(); new NSRect(x + tw * 0.1, H * 0.28, tw * 0.8, 1.5).fill();
        text(footer, new NSRect(x, H * 0.09, tw, H * 0.17), H * 0.105, 0xc2b69c, NSFont.Weight.medium, 1.8);
        color(0xd9ccb0).withAlphaComponent(0.08).setFill();
        for (int i = 0; i < 65; i++) new NSRect((i * 179 + 23) % w, (i * 43 + 17) % h, 1 + i % 6, 1).fill();
        NSGraphicsContext.restoreGraphicsState();
        return bitmap;
    }

    /// <summary>Transform semantics probes: euler order, pivot, look(at:), convertPosition (numbers in measurements.json).</summary>
    private static void VisualTransforms()
    {
        var e = new SCNNode(); e.eulerAngles = new SCNVector3(0.3, 0.5, 0.7);
        var t = e.transform;
        measurements["T_euler_0.3_0.5_0.7_m11_m12_m13"] = new[] { t.m11, t.m12, t.m13 };
        var a = new SCNNode(); a.position = new SCNVector3(1, 2, 3); a.eulerAngles = new SCNVector3(0, Math.PI / 2, 0); a.scale = new SCNVector3(2, 2, 2); a.pivot = SCNMatrix4MakeTranslation(0.5, 0, 0);
        var w = a.convertPosition(new SCNVector3(1, 0, 0), null);
        measurements["T_pivot_convertPosition_1_0_0"] = new[] { w.x, w.y, w.z };
        var e7 = new SCNNode(); e7.eulerAngles = new SCNVector3(0.2, 0, 0); e7.eulerAngles.x = 7.0;
        measurements["T_euler_readback_x7_rotation"] = new[] { e7.eulerAngles.x, e7.rotation.x, e7.rotation.w };
        var look = new SCNNode(); look.position = new SCNVector3(0, 1.05, 2.25);
        look.look(new SCNVector3(0, 0.40, 0), new SCNVector3(0, 1, 0), new SCNVector3(0, 0, -1));
        var o = look.orientation;
        measurements["T_look_orientation"] = new[] { o.x, o.y, o.z, o.w };
    }
}
