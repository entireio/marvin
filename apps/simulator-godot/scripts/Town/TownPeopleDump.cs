using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

/// Validation only (no macOS counterpart flag): the people half of the town port as text, in the format
/// of the Swift reference harness that compiles the unchanged macOS sources (TownCrowd.swift,
/// CitizenMotion.swift, TownResidents.swift, TownStreetResidents.swift):
///   tools/godot -- --town-people-dump DIR  ->  DIR/town-people.txt
/// It hashes every crowd, resident and street-pedestrian geometry (all LODs, every source as Float bits,
/// every element) and the inspection figures, samples FootPlacement.minimum, then runs a deterministic
/// 90 s simulation (a robot circling the first doorway, a rotating visibility pattern, a storm, reset,
/// checkYieldCornerRecovery) and prints every walker, door and material uniform once per simulated second.
public static class TownPeopleDump
{
    [GameMode("--town-people-dump")]
    public static void Run(string dir, Godot.SceneTree tree)
    {
        var lines = new List<string>();
        string f(float v) => description(v);
        string d(double v) => description(v);
        string b(bool v) => v ? "true" : "false";
        ulong fnv(ulong h, ReadOnlySpan<byte> bytes) { foreach (var x in bytes) { h = unchecked((h ^ x) * 0x100000001b3UL); } return h; }
        string sourceHash(SCNGeometrySource s)
        {
            var h = 0xcbf29ce484222325UL; var bytes = s.data;
            for (var i = 0; i < s.vectorCount; i++) { for (var c = 0; c < s.componentsPerVector; c++) {
                var o = s.dataOffset + i * s.dataStride + c * s.bytesPerComponent;
                var v = s.bytesPerComponent == 4 ? BitConverter.ToSingle(bytes, o) : (float)BitConverter.ToDouble(bytes, o);
                h = fnv(h, BitConverter.GetBytes(BitConverter.SingleToInt32Bits(v)));
            }}
            var semantic = s.semantic switch { SCNGeometrySourceSemantic.vertex => "vertex", SCNGeometrySourceSemantic.normal => "normal", SCNGeometrySourceSemantic.color => "color", SCNGeometrySourceSemantic.texcoord => "texcoord", _ => "other" };
            return $"{semantic} {s.vectorCount}x{s.componentsPerVector} {h:x}";
        }
        string elementHash(SCNGeometryElement e)
        {
            var h = 0xcbf29ce484222325UL;
            for (var i = 0; i < e.primitiveCount * 3; i++)
            {
                var v = e.bytesPerIndex == 4 ? BitConverter.ToInt32(e.data, i * 4) : (int)BitConverter.ToUInt16(e.data, i * 2);
                h = fnv(h, BitConverter.GetBytes(v));
            }
            return $"{e.primitiveCount} {h:x}";
        }
        void geometryLines(string name, SCNGeometry g)
        {
            var levels = new[] { g }.Concat((g.levelsOfDetail ?? Array.Empty<SCNLevelOfDetail>()).Select(l => l.geometry).Where(l => l != null)).ToList();
            for (var k = 0; k < levels.Count; k++)
            {
                var level = levels[k]; var bounds = level.boundingBox;
                lines.Add($"geometry {name} lod{k} bounds {d(bounds.min.x)} {d(bounds.min.y)} {d(bounds.min.z)} {d(bounds.max.x)} {d(bounds.max.y)} {d(bounds.max.z)}");
                // SceneKit lists a geometry's sources as vertex, normal, color, texcoords (stable within a semantic).
                int rank(SCNGeometrySource s) => s.semantic switch { SCNGeometrySourceSemantic.vertex => 0, SCNGeometrySourceSemantic.normal => 1, SCNGeometrySourceSemantic.color => 2, SCNGeometrySourceSemantic.texcoord => 3, _ => 4 };
                foreach (var s in level.sources.OrderBy(rank)) { lines.Add("  source " + sourceHash(s)); }
                foreach (var e in level.elements) { lines.Add("  element " + elementHash(e)); }
            }
            var lods = g.levelsOfDetail ?? Array.Empty<SCNLevelOfDetail>();
            for (var k = 0; k < lods.Length; k++) { lines.Add($"  lodDistance {k} {d(lods[k].worldSpaceDistance)}"); }
        }
        var town = new TownWorld();
        foreach (var node in town.root.childNodes)
        {
            var name = node.name;
            if (!(name != null && (name.StartsWith("Crowd cell") || name.StartsWith("Walking resident") || name.StartsWith("Street pedestrian")) && node.geometry is SCNGeometry g)) { continue; }
            geometryLines(name, g);
        }
        var crowd = new TownCrowd();
        foreach (var (name, node) in crowd.inspectionFigures()) { geometryLines("inspection " + name, node.geometry); }
        TownResidents residents = town.residents; TownStreetResidents street = town.streetResidents;
        foreach (var w in residents.walkers)
        {
            lines.Add($"resident {w.index} home {w.home} speed {d(w.speed)} rest {f(w.restingSole)} feet {f(w.feet.phase)} {f(w.feet.hip)} {f(w.feet.knee)} soles {w.feet.soles.Count} materials {w.materials.Count}");
            foreach (var c in strideTo(0.0, 30.0, 0.7)) { foreach (var blend in new[] { 0.0, 0.35, 1.0 }) { lines.Add($"  minimum {d(c)} {d(blend)} {f(w.feet.minimum(cycle: (float)c, blend: (float)blend))}"); } }
        }
        for (var i = 0; i < street.walkers.Count; i++)
        {
            var w = street.walkers[i];
            lines.Add($"street {i} path {w.path.Count} start {w.start} speed {d(w.speed)} rest {f(w.restingSole)} feet {f(w.feet.phase)} {f(w.feet.hip)} {f(w.feet.knee)} soles {w.feet.soles.Count}");
            lines.Add("  route " + string.Join(" ", w.path.Select(p => $"{d(p.x)} {d(p.y)}")));
        }
        string uniform(SCNMaterial m, string k) => m.value(k) is double v ? f((float)v) : "-";
        void state(int frame)
        {
            var stats = residents.updateStatistics;
            lines.Add($"frame {frame} residents entries {residents.entries} exits {residents.exits} pen {d(residents.maximumPenetration)} stats {stats["navigationUpdates"]} {stats["coarseUpdates"]} {stats["poseUpdates"]} visible {residents.visible} street pose {street.poseUpdates} nav {street.navigationUpdates} pen {d(street.maximumPenetration)} visible {street.visible} population {town.visiblePopulation}");
            foreach (var door in residents.doors) { lines.Add($"  door {d(door.opening)} {d(door.hold)} {f((float)door.leaf.position.x)} {f((float)door.leaf.position.z)}"); }
            foreach (var w in residents.walkers)
            {
                var yield = w.yieldPoint is Double2 y ? $"{d(y.x)} {d(y.y)}" : "nil";
                lines.Add($"  resident {w.index} {d(w.position.x)} {d(w.position.y)} h {d(w.heading)} b {d(w.blend)} dist {d(w.distance)} wait {d(w.wait)} indoors {b(w.indoors)} hidden {b(w.node.isHidden)} wp {w.waypoint}/{w.path.Count} home {w.home} dest {w.destination} visits {w.visits} blocked {d(w.blocked)} yield {yield} node {f((float)w.node.position.x)} {f((float)w.node.position.y)} {f((float)w.node.position.z)} {f((float)w.node.eulerAngles.y)} u {uniform(w.materials[0], "crowdTime")} {uniform(w.materials[0], "walkCycle")} {uniform(w.materials[0], "walkBlend")}");
            }
            for (var i = 0; i < street.walkers.Count; i++)
            {
                var w = street.walkers[i];
                lines.Add($"  street {i} {d(w.position.x)} {d(w.position.y)} h {d(w.heading)} t {w.target} dir {w.direction} b {d(w.blend)} dist {d(w.distance)} wait {d(w.wait)} blocked {d(w.blocked)} hidden {b(w.node.isHidden)} node {f((float)w.node.position.x)} {f((float)w.node.position.y)} {f((float)w.node.position.z)} {f((float)w.node.eulerAngles.y)} u {uniform(w.materials[0], "crowdTime")} {uniform(w.materials[0], "walkCycle")} {uniform(w.materials[0], "walkBlend")}");
            }
        }
        // A robot circling the first doorway exercises door requests, yields and pedestrian collisions.
        var anchor = residents.doors[0].outside;
        List<RobotCollisions.Body> robots(int frame)
        {
            double t = (double)frame / 60, a = t * 2 * Math.PI / 24;
            var p = anchor + new Double2(cos(a), sin(a)) * 2.6;
            return new List<RobotCollisions.Body> { new RobotCollisions.Body(position: new Double3(p.x, 0, p.y), heading: atan2(-sin(a), cos(a)), profile: RobotCollisions.profiles[1]) };
        }
        Func<SCNNode, bool> visible(int frame) => node => { var index = int.Parse(node.name.Split(' ').Last()); return (index + frame / 90) % 3 != 0; };
        var frame = 0;
        state(frame);
        void run(int count)
        {
            for (var k = 0; k < count; k++)
            {
                frame += 1;
                town.update(dt: 1.0 / 60, camera: new SCNVector3(0, 5, -12), player: Double2.zero, robots: robots(frame), visible: visible(frame));
                if (frame % 60 == 0) { state(frame); }
            }
        }
        run(3600);
        town.setStorm(true); lines.Add("storm on"); run(600);
        town.setStorm(false); lines.Add("storm off"); run(1200);
        town.update(dt: 0.3, camera: new SCNVector3(0, 5, -12), player: Double2.zero, robots: new List<RobotCollisions.Body>(), visible: null); frame += 1; state(frame);
        town.reset(); lines.Add("reset"); state(frame);
        var corner = residents.checkYieldCornerRecovery();
        string any(object value) => value switch { bool x => b(x), double x => d(x), double[] x => description(x), _ => value?.ToString() ?? "nil" };
        foreach (var key in corner.Keys.OrderBy(k => k, StringComparer.Ordinal)) { lines.Add($"corner {key} {any(corner[key])}"); }
        state(frame);
        File.WriteAllText(Path.Combine(dir, "town-people.txt"), string.Join("\n", lines) + "\n");
        Godot.GD.Print($"wrote {lines.Count} lines to {Path.Combine(dir, "town-people.txt")}");
        tree.Quit();
    }
}
