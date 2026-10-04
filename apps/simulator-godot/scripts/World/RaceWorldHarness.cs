using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Godot;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

/// <summary>
/// The race-world subset of AppController (App.swift, PlayerCharacter.swift) that the race smoke
/// modes drive: the 1280 x 820 SCNView, World's camera, the race state, startDirtTrack(), reset(),
/// advanceRacePhysics(), updateRaceWorld() and updateCamera(). PORT: robots, the town, HUDs, audio
/// and menus belong to other streams and are absent here; see the notes on each member.
/// </summary>
public sealed class RaceWorldHarness
{
    public readonly SCNView view;
    /// World.swift's camera node (fov 48, zNear 0.02, zFar 80, LDR until the race attaches the sky).
    public readonly SCNNode camera = new SCNNode();
    public readonly DirtWorld dirtWorld;
    public bool isDirtTrack = false, inSandbox = false;
    public RacePerformance.Character playerCharacter = RacePerformance.Character.marvin;
    public RacePerformance.Character[] lineup
    {
        get { var order = RacePerformance.CharacterAllCases; (order[0], order[(int)playerCharacter]) = (order[(int)playerCharacter], order[0]); return order; }
    }
    public double? dirtIntro, dirtOutro;
    public readonly double dirtIntroDuration = 3.2;
    public SCNVector3 outroPosition = SCNVector3Zero, outroTarget = SCNVector3Zero;
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
    public RaceMapRegion mapRegion = RaceMapRegion.course;
    /// MainMenu defaults (UserDefaults unset): robot collisions on, steering and braking assists on.
    public bool raceRobotCollisions = true;
    public DirtDrivingAssists raceAssists = new DirtDrivingAssists(steering: true, braking: true);
    /// Robot.modelScale (Robot.swift), computed from the same Marvin CAD geometry. PORT: no robot nodes are built.
    public readonly double modelScale;

    public RaceWorldHarness(SceneTree tree, DirtWorld prebuilt = null)
    {
        // applicationDidFinishLaunching: `if smokeDirectory != nil { weatherOverride=false }`. Every harness run is a
        // smoke mode with an output directory, so races are clear unless a mode overrides the weather itself.
        weatherOverride = false;
        view = new SCNView(new CGRect(0, 0, 1280, 820));
        tree.Root.AddChild(view);
        view.antialiasingMode = SCNAntialiasingMode.multisampling4X;
        camera.camera = new SCNCamera(); camera.camera.fieldOfView = 48;
        camera.camera.zNear = 0.02; camera.camera.zFar = 80;
        camera.camera.wantsHDR = false;
        modelScale = marvinModelScale();
        var started = DateTime.Now;
        dirtWorld = prebuilt ?? new DirtWorld();
        if (prebuilt == null) GD.Print($"DirtWorld built in {(DateTime.Now - started).TotalSeconds:0.0} s");
    }

    /// Robot.swift: modelScale = R2D2.sceneHeight * (0.60 / R2D2.heightMeters) / neutralHeight, where neutralHeight is the
    /// highest vertex of every CAD part except the head and the track belt.
    private static double marvinModelScale()
    {
        var manifest = Json.ParseString(Godot.FileAccess.GetFileAsString("res://assets/Marvin/manifest.json")).AsGodotDictionary();
        var data = Godot.FileAccess.GetFileAsBytes("res://assets/Marvin/geometry.bin");
        double neutralHeight = 0;
        foreach (var part in manifest["parts"].AsGodotArray())
        {
            var p = part.AsGodotDictionary();
            string name = p["name"].AsString();
            if (name == "Head" || name == "09_track") continue;
            int offset = p["vertexOffset"].AsInt32(), count = p["vertexCount"].AsInt32();
            for (int i = 0; i < count; i++) neutralHeight = max(neutralHeight, (double)BitConverter.ToSingle(data, offset + i * 24 + 4));
        }
        return 0.885 * (0.60 / 1.08) / neutralHeight;
    }
    /// ImportedRacer.contacts (BB-8, WALL-E) from the generated mesh JSON.
    public static (double x, double z, double width)[] racerContacts(string kind)
    {
        var mesh = Json.ParseString(Godot.FileAccess.GetFileAsString($"res://assets/{kind}/Generated/mesh.json")).AsGodotDictionary();
        return mesh["contacts"].AsGodotArray().Select(c => { var a = c.AsGodotArray(); return (a[0].AsDouble(), a[1].AsDouble(), a[2].AsDouble()); }).ToArray();
    }

    /// AppController.startDirtTrack() for the race world (no robots, HUD, audio or shadow batch).
    public void startDirtTrack()
    {
        camera.camera.zFar = 250;
        camera.camera.screenSpaceAmbientOcclusionIntensity = 0.70;
        camera.camera.screenSpaceAmbientOcclusionRadius = 1.6;
        camera.camera.screenSpaceAmbientOcclusionBias = 0.025;
        isDirtTrack = true; inSandbox = true; simulation = new Simulation(dirtTrack: true, dirtStartOffset: DirtCourse.playerGrid.offset, dirtStartPhase: DirtCourse.playerGrid.phase);
        dirtWorld.camera = camera;
        dirtWorld.sky.attach(camera: camera);
        view.antialiasingMode = SCNAntialiasingMode.multisampling2X;
        view.scene = dirtWorld.scene; dirtWorld.scene.rootNode.addChildNode(camera);
        dirtWorld.additionalContacts = new[] { racerContacts("BB8"), racerContacts("WallE") };
        mapRegion = RaceMapRegion.course; cameraAim = SCNVector3Zero;
        view.pointOfView = camera;
        reset();
        dirtIntro = 0; updateCamera(snap: true);
        _ = view.prepare(dirtWorld.scene, shouldAbortBlock: null);
    }

    /// AppController.reset(_:) for the race. PORT: MARVIN_GRID_SLOTS ("i,j,k,l" indices into DirtCourse.startingGrid),
    /// MARVIN_DAYLIGHT_FRACTION and MARVIN_DAYLIGHT_PHASE pin the otherwise random grid and daylight so a capture
    /// can be compared with a particular macOS run.
    public void reset()
    {
        cameraMode = 1; orbitYaw = 0.65; orbitPitch = 0.5; cameraDistance = 3.5;
        dirtIntro = null; dirtOutro = null;
        simulation.reset();
        if (isDirtTrack)
        {
            var random = new SystemRandomNumberGenerator();
            var slots = DirtCourse.shuffledGrid(ref random);
            if (System.Environment.GetEnvironmentVariable("MARVIN_GRID_SLOTS") is string order)
                slots = order.Split(',').Select(s => DirtCourse.startingGrid[int.Parse(s.Trim(), CultureInfo.InvariantCulture)]).ToArray();
            simulation = new Simulation(dirtTrack: true, dirtStartOffset: slots[0].offset, dirtStartPhase: slots[0].phase, character: playerCharacter);
            opponent = new DirtOpponent(slot: slots[1]);
            bb8Opponent = new DirtOpponent(slot: slots[2], laneOffset: 0);
            wallEOpponent = new DirtOpponent(slot: slots[3], laneOffset: -0.65);
            race = new DirtRace(startPhase: slots[0].phase); racePhysics = new DirtRacePhysics(characters: lineup, townRoutes: dirtWorld.escapeRoutes); dirtWorld.updateGate(racePhysics.gate);
            dirtWorld.reset(); dirtWorld.sky.apply(pinnedDaylight() ?? BinaryDaylight.random());
            racePhysics.storm = new Sandstorm(enabled: weatherOverride ?? Sandstorm.drawForRace());
            dirtWorld.configureStorm(racePhysics.storm);
            cameraMode = 0; cameraDistance = 4.5; mapRegion = RaceMapRegion.course; cameraAim = SCNVector3Zero;
        }
        updateCamera(snap: true);
    }
    /// PORT: test pin. MARVIN_DAYLIGHT_FRACTION / MARVIN_DAYLIGHT_PHASE may hold comma-separated lists; each reset()
    /// takes the next entry (the last one repeats), so a mode that resets several times (the dirt smoke test resets
    /// for the full race) can reproduce each daylight of a macOS run.
    private static int pinnedDaylightCount = 0;
    public static BinaryDaylight? pinnedDaylight()
    {
        string pick(string name)
        {
            var list = System.Environment.GetEnvironmentVariable(name)?.Split(',');
            return list == null ? null : list[Math.Min(pinnedDaylightCount, list.Length - 1)].Trim();
        }
        var f = pick("MARVIN_DAYLIGHT_FRACTION");
        if (f == null || !double.TryParse(f, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction) || !double.IsFinite(fraction)) return null;
        var p = pick("MARVIN_DAYLIGHT_PHASE");
        double phase = p != null && double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 1.2;
        pinnedDaylightCount += 1;
        return new BinaryDaylight(fraction: fraction, phase: phase);
    }

    public void advanceRacePhysics(DriveInput input, double dt, double raceDT)
    {
        var rivals = opponents;
        racePhysics.sand = dirtWorld.duneSand.field;
        racePhysics.advance(input, ref simulation, ref race, rivals, dt: dt, raceDT: raceDT,
            robotCollisionsEnabled: raceRobotCollisions, assists: raceAssists, city: dirtWorld.town.collisionWorld);
        dirtWorld.updateGate(racePhysics.gate);
        opponent = rivals[0]; bb8Opponent = rivals[1]; wallEOpponent = rivals[2];
    }

    /// PlayerCharacter.swift updateRaceWorld(dt:).
    public void updateRaceWorld(double dt)
    {
        dirtWorld.sky.updateShadowCenter(raceCameraLocked ? Double3.zero : new Double3(simulation.x, simulation.groundY, simulation.z));
        // Effects stay in model order so tires/tracks and emitter counts match
        // their geometry, independent of who occupies the player slot.
        var states = new[] { simulation }.Concat(opponents.Select(o => o.simulation)).ToArray();
        var order = lineup;
        var canonical = RacePerformance.CharacterAllCases.Select(c => states[Array.IndexOf(order, c)]).ToArray();
        dirtWorld.update(canonical[0], opponent: canonical[1], dt: dt,
                         modelScale: modelScale, additional: new[] { canonical[2], canonical[3] });
    }

    /// AppController.updateCamera(snap:dt:), including the camera-boom clipping. PORT: the boom queries
    /// (TownWorld.cameraPivot, terrainCamera, clearCamera, cameraRoom) come from the town stream; until it lands the
    /// stub sees only InfieldLayout.obstacles and the terrain, not the buildings and grandstands.
    public void updateCamera(bool snap, double dt = 1.0 / 60)
    {
        if (isDirtTrack) { mapRegion = RaceMapRegion.at(new Double2(simulation.x, simulation.z), mapRegion); }
        if (raceCameraLocked)
        {
            cameraMode = 2;
            camera.position = new SCNVector3(0, 38, -33);
            camera.look(SCNVector3Zero, up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
            return;
        }
        if (dirtOutro is double outro && isDirtTrack)
        {
            double t = min(1, outro / dirtIntroDuration);
            double blend = t * t * t * (t * (t * 6 - 15) + 10);
            var end = new SCNVector3(0, 38, -33);
            camera.position = new SCNVector3(outroPosition.x + (end.x - outroPosition.x) * blend,
                outroPosition.y + (end.y - outroPosition.y) * blend, outroPosition.z + (end.z - outroPosition.z) * blend);
            camera.look(new SCNVector3(outroTarget.x * (1 - blend), outroTarget.y * (1 - blend), outroTarget.z * (1 - blend)),
                up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
            return;
        }
        double lookAhead = isDirtTrack && cameraMode == 0 ? 1.5 : 0.0;
        var target = new SCNVector3(simulation.x + sin(simulation.heading) * lookAhead,
            simulation.groundY + 0.35, simulation.z + cos(simulation.heading) * lookAhead);
        if (isDirtTrack && max(abs(simulation.x), abs(simulation.z)) > DesertTerrain.townEdge)
        {
            target.y = max(target.y, DirtCourse.height(target.x, target.z) + 0.35);
        }
        SCNVector3 desired;
        if (cameraMode == 2)
        {
            if (isDirtTrack && mapRegion != RaceMapRegion.course)
            {
                desired = new SCNVector3(target.x, target.y + 38, target.z - 33);
            }
            else { desired = isDirtTrack ? new SCNVector3(0, 38, -33) : new SCNVector3(0, 11.7, -10); }
        }
        else
        {
            double angle = cameraMode == 0 ? simulation.heading + Math.PI + (isDirtTrack ? 0 : 0.45) : orbitYaw;
            double elevation = cameraMode == 0 ? (isDirtTrack ? 0.30 : 0.48) : orbitPitch;
            desired = new SCNVector3(simulation.x + sin(angle) * cos(elevation) * cameraDistance,
                simulation.groundY + 0.4 + sin(elevation) * cameraDistance,
                simulation.z + cos(angle) * cos(elevation) * cameraDistance);
        }
        if (dirtIntro is double intro && isDirtTrack)
        {
            double t = max(0, min(1, (intro - 0.35) / (dirtIntroDuration - 0.35)));
            double blend = t * t * t * (t * (t * 6 - 15) + 10);
            var start = new SCNVector3(0, 38, -33);
            camera.position = new SCNVector3(start.x + (desired.x - start.x) * blend,
                start.y + (desired.y - start.y) * blend, start.z + (desired.z - start.z) * blend);
            camera.look(new SCNVector3(target.x * blend, target.y * blend, target.z * blend),
                up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
            return;
        }
        var aim = cameraMode == 2 && (!isDirtTrack || mapRegion == RaceMapRegion.course) ? SCNVector3Zero : target;
        if (cameraMode == 2)
        {
            Double3 eye = new Double3(desired.x, desired.y, desired.z), focus = new Double3(aim.x, aim.y, aim.z);
            if (snap) { overviewMotion.reset(eye: eye, aim: focus); }
            else { overviewMotion.advance(eye: eye, aim: focus, dt: dt); }
            Double3 p = overviewMotion.eye, q = overviewMotion.aim;
            camera.position = new SCNVector3(p.x, p.y, p.z); cameraAim = new SCNVector3(q.x, q.y, q.z);
        }
        else
        {
            var current = freeCameraEye ?? desired; double mix = snap ? 1 : 1 - exp(-7.67 * max(0, dt));
            var eye = new SCNVector3(current.x + (desired.x - current.x) * mix, current.y + (desired.y - current.y) * mix, current.z + (desired.z - current.z) * mix);
            freeCameraEye = eye;
            camera.position = eye;
            cameraAim = target;
        }
        if (isDirtTrack)
        {
            if (cameraMode != 2 || camera.position.y < cameraAim.y + 12)
            {
                // Collision starts at the robot, never the look-ahead point which
                // can already be through a wall during a narrow turn.
                var pivot = dirtWorld.town.cameraPivot(new Double3(simulation.x, simulation.groundY, simulation.z), RobotCollisions.profiles[(int)lineup[0]].height);
                SCNVector3 eye = dirtWorld.town.terrainCamera(pivot, camera.position), clear = dirtWorld.town.clearCamera(pivot, eye);
                double distance(SCNVector3 p) => sqrt(pow(p.x - pivot.x, 2) + pow(p.y - pivot.y, 2) + pow(p.z - pivot.z, 2));
                double allowed = min(1, min(distance(clear), dirtWorld.town.cameraRoom(pivot, distance(eye))) / max(0.001, distance(eye)));
                if (snap || allowed < cameraBoomFraction) { cameraBoomFraction = allowed; }
                else { cameraBoomFraction += (allowed - cameraBoomFraction) * (1 - exp(-3.5 * max(0, dt))); }
                double f = cameraBoomFraction;
                camera.position = new SCNVector3(pivot.x + (eye.x - pivot.x) * f, pivot.y + (eye.y - pivot.y) * f, pivot.z + (eye.z - pivot.z) * f);
                // In a tight alley keep the route visible over the chassis rather
                // than pointing the compressed camera down into its head.
                double close = 1 - min(1, distance(camera.position) / 1.5);
                double raised = min(pivot.y - 0.12, simulation.groundY + RobotCollisions.profiles[(int)lineup[0]].height + 0.45);
                if (cameraMode == 0)
                {
                    cameraAim = new SCNVector3(target.x, target.y + (raised - target.y) * close, target.z);
                }
                else
                {
                    double dx = pivot.x - eye.x, dz = pivot.z - eye.z, length = max(0.001, sqrt(dx * dx + dz * dz));
                    cameraAim = new SCNVector3(target.x + dx / length * close, target.y + (raised - target.y) * close, target.z + dz / length * close);
                }
            }
            if (cameraMode == 2) { camera.position.y = max(camera.position.y, DirtCourse.height(camera.position.x, camera.position.z) + 0.18); }
        }
        camera.look(cameraAim, up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
        if (cameraMode == 2) { freeCameraEye = null; cameraBoomFraction = 1; }
        if (cameraMode != 2)
        {
            SCNVector3 p = camera.position, q = cameraAim;
            overviewMotion.reset(eye: new Double3(p.x, p.y, p.z), aim: new Double3(q.x, q.y, q.z));
        }
    }

    /// TownSmoke.swift saveTownFrame(_:at:): the view's snapshot as NAME.png, failing on shader-error magenta.
    public void saveTownFrame(string name, string directory)
    {
        var bitmap = NSBitmapImageRep.data(view.snapshot().tiffRepresentation);
        var png = bitmap.representation(NSBitmapImageFileType.png);
        File.WriteAllBytes(Path.Combine(directory, name + ".png"), png);
        int magenta = 0;
        for (int y = 0; y < bitmap.pixelsHigh; y += 8)
            for (int x = 0; x < bitmap.pixelsWide; x += 8)
            {
                var c = bitmap.colorAt(x, y)?.usingColorSpace(NSColorSpace.deviceRGB);
                if (c != null && c.redComponent > 0.9 && c.blueComponent > 0.9 && c.greenComponent < 0.15) magenta += 1;
            }
        if (!(magenta < 20)) throw new InvalidOperationException($"Shader failure colour in {name}: {magenta} sampled pixels");
    }
    /// view.snapshot() written as a PNG (the composite smoke test's captures).
    public void saveSnapshot(string file, string directory)
    {
        var bitmap = NSBitmapImageRep.data(view.snapshot().tiffRepresentation);
        File.WriteAllBytes(Path.Combine(directory, file), bitmap.representation(NSBitmapImageFileType.png));
    }

    // ---- JSONSerialization(.prettyPrinted, .sortedKeys)
    public static void writeJSON(string path, object value) => File.WriteAllText(path, json(value, 0) + "\n");
    private static string json(object value, int indent)
    {
        string pad = new string(' ', indent * 2), inner = new string(' ', indent * 2 + 2);
        switch (value)
        {
            case null: return "null";
            case bool b: return b ? "true" : "false";
            case string s: return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            case double d: return double.IsFinite(d) ? (d == Math.Floor(d) && Math.Abs(d) < 1e15 ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString("R", CultureInfo.InvariantCulture)) : "null";
            case float f: return json((double)f, indent);
            case int i: return i.ToString(CultureInfo.InvariantCulture);
            case long l: return l.ToString(CultureInfo.InvariantCulture);
            case System.Collections.IDictionary dict:
            {
                var keys = dict.Keys.Cast<object>().Select(k => k.ToString()).OrderBy(k => k, StringComparer.Ordinal).ToList();
                if (keys.Count == 0) return "{\n\n" + pad + "}";
                var sb = new StringBuilder("{\n");
                for (int k = 0; k < keys.Count; k++)
                    sb.Append(inner).Append(json(keys[k], 0)).Append(" : ").Append(json(dict[keys[k]], indent + 1)).Append(k + 1 < keys.Count ? ",\n" : "\n");
                return sb.Append(pad).Append('}').ToString();
            }
            case System.Collections.IEnumerable list:
            {
                var items = list.Cast<object>().ToList();
                if (items.Count == 0) return "[\n\n" + pad + "]";
                var sb = new StringBuilder("[\n");
                for (int k = 0; k < items.Count; k++) sb.Append(inner).Append(json(items[k], indent + 1)).Append(k + 1 < items.Count ? ",\n" : "\n");
                return sb.Append(pad).Append(']').ToString();
            }
            default: return "\"" + value + "\"";
        }
    }
}
