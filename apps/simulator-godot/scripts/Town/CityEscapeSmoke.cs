using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

// Port of CityEscapeSmoke.swift (an AppController extension and the file's private checkTownImpacts).
// Random state, as on macOS: startDirtTrack() resets with a random starting grid and daylight. The player is respawned
// at the city exit, but the rivals keep their random grid slots and race on; MARVIN_GRID_SLOTS / MARVIN_DAYLIGHT_*
// (TestPins) pin them for a 1:1 comparison with a macOS run.
public partial class AppController
{
    /// Drives ordinary control input through the live coupled simulation. Only
    /// the initial spawn is chosen; no route step changes Marvin's pose directly.
    public bool checkCityEscape(string at)
    {
        var directory = at;
        try
        {
            Directory.CreateDirectory(directory);
            startDirtTrack(); dirtIntro = null; race.countDown(dt: 3); raceHUD.isHidden = true;
            simulation = new Simulation(dirtTrack: true, dirtStartPhase: CityExit.phase);
            race = new DirtRace(startPhase: CityExit.phase); race.countDown(dt: 3);
            var city = dirtWorld.town.collisionWorld;
            var checks = new Dictionary<string, bool> { ["townImpactAndReverseAllChassis"] = checkTownImpacts(city) };
            double travel = 0.0, maxPenetration = 0.0; var frames = 0; var motionValid = true;
            void key(bool repeated = false)
            {
                var @event = NSEvent.keyEvent(NSEvent.EventType.keyDown, NSPoint.zero, 0, ProcessInfo.processInfo.systemUptime, window.windowNumber, null, "g", "g", repeated, 5);
                view.keyDown(@event); view.keyUp(@event);
            }
            void step(DriveInput? given = null)
            {
                var input = given ?? new DriveInput();
                var old = new Double2(simulation.x, simulation.z);
                advanceRacePhysics(input, dt: 1.0 / 60, raceDT: 1.0 / 60);
                travel += Simd.distance(old, new Double2(simulation.x, simulation.z)); frames += 1;
                motionValid = motionValid && double.IsFinite(simulation.x) && double.IsFinite(simulation.z) && simulation.groundY >= DirtCourse.height(x: simulation.x, z: simulation.z) - 0.005;
                var body = new RobotCollisions.Body(position: new Double3(simulation.x, simulation.groundY, simulation.z), heading: simulation.heading, profile: RobotCollisions.profiles[0]);
                foreach (var obstacle in city.nearby(body).Concat(new[] { racePhysics.gate.body() }).Concat(CityExit.posts))
                {
                    if (RobotCollisions.contact(body, obstacle) is RobotCollisions.Contact c) { maxPenetration = max(maxPenetration, c.penetration); }
                }
            }
            void shot(string name, Double3 eye, Double3 target)
            {
                updateOpponents(); updateRaceWorld(dt: 1.0 / 60);
                world.camera.position = new SCNVector3(eye.x, eye.y, eye.z);
                world.camera.look(at: new SCNVector3(target.x, target.y, target.z), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                dirtWorld.town.update(dt: 0, camera: world.camera.position, player: new Double2(simulation.x, simulation.z));
                _ = view.prepare(dirtWorld.scene, shouldAbortBlock: null);
                saveTownFrame(name, directory);
            }
            void exitShot(string name)
            {
                Double2 eye = CityExit.point(5, 7), target = CityExit.point(0, 0);
                shot(name, new Double3(eye.x, 3.2, eye.y), new Double3(target.x, 0.55, target.y));
            }
            exitShot("escape-gate-closed");
            key(); checks["hiddenGOpens"] = racePhysics.gate.wantsOpen;
            key(true); checks["repeatIgnored"] = racePhysics.gate.wantsOpen;
            simulation.paused = true;
            var angle = racePhysics.gate.angle;
            for (var i = 0; i < 60; i++) { step(); }
            checks["pausedGateStationary"] = racePhysics.gate.angle == angle;
            simulation.paused = false;
            for (var i = 0; i < 150; i++) { step(); }
            exitShot("escape-gate-opening");
            for (var i = 0; i < 450; i++) { step(); }
            checks["fullyOpen"] = abs(racePhysics.gate.angle - 100 * Math.PI / 180) < 0.001;
            bool drive(Double2 target)
            {
                for (var i = 0; i < 2400; i++)
                {
                    var d = target - new Double2(simulation.x, simulation.z); var distance = Simd.length(d);
                    if (distance < 0.22) { return true; }
                    var error = atan2(sin(atan2(d.x, d.y) - simulation.heading), cos(atan2(d.x, d.y) - simulation.heading));
                    var input = new DriveInput();
                    input.turn = max(-1, min(1, -error * 2.5));
                    // Slow down before changing direction; let finite steering
                    // torque turn the tracks in place instead of cutting corners.
                    input.throttle = abs(error) < 0.22 ? min(0.5, distance * 0.7) : 0;
                    step(input);
                }
                print($"Escape route stuck at {description(simulation.x)},{description(simulation.z)}, target SIMD2<Double>({description(target.x)}, {description(target.y)})");
                return false;
            }
            var apron = CityExit.point(0, 8);
            var crossed = drive(CityExit.point(0, -1.1)) && drive(CityExit.point(0, 2.5));
            exitShot("escape-crossing");
            checks["crossedRamp"] = crossed && drive(apron);
            exitShot("escape-ramp-open");
            // Find a navigable town walk, respecting actual generated solids.
            var planner = new TownEscapeRoute(city: city, origin: apron);
            var destinations = new[] { new Double2(29, -10), new Double2(33, 3), new Double2(35, 14), new Double2(36, 28) };
            var routes = new List<Double2[]>(); var current = apron; var roamed = true;
            foreach (var destination in destinations)
            {
                if (!(planner.route(current, destination) is Double2[] route)) { roamed = false; break; }
                routes.Add(route);
                foreach (var point in route) { if (!drive(point)) { roamed = false; break; } }
                if (!roamed) { break; }
                current = new Double2(simulation.x, simulation.z);
                var index = routes.Count;
                shot($"escape-town-{index}", new Double3(current.x - 5, 3.5, current.y - 5), new Double3(current.x, 0.6, current.y));
            }
            checks["roamedTown"] = roamed && routes.Count == destinations.Length;
            Double2 p = new Double2(simulation.x, simulation.z), forward = new Double2(sin(simulation.heading), cos(simulation.heading));
            shot("escape-town-pov", new Double3(p.x + forward.x * 0.35, simulation.groundY + 0.46, p.y + forward.y * 0.35), new Double3(p.x + forward.x * 15, simulation.groundY + 0.46, p.y + forward.y * 15));
            shot("escape-birds-eye", new Double3(30, 42, -22), new Double3(18, 0, 4));
            var returned = roamed;
            foreach (var route in Enumerable.Reverse(routes))
            {
                foreach (var point in Enumerable.Reverse(route)) { if (!drive(point)) { returned = false; break; } }
                if (!returned) { break; }
            }
            if (returned) { returned = drive(apron) && drive(CityExit.point(0, 2)) && drive(CityExit.point(0, -1.25)); }
            checks["returnedToTrack"] = returned;
            print("Rival positions: [" + string.Join(", ", opponents.Select(o =>
            {
                var local = CityExit.local(new Double2(o.simulation.x, o.simulation.z));
                return description(new[] { o.simulation.x, o.simulation.z, o.simulation.heading, local.x, local.y });
            })) + "]");
            checks["rivalsRemainInRace"] = opponents.All(o =>
            {
                var projection = DirtCourse.projection(x: o.simulation.x, z: o.simulation.z);
                return projection.offset < 0 || projection.distance < DirtCourse.fenceOffset;
            });
            checks["noSolidPenetration"] = maxPenetration < 0.005;
            checks["finiteGroundedMotion"] = motionValid;
            key(); for (var i = 0; i < 600; i++) { step(); }
            checks["hiddenGCloses"] = racePhysics.gate.angle < 0.001;
            reset(null); checks["resetClosesGate"] = racePhysics.gate.angle == 0 && !racePhysics.gate.wantsOpen;
            var passed = checks.Values.All(v => v);
            var report = new Dictionary<string, object>
            {
                ["passed"] = passed, ["checks"] = checks, ["distanceMetres"] = travel, ["simulatedSeconds"] = (double)frames / 60, ["maximumSolidPenetrationMetres"] = maxPenetration,
                ["cityCollisionSolids"] = city.bodies.Length, ["routes"] = routes.Select(r => r.Select(q => new List<double> { q.x, q.y }).ToList()).ToList(),
            };
            File.WriteAllText(Path.Combine(directory, "city-escape.json"), JSONSerialization.prettyPrintedSortedKeys(report));
            print("[" + string.Join(", ", checks.Select(c => $"\"{c.Key}\": {(c.Value ? "true" : "false")}")) + "]");
            return passed;
        }
        catch (Exception error) { print($"City escape smoke: {error}"); return false; }
    }

    /// Boosted impacts and reversing against generated town geometry, independent
    /// of the path planner (which deliberately avoids collisions).
    private static bool checkTownImpacts(CityCollisionWorld city)
    {
        foreach (var profile in RobotCollisions.profiles)
        {
            var tested = 0;
            foreach (var obstacle in city.bodies.Where(o => o.position.y < 0.1 && o.profile.height > 1 && o.profile.halfWidth > 0.8))
            {
                var side = new Double2(cos(obstacle.heading), -sin(obstacle.heading));
                var p = new Double2(obstacle.position.x, obstacle.position.z) + side * (obstacle.profile.halfWidth + hypot(profile.halfWidth, profile.halfDepth) + 0.7);
                var projection = DirtCourse.projection(x: p.x, z: p.y);
                if (!(projection.offset > 0 && projection.distance > DirtCourse.fenceOffset + 2)) { continue; }
                var bodies = new[] { new RobotCollisions.Body(position: new Double3(p.x, DirtCourse.height(x: p.x, z: p.y), p.y), heading: atan2(-side.x, -side.y), profile: profile) };
                if (!city.nearby(bodies[0]).All(o => RobotCollisions.contact(bodies[0], o) == null)) { continue; }
                var hit = false;
                for (var i = 0; i < 100; i++)
                {
                    var previous = bodies[0].position;
                    bodies[0].velocity = new Double3(-side.x * 12, 0, -side.y * 12);
                    bodies[0].position += bodies[0].velocity / 240;
                    _ = RobotCollisions.resolve(bodies, terrain: true, gate: new CityGate(), city: city, previousPositions: new[] { previous });
                    hit = hit || bodies[0].contacted;
                    if (city.nearby(bodies[0]).Any(o => (RobotCollisions.contact(bodies[0], o)?.penetration ?? 0) > 0.005)) { return false; }
                }
                if (!hit) { continue; }
                var stopped = bodies[0].position;
                for (var i = 0; i < 100; i++)
                {
                    var previous = bodies[0].position;
                    bodies[0].velocity = new Double3(side.x * 3, 0, side.y * 3);
                    bodies[0].position += bodies[0].velocity / 240;
                    _ = RobotCollisions.resolve(bodies, terrain: true, gate: new CityGate(), city: city, previousPositions: new[] { previous });
                }
                if (Simd.length(bodies[0].position - stopped) < 0.5) { return false; }
                tested += 1;
                if (tested == 12) { break; }
            }
            if (tested < 12) { return false; }
        }
        return true;
    }

    [GameMode("--city-escape-smoke-test")]
    public static async System.Threading.Tasks.Task RunCityEscapeSmokeTest(string dir, Godot.SceneTree tree)
    {
        var app = await launchSmoke(tree, dir);
        var passed = app.checkCityEscape(at: dir);
        print($"City escape: {(passed ? "PASS" : "FAIL")} · {dir}");
        exit(passed ? 0 : 1);
    }
}
