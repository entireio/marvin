using System;
using System.Collections.Generic;
using Godot;

namespace Marvin.SceneKit;

/// <summary>
/// SceneKit's screen-space ambient occlusion, ported from its own Metal kernels (scn_ssao_downsample, scn_ssao_compute,
/// scn_ssao_blur_x/y and scn_ssao_upsampling in SceneKit.framework's default.metallib, macOS 27, disassembled from their
/// AIR bitcode) and the uniforms SceneKit binds for them (logged with a Metal API probe). It is McGuire et al.'s Scalable
/// Ambient Obscurance:
/// - a full-resolution view-space depth/normal texture with box-filtered mips (SceneKit renders its own pass; here it is
///   built from Godot's depth prepass: normal from the normal-roughness buffer, or from depth for materials that bend their
///   normal, because SceneKit's pass ignores normal maps and .surface modifiers; materials with a .geometry modifier and
///   transparent ones are left out, as in SceneKit, see ShaderComposer's ROUGHNESS tag);
/// - SCNCamera.screenSpaceAmbientOcclusionDownSample 2 and SampleCount 9 (SceneKit's defaults; the game sets neither): a
///   checkerboard of the nearest / farthest of each 2x2 block, 9 taps on a 7-turn spiral rotated per pixel, disk radius
///   1000 x radius / depth full-resolution pixels (independent of the field of view and the drawable size: the uniform
///   SceneKit binds is 1000 x radius), per tap max((v.n - bias) / (v.v + 1e-4), 0) (the radius falloff term cancels
///   against SceneKit's intensity / radius^6 normalisation), A = max(0, 1 - 2 x intensity x sum / 9);
/// - two 13-tap separable Gaussian blurs (stride 2) that skip taps beyond screenSpaceAmbientOcclusionDepthThreshold in depth
///   or NormalThreshold in either view normal component, then an edge-aware upsampling.
/// The result is written to the global texture scn_ssao at the view's pixel positions; the composer multiplies the ambient
/// and image-based light of physically based materials by it (SceneKit applies SSAO to no other lighting model, measured).
/// A view without SSAO clears the texture to 1. Validated against SceneKit renders of a wall, a sphere and the race
/// probe scene (tools: Calibration.cs CAL_EXP=ssao; PORTING.md "SSAO").
/// </summary>
[GlobalClass]
public partial class SCNSsaoEffect : CompositorEffect
{
    internal float intensity, radius = 5, bias = 0.03f, depthThreshold = 0.2f, normalThreshold = 0.3f;
    private const int DownSample = 2, SampleCount = 9;

    public SCNSsaoEffect()
    {
        EffectCallbackType = EffectCallbackTypeEnum.PreOpaque;
        NeedsNormalRoughness = true;
        AccessResolvedDepth = true;
    }

    internal void Configure(SCNCamera camera)
    {
        intensity = (float)camera.screenSpaceAmbientOcclusionIntensity;
        radius = (float)camera.screenSpaceAmbientOcclusionRadius;
        bias = (float)camera.screenSpaceAmbientOcclusionBias;
        depthThreshold = (float)camera.screenSpaceAmbientOcclusionDepthThreshold;
        normalThreshold = (float)camera.screenSpaceAmbientOcclusionNormalThreshold;
    }

    // ---- per-effect (per view) textures
    private Vector2I size;
    private Rid csz, index, aoA, aoB;
    private readonly List<Rid> cszMips = new();
    private int mipCount;

    public override void _RenderCallback(int effectCallbackType, RenderData renderData)
    {
        if (effectCallbackType != (int)EffectCallbackTypeEnum.PreOpaque) return;
        var rd = RenderingServer.GetRenderingDevice();
        if (rd == null || renderData.GetRenderSceneBuffers() is not RenderSceneBuffersRD rb) return;
        var full = rb.GetInternalSize();
        if (full.X <= 0 || full.Y <= 0) return;
        SCNSsao.EnsureShared(rd, full);
                if (intensity <= 0 || radius <= 0)
        {
            rd.TextureClear(SCNSsao.Output, new Color(1, 1, 1, 1), 0, 1, 0, 1);
            return;
        }
        Allocate(rd, full);
        var sceneData = renderData.GetRenderSceneData();
        var proj = sceneData.GetCamProjection();
        // With MSAA, GetDepthLayer returns the multisampled buffer; the depth Godot resolved after the prepass is this one.
        Rid depth = rb.GetMsaa3D() != RenderingServer.ViewportMsaa.Disabled && rb.HasTexture("render_buffers", "depth") ? rb.GetTexture("render_buffers", "depth") : rb.GetDepthLayer(0);
        Rid normal = rb.GetTexture("forward_clustered", "normal_roughness");
        if (!depth.IsValid || !normal.IsValid) { rd.TextureClear(SCNSsao.Output, new Color(1, 1, 1, 1), 0, 1, 0, 1); return; }
        var half = new Vector2I(full.X / DownSample, full.Y / DownSample);
        var inv = proj.Inverse();
        bool ortho = proj.W.W == 1.0f;
        // SceneKit's projectionInfo for full-resolution pixel coordinates (row 0 at the top): view x = (P.x * px + P.z) * -z,
        // view y = (P.y * py + P.w) * -z (perspective); for an orthographic camera x = P.x * px + P.z (logged values:
        // 2 / (W P00), -2 / (H P11), -1 / P00, 1 / P11 for a symmetric frustum).
        // The projection Godot hands over is its corrected one (y flipped, z reversed to 0..1): undo the y flip here.
        float p00 = proj.X.X, p11 = -proj.Y.Y, p02 = proj.Z.X, p12 = -proj.Z.Y;
        Vector4 projInfo = ortho
            ? new Vector4(2f / (full.X * p00), -2f / (full.Y * p11), (-1f - proj.W.X) / p00, (1f + proj.W.Y) / p11)
            : new Vector4(2f / (full.X * p00), -2f / (full.Y * p11), (p02 - 1f) / p00, (1f + p12) / p11);

        // 1. View-space normal and depth (SceneKit's depth/normal texture), mip 0.
        {
            var pc = new PushConstants();
            pc.Add(inv.Z.X, inv.Z.Y, inv.Z.Z, inv.Z.W); // inverse projection columns 2 and 3 (z and w of the view position)
            pc.Add(inv.W.X, inv.W.Y, inv.W.Z, inv.W.W);
            pc.Add(inv.X.Z, inv.Y.Z, inv.X.W, inv.Y.W); // x/y contributions to view z and w
            pc.Add(full.X, full.Y, projInfo.X, projInfo.Y);
            pc.Add(projInfo.Z, projInfo.W, ortho ? 1 : 0, 0);
            var set = UniformSetCacheRD.GetCache(SCNSsao.CszShader, 0u, new Godot.Collections.Array<RDUniform>
            {
                SCNSsao.SampledUniform(0, SCNSsao.NearestSampler, depth),
                SCNSsao.SampledUniform(1, SCNSsao.NearestSampler, normal),
                SCNSsao.ImageUniform(2, cszMips[0]),
            });
            SCNSsao.Dispatch(rd, SCNSsao.CszPipeline, set, pc, full);
        }
        // 2. Box-filtered mips (only their depth is read).
        for (int m = 1; m < mipCount; m++)
        {
            var src = Mip(full, m - 1); var dst = Mip(full, m);
            var pc = new PushConstants(); pc.Add(src.X, src.Y, dst.X, dst.Y);
            var set = UniformSetCacheRD.GetCache(SCNSsao.MipShader, 0, new Godot.Collections.Array<RDUniform>
            {
                SCNSsao.ImageUniform(0, cszMips[m - 1]), SCNSsao.ImageUniform(1, cszMips[m]),
            });
            SCNSsao.Dispatch(rd, SCNSsao.MipPipeline, set, pc, dst);
        }
        // 3. Checkerboard nearest / farthest sample of each 2x2 block (scn_ssao_downsample).
        {
            var pc = new PushConstants(); pc.Add(half.X, half.Y, full.X, full.Y);
            var set = UniformSetCacheRD.GetCache(SCNSsao.IndexShader, 0, new Godot.Collections.Array<RDUniform>
            {
                SCNSsao.ImageUniform(0, cszMips[0]), SCNSsao.ImageUniform(1, index),
            });
            SCNSsao.Dispatch(rd, SCNSsao.IndexPipeline, set, pc, half);
        }
        // 4. Scalable ambient obscurance at half resolution (scn_ssao_compute).
        {
            float r = 1000f * radius, r2 = r * r;
            var pc = new PushConstants();
            pc.Add(projInfo.X, projInfo.Y, projInfo.Z, projInfo.W);
            pc.Add(half.X, half.Y, full.X, full.Y);
            pc.Add(r, r2, bias, intensity / (r2 * r2 * r2));
            pc.Add(mipCount, ortho ? 1 : 0, 0, 0);
            var set = UniformSetCacheRD.GetCache(SCNSsao.SaoShader, 0, new Godot.Collections.Array<RDUniform>
            {
                SCNSsao.SampledUniform(0, SCNSsao.NearestSampler, csz), SCNSsao.ImageUniform(1, index), SCNSsao.ImageUniform(2, aoA),
            });
            SCNSsao.Dispatch(rd, SCNSsao.SaoPipeline, set, pc, half);
        }
        // 5. Bilateral Gaussian blur, x then y (scn_ssao_blur_x / _y).
        foreach (var (src, dst, dx, dy) in new[] { (aoA, aoB, 1, 0), (aoB, aoA, 0, 1) })
        {
            var pc = new PushConstants(); pc.Add(half.X, half.Y, dx, dy); pc.Add(depthThreshold, normalThreshold, 0, 0);
            var set = UniformSetCacheRD.GetCache(SCNSsao.BlurShader, 0, new Godot.Collections.Array<RDUniform>
            {
                SCNSsao.ImageUniform(0, src), SCNSsao.ImageUniform(1, dst),
            });
            SCNSsao.Dispatch(rd, SCNSsao.BlurPipeline, set, pc, half);
        }
        // 6. Edge-aware upsampling into the global scn_ssao texture (scn_ssao_upsampling).
        {
            var pc = new PushConstants(); pc.Add(full.X, full.Y, half.X, half.Y); pc.Add(depthThreshold, 0, 0, 0);
            var set = UniformSetCacheRD.GetCache(SCNSsao.UpShader, 0, new Godot.Collections.Array<RDUniform>
            {
                SCNSsao.SampledUniform(0, SCNSsao.LinearSampler, aoA), SCNSsao.ImageUniform(1, cszMips[0]), SCNSsao.ImageUniform(2, SCNSsao.Output),
            });
            SCNSsao.Dispatch(rd, SCNSsao.UpPipeline, set, pc, full);
        }
    }

    private static Vector2I Mip(Vector2I s, int m) => new(Math.Max(1, s.X >> m), Math.Max(1, s.Y >> m));

    private void Allocate(RenderingDevice rd, Vector2I full)
    {
        if (full == size && csz.IsValid) return;
        Free(rd);
        size = full;
        mipCount = 1;
        while ((full.X >> mipCount) >= 1 && (full.Y >> mipCount) >= 1) mipCount++;
        var usage = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.CanCopyToBit | RenderingDevice.TextureUsageBits.CanCopyFromBit;
        csz = rd.TextureCreate(new RDTextureFormat { Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat, Width = (uint)full.X, Height = (uint)full.Y, Mipmaps = (uint)mipCount, UsageBits = usage }, new RDTextureView());
        for (int m = 0; m < mipCount; m++) cszMips.Add(rd.TextureCreateSharedFromSlice(new RDTextureView(), csz, 0, (uint)m, 1, RenderingDevice.TextureSliceType.Slice2D));
        var half = new Vector2I(Math.Max(1, full.X / DownSample), Math.Max(1, full.Y / DownSample));
        index = rd.TextureCreate(new RDTextureFormat { Format = RenderingDevice.DataFormat.R32Uint, Width = (uint)half.X, Height = (uint)half.Y, UsageBits = usage }, new RDTextureView());
        aoA = rd.TextureCreate(new RDTextureFormat { Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat, Width = (uint)half.X, Height = (uint)half.Y, UsageBits = usage }, new RDTextureView());
        aoB = rd.TextureCreate(new RDTextureFormat { Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat, Width = (uint)half.X, Height = (uint)half.Y, UsageBits = usage }, new RDTextureView());
    }

    private void Free(RenderingDevice rd)
    {
        foreach (var m in cszMips) if (m.IsValid) rd.FreeRid(m);
        cszMips.Clear();
        foreach (var t in new[] { csz, index, aoA, aoB }) if (t.IsValid) rd.FreeRid(t);
        csz = index = aoA = aoB = default;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationPredelete)
        {
            var rd = RenderingServer.GetRenderingDevice();
            if (rd != null) Free(rd);
        }
    }
}

/// <summary>Shared shaders, samplers and the global scn_ssao texture of SCNSsaoEffect.</summary>
internal static class SCNSsao
{
    internal static Rid CszShader, CszPipeline, MipShader, MipPipeline, IndexShader, IndexPipeline, SaoShader, SaoPipeline,
        BlurShader, BlurPipeline, UpShader, UpPipeline, NearestSampler, LinearSampler, Output;
    private static Vector2I outputSize;
    private static Texture2Drd outputTexture;

    /// <summary>Creates the shaders on first use and grows the global output texture to cover the view.</summary>
    internal static void EnsureShared(RenderingDevice rd, Vector2I viewSize)
    {
        if (!CszPipeline.IsValid)
        {
            (CszShader, CszPipeline) = Compile(rd, CszSource, "csz");
            (MipShader, MipPipeline) = Compile(rd, MipSource, "mip");
            (IndexShader, IndexPipeline) = Compile(rd, IndexSource, "index");
            (SaoShader, SaoPipeline) = Compile(rd, SaoSource, "sao");
            (BlurShader, BlurPipeline) = Compile(rd, BlurSource, "blur");
            (UpShader, UpPipeline) = Compile(rd, UpSource, "upsample");
            NearestSampler = rd.SamplerCreate(new RDSamplerState
            {
                MinFilter = RenderingDevice.SamplerFilter.Nearest, MagFilter = RenderingDevice.SamplerFilter.Nearest, MipFilter = RenderingDevice.SamplerFilter.Nearest,
                RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge, RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge,
            });
            LinearSampler = rd.SamplerCreate(new RDSamplerState
            {
                MinFilter = RenderingDevice.SamplerFilter.Linear, MagFilter = RenderingDevice.SamplerFilter.Linear, MipFilter = RenderingDevice.SamplerFilter.Nearest,
                RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge, RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge,
            });
        }
        if (Output.IsValid && viewSize.X <= outputSize.X && viewSize.Y <= outputSize.Y) return;
        var grown = new Vector2I(Math.Max(viewSize.X, Math.Max(outputSize.X, 1920)), Math.Max(viewSize.Y, Math.Max(outputSize.Y, 1200)));
        var old = Output;
        Output = rd.TextureCreate(new RDTextureFormat
        {
            Format = RenderingDevice.DataFormat.R16Sfloat, Width = (uint)grown.X, Height = (uint)grown.Y,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.CanCopyToBit | RenderingDevice.TextureUsageBits.CanCopyFromBit,
        }, new RDTextureView());
        rd.TextureClear(Output, new Color(1, 1, 1, 1), 0, 1, 0, 1);
        outputSize = grown;
        outputTexture ??= new Texture2Drd();
        outputTexture.TextureRdRid = Output;
        RenderingServer.GlobalShaderParameterSet("scn_ssao", outputTexture);
        if (old.IsValid) rd.FreeRid(old);
    }

    private static bool primed;
    /// <summary>Called when the first view is made, so materials always see a valid (all-ones) scn_ssao texture.</summary>
    internal static void Prime()
    {
        if (primed) return;
        primed = true;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            var rd = RenderingServer.GetRenderingDevice();
            if (rd != null) EnsureShared(rd, new Vector2I(1, 1));
        }));
    }

    private static (Rid, Rid) Compile(RenderingDevice rd, string code, string name)
    {
        var spirv = rd.ShaderCompileSpirVFromSource(new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = code });
        if (!string.IsNullOrEmpty(spirv.CompileErrorCompute)) GD.PushError($"SCNSsao {name}: {spirv.CompileErrorCompute}");
        var shader = rd.ShaderCreateFromSpirV(spirv, "SCNSsao " + name);
        return (shader, rd.ComputePipelineCreate(shader));
    }

    internal static RDUniform SampledUniform(int binding, Rid sampler, Rid texture)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = binding };
        u.AddId(sampler); u.AddId(texture); return u;
    }
    internal static RDUniform ImageUniform(int binding, Rid texture)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = binding };
        u.AddId(texture); return u;
    }
    internal static void Dispatch(RenderingDevice rd, Rid pipeline, Rid set, PushConstants pc, Vector2I size)
    {
        var bytes = pc.Bytes();
        long list = rd.ComputeListBegin();
        rd.ComputeListBindComputePipeline(list, pipeline);
        rd.ComputeListBindUniformSet(list, set, 0);
        rd.ComputeListSetPushConstant(list, bytes, (uint)bytes.Length);
        rd.ComputeListDispatch(list, (uint)((size.X + 7) / 8), (uint)((size.Y + 7) / 8), 1);
        rd.ComputeListEnd();
    }

    // ---- Shaders (GLSL 450 compute). Comments name the SceneKit IR they mirror.
    private const string Header = "#version 450\nlayout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;\n";

    // View-space normal (xyz) and view z (w, negative in front of the camera), like SceneKit's SSAO depth/normal texture.
    // ShaderComposer tags each opaque material in ROUGHNESS: 0 = normal from the buffer, 0.6 = normal from depth (normal map
    // or .surface normal), 1 = not in SceneKit's SSAO pass (.geometry modifier, transparent): stored as the far plane.
    // Godot stores roughness x 127/255 (1 - that for dynamic instances) and its roughness limiter only raises it.
    private const string CszSource = Header + @"
layout(set = 0, binding = 0) uniform sampler2D depth_buffer;
layout(set = 0, binding = 1) uniform sampler2D normal_buffer;
layout(rgba16f, set = 0, binding = 2) uniform restrict writeonly image2D csz;
layout(push_constant, std430) uniform Params { vec4 inv_z; vec4 inv_w; vec4 inv_xy; vec4 size_info; vec4 info2; } p;
float view_z(ivec2 px) {
    float d = texelFetch(depth_buffer, px, 0).r;
    vec2 ndc = vec2((float(px.x) + 0.5) / p.size_info.x * 2.0 - 1.0, 1.0 - (float(px.y) + 0.5) / p.size_info.y * 2.0);
    vec4 h = vec4(ndc.x, -ndc.y, d, 1.0); // RenderSceneData's projection is Godot's corrected one: z 0..1 reversed, y down
    float z = p.inv_xy.x * h.x + p.inv_xy.y * h.y + p.inv_z.z * h.z + p.inv_w.z;
    float w = p.inv_xy.z * h.x + p.inv_xy.w * h.y + p.inv_z.w * h.z + p.inv_w.w;
    return z / w;
}
vec3 view_pos(ivec2 px, float z) {
    vec2 xy = p.info2.z > 0.5 ? p.size_info.zw * vec2(px) + p.info2.xy : (p.size_info.zw * vec2(px) + p.info2.xy) * -z;
    return vec3(xy, z);
}
void main() {
    ivec2 px = ivec2(gl_GlobalInvocationID.xy);
    ivec2 sz = ivec2(p.size_info.xy);
    if (px.x >= sz.x || px.y >= sz.y) return;
    float z = view_z(px);
    vec4 nr = texelFetch(normal_buffer, px, 0);
    float r = (nr.a < 0.5 ? nr.a : 1.0 - nr.a) * (255.0 / 127.0);
    if (r > 0.85 || texelFetch(depth_buffer, px, 0).r <= 0.0) {
        // Not in SceneKit's SSAO pass: nothing occludes from here (the far plane).
        imageStore(csz, px, vec4(0.0, 0.0, 1.0, -1.0e4));
        return;
    }
    vec3 n;
    if (r > 0.5) {
        // Geometric normal from depth: the flatter of the two neighbours on each axis.
        vec3 c = view_pos(px, z);
        ivec2 xl = clamp(px - ivec2(1, 0), ivec2(0), sz - 1), xr = clamp(px + ivec2(1, 0), ivec2(0), sz - 1);
        ivec2 yu = clamp(px - ivec2(0, 1), ivec2(0), sz - 1), yd = clamp(px + ivec2(0, 1), ivec2(0), sz - 1);
        vec3 l = view_pos(xl, view_z(xl)), rr = view_pos(xr, view_z(xr)), u = view_pos(yu, view_z(yu)), dn = view_pos(yd, view_z(yd));
        vec3 dx = abs(rr.z - c.z) < abs(c.z - l.z) ? rr - c : c - l;
        vec3 dy = abs(dn.z - c.z) < abs(c.z - u.z) ? dn - c : c - u;
        n = normalize(cross(dy, dx));
        if (dot(n, -c) < 0.0) n = -n;
    } else {
        n = normalize(nr.rgb * 2.0 - 1.0);
    }
    imageStore(csz, px, vec4(n, z));
}
";

    // Box-filtered mip (Metal generateMipmaps); SceneKit reads only the depth (w) of the mips.
    private const string MipSource = Header + @"
layout(rgba16f, set = 0, binding = 0) uniform restrict readonly image2D src;
layout(rgba16f, set = 0, binding = 1) uniform restrict writeonly image2D dst;
layout(push_constant, std430) uniform Params { vec4 sizes; } p;
void main() {
    ivec2 px = ivec2(gl_GlobalInvocationID.xy);
    if (px.x >= int(p.sizes.z) || px.y >= int(p.sizes.w)) return;
    ivec2 mx = ivec2(p.sizes.xy) - 1;
    vec4 a = imageLoad(src, min(px * 2, mx)), b = imageLoad(src, min(px * 2 + ivec2(1, 0), mx));
    vec4 c = imageLoad(src, min(px * 2 + ivec2(0, 1), mx)), d = imageLoad(src, min(px * 2 + ivec2(1, 1), mx));
    imageStore(dst, px, (a + b + c + d) * 0.25);
}
";

    // scn_ssao_downsample (SSAODownSample 2): per half-resolution pixel the index (gatherUV order (0,1),(1,1),(1,0),(0,0))
    // of the nearest texel of its 2x2 block where x + y is even and of the farthest where it is odd, with the kernel's
    // comparisons (ties included).
    private const string IndexSource = Header + @"
layout(rgba16f, set = 0, binding = 0) uniform restrict readonly image2D csz;
layout(r32ui, set = 0, binding = 1) uniform restrict writeonly uimage2D index_tex;
layout(push_constant, std430) uniform Params { vec4 sizes; } p;
void main() {
    ivec2 hp = ivec2(gl_GlobalInvocationID.xy);
    if (hp.x >= int(p.sizes.x) || hp.y >= int(p.sizes.y)) return;
    ivec2 b = hp * 2, mx = ivec2(p.sizes.zw) - 1;
    // gather components: x (0,1), y (1,1), z (1,0), w (0,0); SceneKit compares half-precision depths
    float x = imageLoad(csz, min(b + ivec2(0, 1), mx)).w;
    float y = imageLoad(csz, min(b + ivec2(1, 1), mx)).w;
    float z = imageLoad(csz, min(b + ivec2(1, 0), mx)).w;
    float w = imageLoad(csz, min(b, mx)).w;
    uint idx;
    if (((hp.x + hp.y) & 1) == 1) {
        // farthest (minimum view z)
        uint a = x >= y ? 1u : 0u;
        uint c = z >= w ? 3u : 2u;
        idx = min(x, y) > min(z, w) ? c : a;
    } else {
        // nearest (maximum view z)
        uint a = x < y ? 1u : 0u;
        uint c = z < w ? 3u : 2u;
        idx = max(x, y) < max(z, w) ? c : a;
    }
    imageStore(index_tex, hp, uvec4(idx));
}
";

    // scn_ssao_compute (SSAODownSample 2, SSAOSampleCount 9, perspective or orthographic).
    private const string SaoSource = Header + @"
layout(set = 0, binding = 0) uniform sampler2D csz;
layout(r32ui, set = 0, binding = 1) uniform restrict readonly uimage2D index_tex;
layout(rgba16f, set = 0, binding = 2) uniform restrict writeonly image2D ao;
layout(push_constant, std430) uniform Params { vec4 proj_info; vec4 sizes; vec4 r; vec4 info; } p;
const uvec2 gather_uv[4] = uvec2[4](uvec2(0, 1), uvec2(1, 1), uvec2(1, 0), uvec2(0, 0));
void main() {
    ivec2 hp = ivec2(gl_GlobalInvocationID.xy);
    if (hp.x >= int(p.sizes.x) || hp.y >= int(p.sizes.y)) return;
    bool ortho = p.info.y > 0.5;
    int mip_max = int(p.info.x) - 1;
    uvec2 base = uvec2(vec2(2.0) * (vec2(hp) + 0.5)); // uint(downsample x fragment position)
    uvec2 ssc = base + gather_uv[imageLoad(index_tex, hp).r];
    ivec2 full_max = ivec2(p.sizes.zw) - 1;
    vec4 t = texelFetch(csz, min(ivec2(ssc), full_max), 0);
    float z = t.w;
    vec2 cxy = p.proj_info.xy * vec2(base) + p.proj_info.zw;
    vec3 c = ortho ? vec3(cxy, z) : vec3(cxy * -z, z);
    vec3 n = t.xyz;
    int ix = int(ssc.x), iy = int(ssc.y);
    int h = (ix * 889516851 * ix + iy) * (iy * -239350325 * iy + ix);
    float angle0 = float(h) * 7.3145904e-10 + 1.5707964;
    float sum = 0.0;
    vec2 center = vec2(ssc);
    for (int i = 0; i < 9; i++) {
        float alpha = (float(i) + 0.5) / 9.0;
        float angle = alpha * 43.982296 + angle0;
        float ssr = alpha * -p.r.x / c.z;
        vec2 q = ssr * vec2(cos(angle), sin(angle)) + center;
        int mip = clamp(int(floor(log2(ssr))) - 3, 0, mip_max);
        ivec2 msz = max(textureSize(csz, mip) - 1, ivec2(0));
        ivec2 qp = clamp(ivec2(uvec2(max(q, vec2(0.0))) >> uint(mip)), ivec2(0), msz);
        float qz = texelFetch(csz, qp, mip).w;
        vec2 qxy = p.proj_info.xy * q + p.proj_info.zw;
        vec3 Q = ortho ? vec3(qxy, qz) : vec3(qxy * -qz, qz);
        vec3 v = Q - c;
        float vv = dot(v, v), vn = dot(v, n);
        float f = max(p.r.y - vv, 0.0);
        sum += f * f * f * max((vn - p.r.z) / (vv + 1.0e-4), 0.0);
    }
    float a = max(0.0, 1.0 - sum * 2.0 * p.r.w / 9.0);
    imageStore(ao, hp, vec4(a, -z, t.x, t.y));
}
";

    // scn_ssao_blur_x / _y: Gaussian taps at 0, +-2 .. +-12, kept where depth and both normal components are within the
    // thresholds (SceneKit computes this in half precision).
    private const string BlurSource = Header + @"
layout(rgba16f, set = 0, binding = 0) uniform restrict readonly image2D src;
layout(rgba16f, set = 0, binding = 1) uniform restrict writeonly image2D dst;
layout(push_constant, std430) uniform Params { vec4 sizes; vec4 thresholds; } p;
const float gauss[7] = float[7](0.11121, 0.10779, 0.09814, 0.08394, 0.06744, 0.05090, 0.03609);
void main() {
    ivec2 px = ivec2(gl_GlobalInvocationID.xy);
    ivec2 sz = ivec2(p.sizes.xy);
    if (px.x >= sz.x || px.y >= sz.y) return;
    ivec2 dir = ivec2(p.sizes.zw);
    vec4 c = imageLoad(src, px);
    float sum = c.x * gauss[0], wsum = gauss[0];
    for (int r = 1; r < 7; r++) {
        for (int s = -1; s <= 1; s += 2) {
            vec4 t = imageLoad(src, clamp(px + dir * (2 * r * s), ivec2(0), sz - 1));
            float w = gauss[r] * float(p.thresholds.x >= abs(t.y - c.y)) * float(p.thresholds.y >= abs(t.z - c.z)) * float(p.thresholds.y >= abs(t.w - c.w));
            sum += w * t.x; wsum += w;
        }
    }
    imageStore(dst, px, vec4(sum / wsum, c.yzw));
}
";

    // scn_ssao_upsampling: away from depth edges the bilinear sample at the half-resolution pixel corner (the average of
    // texels hp-1 and hp on both axes); at an edge (some neighbour's depth differs by depthThreshold or more) the
    // neighbour whose depth is closest.
    private const string UpSource = Header + @"
layout(set = 0, binding = 0) uniform sampler2D ao;
layout(rgba16f, set = 0, binding = 1) uniform restrict readonly image2D csz;
layout(r16f, set = 0, binding = 2) uniform restrict writeonly image2D outp;
layout(push_constant, std430) uniform Params { vec4 sizes; vec4 thresholds; } p;
const vec2 gather_uv[4] = vec2[4](vec2(0, 1), vec2(1, 1), vec2(1, 0), vec2(0, 0));
void main() {
    ivec2 px = ivec2(gl_GlobalInvocationID.xy);
    if (px.x >= int(p.sizes.x) || px.y >= int(p.sizes.y)) return;
    vec2 hsz = p.sizes.zw;
    vec2 hp = vec2(px / 2);
    vec2 c12 = hp - 0.5;
    ivec2 tl = ivec2(floor(c12 - 0.5));
    ivec2 mx = ivec2(hsz) - 1;
    float zf = imageLoad(csz, px).w;
    float d[4];
    for (int k = 0; k < 4; k++) d[k] = abs(texelFetch(ao, clamp(tl + ivec2(gather_uv[k]), ivec2(0), mx), 0).y + zf);
    int mn = min(d[0], d[2]) < min(d[1], d[3]) ? (d[2] < d[0] ? 2 : 0) : (d[3] < d[1] ? 3 : 1);
    int mxi = max(d[0], d[2]) > max(d[1], d[3]) ? (d[2] > d[0] ? 2 : 0) : (d[3] > d[1] ? 3 : 1);
    bool edge = d[mxi] >= p.thresholds.x;
    vec2 coord = edge ? gather_uv[mn] + c12 : hp;
    float a = textureLod(ao, coord / hsz, 0.0).x;
    imageStore(outp, px, vec4(a));
}
";
}

/// <summary>std430 push-constant block of vec4s.</summary>
internal sealed class PushConstants
{
    private readonly List<float> values = new();
    internal void Add(float a, float b, float c, float d) { values.Add(a); values.Add(b); values.Add(c); values.Add(d); }
    internal byte[] Bytes()
    {
        var bytes = new byte[values.Count * 4];
        Buffer.BlockCopy(values.ToArray(), 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
