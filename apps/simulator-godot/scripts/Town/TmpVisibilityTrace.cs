using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

// TEMPORARY (not committed): the facade side of /private/tmp/claude-501/town-people/vis-harness.
public static class TmpVisibilityTrace
{
    [GameMode("--tmp-visibility-trace")]
    public static void Run(string dir, Godot.SceneTree tree)
    {
        var town = new TownWorld();
        var scene = new SCNScene();
        scene.rootNode.addChildNode(town.root);
        var camera = new SCNNode(); camera.camera = new SCNCamera(); camera.camera.fieldOfView = 48; camera.camera.zNear = 0.02; camera.camera.zFar = 250;
        scene.rootNode.addChildNode(camera);
        var view = new SCNView(); view.Size = new Godot.Vector2(1280, 820); tree.Root.AddChild(view);
        view.scene = scene; view.pointOfView = camera;
        camera.position = new SCNVector3(0, 38, -33); camera.look(at: SCNVector3Zero, up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
        _ = view.snapshot();
        var residents = town.residents; var street = town.streetResidents;
        var lines = new List<string>();
        var trace = new System.Text.StringBuilder();
        Func<SCNNode, bool> visibility = node => { var v = view.isNode(node, insideFrustumOf: camera); trace.Append(node.name.Split(' ').Last()).Append(v ? "+ " : "- "); return v; };
        var frames = int.Parse(Environment.GetEnvironmentVariable("MARVIN_TRACE_FRAMES") ?? "2400");
        for (var frame = 0; frame < frames; frame++)
        {
            trace.Clear();
            town.update(dt: 1.0 / 60, camera: camera.position, player: new Double2((double)(float)camera.position.x, (double)(float)camera.position.z), visible: visibility);
            lines.Add($"frame {frame} cam {description((float)camera.position.x)} {description((float)camera.position.y)} {description((float)camera.position.z)} vis {trace}");
            if (new[] { 120, 480, 900, 1260, 1800, 2160 }.Contains(frame))
            {
                var district = frame < 900 ? 0 : (frame < 1800 ? 1 : 2);
                var w = street.walkers[district * street.walkers.Count / 3]; var p = w.path[w.start];
                camera.position = new SCNVector3(p.x + 4, 2.0, p.y + 5);
                camera.look(at: new SCNVector3(p.x, 0.6, p.y), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
            }
            if (new[] { 0, 180, 360 }.Contains(frame))
            {
                var z = DirtCourse.point(0).z;
                camera.position = new SCNVector3(-4.4, 2.6, z - 0.5);
                camera.look(at: new SCNVector3(-4.4, 2.35, z - 4.4), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
            }
            if (frame % 120 == 0 && frame < 2400 && residents.walkers.FirstOrDefault(w => !w.node.isHidden) is TownResidents.Walker walker)
            {
                var p0 = walker.node.position; var p = new SCNVector3((float)p0.x, (float)p0.y, (float)p0.z);
                camera.position = new SCNVector3(p.x + 1.1, p.y + 1.1, p.z + 2.0);
                camera.look(at: new SCNVector3(p.x, p.y + 0.55, p.z), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
            }
            if (frame == 522)
            {
                var n = street.walkers[16].node; var (mn, mx) = n.boundingBox;
                lines.Add($"debug16 pos {description(n.position.x)} {description(n.position.y)} {description(n.position.z)} yaw {description(n.eulerAngles.y)} bmin {description(mn.x)} {description(mn.y)} {description(mn.z)} bmax {description(mx.x)} {description(mx.y)} {description(mx.z)} cam {camera.worldTransform}");
                var lods = n.geometry.levelsOfDetail;
                foreach (var l in lods) { var (a, b) = l.geometry.boundingBox; lines.Add($"lod {description(l.worldSpaceDistance)} {description(a.x)} {description(a.y)} {description(a.z)} {description(b.x)} {description(b.y)} {description(b.z)}"); }
            }
            if (frame % 600 == 0)
            {
                lines.Add("positions " + string.Join(" ", residents.walkers.Select(w => $"{description(w.position.x)} {description(w.position.y)}")) + " | " + string.Join(" ", street.walkers.Select(w => $"{description(w.position.x)} {description(w.position.y)}")));
            }
        }
        File.WriteAllText(Path.Combine(dir, "visibility.txt"), string.Join("\n", lines) + "\n");
        tree.Quit();
    }
}
