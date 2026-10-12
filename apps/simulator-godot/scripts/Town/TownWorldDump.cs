using System.Collections.Generic;
using System.IO;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

/// Validation only (no macOS counterpart flag): writes the generated town as text, one line per item,
/// in the format of the Swift reference dumper that compiles the macOS TownWorld.swift unchanged
/// (see the town port notes), so the two towns can be diffed exactly:
///   tools/godot -- --town-reference-dump DIR   ->  DIR/town-reference.txt, DIR/signs/sign-N.png
/// Doubles use Swift's shortest description. Node transforms are printed through Float, because
/// SceneKit stores node positions and angles as Float (the facade keeps Double).
public static class TownWorldDump
{
    [GameMode("--town-reference-dump")]
    public static void Run(string dir, Godot.SceneTree tree)
    {
        var lines = new List<string>();
        string d(double v) => description(v);
        string v2(Double2 p) => $"{d(p.x)} {d(p.y)}";
        string v3(Double3 p) => $"{d(p.x)} {d(p.y)} {d(p.z)}";
        string b(bool v) => v ? "true" : "false";
        string f(double v) => d((double)(float)v);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var world = new TownWorld();
        Godot.GD.Print($"TownWorld built in {d(stopwatch.Elapsed.TotalSeconds)} s");
        var streets = world.mapStreets;
        for (var i = 0; i < streets.Count; i++) { lines.Add($"street {i} {streets[i].Count} " + string.Join(" ", streets[i].Select(v2))); }
        lines.Add($"lots {world.lots.Count}");
        foreach (var lot in world.lots) { lines.Add($"lot {d(lot.x)} {d(lot.z)} {d(lot.width)} {d(lot.depth)}"); }
        lines.Add($"mapBuildings {world.mapBuildings.Count}");
        foreach (var building in world.mapBuildings) { lines.Add("mapBuilding " + string.Join(" ", building.Select(v2))); }
        lines.Add($"entrances {world.entrances.Count}");
        foreach (var e in world.entrances) { lines.Add($"entrance {v2(e.center)} {d(e.yaw)} {d(e.width)} {d(e.height)} {e.variant} {b(e.walkable)}"); }
        lines.Add($"doorways {world.doorways.Count}");
        foreach (var e in world.doorways) { lines.Add($"doorway {v2(e.center)} {d(e.yaw)}"); }
        lines.Add($"windowSupports {world.windowSupports.Count}");
        foreach (var w in world.windowSupports) { lines.Add($"windowSupport {v3(w)}"); }
        lines.Add($"pedestrianAccess {world.pedestrianAccess.Count}");
        foreach (var a in world.pedestrianAccess) { lines.Add($"access {v2(a.building)} {v2(a.door)} {a.route.Count} " + string.Join(" ", a.route.Select(v2))); }
        lines.Add($"venueAccess {world.venueAccess.Count}");
        foreach (var a in world.venueAccess) { lines.Add($"venueAccess {v2(a.building)} {v2(a.door)} {a.route.Count} " + string.Join(" ", a.route.Select(v2))); }
        lines.Add($"inaccessibleBuildings {world.inaccessibleBuildings.Count}");
        lines.Add($"householdYards {world.householdYards.Count}");
        foreach (var y in world.householdYards) { lines.Add($"yard {v2(y)}"); }
        lines.Add($"streetActivities {world.streetActivities.Count}");
        foreach (var a in world.streetActivities) { lines.Add($"activity {v2(a.position)} {v2(a.target)} {a.role} {a.group} {a.index}"); }
        var bodies = world.collisionWorld.bodies;
        lines.Add($"bodies {bodies.Length}");
        foreach (var body in bodies) { lines.Add($"body {v3(body.position)} {d(body.heading)} {d(body.profile.mass)} {d(body.profile.halfWidth)} {d(body.profile.halfDepth)} {d(body.profile.height)} {b(body.profile.round)}"); }
        foreach (var z in world.spectatorSoundZones) { lines.Add($"spectatorZone {v2(z.position)} {z.people} {z.stormPeople}"); }
        foreach (var z in world.soundZones) { lines.Add($"soundZone {v2(z.position)} {(int)z.kind} {d(z.activity)} {b(z.infieldRepair)}"); }
        var statistics = world.statistics;
        foreach (var key in statistics.Keys.OrderBy(k => k, System.StringComparer.Ordinal)) { lines.Add($"stat {key} {statistics[key]}"); }
        lines.Add($"visiblePopulation {world.visiblePopulation}");
        lines.Add($"validate {b(world.validate())}");
        lines.Add($"cityCoveragePassed {b(world.cityCoveragePassed)}");
        lines.Add($"streetNetworkPassed {b(world.streetNetworkPassed)}");
        // Scene graph: every direct child of the town root (cells, signs, roads, doors...).
        // The angle is printed + 0.0 in both dumpers: SceneKit reads an unrotated eulerAngles.y back as -0.0.
        foreach (var node in world.root.childNodes)
        {
            var desc = $"node {node.name ?? "-"} {f(node.position.x)} {f(node.position.y)} {f(node.position.z)} {f(node.eulerAngles.y + 0.0)} hidden={b(node.isHidden)} shadow={b(node.castsShadow)}";
            if (node.geometry is SCNGeometry g)
            {
                var vertices = g.sourcesFor(SCNGeometrySourceSemantic.vertex).FirstOrDefault()?.vectorCount ?? 0;
                var triangles = g.elements.Sum(e => e.primitiveCount);
                desc += $" geometry vertices={vertices} triangles={triangles} elements={g.elements.Length} materials={g.materials.Count} lods={g.levelsOfDetail?.Length ?? 0}";
                if (g.levelsOfDetail?.FirstOrDefault()?.geometry is SCNGeometry lod)
                {
                    desc += $" lodVertices={lod.sourcesFor(SCNGeometrySourceSemantic.vertex).FirstOrDefault()?.vectorCount ?? 0} lodTriangles={lod.elements.Sum(e => e.primitiveCount)}";
                }
            }
            desc += $" children={node.childNodes.Count}";
            lines.Add(desc);
        }
        // Sign textures: DIR/signs/sign-<index>.png for every "Sign · " child, in scene order.
        var signDir = Path.Combine(dir, "signs");
        Directory.CreateDirectory(signDir);
        var index = 0;
        foreach (var node in world.root.childNodes.Where(n => (n.name ?? "").StartsWith("Sign · ")))
        {
            if (node.childNodes.Count <= 1 || node.childNodes[1].geometry?.materials.FirstOrDefault()?.diffuse.contents is not NSImage image) { continue; }
            File.WriteAllBytes(Path.Combine(signDir, $"sign-{index}.png"), image.tiffRepresentation);
            lines.Add($"signTexture {index} {node.name} {(int)image.size.width}x{(int)image.size.height}");
            index += 1;
        }
        File.WriteAllText(Path.Combine(dir, "town-reference.txt"), string.Join("\n", lines) + "\n");
        Godot.GD.Print($"wrote {lines.Count} lines to {Path.Combine(dir, "town-reference.txt")}");
        tree.Quit();
    }
}
