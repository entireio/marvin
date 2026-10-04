using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

// PORT: TownSmoke.swift's `checkTown(at:)` and `saveTownFrame(_:at:)`. In Swift they extend AppController.
// The app (AppController, the robots, the race HUD, DirtWorld's track and effects) belongs to other port
// streams, so TownSmokeApp below carries exactly the AppController state and functions these smoke tests
// use, ported from App.swift / PlayerCharacter.swift for the dirt track (startDirtTrack, reset,
// advanceRacePhysics, updateRaceWorld, updateCamera), on top of the facade, MarvinCore and DirtWorld.
// Not ported here (other streams): the robot models (Robot, R2D2, ImportedRacer: no robots are drawn),
// DirtWorld.update (trails, dust, dune sand), RacePerformance expressions and the HUD (snapshots omit
// AppKit overlays). The town benchmark (startTownBenchmark/tickTownBenchmark, TownFrameMeter) is part of
// the app's frame loop and is not ported in this file.
//
// Random state, as on macOS: reset() shuffles the starting grid and applies BinaryDaylight.random(), so the
// town-smoke captures use a random sun. For 1:1 comparison with a given macOS run, MARVIN_TOWN_DAYLIGHT=
// "fraction,phase" and MARVIN_TOWN_GRID="a,b,c,d" (a permutation of DirtCourse.startingGrid) fix them.

/// AppController state and functions used by the town smoke tests (TownSmoke.swift, EntranceSmoke.swift).
public sealed partial class TownSmokeApp
{
    /// World.swift: the shared camera node (fov 48, zNear 0.02, zFar 80, no HDR until the sky attaches).
    public sealed class World
    {
        public readonly SCNNode camera = new SCNNode();
        public World()
        {
            camera.camera = new SCNCamera(); camera.camera.fieldOfView = 48;
            camera.camera.zNear = 0.02; camera.camera.zFar = 80;
            camera.camera.wantsHDR = false;
        }
    }
    public readonly SCNView view = new SCNView();
    public readonly World world = new World();
    public DirtWorld dirtWorld;
    public bool inSandbox = false, isDirtTrack = false;
    public RacePerformance.Character playerCharacter = RacePerformance.Character.marvin;
    public double? dirtOutro, dirtIntro;
    public SCNVector3 outroPosition = SCNVector3Zero, outroTarget = SCNVector3Zero;
    public readonly double dirtIntroDuration = 3.2;
    public DirtRace race = new DirtRace();
    public DirtRacePhysics racePhysics = new DirtRacePhysics();
    public DirtOpponent opponent = new DirtOpponent();
    public DirtOpponent bb8Opponent = new DirtOpponent(laneOffset: 0);
    public DirtOpponent wallEOpponent = new DirtOpponent(laneOffset: -0.65);
    public DirtOpponent[] opponents => new[] { opponent, bb8Opponent, wallEOpponent };
    public Simulation simulation = new Simulation();
    public bool raceCameraLocked => isDirtTrack && race.finished;
    public OverviewMotion overviewMotion = new OverviewMotion();
    public SCNVector3? freeCameraEye;
    public double cameraBoomFraction = 1.0;
    public SCNVector3 cameraAim = SCNVector3Zero;
    public int cameraMode = 1; public double orbitYaw = 0.65, orbitPitch = 0.5, cameraDistance = 3.5;
    public bool? weatherOverride; // deterministic native test hook; never a user setting
    /// RaceHUD.mapRegion (the HUD itself is not part of the captures).
    public RaceMapRegion mapRegion = RaceMapRegion.course;
    // MainMenu settings (UserDefaults defaults: robot collisions and both assists on).
    public bool raceRobotCollisions = true;
    public DirtDrivingAssists raceAssists = new DirtDrivingAssists(steering: true, braking: true);
    /// PlayerCharacter.swift: `lineup` keeps the opponent slots, exchanging the chosen robot and Marvin.
    public RacePerformance.Character[] lineup
    {
        get
        {
            var order = RacePerformance.CharacterAllCases;
            (order[0], order[(int)playerCharacter]) = (order[(int)playerCharacter], order[0]);
            return order;
        }
    }

    public TownSmokeApp(Godot.SceneTree tree)
    {
        // The window: 1280 x 820 content (App.swift), the SceneKit view fills it.
        view.Size = new Godot.Vector2(1280, 820);
        tree.Root.AddChild(view);
        view.antialiasingMode = SCNAntialiasingMode.multisampling4X;
    }

    public void advanceRacePhysics(DriveInput input, double dt, double raceDT)
    {
        var rivals = opponents;
        racePhysics.sand = dirtWorld.duneSand.field;
        racePhysics.advance(input, ref simulation, ref race, rivals, dt, raceDT,
            robotCollisionsEnabled: raceRobotCollisions, assists: raceAssists, city: dirtWorld.town.collisionWorld);
        dirtWorld.updateGate(racePhysics.gate);
        opponent = rivals[0]; bb8Opponent = rivals[1]; wallEOpponent = rivals[2];
    }
    /// PORT: App.swift updateOpponents() also drives RacePerformance expressions and the robot models,
    /// which are not part of the town stream; there is nothing else to update here.
    public void updateOpponents() { }
    /// PlayerCharacter.swift updateRaceWorld(dt:): the sun shadow anchor. DirtWorld.update (trails, dust,
    /// dune sand) is not part of the town stream.
    public void updateRaceWorld(double dt)
    {
        dirtWorld.sky.updateShadowCenter(raceCameraLocked ? Double3.zero : new Double3(simulation.x, simulation.groundY, simulation.z));
    }
    public List<RobotCollisions.Body> robotBodies() =>
        new[] { simulation }.Concat(opponents.Select(o => o.simulation)).Select((s, i) =>
            new RobotCollisions.Body(position: new Double3(s.x, s.groundY, s.z), heading: s.heading, profile: RobotCollisions.profiles[(int)lineup[i]])).ToList();

    /// App.swift startDirtTrack() for an already built DirtWorld (no loading screen, no audio).
    public void startDirtTrack()
    {
        dirtWorld ??= new DirtWorld();
        world.camera.camera.zFar = 250;
        world.camera.camera.screenSpaceAmbientOcclusionIntensity = 0.70;
        world.camera.camera.screenSpaceAmbientOcclusionRadius = 1.6;
        world.camera.camera.screenSpaceAmbientOcclusionBias = 0.025;
        isDirtTrack = true; inSandbox = true; simulation = new Simulation(dirtTrack: true, dirtStartOffset: DirtCourse.playerGrid.offset, dirtStartPhase: DirtCourse.playerGrid.phase);
        SCNTransaction.begin(); SCNTransaction.disableActions = true;
        dirtWorld.camera = world.camera;
        dirtWorld.sky.attach(camera: world.camera);
        // Geometry comparison tools own their temporary reference/proxy pairs.
        // Normal gameplay uses the exact-position shadow mesh by default.
        var shadowArguments = CommandLine.arguments;
        var independentGeometryComparison = new[] { "--ground-performance-test", "--shadow-culling-test", "--mesh-reuse-test" }.Any(shadowArguments.Contains);
        var useShadowBatch = !shadowArguments.Contains("--benchmark-shadow-batch-reference")
            && (!independentGeometryComparison || shadowArguments.Contains("--benchmark-shadow-batch-live"));
        if (useShadowBatch)
        {
            try { dirtWorld.town.prepareShadowBatch(camera: world.camera.camera); }
            catch (Exception error) { Godot.GD.Print($"Shadow batch unavailable; retaining original shadow geometry: {error.Message}"); }
        }
        view.antialiasingMode = SCNAntialiasingMode.multisampling2X;
        view.scene = dirtWorld.scene; dirtWorld.scene.rootNode.addChildNode(world.camera);
        mapRegion = RaceMapRegion.course; cameraAim = SCNVector3Zero;
        view.pointOfView = world.camera;
        reset();
        dirtIntro = 0; updateCamera(snap: true);
        SCNTransaction.commit();
        _ = view.prepare(dirtWorld.scene, shouldAbortBlock: null);
        _ = view.snapshot();
    }
    /// App.swift reset(_:) for the dirt track.
    public void reset()
    {
        if (!inSandbox) { return; }
        cameraMode = 1; orbitYaw = 0.65; orbitPitch = 0.5; cameraDistance = 3.5;
        dirtIntro = null; dirtOutro = null;
        simulation.reset();
        if (isDirtTrack)
        {
            var random = new SystemRandomNumberGenerator();
            var slots = DirtCourse.shuffledGrid(ref random);
            if (Environment.GetEnvironmentVariable("MARVIN_TOWN_GRID") is string grid)
            {
                var order = grid.Split(',').Select(int.Parse).ToArray();
                slots = order.Select(i => DirtCourse.startingGrid[i]).ToArray();
            }
            simulation = new Simulation(dirtTrack: true, dirtStartOffset: slots[0].offset, dirtStartPhase: slots[0].phase, character: playerCharacter);
            opponent = new DirtOpponent(slot: slots[1]);
            bb8Opponent = new DirtOpponent(slot: slots[2], laneOffset: 0);
            wallEOpponent = new DirtOpponent(slot: slots[3], laneOffset: -0.65);
            updateOpponents();
            race = new DirtRace(startPhase: slots[0].phase); racePhysics = new DirtRacePhysics(characters: lineup, townRoutes: dirtWorld.escapeRoutes); dirtWorld.updateGate(racePhysics.gate);
            dirtWorld.reset();
            var daylight = BinaryDaylight.random();
            if (Environment.GetEnvironmentVariable("MARVIN_TOWN_DAYLIGHT") is string text)
            {
                var values = text.Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                daylight = new BinaryDaylight(fraction: values[0], phase: values[1]);
            }
            dirtWorld.sky.apply(daylight);
            racePhysics.storm = new Sandstorm(enabled: weatherOverride ?? Sandstorm.drawForRace());
            dirtWorld.configureStorm(racePhysics.storm);
            cameraMode = 0; cameraDistance = 4.5; mapRegion = RaceMapRegion.course; cameraAim = SCNVector3Zero;
        }
        updateCamera(snap: true);
    }

    /// App.swift updateCamera(snap:dt:).
    public void updateCamera(bool snap, double dt = 1.0 / 60)
    {
        if (isDirtTrack) { mapRegion = RaceMapRegion.at(new Double2(simulation.x, simulation.z), previous: mapRegion); }
        if (raceCameraLocked)
        {
            cameraMode = 2;
            world.camera.position = new SCNVector3(0, 38, -33);
            world.camera.look(at: SCNVector3Zero, up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
            return;
        }
        if (dirtOutro is double outro && isDirtTrack)
        {
            var t = min(1, outro / dirtIntroDuration);
            var blend = (CGFloat)(t * t * t * (t * (t * 6 - 15) + 10));
            var end = new SCNVector3(0, 38, -33);
            world.camera.position = new SCNVector3(outroPosition.x + (end.x - outroPosition.x) * blend,
                outroPosition.y + (end.y - outroPosition.y) * blend, outroPosition.z + (end.z - outroPosition.z) * blend);
            world.camera.look(at: new SCNVector3(outroTarget.x * (1 - blend), outroTarget.y * (1 - blend), outroTarget.z * (1 - blend)),
                up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
            return;
        }
        var lookAhead = isDirtTrack && cameraMode == 0 ? 1.5 : 0.0;
        var target = new SCNVector3(simulation.x + sin(simulation.heading) * lookAhead,
            simulation.groundY + 0.35, simulation.z + cos(simulation.heading) * lookAhead);
        if (isDirtTrack && max(abs(simulation.x), abs(simulation.z)) > DesertTerrain.townEdge)
        {
            target.y = max(target.y, (CGFloat)(DirtCourse.height(x: (double)target.x, z: (double)target.z) + 0.35));
        }
        SCNVector3 desired;
        if (cameraMode == 2)
        {
            if (isDirtTrack && mapRegion != RaceMapRegion.course)
            {
                desired = new SCNVector3(target.x, target.y + 38, target.z - 33);
            } else { desired = isDirtTrack ? new SCNVector3(0, 38, -33) : new SCNVector3(0, 11.7, -10); }
        } else
        {
            var angle = cameraMode == 0 ? simulation.heading + Math.PI + (isDirtTrack ? 0 : 0.45) : orbitYaw;
            var elevation = cameraMode == 0 ? (isDirtTrack ? 0.30 : 0.48) : orbitPitch;
            desired = new SCNVector3(simulation.x + sin(angle) * cos(elevation) * cameraDistance,
                simulation.groundY + 0.4 + sin(elevation) * cameraDistance,
                simulation.z + cos(angle) * cos(elevation) * cameraDistance);
        }
        if (dirtIntro is double intro && isDirtTrack)
        {
            var t = max(0, min(1, (intro - 0.35) / (dirtIntroDuration - 0.35)));
            var blend = (CGFloat)(t * t * t * (t * (t * 6 - 15) + 10));
            var start = new SCNVector3(0, 38, -33);
            world.camera.position = new SCNVector3(start.x + (desired.x - start.x) * blend,
                start.y + (desired.y - start.y) * blend, start.z + (desired.z - start.z) * blend);
            world.camera.look(at: new SCNVector3(target.x * blend, target.y * blend, target.z * blend),
                up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
            return;
        }
        var aim = cameraMode == 2 && (!isDirtTrack || mapRegion == RaceMapRegion.course) ? SCNVector3Zero : target;
        if (cameraMode == 2)
        {
            Double3 eye = new Double3((double)desired.x, (double)desired.y, (double)desired.z), focus = new Double3((double)aim.x, (double)aim.y, (double)aim.z);
            if (snap) { overviewMotion.reset(eye: eye, aim: focus); }
            else { overviewMotion.advance(eye: eye, aim: focus, dt: dt); }
            Double3 p = overviewMotion.eye, q = overviewMotion.aim;
            world.camera.position = new SCNVector3(p.x, p.y, p.z); cameraAim = new SCNVector3(q.x, q.y, q.z);
        } else
        {
            var current = freeCameraEye ?? desired; var mix = snap ? 1 : (CGFloat)(1 - exp(-7.67 * max(0, dt)));
            var eye = new SCNVector3(current.x + (desired.x - current.x) * mix, current.y + (desired.y - current.y) * mix, current.z + (desired.z - current.z) * mix);
            freeCameraEye = eye;
            world.camera.position = eye;
            cameraAim = target;
        }
        if (isDirtTrack)
        {
            if (cameraMode != 2 || world.camera.position.y < cameraAim.y + 12)
            {
                // Collision starts at the robot, never the look-ahead point which
                // can already be through a wall during a narrow turn.
                var pivot = dirtWorld.town.cameraPivot(position: new Double3(simulation.x, simulation.groundY, simulation.z), chassisHeight: RobotCollisions.profiles[(int)lineup[0]].height);
                SCNVector3 eye = dirtWorld.town.terrainCamera(from: pivot, to: world.camera.position), clear = dirtWorld.town.clearCamera(from: pivot, to: eye);
                double distance(SCNVector3 p) => sqrt(pow((double)(p.x - pivot.x), 2) + pow((double)(p.y - pivot.y), 2) + pow((double)(p.z - pivot.z), 2));
                var allowed = min(1, min(distance(clear), dirtWorld.town.cameraRoom(at: pivot, range: distance(eye))) / max(0.001, distance(eye)));
                if (snap || allowed < cameraBoomFraction) { cameraBoomFraction = allowed; }
                else { cameraBoomFraction += (allowed - cameraBoomFraction) * (1 - exp(-3.5 * max(0, dt))); }
                var f = (CGFloat)cameraBoomFraction;
                world.camera.position = new SCNVector3(pivot.x + (eye.x - pivot.x) * f, pivot.y + (eye.y - pivot.y) * f, pivot.z + (eye.z - pivot.z) * f);
                // In a tight alley keep the route visible over the chassis rather
                // than pointing the compressed camera down into its head.
                var close = (CGFloat)(1 - min(1, distance(world.camera.position) / 1.5));
                var raised = min(pivot.y - 0.12, (CGFloat)(simulation.groundY + RobotCollisions.profiles[(int)lineup[0]].height + 0.45));
                if (cameraMode == 0)
                {
                    cameraAim = new SCNVector3(target.x, target.y + (raised - target.y) * close, target.z);
                } else
                {
                    CGFloat dx = pivot.x - eye.x, dz = pivot.z - eye.z, length = max(0.001, sqrt(dx * dx + dz * dz));
                    cameraAim = new SCNVector3(target.x + dx / length * close, target.y + (raised - target.y) * close, target.z + dz / length * close);
                }
            }
            if (cameraMode == 2) { world.camera.position.y = max(world.camera.position.y, (CGFloat)(DirtCourse.height(x: (double)world.camera.position.x, z: (double)world.camera.position.z) + 0.18)); }
        }
        world.camera.look(at: cameraAim, up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
        if (cameraMode == 2) { freeCameraEye = null; cameraBoomFraction = 1; }
        if (cameraMode != 2)
        {
            SCNVector3 p = world.camera.position, q = cameraAim;
            overviewMotion.reset(eye: new Double3((double)p.x, (double)p.y, (double)p.z), aim: new Double3((double)q.x, (double)q.y, (double)q.z));
        }
    }

    public sealed class RenderAuditException : Exception { public RenderAuditException(string message) : base(message) { } }

    public void saveTownFrame(string name, string directory)
    {
        var tiff = view.snapshot()?.tiffRepresentation;
        var bitmap = tiff == null ? null : NSBitmapImageRep.data(tiff);
        var png = bitmap?.representation(NSBitmapImageFileType.png);
        if (png == null) { throw new IOException("fileWriteUnknown"); }
        File.WriteAllBytes(Path.Combine(directory, name + ".png"), png);
        var magenta = 0;
        for (var y = 0; y < bitmap.pixelsHigh; y += 8)
        {
            for (var x = 0; x < bitmap.pixelsWide; x += 8)
            {
                if (bitmap.colorAt(x, y)?.usingColorSpace(NSColorSpace.deviceRGB) is NSColor c && c.redComponent > 0.9 && c.blueComponent > 0.9 && c.greenComponent < 0.15) { magenta += 1; }
            }
        }
        if (!(magenta < 20)) { throw new RenderAuditException($"Shader failure colour in {name}: {magenta} sampled pixels"); }
    }

    public bool checkTown(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            startDirtTrack(); dirtIntro = null; race.countDown(dt: 3);
            var valid = dirtWorld.town.validate();
            SCNVector3 target = new SCNVector3(0, 1, -15), blocked = new SCNVector3(0, 1, -22);
            var safe = dirtWorld.town.clearCamera(from: target, to: blocked);
            var cameraPassed = safe.z > blocked.z + 1 && safe.z < target.z;
            var crowd = dirtWorld.town.residents?.walkers.Select(w => w.node).ToList() ?? new List<SCNNode>();
            var before = crowd.Select(n => n.transform).ToList();
            dirtWorld.town.update(dt: 0, camera: world.camera.position, player: Double2.zero);
            var pausePassed = before.Zip(crowd).All(pair => pair.First == pair.Second.transform);
            // raceHUD.isHidden = true: the HUD is an AppKit overlay that view snapshots do not include.
            for (var frame = 0; frame < 90; frame++)
            {
                advanceRacePhysics(DirtOpponent.driveInput(simulation), dt: 1.0 / 60, raceDT: 1.0 / 60);
                updateOpponents(); updateRaceWorld(dt: 1.0 / 60);
                dirtWorld.town.update(dt: 1.0 / 60, camera: new SCNVector3(0, 5, -12), player: new Double2(simulation.x, simulation.z), robots: robotBodies(),
                    visible: node => view.isNode(node, insideFrustumOf: world.camera), shadowCamera: world.camera, viewportAspect: (double)(view.bounds.width / view.bounds.height));
            }
            var cameras = new List<(string, SCNVector3, SCNVector3)> {
                ("town-overview", new SCNVector3(0, 46, -52), new SCNVector3(0, 0, 0)),
                ("town-grandstand", new SCNVector3(5, 4.8, -10), new SCNVector3(0, 1.9, -20.5)),
                ("town-citizens", new SCNVector3(-6.0, 1.35, -28.1), new SCNVector3(-6.7, 0.65, -25.4)),
                ("town-spectators", new SCNVector3(3.3, 2.5, DirtCourse.point(0).z - 1.5), new SCNVector3(3, 2.03, DirtCourse.point(0).z - 4.18)),
                ("town-market", new SCNVector3(-14, 2.6, -27.8), new SCNVector3(0, 1.3, -24.5)),
                ("town-ramp-ground", new SCNVector3(-7.5, 0.58, -8.9), new SCNVector3(-7.5, 0.26, -12.3)),
                ("town-ramp-side", new SCNVector3(-10.0, 0.85, -10.0), new SCNVector3(-7.5, 0.20, -11.5)),
                ("town-service-access", new SCNVector3(-2, 5.8, -18), new SCNVector3(-7.5, 0.15, -10.8)),
                ("town-repair", new SCNVector3(-3.6, 2.9, -13.0), new SCNVector3(-7.5, 0.95, -7.5)),
                ("town-outskirts", new SCNVector3(-46, 13, -26), new SCNVector3(-21, 3, -3)),
                ("town-spaceport", new SCNVector3(17, 9, 0), new SCNVector3(34, 1.5, 13)),
                ("town-skyline", new SCNVector3(-10, 13, 19), new SCNVector3(10, 5, 43)),
                ("town-game-overview", new SCNVector3(0, 38, -33), new SCNVector3(0, 0, 0)) };
            foreach (var (name, eye, aim) in cameras)
            {
                world.camera.position = eye; world.camera.look(at: aim, up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                _ = view.prepare(dirtWorld.scene, shouldAbortBlock: null);
                saveTownFrame(name, directory);
            }
            updateCamera(snap: true);
            saveTownFrame("town-racing", directory);
            // Inspect from just ahead of Marvin's head, at its actual eye height.
            var forward = new Double2(sin(simulation.heading), cos(simulation.heading));
            var robotEye = new SCNVector3(simulation.x + forward.x * 0.34, simulation.groundY + 0.46, simulation.z + forward.y * 0.34);
            world.camera.position = robotEye;
            world.camera.look(at: new SCNVector3(simulation.x + forward.x * 12, simulation.groundY + 0.46, simulation.z + forward.y * 12), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
            saveTownFrame("town-robot-pov", directory);
            var detailsPassed = true;
            foreach (var fraction in new[] { 0.25, 0.60, 0.90 })
            {
                Double2 p = dirtWorld.town.explorationSurveyPoint(fraction), ahead = dirtWorld.town.explorationSurveyPoint(fraction + 0.03);
                world.camera.position = new SCNVector3(p.x, 1.5, p.y);
                world.camera.look(at: new SCNVector3(ahead.x, 1.5, ahead.y), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                foreach (var enabled in new[] { false, true })
                {
                    dirtWorld.town.explorationDetailEnabled = enabled;
                    dirtWorld.town.updateExplorationDetail(camera: world.camera.position, player: p);
                    detailsPassed = detailsPassed && (enabled ? dirtWorld.town.activeExplorationCells > 0 : dirtWorld.town.activeExplorationCells == 0);
                    saveTownFrame($"outer-{(int)(fraction * 100)}-{(enabled ? "detailed" : "simple")}", directory);
                }
            }
            dirtWorld.town.updateExplorationDetail(camera: new SCNVector3(0, 38, -33), player: new Double2(90, 45));
            detailsPassed = detailsPassed && dirtWorld.town.activeExplorationCells == 0;
            dirtWorld.town.updateExplorationDetail(camera: new SCNVector3(25, 2, -10), player: Double2.zero);
            detailsPassed = detailsPassed && dirtWorld.town.activeExplorationCells == 0;
            var count = dirtWorld.town.statistics;
            var children = dirtWorld.town.root.childNodes.Count;
            reset();
            var after = dirtWorld.town.statistics;
            var resetPassed = after.Count == count.Count && count.All(pair => after.TryGetValue(pair.Key, out var value) && value == pair.Value) && dirtWorld.town.root.childNodes.Count == children && race.countdown == 3;
            var passed = valid && resetPassed && cameraPassed && pausePassed && detailsPassed;
            var report = new Dictionary<string, object>
            {
                ["passed"] = passed, ["explorationDetailPassed"] = detailsPassed, ["layoutClearancePassed"] = valid, ["cityCoveragePassed"] = dirtWorld.town.cityCoveragePassed, ["streetNetworkPassed"] = dirtWorld.town.streetNetworkPassed, ["resetPassed"] = resetPassed, ["cameraObstructionPassed"] = cameraPassed, ["crowdPausePassed"] = pausePassed,
                ["town"] = count, ["images"] = cameras.Select(c => c.Item1).Concat(new[] { "town-racing", "town-robot-pov" }).ToList(),
            };
            File.WriteAllText(Path.Combine(directory, "town-smoke.json"), SmokeJSON.prettySorted(report));
            return passed;
        }
        catch (Exception error) { Godot.GD.Print($"Town smoke: {error}"); return false; }
    }

    [GameMode("--town-smoke-test")]
    public static void RunTownSmoke(string dir, Godot.SceneTree tree)
    {
        var app = new TownSmokeApp(tree);
        // App.swift: smoke runs never draw a sandstorm (weatherOverride = false).
        app.weatherOverride = false;
        var passed = app.checkTown(dir);
        Godot.GD.Print($"Town smoke: {(passed ? "PASS" : "FAIL")} · {dir}");
        tree.Quit(passed ? 0 : 1);
    }
}

/// JSONSerialization.data(withJSONObject:options:[.prettyPrinted, .sortedKeys]) text layout: two-space
/// indentation, `"key" : value`, keys sorted, integral doubles printed without a fraction, other doubles
/// with 17 significant digits.
internal static class SmokeJSON
{
    public static string prettySorted(object value)
    {
        var sb = new StringBuilder(); write(sb, value, 0); return sb.ToString();
    }
    private static void write(StringBuilder sb, object value, int depth)
    {
        string pad(int d) => new string(' ', d * 2);
        switch (value)
        {
            case null: sb.Append("null"); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case string s: sb.Append(quote(s)); break;
            case int or long or uint: sb.Append(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)); break;
            // JSONSerialization prints integral doubles as integers and others with %.17g (measured: 48.072460739003368).
            case double d: sb.Append(d == Math.Floor(d) && abs(d) < 1e15 ? ((long)d).ToString(System.Globalization.CultureInfo.InvariantCulture) : d.ToString("G17", System.Globalization.CultureInfo.InvariantCulture).Replace("E", "e")); break;
            case float f: write(sb, (double)f, depth); break;
            case IDictionary dictionary:
            {
                var keys = dictionary.Keys.Cast<object>().Select(k => k.ToString()).OrderBy(k => k, keyOrder).ToList();
                if (keys.Count == 0) { sb.Append("{\n\n" + pad(depth) + "}"); break; }
                sb.Append("{\n");
                for (var i = 0; i < keys.Count; i++)
                {
                    sb.Append(pad(depth + 1)).Append(quote(keys[i])).Append(" : ");
                    write(sb, dictionary[keys[i]], depth + 1);
                    sb.Append(i + 1 < keys.Count ? ",\n" : "\n");
                }
                sb.Append(pad(depth)).Append('}');
                break;
            }
            case IEnumerable sequence:
            {
                var items = sequence.Cast<object>().ToList();
                if (items.Count == 0) { sb.Append("[\n\n" + pad(depth) + "]"); break; }
                sb.Append("[\n");
                for (var i = 0; i < items.Count; i++)
                {
                    sb.Append(pad(depth + 1)); write(sb, items[i], depth + 1);
                    sb.Append(i + 1 < items.Count ? ",\n" : "\n");
                }
                sb.Append(pad(depth)).Append(']');
                break;
            }
            default: sb.Append(quote(value.ToString())); break;
        }
    }
    /// `.sortedKeys` compares keys with [.numeric, .caseInsensitive, .forcedOrdering] (swift-corelibs-foundation
    /// JSONSerialization; measured on macOS: "signs" sorts before "signTextFits").
    private static readonly Comparer<string> keyOrder = Comparer<string>.Create((a, b) =>
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) { i++; }
                while (j < b.Length && char.IsAsciiDigit(b[j])) { j++; }
                var order = decimal.Parse(a[si..i]).CompareTo(decimal.Parse(b[sj..j]));
                if (order != 0) { return order; }
                continue;
            }
            var c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
            if (c != 0) { return c; }
            i++; j++;
        }
        var length = (a.Length - i).CompareTo(b.Length - j);
        return length != 0 ? length : string.CompareOrdinal(a, b);
    });
    private static string quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("/", "\\/").Replace("\n", "\\n") + "\"";
}
