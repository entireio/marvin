using System;
using System.Collections.Generic;
using Godot;

namespace Marvin.SceneKit;

/// <summary>
/// SCNScene: a Godot World3D plus a (never rendering) host SubViewport that keeps
/// rootNode inside the scene tree. SCNView/SCNRenderer viewports render the same
/// World3D with their own Camera3D, so one scene can be shown by several views.
/// </summary>
public sealed class SCNScene : IPropertyOwner
{
    public readonly SCNNode rootNode;
    /// <summary>background.contents: NSColor or an equirectangular NSImage.</summary>
    public readonly SCNMaterialProperty background;
    /// <summary>lightingEnvironment.contents: equirectangular NSImage used for ambient (SH) and reflections; intensity scales both.</summary>
    public readonly SCNMaterialProperty lightingEnvironment;
    private double _fogStartDistance, _fogEndDistance, _fogDensityExponent = 1;
    private object _fogColor = NSColor.white;
    public bool isPaused;
    public string name;
    internal readonly World3D World = new();
    internal SubViewport host;
    internal int stateVersion;

    private bool attached;
    public SCNScene()
    {
        rootNode = new SCNNode { name = "root" };
        rootNode.SetSceneOwner(this);
        background = new SCNMaterialProperty(null, this);
        lightingEnvironment = new SCNMaterialProperty(null, this);
        lightingEnvironment.intensity = 1;
        host = new SubViewport
        {
            Name = "SCNScene",
            World3D = World,
            OwnWorld3D = false,
            Size = new Vector2I(2, 2),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            Disable3D = false,
        };
        host.AddChild(rootNode);
        // A scene built off the main thread stays outside the tree until the main thread shows it.
        if (SceneKitRuntime.OnMainThread) EnsureAttached();
    }
    /// <summary>Main thread: puts the scene's world into the tree and adopts changes parked by builder threads.</summary>
    internal void EnsureAttached()
    {
        if (attached || !SceneKitRuntime.OnMainThread) return;
        attached = true;
        SceneKitRuntime.AttachHost(host);
        SceneKitRuntime.Adopt(rootNode);
    }

    public double fogStartDistance { get => _fogStartDistance; set { _fogStartDistance = value; Changed(); } }
    public double fogEndDistance { get => _fogEndDistance; set { _fogEndDistance = value; Changed(); } }
    public double fogDensityExponent { get => _fogDensityExponent; set { _fogDensityExponent = value; Changed(); } }
    /// <summary>fogColor (Any: NSColor).</summary>
    public object fogColor { get => _fogColor; set { _fogColor = value; Changed(); } }

    void IPropertyOwner.PropertyChanged(SCNMaterialProperty property) => Changed();
    private void Changed() { stateVersion++; SceneKitRuntime.SceneStateDirty(); }

    // ---- derived state for rendering
    private object shSource;
    private double[] sh = new double[27];
    private Sky sky;
    private PanoramaSkyMaterial skyMaterial;
    private object skySource;

    /// <summary>Order-2 spherical harmonics of lightingEnvironment, pre-convolved for Lambertian irradiance
    /// (radiance-equivalent: a uniform environment v gives v).</summary>
    internal double[] IrradianceSH()
    {
        var src = lightingEnvironment.contents;
        if (ReferenceEquals(src, shSource)) return sh;
        shSource = src;
        sh = new double[27];
        Image img = src switch { NSImage n => n.GodotImage, string p => NSImage.contentsOf(p)?.GodotImage, _ => null };
        if (img == null) return sh;
        img = (Image)img.Duplicate();
        if (img.IsCompressed()) img.Decompress();
        if (img.GetFormat() != Image.Format.Rgba8 && img.GetFormat() != Image.Format.Rgbaf) img.Convert(Image.Format.Rgba8);
        int w = img.GetWidth(), h = img.GetHeight();
        // Godot panorama convention: u = atan(x, -z) / 2pi + 0.5, v = acos(y) / pi.
        var L = new double[27];
        double dPhi = 2 * Math.PI / w, dTheta = Math.PI / h;
        for (int y = 0; y < h; y++)
        {
            double theta = (y + 0.5) * dTheta, st = Math.Sin(theta), ct = Math.Cos(theta);
            double dw = st * dTheta * dPhi;
            for (int x = 0; x < w; x++)
            {
                double phi = ((x + 0.5) / w - 0.5) * 2 * Math.PI;
                double dx = st * Math.Sin(phi), dy = ct, dz = -st * Math.Cos(phi);
                var c = img.GetPixel(x, y);
                double r = NSColor.SrgbToLinear(c.R), g = NSColor.SrgbToLinear(c.G), b = NSColor.SrgbToLinear(c.B);
                double[] Y = Basis(dx, dy, dz);
                for (int k = 0; k < 9; k++) { L[k * 3] += r * Y[k] * dw; L[k * 3 + 1] += g * Y[k] * dw; L[k * 3 + 2] += b * Y[k] * dw; }
            }
        }
        // E(n)/pi with A0 = pi, A1 = 2pi/3, A2 = pi/4, folded with the basis constants used by the shader polynomial.
        double[] scale = { 1.0 * 0.282095, 2.0 / 3 * 0.488603, 2.0 / 3 * 0.488603, 2.0 / 3 * 0.488603, 0.25 * 1.092548, 0.25 * 1.092548, 0.25 * 0.315392, 0.25 * 1.092548, 0.25 * 0.546274 };
        for (int k = 0; k < 9; k++) for (int c = 0; c < 3; c++) sh[k * 3 + c] = L[k * 3 + c] * scale[k];
        return sh;
    }
    private static double[] Basis(double x, double y, double z) => new[]
    {
        0.282095, 0.488603 * y, 0.488603 * z, 0.488603 * x, 1.092548 * x * y, 1.092548 * y * z, 0.315392 * (3 * z * z - 1), 1.092548 * x * z, 0.546274 * (x * x - y * y),
    };

    internal bool HasLightingEnvironment => lightingEnvironment.Kind == SCNMaterialProperty.ContentKind.Texture;

    // ---- Pre-filtered radiance (specular image-based lighting)
    internal const int RadianceWidth = 64, RadianceHeight = 32, RadianceLevels = 6;
    private object radianceSource;
    private ImageTexture radianceTexture;
    /// <summary>
    /// lightingEnvironment convolved with GGX lobes for roughness 0, 0.2, ... 1.0 (alpha = r^2, weight D(N.H) x N.L x
    /// solid angle with N = V = R), as RadianceLevels equirect bands of RadianceWidth x RadianceHeight stacked
    /// vertically (linear float). SceneKit reflects a sharp environment on smooth materials (the game's sky probe has
    /// a hard horizon); order-2 SH cannot, so the composer samples these bands for the specular part.
    /// </summary>
    internal ImageTexture RadianceTexture()
    {
        var src = lightingEnvironment.contents;
        if (radianceTexture != null && ReferenceEquals(src, radianceSource)) return radianceTexture;
        radianceSource = src;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Image img = src switch { NSImage n => n.GodotImage, string p => NSImage.contentsOf(p)?.GodotImage, _ => null };
        int W = RadianceWidth, H = RadianceHeight;
        var input = new Vector3[W * H];
        if (img != null)
        {
            img = (Image)img.Duplicate();
            if (img.IsCompressed()) img.Decompress();
            img.Convert(Image.Format.Rgbaf);
            if (img.GetWidth() != W || img.GetHeight() != H) img.Resize(W, H, Image.Interpolation.Bilinear);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    var c = img.GetPixel(x, y);
                    input[y * W + x] = new Vector3((float)NSColor.SrgbToLinear(c.R), (float)NSColor.SrgbToLinear(c.G), (float)NSColor.SrgbToLinear(c.B));
                }
        }
        // Texel directions (Godot panorama convention, as IrradianceSH) and solid angles.
        int N = W * H;
        var dx = new float[N]; var dy = new float[N]; var dz = new float[N]; var dw = new float[N];
        for (int y = 0; y < H; y++)
        {
            double theta = (y + 0.5) * Math.PI / H, st = Math.Sin(theta), ct = Math.Cos(theta);
            for (int x = 0; x < W; x++)
            {
                double phi = ((x + 0.5) / W - 0.5) * 2 * Math.PI;
                int i = y * W + x;
                dx[i] = (float)(st * Math.Sin(phi)); dy[i] = (float)ct; dz[i] = (float)(-st * Math.Cos(phi));
                dw[i] = (float)(st * (Math.PI / H) * (2 * Math.PI / W));
            }
        }
        var rgba = new float[N * RadianceLevels * 4];
        for (int o = 0; o < N; o++) { rgba[o * 4] = input[o].X; rgba[o * 4 + 1] = input[o].Y; rgba[o * 4 + 2] = input[o].Z; rgba[o * 4 + 3] = 1; }
        for (int level = 1; level < RadianceLevels; level++)
        {
            float r = (float)level / (RadianceLevels - 1), a2 = Math.Max(r * r, 1e-3f); a2 *= a2;
            // Skip the lobe's tail (D below ~0.4% of its peak): angle to R > 2 atan(5 alpha), at most a hemisphere.
            float minCos = (float)Math.Cos(Math.Min(Math.PI / 2, 2 * Math.Atan(5 * r * r)));
            int lv = level;
            System.Threading.Tasks.Parallel.For(0, N, o =>
            {
                float rx = dx[o], ry = dy[o], rz = dz[o], sr = 0, sg = 0, sb = 0, ws = 0;
                for (int i = 0; i < N; i++)
                {
                    float nl = rx * dx[i] + ry * dy[i] + rz * dz[i];
                    if (nl <= minCos || nl <= 0) continue;
                    float den = (1 + nl) * 0.5f * (a2 - 1) + 1; // N.H^2 (a^2 - 1) + 1 with N = V = R
                    float w = nl * dw[i] / (den * den);           // D(N.H) x N.L x solid angle (constant factors cancel)
                    var c = input[i]; sr += c.X * w; sg += c.Y * w; sb += c.Z * w; ws += w;
                }
                int j = (lv * N + o) * 4;
                if (ws > 0) { rgba[j] = sr / ws; rgba[j + 1] = sg / ws; rgba[j + 2] = sb / ws; } else { rgba[j] = input[o].X; rgba[j + 1] = input[o].Y; rgba[j + 2] = input[o].Z; }
                rgba[j + 3] = 1;
            });
        }
        var bytes = new byte[rgba.Length * 4];
        Buffer.BlockCopy(rgba, 0, bytes, 0, bytes.Length);
        var output = Image.CreateFromData(W, H * RadianceLevels, false, Image.Format.Rgbaf, bytes);
        if (radianceTexture == null) radianceTexture = ImageTexture.CreateFromImage(output); else radianceTexture.Update(output);
        if (System.Environment.GetEnvironmentVariable("MARVIN_SCN_TIMING") != null) GD.Print($"radiance prefilter {watch.Elapsed.TotalMilliseconds:0} ms");
        return radianceTexture;
    }

    /// <summary>Sky resource for specular reflections (and image backgrounds).</summary>
    internal Sky SkyFor(object contents, double energy)
    {
        if (sky == null) { skyMaterial = new PanoramaSkyMaterial(); sky = new Sky { SkyMaterial = skyMaterial, RadianceSize = Sky.RadianceSizeEnum.Size128 }; }
        if (!ReferenceEquals(contents, skySource))
        {
            skySource = contents;
            skyMaterial.Panorama = contents switch { NSImage n => n.GodotTexture, string p => NSImage.contentsOf(p)?.GodotTexture, _ => null };
        }
        skyMaterial.EnergyMultiplier = (float)energy;
        return sky;
    }
}
