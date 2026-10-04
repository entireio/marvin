using System;
using static Marvin.Core.Swift;

namespace Marvin;

/// Irregular density rather than a radial white sprite. Geometry colors are
/// explicitly consumed: SceneKit's constant material does not apply them for us.
public static class WindblownDust
{
    public static void configure(SCNMaterial material)
    {
        material.shaderModifiers = new()
        {
            [SCNShaderModifierEntryPoint.geometry] = @"
#pragma varyings
vec4 dustTint;
#pragma body
dustTint = COLOR;
",
            [SCNShaderModifierEntryPoint.surface] = @"
#pragma body
ALBEDO *= pow(max(dustTint.rgb, vec3(0.0)), vec3(2.2));
",
            // _surface.diffuse.a is ALPHA here (#pragma transparent); `_output.color.rgb *= opacity;
            // _output.color.a = opacity;` is the premultiplied idiom `ALPHA = opacity;` (PORTING.md).
            [SCNShaderModifierEntryPoint.fragment] = @"
#pragma transparent
#pragma body
float opacity = ALPHA * dustTint.a;
ALPHA = opacity;
",
        };
    }
    public static NSImage texture(bool plume = false)
    {
        int size = 128;
        var bitmap = new NSBitmapImageRep(bitmapDataPlanes: null, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: NSColorSpaceName.deviceRGB, bytesPerRow: size * 4, bitsPerPixel: 32);
        var bytes = bitmap.bitmapData;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double u = (double)x / (size - 1), v = (double)y / (size - 1);
                double broad = CityMaterials.surfaceNoise(u, v, cells: 5, seed: 71);
                double fine = CityMaterials.surfaceNoise(u, v, cells: 17, seed: 29);
                double envelope = pow(max(0, sin(u * Math.PI) * sin(v * Math.PI)), 2);
                double noise = max(0, min(1, (broad * 0.7 + fine * 0.3 - 0.22) * 1.6));
                // Tire plumes need optical density; ambient storm wisps stay diffuse.
                double density = plume ? pow(envelope, 0.65) * pow(noise, 0.55) : envelope * noise;
                int i = (y * size + x) * 4;
                bytes[i] = 255; bytes[i + 1] = 255; bytes[i + 2] = 255; bytes[i + 3] = (byte)(density * 255);
            }
        var image = new NSImage(new NSSize(size, size)); image.addRepresentation(bitmap); return image;
    }
}
