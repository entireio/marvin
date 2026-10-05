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
    /// <summary>setLineDash(_:count:phase:) state (null: solid).</summary>
    internal double[] lineDash;
    internal double lineDashPhase;
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
    /// <summary>setLineDash(_:count:phase:): on/off lengths in user space; count 0 or a null pattern makes the line solid.</summary>
    public void setLineDash(double[] pattern, int count, double phase)
    {
        lineDash = pattern == null || count <= 0 ? null : pattern.Take(Math.Min(count, pattern.Length)).ToArray();
        if (lineDash != null && lineDash.Sum() <= 0) lineDash = null;
        lineDashPhase = phase;
    }
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
    /// <summary>Flattened subpaths with their closure (stroking needs to know which subpaths close).</summary>
    internal List<(List<CGPoint> points, bool closed)> SubpathsForStroke(double tolerance)
    {
        var result = new List<(List<CGPoint>, bool)>();
        List<CGPoint> cur = null;
        CGPoint last = default;
        foreach (var (type, p, c1, c2) in elements)
        {
            switch (type)
            {
                case ElementType.moveTo:
                    if (cur != null && cur.Count > 1) result.Add((cur, false));
                    cur = new List<CGPoint> { p }; last = p; break;
                case ElementType.lineTo:
                    cur ??= new List<CGPoint> { last };
                    cur.Add(p); last = p; break;
                case ElementType.curveTo:
                    cur ??= new List<CGPoint> { last };
                    Flatten(last, c1, c2, p, tolerance, cur, 0); last = p; break;
                case ElementType.closePath:
                    if (cur != null && cur.Count > 1)
                    {
                        if (cur.Count > 2 && cur[0] == cur[^1]) cur.RemoveAt(cur.Count - 1);
                        result.Add((cur, true));
                    }
                    cur = null; last = p; break;
            }
        }
        if (cur != null && cur.Count > 1) result.Add((cur, false));
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
    /// <summary>addClip(): the facade clips to the path's bounding rectangle (in device space, intersected with the current clip).</summary>
    public void addClip() => NSGraphicsContext.current?.AddClip(this);
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
/// NSGraphicsContext: the current drawing destination plus AppKit's graphics state (fill and stroke colour,
/// current transformation matrix, clip). Two backends:
/// - bitmap (NSBitmapImageRep, NSImage.lockFocus): CPU scanline rasteriser with 4x4 anti-aliasing and
///   source-over blending, AppKit coordinates (origin bottom-left unless flipped);
/// - canvas (NSView drawing, cacheDisplay): Godot canvas items, see NSGraphicsCanvas.
/// Paths are flattened in user space and mapped to device space by the CTM (NSAffineTransform.concat).
/// Strokes are outlined with AppKit's joins (miter up to miterLimit, round, bevel), caps and dashes.
/// Clipping is rectangular: the clip path's device-space bounds, intersected with the current clip.
/// Text: see NSAttributedString.
/// </summary>
public sealed class NSGraphicsContext
{
    // Per thread, as in AppKit (images may be drawn on a builder thread).
    [ThreadStatic] public static NSGraphicsContext current;
    [ThreadStatic] private static Stack<GState> stackStorage;
    [ThreadStatic] private static NSColor fillColor, strokeColor;
    private readonly record struct GState(NSGraphicsContext ctx, NSColor fill, NSColor stroke, AffineTransform ctm, CGRect? clip);
    private static Stack<GState> stack => stackStorage ??= new();
    internal static NSColor FillColor { get => fillColor ?? NSColor.black; set => fillColor = value; }
    internal static NSColor StrokeColor { get => strokeColor ?? NSColor.black; set => strokeColor = value; }
    internal readonly NSBitmapImageRep rep;
    /// <summary>Canvas backend (view drawing); null for bitmap contexts.</summary>
    internal readonly NSGraphicsCanvas canvas;
    public bool shouldAntialias = true;
    /// <summary>CGContext.setShouldSmoothFonts: CoreText's font smoothing (glyph dilation, FontSmoothing). On by default, as in AppKit.</summary>
    public bool shouldSmoothFonts = true;
    /// <summary>Text drawn by a control cell (NSTextField, NSButton title) over its own transparent background (FontSmoothing).</summary>
    internal bool cellText;
    /// <summary>Calibration only: a fixed stem gain in pixels instead of FontSmoothing's model.</summary>
    internal double? stemGainOverride;
    /// <summary>isFlipped: user-space y grows downwards (flipped views, NSImage.lockFocusFlipped(true)).</summary>
    public readonly bool isFlipped;
    /// <summary>Clip rectangle in device space (bitmap: AppKit bitmap coordinates; canvas: Godot pixels).</summary>
    internal CGRect? clip;
    /// <summary>Current transformation matrix: user space to device space.</summary>
    internal AffineTransform ctm = AffineTransform.identity;
    private readonly AffineTransform baseCtm = AffineTransform.identity;
    internal NSGraphicsContext(NSBitmapImageRep rep, bool flipped = false) { this.rep = rep; isFlipped = flipped; }
    /// <summary>Canvas context: baseTransform maps the view's user space to the canvas item's pixels (y down).</summary>
    internal NSGraphicsContext(NSGraphicsCanvas canvas, bool flipped, AffineTransform baseTransform)
    {
        this.canvas = canvas; isFlipped = flipped; ctm = baseCtm = baseTransform;
    }
    /// <summary>NSGraphicsContext(bitmapImageRep:).</summary>
    public static NSGraphicsContext bitmapImageRep(NSBitmapImageRep rep) => new(rep);
    public static void saveGraphicsState() => stack.Push(new GState(current, FillColor, StrokeColor, current?.ctm ?? AffineTransform.identity, current?.clip));
    public static void restoreGraphicsState()
    {
        if (stack.Count == 0) return;
        var s = stack.Pop();
        current = s.ctx; FillColor = s.fill; StrokeColor = s.stroke;
        if (current != null) { current.ctm = s.ctm; current.clip = s.clip; }
    }
    /// <summary>CGContextConcatCTM: t is applied to user coordinates before the current CTM.</summary>
    internal void Concat(AffineTransform t) => ctm = AffineTransform.Multiply(t, ctm);
    /// <summary>NSAffineTransform.set(): the CTM becomes t (relative to the context's default space).</summary>
    internal void SetCTM(AffineTransform t) => ctm = AffineTransform.Multiply(t, baseCtm);
    internal CGPoint ToDevice(CGPoint p) => ctm.transform(p);
    private List<List<CGPoint>> ToDevice(List<List<CGPoint>> contours)
    {
        if (ctm.isIdentity) return contours;
        return contours.Select(c => c.Select(ctm.transform).ToList()).ToList();
    }
    private List<CGPoint> ToDevice(List<CGPoint> points) => ctm.isIdentity ? points : points.Select(ctm.transform).ToList();
    /// <summary>Device-space bounding box of a user-space rectangle.</summary>
    internal CGRect ToDevice(CGRect r)
    {
        if (ctm.isIdentity) return r;
        var a = ctm.transform(new CGPoint(r.minX, r.minY)); var b = ctm.transform(new CGPoint(r.maxX, r.minY));
        var c = ctm.transform(new CGPoint(r.maxX, r.maxY)); var d = ctm.transform(new CGPoint(r.minX, r.maxY));
        double x0 = Math.Min(Math.Min(a.x, b.x), Math.Min(c.x, d.x)), x1 = Math.Max(Math.Max(a.x, b.x), Math.Max(c.x, d.x));
        double y0 = Math.Min(Math.Min(a.y, b.y), Math.Min(c.y, d.y)), y1 = Math.Max(Math.Max(a.y, b.y), Math.Max(c.y, d.y));
        return new CGRect(x0, y0, x1 - x0, y1 - y0);
    }
    /// <summary>Uniform scale of the CTM (line widths).</summary>
    internal double CtmScale => Math.Sqrt(Math.Abs(ctm.m11 * ctm.m22 - ctm.m12 * ctm.m21));

    internal void AddClip(NSBezierPath path)
    {
        var pts = ToDevice(path.Contours(Math.Min(path.flatness, 0.25))).SelectMany(c => c).ToList();
        var b = pts.Count == 0 ? CGRect.zero : new CGRect(pts.Min(p => p.x), pts.Min(p => p.y), pts.Max(p => p.x) - pts.Min(p => p.x), pts.Max(p => p.y) - pts.Min(p => p.y));
        clip = clip is CGRect existing ? existing.intersection(b) : b;
    }

    /// <summary>Draws an image into rect (source-over, bilinear), honouring the clip rect. Images are drawn upright.</summary>
    internal void DrawImage(NSImage image, CGRect rect, double fraction)
    {
        var device = ToDevice(rect);
        if (canvas != null) { canvas.Image(image, device, fraction, clip, isFlipped); return; }
        var src = image.GodotImage;
        rect = device;
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
        var contours = ToDevice(path.Contours(Math.Min(path.flatness, 0.25)));
        if (canvas != null) { canvas.Fill(contours, path.windingRule == NSBezierPath.WindingRule.evenOdd, color, clip); return; }
        Rasterize(contours, path.windingRule == NSBezierPath.WindingRule.evenOdd, color);
    }
    internal void StrokePath(NSBezierPath path, NSColor color, double width)
    {
        // lineWidth 0 is AppKit's thinnest line: one device pixel.
        if (width <= 0) width = 1 / Math.Max(1e-9, CtmScale);
        var subpaths = path.SubpathsForStroke(0.25);
        if (path.lineDash != null) subpaths = Dash(subpaths, path.lineDash, path.lineDashPhase);
        if (canvas != null)
        {
            foreach (var (points, closed) in subpaths)
                canvas.Stroke(ToDevice(points), closed, width * CtmScale, path.lineJoinStyle, path.lineCapStyle, path.miterLimit, color, clip);
            return;
        }
        var polys = StrokeOutline(subpaths, width, path.lineJoinStyle, path.lineCapStyle, path.miterLimit);
        Rasterize(ToDevice(polys), false, color);
    }

    /// <summary>Splits subpaths into the "on" intervals of a dash pattern (user-space lengths, phase offset).</summary>
    internal static List<(List<CGPoint> points, bool closed)> Dash(List<(List<CGPoint> points, bool closed)> subpaths, double[] pattern, double phase)
    {
        var result = new List<(List<CGPoint>, bool)>();
        double period = pattern.Sum();
        foreach (var (points, closed) in subpaths)
        {
            var pts = closed ? points.Append(points[0]).ToList() : points;
            int index = 0; double left = pattern[0], offset = ((phase % period) + period) % period;
            while (offset > 0) { if (offset >= left) { offset -= left; index = (index + 1) % pattern.Length; left = pattern[index]; } else { left -= offset; offset = 0; } }
            List<CGPoint> dash = index % 2 == 0 ? new List<CGPoint> { pts[0] } : null;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                CGPoint a = pts[i], b = pts[i + 1];
                double len = Math.Sqrt((b.x - a.x) * (b.x - a.x) + (b.y - a.y) * (b.y - a.y)), t = 0;
                while (len - t > left)
                {
                    t += left;
                    var p = new CGPoint(a.x + (b.x - a.x) * t / len, a.y + (b.y - a.y) * t / len);
                    if (dash != null) { dash.Add(p); if (dash.Count > 1) result.Add((dash, false)); dash = null; }
                    else dash = new List<CGPoint> { p };
                    index = (index + 1) % pattern.Length; left = pattern[index];
                }
                left -= len - t;
                dash?.Add(b);
            }
            if (dash != null && dash.Count > 1) result.Add((dash, false));
        }
        return result;
    }

    /// <summary>
    /// Stroke outline as polygons whose nonzero union is the stroked area: one quad per segment, join
    /// pieces (round: disc, miter: wedge up to miterLimit else bevel, bevel: triangle) and caps.
    /// Every polygon is oriented counter-clockwise so overlaps never cancel.
    /// </summary>
    internal static List<List<CGPoint>> StrokeOutline(List<(List<CGPoint> points, bool closed)> subpaths, double width,
        NSBezierPath.LineJoinStyle join, NSBezierPath.LineCapStyle cap, double miterLimit)
    {
        var polys = new List<List<CGPoint>>();
        double h = width / 2;
        void Add(List<CGPoint> poly)
        {
            double area = 0;
            for (int i = 0; i < poly.Count; i++) { var a = poly[i]; var b = poly[(i + 1) % poly.Count]; area += a.x * b.y - b.x * a.y; }
            if (Math.Abs(area) < 1e-12) return;
            if (area < 0) poly.Reverse();
            polys.Add(poly);
        }
        List<CGPoint> Disc(CGPoint c)
        {
            int n = Math.Max(12, (int)Math.Ceiling(2 * Math.PI * h / 0.5));
            return Enumerable.Range(0, n).Select(i => new CGPoint(c.x + h * Math.Cos(2 * Math.PI * i / n), c.y + h * Math.Sin(2 * Math.PI * i / n))).ToList();
        }
        foreach (var (raw, closed) in subpaths)
        {
            var pts = new List<CGPoint>();
            foreach (var p in raw) if (pts.Count == 0 || Math.Abs(pts[^1].x - p.x) + Math.Abs(pts[^1].y - p.y) > 1e-9) pts.Add(p);
            if (closed && pts.Count > 2 && Math.Abs(pts[0].x - pts[^1].x) + Math.Abs(pts[0].y - pts[^1].y) <= 1e-9) pts.RemoveAt(pts.Count - 1);
            int n = pts.Count;
            if (n < 2)
            {
                if (n == 1 && cap == NSBezierPath.LineCapStyle.round) Add(Disc(pts[0]));
                if (n == 1 && cap == NSBezierPath.LineCapStyle.square) Add(new List<CGPoint> { new(pts[0].x - h, pts[0].y - h), new(pts[0].x + h, pts[0].y - h), new(pts[0].x + h, pts[0].y + h), new(pts[0].x - h, pts[0].y + h) });
                continue;
            }
            int segs = closed ? n : n - 1;
            (double x, double y) Dir(int i) { var a = pts[i]; var b = pts[(i + 1) % n]; double dx = b.x - a.x, dy = b.y - a.y, l = Math.Sqrt(dx * dx + dy * dy); return (dx / l, dy / l); }
            for (int i = 0; i < segs; i++)
            {
                var a = pts[i]; var b = pts[(i + 1) % n];
                var (dx, dy) = Dir(i);
                double nx = -dy * h, ny = dx * h;
                double ex0 = 0, ey0 = 0, ex1 = 0, ey1 = 0;
                if (!closed && cap == NSBezierPath.LineCapStyle.square)
                {
                    if (i == 0) { ex0 = -dx * h; ey0 = -dy * h; }
                    if (i == segs - 1) { ex1 = dx * h; ey1 = dy * h; }
                }
                Add(new List<CGPoint> { new(a.x + ex0 + nx, a.y + ey0 + ny), new(b.x + ex1 + nx, b.y + ey1 + ny), new(b.x + ex1 - nx, b.y + ey1 - ny), new(a.x + ex0 - nx, a.y + ey0 - ny) });
            }
            if (!closed && cap == NSBezierPath.LineCapStyle.round) { Add(Disc(pts[0])); Add(Disc(pts[^1])); }
            for (int k = closed ? 0 : 1; k < (closed ? n : n - 1); k++)
            {
                var v = pts[k];
                var d0 = Dir((k - 1 + n) % n); var d1 = Dir(k);
                double cross = d0.x * d1.y - d0.y * d1.x, dot = d0.x * d1.x + d0.y * d1.y;
                if (Math.Abs(cross) < 1e-12 && dot > 0) continue;
                if (join == NSBezierPath.LineJoinStyle.round) { Add(Disc(v)); continue; }
                // Outer side of the turn: right of the path for a left (counter-clockwise) turn.
                double s = cross > 0 ? -1 : 1;
                var o0 = new CGPoint(v.x - d0.y * h * s, v.y + d0.x * h * s);
                var o1 = new CGPoint(v.x - d1.y * h * s, v.y + d1.x * h * s);
                // Miter length / line width = 1 / sin(phi / 2), phi = angle between the segments.
                double phi = Math.Acos(Math.Clamp(-dot, -1, 1));
                double ratio = 1 / Math.Max(1e-9, Math.Sin(phi / 2));
                if (join == NSBezierPath.LineJoinStyle.miter && ratio <= miterLimit)
                {
                    double bx = (o0.x - v.x) + (o1.x - v.x), by = (o0.y - v.y) + (o1.y - v.y), bl = Math.Sqrt(bx * bx + by * by);
                    if (bl > 1e-12)
                    {
                        double reach = h * ratio;
                        var tip = new CGPoint(v.x + bx / bl * reach, v.y + by / bl * reach);
                        Add(new List<CGPoint> { v, o0, tip, o1 });
                        continue;
                    }
                }
                Add(new List<CGPoint> { v, o0, o1 });
            }
        }
        return polys;
    }

    private void Rasterize(List<List<CGPoint>> polys, bool evenOdd, NSColor color)
    {
        if (polys.Count == 0 || rep == null) return;
        int w = rep.pixelsWide, h = rep.pixelsHigh, ss = shouldAntialias ? 4 : 1;
        var c = color.usingColorSpace(NSColorSpace.sRGB);
        double alpha = color.alphaComponent;
        double minY = polys.SelectMany(p => p).Min(p => p.y), maxY = polys.SelectMany(p => p).Max(p => p.y);
        double minX = polys.SelectMany(p => p).Min(p => p.x), maxX = polys.SelectMany(p => p).Max(p => p.x);
        int y0 = Math.Max(0, (int)Math.Floor(minY)), y1 = Math.Min(h - 1, (int)Math.Ceiling(maxY));
        int x0 = Math.Max(0, (int)Math.Floor(minX) - 1), x1 = Math.Min(w - 1, (int)Math.Ceiling(maxX) + 1);
        if (x1 < x0) return;
        var coverage = new double[w];
        var crossings = new List<(double x, int dir)>();
        for (int py = y0; py <= y1; py++)
        {
            Array.Clear(coverage, x0, x1 - x0 + 1);
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
                        for (int px = Math.Max(x0, ia); px <= Math.Min(x1, ib); px++) coverage[px] += 1.0 / (ss * ss);
                    }
                }
            }
            int row = isFlipped ? py : h - 1 - py;
            for (int px = x0; px <= x1; px++)
            {
                double a = Math.Min(1, coverage[px]) * alpha;
                if (a <= 0) continue;
                if (clip is CGRect cr && !cr.contains(new CGPoint(px + 0.5, py + 0.5))) continue;
                Blend(px, row, c.redComponent, c.greenComponent, c.blueComponent, a);
            }
        }
        rep.version++;
    }
    /// <summary>
    /// CPU rasterisation of device-space polygons (y down) into a straight-alpha image covering their bounds
    /// (the canvas backend's fallback for multi-contour and self-intersecting fills).
    /// </summary>
    internal static Image RasterizeImage(List<List<CGPoint>> polys, bool evenOdd, NSColor color, out Rect2I rect)
    {
        var pts = polys.SelectMany(p => p).ToList();
        int x0 = (int)Math.Floor(pts.Min(p => p.x)) - 1, y0 = (int)Math.Floor(pts.Min(p => p.y)) - 1;
        int x1 = (int)Math.Ceiling(pts.Max(p => p.x)) + 1, y1 = (int)Math.Ceiling(pts.Max(p => p.y)) + 1;
        rect = new Rect2I(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
        var rep = new NSBitmapImageRep(null, rect.Size.X, rect.Size.Y, 8, 4, true, false, NSColorSpaceName.deviceRGB, rect.Size.X * 4, 32);
        var ctx = new NSGraphicsContext(rep, flipped: true);
        ctx.Rasterize(polys.Select(p => p.Select(q => new CGPoint(q.x - x0, q.y - y0)).ToList()).ToList(), evenOdd, color);
        return rep.ToGodotImage(straightAlpha: true);
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

/// <summary>
/// NSFont. System fonts resolve to the platform font through Godot SystemFont: SF Pro (".AppleSystemUIFont")
/// and SF Mono (".SF NS Mono"; plain "SF Mono" is not a resolvable family name) on macOS, Segoe UI /
/// Consolas on Windows (Apple's fonts may not be redistributed). Named fonts fall back the same way; the town signs'
/// Avenir Next Condensed falls back to Windows' narrow Bahnschrift (the signs shrink their text to fit, TownSigns.swift).
/// AppKit behaviour reproduced (measured against the game's captures):
/// - weights select the fonts' named instances: SF Pro wght 400/510/590/700/860/1000 (regular, medium,
///   semibold, bold, heavy, black), SF Mono wght + YAXS pairs (e.g. bold 683.3/335.8);
/// - SF Pro's optical size follows the point size (opsz = size, clamped to the axis' 17...96);
/// - CoreText adds the font's 'trak' tracking (track 0, interpolated over its size table) after every
///   glyph (Apple HIG: 17 pt -> -0.43 pt);
/// - monospacedDigitSystemFont enables tabular figures ('tnum');
/// - ascender/descender are the font's exact hhea values (FreeType's pixel metrics are rounded);
/// - 2D text is rasterised with CoreText's font-smoothing dilation (RenderFont(stem gain), chosen per draw by
///   FontSmoothing from the size, weight, text colour and drawing path); godotFont keeps the plain outlines for
///   shaping and SCNText geometry.
/// Glyphs are rendered unhinted; view drawing places them on CoreText's subpixel grid (FontSmoothing.GlyphX), then Godot's quarter pixels.
/// Godot fonts are cached per family, weight, optical size and features, so creating NSFonts per draw is cheap.
/// </summary>
public sealed class NSFont
{
    public enum Weight { ultraLight = 100, thin = 200, light = 300, regular = 400, medium = 500, semibold = 600, bold = 700, heavy = 800, black = 900 }
    public const double systemFontSize = 13, smallSystemFontSize = 11, labelFontSize = 10;
    public readonly double pointSize;
    public readonly string fontName;
    /// <summary>The font's outlines and metrics (shaping, SCNText geometry).</summary>
    internal readonly Font godotFont;
    /// <summary>The font 2D text is rasterised with by default: godotFont plus CoreText's font smoothing for black text.</summary>
    internal readonly Font renderFont;
    internal readonly int weight;
    /// <summary>True for SF Mono (monospacedSystemFont).</summary>
    internal readonly bool monospaced;
    /// <summary>Tracking CoreText adds after every glyph at this size, in points.</summary>
    internal readonly double tracking;
    /// <summary>
    /// System fonts use AppKit's measured line metrics (exact ascender/descender, baseline on a device pixel).
    /// Named fonts (the town signs' Avenir Next Condensed) keep FreeType's pixel metrics, which their textures
    /// were calibrated with (V_sign).
    /// </summary>
    internal readonly bool appKitMetrics;
    private readonly double ascenderEm, descenderEm;

    private enum Family { System, Monospaced, Named }
    private sealed class Face
    {
        public Font font; public double ascenderEm, descenderEm; public TrakTable trak;
        // Dilated twins (CoreText font smoothing), by stem gain in 1/32 px.
        public SystemFont systemFont; public Godot.Collections.Dictionary coords, features; public int pixels; public double capHeight;
        private readonly Dictionary<int, Font> dilated = new();
        /// <summary>
        /// The font widened by stemGain pixels: Godot's embolden widens outlines by embolden x size / 16 pixels
        /// (horizontally); the cap top rises through a vertical outline scale (SceneKitCalibration.FontDilationRise).
        /// </summary>
        public Font Dilated(double stemGain)
        {
            int key = (int)Math.Round(stemGain * 32);
            if (key <= 0 || systemFont == null || pixels <= 0) return font;
            lock (dilated)
            {
                if (dilated.TryGetValue(key, out var cached)) return cached;
                double gain = key / 32.0;
                var variation = new FontVariation { BaseFont = systemFont, VariationOpentype = coords, OpentypeFeatures = features };
                variation.VariationEmbolden = (float)(gain * 16 / pixels);
                if (capHeight > 1) variation.VariationTransform = new Transform2D(new Vector2(1, 0), new Vector2(0, (float)(1 + SceneKitCalibration.FontDilationRise * gain / capHeight)), Vector2.Zero);
                dilated[key] = variation;
                return variation;
            }
        }
    }
    private readonly Face face;
    private static readonly Dictionary<string, Face> faces = new();
    private static readonly Dictionary<string, SystemFont> bases = new();
    private static readonly Dictionary<string, TrakTable> trakTables = new();
    private static readonly object gate = new();
    private static readonly string[] SystemNames = { ".AppleSystemUIFont", "SF Pro Text", "SF Pro", "Segoe UI", "sans-serif" };
    private static readonly string[] MonospacedNames = { ".SF NS Mono", "SF Mono", "Menlo", "Consolas", "monospace" };

    private NSFont(string name, double size, int weight, Family family, bool tabularDigits)
    {
        pointSize = size; fontName = name; this.weight = weight;
        face = Resolve(name, size, weight, family, tabularDigits);
        godotFont = face.font; ascenderEm = face.ascenderEm; descenderEm = face.descenderEm;
        appKitMetrics = family != Family.Named;
        monospaced = family == Family.Monospaced;
        renderFont = RenderFont(FontSmoothing.StemGain(this, 0, cell: false));
        tracking = face.trak?.Tracking(size) ?? 0;
    }
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
    public static NSFont systemFont(double ofSize, Weight weight = Weight.regular) => new(".SFNS", ofSize, (int)weight, Family.System, false);
    public static NSFont boldSystemFont(double ofSize) => new(".SFNS", ofSize, 700, Family.System, false);
    public static NSFont monospacedSystemFont(double ofSize, Weight weight) => new(".SFNSMono", ofSize, (int)weight, Family.Monospaced, false);
    public static NSFont monospacedDigitSystemFont(double ofSize, Weight weight) => new(".SFNS", ofSize, (int)weight, Family.System, true);
    /// <summary>NSFont(name:size:) - returns a SystemFont lookup (never nil in the facade).</summary>
    public static NSFont named(string name, double size)
    {
        int weight = name.Contains("Bold") || name.Contains("DemiBold") ? 600 : name.Contains("Medium") ? 500 : 400;
        return new NSFont(name, size, weight, Family.Named, false);
    }
    public double ascender => ascenderEm * pointSize;
    public double descender => -descenderEm * pointSize;
    public double leading => 0;
    public double capHeight => ascender * 0.75;
    /// <summary>Godot Font for UI text (with CoreText's font-smoothing dilation for black text).</summary>
    public Font GodotFont => renderFont;
    /// <summary>The font rasterised with CoreText's font-smoothing dilation of stemGain pixels (system fonts only).</summary>
    internal Font RenderFont(double stemGain) => appKitMetrics ? face.Dilated(stemGain) : godotFont;

    // SF named instances (fvar of SFNS.ttf / SFNSMono.ttf on macOS 27).
    private static double SystemWeight(int w) => w switch
    {
        <= 100 => 30.925, <= 200 => 110.725, <= 300 => 274.315, <= 400 => 400, <= 500 => 510, <= 600 => 590, <= 700 => 700, <= 800 => 860, _ => 1000,
    };
    private static (double wght, double yaxs) MonospacedWeight(int w) => w switch
    {
        <= 300 => (294.673, 294.673), <= 400 => (400, 324.334), <= 500 => (483.535, 320.702), <= 600 => (571.307, 324.939), <= 700 => (683.293, 335.835), _ => (900, 294.673),
    };

    private static Face Resolve(string name, double size, int weight, Family family, bool tabularDigits)
    {
        int opsz = family == Family.System ? (int)Math.Clamp(Math.Round(size), 17, 96) : 0;
        // The dilated twins depend on the rendered pixel size.
        int pixels = family == Family.Named ? 0 : Math.Max(1, (int)Math.Round(size));
        string key = $"{family}|{name}|{weight}|{opsz}|{tabularDigits}|{pixels}";
        lock (gate)
        {
            if (faces.TryGetValue(key, out var cached)) return cached;
            string[] names = family switch
            {
                Family.System => SystemNames,
                Family.Monospaced => MonospacedNames,
                _ => new[] { name.Split('-')[0] == "AvenirNextCondensed" ? "Avenir Next Condensed" : name.Split('-')[0], "Avenir Next Condensed", "Bahnschrift", "SF Pro Text", "Segoe UI", "sans-serif" },
            };
            string baseKey = string.Join(",", names) + "|" + weight;
            if (!bases.TryGetValue(baseKey, out var systemFont))
            {
                systemFont = new SystemFont
                {
                    FontNames = names, FontWeight = weight,
                    Antialiasing = TextServer.FontAntialiasing.Gray,
                    Hinting = family == Family.Named ? TextServer.Hinting.Light : TextServer.Hinting.None,
                    SubpixelPositioning = family == Family.Named ? TextServer.SubpixelPositioning.Auto : TextServer.SubpixelPositioning.OneQuarter,
                };
                bases[baseKey] = systemFont;
            }
            Font font = systemFont;
            Godot.Collections.Dictionary dilationCoords = null, dilationFeatures = null;
            double capHeight = 0;
            // NSFont(name:size:) names one exact face of a font collection (town signs: "AvenirNextCondensed-DemiBold").
            if (family == Family.Named && name.Contains('-') && CollectionFace(names[0], name.Substring(name.IndexOf('-') + 1), weight) is Font exact)
                font = exact;
            if (family != Family.Named)
            {
                var ts = TextServerManager.GetPrimaryInterface();
                var rids = systemFont.GetRids();
                var supported = rids.Count > 0 ? ts.FontSupportedVariationList(rids[0]) : new Godot.Collections.Dictionary();
                var coords = new Godot.Collections.Dictionary();
                void Axis(string tag, double value) { long t = ts.NameToTag(tag); if (supported.ContainsKey(t)) coords[t] = value; }
                if (family == Family.System) { Axis("wght", SystemWeight(weight)); Axis("opsz", opsz); }
                else { var (wght, yaxs) = MonospacedWeight(weight); Axis("wght", wght); Axis("YAXS", yaxs); }
                var features = tabularDigits ? new Godot.Collections.Dictionary { [ts.NameToTag("tnum")] = 1 } : new Godot.Collections.Dictionary();
                var variation = new FontVariation { BaseFont = systemFont, VariationOpentype = coords, OpentypeFeatures = features };
                font = variation;
                dilationCoords = coords; dilationFeatures = features;
                capHeight = CapHeightEm(variation) * pixels;
            }
            var face = new Face
            {
                font = font,
                systemFont = family != Family.Named ? systemFont : null, coords = dilationCoords, features = dilationFeatures, pixels = pixels, capHeight = capHeight,
                // Exact em metrics: FreeType rounds pixel metrics up, so read them at 2048 px (= units for 2048-unit fonts).
                ascenderEm = font.GetAscent(2048) / 2048.0,
                descenderEm = font.GetDescent(2048) / 2048.0,
                trak = family == Family.System ? Trak(names) : null,
            };
            faces[key] = face;
            return face;
        }
    }
    /// <summary>Height of 'H' in em (outline top at 1000 px), before any outline transform.</summary>
    private static double CapHeightEm(Font font)
    {
        var ts = TextServerManager.GetPrimaryInterface();
        var rids = font.GetRids();
        if (rids.Count == 0) return 0.7;
        long glyph = ts.FontGetGlyphIndex(rids[0], 1000, 'H', 0);
        var points = ts.FontGetGlyphContours(rids[0], 1000, glyph)["points"].AsVector3Array();
        if (points.Length == 0) return 0.7;
        float top = 0; foreach (var p in points) top = Math.Min(top, p.Y);
        return -top / 1000.0;
    }
    private static TrakTable Trak(string[] names)
    {
        foreach (var n in names)
        {
            string path = OS.GetSystemFontPath(n, 400);
            if (string.IsNullOrEmpty(path)) continue;
            if (!trakTables.TryGetValue(path, out var table)) trakTables[path] = table = TrakTable.Load(path);
            return table;
        }
        return null;
    }

    /// <summary>The 'trak' table's normal track (font units per glyph by point size).</summary>
    private sealed class TrakTable
    {
        private double[] sizes, values;
        private double unitsPerEm = 1000;
        internal double Tracking(double size)
        {
            if (sizes == null || sizes.Length == 0) return 0;
            double v;
            if (size <= sizes[0]) v = values[0];
            else if (size >= sizes[^1]) v = values[^1];
            else
            {
                int i = 1; while (sizes[i] < size) i++;
                double t = (size - sizes[i - 1]) / (sizes[i] - sizes[i - 1]);
                v = values[i - 1] + (values[i] - values[i - 1]) * t;
            }
            return v / unitsPerEm * size;
        }
        internal static TrakTable Load(string path)
        {
            var table = new TrakTable();
            try
            {
                using var f = System.IO.File.OpenRead(path);
                using var r = new System.IO.BinaryReader(f);
                uint U32() { var b = r.ReadBytes(4); return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }
                ushort U16() { var b = r.ReadBytes(2); return (ushort)(b[0] << 8 | b[1]); }
                f.Position = 4; int numTables = U16();
                long trak = -1, head = -1;
                for (int i = 0; i < numTables; i++)
                {
                    f.Position = 12 + i * 16;
                    string tag = System.Text.Encoding.ASCII.GetString(r.ReadBytes(4)); U32(); long offset = U32();
                    if (tag == "trak") trak = offset; else if (tag == "head") head = offset;
                }
                if (head >= 0) { f.Position = head + 18; table.unitsPerEm = U16(); }
                if (trak < 0) return table;
                f.Position = trak + 6; long horiz = U16();
                if (horiz == 0) return table;
                f.Position = trak + horiz; int nTracks = U16(), nSizes = U16(); long sizeTable = U32();
                for (int t = 0; t < nTracks; t++)
                {
                    f.Position = trak + horiz + 8 + t * 8;
                    int track = (int)U32(); U16(); long valueOffset = U16();
                    if (track != 0) continue;
                    table.sizes = new double[nSizes]; table.values = new double[nSizes];
                    for (int s = 0; s < nSizes; s++) { f.Position = trak + sizeTable + s * 4; table.sizes[s] = (int)U32() / 65536.0; }
                    for (int s = 0; s < nSizes; s++) { f.Position = trak + valueOffset + s * 2; table.values[s] = (short)U16(); }
                }
            }
            catch (Exception e) { GD.PushWarning($"NSFont: could not read 'trak' from {path}: {e.Message}"); }
            return table;
        }
    }
}
