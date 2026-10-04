using System;
using System.Collections.Generic;
using System.Linq;
using Marvin.Core;

namespace Marvin;

/// Shadow geometry: identical static town triangles, welded only by
/// exact position, with separate single/double-sided material groups. The main
/// camera keeps its original geometry, materials, tangents and LODs.
// PORT: Swift's `deinit` (disable, then remove the proxies) is Dispose(); C# has no deterministic
// destructor and a finalizer must not touch the scene graph. Swift's `throws` is an exception.
public sealed class TownShadowBatch : IDisposable
{
    private struct Pair
    {
        public readonly SCNNode original, proxy;
        public bool castsShadow;
        public Pair(SCNNode original, SCNNode proxy, bool castsShadow) { this.original = original; this.proxy = proxy; this.castsShadow = castsShadow; }
    }
    private List<Pair> pairs = new();
    private Dictionary<SCNNode, int> pairIndices = new(ReferenceEqualityComparer.Instance);
    private readonly SCNCamera camera;
    private readonly int cameraMask;
    private readonly List<(SCNLight, int)> lights;
    private bool enabled = false;
    public int inputVertices { get; private set; } = 0;
    public int outputVertices { get; private set; } = 0;
    public int inputElements { get; private set; } = 0;
    public int outputElements { get; private set; } = 0;
    public int triangles { get; private set; } = 0;
    private Dictionary<SCNGeometry, SCNGeometry> cache = new(ReferenceEqualityComparer.Instance);
    private static readonly int proxyBit = 1 << 20;
    private readonly SCNMaterial single = new SCNMaterial(), @double = new SCNMaterial();
    /// Swift `enum Failure: Error { case unsupportedGeometry }`.
    public sealed class Failure : Exception
    {
        private Failure(string message) : base(message) { }
        public static Failure unsupportedGeometry => new("unsupportedGeometry");
    }

    public TownShadowBatch(SCNNode root, SCNCamera camera)
    {
        this.camera = camera; cameraMask = camera.categoryBitMask;
        var lights = new List<(SCNLight, int)>(); var nodes = new List<SCNNode>();
        root.enumerateChildNodes((node, _) =>
        {
            if (node.light is SCNLight light && light.castsShadow) { lights.Add((light, light.categoryBitMask)); }
            if (node.name?.StartsWith("Town cell ", StringComparison.Ordinal) == true) { nodes.Add(node); }
        });
        this.lights = lights;
        single.lightingModel = SCNMaterial.LightingModel.constant; @double.lightingModel = SCNMaterial.LightingModel.constant; @double.isDoubleSided = true;
        foreach (var node in nodes)
        {
            if (node.geometry is not SCNGeometry geometry) { throw Failure.unsupportedGeometry; }
            var proxy = new SCNNode(build(geometry)); proxy.simdTransform = node.simdTransform; proxy.pivot = node.pivot;
            proxy.name = "Town shadow batch"; proxy.categoryBitMask = TownShadowBatch.proxyBit; proxy.isHidden = true;
            node.parent.addChildNode(proxy);
            pairIndices[node] = pairs.Count;
            pairs.Add(new Pair(original: node, proxy: proxy, castsShadow: node.castsShadow));
        }
        if (pairs.Count == 0) { throw Failure.unsupportedGeometry; }
    }
    public void Dispose()
    {
        setEnabled(false);
        foreach (var pair in pairs) { pair.proxy.removeFromParentNode(); }
    }
    public void setEnabled(bool value)
    {
        if (value == enabled) { return; }
        enabled = value;
        camera.categoryBitMask = value ? cameraMask & ~TownShadowBatch.proxyBit : cameraMask;
        foreach (var (light, mask) in lights) { light.categoryBitMask = value ? mask | TownShadowBatch.proxyBit : mask; }
        for (var i = 0; i < pairs.Count; i++)
        {
            var pair = pairs[i];
            if (value)
            {
                pair.castsShadow = pair.original.castsShadow;
                pair.original.castsShadow = false;
                pair.proxy.isHidden = !pair.castsShadow;
                pair.proxy.castsShadow = pair.castsShadow;
            } else
            {
                pair.original.castsShadow = pair.castsShadow; pair.proxy.isHidden = true;
            }
            pairs[i] = pair;
        }
    }
    /// Apply the town's authoritative caster decision every frame. Originals
    /// stay disabled while the shadow copy follows distance/frustum visibility.
    public bool setCaster(SCNNode original, bool enabled)
    {
        var value = enabled;
        if (!(this.enabled && pairIndices.TryGetValue(original, out var i))) { return false; }
        var pair = pairs[i]; pair.castsShadow = value; pairs[i] = pair;
        if (original.castsShadow) { original.castsShadow = false; }
        var proxy = pairs[i].proxy;
        if (proxy.castsShadow != value) { proxy.castsShadow = value; }
        if (proxy.isHidden == value) { proxy.isHidden = !value; }
        return true;
    }
    private SCNGeometry build(SCNGeometry geometry)
    {
        if (cache.TryGetValue(geometry, out var existing)) { return existing; }
        var allowed = new[] { CityMaterials.plaster, CityMaterials.adobe, CityMaterials.cloth, CityMaterials.metal };
        var materials = geometry.materials;
        if (!(materials.Count != 0 && materials.All(m => allowed.Any(a => ReferenceEquals(a, m))) &&
              geometry.sourcesFor(SCNGeometrySourceSemantic.vertex).FirstOrDefault() is SCNGeometrySource source && source.usesFloatComponents &&
              source.componentsPerVector == 3 && new[] { 4, 8 }.Contains(source.bytesPerComponent))) { throw Failure.unsupportedGeometry; }
        List<SCNVector3> vertices = new(); var lookup = new Dictionary<Float3, int>(); var remap = new List<int>();
        var bytes = source.data;
        for (var i = 0; i < source.vectorCount; i++)
        {
            var point = Float3.zero;
            for (var c = 0; c < 3; c++)
            {
                var offset = source.dataOffset + i * source.dataStride + c * source.bytesPerComponent;
                var value = source.bytesPerComponent == 4 ? (double)BitConverter.ToSingle(bytes, offset) : BitConverter.ToDouble(bytes, offset);
                if (!(double.IsFinite(value) && (double)(float)value == value)) { throw Failure.unsupportedGeometry; }
                point[c] = (float)value;
            }
            if (lookup.TryGetValue(point, out var index)) { remap.Add(index); }
            else { var added = vertices.Count; lookup[point] = added; remap.Add(added); vertices.Add(new SCNVector3(point.x, point.y, point.z)); }
        }
        var groups = new[] { new List<int>(), new List<int>() };
        foreach (var (i, element) in geometry.elements.Select((element, i) => (i, element)))
        {
            if (!(element.primitiveType == SCNGeometryPrimitiveType.triangles && new[] { 1, 2, 4 }.Contains(element.bytesPerIndex))) { throw Failure.unsupportedGeometry; }
            var data = element.data;
            var group = materials[i % materials.Count].isDoubleSided ? 1 : 0;
            for (var j = 0; j < element.primitiveCount * 3; j++)
            {
                int old;
                {
                    var offset = j * element.bytesPerIndex;
                    if (element.bytesPerIndex == 1) { old = data[offset]; }
                    else if (element.bytesPerIndex == 2) { old = BitConverter.ToUInt16(data, offset); }
                    else { old = (int)BitConverter.ToUInt32(data, offset); }
                }
                if (!(old >= 0 && old < remap.Count)) { throw Failure.unsupportedGeometry; }
                groups[group].Add(remap[old]);
            }
            triangles += element.primitiveCount;
        }
        var active = Enumerable.Range(0, groups.Length).Where(g => groups[g].Count != 0).ToList();
        var result = new SCNGeometry(new[] { SCNGeometrySource.vertices(vertices) }, active.Select(g => new SCNGeometryElement(groups[g], SCNGeometryPrimitiveType.triangles)).ToList());
        result.materials = active.Select(g => g == 0 ? single : @double).ToList();
        cache[geometry] = result;
        inputVertices += source.vectorCount; outputVertices += vertices.Count;
        inputElements += geometry.elements.Length; outputElements += active.Count;
        result.levelsOfDetail = geometry.levelsOfDetail?.Select(level =>
        {
            if (level.geometry is not SCNGeometry original) { throw Failure.unsupportedGeometry; }
            var lodGeometry = build(original);
            return level.screenSpaceRadius > 0 ? SCNLevelOfDetail.withScreenSpaceRadius(lodGeometry, level.screenSpaceRadius) : SCNLevelOfDetail.withWorldSpaceDistance(lodGeometry, level.worldSpaceDistance);
        }).ToArray();
        return result;
    }
    // Telemetry must not query hundreds of SceneKit properties during a drive.
    public Dictionary<string, int> telemetryStatistics => new()
    {
        ["enabled"] = enabled ? 1 : 0, ["activeProxies"] = enabled ? pairs.Count(pair => pair.castsShadow) : 0,
        ["nodes"] = pairs.Count, ["inputVertices"] = inputVertices, ["outputVertices"] = outputVertices,
        ["inputElements"] = inputElements, ["outputElements"] = outputElements, ["triangles"] = triangles,
    };
    public Dictionary<string, int> statistics => new() { ["enabled"] = enabled ? 1 : 0, ["activeProxies"] = pairs.Count(pair => !pair.proxy.isHidden && pair.proxy.castsShadow), ["originalCasters"] = pairs.Count(pair => pair.original.castsShadow), ["nodes"] = pairs.Count, ["inputVertices"] = inputVertices, ["outputVertices"] = outputVertices, ["inputElements"] = inputElements, ["outputElements"] = outputElements, ["triangles"] = triangles };
}
