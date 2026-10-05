using System.Collections.Generic;
using System.Linq;

namespace Marvin;

// Port of MeshReuseValidation.swift (a module-level function; call it as MeshReuseValidation.validateMeshReuse).
public static class MeshReuseValidation
{
    /// Compare expanded indexed attributes, not just triangle/vertex counts. This
    /// catches UV seams, material partitioning and normal/color wiring regressions.
    public static bool validateMeshReuse(SCNGeometry original, SCNGeometry optimized)
    {
        if (!(original.elements.Length == optimized.elements.Length && original.sources.Length == optimized.sources.Length &&
              original.materials.Count == optimized.materials.Count)) { return false; }
        foreach (var (a, b) in original.materials.Zip(optimized.materials)) { if (!ReferenceEquals(a, b)) { return false; } }
        static int[] indices(SCNGeometryElement e)
        {
            var bytes = e.data;
            return Enumerable.Range(0, e.primitiveCount * 3).Select(offset => e.bytesPerIndex switch
            {
                1 => (int)bytes[offset],
                2 => (int)System.BitConverter.ToUInt16(bytes, offset * 2),
                _ => (int)System.BitConverter.ToUInt32(bytes, offset * 4),
            }).ToArray();
        }
        static double[] values(SCNGeometrySource s)
        {
            var bytes = s.data;
            var result = new List<double>(s.vectorCount * s.componentsPerVector);
            for (var index = 0; index < s.vectorCount; index++)
            {
                for (var axis = 0; axis < s.componentsPerVector; axis++)
                {
                    var offset = s.dataOffset + index * s.dataStride + axis * s.bytesPerComponent;
                    result.Add(s.bytesPerComponent == 4 ? (double)System.BitConverter.ToSingle(bytes, offset) : System.BitConverter.ToDouble(bytes, offset));
                }
            }
            return result.ToArray();
        }
        foreach (var (a, b) in original.sources.Zip(optimized.sources))
        {
            if (!(a.semantic == b.semantic && a.componentsPerVector == b.componentsPerVector && a.usesFloatComponents && b.usesFloatComponents)) { return false; }
        }
        var sources = original.sources.Zip(optimized.sources).Select(pair => (pair.First.componentsPerVector, values(pair.First), values(pair.Second))).ToList();
        foreach (var (a, b) in original.elements.Zip(optimized.elements))
        {
            if (!(a.primitiveType == SCNGeometryPrimitiveType.triangles && b.primitiveType == SCNGeometryPrimitiveType.triangles && a.primitiveCount == b.primitiveCount)) { return false; }
            foreach (var (i, j) in indices(a).Zip(indices(b)))
            {
                foreach (var (width, va, vb) in sources)
                {
                    if (!((i + 1) * width <= va.Length && (j + 1) * width <= vb.Length)) { return false; }
                    for (var axis = 0; axis < width; axis++) { if (va[i * width + axis] != vb[j * width + axis]) { return false; } }
                }
            }
        }
        return true;
    }
}
