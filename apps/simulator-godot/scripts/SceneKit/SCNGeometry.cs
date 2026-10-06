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
        var bytes = new byte[uv.Count * 8];
        var f = MemoryMarshal.Cast<byte, float>(bytes.AsSpan());
        if (uv is List<CGPoint> list)
        {
            var points = CollectionsMarshal.AsSpan(list);
            for (int i = 0; i < points.Length; i++) { f[i * 2] = (float)points[i].x; f[i * 2 + 1] = (float)points[i].y; }
        }
        else for (int i = 0; i < uv.Count; i++) { f[i * 2] = (float)uv[i].x; f[i * 2 + 1] = (float)uv[i].y; }
        return new(bytes, SCNGeometrySourceSemantic.texcoord, uv.Count, true, 2, 4, 0, 8);
    }
    /// <summary>Data(bytes of a value array): `values.withUnsafeBytes { Data($0) }`.</summary>
    public static byte[] Bytes<T>(T[] values) where T : unmanaged => MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
    public static byte[] Bytes<T>(List<T> values) where T : unmanaged => MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(values)).ToArray();
    /// <summary>Float32 x3 bytes, written straight into the source's byte array (per-frame batches build several per frame).</summary>
    private static byte[] PackF3(IReadOnlyList<SCNVector3> v)
    {
        var bytes = new byte[v.Count * 12];
        var f = MemoryMarshal.Cast<byte, float>(bytes.AsSpan());
        if (v is List<SCNVector3> list)
        {
            var vectors = CollectionsMarshal.AsSpan(list);
            for (int i = 0; i < vectors.Length; i++) { f[i * 3] = (float)vectors[i].x; f[i * 3 + 1] = (float)vectors[i].y; f[i * 3 + 2] = (float)vectors[i].z; }
        }
        else for (int i = 0; i < v.Count; i++) { f[i * 3] = (float)v[i].x; f[i * 3 + 1] = (float)v[i].y; f[i * 3 + 2] = (float)v[i].z; }
        return bytes;
    }
    /// <summary>The raw bytes (Swift Data).</summary>
    public byte[] data => _buffer != null ? _buffer.bytes : _data;
    /// <summary>Version of the backing MTLBuffer (0 for data sources): changes when its contents are written.</summary>
    internal int BufferVersion => _buffer?.version ?? 0;

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
    /// <summary>V3 of vectors 0..count-1 into dst; float32 sources are read directly (Component's double round trip of a
    /// float is exact, so the values are the same).</summary>
    internal void ReadV3(Vector3[] dst, int count)
    {
        if (usesFloatComponents && bytesPerComponent == 4 && componentsPerVector >= 3)
        {
            var bytes = data;
            for (int i = 0; i < count; i++)
            {
                int o = dataOffset + i * dataStride;
                dst[i] = new Vector3(BitConverter.ToSingle(bytes, o), BitConverter.ToSingle(bytes, o + 4), BitConverter.ToSingle(bytes, o + 8));
            }
        }
        else for (int i = 0; i < count; i++) dst[i] = V3(i);
    }
    /// <summary>V2 of vectors 0..count-1 into dst (float32 sources read directly).</summary>
    internal void ReadV2(Vector2[] dst, int count)
    {
        if (usesFloatComponents && bytesPerComponent == 4 && componentsPerVector >= 2)
        {
            var bytes = data;
            for (int i = 0; i < count; i++)
            {
                int o = dataOffset + i * dataStride;
                dst[i] = new Vector2(BitConverter.ToSingle(bytes, o), BitConverter.ToSingle(bytes, o + 4));
            }
        }
        else for (int i = 0; i < count; i++) dst[i] = V2(i);
    }
    /// <summary>C4 of vectors 0..count-1 into dst (float32 RGBA sources read directly).</summary>
    internal void ReadC4(Color[] dst, int count)
    {
        if (usesFloatComponents && bytesPerComponent == 4 && componentsPerVector >= 4)
        {
            var bytes = data;
            for (int i = 0; i < count; i++)
            {
                int o = dataOffset + i * dataStride;
                dst[i] = new Color(BitConverter.ToSingle(bytes, o), BitConverter.ToSingle(bytes, o + 4), BitConverter.ToSingle(bytes, o + 8), BitConverter.ToSingle(bytes, o + 12));
            }
        }
        else for (int i = 0; i < count; i++) dst[i] = C4(i);
    }
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
        : this(IndexBytes(indices), primitiveType, Count(indices.Count, primitiveType), 4) { }
    public SCNGeometryElement(IReadOnlyList<uint> indices, SCNGeometryPrimitiveType primitiveType)
        : this(IndexBytes(indices), primitiveType, Count(indices.Count, primitiveType), 4) { }
    /// <summary>The indices' bytes with a single copy for lists and arrays (the same bytes as Bytes(indices.ToArray())).</summary>
    private static byte[] IndexBytes<T>(IReadOnlyList<T> indices) where T : unmanaged => indices switch
    {
        List<T> list => SCNGeometrySource.Bytes(list),
        T[] array => SCNGeometrySource.Bytes(array),
        _ => SCNGeometrySource.Bytes(indices.ToArray()),
    };
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
            // 32-bit indices reinterpret as int exactly like Index() ((int)ToUInt32).
            if (bytesPerIndex == 4) return MemoryMarshal.Cast<byte, int>(data.AsSpan(0, n * 4)).ToArray();
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
    /// <summary>The material list; null while the geometry still has SceneKit's default material (one new SCNMaterial),
    /// which is created when first read: per-frame batch geometries replace it at once, and it built 16 properties each.</summary>
    private List<SCNMaterial> _materialList;
    private List<SCNMaterial> _materials
    {
        get => _materialList ??= new List<SCNMaterial> { new SCNMaterial() };
        set => _materialList = value;
    }
    private SCNLevelOfDetail[] _levelsOfDetail;
    private (SCNVector3 min, SCNVector3 max)? _customBounds;
    private Dictionary<SCNShaderModifierEntryPoint, string> _shaderModifiers;
    private bool _godotAutomaticLevelsOfDetail;
    internal readonly Dictionary<string, object> arguments = new();
    /// <summary>Binding version: materials, LODs, shader modifiers, arguments.</summary>
    internal int version;
    /// <summary>Mesh data version: sources, elements, custom bounds.</summary>
    internal int meshDataVersion;
    internal ArrayMesh mesh;
    private int meshVersion = -1;
    internal readonly HashSet<SCNNode> users = new();
    internal readonly List<ShaderMaterial> argumentVariants = new();

    protected SCNGeometry() { _sources = Array.Empty<SCNGeometrySource>(); _elements = Array.Empty<SCNGeometryElement>(); }
    public SCNGeometry(IEnumerable<SCNGeometrySource> sources, IEnumerable<SCNGeometryElement> elements)
    {
        _sources = sources.ToArray(); _elements = elements?.ToArray() ?? Array.Empty<SCNGeometryElement>();
    }
    /// <summary>copy(): shares sources and elements; copies the material list, LODs and shader modifiers.
    /// A primitive (SCNBox, SCNCylinder, ...) copies to the same class with the same parameters, as in
    /// SceneKit (`box.copy() as? SCNBox` succeeds).</summary>
    public virtual SCNGeometry copy()
    {
        EnsureBuilt();
        var g = CopyShape() ?? new SCNGeometry(_sources, _elements);
        g.name = name; g._materials = new List<SCNMaterial>(_materials); g._levelsOfDetail = _levelsOfDetail; g._customBounds = _customBounds;
        g._godotAutomaticLevelsOfDetail = _godotAutomaticLevelsOfDetail;
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
    /// <summary>The first vertex source (hit testing).</summary>
    internal SCNGeometrySource VertexSource => Array.Find(sources, s => s.semantic == SCNGeometrySourceSemantic.vertex);
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
    /// <summary>
    /// Godot-only: draw this geometry's shadows from screen-space levels of detail, for static, very finely tessellated
    /// geometry that SceneKit draws at full detail (the robots' CAD and scanned meshes: Marvin's 695,000 triangles cover a
    /// few thousand pixels in a town view, so every shadow map rasterised tens of triangles per pixel). The mesh gets
    /// Godot's own mesh LODs (meshoptimizer, as for imported scenes; vertex normals weighted in the error); the node draws
    /// the full mesh for the camera and the depth prepass, unchanged, and a shadow-only twin instance with the levels casts
    /// its shadows (SCNNode.RebuildMeshes; SceneKitCalibration.MeshLodForCamera lets the camera draw the levels too). A
    /// level is used only while its geometric error stays below SceneKitCalibration.MeshLodThreshold pixels of the
    /// camera's view, far below the shadow maps' texels. Surfaces with fewer than AutomaticLodMinTriangles triangles,
    /// vertex colours or SceneKit levels of detail are left alone. Generated when the mesh is prepared (on worker threads
    /// in a large flush).
    /// </summary>
    public bool godotAutomaticLevelsOfDetail { get => _godotAutomaticLevelsOfDetail; set { if (_godotAutomaticLevelsOfDetail == value) return; _godotAutomaticLevelsOfDetail = value; Changed(true); } }
    internal static int AutomaticLodMinTriangles => SceneKitCalibration.MeshLodMinTriangles;
    public Dictionary<SCNShaderModifierEntryPoint, string> shaderModifiers
    {
        get => _shaderModifiers;
        set { _shaderModifiers = value == null ? null : new Dictionary<SCNShaderModifierEntryPoint, string>(value); Changed(); }
    }
    /// <summary>setValue(_:forKey:) on a geometry: a shader argument for this geometry only (overrides the material's value).</summary>
    public void setValue(object value, string forKey)
    {
        if (value is SCNMaterialProperty p) { p.AddOwner(this); p.ArgumentOwner(this); }
        bool had = arguments.TryGetValue(forKey, out var old);
        var stored = value is float f ? (double)f : value;
        arguments[forKey] = stored;
        if (!had) { Changed(); return; }
        if (InstanceArguments.Contains(forKey) && InstanceValue(stored))
        {
            // A per-instance argument (Godot instance uniforms): the mesh instances of the user nodes take the new value.
            foreach (var n in users) n.MarkGeometryDirty();
            return;
        }
        // Another bound value selects another shared variant (see ShadingKey).
        if (!SameBoundValue(old, stored)) { Changed(); return; }
        foreach (var v in argumentVariants) MaterialGpu.ApplyArgument(v, forKey, stored, DrawnScene);
    }

    // ---- Per-instance shader arguments (Godot-only; see ShaderComposer.ShadingKey)
    /// <summary>
    /// The arguments of this geometry's own shader modifiers that are passed per mesh instance (Godot instance uniforms)
    /// instead of per material: matrices declared as mat4 in a modifier of this geometry. Geometries whose own shading
    /// differs only in these values share one Godot material, so Godot can draw equal meshes with it as one instanced
    /// draw (DirtCoating gives every robot part its own dirtToBody matrix; Marvin's 112 track shoes and 112 ribs were 224
    /// draws per pass). The matrix reaches the shader as the same 32-bit floats a material uniform holds, and the robots'
    /// captures are pixel-identical. Numbers and vectors stay material uniforms (geometries with equal values share them):
    /// passed per instance, DirtCoating's dirtHeight divisor changed a few dirty-robot pixels by one 8-bit step (the
    /// shader compiler evaluates a division by a uniform differently).
    /// </summary>
    internal IReadOnlyCollection<string> InstanceArguments
    {
        get
        {
            if (_shaderModifiers == null || arguments.Count == 0) return NoInstanceArguments;
            if (instanceArgumentsVersion == version && instanceArguments != null) return instanceArguments;
            var result = new HashSet<string>();
            foreach (var snippet in _shaderModifiers.Values)
                foreach (var (name, type) in ShaderComposer.DeclaredArguments(snippet))
                    if (type == "mat4" && arguments.TryGetValue(name, out var v) && InstanceValue(v)) result.Add(name);
            instanceArguments = result; instanceArgumentsVersion = version;
            return result;
        }
    }
    private HashSet<string> instanceArguments;
    private int instanceArgumentsVersion = -1;
    private static readonly HashSet<string> NoInstanceArguments = new();
    internal static bool InstanceValue(object v) => v is SCNMatrix4 or NSValue { value: SCNMatrix4 };
    private static bool SameBoundValue(object a, object b) =>
        ReferenceEquals(a, b) || (a is double x && b is double y && BitConverter.DoubleToInt64Bits(x) == BitConverter.DoubleToInt64Bits(y));
    /// <summary>The part of this geometry's own shading that selects its material variants (null: none of its own).</summary>
    internal ShaderComposer.ShadingKey ShadingKey
    {
        get
        {
            if (!HasOwnShading) return null;
            if (shadingKey != null && shadingKeyVersion == version) return shadingKey;
            var instance = InstanceArguments;
            var bound = new List<(string, object)>();
            foreach (var kv in arguments) if (!instance.Contains(kv.Key)) bound.Add((kv.Key, kv.Value));
            shadingKey = new ShaderComposer.ShadingKey(_shaderModifiers, instance, bound);
            shadingKeyVersion = version;
            return shadingKey;
        }
    }
    private ShaderComposer.ShadingKey shadingKey;
    private int shadingKeyVersion = -1;
    /// <summary>Sets this geometry's per-instance arguments on a Godot instance that draws it (SCNNode.RebuildMeshes).</summary>
    internal void ApplyInstanceArguments(GeometryInstance3D instance)
    {
        foreach (var name in InstanceArguments)
        {
            var value = arguments[name];
            // The columns of the Godot matrix a material uniform would get (MaterialGpu.ApplyArgument).
            var m = (value is NSValue nv ? (SCNMatrix4)nv.value : (SCNMatrix4)value).ToGodotProjection();
            instance.SetInstanceShaderParameter(ShaderNames.Of(name + "_c0"), m.X);
            instance.SetInstanceShaderParameter(ShaderNames.Of(name + "_c1"), m.Y);
            instance.SetInstanceShaderParameter(ShaderNames.Of(name + "_c2"), m.Z);
            instance.SetInstanceShaderParameter(ShaderNames.Of(name + "_c3"), m.W);
        }
    }
    public object value(string forKey) => arguments.TryGetValue(forKey, out var v) ? v : null;
    void IPropertyOwner.PropertyChanged(SCNMaterialProperty property)
    {
        // Geometries that share a variant (equal ShadingKey) hold the same property, so each binds the same contents.
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
            bool tangents = NeedsTangents;
            if (mesh != null && meshVersion == meshDataVersion && runs.SequenceEqual(meshRuns) && (meshTangents || !tangents)) return mesh;
            meshRuns = runs;
            var old = mesh;
            bool oldShared = sharedMesh != null;
            string key = SharedMeshKey(runs, tangents);
            if (key != null && sharedMeshes.TryGetValue(key, out var weak) && weak.TryGetTarget(out var entry))
            {
                // Godot-only: an equal primitive built before (see SharedMeshKey).
                sharedMesh = entry; mesh = entry.mesh; prepared = null;
                meshTangents = entry.tangents; MeshHasLods = entry.hasLods;
                surfaceElements.Clear(); surfaceElements.AddRange(entry.surfaceElements);
                if (!meshTangents && !registeredWithoutTangents) { registeredWithoutTangents = true; lock (withoutTangents) withoutTangents.Add(new WeakReference<SCNGeometry>(this)); }
            }
            else
            {
                mesh = BuildMesh(tangents);
                sharedMesh = null;
                if (key != null)
                {
                    sharedMesh = new SharedMesh { mesh = mesh, surfaceElements = surfaceElements.ToArray(), hasLods = MeshHasLods, tangents = meshTangents };
                    sharedMeshes[key] = new WeakReference<SharedMesh>(sharedMesh);
                }
            }
            meshVersion = meshDataVersion;
            // Release the replaced mesh's wrapper now instead of through the finalizer thread (per-frame batches replace
            // theirs every frame; finalizing them contended with the main thread). Mesh instances that still show it keep
            // the engine object alive until their nodes rebuild. A shared mesh's wrapper may still be used by other geometries.
            if (!oldShared && !ReferenceEquals(old, mesh)) old?.Dispose();
            return mesh;
        }
    }

    // ---- Shared meshes of equal primitives (Godot-only)
    /// <summary>
    /// Primitive geometries (SCNBox, SCNSphere, SCNCylinder, SCNPlane) with the same shape parameters build the same
    /// arrays; they share one Godot mesh, so Godot can draw equal primitives with the same material as one instanced draw
    /// (its forward renderer joins consecutive draws of the same mesh surface and material). Marvin's track belts are 112
    /// copies of one shoe box and 112 equal rib boxes. Null: not shared (plain geometry, custom bounds, SceneKit LODs).
    /// </summary>
    internal virtual string MeshShapeKey => null;
    /// <summary>A double's exact bits, for shape keys.</summary>
    protected static string K(double v) => BitConverter.DoubleToInt64Bits(v).ToString("X16");
    private string SharedMeshKey(int[] runs, bool tangents)
    {
        var shape = MeshShapeKey;
        if (shape == null || _customBounds.HasValue || (_levelsOfDetail != null && _levelsOfDetail.Length > 0)) return null;
        return $"{shape}|{string.Join(",", runs)}|{tangents}|{_godotAutomaticLevelsOfDetail}";
    }
    private sealed class SharedMesh { public ArrayMesh mesh; public int[] surfaceElements; public bool hasLods, tangents; }
    /// <summary>The shared mesh this geometry draws (keeps the table's weak entry alive while it is used).</summary>
    private SharedMesh sharedMesh;
    private static readonly Dictionary<string, WeakReference<SharedMesh>> sharedMeshes = new();
    /// <summary>
    /// Tangents feed only normal maps (the composer's TANGENT/BINORMAL), so they are generated only when a material of
    /// the geometry, or its own shader modifiers, uses them; generating them for every textured mesh cost 1.3 s when the
    /// race world was first shown and was repeated for every per-frame batch (dust, clods, trails). A geometry built
    /// without them is rebuilt when a material later needs them (see RecheckTangents).
    /// </summary>
    internal bool NeedsTangents
    {
        get
        {
            if (_shaderModifiers != null && _shaderModifiers.Values.Any(s => s != null && (s.Contains("TANGENT") || s.Contains("BINORMAL")))) return true;
            foreach (var m in _materials) if (m != null && m.NeedsTangents) return true;
            return false;
        }
    }
    private bool meshTangents;
    private bool registeredWithoutTangents;
    private static readonly List<WeakReference<SCNGeometry>> withoutTangents = new();
    private static int checkedStructureVersion = -1;
    /// <summary>Main thread, once per flush: after a structural material change, geometries whose mesh was built without
    /// tangents and whose materials now need them are marked changed, so their nodes rebuild the mesh.</summary>
    internal static void RecheckTangents()
    {
        int version = SCNMaterial.StructureVersion;
        if (version == checkedStructureVersion) return;
        checkedStructureVersion = version;
        lock (withoutTangents)
        {
            for (int i = withoutTangents.Count - 1; i >= 0; i--)
            {
                if (!withoutTangents[i].TryGetTarget(out var g)) { withoutTangents.RemoveAt(i); continue; }
                if (g.mesh != null && !g.meshTangents && g.NeedsTangents) g.Changed();
            }
        }
    }
    internal bool HasNormals => sources.Any(s => s.semantic == SCNGeometrySourceSemantic.normal);
    internal bool HasColors => sources.Any(s => s.semantic == SCNGeometrySourceSemantic.color);
    internal int SurfaceCount => mesh?.GetSurfaceCount() ?? 0;
    /// <summary>The current Godot mesh has levels of detail (godotAutomaticLevelsOfDetail) on at least one surface.</summary>
    internal bool MeshHasLods { get; private set; }
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

    /// <summary>
    /// Godot's mesh LODs for one surface (ImporterMesh.GenerateLods, as for imported scenes: vertices merged within 60
    /// degrees of normal for the simplifier, normals weighted in its error). The levels index the surface's own vertices,
    /// so the full-detail level is the surface unchanged. Any thread.
    /// </summary>
    private static void GenerateLods(PreparedSurface s)
    {
        using var importer = new ImporterMesh();
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = s.vertices;
        arrays[(int)Mesh.ArrayType.Normal] = s.normals;
        if (s.uv0 != null) arrays[(int)Mesh.ArrayType.TexUV] = s.uv0;
        if (s.uv1 != null) arrays[(int)Mesh.ArrayType.TexUV2] = s.uv1;
        arrays[(int)Mesh.ArrayType.Index] = s.indices;
        importer.AddSurface(Mesh.PrimitiveType.Triangles, arrays);
        using var noBones = new Godot.Collections.Array();
        importer.GenerateLods(60, 0, noBones);
        int count = importer.GetSurfaceLodCount(0);
        if (count == 0) return;
        s.lodSizes = new float[count]; s.lodIndices = new int[count][];
        for (int i = 0; i < count; i++) { s.lodSizes[i] = importer.GetSurfaceLodSize(0, i); s.lodIndices[i] = importer.GetSurfaceLodIndices(0, i); }
    }

    /// <summary>Godot meshes built so far (diagnostics).</summary>
    internal static int MeshesBuilt;

    /// <summary>The CPU half of a mesh build: one entry per Godot surface (see BuildMesh).</summary>
    private sealed class PreparedMesh
    {
        internal int dataVersion; internal int[] runs; internal bool tangents;
        internal readonly List<(Godot.Collections.Array arrays, Mesh.ArrayFormat flags, int element)> surfaces = new();
        internal List<PreparedSurface> pending = new();
    }
    private sealed class PreparedSurface
    {
        internal Vector3[] vertices, normals; internal float[] tangents, colors; internal Vector2[] uv0, uv1;
        internal float[][] custom; internal int[] indices; internal Mesh.ArrayFormat flags; internal int element;
        /// <summary>Godot mesh LODs (godotAutomaticLevelsOfDetail): screen-space error of each level and its indices.</summary>
        internal float[] lodSizes; internal int[][] lodIndices;
    }
    private volatile PreparedMesh prepared;

    /// <summary>The mesh still has to be (re)built for the current data, material runs and tangent needs.</summary>
    internal bool NeedsMeshBuild => mesh == null || meshVersion != meshDataVersion || !MaterialRuns().SequenceEqual(meshRuns) || (!meshTangents && NeedsTangents);

    /// <summary>
    /// Any thread (the facade prepares many geometries in parallel when a large scene is flushed, see
    /// SceneKitRuntime.PrepareMeshes): computes the vertex and index arrays of every surface, as BuildMesh would.
    /// Call EnsureBuilt() on the main thread first. The main thread then only hands the arrays to Godot.
    /// </summary>
    internal void PrepareMesh()
    {
        var runs = MaterialRuns();
        bool tangents = NeedsTangents;
        prepared = Prepare(runs, tangents);
    }
    /// <summary>Building the mesh now would only hand prepared arrays to Godot (or nothing is to be built).</summary>
    internal bool MeshReady
    {
        get
        {
            if (!NeedsMeshBuild) return true;
            var ready = prepared;
            return ready != null && ready.dataVersion == meshDataVersion && ready.runs.SequenceEqual(MaterialRuns()) && (ready.tangents || !NeedsTangents);
        }
    }

    private PreparedMesh Prepare(int[] runStarts, bool needsTangents)
    {
        var result = new PreparedMesh { dataVersion = meshDataVersion, runs = runStarts, tangents = true };
        // The first source of each semantic, and up to eight texture coordinate channels in order.
        SCNGeometrySource pos = null, nrm = null, tan = null, col = null;
        var texcoords = new List<SCNGeometrySource>(2);
        foreach (var source in _sources)
            switch (source.semantic)
            {
                case SCNGeometrySourceSemantic.vertex: pos ??= source; break;
                case SCNGeometrySourceSemantic.normal: nrm ??= source; break;
                case SCNGeometrySourceSemantic.tangent: tan ??= source; break;
                case SCNGeometrySourceSemantic.color: col ??= source; break;
                case SCNGeometrySourceSemantic.texcoord: if (texcoords.Count < 8) texcoords.Add(source); break;
            }
        if (pos == null || pos.vectorCount == 0) return result;
        int n = pos.vectorCount;
        var tcs = texcoords;

        var P = new Vector3[n];
        pos.ReadV3(P, n);
        // Without a normal source SceneKit shades flat face normals (measured); the composer derives them per pixel
        // (VariantFlags.NoNormals). The +Z placeholder only feeds tangent generation and .geometry modifiers.
        var N = new Vector3[n];
        if (nrm != null) nrm.ReadV3(N, Math.Min(n, nrm.vectorCount));
        else for (int i = 0; i < n; i++) N[i] = new Vector3(0, 0, 1);
        Color[] C = null;
        if (col != null)
        {
            C = new Color[n];
            int known = Math.Min(n, col.vectorCount);
            col.ReadC4(C, known);
            for (int i = known; i < n; i++) C[i] = new Color(1, 1, 1, 1);
        }
        var UV = new Vector2[tcs.Count][];
        for (int t = 0; t < tcs.Count; t++) { UV[t] = new Vector2[n]; tcs[t].ReadV2(UV[t], Math.Min(n, tcs[t].vectorCount)); }

        // All triangle lists (SceneKit CCW order) per element.
        var elementLists = new int[_elements.Length][];
        for (int e = 0; e < _elements.Length; e++) elementLists[e] = _elements[e].TriangleList();
        float[] T = null;
        if (tan != null)
        {
            T = new float[n * 4];
            for (int i = 0; i < n; i++) { var v = tan.V3(i); T[i * 4] = v.X; T[i * 4 + 1] = v.Y; T[i * 4 + 2] = v.Z; T[i * 4 + 3] = tan.componentsPerVector > 3 ? (float)tan.Component(i, 3) : 1; }
        }
        else if (UV.Length > 0 && needsTangents) T = GenerateTangents(P, N, UV[0], elementLists);
        result.tangents = T != null || UV.Length == 0;
        // One surface per run of elements sharing a material (see MaterialRuns), triangles in element order.
        var lists = new int[runStarts.Length][];
        for (int r = 0; r < runStarts.Length; r++)
        {
            int end = r + 1 < runStarts.Length ? runStarts[r + 1] : elementLists.Length;
            if (end - runStarts[r] == 1 && elementLists[runStarts[r]].Length % 3 == 0) { lists[r] = elementLists[runStarts[r]]; continue; }
            int total = 0;
            for (int e = runStarts[r]; e < end; e++) total += elementLists[e].Length - elementLists[e].Length % 3;
            var merged = new int[total];
            int at = 0;
            for (int e = runStarts[r]; e < end; e++) { var t = elementLists[e]; int k = t.Length - t.Length % 3; Array.Copy(t, 0, merged, at, k); at += k; }
            lists[r] = merged;
        }

        // remap[source vertex] = surface vertex + 1 (0: not used yet); reset after each surface.
        var remap = new int[n];
        for (int e = 0; e < lists.Length; e++)
        {
            var tri = lists[e];
            if (tri.Length < 3) continue;
            // Compact to the vertices this element uses (bounded memory for multi-element geometry), in first-use order.
            var idx = new int[tri.Length - tri.Length % 3];
            var order = new int[Math.Min(idx.Length, n)];
            int m = 0;
            for (int i = 0; i < idx.Length; i++)
            {
                int src = tri[i];
                if (src < 0 || src >= n) src = 0;
                int dst = remap[src] - 1;
                if (dst < 0) { dst = m++; remap[src] = dst + 1; order[dst] = src; }
                idx[i] = dst;
            }
            for (int i = 0; i < m; i++) remap[order[i]] = 0;
            // SceneKit front faces are counter-clockwise, Godot's clockwise: swap 2nd and 3rd index.
            for (int i = 0; i + 2 < idx.Length; i += 3) (idx[i + 1], idx[i + 2]) = (idx[i + 2], idx[i + 1]);
            var s = new PreparedSurface { indices = idx, element = runStarts[e], flags = Mesh.ArrayFormat.FormatVertex };
            s.vertices = new Vector3[m];
            for (int i = 0; i < m; i++) s.vertices[i] = P[order[i]];
            s.normals = new Vector3[m]; for (int i = 0; i < m; i++) s.normals[i] = N[order[i]];
            if (T != null) { s.tangents = new float[m * 4]; for (int i = 0; i < m; i++) for (int k = 0; k < 4; k++) s.tangents[i * 4 + k] = T[order[i] * 4 + k]; }
            // Vertex colours go to CUSTOM3 as RGBA floats: Godot's COLOR attribute is 8-bit unorm, and the
            // game stores data there (trail birth times > 1, signed slopes). The composer copies CUSTOM3 to COLOR.
            if (C != null)
            {
                s.colors = new float[m * 4];
                for (int i = 0; i < m; i++) { var c = C[order[i]]; s.colors[i * 4] = c.R; s.colors[i * 4 + 1] = c.G; s.colors[i * 4 + 2] = c.B; s.colors[i * 4 + 3] = c.A; }
            }
            if (UV.Length > 0) { s.uv0 = new Vector2[m]; for (int i = 0; i < m; i++) s.uv0[i] = UV[0][order[i]]; }
            if (UV.Length > 1) { s.uv1 = new Vector2[m]; for (int i = 0; i < m; i++) s.uv1[i] = UV[1][order[i]]; }
            s.custom = new float[3][];
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
                s.custom[c] = custom;
                s.flags |= (Mesh.ArrayFormat)((long)Mesh.ArrayCustomFormat.RgbaFloat << ((int)Mesh.ArrayFormat.FormatCustom0Shift + c * (int)Mesh.ArrayFormat.FormatCustomBits));
            }
            if (C != null)
                s.flags |= (Mesh.ArrayFormat)((long)Mesh.ArrayCustomFormat.RgbaFloat << ((int)Mesh.ArrayFormat.FormatCustom0Shift + 3 * (int)Mesh.ArrayFormat.FormatCustomBits));
            if (_godotAutomaticLevelsOfDetail && C == null && (_levelsOfDetail == null || _levelsOfDetail.Length == 0) && idx.Length / 3 >= AutomaticLodMinTriangles)
                GenerateLods(s);
            result.pending.Add(s);
        }
        return result;
    }

    /// <summary>Empty blend shapes and LODs for AddSurfaceFromArrays (main thread): passing null makes the binding create
    /// a new Array and Dictionary per surface, which the finalizer thread then had to release.</summary>
    private static readonly Godot.Collections.Array<Godot.Collections.Array> NoBlendShapes = new();
    private static readonly Godot.Collections.Dictionary NoLods = new();
    /// <summary>Stores a packed array in a mesh array slot and releases the temporary Variant at once (the array keeps
    /// its own copy; undisposed Variants of packed arrays waited for the finalizer thread).</summary>
    private static void Put(Godot.Collections.Array arrays, int slot, Variant value)
    {
        arrays[slot] = value;
        value.Dispose();
    }

    /// <summary>Builds the Godot mesh: the prepared arrays when PrepareMesh ran for this state, else prepared now.</summary>
    private ArrayMesh BuildMesh(bool needsTangents = true)
    {
        MeshesBuilt++;
        var ready = prepared; prepared = null;
        if (ready == null || ready.dataVersion != meshDataVersion || !ready.runs.SequenceEqual(meshRuns) || (needsTangents && !ready.tangents))
            ready = Prepare(meshRuns.Length > 0 ? meshRuns : MaterialRuns(), needsTangents);
        meshTangents = ready.tangents;
        if (!meshTangents && !registeredWithoutTangents) { registeredWithoutTangents = true; lock (withoutTangents) withoutTangents.Add(new WeakReference<SCNGeometry>(this)); }
        var result = new ArrayMesh();
        surfaceElements.Clear();
        MeshHasLods = ready.pending.Any(p => p.lodIndices != null);
        foreach (var s in ready.pending)
        {
            using var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            Put(arrays, (int)Mesh.ArrayType.Vertex, s.vertices);
            Put(arrays, (int)Mesh.ArrayType.Normal, s.normals);
            if (s.tangents != null) Put(arrays, (int)Mesh.ArrayType.Tangent, s.tangents);
            if (s.colors != null) Put(arrays, (int)Mesh.ArrayType.Custom3, s.colors);
            if (s.uv0 != null) Put(arrays, (int)Mesh.ArrayType.TexUV, s.uv0);
            if (s.uv1 != null) Put(arrays, (int)Mesh.ArrayType.TexUV2, s.uv1);
            for (int c = 0; c < 3; c++) if (s.custom[c] != null) Put(arrays, (int)Mesh.ArrayType.Custom0 + c, s.custom[c]);
            Put(arrays, (int)Mesh.ArrayType.Index, s.indices);
            if (s.lodIndices != null)
            {
                using var lods = new Godot.Collections.Dictionary();
                for (int i = 0; i < s.lodIndices.Length; i++) lods[s.lodSizes[i]] = s.lodIndices[i];
                result.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, NoBlendShapes, lods, s.flags);
            }
            else result.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, NoBlendShapes, NoLods, s.flags);
            surfaceElements.Add(s.element);
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
