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
