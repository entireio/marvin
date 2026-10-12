using System;
using System.Collections.Generic;

namespace Marvin.SceneKit;

/// <summary>
/// Polygon triangulation with holes (ear clipping with hole bridging, after the
/// earcut algorithm by Mapbox, ISC licence). Used by SCNShape and SCNText.
/// Input: flat x,y array; holeIndices are vertex indices where each hole starts.
/// </summary>
internal static class Earcut
{
    private sealed class Node
    {
        public int i; public double x, y; public Node prev, next; public bool steiner;
        public Node(int i, double x, double y) { this.i = i; this.x = x; this.y = y; }
    }

    public static List<int> Triangulate(double[] data, int[] holeIndices)
    {
        var triangles = new List<int>();
        bool hasHoles = holeIndices != null && holeIndices.Length > 0;
        int outerLen = hasHoles ? holeIndices[0] * 2 : data.Length;
        var outer = LinkedList(data, 0, outerLen, true);
        if (outer == null || outer.next == outer.prev) return triangles;
        if (hasHoles) outer = EliminateHoles(data, holeIndices, outer);
        EarcutLinked(outer, triangles, 0);
        return triangles;
    }

    private static Node LinkedList(double[] data, int start, int end, bool clockwise)
    {
        Node last = null;
        if (clockwise == (SignedArea(data, start, end) > 0))
            for (int i = start; i < end; i += 2) last = InsertNode(i / 2, data[i], data[i + 1], last);
        else
            for (int i = end - 2; i >= start; i -= 2) last = InsertNode(i / 2, data[i], data[i + 1], last);
        if (last != null && EqualPts(last, last.next)) { RemoveNode(last); last = last.next; }
        return last;
    }
    private static double SignedArea(double[] data, int start, int end)
    {
        double sum = 0;
        for (int i = start, j = end - 2; i < end; i += 2) { sum += (data[j] - data[i]) * (data[i + 1] + data[j + 1]); j = i; }
        return sum;
    }
    private static Node FilterPoints(Node start, Node end)
    {
        if (start == null) return null;
        end ??= start;
        var p = start;
        bool again;
        do
        {
            again = false;
            if (!p.steiner && (EqualPts(p, p.next) || Area(p.prev, p, p.next) == 0))
            {
                RemoveNode(p);
                p = end = p.prev;
                if (p == p.next) break;
                again = true;
            }
            else p = p.next;
        } while (again || p != end);
        return end;
    }
    private static void EarcutLinked(Node ear, List<int> triangles, int pass)
    {
        if (ear == null) return;
        var stop = ear;
        int guard = 0;
        while (ear.prev != ear.next && guard++ < 1_000_000)
        {
            var prev = ear.prev; var next = ear.next;
            if (IsEar(ear))
            {
                triangles.Add(prev.i); triangles.Add(ear.i); triangles.Add(next.i);
                RemoveNode(ear);
                ear = next.next; stop = next.next;
                continue;
            }
            ear = next;
            if (ear == stop)
            {
                if (pass == 0) EarcutLinked(FilterPoints(ear, null), triangles, 1);
                else if (pass == 1) { ear = CureLocalIntersections(FilterPoints(ear, null), triangles); EarcutLinked(ear, triangles, 2); }
                else if (pass == 2) SplitEarcut(ear, triangles);
                break;
            }
        }
    }
    private static bool IsEar(Node ear)
    {
        Node a = ear.prev, b = ear, c = ear.next;
        if (Area(a, b, c) >= 0) return false;
        var p = ear.next.next;
        while (p != ear.prev)
        {
            if (PointInTriangle(a.x, a.y, b.x, b.y, c.x, c.y, p.x, p.y) && Area(p.prev, p, p.next) >= 0) return false;
            p = p.next;
        }
        return true;
    }
    private static Node CureLocalIntersections(Node start, List<int> triangles)
    {
        var p = start;
        do
        {
            Node a = p.prev, b = p.next.next;
            if (!EqualPts(a, b) && Intersects(a, p, p.next, b) && LocallyInside(a, b) && LocallyInside(b, a))
            {
                triangles.Add(a.i); triangles.Add(p.i); triangles.Add(b.i);
                RemoveNode(p); RemoveNode(p.next);
                p = start = b;
            }
            p = p.next;
        } while (p != start);
        return FilterPoints(p, null);
    }
    private static void SplitEarcut(Node start, List<int> triangles)
    {
        var a = start;
        do
        {
            var b = a.next.next;
            while (b != a.prev)
            {
                if (a.i != b.i && IsValidDiagonal(a, b))
                {
                    var c = SplitPolygon(a, b);
                    a = FilterPoints(a, a.next);
                    c = FilterPoints(c, c.next);
                    EarcutLinked(a, triangles, 0);
                    EarcutLinked(c, triangles, 0);
                    return;
                }
                b = b.next;
            }
            a = a.next;
        } while (a != start);
    }
    private static Node EliminateHoles(double[] data, int[] holeIndices, Node outer)
    {
        var queue = new List<Node>();
        for (int i = 0; i < holeIndices.Length; i++)
        {
            int start = holeIndices[i] * 2;
            int end = i < holeIndices.Length - 1 ? holeIndices[i + 1] * 2 : data.Length;
            var list = LinkedList(data, start, end, false);
            if (list == null) continue;
            if (list == list.next) list.steiner = true;
            queue.Add(GetLeftmost(list));
        }
        queue.Sort((a, b) => a.x.CompareTo(b.x));
        foreach (var hole in queue) outer = EliminateHole(hole, outer);
        return outer;
    }
    private static Node EliminateHole(Node hole, Node outer)
    {
        var bridge = FindHoleBridge(hole, outer);
        if (bridge == null) return outer;
        var bridgeReverse = SplitPolygon(bridge, hole);
        FilterPoints(bridgeReverse, bridgeReverse.next);
        return FilterPoints(bridge, bridge.next);
    }
    private static Node FindHoleBridge(Node hole, Node outer)
    {
        var p = outer;
        double hx = hole.x, hy = hole.y, qx = double.NegativeInfinity;
        Node m = null;
        do
        {
            if (hy <= p.y && hy >= p.next.y && p.next.y != p.y)
            {
                double x = p.x + (hy - p.y) * (p.next.x - p.x) / (p.next.y - p.y);
                if (x <= hx && x > qx)
                {
                    qx = x;
                    m = p.x < p.next.x ? p : p.next;
                    if (x == hx) return m;
                }
            }
            p = p.next;
        } while (p != outer);
        if (m == null) return null;
        var stop = m;
        double mx = m.x, my = m.y, tanMin = double.PositiveInfinity;
        p = m;
        do
        {
            if (hx >= p.x && p.x >= mx && hx != p.x &&
                PointInTriangle(hy < my ? hx : qx, hy, mx, my, hy < my ? qx : hx, hy, p.x, p.y))
            {
                double tan = Math.Abs(hy - p.y) / (hx - p.x);
                if (LocallyInside(p, hole) && (tan < tanMin || (tan == tanMin && (p.x > m.x || (p.x == m.x && SectorContainsSector(m, p))))))
                { m = p; tanMin = tan; }
            }
            p = p.next;
        } while (p != stop);
        return m;
    }
    private static bool SectorContainsSector(Node m, Node p) => Area(m.prev, m, p.prev) < 0 && Area(p.next, m, m.next) < 0;
    private static Node GetLeftmost(Node start)
    {
        Node p = start, leftmost = start;
        do { if (p.x < leftmost.x || (p.x == leftmost.x && p.y < leftmost.y)) leftmost = p; p = p.next; } while (p != start);
        return leftmost;
    }
    private static bool PointInTriangle(double ax, double ay, double bx, double by, double cx, double cy, double px, double py) =>
        (cx - px) * (ay - py) >= (ax - px) * (cy - py) && (ax - px) * (by - py) >= (bx - px) * (ay - py) && (bx - px) * (cy - py) >= (cx - px) * (by - py);
    private static bool IsValidDiagonal(Node a, Node b) =>
        a.next.i != b.i && a.prev.i != b.i && !IntersectsPolygon(a, b) &&
        (LocallyInside(a, b) && LocallyInside(b, a) && MiddleInside(a, b) && (Area(a.prev, a, b.prev) != 0 || Area(a, b.prev, b) != 0) ||
         EqualPts(a, b) && Area(a.prev, a, a.next) > 0 && Area(b.prev, b, b.next) > 0);
    private static double Area(Node p, Node q, Node r) => (q.y - p.y) * (r.x - q.x) - (q.x - p.x) * (r.y - q.y);
    private static bool EqualPts(Node a, Node b) => a.x == b.x && a.y == b.y;
    private static bool Intersects(Node p1, Node q1, Node p2, Node q2)
    {
        int o1 = Sign(Area(p1, q1, p2)), o2 = Sign(Area(p1, q1, q2)), o3 = Sign(Area(p2, q2, p1)), o4 = Sign(Area(p2, q2, q1));
        if (o1 != o2 && o3 != o4) return true;
        if (o1 == 0 && OnSegment(p1, p2, q1)) return true;
        if (o2 == 0 && OnSegment(p1, q2, q1)) return true;
        if (o3 == 0 && OnSegment(p2, p1, q2)) return true;
        if (o4 == 0 && OnSegment(p2, q1, q2)) return true;
        return false;
    }
    private static bool OnSegment(Node p, Node q, Node r) =>
        q.x <= Math.Max(p.x, r.x) && q.x >= Math.Min(p.x, r.x) && q.y <= Math.Max(p.y, r.y) && q.y >= Math.Min(p.y, r.y);
    private static int Sign(double v) => v > 0 ? 1 : v < 0 ? -1 : 0;
    private static bool IntersectsPolygon(Node a, Node b)
    {
        var p = a;
        do
        {
            if (p.i != a.i && p.next.i != a.i && p.i != b.i && p.next.i != b.i && Intersects(p, p.next, a, b)) return true;
            p = p.next;
        } while (p != a);
        return false;
    }
    private static bool LocallyInside(Node a, Node b) =>
        Area(a.prev, a, a.next) < 0 ? Area(a, b, a.next) >= 0 && Area(a, a.prev, b) >= 0 : Area(a, b, a.prev) < 0 || Area(a, a.next, b) < 0;
    private static bool MiddleInside(Node a, Node b)
    {
        var p = a;
        bool inside = false;
        double px = (a.x + b.x) / 2, py = (a.y + b.y) / 2;
        do
        {
            if ((p.y > py) != (p.next.y > py) && p.next.y != p.y && px < (p.next.x - p.x) * (py - p.y) / (p.next.y - p.y) + p.x) inside = !inside;
            p = p.next;
        } while (p != a);
        return inside;
    }
    private static Node SplitPolygon(Node a, Node b)
    {
        var a2 = new Node(a.i, a.x, a.y); var b2 = new Node(b.i, b.x, b.y);
        Node an = a.next, bp = b.prev;
        a.next = b; b.prev = a;
        a2.next = an; an.prev = a2;
        b2.next = a2; a2.prev = b2;
        bp.next = b2; b2.prev = bp;
        return b2;
    }
    private static Node InsertNode(int i, double x, double y, Node last)
    {
        var p = new Node(i, x, y);
        if (last == null) { p.prev = p; p.next = p; }
        else { p.next = last.next; p.prev = last; last.next.prev = p; last.next = p; }
        return p;
    }
    private static void RemoveNode(Node p) { p.next.prev = p.prev; p.prev.next = p.next; }
}
