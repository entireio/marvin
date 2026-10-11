using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Marvin.Core;

namespace Marvin;

/// <summary>--sand-lifetime-test DIR: exercise actual patch uploads, cache eviction and race reset with the base
/// material still alive. Weak references measure resource reachability, rather than the field's bounded tile count.</summary>
public static class SandLifetimeSmoke
{
    [GameMode("--sand-lifetime-test")]
    public static async Task Run(string dir, SceneTree tree)
    {
        Directory.CreateDirectory(dir);
        var scene = new SCNScene();
        var material = new SCNMaterial { lightingModel = SCNMaterial.LightingModel.constant };
        material.diffuse.contents = NSColor.srgbRed(0.72, 0.55, 0.32, 1);
        // The live world keeps this material on its static terrain as well as in DeformableSand.
        var baseNode = new SCNNode(new SCNPlane(4, 4)); baseNode.geometry.materials = new() { material };
        scene.rootNode.addChildNode(baseNode);
        var sand = new DeformableSand(); sand.configure(material, scene.rootNode);
        var camera = new SCNNode { camera = new SCNCamera() };
        camera.camera.usesOrthographicProjection = true; camera.camera.orthographicScale = 4;
        scene.rootNode.addChildNode(camera);
        var renderer = new SCNRenderer(null, null) { scene = scene, pointOfView = camera };
        var contacts = new[] { new SandDeformation.Contact(0.30, 0, 0.196, 0.44), new SandDeformation.Contact(-0.30, 0, 0.196, 0.44) };
        var geometries = new List<WeakReference>(); var textures = new List<WeakReference>();
        var seen = new ConditionalWeakTable<SCNGeometry, object>();
        var shaders = new HashSet<ulong>();
        for (int i = 0; i < 96; i++)
        {
            var p = DirtCourse.projection((i % 2 == 0 ? 1 : -1) * (220 + i % 40 * 10), 80 + i / 40 * 12);
            var visitor = new Simulation(dirtTrack: true, dirtStartOffset: p.offset, dirtStartPhase: p.phase, character: RacePerformance.Character.wallE);
            sand.update(new[] { visitor }, new[] { contacts }, 1.0 / 30);
            camera.position = new SCNVector3(visitor.x, visitor.groundY + 7, visitor.z + 5);
            camera.look(new SCNVector3(visitor.x, visitor.groundY, visitor.z), new SCNVector3(0, 1, 0), new SCNVector3(0, 0, -1));
            // Snapshot traverses the real renderer, creating geometry-owned argument variants and uploading textures.
            using (var image = renderer.SnapshotImage(new Vector2I(160, 120), SCNAntialiasingMode.none))
                if (i == 95 && !SceneKitRuntime.Headless) image.SavePng(Path.Combine(dir, "sand-active.png"));
            Observe(sand, seen, geometries, textures, shaders);
            await AppController.frame(tree);
        }
        await Collect(tree);
        int patchesBeforeReset = sand.patchCount;
        int geometriesAfterEviction = Alive(geometries), texturesAfterEviction = Alive(textures);
        sand.reset();
        await Collect(tree);
        int geometriesAfterReset = Alive(geometries), texturesAfterReset = Alive(textures);
        bool passed = geometries.Count > SandDeformation.capacity * 2 && patchesBeforeReset == SandDeformation.capacity
            && geometriesAfterEviction == patchesBeforeReset && texturesAfterEviction == patchesBeforeReset
            && geometriesAfterReset == 0 && texturesAfterReset == 0 && shaders.Count == 1
            && baseNode.geometry.firstMaterial == material;
        var report = new
        {
            passed, geometriesCreated = geometries.Count, patchesBeforeReset, geometriesAfterEviction, texturesAfterEviction,
            geometriesAfterReset, texturesAfterReset, uniquePatchShaders = shaders.Count,
            headless = SceneKitRuntime.Headless,
        };
        File.WriteAllText(Path.Combine(dir, "sand-lifetime.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        GD.Print($"Sand lifetime: {(passed ? "PASS" : "FAIL")}, {geometries.Count} uploaded geometries; " +
            $"alive after eviction {geometriesAfterEviction}/{texturesAfterEviction}, after reset {geometriesAfterReset}/{texturesAfterReset}, shaders {shaders.Count}");
        // Retain the same world and shared material through both collection checks.
        GC.KeepAlive(material); GC.KeepAlive(scene); GC.KeepAlive(sand); GC.KeepAlive(renderer);
        SceneKit.Foundation.exit(passed ? 0 : 1);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Observe(DeformableSand sand, ConditionalWeakTable<SCNGeometry, object> seen,
        List<WeakReference> geometries, List<WeakReference> textures, HashSet<ulong> shaders)
    {
        foreach (var node in sand.root.childNodes)
        {
            var geometry = node.geometry;
            if (geometry == null || seen.TryGetValue(geometry, out _)) continue;
            seen.Add(geometry, new object());
            geometries.Add(new WeakReference(geometry));
            textures.Add(new WeakReference(((SCNMaterialProperty)geometry.value("duneHeights")).contents));
            foreach (var variant in geometry.argumentVariants) shaders.Add(variant.Shader.GetInstanceId());
        }
    }

    private static int Alive(List<WeakReference> references) => references.Count(reference => reference.IsAlive);
    private static async Task Collect(SceneTree tree)
    {
        // Removal queues Godot nodes for deletion at frame end; drop native mesh/material references before collecting.
        for (int i = 0; i < 3; i++) await AppController.frame(tree);
        // Property-to-scene argument bindings use ConditionalWeakTable; its dependent handles and Godot resources
        // finalize after the geometry drops out, so finish their subsequent collection cycles as well.
        for (int i = 0; i < 3; i++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            await AppController.frame(tree);
        }
    }
}
