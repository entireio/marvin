using System;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

public partial class RaceHUD
{
    /// Rasterize static town/terrain once, rather than redrawing hundreds of buildings
    /// or sampling dune heights on every HUD frame.
    public void configureNavigationMap(TownWorld town)
    {
        if (townMapImage != null) return;
        var image = new NSImage(new NSSize(336, 336));
        image.lockFocusFlipped(true);
        var scale = 1.0;
        NSPoint point(Double2 p) => new NSPoint(168 - p.x * scale, 168 - p.y * scale);
        color(0xb49a71, alpha: 0.7).setFill();
        foreach (var corners in town.mapBuildings)
        {
            var path = new NSBezierPath();
            int i = 0;
            foreach (var p in corners) { if (i == 0) { path.move(point(p)); } else { path.line(point(p)); } i++; }
            path.close(); path.fill();
        }
        color(0xe4c899, alpha: 0.8).setStroke();
        foreach (var street in town.mapStreets)
        {
            var path = new NSBezierPath(); path.lineWidth = 1.5; path.lineJoinStyle = NSBezierPath.LineJoinStyle.round;
            int i = 0;
            foreach (var p in street) { if (i == 0) { path.move(point(p)); } else { path.line(point(p)); } i++; }
            path.stroke();
        }
        var course = new NSBezierPath();
        for (int i = 0; i <= 160; i++)
        {
            var p = DirtCourse.point((double)i * 2 * Math.PI / 160); var q = point(new Double2(p.x, p.z));
            if (i == 0) { course.move(q); } else { course.line(q); }
        }
        color(0xf1a477).setStroke(); course.lineWidth = 2.5; course.stroke();
        image.unlockFocus(); townMapImage = image;

        int n = 384; var bitmap = new NSBitmapImageRep(bitmapDataPlanes: null, pixelsWide: n, pixelsHigh: n, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: NSColorSpaceName.deviceRGB, bytesPerRow: n * 4, bitsPerPixel: 32);
        var data = bitmap.bitmapData;
        for (int y = 0; y < n; y++) { for (int x = 0; x < n; x++) {
            double px = ((double)n / 2 - (double)x) * 4, pz = ((double)n / 2 - (double)y) * 4;
            var h = DesertTerrain.height(px, pz);
            var band = 0.78 + 0.14 * min(1, max(0, h / 9)) - (abs(rounded(h) - h) < 0.09 && h > 0.2 ? 0.08 : 0);
            var i = y * bitmap.bytesPerRow + x * 4;
            data[i] = (byte)(110 * band); data[i + 1] = (byte)(98 * band); data[i + 2] = (byte)(70 * band); data[i + 3] = 255;
        } }
        var terrain = new NSImage(new NSSize(n, n)); terrain.addRepresentation(bitmap); planetMapImage = terrain;
    }
    public void drawNavigationMap(NSRect @in)
    {
        var map = @in;
        if (mapRegion == RaceMapRegion.course) { drawCourseMap(@in: map); return; }
        NSGraphicsContext.saveGraphicsState();
        try
        {
            new NSBezierPath(map).addClip();
            var radius = mapRegion == RaceMapRegion.town ? max(150, max(abs(x), abs(z)) + 14) : max(240, max(abs(x), abs(z)) * 1.2);
            var scale = (double)map.height / (radius * 2);
            NSPoint point(double px, double pz) => new NSPoint(map.midX - px * scale, map.midY - pz * scale);
            void image(NSImage image, double halfWidth, double halfHeight)
            {
                var corner = point(halfWidth, halfHeight);
                image?.draw(new NSRect(corner.x, corner.y, halfWidth * 2 * scale, halfHeight * 2 * scale), NSRect.zero, NSCompositingOperation.sourceOver, 1, respectFlipped: true, hints: null);
            }
            if (mapRegion == RaceMapRegion.dunes)
            {
                image(planetMapImage, halfWidth: 768, halfHeight: 768);
                var outline = new NSBezierPath();
                for (int i = 0; i <= 128; i++)
                {
                    var a = (double)i * 2 * Math.PI / 128; var r = TownFootprint.radius(a);
                    var p = point(cos(a) * r, sin(a) * r);
                    if (i == 0) { outline.move(p); } else { outline.line(p); }
                }
                outline.close(); color(0x253d38, alpha: 0.65).setFill(); outline.fill();
            }
            image(townMapImage, halfWidth: 168, halfHeight: 168);
            if (mapRegion == RaceMapRegion.dunes)
            {
                var line = new NSBezierPath(); line.move(point(0, 0)); line.line(point(x, z)); line.setLineDash(new double[] { 3, 3 }, count: 2, phase: 0);
                color(0xffdf9d, alpha: 0.65).setStroke(); line.lineWidth = 1; line.stroke();
                var town = point(0, 0);
                text("MOS ASTER", town.x + 7, town.y + 7, 9, true);
                text(Swift.format("TOWN  %.0f m", hypot(x, z)), map.minX + 5, map.maxY - 16, 10, true);
            }
            void marker(double px, double pz, uint tint, bool player = false)
            {
                var p = point(px, pz); CGFloat r = player ? 4 : 2.5;
                if (!map.insetBy(r, r).contains(p)) return;
                color(0x14251e).setFill(); NSBezierPath.ovalIn(new NSRect(p.x - r - 2, p.y - r - 2, (r + 2) * 2, (r + 2) * 2)).fill();
                color(tint).setFill(); NSBezierPath.ovalIn(new NSRect(p.x - r, p.y - r, r * 2, r * 2)).fill();
                if (player)
                {
                    color(0xffffff).setStroke(); var ring = NSBezierPath.ovalIn(new NSRect(p.x - r - 1, p.y - r - 1, (r + 1) * 2, (r + 1) * 2)); ring.lineWidth = 1; ring.stroke();
                    var arrow = new NSBezierPath(); arrow.move(p); arrow.line(new NSPoint(p.x - sin(heading) * 11, p.y - cos(heading) * 11)); arrow.lineWidth = 2; arrow.stroke();
                }
            }
            marker(x, z, playerColor, true);
        }
        finally { NSGraphicsContext.restoreGraphicsState(); }
    }
}
