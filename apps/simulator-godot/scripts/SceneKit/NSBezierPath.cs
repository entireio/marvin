using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Marvin.SceneKit;

/// <summary>NSBezierPath: path construction, flattening (for SCNShape) and simple filling/stroking into bitmaps.</summary>
public sealed class NSBezierPath
{
    public enum WindingRule { nonZero = 0, evenOdd = 1 }
    public enum LineCapStyle { butt = 0, round = 1, square = 2 }
    public enum LineJoinStyle { miter = 0, round = 1, bevel = 2 }
    public enum ElementType { moveTo, lineTo, curveTo, closePath }

    internal readonly List<(ElementType type, CGPoint p, CGPoint c1, CGPoint c2)> elements = new();
    public WindingRule windingRule = WindingRule.nonZero;
    /// <summary>flatness: maximum chord error when curves are flattened (AppKit default 0.6).</summary>
    public double flatness = 0.6;
    public double lineWidth = 1;
    public LineCapStyle lineCapStyle;
    public LineJoinStyle lineJoinStyle;
    public double miterLimit = 10;
    private CGPoint current, subpathStart;

    public NSBezierPath() { }
    /// <summary>NSBezierPath(rect:).</summary>
    public NSBezierPath(CGRect rect) { appendRect(rect); }
    /// <summary>NSBezierPath(ovalIn:).</summary>
    public static NSBezierPath ovalIn(CGRect rect) { var p = new NSBezierPath(); p.appendOval(rect); return p; }
    /// <summary>NSBezierPath(roundedRect:xRadius:yRadius:).</summary>
    public static NSBezierPath roundedRect(CGRect rect, double xRadius, double yRadius) { var p = new NSBezierPath(); p.appendRoundedRect(rect, xRadius, yRadius); return p; }

    public void move(CGPoint to) { elements.Add((ElementType.moveTo, to, default, default)); current = subpathStart = to; }
    public void line(CGPoint to) { if (elements.Count == 0) move(current); elements.Add((ElementType.lineTo, to, default, default)); current = to; }
    public void curve(CGPoint to, CGPoint controlPoint1, CGPoint controlPoint2) { if (elements.Count == 0) move(current); elements.Add((ElementType.curveTo, to, controlPoint1, controlPoint2)); current = to; }
    public void relativeMove(CGPoint to) => move(new CGPoint(current.x + to.x, current.y + to.y));
    public void relativeLine(CGPoint to) => line(new CGPoint(current.x + to.x, current.y + to.y));
    public void close() { elements.Add((ElementType.closePath, subpathStart, default, default)); current = subpathStart; }
    public void appendRect(CGRect r)
    {
        move(new CGPoint(r.minX, r.minY)); line(new CGPoint(r.maxX, r.minY)); line(new CGPoint(r.maxX, r.maxY)); line(new CGPoint(r.minX, r.maxY)); close();
    }
    /// <summary>appendOval(in:): four cubic segments, counter-clockwise (y up), starting at 315° like AppKit.</summary>
    public void appendOval(CGRect r)
    {
        double cx = r.midX, cy = r.midY, rx = r.width / 2, ry = r.height / 2;
        const double k = 0.5522847498;
        // AppKit's oval starts at the lower-right 45° point.
        double s = Math.Sqrt(0.5);
        CGPoint P(double a) => new(cx + rx * Math.Cos(a), cy + ry * Math.Sin(a));
        CGPoint T(double a, double sign) => new(-Math.Sin(a) * rx * k * sign, Math.Cos(a) * ry * k * sign);
        double start = -Math.PI / 4;
        move(P(start));
        for (int i = 0; i < 4; i++)
        {
            double a0 = start + i * Math.PI / 2, a1 = a0 + Math.PI / 2;
            var p0 = P(a0); var p1 = P(a1); var t0 = T(a0, 1); var t1 = T(a1, 1);
            curve(p1, new CGPoint(p0.x + t0.x, p0.y + t0.y), new CGPoint(p1.x - t1.x, p1.y - t1.y));
        }
        _ = s;
        close();
    }
    public void appendRoundedRect(CGRect r, double xRadius, double yRadius)
    {
        double rx = Math.Min(xRadius, r.width / 2), ry = Math.Min(yRadius, r.height / 2);
        if (rx <= 0 || ry <= 0) { appendRect(r); return; }
        const double k = 0.5522847498;
        move(new CGPoint(r.minX + rx, r.minY));
        line(new CGPoint(r.maxX - rx, r.minY));
        curve(new CGPoint(r.maxX, r.minY + ry), new CGPoint(r.maxX - rx + rx * k, r.minY), new CGPoint(r.maxX, r.minY + ry - ry * k));
        line(new CGPoint(r.maxX, r.maxY - ry));
        curve(new CGPoint(r.maxX - rx, r.maxY), new CGPoint(r.maxX, r.maxY - ry + ry * k), new CGPoint(r.maxX - rx + rx * k, r.maxY));
        line(new CGPoint(r.minX + rx, r.maxY));
        curve(new CGPoint(r.minX, r.maxY - ry), new CGPoint(r.minX + rx - rx * k, r.maxY), new CGPoint(r.minX, r.maxY - ry + ry * k));
        line(new CGPoint(r.minX, r.minY + ry));
        curve(new CGPoint(r.minX + rx, r.minY), new CGPoint(r.minX, r.minY + ry - ry * k), new CGPoint(r.minX + rx - rx * k, r.minY));
        close();
    }
    public void appendArc(CGPoint withCenter, double radius, double startAngle, double endAngle, bool clockwise = false)
    {
        double a0 = startAngle * Math.PI / 180, a1 = endAngle * Math.PI / 180;
        if (!clockwise) { while (a1 < a0) a1 += 2 * Math.PI; } else { while (a1 > a0) a1 -= 2 * Math.PI; }
        int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(a1 - a0) / (Math.PI / 2)));
        var start = new CGPoint(withCenter.x + radius * Math.Cos(a0), withCenter.y + radius * Math.Sin(a0));
        if (elements.Count == 0) move(start); else line(start);
        for (int i = 0; i < n; i++)
        {
            double b0 = a0 + (a1 - a0) * i / n, b1 = a0 + (a1 - a0) * (i + 1) / n;
            double k = 4.0 / 3 * Math.Tan((b1 - b0) / 4) * radius;
            var p0 = new CGPoint(withCenter.x + radius * Math.Cos(b0), withCenter.y + radius * Math.Sin(b0));
            var p1 = new CGPoint(withCenter.x + radius * Math.Cos(b1), withCenter.y + radius * Math.Sin(b1));
            curve(p1, new CGPoint(p0.x - k * Math.Sin(b0), p0.y + k * Math.Cos(b0)), new CGPoint(p1.x + k * Math.Sin(b1), p1.y - k * Math.Cos(b1)));
        }
    }
    public void append(NSBezierPath path) { foreach (var e in path.elements) elements.Add(e); current = path.current; subpathStart = path.subpathStart; }
    public void removeAllPoints() => elements.Clear();
    public void setLineDash(double[] pattern, int count, double phase) { }
    public int elementCount => elements.Count;
    public bool isEmpty => elements.Count == 0;
    public CGPoint currentPoint => current;
    public NSBezierPath reversed
    {
        get
        {
            var r = new NSBezierPath { windingRule = windingRule, flatness = flatness, lineWidth = lineWidth };
            foreach (var c in Contours(0.001)) { r.move(c[^1]); for (int i = c.Count - 2; i >= 0; i--) r.line(c[i]); r.close(); }
            return r;
        }
    }
    public CGRect bounds
    {
        get
        {
            var pts = Contours(flatness).SelectMany(c => c).ToList();
            if (pts.Count == 0) return CGRect.zero;
            double x0 = pts.Min(p => p.x), x1 = pts.Max(p => p.x), y0 = pts.Min(p => p.y), y1 = pts.Max(p => p.y);
            return new CGRect(x0, y0, x1 - x0, y1 - y0);
        }
    }
    public void transform(Func<CGPoint, CGPoint> using_)
    {
        for (int i = 0; i < elements.Count; i++)
        {
            var e = elements[i];
            elements[i] = (e.type, using_(e.p), using_(e.c1), using_(e.c2));
        }
    }

    /// <summary>Flattened subpaths. Curves are subdivided until their control points lie within `tolerance` of the chord.</summary>
    internal List<List<CGPoint>> Contours(double tolerance)
    {
        var result = new List<List<CGPoint>>();
        List<CGPoint> cur = null;
        CGPoint last = default;
        foreach (var (type, p, c1, c2) in elements)
        {
            switch (type)
            {
                case ElementType.moveTo:
                    if (cur != null && cur.Count > 1) result.Add(cur);
                    cur = new List<CGPoint> { p }; last = p; break;
                case ElementType.lineTo:
                    cur ??= new List<CGPoint> { last };
                    cur.Add(p); last = p; break;
                case ElementType.curveTo:
                    cur ??= new List<CGPoint> { last };
                    Flatten(last, c1, c2, p, tolerance, cur, 0); last = p; break;
                case ElementType.closePath:
                    if (cur != null && cur.Count > 1) result.Add(cur);
                    cur = null; last = p; break;
            }
        }
        if (cur != null && cur.Count > 1) result.Add(cur);
        foreach (var c in result) if (c.Count > 2 && c[0] == c[^1]) c.RemoveAt(c.Count - 1);
        return result;
    }
    private static void Flatten(CGPoint p0, CGPoint p1, CGPoint p2, CGPoint p3, double tol, List<CGPoint> output, int depth)
    {
        double d1 = DistToLine(p1, p0, p3), d2 = DistToLine(p2, p0, p3);
        if (depth > 16 || Math.Max(d1, d2) <= tol) { output.Add(p3); return; }
        CGPoint M(CGPoint a, CGPoint b) => new((a.x + b.x) / 2, (a.y + b.y) / 2);
        var p01 = M(p0, p1); var p12 = M(p1, p2); var p23 = M(p2, p3);
        var p012 = M(p01, p12); var p123 = M(p12, p23); var mid = M(p012, p123);
        Flatten(p0, p01, p012, mid, tol, output, depth + 1);
        Flatten(mid, p123, p23, p3, tol, output, depth + 1);
    }
    private static double DistToLine(CGPoint p, CGPoint a, CGPoint b)
    {
        double dx = b.x - a.x, dy = b.y - a.y, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-15) return Math.Sqrt((p.x - a.x) * (p.x - a.x) + (p.y - a.y) * (p.y - a.y));
        return Math.Abs((p.x - a.x) * dy - (p.y - a.y) * dx) / len;
    }

    // ---- drawing into the current NSGraphicsContext (bitmap)
    /// <summary>addClip(): the facade clips to the path's bounding rectangle.</summary>
    public void addClip() { if (NSGraphicsContext.current != null) NSGraphicsContext.current.clip = bounds; }
    public void fill() => NSGraphicsContext.current?.FillPath(this, NSGraphicsContext.FillColor);
    public void stroke() => NSGraphicsContext.current?.StrokePath(this, NSGraphicsContext.StrokeColor, lineWidth);
    public static void fillRect(CGRect r) => new NSBezierPath(r).fill();
    public static void strokeRect(CGRect r) => new NSBezierPath(r).stroke();
}

/// <summary>NSRect.fill() / NSRectFill and NSColor.setFill() helpers.</summary>
public static class AppKitDrawing
{
    public static void fill(this CGRect rect) => NSBezierPath.fillRect(rect);
    public static void frame(this CGRect rect) => NSBezierPath.strokeRect(rect);
}

/// <summary>
/// NSGraphicsContext over an NSBitmapImageRep: anti-aliased polygon fill and stroke
/// with source-over blending, AppKit coordinates (origin bottom-left).
/// Text drawing (NSAttributedString.draw) is not implemented. PORT: see PORTING.md.
/// </summary>
public sealed class NSGraphicsContext
{
    // Per thread, as in AppKit (images may be drawn on a builder thread).
    [ThreadStatic] public static NSGraphicsContext current;
    [ThreadStatic] private static Stack<(NSGraphicsContext ctx, NSColor fill, NSColor stroke)> stackStorage;
    [ThreadStatic] private static NSColor fillColor, strokeColor;
    private static Stack<(NSGraphicsContext ctx, NSColor fill, NSColor stroke)> stack => stackStorage ??= new();
    internal static NSColor FillColor { get => fillColor ?? NSColor.black; set => fillColor = value; }
    internal static NSColor StrokeColor { get => strokeColor ?? NSColor.black; set => strokeColor = value; }
    internal readonly NSBitmapImageRep rep;
    public bool shouldAntialias = true;
    /// <summary>isFlipped: y grows downwards (NSImage.lockFocusFlipped(true)).</summary>
    public readonly bool isFlipped;
    internal CGRect? clip;
    internal NSGraphicsContext(NSBitmapImageRep rep, bool flipped = false) { this.rep = rep; isFlipped = flipped; }
    /// <summary>NSGraphicsContext(bitmapImageRep:).</summary>
    public static NSGraphicsContext bitmapImageRep(NSBitmapImageRep rep) => new(rep);
    public static void saveGraphicsState() => stack.Push((current, FillColor, StrokeColor));
    public static void restoreGraphicsState() { if (stack.Count > 0) (current, FillColor, StrokeColor) = stack.Pop(); }

    /// <summary>Draws an image into rect (source-over, bilinear), honouring the clip rect.</summary>
    internal void DrawImage(NSImage image, CGRect rect, double fraction)
    {
        var src = image.GodotImage;
        if (src == null || rect.width <= 0 || rect.height <= 0) return;
        if (src.IsCompressed()) { src = (Image)src.Duplicate(); src.Decompress(); }
        int w = rep.pixelsWide, h = rep.pixelsHigh, sw = src.GetWidth(), sh = src.GetHeight();
        for (int py = Math.Max(0, (int)Math.Floor(rect.minY)); py < Math.Min(h, (int)Math.Ceiling(rect.maxY)); py++)
            for (int px = Math.Max(0, (int)Math.Floor(rect.minX)); px < Math.Min(w, (int)Math.Ceiling(rect.maxX)); px++)
            {
                double x = px + 0.5, y = py + 0.5;
                if (clip is CGRect c && !c.contains(new CGPoint(x, y))) continue;
                double u = (x - rect.minX) / rect.width, v = (y - rect.minY) / rect.height;
                // AppKit draws images upright in unflipped contexts: v = 0 at the top of the image is the rect's maxY.
                double sy = isFlipped ? v * sh : (1 - v) * sh;
                var col = src.GetPixel(Math.Clamp((int)(u * sw), 0, sw - 1), Math.Clamp((int)sy, 0, sh - 1));
                double a = col.A * fraction;
                if (a <= 0) continue;
                Blend(px, isFlipped ? py : h - 1 - py, col.R, col.G, col.B, a);
            }
        rep.version++;
    }

    internal void FillPath(NSBezierPath path, NSColor color)
    {
        var contours = path.Contours(Math.Min(path.flatness, 0.25));
        Rasterize(contours, path.windingRule == NSBezierPath.WindingRule.evenOdd, color);
    }
    internal void StrokePath(NSBezierPath path, NSColor color, double width)
    {
        var quads = new List<List<CGPoint>>();
        double h = Math.Max(width, 0.01) / 2;
        foreach (var c in path.Contours(0.25))
        {
            bool closed = path.elements.Any(e => e.type == NSBezierPath.ElementType.closePath);
            int n = c.Count, segs = closed ? n : n - 1;
            for (int i = 0; i < segs; i++)
            {
                var a = c[i]; var b = c[(i + 1) % n];
                double dx = b.x - a.x, dy = b.y - a.y, l = Math.Sqrt(dx * dx + dy * dy);
                if (l < 1e-9) continue;
                double nx = -dy / l * h, ny = dx / l * h, ex = dx / l * h, ey = dy / l * h;
                quads.Add(new List<CGPoint> { new(a.x - ex + nx, a.y - ey + ny), new(b.x + ex + nx, b.y + ey + ny), new(b.x + ex - nx, b.y + ey - ny), new(a.x - ex - nx, a.y - ey - ny) });
            }
        }
        Rasterize(quads, false, color);
    }
    private void Rasterize(List<List<CGPoint>> polys, bool evenOdd, NSColor color)
    {
        if (polys.Count == 0) return;
        int w = rep.pixelsWide, h = rep.pixelsHigh, ss = shouldAntialias ? 4 : 1;
        var c = color.usingColorSpace(NSColorSpace.sRGB);
        double alpha = color.alphaComponent;
        double minY = polys.SelectMany(p => p).Min(p => p.y), maxY = polys.SelectMany(p => p).Max(p => p.y);
        int y0 = Math.Max(0, (int)Math.Floor(minY)), y1 = Math.Min(h - 1, (int)Math.Ceiling(maxY));
        var coverage = new double[w];
        var crossings = new List<(double x, int dir)>();
        for (int py = y0; py <= y1; py++)
        {
            Array.Clear(coverage);
            for (int sy = 0; sy < ss; sy++)
            {
                double y = py + (sy + 0.5) / ss;
                crossings.Clear();
                foreach (var poly in polys)
                    for (int i = 0; i < poly.Count; i++)
                    {
                        var a = poly[i]; var b = poly[(i + 1) % poly.Count];
                        if ((a.y <= y) == (b.y <= y)) continue;
                        double x = a.x + (y - a.y) * (b.x - a.x) / (b.y - a.y);
                        crossings.Add((x, b.y > a.y ? 1 : -1));
                    }
                crossings.Sort((p, q) => p.x.CompareTo(q.x));
                int wind = 0;
                for (int i = 0; i + 1 < crossings.Count; i++)
                {
                    wind += crossings[i].dir;
                    bool inside = evenOdd ? (Math.Abs(wind) % 2 == 1) : wind != 0;
                    if (!inside) continue;
                    double xa = crossings[i].x, xb = crossings[i + 1].x;
                    for (int sx = 0; sx < ss; sx++)
                    {
                        double off = (sx + 0.5) / ss;
                        int ia = (int)Math.Ceiling(xa - off), ib = (int)Math.Floor(xb - off);
                        for (int px = Math.Max(0, ia); px <= Math.Min(w - 1, ib); px++) coverage[px] += 1.0 / (ss * ss);
                    }
                }
            }
            int row = isFlipped ? py : h - 1 - py;
            for (int px = 0; px < w; px++)
            {
                double a = Math.Min(1, coverage[px]) * alpha;
                if (a <= 0) continue;
                if (clip is CGRect cr && !cr.contains(new CGPoint(px + 0.5, py + 0.5))) continue;
                Blend(px, row, c.redComponent, c.greenComponent, c.blueComponent, a);
            }
        }
        rep.version++;
    }
    internal void BlendPixel(int x, int y, double r, double g, double b, double a) => Blend(x, y, r, g, b, a);
    private void Blend(int x, int y, double r, double g, double b, double a)
    {
        int i = y * rep.bytesPerRow + x * rep.bitsPerPixel / 8;
        var d = rep.bitmapData;
        bool premultiplied = rep.hasAlpha && (rep.bitmapFormat & NSBitmapFormat.alphaNonpremultiplied) == 0;
        double da = rep.hasAlpha ? d[i + rep.samplesPerPixel - 1] / 255.0 : 1;
        double outA = a + da * (1 - a);
        for (int k = 0; k < Math.Min(3, rep.samplesPerPixel); k++)
        {
            double src = k == 0 ? r : k == 1 ? g : b;
            double dst = d[i + k] / 255.0;
            double v = premultiplied ? src * a + dst * (1 - a) : (outA > 0 ? (src * a + dst * da * (1 - a)) / outA : 0);
            d[i + k] = (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
        }
        if (rep.hasAlpha) d[i + rep.samplesPerPixel - 1] = (byte)Math.Clamp(Math.Round(outA * 255), 0, 255);
    }
}

/// <summary>NSFont: system fonts map to Godot SystemFont (SF Pro / SF Mono, falling back to Segoe UI / Consolas).</summary>
public sealed class NSFont
{
    public enum Weight { ultraLight = 100, thin = 200, light = 300, regular = 400, medium = 500, semibold = 600, bold = 700, heavy = 800, black = 900 }
    public readonly double pointSize;
    public readonly string fontName;
    internal readonly Font godotFont;
    internal readonly int weight;
    private NSFont(string name, double size, int weight, bool monospaced)
    {
        pointSize = size; fontName = name; this.weight = weight;
        godotFont = new SystemFont
        {
            FontNames = monospaced ? new[] { "SF Mono", "Menlo", "Consolas", "monospace" }
                : name != null && !name.StartsWith(".") ? new[] { name, "Avenir Next Condensed", "SF Pro Text", "Segoe UI", "sans-serif" }
                : new[] { "SF Pro Text", "SF Pro", ".AppleSystemUIFont", "Segoe UI", "sans-serif" },
            FontWeight = weight,
            Antialiasing = TextServer.FontAntialiasing.Gray,
        };
    }
    private NSFont(string name, double size, int weight, Font face) { pointSize = size; fontName = name; this.weight = weight; godotFont = face; }
    /// <summary>
    /// The face of a system font collection whose style name matches a PostScript style suffix
    /// ("DemiBold" -> "Demi Bold", "Medium"), or null. Measured: Godot's SystemFont returns face 0 of a
    /// .ttc for every weight ("Avenir Next Condensed.ttc" face 0 is Bold), while NSFont(name:size:) names
    /// one exact face. The face falls back to the family's SystemFont for missing glyphs (CoreText does too).
    /// </summary>
    private static Font CollectionFace(string family, string style, int weight)
    {
        string key = family + "|" + style;
        lock (collectionFaces)
        {
            if (collectionFaces.TryGetValue(key, out var cached)) return cached;
            Font result = null;
            string path = OS.GetSystemFontPath(family, weight, 100, false);
            byte[] bytes = string.IsNullOrEmpty(path) ? null : Godot.FileAccess.GetFileAsBytes(path);
            if (bytes != null && bytes.Length > 0)
            {
                var probe = new FontFile { Data = bytes };
                for (int i = 0; i < (int)probe.GetFaceCount() && result == null; i++)
                {
                    var face = new FontFile();
                    face.SetFaceIndex(0, i); // before Data: a face index set afterwards is ignored (measured)
                    face.Data = bytes;
                    if (string.Equals(face.GetFontStyleName().Replace(" ", ""), style, StringComparison.OrdinalIgnoreCase))
                    {
                        face.Antialiasing = TextServer.FontAntialiasing.Gray;
                        face.Fallbacks = new Godot.Collections.Array<Font> { new SystemFont { FontNames = new[] { family, "SF Pro Text", "Segoe UI", "sans-serif" }, FontWeight = weight, Antialiasing = TextServer.FontAntialiasing.Gray } };
                        result = face;
                    }
                }
            }
            collectionFaces[key] = result;
            return result;
        }
    }
    private static readonly Dictionary<string, Font> collectionFaces = new();
    public static NSFont systemFont(double ofSize, Weight weight = Weight.regular) => new(".SFNS", ofSize, (int)weight, false);
    public static NSFont boldSystemFont(double ofSize) => new(".SFNS", ofSize, 700, false);
    public static NSFont monospacedSystemFont(double ofSize, Weight weight) => new(".SFNSMono", ofSize, (int)weight, true);
    public static NSFont monospacedDigitSystemFont(double ofSize, Weight weight) => new(".SFNS", ofSize, (int)weight, false);
    /// <summary>NSFont(name:size:) - returns a SystemFont lookup (never nil in the facade).</summary>
    public static NSFont named(string name, double size)
    {
        int weight = name.Contains("Bold") || name.Contains("DemiBold") ? 600 : name.Contains("Medium") ? 500 : 400;
        string family = name.Split('-')[0];
        if (family == "AvenirNextCondensed") family = "Avenir Next Condensed";
        if (name.Contains('-') && CollectionFace(family, name.Substring(name.IndexOf('-') + 1), weight) is Font face) return new NSFont(family, size, weight, face);
        return new NSFont(family, size, weight, false);
    }
    public double ascender => godotFont.GetAscent(64) / 64.0 * pointSize;
    public double descender => -godotFont.GetDescent(64) / 64.0 * pointSize;
    public double leading => 0;
    public double capHeight => ascender * 0.75;
    /// <summary>Godot Font for UI text.</summary>
    public Font GodotFont => godotFont;
}
