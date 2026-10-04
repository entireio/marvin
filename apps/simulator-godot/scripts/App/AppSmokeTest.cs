// Port of App.swift's smokeTest() (`--smoke-test DIR`), in its own file for size.
//
// The composite native smoke test: the main-menu check at menu frame 20 (MenuSmoke.cs), then the sandbox drive
// (frames 30-120) and at smoke frame 150 every sandbox, robot, race-track, HUD, score and mode check of App.swift in
// its order, with the same captures and smoke.json keys as reference/mac/smoke.
// PORT: the light/dark app-icon checks (lightIconPassed, darkIconPassed) need the App port's Dock icon handling and
// the OS appearance; they are not run, not reported and not part of `passed`.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

public partial class AppController
{
    [GameMode("--smoke-test")]
    public static async Task RunSmokeTest(string dir, SceneTree tree)
    {
        var app = await launchSmoke(tree, dir);
        // tick() at menu frame 20: the menu check, which ends by activating the sandbox (mainMenu.activate()).
        await app.checkMainMenu(at: dir, tree);
        // tick() then runs the sandbox and calls smokeTest() every frame until it exits at smoke frame 150.
        app.lastTime = ProcessInfo.processInfo.systemUptime;
        app.timer = new Marvin.SceneKit.Timer(1.0 / 60);
        while (app.timer.isValid) { await frame(tree); }
    }

    /// `--debris-smoke-test DIR` (App.swift tick: `dirtWorld.checkDebris()`; prints its results, writes nothing).
    [GameMode("--debris-smoke-test")]
    public static async Task RunDebrisSmokeTest(string dir, SceneTree tree)
    {
        var app = await launchSmoke(tree, dir);
        var passed = app.dirtWorld.checkDebris();
        exit(passed ? 0 : 1);
    }

    public void smokeTest()
    {
        if (!(smokeDirectory is string directory)) { return; }
        smokeFrames += 1;
        void key(ushort code, bool down, NSEvent.ModifierFlags modifiers = 0)
        {
            var @event = NSEvent.keyEvent(down ? NSEvent.EventType.keyDown : NSEvent.EventType.keyUp, NSPoint.zero,
                modifiers, ProcessInfo.processInfo.systemUptime, window.windowNumber, null, "", "", false, code);
            if (down) { view.keyDown(@event); } else { view.keyUp(@event); }
        }
        if (smokeFrames >= 30 && smokeFrames < 90) { key(13, down: true); }
        if (smokeFrames >= 90 && smokeFrames < 120) { key(13, down: false); key(2, down: true); }
        if (smokeFrames == 120) { view.clearInput(); }
        if (smokeFrames != 150) { return; }
        // PORT: exit() quits at the end of this frame in Godot; stop ticking so no frame runs after it.
        timer?.invalidate();
        try
        {
            var url = directory;
            Directory.CreateDirectory(url);
            var bitmap = NSBitmapImageRep.data(view.snapshot().tiffRepresentation);
            var scenePNG = bitmap?.representation(NSBitmapImageFileType.png) ?? throw new IOException("fileWriteUnknown");
            File.WriteAllBytes(Path.Combine(url, "native-scene.png"), scenePNG);
            var boostInputPassed = true;
            foreach (var (forward, turn) in new (ushort, ushort)[] { (13, 0), (13, 2), (126, 123), (126, 124) })
            {
                view.clearInput();
                var shift = NSEvent.keyEvent(NSEvent.EventType.flagsChanged, NSPoint.zero,
                    NSEvent.ModifierFlags.shift, 0, window.windowNumber, null, "", "", false, 56);
                view.flagsChanged(shift);
                key(forward, down: true, modifiers: NSEvent.ModifierFlags.shift); key(turn, down: true, modifiers: NSEvent.ModifierFlags.shift);
                var input = view.driveInput;
                var sample = new Simulation(dirtTrack: true);
                var startHeading = sample.heading;
                sample.advance(input, dt: 0.1); sample.advance(input, dt: 0.1);
                var angle = atan2(sin(sample.heading - startHeading), cos(sample.heading - startHeading));
                boostInputPassed = boostInputPassed && input.boost && input.throttle == 1
                    && abs(input.turn) == 1 && angle * input.turn < -0.04;
                key(turn, down: false, modifiers: NSEvent.ModifierFlags.shift);
                boostInputPassed = boostInputPassed && view.driveInput.turn == 0 && view.driveInput.boost;
            }
            view.clearInput();
            double traveled = simulation.distance, heading = simulation.heading;
            togglePause(null);
            var elapsed = simulation.elapsed;
            key(13, down: true);
            simulation.advance(view.driveInput, dt: 0.1);
            var pausePassed = simulation.paused && simulation.elapsed == elapsed;
            togglePause(null);
            key(13, down: true); key(49, down: true);
            simulation.advance(view.driveInput, dt: 0.1);
            var brakePassed = simulation.speed == 0;
            view.clearInput();
            var focusPassed = view.held.Count == 0 && simulation.speed == 0;
            key(14, down: true); key(15, down: true);
            simulation.advance(view.driveInput, dt: 0.1);
            var headPassed = simulation.yaw < 0 && simulation.pitch > 0;
            reset(null);
            var resetPassed = simulation.z == -2.6 && simulation.distance == 0 && simulation.yaw == 0;
            var cameraPassed = true;
            for (int mode = 0; mode <= 2; mode++)
            {
                cameraMode = mode;
                foreach (var orbit in new[] { -2.8, -1.0, 0.0, 1.0, 2.8 })
                {
                    orbitYaw = orbit; orbitPitch = 0.7;
                    updateCamera(snap: true);
                    // A level camera has a horizontal right axis and an
                    // upward-facing up axis, independent of orbit azimuth.
                    var transform = float4x4(world.camera.simdWorldTransform);
                    cameraPassed = cameraPassed && abs(transform.column0.y) < 0.00001f
                        && transform.column1.y > 0;
                }
            }
            reset(null);
            // PORT: the light/dark app-icon checks are not ported (see the file header).
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
            var neck = robot.root.childNode(withName: "07_neck", recursively: true);
            foreach (var degrees in new[] { -80.0, 0.0, 80.0 })
            {
                robot.yawNode.eulerAngles.y = (CGFloat)(degrees * Math.PI / 180);
                var center = neck.convertPosition(new SCNVector3(0, 0.295, -0.01886), to: robot.root);
                neckPassed = neckPassed && abs(center.x) < 0.000001
                    && abs(center.y - 0.295) < 0.000001 && abs(center.z + 0.01886) < 0.000001;
                world.camera.position = new SCNVector3(0.85, 0.8, -1.1);
                world.camera.look(at: new SCNVector3(0, 0.37, -2.6), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                saveSnapshot($"neck-pan-{(int)degrees}.png", url);
            }
            robot.update(new Simulation()); updateCamera(snap: true);
            var previousCourse = simulation.checkpoints;
            reset(null); world.update(simulation);
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
                if (node.geometry is not SCNGeometry geometry) { return false; }
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
                var labelAngle = CourseRoute.labelYaw(start, point);
                var length = hypot((double)bottom.x, (double)bottom.z);
                return abs((double)positioned.x - point.x) < 0.00001 && abs((double)positioned.z - point.z) < 0.00001
                    && abs((double)bottom.x / length - sin(labelAngle)) < 0.00001
                    && abs((double)bottom.z / length - cos(labelAngle)) < 0.00001
                    && abs((double)((bounds.max.y - bounds.min.y) * label.scale.y) - 0.36) < 0.00001;
            }).All(x => x);
            startDirtTrack();
            var dirtStartPassed = isDirtTrack && race.countdown == 3 && !raceHUD.isHidden && hud.isHidden
                && view.scene == dirtWorld.scene && simulation.dirtTrack;
            var introPassed = dirtIntro == 0 && abs(world.camera.position.y - 38) < 0.001 && race.elapsed == 0;
            dirtIntro = dirtIntroDuration / 2; updateCamera(snap: true);
            var introMidPassed = world.camera.position.y > 3 && world.camera.position.y < 38;
            dirtIntro = null; updateCamera(snap: true);
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
            r2d2.update(opponent.simulation);
            bool bb8MotionPassed = bb8.checkMotion(), wallEMotionPassed = wallE.checkMotion();
            updateOpponents();
            var actingPassed = checkRaceActing(at: url);
            var robotContactsPassed = checkRobotContacts(at: url);
            var newModelsPassed = bb8MotionPassed && wallEMotionPassed
                && abs(bb8.height / R2D2.sceneHeight - 0.67 / 1.08) < 1e-7
                && abs(wallE.height / R2D2.sceneHeight - 1.016 / 1.08) < 1e-7
                && bb8.root.parent == dirtWorld.scene.rootNode && wallE.root.parent == dirtWorld.scene.rootNode;
            var r2 = opponent.simulation; var r2Angle = r2.heading;
            foreach (var (name, side, height, front, lookHeight) in new[] {
                ("r2d2-front.png", 0.85, 0.7, 1.5, 0.43),
                ("r2d2-wheels.png", 0.65, 0.16, 0.85, 0.15) })
            {
                world.camera.position = new SCNVector3(r2.x + cos(r2Angle) * side + sin(r2Angle) * front,
                    r2.groundY + height, r2.z - sin(r2Angle) * side + cos(r2Angle) * front);
                world.camera.look(at: new SCNVector3(r2.x, r2.groundY + lookHeight, r2.z),
                    up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                saveSnapshot(name, url);
            }
            foreach (var (name, model, state) in new[] { ("bb8-front.png", bb8, bb8Opponent.simulation), ("walle-front.png", wallE, wallEOpponent.simulation) })
            {
                var stateAngle = state.heading;
                world.camera.position = new SCNVector3(state.x + cos(stateAngle) * 0.8 + sin(stateAngle) * 1.5, state.groundY + 0.75, state.z - sin(stateAngle) * 0.8 + cos(stateAngle) * 1.5);
                world.camera.look(at: new SCNVector3(state.x, state.groundY + model.height * 0.5, state.z), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                saveSnapshot(name, url);
            }
            updateCamera(snap: true);
            // No racer moves during countdown, pause or inactive frames.
            advanceRaceFrame(step: 1.0 / 60, raceDelta: 1.0 / 60, advancing: true);
            var heldAtStart = opponents.All(o => o.simulation.distance == 0 && o.race.elapsed == 0);
            race.countDown(dt: 3);
            advanceRaceFrame(step: 1.0 / 60, raceDelta: 1.0 / 60, advancing: false);
            var opponentGatePassed = heldAtStart && opponents.All(o => o.simulation.distance == 0 && o.race.elapsed == 0);
            raceHUD.introducing = false; raceHUD.needsDisplay = true;
            view.displayIfNeeded();
            saveSnapshot("dirt-grid.png", url);
            race.countDown(dt: 3);
            var airborneTrailsPassed = true;
            for (int k = 0; k < 240; k++)
            {
                var phase = DirtCourse.phase(x: simulation.x, z: simulation.z);
                var target = DirtCourse.point(phase + 0.055);
                var desired = atan2(target.x - simulation.x, target.z - simulation.z);
                var error = atan2(sin(desired - simulation.heading), cos(desired - simulation.heading));
                var input = new DriveInput(); input.throttle = 1; input.boost = true; input.turn = -error * 1.5;
                advanceRacePhysics(input, dt: 1.0 / 60, raceDT: 1.0 / 60);
                var beforeTrails = dirtWorld.trailCounts;
                robot.update(simulation); updateOpponents(); dirtWorld.update(simulation, opponent: opponent.simulation, dt: 1.0 / 60, modelScale: robot.modelScale, additional: new[] { bb8Opponent.simulation, wallEOpponent.simulation });
                if (!simulation.hasDirtContact) { airborneTrailsPassed = airborneTrailsPassed && dirtWorld.trailCounts[0] == beforeTrails[0]; }
                var rivals = opponents;
                for (int i = 0; i < rivals.Length; i++)
                {
                    if (rivals[i].simulation.hasDirtContact) { continue; }
                    airborneTrailsPassed = airborneTrailsPassed && dirtWorld.trailCounts[i + 1] == beforeTrails[i + 1];
                }
            }
            var opponentPassed = opponents.All(o => o.simulation.distance > 10 && o.race.elapsed > 3.9) && wheelsPassed && opponentGatePassed && r2d2.triangleCount == 25158 && r2d2.hasCenterLeg && r2d2.wheels.Count == 3 && r2d2.root.parent == dirtWorld.scene.rootNode
                && opponent.simulation.distance > 10 && opponent.race.elapsed > 3.9;
            raceHUD.opponents = opponents; raceHUD.race = race;
            raceHUD.x = simulation.x; raceHUD.z = simulation.z; raceHUD.heading = simulation.heading;
            raceHUD.introducing = false; raceHUD.needsDisplay = true;
            // SCNView.snapshot excludes AppKit subviews; capture the HUD
            // separately for layout QA at the minimum supported window size.
            // PORT: on the Mac the overlay's Auto Layout constraints keep it at the 1280 x 792 content size while the
            // bitmap is 900 x 550, so dirt-hud.png is the top-left 900 x 550 of the full-size HUD; the facade has no
            // constraints, so the HUD keeps its frame and that rectangle is cached (as UISmoke's dirt-hud.png).
            var hudRect = new NSRect(0, 0, 900, 550);
            if (raceHUD.bitmapImageRepForCachingDisplay(hudRect) is NSBitmapImageRep hudBitmap)
            {
                raceHUD.cacheDisplay(hudRect, hudBitmap);
                if (hudBitmap.representation(NSBitmapImageFileType.png) is byte[] png) { File.WriteAllBytes(Path.Combine(url, "dirt-hud.png"), png); }
            }
            // Both brick boundaries must exist, start below the ground and
            // reach their target height wherever bricks are laid; only the
            // gate and service openings may leave short uncovered stretches.
            int trackWalls = 0;
            var wallPassed = true;
            var wallReport = new Dictionary<string, object>();
            dirtWorld.scene.rootNode.enumerateChildNodes((node, _) =>
            {
                if (!(node.name is string name && name.EndsWith("irregular brick track wall") && node.geometry?.sourcesFor(SCNGeometrySourceSemantic.vertex).FirstOrDefault() is SCNGeometrySource source)) { return; }
                trackWalls += 1;
                var cells = new Dictionary<(int, int), List<Double3>>();
                var bytes = source.data;
                for (int i = 0; i < source.vectorCount; i++)
                {
                    var values = Enumerable.Range(0, 3).Select(component =>
                    {
                        int offset = source.dataOffset + i * source.dataStride + component * source.bytesPerComponent;
                        return source.bytesPerComponent == 4 ? (double)BitConverter.ToSingle(bytes, offset) : BitConverter.ToDouble(bytes, offset);
                    }).ToArray();
                    var cellKey = ((int)floor(values[0] / 0.5), (int)floor(values[2] / 0.5));
                    if (!cells.TryGetValue(cellKey, out var list)) { list = new List<Double3>(); cells[cellKey] = list; }
                    list.Add(new Double3(values[0], values[1], values[2]));
                }
                var side = name.StartsWith("Inner") ? -1.0 : 1.0;
                var samples = DirtCourse.surfacePoints(offset: side * (DirtCourse.fenceOffset + DirtCourse.boundaryWallThickness / 2));
                int covered = 0, floating = 0, @short = 0; double worstShortfall = 0.0, worstFloat = 0.0;
                foreach (var p in samples)
                {
                    var cell = ((int)floor(p.x / 0.5), (int)floor(p.y / 0.5));
                    var nearby = new List<Double3>();
                    for (int dx = -1; dx <= 1; dx++) { for (int dz = -1; dz <= 1; dz++) { if (cells.TryGetValue((cell.Item1 + dx, cell.Item2 + dz), out var found)) { nearby.AddRange(found); } } }
                    nearby = nearby.Where(v => Simd.length(new Double2(v.x, v.z) - p) < 0.3).ToList();
                    if (nearby.Count == 0) { continue; }
                    double top = nearby.Max(v => v.y), bottom = nearby.Min(v => v.y);
                    covered += 1;
                    var ground = DirtCourse.height(x: p.x, z: p.y);
                    var wallTarget = side > 0 ? CityExit.wallTop(p) : ground + DirtCourse.postHeight;
                    if (bottom > ground) { floating += 1; worstFloat = max(worstFloat, bottom - ground); }
                    if (top < wallTarget - 0.03) { @short += 1; worstShortfall = max(worstShortfall, wallTarget - top); }
                }
                wallPassed = wallPassed && floating == 0 && @short == 0 && covered * 10 >= samples.Length * 9;
                wallReport[name] = new Dictionary<string, object> { ["samples"] = samples.Length, ["covered"] = covered, ["floating"] = floating, ["worstFloat"] = worstFloat,
                    ["short"] = @short, ["worstShortfall"] = worstShortfall };
            });
            wallPassed = wallPassed && trackWalls == 2;
            var raceContactCount = racePhysics.contactCount;
            var trailCounts = dirtWorld.trailCounts; var racerEmissions = dirtWorld.racerEmittedCount.ToArray();
            dirtWorld.update(simulation, opponent: opponent.simulation, dt: 0, modelScale: robot.modelScale, additional: new[] { bb8Opponent.simulation, wallEOpponent.simulation });
            var effectsPaused = trailCounts.SequenceEqual(dirtWorld.trailCounts) && racerEmissions.SequenceEqual(dirtWorld.racerEmittedCount);
            Simulation stoppedPlayer = simulation, stoppedRival = opponent.simulation;
            stoppedPlayer.stop(); stoppedRival.stop();
            var stoppedOthers = new[] { bb8Opponent.simulation, wallEOpponent.simulation }.Select(state => { var stopped = state; stopped.stop(); return stopped; }).ToArray();
            dirtWorld.update(stoppedPlayer, opponent: stoppedRival, dt: 0.1, modelScale: robot.modelScale, additional: stoppedOthers);
            var dirtEffectsPassed = airborneTrailsPassed && effectsPaused && trailCounts.All(c => c > 40)
                && racerEmissions.All(c => c > 20) && trailCounts.SequenceEqual(dirtWorld.trailCounts)
                && racerEmissions.SequenceEqual(dirtWorld.racerEmittedCount);
            var dirtPassed = dirtEffectsPassed && wallPassed && opponentPassed && dirtStartPassed && dirtWorld.emittedCount > 20 && race.elapsed > 3.9
                && DirtCourse.projection(x: simulation.x, z: simulation.z).distance < DirtCourse.fenceOffset;
            foreach (var (mode, name) in new[] { (2, "dirt-overview.png"), (0, "dirt-driving.png") })
            {
                cameraMode = mode; updateCamera(snap: true);
                saveSnapshot(name, url);
            }
            var trailView = DirtCourse.point(0.18);
            world.camera.position = new SCNVector3(trailView.x + 1.8, 3.8, trailView.z + 2.5);
            world.camera.look(at: new SCNVector3(trailView.x, 0, trailView.z), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
            saveSnapshot("dirt-trails.png", url);
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
            foreach (var (name, state, height) in new[] { ("marvin-dirty.png", simulation, 0.28), ("r2d2-dirty.png", opponent.simulation, 0.44), ("bb8-dirty.png", bb8Opponent.simulation, 0.27), ("walle-dirty.png", wallEOpponent.simulation, 0.42) })
            {
                var stateAngle = state.heading;
                world.camera.position = new SCNVector3(state.x + cos(stateAngle) * 0.95 + sin(stateAngle) * 1.4, state.groundY + 0.8, state.z - sin(stateAngle) * 0.95 + cos(stateAngle) * 1.4);
                world.camera.look(at: new SCNVector3(state.x, state.groundY + height, state.z), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                saveSnapshot(name, url);
            }
            var hill = DirtCourse.point(0.69 * 2 * Math.PI);
            var hillside = DirtCourse.point(0.64 * 2 * Math.PI, offset: 6);
            world.camera.position = new SCNVector3(hillside.x, 3.6, hillside.z);
            world.camera.look(at: new SCNVector3(hill.x, 0.9, hill.z), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
            saveSnapshot("dirt-fence.png", url);
            race = new DirtRace(); race.countDown(dt: 3);
            for (int i = 1; i <= 1080; i++)
            {
                var p = DirtCourse.point((double)i * 2 * Math.PI / 360);
                race.advance(x: p.x, z: p.z, dt: 0.1);
            }
            var scoreCount = scores.Length;
            recordRaceScore(); recordRaceScore();
            var stored = DirtScores.load(scoreURL.path);
            var scoresPassed = race.finished && scores.Length == Math.Min(10, scoreCount + 1) && stored.Length == scores.Length;
            var fullRaceTrailsPassed = checkFullRaceTrails(at: url);
            var raceFinishPassed = checkRaceFinish(at: url);
            reset(null);
            var dirtResetPassed = opponent.race.elapsed == 0 && opponent.simulation.distance == 0 && race.laps.Length == 0 && race.countdown == 3 && dirtWorld.emittedCount == 0 && robot.dirtCoating.amount == 0 && r2d2.dirtCoating.amount == 0 && dirtWorld.trailCounts.SequenceEqual(new[] { 0, 0, 0, 0 }) && dirtWorld.racerEmittedCount.SequenceEqual(new[] { 0, 0, 0, 0 }) && bb8.dirtCoating.amount == 0 && wallE.dirtCoating.amount == 0 && opponents.All(o => o.race.elapsed == 0 && o.simulation.distance == 0);
            showMainMenu(null); startSandbox();
            var modeReturnPassed = !isDirtTrack && view.scene == world.scene && raceHUD.isHidden && !hud.isHidden;
            var sandboxContactPassed = checkSandboxContact(at: url);
            var passed = sandboxContactPassed && fullRaceTrailsPassed && raceFinishPassed && robotContactsPassed && actingPassed && newModelsPassed && coatingPassed && boostInputPassed && modelScalePassed && introPassed && introMidPassed && scoresPassed && dirtPassed && dirtResetPassed && modeReturnPassed && menuSmokePassed && robot.partCount == 23 && robot.triangleCount > 600_000
                && traveled > 0.3 && abs(heading) > 0.3
                && labelsPassed && groovesPassed && coursePassed && neckPassed && tracksPassed && groundContactPassed && pausePassed && brakePassed && focusPassed && headPassed && resetPassed && cameraPassed;
            var report = new Dictionary<string, object> { ["passed"] = passed, ["sandboxContactPassed"] = sandboxContactPassed, ["fullRaceTrailsPassed"] = fullRaceTrailsPassed, ["raceFinishPassed"] = raceFinishPassed, ["robotContactsPassed"] = robotContactsPassed, ["raceContactCount"] = raceContactCount, ["actingPassed"] = actingPassed, ["bb8MotionPassed"] = bb8MotionPassed, ["wallEMotionPassed"] = wallEMotionPassed, ["newModelsPassed"] = newModelsPassed, ["coatingPassed"] = coatingPassed, ["bodyDirtAmounts"] = dirtAmounts, ["dirtEffectsPassed"] = dirtEffectsPassed, ["racerTrailMarks"] = trailCounts, ["racerDirtParticles"] = racerEmissions, ["boostSteeringPassed"] = boostInputPassed, ["modelScalePassed"] = modelScalePassed, ["marvinToR2D2HeightRatio"] = modelHeightRatio, ["opponentPassed"] = opponentPassed, ["wallPassed"] = wallPassed, ["trackWalls"] = wallReport, ["r2d2WheelsPassed"] = wheelsPassed, ["introPassed"] = introPassed && introMidPassed, ["scoresPassed"] = scoresPassed, ["dirtPassed"] = dirtPassed, ["dirtResetPassed"] = dirtResetPassed, ["modeReturnPassed"] = modeReturnPassed, ["menuPassed"] = menuSmokePassed, ["parts"] = robot.partCount,
                ["triangles"] = robot.triangleCount, ["distance"] = traveled,
                ["heading"] = heading, ["pausePassed"] = pausePassed, ["brakePassed"] = brakePassed,
                ["focusPassed"] = focusPassed, ["headPassed"] = headPassed, ["resetPassed"] = resetPassed,
                ["cameraPassed"] = cameraPassed,
                ["labelsPassed"] = labelsPassed, ["groovesPassed"] = groovesPassed, ["coursePassed"] = coursePassed, ["neckPassed"] = neckPassed, ["tracksPassed"] = tracksPassed, ["groundContactPassed"] = groundContactPassed,
                ["renderer"] = "Godot / SceneKit facade", ["width"] = bitmap.pixelsWide,
                ["height"] = bitmap.pixelsHigh };
            File.WriteAllText(Path.Combine(url, "smoke.json"), JSONSerialization.prettyPrintedSortedKeys(report));
            print($"Native smoke test: {(passed ? "PASS" : "FAIL")} · {directory}");
            exit(passed ? 0 : 1);
        }
        catch (Exception error) { Console.Error.WriteLine($"Smoke test failed: {error}"); exit(1); }
    }

    // ---- simd bridging: the facade's simd aliases (SCNFloat3, SCNQuatF, SCNFloat4x4) <-> MarvinCore's types,
    // which the Swift checks use (SIMD3<Float>, simd_quatf, simd_float4x4).
    internal static QuatF quat(SCNQuatF q) { var (x, y, z, w) = SimdBridge.Get(q); return new QuatF((float)x, (float)y, (float)z, (float)w); }
    internal static Float3 float3(SCNFloat3 v) { var (x, y, z) = SimdBridge.Get(v); return new Float3((float)x, (float)y, (float)z); }
    internal static SCNFloat3 simd(Float3 v) => SimdBridge.F3(v.x, v.y, v.z);
    internal static Float4x4 float4x4(SCNFloat4x4 m)
    {
        var s = SimdBridge.M(m);
        return new Float4x4(new Float4((float)s.m11, (float)s.m12, (float)s.m13, (float)s.m14), new Float4((float)s.m21, (float)s.m22, (float)s.m23, (float)s.m24),
                            new Float4((float)s.m31, (float)s.m32, (float)s.m33, (float)s.m34), new Float4((float)s.m41, (float)s.m42, (float)s.m43, (float)s.m44));
    }
}
