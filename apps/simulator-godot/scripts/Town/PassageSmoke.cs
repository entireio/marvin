using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

// Port of PassageSmoke.swift (an AppController extension). Deterministic like the macOS check: the daylight is fixed
// (0.48, phase 1.2), the weather clear, and each camera run respawns the player with Simulation(seed: 1).
public partial class AppController
{
    /// SIMD3<Float> of a node position (SceneKit's simdPosition), as MarvinCore's Float3 for simd_distance.
    private static Float3 simdF3(SCNFloat3 v) { var (x, y, z) = SimdBridge.Get(v); return new Float3((float)x, (float)y, (float)z); }

    /// Native regressions for the two shoulder slots and compressed city cameras.
    public bool checkPassages(string at)
    {
        var directory = at;
        try
        {
            Directory.CreateDirectory(directory);
            weatherOverride = false;
            try
            {
                startDirtTrack(); dirtIntro = null; race.countDown(dt: 3); setRaceControlsHidden(true);
                dirtWorld.sky.apply(new BinaryDaylight(fraction: 0.48, phase: 1.2));
                TownWorld town = dirtWorld.town; var city = town.collisionWorld;
                void capture(string name)
                {
                    updateOpponents(); updateRaceWorld(dt: 0);
                    town.update(dt: 0, camera: world.camera.position, player: new Double2(simulation.x, simulation.z));
                    _ = view.prepare(dirtWorld.scene, shouldAbortBlock: null);
                    saveTownFrame(name, directory);
                }
                var maxJoinStep = 0.0;
                foreach (var side in new[] { -1.0, 1.0 })
                {
                    foreach (var @out in new[] { -1.2, -1.0 })
                    {
                        double? last = null;
                        foreach (var along in stride(3.8, 4.2, 0.005))
                        {
                            var p = CityExit.point(side * along, @out); var h = DirtCourse.height(x: p.x, z: p.y);
                            if (last is double previous) { maxJoinStep = max(maxJoinStep, abs(h - previous)); } last = h;
                        }
                    }
                    foreach (var opened in new[] { false, true })
                    {
                        var gate = new CityGate(); gate.wantsOpen = opened;
                        for (var i = 0; i < 900; i++) { gate.advance(dt: 1.0 / 60, bodies: Array.Empty<RobotCollisions.Body>()); } racePhysics.gate = gate; dirtWorld.updateGate(gate);
                        Double2 p = CityExit.point(side * 4, -1), eye = CityExit.point(side * 6, -3.5);
                        world.camera.position = new SCNVector3(eye.x, DirtCourse.height(x: eye.x, z: eye.y) + 0.8, eye.y);
                        world.camera.look(at: new SCNVector3(p.x, DirtCourse.height(x: p.x, z: p.y), p.y), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                        capture($"gate-flank-{(long)side}-{(opened ? "open" : "closed")}");
                    }
                }
                var center = CityExit.point(0, 0);
                world.camera.position = new SCNVector3(center.x, 18, center.y);
                world.camera.look(at: new SCNVector3(center.x, 0, center.y), up: new SCNVector3(0, 0, -1), localFront: new SCNVector3(0, 0, -1));
                capture("gate-overhead");
                foreach (var (name, eye, aim) in new (string, Double3, Double3)[] {
                    ("service-track-side", new Double3(-11.5, 1.4, -15), new Double3(-7.5, 0.25, -11.5)),
                    ("service-infield-side", new Double3(-5.2, 1.2, -8.5), new Double3(-7.5, 0.3, -12.4)),
                    ("service-infield-shifted", new Double3(-4.9, 1.2, -8.5), new Double3(-7.2, 0.3, -12.4)),
                    ("service-overhead", new Double3(-7.5, 18, -12), new Double3(-7.5, 0, -12)) })
                {
                    world.camera.position = new SCNVector3(eye.x, eye.y, eye.z);
                    world.camera.look(at: new SCNVector3(aim.x, aim.y, aim.z), up: name == "service-overhead" ? new SCNVector3(0, 0, -1) : new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                    capture(name);
                    if (name == "service-overhead")
                    {
                        SCNCamera camera = world.camera.camera; var occlusion = camera.screenSpaceAmbientOcclusionIntensity;
                        camera.screenSpaceAmbientOcclusionIntensity = 0;
                        capture("service-overhead-no-occlusion");
                        camera.screenSpaceAmbientOcclusionIntensity = occlusion;
                        var pigment = dirtWorld.scene.rootNode.childNode(withName: "Track clay mixed into service sand", recursively: false);
                        if (pigment != null) { pigment.isHidden = true; }
                        capture("service-overhead-no-clay");
                        if (pigment != null) { pigment.isHidden = false; }
                        var shadowLights = new List<SCNLight>();
                        dirtWorld.scene.rootNode.enumerateChildNodes((node, _) =>
                        {
                            if (node.light is SCNLight light && light.castsShadow) { shadowLights.Add(light); light.castsShadow = false; }
                        });
                        capture("service-overhead-no-shadows");
                        foreach (var light in shadowLights) { light.castsShadow = true; }
                    }
                }
                bool vestibuleClear = true; var vestibuleStep = 0.0;
                if (minBy(town.doorways, (a, b) => Simd.distance(a.center, new Double2(24.585, 3.8)) < Simd.distance(b.center, new Double2(24.585, 3.8)), out var door))
                {
                    door.opening = 1; door.place();
                    foreach (var profile in RobotCollisions.profiles)
                    {
                        Double3? previousPivot = null;
                        foreach (var offset in stride(0.9, -0.4, -0.025))
                        {
                            var p = door.center + door.outward * offset; var ground = DirtCourse.height(x: p.x, z: p.y);
                            var pivot = town.cameraPivot(position: new Double3(p.x, ground, p.y), chassisHeight: profile.height);
                            var current = new Double3((double)pivot.x, (double)pivot.y, (double)pivot.z);
                            if (previousPivot is Double3 previous) { vestibuleStep = max(vestibuleStep, Simd.distance(current, previous)); } previousPivot = current;
                            var probe = new RobotCollisions.Body(position: new Double3((double)pivot.x, (double)pivot.y - 0.08, (double)pivot.z), profile: new RobotCollisions.Profile(mass: 1, halfWidth: 0.08, halfDepth: 0.08, height: 0.16, round: true));
                            if (town.collisionWorld.nearby(probe).Any(o => o.profile.mass != 70 && RobotCollisions.contact(probe, o) != null)) { vestibuleClear = false; }
                        }
                    }
                    door.opening = 0; door.place();
                }
                else { vestibuleClear = false; }
                var duneEyes = new List<Double3>();
                foreach (var x in new[] { 192.60, 192.61 })
                {
                    var ground = DirtCourse.height(x: x, z: 180); var pivot = new SCNVector3(x, ground + 1.392, 180);
                    var desired = new SCNVector3(x - cos(0.15) * 9, ground + 0.4 + sin(0.15) * 9, 180);
                    SCNVector3 lifted = town.terrainCamera(from: pivot, to: desired), eye = town.clearCamera(from: pivot, to: lifted);
                    duneEyes.Add(new Double3((double)eye.x, (double)eye.y, (double)eye.z));
                }
                var duneCrestStep = Simd.distance(duneEyes[0], duneEyes[1]);
                var preflight = new Dictionary<string, object> { ["shoulderJoinStep"] = maxJoinStep, ["vestibuleClear"] = vestibuleClear, ["vestibuleStep"] = vestibuleStep, ["duneCrestStep"] = duneCrestStep };
                File.WriteAllText(Path.Combine(directory, "preflight.json"), JSONSerialization.prettyPrintedSortedKeys(preflight));
                if (CommandLine.arguments.Contains("--passage-preflight-only")) { return maxJoinStep < 0.025 && vestibuleClear && vestibuleStep < 0.06 && duneCrestStep < 0.1; }
                // Choose traversable routes with the least lateral camera clearance,
                // rather than the broadest entrance used by the earlier check.
                var ranked = new List<(double, List<Double2>)>();
                foreach (var entry in town.pedestrianAccess.Concat(town.venueAccess))
                {
                    var route = entry.route.Skip(2).Reverse().ToList();
                    if (!(route.Count > 2 && Simd.distance(route[0], route[route.Count - 1]) > 3)) { continue; }
                    bool valid = true; var score = 9.0;
                    foreach (var p in route)
                    {
                        var probe = new RobotCollisions.Body(position: new Double3(p.x, 0.02, p.y), profile: RobotCollisions.profiles[0]);
                        if (city.nearby(probe).Any(o => o.profile.mass != 70 && RobotCollisions.contact(probe, o) != null)) { valid = false; break; }
                        foreach (var angle in new[] { 0.0, Math.PI / 2, Math.PI, Math.PI * 1.5 })
                        {
                            SCNVector3 a = new SCNVector3(p.x, 0.45, p.y), b = new SCNVector3(p.x + sin(angle) * 3, 1.2, p.y + cos(angle) * 3);
                            var c = town.clearCamera(from: a, to: b);
                            score = min(score, hypot((double)(c.x - a.x), (double)(c.z - a.z)));
                        }
                    }
                    if (valid && score > 0.16 && score < 1.2) { ranked.Add((score, route)); }
                }
                ranked = sorted(ranked, (l, r) => l.Item1 < r.Item1).ToList();
                var routes = new List<List<Double2>>();
                foreach (var item in ranked)
                {
                    if (routes.All(r => Simd.distance(r[0], item.Item2[0]) > 12)) { routes.Add(item.Item2); }
                    if (routes.Count == 3) { break; }
                }
                var reports = new List<Dictionary<string, object>>();
                var passed = routes.Count == 3 && maxJoinStep < 0.025 && town.cameraRoom(at: new SCNVector3(400, 100, 400)) > 9 && duneCrestStep < 0.1 && vestibuleClear && vestibuleStep < 0.06;
                for (var routeIndex = 0; routeIndex < routes.Count; routeIndex++)
                {
                    var route = routes[routeIndex];
                    foreach (var hz in new[] { 30, 60, 120 })
                    {
                        foreach (var mode in new[] { 0, 1 })
                        {
                            var p = route[0]; var projection = DirtCourse.projection(x: p.x, z: p.y);
                            simulation = new Simulation(seed: 1, dirtTrack: true, dirtStartOffset: projection.offset, dirtStartPhase: projection.phase);
                            town.reset(); cameraMode = mode; cameraDistance = 3.5; orbitYaw = 0.65; orbitPitch = 0.5; freeCameraEye = null; cameraBoomFraction = 1;
                            updateCamera(snap: true);
                            int frames = 0, reached = 0, penetrations = 0, selfOcclusions = 0; double minBoom = 9.0, maxStep = 0.0; var last = simdF3(world.camera.simdPosition);
                            void step(DriveInput input)
                            {
                                var dt = 1.0 / (double)hz;
                                advanceRacePhysics(input, dt: dt, raceDT: dt);
                                updatePlayerModel();
                                var player = new RobotCollisions.Body(position: new Double3(simulation.x, simulation.groundY, simulation.z), heading: simulation.heading, profile: RobotCollisions.profiles[0]);
                                town.update(dt: dt, camera: world.camera.position, player: new Double2(simulation.x, simulation.z), robots: new List<RobotCollisions.Body> { player }, visible: node => view.isNode(node, insideFrustumOf: world.camera));
                                if (mode == 1) { orbitYaw += dt * 0.35; }
                                updateCamera(snap: false, dt: dt);
                                var eye = simdF3(world.camera.simdPosition);
                                var local = robot.root.convertPositionFrom(world.camera.position, null); var bounds = robot.root.boundingBox;
                                if (local.x > bounds.min.x && local.x < bounds.max.x && local.y > bounds.min.y && local.y < bounds.max.y && local.z > bounds.min.z && local.z < bounds.max.z) { selfOcclusions += 1; }
                                if (frames > 5)
                                {
                                    var jump = (double)Simd.distance(last, eye);
                                    if (jump > max(0.8, maxStep)) { print($"JUMP route {routeIndex} mode {mode} hz {hz} frame {frames} robot {description(simulation.x)},{description(simulation.z)} eye SIMD3<Float>({description(eye.x)}, {description(eye.y)}, {description(eye.z)}) old SIMD3<Float>({description(last.x)}, {description(last.y)}, {description(last.z)}) fraction {description(cameraBoomFraction)}"); }
                                    maxStep = max(maxStep, jump);
                                }
                                last = eye;
                                var pivot = new Double3(simulation.x, simulation.groundY + 0.45, simulation.z);
                                minBoom = min(minBoom, Simd.distance(new Double3((double)eye.x, (double)eye.y, (double)eye.z), pivot));
                                var probe = new RobotCollisions.Body(position: new Double3((double)eye.x, (double)eye.y - 0.08, (double)eye.z), profile: new RobotCollisions.Profile(mass: 1, halfWidth: 0.08, halfDepth: 0.08, height: 0.16, round: true));
                                if (town.collisionWorld.nearby(probe).Any(o => o.profile.mass != 70 && (RobotCollisions.contact(probe, o)?.penetration ?? 0) > 0.002))
                                {
                                    if (penetrations < 2) { print($"CAMERA HIT {probe.position} solids {city.nearby(probe).Count(o => o.profile.mass != 70 && (RobotCollisions.contact(probe, o)?.penetration ?? 0) > 0.002)}"); }
                                    penetrations += 1;
                                }
                                if (hz == 60 && frames % (hz * 2) == 0 && frames < hz * 18) { capture($"passage-{routeIndex}-mode-{mode}-{frames / hz}"); }
                                frames += 1;
                            }
                            foreach (var target in route.Skip(1).Concat(Enumerable.Reverse(route).Skip(1)).ToList())
                            {
                                var arrived = false;
                                for (var i = 0; i < hz * 16; i++)
                                {
                                    var d = target - new Double2(simulation.x, simulation.z); var distance = Simd.length(d);
                                    if (distance < 0.24) { arrived = true; reached += 1; break; }
                                    var error = atan2(sin(atan2(d.x, d.y) - simulation.heading), cos(atan2(d.x, d.y) - simulation.heading));
                                    var input = new DriveInput(); input.turn = max(-1, min(1, -error * 2.5)); input.throttle = abs(error) < 0.2 ? min(0.22, distance * 0.5) : 0;
                                    step(input);
                                }
                                if (!arrived) { break; }
                            }
                            for (var i = 0; i < hz; i++) { step(new DriveInput()); }
                            var success = reached == 2 * (route.Count - 1) && penetrations == 0 && selfOcclusions == 0 && minBoom > 0.10 && maxStep < (double)(12.0 / (double)hz);
                            passed = passed && success;
                            reports.Add(new Dictionary<string, object>
                            {
                                ["route"] = routeIndex, ["hz"] = hz, ["mode"] = mode, ["passed"] = success, ["reached"] = reached, ["expected"] = 2 * (route.Count - 1), ["cameraPenetrations"] = penetrations,
                                ["cameraInsideRobot"] = selfOcclusions, ["cameraPeakMetresPerSecond"] = maxStep * (double)hz, ["minBoom"] = minBoom, ["maxFrameStep"] = maxStep, ["seconds"] = (double)frames / (double)hz,
                            });
                            print("[" + string.Join(", ", reports[reports.Count - 1].Select(kv => $"\"{kv.Key}\": {(kv.Value is double v ? description(v) : kv.Value is bool b ? (b ? "true" : "false") : Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture))}")) + "]");
                        }
                    }
                }
                var report = new Dictionary<string, object>
                {
                    ["passed"] = passed, ["vestibulePivotStepFor25mmMovement"] = vestibuleStep, ["lowVestibuleCameraClearAllChassis"] = vestibuleClear, ["duneCrestStepFor1cmMovement"] = duneCrestStep,
                    ["maximumShoulderJoinStepAt5mm"] = maxJoinStep, ["routes"] = routes.Select(r => r.Select(q => new List<double> { q.x, q.y }).ToList()).ToList(), ["camera"] = reports,
                };
                File.WriteAllText(Path.Combine(directory, "passages.json"), JSONSerialization.prettyPrintedSortedKeys(report));
                return passed;
            }
            finally { weatherOverride = null; }
        }
        catch (Exception error) { print($"Passage check: {error}"); return false; }
    }

    [GameMode("--passage-smoke-test")]
    public static async System.Threading.Tasks.Task RunPassageSmokeTest(string dir, Godot.SceneTree tree)
    {
        var app = await launchSmoke(tree, dir);
        var passed = app.checkPassages(at: dir);
        print($"Passages: {(passed ? "PASS" : "FAIL")}"); exit(passed ? 0 : 1);
    }
}
