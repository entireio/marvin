// Godot-only diagnostic (no Swift counterpart): what the frame's draw calls are made of.
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Marvin.SceneKit;

namespace Marvin;

/// <summary>
/// Counts the Godot mesh instances of a scene that are visible in the tree, grouped by their top-most named SCNNode:
/// instances and surfaces, how many lie in the camera's frustum, cast shadows, distinct meshes and materials. Draw calls
/// are (instance, surface) pairs that Godot does not merge (its automatic instancing joins consecutive pairs with the same
/// mesh surface and material), so this shows where they come from.
/// </summary>
public static class DrawCensus
{
    /// <summary>The census, grouped by the first <paramref name="depth"/> named SCNNodes on the path from the scene root.</summary>
    public static string Report(SCNNode sceneRoot, Camera3D camera, int depth = 1)
    {
        var planes = camera?.GetFrustum();
        var groups = new SortedDictionary<string, (int instances, int surfaces, int inView, int surfacesInView, int casting, HashSet<ulong> meshes, HashSet<ulong> materials, HashSet<(ulong, ulong)> pairs)>(StringComparer.Ordinal);
        void Visit(Node node, string group, int named)
        {
            if (node is SCNNode scn && scn.name is string n && n.Length > 0 && named < depth) { group = group == null ? n : group + " / " + n; named++; }
            if (node is MeshInstance3D mi && mi.IsVisibleInTree() && mi.Mesh != null)
            {
                string key = group ?? "(unnamed)";
                if (!groups.TryGetValue(key, out var g)) g = (0, 0, 0, 0, 0, new(), new(), new());
                int surfaces = mi.Mesh.GetSurfaceCount();
                var box = mi.GlobalTransform * mi.GetAabb();
                bool inView = planes == null || InFrustum(box, planes);
                g.instances++; g.surfaces += surfaces;
                if (inView) { g.inView++; g.surfacesInView += surfaces; }
                if (mi.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off) g.casting += surfaces;
                g.meshes.Add(mi.Mesh.GetRid().Id);
                for (int s = 0; s < surfaces; s++)
                {
                    var m = mi.GetSurfaceOverrideMaterial(s) ?? mi.Mesh.SurfaceGetMaterial(s);
                    ulong mid = m?.GetRid().Id ?? 0;
                    g.materials.Add(mid);
                    g.pairs.Add((mi.Mesh.GetRid().Id * 64 + (ulong)s, mid));
                }
                groups[key] = g;
            }
            foreach (var child in node.GetChildren(true)) Visit(child, group, named);
        }
        foreach (var child in sceneRoot.GetChildren(true)) Visit(child, null, 0);
        var lines = new List<string> { "DRAW_CENSUS group | instances | surfaces | in view | surfaces in view | casting surfaces | meshes | materials | distinct (mesh surface, material)" };
        foreach (var (k, g) in groups.OrderByDescending(kv => kv.Value.surfaces))
            lines.Add($"DRAW_CENSUS {k} | {g.instances} | {g.surfaces} | {g.inView} | {g.surfacesInView} | {g.casting} | {g.meshes.Count} | {g.materials.Count} | {g.pairs.Count}");
        return string.Join("\n", lines);
    }

    private static bool InFrustum(Aabb box, Godot.Collections.Array<Plane> planes)
    {
        foreach (var p in planes)
        {
            // Godot's frustum planes point outwards: the box is outside when its support point towards -normal is in front.
            var support = new Vector3(p.Normal.X > 0 ? box.Position.X : box.End.X, p.Normal.Y > 0 ? box.Position.Y : box.End.Y, p.Normal.Z > 0 ? box.Position.Z : box.End.Z);
            if (p.DistanceTo(support) > 0) return false;
        }
        return true;
    }
}
