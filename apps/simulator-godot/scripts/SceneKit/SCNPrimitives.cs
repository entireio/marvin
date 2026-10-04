using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Marvin.SceneKit;

/// <summary>Accumulates vertices and per-element triangle lists (SceneKit counter-clockwise order).</summary>
internal sealed class PrimitiveBuilder
{
    public readonly List<SCNVector3> p = new(), n = new();
    public readonly List<CGPoint> uv = new();
    public readonly List<List<int>> elements = new();
    public int Vertex(SCNVector3 position, SCNVector3 normal, double u, double v) { p.Add(position); n.Add(normal); uv.Add(new CGPoint(u, v)); return p.Count - 1; }
    public List<int> Element() { var e = new List<int>(); elements.Add(e); return e; }
    /// <summary>Adds a triangle facing `outward` (counter-clockwise seen from outside).</summary>
    public void Triangle(List<int> e, int a, int b, int c, SCNVector3 outward)
    {
        var cross = SCNVector3.Cross(p[b] - p[a], p[c] - p[a]);
        if (SCNVector3.Dot(cross, outward) < 0) (b, c) = (c, b);
        e.Add(a); e.Add(b); e.Add(c);
    }
    public (SCNGeometrySource[], SCNGeometryElement[]) Build() =>
        (new[] { SCNGeometrySource.vertices(p), SCNGeometrySource.normals(n), SCNGeometrySource.textureCoordinates(uv) },
         elements.Select(e => new SCNGeometryElement(e, SCNGeometryPrimitiveType.triangles)).ToArray());
}

/// <summary>SCNBox with chamferRadius and segment counts. One element per face: front, right, back, left, top, bottom.</summary>
public sealed class SCNBox : SCNGeometry
{
    private double _w = 1, _h = 1, _l = 1, _r;
    private int _ws = 1, _hs = 1, _ls = 1, _cs = 10;
    public SCNBox() { }
    /// <summary>SCNBox(width:height:length:chamferRadius:).</summary>
    public SCNBox(double width, double height, double length, double chamferRadius) { _w = width; _h = height; _l = length; _r = chamferRadius; }
    public double width { get => _w; set { _w = value; Rebuild(); } }
    public double height { get => _h; set { _h = value; Rebuild(); } }
    public double length { get => _l; set { _l = value; Rebuild(); } }
    public double chamferRadius { get => _r; set { _r = value; Rebuild(); } }
    public int widthSegmentCount { get => _ws; set { _ws = value; Rebuild(); } }
    public int heightSegmentCount { get => _hs; set { _hs = value; Rebuild(); } }
    public int lengthSegmentCount { get => _ls; set { _ls = value; Rebuild(); } }
    public int chamferSegmentCount { get => _cs; set { _cs = value; Rebuild(); } }
    protected override SCNGeometry CopyShape() => new SCNBox(_w, _h, _l, _r) { _ws = _ws, _hs = _hs, _ls = _ls, _cs = _cs };

    protected override void Build()
    {
        double hx = _w / 2, hy = _h / 2, hz = _l / 2;
        double r = Math.Clamp(_r, 0, Math.Min(hx, Math.Min(hy, hz)));
        int steps = Math.Max(1, _cs / 2);
        var b = new PrimitiveBuilder();
        // Grid coordinates along one axis: chamfer zone (uniform angle steps), flat interior, chamfer zone.
        List<double> Axis(double half, int segments)
        {
            var list = new List<double>();
            double inner = half - r;
            if (r > 0) for (int k = steps; k >= 1; k--) list.Add(-inner - r * Math.Tan(k * Math.PI / 4 / steps));
            for (int i = 0; i <= Math.Max(1, segments); i++) list.Add(-inner + 2 * inner * i / Math.Max(1, segments));
            if (r > 0) for (int k = 1; k <= steps; k++) list.Add(inner + r * Math.Tan(k * Math.PI / 4 / steps));
            if (inner <= 0 && r > 0) list = list.Distinct().ToList();
            return list;
        }
        var ax = Axis(hx, _ws); var ay = Axis(hy, _hs); var az = Axis(hz, _ls);
        SCNVector3 Round(SCNVector3 q)
        {
            if (r <= 0) return q;
            var inner = new SCNVector3(Math.Clamp(q.x, -(hx - r), hx - r), Math.Clamp(q.y, -(hy - r), hy - r), Math.Clamp(q.z, -(hz - r), hz - r));
            var d = q - inner;
            return d.Length < 1e-12 ? q : inner + d.Normalized() * r;
        }
        SCNVector3 Normal(SCNVector3 q, SCNVector3 face)
        {
            if (r <= 0) return face;
            var inner = new SCNVector3(Math.Clamp(q.x, -(hx - r), hx - r), Math.Clamp(q.y, -(hy - r), hy - r), Math.Clamp(q.z, -(hz - r), hz - r));
            var d = q - inner;
            return d.Length < 1e-12 ? face : d.Normalized();
        }
        // face: normal, u axis (as point function), v axis, uv mapping (measured on SCNBox).
        void Face(SCNVector3 normal, List<double> us, List<double> vs, Func<double, double, SCNVector3> point, Func<SCNVector3, (double u, double v)> uvOf)
        {
            var e = b.Element();
            int cols = us.Count, rows = vs.Count;
            var idx = new int[cols, rows];
            for (int i = 0; i < cols; i++)
                for (int j = 0; j < rows; j++)
                {
                    var q = point(us[i], vs[j]);
                    var (u, v) = uvOf(q);
                    idx[i, j] = b.Vertex(Round(q), Normal(q, normal), u, v);
                }
            for (int i = 0; i + 1 < cols; i++)
                for (int j = 0; j + 1 < rows; j++)
                {
                    b.Triangle(e, idx[i, j], idx[i + 1, j], idx[i + 1, j + 1], normal);
                    b.Triangle(e, idx[i, j], idx[i + 1, j + 1], idx[i, j + 1], normal);
                }
        }
        Face(new(0, 0, 1), ax, ay, (x, y) => new(x, y, hz), q => ((q.x + hx) / _w, (hy - q.y) / _h));          // front
        Face(new(1, 0, 0), az, ay, (z, y) => new(hx, y, z), q => ((hz - q.z) / _l, (hy - q.y) / _h));          // right
        Face(new(0, 0, -1), ax, ay, (x, y) => new(x, y, -hz), q => ((hx - q.x) / _w, (hy - q.y) / _h));        // back
        Face(new(-1, 0, 0), az, ay, (z, y) => new(-hx, y, z), q => ((q.z + hz) / _l, (hy - q.y) / _h));        // left
        Face(new(0, 1, 0), ax, az, (x, z) => new(x, hy, z), q => ((q.x + hx) / _w, (q.z + hz) / _l));          // top
        Face(new(0, -1, 0), ax, az, (x, z) => new(x, -hy, z), q => ((q.x + hx) / _w, (hz - q.z) / _l));        // bottom
        var (s, el) = b.Build();
        SetData(s, el);
    }
}

/// <summary>SCNSphere: UV sphere with segmentCount meridians and parallels (SceneKit default 24).</summary>
public sealed class SCNSphere : SCNGeometry
{
    private double _r = 0.5;
    private int _seg = 24;
    private bool _geodesic;
    public SCNSphere() { }
    public SCNSphere(double radius) { _r = radius; }
    public double radius { get => _r; set { _r = value; Rebuild(); } }
    public int segmentCount { get => _seg; set { _seg = value; Rebuild(); } }
    public bool isGeodesic { get => _geodesic; set { _geodesic = value; Rebuild(); } }
    protected override SCNGeometry CopyShape() => new SCNSphere(_r) { _seg = _seg, _geodesic = _geodesic };
    protected override void Build()
    {
        int n = Math.Max(3, _seg);
        var b = new PrimitiveBuilder();
        var e = b.Element();
        // Columns are meridians (u), starting at -Z; rows run from the bottom pole (v = 1) to the top (v = 0).
        for (int i = 0; i <= n; i++)
        {
            double phi = 2 * Math.PI * i / n;
            for (int j = 0; j <= n; j++)
            {
                double theta = Math.PI * (1 - (double)j / n);
                var dir = new SCNVector3(Math.Sin(theta) * Math.Sin(phi), Math.Cos(theta), -Math.Sin(theta) * Math.Cos(phi));
                b.Vertex(dir * _r, dir, (double)i / n, 1 - (double)j / n);
            }
        }
        int Id(int i, int j) => i * (n + 1) + j;
        // Triangles are emitted meridian by meridian from -Z through -X, +Z and +X (decreasing u), as SceneKit draws them
        // (measured: a double-sided transparent sphere shows its far side through the near side on the -X half only).
        for (int i = n - 1; i >= 0; i--)
            for (int j = 0; j < n; j++)
            {
                var mid = (b.p[Id(i, j)] + b.p[Id(i + 1, j + 1)]) * 0.5;
                if (j != 0) b.Triangle(e, Id(i, j), Id(i + 1, j), Id(i + 1, j + 1), mid);
                if (j != n - 1) b.Triangle(e, Id(i, j), Id(i + 1, j + 1), Id(i, j + 1), mid);
            }
        var (s, el) = b.Build();
        SetData(s, el);
    }
}

/// <summary>SCNCylinder: three elements (side, top, bottom) like SceneKit.</summary>
public sealed class SCNCylinder : SCNGeometry
{
    private double _r = 0.5, _h = 1;
    private int _radial = 48, _hseg = 1;
    public SCNCylinder() { }
    public SCNCylinder(double radius, double height) { _r = radius; _h = height; }
    public double radius { get => _r; set { _r = value; Rebuild(); } }
    public double height { get => _h; set { _h = value; Rebuild(); } }
    public int radialSegmentCount { get => _radial; set { _radial = value; Rebuild(); } }
    public int heightSegmentCount { get => _hseg; set { _hseg = value; Rebuild(); } }
    protected override SCNGeometry CopyShape() => new SCNCylinder(_r, _h) { _radial = _radial, _hseg = _hseg };
    protected override void Build()
    {
        int n = Math.Max(3, _radial), m = Math.Max(1, _hseg);
        double hh = _h / 2;
        var b = new PrimitiveBuilder();
        var side = b.Element();
        for (int i = 0; i <= n; i++)
        {
            double a = 2 * Math.PI * i / n;
            var dir = new SCNVector3(Math.Sin(a), 0, Math.Cos(a));
            for (int j = 0; j <= m; j++)
            {
                double y = -hh + _h * j / m;
                b.Vertex(new SCNVector3(dir.x * _r, y, dir.z * _r), dir, (double)i / n, 1 - (double)j / m);
            }
        }
        for (int i = 0; i < n; i++)
            for (int j = 0; j < m; j++)
            {
                int a0 = i * (m + 1) + j, a1 = (i + 1) * (m + 1) + j;
                var outward = (b.p[a0] + b.p[a1 + 1]) * 0.5; outward.y = 0;
                b.Triangle(side, a0, a1, a1 + 1, outward);
                b.Triangle(side, a0, a1 + 1, a0 + 1, outward);
            }
        foreach (var top in new[] { true, false })
        {
            var e = b.Element();
            double y = top ? hh : -hh;
            var normal = new SCNVector3(0, top ? 1 : -1, 0);
            int center = b.Vertex(new SCNVector3(0, y, 0), normal, 0.5, 0.5);
            int first = b.p.Count;
            for (int i = 0; i <= n; i++)
            {
                double a = 2 * Math.PI * i / n;
                double x = Math.Sin(a) * _r, z = Math.Cos(a) * _r;
                b.Vertex(new SCNVector3(x, y, z), normal, 0.5 - z / (2 * _r), top ? 0.5 - x / (2 * _r) : 0.5 + x / (2 * _r));
            }
            for (int i = 0; i < n; i++) b.Triangle(e, center, first + i, first + i + 1, normal);
        }
        var (s, el) = b.Build();
        SetData(s, el);
    }
}

/// <summary>SCNPlane in the XY plane facing +Z; uv (0,0) at the top left (measured).</summary>
public sealed class SCNPlane : SCNGeometry
{
    private double _w = 1, _h = 1, _corner;
    private int _ws = 1, _hs = 1, _cs = 10;
    public SCNPlane() { }
    public SCNPlane(double width, double height) { _w = width; _h = height; }
    public double width { get => _w; set { _w = value; Rebuild(); } }
    public double height { get => _h; set { _h = value; Rebuild(); } }
    public double cornerRadius { get => _corner; set { _corner = value; Rebuild(); } }
    public int widthSegmentCount { get => _ws; set { _ws = value; Rebuild(); } }
    public int heightSegmentCount { get => _hs; set { _hs = value; Rebuild(); } }
    public int cornerSegmentCount { get => _cs; set { _cs = value; Rebuild(); } }
    protected override SCNGeometry CopyShape() => new SCNPlane(_w, _h) { _corner = _corner, _ws = _ws, _hs = _hs, _cs = _cs };
    protected override void Build()
    {
        int nx = Math.Max(1, _ws), ny = Math.Max(1, _hs);
        var b = new PrimitiveBuilder();
        var e = b.Element();
        var normal = new SCNVector3(0, 0, 1);
        for (int i = 0; i <= nx; i++)
            for (int j = 0; j <= ny; j++)
            {
                double u = (double)i / nx, v = (double)j / ny;
                b.Vertex(new SCNVector3((u - 0.5) * _w, (0.5 - v) * _h, 0), normal, u, v);
            }
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                int a = i * (ny + 1) + j, c = (i + 1) * (ny + 1) + j;
                b.Triangle(e, a, c, c + 1, normal);
                b.Triangle(e, a, c + 1, a + 1, normal);
            }
        var (s, el) = b.Build();
        SetData(s, el);
    }
}

/// <summary>SCNFloor: a large plane at y = 0 facing +Y. PORT: reflections are not rendered (the game uses reflectivity 0).</summary>
public sealed class SCNFloor : SCNGeometry
{
    private double _width, _length;
    public double reflectivity = 0.25, reflectionFalloffStart, reflectionFalloffEnd, reflectionResolutionScaleFactor = 0.5;
    public int reflectionCategoryBitMask = -1;
    public SCNFloor() { }
    public double width { get => _width; set { _width = value; Rebuild(); } }
    public double length { get => _length; set { _length = value; Rebuild(); } }
    protected override SCNGeometry CopyShape() => new SCNFloor { _width = _width, _length = _length, reflectivity = reflectivity, reflectionFalloffStart = reflectionFalloffStart, reflectionFalloffEnd = reflectionFalloffEnd, reflectionResolutionScaleFactor = reflectionResolutionScaleFactor, reflectionCategoryBitMask = reflectionCategoryBitMask };
    protected override void Build()
    {
        double w = _width > 0 ? _width : 8192, l = _length > 0 ? _length : 8192;
        var b = new PrimitiveBuilder();
        var e = b.Element();
        var up = new SCNVector3(0, 1, 0);
        int a = b.Vertex(new SCNVector3(-w / 2, 0, -l / 2), up, -w / 2, -l / 2);
        int c = b.Vertex(new SCNVector3(w / 2, 0, -l / 2), up, w / 2, -l / 2);
        int d = b.Vertex(new SCNVector3(w / 2, 0, l / 2), up, w / 2, l / 2);
        int f = b.Vertex(new SCNVector3(-w / 2, 0, l / 2), up, -w / 2, l / 2);
        b.Triangle(e, a, c, d, up); b.Triangle(e, a, d, f, up);
        var (s, el) = b.Build();
        SetData(s, el);
    }
}

/// <summary>
/// SCNShape: an NSBezierPath filled (extrusionDepth 0: one front face, normal +Z) or
/// extruded (front, back, sides). Tessellation follows measured SceneKit behaviour:
/// curves are flattened with the path's flatness (default 0.6, so small curves become
/// chords), then each contour keeps only points at least 0.01 path units from the last point
/// it kept (see <see cref="Decimate"/>; contours left with fewer than three points vanish),
/// contained contours become holes by nesting depth and overlapping contours are unioned
/// (independent of winding rule direction).
/// </summary>
public sealed class SCNShape : SCNGeometry
{
    private NSBezierPath _path;
    private double _depth;
    public double chamferRadius;
    public SCNShape() { }
    /// <summary>SCNShape(path:extrusionDepth:).</summary>
    public SCNShape(NSBezierPath path, double extrusionDepth) { _path = path; _depth = extrusionDepth; }
    public NSBezierPath path { get => _path; set { _path = value; Rebuild(); } }
    public double extrusionDepth { get => _depth; set { _depth = value; Rebuild(); } }
    protected override SCNGeometry CopyShape() => new SCNShape(_path, _depth) { chamferRadius = chamferRadius };

    protected override void Build()
    {
        var b = new PrimitiveBuilder();
        if (_path == null) { var (s0, e0) = b.Build(); SetData(s0, e0); return; }
        double flat = _path.flatness > 0 ? _path.flatness : 0.6;
        var contours = _path.Contours(flat).Select(Decimate).Where(c => c.Count >= 3).ToList();
        // Nesting depth decides solid vs hole.
        var depth = contours.Select(c => contours.Count(o => o != c && c.All(p => Inside(o, p)))).ToList();
        var outers = new List<(List<CGPoint> outer, List<List<CGPoint>> holes)>();
        for (int i = 0; i < contours.Count; i++) if (depth[i] % 2 == 0) outers.Add((contours[i], new List<List<CGPoint>>()));
        for (int i = 0; i < contours.Count; i++)
        {
            if (depth[i] % 2 == 0) continue;
            var container = outers.Where(o => contours[i].All(p => Inside(o.outer, p))).OrderBy(o => Math.Abs(Area(o.outer))).FirstOrDefault();
            container.holes?.Add(contours[i]);
        }
        var bounds = _path.bounds;
        double minX = bounds.minX, maxY = bounds.maxY, w = Math.Max(bounds.width, 1e-9), h = Math.Max(bounds.height, 1e-9);
        double z = _depth / 2;
        var front = b.Element();
        List<int> back = _depth > 0 ? b.Element() : null;
        List<int> sides = _depth > 0 ? b.Element() : null;
        foreach (var (outer, holes) in outers)
        {
            var rings = new List<List<CGPoint>> { outer };
            rings.AddRange(holes);
            var data = new List<double>();
            var holeIdx = new List<int>();
            var flatPts = new List<CGPoint>();
            foreach (var ring in rings)
            {
                if (ring != outer) holeIdx.Add(flatPts.Count);
                foreach (var p in ring) { data.Add(p.x); data.Add(p.y); flatPts.Add(p); }
            }
            var tris = Earcut.Triangulate(data.ToArray(), holeIdx.ToArray());
            int baseFront = b.p.Count;
            foreach (var p in flatPts) b.Vertex(new SCNVector3(p.x, p.y, z), new SCNVector3(0, 0, 1), (p.x - minX) / w, (maxY - p.y) / h);
            for (int i = 0; i + 2 < tris.Count; i += 3) b.Triangle(front, baseFront + tris[i], baseFront + tris[i + 1], baseFront + tris[i + 2], new SCNVector3(0, 0, 1));
            if (_depth > 0)
            {
                int baseBack = b.p.Count;
                foreach (var p in flatPts) b.Vertex(new SCNVector3(p.x, p.y, -z), new SCNVector3(0, 0, -1), (p.x - minX) / w, (maxY - p.y) / h);
                for (int i = 0; i + 2 < tris.Count; i += 3) b.Triangle(back, baseBack + tris[i], baseBack + tris[i + 1], baseBack + tris[i + 2], new SCNVector3(0, 0, -1));
                foreach (var ring in rings)
                {
                    bool ccw = Area(ring) > 0;
                    bool isHole = ring != outer;
                    for (int i = 0; i < ring.Count; i++)
                    {
                        var p0 = ring[i]; var p1 = ring[(i + 1) % ring.Count];
                        double dx = p1.x - p0.x, dy = p1.y - p0.y, len = Math.Sqrt(dx * dx + dy * dy);
                        if (len < 1e-12) continue;
                        var nrm = new SCNVector3(dy / len, -dx / len, 0);
                        if (ccw == isHole) nrm = nrm * -1;
                        int a0 = b.Vertex(new SCNVector3(p0.x, p0.y, z), nrm, 0, 0), a1 = b.Vertex(new SCNVector3(p1.x, p1.y, z), nrm, 1, 0);
                        int b0 = b.Vertex(new SCNVector3(p0.x, p0.y, -z), nrm, 0, 1), b1 = b.Vertex(new SCNVector3(p1.x, p1.y, -z), nrm, 1, 1);
                        b.Triangle(sides, a0, a1, b1, nrm); b.Triangle(sides, a0, b1, b0, nrm);
                    }
                }
            }
        }
        var (s, el) = b.Build();
        SetData(s, el);
    }
    /// <summary>Minimum distance between the points SceneKit keeps on a flattened contour (path units).</summary>
    internal const double MinimumSpacing = 0.01;
    /// <summary>
    /// Measured (tools/scenekit-reference/robots/ShapeTessellation.swift, macOS 27): SceneKit walks each flattened
    /// contour and drops every point closer than 0.01 path units to the last point it kept, independent of flatness
    /// and of the path's size (a 64-gon of radius 0.1 keeps every second point, of radius 0.02 every sixth), then
    /// drops trailing points closer than 0.01 to the contour's first point. Contours left with fewer than three
    /// points vanish (an oval of diameter 0.013 flattens to a diamond with 0.0092 sides: the robot eyes' end caps).
    /// Collinear points and thin or tiny contours with sides of at least 0.01 are kept. Marvin's eye arcs lose their
    /// last outer and inner points this way, which gives their ends SceneKit's slanted cut.
    /// </summary>
    private static List<CGPoint> Decimate(List<CGPoint> pts)
    {
        static double Distance(CGPoint a, CGPoint b) => Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y));
        var r = new List<CGPoint>();
        foreach (var p in pts) if (r.Count == 0 || Distance(p, r[^1]) >= MinimumSpacing) r.Add(p);
        while (r.Count > 1 && Distance(r[^1], r[0]) < MinimumSpacing) r.RemoveAt(r.Count - 1);
        return r;
    }
    private static double Area(List<CGPoint> c)
    {
        double s = 0;
        for (int i = 0; i < c.Count; i++) { var a = c[i]; var b = c[(i + 1) % c.Count]; s += a.x * b.y - b.x * a.y; }
        return s / 2;
    }
    private static bool Inside(List<CGPoint> poly, CGPoint p)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            if ((poly[i].y > p.y) != (poly[j].y > p.y) && p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x) inside = !inside;
        }
        return inside;
    }
}

/// <summary>
/// SCNText: glyph outlines from Godot's TextMesh, placed like SceneKit (measured with
/// monospacedSystemFont: pen starts at x = 0, baseline at y = 1.0).
/// </summary>
public sealed class SCNText : SCNGeometry
{
    private string _string;
    private double _depth;
    private NSFont _font = NSFont.systemFont(36);
    private double _flatness = 0.6;
    public SCNText() { }
    /// <summary>SCNText(string:extrusionDepth:).</summary>
    public SCNText(string @string, double extrusionDepth) { _string = @string; _depth = extrusionDepth; }
    public string @string { get => _string; set { _string = value; Rebuild(); } }
    public double extrusionDepth { get => _depth; set { _depth = value; Rebuild(); } }
    public NSFont font { get => _font; set { _font = value; Rebuild(); } }
    public double flatness { get => _flatness; set { _flatness = value; Rebuild(); } }
    protected override SCNGeometry CopyShape() => new SCNText(_string, _depth) { _font = _font, _flatness = _flatness, chamferRadius = chamferRadius, isWrapped = isWrapped, containerFrame = containerFrame };
    public double chamferRadius;
    public bool isWrapped;
    public CGRect containerFrame;
    protected override void Build()
    {
        var b = new PrimitiveBuilder();
        var front = b.Element();
        if (!string.IsNullOrEmpty(_string))
        {
            const int px = 64;
            double size = _font.pointSize;
            var tm = new TextMesh
            {
                Text = _string, Font = _font.godotFont, FontSize = px, PixelSize = (float)(size / px), Depth = (float)_depth,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                CurveStep = (float)Math.Clamp(_flatness * 2, 0.1, 10),
            };
            var arrays = tm.GetMeshArrays();
            if (arrays.Count > 0 && arrays[(int)Mesh.ArrayType.Vertex].VariantType != Variant.Type.Nil)
            {
                var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var norms = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
                var uvs = arrays[(int)Mesh.ArrayType.TexUV].VariantType != Variant.Type.Nil ? arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array() : new Vector2[verts.Length];
                var idx = arrays[(int)Mesh.ArrayType.Index].VariantType != Variant.Type.Nil ? arrays[(int)Mesh.ArrayType.Index].AsInt32Array() : Enumerable.Range(0, verts.Length).ToArray();
                // Godot places the top of the line at y = 0; SceneKit's baseline is at y = 1.0.
                double ascent = _font.godotFont.GetAscent(px) * size / px;
                double dy = 1.0 + ascent;
                double dz = _depth / 2;
                for (int i = 0; i < verts.Length; i++)
                    b.Vertex(new SCNVector3(verts[i].X, verts[i].Y + dy, verts[i].Z + (_depth > 0 ? 0 : 0) + (_depth > 0 ? dz - _depth / 2 : 0)), new SCNVector3(norms[i].X, norms[i].Y, norms[i].Z), uvs[i].X, uvs[i].Y);
                // Godot triangles are clockwise: swap to SceneKit's counter-clockwise order.
                for (int i = 0; i + 2 < idx.Length; i += 3) { front.Add(idx[i]); front.Add(idx[i + 2]); front.Add(idx[i + 1]); }
            }
        }
        var (s, el) = b.Build();
        SetData(s, el);
    }
}
