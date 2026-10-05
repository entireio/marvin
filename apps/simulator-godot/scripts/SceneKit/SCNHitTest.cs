using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Marvin.SceneKit;

/// <summary>
/// Ray/segment hit testing for SCNNode.hitTestWithSegment and SCNView/SCNRenderer.hitTest. Each geometry gets a cached
/// bounding volume hierarchy over its triangles (built on first use, rebuilt when its mesh data changes), so a ray
/// costs a few dozen box and triangle tests instead of one per triangle (Marvin alone has 695,172 triangles; SceneKit
/// hit tests are accelerated likewise). Options: <c>backFaceCulling</c> (default true), <c>categoryBitMask</c> (nodes whose
/// mask shares no bit are skipped), <c>ignoreHiddenNodes</c> (default true: hidden nodes and their subtrees are skipped)
/// and <c>firstFoundOnly</c>. PORT: with firstFoundOnly SceneKit returns whichever hit its traversal finds first; the
/// facade returns the nearest hit.
/// </summary>
internal static class SCNHitTest
{
    private sealed class Mesh
    {
        internal int dataVersion, bufferVersion;
        internal object data;
        internal double[] v;      // vertex positions xyz
        internal int[] tri;       // 3 vertex indices per triangle
        internal int[] element;   // element index per triangle
        internal int[] face;      // face index within the element per triangle
        internal int[] order;     // triangle order of the BVH leaves
        internal double[] box;    // per BVH node: min xyz, max xyz
        internal int[] node;      // per BVH node: first (leaf) or right child (inner, left = index + 1), count (0 = inner)
    }
    private static readonly ConditionalWeakTable<SCNGeometry, Mesh> meshes = new();
    private const int LeafSize = 6;

    private static Mesh MeshOf(SCNGeometry g)
    {
        var pos = g.VertexSource;
        if (pos == null) return null;
        if (meshes.TryGetValue(g, out var cached) && cached.dataVersion == g.meshDataVersion && ReferenceEquals(cached.data, pos.data) && cached.bufferVersion == pos.BufferVersion) return cached;
        var m = new Mesh { dataVersion = g.meshDataVersion, data = pos.data, bufferVersion = pos.BufferVersion };
        m.v = new double[pos.vectorCount * 3];
        for (int i = 0; i < pos.vectorCount; i++) { m.v[i * 3] = pos.Component(i, 0); m.v[i * 3 + 1] = pos.Component(i, 1); m.v[i * 3 + 2] = pos.Component(i, 2); }
        var tris = new List<int>(); var elements = new List<int>(); var faces = new List<int>();
        for (int e = 0; e < g.elements.Length; e++)
        {
            var list = g.elements[e].TriangleList();
            for (int i = 0; i + 2 < list.Length; i += 3)
            {
                tris.Add(list[i]); tris.Add(list[i + 1]); tris.Add(list[i + 2]);
                elements.Add(e); faces.Add(i / 3);
            }
        }
        m.tri = tris.ToArray(); m.element = elements.ToArray(); m.face = faces.ToArray();
        Build(m);
        meshes.AddOrUpdate(g, m);
        return m;
    }

    private static void Build(Mesh m)
    {
        int n = m.element.Length;
        m.order = new int[n];
        var centroid = new double[n * 3];
        for (int t = 0; t < n; t++)
        {
            m.order[t] = t;
            for (int c = 0; c < 3; c++)
                centroid[t * 3 + c] = (m.v[m.tri[t * 3] * 3 + c] + m.v[m.tri[t * 3 + 1] * 3 + c] + m.v[m.tri[t * 3 + 2] * 3 + c]) / 3;
        }
        var boxes = new List<double>(); var nodes = new List<int>();
        int Node(int first, int count)
        {
            int index = nodes.Count / 2;
            nodes.Add(first); nodes.Add(count);
            double lx = double.MaxValue, ly = double.MaxValue, lz = double.MaxValue, hx = double.MinValue, hy = double.MinValue, hz = double.MinValue;
            double cx0 = double.MaxValue, cy0 = double.MaxValue, cz0 = double.MaxValue, cx1 = double.MinValue, cy1 = double.MinValue, cz1 = double.MinValue;
            for (int k = first; k < first + count; k++)
            {
                int t = m.order[k];
                for (int j = 0; j < 3; j++)
                {
                    int vi = m.tri[t * 3 + j] * 3;
                    lx = Math.Min(lx, m.v[vi]); ly = Math.Min(ly, m.v[vi + 1]); lz = Math.Min(lz, m.v[vi + 2]);
                    hx = Math.Max(hx, m.v[vi]); hy = Math.Max(hy, m.v[vi + 1]); hz = Math.Max(hz, m.v[vi + 2]);
                }
                cx0 = Math.Min(cx0, centroid[t * 3]); cy0 = Math.Min(cy0, centroid[t * 3 + 1]); cz0 = Math.Min(cz0, centroid[t * 3 + 2]);
                cx1 = Math.Max(cx1, centroid[t * 3]); cy1 = Math.Max(cy1, centroid[t * 3 + 1]); cz1 = Math.Max(cz1, centroid[t * 3 + 2]);
            }
            boxes.Add(lx); boxes.Add(ly); boxes.Add(lz); boxes.Add(hx); boxes.Add(hy); boxes.Add(hz);
            if (count <= LeafSize) return index;
            double ex = cx1 - cx0, ey = cy1 - cy0, ez = cz1 - cz0;
            int axis = ex >= ey && ex >= ez ? 0 : ey >= ez ? 1 : 2;
            double extent = axis == 0 ? ex : axis == 1 ? ey : ez;
            if (extent <= 0) return index;
            // Median split along the widest centroid axis.
            int mid = first + count / 2;
            var keys = new double[count]; var items = new int[count];
            for (int k = 0; k < count; k++) { items[k] = m.order[first + k]; keys[k] = centroid[items[k] * 3 + axis]; }
            Array.Sort(keys, items);
            Array.Copy(items, 0, m.order, first, count);
            nodes[index * 2 + 1] = 0; // inner
            Node(first, mid - first);
            nodes[index * 2] = Node(mid, first + count - mid);
            return index;
        }
        if (n > 0) Node(0, n);
        m.box = boxes.ToArray(); m.node = nodes.ToArray();
    }

    /// <summary>Hits of the segment a -> b (in the geometry's local space) with geometry g: (local point, local face normal, face, element, t).</summary>
    private static void Segment(SCNGeometry g, SCNVector3 a, SCNVector3 b, bool cull, List<(SCNVector3 p, SCNVector3 n, int face, int element, double t)> hits)
    {
        var m = MeshOf(g);
        if (m == null || m.node.Length == 0) return;
        var dir = b - a;
        double ix = 1 / dir.x, iy = 1 / dir.y, iz = 1 / dir.z;
        Span<int> stack = stackalloc int[128];
        int sp = 0; stack[sp++] = 0;
        while (sp > 0)
        {
            int index = stack[--sp];
            int bo = index * 6;
            if (!SlabHit(a, ix, iy, iz, m.box[bo], m.box[bo + 1], m.box[bo + 2], m.box[bo + 3], m.box[bo + 4], m.box[bo + 5])) continue;
            int first = m.node[index * 2], count = m.node[index * 2 + 1];
            if (count == 0)
            {
                if (sp + 2 > stack.Length) { Brute(m, a, dir, cull, hits); return; }
                stack[sp++] = first; stack[sp++] = index + 1;
                continue;
            }
            for (int k = first; k < first + count; k++) Triangle(m, m.order[k], a, dir, cull, hits);
        }
    }
    private static void Brute(Mesh m, SCNVector3 a, SCNVector3 dir, bool cull, List<(SCNVector3 p, SCNVector3 n, int face, int element, double t)> hits)
    {
        hits.Clear();
        for (int t = 0; t < m.element.Length; t++) Triangle(m, t, a, dir, cull, hits);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool SlabHit(SCNVector3 a, double ix, double iy, double iz, double lx, double ly, double lz, double hx, double hy, double hz)
    {
        double t0 = 0, t1 = 1;
        if (!Slab(a.x, ix, lx, hx, ref t0, ref t1)) return false;
        if (!Slab(a.y, iy, ly, hy, ref t0, ref t1)) return false;
        return Slab(a.z, iz, lz, hz, ref t0, ref t1);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Slab(double origin, double inverse, double lo, double hi, ref double t0, ref double t1)
    {
        if (double.IsInfinity(inverse)) return origin >= lo - 1e-9 && origin <= hi + 1e-9;
        double ta = (lo - origin) * inverse, tb = (hi - origin) * inverse;
        if (ta > tb) (ta, tb) = (tb, ta);
        // A small margin keeps hits on box faces (axis-aligned triangles) from being lost to rounding.
        double pad = 1e-9 * Math.Max(1, Math.Abs(tb - ta));
        t0 = Math.Max(t0, ta - pad); t1 = Math.Min(t1, tb + pad);
        return t0 <= t1;
    }
    /// <summary>Möller-Trumbore, with the facade's original arithmetic (front faces are counter-clockwise).</summary>
    private static void Triangle(Mesh m, int t, SCNVector3 a, SCNVector3 dir, bool cull, List<(SCNVector3 p, SCNVector3 n, int face, int element, double t)> hits)
    {
        int i0 = m.tri[t * 3] * 3, i1 = m.tri[t * 3 + 1] * 3, i2 = m.tri[t * 3 + 2] * 3;
        var p0 = new SCNVector3(m.v[i0], m.v[i0 + 1], m.v[i0 + 2]);
        var p1 = new SCNVector3(m.v[i1], m.v[i1 + 1], m.v[i1 + 2]);
        var p2 = new SCNVector3(m.v[i2], m.v[i2 + 1], m.v[i2 + 2]);
        var e1 = p1 - p0; var e2 = p2 - p0;
        var pv = SCNVector3.Cross(dir, e2);
        double det = SCNVector3.Dot(e1, pv);
        if (cull ? det < 1e-12 : Math.Abs(det) < 1e-12) return;
        double inv = 1 / det;
        var tv = a - p0;
        double u = SCNVector3.Dot(tv, pv) * inv;
        if (u < 0 || u > 1) return;
        var qv = SCNVector3.Cross(tv, e1);
        double v = SCNVector3.Dot(dir, qv) * inv;
        if (v < 0 || u + v > 1) return;
        double s = SCNVector3.Dot(e2, qv) * inv;
        if (s < 0 || s > 1) return;
        hits.Add((a + dir * s, SCNVector3.Cross(e1, e2).Normalized(), m.face[t], m.element[t], s));
    }

    /// <summary>
    /// All hits of the world-space segment from -> to with root's subtree, nearest first. <paramref name="renderTransforms"/>
    /// selects the rendered transforms (views and renderers) or the model API's (hitTestWithSegment).
    /// </summary>
    internal static List<SCNHitTestResult> Run(SCNNode root, SCNVector3 from, SCNVector3 to, Dictionary<string, object> options, bool renderTransforms)
    {
        bool cull = true, ignoreHidden = true, firstOnly = false;
        int mask = -1;
        if (options != null)
        {
            if (options.TryGetValue(SCNHitTestOption.backFaceCulling, out var bf) && bf is bool b) cull = b;
            if (options.TryGetValue(SCNHitTestOption.ignoreHiddenNodes, out var ih) && ih is bool h) ignoreHidden = h;
            if (options.TryGetValue(SCNHitTestOption.firstFoundOnly, out var ff) && ff is bool f) firstOnly = f;
            if (options.TryGetValue(SCNHitTestOption.categoryBitMask, out var cm)) mask = Convert.ToInt32(cm);
        }
        var results = new List<SCNHitTestResult>();
        var local = new List<(SCNVector3 p, SCNVector3 n, int face, int element, double t)>();
        // World transforms accumulate down the tree (parent world x local), as RenderWorld()/ModelWorld() compute them.
        void Test(SCNNode n, SCNMatrix4 world)
        {
            if (ignoreHidden && n.isHidden) return;
            if (n.geometry is SCNGeometry g && (n.categoryBitMask & mask) != 0)
            {
                var inv = SCNMatrix4.Inverse(world);
                var la = inv.TransformPoint(from); var lb = inv.TransformPoint(to);
                local.Clear();
                Segment(g, la, lb, cull, local);
                foreach (var hit in local)
                {
                    var wp = world.TransformPoint(hit.p);
                    results.Add(new SCNHitTestResult(n, hit.p, wp, hit.n, world.TransformVector(hit.n).Normalized(), hit.face, hit.element, (wp - from).Length));
                }
            }
            foreach (var c in n.ChildrenView) Test(c, SCNMatrix4.Mul(world, renderTransforms ? c.RenderLocal() : c.ModelLocal()));
        }
        Test(root, renderTransforms ? root.RenderWorld() : root.ModelWorld());
        results.Sort((x, y) => x.distance.CompareTo(y.distance));
        if (firstOnly && results.Count > 1) results.RemoveRange(1, results.Count - 1);
        return results;
    }
}
