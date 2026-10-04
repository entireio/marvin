using System;
using System.Collections.Generic;
using Godot;

namespace Marvin.SceneKit;

public enum NSTextAlignment { left = 0, center = 1, right = 2, justified = 3, natural = 4 }
public enum NSLineBreakMode { byWordWrapping = 0, byCharWrapping, byClipping, byTruncatingHead, byTruncatingTail, byTruncatingMiddle }

/// <summary>NSMutableParagraphStyle (alignment, lineBreakMode).</summary>
public class NSMutableParagraphStyle
{
    public NSTextAlignment alignment = NSTextAlignment.natural;
    public NSLineBreakMode lineBreakMode = NSLineBreakMode.byWordWrapping;
    public double lineSpacing, paragraphSpacing;
}

/// <summary>
/// NSAttributedString, single line, drawn into the current NSGraphicsContext (a bitmap, or Godot canvas
/// items for NSView drawing). Supports .font, .foregroundColor, .paragraphStyle (alignment) and .kern.
/// Swift `[.font: f, .kern: k]` -> `new() { [NSAttributedString.Key.font] = f, ... }`.
/// Metrics follow AppKit (measured against the game's captures): the line height is the font's exact
/// ascender + descender; every glyph advances by its shaped advance + kern + the font's CoreText tracking;
/// draw(at:) puts the line's top (flipped) or bottom (unflipped) at the point; draw(in:) puts the line's
/// top at the rect's top edge; the baseline lands on the nearest device pixel (round(top + ascender)).
/// PORT: glyph shapes come from the platform font Godot finds (SF Pro/SF Mono and Avenir Next Condensed on
/// macOS), rasterised by FreeType; draw(in:) does not wrap lines.
/// </summary>
public sealed class NSAttributedString
{
    public enum Key { font, foregroundColor, backgroundColor, paragraphStyle, kern, baselineOffset, underlineStyle, strokeColor, strokeWidth }
    public readonly string @string;
    private readonly Dictionary<Key, object> attributes;
    public NSAttributedString(string @string, Dictionary<Key, object> attributes = null) { this.@string = @string ?? ""; this.attributes = attributes ?? new(); }
    public int length => @string.Length;

    private NSFont Font => attributes.TryGetValue(Key.font, out var f) && f is NSFont nf ? nf : NSFont.systemFont(12);
    private NSColor Color => attributes.TryGetValue(Key.foregroundColor, out var c) && c is NSColor nc ? nc : NSColor.black;
    private double Kern => attributes.TryGetValue(Key.kern, out var k) ? Convert.ToDouble(k) : 0;
    private NSTextAlignment Alignment => attributes.TryGetValue(Key.paragraphStyle, out var p) && p is NSMutableParagraphStyle ps ? ps.alignment : NSTextAlignment.natural;

    internal readonly struct Line
    {
        internal readonly List<(Rid font, long index, Vector2 offset, double x)> glyphs;
        internal readonly double width, ascent, descent;
        internal readonly int size;
        internal Line(List<(Rid, long, Vector2, double)> glyphs, double width, double ascent, double descent, int size)
        { this.glyphs = glyphs; this.width = width; this.ascent = ascent; this.descent = descent; this.size = size; }
    }

    // Shaped lines by (string, font, size, extra advance): views redraw the same strings every frame and shaping
    // through TextServer costs about 0.1 ms per string.
    private static readonly Dictionary<(string, Font, int, double), Line> layouts = new();
    private static readonly object layoutGate = new();
    private Line Layout()
    {
        var font = Font;
        int size = Math.Max(1, (int)Math.Round(font.pointSize));
        var key = (@string, font.renderFont, size, Kern + font.tracking * size / font.pointSize);
        lock (layoutGate) { if (layouts.TryGetValue(key, out var cached)) return cached; }
        var line = Shape(font, size);
        lock (layoutGate)
        {
            if (layouts.Count > 4096) layouts.Clear();
            layouts[key] = line;
        }
        return line;
    }
    private Line Shape(NSFont font, int size)
    {
        var ts = TextServerManager.GetPrimaryInterface();
        var shaped = ts.CreateShapedText();
        // Shaped (advances, kerning) with the plain font; rasterised with its dilated twin (CoreText font smoothing).
        var rids = font.godotFont.GetRids();
        var renderRids = font.renderFont.GetRids();
        ts.ShapedTextAddString(shaped, @string, rids, size, font.godotFont is FontVariation v ? v.OpentypeFeatures : null);
        var glyphs = new List<(Rid, long, Vector2, double)>();
        double x = 0, advance = Kern + font.tracking * size / font.pointSize;
        foreach (var g in ts.ShapedTextGetGlyphs(shaped))
        {
            var rid = g["font_rid"].AsRid();
            int k = rids.IndexOf(rid);
            if (k >= 0 && k < renderRids.Count) rid = renderRids[k];
            long index = g["index"].AsInt64();
            glyphs.Add((rid, index, g["offset"].AsVector2(), x));
            x += g["advance"].AsDouble() + advance;
        }
        ts.FreeRid(shaped);
        return font.appKitMetrics
            ? new Line(glyphs, x, font.ascender, -font.descender, size)
            : new Line(glyphs, x, font.godotFont.GetAscent(size), font.godotFont.GetDescent(size), size);
    }
    /// <summary>size(): width including kern and tracking, height = ascender + descender.</summary>
    public CGSize size()
    {
        var l = Layout();
        return new CGSize(l.width, l.ascent + l.descent);
    }
    /// <summary>draw(in:): one line, top-aligned in rect, horizontally aligned by the paragraph style.</summary>
    public void draw(CGRect @in)
    {
        var ctx = NSGraphicsContext.current;
        if (ctx == null) return;
        var l = Layout();
        double x0 = Alignment switch
        {
            NSTextAlignment.center => @in.minX + (@in.width - l.width) / 2,
            NSTextAlignment.right => @in.maxX - l.width,
            _ => @in.minX,
        };
        // The line's top is the rect's top edge: maxY in an unflipped context, minY when flipped.
        double baseline = ctx.isFlipped ? @in.minY + l.ascent : @in.maxY - l.ascent;
        Render(ctx, l, new CGPoint(x0, baseline));
    }
    /// <summary>draw(at:): the point is the line's lower-left corner (upper-left when flipped).</summary>
    public void draw(CGPoint at)
    {
        var ctx = NSGraphicsContext.current;
        if (ctx == null) return;
        var l = Layout();
        double baseline = ctx.isFlipped ? at.y + l.ascent : at.y + l.descent;
        Render(ctx, l, new CGPoint(at.x, baseline));
    }
    private void Render(NSGraphicsContext ctx, Line l, CGPoint userBaseline)
    {
        var color = Color.usingColorSpace(NSColorSpace.sRGB);
        var device = ctx.ToDevice(userBaseline);
        // AppKit places the baseline on a device pixel boundary (measured for the system fonts).
        if (Font.appKitMetrics) device.y = Math.Round(device.y);
        if (ctx.canvas != null)
        {
            ctx.canvas.Text(l, device, ctx.ctm, ctx.isFlipped, new Godot.Color((float)color.redComponent, (float)color.greenComponent, (float)color.blueComponent, (float)color.alphaComponent), ctx.clip);
            return;
        }
        var ts = TextServerManager.GetPrimaryInterface();
        var rep = ctx.rep;
        int h = rep.pixelsHigh, w = rep.pixelsWide;
        var size = new Vector2I(l.size, 0);
        var cache = new Dictionary<(Rid, int), Image>();
        double x0 = device.x, baseline = device.y;
        foreach (var (font, index, offset, gx) in l.glyphs)
        {
            ts.FontRenderGlyph(font, size, index);
            int texIdx = (int)ts.FontGetGlyphTextureIdx(font, size, index);
            if (texIdx < 0) continue;
            var uv = ts.FontGetGlyphUVRect(font, size, index);
            var off = ts.FontGetGlyphOffset(font, size, index);
            var gsize = ts.FontGetGlyphSize(font, size, index);
            if (!cache.TryGetValue((font, texIdx), out var tex)) { tex = ts.FontGetTextureImage(font, size, texIdx); if (tex.IsCompressed()) tex.Decompress(); cache[(font, texIdx)] = tex; }
            int gw = (int)Math.Round(gsize.X), gh = (int)Math.Round(gsize.Y);
            for (int j = 0; j < gh; j++)
                for (int i = 0; i < gw; i++)
                {
                    // Glyph pixel position: pen + offset, y down from the baseline.
                    double px = x0 + gx + offset.X + off.X + i;
                    double yDownFromBaseline = offset.Y + off.Y + j;
                    double py = ctx.isFlipped ? baseline + yDownFromBaseline : baseline - yDownFromBaseline - 1;
                    int ix = (int)Math.Floor(px), iy = (int)Math.Floor(py);
                    if (ix < 0 || ix >= w || iy < 0 || iy >= h) continue;
                    if (ctx.clip is CGRect c && !c.contains(new CGPoint(ix + 0.5, iy + 0.5))) continue;
                    int tx = (int)(uv.Position.X + (i + 0.5) * uv.Size.X / Math.Max(1, gw)), ty = (int)(uv.Position.Y + (j + 0.5) * uv.Size.Y / Math.Max(1, gh));
                    if (tx < 0 || ty < 0 || tx >= tex.GetWidth() || ty >= tex.GetHeight()) continue;
                    var t = tex.GetPixel(tx, ty);
                    double coverage = t.A * color.alphaComponent;
                    if (coverage <= 0) continue;
                    ctx.BlendPixel(ix, ctx.isFlipped ? iy : h - 1 - iy, color.redComponent, color.greenComponent, color.blueComponent, coverage);
                }
        }
        rep.version++;
    }
}

/// <summary>NSString drawing helpers: `(value as NSString).draw(at:withAttributes:)` -> `value.draw(at, attributes)`.</summary>
public static class NSStringDrawing
{
    public static void draw(this string value, CGPoint at, Dictionary<NSAttributedString.Key, object> withAttributes) => new NSAttributedString(value, withAttributes).draw(at);
    public static void draw(this string value, CGRect @in, Dictionary<NSAttributedString.Key, object> withAttributes) => new NSAttributedString(value, withAttributes).draw(@in);
    public static CGSize size(this string value, Dictionary<NSAttributedString.Key, object> withAttributes) => new NSAttributedString(value, withAttributes).size();
}
