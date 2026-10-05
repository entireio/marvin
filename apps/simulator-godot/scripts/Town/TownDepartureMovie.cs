using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

// Port of TownDepartureMovie.swift (an AppController extension, `--town-departure-movie DIR`). As on macOS the
// daylight is the random one of startDirtTrack()'s reset (pin it with MARVIN_DAYLIGHT_FRACTION/PHASE, TestPins);
// MARVIN_DEPARTURE_CLOSEUP=1 selects the close camera.
public partial class AppController
{
    /// Offline capture of ordinary driving physics, at 30 fps with two 60 Hz
    /// simulation steps per image. The only placed pose is the initial spawn.
    /// The drive continues over the dune heightfield beyond the last houses.
    public bool captureTownDeparture(string at)
    {
        var directory = at;
        try
        {
            Directory.CreateDirectory(directory);
            startDirtTrack(); dirtIntro = null; race.countDown(dt: 3); raceHUD.isHidden = true;
            var spawn = new Double2(142, 50);
            var projection = DirtCourse.projection(x: spawn.x, z: spawn.y);
            simulation = new Simulation(dirtTrack: true, dirtStartOffset: projection.distance,
                dirtStartPhase: DirtCourse.phase(x: spawn.x, z: spawn.y));
            Double2 start = new Double2(simulation.x, simulation.z), goal = new Double2(207, 65);
            var city = dirtWorld.town.collisionWorld;
            var route = new List<Double2> { start }; var cursor = start;
            foreach (var destination in new[] { new Double2(170, 56), goal })
            {
                if (!(new TownEscapeRoute(city: city, origin: cursor).route(cursor, destination) is Double2[] leg))
                {
                    print($"No safe departure route from SIMD2<Double>({description(cursor.x)}, {description(cursor.y)})"); return false;
                }
                route.AddRange(leg.Skip(1)); cursor = leg[leg.Length - 1];
            }
            var renderer = new SCNRenderer(view.device, null);
            renderer.scene = dirtWorld.scene; renderer.pointOfView = world.camera;
            int waypoint = 1, frame = 0; double distance = 0.0, maxPenetration = 0.0; var arrived = false;
            var camera = new Double3(start.x + 6, 2.9, start.y + 1.3);
            double minimumHeight = double.PositiveInfinity, maximumHeight = double.NegativeInfinity, maximumPitch = 0.0, maximumGroundPenetration = 0.0;
            // Settle the heading before the shot starts, using actual controls.
            for (var i = 0; i < 300; i++)
            {
                var d = route[1] - new Double2(simulation.x, simulation.z);
                var error = atan2(sin(atan2(d.x, d.y) - simulation.heading), cos(atan2(d.x, d.y) - simulation.heading));
                var input = new DriveInput(); input.turn = max(-1, min(1, -error * 2.5));
                advanceRacePhysics(input, dt: 1.0 / 60, raceDT: 1.0 / 60);
            }
            var closeup = Environment.GetEnvironmentVariable("MARVIN_DEPARTURE_CLOSEUP") == "1";
            while (frame < 2700)
            {
                for (var substep = 0; substep < 2; substep++)
                {
                    var position = new Double2(simulation.x, simulation.z);
                    if (Simd.distance(position, route[waypoint]) < 0.24)
                    {
                        if (waypoint + 1 < route.Count) { waypoint += 1; } else { arrived = true; }
                    }
                    var delta = route[waypoint] - position;
                    var error = atan2(sin(atan2(delta.x, delta.y) - simulation.heading), cos(atan2(delta.x, delta.y) - simulation.heading));
                    var input = new DriveInput();
                    input.turn = max(-1, min(1, -error * 2.5));
                    input.throttle = !arrived && abs(error) < 0.22 ? min(0.65, Simd.length(delta) * 0.7) : 0;
                    input.brake = arrived;
                    advanceRacePhysics(input, dt: 1.0 / 60, raceDT: 1.0 / 60);
                    distance += Simd.distance(position, new Double2(simulation.x, simulation.z));
                    minimumHeight = min(minimumHeight, simulation.groundY); maximumHeight = max(maximumHeight, simulation.groundY);
                    maximumPitch = max(maximumPitch, abs(simulation.bodyPitch));
                    maximumGroundPenetration = max(maximumGroundPenetration, DirtCourse.height(x: simulation.x, z: simulation.z) - simulation.groundY);
                    var body = new RobotCollisions.Body(position: new Double3(simulation.x, simulation.groundY, simulation.z), heading: simulation.heading, profile: RobotCollisions.profiles[0]);
                    foreach (var obstacle in city.nearby(body))
                    {
                        if (RobotCollisions.contact(body, obstacle) is RobotCollisions.Contact contact) { maxPenetration = max(maxPenetration, contact.penetration); }
                    }
                    updateOpponents(); updateRaceWorld(dt: 1.0 / 60);
                }
                var target = new Double3(simulation.x, simulation.groundY + 0.4, simulation.z);
                // Ease around to a rear quarter view once clear of the town,
                // revealing the dune field ahead while keeping Marvin visible.
                var orbit = max(0, min(1, (simulation.x - 155) / 20)) * Math.PI * 0.85;
                var desired = target + (closeup ? new Double3(2.6 * cos(orbit), 1.05, 2.6 * sin(orbit) + 0.55) : new Double3(6 * cos(orbit), 2.5, 6 * sin(orbit) + 1.3));
                camera += (desired - camera) * 0.08;
                camera.y = max(camera.y, DirtCourse.height(x: camera.x, z: camera.z) + 1.2);
                world.camera.position = dirtWorld.town.clearCamera(from: new SCNVector3(target.x, target.y, target.z), to: new SCNVector3(camera.x, camera.y, camera.z));
                world.camera.look(at: new SCNVector3(target.x, target.y, target.z), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                dirtWorld.town.update(dt: 1.0 / 30, camera: world.camera.position, player: new Double2(simulation.x, simulation.z));
                var image = renderer.snapshot((double)frame / 30, new CGSize(1280, 720), SCNAntialiasingMode.multisampling4X);
                var jpeg = NSBitmapImageRep.data(image.tiffRepresentation)?.representation(NSBitmapImageFileType.jpeg, new() { [NSBitmapImageRep.PropertyKey.compressionFactor] = 0.92 }) ?? throw new IOException("fileWriteUnknown");
                File.WriteAllBytes(Path.Combine(directory, format("frame-%05d.jpg", frame)), jpeg);
                frame += 1;
                if (frame % 150 == 0) { print($"Departure frame {frame}, position {description(simulation.x)}, {description(simulation.z)}"); }
                if (arrived) { break; }
            }
            var report = new Dictionary<string, object>
            {
                ["arrived"] = arrived, ["frames"] = frame, ["fps"] = 30, ["distanceMetres"] = distance, ["maximumPenetrationMetres"] = maxPenetration, ["terrain"] = "Shared rendered/collision dune heightfield",
                ["minimumHeight"] = minimumHeight, ["maximumHeight"] = maximumHeight, ["maximumPitchDegrees"] = maximumPitch * 180 / Math.PI, ["maximumGroundPenetrationMetres"] = maximumGroundPenetration,
                ["start"] = new List<double> { start.x, start.y }, ["end"] = new List<double> { simulation.x, simulation.z },
            };
            File.WriteAllText(Path.Combine(directory, "departure.json"), JSONSerialization.prettyPrintedSortedKeys(report));
            print(JSONSerialization.prettyPrintedSortedKeys(report));
            // A separate overview makes the terrain extent and winding crests reviewable.
            world.camera.position = new SCNVector3(212, 52, 105);
            world.camera.look(at: new SCNVector3(205, 3, 48), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
            saveTownFrame("dunes-overview", directory);
            return arrived && maxPenetration < 0.005 && maximumGroundPenetration < 0.005 && maximumHeight - minimumHeight > 2;
        }
        catch (Exception error) { print($"Departure capture: {error}"); return false; }
    }

    [GameMode("--town-departure-movie")]
    public static async System.Threading.Tasks.Task RunTownDepartureMovie(string dir, Godot.SceneTree tree)
    {
        var app = await launchSmoke(tree, dir);
        var passed = app.captureTownDeparture(at: dir);
        exit(passed ? 0 : 1);
    }
}
