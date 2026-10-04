using System;
using System.Collections.Generic;
using Marvin.Core;

namespace Marvin;

public sealed partial class DirtWorld
{
    /// Prebuilt macro terrain. Local deformable patches replace touched cells;
    /// distant LODs retain matching vertices along their shared edges.
    public void addDesertTerrain(SCNMaterial earth, Action<double> progress = null)
    {
        var sand = earth.copy();
        sand.diffuse.contentsTransform = SCNMatrix4Identity;
        sand.roughness.contents = 0.94;
        sand.shaderModifiers = new() { [SCNShaderModifierEntryPoint.surface] = TownGround.terrainSurface };
        // Sharing world coordinates removes the visible 256 m square where
        // the original ground plane meets the surrounding terrain tiles.
        earth.shaderModifiers = sand.shaderModifiers;
        duneSand.configure(material: sand, root: scene.rootNode);
        double tile = 64.0;
        SCNGeometry geometry(double x, double z, int stride)
        {
            var vertices = new List<SCNVector3>(); var normals = new List<SCNVector3>(); var uv = new List<CGPoint>(); var indices = new List<int>();
            var vertexIDs = new Dictionary<int, int>();
            int vertex(int i, int j)
            {
                int key = j * 33 + i;
                if (vertexIDs.TryGetValue(key, out var existing)) return existing;
                double px = x + (double)i * 2, pz = z + (double)j * 2;
                double y = DesertTerrain.vertexHeight(x: px, z: pz);
                var g = DesertTerrain.gradient(x: px, z: pz); var normal = Simd.normalize(new Double3(-g.x, 1, -g.y));
                int id = vertices.Count; vertexIDs[key] = id;
                vertices.Add(new SCNVector3((double)i * 2, y, (double)j * 2));
                normals.Add(new SCNVector3(normal.x, normal.y, normal.z)); uv.Add(new CGPoint(px / 4, pz / 4));
                return id;
            }
            if (stride == 1) { for (int j = 0; j <= 32; j++) for (int i = 0; i <= 32; i++) _ = vertex(i, j); }
            for (int j = 0; j < 32; j += stride)
                for (int i = 0; i < 32; i += stride)
                {
                    var corners = new[] { (x: i, y: j), (x: i, y: j + stride), (x: i + stride, y: j + stride), (x: i + stride, y: j) };
                    if (stride == 1 || (i > 0 && j > 0 && i + stride < 32 && j + stride < 32))
                    {
                        var ids = Array.ConvertAll(corners, c => vertex(c.x, c.y));
                        indices.AddRange(new[] { ids[0], ids[1], ids[3], ids[3], ids[1], ids[2] });
                    }
                    else
                    {
                        // Every LOD preserves the exact 2 m boundary, stitching its
                        // coarse interior to identical neighbor vertices. No cracks
                        // or dark vertical skirt strips across the sand ridgelines.
                        var polygon = new List<int>();
                        var boundary = new[] { i == 0, j + stride == 32, i + stride == 32, j == 0 };
                        for (int edge = 0; edge < 4; edge++)
                        {
                            var a = corners[edge]; var b = corners[(edge + 1) % 4]; int steps = boundary[edge] ? stride : 1;
                            for (int k = 0; k < steps; k++)
                            {
                                int qx = a.x + (b.x - a.x) * k / steps, qz = a.y + (b.y - a.y) * k / steps;
                                polygon.Add(vertex(qx, qz));
                            }
                        }
                        int center = vertex(i + stride / 2, j + stride / 2);
                        for (int k = 0; k < polygon.Count; k++) indices.AddRange(new[] { center, polygon[k], polygon[(k + 1) % polygon.Count] });
                    }
                }
            var result = new SCNGeometry(new[] { SCNGeometrySource.vertices(vertices), SCNGeometrySource.normals(normals), SCNGeometrySource.textureCoordinates(uv) }, new[] { new SCNGeometryElement(indices, SCNGeometryPrimitiveType.triangles) });
            result.materials = new() { sand }; return result;
        }
        for (int j = -12; j < 12; j++)
        {
            for (int i = -12; i < 12; i++)
            {
                if (i >= -2 && i < 2 && j >= -2 && j < 2) continue;
                double x = (double)i * tile, z = (double)j * tile;
                var mesh = geometry(x, z, 1);
                mesh.levelsOfDetail = new[] { new SCNLevelOfDetail(geometry(x, z, 2), worldSpaceDistance: 130), new SCNLevelOfDetail(geometry(x, z, 4), worldSpaceDistance: 260) };
                var node = new SCNNode(mesh); node.position = new SCNVector3(x, 0, z);
                node.name = "Wind-shaped sand dunes"; node.castsShadow = false;
                scene.rootNode.addChildNode(node);
                duneSand.register(node, x: x, z: z);
            }
            progress?.Invoke((double)(j + 13) / 24);
        }
        // Fill beyond the finite dune field; its perimeter eases back to flat.
        var horizon = new SCNNode(new SCNPlane(width: 4000, height: 4000));
        horizon.geometry.materials = new() { earth }; horizon.eulerAngles.x = -Math.PI / 2; horizon.position.y = -0.05;
        horizon.castsShadow = false; scene.rootNode.addChildNode(horizon);
    }
}
