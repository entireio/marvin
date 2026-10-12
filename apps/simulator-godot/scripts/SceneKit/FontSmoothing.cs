using System;

namespace Marvin.SceneKit;

/// <summary>
/// CoreText font smoothing as glyph dilation (stem gain in pixels for FreeType's emboldening), measured with
/// tools/scenekit-reference/TextCalibration.swift against `tools/godot -- --text-calibration DIR`
/// (PORTING.md, "AppKit views, events and text"; constants in SceneKitCalibration). CoreText's dilation depends on
/// the point size (linear, capped), the text colour's luminance (light text is dilated up to 2.4 times more than
/// black text), and the drawing path: control cells (NSTextField, borderless NSButton) drawing over a transparent
/// background add a constant 0.31 px. The background colour has no effect (measured over black, grey and white).
/// </summary>
internal static class FontSmoothing
{
    /// <summary>Linear-light luminance (Rec. 709) of an sRGB text colour.</summary>
    internal static double Luminance(NSColor srgb)
    {
        static double Linear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        return 0.2126 * Linear(srgb.redComponent) + 0.7152 * Linear(srgb.greenComponent) + 0.0722 * Linear(srgb.blueComponent);
    }
    /// <summary>Stem gain (pixels) CoreText's font smoothing gives this font's glyphs for a text colour of the given luminance.</summary>
    internal static double StemGain(NSFont font, double luminance, bool cell)
    {
        if (!font.appKitMetrics) return 0;
        double floor = SceneKitCalibration.FontSmoothingLuminanceFloor;
        double f = Math.Pow(Math.Clamp((luminance - floor) / (1 - floor), 0, 1), SceneKitCalibration.FontSmoothingLuminancePower);
        double perPoint = SceneKitCalibration.FontSmoothingBlack + (SceneKitCalibration.FontSmoothingWhite - SceneKitCalibration.FontSmoothingBlack) * f;
        double gain = Math.Min(SceneKitCalibration.FontSmoothingCap, font.pointSize * perPoint);
        return gain + (cell ? SceneKitCalibration.FontSmoothingCell : 0);
    }

    /// <summary>
    /// CoreText's horizontal subpixel positions per pixel for a glyph of this pixel size (measured, 1x backing):
    /// glyph origins are floored to multiples of 1/n px, n = 5 up to 8 px, 4 up to 11, 3 up to 16.5, 2 up to 33,
    /// whole pixels above.
    /// </summary>
    internal static int SubpixelPositions(double pixelSize) =>
        pixelSize <= 8 ? 5 : pixelSize <= 11 ? 4 : pixelSize <= 16.5 ? 3 : pixelSize <= 33 ? 2 : 1;

    /// <summary>
    /// Device x at which to draw a glyph whose CoreText origin is x: CoreText's quantized origin, moved left by
    /// SceneKitCalibration.FontEmboldenShift x stem gain because FreeType's emboldening keeps a glyph's left edges
    /// and widens it to the right, while CoreText dilates about symmetrically. Godot then rounds the result to its
    /// own quarter-pixel glyph positions.
    /// </summary>
    internal static double GlyphX(double x, double pixelSize, double stemGain)
    {
        int n = SubpixelPositions(pixelSize);
        return Math.Floor(x * n + 1e-6) / n - stemGain * SceneKitCalibration.FontEmboldenShift;
    }
}
