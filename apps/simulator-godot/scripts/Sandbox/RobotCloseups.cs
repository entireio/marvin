// --robot-closeups DIR: each robot alone at the sandbox dock, from the macOS smoke test's robot-relative
// close-up cameras and a few more (face, rear, running gear, top), clean and after the smoke test's
// 1800-frame dusty drive. Validation mode without a macOS flag: its SceneKit twin is
// tools/scenekit-reference/robots/RobotCloseups.swift, which compiles the macOS game's own Robot, R2D2,
// ImportedRacer, TrackBelt, DirtCoating, World and FloorGroove sources and renders the same views with
// SceneKit (the macOS smoke test takes its close-ups on the dirt track, which a sandbox port cannot match).
// Keep the two files in step.
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

public static class RobotCloseups
{
    // (file, robot, side, front, height, look height as a fraction of the robot's height (negative: metres))
    private static readonly (string, RacePerformance.Character, double, double, double, double)[] views =
    {
        ("r2d2-front", RacePerformance.Character.r2d2, 0.85, 1.5, 0.7, -0.43),
        ("r2d2-wheels", RacePerformance.Character.r2d2, 0.65, 0.85, 0.16, -0.15),
        ("bb8-front", RacePerformance.Character.bb8, 0.8, 1.5, 0.75, 0.5),
        ("walle-front", RacePerformance.Character.wallE, 0.8, 1.5, 0.75, 0.5),
        ("marvin-front", RacePerformance.Character.marvin, 0.95, 1.4, 0.8, -0.28),
        ("marvin-face", RacePerformance.Character.marvin, 0.18, 0.75, 0.42, 0.72),
        ("marvin-rear", RacePerformance.Character.marvin, -0.8, -1.2, 0.75, 0.5),
        ("marvin-gear", RacePerformance.Character.marvin, 0.75, 0.45, 0.14, 0.2),
        ("marvin-top", RacePerformance.Character.marvin, 0.35, 0.55, 1.5, 0.3),
        ("r2d2-face", RacePerformance.Character.r2d2, 0.2, 0.9, 0.85, 0.8),
        ("r2d2-rear", RacePerformance.Character.r2d2, -0.85, -1.4, 0.8, 0.5),
        ("r2d2-top", RacePerformance.Character.r2d2, 0.35, 0.6, 1.8, 0.6),
        ("bb8-face", RacePerformance.Character.bb8, 0.15, 0.85, 0.55, 0.75),
        ("bb8-rear", RacePerformance.Character.bb8, -0.8, -1.3, 0.7, 0.5),
        ("bb8-top", RacePerformance.Character.bb8, 0.3, 0.5, 1.5, 0.5),
        ("walle-face", RacePerformance.Character.wallE, 0.15, 1.0, 0.75, 0.8),
        ("walle-rear", RacePerformance.Character.wallE, -0.85, -1.4, 0.8, 0.5),
        ("walle-gear", RacePerformance.Character.wallE, 0.85, 0.6, 0.16, 0.15),
        ("walle-top", RacePerformance.Character.wallE, 0.35, 0.6, 1.8, 0.5),
    };
    private static readonly (string, RacePerformance.Character, double)[] dirtyViews =
    {
        ("marvin-dirty", RacePerformance.Character.marvin, 0.28), ("r2d2-dirty", RacePerformance.Character.r2d2, 0.44),
        ("bb8-dirty", RacePerformance.Character.bb8, 0.27), ("walle-dirty", RacePerformance.Character.wallE, 0.42),
    };

    [GameMode("--robot-closeups")]
    public static async Task Run(string directory, SceneTree tree)
    {
        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        const string resources = "res://assets";
        var world = new World();
        var robot = new Robot(resources);
        var r2d2 = new R2D2(resources);
        var bb8 = new ImportedRacer(ImportedRacer.Kind.bb8, resources);
        var wallE = new ImportedRacer(ImportedRacer.Kind.wallE, resources);
        // AppController.startSandbox(): LDR, no bloom, no SSAO, zFar 80.
        world.camera.camera.zFar = 80;
        world.camera.camera.wantsHDR = false; world.camera.camera.bloomIntensity = 0;
        world.camera.camera.screenSpaceAmbientOcclusionIntensity = 0;
        // CLOSEUP_LIGHT=race[,FRACTION,PHASE]: DirtWorld's light instead of the sandbox's (BinarySky: sky dome and probe, two
        // forward-shadow suns, ambient, fog) and the race camera (App.swift startDirtTrack: zFar 250, SSAO 0.70 / 1.6 / 0.025),
        // to compare the robots' shading under the race light. Keep in step with RobotCloseups.swift.
        var spec = System.Environment.GetEnvironmentVariable("CLOSEUP_LIGHT");
        BinarySky sky = null;
        if (spec != null && spec.StartsWith("race"))
        {
            var lights = new List<SCNNode>();
            world.scene.rootNode.enumerateChildNodes((n, _) => { if (n.light != null) lights.Add(n); });
            foreach (var n in lights) n.removeFromParentNode();
            sky = new BinarySky(world.scene);
            sky.attach(world.camera);
            var parts = spec.Split(',').Skip(1).Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            sky.apply(new BinaryDaylight(parts.Length > 0 ? parts[0] : 0.5, parts.Length > 1 ? parts[1] : 1.2));
            sky.updateShadowCenter(new Double3(0, 0, 0));
            world.camera.camera.zFar = 250;
            world.camera.camera.screenSpaceAmbientOcclusionIntensity = 0.70;
            world.camera.camera.screenSpaceAmbientOcclusionRadius = 1.6;
            world.camera.camera.screenSpaceAmbientOcclusionBias = 0.025;
            // CLOSEUP_EXP=noibl,noamb,nosun,nossao switches single light terms off (to isolate shading differences); nonormal below.
            var exp = System.Environment.GetEnvironmentVariable("CLOSEUP_EXP") ?? "";
            if (exp.Contains("noibl")) world.scene.lightingEnvironment.intensity = 0;
            if (exp.Contains("noamb")) world.scene.rootNode.enumerateChildNodes((n, _) => { if (n.light?.type == SCNLight.LightType.ambient) n.light.intensity = 0; });
            if (exp.Contains("nosun")) foreach (var sun in sky.suns) sun.light.intensity = 0;
            if (exp.Contains("nossao")) world.camera.camera.screenSpaceAmbientOcclusionIntensity = 0;
        }
        var renderer = new SCNRenderer(null, null);
        renderer.scene = world.scene; renderer.pointOfView = world.camera;

        SCNNode modelRoot(RacePerformance.Character character) => character switch
        {
            RacePerformance.Character.marvin => robot.root,
            RacePerformance.Character.r2d2 => r2d2.root,
            RacePerformance.Character.bb8 => bb8.root,
            _ => wallE.root,
        };
        // PlayerCharacter.swift updateModel(_:state:expression:) with the neutral expression.
        void updateModel(RacePerformance.Character character, Simulation state)
        {
            var expression = new RacePerformance.Pose();
            var pose = expression;
            pose.yaw += state.yaw; pose.pitch += state.pitch;
            switch (character)
            {
                case RacePerformance.Character.marvin: robot.update(state); robot.applyExpression(expression, state: state); break;
                case RacePerformance.Character.r2d2: r2d2.update(state); r2d2.applyExpression(pose); break;
                case RacePerformance.Character.bb8: bb8.update(state); bb8.applyExpression(pose, heading: state.heading); break;
                case RacePerformance.Character.wallE: wallE.update(state); wallE.applyExpression(pose, heading: state.heading); break;
            }
        }
        double modelHeight(RacePerformance.Character character) => character switch
        {
            RacePerformance.Character.marvin => robot.neutralHeight * robot.modelScale,
            RacePerformance.Character.r2d2 => R2D2.sceneHeight,
            RacePerformance.Character.bb8 => bb8.height,
            _ => wallE.height,
        };

        var states = new Dictionary<RacePerformance.Character, Simulation>();
        foreach (var character in RacePerformance.CharacterAllCases)
        {
            states[character] = new Simulation(seed: 0, character: character);
            updateModel(character, states[character]);
        }
        world.update(states[RacePerformance.Character.marvin]);
        // CLOSEUP_EXP=nonormal removes the robots' normal maps (to see what SSAO and shading take from them).
        if ((System.Environment.GetEnvironmentVariable("CLOSEUP_EXP") ?? "").Contains("nonormal"))
        {
            foreach (var character in RacePerformance.CharacterAllCases)
                modelRoot(character).enumerateHierarchy((n, _) => { if (n.geometry != null) foreach (var m in n.geometry.materials) m.normal.contents = null; });
        }
        void stage(RacePerformance.Character character)
        {
            foreach (var other in RacePerformance.CharacterAllCases) { modelRoot(other).removeFromParentNode(); }
            world.scene.rootNode.addChildNode(modelRoot(character));
        }
        void capture(string name, RacePerformance.Character character, double side, double front, double height, double look)
        {
            var state = states[character]; var angle = state.heading;
            stage(character);
            world.camera.position = new SCNVector3(state.x + cos(angle) * side + sin(angle) * front, state.groundY + height, state.z - sin(angle) * side + cos(angle) * front);
            world.camera.look(new SCNVector3(state.x, state.groundY + look, state.z), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
            var image = renderer.snapshot(0, new CGSize(1280, 820), SCNAntialiasingMode.multisampling4X);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory, name + ".png"), image.tiffRepresentation);
        }
        foreach (var (name, character, side, front, height, look) in views)
        {
            capture(name, character, side, front, height, look < 0 ? -look : modelHeight(character) * look);
        }
        // App.swift: accumulate a representative stretch of driving, then inspect the coating.
        var dustyDrive = new DirtOpponent();
        for (int i = 0; i < 1800; i++)
        {
            dustyDrive.advance(dt: 1.0 / 60, raceDT: 1.0 / 60);
            robot.dirtCoating.update(dustyDrive.simulation);
            r2d2.dirtCoating.update(dustyDrive.simulation);
            bb8.dirtCoating.update(dustyDrive.simulation); wallE.dirtCoating.update(dustyDrive.simulation);
        }
        foreach (var (name, character, height) in dirtyViews)
        {
            capture(name, character, 0.95, 1.4, 0.8, height);
        }
        foreach (var (name, character, side, front, height, look) in views)
        {
            if (!(name.EndsWith("-face") || name.EndsWith("-rear"))) { continue; }
            capture(name + "-dirty", character, side, front, height, look < 0 ? -look : modelHeight(character) * look);
        }
        var amounts = new[] { robot.dirtCoating.amount, r2d2.dirtCoating.amount, bb8.dirtCoating.amount, wallE.dirtCoating.amount };
        GD.Print($"Robot close-ups (Godot): {views.Length + dirtyViews.Length + 8} images, dirt [{string.Join(", ", System.Array.ConvertAll(amounts, a => Swift.description(a)))}] · {directory}");
        tree.Quit(0);
    }
}
