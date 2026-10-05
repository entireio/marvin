using System;
using System.Collections.Generic;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

// PORT: TownResidents.swift. Swift arrays are List<T>, sets HashSet<T>; optionals of value types are T?.

public sealed class TownDoorway
{
    public readonly Double2 center, outward; public readonly double yaw; public readonly SCNNode leaf;
    public double opening = 0.0, hold = 0.0;
    public Double2 outside => center + outward * 0.52;
    public Double2 inside => center - outward * 0.68;
    public RobotCollisions.Body body
    {
        get
        {
            var slide = opening * opening * (3 - 2 * opening) * 0.94;
            var p = center + new Double2(outward.y, -outward.x) * slide - outward * 0.06;
            return new RobotCollisions.Body(position: new Double3(p.x, 0.01, p.y), heading: yaw, profile: new RobotCollisions.Profile(mass: 100, halfWidth: 0.44, halfDepth: 0.035, height: 1.28));
        }
    }
    public TownDoorway(Double2 center, double yaw, SCNNode root, int variant = 0)
    {
        this.center = center; this.yaw = yaw; outward = new Double2(sin(yaw), cos(yaw));
        var geometry = new SCNBox(width: 0.88, height: 1.28, length: 0.07, chamferRadius: 0.015);
        var material = new SCNMaterial(); material.lightingModel = SCNMaterial.LightingModel.physicallyBased;
        uint[] colors = { 0x72634f, 0x65716b, 0x846654, 0x574b40, 0x74716a }; var color = colors[variant % 5];
        material.diffuse.contents = NSColor.calibratedRed((double)((color >> 16) & 255) / 255, (double)((color >> 8) & 255) / 255, (double)(color & 255) / 255, 1); material.roughness.contents = 0.8;
        geometry.materials = new() { material }; leaf = new SCNNode(geometry);
        var seam = new SCNMaterial(); seam.diffuse.contents = NSColor.calibratedWhite(0.18, 1); seam.roughness.contents = 0.8;
        for (var k = 0; k < variant % 3 + 1; k++)
        {
            var rib = new SCNBox(width: 0.018, height: 1.14, length: 0.012, chamferRadius: 0);
            rib.materials = new() { seam }; var n = new SCNNode(rib);
            n.position = new SCNVector3(-0.27 + (double)k * 0.23, 0, 0.041); leaf.addChildNode(n);
        }
        leaf.name = "Sliding resident doorway"; root.addChildNode(leaf); place();
    }
    public void place() { var p = body.position; leaf.position = new SCNVector3(p.x, p.y + 0.64, p.z); leaf.eulerAngles.y = (CGFloat)yaw; }
    public void update(double dt, bool requested)
    {
        if (requested) { hold = 1.5; } else { hold = max(0, hold - dt); }
        opening = max(0, min(1, opening + (hold > 0 ? dt : -dt) * 1.25)); place();
    }
}

/// Small bounded pedestrian population. Cached routes, physical swept footsteps,
/// distance-driven gait, independent schedules, and real doorway thresholds.
public sealed class TownResidents
{
    public sealed class Walker
    {
        public readonly SCNNode node; public readonly int index, initialHome; public readonly double speed; public readonly List<SCNMaterial> materials; public readonly CitizenMotion.FootPlacement feet;
        public readonly float restingSole;
        public int home, destination; public List<Double2> path = new(); public int waypoint = 0;
        public Double2 position; public double heading = 0.0, wait, distance = 0.0, blend = 0.0; public bool indoors = true;
        public int visits = 0; public double blocked = 0.0, nextAttempt = 0.0, firstEntry = -1.0;
        public double pending = 0.0, lastSeen = -1.0; public bool wasDetailed = true;
        public bool settlingInside = false;
        public Double2? yieldPoint, yieldOrigin; public double yieldUntil = 0.0;
        public HashSet<Int2> reserved = new();
        public Walker(SCNNode node, int index, int home, Double2 position)
        {
            this.node = node; this.index = index; this.home = home; initialHome = home; destination = home; this.position = position;
            speed = 0.52 + (double)(index % 5) * 0.045; wait = (double)(index % 9) * 1.7;
            materials = node.geometry?.materials ?? new List<SCNMaterial>(); feet = new CitizenMotion.FootPlacement(node.geometry);
            restingSole = feet.minimum(cycle: 0, blend: 0);
        }
    }
    public readonly List<TownDoorway> doors; public readonly CityCollisionWorld staticWorld;
    public List<Walker> walkers { get; private set; } = new();
    private Dictionary<int, List<(int, List<Double2>)>> routes = new();
    private bool storm = false; private double clock = 0.0;
    private Dictionary<string, HashSet<Int2>> reservationCells = new();
    private HashSet<Int2> cells(List<Double2> path)
    {
        var cells = new HashSet<Int2>();
        for (var k = 1; k < path.Count; k++)
        {
            Double2 a = path[k - 1], b = path[k];
            var steps = max(1, (int)ceil(Simd.distance(a, b) / 0.3));
            for (var step = 0; step <= steps; step++)
            {
                var p = a + (b - a) * (double)step / (double)steps; var cell = new Int2((int)floor(p.x * 2), (int)floor(p.y * 2));
                for (var dx = -1; dx <= 1; dx++) { for (var dz = -1; dz <= 1; dz++) { cells.Add(cell + new Int2(dx, dz)); } }
            }
        }
        return cells;
    }
    public int entries { get; private set; } = 0;
    public int exits { get; private set; } = 0;
    public double maximumPenetration { get; private set; } = 0.0;
    public int navigationUpdates { get; private set; } = 0;
    public int coarseUpdates { get; private set; } = 0;
    public int poseUpdates { get; private set; } = 0;
    public Dictionary<string, int> updateStatistics => new() { ["navigationUpdates"] = navigationUpdates, ["coarseUpdates"] = coarseUpdates, ["poseUpdates"] = poseUpdates };
    public List<RobotCollisions.Body> bodies => walkers.Where(w => !w.node.isHidden).Select(w => body(w.position)).Concat(doors.Select(door => door.body)).ToList();
    public int visible => walkers.Count(w => !w.node.isHidden);
    public int connections => routes.Values.Aggregate(0, (sum, list) => sum + list.Count);
    public TownResidents(List<TownDoorway> doors, CityCollisionWorld city, TownCrowd crowd, SCNNode root, int count)
    {
        this.doors = doors; staticWorld = city;
        var planner = new TownEscapeRoute(city: city, origin: Double2.zero);
        for (var i = 0; i < doors.Count; i++)
        {
            var candidates = sorted(Enumerable.Range(0, doors.Count).Where(j => j != i), (a, b) => Simd.distance(doors[i].outside, doors[a].outside) < Simd.distance(doors[i].outside, doors[b].outside));
            foreach (var j in candidates)
            {
                if (!((routes.TryGetValue(i, out var existing) ? existing.Count : 0) < 2)) { break; }
                var found = planner.route(doors[i].outside, doors[j].outside, rounded: true, clearance: 0.24, goalTolerance: 0.3, cellSize: 0.25);
                if (found != null && found.Length > 1 && Simd.distance(found[^1], doors[j].outside) < 0.4)
                {
                    var path = found.Concat(new[] { doors[j].outside }).ToList();
                    if (!routes.ContainsKey(i)) { routes[i] = new(); }
                    routes[i].Add((j, path));
                    reservationCells[$"{i}:{j}"] = cells(new[] { doors[i].inside }.Concat(path).Concat(new[] { doors[j].inside }).ToList());
                }
            }
        }
        var homes = sorted(routes.Keys.Where(key => (routes.TryGetValue(key, out var list) ? list : new()).Count != 0));
        if (!(homes.Length > 1)) { Godot.GD.Print("Resident navigation: no connected doors"); return; }
        for (var i = 0; i < min(count, homes.Length); i++)
        {
            var home = homes[i % homes.Length]; var p = doors[home].inside;
            // Use short tunics for walking; seated and long-robed figures stay in the stands.
            var modelIndex = 6000 + (i % 2) + (i / 2 % 2) * 2 + i / 4 * 12;
            var node = crowd.add(x: p.x, y: 0, z: p.y, yaw: 0, index: modelIndex, seated: false, animated: true);
            if (node == null) { continue; }
            node.name = $"Walking resident {i}"; node.castsShadow = true; node.isHidden = true; root.addChildNode(node);
            walkers.Add(new Walker(node: node, index: i, home: home, position: p));
        }
        Godot.GD.Print($"Resident navigation: {walkers.Count} walkers, {doors.Count} doors, {connections} routes");
    }
    private RobotCollisions.Body body(Double2 p) =>
        new RobotCollisions.Body(position: new Double3(p.x, 0, p.y), profile: new RobotCollisions.Profile(mass: 70, halfWidth: 0.17, halfDepth: 0.17, height: 1.05, round: true));
    public void setStorm(bool value) { storm = value; reset(); }
    public void reset()
    {
        clock = 0; entries = 0; exits = 0; maximumPenetration = 0;
        navigationUpdates = 0; coarseUpdates = 0; poseUpdates = 0;
        foreach (var w in walkers)
        {
            w.home = w.initialHome; w.position = doors[w.home].inside; w.indoors = true; w.wait = (double)(w.index % 9) * 1.7;
            w.path = new(); w.settlingInside = false; w.firstEntry = -1; w.reserved = new(); w.nextAttempt = 0; w.waypoint = 0; w.node.isHidden = true; w.distance = 0; w.visits = 0; w.blocked = 0;
            w.yieldPoint = null; w.yieldOrigin = null; w.yieldUntil = 0; w.pending = 0; w.lastSeen = -1; w.wasDetailed = true;
        }
        foreach (var door in doors) { door.opening = 0; door.hold = 0; door.place(); }
    }
    public void update(double dt, List<RobotCollisions.Body> robots, List<RobotCollisions.Body> pedestrians = null, Func<SCNNode, bool> visible = null)
    {
        pedestrians ??= new List<RobotCollisions.Body>();
        if (!(dt > 0)) { return; }
        dt = min(dt, 0.05); clock += dt;
        // PORT: the per-frame contains(where:)/allSatisfy/filter closures and concatenated arrays of TownResidents.swift
        // are loops over reused lists here (they allocated about 80 KB per frame); the same tests in the same order.
        for (var i = 0; i < doors.Count; i++)
        {
            var door = doors[i];
            var request = false;
            foreach (var w in walkers)
            {
                if (storm && w.index % 11 != 0) { continue; }
                if ((w.indoors && !w.settlingInside && w.home == i && w.wait < 1.2 && w.reserved.Count != 0)
                    || (!w.indoors && Simd.distance(w.position, door.center) < 1.6)) { request = true; break; }
            }
            if (!request) foreach (var robot in robots) if (Simd.distance(new Double2(robot.position.x, robot.position.z), door.center) < 1.1) { request = true; break; }
            door.update(dt: dt, requested: request);
        }
        var everyone = everyoneBuffer; everyone.Clear(); everyone.AddRange(robots); everyone.AddRange(pedestrians);
        foreach (var w in walkers)
        {
            if (!(!storm || w.index % 11 == 0)) { continue; }
            w.pending += dt;
            if (!w.node.isHidden && (visible?.Invoke(w.node) ?? true)) { w.lastSeen = clock; }
            // Grace prevents rate oscillation at a camera edge. Collision-critical
            // residents stay responsive even outside the camera frustum.
            var detailed = clock - w.lastSeen < 0.35;
            var interactive = false;
            foreach (var b in everyone) if (Simd.distance(new Double2(b.position.x, b.position.z), w.position) < 4) { interactive = true; break; }
            if (!interactive && !w.node.isHidden) foreach (var door in doors) if (Simd.distance(door.center, w.position) < 1.6) { interactive = true; break; }
            if (!interactive && !w.node.isHidden) foreach (var other in walkers) if (other != w && !other.node.isHidden && Simd.distance(other.position, w.position) < 1.2) { interactive = true; break; }
            if (!(detailed || interactive || w.pending >= 0.1 - 1e-8)) { continue; }
            // PORT: Swift shadows `dt` with the walker's accumulated step.
            var step_dt = w.pending; w.pending = 0;
            navigationUpdates += 1;
            if (!detailed && !interactive) { coarseUpdates += 1; }
            if (w.wasDetailed && !detailed)
            {
                foreach (var m in w.materials) { m.setValue(0f, "walkBlend"); }
            }
            w.wasDetailed = detailed;
            if (w.indoors)
            {
                w.wait = max(0, w.wait - step_dt);
                if (w.settlingInside)
                {
                    if (doors[w.home].opening < 0.02) { w.node.isHidden = true; w.settlingInside = false; w.reserved = new(); }
                    continue;
                }
                if (!(w.wait == 0 && routes.TryGetValue(w.home, out var choices) && choices.Count != 0)) { continue; }
                if (w.reserved.Count == 0)
                {
                    if (!(clock >= w.nextAttempt && doors[w.home].opening < 0.02)) { continue; }
                    w.nextAttempt = clock + 0.5;
                    var route = choices[(w.visits + w.index) % choices.Count];
                    var cells = reservationCells.TryGetValue($"{w.home}:{route.Item1}", out var reservation) ? reservation : new HashSet<Int2>();
                    if (!walkers.All(other => other == w || !other.reserved.Overlaps(cells))) { continue; }
                    w.reserved = cells; w.destination = route.Item1;
                    w.path = new[] { doors[w.home].outside }.Concat(route.Item2.Skip(1)).Concat(new[] { doors[w.destination].inside }).ToList();
                    // Reveal behind a closed door, never pop into an open doorway.
                    w.heading = doors[w.home].yaw; w.blend = 0;
                    w.node.position = new SCNVector3(w.position.x, 0.01 - (double)w.restingSole, w.position.y);
                    w.node.eulerAngles.y = (CGFloat)w.heading; w.node.isHidden = false;
                    foreach (var m in w.materials) { m.setValue(0f, "walkBlend"); }
                }
                if (!(doors[w.home].opening > 0.97
                      && walkers.All(other => other == w || other.node.isHidden || Simd.distance(other.position, doors[w.home].center) > 1.5))) { continue; }
                w.waypoint = 0; w.indoors = false; w.node.isHidden = false; exits += 1;
            }
            while (w.yieldOrigin == null && w.waypoint < w.path.Count && Simd.distance(w.position, w.path[w.waypoint]) < 0.06) { w.waypoint += 1; }
            if (w.waypoint == w.path.Count)
            {
                w.home = w.destination; w.visits += 1; entries += 1; w.indoors = true; w.settlingInside = true;
                if (w.firstEntry < 0) { w.firstEntry = clock; }
                foreach (var m in w.materials) { m.setValue(0f, "walkBlend"); }
                w.node.position.y = (CGFloat)(0.01 - (double)w.restingSole);
                w.wait = 8 + (double)((w.index * 7 + w.visits * 13) % 24); w.blend = 0; continue;
            }
            var target = w.path[w.waypoint];
            if (w.waypoint > 0)
            {
                Double2 a = w.path[w.waypoint - 1], segment = target - a;
                var t = max(0, min(1, Simd.dot(w.position - a, segment) / max(0.0001, Simd.length_squared(segment))));
                Double2 closest = a + segment * t; var remaining = Simd.distance(closest, target);
                if (remaining > 0.3) { target = closest + Simd.normalize(segment) * 0.3; }
            }
            // Step out of an approaching robot's lane, but only through swept,
            // unoccupied ground. Keep that space until the robot has passed.
            // Street walkers can be boxed against a route endpoint. After a
            // short pedestrian stand-off, use the same swept sidestep as for
            // robots, without treating pedestrians as door-opening requests.
            var approaching = approachingBuffer; approaching.Clear();
            foreach (var r in robots) if (Approaches(w, r)) approaching.Add(r);
            if (w.blocked > 0.5)
                foreach (var b in pedestrians)
                    if (Simd.distance(new Double2(b.position.x, b.position.z), w.position) < 1.0 && Approaches(w, b)) approaching.Add(b);
            if (approaching.Count != 0) { w.yieldUntil = clock + 1.2; }
            if (w.yieldPoint == null && w.yieldOrigin == null && minBy(approaching, (x, y) => Simd.distance(new Double2(x.position.x, x.position.z), w.position) < Simd.distance(new Double2(y.position.x, y.position.z), w.position), out var robot))
            {
                var side = new Double2(cos(robot.heading), -sin(robot.heading));
                var obstacles = staticWorld.nearby(new RobotCollisions.Body(position: new Double3(w.position.x, 0, w.position.y), profile: new RobotCollisions.Profile(mass: 70, halfWidth: 1.5, halfDepth: 1.5, height: 1.05)))
                    .Concat(doors.Select(door => door.body)).Concat(robots).Concat(pedestrians).ToList();
                var placed = false;
                foreach (var width in new[] { 0.7, 1.0 })
                {
                    foreach (var sign in new[] { -1.0, 1.0 })
                    {
                        var candidate = w.position + side * (width * sign);
                        var lane = abs(Simd.dot(candidate - new Double2(robot.position.x, robot.position.z), side));
                        if (!(lane > robot.profile.halfWidth + 0.3)) { continue; }
                        var free = true;
                        for (var step = 1; step <= 20 && free; step++)
                        {
                            var p = w.position + (candidate - w.position) * (double)step / 20;
                            free = obstacles.All(o => RobotCollisions.contact(body(p), o) == null)
                                && walkers.All(other => other == w || other.node.isHidden || Simd.distance(p, other.position) > 0.38);
                        }
                        if (free) { w.yieldOrigin = w.position; w.yieldPoint = candidate; placed = true; break; }
                    }
                    if (placed) { break; }
                }
            }
            if (w.yieldPoint is Double2 point)
            {
                if (clock > w.yieldUntil) { w.yieldPoint = null; }
                else { target = point; }
            }
            // The outbound sidestep was swept for clearance. Retrace it before
            // resuming the route; aiming straight at the next waypoint can cut
            // through a wall corner from this off-route position.
            if (w.yieldPoint == null && w.yieldOrigin is Double2 origin)
            {
                if (Simd.distance(w.position, origin) < 0.015) { w.yieldOrigin = null; }
                else { target = origin; }
            }
            Double2 delta = target - w.position, direction = delta / max(0.001, Simd.length(delta));
            var steering = direction;
            var robotNear = false;
            foreach (var r in robots)
            {
                var d = new Double2(r.position.x, r.position.z) - w.position;
                if (Simd.length(d) < 1.2 && Simd.dot(d, direction) > 0) { robotNear = true; break; }
            }
            var moved = 0.0;
            var heading = atan2(steering.x, steering.y);
            var angle = atan2(sin(heading - w.heading), cos(heading - w.heading));
            w.heading += max(-step_dt * 3, min(step_dt * 3, angle));
            // Turn before stepping around tight door jambs. The swept path must
            // follow the planned clearance, not cut the corner in a heading arc.
            // PORT: Swift `step` and `free`; C# cannot reuse names declared in the nested sidestep sweep above.
            var stepLength = w.speed * step_dt * (abs(angle) < 0.35 ? max(0, cos(angle)) : 0) * (robotNear && w.yieldPoint == null ? 0 : 1);
            var next = w.position + direction * min(stepLength, Simd.length(delta));
            var probe = body(next);
            var solids = solidsBuffer; solids.Clear();
            solids.AddRange(staticWorld.nearby(probe));
            foreach (var door in doors) solids.Add(door.body);
            solids.AddRange(robots); solids.AddRange(pedestrians);
            // Coarse updates still sweep the whole step; thin scenery cannot
            // disappear between the old and new positions.
            var subdivisions = max(1, (int)ceil(Simd.distance(next, w.position) / 0.015));
            var swept = true;
            for (var i = 1; i <= subdivisions && swept; i++)
            {
                var p = w.position + (next - w.position) * (double)i / (double)subdivisions;
                var sample = body(p);
                foreach (var solid in solids) if (RobotCollisions.contact(sample, solid) != null) { swept = false; break; }
                if (swept) foreach (var other in walkers) if (!(other == w || other.node.isHidden || Simd.distance(p, other.position) > 0.35)) { swept = false; break; }
            }
            if (swept) { moved = Simd.distance(next, w.position); w.position = next; }
            var previousBlocked = w.blocked;
            w.blocked = moved < 0.0001 ? w.blocked + step_dt : 0;
            if (previousBlocked < 10 && w.blocked >= 10)
            {
                var colliders = solids.Where(solid => RobotCollisions.contact(probe, solid) != null).Select(describe);
                var others = walkers.Where(other => other != w && !other.node.isHidden && Simd.distance(next, other.position) <= 0.35).Select(other => other.index);
                Godot.GD.Print($"Resident blocked: index={w.index} home={w.home} destination={w.destination} position={w.position} waypoint={w.waypoint}/{w.path.Count} target={target} yield={(w.yieldPoint is Double2 yp ? $"Optional({yp})" : "nil")} angle={description(angle)} robotNear={(robotNear ? "true" : "false")} colliders=[{string.Join(", ", colliders)}] otherResidents={description(others)}");
                Console.Out.Flush();
            }
            w.distance += moved; w.blend += ((moved > 0.0001 ? 1.0 : 0) - w.blend) * min(1, step_dt * 10);
            var cycle = (float)(w.distance / 0.24 * Math.PI);
            TownDoorway nearDoor = null;
            foreach (var door in doors) if (Simd.distance(w.position, door.center) < 1) { nearDoor = door; break; }
            var threshold = nearDoor != null ? max(0, min(1, 0.5 - Simd.dot(w.position - nearDoor.center, nearDoor.outward) * 3)) : 0;
            var floor = -0.016 + threshold * 0.026;
            var y = floor - (double)(detailed ? w.feet.minimum(cycle: cycle, blend: (float)w.blend) : w.restingSole);
            w.node.position = new SCNVector3(w.position.x, y, w.position.y); w.node.eulerAngles.y = (CGFloat)w.heading;
            if (detailed)
            {
                poseUpdates += 1;
                foreach (var m in w.materials)
                {
                    m.setValue((float)clock, "crowdTime"); m.setValue((float)(w.distance / 0.24 * Math.PI), "walkCycle");
                    m.setValue((float)w.blend, "walkBlend");
                }
            }
            var standing = body(w.position);
            foreach (var obstacle in solids) { if (RobotCollisions.contact(standing, obstacle) is RobotCollisions.Contact c) { maximumPenetration = max(maximumPenetration, c.penetration); } }
        }
    }
    private readonly List<RobotCollisions.Body> everyoneBuffer = new(), approachingBuffer = new(), solidsBuffer = new();
    /// <summary>A robot (or blocking pedestrian) within 2.4 m that is not moving away from the walker.</summary>
    private static bool Approaches(Walker w, RobotCollisions.Body r)
    {
        var d = w.position - new Double2(r.position.x, r.position.z);
        return Simd.length(d) < 2.4 && Simd.dot(d, new Double2(sin(r.heading), cos(r.heading))) > -0.2;
    }
    /// Swift's description of a RobotCollisions.Body (debug print only).
    private static string describe(RobotCollisions.Body b) =>
        $"Body(position: {b.position}, velocity: {b.velocity}, heading: {description(b.heading)}, angularVelocity: {description(b.angularVelocity)}, profile: SimulationCore.RobotCollisions.Profile(mass: {description(b.profile.mass)}, halfWidth: {description(b.profile.halfWidth)}, halfDepth: {description(b.profile.halfDepth)}, height: {description(b.profile.height)}, round: {(b.profile.round ? "true" : "false")}), contacted: {(b.contacted ? "true" : "false")})";

    // Native regression using the generated town's actual collision geometry.
    public Dictionary<string, object> checkYieldCornerRecovery()
    {
        bool clear(Double2 a, Double2 b)
        {
            var count = max(1, (int)ceil(Simd.distance(a, b) / 0.02));
            for (var i = 0; i <= count; i++)
            {
                var probe = body(a + (b - a) * (double)i / (double)count);
                if (!staticWorld.nearby(probe).Concat(doors.Select(door => door.body)).All(o => RobotCollisions.contact(probe, o) == null)) { return false; }
            }
            return true;
        }
        foreach (var home in sorted(routes.Keys))
        {
            foreach (var (destination, path) in routes[home])
            {
                for (var k = 1; k < path.Count; k++)
                {
                    Double2 a = path[k - 1], b = path[k], segment = b - a;
                    if (!(Simd.length(segment) > 0.35)) { continue; }
                    foreach (var fraction in new[] { 0.25, 0.5, 0.75 })
                    {
                        var origin = a + segment * fraction;
                        if (!doors.All(door => Simd.distance(door.center, origin) > 2)) { continue; }
                        for (var direction = 0; direction < 16; direction++)
                        {
                            var angle = (double)direction * Math.PI / 8;
                            var candidate = origin + new Double2(cos(angle), sin(angle)) * 0.7;
                            var t = max(0, min(1, Simd.dot(candidate - a, segment) / Simd.length_squared(segment)));
                            var closest = a + segment * t;
                            var target = Simd.distance(closest, b) > 0.3 ? closest + Simd.normalize(segment) * 0.3 : b;
                            if (!(clear(origin, candidate) && clear(origin, b) && !clear(candidate, target))) { continue; }
                            reset();
                            foreach (var other in walkers) { other.wait = 1000; }
                            var w = walkers[0];
                            w.home = home; w.destination = destination; w.indoors = false; w.node.isHidden = false;
                            w.position = candidate; w.path = new List<Double2> { a, b }; w.waypoint = 1;
                            w.yieldOrigin = origin; w.yieldPoint = candidate; w.yieldUntil = 0.5;
                            Godot.GD.Print($"Yield corner regression: origin={origin}, sidestep={candidate}, routeTarget={b}");
                            bool reached = false; var peakBlocked = 0.0;
                            for (var frame = 0; frame < 2400; frame++)
                            {
                                update(dt: 1.0 / 60, robots: new List<RobotCollisions.Body>(), visible: _ => true);
                                peakBlocked = max(peakBlocked, w.blocked);
                                if (Simd.distance(w.position, b) < 0.08) { reached = true; break; }
                            }
                            return new Dictionary<string, object>
                            {
                                ["passed"] = reached && maximumPenetration < 0.005, ["reached"] = reached, ["maximumBlockedSeconds"] = peakBlocked, ["maximumPenetration"] = maximumPenetration,
                                ["origin"] = new[] { origin.x, origin.y }, ["sidestep"] = new[] { candidate.x, candidate.y }, ["target"] = new[] { b.x, b.y }, ["finalPosition"] = new[] { w.position.x, w.position.y },
                            };
                        }
                    }
                }
            }
        }
        return new Dictionary<string, object> { ["passed"] = false, ["error"] = "No obstructed corner return found in town geometry" };
    }
}
