// Port of Sources/MarvinSimulator/World.swift (the sandbox playground).
using System;
using System.Collections.Generic;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

public sealed class World
{
    public readonly SCNScene scene = new SCNScene(); public readonly SCNNode camera = new SCNNode();
    public List<SCNNode> beacons = new();
    public List<SCNNode> beaconLabels = new();
    public readonly SCNNode floorSurface = new SCNNode(), floorDetails = new SCNNode();
    private Checkpoint[] floorCheckpoints = Array.Empty<Checkpoint>();
    public readonly SCNMaterial soil = material(0xb49470, roughness: 0.98);
    public readonly SCNMaterial teal = material(0x2b8e7f), orange = material(0xd89c64);

    public World()
    {
        scene.background.contents = color(0xdbe4df);
        scene.fogColor = color(0xdbe4df); scene.fogStartDistance = 15; scene.fogEndDistance = 38;
        camera.camera = new SCNCamera(); camera.camera.fieldOfView = 48;
        camera.camera.zNear = 0.02; camera.camera.zFar = 80;
        camera.camera.wantsHDR = false;
        scene.rootNode.addChildNode(camera);
        var ambient = new SCNNode(); ambient.light = new SCNLight();
        ambient.light.type = SCNLight.LightType.ambient; ambient.light.intensity = 550;
        ambient.light.color = color(0xe7f4ff); scene.rootNode.addChildNode(ambient);
        var sun = new SCNNode(); sun.light = new SCNLight(); sun.light.type = SCNLight.LightType.directional;
        sun.light.intensity = 1400; sun.light.color = color(0xfff1df);
        sun.eulerAngles = new SCNVector3(-0.85, -0.45, -0.25);
        sun.light.castsShadow = true; sun.light.shadowMode = SCNShadowMode.deferred;
        sun.light.shadowMapSize = new CGSize(4096, 4096);
        sun.light.shadowSampleCount = 16; sun.light.shadowColor = NSColor.black.withAlphaComponent(0.24);
        sun.light.orthographicScale = 10; sun.light.maximumShadowDistance = 22;
        scene.rootNode.addChildNode(sun);
        // Keep the plinth top below the deck's y=0 surface. Coplanar top
        // faces cause depth-buffer fighting whenever the camera or robot moves.
        box(0, -0.175, 0, 12.5, 0.3, 10.5, material(0x927455, roughness: 0.95), radius: 0.14);
        // Solid backing ends below the channels; the cut-out surface is at y=0.
        box(0, -0.05, 0, 12, 0.05, 10, soil, radius: 0.04);
        floorSurface.eulerAngles.x = -Math.PI / 2;
        scene.rootNode.addChildNode(floorSurface);
        scene.rootNode.addChildNode(floorDetails);
        var wall = material(0xa5b9ad);
        box(-6.08, 0.18, 0, 0.16, 0.36, 10.3, wall, radius: 0.04);
        box(6.08, 0.18, 0, 0.16, 0.36, 10.3, wall, radius: 0.04);
        box(0, 0.18, -5.08, 12.3, 0.36, 0.16, wall, radius: 0.04);
        box(0, 0.18, 5.08, 12.3, 0.36, 0.16, wall, radius: 0.04);
        for (int i = 0; i < Simulation.obstacles.Length; i++)
        {
            var o = Simulation.obstacles[i];
            var body = box(o.x, o.height / 2, o.z, o.width, o.height, o.depth,
                           material(i % 2 == 0 ? 0x96b4a7u : 0xd2b59au), radius: 0.07);
            body.name = $"Obstacle {i + 1}";
        }
        // Dock and course markers are painted on the floor, not collision solids.
        box(0, 0, -2.6, 1.35, 0.001, 1.25, material(0xb7cdc0), radius: 0.08).castsShadow = false;
        foreach (var x in new[] { -0.6, 0.6 }) { box(x, 0.0005, -2.6, 0.025, 0.001, 1.1, teal).castsShadow = false; }
        for (int i = 0; i < CourseLayout.count; i++)
        {
            var ring = FloorGroove.geometry();
            ring.materials = new() { material(0x70583f, roughness: 0.95) };
            var node = new SCNNode(ring); node.position = new SCNVector3(0, 0, 0);
            scene.rootNode.addChildNode(node); beacons.Add(node);
            var text = new SCNText((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), 0);
            text.font = NSFont.monospacedSystemFont(1, NSFont.Weight.semibold);
            text.flatness = 0.2; text.materials = new() { material(0x4e756a) };
            var label = new SCNNode(text);
            var bounds = text.boundingBox;
            var height = bounds.max.y - bounds.min.y;
            var scale = (CGFloat)0.36 / height;
            label.scale = new SCNVector3(scale, scale, scale);
            label.pivot = SCNMatrix4MakeTranslation((bounds.min.x + bounds.max.x) / 2, (bounds.min.y + bounds.max.y) / 2, 0);
            label.eulerAngles.x = -Math.PI / 2;
            label.position = new SCNVector3(0, 0.0008, 0);
            label.castsShadow = false;
            scene.rootNode.addChildNode(label); beaconLabels.Add(label);
        }
    }
    /// Swift `@discardableResult func box(_:_:_:_:_:_:_:radius:)`.
    public SCNNode box(double x, double y, double z, double w, double h, double d,
                       SCNMaterial material, double radius = 0)
    {
        var geometry = new SCNBox(w, h, d, radius);
        geometry.materials = new() { material };
        var node = new SCNNode(geometry); node.position = new SCNVector3(x, y, z);
        scene.rootNode.addChildNode(node); return node;
    }
    private void rebuildFloor(Checkpoint[] checkpoints)
    {
        floorCheckpoints = checkpoints.ToArray();
        for (int i = 0; i < checkpoints.Length; i++)
        {
            var point = checkpoints[i];
            var start = i == 0 ? new Checkpoint(x: CourseLayout.launch.x, z: CourseLayout.launch.z) : checkpoints[i - 1];
            beaconLabels[i].eulerAngles = new SCNVector3(-Math.PI / 2, CourseRoute.labelYaw(start, point), 0);
        }
        floorDetails.childNodes.ForEach(n => n.removeFromParentNode());
        var path = new NSBezierPath(new NSRect(-6, -5, 12, 10));
        path.windingRule = NSBezierPath.WindingRule.evenOdd;
        foreach (var point in checkpoints)
        {
            var radius = FloorGroove.outerRadius;
            for (int i = 0; i < 128; i++)
            {
                var angle = (double)i * 2 * Math.PI / 128;
                var p = new NSPoint(point.x + radius * cos(angle), -point.z - radius * sin(angle));
                if (i == 0) { path.move(p); } else { path.line(p); }
            }
            path.close();
            // The untouched floor inside each annular channel.
            var disk = new SCNCylinder((CGFloat)FloorGroove.innerRadius, 0.025);
            disk.radialSegmentCount = 128; disk.materials = new() { soil };
            var center = new SCNNode(disk);
            center.position = new SCNVector3(point.x, -0.0125, point.z);
            floorDetails.addChildNode(center);
        }
        var surface = new SCNShape(path, 0);
        surface.materials = new() { soil };
        floorSurface.geometry = surface;

        var line = material(0xa48766, roughness: 0.98);
        void stroke(double @fixed, bool alongZ, (double, double)[] spans)
        {
            var segments = spans.ToList();
            foreach (var point in checkpoints)
            {
                var distance = @fixed - (alongZ ? point.x : point.z);
                var radius = FloorGroove.outerRadius + 0.006;
                if (!(abs(distance) < radius)) { continue; }
                var half = sqrt(radius * radius - distance * distance);
                var center = alongZ ? point.z : point.x;
                double lo = center - half, hi = center + half;
                segments = segments.SelectMany(s =>
                {
                    var (a, b) = s;
                    if (hi <= a || lo >= b) { return new[] { (a, b) }; }
                    return new[] { (a, min(b, lo)), (max(a, hi), b) }.Where(t => t.Item2 > t.Item1).ToArray();
                }).ToList();
            }
            foreach (var (a, b) in segments)
            {
                var node = box(alongZ ? @fixed : (a + b) / 2, 0.003, alongZ ? (a + b) / 2 : @fixed,
                               alongZ ? 0.009 : b - a, 0.005, alongZ ? b - a : 0.009, line);
                node.castsShadow = false; floorDetails.addChildNode(node);
            }
        }
        for (int x = -5; x <= 5; x++)
        {
            stroke(@fixed: (double)x, alongZ: true, spans: x == 0 ? new[] { (-4.95, -3.225), (-1.975, 4.95) } : new[] { (-4.95, 4.95) });
        }
        for (int z = -4; z <= 4; z++)
        {
            stroke(@fixed: (double)z, alongZ: false, spans: new[] { -3, -2 }.Contains(z) ? new[] { (-5.95, -0.675), (0.675, 5.95) } : new[] { (-5.95, 5.95) });
        }
    }
    public void update(Simulation state)
    {
        if (!state.checkpoints.SequenceEqual(floorCheckpoints)) { rebuildFloor(state.checkpoints); }
        for (int i = 0; i < beacons.Count; i++)
        {
            var node = beacons[i];
            var point = state.checkpoints[i];
            node.position = new SCNVector3(point.x, 0, point.z);
            beaconLabels[i].position = new SCNVector3(point.x, 0.0008, point.z);
            node.geometry.firstMaterial.diffuse.contents = color(i < state.checkpoint ? 0x2b8e7fu : i == state.checkpoint ? 0xc38b41u : 0x70583fu);
            // Pulse the light, not the cut-out dimensions of a physical groove.
            var glow = (CGFloat)(0.18 + 0.12 * (1 + sin(state.elapsed * 2)) / 2);
            node.geometry.firstMaterial.emission.contents = i == state.checkpoint
                ? NSColor.srgbRed(glow, glow * 0.55, glow * 0.12, 1) : NSColor.black;
        }
    }
}
