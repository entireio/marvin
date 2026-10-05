using System;
using System.Runtime.InteropServices;
using Godot;

namespace Marvin.SceneKit;

// The small part of Metal the game uses directly: shared vertex buffers for
// SCNGeometrySource(buffer:...) and float textures passed to shader modifiers.
// Everything is CPU-side; textures become Godot ImageTextures.

[Flags]
public enum MTLResourceOptions { storageModeShared = 0, cpuCacheModeDefaultCache = 0, storageModeManaged = 16, storageModePrivate = 32, cpuCacheModeWriteCombined = 1 }
public enum MTLStorageMode { shared, managed, @private, memoryless }
[Flags]
public enum MTLTextureUsage { unknown = 0, shaderRead = 1, shaderWrite = 2, renderTarget = 4, pixelFormatView = 16 }
public enum MTLGPUFamily { apple1 = 1001, apple2, apple3, apple4, apple5, apple6, apple7, apple8, apple9, mac1 = 2001, mac2, common1 = 3001, common2, common3, metal3 = 5001 }
public enum MTLTextureType { type1D, type1DArray, type2D, type2DArray, type2DMultisample, typeCube, typeCubeArray, type3D }
public enum MTLPixelFormat { invalid, r8Unorm, rg8Unorm, rgba8Unorm, rgba8Unorm_srgb, bgra8Unorm, bgra8Unorm_srgb, r16Float, rg16Float, rgba16Float, r32Float, rg32Float, rgba32Float, depth32Float }

public struct MTLOrigin { public int x, y, z; public MTLOrigin(int x, int y, int z) { this.x = x; this.y = y; this.z = z; } }
public struct MTLSize { public int width, height, depth; public MTLSize(int width, int height, int depth) { this.width = width; this.height = height; this.depth = depth; } }
public struct MTLRegion { public MTLOrigin origin; public MTLSize size; public MTLRegion(MTLOrigin origin, MTLSize size) { this.origin = origin; this.size = size; } }
public struct MTLClearColor { public double red, green, blue, alpha; public MTLClearColor(double r, double g, double b, double a) { red = r; green = g; blue = b; alpha = a; } }

public sealed class MTLDevice
{
    internal static readonly MTLDevice Shared = new();
    /// <summary>name: the adapter name; Godot's Metal driver appends the GPU family ("Apple M2 (Apple8)"), MTLDevice's
    /// name does not ("Apple M2"), so the suffix is dropped.</summary>
    public string name
    {
        get
        {
            var adapter = RenderingServer.GetVideoAdapterName();
            var family = System.Text.RegularExpressions.Regex.Match(adapter, @"^(.*) \(Apple\d+\)$");
            return family.Success && RenderingServer.GetCurrentRenderingDriverName() == "metal" ? family.Groups[1].Value : adapter;
        }
    }
    public MTLBuffer makeBuffer(int length, MTLResourceOptions options = MTLResourceOptions.storageModeShared) => new(length);
    public MTLTexture makeTexture(MTLTextureDescriptor descriptor) => new(descriptor);
    public bool supportsFamily(MTLGPUFamily family) => true;
}

/// <summary>A CPU byte buffer standing in for a shared MTLBuffer.</summary>
public sealed class MTLBuffer
{
    internal readonly byte[] bytes;
    internal int version;
    internal MTLBuffer(int length) { bytes = new byte[length]; }
    public int length => bytes.Length;
    /// <summary>contents() as raw bytes. Writes are visible to geometry built after them.</summary>
    public byte[] contents() { version++; return bytes; }
    /// <summary>contents().bindMemory(to: T.self, capacity:) equivalent.</summary>
    public Span<T> contents<T>() where T : unmanaged { version++; return MemoryMarshal.Cast<byte, T>(bytes.AsSpan()); }
    public void didModifyRange(System.Range range) { version++; }
}

public sealed class MTLTextureDescriptor
{
    public MTLPixelFormat pixelFormat;
    public int width, height, depth = 1, mipmapLevelCount = 1, sampleCount = 1, arrayLength = 1;
    public MTLTextureType textureType = MTLTextureType.type2D;
    public MTLStorageMode storageMode = MTLStorageMode.managed;
    public MTLTextureUsage usage = MTLTextureUsage.shaderRead;
    public static MTLTextureDescriptor texture2DDescriptor(MTLPixelFormat pixelFormat, int width, int height, bool mipmapped) =>
        new() { pixelFormat = pixelFormat, width = width, height = height, mipmapLevelCount = mipmapped ? 1 + (int)Math.Floor(Math.Log2(Math.Max(width, height))) : 1 };
}

/// <summary>A texture whose texels are uploaded with replace(region:...). Backed by a Godot Image/ImageTexture.</summary>
public sealed class MTLTexture
{
    public readonly int width, height;
    public readonly MTLPixelFormat pixelFormat;
    public readonly MTLTextureType textureType = MTLTextureType.type2D;
    private readonly byte[] data;
    private readonly int bytesPerPixel;
    private readonly Image.Format format;
    private ImageTexture texture;
    private bool dirty = true;
    internal MTLTexture(MTLTextureDescriptor d)
    {
        width = d.width; height = d.height; pixelFormat = d.pixelFormat;
        (format, bytesPerPixel) = d.pixelFormat switch
        {
            MTLPixelFormat.r32Float => (Image.Format.Rf, 4),
            MTLPixelFormat.rg32Float => (Image.Format.Rgf, 8),
            MTLPixelFormat.rgba32Float => (Image.Format.Rgbaf, 16),
            MTLPixelFormat.r16Float => (Image.Format.Rh, 2),
            MTLPixelFormat.rg16Float => (Image.Format.Rgh, 4),
            MTLPixelFormat.rgba16Float => (Image.Format.Rgbah, 8),
            MTLPixelFormat.r8Unorm => (Image.Format.R8, 1),
            MTLPixelFormat.rg8Unorm => (Image.Format.Rg8, 2),
            _ => (Image.Format.Rgba8, 4),
        };
        data = new byte[width * height * bytesPerPixel];
    }
    public void replace(MTLRegion region, int mipmapLevel, ReadOnlySpan<byte> withBytes, int bytesPerRow)
    {
        if (mipmapLevel != 0) return;
        int rowBytes = region.size.width * bytesPerPixel;
        bool bgra = pixelFormat is MTLPixelFormat.bgra8Unorm or MTLPixelFormat.bgra8Unorm_srgb;
        for (int y = 0; y < region.size.height; y++)
        {
            var src = withBytes.Slice(y * bytesPerRow, rowBytes);
            var dst = data.AsSpan(((region.origin.y + y) * width + region.origin.x) * bytesPerPixel, rowBytes);
            src.CopyTo(dst);
            if (bgra) for (int i = 0; i < rowBytes; i += 4) (dst[i], dst[i + 2]) = (dst[i + 2], dst[i]);
        }
        dirty = true;
        if (texture != null) SceneKitRuntime.TextureDirty(this); // already bound: refresh in place before the next frame
    }
    public void replace(MTLRegion region, int mipmapLevel, float[] withBytes, int bytesPerRow) =>
        replace(region, mipmapLevel, MemoryMarshal.AsBytes(withBytes.AsSpan()), bytesPerRow);
    public void replace(MTLRegion region, int mipmapLevel, byte[] withBytes, int bytesPerRow) =>
        replace(region, mipmapLevel, withBytes.AsSpan(), bytesPerRow);
    public void getBytes(Span<byte> pixelBytes, int bytesPerRow, MTLRegion fromRegion, int mipmapLevel)
    {
        int rowBytes = fromRegion.size.width * bytesPerPixel;
        for (int y = 0; y < fromRegion.size.height; y++)
            data.AsSpan(((fromRegion.origin.y + y) * width + fromRegion.origin.x) * bytesPerPixel, rowBytes).CopyTo(pixelBytes.Slice(y * bytesPerRow, rowBytes));
    }
    /// <summary>Godot texture with the current texels (updated in place after replace()).</summary>
    internal void Refresh() { if (dirty) _ = GodotTexture; }
    internal Texture2D GodotTexture
    {
        get
        {
            if (texture == null || dirty)
            {
                var image = Image.CreateFromData(width, height, false, format, data);
                if (texture == null) texture = ImageTexture.CreateFromImage(image); else texture.Update(image);
                dirty = false;
            }
            return texture;
        }
    }
}
