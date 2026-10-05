using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Marvin.SceneKit;

/// <summary>
/// `tools/godot -- --calibration DIR`: renders game-like calibration scenes through the facade at
/// 1280x820 and writes DIR/NAME.png, then quits. The same scenes were rendered with SceneKit by a
/// Swift reference (see PORTING.md, "Calibration against game scenes"); keep the construction
/// identical (positions, materials, lights, camera) so image pairs are comparable pixel by pixel:
/// - race_*: BinarySky (two forward-shadow suns, ambient, 64x32 sky probe, fog 115-240, sky dome),
///   race camera (HDR, fixed exposure, bloom 0.38/1.2/12, SSAO 0.70/1.6/0.025, 2x MSAA).
/// - sandbox_*: World.swift (LDR, deferred 4096 sun, fog 15-38, 4x MSAA).
/// - menu_portrait: MainMenu.swift (constant SCNFloor, deferred key light, 4x MSAA).
/// Every scene holds the same test objects: PBR spheres (roughness x metalness), Marvin's CAD
/// model, scanned City materials, the Dirt clay, a vertex-coloured banner, a vertex-tinted house,
/// emissive patches, ground-tint overlays (premultiplied .fragment idiom), a translucent pane,
/// and shadow casters. CAL_ONLY=prefix renders only matching scenes; CAL_NOSHADOW, CAL_NOSSAO and
/// CAL_NOBLOOM switch one effect off (same switches in the Swift reference) to isolate it.
/// Focused experiments print numbers instead (each mirrors a Swift twin, see PORTING.md):
/// CAL_EXP=sphere (deferred/forward self-shadowing vs angle), pane (alpha and transparency per
/// lighting model), penumbra (shadow edge width), mirror (pre-filtered reflections vs roughness;
/// CAL_MIRROR_AMB=intensity, CAL_MIRROR_GRAY=1).
/// </summary>
public static class Calibration
{
    private const int W = 1280, H = 820;
    private static readonly SCNRenderer renderer = new(null, null);
    private static string dir;
    private static readonly string only = System.Environment.GetEnvironmentVariable("CAL_ONLY");

    [Marvin.GameMode("--calibration")]
    public static void Run(string outputDirectory, SceneTree tree)
    {
        dir = outputDirectory;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        DirAccess.MakeDirRecursiveAbsolute(dir);
        GD.Print($"calibration: writing to {dir}");
        if (System.Environment.GetEnvironmentVariable("CAL_EXP") == "sphere") { ShadowSphere(); tree.Quit(); return; }
        if (System.Environment.GetEnvironmentVariable("CAL_EXP") == "pane") { Pane(); tree.Quit(); return; }
        if (System.Environment.GetEnvironmentVariable("CAL_EXP") == "penumbra") { Penumbra(); tree.Quit(); return; }
        if (System.Environment.GetEnvironmentVariable("CAL_EXP") == "mirror") { Mirror(); tree.Quit(); return; }
        if (System.Environment.GetEnvironmentVariable("CAL_EXP") == "ssao") { SsaoProbe(); tree.Quit(); return; }
        Race(0.5, "race_midday");
        Race(0.96, "race_evening");
        Sandbox();
        Menu();
        tree.Quit();
    }

    // ---- helpers mirroring the Swift reference
    private static NSColor color(uint hex, double alpha = 1) =>
        NSColor.srgbRed(((hex >> 16) & 255) / 255.0, ((hex >> 8) & 255) / 255.0, (hex & 255) / 255.0, alpha);
    private static SCNMaterial material(uint hex, double metal = 0, double roughness = 0.6)
    {
        var m = new SCNMaterial { lightingModel = SCNMaterial.LightingModel.physicallyBased };
        m.diffuse.contents = color(hex); m.metalness.contents = metal; m.roughness.contents = roughness; m.isDoubleSided = true;
        return m;
    }
    private static NSColor ink(Vector3 p) => NSColor.calibratedRed(p.X, p.Y, p.Z, 1);
    private static string asset(string path) => "res://assets/" + path;
    private static SCNNode add(SCNNode parent, SCNGeometry g, SCNVector3 p)
    {
        var n = new SCNNode(g) { position = p }; parent.addChildNode(n); return n;
    }

    // ---- Game materials
    private static SCNMaterial scanned(string name, double normal, double scale)
    {
        var m = new SCNMaterial { lightingModel = SCNMaterial.LightingModel.physicallyBased };
        m.diffuse.contents = asset($"City/{name}-base.jpg");
        m.normal.contents = asset($"City/{name}-normal.jpg"); m.normal.intensity = normal;
        m.roughness.contents = asset($"City/{name}-rough.jpg"); m.metalness.contents = 0.0;
        foreach (var p in new[] { m.diffuse, m.normal, m.roughness })
        {
            p.wrapS = SCNWrapMode.repeat; p.wrapT = SCNWrapMode.repeat; p.mipFilter = SCNFilterMode.linear; p.maxAnisotropy = 4;
            p.contentsTransform = SCNMatrix4MakeScale(scale, scale, 1);
        }
        return m;
    }
    private static SCNMaterial clay(double scale)
    {
        var m = material(0x986441, roughness: 0.94);
        m.diffuse.contents = NSImage.contentsOf(asset("Dirt/diffuse.jpg"));
        m.normal.contents = NSImage.contentsOf(asset("Dirt/normal.jpg")); m.normal.intensity = 0.65;
        m.roughness.contents = NSImage.contentsOf(asset("Dirt/roughness.jpg"));
        foreach (var p in new[] { m.diffuse, m.normal, m.roughness }) { p.wrapS = SCNWrapMode.repeat; p.wrapT = SCNWrapMode.repeat; p.contentsTransform = SCNMatrix4MakeScale(scale, scale, 1); }
        m.shaderModifiers = new()
        {
            [SCNShaderModifierEntryPoint.surface] = @"
#pragma body
float clayDetail = dot(ALBEDO, vec3(0.2126, 0.7152, 0.0722));
ALBEDO = vec3(0.34, 0.205, 0.145) + clayDetail * vec3(0.58, 0.44, 0.33);
",
        };
        return m;
    }
    private static Dictionary<SCNShaderModifierEntryPoint, string> groundTintModifiers => new()
    {
        [SCNShaderModifierEntryPoint.geometry] = FacadeTest.GroundTintGeometry,
        [SCNShaderModifierEntryPoint.surface] = "ALBEDO = groundTint.rgb;",
        [SCNShaderModifierEntryPoint.fragment] = FacadeTest.GroundTintFragment,
    };
    private static Dictionary<SCNShaderModifierEntryPoint, string> cityTintModifiers => new()
    {
        [SCNShaderModifierEntryPoint.geometry] = @"
#pragma varyings
vec3 cityTint;
#pragma body
cityTint = pow(max(COLOR.rgb, vec3(0.0)), vec3(2.2));
",
        [SCNShaderModifierEntryPoint.surface] = @"
#pragma body
float grain = dot(ALBEDO, vec3(0.2126, 0.7152, 0.0722));
ALBEDO = cityTint * (0.62 + grain * 0.65);
",
    };
    private static SCNGeometrySource colorSource(List<float> c, int count) =>
        new(SCNGeometrySource.Bytes(c), SCNGeometrySourceSemantic.color, count, true, 4, 4, 0, 16);

    // ---- Test objects
    private static void spheres(SCNNode root, SCNVector3 o)
    {
        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 3; j++)
            {
                var s = new SCNSphere(0.2) { segmentCount = 48 };
                s.materials = new() { material(j == 1 ? 0xb5532fu : 0xc3c7c9u, metal: j == 2 ? 1 : 0, roughness: 0.1 + i * 0.2) };
                add(root, s, new SCNVector3(o.x + i * 0.5, 0.2, o.z + j * 0.5));
            }
    }
    private sealed class Part { public string name { get; set; } public int vertexOffset { get; set; } public int vertexCount { get; set; } public int indexOffset { get; set; } public int triangleCount { get; set; } }
    private sealed class Manifest { public List<Part> parts { get; set; } }
    private static byte[] marvinData;
    private static Manifest marvinManifest;
    private static void marvin(SCNNode parent, SCNVector3 p, double yaw, double headYaw)
    {
        marvinData ??= Godot.FileAccess.GetFileAsBytes(asset("Marvin/geometry.bin"));
        marvinManifest ??= System.Text.Json.JsonSerializer.Deserialize<Manifest>(Godot.FileAccess.GetFileAsString(asset("Marvin/manifest.json")));
        var root = new SCNNode(); var yawNode = new SCNNode(); var pitchNode = new SCNNode();
        var yawPivot = new SCNVector3(0, 0.30, -0.01886); var pitchPivot = new SCNVector3(0, 0.56, 0);
        yawNode.position = yawPivot; pitchNode.position = pitchPivot - yawPivot;
        root.addChildNode(yawNode); yawNode.addChildNode(pitchNode);
        var shell = material(0xc3c7c9, metal: 0.8, roughness: 0.65);
        var rubber = material(0x202a29, roughness: 0.88);
        var graphite = material(0x37403f, metal: 0.25, roughness: 0.42);
        var steel = material(0x919b9d, metal: 0.7, roughness: 0.28);
        var electronics = material(0x163e36, roughness: 0.6);
        var face = material(0x25282b, roughness: 0.38);
        double neutral = 0;
        foreach (var part in marvinManifest.parts.Where(q => q.vertexCount > 0 && q.triangleCount > 0))
        {
            var positions = new SCNGeometrySource(marvinData, SCNGeometrySourceSemantic.vertex, part.vertexCount, true, 3, 4, part.vertexOffset, 24);
            var normals = new SCNGeometrySource(marvinData, SCNGeometrySourceSemantic.normal, part.vertexCount, true, 3, 4, part.vertexOffset + 12, 24);
            var indices = marvinData[part.indexOffset..(part.indexOffset + part.triangleCount * 12)];
            var g = new SCNGeometry(new[] { positions, normals }, new[] { new SCNGeometryElement(indices, SCNGeometryPrimitiveType.triangles, part.triangleCount, 4) });
            if (part.name is not ("Head" or "09_track")) neutral = Math.Max(neutral, g.boundingBox.max.y);
            if (part.name == "09_track") g.materials = new() { rubber };
            else if (part.name is "04_wheel" or "Top" or "Servo_Head" or "Servo_Tilt") g.materials = new() { graphite };
            else if (part.name is "Bearings" or "Motor_Left" or "Motor_Right" or "Axis_Mount") g.materials = new() { steel };
            else if (part.name == "Display_and_electronics") g.materials = new() { face };
            else if (part.name == "Battery") g.materials = new() { electronics };
            else g.materials = new() { shell };
            var node = new SCNNode(g) { isHidden = part.name == "Head" };
            if (part.name is "05_head_base" or "06_head_cover" or "Display_and_electronics" or "Head" or "Top" or "Battery")
            { node.position = new SCNVector3(-pitchPivot.x, -pitchPivot.y, -pitchPivot.z); pitchNode.addChildNode(node); }
            else if (part.name is "07_neck" or "08_neck_mount" or "Servo_Tilt" or "Axis_Mount")
            { node.position = new SCNVector3(-yawPivot.x, -yawPivot.y, -yawPivot.z); yawNode.addChildNode(node); }
            else root.addChildNode(node);
        }
        var eyePath = new NSBezierPath();
        for (int i = 0; i <= 40; i++) { double a = Math.PI * (1 - i / 40.0); var q = new NSPoint(Math.Cos(a) * 0.061, Math.Sin(a) * 0.046); if (i == 0) eyePath.move(q); else eyePath.line(q); }
        for (int i = 0; i <= 40; i++) { double a = Math.PI * i / 40.0; eyePath.line(new NSPoint(Math.Cos(a) * 0.048, Math.Sin(a) * 0.033)); }
        eyePath.close();
        foreach (var x in new[] { -0.0545, 0.0545 }) eyePath.appendOval(new NSRect(x - 0.0065, -0.0065, 0.013, 0.013));
        foreach (var x in new[] { -0.145, 0.145 })
        {
            var eye = new SCNShape(eyePath, 0);
            var glow = new SCNMaterial { lightingModel = SCNMaterial.LightingModel.constant }; glow.diffuse.contents = NSColor.white;
            glow.emission.contents = NSColor.white; glow.isDoubleSided = true; eye.materials = new() { glow };
            var n = new SCNNode(eye) { position = new SCNVector3(x, 0.008, 0.348), castsShadow = false }; pitchNode.addChildNode(n);
        }
        foreach (var x in new[] { -0.337, 0.337 })
            foreach (var z in new[] { -0.247, 0.164 })
            {
                var spoke = new SCNBox(0.008, 0.10, 0.022, 0.004); spoke.materials = new() { material(0xb4c8c0, metal: 0.5) };
                add(root, spoke, new SCNVector3(x, 0.112, z));
            }
        double scale = 0.885 * (0.60 / 1.08) / neutral;
        root.scale = new SCNVector3(scale, scale, scale);
        yawNode.eulerAngles.y = headYaw;
        root.position = p; root.eulerAngles = new SCNVector3(0, yaw, 0);
        parent.addChildNode(root);
    }
    private static void plasterBox(SCNNode root, SCNVector3 p)
    {
        var b = new SCNBox(0.8, 0.8, 0.8, 0.08); b.materials = new() { scanned("plaster", 1, 1) };
        add(root, b, new SCNVector3(p.x, 0.4, p.z)).eulerAngles.y = 0.5;
    }
    private static void coloredBox(SCNNode root, SCNVector3 p)
    {
        var b = new SCNBox(0.5, 1.0, 0.5, 0.06); b.materials = new() { material(0x96b4a7) };
        add(root, b, new SCNVector3(p.x, 0.5, p.z)).eulerAngles.y = -0.3;
    }
    private static void tintedHouse(SCNNode root, SCNVector3 p)
    {
        float w = 1.6f, h = 1.2f, d = 1.2f;
        var v = new List<SCNVector3>(); var n = new List<SCNVector3>(); var uv = new List<CGPoint>(); var c = new List<float>(); var idx = new List<int>();
        var faces = new (Vector3 normal, Vector3 u, Vector3 up)[]
        {
            (new(0, 0, 1), new(1, 0, 0), new(0, 1, 0)), (new(0, 0, -1), new(-1, 0, 0), new(0, 1, 0)),
            (new(1, 0, 0), new(0, 0, -1), new(0, 1, 0)), (new(-1, 0, 0), new(0, 0, 1), new(0, 1, 0)),
            (new(0, 1, 0), new(1, 0, 0), new(0, 0, -1)),
        };
        var palette = new Vector3[] { new(0.79f, 0.64f, 0.48f), new(0.72f, 0.52f, 0.38f), new(0.86f, 0.78f, 0.62f), new(0.62f, 0.44f, 0.32f), new(0.80f, 0.70f, 0.56f) };
        var half = new Vector3(w / 2, h / 2, d / 2);
        for (int k = 0; k < faces.Length; k++)
        {
            var (normal, u, up) = faces[k];
            var center = normal * half + new Vector3(0, h / 2, 0);
            float eu = Math.Abs(u.Dot(half)), ev = Math.Abs(up.Dot(half));
            int b = v.Count;
            foreach (var (su, sv) in new (float, float)[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
            {
                var q = center + u * eu * su + up * ev * sv;
                v.Add(SCNVector3.FromGodot(q)); n.Add(SCNVector3.FromGodot(normal));
                uv.Add(new CGPoint((su + 1) * eu, (1 - sv) * ev));
                var col = palette[k] * (sv > 0 ? 1.0f : 0.82f);
                c.AddRange(new[] { col.X, col.Y, col.Z, 1f });
            }
            idx.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
        }
        var g = new SCNGeometry(new[] { SCNGeometrySource.vertices(v), SCNGeometrySource.normals(n), SCNGeometrySource.textureCoordinates(uv), colorSource(c, v.Count) },
            new[] { new SCNGeometryElement(idx, SCNGeometryPrimitiveType.triangles) });
        var m = scanned("adobe", 0.8, 0.5); m.shaderModifiers = cityTintModifiers;
        g.materials = new() { m };
        add(root, g, new SCNVector3(p.x, 0, p.z)).eulerAngles.y = 0.25;
    }
    private static void banner(SCNNode root, SCNVector3 p)
    {
        var v = new List<SCNVector3>(); var n = new List<SCNVector3>(); var c = new List<float>(); var idx = new List<int>();
        var cols = new[] { new[] { 0.80f, 0.10f, 0.05f }, new[] { 0.90f, 0.60f, 0.05f }, new[] { 0.10f, 0.50f, 0.20f }, new[] { 0.05f, 0.20f, 0.70f }, new[] { 0.50f, 0.10f, 0.60f } };
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 5; col++)
            {
                v.Add(new SCNVector3(-0.8 + col * 0.4, 0.15 + row * 0.4, 0)); n.Add(new SCNVector3(0, 0, 1));
                var k = cols[(col + row) % 5]; float s = row == 1 ? 1 : 0.6f;
                c.AddRange(new[] { k[0] * s, k[1] * s, k[2] * s, 1f });
            }
        for (int row = 0; row < 2; row++)
            for (int col = 0; col < 4; col++) { int a = row * 5 + col; idx.AddRange(new[] { a, a + 1, a + 6, a, a + 6, a + 5 }); }
        var g = new SCNGeometry(new[] { SCNGeometrySource.vertices(v), SCNGeometrySource.normals(n), colorSource(c, v.Count) }, new[] { new SCNGeometryElement(idx, SCNGeometryPrimitiveType.triangles) });
        g.materials = new() { material(0xffffff, roughness: 0.8) };
        add(root, g, new SCNVector3(p.x, 0, p.z)).eulerAngles.y = 0.15;
    }
    private static void emissive(SCNNode root, SCNVector3 p)
    {
        var list = new[] { (0xffa040u, 3.0), (0x40c0ffu, 0.8) };
        for (int k = 0; k < list.Length; k++)
        {
            var plane = new SCNPlane(0.4, 0.25);
            var m = material(0x202020, roughness: 0.5); m.emission.contents = color(list[k].Item1); m.emission.intensity = list[k].Item2;
            plane.materials = new() { m };
            add(root, plane, new SCNVector3(p.x + k * 0.55, 0.3, p.z));
        }
    }
    private static void overlays(SCNNode root, SCNVector3 p)
    {
        var overlay = new SCNMaterial { lightingModel = SCNMaterial.LightingModel.physicallyBased };
        overlay.roughness.contents = 1.0; overlay.diffuse.contents = NSColor.white;
        overlay.writesToDepthBuffer = false; overlay.transparencyMode = SCNTransparencyMode.aOne;
        overlay.shaderModifiers = groundTintModifiers;
        var verts = new List<SCNVector3>(); var colors = new List<float>(); var idx = new List<int>();
        var tints = new[] { new[] { 0x8e / 255f, 0x80 / 255f, 0x6a / 255f }, new[] { 0x5a / 255f, 0x4a / 255f, 0x36 / 255f }, new[] { 0xc8 / 255f, 0xb8 / 255f, 0x9a / 255f } };
        var offsets = new[] { (0.0, 0.0), (1.0, 0.3), (-0.9, 0.4) };
        for (int k = 0; k < 3; k++)
        {
            double cx = p.x + offsets[k].Item1, cz = p.z + offsets[k].Item2;
            int center = verts.Count; verts.Add(new SCNVector3(cx, 0.004, cz)); colors.AddRange(tints[k]); colors.Add(0.7f);
            for (int i = 0; i < 18; i++)
            {
                double a = i * 2 * Math.PI / 18;
                verts.Add(new SCNVector3(cx + Math.Cos(a) * 0.6, 0.004, cz + Math.Sin(a) * 0.45)); colors.AddRange(tints[k]); colors.Add(0f);
            }
            for (int i = 0; i < 18; i++) { idx.Add(center); idx.Add(center + 1 + (i + 1) % 18); idx.Add(center + 1 + i); }
        }
        var g = new SCNGeometry(new[] { SCNGeometrySource.vertices(verts), SCNGeometrySource.normals(Enumerable.Repeat(new SCNVector3(0, 1, 0), verts.Count).ToArray()), colorSource(colors, verts.Count) },
            new[] { new SCNGeometryElement(idx, SCNGeometryPrimitiveType.triangles) });
        g.materials = new() { overlay };
        add(root, g, SCNVector3Zero).castsShadow = false;
    }
    private static void pane(SCNNode root, SCNVector3 p)
    {
        var b = new SCNBox(0.9, 0.6, 0.02, 0); b.materials = new() { material(0x6fa8dc, roughness: 0.2) };
        b.firstMaterial.diffuse.contents = color(0x6fa8dc, 0.45);
        add(root, b, new SCNVector3(p.x, 0.42, p.z)).eulerAngles.y = 0.35;
    }
    private static void pole(SCNNode root, SCNVector3 p)
    {
        var c = new SCNCylinder(0.04, 2.0); c.materials = new() { material(0x555653, roughness: 0.94) };
        add(root, c, new SCNVector3(p.x, 1.0, p.z));
    }
    private static void table(SCNNode root, SCNVector3 p)
    {
        var m = material(0x805443, roughness: 0.94);
        var top = new SCNBox(1.0, 0.04, 0.6, 0.01); top.materials = new() { m };
        add(root, top, new SCNVector3(p.x, 0.7, p.z));
        foreach (var (dx, dz) in new[] { (-0.45, -0.25), (0.45, -0.25), (-0.45, 0.25), (0.45, 0.25) })
        {
            var leg = new SCNBox(0.04, 0.68, 0.04, 0); leg.materials = new() { m };
            add(root, leg, new SCNVector3(p.x + dx, 0.34, p.z + dz));
        }
    }
    private static SCNNode groundPlane(SCNNode root, SCNMaterial m, double w, double l, SCNVector3 p)
    {
        var plane = new SCNPlane(w, l); plane.materials = new() { m };
        var n = add(root, plane, p); n.eulerAngles.x = -Math.PI / 2; return n;
    }
    private static void cluster(SCNNode root)
    {
        spheres(root, new SCNVector3(-2.4, 0, -0.9));
        marvin(root, new SCNVector3(0.6, 0, 0.1), 0.35, -0.30);
        plasterBox(root, new SCNVector3(2.0, 0, -0.6)); coloredBox(root, new SCNVector3(1.2, 0, -1.4));
        tintedHouse(root, new SCNVector3(3.2, 0, -2.4)); banner(root, new SCNVector3(-1.2, 0, -1.9));
        emissive(root, new SCNVector3(-0.1, 0, 0.9)); overlays(root, new SCNVector3(0.9, 0, 1.1));
        pane(root, new SCNVector3(-2.1, 0, 0.8)); pole(root, new SCNVector3(-0.2, 0, -1.4)); table(root, new SCNVector3(2.5, 0, 0.7));
    }

    // ---- Rendering
    private static SCNNode camera(SCNScene scene, SCNVector3 p, SCNVector3 target, double fov, double near, double far)
    {
        var c = new SCNNode { camera = new SCNCamera() }; c.camera.fieldOfView = fov; c.camera.zNear = near; c.camera.zFar = far;
        c.position = p; c.look(target, new SCNVector3(0, 1, 0), new SCNVector3(0, 0, -1));
        scene.rootNode.addChildNode(c); return c;
    }
    private static void render(string name, SCNScene scene, SCNNode cam, SCNAntialiasingMode aa)
    {
        if (!string.IsNullOrEmpty(only) && !name.StartsWith(only)) return;
        // Isolation toggles, identical in the Swift reference: CAL_NOSHADOW, CAL_NOSSAO, CAL_NOBLOOM.
        scene.rootNode.enumerateHierarchy((n, _) =>
        {
            if (System.Environment.GetEnvironmentVariable("CAL_NOSHADOW") != null && n.light != null) n.light.castsShadow = false;
            if (n.camera != null)
            {
                if (System.Environment.GetEnvironmentVariable("CAL_NOSSAO") != null) n.camera.screenSpaceAmbientOcclusionIntensity = 0;
                if (System.Environment.GetEnvironmentVariable("CAL_NOBLOOM") != null) n.camera.bloomIntensity = 0;
            }
        });
        renderer.scene = scene; renderer.pointOfView = cam;
        renderer.SnapshotImage(new Vector2I(W, H), aa);
        var img = renderer.SnapshotImage(new Vector2I(W, H), aa);
        img.SavePng(dir.PathJoin(name + ".png"));
        GD.Print($"calibration: rendered {name}");
    }

    // ---- Race (BinarySky)
    private readonly struct BinaryDaylight
    {
        public readonly double fraction;
        public readonly SCNVector3[] directions;
        public BinaryDaylight(double fraction, double phase)
        {
            this.fraction = Math.Max(0.015, Math.Min(0.985, fraction));
            var offsets = new[] { Math.Atan2(-0.06 * Math.Sin(phase), 1 + 0.06 * Math.Cos(phase)), Math.Atan2(0.14 * Math.Sin(phase), 1 - 0.14 * Math.Cos(phase)) };
            double first = Math.Max(-Math.PI / 2 - offsets[0], -Math.PI / 2 - offsets[1]), last = Math.Min(Math.PI / 2 - offsets[0], Math.PI / 2 - offsets[1]);
            double h = first + (last - first) * this.fraction, latitude = 25 * Math.PI / 180;
            directions = offsets.Select(d => new SCNVector3(-Math.Sin(h + d), Math.Cos(latitude) * Math.Cos(h + d), -Math.Sin(latitude) * Math.Cos(h + d))).ToArray();
        }
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
    private static void Race(double fraction, string tag)
    {
        if (!string.IsNullOrEmpty(only) && !tag.StartsWith(only) && !only.StartsWith(tag)) return;
        var scene = new SCNScene();
        var value = new BinaryDaylight(fraction, 1.2);
        var skyMaterial = new SCNMaterial { lightingModel = SCNMaterial.LightingModel.constant, cullMode = SCNCullMode.front, readsFromDepthBuffer = false, writesToDepthBuffer = false };
        skyMaterial.shaderModifiers = new() { [SCNShaderModifierEntryPoint.geometry] = FacadeTest.SkyGeometry, [SCNShaderModifierEntryPoint.fragment] = FacadeTest.SkyFragment };
        var sphere = new SCNSphere(220) { segmentCount = 48 }; sphere.materials = new() { skyMaterial };
        var dome = new SCNNode(sphere) { castsShadow = false, renderingOrder = -10000 }; scene.rootNode.addChildNode(dome);
        var ambient = new SCNNode { light = new SCNLight { type = SCNLight.LightType.ambient } }; scene.rootNode.addChildNode(ambient);
        var suns = new[] { new SCNNode(), new SCNNode() };
        for (int i = 0; i < 2; i++)
        {
            var l = new SCNLight { type = SCNLight.LightType.directional };
            suns[i].light = l;
            l.castsShadow = true; l.shadowMode = SCNShadowMode.forward;
            int resolution = i == 0 ? 4096 : 2048;
            l.shadowMapSize = new CGSize(resolution, resolution);
            l.automaticallyAdjustsShadowProjection = false; l.sampleDistributedShadowMaps = false; l.forcesBackFaceCasters = false;
            l.zNear = 0.1; l.zFar = 220; l.maximumShadowDistance = 500;
            l.orthographicScale = 58; l.shadowRadius = i == 0 ? 3 : 2;
            l.shadowSampleCount = 8; l.shadowColor = NSColor.black; l.shadowBias = 0.6;
            scene.rootNode.addChildNode(suns[i]);
        }
        skyMaterial.setValue(0f, "storm");
        skyMaterial.setValue(NSValue.scnVector3(new SCNVector3(0.46, 0.29, 0.14)), "stormTint");
        skyMaterial.setValue(NSValue.point(new NSPoint(0.55 * Math.PI / 180, 0.38 * Math.PI / 180)), "sunRadii");
        double elevation = Math.Max(0, value.directions.Max(d => d.y));
        double day = Math.Min(1, elevation / 0.55);
        float evening = value.fraction > 0.5 ? 1 : 0;
        var lowZenith = new Vector3(0.018f, 0.070f, 0.17f) * (1 - evening) + new Vector3(0.065f, 0.027f, 0.10f) * evening;
        var lowHorizon = new Vector3(0.95f, 0.36f, 0.09f) * (1 - evening) + new Vector3(0.85f, 0.19f, 0.035f) * evening;
        var zenith = lowZenith + (new Vector3(0.20f, 0.38f, 0.62f) - lowZenith) * (float)day;
        var horizon = lowHorizon + (new Vector3(0.65f, 0.72f, 0.75f) - lowHorizon) * (float)day;
        skyMaterial.setValue((float)day, "daylight");
        var band = new Vector3(0.04f, 0.055f, 0.06f) * (1 - evening) + new Vector3(0.22f, 0.045f, 0.085f) * evening;
        skyMaterial.setValue(NSValue.scnVector3(SCNVector3.FromGodot(band)), "duskBand");
        skyMaterial.setValue(NSValue.scnVector3(SCNVector3.FromGodot(zenith)), "zenith");
        skyMaterial.setValue(NSValue.scnVector3(SCNVector3.FromGodot(horizon)), "horizon");
        for (int i = 0; i < 2; i++)
        {
            var d = value.directions[i]; double height = Math.Max(0, d.y);
            double altitude = Math.Asin(height) * 180 / Math.PI;
            double mass = 1 / (height + 0.50572 * Math.Pow(altitude + 6.07995, -1.6364));
            var attenuation = new SCNVector3(Math.Exp(-0.035 * mass), Math.Exp(-0.070 * mass), Math.Exp(-0.15 * mass));
            var intrinsic = i == 0 ? new SCNVector3(1, 0.94, 0.83) : new SCNVector3(1, 0.73, 0.46);
            var tint = new Vector3((float)(intrinsic.x * attenuation.x), (float)(intrinsic.y * attenuation.y), (float)(intrinsic.z * attenuation.z));
            suns[i].position = d * 80;
            suns[i].look(SCNVector3Zero, new SCNVector3(0, 1, 0), new SCNVector3(0, 0, -1));
            suns[i].light.color = ink(tint);
            suns[i].light.intensity = i == 0 ? 1550 : 470;
            skyMaterial.setValue(NSValue.scnVector3(d), i == 0 ? "sunA" : "sunB");
            skyMaterial.setValue(NSValue.scnVector3(SCNVector3.FromGodot(tint)), i == 0 ? "tintA" : "tintB");
        }
        ambient.light.color = ink(new Vector3(0.54f, 0.62f, 0.78f)); ambient.light.intensity = 260 - 70 * day;
        scene.fogColor = ink(horizon); scene.fogStartDistance = 115; scene.fogEndDistance = 240;
        scene.lightingEnvironment.contents = skyProbe(zenith, horizon);
        scene.lightingEnvironment.intensity = 0.95 - 0.20 * day;
        var root = scene.rootNode;
        var city = groundPlane(root, scanned("ground", 0.65, 125), 500, 500, new SCNVector3(0, -0.01, 0));
        var gm = city.geometry.firstMaterial; foreach (var p in new[] { gm.diffuse, gm.normal, gm.roughness }) p.maxAnisotropy = 8;
        groundPlane(root, clay(7.5), 30, 30, new SCNVector3(0, 0, 0));
        for (int k = 0; k < 10; k++)
        {
            double a = k * 0.628 + 0.3, r = 10.0 + (k % 3) * 6;
            var b = new SCNBox(3, 2.5 + k % 2, 3, 0.12); b.materials = new() { scanned("plaster", 1, 1.5) };
            add(root, b, new SCNVector3(Math.Cos(a) * r, (2.5 + k % 2) / 2.0, Math.Sin(a) * r)).eulerAngles.y = a;
        }
        for (int k = 0; k < 5; k++)
        {
            var b = new SCNBox(1.5, 6, 1.5, 0.05); b.materials = new() { material(0x805443, roughness: 0.94) };
            add(root, b, new SCNVector3(-6 + k * 3, 3, -30 - k * 45));
        }
        cluster(root);
        void attach(SCNNode cam)
        {
            dome.constraints = new() { SCNTransformConstraint.positionConstraint(true, (_, _) => cam.presentation.worldPosition) };
            var lens = cam.camera;
            lens.wantsHDR = true; lens.wantsExposureAdaptation = false;
            lens.exposureOffset = 0; lens.bloomIntensity = 0.38; lens.bloomThreshold = 1.2; lens.bloomBlurRadius = 12;
            lens.screenSpaceAmbientOcclusionIntensity = 0.70; lens.screenSpaceAmbientOcclusionRadius = 1.6; lens.screenSpaceAmbientOcclusionBias = 0.025;
        }
        if (fraction < 0.9)
        {
            var chase = camera(scene, new SCNVector3(0.4, 1.9, 5.6), new SCNVector3(0.2, 0.35, -0.4), 48, 0.02, 250);
            attach(chase);
            render(tag + "_chase", scene, chase, SCNAntialiasingMode.multisampling2X);
            var overview = camera(scene, new SCNVector3(0, 38, -33), SCNVector3Zero, 48, 0.02, 250);
            attach(overview);
            render(tag + "_overview", scene, overview, SCNAntialiasingMode.multisampling2X);
        }
        else
        {
            var chase = camera(scene, new SCNVector3(5.5, 1.5, 1.8), new SCNVector3(-2.0, 1.0, -0.8), 48, 0.02, 250);
            attach(chase);
            render(tag + "_chase", scene, chase, SCNAntialiasingMode.multisampling2X);
        }
    }

    // ---- Sandbox (World.swift)
    private static void Sandbox()
    {
        if (!string.IsNullOrEmpty(only) && !"sandbox".StartsWith(only) && !only.StartsWith("sandbox")) return;
        var scene = new SCNScene();
        scene.background.contents = color(0xdbe4df);
        scene.fogColor = color(0xdbe4df); scene.fogStartDistance = 15; scene.fogEndDistance = 38;
        var root = scene.rootNode;
        var ambient = new SCNNode { light = new SCNLight { type = SCNLight.LightType.ambient, intensity = 550, color = color(0xe7f4ff) } }; root.addChildNode(ambient);
        var sun = new SCNNode { light = new SCNLight { type = SCNLight.LightType.directional, intensity = 1400, color = color(0xfff1df) } };
        sun.eulerAngles = new SCNVector3(-0.85, -0.45, -0.25);
        sun.light.castsShadow = true; sun.light.shadowMode = SCNShadowMode.deferred;
        sun.light.shadowMapSize = new CGSize(4096, 4096);
        sun.light.shadowSampleCount = 16; sun.light.shadowColor = NSColor.black.withAlphaComponent(0.24);
        sun.light.orthographicScale = 10; sun.light.maximumShadowDistance = 22;
        root.addChildNode(sun);
        SCNNode box(double x, double y, double z, double w, double h, double d, SCNMaterial m, double radius = 0)
        {
            var g = new SCNBox(w, h, d, radius); g.materials = new() { m }; return add(root, g, new SCNVector3(x, y, z));
        }
        var soil = material(0xb49470, roughness: 0.98);
        box(0, -0.175, 0, 12.5, 0.3, 10.5, material(0x927455, roughness: 0.95), 0.14);
        box(0, -0.05, 0, 12, 0.05, 10, soil, 0.04);
        groundPlane(root, soil, 12, 10, SCNVector3Zero);
        var wall = material(0xa5b9ad);
        box(-6.08, 0.18, 0, 0.16, 0.36, 10.3, wall, 0.04); box(6.08, 0.18, 0, 0.16, 0.36, 10.3, wall, 0.04);
        box(0, 0.18, -5.08, 12.3, 0.36, 0.16, wall, 0.04); box(0, 0.18, 5.08, 12.3, 0.36, 0.16, wall, 0.04);
        var obstacles = new[] { (-2.2, -0.4, 1.25, 1.25, 0.6), (1.65, 0.3, 1.1, 1.65, 0.85), (-3.55, 2.4, 1.0, 1.2, 0.4), (3.5, -2.3, 1.2, 0.8, 0.45), (0.0, 3.3, 1.6, 0.65, 0.35) };
        for (int i = 0; i < obstacles.Length; i++)
        {
            var o = obstacles[i];
            box(o.Item1, o.Item5 / 2, o.Item2, o.Item3, o.Item5, o.Item4, material(i % 2 == 0 ? 0x96b4a7u : 0xd2b59au), 0.07);
        }
        box(0, 0, -2.6, 1.35, 0.001, 1.25, material(0xb7cdc0), 0.08).castsShadow = false;
        groundPlane(root, clay(1), 1.8, 1.4, new SCNVector3(2.6, 0.001, 2.0));
        spheres(root, new SCNVector3(-1.0, 0, -4.3));
        marvin(root, new SCNVector3(0.2, 0, 1.6), 0.35, -0.30);
        plasterBox(root, new SCNVector3(-4.4, 0, -1.2)); tintedHouse(root, new SCNVector3(4.3, 0, 2.6)); banner(root, new SCNVector3(2.2, 0, -4.2));
        emissive(root, new SCNVector3(-0.3, 0, -1.6)); overlays(root, new SCNVector3(-1.4, 0, 1.4)); pane(root, new SCNVector3(-4.0, 0, 1.0));
        pole(root, new SCNVector3(1.0, 0, -1.4)); table(root, new SCNVector3(-4.3, 0, -3.6));
        var orbit = camera(scene, new SCNVector3(4.27, 3.76, 4.9), new SCNVector3(0.5, 0.4, -0.3), 48, 0.02, 80);
        orbit.camera.wantsHDR = false;
        render("sandbox_orbit", scene, orbit, SCNAntialiasingMode.multisampling4X);
        var overview = camera(scene, new SCNVector3(0, 11.7, -10), SCNVector3Zero, 48, 0.02, 80);
        overview.camera.wantsHDR = false;
        render("sandbox_overview", scene, overview, SCNAntialiasingMode.multisampling4X);
    }

    // ---- Self-shadowing experiment (mirrors exp/deferred.swift): sphere lit from +Y, ratio to the unshadowed render.
    private static Image SphereRender(SCNShadowMode? mode, double bias, double radius, int samples, double mapSize, double ortho, double alpha)
    {
        var scene = new SCNScene(); scene.background.contents = NSColor.black;
        var s = new SCNSphere(1) { segmentCount = 96 };
        var m = new SCNMaterial { lightingModel = SCNMaterial.LightingModel.physicallyBased }; m.diffuse.contents = NSColor.white; m.roughness.contents = 1.0; m.metalness.contents = 0.0;
        s.materials = new() { m }; scene.rootNode.addChildNode(new SCNNode(s));
        var l = new SCNNode { light = new SCNLight { type = SCNLight.LightType.directional, intensity = 1000 } };
        l.eulerAngles = new SCNVector3(-Math.PI / 2, 0, 0);
        if (mode.HasValue)
        {
            l.light.castsShadow = true; l.light.shadowMode = mode.Value; l.light.shadowBias = bias; l.light.shadowRadius = radius;
            l.light.shadowSampleCount = samples; l.light.shadowMapSize = new CGSize(mapSize, mapSize); l.light.orthographicScale = ortho;
            l.light.shadowColor = NSColor.black.withAlphaComponent(alpha);
        }
        scene.rootNode.addChildNode(l);
        scene.rootNode.addChildNode(new SCNNode { light = new SCNLight { type = SCNLight.LightType.ambient, intensity = 300 } });
        var cam = new SCNNode { camera = new SCNCamera() }; cam.camera.usesOrthographicProjection = true; cam.camera.orthographicScale = 1.2;
        cam.position = new SCNVector3(0, 0, 5); scene.rootNode.addChildNode(cam);
        renderer.scene = scene; renderer.pointOfView = cam;
        renderer.SnapshotImage(new Vector2I(240, 240), SCNAntialiasingMode.none);
        return renderer.SnapshotImage(new Vector2I(240, 240), SCNAntialiasingMode.none);
    }
    private static void ShadowSphere()
    {
        var angles = new[] { 0.0, 20, 40, 60, 70, 80, 85, 90, 100, 120, 150 };
        var baseImage = SphereRender(null, 1, 3, 1, 2048, 2, 1);
        static double lin(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        void row(string label, Image b)
        {
            var o = label.PadRight(34);
            foreach (var a in angles)
            {
                double t = a * Math.PI / 180; int px = 120, py = (int)Math.Round(120 - Math.Cos(t) * 100, MidpointRounding.AwayFromZero);
                double v = lin(b.GetPixel(px, py).R), v0 = lin(baseImage.GetPixel(px, py).R);
                o += $" {(v0 > 0.002 ? v / v0 : -1),5:0.00}";
            }
            GD.Print(o);
        }
        GD.Print("angle from light (deg):".PadRight(34) + string.Concat(angles.Select(a => $" {a,5:0}")));
        row("deferred a1 bias1 r3 s16", SphereRender(SCNShadowMode.deferred, 1, 3, 16, 2048, 2, 1));
        row("deferred a1 bias0.001 r32 s32", SphereRender(SCNShadowMode.deferred, 0.001, 32, 32, 2048, 2, 1));
        row("deferred a1 bias1 r32 s32", SphereRender(SCNShadowMode.deferred, 1, 32, 32, 2048, 2, 1));
        row("deferred a1 bias1 r1 s1", SphereRender(SCNShadowMode.deferred, 1, 1, 1, 2048, 2, 1));
        row("forward a1 bias1 r3 s16", SphereRender(SCNShadowMode.forward, 1, 3, 16, 2048, 2, 1));
        row("forward a1 bias0.001 r32 s32", SphereRender(SCNShadowMode.forward, 0.001, 32, 32, 2048, 2, 1));
    }

    // ---- Transparency experiment (mirrors exp/pane.swift): centre pixel, linear.
    private static void PaneRun(string label, SCNMaterial.LightingModel model, bool box, bool doubleSided, double alpha, double transparency = 1, double light = 1000, double ambient = 0, double rough = 1, SCNTransparencyMode mode = SCNTransparencyMode.aOne)
    {
        var scene = new SCNScene(); scene.background.contents = NSColor.srgbRed(0.5, 0, 0, 1);
        var m = new SCNMaterial { lightingModel = model }; m.diffuse.contents = NSColor.srgbRed(0.2, 0.6, 0.9, alpha);
        m.roughness.contents = rough; m.metalness.contents = 0.0; m.isDoubleSided = doubleSided; m.transparency = transparency; m.transparencyMode = mode;
        SCNGeometry g = box ? new SCNBox(4, 4, 0.02, 0) : new SCNPlane(4, 4);
        g.materials = new() { m }; scene.rootNode.addChildNode(new SCNNode(g));
        if (light > 0) scene.rootNode.addChildNode(new SCNNode { light = new SCNLight { type = SCNLight.LightType.directional, intensity = light } });
        if (ambient > 0) scene.rootNode.addChildNode(new SCNNode { light = new SCNLight { type = SCNLight.LightType.ambient, intensity = ambient } });
        var cam = new SCNNode { camera = new SCNCamera() }; cam.camera.usesOrthographicProjection = true; cam.position = new SCNVector3(0, 0, 5); scene.rootNode.addChildNode(cam);
        renderer.scene = scene; renderer.pointOfView = cam;
        renderer.SnapshotImage(new Vector2I(64, 64), SCNAntialiasingMode.none);
        var c = renderer.SnapshotImage(new Vector2I(64, 64), SCNAntialiasingMode.none).GetPixel(32, 32);
        static double lin(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        GD.Print($"{label,-40}{lin(c.R):0.0000} {lin(c.G):0.0000} {lin(c.B):0.0000}");
    }
    private static void Pane()
    {
        var pbr = SCNMaterial.LightingModel.physicallyBased;
        PaneRun("pbr plane a0.45", pbr, false, false, 0.45);
        PaneRun("pbr plane a0.45 2sided", pbr, false, true, 0.45);
        PaneRun("pbr box a0.45 2sided", pbr, true, true, 0.45);
        PaneRun("pbr box a0.45", pbr, true, false, 0.45);
        PaneRun("pbr plane a0.45 rough0.2", pbr, false, false, 0.45, rough: 0.2);
        PaneRun("pbr plane a0.45 amb500 nolight", pbr, false, false, 0.45, light: 0, ambient: 500);
        PaneRun("lambert plane a0.45", SCNMaterial.LightingModel.lambert, false, false, 0.45);
        PaneRun("constant plane a0.45", SCNMaterial.LightingModel.constant, false, false, 0.45);
        PaneRun("pbr plane transparency0.45", pbr, false, false, 1, transparency: 0.45);
        PaneRun("pbr plane a0.45 dualLayer", pbr, false, true, 0.45, mode: SCNTransparencyMode.dualLayer);
    }

    // ---- Penumbra experiment (mirrors exp/penumbra.swift): 10-90% shadow edge width of a 3 m box edge, light 60 deg up.
    private static void PenumbraRun(string label, bool auto, double ortho, double map, double radius, int samples, SCNShadowMode mode = SCNShadowMode.forward, double height = 3)
    {
        var scene = new SCNScene(); scene.background.contents = NSColor.black;
        var fm = new SCNMaterial { lightingModel = SCNMaterial.LightingModel.physicallyBased }; fm.diffuse.contents = NSColor.white; fm.roughness.contents = 1.0;
        var floor = new SCNPlane(40, 40); floor.materials = new() { fm };
        var fn = new SCNNode(floor); fn.eulerAngles.x = -Math.PI / 2; scene.rootNode.addChildNode(fn);
        var box = new SCNBox(10, height, 10, 0); box.materials = new() { fm };
        scene.rootNode.addChildNode(new SCNNode(box) { position = new SCNVector3(-5, height / 2, 0) });
        var l = new SCNNode { light = new SCNLight { type = SCNLight.LightType.directional, intensity = 1000 } };
        l.light.castsShadow = true; l.light.shadowMode = mode; l.light.shadowColor = NSColor.black; l.light.shadowRadius = radius; l.light.shadowSampleCount = samples;
        l.light.shadowMapSize = new CGSize(map, map); l.light.orthographicScale = ortho; l.light.automaticallyAdjustsShadowProjection = auto;
        l.light.zNear = 0.1; l.light.zFar = 220; l.light.maximumShadowDistance = 500; l.light.shadowBias = 0.6;
        var d = new SCNVector3(-Math.Cos(Math.PI / 3), Math.Sin(Math.PI / 3), 0);
        l.position = d * 80; l.look(SCNVector3Zero, new SCNVector3(0, 1, 0), new SCNVector3(0, 0, -1));
        scene.rootNode.addChildNode(l);
        var cam = new SCNNode { camera = new SCNCamera { fieldOfView = 10, zNear = 1, zFar = 250 } };
        double edge = height / Math.Tan(Math.PI / 3);
        cam.position = new SCNVector3(edge, 10, 0); cam.look(new SCNVector3(edge, 0, 0), new SCNVector3(0, 0, -1), new SCNVector3(0, 0, -1));
        scene.rootNode.addChildNode(cam);
        renderer.scene = scene; renderer.pointOfView = cam;
        const int Wd = 512;
        renderer.SnapshotImage(new Vector2I(Wd, Wd), SCNAntialiasingMode.none);
        var b = renderer.SnapshotImage(new Vector2I(Wd, Wd), SCNAntialiasingMode.none);
        static double lin(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        double half = 10 * Math.Tan(5 * Math.PI / 180); int row = Wd / 2;
        var prof = new double[Wd];
        for (int x = 0; x < Wd; x++) { double sum = 0; for (int dy = -4; dy <= 4; dy++) sum += lin(b.GetPixel(x, row + dy).R); prof[x] = sum / 9; }
        double lit = prof.Skip(Wd - 20).Average(), dark = prof.Take(20).Average();
        double cross(double f) { double t = dark + (lit - dark) * f; for (int x = 1; x < Wd; x++) if (prof[x - 1] < t && prof[x] >= t) return x - 1 + (t - prof[x - 1]) / (prof[x] - prof[x - 1]); return double.NaN; }
        double px = 2 * half / Wd;
        GD.Print($"{label,-44}penumbra10-90 {(cross(0.9) - cross(0.1)) * px * 100,6:0.00} cm  edge offset {(cross(0.5) - Wd / 2.0) * px * 100,6:0.00} cm  dark {dark:0.000} lit {lit:0.000}");
    }
    private static void Penumbra()
    {
        PenumbraRun("sunA ortho58 map4096 r3 s8", false, 58, 4096, 3, 8);
        PenumbraRun("sunB ortho58 map2048 r2 s8", false, 58, 2048, 2, 8);
        PenumbraRun("ortho58 map4096 r1 s8", false, 58, 4096, 1, 8);
        PenumbraRun("ortho58 map4096 r8 s8", false, 58, 4096, 8, 8);
        PenumbraRun("ortho58 map4096 r3 s1", false, 58, 4096, 3, 1);
        PenumbraRun("ortho20 map4096 r3 s8", false, 20, 4096, 3, 8);
        PenumbraRun("sandbox auto ortho10 map4096 r3 s16 deferred", true, 10, 4096, 3, 16, SCNShadowMode.deferred);
        PenumbraRun("menu auto ortho2 map2048 r32 s32 deferred", true, 2, 2048, 32, 32, SCNShadowMode.deferred);
    }

    // ---- Mirror experiment (mirrors exp/mirror.swift): metal sphere under the BinarySky probe only.
    private static void Mirror()
    {
        var probe = skyProbe(new Vector3(0.20f, 0.38f, 0.62f), new Vector3(0.65f, 0.72f, 0.75f));
        static double lin(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        foreach (var r in new[] { 0.0, 0.1, 0.2, 0.3, 0.45, 0.6, 0.8, 1.0 })
        {
            var scene = new SCNScene(); scene.background.contents = NSColor.black;
            scene.lightingEnvironment.contents = probe; scene.lightingEnvironment.intensity = 1;
            if (double.TryParse(System.Environment.GetEnvironmentVariable("CAL_MIRROR_AMB"), out var amb))
                scene.rootNode.addChildNode(new SCNNode { light = new SCNLight { type = SCNLight.LightType.ambient, intensity = amb, color = ink(new Vector3(0.54f, 0.62f, 0.78f)) } });
            var s = new SCNSphere(1) { segmentCount = 96 };
            var m = new SCNMaterial { lightingModel = SCNMaterial.LightingModel.physicallyBased }; m.diffuse.contents = System.Environment.GetEnvironmentVariable("CAL_MIRROR_GRAY") != null ? color(0xc3c7c9) : NSColor.white; m.metalness.contents = 1.0; m.roughness.contents = r;
            s.materials = new() { m }; scene.rootNode.addChildNode(new SCNNode(s));
            var cam = new SCNNode { camera = new SCNCamera { usesOrthographicProjection = true, orthographicScale = 1.05 } };
            cam.position = new SCNVector3(0, 0, 5); scene.rootNode.addChildNode(cam);
            renderer.scene = scene; renderer.pointOfView = cam;
            renderer.SnapshotImage(new Vector2I(210, 210), SCNAntialiasingMode.none);
            var b = renderer.SnapshotImage(new Vector2I(210, 210), SCNAntialiasingMode.none);
            var o = $"r{r:0.0} ";
            for (double ne = -80; ne <= 80; ne += 10)
            {
                double y = Math.Sin(ne * Math.PI / 180); int py = (int)Math.Round(105 - y * 100, MidpointRounding.AwayFromZero);
                var c = b.GetPixel(105, py);
                o += $" {0.2126 * lin(c.R) + 0.7152 * lin(c.G) + 0.0722 * lin(c.B):0.000}";
            }
            GD.Print(o);
        }
    }

    // ---- SSAO probe (Swift twin ssao.swift): ambient-only race camera (HDR, fixed exposure, no bloom), SSAO
    // 0.70/1.6/0.025 on and off; Marvin, an R2-like cylinder, a BB-8-like sphere and a box on a flat ground, from four
    // distances. Writes ssao_NAME_on/off.png; the on/off ratio is SceneKit's ambient occlusion factor.
    private static void SsaoProbe()
    {
        var env = System.Environment.GetEnvironmentVariable("SSAO_I");
        double ssaoIntensity = env != null ? double.Parse(env) : 0.70;
        env = System.Environment.GetEnvironmentVariable("SSAO_R");
        double ssaoRadius = env != null ? double.Parse(env) : 1.6;
        bool ambientOnly = System.Environment.GetEnvironmentVariable("SSAO_SUN") == null;
        foreach (var (name, eye, target) in new[] {
            ("near", new SCNVector3(0.9, 0.55, 1.6), new SCNVector3(0, 0.25, 0)),
            ("mid", new SCNVector3(1.8, 1.3, 3.6), new SCNVector3(0, 0.25, 0)),
            ("far", new SCNVector3(5, 4.5, 11), new SCNVector3(0, 0.25, 0)),
            ("low", new SCNVector3(0.7, 0.18, 1.3), new SCNVector3(0, 0.2, 0)) })
        {
            foreach (var on in new[] { true, false })
            {
                var scene = new SCNScene(); scene.background.contents = color(0x9db7cf);
                var ground = new SCNBox(60, 0.2, 60, 0); ground.materials = new() { material(0xb07a55, roughness: 0.95) };
                add(scene.rootNode, ground, new SCNVector3(0, -0.1, 0));
                marvin(scene.rootNode, new SCNVector3(0, 0, 0), 0.35, -0.30);
                var cyl = new SCNCylinder(0.22, 0.6); cyl.materials = new() { material(0xe8ecef, roughness: 0.5) };
                add(scene.rootNode, cyl, new SCNVector3(0.9, 0.3, -0.5));
                var sph = new SCNSphere(0.28) { segmentCount = 64 }; sph.materials = new() { material(0xd8d2c8, roughness: 0.55) };
                add(scene.rootNode, sph, new SCNVector3(-0.8, 0.28, -0.2));
                var box = new SCNBox(0.4, 0.4, 0.4, 0.02); box.materials = new() { material(0x8a9aa0, roughness: 0.7) };
                add(scene.rootNode, box, new SCNVector3(0.3, 0.2, -1.3));
                scene.rootNode.addChildNode(new SCNNode { light = new SCNLight { type = SCNLight.LightType.ambient, intensity = ambientOnly ? 1000 : 190 } });
                if (!ambientOnly)
                {
                    var sun = new SCNNode { light = new SCNLight { type = SCNLight.LightType.directional, intensity = 1550 } };
                    sun.eulerAngles = new SCNVector3(-0.9, 2.6, 0); scene.rootNode.addChildNode(sun);
                }
                var cam = camera(scene, eye, target, 48, 0.02, 250);
                var lens = cam.camera;
                lens.wantsHDR = true; lens.wantsExposureAdaptation = false; lens.exposureOffset = 0; lens.bloomIntensity = 0;
                lens.screenSpaceAmbientOcclusionIntensity = on ? ssaoIntensity : 0;
                lens.screenSpaceAmbientOcclusionRadius = ssaoRadius; lens.screenSpaceAmbientOcclusionBias = 0.025;
                renderer.scene = scene; renderer.pointOfView = cam;
                var aa = System.Environment.GetEnvironmentVariable("SSAO_AA") == "none" ? SCNAntialiasingMode.none : SCNAntialiasingMode.multisampling2X;
                renderer.SnapshotImage(new Vector2I(W, H), aa);
                var img = renderer.SnapshotImage(new Vector2I(W, H), aa);
                img.SavePng(dir.PathJoin($"ssao_{name}_{(on ? "on" : "off")}.png"));
                GD.Print($"rendered ssao_{name}_{(on ? "on" : "off")}");
            }
        }
    }

    // ---- Main menu portrait (MainMenu.swift)
    private static void Menu()
    {
        if (!string.IsNullOrEmpty(only) && !"menu".StartsWith(only) && !only.StartsWith("menu")) return;
        var stage = new SCNScene();
        stage.background.contents = color(0xf2f1e9);
        var cam = camera(stage, new SCNVector3(0, 1.05, 2.25), new SCNVector3(0, 0.40, 0), 36, 1, 100);
        var ambient = new SCNNode { light = new SCNLight { type = SCNLight.LightType.ambient, intensity = 650 } }; stage.rootNode.addChildNode(ambient);
        var key = new SCNNode { light = new SCNLight { type = SCNLight.LightType.directional, intensity = 1100 } };
        key.eulerAngles = new SCNVector3(-1.15, -0.6, 0);
        key.light.castsShadow = true; key.light.shadowMode = SCNShadowMode.deferred;
        key.light.shadowMapSize = new CGSize(2048, 2048);
        key.light.orthographicScale = 2;
        key.light.shadowSampleCount = 32; key.light.shadowRadius = 32;
        key.light.shadowBias = 0.001;
        key.light.shadowColor = NSColor.black.withAlphaComponent(0.18);
        stage.rootNode.addChildNode(key);
        var floor = new SCNFloor { reflectivity = 0 };
        var surface = new SCNMaterial(); surface.diffuse.contents = color(0xf2f1e9); surface.lightingModel = SCNMaterial.LightingModel.constant;
        floor.materials = new() { surface }; stage.rootNode.addChildNode(new SCNNode(floor));
        marvin(stage.rootNode, SCNVector3Zero, 0.35, -0.30);
        var s1 = new SCNSphere(0.14) { segmentCount = 48 }; s1.materials = new() { material(0xc3c7c9, metal: 0.8, roughness: 0.65) };
        add(stage.rootNode, s1, new SCNVector3(-0.8, 0.14, -0.2));
        var s2 = new SCNSphere(0.14) { segmentCount = 48 }; s2.materials = new() { material(0xc83a24, roughness: 0.45) };
        add(stage.rootNode, s2, new SCNVector3(0.8, 0.14, -0.2));
        render("menu_portrait", stage, cam, SCNAntialiasingMode.multisampling4X);
    }
}
