using System;

namespace Marvin.SceneKit;

/// <summary>
/// Foundation AffineTransform (value type). Row-vector convention like CGAffineTransform:
/// x' = m11·x + m21·y + tX, y' = m12·x + m22·y + tY. The mutating methods prepend
/// (translate/rotate/scale act in the transform's own coordinate system, as in Foundation),
/// so <c>var t = AffineTransform.translationByX(x, y); t.rotate(byRadians: a)</c> rotates first, then translates.
/// </summary>
public struct AffineTransform : IEquatable<AffineTransform>
{
    public double m11, m12, m21, m22, tX, tY;
    public AffineTransform(double m11, double m12, double m21, double m22, double tX, double tY)
    { this.m11 = m11; this.m12 = m12; this.m21 = m21; this.m22 = m22; this.tX = tX; this.tY = tY; }
    public static readonly AffineTransform identity = new(1, 0, 0, 1, 0, 0);
    /// <summary>AffineTransform(translationByX:byY:).</summary>
    public static AffineTransform translationByX(double x, double byY) => new(1, 0, 0, 1, x, byY);
    /// <summary>AffineTransform(rotationByRadians:).</summary>
    public static AffineTransform rotationByRadians(double angle) { double c = Math.Cos(angle), s = Math.Sin(angle); return new(c, s, -s, c, 0, 0); }
    /// <summary>AffineTransform(rotationByDegrees:).</summary>
    public static AffineTransform rotationByDegrees(double angle) => rotationByRadians(angle * Math.PI / 180);
    /// <summary>AffineTransform(scaleByX:byY:).</summary>
    public static AffineTransform scaleByX(double x, double byY) => new(x, 0, 0, byY, 0, 0);
    /// <summary>AffineTransform(scale:).</summary>
    public static AffineTransform scale(double factor) => new(factor, 0, 0, factor, 0, 0);

    /// <summary>a·b: apply a first, then b.</summary>
    public static AffineTransform Multiply(AffineTransform a, AffineTransform b) => new(
        a.m11 * b.m11 + a.m12 * b.m21, a.m11 * b.m12 + a.m12 * b.m22,
        a.m21 * b.m11 + a.m22 * b.m21, a.m21 * b.m12 + a.m22 * b.m22,
        a.tX * b.m11 + a.tY * b.m21 + b.tX, a.tX * b.m12 + a.tY * b.m22 + b.tY);
    public void translate(double x, double y) => this = Multiply(translationByX(x, y), this);
    public void rotate(double byRadians) => this = Multiply(rotationByRadians(byRadians), this);
    public void rotateByDegrees(double degrees) => this = Multiply(rotationByDegrees(degrees), this);
    public void scale(double x, double y) => this = Multiply(scaleByX(x, y), this);
    /// <summary>append(_:): the other transform is applied after this one.</summary>
    public void append(AffineTransform transform) => this = Multiply(this, transform);
    /// <summary>prepend(_:): the other transform is applied before this one.</summary>
    public void prepend(AffineTransform transform) => this = Multiply(transform, this);
    public void invert() => this = inverted();
    public AffineTransform inverted()
    {
        double det = m11 * m22 - m12 * m21;
        if (Math.Abs(det) < 1e-300) return this;
        double a = m22 / det, b = -m12 / det, c = -m21 / det, d = m11 / det;
        return new(a, b, c, d, -(tX * a + tY * c), -(tX * b + tY * d));
    }
    public CGPoint transform(CGPoint point) => new(m11 * point.x + m21 * point.y + tX, m12 * point.x + m22 * point.y + tY);
    public CGSize transform(CGSize size) => new(m11 * size.width + m21 * size.height, m12 * size.width + m22 * size.height);
    public bool isIdentity => this == identity;
    /// <summary>(t as NSAffineTransform).concat(): prepends this transform to the current context's CTM.</summary>
    public void concat() => NSGraphicsContext.current?.Concat(this);
    public bool Equals(AffineTransform o) => m11 == o.m11 && m12 == o.m12 && m21 == o.m21 && m22 == o.m22 && tX == o.tX && tY == o.tY;
    public override bool Equals(object o) => o is AffineTransform t && Equals(t);
    public override int GetHashCode() => HashCode.Combine(m11, m12, m21, m22, tX, tY);
    public static bool operator ==(AffineTransform a, AffineTransform b) => a.Equals(b);
    public static bool operator !=(AffineTransform a, AffineTransform b) => !a.Equals(b);
}

/// <summary>
/// NSAffineTransform (reference type). <c>(transform as NSAffineTransform)</c> is the explicit conversion
/// <c>(NSAffineTransform)transform</c>. translateX/scaleX/rotate prepend like AppKit; concat() prepends the
/// matrix to the current graphics context's transformation (CGContextConcatCTM).
/// </summary>
public sealed class NSAffineTransform
{
    public AffineTransform transformStruct;
    public NSAffineTransform() { transformStruct = AffineTransform.identity; }
    public NSAffineTransform(NSAffineTransform transform) { transformStruct = transform.transformStruct; }
    private NSAffineTransform(AffineTransform t) { transformStruct = t; }
    public static explicit operator NSAffineTransform(AffineTransform t) => new(t);
    public static implicit operator AffineTransform(NSAffineTransform t) => t.transformStruct;
    /// <summary>translateX(by:yBy:).</summary>
    public void translateX(double by, double yBy) => transformStruct.translate(by, yBy);
    /// <summary>scaleX(by:yBy:).</summary>
    public void scaleX(double by, double yBy) => transformStruct.scale(by, yBy);
    /// <summary>scale(by:).</summary>
    public void scale(double by) => transformStruct.scale(by, by);
    /// <summary>rotate(byRadians:).</summary>
    public void rotate(double byRadians) => transformStruct.rotate(byRadians);
    /// <summary>rotate(byDegrees:).</summary>
    public void rotateByDegrees(double degrees) => transformStruct.rotateByDegrees(degrees);
    public void append(NSAffineTransform transform) => transformStruct.append(transform.transformStruct);
    public void prepend(NSAffineTransform transform) => transformStruct.prepend(transform.transformStruct);
    public void invert() => transformStruct.invert();
    public CGPoint transform(CGPoint point) => transformStruct.transform(point);
    public void concat() => NSGraphicsContext.current?.Concat(transformStruct);
    /// <summary>set(): replaces the current context's CTM.</summary>
    public void set() { if (NSGraphicsContext.current is NSGraphicsContext c) c.SetCTM(transformStruct); }
}

/// <summary>NSCompositingOperation (only sourceOver is drawn; others are accepted for API parity).</summary>
public enum NSCompositingOperation { clear = 0, copy = 1, sourceOver = 2, sourceIn = 3, sourceOut = 4, sourceAtop = 5, destinationOver = 6, destinationIn = 7, destinationOut = 8, destinationAtop = 9, xor = 10, plusDarker = 11, plusLighter = 13, multiply = 14 }

/// <summary>CGRect operations AppKit code uses beyond SCNVector.cs.</summary>
public static class CGRectExtensions
{
    /// <summary>contains(_ rect:): the other rectangle lies completely inside.</summary>
    public static bool contains(this CGRect r, CGRect other) =>
        other.minX >= r.minX && other.maxX <= r.maxX && other.minY >= r.minY && other.maxY <= r.maxY;
    public static CGRect intersection(this CGRect a, CGRect b)
    {
        double x0 = Math.Max(a.minX, b.minX), y0 = Math.Max(a.minY, b.minY), x1 = Math.Min(a.maxX, b.maxX), y1 = Math.Min(a.maxY, b.maxY);
        return x1 < x0 || y1 < y0 ? CGRect.zero : new CGRect(x0, y0, x1 - x0, y1 - y0);
    }
    public static bool intersects(this CGRect a, CGRect b) => a.minX < b.maxX && b.minX < a.maxX && a.minY < b.maxY && b.minY < a.maxY;
    public static CGRect offsetBy(this CGRect r, double dx, double dy) => new(r.origin.x + dx, r.origin.y + dy, r.size.width, r.size.height);
    public static bool isEmpty(this CGRect r) => r.size.width <= 0 || r.size.height <= 0;
}
