using System;
using System.Collections.Generic;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

/// Opaque replacement patches, not overlays. Coarse terrain triangles are
/// removed beneath them so troughs cannot expose an undeformed second surface.
public sealed class DeformableSand
{
    public readonly SandDeformation field = new SandDeformation();
    public readonly SCNNode root = new SCNNode();
    private readonly bool enabled = !CommandLine.arguments.Contains("--benchmark-no-deformation");
    private readonly MTLDevice device = MTLCreateSystemDefaultDevice();
    private SCNMaterial material;
    private readonly struct Base
    {
        public readonly SCNNode node; public readonly SCNGeometry geometry; public readonly int x, z;
        public Base(SCNNode node, SCNGeometry geometry, int x, int z) { this.node = node; this.geometry = geometry; this.x = x; this.z = z; }
    }
    private readonly Dictionary<SandDeformation.Key, Base> bases = new();
    private sealed class Patch
    {
        public float[] lastGrid = Array.Empty<float>();
        public readonly SCNMaterialProperty heights = new SCNMaterialProperty();
        /// <summary>PORT: the patch's height texture, updated in place (see rebuild).</summary>
        public MTLTexture texture;
        public readonly SCNNode node = new SCNNode();
        public readonly SCNGeometrySource uv;
        public readonly float[] @base;
        public readonly Float3[] normals;
        public Patch(SandDeformation.Key key)
        {
            node.position = new SCNVector3((double)key.x * 4, 0, (double)key.z * 4);
            node.name = "Displaced dune sand"; node.castsShadow = !CommandLine.arguments.Contains("--benchmark-no-sand-shadows");
            var points = new List<CGPoint>(); var heights = new List<float>(); var normals = new List<Float3>();
            var cornerNormals = new Dictionary<(int, int), Float3>();
            Float3 normal(int ix, int iz)
            {
                var k = (ix, iz);
                if (cornerNormals.TryGetValue(k, out var n)) return n;
                var g = DesertTerrain.gradient(x: (double)ix * 2, z: (double)iz * 2);
                var result = Simd.normalize(new Float3((float)-g.x, 1, (float)-g.y)); cornerNormals[k] = result; return result;
            }
            for (int j = -1; j <= 65; j++)
                for (int i = -1; i <= 65; i++)
                {
                    double x = (double)key.x * 4 + (double)i * SandDeformation.step, z = (double)key.z * 4 + (double)j * SandDeformation.step;
                    heights.Add((float)DesertTerrain.height(x: x, z: z));
                    if (i >= 0 && i <= 64 && j >= 0 && j <= 64)
                    {
                        points.Add(new CGPoint(x / 4, z / 4));
                        int ix = (int)floor(x / 2), iz = (int)floor(z / 2); float u = (float)(x / 2 - (double)ix), v = (float)(z / 2 - (double)iz);
                        Float3 a = normal(ix, iz), b = normal(ix + 1, iz), c = normal(ix, iz + 1), d = normal(ix + 1, iz + 1);
                        normals.Add(Simd.normalize(u + v <= 1 ? a + (b - a) * u + (c - a) * v : d + (c - d) * (1 - u) + (b - d) * (1 - v)));
                    }
                }
            @base = heights.ToArray(); this.normals = normals.ToArray(); uv = SCNGeometrySource.textureCoordinates(points);
        }
    }
    private readonly Dictionary<SandDeformation.Key, Patch> patches = new();
    private readonly SCNGeometryElement elements;
    private int topology = -1;
    private double pending = 0.0;
    public int updates { get; private set; } = 0;
    public int patchCount => patches.Count;
    public DeformableSand()
    {
        var indices = new List<uint>();
        for (int j = 0; j < 64; j++)
            for (int i = 0; i < 64; i++)
            {
                uint a = (uint)(j * 65 + i), b = a + 1, c = a + 65, d = c + 1;
                indices.AddRange(new[] { a, c, b, b, c, d });
            }
        elements = new SCNGeometryElement(indices, SCNGeometryPrimitiveType.triangles);
    }
    public void configure(SCNMaterial material, SCNNode root) { this.material = material; root.addChildNode(this.root); }
    public void register(SCNNode node, double x, double z)
    {
        bases[new SandDeformation.Key((int)(x / 64), (int)(z / 64))] = new Base(node, node.geometry, (int)x, (int)z);
    }
    public void reset()
    {
        field.reset(); foreach (var patch in patches.Values) patch.node.removeFromParentNode(); patches.Clear();
        foreach (var @base in bases.Values) @base.node.geometry = @base.geometry;
        topology = -1; pending = 0; updates = 0;
    }
    public void update(IReadOnlyList<Simulation> states, IReadOnlyList<SandDeformation.Contact[]> contacts, double dt)
    {
        if (!enabled) return;
        for (int i = 0; i < contacts.Count; i++) field.contactLayouts[i] = contacts[i];
        pending += dt;
        if (!(pending >= 1.0 / 30 - 1e-8)) return;
        double step = min(pending, 0.05); pending = 0;
        if (!(field.tiles.Count > 0 || states.Any(s => max(abs(s.x), abs(s.z)) > DesertTerrain.townEdge + 8))) return;
        field.begin(dt: step, positions: states.Select(s => new Double2(s.x, s.z)));
        foreach (var (state, feet) in states.Zip(contacts)) field.stamp(state, contacts: feet, dt: step);
        field.settle(dt: step);
        if (topology != field.topologyVersion)
        {
            var removed = new HashSet<SandDeformation.Key>(patches.Keys); removed.ExceptWith(field.tiles.Keys);
            foreach (var key in removed) { if (patches.Remove(key, out var gone)) gone.node.removeFromParentNode(); }
            foreach (var key in field.tiles.Keys.Where(k => !patches.ContainsKey(k)).ToList())
            {
                var patch = new Patch(key); patches[key] = patch; root.addChildNode(patch.node);
            }
            foreach (var @base in bases.Values)
            {
                var holes = new HashSet<SandDeformation.Key>(field.tiles.Keys.Where(k => k.x * 4 >= @base.x && k.x * 4 < @base.x + 64 && k.z * 4 >= @base.z && k.z * 4 < @base.z + 64));
                if (holes.Count == 0) { @base.node.geometry = @base.geometry; continue; }
                var indices = new List<uint>();
                for (int j = 0; j < 32; j++)
                    for (int i = 0; i < 32; i++)
                    {
                        var key = new SandDeformation.Key((int)floor((double)(@base.x + i * 2) / 4), (int)floor((double)(@base.z + j * 2) / 4));
                        if (holes.Contains(key)) continue;
                        uint a = (uint)(j * 33 + i), b = a + 1, c = a + 33, d = c + 1;
                        indices.AddRange(new[] { a, c, b, b, c, d });
                    }
                var mesh = new SCNGeometry(@base.geometry.sources, new[] { new SCNGeometryElement(indices, SCNGeometryPrimitiveType.triangles) });
                mesh.materials = @base.geometry.materials; @base.node.geometry = mesh;
            }
            topology = field.topologyVersion;
        }
        foreach (var key in field.dirty) { if (patches.TryGetValue(key, out var patch)) rebuild(patch, key: key); }
    }
    private struct Vertex { public Float4 position, normal; }
    private void rebuild(Patch patch, SandDeformation.Key key)
    {
        var delta = field.grid(key);
        // Accumulate submillimetre settling until it changes the visible shape.
        if (patch.lastGrid.Length > 0 && delta.Zip(patch.lastGrid).All(p => abs(p.First - p.Second) < 0.0005f)) return;
        patch.lastGrid = delta;
        if (patch.node.geometry == null)
        {
            var buffer = device.makeBuffer(length: 65 * 65 * System.Runtime.InteropServices.Marshal.SizeOf<Vertex>(), options: MTLResourceOptions.storageModeShared);
            var vertices = buffer.contents<Vertex>();
            float low = 1000, high = -1000;
            for (int j = 0; j <= 64; j++)
                for (int i = 0; i <= 64; i++)
                {
                    float h = patch.@base[(j + 1) * 67 + i + 1]; var normal = patch.normals[j * 65 + i];
                    vertices[j * 65 + i] = new Vertex { position = new Float4((float)i * 0.0625f, h, (float)j * 0.0625f, 1), normal = new Float4(normal, 0) };
                    low = min(low, h); high = max(high, h);
                }
            var position = new SCNGeometrySource(buffer, MTLVertexFormat.float3, SCNGeometrySourceSemantic.vertex, vertexCount: 4225, dataOffset: 0, dataStride: 32);
            var normalSource = new SCNGeometrySource(buffer, MTLVertexFormat.float3, SCNGeometrySourceSemantic.normal, vertexCount: 4225, dataOffset: 16, dataStride: 32);
            var mesh = new SCNGeometry(new[] { position, normalSource, patch.uv }, new[] { elements }); mesh.materials = new() { material };
            mesh.shaderModifiers = new() { [SCNShaderModifierEntryPoint.geometry] = displacement };
            mesh.boundingBox = (new SCNVector3(0, (double)low - 0.14, 0), new SCNVector3(4, (double)high + 0.11, 4));
            patch.heights.minificationFilter = SCNFilterMode.nearest; patch.heights.magnificationFilter = SCNFilterMode.nearest; patch.heights.mipFilter = SCNFilterMode.none;
            mesh.setValue(patch.heights, "duneHeights");
            patch.node.geometry = mesh;
        }
        // Immutable small height texture, retained by SceneKit until its draw
        // completes. Static vertex/index buffers stay on the GPU throughout.
        // PORT: Swift makes a new texture per update because SceneKit's in-flight draws still read the previous one.
        // Godot updates a texture in place safely (ImageTexture.Update before the frame is drawn), and the patch
        // geometry's argument shows content changes either way (a custom SCNGeometry: SCNMaterialProperty.ArgumentContents),
        // so each patch keeps one texture and replaces its texels: the same heights, without a new GPU texture per update
        // (about 50 per second while robots drive over the dunes).
        if (patch.texture == null)
        {
            var descriptor = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: MTLPixelFormat.r32Float, width: 67, height: 67, mipmapped: false);
            descriptor.storageMode = device.supportsFamily(MTLGPUFamily.apple1) ? MTLStorageMode.shared : MTLStorageMode.managed; descriptor.usage = MTLTextureUsage.shaderRead;
            patch.texture = device.makeTexture(descriptor);
        }
        var texture = patch.texture;
        texture.replace(MTLRegionMake2D(0, 0, 67, 67), mipmapLevel: 0, withBytes: delta, bytesPerRow: 67 * 4);
        if (patch.heights.contents != texture) patch.heights.contents = texture;
        updates += 1;
    }
    // MSL `duneHeights.read(p)` -> texelFetch (PORTING.md, shader modifier translation guide).
    private const string displacement = @"
#pragma arguments
sampler2D duneHeights : filter_nearest;
#pragma body
uvec2 p = uvec2(round(VERTEX.xz * 16.0)) + uvec2(1);
float h = texelFetch(duneHeights, ivec2(p), 0).r;
float dx = (texelFetch(duneHeights, ivec2(p + uvec2(1, 0)), 0).r - texelFetch(duneHeights, ivec2(p - uvec2(1, 0)), 0).r) * 8.0;
float dz = (texelFetch(duneHeights, ivec2(p + uvec2(0, 1)), 0).r - texelFetch(duneHeights, ivec2(p - uvec2(0, 1)), 0).r) * 8.0;
VERTEX.y += h;
NORMAL = normalize(NORMAL + vec3(-dx, 0.0, -dz) * NORMAL.y);
";
}
