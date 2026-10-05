// Port of Sources/MarvinSimulator/VisualRegressionSmoke.swift (an AppController extension): `--visual-regression-test DIR`.
// A shadow sweep of the four robots under the race suns from 24 camera angles (shadow-angle-N.png; shadow ratios of
// floor samples that must stay stable between angles), 12 close-ups around Marvin on the track (track-closeup-N.png,
// track-no-shadows.png) and contact sheets of every citizen variant and detail level (citizens-lodN-front/back.png).
// Writes visual-regression.json (.prettyPrinted without .sortedKeys, as on macOS: Swift's key order is not defined there;
// here keys keep the report's insertion order).
//
// PORT: colorAt(...).usingColorSpace(.deviceRGB) reads the stored sRGB values (PORTING.md "Known deviations": macOS converts
// through the display profile, which lifts mid-tones a little); the shadow ratios compare two such reads, so both sides
// shift alike. The hit test that rejects floor samples hidden by a robot (firstFoundOnly) uses the nearest hit.
using System;
using System.Collections.Generic;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

public partial class AppController
{
    [GameMode("--visual-regression-test")]
    public static async System.Threading.Tasks.Task RunVisualRegressionTest(string dir, Godot.SceneTree tree)
    {
        var app = await launchSmoke(tree, dir);
        var passed = app.checkVisualRegressions(at: URL.fileURLWithPath(dir));
        exit(passed ? 0 : 1);
    }

    public bool checkVisualRegressions(URL at)
    {
        var directory = at;
        try
        {
            FileManager.@default.createDirectory(directory, withIntermediateDirectories: true);
            weatherOverride = false; startDirtTrack(); dirtIntro = null; raceHUD.isHidden = true;
            dirtWorld.sky.apply(new BinaryDaylight(fraction: 0.32, phase: 1.2));
            SCNScene scene = new SCNScene(); SCNNode camera = new SCNNode(); camera.camera = new SCNCamera(); camera.camera.zNear = 0.05; camera.camera.zFar = 250;
            scene.background.contents = RobotFunctions.color(0xb9c5ca); scene.rootNode.addChildNode(camera);
            var floor = new SCNNode(new SCNPlane(1536, 1536)); floor.eulerAngles.x = -Math.PI / 2; floor.position.y = -0.006; floor.name = "QA floor";
            var mat = new SCNMaterial(); mat.diffuse.contents = RobotFunctions.color(0xa88864); mat.lightingModel = SCNMaterial.LightingModel.lambert; floor.geometry.materials = new() { mat }; scene.rootNode.addChildNode(floor);
            // Match the town's depth range. A tiny isolated receiver hides the
            // automatic projection failure caused by distant city geometry.
            for (int i = 0; i < 16; i++)
            {
                double a = (double)i * Math.PI / 8, r = (double)(i % 2 == 0 ? 60 : 110);
                var tower = new SCNNode(new SCNBox(8, 12, 8, 0));
                tower.position = new SCNVector3(sin(a) * r, 6, cos(a) * r); scene.rootNode.addChildNode(tower);
            }
            var ambient = new SCNNode(); ambient.light = new SCNLight(); ambient.light.type = SCNLight.LightType.ambient; ambient.light.intensity = 250; scene.rootNode.addChildNode(ambient);
            var lights = dirtWorld.sky.suns.Select(s => s.clone()).ToList(); foreach (var l in lights) { scene.rootNode.addChildNode(l); }
            if (environmentValue("MARVIN_SHADOW_AUTO") == "1")
            {
                foreach (var (light, i) in lights.Select((l, i) => (l, i)))
                {
                    light.light.automaticallyAdjustsShadowProjection = true;
                    light.light.shadowMapSize = new CGSize(i == 0 ? 2048 : 1024, i == 0 ? 2048 : 1024);
                    light.light.shadowSampleCount = 4;
                }
            }
            var robots = new List<SCNNode>();
            foreach (var (c, i) in RacePerformance.CharacterAllCases.Select((c, i) => (c, i)))
            {
                var node = modelRoot(c).clone(); node.position = new SCNVector3((double)i * 2.3 - 3.45, 0, 0); node.eulerAngles = SCNVector3Zero; scene.rootNode.addChildNode(node); robots.Add(node);
            }
            var renderer = new SCNRenderer(view.device, null); renderer.scene = scene; renderer.pointOfView = camera;
            NSBitmapImageRep capture() => NSBitmapImageRep.data(renderer.snapshot(0, new CGSize(1280, 800), SCNAntialiasingMode.multisampling4X).tiffRepresentation);
            void write(NSBitmapImageRep bitmap, string name) => bitmap.representation(NSBitmapImageFileType.png).write(directory.appendingPathComponent(name));
            var observations = new Dictionary<int, List<double>>(); var perRobotShadowSamples = new int[4];
            double sum(NSColor c) => (double)(c.redComponent + c.greenComponent + c.blueComponent);
            for (int i = 0; i < 24; i++)
            {
                var angle = (double)(i % 12) * Math.PI / 6; var near = i >= 12;
                var altitude = near ? (i % 3 == 0 ? 0.75 : 1.6) : (i % 3 == 0 ? 1.7 : 4.2);
                double radius = near ? 2.2 : 7.0, centerX = near ? -3.45 : 0.0;
                camera.position = new SCNVector3(centerX + sin(angle) * radius, altitude, cos(angle) * radius);
                camera.look(at: new SCNVector3(centerX, 0.2, 0), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                foreach (var l in lights) { l.light.castsShadow = false; } var unshadowed = capture();
                foreach (var l in lights) { l.light.castsShadow = true; } var shadowed = capture();
                write(shadowed, $"shadow-angle-{i}.png");
                for (int z = -15; z <= 15; z++)
                {
                    for (int x = -45; x <= 45; x++)
                    {
                        var p = new SCNVector3((double)x * 0.1, 0, (double)z * 0.1); var screen = renderer.projectPoint(p);
                        int px = (int)screen.x, py = 799 - (int)screen.y;
                        if (!(px >= 2 && px < 1278 && py >= 2 && py < 798 && screen.z > 0 && screen.z < 1)) { continue; }
                        // Reject samples whose view is occluded by a robot.
                        var hits = renderer.hitTest(new CGPoint((double)screen.x, (double)screen.y), new Dictionary<string, object> { [SCNHitTestOption.firstFoundOnly] = true, [SCNHitTestOption.categoryBitMask] = 1 });
                        if (hits.FirstOrDefault()?.node.name != "QA floor") { continue; }
                        // Exclude silhouette edges: their subpixel coverage changes
                        // with viewing angle even when the world shadow is stable.
                        var localRatios = new List<double>();
                        foreach (var dy in new[] { -2, 0, 2 })
                        {
                            foreach (var dx in new[] { -2, 0, 2 })
                            {
                                var u = unshadowed.colorAt(px + dx, py + dy).usingColorSpace(NSColorSpace.deviceRGB);
                                var v = shadowed.colorAt(px + dx, py + dy).usingColorSpace(NSColorSpace.deviceRGB);
                                localRatios.Add(sum(v) / max(0.01, sum(u)));
                            }
                        }

                        var a = unshadowed.colorAt(px, py).usingColorSpace(NSColorSpace.deviceRGB); var b = shadowed.colorAt(px, py).usingColorSpace(NSColorSpace.deviceRGB);
                        var reference = sum(a);
                        var ratio = sum(b) / max(0.01, reference);
                        if (ratio < 0.85) { var nearest = max(0, min(3, (int)Math.Round(((double)x * 0.1 + 3.45) / 2.3, MidpointRounding.AwayFromZero))); perRobotShadowSamples[nearest] += 1; }
                        if (!(localRatios.Max() - localRatios.Min() < 0.08)) { continue; }
                        var key = (z + 15) * 91 + x + 45;
                        if (!observations.TryGetValue(key, out var list)) { observations[key] = list = new List<double>(); }
                        list.Add(ratio);
                    }
                }
            }
            var repeated = observations.Values.Where(o => o.Count >= 3).ToList();
            var unstable = repeated.Count(o => (o.Max() - o.Min()) > 0.15);
            var shadowSamples = observations.Values.SelectMany(o => o).Count(r => r < 0.85);
            var passed = repeated.Count > 100 && perRobotShadowSamples.All(s => s > 15) && shadowSamples > 30 && unstable < max(8, repeated.Count / 100);
            print($"Shadow sweep: {repeated.Count} world samples, {shadowSamples} shadow observations, {unstable} unstable");
            updateOpponents();
            var center = robot.root.worldPosition;
            renderer.scene = dirtWorld.scene; renderer.pointOfView = world.camera;
            for (int i = 0; i < 12; i++)
            {
                var a = (double)i * Math.PI / 6; var high = i % 2 == 0;
                world.camera.position = new SCNVector3((double)center.x + sin(a) * 1.5, (double)center.y + (high ? 1.6 : 0.7), (double)center.z + cos(a) * 1.5);
                world.camera.look(at: new SCNVector3(center.x, center.y + 0.3, center.z), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                write(capture(), $"track-closeup-{i}.png");
                if (i == 1)
                {
                    foreach (var sun in dirtWorld.sky.suns) { sun.light.castsShadow = false; }
                    write(capture(), "track-no-shadows.png");
                    foreach (var sun in dirtWorld.sky.suns) { sun.light.castsShadow = true; }
                }
            }
            renderer.scene = scene; renderer.pointOfView = camera;
            foreach (var r in robots) { r.removeFromParentNode(); }
            var crowd = new TownCrowd(); var figures = crowd.inspectionFigures();
            passed = passed && figures.Count == 36;
            for (int lod = 0; lod < 3; lod++)
            {
                foreach (var back in new[] { false, true })
                {
                    var sheet = new NSImage(new NSSize(1280, 960)); sheet.lockFocus(); RobotFunctions.color(0xb9c5ca).setFill(); new NSRect(0, 0, 1280, 960).fill(); sheet.unlockFocus();
                    foreach (var (entry, index) in figures.Where(f => f.Item1.EndsWith($"lod{lod}", StringComparison.Ordinal)).Select((f, i) => (f, i)))
                    {
                        var node = entry.Item2; scene.rootNode.addChildNode(node);
                        var seated = entry.Item1.Contains("sit"); var target = new SCNVector3(0, seated ? 0.30 : 0.51, 0);
                        camera.position = new SCNVector3(back ? -0.3 : 0.3, seated ? 0.55 : 0.62, back ? -1.8 : 1.8); camera.look(at: target, up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                        var shot = renderer.snapshot(0, new CGSize(320, 320), SCNAntialiasingMode.multisampling4X);
                        sheet.lockFocus(); var rect = new NSRect(index % 4 * 320, (2 - index / 4) * 320, 320, 320); shot.draw(rect);
                        entry.Item1.draw(new NSPoint(rect.minX + 8, rect.minY + 8), new Dictionary<NSAttributedString.Key, object> { [NSAttributedString.Key.font] = NSFont.systemFont(12), [NSAttributedString.Key.foregroundColor] = NSColor.black }); sheet.unlockFocus();
                        node.removeFromParentNode();
                    }
                    write(NSBitmapImageRep.data(sheet.tiffRepresentation), $"citizens-lod{lod}-{(back ? "back" : "front")}.png");
                }
            }
            var report = new Dictionary<string, object> { ["passed"] = passed, ["repeatedWorldSamples"] = repeated.Count, ["shadowSamples"] = shadowSamples, ["unstableSamples"] = unstable, ["citizenVariantsAndLODs"] = figures.Count, ["perRobotShadowSamples"] = perRobotShadowSamples };
            JSONSerialization.data(report, JSONSerialization.WritingOptions.prettyPrinted).write(directory.appendingPathComponent("visual-regression.json"));
            return passed;
        }
        catch (Exception error) { print(error.ToString()); return false; }
    }
}
