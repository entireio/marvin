// Port of Sources/MarvinSimulator/R2D2.swift.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

/// LordDiego's detailed CC BY model in its deployed three-leg driving pose.
public sealed class R2D2
{
    public const double heightMeters = 1.08;
    // Exported body height 0.85 plus the 0.035 wheel clearance.
    public const double sceneHeight = 0.885;
    // One broad roller inside each artistic foot shell; see WHEEL_REFERENCE.md.
    public readonly struct Tire
    {
        public readonly double radius, width;
        public Tire(double radius, double width) { this.radius = radius; this.width = width; }
    }
    public static readonly Tire outerTire = new Tire(radius: 0.055, width: 0.120);
    public static readonly Tire centerTire = new Tire(radius: 0.050, width: 0.090);
    public static readonly (double x, double z, double width)[] groundContacts =
    {
        (x: 0.213, z: -0.1415, width: outerTire.width),
        (x: -0.213, z: -0.1415, width: outerTire.width),
        (x: 0.0, z: 0.1445, width: centerTire.width),
    };
    private sealed class Mesh
    {
        public sealed class Part
        {
            public string name { get; set; }
            public bool head { get; set; }
            public List<List<float>> positions { get; set; }
            public List<List<float>> normals { get; set; }
            public List<List<float>> texcoords { get; set; }
            public List<uint> indices { get; set; }
            public int material { get; set; }
        }
        public List<Part> parts { get; set; }
        public List<float> headPivot { get; set; }
        public List<float> headAxis { get; set; }
    }
    public readonly struct Wheel
    {
        public readonly SCNNode node; public readonly int side; public readonly double radius;
        public Wheel(SCNNode node, int side, double radius) { this.node = node; this.side = side; this.radius = radius; }
    }
    public readonly DirtCoating dirtCoating = new DirtCoating();
    public readonly SCNNode root = new SCNNode(), head = new SCNNode();
    public List<Wheel> wheels { get; private set; } = new();
    public int triangleCount { get; private set; } = 0;
    public bool hasCenterLeg { get; private set; } = false;
    private SCNVector3 headAxis = new SCNVector3(0, 1, 0);

    /// Swift `init(resources: URL) throws`; `resources` is the asset root (res://assets).
    public R2D2(string resources)
    {
        var directory = resources + "/R2D2";
        var mesh = JsonSerializer.Deserialize<Mesh>(Godot.FileAccess.GetFileAsString(directory + "/mesh.json"));
        if (mesh?.parts == null || mesh.headPivot == null || mesh.headAxis == null
            || !(mesh.headPivot.Count == 3 && mesh.headAxis.Count == 3)) { throw new InvalidDataException("fileReadCorruptFile: R2D2/mesh.json"); }
        root.name = "R2-D2 · LordDiego · CC BY 4.0";
        var pivot = new SCNVector3(mesh.headPivot[0], mesh.headPivot[1], mesh.headPivot[2]);
        headAxis = new SCNVector3(mesh.headAxis[0], mesh.headAxis[1], mesh.headAxis[2]);
        head.position = pivot; root.addChildNode(head);
        NSImage texture(string name)
        {
            var image = NSImage.contentsOf(directory + $"/Textures/{name}.png");
            if (image == null) { throw new InvalidDataException($"fileReadCorruptFile: R2D2/Textures/{name}.png"); }
            return image;
        }
        var shell = new SCNMaterial();
        shell.lightingModel = SCNMaterial.LightingModel.physicallyBased;
        shell.diffuse.contents = texture("R2D2_Base_Color");
        shell.metalness.contents = texture("R2D2_Metalness");
        shell.roughness.contents = texture("R2D2_Roughness");
        shell.emission.contents = texture("R2D2_Emission");
        var panels = new SCNMaterial();
        panels.lightingModel = SCNMaterial.LightingModel.physicallyBased;
        panels.diffuse.contents = texture("R2D2_Barrel_Base");
        panels.roughness.contents = texture("R2D2_Barrel_Roughness");
        panels.normal.contents = texture("R2D2_Barrel_Normal");
        panels.metalness.contents = 0.15;
        var surfaces = new[] { shell, panels };
        foreach (var part in mesh.parts)
        {
            if (!(part.positions != null && part.normals != null && part.texcoords != null && part.indices != null
                  && part.positions.Count > 0 && part.positions.Count == part.normals.Count
                  && part.positions.Count == part.texcoords.Count
                  && part.positions.All(p => p.Count == 3) && part.normals.All(n => n.Count == 3)
                  && part.texcoords.All(t => t.Count == 2) && part.material >= 0 && part.material < surfaces.Length
                  && part.indices.Count % 3 == 0
                  && part.indices.All(i => (int)i < part.positions.Count)))
            {
                throw new InvalidDataException($"fileReadCorruptFile: R2D2 part {part.name}");
            }
            var vertices = SCNGeometrySource.vertices(part.positions.Select(p =>
                part.head ? new SCNVector3((CGFloat)p[0] - pivot.x, (CGFloat)p[1] - pivot.y, (CGFloat)p[2] - pivot.z)
                    : new SCNVector3(p[0], p[1], p[2])).ToList());
            var normals = SCNGeometrySource.normals(part.normals.Select(n => new SCNVector3(n[0], n[1], n[2])).ToList());
            var uv = SCNGeometrySource.textureCoordinates(part.texcoords.Select(t => new CGPoint((CGFloat)t[0], (CGFloat)t[1])).ToList());
            var elements = new SCNGeometryElement(part.indices, SCNGeometryPrimitiveType.triangles);
            var geometry = new SCNGeometry(new[] { vertices, normals, uv }, new[] { elements });
            geometry.materials = new() { surfaces[part.material] };
            var node = new SCNNode(geometry); node.name = part.name;
            (part.head ? head : root).addChildNode(node);
            triangleCount += part.indices.Count / 3;
            if (part.name == "R2D2_Leg_Center") { hasCenterLeg = true; }
        }
        // PORT: Godot-only, the same look: the three wheels share their rubber and hub materials (Swift creates equal ones
        // per wheel), so Godot draws equal tires and spokes as one instanced draw (docs/performance.md, "Draw calls").
        var rubber = material(0x555653, roughness: 0.94);
        var hub = material(0x8b969e, metal: 0.75, roughness: 0.38);
        foreach (var contact in groundContacts)
        {
            var side = contact.x > 0 ? 1 : contact.x < 0 ? -1 : 0;
            addWheel(x: contact.x, z: contact.z, tire: side == 0 ? centerTire : outerTire, side: side, rubber: rubber, hub: hub);
        }
        dirtCoating.install(root, height: sceneHeight, wheelOffset: 0.213);
    }

    private void addWheel(double x, double z, Tire tire, int side, SCNMaterial rubber, SCNMaterial hub)
    {
        double radius = tire.radius, width = tire.width;
        var axle = new SCNNode(); axle.name = "R2-D2 rolling tire";
        axle.position = new SCNVector3(x, radius, z);
        SCNNode cylinder(double radius, double width, SCNMaterial surface)
        {
            var shape = new SCNCylinder(radius, width); shape.radialSegmentCount = 48;
            shape.materials = new() { surface };
            var node = new SCNNode(shape); node.eulerAngles.z = Math.PI / 2;
            return node;
        }
        axle.addChildNode(cylinder(radius: radius, width: width, surface: rubber));
        axle.addChildNode(cylinder(radius: radius * 0.52, width: width - 0.004, surface: hub));
        // Smooth rubber tread with recessed side hubs.
        foreach (var face in new[] { -1.0, 1.0 })
        {
            for (int i = 0; i < 5; i++)
            {
                var angle = (double)i * 2 * Math.PI / 5;
                var spoke = new SCNBox(0.003, radius * 0.54, radius * 0.125, 0.001);
                spoke.materials = new() { hub };
                var node = new SCNNode(spoke);
                node.position = new SCNVector3(face * (width / 2 - 0.001), cos(angle) * radius * 0.375, sin(angle) * radius * 0.375);
                node.eulerAngles.x = angle; axle.addChildNode(node);
            }
        }
        root.addChildNode(axle); wheels.Add(new Wheel(node: axle, side: side, radius: radius));
    }

    public void update(Simulation state)
    {
        dirtCoating.update(state);
        root.position = new SCNVector3(state.x, state.groundY, state.z);
        if (max(abs(state.x), abs(state.z)) > DesertTerrain.townEdge)
        {
            var q = state.duneOrientation.vector;
            root.simdOrientation = SimdBridge.Q((float)q.x, (float)q.y, (float)q.z, (float)q.w);
        }
        else { root.eulerAngles = new SCNVector3(state.bodyPitch, state.heading, state.bodyRoll); }
        foreach (var wheel in wheels)
        {
            var travel = wheel.side > 0 ? state.leftTravel : wheel.side < 0 ? state.rightTravel : (state.leftTravel + state.rightTravel) / 2;
            // Fixed axles keep the broad rollers inside their foot housings.
            wheel.node.eulerAngles.x = (CGFloat)((travel / wheel.radius) % (2 * Math.PI));
        }
        head.rotation = new SCNVector4(headAxis.x, headAxis.y, headAxis.z, 0.14 * sin(state.elapsed * 0.7));
    }
    public void applyExpression(RacePerformance.Pose pose)
    {
        head.rotation = new SCNVector4(headAxis.x, headAxis.y, headAxis.z, pose.yaw);
    }
}
