using System;
using System.Collections.Generic;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

/// Street errands complement doorway visits. Routes are clipped against actual
/// scenery, and every step is swept; neither LOD nor camera cuts teleport people.
public sealed class TownStreetResidents
{
    public sealed class Walker
    {
        public readonly SCNNode node; public readonly List<Double2> path; public readonly CitizenMotion.FootPlacement feet; public readonly List<SCNMaterial> materials;
        public readonly int start; public readonly double speed; public readonly float restingSole;
        public bool wasDetailed = true;
        public Double2 position; public int target, direction; public double heading;
        public double distance = 0.0, blend = 0.0, pending = 0.0, wait = 0.0, blocked = 0.0;
        public Walker(SCNNode node, List<Double2> path, int index)
        {
            this.node = node; this.path = path; start = path.Count / 2; position = path[start];
            direction = index % 2 == 0 ? 1 : -1; target = start + direction;
            var d = path[target] - position; heading = atan2(d.x, d.y);
            speed = 0.65 + (double)(index % 7) * 0.047;
            feet = new CitizenMotion.FootPlacement(node.geometry); materials = node.geometry.materials;
            restingSole = feet.minimum(cycle: 0, blend: 0);
        }
    }
    public List<Walker> walkers { get; private set; } = new();
    private readonly CityCollisionWorld city;
    private bool storm = false; private double time = 0.0;
    public double maximumPenetration { get; private set; } = 0.0;
    public int poseUpdates { get; private set; } = 0;
    public int navigationUpdates { get; private set; } = 0;
    private static RobotCollisions.Body body(Double2 p) =>
        new RobotCollisions.Body(position: new Double3(p.x, 0, p.y), profile: new RobotCollisions.Profile(mass: 70, halfWidth: 0.17, halfDepth: 0.17, height: 1.05, round: true));
    public List<RobotCollisions.Body> bodies => storm ? new List<RobotCollisions.Body>() : walkers.Select(w =>
    {
        var body = TownStreetResidents.body(w.position); body.heading = w.heading; return body;
    }).ToList();
    public int visible => storm ? 0 : walkers.Count;
    public TownStreetResidents(List<List<Double2>> paths, CityCollisionWorld city, TownCrowd crowd, SCNNode root)
    {
        this.city = city;
        // Interleave the authored streets so a population cap cannot fill only
        // the first block. Each route is resampled for reliable collision sweeps.
        var ordered = sorted(paths.Select((path, offset) => (offset, path)), (a, b) => ((a.offset * 7919) % max(1, paths.Count)) < ((b.offset * 7919) % max(1, paths.Count)));
        foreach (var (_, path) in ordered)
        {
            if (!(walkers.Count < 24)) { break; }
            var runs = new List<List<Double2>>(); var run = new List<Double2>();
            for (var k = 1; k < path.Count; k++)
            {
                Double2 a = path[k - 1], b = path[k];
                var steps = max(1, (int)ceil(Simd.distance(a, b) / 0.12));
                for (var j = 0; j < steps; j++)
                {
                    Double2 p = a + (b - a) * (double)j / (double)steps; var probe = body(p);
                    var free = city.nearby(probe).All(o => RobotCollisions.contact(probe, o) == null);
                    if (free) { run.Add(p); } else if (run.Count != 0) { runs.Add(run); run = new List<Double2>(); }
                }
            }
            if (run.Count != 0) { runs.Add(run); }
            if (!(maxBy(runs, (x, y) => x.Count < y.Count, out var route) && route.Count > 35
                  && walkers.All(w => Simd.distance(w.position, route[route.Count / 2]) > 5))) { continue; }
            int i = walkers.Count; var position = route[route.Count / 2];
            var node = crowd.add(x: position.x, y: 0, z: position.y, yaw: 0, index: 7200 + (i % 2) + (i / 2 % 2) * 2 + i / 4 * 12, seated: false, animated: true);
            if (node == null) { continue; }
            node.name = $"Street pedestrian {i}"; node.castsShadow = true; root.addChildNode(node);
            walkers.Add(new Walker(node: node, path: route, index: i));
        }
        reset();
        Godot.GD.Print($"Street residents: {walkers.Count} distributed walking routes");
    }
    public void setStorm(bool value) { storm = value; reset(); }
    public void reset()
    {
        time = 0; maximumPenetration = 0; poseUpdates = 0; navigationUpdates = 0;
        for (var i = 0; i < walkers.Count; i++)
        {
            var w = walkers[i];
            w.position = w.path[w.start]; w.direction = i % 2 == 0 ? 1 : -1; w.target = w.start + w.direction;
            var d = w.path[w.target] - w.position; w.heading = atan2(d.x, d.y);
            w.distance = 0; w.blend = 0; w.pending = 0; w.wait = (double)(i % 4) * 0.35; w.blocked = 0;
            w.node.isHidden = storm; pose(w, detailed: true);
        }
    }
    private void pose(Walker w, bool detailed)
    {
        float cycle = (float)(w.distance / 0.24 * Math.PI), blend = detailed ? (float)w.blend : 0;
        w.node.position = new SCNVector3(w.position.x, -0.016 - (double)(detailed ? w.feet.minimum(cycle: cycle, blend: blend) : w.restingSole), w.position.y);
        w.node.eulerAngles.y = (CGFloat)w.heading;
        // Off-screen navigation continues at 10 Hz, but gait deformation and
        // uniform writes sleep until the resident returns to the camera.
        if (!(detailed || w.wasDetailed)) { return; }
        w.wasDetailed = detailed;
        foreach (var m in w.materials)
        {
            m.setValue((float)time, "crowdTime"); m.setValue(cycle, "walkCycle"); m.setValue(blend, "walkBlend");
        }
    }
    public void update(double dt, List<RobotCollisions.Body> obstacles, Func<SCNNode, bool> visible = null)
    {
        if (!(dt > 0 && !storm)) { return; } time += dt;
        for (var i = 0; i < walkers.Count; i++)
        {
            var w = walkers[i];
            w.pending += min(dt, 0.05);
            var detailed = visible?.Invoke(w.node) ?? true;
            var near = obstacles.Any(o => Simd.distance(new Double2(o.position.x, o.position.z), w.position) < 3);
            if (!(detailed || near || w.pending >= 0.1 - 1e-8)) { continue; }
            var step = w.pending; w.pending = 0; navigationUpdates += 1;
            if (Simd.distance(w.position, w.path[w.target]) < 0.15)
            {
                var next_ = w.target + w.direction;
                if (next_ >= 0 && next_ < w.path.Count) { w.target = next_; }
                else { w.direction *= -1; w.target += w.direction; w.wait = 1.5 + (double)(i % 5) * 0.6; }
            }
            Double2 delta = w.path[w.target] - w.position; var angle = atan2(delta.x, delta.y);
            var turn = atan2(sin(angle - w.heading), cos(angle - w.heading));
            w.heading += max(-step * 2.4, min(step * 2.4, turn));
            w.wait = max(0, w.wait - step);
            // Turn before stepping after a reversal. Sweeping a turning arc
            // can leave the validated corridor and pin a walker to a wall.
            var length = w.wait > 0 || abs(turn) > 0.25 ? 0 : min(Simd.length(delta), w.speed * step);
            var next = w.position + delta / max(0.001, Simd.length(delta)) * length;
            var solids = city.nearby(body(next)).Concat(obstacles).ToList();
            var samples = max(1, (int)ceil(length / 0.015));
            var free = true;
            for (var j = 1; j <= samples && free; j++)
            {
                var p = w.position + (next - w.position) * (double)j / (double)samples;
                free = solids.All(solid => RobotCollisions.contact(body(p), solid) == null)
                    && walkers.All(other => other == w || Simd.distance(p, other.position) > 0.38);
            }
            var moved = free ? Simd.distance(w.position, next) : 0;
            if (free) { w.position = next; }
            w.distance += moved; w.blend += ((moved > 0.0001 ? 1.0 : 0) - w.blend) * min(1, step * 10);
            w.blocked = !free ? w.blocked + step : 0;
            if (w.blocked > 2.5 + (double)(i % 3) * 0.4)
            {
                // A one-sample reversal can still aim ahead of the body and
                // oscillate forever against an oncoming visitor. Retreat toward
                // a point a metre behind our actual position on the path.
                minBy(Enumerable.Range(0, w.path.Count), (x, y) => Simd.distance(w.path[x], w.position) < Simd.distance(w.path[y], w.position), out var nearest);
                w.direction *= -1;
                w.target = max(0, min(w.path.Count - 1, nearest + w.direction * 8)); w.blocked = 0;
            }
            if (detailed) { poseUpdates += 1; }
            pose(w, detailed: detailed);
            foreach (var b in solids) { if (RobotCollisions.contact(body(w.position), b) is RobotCollisions.Contact c) { maximumPenetration = max(maximumPenetration, c.penetration); } }
        }
    }
}
