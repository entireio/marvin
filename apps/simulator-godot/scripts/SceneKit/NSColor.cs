using System;
using Godot;

namespace Marvin.SceneKit;

/// <summary>
/// AppKit colour spaces used by the game. Conversions to extended linear sRGB
/// (the space SceneKit shades in) were measured on macOS with
/// NSColor.usingColorSpace(extendedLinearSRGB):
/// - sRGB, deviceRGB, deviceWhite, white:, genericGamma22White: sRGB transfer curve.
/// - calibratedRGB ("Generic RGB"): pure gamma 1.8 then a 3x3 primaries matrix.
/// - calibratedWhite ("Generic Gray"): pure gamma 1.8.
/// </summary>
public sealed class NSColorSpace
{
    internal enum Kind { SRGB, ExtendedSRGB, GenericRGB, GenericGray, Gray22, LinearSRGB, DisplayP3 }
    internal readonly Kind kind;
    private NSColorSpace(Kind kind) { this.kind = kind; }
    public static readonly NSColorSpace sRGB = new(Kind.SRGB);
    public static readonly NSColorSpace extendedSRGB = new(Kind.ExtendedSRGB);
    public static readonly NSColorSpace deviceRGB = new(Kind.SRGB);
    public static readonly NSColorSpace genericRGB = new(Kind.GenericRGB);
    public static readonly NSColorSpace genericGray = new(Kind.GenericGray);
    public static readonly NSColorSpace deviceGray = new(Kind.Gray22);
    public static readonly NSColorSpace genericGamma22Gray = new(Kind.Gray22);
    public static readonly NSColorSpace extendedLinearSRGB = new(Kind.LinearSRGB);
    public static readonly NSColorSpace displayP3 = new(Kind.DisplayP3);
    internal static NSColorSpace Of(Kind k) => k switch
    {
        Kind.SRGB => sRGB, Kind.ExtendedSRGB => extendedSRGB, Kind.GenericRGB => genericRGB, Kind.GenericGray => genericGray,
        Kind.Gray22 => genericGamma22Gray, Kind.LinearSRGB => extendedLinearSRGB, _ => displayP3,
    };
    public override string ToString() => kind.ToString();
}

/// <summary>NSColorSpaceName values passed to NSBitmapImageRep.</summary>
public enum NSColorSpaceName { deviceRGB, calibratedRGB, deviceWhite, calibratedWhite }

/// <summary>
/// NSColor. Swift initialisers with labels map to static factories named after the
/// first label (NSColor(srgbRed:green:blue:alpha:) -> NSColor.srgbRed(r,g,b,a)).
/// NSColor(red:green:blue:alpha:) -> NSColor.rgb(...), NSColor(white:alpha:) -> NSColor.whiteAlpha(...).
/// </summary>
public sealed class NSColor : IEquatable<NSColor>
{
    internal readonly NSColorSpace.Kind space;
    internal readonly double c0, c1, c2;
    public readonly double alphaComponent;
    private NSColor(NSColorSpace.Kind space, double r, double g, double b, double a) { this.space = space; c0 = r; c1 = g; c2 = b; alphaComponent = a; }

    public static NSColor srgbRed(double red, double green, double blue, double alpha) => new(NSColorSpace.Kind.SRGB, red, green, blue, alpha);
    public static NSColor rgb(double red, double green, double blue, double alpha) => new(NSColorSpace.Kind.SRGB, red, green, blue, alpha);
    public static NSColor deviceRed(double red, double green, double blue, double alpha) => new(NSColorSpace.Kind.SRGB, red, green, blue, alpha);
    public static NSColor calibratedRed(double red, double green, double blue, double alpha) => new(NSColorSpace.Kind.GenericRGB, red, green, blue, alpha);
    public static NSColor displayP3Red(double red, double green, double blue, double alpha) => new(NSColorSpace.Kind.DisplayP3, red, green, blue, alpha);
    public static NSColor calibratedWhite(double white, double alpha) => new(NSColorSpace.Kind.GenericGray, white, white, white, alpha);
    public static NSColor deviceWhite(double white, double alpha) => new(NSColorSpace.Kind.Gray22, white, white, white, alpha);
    public static NSColor genericGamma22White(double white, double alpha) => new(NSColorSpace.Kind.Gray22, white, white, white, alpha);
    public static NSColor whiteAlpha(double white, double alpha) => new(NSColorSpace.Kind.Gray22, white, white, white, alpha);
    /// <summary>Linear-light extended sRGB components (not an AppKit initialiser; facade helper).</summary>
    public static NSColor linear(double red, double green, double blue, double alpha = 1) => new(NSColorSpace.Kind.LinearSRGB, red, green, blue, alpha);

    // Named colours (AppKit: white/black/gray family are Generic Gray Gamma 2.2; the rest sRGB).
    public static readonly NSColor white = new(NSColorSpace.Kind.Gray22, 1, 1, 1, 1);
    public static readonly NSColor black = new(NSColorSpace.Kind.Gray22, 0, 0, 0, 1);
    public static readonly NSColor clear = new(NSColorSpace.Kind.Gray22, 0, 0, 0, 0);
    public static readonly NSColor gray = new(NSColorSpace.Kind.Gray22, 0.5, 0.5, 0.5, 1);
    public static readonly NSColor darkGray = new(NSColorSpace.Kind.Gray22, 1.0 / 3, 1.0 / 3, 1.0 / 3, 1);
    public static readonly NSColor lightGray = new(NSColorSpace.Kind.Gray22, 2.0 / 3, 2.0 / 3, 2.0 / 3, 1);
    public static readonly NSColor red = new(NSColorSpace.Kind.SRGB, 1, 0, 0, 1);
    public static readonly NSColor green = new(NSColorSpace.Kind.SRGB, 0, 1, 0, 1);
    public static readonly NSColor blue = new(NSColorSpace.Kind.SRGB, 0, 0, 1, 1);
    public static readonly NSColor yellow = new(NSColorSpace.Kind.SRGB, 1, 1, 0, 1);
    public static readonly NSColor orange = new(NSColorSpace.Kind.SRGB, 1, 0.5, 0, 1);
    public static readonly NSColor cyan = new(NSColorSpace.Kind.SRGB, 0, 1, 1, 1);
    public static readonly NSColor magenta = new(NSColorSpace.Kind.SRGB, 1, 0, 1, 1);
    /// <summary>windowBackgroundColor (light appearance).</summary>
    public static readonly NSColor windowBackgroundColor = new(NSColorSpace.Kind.SRGB, 0.925, 0.925, 0.925, 1);

    public NSColorSpace colorSpace => NSColorSpace.Of(space);
    /// <summary>setFill()/setStroke()/set(): current drawing colours for NSBezierPath/NSRect drawing.</summary>
    public void setFill() => NSGraphicsContext.FillColor = this;
    public void setStroke() => NSGraphicsContext.StrokeColor = this;
    public void set() { NSGraphicsContext.FillColor = this; NSGraphicsContext.StrokeColor = this; }
    public NSColor withAlphaComponent(double alpha) => new(space, c0, c1, c2, alpha);
    /// <summary>Components in the colour's own space (AppKit raises for non-RGB; this returns the stored value).</summary>
    public double redComponent => c0;
    public double greenComponent => c1;
    public double blueComponent => c2;
    public double whiteComponent => c0;
    public NSColor cgColor => this;
    public NSColor usingColorSpace(NSColorSpace target)
    {
        var (r, g, b) = LinearRGB;
        return target.kind switch
        {
            NSColorSpace.Kind.LinearSRGB => new NSColor(target.kind, r, g, b, alphaComponent),
            NSColorSpace.Kind.SRGB or NSColorSpace.Kind.ExtendedSRGB =>
                new NSColor(target.kind, LinearToSrgb(r), LinearToSrgb(g), LinearToSrgb(b), alphaComponent),
            NSColorSpace.Kind.Gray22 => new NSColor(target.kind, LinearToSrgb(Luma(r, g, b)), LinearToSrgb(Luma(r, g, b)), LinearToSrgb(Luma(r, g, b)), alphaComponent),
            NSColorSpace.Kind.GenericGray => new NSColor(target.kind, Pow18Inv(Luma(r, g, b)), Pow18Inv(Luma(r, g, b)), Pow18Inv(Luma(r, g, b)), alphaComponent),
            NSColorSpace.Kind.GenericRGB => FromLinearToGeneric(r, g, b, alphaComponent),
            _ => this,
        };
    }

    /// <summary>Extended linear sRGB components (what SceneKit shades with).</summary>
    public (double r, double g, double b) LinearRGB
    {
        get
        {
            switch (space)
            {
                case NSColorSpace.Kind.LinearSRGB: return (c0, c1, c2);
                case NSColorSpace.Kind.SRGB:
                case NSColorSpace.Kind.ExtendedSRGB:
                case NSColorSpace.Kind.Gray22: return (SrgbToLinear(c0), SrgbToLinear(c1), SrgbToLinear(c2));
                case NSColorSpace.Kind.GenericGray: return (Pow18(c0), Pow18(c1), Pow18(c2));
                case NSColorSpace.Kind.GenericRGB:
                {
                    double r = Pow18(c0), g = Pow18(c1), b = Pow18(c2);
                    return (G[0] * r + G[3] * g + G[6] * b, G[1] * r + G[4] * g + G[7] * b, G[2] * r + G[5] * g + G[8] * b);
                }
                default:
                {
                    // Display P3 (sRGB curve, P3 primaries) to linear sRGB.
                    double r = SrgbToLinear(c0), g = SrgbToLinear(c1), b = SrgbToLinear(c2);
                    return (1.2249 * r - 0.2247 * g, -0.0420 * r + 1.0419 * g, -0.0197 * r - 0.0786 * g + 1.0979 * b);
                }
            }
        }
    }
    /// <summary>Linear RGBA as a Godot Color (raw linear components, for shader uniforms).</summary>
    public Color GodotLinear { get { var (r, g, b) = LinearRGB; return new Color((float)r, (float)g, (float)b, (float)alphaComponent); } }
    /// <summary>sRGB-encoded Godot Color (for Godot properties that expect sRGB: light colour, background colour).</summary>
    public Color GodotSrgb { get { var (r, g, b) = LinearRGB; return new Color((float)LinearToSrgb(r), (float)LinearToSrgb(g), (float)LinearToSrgb(b), (float)alphaComponent); } }

    // Generic RGB -> linear sRGB, columns are the images of (1,0,0), (0,1,0), (0,0,1) (measured).
    private static readonly double[] G = { 1.0252417, 0.0194008, -0.0017621, -0.0265452, 0.9480088, -0.0014345, 0.0013036, 0.0325904, 1.0031966 };
    private static NSColor FromLinearToGeneric(double r, double g, double b, double a)
    {
        // Invert G.
        var m = new SCNMatrix4(G[0], G[1], G[2], 0, G[3], G[4], G[5], 0, G[6], G[7], G[8], 0, 0, 0, 0, 1);
        var v = SCNMatrix4.Inverse(m).TransformVector(new SCNVector3(r, g, b));
        return new NSColor(NSColorSpace.Kind.GenericRGB, Pow18Inv(v.x), Pow18Inv(v.y), Pow18Inv(v.z), a);
    }
    private static double Pow18(double v) => Math.Sign(v) * Math.Pow(Math.Abs(v), 1.8);
    private static double Pow18Inv(double v) => Math.Sign(v) * Math.Pow(Math.Abs(v), 1 / 1.8);
    private static double Luma(double r, double g, double b) => 0.2126 * r + 0.7152 * g + 0.0722 * b;
    public static double SrgbToLinear(double v)
    {
        double a = Math.Abs(v);
        double l = a <= 0.04045 ? a / 12.92 : Math.Pow((a + 0.055) / 1.055, 2.4);
        return Math.Sign(v) * l;
    }
    public static double LinearToSrgb(double v)
    {
        double a = Math.Abs(v);
        double s = a <= 0.0031308 ? a * 12.92 : 1.055 * Math.Pow(a, 1 / 2.4) - 0.055;
        return Math.Sign(v) * s;
    }

    public bool Equals(NSColor o) => o is not null && space == o.space && c0 == o.c0 && c1 == o.c1 && c2 == o.c2 && alphaComponent == o.alphaComponent;
    public override bool Equals(object o) => o is NSColor c && Equals(c);
    public override int GetHashCode() => HashCode.Combine(space, c0, c1, c2, alphaComponent);
    public override string ToString() => $"NSColor({space} {c0} {c1} {c2} {alphaComponent})";
}

/// <summary>NSValue boxes used for shader arguments via setValue(_:forKey:).</summary>
public sealed class NSValue
{
    public readonly object value;
    private NSValue(object value) { this.value = value; }
    public static NSValue scnVector3(SCNVector3 v) => new(v);
    public static NSValue scnVector4(SCNVector4 v) => new(v);
    public static NSValue scnMatrix4(SCNMatrix4 m) => new(m);
    public static NSValue point(CGPoint p) => new(p);
    public static NSValue size(CGSize s) => new(s);
    public static NSValue rect(CGRect r) => new(r);
    public SCNVector3 scnVector3Value => (SCNVector3)value;
    public SCNVector4 scnVector4Value => (SCNVector4)value;
    public SCNMatrix4 scnMatrix4Value => (SCNMatrix4)value;
    public CGPoint pointValue => (CGPoint)value;
}
