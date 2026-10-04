using System;
using Godot;

namespace Marvin.SceneKit;

/// <summary>
/// Facade probe for SceneKit's fixed directional shadow box (automaticallyAdjustsShadowProjection = false).
/// Mirrors the SceneKit probe used to measure it (a 13 x 13 grid of posts and a 30 m post on a floor, a tilted
/// directional light at (0, 20, 0) with orthographicScale 5, map 1024, radius 1, zNear 0.1, seen from above):
/// shadows exist only inside the light's box; receivers beyond zFar are unshadowed.
///   tools/godot -- --shadow-box-probe DIR      writes DIR/box-zfar40.png, box-zfar12.png, box-tilt09.png
/// </summary>
public static class ShadowBoxProbe
{
    [Marvin.GameMode("--shadow-box-probe")]
    public static void Run(string dir, SceneTree tree)
    {
        Render(dir, "box-zfar40", 40, 20, 0.3);
        Render(dir, "box-zfar12", 12, 20, 0.3);
        Render(dir, "box-tilt09", 40, 20, 0.9);
        tree.Quit();
    }

    private static void Render(string dir, string name, double zFar, double lightY, double tilt)
    {
        var scene = new SCNScene();
        var floorMat = new SCNMaterial { lightingModel = SCNMaterial.LightingModel.lambert }; floorMat.diffuse.contents = NSColor.whiteAlpha(0.8, 1);
        var floorBox = new SCNBox(40, 0.1, 40, 0); floorBox.materials = new() { floorMat };
        scene.rootNode.addChildNode(new SCNNode(floorBox) { position = new SCNVector3(0, -0.05, 0) });
        var postMat = new SCNMaterial { lightingModel = SCNMaterial.LightingModel.lambert }; postMat.diffuse.contents = NSColor.red;
        for (int i = -6; i <= 6; i++)
            for (int j = -6; j <= 6; j++)
            {
                var post = new SCNBox(0.3, 2, 0.3, 0); post.materials = new() { postMat };
                scene.rootNode.addChildNode(new SCNNode(post) { position = new SCNVector3(i * 2.0, 1, j * 2.0) });
            }
        var tall = new SCNBox(0.5, 30, 0.5, 0); tall.materials = new() { postMat };
        scene.rootNode.addChildNode(new SCNNode(tall) { position = new SCNVector3(1, 15, 1) });
        var sun = new SCNNode { light = new SCNLight { type = SCNLight.LightType.directional } };
        sun.light.castsShadow = true; sun.light.shadowMode = SCNShadowMode.forward;
        sun.light.shadowMapSize = new CGSize(1024, 1024);
        sun.light.automaticallyAdjustsShadowProjection = false;
        sun.light.sampleDistributedShadowMaps = false; sun.light.forcesBackFaceCasters = false;
        sun.light.zNear = 0.1; sun.light.zFar = zFar; sun.light.maximumShadowDistance = 500;
        sun.light.orthographicScale = 5; sun.light.shadowRadius = 1; sun.light.shadowSampleCount = 8;
        sun.light.shadowColor = NSColor.black; sun.light.shadowBias = 0.6;
        sun.position = new SCNVector3(0, lightY, 0); sun.eulerAngles = new SCNVector3(-Math.PI / 2 + tilt, 0.4, 0);
        scene.rootNode.addChildNode(sun);
        scene.rootNode.addChildNode(new SCNNode { light = new SCNLight { type = SCNLight.LightType.ambient, intensity = 300 } });
        var camera = new SCNNode { camera = new SCNCamera { usesOrthographicProjection = true, orthographicScale = 14, zNear = 1, zFar = 200 } };
        camera.position = new SCNVector3(0, 100, 0); camera.eulerAngles = new SCNVector3(-Math.PI / 2, 0, 0);
        scene.rootNode.addChildNode(camera);
        var renderer = new SCNRenderer(null, null) { scene = scene, pointOfView = camera };
        renderer.SnapshotImage(new Vector2I(800, 800), SCNAntialiasingMode.none);
        renderer.SnapshotImage(new Vector2I(800, 800), SCNAntialiasingMode.none).SavePng(System.IO.Path.Combine(dir, name + ".png"));
    }
}
