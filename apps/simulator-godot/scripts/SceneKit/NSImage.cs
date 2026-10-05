using System;
using System.Collections.Generic;
using Godot;

namespace Marvin.SceneKit;

public enum NSBitmapImageFileType { tiff, bmp, gif, jpeg, png, jpeg2000 }
[Flags]
public enum NSBitmapFormat { alphaFirst = 1, alphaNonpremultiplied = 2, floatingPointSamples = 4 }

/// <summary>
/// NSBitmapImageRep: an 8-bit (or 16/32-bit float) raster whose bitmapData the
/// game fills directly. Row 0 is the TOP row (AppKit bitmap order), which is
/// also texture v = 0 in both SceneKit and Godot.
/// </summary>
public sealed class NSBitmapImageRep
{
    public readonly int pixelsWide, pixelsHigh, bitsPerSample, samplesPerPixel, bytesPerRow, bitsPerPixel;
    public readonly bool hasAlpha, isPlanar;
    public readonly NSColorSpaceName colorSpaceName;
    public NSBitmapFormat bitmapFormat;
    /// <summary>bitmapData: the raw samples (UnsafeMutablePointer&lt;UInt8&gt; in Swift).</summary>
    public readonly byte[] bitmapData;
    internal int version;

    public NSBitmapImageRep(byte[] bitmapDataPlanes, int pixelsWide, int pixelsHigh, int bitsPerSample, int samplesPerPixel,
                            bool hasAlpha, bool isPlanar, NSColorSpaceName colorSpaceName, int bytesPerRow, int bitsPerPixel,
                            NSBitmapFormat bitmapFormat = 0)
    {
        this.pixelsWide = pixelsWide; this.pixelsHigh = pixelsHigh; this.bitsPerSample = bitsPerSample;
        this.samplesPerPixel = samplesPerPixel; this.hasAlpha = hasAlpha; this.isPlanar = isPlanar;
        this.colorSpaceName = colorSpaceName; this.bitmapFormat = bitmapFormat;
        this.bitsPerPixel = bitsPerPixel == 0 ? bitsPerSample * samplesPerPixel : bitsPerPixel;
        this.bytesPerRow = bytesPerRow == 0 ? pixelsWide * this.bitsPerPixel / 8 : bytesPerRow;
        bitmapData = bitmapDataPlanes ?? new byte[this.bytesPerRow * pixelsHigh];
    }
    /// <summary>NSBitmapImageRep(data:) - decodes PNG/JPEG/WebP bytes (tiffRepresentation returns PNG bytes).</summary>
    public static NSBitmapImageRep data(byte[] bytes)
    {
        var image = NSImage.Decode(bytes);
        return image == null ? null : FromGodot(image);
    }
    internal static NSBitmapImageRep FromGodot(Image image)
    {
        var img = (Image)image.Duplicate();
        if (img.IsCompressed()) img.Decompress();
        if (img.GetFormat() != Image.Format.Rgba8) img.Convert(Image.Format.Rgba8);
        int w = img.GetWidth(), h = img.GetHeight();
        var rep = new NSBitmapImageRep(img.GetData(), w, h, 8, 4, true, false, NSColorSpaceName.deviceRGB, w * 4, 32, NSBitmapFormat.alphaNonpremultiplied);
        return rep;
    }
    public CGSize size => new(pixelsWide, pixelsHigh);
    public NSColorSpace colorSpace => colorSpaceName is NSColorSpaceName.calibratedRGB ? NSColorSpace.genericRGB : NSColorSpace.sRGB;
    /// <summary>Pixel colour (sRGB components of the stored, un-premultiplied samples). y = 0 is the top row.</summary>
    public NSColor colorAt(int x, int y)
    {
        if (x < 0 || y < 0 || x >= pixelsWide || y >= pixelsHigh) return null;
        var (r, g, b, a) = Sample(x, y);
        if (hasAlpha && (bitmapFormat & NSBitmapFormat.alphaNonpremultiplied) == 0 && a > 0) { r /= a; g /= a; b /= a; }
        return NSColor.srgbRed(r, g, b, a);
    }
    public void setColor(NSColor color, int atX, int y)
    {
        var c = color.usingColorSpace(NSColorSpace.sRGB);
        int i = y * bytesPerRow + atX * bitsPerPixel / 8;
        double a = color.alphaComponent;
        bool premultiplied = hasAlpha && (bitmapFormat & NSBitmapFormat.alphaNonpremultiplied) == 0;
        double k = premultiplied ? a : 1;
        if (bitsPerSample == 8)
        {
            bitmapData[i] = Byte(c.redComponent * k);
            if (samplesPerPixel > 1) bitmapData[i + 1] = Byte(c.greenComponent * k);
            if (samplesPerPixel > 2) bitmapData[i + 2] = Byte(c.blueComponent * k);
            if (samplesPerPixel > 3) bitmapData[i + 3] = Byte(a);
        }
        version++;
    }
    private static byte Byte(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
    internal (double r, double g, double b, double a) Sample(int x, int y)
    {
        int i = y * bytesPerRow + x * bitsPerPixel / 8;
        if (bitsPerSample == 8)
        {
            double r = bitmapData[i] / 255.0;
            double g = samplesPerPixel > 2 ? bitmapData[i + 1] / 255.0 : r;
            double b = samplesPerPixel > 2 ? bitmapData[i + 2] / 255.0 : r;
            double a = hasAlpha ? bitmapData[i + samplesPerPixel - 1] / 255.0 : 1;
            return (r, g, b, a);
        }
        if (bitsPerSample == 32)
        {
            float F(int k) => BitConverter.ToSingle(bitmapData, i + k * 4);
            double r = F(0), g = samplesPerPixel > 2 ? F(1) : r, b = samplesPerPixel > 2 ? F(2) : r;
            return (r, g, b, hasAlpha ? F(samplesPerPixel - 1) : 1);
        }
        return (0, 0, 0, 1);
    }
    /// <summary>NSBitmapImageRep.PropertyKey (the keys of <c>representation(using:properties:)</c>).</summary>
    public static class PropertyKey
    {
        /// <summary>.compressionFactor: JPEG quality 0...1.</summary>
        public const string compressionFactor = "NSImageCompressionFactor";
    }
    /// <summary>representation(using: .png/.jpeg, properties: [:]); JPEG honours .compressionFactor (default 0.9).</summary>
    public byte[] representation(NSBitmapImageFileType @using, Dictionary<string, object> properties = null)
    {
        var img = ToGodotImage(straightAlpha: true);
        var quality = properties != null && properties.TryGetValue(PropertyKey.compressionFactor, out var value) ? Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture) : 0.9f;
        return @using == NSBitmapImageFileType.jpeg ? img.SaveJpgToBuffer(quality) : img.SavePngToBuffer();
    }
    public NSImage cgImage => new NSImage(this);

    /// <summary>
    /// The bitmap as a Godot RGBA8 image. With straightAlpha, premultiplied samples
    /// (the AppKit default when hasAlpha) are divided by alpha, which is how SceneKit
    /// samples them (measured: rgb ≤ alpha gives sRGB(rgb/alpha) premultiplied in linear).
    /// Samples with rgb &gt; alpha are invalid premultiplied data; SceneKit's result is
    /// not reproducible and they are clamped to 1. PORT: see PORTING.md (dust texture).
    /// </summary>
    internal Image ToGodotImage(bool straightAlpha)
    {
        int w = pixelsWide, h = pixelsHigh;
        var bytes = new byte[w * h * 4];
        bool premultiplied = hasAlpha && (bitmapFormat & NSBitmapFormat.alphaNonpremultiplied) == 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b, a) = Sample(x, y);
                if (straightAlpha && premultiplied)
                {
                    if (a > 0) { r = Math.Min(1, r / a); g = Math.Min(1, g / a); b = Math.Min(1, b / a); }
                    else { r = g = b = 0; }
                }
                int o = (y * w + x) * 4;
                bytes[o] = Byte(r); bytes[o + 1] = Byte(g); bytes[o + 2] = Byte(b); bytes[o + 3] = Byte(a);
            }
        return Image.CreateFromData(w, h, false, Image.Format.Rgba8, bytes);
    }
}

/// <summary>
/// NSImage / CGImage. Either a set of bitmap representations (procedural textures)
/// or a file loaded from res:// (imported texture) or the filesystem.
/// </summary>
public sealed class NSImage
{
    private readonly List<NSBitmapImageRep> reps = new();
    private Texture2D loaded;
    private Image loadedImage;
    private ImageTexture texture;
    private int textureVersion = -1;
    internal string path;
    public CGSize size;

    public NSImage(CGSize size) { this.size = size; }
    internal NSImage(NSBitmapImageRep rep) { size = rep.size; reps.Add(rep); }
    private NSImage(Texture2D texture, string path) { loaded = texture; this.path = path; size = new CGSize(texture.GetWidth(), texture.GetHeight()); }
    private NSImage(Image image, string path) { loadedImage = image; this.path = path; size = new CGSize(image.GetWidth(), image.GetHeight()); }

    /// <summary>res:// textures whose loading was started on Godot's loader threads (Preload) and not yet taken.</summary>
    private static readonly HashSet<string> threadedLoads = new();
    /// <summary>
    /// Starts loading an imported texture on Godot's loader threads (ResourceLoader.LoadThreadedRequest), so the first
    /// material that binds it (contentsOf, on the main thread) only takes the finished resource. Material properties
    /// whose contents are res:// paths call this when the contents are set: the town's scanned 2048² textures are
    /// assigned while the race world is built on a background queue and cost 30-140 ms each to decode, which froze the
    /// loading screen when the world was first shown. The resource is the one ResourceLoader.Load returns.
    /// </summary>
    internal static void Preload(string path)
    {
        if (path == null || !path.StartsWith("res://")) return;
        lock (threadedLoads) { if (!threadedLoads.Add(path)) return; }
        if (!ResourceLoader.Exists(path) || ResourceLoader.LoadThreadedRequest(path, "Texture2D") != Error.Ok)
            lock (threadedLoads) threadedLoads.Remove(path);
    }
    /// <summary>NSImage(contentsOf:) / NSImage(contentsOfFile:). res:// paths load the imported texture. Returns null when missing.</summary>
    public static NSImage contentsOf(string path)
    {
        if (path == null) return null;
        if (path.StartsWith("res://") && ResourceLoader.Exists(path))
        {
            bool requested;
            lock (threadedLoads) requested = threadedLoads.Remove(path);
            var tex = (requested ? ResourceLoader.LoadThreadedGet(path) as Texture2D : null) ?? ResourceLoader.Load<Texture2D>(path);
            // Grayscale images (L8/LA8) keep a single colour channel in Godot, and Godot's source_color samplers do not
            // sRGB-decode them (measured: an R2-D2 panel line of 148 rendered as 200 by a constant material, 148 in
            // SceneKit, which decodes gray images like RGB ones). Give them RGB channels so every sampler sees SceneKit's values.
            if (tex != null && tex.GetImage() is Image gray && gray.GetFormat() is Image.Format.L8 or Image.Format.La8)
            {
                gray.Convert(gray.GetFormat() == Image.Format.L8 ? Image.Format.Rgb8 : Image.Format.Rgba8);
                return new NSImage(gray, path);
            }
            if (tex != null) return new NSImage(tex, path);
        }
        var file = path.StartsWith("res://") || path.StartsWith("user://") ? ProjectSettings.GlobalizePath(path) : path;
        if (!System.IO.File.Exists(file)) return null;
        var image = Image.LoadFromFile(file);
        return image == null || image.IsEmpty() ? null : new NSImage(image, path);
    }
    public static NSImage contentsOfFile(string path) => contentsOf(path);
    /// <summary>
    /// NSImage(systemSymbolName:accessibilityDescription:). PORT: SF Symbols are Apple system artwork and are not
    /// shipped; the image keeps the symbol's name and the places that show symbols (the toolbar) draw a vector stand-in.
    /// </summary>
    public static NSImage systemSymbolName(string name, string accessibilityDescription) =>
        new(new CGSize(0, 0)) { symbolName = name, accessibilityDescription = accessibilityDescription };
    /// <summary>The SF Symbol name of a systemSymbolName image (null otherwise).</summary>
    public string symbolName { get; private set; }
    public string accessibilityDescription;
    /// <summary>NSImage(data:).</summary>
    public static NSImage data(byte[] bytes) { var img = Decode(bytes); return img == null ? null : new NSImage(img, null); }
    /// <summary>
    /// A rendered snapshot's pixels. Snapshots used to be returned as NSImage.data(image.SavePngToBuffer()); PNG keeps the
    /// 8-bit formats unchanged (RGBA8 is written as RGBA and decoded to RGBA8), so for those the image itself is the same
    /// NSImage without the encode and decode (0.3 s of the loading screen's freeze for revealDirtTrack's discarded snapshot).
    /// Other formats still take the PNG path, which converts them.
    /// </summary>
    internal static NSImage Snapshot(Image image)
    {
        if (image == null || image.IsEmpty()) return null;
        if (image.GetFormat() is Image.Format.Rgba8 or Image.Format.Rgb8 && !image.HasMipmaps()) return new NSImage(image, null);
        return data(image.SavePngToBuffer());
    }

    public void addRepresentation(NSBitmapImageRep rep) { reps.Add(rep); }

    // ---- drawing (lockFocus / draw(in:)); 1 pixel per point.
    private NSGraphicsContext focusContext, previousContext;
    /// <summary>lockFocus(): subsequent NSBezierPath/NSRect drawing goes into this image (origin bottom-left).</summary>
    public void lockFocus() => LockFocus(false);
    /// <summary>lockFocusFlipped(_:): like lockFocus with y growing downwards when flipped.</summary>
    public void lockFocusFlipped(bool flipped) => LockFocus(flipped);
    private void LockFocus(bool flipped)
    {
        int w = Math.Max(1, (int)Math.Ceiling(size.width)), h = Math.Max(1, (int)Math.Ceiling(size.height));
        var rep = reps.Count > 0 && reps[0].pixelsWide == w && reps[0].pixelsHigh == h && reps[0].bitsPerSample == 8 && reps[0].samplesPerPixel == 4 ? reps[0] : null;
        if (rep == null)
        {
            rep = new NSBitmapImageRep(null, w, h, 8, 4, true, false, NSColorSpaceName.deviceRGB, w * 4, 32);
            if (GodotImage is Image existing) { var old = new NSGraphicsContext(rep); old.DrawImage(this, new CGRect(0, 0, w, h), 1); }
            reps.Clear(); reps.Add(rep); loaded = null; loadedImage = null;
        }
        previousContext = NSGraphicsContext.current;
        focusContext = new NSGraphicsContext(rep, flipped);
        NSGraphicsContext.current = focusContext;
    }
    public void unlockFocus()
    {
        if (NSGraphicsContext.current == focusContext) NSGraphicsContext.current = previousContext;
        focusContext = null;
    }
    /// <summary>draw(in:) into the current NSGraphicsContext (source-over).</summary>
    public void draw(CGRect @in) => NSGraphicsContext.current?.DrawImage(this, @in, 1);
    /// <summary>draw(in:from:operation:fraction:respectFlipped:hints:) - source rect, operation and hints are ignored.</summary>
    public void draw(CGRect @in, CGRect from, object operation, double fraction, bool respectFlipped = true, object hints = null) =>
        NSGraphicsContext.current?.DrawImage(this, @in, fraction);
    public IReadOnlyList<NSBitmapImageRep> representations => reps;
    public bool isValid => reps.Count > 0 || loaded != null || loadedImage != null;
    /// <summary>tiffRepresentation: PNG-encoded bytes in this facade (decoded again by NSBitmapImageRep.data(...)).</summary>
    public byte[] tiffRepresentation => GodotImage?.SavePngToBuffer();
    public NSImage cgImage(ref CGRect proposedRect) => this;
    public NSImage cgImage() => this;

    internal static Image Decode(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 4) return null;
        var image = new Image();
        Error err;
        if (bytes[0] == 0x89 && bytes[1] == 0x50) err = image.LoadPngFromBuffer(bytes);
        else if (bytes[0] == 0xFF && bytes[1] == 0xD8) err = image.LoadJpgFromBuffer(bytes);
        else if (bytes[0] == 'R' && bytes[1] == 'I') err = image.LoadWebpFromBuffer(bytes);
        else err = image.LoadPngFromBuffer(bytes);
        return err == Error.Ok ? image : null;
    }

    /// <summary>Straight-alpha RGBA8 (or the loaded format) Godot image of the first representation.</summary>
    public Image GodotImage
    {
        get
        {
            if (reps.Count > 0) return reps[0].ToGodotImage(straightAlpha: true);
            if (loadedImage != null) return loadedImage;
            if (loaded != null) { loadedImage = loaded.GetImage(); if (loadedImage.IsCompressed()) loadedImage.Decompress(); return loadedImage; }
            return null;
        }
    }
    /// <summary>True if any texel has alpha below 1 (SceneKit then blends the material).</summary>
    internal bool HasTranslucency
    {
        get
        {
            if (translucencyVersion == Version) return translucency;
            translucencyVersion = Version;
            translucency = false;
            if (reps.Count > 0)
            {
                var rep = reps[0];
                if (rep.hasAlpha && rep.bitsPerSample == 8)
                    for (int i = rep.samplesPerPixel - 1; i < rep.bitmapData.Length; i += rep.samplesPerPixel)
                        if (rep.bitmapData[i] < 255) { translucency = true; break; }
            }
            else if (GodotImage is Image img && img.DetectAlpha() != Image.AlphaMode.None) translucency = true;
            return translucency;
        }
    }
    private bool translucency;
    private int translucencyVersion = -2;
    private int Version { get { int v = 0; foreach (var r in reps) v += r.version + 1; return v; } }

    /// <summary>The image GodotTexture uploads: a copy of GodotImage with mipmaps.</summary>
    private Image MipmappedImage()
    {
        var img = GodotImage;
        if (img == null) return null;
        img = (Image)img.Duplicate();
        if (!img.HasMipmaps()) img.GenerateMipmaps();
        return img;
    }
    private volatile Image prepared;
    private int preparedVersion = -1;
    private Image TakePrepared(int version)
    {
        var ready = prepared;
        if (ready == null) return null;
        prepared = null;
        return preparedVersion == version ? ready : null;
    }
    /// <summary>The texture would be (re)built at its next use (a procedural image not uploaded at its current version).</summary>
    internal bool NeedsTexture => loaded == null && !(texture != null && textureVersion == Version) && !(prepared != null && preparedVersion == Version);
    /// <summary>
    /// Any thread: computes the mipmapped image of a procedural texture (the per-pixel conversion and the mipmaps are most
    /// of a texture's cost), so the main thread only uploads it when a material first binds it. SceneKitRuntime prepares
    /// the textures of a large flush (a scene shown for the first time) on all cores; the image is the one GodotTexture
    /// would compute itself. Do not draw into the image meanwhile.
    /// </summary>
    internal void PrepareTexture()
    {
        if (!NeedsTexture) return;
        int v = Version;
        var img = MipmappedImage();
        if (img == null) return;
        preparedVersion = v;
        prepared = img;
    }

    /// <summary>Mipmapped texture for sampling. Built once per bitmap version.</summary>
    internal Texture2D GodotTexture
    {
        get
        {
            if (loaded != null) return loaded;
            int v = Version;
            if (texture != null && textureVersion == v) return texture;
            var img = TakePrepared(v) ?? MipmappedImage();
            if (img == null) return null;
            texture = ImageTexture.CreateFromImage(img);
            FrameProfile.ImageTexturesCreated++; FrameProfile.TotalImageTexturesCreated++;
            textureVersion = v;
            return texture;
        }
    }
}
