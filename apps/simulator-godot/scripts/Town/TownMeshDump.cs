using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Marvin;

/// Validation only (no macOS counterpart flag): with `--mesh-reuse-test` among the arguments,
/// TownMesh keeps (original, merged) geometry pairs. This writes every pair's vertex counts and,
/// for merged geometries whose vertex count is listed in MARVIN_MESH_COUNTS (comma separated), each
/// raw input vertex (float bit patterns of every source) and its merged index, in the format of the
/// Swift mesh-dump harness, so vertex welding can be diffed against the macOS TownMesh:
///   MARVIN_MESH_COUNTS=6061 tools/godot -- --town-mesh-dump DIR --mesh-reuse-test  ->  DIR/town-mesh.txt
public static class TownMeshDump
{
    [GameMode("--town-mesh-dump")]
    public static void Run(string dir, Godot.SceneTree tree)
    {
        var wanted = (Environment.GetEnvironmentVariable("MARVIN_MESH_COUNTS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
        _ = new TownWorld();
        var lines = new List<string>();
        static float[] floats(SCNGeometrySource s, int i) =>
            Enumerable.Range(0, s.componentsPerVector).Select(c => BitConverter.ToSingle(s.data, s.dataOffset + i * s.dataStride + c * s.bytesPerComponent)).ToArray();
        static int[] indices(SCNGeometryElement e) =>
            Enumerable.Range(0, e.primitiveCount * 3).Select(j => e.bytesPerIndex == 2 ? BitConverter.ToUInt16(e.data, j * 2) : (int)BitConverter.ToUInt32(e.data, j * 4)).ToArray();
        foreach (var (n, (original, merged)) in TownMesh.validationPairs.Select((pair, n) => (n, pair)))
        {
            var count = merged.sourcesFor(SCNGeometrySourceSemantic.vertex).First().vectorCount;
            lines.Add($"pair {n} raw={original.sourcesFor(SCNGeometrySourceSemantic.vertex).First().vectorCount} merged={count}");
            if (!wanted.Contains(count)) { continue; }
            var sources = original.sources; var raw = sources[0].vectorCount;
            var remap = Enumerable.Repeat(-1, raw).ToArray();
            for (var e = 0; e < original.elements.Length; e++)
            {
                int[] a = indices(original.elements[e]), b = indices(merged.elements[e]);
                for (var k = 0; k < a.Length; k++) { remap[a[k]] = b[k]; }
            }
            for (var i = 0; i < raw; i++)
            {
                var parts = sources.Select(s => string.Join(",", floats(s, i).Select(f => BitConverter.SingleToUInt32Bits(f).ToString("x"))));
                lines.Add($"v {n} {i} {remap[i]} " + string.Join(" ", parts));
            }
        }
        File.WriteAllText(Path.Combine(dir, "town-mesh.txt"), string.Join("\n", lines) + "\n");
        Godot.GD.Print($"wrote {lines.Count} lines");
        tree.Quit();
    }
}
