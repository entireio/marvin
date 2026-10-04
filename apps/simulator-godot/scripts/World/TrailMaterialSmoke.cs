using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

/// <summary>
/// `--trail-material-smoke-test DIR` (TrailMaterialSmoke.swift, AppController.checkTrailMaterial): sub-spacing motion
/// must not re-upload trail geometry, storm uniforms update without an upload, and the multiply ink darkens sand,
/// clay and dark ground without hue shifts. Same 256 x 256 4x-MSAA captures (trail-sand.png, trail-clay.png,
/// trail-dark-ground.png) and printed results.
/// </summary>
public static class TrailMaterialSmoke
{
    [GameMode("--trail-material-smoke-test")]
    public static Task Run(string dir, SceneTree tree)
    {
        bool passed = checkTrailMaterial(dir);
        tree.Quit(passed ? 0 : 1);
        return Task.CompletedTask;
    }

    public static bool checkTrailMaterial(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var trail = new DirtTrail(style: DirtTrail.Style.tracks);
            var state = new Simulation(); var input = new DriveInput(); input.throttle = 1;
            var contacts = new[] { (x: 0.0, z: 0.0, width: 0.2) };
            for (int k = 0; k < 45; k++) { trail.update(state, contacts: contacts); state.advance(input, dt: 1.0 / 60); }
            // Small real motion below mark spacing must not replace already
            // published geometry. Previously every moving update uploaded it.
            trail.update(state, contacts: contacts);
            int beforeCount = trail.count, beforeUploads = trail.geometryUploads;
            var identities = trail.root.childNodes.Select(n => n.geometry).Where(g => g != null).ToList();
            state.advance(input, dt: 0.000001);
            trail.update(state, contacts: contacts);
            bool unchanged = trail.count == beforeCount && trail.geometryUploads == beforeUploads
                && identities.SequenceEqual(trail.root.childNodes.Select(n => n.geometry).Where(g => g != null), ReferenceEqualityComparer.Instance);
            if (!unchanged) { GD.Print("Sub-spacing trail upload regression"); return false; }
            state.storm.enabled = true; state.storm.advance(10);
            trail.update(state, contacts: contacts);
            var ink = trail.root.childNodes.FirstOrDefault()?.geometry?.firstMaterial;
            if (!(ink != null && ink.value("stormTime") is IConvertible stormTime && Convert.ToDouble(stormTime) == 10 && trail.geometryUploads == beforeUploads))
            { GD.Print("Storm uniforms stopped without geometry upload"); return false; }
            state.storm.enabled = false; trail.update(state, contacts: contacts);
            var first = trail.root.childNodes.FirstOrDefault();
            if (first?.geometry is not SCNGeometry geometry) return false;
            var source = geometry.sourcesFor(SCNGeometrySourceSemantic.vertex)[0];
            var points = new List<SCNVector3>();
            var bytes = source.data;
            for (int i = 0; i < 4; i++)
            {
                int offset = source.dataOffset + i * source.dataStride;
                points.Add(new SCNVector3(BitConverter.ToSingle(bytes, offset), BitConverter.ToSingle(bytes, offset + 4), BitConverter.ToSingle(bytes, offset + 8)));
            }
            var center = new SCNVector3(points.Sum(p => p.x) / 4, points.Sum(p => p.y) / 4, points.Sum(p => p.z) / 4);
            SCNScene scene = new SCNScene(); SCNNode camera = new SCNNode(); camera.camera = new SCNCamera(); camera.camera.usesOrthographicProjection = true; camera.camera.orthographicScale = 0.14;
            camera.position = new SCNVector3(center.x, center.y + 2, center.z); camera.look(center, up: new SCNVector3(0, 0, -1), localFront: new SCNVector3(0, 0, -1)); scene.rootNode.addChildNode(camera);
            var ground = new SCNNode(new SCNPlane(width: 2, height: 2)); ground.eulerAngles.x = -Math.PI / 2; ground.position = new SCNVector3(center.x, center.y - 0.007, center.z);
            var mat = new SCNMaterial(); mat.lightingModel = SCNMaterial.LightingModel.constant; ground.geometry.materials = new() { mat }; scene.rootNode.addChildNode(ground); scene.rootNode.addChildNode(trail.root);
            var renderer = new SCNRenderer(device: null, options: null); renderer.scene = scene; renderer.pointOfView = camera;
            bool passed = true;
            foreach (var (name, inkColor) in new[] { ("sand", 0xd8b47cu), ("clay", 0x986441u), ("dark-ground", 0x575b60u) })
            {
                mat.diffuse.contents = color(inkColor);
                NSBitmapImageRep shot(bool marks)
                {
                    trail.root.isHidden = !marks;
                    var image = renderer.snapshot(atTime: 0, with: new CGSize(256, 256), antialiasingMode: SCNAntialiasingMode.multisampling4X);
                    return NSBitmapImageRep.data(image.tiffRepresentation);
                }
                NSBitmapImageRep @base = shot(false), marked = shot(true);
                int changed = 0, bad = 0;
                for (int y = 0; y < 256; y++)
                    for (int x = 0; x < 256; x++)
                    {
                        var a = @base.colorAt(x, y).usingColorSpace(NSColorSpace.deviceRGB); var b = marked.colorAt(x, y).usingColorSpace(NSColorSpace.deviceRGB);
                        var ratios = new[] { b.redComponent / a.redComponent, b.greenComponent / a.greenComponent, b.blueComponent / a.blueComponent };
                        if (ratios.Min() < 0.98)
                        {
                            changed += 1;
                            if (ratios.Min() < 0.65 || ratios.Max() - ratios.Min() > 0.04) { bad += 1; }
                        }
                    }
                passed = passed && changed > 100 && bad == 0;
                GD.Print($"Trail {name}: {changed} impression pixels, {bad} excessive contrast/hue errors");
                File.WriteAllBytes(Path.Combine(directory, $"trail-{name}.png"), marked.representation(NSBitmapImageFileType.png));
            }
            GD.Print($"Trail material: {(passed ? "PASS" : "FAIL")}"); return passed;
        }
        catch (Exception error) { GD.Print(error.ToString()); return false; }
    }
}
