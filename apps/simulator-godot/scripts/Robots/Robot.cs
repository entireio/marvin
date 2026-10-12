// Port of Sources/MarvinSimulator/Robot.swift.
// Swift's module-level color(_:alpha:) and material(_:metal:roughness:) live in RobotFunctions; the
// global using below makes them callable unqualified in every file of the assembly, as in Swift.
global using static Marvin.RobotFunctions;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

/// Module-level functions of Robot.swift.
public static class RobotFunctions
{
    public static NSColor color(uint hex, double alpha = 1) =>
        NSColor.srgbRed((CGFloat)((hex >> 16) & 255) / 255,
                        (CGFloat)((hex >> 8) & 255) / 255,
                        (CGFloat)(hex & 255) / 255, alpha);
    public static SCNMaterial material(uint hex, double metal = 0, double roughness = 0.6)
    {
        var m = new SCNMaterial();
        m.lightingModel = SCNMaterial.LightingModel.physicallyBased;
        m.diffuse.contents = color(hex); m.metalness.contents = metal;
        m.roughness.contents = roughness; m.isDoubleSided = true;
        return m;
    }
}

public sealed class MeshManifest
{
    public sealed class Part
    {
        public string name { get; set; }
        public int vertexOffset { get; set; }
        public int vertexCount { get; set; }
        public int indexOffset { get; set; }
        public int triangleCount { get; set; }
    }
    public List<Part> parts { get; set; }
}

public sealed class Robot
{
    public const uint silverColor = 0xc3c7c9;
    public readonly DirtCoating dirtCoating = new DirtCoating();
    public readonly SCNNode root = new SCNNode(), yawNode = new SCNNode(), pitchNode = new SCNNode();
    public List<(SCNNode node, bool left)> wheels = new();
    public List<TrackBelt> tracks = new();
    public List<SCNNode> eyes = new();
    public int partCount = 0, triangleCount = 0;
    public double modelScale { get; private set; } = 1.0;
    public double neutralHeight { get; private set; } = 0.0;

    /// Swift `init(resources: URL) throws`; `resources` is the asset root (res://assets).
    public Robot(string resources)
    {
        var directory = resources + "/Marvin";
        var manifest = JsonSerializer.Deserialize<MeshManifest>(Godot.FileAccess.GetFileAsString(directory + "/manifest.json"))
            ?? throw new InvalidDataException("Invalid Marvin manifest");
        var data = Godot.FileAccess.GetFileAsBytes(directory + "/geometry.bin");
        if (manifest.parts == null || data.Length == 0) throw new InvalidDataException($"Missing {directory}/geometry.bin or manifest.json");
        root.name = "Marvin CAD assembly";
        // Keep the circular neck concentric with the body's socket during pan.
        yawNode.position = new SCNVector3(HeadRig.yawPivot.x, HeadRig.yawPivot.y, HeadRig.yawPivot.z);
        var pitchOffset = HeadRig.pitchPivot - HeadRig.yawPivot;
        pitchNode.position = new SCNVector3(pitchOffset.x, pitchOffset.y, pitchOffset.z);
        root.addChildNode(yawNode); yawNode.addChildNode(pitchNode);
        // Satin aluminium: broad, muted highlights instead of white plastic or chrome.
        var shell = material(silverColor, metal: 0.8, roughness: 0.65);
        var rubber = material(0x202a29, roughness: 0.88);
        var graphite = material(0x37403f, metal: 0.25, roughness: 0.42);
        var steel = material(0x919b9d, metal: 0.7, roughness: 0.28);
        var electronics = material(0x163e36, roughness: 0.6);
        var faceMaterial = material(0x25282b, roughness: 0.38);
        var redButton = material(0xc83a24, roughness: 0.45);
        foreach (var part in manifest.parts)
        {
            if (!(part.vertexCount > 0 && part.triangleCount > 0
                  && part.vertexOffset >= 0 && part.indexOffset >= 0
                  && part.vertexOffset + part.vertexCount * 24 <= data.Length
                  && part.indexOffset + part.triangleCount * 12 <= data.Length))
            {
                throw new InvalidDataException($"Invalid geometry for {part.name}");
            }
            var positions = new SCNGeometrySource(data, SCNGeometrySourceSemantic.vertex,
                part.vertexCount, true,
                3, 4,
                part.vertexOffset, 24);
            var normals = new SCNGeometrySource(data, SCNGeometrySourceSemantic.normal,
                part.vertexCount, true,
                3, 4,
                part.vertexOffset + 12, 24);
            var indexData = data[part.indexOffset..(part.indexOffset + part.triangleCount * 12)];
            var elements = new SCNGeometryElement(indexData, SCNGeometryPrimitiveType.triangles,
                part.triangleCount, 4);
            var geometry = new SCNGeometry(new[] { positions, normals }, new[] { elements });
            if (!new[] { "Head", "09_track" }.Contains(part.name))
            {
                neutralHeight = max(neutralHeight, (double)geometry.boundingBox.max.y);
            }
            if (part.name == "09_track") { geometry.materials = new() { rubber }; }
            else if (new[] { "04_wheel", "Top", "Servo_Head", "Servo_Tilt" }.Contains(part.name))
            {
                geometry.materials = new() { graphite };
            }
            else if (new[] { "Bearings", "Motor_Left", "Motor_Right", "Axis_Mount" }.Contains(part.name))
            {
                geometry.materials = new() { steel };
            }
            else if (part.name == "Display_and_electronics")
            {
                // The CAD already includes the full, flush front panel.
                geometry.materials = new() { faceMaterial };
            }
            else if (part.name == "Battery")
            {
                geometry.materials = new() { electronics };
            }
            else { geometry.materials = new() { shell }; }
            if (part.name == "Buttons")
            {
                // STEP merges the six controls into one mesh. Partition triangles
                // by their CAD lateral positions without replacing their geometry.
                var groups = new[] { new List<byte>(), new List<byte>(), new List<byte>() };
                for (int triangle = 0; triangle < part.triangleCount; triangle++)
                {
                    var offset = triangle * 12;
                    var indices = Enumerable.Range(0, 3).Select(i => BitConverter.ToUInt32(indexData, offset + i * 4)).ToArray();
                    var x = indices.Aggregate(0f, (sum, index) =>
                        sum + BitConverter.ToSingle(data, part.vertexOffset + (int)index * 24)) / 3;
                    var group = x < -0.061f ? 0 : x > 0.05f ? 2 : 1;
                    groups[group].AddRange(indexData[offset..(offset + 12)]);
                }
                var buttonElements = groups.Select(bytes =>
                {
                    var element = new SCNGeometryElement(bytes.ToArray(), SCNGeometryPrimitiveType.triangles,
                        bytes.Count / 12, 4);
                    return element;
                }).ToArray();
                var buttons = new SCNGeometry(new[] { positions, normals }, buttonElements);
                buttons.materials = new() { redButton, shell, shell };
                var buttonNode = new SCNNode(buttons);
                buttonNode.name = part.name; root.addChildNode(buttonNode);
                partCount += 1; triangleCount += part.triangleCount;
                continue;
            }
            var node = new SCNNode(geometry);
            node.name = part.name; node.castsShadow = true;
            // The source layer "Head" contains only an isolated 3 mm solid
            // below the shell, not the head assembly. Retain the imported data
            // but exclude this loose object from the visible simulator model.
            // The static CAD belt is retained but replaced visually by TrackBelt.
            node.isHidden = new[] { "Head", "09_track" }.Contains(part.name);
            if (new[] { "05_head_base", "06_head_cover", "Display_and_electronics", "Head", "Top", "Battery" }.Contains(part.name))
            {
                node.position = new SCNVector3(-HeadRig.pitchPivot.x, -HeadRig.pitchPivot.y, -HeadRig.pitchPivot.z); pitchNode.addChildNode(node);
            }
            else if (new[] { "07_neck", "08_neck_mount", "Servo_Tilt", "Axis_Mount" }.Contains(part.name))
            {
                node.position = new SCNVector3(-HeadRig.yawPivot.x, -HeadRig.yawPivot.y, -HeadRig.yawPivot.z); yawNode.addChildNode(node);
            }
            else { root.addChildNode(node); }
            partCount += 1; triangleCount += part.triangleCount;
        }
        // Only the luminous strokes sit over the CAD panel; no second bezel
        // or raised display box. Coordinates are in the assembled head frame.
        var eyePath = new NSBezierPath();
        for (int i = 0; i <= 40; i++)
        {
            var angle = Math.PI * (1 - (double)i / 40);
            var point = new NSPoint(cos(angle) * 0.061, sin(angle) * 0.046);
            if (i == 0) { eyePath.move(point); } else { eyePath.line(point); }
        }
        for (int i = 0; i <= 40; i++)
        {
            var angle = Math.PI * (double)i / 40;
            eyePath.line(new NSPoint(cos(angle) * 0.048, sin(angle) * 0.033));
        }
        eyePath.close();
        foreach (var x in new[] { -0.0545, 0.0545 })
        {
            eyePath.appendOval(new NSRect(x - 0.0065, -0.0065, 0.013, 0.013));
        }
        foreach (var x in new[] { -0.145, 0.145 })
        {
            var eye = new SCNShape(eyePath, 0);
            var glow = new SCNMaterial();
            glow.lightingModel = SCNMaterial.LightingModel.constant; glow.diffuse.contents = NSColor.white;
            glow.emission.contents = NSColor.white; glow.isDoubleSided = true;
            eye.materials = new() { glow };
            var node = new SCNNode(eye);
            node.position = new SCNVector3(x, 0.008, 0.348);
            node.castsShadow = false;
            pitchNode.addChildNode(node); eyes.Add(node);
        }
        foreach (var x in new[] { -0.262225, 0.262225 })
        {
            var belt = new TrackBelt(x, rubber);
            root.addChildNode(belt.node); tracks.Add(belt);
        }
        // Hub markers rotate with the same signed travel as their belt.
        // PORT: Godot-only, the same look: one material for the four equal spokes (Swift creates an equal one per spoke),
        // so Godot draws them as one instanced draw (docs/performance.md, "Draw calls").
        var spokeMaterial = material(0xb4c8c0, metal: 0.5);
        foreach (var x in new[] { -0.337, 0.337 })
        {
            foreach (var z in new[] { -0.247, 0.164 })
            {
                var hub = new SCNNode();
                hub.position = new SCNVector3(x, 0.112, z);
                var spoke = new SCNBox(0.008, 0.10, 0.022, 0.004);
                spoke.materials = new() { spokeMaterial };
                hub.addChildNode(new SCNNode(spoke));
                root.addChildNode(hub); wheels.Add((hub, x > 0));
            }
        }
        // Both assets keep their ground origin. Match real heights without
        // changing course dimensions or the imported head/track pivots.
        modelScale = R2D2.sceneHeight * (0.60 / R2D2.heightMeters) / neutralHeight;
        dirtCoating.install(root, height: neutralHeight, wheelOffset: 0.262225);
        root.scale = new SCNVector3(modelScale, modelScale, modelScale);
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
        yawNode.eulerAngles.y = (CGFloat)state.yaw;
        pitchNode.eulerAngles.x = (CGFloat)(-state.pitch);
        foreach (var track in tracks) { track.update(travel: (track.left ? state.leftTravel : state.rightTravel) / modelScale); }
        foreach (var wheel in wheels)
        {
            wheel.node.eulerAngles.x = (CGFloat)((wheel.left ? state.leftTravel : state.rightTravel) / (0.107 * modelScale));
        }
        var blink = state.elapsed % 4.6 > 4.43;
        foreach (var eye in eyes) { eye.scale.y = blink ? 0.12 : 1; }
    }
    public void applyExpression(RacePerformance.Pose pose, Simulation state)
    {
        var yaw = max(-80 * Math.PI / 180, min(80 * Math.PI / 180, state.yaw + pose.yaw));
        var pitch = max(-45 * Math.PI / 180, min(45 * Math.PI / 180, state.pitch + pose.pitch));
        // Retain manual head input and the CAD head/body clearance constraint.
        if (HeadClearance.isClear(yaw: yaw, pitch: pitch))
        {
            yawNode.eulerAngles.y = (CGFloat)yaw; pitchNode.eulerAngles.x = (CGFloat)(-pitch);
        }
        foreach (var eye in eyes) { eye.scale.y *= (CGFloat)(1 - pose.focus * 0.12); }
    }
}
