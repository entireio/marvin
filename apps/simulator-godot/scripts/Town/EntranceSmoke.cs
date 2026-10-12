using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

// Port of EntranceSmoke.swift (an AppController extension). The captures use the deterministic survey daylight
// (MARVIN_SURVEY_DAYLIGHT, default 0.48, phase 1.2), so their lighting compares 1:1 with the macOS captures.
public partial class AppController
{
    private static string describe(IEnumerable<Double3> values) => "[" + string.Join(", ", values) + "]";

    public bool checkTownEntrances(string at)
    {
        var directory = at;
        try
        {
            Directory.CreateDirectory(directory);
            weatherOverride = false;
            try
            {
                startDirtTrack(); dirtIntro = null; setRaceControlsHidden(true);
                var surveyDaylight = Environment.GetEnvironmentVariable("MARVIN_SURVEY_DAYLIGHT");
                dirtWorld.sky.apply(new BinaryDaylight(fraction: surveyDaylight != null && double.TryParse(surveyDaylight, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0.48, phase: 1.2));
                TownWorld town = dirtWorld.town; var entries = town.entrances; var city = town.collisionWorld;
                // Moving people can temporarily cross a threshold. Audit permanent
                // obstructions here; PeopleSmoke separately sweeps moving bodies.
                var transient = new HashSet<Double2>((town.streetResidents?.walkers.Select(w => w.position) ?? Enumerable.Empty<Double2>())
                    .Concat(town.residents?.walkers.Where(w => !w.node.isHidden).Select(w => w.position) ?? Enumerable.Empty<Double2>()));
                var blocked = new List<List<double>>();
                foreach (var e in entries)
                {
                    var @out = new Double2(sin(e.yaw), cos(e.yaw));
                    foreach (var distance in new[] { 0.42, 0.55, 0.65 })
                    {
                        var p = e.center + @out * distance;
                        var body = new RobotCollisions.Body(position: new Double3(p.x, 0.02, p.y), profile: new RobotCollisions.Profile(mass: 70, halfWidth: 0.17, halfDepth: 0.17, height: 1.05, round: true));
                        if (city.nearby(body).Any(obstacle =>
                        {
                            var moving = obstacle.profile.mass == 70 && transient.Contains(new Double2(obstacle.position.x, obstacle.position.z));
                            return !moving && RobotCollisions.contact(body, obstacle) != null;
                        }))
                        {
                            Godot.GD.Print($"Entrance blocker at {e.center}: [{string.Join(", ", city.nearby(body).Where(o => RobotCollisions.contact(body, o) != null).Select(o => $"Body(position: {o.position}, heading: {description(o.heading)}, mass: {description(o.profile.mass)})"))}]");
                            blocked.Add(new List<double> { e.center.x, e.center.y, e.yaw, distance, e.walkable ? 1 : 0 }); break;
                        }
                    }
                }
                var unsupportedWindows = town.windowSupports.Where(point =>
                {
                    var probe = new RobotCollisions.Body(position: point, profile: new RobotCollisions.Profile(mass: 1, halfWidth: 0.025, halfDepth: 0.025, height: 0.05, round: true));
                    return !city.nearby(probe).Any(body =>
                        body.profile.mass != 70 && body.profile.halfWidth > 0.2 && body.profile.halfDepth > 0.2 && RobotCollisions.contact(probe, body) != null);
                }).ToList();
                Godot.GD.Print($"Window supports: {town.windowSupports.Count}, unsupported {describe(unsupportedWindows)}");
                if (unsupportedWindows.Count != 0) { return false; }
                var blockedRoutes = new List<List<double>>(); var routeSamples = 0;
                foreach (var access in town.pedestrianAccess.Concat(town.venueAccess))
                {
                    foreach (var p in access.route)
                    {
                        routeSamples += 1;
                        var body = new RobotCollisions.Body(position: new Double3(p.x, 0.02, p.y), profile: new RobotCollisions.Profile(mass: 70, halfWidth: 0.22, halfDepth: 0.22, height: 1.45, round: true));
                        if (city.nearby(body).Any(o => o.profile.mass != 70 && RobotCollisions.contact(body, o) != null))
                        {
                            blockedRoutes.Add(new List<double> { access.building.x, access.building.y, p.x, p.y }); break;
                        }
                    }
                }
                var accessReport = new Dictionary<string, object> { ["compounds"] = town.lots.Count, ["connected"] = town.pedestrianAccess.Count, ["missing"] = town.inaccessibleBuildings.Select(m => new List<double> { m.x, m.y }).ToList(), ["blockedRoutes"] = blockedRoutes, ["samples"] = routeSamples, ["connectedVenues"] = town.venueAccess.Count };
                File.WriteAllText(Path.Combine(directory, "pedestrian-access.json"), JSONSerialization.prettyPrintedSortedKeys(accessReport));
                Godot.GD.Print($"All-compound access audit: [\"blockedRoutes\": [{string.Join(", ", blockedRoutes.Select(r => description(r)))}], \"samples\": {routeSamples}, \"connectedVenues\": {town.venueAccess.Count}, \"connected\": {town.pedestrianAccess.Count}, \"missing\": [{string.Join(", ", town.inaccessibleBuildings.Select(m => description(new[] { m.x, m.y })))}], \"compounds\": {town.lots.Count}]");
                if (world.camera.camera is SCNCamera camera)
                {
                    bool orthographic = camera.usesOrthographicProjection; double scale = camera.orthographicScale, far = camera.zFar;
                    double fogStart = dirtWorld.scene.fogStartDistance, fogEnd = dirtWorld.scene.fogEndDistance;
                    camera.usesOrthographicProjection = true; camera.orthographicScale = 178; camera.zFar = 900;
                    dirtWorld.scene.fogStartDistance = 600; dirtWorld.scene.fogEndDistance = 800;
                    world.camera.position = new SCNVector3(0, 400, 0);
                    world.camera.look(at: SCNVector3Zero, up: new SCNVector3(0, 0, -1), localFront: new SCNVector3(0, 0, -1));
                    town.update(dt: 0, camera: world.camera.position, player: new Double2(0, 0));
                    saveTownFrame("town-plan", directory);
                    camera.orthographicScale = 34;
                    world.camera.position = new SCNVector3(130, 110, 49);
                    world.camera.look(at: new SCNVector3(130, 0, 49), up: new SCNVector3(0, 0, -1), localFront: new SCNVector3(0, 0, -1));
                    dirtWorld.sky.updateShadowCenter(new Double3(130, 0, 49));
                    town.update(dt: 0, camera: world.camera.position, player: new Double2(130, 49));
                    saveTownFrame("road-into-sand", directory);
                    camera.usesOrthographicProjection = orthographic; camera.orthographicScale = scale; camera.zFar = far;
                    dirtWorld.scene.fogStartDistance = fogStart; dirtWorld.scene.fogEndDistance = fogEnd;
                }
                var districtPoints = new List<(string, Double2)> { ("south-grandstands", new Double2(0.0, -41.0)), ("east", new Double2(77, 15)), ("northeast", new Double2(72, 78)), ("north", new Double2(0, 82)), ("northwest", new Double2(-75, 73)), ("west", new Double2(-86, 1)), ("southwest", new Double2(-69, -68)), ("south", new Double2(4, -89)), ("southeast", new Double2(77, -78)) };
                foreach (var (name, p) in districtPoints)
                {
                    world.camera.position = new SCNVector3(p.x, 26, p.y - 23);
                    world.camera.look(at: new SCNVector3(p.x, 0, p.y), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                    dirtWorld.sky.updateShadowCenter(new Double3(p.x, 0, p.y));
                    town.update(dt: 0, camera: world.camera.position, player: p);
                    saveTownFrame("district-" + name, directory);
                }
                // Match the low oblique establishing angle of the film references,
                // alongside the high district and full settlement audits.
                world.camera.position = new SCNVector3(0, 16, -65);
                world.camera.look(at: new SCNVector3(0, 3, 20), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                dirtWorld.sky.updateShadowCenter(new Double3(0, 0, -25));
                town.update(dt: 0, camera: world.camera.position, player: new Double2(0, -25));
                saveTownFrame("city-oblique", directory);
                foreach (var (i, p) in town.householdYards.Take(6).Select((p, i) => (i, p)))
                {
                    if (!minBy(town.entrances, (a, b) => Simd.distance(a.center, p) < Simd.distance(b.center, p), out var entry)) { continue; }
                    var @out = new Double2(sin(entry.yaw), cos(entry.yaw)); var eye = p + @out * 4.5;
                    world.camera.position = new SCNVector3(eye.x, 3.2, eye.y);
                    world.camera.look(at: new SCNVector3(p.x, 0.8, p.y), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                    dirtWorld.sky.updateShadowCenter(new Double3(p.x, 0, p.y));
                    town.update(dt: 0, camera: world.camera.position, player: p);
                    saveTownFrame($"household-{i}", directory);
                }
                for (var i = 0; i < town.venueSites.Count; i++)
                {
                    var site = town.venueSites[i];
                    Double2 @out = new Double2(sin(site.yaw), cos(site.yaw)), side = new Double2(cos(site.yaw), -sin(site.yaw)), p = site.center + @out * 16 + side * 10;
                    world.camera.position = new SCNVector3(p.x, 9, p.y);
                    world.camera.look(at: new SCNVector3(site.center.x, 1, site.center.y), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                    dirtWorld.sky.updateShadowCenter(new Double3(site.center.x, 0, site.center.y));
                    town.update(dt: 0, camera: world.camera.position, player: site.center);
                    saveTownFrame($"venue-{i}", directory);
                    var street = site.center + @out * 14 + side * 2;
                    world.camera.position = new SCNVector3(street.x, 1.65, street.y);
                    world.camera.look(at: new SCNVector3(site.center.x, 1.8, site.center.y), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                    town.update(dt: 0, camera: world.camera.position, player: street);
                    saveTownFrame($"venue-pov-{i}", directory);
                    var close = site.center + @out * (i == 2 ? 1.8 : 5.2) + side * (-1.5);
                    var target = site.center + @out * (-1.8) + side * (i == 0 ? 4.4 : -5.0);
                    world.camera.position = new SCNVector3(close.x, 1.15, close.y);
                    world.camera.look(at: new SCNVector3(target.x, 1.15, target.y), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                    town.update(dt: 0, camera: world.camera.position, player: close);
                    saveTownFrame($"venue-detail-{i}", directory);
                }
                foreach (var (i, target) in new[] { new Double2(0.0, -41.0), new Double2(77, 15), new Double2(-69, -68) }.Select((t, i) => (i, t)))
                {
                    if (!(minBy(town.pedestrianAccess, (a, b) => Simd.distance(a.building, target) < Simd.distance(b.building, target), out var access) && access.route.Count > 5)) { continue; }
                    Double2 p = access.route[min(5, access.route.Count - 1)], aim = access.route[min(20, access.route.Count - 1)];
                    world.camera.position = new SCNVector3(p.x, 1.65, p.y);
                    world.camera.look(at: new SCNVector3(aim.x, 1.2, aim.y), up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                    dirtWorld.sky.updateShadowCenter(new Double3(p.x, 0, p.y));
                    town.update(dt: 0, camera: world.camera.position, player: p);
                    saveTownFrame($"alley-{i}", directory);
                }
                for (var variant = 0; variant < 5; variant++)
                {
                    var candidates = entries.Where(e => e.variant == variant && !e.walkable && max(abs(e.center.x), abs(e.center.y)) < 48).ToList();
                    double room(TownWorld.Entrance e)
                    {
                        var @out = new Double2(sin(e.yaw), cos(e.yaw)); SCNVector3 a = new SCNVector3(e.center.x + @out.x * 0.4, 0.85, e.center.y + @out.y * 0.4), b = new SCNVector3(e.center.x + @out.x * 3.8, 1.75, e.center.y + @out.y * 3.8);
                        var clear = town.clearCamera(from: a, to: b);
                        return hypot((double)(clear.x - a.x), (double)(clear.z - a.z));
                    }
                    if (!maxBy(candidates, (a, b) => room(a) < room(b), out var e)) { return false; }
                    Double2 entranceOut = new Double2(sin(e.yaw), cos(e.yaw)), side = new Double2(entranceOut.y, -entranceOut.x);
                    Double2 p = e.center + entranceOut * 3.8 + side * 1.2, target = e.center + entranceOut * 0.4;
                    var aim = new SCNVector3(target.x, 0.85, target.y);
                    world.camera.position = town.clearCamera(from: aim, to: new SCNVector3(p.x, 1.75, p.y));
                    world.camera.look(at: aim, up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                    dirtWorld.sky.updateShadowCenter(new Double3(p.x, 0, p.y));
                    town.update(dt: 0, camera: world.camera.position, player: p);
                    saveTownFrame($"entrance-{variant}", directory);
                }
                var activities = town.streetActivities;
                var conversations = activities.Where(a => a.role == "conversation").GroupBy(a => a.group).ToDictionary(g => g.Key, g => g.ToList());
                var completeGroups = conversations.Values.All(group =>
                    group.Count >= 2 && group.Count <= 3 && group.All(person =>
                        group.Any(other => other.index != person.index && Simd.distance(other.position, person.position) < 1.1)));
                var roles = activities.GroupBy(a => a.role).ToDictionary(g => g.Key, g => g.Count());
                var activityClear = true;
                foreach (var person in activities)
                {
                    var p = person.position;
                    var body = new RobotCollisions.Body(position: new Double3(p.x, 0.02, p.y), profile: new RobotCollisions.Profile(mass: 70, halfWidth: 0.20, halfDepth: 0.20, height: 1.45, round: true));
                    if (city.nearby(body).Any(obstacle =>
                    {
                        var other = new Double2(obstacle.position.x, obstacle.position.z);
                        return Simd.distance(p, other) > 0.001 && !transient.Contains(other) && RobotCollisions.contact(body, obstacle) != null;
                    })) { activityClear = false; }
                }
                town.setStorm(true);
                var sheltered = activities.All(person =>
                {
                    var body = new RobotCollisions.Body(position: new Double3(person.position.x, 0.02, person.position.y), profile: new RobotCollisions.Profile(mass: 70, halfWidth: 0.2, halfDepth: 0.2, height: 1.45, round: true));
                    return !town.collisionWorld.nearby(body).Any(o => o.profile.mass == 70 && Simd.distance(new Double2(o.position.x, o.position.z), person.position) < 0.001);
                });
                town.setStorm(false);
                foreach (var role in new[] { "conversation", "waiting at door", "market vendor" })
                {
                    var found = activities.FindIndex(a => a.role == role);
                    if (found < 0) { continue; }
                    var person = activities[found];
                    var target = (person.position + person.target) / 2;
                    var aim = new SCNVector3(target.x, 0.7, target.y);
                    var side = new Double2(cos(person.yaw), -sin(person.yaw));
                    var p = target + side * 3.8 + new Double2(sin(person.yaw), cos(person.yaw)) * 1.4;
                    world.camera.position = town.clearCamera(from: aim, to: new SCNVector3(p.x, 1.9, p.y));
                    world.camera.look(at: aim, up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
                    town.update(dt: 1, camera: world.camera.position, player: p);
                    saveTownFrame(role.Replace(" ", "-"), directory);
                }
                var activityReport = new Dictionary<string, object> { ["roles"] = roles, ["conversationGroups"] = conversations.Count, ["completeGroups"] = completeGroups, ["clearOfScenery"] = activityClear, ["shelterTogether"] = sheltered };
                File.WriteAllText(Path.Combine(directory, "activities.json"), JSONSerialization.prettyPrintedSortedKeys(activityReport));
                Godot.GD.Print($"Street activity audit: [\"conversationGroups\": {conversations.Count}, \"completeGroups\": {(completeGroups ? "true" : "false")}, \"roles\": [{string.Join(", ", roles.Select(r => $"\"{r.Key}\": {r.Value}"))}], \"clearOfScenery\": {(activityClear ? "true" : "false")}, \"shelterTogether\": {(sheltered ? "true" : "false")}]");
                if (!(completeGroups && activityClear && sheltered && conversations.Count >= 5 && (roles.TryGetValue("waiting at door", out var waiting) ? waiting : 0) > 0 && (roles.TryGetValue("market vendor", out var vendors) ? vendors : 0) > 0)) { return false; }
                var orientations = new HashSet<int>(entries.Select(e => (int)(((e.yaw + Math.PI / 4) % (2 * Math.PI)) / (Math.PI / 2))));
                var statistics = town.statistics;
                var report = new Dictionary<string, object>
                {
                    ["buildings"] = statistics.TryGetValue("buildings", out var buildings) ? buildings : 0, ["entrances"] = entries.Count, ["variants"] = Enumerable.Range(0, 5).Select(v => entries.Count(e => e.variant == v)).ToList(), ["blockedApproaches"] = blocked,
                    ["workingDoors"] = town.doorways.Count, ["doorConnections"] = town.residents?.connections ?? 0, ["townValid"] = town.validate(), ["orientations"] = orientations.OrderBy(o => o).ToList(), ["statistics"] = statistics,
                };
                File.WriteAllText(Path.Combine(directory, "entrances.json"), JSONSerialization.prettyPrintedSortedKeys(report));
                Godot.GD.Print($"Entrance audit: [\"townValid\": {(town.validate() ? "true" : "false")}, \"entrances\": {entries.Count}, \"variants\": {description(Enumerable.Range(0, 5).Select(v => entries.Count(e => e.variant == v)))}, \"buildings\": {(statistics.TryGetValue("buildings", out var count) ? count : 0)}, \"doorConnections\": {town.residents?.connections ?? 0}, \"workingDoors\": {town.doorways.Count}, \"blockedApproaches\": {blocked.Count}, \"orientations\": {description(orientations.OrderBy(o => o))}]");
                return blocked.Count == 0 && blockedRoutes.Count == 0 && town.inaccessibleBuildings.Count == 0 && entries.Count == town.lots.Count && entries.Count > 100 && orientations.Count >= 4 && town.validate();
            }
            finally { weatherOverride = null; }
        }
        catch (Exception error) { Godot.GD.Print(error.ToString()); return false; }
    }

    [GameMode("--entrance-smoke-test")]
    public static async System.Threading.Tasks.Task RunEntranceSmokeTest(string dir, Godot.SceneTree tree)
    {
        var app = await launchSmoke(tree, dir);
        var passed = app.checkTownEntrances(at: dir);
        exit(passed ? 0 : 1);
    }
}
