// Port of Sources/MarvinSimulator/ImportedRacer.swift.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

/// Attributed mesh assets with movement driven by simulation travel, never a
/// free-running walk animation. The original WALL-E track loop is retained.
public sealed class ImportedRacer
{
    public enum Kind { bb8, wallE }
    /// Swift `Kind.rawValue` (the resource folder name).
    public static string rawValue(Kind kind) => kind == Kind.bb8 ? "BB8" : "WallE";
    public sealed class Mesh
    {
        public sealed class Surface
        {
            public List<double> color { get; set; }
            public string @base { get; set; }
            public string normal { get; set; }
            public string emission { get; set; }
            public string metalRough { get; set; }
            public double metal { get; set; }
            public double rough { get; set; }
        }
        public sealed class Part
        {
            public string name { get; set; }
            public string role { get; set; }
            public int side { get; set; }
            public int material { get; set; }
            public List<List<float>> positions { get; set; }
            public List<List<float>> normals { get; set; }
            public List<List<float>> texcoords { get; set; }
            public List<uint> indices { get; set; }
            public List<double> pivot { get; set; }
            public List<List<float>> frames { get; set; }
        }
        public List<Part> parts { get; set; }
        public List<Surface> materials { get; set; }
        public double height { get; set; }
        public double ballRadius { get; set; }
        public double beltLength { get; set; }
        public List<double> ballCenter { get; set; }
        public List<double> headCenter { get; set; }
        public List<List<double>> contacts { get; set; }
        public List<List<double>> armPivots { get; set; }
    }
    public readonly Kind kind; public readonly SCNNode root = new SCNNode(), ball = new SCNNode(), head = new SCNNode();
    public readonly DirtCoating dirtCoating = new DirtCoating();
    public readonly SCNNode headMount = new SCNNode();
    public List<SCNNode> arms { get; private set; } = new();
    public readonly double height, ballRadius, beltLength;
    public readonly (double x, double z, double width)[] contacts;
    public int triangleCount { get; private set; } = 0;
    public List<(SCNNode node, int side, Float4x4[] frames)> links { get; private set; } = new();
    private readonly List<(SCNNode node, int side, double radius)> gears = new();
    private (Double2 rollingTravel, double elapsed)? previous;

    /// Swift `init(kind:resources:) throws`; `resources` is the asset root (res://assets).
    public ImportedRacer(Kind kind, string resources)
    {
        this.kind = kind;
        var folder = resources + "/" + rawValue(kind) + "/Generated";
        var mesh = JsonSerializer.Deserialize<Mesh>(Godot.FileAccess.GetFileAsString(folder + "/mesh.json"));
        if (mesh?.parts == null || mesh.materials == null) throw new InvalidDataException($"fileReadCorruptFile: {folder}/mesh.json");
        height = mesh.height; ballRadius = mesh.ballRadius; beltLength = mesh.beltLength;
        contacts = mesh.contacts.Select(c => (c[0], c[1], c[2])).ToArray();
        root.name = kind == Kind.bb8 ? "BB-8 · Willy Decarpentrie · CC BY 4.0" : "WALL-E · Janis Zeps · CC BY 4.0";
        NSImage texture(string name)
        {
            if (name == null) return null;
            var image = NSImage.contentsOf(folder + "/" + name);
            if (image == null) throw new InvalidDataException($"fileReadCorruptFile: {folder}/{name}");
            return image;
        }
        var materials = mesh.materials.Select(source =>
        {
            var m = new SCNMaterial(); m.lightingModel = SCNMaterial.LightingModel.physicallyBased; m.isDoubleSided = true;
            m.diffuse.contents = (object)texture(source.@base) ?? NSColor.srgbRed(source.color[0], source.color[1], source.color[2], source.color[3]);
            m.normal.contents = texture(source.normal);
            m.emission.contents = texture(source.emission);
            m.metalness.contents = source.metal; m.roughness.contents = source.rough;
            var packed = texture(source.metalRough);
            if (packed != null)
            {
                m.metalness.contents = packed; m.metalness.textureComponents = SCNColorMask.blue;
                m.roughness.contents = packed; m.roughness.textureComponents = SCNColorMask.green;
                m.ambientOcclusion.contents = packed; m.ambientOcclusion.textureComponents = SCNColorMask.red;
            }
            return m;
        }).ToList();
        ball.position = new SCNVector3(mesh.ballCenter[0], mesh.ballCenter[1], mesh.ballCenter[2]);
        head.position = new SCNVector3(mesh.headCenter[0], mesh.headCenter[1], mesh.headCenter[2]);
        root.addChildNode(ball);
        if (kind == Kind.bb8)
        {
            headMount.position = ball.position;
            head.position = new SCNVector3(head.position.x - ball.position.x, head.position.y - ball.position.y, head.position.z - ball.position.z);
            root.addChildNode(headMount); headMount.addChildNode(head);
        }
        else { root.addChildNode(head); }
        foreach (var pivot in mesh.armPivots)
        {
            var arm = new SCNNode(); arm.position = new SCNVector3(pivot[0], pivot[1], pivot[2]);
            root.addChildNode(arm); arms.Add(arm);
        }
        foreach (var part in mesh.parts)
        {
            if (!(part.positions.Count == part.normals.Count && part.positions.Count == part.texcoords.Count
                  && part.indices.All(i => (int)i < part.positions.Count) && part.material >= 0 && part.material < materials.Count))
            {
                throw new InvalidDataException($"fileReadCorruptFile: {folder} part {part.name}");
            }
            var geometry = new SCNGeometry(new[] { SCNGeometrySource.vertices(part.positions.Select(p => new SCNVector3(p[0], p[1], p[2])).ToList()),
                SCNGeometrySource.normals(part.normals.Select(n => new SCNVector3(n[0], n[1], n[2])).ToList()),
                SCNGeometrySource.textureCoordinates(part.texcoords.Select(t => new CGPoint((CGFloat)t[0], (CGFloat)t[1])).ToList()) },
                new[] { new SCNGeometryElement(part.indices, SCNGeometryPrimitiveType.triangles) });
            geometry.materials = new() { materials[part.material] };
            var node = new SCNNode(geometry); node.name = part.role + ":" + part.name; node.castsShadow = true;
            if (part.role == "ball") { ball.addChildNode(node); }
            else if (part.role == "head") { head.addChildNode(node); }
            else if (part.role == "arm") { arms[part.side > 0 ? 0 : 1].addChildNode(node); }
            else
            {
                node.position = new SCNVector3(part.pivot[0], part.pivot[1], part.pivot[2]); root.addChildNode(node);
            }
            if (part.role == "link")
            {
                var frames = part.frames.Select(values =>
                    new Float4x4(new Float4(values[0], values[1], values[2], values[3]), new Float4(values[4], values[5], values[6], values[7]),
                                 new Float4(values[8], values[9], values[10], values[11]), new Float4(values[12], values[13], values[14], values[15]))
                ).ToArray();
                links.Add((node, part.side, frames));
            }
            else if (part.role == "gear")
            {
                var b = geometry.boundingBox;
                gears.Add((node, part.side, (double)max(b.max.y - b.min.y, b.max.z - b.min.z) / 2));
            }
            triangleCount += part.indices.Count / 3;
        }
        dirtCoating.install(root, height: height, wheelOffset: kind == Kind.wallE ? abs(contacts[0].x) : 0, rolling: kind == Kind.bb8);
    }

    // simd bridging between MarvinCore's simd types (Swift's simd_quatf, SIMD3<Float>) and the facade's.
    private static QuatF quat(SCNQuatF q) { var (x, y, z, w) = SimdBridge.Get(q); return new QuatF((float)x, (float)y, (float)z, (float)w); }
    private static SCNQuatF simd(QuatF q) => SimdBridge.Q(q.vector.x, q.vector.y, q.vector.z, q.vector.w);
    private static Float3 float3(SCNFloat3 v) { var (x, y, z) = SimdBridge.Get(v); return new Float3((float)x, (float)y, (float)z); }
    private static SCNFloat3 simd(Float3 v) => SimdBridge.F3(v.x, v.y, v.z);

    public void update(Simulation state)
    {
        dirtCoating.update(state);
        var reset = previous == null || state.elapsed < previous.Value.elapsed || state.elapsed == 0;
        root.position = new SCNVector3(state.x, state.groundY, state.z);
        if (kind == Kind.bb8)
        {
            // Rolling axes and head heading are world-space. Discard the
            // selection screen's display rotation before applying either.
            root.eulerAngles = SCNVector3Zero;
            if (reset) { ball.simdOrientation = simd(new QuatF(angle: 0, axis: new Float3(1, 0, 0))); }
            else if (previous is { } prior)
            {
                var travel = state.rollingTravel - prior.rollingTravel;
                double dx = travel.x, dz = travel.y, distance = hypot(dx, dz);
                if (distance > 1e-9 && distance < 2)
                {
                    var turn = new QuatF(angle: (float)(distance / ballRadius), axis: new Float3((float)(dz / distance), 0, (float)(-dx / distance)));
                    ball.simdOrientation = simd(Simd.normalize(turn * quat(ball.simdOrientation)));
                }
            }
            // Head steers independently and remains above the rolling sphere.
            headMount.eulerAngles = new SCNVector3(0, state.heading, 0);
            head.eulerAngles = SCNVector3Zero;
        }
        else
        {
            if (max(abs(state.x), abs(state.z)) > DesertTerrain.townEdge)
            {
                var q = state.duneOrientation.vector;
                root.simdOrientation = SimdBridge.Q((float)q.x, (float)q.y, (float)q.z, (float)q.w);
            }
            else { root.eulerAngles = new SCNVector3(state.bodyPitch, state.heading, state.bodyRoll); }
            foreach (var link in links)
            {
                var travel = link.side > 0 ? state.leftTravel : state.rightTravel;
                var phase = (travel / beltLength) % 1;
                if (phase < 0) { phase += 1; }
                float t = (float)phase * (float)(link.frames.Length - 1); int i = (int)t; float f = t - (float)i;
                Float4x4 a = link.frames[i], b = link.frames[Math.Min(i + 1, link.frames.Length - 1)];
                link.node.simdPosition = simd(new Float3(a.column3.x, a.column3.y, a.column3.z) * (1 - f) + new Float3(b.column3.x, b.column3.y, b.column3.z) * f);
                link.node.simdOrientation = simd(Simd.slerp(new QuatF(a), new QuatF(b), f));
            }
            foreach (var gear in gears) { gear.node.eulerAngles.x = (CGFloat)((gear.side > 0 ? state.leftTravel : state.rightTravel) / max(gear.radius, 0.01)); }
            head.eulerAngles = SCNVector3Zero;
            applyArms(new RacePerformance.Pose());
        }
        previous = (state.rollingTravel, state.elapsed);
    }
    public void applyExpression(RacePerformance.Pose pose, double heading)
    {
        if (kind == Kind.bb8)
        {
            // Orbit the head around the shell center, keeping the contact point
            // on the sphere while the shell rolls independently beneath it.
            headMount.eulerAngles = new SCNVector3(-pose.pitch, heading, pose.roll);
            head.eulerAngles.y = (CGFloat)pose.yaw;
        }
        else
        {
            head.eulerAngles = new SCNVector3(-pose.pitch, pose.yaw, pose.roll);
            applyArms(pose);
        }
    }
    private void applyArms(RacePerformance.Pose pose)
    {
        if (arms.Count != 2) { return; }
        // Lower the source model's permanently raised hand into a driving pose.
        // Positive gesture values lift the appropriate arm from its shoulder.
        arms[0].eulerAngles = new SCNVector3(-0.15 - pose.leftArm, 0, pose.leftArm * 0.25);
        arms[1].eulerAngles = new SCNVector3(1.20 - pose.rightArm, 0, -pose.rightArm * 0.25);
    }
    /// Native smoke checks the actual scene nodes, including signed motion.
    public bool checkMotion()
    {
        Simulation state = new Simulation(seed: 0, dirtTrack: true); DriveInput input = new DriveInput();
        update(state);
        var bounds = root.boundingBox;
        if (!(abs((double)(bounds.max.y - bounds.min.y) - height) < 0.002)) { return false; }
        bool same(QuatF a, QuatF b) => abs(Simd.dot(a.vector, b.vector)) > 0.999999f;
        if (kind == Kind.bb8)
        {
            var initial = quat(ball.simdOrientation);
            input.throttle = 1; state.advance(input, dt: 0.1); update(state);
            var forward = quat(ball.simdOrientation);
            if (!(!same(initial, forward) && abs(head.eulerAngles.x) < 1e-7 && abs(head.eulerAngles.z) < 1e-7)) { return false; }
            var expectedAxis = new Float3((float)cos(state.heading), 0, (float)(-sin(state.heading)));
            if (!(Simd.dot(forward.axis, expectedAxis) > 0.999f)) { return false; }
            input.brake = true; state.advance(input, dt: 0.1); update(state);
            if (!same(forward, quat(ball.simdOrientation))) { return false; }
            state.reset(); update(state);
            if (!same(initial, quat(ball.simdOrientation))) { return false; }
            input.brake = false; input.throttle = -1; state.advance(input, dt: 0.1); update(state);
            if (!(Simd.dot(quat(ball.simdOrientation).axis, expectedAxis) < -0.999f)) { return false; }
        }
        else
        {
            if (!(links.Count == 58 && beltLength > 1)) { return false; }
            var bottoms = new[] { 1, -1 }.Select(side => links.Where(l => l.side == side).MinBy(l => l.node.position.y)).ToArray();
            var initial = bottoms.Select(l => float3(l.node.simdPosition)).ToArray();
            // Pivoting drives the two belts in opposite directions.
            input.turn = 1; state.advance(input, dt: 0.1); update(state);
            var turned = bottoms.Select(l => float3(l.node.simdPosition)).ToArray();
            if (!((turned[0].z - initial[0].z) * (turned[1].z - initial[1].z) < 0)) { return false; }
            input.brake = true; state.advance(input, dt: 0.1); update(state);
            if (!bottoms.Zip(turned).All(p => Simd.distance(float3(p.First.node.simdPosition), p.Second) < 1e-7f)) { return false; }
            state.reset(); update(state);
            if (!bottoms.Zip(initial).All(p => Simd.distance(float3(p.First.node.simdPosition), p.Second) < 1e-7f)) { return false; }
            input.turn = 0; input.brake = false; input.throttle = 1; state.advance(input, dt: 0.1); update(state);
            if (!bottoms.Zip(initial).All(p => float3(p.First.node.simdPosition).z < p.Second.z)) { return false; }
            state.reset(); update(state);
            input.throttle = -1; state.advance(input, dt: 0.1); update(state);
            if (!bottoms.Zip(initial).All(p => float3(p.First.node.simdPosition).z > p.Second.z)) { return false; }
        }
        state.reset(); update(state);
        return true;
    }
}
