using System;
using static Marvin.Core.Swift;

namespace Marvin.Core;

/// A baked heightfield shared by rendering, wheel contact and terrain collision.
/// The settlement stays flat; wind-shaped ridges rise beyond its last compounds.
public static class DesertTerrain
{
    public const double extent = 768.0, spacing = 2.0, townEdge = 156.0;
    private const int count = (int)(extent * 2 / spacing) + 1;
    private static double smooth(double x) { var t = max(0, min(1, x)); return t * t * (3 - 2 * t); }
    private static double noise(double x, double z)
    {
        long ix = (long)floor(x), iz = (long)floor(z);
        double u = smooth(x - floor(x)), v = smooth(z - floor(z));
        static double hash(long a, long b)
        {
            unchecked
            {
                var n = ((ulong)a * 0x9e3779b185ebca87) ^ ((ulong)b * 0xc2b2ae3d27d4eb4f);
                n ^= n >> 29; n *= 0x165667b19e3779f9; n ^= n >> 32;
                return (double)(n & 0xffffff) / (double)0xffffff;
            }
        }
        return (hash(ix, iz) * (1 - u) + hash(ix + 1, iz) * u) * (1 - v) + (hash(ix, iz + 1) * (1 - u) + hash(ix + 1, iz + 1) * u) * v;
    }
    private static double sample(double x, double z)
    {
        var edge = max(abs(x), abs(z));
        if (!(edge > townEdge && edge < extent)) return -0.025;
        var apron = smooth((edge - townEdge) / 30);
        // Fade out only at the distant map perimeter, beyond the fog horizon.
        var perimeter = smooth((extent - edge) / 96);
        var warp = 15 * (noise(x / 90, z / 90) - 0.5) + 7 * sin(z * 0.019);
        var phase = (x * 0.94 + z * 0.34 + warp) / 64;
        double u = phase - floor(phase), crest = 0.67;
        var ridge = u < crest ? smooth(u / crest) : 1 - smooth((u - crest) / (1 - crest));
        var amplitude = 6.0 + 4.0 * noise(x / 127 + 8, z / 113 - 3);
        var swell = 1.8 * pow(0.5 + 0.5 * sin((x * 0.3 - z * 0.95) / 29 + noise(x / 93, z / 101) * 2), 2);
        return -0.025 + 0.8 * apron * perimeter * (amplitude * ridge + swell);
    }
    // PORT: Swift static lets are lazy; a nested holder class keeps this ~590k-sample bake
    // from running until a height is actually requested.
    private static class Baked
    {
        internal static readonly double[] samples = build();
        private static double[] build()
        {
            var result = new double[count * count];
            for (var z = 0; z < count; z++)
            {
                for (var x = 0; x < count; x++)
                {
                    result[z * count + x] = sample((double)x * spacing - extent, (double)z * spacing - extent);
                }
            }
            return result;
        }
    }
    public static double vertexHeight(double x, double z)
    {
        var ix = max(0, min(count - 1, (int)rounded((x + extent) / spacing)));
        var iz = max(0, min(count - 1, (int)rounded((z + extent) / spacing)));
        return Baked.samples[iz * count + ix];
    }
    /// Same diagonal and barycentric interpolation as the two rendered triangles.
    public static double height(double x, double z)
    {
        if (!(max(abs(x), abs(z)) > townEdge - 2 && max(abs(x), abs(z)) < extent)) return -0.025;
        double gx = (x + extent) / spacing, gz = (z + extent) / spacing;
        int ix = (int)floor(gx), iz = (int)floor(gz);
        double u = gx - (double)ix, v = gz - (double)iz;
        var samples = Baked.samples;
        double a = samples[iz * count + ix], b = samples[iz * count + ix + 1];
        double c = samples[(iz + 1) * count + ix], d = samples[(iz + 1) * count + ix + 1];
        return u + v <= 1 ? a + (b - a) * u + (c - a) * v : d + (c - d) * (1 - u) + (b - d) * (1 - v);
    }
    public static Double2 gradient(double x, double z)
    {
        var e = 0.3;
        return new Double2((height(x + e, z) - height(x - e, z)) / (2 * e),
                           (height(x, z + e) - height(x, z - e)) / (2 * e));
    }
}
