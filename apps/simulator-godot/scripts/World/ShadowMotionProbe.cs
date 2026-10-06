using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Marvin.Core;

namespace Marvin;

public partial class AppController
{
    /// <summary>
    /// `--shadow-motion-probe DIR`: are the suns' shadow maps stable while the camera moves (no crawl or shimmer)?
    /// PORT: a Godot-only QA mode for the facade's split fit (SceneKitRuntime.FitShadows; docs/performance.md, "Near
    /// shadow splits").
    /// Exact part: the race suns (BinarySky, daylight 0.5) over a flat ground with shadow-only casters (rotated boxes the
    /// camera does not draw), seen straight down from 3, 14, 40 and 85 m (one height per split of the hard filter's near
    /// splits) through the race camera's lens (fov 48, near 0.02, far 250) at 960 x 540. Each frame moves the camera by
    /// exactly STEP pixel footprints along x or z, so frame i+1 equals frame i shifted by STEP pixels wherever the shadow
    /// map's texels stayed put. A camera moving straight down never changes the box's farthest view depth, which the last
    /// split follows, so a last run moves the suns (and their boxes) 2 m per frame along the light instead. Prints the share
    /// of shadow-edge pixels that changed by more than 3/255 between consecutive frames and the shadow distances used, and
    /// writes the first frame and the worst difference (x 8) of each run.
    /// Race part (pin it, e.g. MARVIN_GRID_SLOTS=0,3,1,2 MARVIN_DAYLIGHT_FRACTION=0.5 MARVIN_DAYLIGHT_PHASE=1.2): the race
    /// world at the start grid; the chase camera moved forward 2 cm per frame (48 frames: the near
    /// splits) and the same view 6 m higher, tilted up, moved 10 cm per frame (the far splits); frames in DIR/chase and
    /// DIR/far. Each frame is also rendered without the suns' shadows; the ratio is the shadow factor, whose temporal second
    /// difference |f(i) - (f(i-1) + f(i+1)) / 2| per image row band is small where shadow edges move with the view and
    /// large where texels jump.
    /// MARVIN_SHADOW_MOTION=exact|race|both (default both); =casters lists the race world's largest shadow casters instead.
    /// </summary>
    [GameMode("--shadow-motion-probe")]
    public static async Task RunShadowMotionProbe(string dir, SceneTree tree)
    {
        bool passed = false;
        try
        {
            string part = System.Environment.GetEnvironmentVariable("MARVIN_SHADOW_MOTION") ?? "both";
            var report = new List<string>();
            if (part != "race" && part != "casters")
            {
                foreach (var height in new[] { 3.0, 14.0, 40.0, 85.0 })
                    foreach (var alongZ in new[] { false, true }) report.Add(ShadowMotionExact(dir, height, alongZ, false));
                report.Add(ShadowMotionExact(dir, 85, false, true));
            }
            if (part != "exact")
            {
                var app = await launchSmoke(tree, dir);
                app.startDirtTrack(); app.dirtIntro = null; app.race.countDown(dt: 3);
                app.raceHUD.isHidden = true; app.updateCamera(snap: true);
                for (int frame = 0; frame < 3; frame++)
                {
                    app.updateOpponents(); app.updateRaceWorld(dt: 1.0 / 60);
                    await AppController.frame(tree);
                }
                if (part == "casters") report.AddRange(app.ShadowCasterList());
                else report.AddRange(app.ShadowMotionRace(dir));
            }
            File.WriteAllLines(Path.Combine(dir, "shadow-motion.txt"), report);
            foreach (var line in report) GD.Print(line);
            passed = true;
        }
        catch (Exception error) { GD.PrintErr($"Shadow motion probe failed: {error}"); }
        exit(passed ? 0 : 1);
    }

    private static string F2(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    private static string ShadowMotionExact(string dir, double height, bool alongZ, bool rampFar)
    {
        const int width = 960, rows = 540, step = 5, frames = 16;
        var scene = new SCNScene();
        var sky = new BinarySky(scene: scene);
        sky.apply(new BinaryDaylight(fraction: 0.5, phase: 1.2));
        var ground = new SCNNode(new SCNPlane(width: 600, height: 600)); ground.eulerAngles.x = -Math.PI / 2;
        var groundMaterial = new SCNMaterial { lightingModel = SCNMaterial.LightingModel.lambert };
        groundMaterial.diffuse.contents = NSColor.whiteAlpha(0.3, 1); ground.geometry.materials = new() { groundMaterial };
        scene.rootNode.addChildNode(ground);
        // Shadow-only casters (category 2; the camera draws category 1): rotated boxes scaled with the height.
        double s = height;
        int k = 0;
        for (double x = -1.4 * s; x <= 2.2 * s; x += 0.45 * s)
            for (double z = -0.9 * s; z <= 0.9 * s; z += 0.3 * s, k++)
            {
                var box = new SCNNode(new SCNBox(0.10 * s, 0.25 * s, 0.03 * s, 0));
                box.position = new SCNVector3(x + 0.07 * s * Math.Sin(k), 0.125 * s + 0.002 * s, z);
                box.eulerAngles.y = 0.37 + 0.61 * k; box.categoryBitMask = 2;
                scene.rootNode.addChildNode(box);
            }
        var camera = new SCNNode { camera = new SCNCamera { fieldOfView = 48, zNear = 0.02, zFar = 250, categoryBitMask = 1 } };
        camera.eulerAngles = new SCNVector3(-Math.PI / 2, 0, 0);
        scene.rootNode.addChildNode(camera);
        var renderer = new SCNRenderer(null, null) { scene = scene, pointOfView = camera };
        double footprint = 2 * height * Math.Tan(24 * Math.PI / 180) / rows;
        var images = new List<Image>();
        var maxDistances = new HashSet<float>();
        for (int i = 0; i < frames; i++)
        {
            // Along x the view moves right (the ground moves left by STEP pixels), along z it moves up (the ground moves down).
            // rampFar: the suns' boxes move 2 m per frame along the light (the ground stays inside them).
            if (rampFar)
                foreach (var (sun, d) in sky.suns.Zip(sky.daylight.directions))
                    sun.position = new SCNVector3(d.x * (80 + 2 * i), d.y * (80 + 2 * i), d.z * (80 + 2 * i));
            camera.position = alongZ ? new SCNVector3(0, height, -i * step * footprint) : new SCNVector3(i * step * footprint, height, 0);
            var image = renderer.SnapshotImage(new Vector2I(width, rows), SCNAntialiasingMode.none);
            image.Convert(Image.Format.Rgba8); images.Add(image);
            if (sky.suns[0].GodotLight is DirectionalLight3D lm) maxDistances.Add(lm.DirectionalShadowMaxDistance);
        }
        var data = images.Select(im => im.GetData()).ToList();
        float L(byte[] im, int x, int y) { int o = (y * width + x) * 4; return (im[o] + im[o + 1] + im[o + 2]) / (3f * 255); }
        var shares = new List<double>(); long changedTotal = 0, edgeTotal = 0;
        byte[] worstDiff = null; double worst = -1;
        for (int i = 0; i + 1 < frames; i++)
        {
            byte[] a = data[i], b = data[i + 1];
            var diff = new byte[width * rows * 4];
            long edges = 0, changed = 0;
            int dx = alongZ ? 0 : step, dy = alongZ ? -step : 0;
            for (int y = 1 + step; y < rows - step - 1; y++)
                for (int x = 1; x < width - step - 1; x++)
                {
                    // Frame i+1 at (x, y) shows the ground point frame i showed at (x + dx, y + dy).
                    float va = L(a, x + dx, y + dy);
                    bool edge = Math.Abs(L(a, x + dx + 1, y + dy) - va) > 10f / 255 || Math.Abs(L(a, x + dx, y + dy + 1) - va) > 10f / 255;
                    float d = Math.Abs(L(b, x, y) - va);
                    if (edge) edges++;
                    if (d > 3f / 255) changed++;
                    int o = (y * width + x) * 4; byte v = (byte)Math.Min(255, d * 8 * 255);
                    diff[o] = diff[o + 1] = diff[o + 2] = v; diff[o + 3] = 255;
                }
            double share = edges > 0 ? (double)changed / edges : 0;
            shares.Add(share); changedTotal += changed; edgeTotal += edges;
            if (share > worst) { worst = share; worstDiff = diff; }
        }
        string name = $"exact-h{height:0}-{(alongZ ? "z" : "x")}{(rampFar ? "-far" : "")}";
        images[0].SavePng(Path.Combine(dir, name + ".png"));
        if (worstDiff != null) Image.CreateFromData(width, rows, false, Image.Format.Rgba8, worstDiff).SavePng(Path.Combine(dir, name + "-diff.png"));
        var light = sky.suns[0].GodotLight as DirectionalLight3D;
        return $"exact h={height:0} m along {(alongZ ? "z" : "x")}{(rampFar ? ", boxes moved 2 m per frame along the light" : "")}: changed / edge pixels per frame mean {F2(shares.Average())} max {F2(shares.Max())} " +
            $"(changed {changedTotal}, edge {edgeTotal}; frames with changes {shares.Count(v => v > 0.002)} of {shares.Count}); " +
            $"sun A {light?.DirectionalShadowMode}, {maxDistances.Count} shadow distance(s) {light?.DirectionalShadowMaxDistance:0.0}, splits " +
            $"{light?.DirectionalShadowSplit1:0.000}/{light?.DirectionalShadowSplit2:0.000}/{light?.DirectionalShadowSplit3:0.000}";
    }

    /// <summary>MARVIN_SHADOW_MOTION=casters: the race world's largest shadow casters (triangles, world size, shader modifiers)
    /// and the flat ones (under 5 cm high, over 100 square metres), which can only shade what lies below them.</summary>
    private IEnumerable<string> ShadowCasterList()
    {
        var rows = new List<(long tris, Aabb box, string line)>();
        void Visit(Node node, string path)
        {
            if (node is MeshInstance3D mi && mi.Mesh is ArrayMesh mesh && mi.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off && mi.IsVisibleInTree())
            {
                long tris = 0;
                for (int s = 0; s < mesh.GetSurfaceCount(); s++)
                {
                    int n = mesh.SurfaceGetArrayIndexLen(s);
                    tris += (n > 0 ? n : mesh.SurfaceGetArrayLen(s)) / 3;
                }
                var box = mi.GlobalTransform * mesh.GetAabb();
                string mods = "";
                if (node.GetParent() is SCNNode owner && owner.geometry != null)
                    foreach (var m in owner.geometry.materials)
                        if (m.shaderModifiers != null) foreach (var (k, _) in m.shaderModifiers) mods += $"{k} ";
                rows.Add((tris, box, $"{tris,8} tris  {box.Size.X,6:0.0} x {box.Size.Y,5:0.0} x {box.Size.Z,6:0.0} m  {mi.CastShadow,-11} [{mods.Trim()}] {path}"));
            }
            foreach (var child in node.GetChildren(true)) Visit(child, node is SCNNode sn && !string.IsNullOrEmpty(sn.name) ? sn.name : path);
        }
        Visit(dirtWorld.scene.rootNode, "");
        var lines = new List<string> { $"casters: {rows.Count}, triangles {rows.Sum(r => r.tris)}" };
        lines.AddRange(rows.OrderByDescending(r => r.tris).Take(40).Select(r => r.line));
        lines.Add("flat (under 5 cm high, over 100 square metres):");
        lines.AddRange(rows.Where(r => r.box.Size.Y < 0.05f && r.box.Size.X * r.box.Size.Z > 100).Select(r => r.line));
        return lines;
    }

    private IEnumerable<string> ShadowMotionRace(string dir)
    {
        var lines = new List<string>();
        var renderer = new SCNRenderer(null, null) { scene = dirtWorld.scene, pointOfView = world.camera };
        var start = world.camera.position;
        var front = world.camera.convertVector(new SCNVector3(0, 0, -1), to: null);
        var flat = new SCNVector3(front.x, 0, front.z);
        double len = Math.Sqrt(flat.x * flat.x + flat.z * flat.z); flat = new SCNVector3(flat.x / len, 0, flat.z / len);
        var orientation = world.camera.orientation;
        const int width = 1280, rows = 720, frames = 48;
        foreach (var (name, stepMetres, lift, pitch) in new[] { ("chase", 0.02, 0.0, 0.0), ("far", 0.10, 6.0, 0.12) })
        {
            string sub = Path.Combine(dir, name); Directory.CreateDirectory(sub);
            // The shadow factor of each pixel: the frame over the same frame rendered without the suns' shadows, so the
            // ground texture's own motion cancels and only shadow changes remain.
            var factors = new List<float[]>();
            for (int i = 0; i < frames; i++)
            {
                world.camera.orientation = orientation;
                if (pitch != 0) world.camera.eulerAngles.x += pitch;
                world.camera.position = new SCNVector3(start.x + flat.x * stepMetres * i, start.y + lift, start.z + flat.z * stepMetres * i);
                var image = renderer.SnapshotImage(new Vector2I(width, rows), SCNAntialiasingMode.multisampling4X);
                image.Convert(Image.Format.Rgba8);
                image.SavePng(Path.Combine(sub, $"{name}-{i:00}.png"));
                foreach (var sun in dirtWorld.sky.suns) sun.light.castsShadow = false;
                var lit = renderer.SnapshotImage(new Vector2I(width, rows), SCNAntialiasingMode.multisampling4X);
                lit.Convert(Image.Format.Rgba8);
                foreach (var sun in dirtWorld.sky.suns) sun.light.castsShadow = true;
                byte[] a = image.GetData(), b = lit.GetData();
                var f = new float[width * rows];
                for (int p = 0; p < f.Length; p++)
                {
                    int o = p * 4;
                    f[p] = Math.Min(1f, (a[o] + a[o + 1] + a[o + 2] + 3f) / (b[o] + b[o + 1] + b[o + 2] + 3f));
                }
                factors.Add(f);
            }
            // Temporal second difference of the shadow factor per row band (top to bottom, eight bands), and the share of
            // pixels where it exceeds 0.1 (a texel row that jumped instead of moving with the view).
            var bands = new double[8]; var jumps = new long[8]; var counts = new long[8];
            for (int i = 1; i + 1 < frames; i++)
                for (int p = 0; p < width * rows; p++)
                {
                    double d = Math.Abs(factors[i][p] - (factors[i - 1][p] + factors[i + 1][p]) / 2);
                    int band = p / width * 8 / rows;
                    bands[band] += d; counts[band]++; if (d > 0.1) jumps[band]++;
                }
            lines.Add($"race {name}: shadow-factor second difference per row band (top..bottom) mean " +
                string.Join(" ", bands.Select((v, j) => F2(v / Math.Max(1, counts[j])))) + "; share over 0.1 (%) " +
                string.Join(" ", jumps.Select((v, j) => F2(100.0 * v / Math.Max(1, counts[j])))));
        }
        world.camera.position = start; world.camera.orientation = orientation;
        return lines;
    }
}
