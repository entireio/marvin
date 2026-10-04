using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Godot;

namespace Marvin.SceneKit;

/// <summary>SCNGeometrySource.Semantic.</summary>
public enum SCNGeometrySourceSemantic { vertex, normal, color, texcoord, tangent, vertexCrease, edgeCrease, boneWeights, boneIndices }
/// <summary>SCNGeometryPrimitiveType.</summary>
public enum SCNGeometryPrimitiveType { triangles = 0, triangleStrip = 1, line = 2, point = 3, polygon = 4 }
/// <summary>MTLVertexFormat subset used with SCNGeometrySource(buffer:...).</summary>
public enum MTLVertexFormat { float1 = 28, float2 = 29, float3 = 30, float4 = 31, half2 = 25, half4 = 27, uchar4Normalized = 9 }

/// <summary>
/// SCNGeometrySource. Holds raw bytes exactly like SceneKit (data, offset, stride,
/// bytesPerComponent), so code that reads sources back (TownShadowBatch,
/// FootPlacement) works unchanged. SCNGeometrySource(vertices:/normals:) store
/// Float32 x3 (stride 12) and textureCoordinates: Float32 x2 (stride 8), as measured.
/// </summary>
public sealed class SCNGeometrySource
{
    public readonly SCNGeometrySourceSemantic semantic;
    public readonly int vectorCount, componentsPerVector, bytesPerComponent, dataOffset, dataStride;
    public readonly bool usesFloatComponents;
    private readonly byte[] _data;
    private readonly MTLBuffer _buffer;
    private readonly int _bufferVersion;

    public SCNGeometrySource(byte[] data, SCNGeometrySourceSemantic semantic, int vectorCount, bool usesFloatComponents,
                             int componentsPerVector, int bytesPerComponent, int dataOffset, int dataStride)
    {
        _data = data; this.semantic = semantic; this.vectorCount = vectorCount; this.usesFloatComponents = usesFloatComponents;
        this.componentsPerVector = componentsPerVector; this.bytesPerComponent = bytesPerComponent;
        this.dataOffset = dataOffset; this.dataStride = dataStride == 0 ? componentsPerVector * bytesPerComponent : dataStride;
    }
    /// <summary>SCNGeometrySource(buffer:vertexFormat:semantic:vertexCount:dataOffset:dataStride:).</summary>
    public SCNGeometrySource(MTLBuffer buffer, MTLVertexFormat vertexFormat, SCNGeometrySourceSemantic semantic, int vertexCount, int dataOffset, int dataStride)
    {
        _buffer = buffer; _bufferVersion = buffer.version; this.semantic = semantic; vectorCount = vertexCount;
        (componentsPerVector, bytesPerComponent, usesFloatComponents) = vertexFormat switch
        {
            MTLVertexFormat.float1 => (1, 4, true), MTLVertexFormat.float2 => (2, 4, true), MTLVertexFormat.float3 => (3, 4, true),
            MTLVertexFormat.float4 => (4, 4, true), MTLVertexFormat.half2 => (2, 2, true), MTLVertexFormat.half4 => (4, 2, true),
            _ => (4, 1, false),
        };
        this.dataOffset = dataOffset; this.dataStride = dataStride;
    }
    public SCNGeometrySource(SCNVector3[] vertices) : this(PackF3(vertices), SCNGeometrySourceSemantic.vertex, vertices.Length, true, 3, 4, 0, 12) { }
    public SCNGeometrySource(IReadOnlyList<SCNVector3> vertices) : this(PackF3(vertices), SCNGeometrySourceSemantic.vertex, vertices.Count, true, 3, 4, 0, 12) { }
    /// <summary>SCNGeometrySource(vertices:) / (normals:) / (textureCoordinates:) are static factories in C#
    /// because their parameter types coincide: SCNGeometrySource.vertices(...), .normals(...), .textureCoordinates(...).</summary>
    public static SCNGeometrySource vertices(IReadOnlyList<SCNVector3> v) => new(PackF3(v), SCNGeometrySourceSemantic.vertex, v.Count, true, 3, 4, 0, 12);
    public static SCNGeometrySource normals(IReadOnlyList<SCNVector3> n) => new(PackF3(n), SCNGeometrySourceSemantic.normal, n.Count, true, 3, 4, 0, 12);
    public static SCNGeometrySource textureCoordinates(IReadOnlyList<CGPoint> uv)
    {
        var f = new float[uv.Count * 2];
        for (int i = 0; i < uv.Count; i++) { f[i * 2] = (float)uv[i].x; f[i * 2 + 1] = (float)uv[i].y; }
        return new(Bytes(f), SCNGeometrySourceSemantic.texcoord, uv.Count, true, 2, 4, 0, 8);
    }
    /// <summary>Data(bytes of a value array): `values.withUnsafeBytes { Data($0) }`.</summary>
    public static byte[] Bytes<T>(T[] values) where T : unmanaged => MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
    public static byte[] Bytes<T>(List<T> values) where T : unmanaged => MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(values)).ToArray();
    private static byte[] PackF3(IReadOnlyList<SCNVector3> v)
    {
        var f = new float[v.Count * 3];
        for (int i = 0; i < v.Count; i++) { f[i * 3] = (float)v[i].x; f[i * 3 + 1] = (float)v[i].y; f[i * 3 + 2] = (float)v[i].z; }
        return Bytes(f);
    }
    /// <summary>The raw bytes (Swift Data).</summary>
    public byte[] data => _buffer != null ? _buffer.bytes : _data;

    /// <summary>Component c of vector i as double (float, half, double or normalised integer sources).</summary>
    internal double Component(int i, int c)
    {
        var bytes = data;
        int o = dataOffset + i * dataStride + c * bytesPerComponent;
        if (usesFloatComponents)
            return bytesPerComponent switch
            {
                4 => BitConverter.ToSingle(bytes, o),
                8 => BitConverter.ToDouble(bytes, o),
                2 => (double)BitConverter.ToHalf(bytes, o),
                _ => 0,
            };
        return bytesPerComponent switch
        {
            1 => bytes[o] / 255.0,
            2 => BitConverter.ToUInt16(bytes, o) / 65535.0,
            4 => BitConverter.ToInt32(bytes, o),
            _ => 0,
        };
    }
    internal Vector3 V3(int i) => new((float)Component(i, 0), componentsPerVector > 1 ? (float)Component(i, 1) : 0, componentsPerVector > 2 ? (float)Component(i, 2) : 0);
    internal Vector2 V2(int i) => new((float)Component(i, 0), componentsPerVector > 1 ? (float)Component(i, 1) : 0);
    internal Color C4(int i) => new((float)Component(i, 0), componentsPerVector > 1 ? (float)Component(i, 1) : 0,
        componentsPerVector > 2 ? (float)Component(i, 2) : 0, componentsPerVector > 3 ? (float)Component(i, 3) : 1);
}

/// <summary>SCNGeometryElement: index data for one material slot.</summary>
public sealed class SCNGeometryElement
{
    public readonly byte[] data;
    public readonly SCNGeometryPrimitiveType primitiveType;
    public readonly int primitiveCount, bytesPerIndex;
    public double pointSize = 1, minimumPointScreenSpaceRadius, maximumPointScreenSpaceRadius;

    public SCNGeometryElement(byte[] data, SCNGeometryPrimitiveType primitiveType, int primitiveCount, int bytesPerIndex)
    { this.data = data; this.primitiveType = primitiveType; this.primitiveCount = primitiveCount; this.bytesPerIndex = bytesPerIndex; }
    public SCNGeometryElement(IReadOnlyList<int> indices, SCNGeometryPrimitiveType primitiveType)
        : this(SCNGeometrySource.Bytes(indices.ToArray()), primitiveType, Count(indices.Count, primitiveType), 4) { }
    public SCNGeometryElement(IReadOnlyList<uint> indices, SCNGeometryPrimitiveType primitiveType)
        : this(SCNGeometrySource.Bytes(indices.ToArray()), primitiveType, Count(indices.Count, primitiveType), 4) { }
    public SCNGeometryElement(IReadOnlyList<short> indices, SCNGeometryPrimitiveType primitiveType)
        : this(SCNGeometrySource.Bytes(indices.ToArray()), primitiveType, Count(indices.Count, primitiveType), 2) { }
    public SCNGeometryElement(IReadOnlyList<ushort> indices, SCNGeometryPrimitiveType primitiveType)
        : this(SCNGeometrySource.Bytes(indices.ToArray()), primitiveType, Count(indices.Count, primitiveType), 2) { }
    private static int Count(int n, SCNGeometryPrimitiveType t) => t switch
    {
        SCNGeometryPrimitiveType.triangles => n / 3,
        SCNGeometryPrimitiveType.triangleStrip => Math.Max(0, n - 2),
        SCNGeometryPrimitiveType.line => n / 2,
        _ => n,
    };
    internal int IndexCount => primitiveType switch
    {
        SCNGeometryPrimitiveType.triangles => primitiveCount * 3,
        SCNGeometryPrimitiveType.triangleStrip => primitiveCount + 2,
        SCNGeometryPrimitiveType.line => primitiveCount * 2,
        _ => primitiveCount,
    };
    internal int Index(int i) => bytesPerIndex switch
    {
        1 => data[i],
        2 => BitConverter.ToUInt16(data, i * 2),
        _ => (int)BitConverter.ToUInt32(data, i * 4),
    };
    /// <summary>Triangle list indices in SceneKit (counter-clockwise) order.</summary>
    internal int[] TriangleList()
    {
        int n = IndexCount;
        if (primitiveType == SCNGeometryPrimitiveType.triangles)
        {
            var r = new int[n];
            for (int i = 0; i < n; i++) r[i] = Index(i);
            return r;
        }
        if (primitiveType == SCNGeometryPrimitiveType.triangleStrip)
        {
            var r = new List<int>();
            for (int i = 0; i + 2 < n; i++)
            {
                int a = Index(i), b = Index(i + 1), c = Index(i + 2);
                if (i % 2 == 0) { r.Add(a); r.Add(b); r.Add(c); } else { r.Add(b); r.Add(a); r.Add(c); }
            }
            return r.ToArray();
        }
        return Array.Empty<int>();
    }
}

/// <summary>SCNLevelOfDetail.</summary>
public sealed class SCNLevelOfDetail
{
    public readonly SCNGeometry geometry;
    public readonly double screenSpaceRadius, worldSpaceDistance;
    private SCNLevelOfDetail(SCNGeometry g, double screen, double world) { geometry = g; screenSpaceRadius = screen; worldSpaceDistance = world; }
    /// <summary>SCNLevelOfDetail(geometry:worldSpaceDistance:).</summary>
    public static SCNLevelOfDetail withWorldSpaceDistance(SCNGeometry geometry, double worldSpaceDistance) => new(geometry, 0, worldSpaceDistance);
    /// <summary>SCNLevelOfDetail(geometry:screenSpaceRadius:) -> SCNLevelOfDetail.withScreenSpaceRadius(geometry, r).</summary>
    public static SCNLevelOfDetail withScreenSpaceRadius(SCNGeometry geometry, double screenSpaceRadius) => new(geometry, screenSpaceRadius, 0);
    public SCNLevelOfDetail(SCNGeometry geometry, double worldSpaceDistance) : this(geometry, 0, worldSpaceDistance) { }
}

/// <summary>
/// SCNGeometry. Converted lazily to a Godot ArrayMesh with one surface per
/// element (materials repeat cyclically like SceneKit). See PORTING.md for the
/// vertex attribute packing.
/// </summary>
public class SCNGeometry : IPropertyOwner
{
    public string name;
    private SCNGeometrySource[] _sources;
    private SCNGeometryElement[] _elements;
    private List<SCNMaterial> _materials = new();
    private SCNLevelOfDetail[] _levelsOfDetail;
    private (SCNVector3 min, SCNVector3 max)? _customBounds;
    private Dictionary<SCNShaderModifierEntryPoint, string> _shaderModifiers;
    internal readonly Dictionary<string, object> arguments = new();
    /// <summary>Binding version: materials, LODs, shader modifiers, arguments.</summary>
    internal int version;
    /// <summary>Mesh data version: sources, elements, custom bounds.</summary>
    internal int meshDataVersion;
    internal ArrayMesh mesh;
    private int meshVersion = -1;
    internal readonly HashSet<SCNNode> users = new();
    internal readonly List<ShaderMaterial> argumentVariants = new();

    protected SCNGeometry() { _sources = Array.Empty<SCNGeometrySource>(); _elements = Array.Empty<SCNGeometryElement>(); _materials.Add(new SCNMaterial()); }
    public SCNGeometry(IEnumerable<SCNGeometrySource> sources, IEnumerable<SCNGeometryElement> elements)
    {
        _sources = sources.ToArray(); _elements = elements?.ToArray() ?? Array.Empty<SCNGeometryElement>();
        _materials.Add(new SCNMaterial());
    }
    /// <summary>copy(): shares sources and elements; copies the material list, LODs and shader modifiers.
    /// A primitive (SCNBox, SCNCylinder, ...) copies to the same class with the same parameters, as in
    /// SceneKit (`box.copy() as? SCNBox` succeeds).</summary>
    public virtual SCNGeometry copy()
    {
        EnsureBuilt();
        var g = CopyShape() ?? new SCNGeometry(_sources, _elements);
        g.name = name; g._materials = new List<SCNMaterial>(_materials); g._levelsOfDetail = _levelsOfDetail; g._customBounds = _customBounds;
        g._shaderModifiers = _shaderModifiers == null ? null : new Dictionary<SCNShaderModifierEntryPoint, string>(_shaderModifiers);
        foreach (var kv in arguments) g.arguments[kv.Key] = kv.Value;
        return g;
    }
    /// <summary>A new instance of a primitive subclass with this one's shape parameters (null: plain geometry).</summary>
    protected virtual SCNGeometry CopyShape() => null;

    /// <summary>Primitive geometries (SCNBox, ...) build their sources on demand.</summary>
    protected virtual void Build() { }
    private bool buildDirty = true;
    protected void Rebuild() { buildDirty = true; Changed(true); }
    internal void EnsureBuilt()
    {
        if (!buildDirty) return;
        buildDirty = false;
        if (GetType() != typeof(SCNGeometry)) Build();
    }
    protected void SetData(SCNGeometrySource[] sources, SCNGeometryElement[] elements) { _sources = sources; _elements = elements; }

    public SCNGeometrySource[] sources { get { EnsureBuilt(); return _sources; } }
    public SCNGeometryElement[] elements { get { EnsureBuilt(); return _elements; } }
    public int elementCount => elements.Length;
    public SCNGeometrySource[] sourcesForSemantic(SCNGeometrySourceSemantic semantic) => sources.Where(s => s.semantic == semantic).ToArray();
    /// <summary>sources(for:) - `sources(for: .vertex)` -> `sources(SCNGeometrySourceSemantic.vertex)`.</summary>
    public SCNGeometrySource[] sourcesFor(SCNGeometrySourceSemantic semantic) => sourcesForSemantic(semantic);
    public SCNGeometryElement element(int at) => elements[at];

    /// <summary>materials. Swift arrays are values: the getter returns a copy, assign to change.</summary>
    public List<SCNMaterial> materials
    {
        get => new List<SCNMaterial>(_materials);
        set { _materials = value == null ? new List<SCNMaterial>() : new List<SCNMaterial>(value); Changed(); }
    }
    public SCNMaterial firstMaterial
    {
        get => _materials.Count > 0 ? _materials[0] : null;
        set { if (_materials.Count == 0) _materials.Add(value); else _materials[0] = value; Changed(); }
    }
    public SCNLevelOfDetail[] levelsOfDetail { get => _levelsOfDetail; set { _levelsOfDetail = value; Changed(); } }
    public Dictionary<SCNShaderModifierEntryPoint, string> shaderModifiers
    {
        get => _shaderModifiers;
        set { _shaderModifiers = value == null ? null : new Dictionary<SCNShaderModifierEntryPoint, string>(value); Changed(); }
    }
    /// <summary>setValue(_:forKey:) on a geometry: a shader argument for this geometry only (overrides the material's value).</summary>
    public void setValue(object value, string forKey)
    {
        if (value is SCNMaterialProperty p) { p.AddOwner(this); p.ArgumentOwner(this); }
        bool had = arguments.ContainsKey(forKey);
        arguments[forKey] = value is float f ? (double)f : value;
        if (!had) Changed(); else foreach (var v in argumentVariants) MaterialGpu.ApplyArgument(v, forKey, arguments[forKey], DrawnScene);
    }
    public object value(string forKey) => arguments.TryGetValue(forKey, out var v) ? v : null;
    void IPropertyOwner.PropertyChanged(SCNMaterialProperty property)
    {
        foreach (var kv in arguments) if (ReferenceEquals(kv.Value, property)) foreach (var v in argumentVariants) MaterialGpu.ApplyArgument(v, kv.Key, property, DrawnScene);
    }
    /// <summary>The scene this geometry is drawn in (its first user node's scene; null when detached).</summary>
    internal SCNScene DrawnScene
    {
        get { foreach (var n in users) if (n.sceneOwner != null) return n.sceneOwner; return null; }
    }
    /// <summary>A user node moved to another scene: SceneKit draws it with another program there, which resolves
    /// SCNMaterialProperty arguments again (see SCNMaterialProperty.ArgumentContents); rebind at the next flush.</summary>
    internal void SceneChanged()
    {
        foreach (var v in arguments.Values)
            if (v is SCNMaterialProperty) { foreach (var m in _materials) m.gpu.MarkDirty(false); return; }
    }
    internal IReadOnlyList<SCNMaterial> MaterialList => _materials;
    internal bool HasOwnShading => (_shaderModifiers != null && _shaderModifiers.Count > 0) || arguments.Count > 0;

    /// <summary>boundingBox (get/set). Setting it overrides the computed box (Godot CustomAabb), like SceneKit.</summary>
    public (SCNVector3 min, SCNVector3 max) boundingBox
    {
        get => _customBounds ?? ComputedBounds();
        set { _customBounds = value; Changed(true); }
    }
    public (SCNVector3 center, double radius) boundingSphere
    {
        get
        {
            var (mn, mx) = boundingBox;
            var c = new SCNVector3((mn.x + mx.x) / 2, (mn.y + mx.y) / 2, (mn.z + mx.z) / 2);
            return (c, (mx - c).Length);
        }
    }
    private (SCNVector3, SCNVector3)? cachedBounds;
    private int cachedBoundsVersion = -1;
    protected virtual (SCNVector3 min, SCNVector3 max) ComputedBounds()
    {
        if (cachedBounds.HasValue && cachedBoundsVersion == meshDataVersion) return cachedBounds.Value;
        var v = sources.FirstOrDefault(s => s.semantic == SCNGeometrySourceSemantic.vertex);
        SCNVector3 mn = SCNVector3.Zero, mx = SCNVector3.Zero;
        if (v != null && v.vectorCount > 0)
        {
            mn = new SCNVector3(double.MaxValue, double.MaxValue, double.MaxValue); mx = new SCNVector3(double.MinValue, double.MinValue, double.MinValue);
            for (int i = 0; i < v.vectorCount; i++)
            {
                double x = v.Component(i, 0), y = v.Component(i, 1), z = v.Component(i, 2);
                mn = new SCNVector3(Math.Min(mn.x, x), Math.Min(mn.y, y), Math.Min(mn.z, z));
                mx = new SCNVector3(Math.Max(mx.x, x), Math.Max(mx.y, y), Math.Max(mx.z, z));
            }
        }
        cachedBounds = (mn, mx); cachedBoundsVersion = meshDataVersion;
        return (mn, mx);
    }

    internal void Changed(bool meshData = false)
    {
        version++;
        if (meshData) meshDataVersion++;
        foreach (var n in users) n.MarkGeometryDirty();
    }

    // ---- Godot mesh
    /// <summary>
    /// The ArrayMesh for this geometry. Vertex attributes: vertex -> VERTEX,
    /// normal -> NORMAL, tangent (or generated from UV0 when absent) -> TANGENT,
    /// color -> COLOR (raw floats), texcoord channel 0 -> UV, 1 -> UV2,
    /// 2/3 -> CUSTOM0.xy/.zw, 4/5 -> CUSTOM1.xy/.zw, 6/7 -> CUSTOM2.xy/.zw (RGBA float).
    /// Triangles are re-wound clockwise (Godot front faces).
    /// </summary>
    internal ArrayMesh GodotMesh
    {
        get
        {
            EnsureBuilt();
            var runs = MaterialRuns();
            if (mesh != null && meshVersion == meshDataVersion && runs.SequenceEqual(meshRuns)) return mesh;
            meshRuns = runs;
            mesh = BuildMesh();
            meshVersion = meshDataVersion;
            return mesh;
        }
    }
    internal bool HasNormals => sources.Any(s => s.semantic == SCNGeometrySourceSemantic.normal);
    internal bool HasColors => sources.Any(s => s.semantic == SCNGeometrySourceSemantic.color);
    internal int SurfaceCount => mesh?.GetSurfaceCount() ?? 0;
    /// <summary>For each Godot surface, the index of the first SceneKit element it came from.</summary>
    internal readonly List<int> surfaceElements = new();
    private int[] meshRuns = Array.Empty<int>();
    /// <summary>
    /// First element of each run of consecutive elements that draw with the same material (materials[e % count]).
    /// A run becomes one Godot surface with the elements' triangles in SceneKit's order: SceneKit draws a geometry's
    /// elements in order (so a double-sided transparent box shows its front face, which then depth-rejects the back
    /// face), while Godot's order among one instance's transparent surfaces is undefined. Also fewer draw calls.
    /// </summary>
    private int[] MaterialRuns()
    {
        var runs = new List<int>();
        int count = _materials.Count;
        for (int e = 0; e < _elements.Length; e++)
            if (e == 0 || count == 0 || !ReferenceEquals(_materials[e % count], _materials[(e - 1) % count])) runs.Add(e);
        return runs.ToArray();
    }

    private ArrayMesh BuildMesh()
    {
        var result = new ArrayMesh();
        surfaceElements.Clear();
        var pos = sources.FirstOrDefault(s => s.semantic == SCNGeometrySourceSemantic.vertex);
        if (pos == null || pos.vectorCount == 0) return result;
        int n = pos.vectorCount;
        var nrm = sources.FirstOrDefault(s => s.semantic == SCNGeometrySourceSemantic.normal);
        var tan = sources.FirstOrDefault(s => s.semantic == SCNGeometrySourceSemantic.tangent);
        var col = sources.FirstOrDefault(s => s.semantic == SCNGeometrySourceSemantic.color);
        var tcs = sources.Where(s => s.semantic == SCNGeometrySourceSemantic.texcoord).Take(8).ToArray();

        var P = new Vector3[n];
        for (int i = 0; i < n; i++) P[i] = pos.V3(i);
        // Without a normal source SceneKit shades flat face normals (measured); the composer derives them per pixel
        // (VariantFlags.NoNormals). The +Z placeholder only feeds tangent generation and .geometry modifiers.
        var N = new Vector3[n];
        if (nrm != null) { for (int i = 0; i < Math.Min(n, nrm.vectorCount); i++) N[i] = nrm.V3(i); }
        else for (int i = 0; i < n; i++) N[i] = new Vector3(0, 0, 1);
        Color[] C = null;
        if (col != null)
        {
            C = new Color[n];
            for (int i = 0; i < n; i++) C[i] = i < col.vectorCount ? col.C4(i) : new Color(1, 1, 1, 1);
        }
        var UV = new Vector2[tcs.Length][];
        for (int t = 0; t < tcs.Length; t++) { UV[t] = new Vector2[n]; for (int i = 0; i < Math.Min(n, tcs[t].vectorCount); i++) UV[t][i] = tcs[t].V2(i); }

        // All triangle lists (SceneKit CCW order) per element.
        var elementLists = elements.Select(e => e.TriangleList()).ToArray();
        float[] T = null;
        if (tan != null)
        {
            T = new float[n * 4];
            for (int i = 0; i < n; i++) { var v = tan.V3(i); T[i * 4] = v.X; T[i * 4 + 1] = v.Y; T[i * 4 + 2] = v.Z; T[i * 4 + 3] = tan.componentsPerVector > 3 ? (float)tan.Component(i, 3) : 1; }
        }
        else if (N != null && UV.Length > 0) T = GenerateTangents(P, N, UV[0], elementLists);
        // One surface per run of elements sharing a material (see MaterialRuns), triangles in element order.
        var runStarts = meshRuns.Length > 0 ? meshRuns : MaterialRuns();
        var lists = new int[runStarts.Length][];
        for (int r = 0; r < runStarts.Length; r++)
        {
            int end = r + 1 < runStarts.Length ? runStarts[r + 1] : elementLists.Length;
            var merged = new List<int>();
            for (int e = runStarts[r]; e < end; e++) { var t = elementLists[e]; merged.AddRange(t.AsSpan(0, t.Length - t.Length % 3).ToArray()); }
            lists[r] = merged.ToArray();
        }

        Aabb aabb = default;
        bool first = true;
        for (int e = 0; e < lists.Length; e++)
        {
            var tri = lists[e];
            if (tri.Length < 3) continue;
            // Compact to the vertices this element uses (bounded memory for multi-element geometry).
            var remap = new Dictionary<int, int>();
            var order = new List<int>();
            var idx = new int[tri.Length - tri.Length % 3];
            for (int i = 0; i < idx.Length; i++)
            {
                int src = tri[i];
                if (src < 0 || src >= n) src = 0;
                if (!remap.TryGetValue(src, out int dst)) { dst = order.Count; remap[src] = dst; order.Add(src); }
                idx[i] = dst;
            }
            // SceneKit front faces are counter-clockwise, Godot's clockwise: swap 2nd and 3rd index.
            for (int i = 0; i + 2 < idx.Length; i += 3) (idx[i + 1], idx[i + 2]) = (idx[i + 2], idx[i + 1]);
            int m = order.Count;
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            var vp = new Vector3[m];
            for (int i = 0; i < m; i++) vp[i] = P[order[i]];
            arrays[(int)Mesh.ArrayType.Vertex] = vp;
            foreach (var p in vp) { if (first) { aabb = new Aabb(p, Vector3.Zero); first = false; } else aabb = aabb.Expand(p); }
            if (N != null) { var vn = new Vector3[m]; for (int i = 0; i < m; i++) vn[i] = N[order[i]]; arrays[(int)Mesh.ArrayType.Normal] = vn; }
            if (T != null) { var vt = new float[m * 4]; for (int i = 0; i < m; i++) for (int k = 0; k < 4; k++) vt[i * 4 + k] = T[order[i] * 4 + k]; arrays[(int)Mesh.ArrayType.Tangent] = vt; }
            // Vertex colours go to CUSTOM3 as RGBA floats: Godot's COLOR attribute is 8-bit unorm, and the
            // game stores data there (trail birth times > 1, signed slopes). The composer copies CUSTOM3 to COLOR.
            if (C != null)
            {
                var vc = new float[m * 4];
                for (int i = 0; i < m; i++) { var c = C[order[i]]; vc[i * 4] = c.R; vc[i * 4 + 1] = c.G; vc[i * 4 + 2] = c.B; vc[i * 4 + 3] = c.A; }
                arrays[(int)Mesh.ArrayType.Custom3] = vc;
            }
            if (UV.Length > 0) { var u = new Vector2[m]; for (int i = 0; i < m; i++) u[i] = UV[0][order[i]]; arrays[(int)Mesh.ArrayType.TexUV] = u; }
            if (UV.Length > 1) { var u = new Vector2[m]; for (int i = 0; i < m; i++) u[i] = UV[1][order[i]]; arrays[(int)Mesh.ArrayType.TexUV2] = u; }
            var flags = Mesh.ArrayFormat.FormatVertex;
            for (int c = 0; c < 3; c++)
            {
                int a = 2 + c * 2, b = a + 1;
                if (UV.Length <= a) break;
                var custom = new float[m * 4];
                for (int i = 0; i < m; i++)
                {
                    var ua = UV[a][order[i]]; var ub = UV.Length > b ? UV[b][order[i]] : Vector2.Zero;
                    custom[i * 4] = ua.X; custom[i * 4 + 1] = ua.Y; custom[i * 4 + 2] = ub.X; custom[i * 4 + 3] = ub.Y;
                }
                arrays[(int)Mesh.ArrayType.Custom0 + c] = custom;
                flags |= (Mesh.ArrayFormat)((long)Mesh.ArrayCustomFormat.RgbaFloat << ((int)Mesh.ArrayFormat.FormatCustom0Shift + c * (int)Mesh.ArrayFormat.FormatCustomBits));
            }
            if (C != null)
                flags |= (Mesh.ArrayFormat)((long)Mesh.ArrayCustomFormat.RgbaFloat << ((int)Mesh.ArrayFormat.FormatCustom0Shift + 3 * (int)Mesh.ArrayFormat.FormatCustomBits));
            arrays[(int)Mesh.ArrayType.Index] = idx;
            result.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, flags);
            surfaceElements.Add(runStarts[e]);
        }
        if (_customBounds.HasValue)
        {
            var (mn, mx) = _customBounds.Value;
            result.CustomAabb = new Aabb(mn.ToGodot(), (mx - mn).ToGodot());
        }
        return result;
    }

    /// <summary>
    /// Per-vertex tangents from UV0, accumulated over adjacent triangles and
    /// orthogonalised against the normal (SceneKit generates tangents the same way
    /// when a material needs them). w makes Godot's BINORMAL = cross(N,T)*w point
    /// towards decreasing v (image up), so normal maps follow the OpenGL (Y+) convention.
    /// </summary>
    private static float[] GenerateTangents(Vector3[] P, Vector3[] N, Vector2[] uv, int[][] lists)
    {
        int n = P.Length;
        var tan = new Vector3[n];
        var bit = new Vector3[n];
        foreach (var tri in lists)
            for (int i = 0; i + 2 < tri.Length; i += 3)
            {
                int a = tri[i], b = tri[i + 1], c = tri[i + 2];
                if (a >= n || b >= n || c >= n) continue;
                Vector3 e1 = P[b] - P[a], e2 = P[c] - P[a];
                Vector2 d1 = uv[b] - uv[a], d2 = uv[c] - uv[a];
                float det = d1.X * d2.Y - d1.Y * d2.X;
                if (Mathf.Abs(det) < 1e-12f) continue;
                float r = 1f / det;
                var t = (e1 * d2.Y - e2 * d1.Y) * r;
                var s = (e2 * d1.X - e1 * d2.X) * r;
                tan[a] += t; tan[b] += t; tan[c] += t;
                bit[a] += s; bit[b] += s; bit[c] += s;
            }
        var result = new float[n * 4];
        for (int i = 0; i < n; i++)
        {
            var nn = N[i].LengthSquared() > 0 ? N[i].Normalized() : Vector3.Up;
            var t = tan[i] - nn * nn.Dot(tan[i]);
            if (t.LengthSquared() < 1e-20f) t = Mathf.Abs(nn.X) < 0.9f ? Vector3.Right.Cross(nn).Cross(nn) * -1 : Vector3.Up.Cross(nn);
            t = t.Normalized();
            // Image-up bitangent is -dP/dv.
            float w = nn.Cross(t).Dot(-bit[i]) < 0 ? -1f : 1f;
            result[i * 4] = t.X; result[i * 4 + 1] = t.Y; result[i * 4 + 2] = t.Z; result[i * 4 + 3] = w;
        }
        return result;
    }
}
