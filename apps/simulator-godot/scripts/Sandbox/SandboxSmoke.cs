// Native smoke captures of the sandbox and the four robots, mirroring the macOS smoke modes:
//   --smoke-test DIR             (App.swift smokeTest(): the sandbox and robot captures and checks)
//   --character-smoke-test DIR   (PlayableCharacterSmoke.swift + PoweredRollingSmoke.swift, sandbox half)
//   --bb8-motion-smoke-test DIR  (BB8MotionSmoke.swift, sandbox half)
// Flags, camera setups, states and output file names are the macOS ones, so captures compare 1:1 with
// reference/mac/smoke, reference/mac/character and the macOS --bb8-motion-smoke-test output.
//
// PORT: AppController (App.swift) belongs to the App port. SandboxSmoke carries the AppController state
// and the sandbox branches of startSandbox(), reset(), tick(), updateCamera() and SimulatorView's held-key
// drive input that these modes run through, with App.swift's names. Race-track parts of the macOS modes
// need DirtWorld, DirtRacePhysics, RaceHUD and the main menu (other streams) and are not run here:
// the dirt-track captures (dirt-*.png, race-*.png, {key}-race.png), the menu captures and the App-level
// icon checks. The sandbox input, pause, brake, focus, head, reset and camera checks run on this AppController stand-in. The robot close-ups that macOS takes on the dirt track (r2d2-front.png,
// r2d2-wheels.png, bb8-front.png, walle-front.png, *-dirty.png) are taken with the same robot-relative
// camera, robot and coating state, with the robot at the sandbox dock (see each capture below); compare them
// with the output of tools/scenekit-reference/robots/RobotCloseups.swift (--robot-closeups, RobotCloseups.cs),
// which renders the same views with SceneKit from the macOS sources.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Godot;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

public sealed class SandboxSmoke
{
    // ---- AppController members used by the sandbox path (App.swift names)
    private readonly SCNView view;
    private readonly World world = new World();
    private readonly Robot robot;
    private readonly R2D2 r2d2;
    private readonly ImportedRacer bb8, wallE;
    /// MainMenuView.character (the robot chosen on the menu).
    private RacePerformance.Character menuCharacter = RacePerformance.Character.marvin;
    private RacePerformance.Character playerCharacter = RacePerformance.Character.marvin;
    private Simulation simulation = new Simulation();
    private bool inSandbox = false, isDirtTrack = false;
    private int cameraMode = 1; private double orbitYaw = 0.65, orbitPitch = 0.5, cameraDistance = 3.5;
    private OverviewMotion overviewMotion = new OverviewMotion();
    private SCNVector3? freeCameraEye;
    private SCNVector3 cameraAim = SCNVector3Zero;
    private int smokeFrames = 0;
    private readonly string directory;
    private readonly SceneTree tree;
    /// SimulatorView.held (key codes of held movement keys).
    private readonly HashSet<ushort> held = new();

    private SandboxSmoke(string directory, SceneTree tree)
    {
        this.directory = directory; this.tree = tree;
        const string resources = "res://assets";
        robot = new Robot(resources);
        r2d2 = new R2D2(resources);
        bb8 = new ImportedRacer(ImportedRacer.Kind.bb8, resources);
        wallE = new ImportedRacer(ImportedRacer.Kind.wallE, resources);
        world.scene.rootNode.addChildNode(robot.root);
        // The macOS window's content view: 1280 x 820, 4x MSAA.
        view = new SCNView(new CGRect(0, 0, 1280, 820));
        tree.Root.AddChild(view);
        view.scene = world.scene; view.pointOfView = world.camera;
        view.antialiasingMode = SCNAntialiasingMode.multisampling4X;
        robot.update(simulation);
    }

    // ================================================================ modes
    [GameMode("--smoke-test")]
    public static async Task RunSmokeTest(string directory, SceneTree tree)
    {
        var app = new SandboxSmoke(directory, tree);
        await app.frame();
        // macOS: 20 menu frames, then the menu test activates the sandbox (mainMenu.activate()).
        app.startSandbox();
        bool? passed = null;
        while (passed == null)
        {
            await app.frame();
            passed = app.tick();
        }
        tree.Quit(passed == true ? 0 : 1);
    }

    [GameMode("--character-smoke-test")]
    public static async Task RunCharacterSmokeTest(string directory, SceneTree tree)
    {
        var app = new SandboxSmoke(directory, tree);
        await app.frame();
        var passed = app.checkPlayableCharacters();
        GD.Print($"Playable character smoke test: {(passed ? "PASS" : "FAIL")} · {directory}");
        tree.Quit(passed ? 0 : 1);
    }

    [GameMode("--bb8-motion-smoke-test")]
    public static async Task RunBB8MotionSmokeTest(string directory, SceneTree tree)
    {
        var app = new SandboxSmoke(directory, tree);
        await app.frame();
        var passed = app.checkBB8RenderedMotion();
        GD.Print($"BB-8 rendered motion: {(passed ? "PASS" : "FAIL")} · {directory}");
        tree.Quit(passed ? 0 : 1);
    }

    private async Task frame() => await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);

    // ================================================================ AppController (sandbox path)
    private SCNNode modelRoot(RacePerformance.Character character) => character switch
    {
        RacePerformance.Character.marvin => robot.root,
        RacePerformance.Character.r2d2 => r2d2.root,
        RacePerformance.Character.bb8 => bb8.root,
        _ => wallE.root,
    };
    /// PlayerCharacter.swift `updateModel(_:state:expression:)` (AppController.updateModel in PlayerCharacter.cs).
    private void updateModel(RacePerformance.Character character, Simulation state) => updateModel(character, state, new RacePerformance.Pose());
    private void updateModel(RacePerformance.Character character, Simulation state, RacePerformance.Pose expression)
    {
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
    private void updatePlayerModel() { updateModel(playerCharacter, simulation); }
    /// configurePlayer(): the sandbox only needs the player slot (the race HUD names belong to the race).
    private void configurePlayer() { playerCharacter = menuCharacter; }

    private void startSandbox()
    {
        configurePlayer();
        world.camera.camera.zFar = 80;
        world.camera.camera.wantsHDR = false; world.camera.camera.bloomIntensity = 0;
        world.camera.camera.screenSpaceAmbientOcclusionIntensity = 0;
        isDirtTrack = false; simulation = new Simulation(character: playerCharacter);
        view.antialiasingMode = SCNAntialiasingMode.multisampling4X;
        view.scene = world.scene; world.scene.rootNode.addChildNode(world.camera);
        view.pointOfView = world.camera;
        inSandbox = true;
        foreach (var character in RacePerformance.CharacterAllCases) { modelRoot(character).removeFromParentNode(); }
        world.scene.rootNode.addChildNode(modelRoot(playerCharacter));
        reset(); updatePlayerModel(); world.update(simulation);
    }

    private void reset()
    {
        if (!inSandbox) { return; }
        cameraMode = 1; orbitYaw = 0.65; orbitPitch = 0.5; cameraDistance = 3.5;
        simulation.reset(); updatePlayerModel();
        clearInput();
        updateCamera(snap: true);
    }

    // SimulatorView: held keys, drive input and focus loss.
    private static readonly ushort[] movement = { 0, 1, 2, 13, 123, 124, 125, 126, 12, 14, 15, 3, 49 };
    private void key(ushort code, bool down)
    {
        if (down) { if (movement.Contains(code)) { held.Add(code); } }
        else { held.Remove(code); }
    }
    /// SimulatorView.flagsChanged(with:): shift is held as key code 56 (boost).
    private void flagsChanged(bool shift) { if (shift) { held.Add(56); } else { held.Remove(56); } }
    /// AppController.togglePause(_:), sandbox path.
    private void togglePause()
    {
        if (!inSandbox) { return; }
        simulation.paused = !simulation.paused; clearInput();
    }
    private void clearInput() { held.Clear(); if (!isDirtTrack) { simulation.stop(); } }
    private DriveInput driveInput
    {
        get
        {
            double down(params ushort[] codes) => codes.Any(held.Contains) ? 1 : 0;
            var input = new DriveInput();
            input.throttle = down(13, 126) - down(1, 125);
            input.turn = down(2, 124) - down(0, 123);
            input.headYaw = down(14) - down(12);
            input.headPitch = down(15) - down(3);
            input.boost = held.Contains(56); input.brake = held.Contains(49);
            return input;
        }
    }

    /// updateCamera(snap:dt:), sandbox branches (isDirtTrack is false).
    private void updateCamera(bool snap, double dt = 1.0 / 60)
    {
        var lookAhead = isDirtTrack && cameraMode == 0 ? 1.5 : 0.0;
        var target = new SCNVector3(simulation.x + sin(simulation.heading) * lookAhead,
            simulation.groundY + 0.35, simulation.z + cos(simulation.heading) * lookAhead);
        SCNVector3 desired;
        if (cameraMode == 2) { desired = new SCNVector3(0, 11.7, -10); }
        else
        {
            var angle = cameraMode == 0 ? simulation.heading + Math.PI + (isDirtTrack ? 0 : 0.45) : orbitYaw;
            var elevation = cameraMode == 0 ? (isDirtTrack ? 0.30 : 0.48) : orbitPitch;
            desired = new SCNVector3(simulation.x + sin(angle) * cos(elevation) * cameraDistance,
                simulation.groundY + 0.4 + sin(elevation) * cameraDistance,
                simulation.z + cos(angle) * cos(elevation) * cameraDistance);
        }
        var aim = cameraMode == 2 ? SCNVector3Zero : target;
        if (cameraMode == 2)
        {
            Double3 eye = new Double3(desired.x, desired.y, desired.z), focus = new Double3(aim.x, aim.y, aim.z);
            if (snap) { overviewMotion.reset(eye: eye, aim: focus); }
            else { overviewMotion.advance(eye: eye, aim: focus, dt: dt); }
            Double3 p = overviewMotion.eye, q = overviewMotion.aim;
            world.camera.position = new SCNVector3(p.x, p.y, p.z); cameraAim = new SCNVector3(q.x, q.y, q.z);
        }
        else
        {
            var current = freeCameraEye ?? desired; CGFloat mix = snap ? 1 : (CGFloat)(1 - exp(-7.67 * max(0, dt)));
            var eye = new SCNVector3(current.x + (desired.x - current.x) * mix, current.y + (desired.y - current.y) * mix, current.z + (desired.z - current.z) * mix);
            freeCameraEye = eye;
            world.camera.position = eye;
            cameraAim = target;
        }
        world.camera.look(cameraAim, up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
        if (cameraMode == 2) { freeCameraEye = null; } // (cameraBoomFraction = 1: race camera boom only)
        if (cameraMode != 2)
        {
            SCNVector3 p = world.camera.position, q = cameraAim;
            overviewMotion.reset(eye: new Double3(p.x, p.y, p.z), aim: new Double3(q.x, q.y, q.z));
        }
    }

    /// tick(), sandbox branch with the smoke test's fixed clock. Returns the smoke result once finished.
    private bool? tick()
    {
        var step = 1.0 / 60;
        var advancing = !simulation.paused;
        if (advancing) { simulation.advance(driveInput, dt: step); }
        updatePlayerModel(); world.update(simulation);
        updateCamera(snap: false, dt: step);
        return smokeTest();
    }

    private void snapshot(string name)
    {
        var png = view.snapshot().tiffRepresentation;
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory, name), png);
    }

    // ================================================================ App.swift smokeTest() (sandbox and robots)
    private bool? smokeTest()
    {
        smokeFrames += 1;
        if (smokeFrames >= 30 && smokeFrames < 90) { key(13, down: true); }
        if (smokeFrames >= 90 && smokeFrames < 120) { key(13, down: false); key(2, down: true); }
        if (smokeFrames == 120) { clearInput(); }
        if (smokeFrames != 150) { return null; }
        System.IO.Directory.CreateDirectory(directory);
        snapshot("native-scene.png");
        var boostInputPassed = true;
        foreach (var (forward, turn) in new (ushort, ushort)[] { (13, 0), (13, 2), (126, 123), (126, 124) })
        {
            clearInput();
            flagsChanged(shift: true);
            key(forward, down: true); key(turn, down: true);
            var input = driveInput;
            var sample = new Simulation(dirtTrack: true);
            var startHeading = sample.heading;
            sample.advance(input, dt: 0.1); sample.advance(input, dt: 0.1);
            var angle = atan2(sin(sample.heading - startHeading), cos(sample.heading - startHeading));
            boostInputPassed = boostInputPassed && input.boost && input.throttle == 1
                && abs(input.turn) == 1 && angle * input.turn < -0.04;
            key(turn, down: false);
            boostInputPassed = boostInputPassed && driveInput.turn == 0 && driveInput.boost;
        }
        clearInput();
        var traveled = simulation.distance; var heading = simulation.heading;
        togglePause();
        var elapsed = simulation.elapsed;
        key(13, down: true);
        simulation.advance(driveInput, dt: 0.1);
        var pausePassed = simulation.paused && simulation.elapsed == elapsed;
        togglePause();
        key(13, down: true); key(49, down: true);
        simulation.advance(driveInput, dt: 0.1);
        var brakePassed = simulation.speed == 0;
        clearInput();
        var focusPassed = held.Count == 0 && simulation.speed == 0;
        key(14, down: true); key(15, down: true);
        simulation.advance(driveInput, dt: 0.1);
        var headPassed = simulation.yaw < 0 && simulation.pitch > 0;
        reset();
        var resetPassed = simulation.z == -2.6 && simulation.distance == 0 && simulation.yaw == 0;
        var cameraPassed = true;
        for (int mode = 0; mode <= 2; mode++)
        {
            cameraMode = mode;
            foreach (var angle in new[] { -2.8, -1.0, 0.0, 1.0, 2.8 })
            {
                orbitYaw = angle; orbitPitch = 0.7;
                updateCamera(snap: true);
                // A level camera has a horizontal right axis and an
                // upward-facing up axis, independent of orbit azimuth.
                var transform = float4x4(world.camera.simdWorldTransform);
                cameraPassed = cameraPassed && abs(transform.column0.y) < 0.00001f
                    && transform.column1.y > 0;
            }
        }
        reset();
        // (The light/dark app icon checks belong to the App port.)
        var tracksPassed = robot.tracks.Count == 2;
        foreach (var (throttle, turn) in new[] { (1.0, 0.0), (-1.0, 0.0), (0.0, 1.0), (0.0, -1.0) })
        {
            var sample = new Simulation(); var input = new DriveInput();
            robot.update(sample);
            var initial = robot.tracks.Select(t => t.shoes[7].position.z).ToArray();
            input.throttle = throttle; input.turn = turn;
            sample.advance(input, dt: 0.1); robot.update(sample);
            for (int i = 0; i < robot.tracks.Count; i++)
            {
                var track = robot.tracks[i];
                var expected = throttle != 0 ? throttle : (track.left ? turn : -turn);
                tracksPassed = tracksPassed && (double)(track.shoes[7].position.z - initial[i]) * expected < 0
                    && (track.left == (track.node.position.x > 0));
            }
        }
        robot.update(new Simulation());
        var modelHeightRatio = robot.neutralHeight * (double)robot.root.scale.y / R2D2.sceneHeight;
        var modelScalePassed = abs(modelHeightRatio - 0.60 / 1.08) < 0.000001
            && robot.root.scale.x == robot.root.scale.y && robot.root.scale.y == robot.root.scale.z;
        var groundContactPassed = robot.root.position.y == 0
            && abs(TrackLoop.sample(TrackLoop.straight / 2).y - 0.0055) < 0.000001;
        var neckPassed = true;
        var neck = robot.root.childNode("07_neck", recursively: true);
        foreach (var degrees in new[] { -80.0, 0.0, 80.0 })
        {
            robot.yawNode.eulerAngles.y = (CGFloat)(degrees * Math.PI / 180);
            var center = neck.convertPosition(new SCNVector3(0, 0.295, -0.01886), to: robot.root);
            neckPassed = neckPassed && abs(center.x) < 0.000001
                && abs(center.y - 0.295) < 0.000001 && abs(center.z + 0.01886) < 0.000001;
            world.camera.position = new SCNVector3(0.85, 0.8, -1.1);
            world.camera.look(new SCNVector3(0, 0.37, -2.6), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
            snapshot($"neck-pan-{(int)degrees}.png");
        }
        robot.update(new Simulation()); updateCamera(snap: true);
        var previousCourse = simulation.checkpoints;
        reset(); world.update(simulation);
        var coursePassed = !previousCourse.SequenceEqual(simulation.checkpoints)
            && world.beacons.Count == 5 && world.beaconLabels.Count == 5
            && simulation.checkpoints.Select((point, i) =>
            {
                SCNVector3 ring = world.beacons[i].position, label = world.beaconLabels[i].position;
                return abs((double)ring.x - point.x) < 0.00001 && abs((double)ring.z - point.z) < 0.00001
                    && abs((double)label.x - point.x) < 0.00001 && abs((double)label.z - point.z) < 0.00001
                    && CourseLayout.isClear(point, simulation.checkpoints.Take(i).ToArray());
            }).All(x => x);
        var groovesPassed = world.floorSurface.geometry != null && world.beacons.All(node =>
        {
            var geometry = node.geometry;
            if (geometry == null) { return false; }
            var bounds = geometry.boundingBox;
            return node.position.y == 0 && abs((double)bounds.min.y + FloorGroove.depth) < 0.000001
                && bounds.max.y == 0 && node.scale.x == 1 && node.scale.z == 1;
        });
        var labelsPassed = simulation.checkpoints.Select((point, i) =>
        {
            var label = world.beaconLabels[i]; var bounds = world.beaconLabels[i].geometry.boundingBox;
            var center = new Float4((float)((bounds.min.x + bounds.max.x) / 2), (float)((bounds.min.y + bounds.max.y) / 2), 0, 1);
            var positioned = float4x4(label.simdTransform) * Simd.inverse(float4x4(label.simdPivot)) * center;
            var bottom = float4x4(label.simdTransform) * new Float4(0, -1, 0, 0);
            var start = i == 0 ? new Checkpoint(x: 0, z: -2.6) : simulation.checkpoints[i - 1];
            var angle = CourseRoute.labelYaw(start, point);
            var length = hypot((double)bottom.x, (double)bottom.z);
            return abs((double)positioned.x - point.x) < 0.00001 && abs((double)positioned.z - point.z) < 0.00001
                && abs((double)bottom.x / length - sin(angle)) < 0.00001
                && abs((double)bottom.z / length - cos(angle)) < 0.00001
                && abs((double)((bounds.max.y - bounds.min.y) * label.scale.y) - 0.36) < 0.00001;
        }).All(x => x);
        // Verify wheel motion from signed travel, including brake,
        // reverse, and reset. These checks exercise the rendered nodes.
        var wheelState = new Simulation(seed: 0, dirtTrack: true); var wheelInput = new DriveInput();
        wheelInput.throttle = 1; wheelState.advance(wheelInput, dt: 0.1); r2d2.update(wheelState);
        var forwardWheels = r2d2.wheels.Select(w => w.node.eulerAngles.x).ToArray();
        wheelInput.brake = true; wheelState.advance(wheelInput, dt: 0.1); r2d2.update(wheelState);
        var wheelsBrake = r2d2.wheels.Zip(forwardWheels).All(p => abs(p.First.node.eulerAngles.x - p.Second) < 0.000001);
        wheelState.reset(); wheelInput.brake = false; wheelInput.throttle = -1;
        wheelState.advance(wheelInput, dt: 0.1); r2d2.update(wheelState);
        var wheelsReverse = r2d2.wheels.All(w => w.node.eulerAngles.x < 0);
        wheelState.reset(); r2d2.update(wheelState);
        var wheelsReset = r2d2.wheels.All(w => w.node.eulerAngles.x == 0 && abs((double)w.node.position.y - w.radius) < 0.000001);
        var wheelDimensionsPassed = abs(R2D2.centerTire.width / R2D2.outerTire.width - 0.75) < 1e-9
            && r2d2.wheels.All(w =>
            {
                var expected = w.side == 0 ? R2D2.centerTire : R2D2.outerTire;
                if (w.node.childNodes.FirstOrDefault()?.geometry is not SCNCylinder tire) { return false; }
                return abs((double)tire.radius - expected.radius) < 1e-7 && abs((double)tire.height - expected.width) < 1e-7;
            });
        var wheelsPassed = wheelDimensionsPassed && forwardWheels.All(x => x > 0) && wheelsBrake && wheelsReverse && wheelsReset;
        bool bb8MotionPassed = bb8.checkMotion(), wallEMotionPassed = wallE.checkMotion();
        var newModelsPassed = bb8MotionPassed && wallEMotionPassed
            && abs(bb8.height / R2D2.sceneHeight - 0.67 / 1.08) < 1e-7
            && abs(wallE.height / R2D2.sceneHeight - 1.016 / 1.08) < 1e-7;
        var r2d2Passed = r2d2.triangleCount == 25158 && r2d2.hasCenterLeg && r2d2.wheels.Count == 3;
        // Robot close-ups. macOS takes these on the dirt track with each robot at its race grid state;
        // here each robot stands alone at the sandbox dock (heading 0, neutral expression) and the camera
        // uses the same robot-relative placement, so the robot is seen from the same side and distance.
        var states = new Dictionary<RacePerformance.Character, Simulation>();
        foreach (var character in RacePerformance.CharacterAllCases)
        {
            states[character] = new Simulation(seed: 0, character: character);
            updateModel(character, states[character]);
        }
        void stage(RacePerformance.Character character)
        {
            foreach (var other in RacePerformance.CharacterAllCases) { modelRoot(other).removeFromParentNode(); }
            world.scene.rootNode.addChildNode(modelRoot(character));
        }
        var r2 = states[RacePerformance.Character.r2d2]; var r2Angle = r2.heading;
        stage(RacePerformance.Character.r2d2);
        foreach (var (name, side, height, front, lookHeight) in new[] {
            ("r2d2-front.png", 0.85, 0.7, 1.5, 0.43),
            ("r2d2-wheels.png", 0.65, 0.16, 0.85, 0.15) })
        {
            world.camera.position = new SCNVector3(r2.x + cos(r2Angle) * side + sin(r2Angle) * front,
                r2.groundY + height, r2.z - sin(r2Angle) * side + cos(r2Angle) * front);
            world.camera.look(new SCNVector3(r2.x, r2.groundY + lookHeight, r2.z),
                up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
            snapshot(name);
        }
        foreach (var (name, character, model) in new[] { ("bb8-front.png", RacePerformance.Character.bb8, bb8), ("walle-front.png", RacePerformance.Character.wallE, wallE) })
        {
            var state = states[character]; var angle = state.heading;
            stage(character);
            world.camera.position = new SCNVector3(state.x + cos(angle) * 0.8 + sin(angle) * 1.5, state.groundY + 0.75, state.z - sin(angle) * 0.8 + cos(angle) * 1.5);
            world.camera.look(new SCNVector3(state.x, state.groundY + model.height * 0.5, state.z), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
            snapshot(name);
        }
        // Accumulate a representative stretch of driving, then inspect
        // the surface treatment on all four independently posed models.
        var dustyDrive = new DirtOpponent();
        for (int i = 0; i < 1800; i++)
        {
            dustyDrive.advance(dt: 1.0 / 60, raceDT: 1.0 / 60);
            robot.dirtCoating.update(dustyDrive.simulation);
            r2d2.dirtCoating.update(dustyDrive.simulation);
            bb8.dirtCoating.update(dustyDrive.simulation); wallE.dirtCoating.update(dustyDrive.simulation);
        }
        var dirtAmounts = new[] { robot.dirtCoating.amount, r2d2.dirtCoating.amount, bb8.dirtCoating.amount, wallE.dirtCoating.amount };
        var coatingPassed = dirtAmounts.All(a => a > 0.5);
        foreach (var (name, character, height) in new[] { ("marvin-dirty.png", RacePerformance.Character.marvin, 0.28), ("r2d2-dirty.png", RacePerformance.Character.r2d2, 0.44),
                                                          ("bb8-dirty.png", RacePerformance.Character.bb8, 0.27), ("walle-dirty.png", RacePerformance.Character.wallE, 0.42) })
        {
            var state = states[character]; var angle = state.heading;
            stage(character);
            world.camera.position = new SCNVector3(state.x + cos(angle) * 0.95 + sin(angle) * 1.4, state.groundY + 0.8, state.z - sin(angle) * 0.95 + cos(angle) * 1.4);
            world.camera.look(new SCNVector3(state.x, state.groundY + height, state.z), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
            snapshot(name);
        }
        // showMainMenu(nil); startSandbox(): back to the sandbox with the player's robot.
        startSandbox();
        var sandboxContactPassed = checkSandboxContact();
        var passed = sandboxContactPassed && newModelsPassed && coatingPassed && boostInputPassed && modelScalePassed
            && wheelsPassed && r2d2Passed && robot.partCount == 23 && robot.triangleCount > 600_000
            && traveled > 0.3 && abs(heading) > 0.3
            && labelsPassed && groovesPassed && coursePassed && neckPassed && tracksPassed && groundContactPassed
            && pausePassed && brakePassed && focusPassed && headPassed && resetPassed && cameraPassed;
        var report = new Dictionary<string, object>
        {
            ["passed"] = passed, ["sandboxContactPassed"] = sandboxContactPassed,
            ["bb8MotionPassed"] = bb8MotionPassed, ["wallEMotionPassed"] = wallEMotionPassed, ["newModelsPassed"] = newModelsPassed,
            ["coatingPassed"] = coatingPassed, ["bodyDirtAmounts"] = dirtAmounts.Cast<object>().ToList(),
            ["boostSteeringPassed"] = boostInputPassed, ["pausePassed"] = pausePassed, ["brakePassed"] = brakePassed,
            ["focusPassed"] = focusPassed, ["headPassed"] = headPassed, ["resetPassed"] = resetPassed, ["cameraPassed"] = cameraPassed,
            ["modelScalePassed"] = modelScalePassed, ["marvinToR2D2HeightRatio"] = modelHeightRatio,
            ["r2d2WheelsPassed"] = wheelsPassed, ["r2d2ModelPassed"] = r2d2Passed, ["parts"] = robot.partCount,
            ["triangles"] = robot.triangleCount, ["distance"] = traveled, ["heading"] = heading,
            ["labelsPassed"] = labelsPassed, ["groovesPassed"] = groovesPassed, ["coursePassed"] = coursePassed,
            ["neckPassed"] = neckPassed, ["tracksPassed"] = tracksPassed, ["groundContactPassed"] = groundContactPassed,
            ["renderer"] = "Godot / SceneKit facade", ["width"] = (int)view.Size.X, ["height"] = (int)view.Size.Y,
        };
        System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "smoke.json"), JSONSerialization.prettySorted(report));
        GD.Print($"Native smoke test: {(passed ? "PASS" : "FAIL")} · {directory}");
        return passed;
    }

    /// RobotCollisionSmoke.swift checkSandboxContact(at:).
    private bool checkSandboxContact()
    {
        var contact = new Simulation(seed: 0); var input = new DriveInput(); input.throttle = 1;
        for (int i = 0; i < 600; i++) { contact.advance(input, dt: 1.0 / 60); }
        var face = 3.3 - 0.65 / 2;
        var clearance = face - contact.z - Simulation.bodyHalfDepth;
        robot.update(contact);
        world.camera.position = new SCNVector3(1.4, 1.2, contact.z - 1.1);
        world.camera.look(new SCNVector3(0, 0.28, contact.z + 0.15), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
        snapshot("sandbox-contact.png");
        robot.update(simulation); updateCamera(snap: true);
        return contact.contacting && clearance >= Simulation.collisionClearance - 1e-8 && clearance < 0.025
            && Simulation.isFree(x: contact.x, z: contact.z, heading: contact.heading);
    }

    // ================================================================ PoweredRollingSmoke.swift
    private bool checkPoweredRolling()
    {
        void update(Simulation state)
        {
            foreach (var character in RacePerformance.CharacterAllCases) { updateModel(character, state); }
        }
        bool spins(Simulation before, Simulation after)
        {
            update(new Simulation(seed: 0));
            update(before);
            var marvin = robot.wheels.Select(w => w.node.eulerAngles.x).ToArray();
            var r2 = r2d2.wheels.Select(w => w.node.eulerAngles.x).ToArray();
            var sphere = quat(bb8.ball.simdOrientation);
            var links = wallE.links.Select(l => float3(l.node.simdPosition)).ToArray();
            update(after);
            return robot.wheels.Zip(marvin).All(p => abs(p.First.node.eulerAngles.x - p.Second) > 1e-5)
                && r2d2.wheels.Zip(r2).All(p => abs(p.First.node.eulerAngles.x - p.Second) > 1e-5)
                && abs(Simd.dot(sphere.vector, quat(bb8.ball.simdOrientation).vector)) < 0.99999f
                && wallE.links.Zip(links).All(p => Simd.distance(float3(p.First.node.simdPosition), p.Second) > 1e-5f);
        }
        var input = new DriveInput(); input.throttle = 1;
        var blocked = new Simulation(seed: 0);
        for (int i = 0; i < 600; i++) { blocked.advance(input, dt: 1.0 / 60); }
        var before = blocked;
        blocked.advance(input, dt: 0.1);
        if (!(blocked.contacting && blocked.x == before.x && blocked.z == before.z
              && spins(before, blocked))) { return false; }
        var fence = new Simulation(seed: 0, dirtTrack: true, dirtStartPhase: 0.6); var fencePassed = false;
        for (int i = 0; i < 600; i++)
        {
            var prior = fence; fence.advance(input, dt: 1.0 / 60);
            if (prior.contacting && fence.contacting)
            {
                fencePassed = spins(prior, fence); break;
            }
        }
        var jumper = new DirtOpponent(); var jumpPassed = false;
        for (int i = 0; i < 600; i++)
        {
            var prior = jumper.simulation;
            jumper.advance(dt: 1.0 / 60, raceDT: 1.0 / 60);
            if (prior.airborne && jumper.simulation.airborne)
            {
                jumpPassed = spins(prior, jumper.simulation); break;
            }
        }
        update(new Simulation(seed: 0));
        GD.Print($"All robot running gear: sandbox {"true"}, fence {(fencePassed ? "true" : "false")}, jump {(jumpPassed ? "true" : "false")}");
        return fencePassed && jumpPassed;
    }

    // ================================================================ PlayableCharacterSmoke.swift (sandbox half)
    private bool checkPlayableCharacters()
    {
        var results = new Dictionary<string, object>();
        if (!checkPoweredRolling()) { return false; }
        System.IO.Directory.CreateDirectory(directory);
        foreach (var (key, character) in new[] { ("b", RacePerformance.Character.bb8), ("r", RacePerformance.Character.r2d2), ("w", RacePerformance.Character.wallE), ("m", RacePerformance.Character.marvin) })
        {
            // showMainMenu(nil); key press on the menu selects the robot.
            menuCharacter = character;
            startSandbox();
            var root = modelRoot(character);
            var passed = playerCharacter == character && simulation.character == character
                && root.parent == world.scene.rootNode
                && RacePerformance.CharacterAllCases.Count(c => modelRoot(c).parent == world.scene.rootNode) == 1;
            if (character == RacePerformance.Character.bb8) { passed = checkBB8SandboxRolling() && passed; }
            var input = new DriveInput(); input.throttle = 1; input.headYaw = 0.3; input.headPitch = 0.1;
            for (int i = 0; i < 45; i++) { simulation.advance(input, dt: 1.0 / 60); updatePlayerModel(); }
            passed = passed && simulation.distance > 0.1 && abs((double)root.position.z - simulation.z) < 1e-6;
            updateCamera(snap: true);
            snapshot($"{key}-sandbox.png");
            reset();
            passed = passed && playerCharacter == character && simulation.character == character && simulation.distance == 0;
            // The race half ({key}-race.png: startDirtTrack(), 120 race frames) needs DirtWorld and the race
            // controller from other streams; it runs in the App port.
            results[character.displayName()] = passed;
        }
        System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "characters.json"), JSONSerialization.prettySorted(results));
        return results.Count == 4 && results.Values.All(v => (bool)v);
    }

    private bool checkBB8SandboxRolling()
    {
        try
        {
            // Follow a material point initially touching the floor. Its rotation
            // must cancel translation, regardless of the menu's model orientation.
            foreach (var throttle in new[] { 1.0, -1.0 })
            {
                reset();
                var input = new DriveInput(); input.throttle = throttle; input.turn = 0.35;
                for (int i = 0; i < 45; i++)
                {
                    var before = new Float3((float)simulation.x, 0, (float)simulation.z);
                    var contact = quat(bb8.ball.simdWorldOrientation).inverse.act(new Float3(0, -(float)bb8.ballRadius, 0));
                    simulation.advance(input, dt: 1.0 / 60); updatePlayerModel();
                    var movement = new Float3((float)simulation.x, 0, (float)simulation.z) - before;
                    var rotated = quat(bb8.ball.simdWorldOrientation).act(contact);
                    var slip = movement + new Float3(rotated.x, 0, rotated.z);
                    if (Simd.length(movement) > 1e-6f && Simd.length(slip) > Simd.length(movement) * 0.01f) { return false; }
                }
            }
            return true;
        }
        finally { reset(); }
    }

    // ================================================================ BB8MotionSmoke.swift (sandbox half)
    private bool checkBB8RenderedMotion()
    {
        System.IO.Directory.CreateDirectory(directory);
        menuCharacter = RacePerformance.Character.bb8;
        var report = new List<object>();
        var passed = true;
        foreach (var dirt in new[] { false })
        {
            // (dirt == true runs on the race track: startDirtTrack(), DirtWorld; App port.)
            startSandbox();
            // Fix the camera in world space; following the robot can mask
            // a reversed roll. Inspect the source lens and rendered nodes.
            simulation = new Simulation(seed: 0, dirtTrack: dirt, dirtStartPhase: 0, character: RacePerformance.Character.bb8);
            updatePlayerModel();
            var origin = float3(bb8.root.simdPosition);
            world.camera.position = new SCNVector3(origin.x + 1.2, origin.y + 0.6, origin.z + 0.75);
            world.camera.look(new SCNVector3(origin.x, origin.y + 0.27, origin.z + 0.3));
            Float3? previousContact = null, contactInBall = null, previousCenter = null;
            for (int frame = 0; frame <= 48; frame++)
            {
                if (frame > 0)
                {
                    var input = new DriveInput(); input.throttle = 0.35; input.turn = 0.2;
                    simulation.advance(input, dt: 1.0 / 60); updatePlayerModel();
                }
                var center = float3(bb8.ball.presentation.simdWorldPosition);
                var slip = 0f;
                if (previousContact is { } pc && contactInBall is { } cib && previousCenter is { } pce && !simulation.airborne)
                {
                    var moved = float3(bb8.ball.presentation.simdConvertPosition(simd(cib), to: null)) - pc;
                    slip = hypot(moved.x, moved.z);
                    var travel = hypot(center.x - pce.x, center.z - pce.z);
                    passed = passed && slip < max(0.00001f, travel * 0.03f);
                }
                previousCenter = center;
                previousContact = center + new Float3(0, -(float)bb8.ballRadius, 0);
                contactInBall = float3(bb8.ball.presentation.simdConvertPositionFrom(simd(previousContact.Value), from: null));
                var orientation = quat(bb8.ball.simdWorldOrientation);
                var rendered = quat(bb8.ball.presentation.simdWorldOrientation);
                var eye = float3(bb8.head.simdConvertVector(simd(new Float3(0, 0, 1)), to: null));
                var forward = new Float3((float)sin(simulation.heading), 0, (float)cos(simulation.heading));
                var match = abs(Simd.dot(orientation.vector, rendered.vector));
                passed = passed && match > 0.999f && Simd.dot(eye, forward) > 0.999f;
                if (frame % 8 == 0)
                {
                    snapshot($"{(dirt ? "race" : "sandbox")}-{frame}.png");
                    report.Add(new Dictionary<string, object> { ["dirt"] = dirt, ["frame"] = frame, ["x"] = simulation.x, ["z"] = simulation.z,
                        ["contactSlip"] = (double)slip, ["modelVsPresentation"] = (double)match, ["eyeForward"] = (double)Simd.dot(eye, forward) });
                }
            }
        }
        System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "motion.json"), JSONSerialization.prettySorted(report));
        return passed;
    }

    // ---- simd bridging (facade simd aliases <-> MarvinCore simd types used by the Swift code)
    private static QuatF quat(SCNQuatF q) { var (x, y, z, w) = SimdBridge.Get(q); return new QuatF((float)x, (float)y, (float)z, (float)w); }
    private static Float3 float3(SCNFloat3 v) { var (x, y, z) = SimdBridge.Get(v); return new Float3((float)x, (float)y, (float)z); }
    private static SCNFloat3 simd(Float3 v) => SimdBridge.F3(v.x, v.y, v.z);
    private static Float4x4 float4x4(SCNFloat4x4 m)
    {
        var s = SimdBridge.M(m);
        return new Float4x4(new Float4((float)s.m11, (float)s.m12, (float)s.m13, (float)s.m14), new Float4((float)s.m21, (float)s.m22, (float)s.m23, (float)s.m24),
                            new Float4((float)s.m31, (float)s.m32, (float)s.m33, (float)s.m34), new Float4((float)s.m41, (float)s.m42, (float)s.m43, (float)s.m44));
    }

    /// Foundation's JSONSerialization with [.prettyPrinted, .sortedKeys] (as the macOS smoke reports are written).
    private static class JSONSerialization
    {
        public static string prettySorted(object value)
        {
            var sb = new StringBuilder();
            write(sb, value, 0);
            return sb.ToString();
        }
        private static void write(StringBuilder sb, object value, int indent)
        {
            string pad(int n) => new string(' ', n * 2);
            switch (value)
            {
                case null: sb.Append("null"); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case string s: sb.Append('"').Append(s.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"'); break;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case double d: sb.Append(d == Math.Floor(d) && Math.Abs(d) < 1e15 ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString("G17", CultureInfo.InvariantCulture)); break;
                case IDictionary<string, object> dict:
                    if (dict.Count == 0) { sb.Append("{\n\n").Append(pad(indent)).Append('}'); break; }
                    sb.Append("{\n");
                    var keys = dict.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
                    for (int k = 0; k < keys.Count; k++)
                    {
                        sb.Append(pad(indent + 1)).Append('"').Append(keys[k]).Append("\" : ");
                        write(sb, dict[keys[k]], indent + 1);
                        sb.Append(k + 1 < keys.Count ? ",\n" : "\n");
                    }
                    sb.Append(pad(indent)).Append('}');
                    break;
                case System.Collections.IEnumerable list:
                    var items = list.Cast<object>().ToList();
                    sb.Append("[\n");
                    for (int k = 0; k < items.Count; k++)
                    {
                        sb.Append(pad(indent + 1));
                        write(sb, items[k], indent + 1);
                        sb.Append(k + 1 < items.Count ? ",\n" : "\n");
                    }
                    sb.Append(pad(indent)).Append(']');
                    break;
                default: sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
            }
        }
    }
}
