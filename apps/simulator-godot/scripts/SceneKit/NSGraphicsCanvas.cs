using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Marvin.SceneKit;

/// <summary>
/// NSGraphicsContext backend for view drawing (NSView.draw, cacheDisplay): AppKit drawing becomes Godot
/// canvas-item commands (RenderingServer) in device space (the canvas item's pixels, y down).
/// Anti-aliasing reproduces Quartz's area coverage with a one-pixel feather instead of MSAA:
/// - fills: the contour inset by half a pixel is filled solid, and a ring to the contour outset by half a
///   pixel ramps the alpha to zero (exact box-filter coverage for straight edges);
/// - strokes: a triangle strip along the polyline whose cross-section is the box-filtered line profile
///   (full alpha within |d| ≤ (w-1)/2, zero at (w+1)/2; peak w for hairlines), mitered joins (round joins
///   are approximated by the miter on smooth paths), feathered butt/square caps and round end discs;
/// - fills with several contours, holes or self-intersections fall back to the CPU rasteriser
///   (NSGraphicsContext.RasterizeImage) drawn as a texture.
/// Text uses TextServer.FontDrawGlyph (Godot's glyph cache, quarter-pixel positioning); images draw their
/// texture upright. A clip (addClip) starts a new child canvas item clipped to the rectangle; once any
/// child exists every later command goes to a new child, so painter's order is preserved. Child items
/// sort before the view's subviews (negative draw index).
/// </summary>
internal sealed class NSGraphicsCanvas : IDisposable
{
    private readonly Rid root;
    private readonly List<Rid> segments = new();
    private readonly List<GodotObject> keepAlive = new();
    private Rid current;
    private CGRect? currentClip;
    private bool usingRoot = true;

    internal NSGraphicsCanvas(Rid root) { this.root = root; current = root; }

    /// <summary>Frees the child items of the previous drawing (call before each redraw).</summary>
    internal void Reset()
    {
        foreach (var s in segments) RenderingServer.FreeRid(s);
        segments.Clear(); keepAlive.Clear();
        current = root; currentClip = null; usingRoot = true;
    }
    public void Dispose() => Reset();

    private Rid Target(CGRect? clip)
    {
        if (usingRoot && clip == null) return root;
        if (!usingRoot && Nullable.Equals(clip, currentClip)) return current;
        var item = RenderingServer.CanvasItemCreate();
        RenderingServer.CanvasItemSetParent(item, root);
        RenderingServer.CanvasItemSetDrawIndex(item, -1_000_000 + segments.Count);
        if (clip is CGRect r)
        {
            RenderingServer.CanvasItemSetClip(item, true);
            RenderingServer.CanvasItemSetCustomRect(item, true, new Rect2((float)r.minX, (float)r.minY, (float)r.width, (float)r.height));
        }
        segments.Add(item);
        current = item; currentClip = clip; usingRoot = false;
        return item;
    }

    private static Color ToColor(NSColor color)
    {
        var c = color.usingColorSpace(NSColorSpace.sRGB);
        return new Color((float)c.redComponent, (float)c.greenComponent, (float)c.blueComponent, (float)color.alphaComponent);
    }

    // ---- Fills
    internal void Fill(List<List<CGPoint>> contours, bool evenOdd, NSColor color, CGRect? clip)
    {
        if (color.alphaComponent <= 0) return;
        var polys = contours.Select(Clean).Where(c => c.Count >= 3).ToList();
        if (polys.Count == 0) return;
        var item = Target(clip);
        var c = ToColor(color);
        if (polys.Count == 1 && IsSimple(polys[0])) { FeatherFill(item, polys[0], c); return; }
        var image = NSGraphicsContext.RasterizeImage(polys.Select(p => p.Select(q => new CGPoint(q.X, q.Y)).ToList()).ToList(), evenOdd, color, out var rect);
        var texture = ImageTexture.CreateFromImage(image);
        keepAlive.Add(texture);
        RenderingServer.CanvasItemAddTextureRect(item, new Rect2(rect.Position, rect.Size), texture.GetRid(), false, new Color(1, 1, 1, 1));
    }

    private static List<Vector2> Clean(List<CGPoint> contour)
    {
        var result = new List<Vector2>(contour.Count);
        foreach (var p in contour)
        {
            var v = new Vector2((float)p.x, (float)p.y);
            if (result.Count == 0 || result[^1].DistanceSquaredTo(v) > 1e-8f) result.Add(v);
        }
        while (result.Count > 1 && result[0].DistanceSquaredTo(result[^1]) <= 1e-8f) result.RemoveAt(result.Count - 1);
        return result;
    }
    private static double SignedArea(IReadOnlyList<Vector2> p)
    {
        double a = 0;
        for (int i = 0; i < p.Count; i++) { var u = p[i]; var v = p[(i + 1) % p.Count]; a += (double)u.X * v.Y - (double)v.X * u.Y; }
        return a / 2;
    }
    /// <summary>True when no two non-adjacent edges intersect (convex polygons skip the quadratic test).</summary>
    private static bool IsSimple(List<Vector2> p)
    {
        int n = p.Count;
        if (n <= 3) return true;
        bool convex = true; int sign = 0;
        for (int i = 0; i < n && convex; i++)
        {
            var a = p[i]; var b = p[(i + 1) % n]; var c = p[(i + 2) % n];
            double cross = (double)(b.X - a.X) * (c.Y - b.Y) - (double)(b.Y - a.Y) * (c.X - b.X);
            if (Math.Abs(cross) < 1e-9) continue;
            int s = Math.Sign(cross);
            if (sign == 0) sign = s; else if (s != sign) convex = false;
        }
        if (convex) return true;
        for (int i = 0; i < n; i++)
            for (int j = i + 2; j < n; j++)
            {
                if (i == 0 && j == n - 1) continue;
                if (Geometry2D.SegmentIntersectsSegment(p[i], p[(i + 1) % n], p[j], p[(j + 1) % n]).VariantType != Variant.Type.Nil) return false;
            }
        return true;
    }
    /// <summary>Per-vertex offset (outward, unit edge distance) of a simple polygon; miter clamped at 4.</summary>
    private static Vector2[] Offsets(List<Vector2> p, bool positive)
    {
        int n = p.Count;
        var normals = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            var d = (p[(i + 1) % n] - p[i]).Normalized();
            // For positive signed area the interior is left of each edge (-d.y, d.x); outward is (d.y, -d.x).
            normals[i] = positive ? new Vector2(d.Y, -d.X) : new Vector2(-d.Y, d.X);
        }
        var offsets = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            var a = normals[(i - 1 + n) % n]; var b = normals[i];
            var m = a + b;
            float len2 = m.LengthSquared();
            offsets[i] = len2 < 1e-6f ? b : m * Math.Min(4f, 2f / len2) / 1f;
            if (offsets[i].Length() > 4f) offsets[i] = offsets[i].Normalized() * 4f;
        }
        return offsets;
    }
    private void FeatherFill(Rid item, List<Vector2> p, Color color)
    {
        int n = p.Count;
        double area = SignedArea(p);
        if (Math.Abs(area) < 1e-6) return;
        var off = Offsets(p, area > 0);
        var inner = new Vector2[n]; var outer = new Vector2[n];
        for (int i = 0; i < n; i++) { inner[i] = p[i] - off[i] * 0.5f; outer[i] = p[i] + off[i] * 0.5f; }
        var points = new List<Vector2>(3 * n); var colors = new List<Color>(3 * n); var indices = new List<int>(12 * n);
        var clear = new Color(color.R, color.G, color.B, 0);
        // Solid interior (the inset contour), when it keeps its orientation.
        double innerArea = SignedArea(inner);
        if (Math.Sign(innerArea) == Math.Sign(area) && Math.Abs(innerArea) > 1e-4)
        {
            var tri = Geometry2D.TriangulatePolygon(inner);
            if (tri.Length >= 3)
            {
                for (int i = 0; i < n; i++) { points.Add(inner[i]); colors.Add(color); }
                indices.AddRange(tri);
            }
        }
        // Feather ring: alpha ramps from the inset contour to the outset contour.
        int ring = points.Count;
        for (int i = 0; i < n; i++) { points.Add(inner[i]); colors.Add(color); }
        for (int i = 0; i < n; i++) { points.Add(outer[i]); colors.Add(clear); }
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            indices.Add(ring + i); indices.Add(ring + j); indices.Add(ring + n + j);
            indices.Add(ring + i); indices.Add(ring + n + j); indices.Add(ring + n + i);
        }
        RenderingServer.CanvasItemAddTriangleArray(item, indices.ToArray(), points.ToArray(), colors.ToArray());
    }

    // ---- Strokes
    internal void Stroke(List<CGPoint> polyline, bool closed, double width, NSBezierPath.LineJoinStyle join, NSBezierPath.LineCapStyle cap, double miterLimit, NSColor color, CGRect? clip)
    {
        if (color.alphaComponent <= 0 || width <= 0) return;
        var p = Clean(polyline);
        if (closed && p.Count < 3) closed = false;
        var item = Target(clip);
        var c = ToColor(color);
        float h = (float)width / 2;
        if (p.Count < 2)
        {
            if (p.Count == 1 && cap != NSBezierPath.LineCapStyle.butt) FeatherFill(item, CapShape(p[0], Vector2.Right, h, cap, true), c);
            return;
        }
        // Box-filtered cross-section: |d| ≤ inner at peak alpha, ramping to zero at |d| = outerD.
        float innerD = Math.Abs((float)width - 1) / 2, outerD = ((float)width + 1) / 2, peak = (float)Math.Min(1, width);
        var profile = new[] { -outerD, -innerD, innerD, outerD };
        var alpha = new[] { 0f, peak, peak, 0f };
        int n = p.Count;
        var pts = new List<Vector2>(p);
        // Open paths: square caps extend by half the width; butt edges get their own half-pixel feather.
        Vector2 startDir = (p[1] - p[0]).Normalized(), endDir = (p[n - 1] - p[n - 2]).Normalized();
        if (!closed && cap == NSBezierPath.LineCapStyle.square) { pts[0] -= startDir * h; pts[n - 1] += endDir * h; }
        int count = closed ? n + 1 : n;
        var points = new List<Vector2>(); var colors = new List<Color>(); var indices = new List<int>();
        Vector2 Normal(Vector2 d) => new(-d.Y, d.X);
        Vector2 Miter(int k)
        {
            if (!closed && k == 0) return Normal(startDir);
            if (!closed && k == n - 1) return Normal(endDir);
            var a = Normal((pts[k % n] - pts[(k - 1 + n) % n]).Normalized());
            var b = Normal((pts[(k + 1) % n] - pts[k % n]).Normalized());
            var m = a + b;
            float len2 = m.LengthSquared();
            if (len2 < 1e-6f) return b;
            // 1/cos(half angle) = 2/|a+b|²·|a+b|; beyond the miter limit (or for round/bevel joins at sharp
            // turns) the offset is clamped, which approximates a bevel.
            float limit = join == NSBezierPath.LineJoinStyle.miter ? (float)Math.Max(1, miterLimit) : 2f;
            var miter = m * (2f / len2);
            return miter.Length() > limit ? miter.Normalized() * limit : miter;
        }
        for (int k = 0; k < count; k++)
        {
            var center = pts[k % n];
            var m = Miter(k % n);
            for (int b = 0; b < 4; b++) { points.Add(center + m * profile[b]); colors.Add(new Color(c.R, c.G, c.B, c.A * alpha[b])); }
            if (k == 0) continue;
            int s0 = (k - 1) * 4, s1 = k * 4;
            for (int b = 0; b < 3; b++)
            {
                indices.Add(s0 + b); indices.Add(s0 + b + 1); indices.Add(s1 + b + 1);
                indices.Add(s0 + b); indices.Add(s1 + b + 1); indices.Add(s1 + b);
            }
        }
        if (!closed && cap != NSBezierPath.LineCapStyle.round)
        {
            // Feather across the flat ends: ramp from the end section (moved in by half a pixel) to zero half a pixel beyond.
            void EndFeather(int section, Vector2 outward)
            {
                int baseIndex = section * 4;
                for (int b = 0; b < 4; b++) points[baseIndex + b] -= outward * 0.5f;
                int start = points.Count;
                for (int b = 0; b < 4; b++) { points.Add(points[baseIndex + b] + outward); colors.Add(new Color(c.R, c.G, c.B, 0)); }
                for (int b = 0; b < 3; b++)
                {
                    indices.Add(baseIndex + b); indices.Add(baseIndex + b + 1); indices.Add(start + b + 1);
                    indices.Add(baseIndex + b); indices.Add(start + b + 1); indices.Add(start + b);
                }
            }
            if ((pts[1] - pts[0]).Length() > 1) EndFeather(0, -startDir);
            if ((pts[n - 1] - pts[n - 2]).Length() > 1) EndFeather(n - 1, endDir);
        }
        RenderingServer.CanvasItemAddTriangleArray(item, indices.ToArray(), points.ToArray(), colors.ToArray());
        if (!closed && cap == NSBezierPath.LineCapStyle.round)
        {
            FeatherFill(item, CapShape(p[0], -startDir, h, cap, false), c);
            FeatherFill(item, CapShape(p[n - 1], endDir, h, cap, false), c);
        }
    }
    /// <summary>Round cap: half disc beyond the end (full disc for a single point); square cap of a single point.</summary>
    private static List<Vector2> CapShape(Vector2 center, Vector2 outward, float h, NSBezierPath.LineCapStyle cap, bool full)
    {
        if (cap == NSBezierPath.LineCapStyle.square)
            return new List<Vector2> { center + new Vector2(-h, -h), center + new Vector2(h, -h), center + new Vector2(h, h), center + new Vector2(-h, h) };
        int steps = Math.Max(8, (int)Math.Ceiling(Math.PI * h / 0.5));
        float start = full ? 0 : outward.Angle() - Mathf.Pi / 2, span = full ? Mathf.Tau : Mathf.Pi;
        var result = new List<Vector2>();
        for (int i = 0; i <= steps - (full ? 1 : 0); i++) result.Add(center + Vector2.FromAngle(start + span * i / steps) * h);
        return result;
    }

    // ---- Text and images
    internal void Text(NSAttributedString.Line line, CGPoint deviceBaseline, AffineTransform ctm, bool flipped, Color color, CGRect? clip)
    {
        if (color.A <= 0 || line.glyphs.Count == 0) return;
        var item = Target(clip);
        var ts = TextServerManager.GetPrimaryInterface();
        bool upright = Math.Abs(ctm.m12) < 1e-9 && Math.Abs(ctm.m21) < 1e-9 && Math.Abs(ctm.m11 - 1) < 1e-9 && Math.Abs(Math.Abs(ctm.m22) - 1) < 1e-9;
        var origin = new Vector2((float)deviceBaseline.x, (float)deviceBaseline.y);
        if (!upright)
        {
            // Glyphs follow the user space's x axis and its "down" direction (upright text in a rotated or scaled CTM).
            var xAxis = new Vector2((float)ctm.m11, (float)ctm.m12);
            var down = flipped ? new Vector2((float)ctm.m21, (float)ctm.m22) : new Vector2((float)-ctm.m21, (float)-ctm.m22);
            RenderingServer.CanvasItemAddSetTransform(item, new Transform2D(xAxis, down, origin));
            origin = Vector2.Zero;
        }
        foreach (var (font, index, offset, x) in line.glyphs)
            ts.FontDrawGlyph(font, item, line.size, origin + new Vector2((float)x + offset.X, offset.Y), index, color);
        if (!upright) RenderingServer.CanvasItemAddSetTransform(item, Transform2D.Identity);
    }
    internal void Image(NSImage image, CGRect deviceRect, double fraction, CGRect? clip, bool flipped)
    {
        var texture = image?.GodotTexture;
        if (texture == null || deviceRect.width <= 0 || deviceRect.height <= 0 || fraction <= 0) return;
        var item = Target(clip);
        keepAlive.Add(texture);
        RenderingServer.CanvasItemAddTextureRect(item, new Rect2((float)deviceRect.minX, (float)deviceRect.minY, (float)deviceRect.width, (float)deviceRect.height), texture.GetRid(), false, new Color(1, 1, 1, (float)fraction));
    }
}
