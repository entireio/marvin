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
/// top at the rect's top edge; a fractional origin moves down to the next device pixel row, then the baseline
/// lands on the nearest device pixel (round(ceil(top) + ascender)). Glyphs get CoreText's font-smoothing
/// dilation for their size, colour and drawing path (FontSmoothing).
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
        /// <summary>Glyphs with the shaping font's RIDs (godotFont); Glyphs(renderFont) maps them to a dilated twin.</summary>
        internal readonly List<(Rid font, long index, Vector2 offset, double x)> glyphs;
        internal readonly double width, ascent, descent;
        internal readonly int size;
        private readonly Godot.Collections.Array<Rid> rids;
        private readonly Dictionary<Font, List<(Rid, long, Vector2, double)>> rendered;
        internal Line(List<(Rid, long, Vector2, double)> glyphs, double width, double ascent, double descent, int size, Godot.Collections.Array<Rid> rids)
        {
            this.glyphs = glyphs; this.width = width; this.ascent = ascent; this.descent = descent; this.size = size;
            this.rids = rids; rendered = new();
        }
        /// <summary>The glyphs drawn with renderFont (the shaping font or one of its dilated twins: same RID order).</summary>
        internal List<(Rid font, long index, Vector2 offset, double x)> Glyphs(Font renderFont)
        {
            if (renderFont == null || rids == null) return glyphs;
            lock (rendered)
            {
                if (rendered.TryGetValue(renderFont, out var cached)) return cached;
                var renderRids = renderFont.GetRids();
                var mapped = new List<(Rid, long, Vector2, double)>(glyphs.Count);
                foreach (var (rid, index, offset, x) in glyphs)
                {
                    int k = rids.IndexOf(rid);
                    mapped.Add((k >= 0 && k < renderRids.Count ? renderRids[k] : rid, index, offset, x));
                }
                rendered[renderFont] = mapped;
                return mapped;
            }
        }
    }

    // Shaped lines by (string, font, size, extra advance): views redraw the same strings every frame and shaping
    // through TextServer costs about 0.1 ms per string.
    private static readonly Dictionary<(string, Font, int, double), Line> layouts = new();
    private static readonly object layoutGate = new();
    private Line Layout()
    {
        var font = Font;
        int size = Math.Max(1, (int)Math.Round(font.pointSize));
        var key = (@string, font.godotFont, size, Kern + font.tracking * size / font.pointSize);
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
        // Shaped (advances, kerning) with the plain font; rasterised with a dilated twin (CoreText font smoothing, Render).
        var rids = font.godotFont.GetRids();
        ts.ShapedTextAddString(shaped, @string, rids, size, font.godotFont is FontVariation v ? v.OpentypeFeatures : null);
        var glyphs = new List<(Rid, long, Vector2, double)>();
        double x = 0, advance = Kern + font.tracking * size / font.pointSize;
        foreach (var g in ts.ShapedTextGetGlyphs(shaped))
        {
            var rid = g["font_rid"].AsRid();
            long index = g["index"].AsInt64();
            glyphs.Add((rid, index, g["offset"].AsVector2(), x));
            x += g["advance"].AsDouble() + advance;
        }
        ts.FreeRid(shaped);
        return font.appKitMetrics
            ? new Line(glyphs, x, font.ascender, -font.descender, size, rids)
            : new Line(glyphs, x, font.godotFont.GetAscent(size), font.godotFont.GetDescent(size), size, rids);
    }
    /// <summary>
    /// size(): width including kern and tracking. Height: for the system fonts AppKit's line fragment height,
    /// round(ascender) + descender rounded up up to 21 pt and to nearest above (measured for SF Pro and SF Mono,
    /// regular and bold, 8-80 pt: 292 of 292 sizes); otherwise ascender + descender.
    /// </summary>
    public CGSize size()
    {
        var l = Layout();
        var font = Font;
        if (!font.appKitMetrics) return new CGSize(l.width, l.ascent + l.descent);
        double descent = font.pointSize <= 21 ? Math.Ceiling(l.descent - 1e-9) : Math.Round(l.descent, MidpointRounding.AwayFromZero);
        return new CGSize(l.width, Math.Round(l.ascent, MidpointRounding.AwayFromZero) + descent);
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
        Render(ctx, l, new CGPoint(x0, ctx.isFlipped ? @in.minY : @in.maxY), ctx.isFlipped ? l.ascent : -l.ascent);
    }
    /// <summary>draw(at:): the point is the line's lower-left corner (upper-left when flipped).</summary>
    public void draw(CGPoint at)
    {
        var ctx = NSGraphicsContext.current;
        if (ctx == null) return;
        var l = Layout();
        Render(ctx, l, at, ctx.isFlipped ? l.ascent : l.descent);
    }
    /// <summary>
    /// Draws the line from its origin (the line's top, or its bottom for an unflipped draw(at:)), whose baseline lies
    /// toBaseline user units further along y. AppKit (measured, system fonts, axis-aligned CTM): the origin moves down
    /// to the next device pixel row when it is fractional (in flipped and unflipped views, draw(at:) and draw(in:)),
    /// then the baseline lands on the nearest device pixel boundary.
    /// </summary>
    private void Render(NSGraphicsContext ctx, Line l, CGPoint userOrigin, double toBaseline)
    {
        var color = Color.usingColorSpace(NSColorSpace.sRGB);
        var font = Font;
        var origin = ctx.ToDevice(userOrigin);
        var device = ctx.ToDevice(new CGPoint(userOrigin.x, userOrigin.y + toBaseline));
        bool axisAligned = Math.Abs(ctx.ctm.m12) < 1e-9 && Math.Abs(ctx.ctm.m21) < 1e-9;
        if (font.appKitMetrics && axisAligned)
        {
            // Device y grows downwards on canvases and flipped bitmaps, upwards in unflipped bitmaps.
            bool down = ctx.canvas != null || ctx.isFlipped;
            double snapped = down ? Math.Ceiling(origin.y - 1e-6) : Math.Floor(origin.y + 1e-6);
            device.y = Math.Round(device.y + snapped - origin.y);
        }
        else if (font.appKitMetrics) device.y = Math.Round(device.y);
        // CoreText's font smoothing: the glyphs' dilation depends on the size, weight, text colour and drawing path.
        double stemGain = ctx.stemGainOverride ?? (ctx.shouldSmoothFonts ? FontSmoothing.StemGain(font, FontSmoothing.Luminance(color), ctx.cellText) : 0);
        var glyphs = l.Glyphs(font.RenderFont(stemGain));
        if (ctx.canvas != null)
        {
            // CoreText's glyph positions (FontSmoothing.GlyphX) for upright text at one device pixel per point.
            if (font.appKitMetrics && Math.Abs(ctx.ctm.m12) < 1e-9 && Math.Abs(ctx.ctm.m21) < 1e-9 && Math.Abs(Math.Abs(ctx.ctm.m11) - 1) < 1e-9 && Math.Abs(Math.Abs(ctx.ctm.m22) - 1) < 1e-9)
            {
                var placed = new List<(Rid, long, Vector2, double)>(glyphs.Count);
                foreach (var (rid, index, offset, x) in glyphs)
                    placed.Add((rid, index, offset, FontSmoothing.GlyphX(device.x + x + offset.X, font.pointSize, stemGain) - device.x - offset.X));
                glyphs = placed;
            }
            ctx.canvas.Text(l, glyphs, device, ctx.ctm, ctx.isFlipped, new Godot.Color((float)color.redComponent, (float)color.greenComponent, (float)color.blueComponent, (float)color.alphaComponent), ctx.clip);
            return;
        }
        var ts = TextServerManager.GetPrimaryInterface();
        var rep = ctx.rep;
        int h = rep.pixelsHigh, w = rep.pixelsWide;
        var size = new Vector2I(l.size, 0);
        var cache = new Dictionary<(Rid, int), Image>();
        double x0 = device.x, baseline = device.y;
        foreach (var (glyphFont, index, offset, gx) in glyphs)
        {
            ts.FontRenderGlyph(glyphFont, size, index);
            int texIdx = (int)ts.FontGetGlyphTextureIdx(glyphFont, size, index);
            if (texIdx < 0) continue;
            var uv = ts.FontGetGlyphUVRect(glyphFont, size, index);
            var off = ts.FontGetGlyphOffset(glyphFont, size, index);
            var gsize = ts.FontGetGlyphSize(glyphFont, size, index);
            if (!cache.TryGetValue((glyphFont, texIdx), out var tex)) { tex = ts.FontGetTextureImage(glyphFont, size, texIdx); if (tex.IsCompressed()) tex.Decompress(); cache[(glyphFont, texIdx)] = tex; }
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
