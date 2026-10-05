using System;
using System.Collections.Generic;
using Godot;

namespace Marvin.SceneKit;

public enum SCNWrapMode { clamp = 1, repeat = 2, clampToBorder = 3, mirror = 4 }
public enum SCNFilterMode { none = 0, nearest = 1, linear = 2 }
[Flags]
public enum SCNColorMask { none = 0, red = 8, green = 4, blue = 2, alpha = 1, all = 15 }
public enum SCNTransparencyMode { aOne = 0, rgbZero = 1, singleLayer = 2, dualLayer = 3, @default = 0 }
public enum SCNBlendMode { alpha = 0, add = 1, subtract = 2, multiply = 3, screen = 4, replace = 5, max = 6 }
public enum SCNCullMode { back = 0, front = 1 }
/// <summary>SCNShaderModifierEntryPoint. Snippets are GODOT SHADING LANGUAGE (see PORTING.md, "SceneKit facade").</summary>
public enum SCNShaderModifierEntryPoint { geometry, surface, lightingModel, fragment }

/// <summary>
/// One material slot. Contents may be NSColor, a number (double/float/int),
/// NSImage, MTLTexture, a res:// or file path string, or null.
/// </summary>
public sealed class SCNMaterialProperty
{
    private object _contents;
    private double _intensity = 1;
    private SCNMatrix4 _contentsTransform = SCNMatrix4.Identity;
    private SCNWrapMode _wrapS = SCNWrapMode.clamp, _wrapT = SCNWrapMode.clamp;
    private SCNFilterMode _min = SCNFilterMode.linear, _mag = SCNFilterMode.linear, _mip = SCNFilterMode.nearest;
    private double _maxAnisotropy = double.MaxValue;
    private int _mappingChannel;
    private SCNColorMask _textureComponents = SCNColorMask.all;
    internal readonly List<WeakReference<IPropertyOwner>> owners = new();

    public SCNMaterialProperty() { }
    public SCNMaterialProperty(object contents) { _contents = Normalize(contents); }
    internal SCNMaterialProperty(object contents, IPropertyOwner owner) { _contents = contents; AddOwner(owner); }

    public object contents { get => _contents; set { _contents = Normalize(value); Changed(); } }
    public double intensity { get => _intensity; set { _intensity = value; Changed(); } }
    public SCNMatrix4 contentsTransform { get => _contentsTransform; set { _contentsTransform = value; Changed(); } }
    public SCNWrapMode wrapS { get => _wrapS; set { _wrapS = value; Changed(); } }
    public SCNWrapMode wrapT { get => _wrapT; set { _wrapT = value; Changed(); } }
    public SCNFilterMode minificationFilter { get => _min; set { _min = value; Changed(); } }
    public SCNFilterMode magnificationFilter { get => _mag; set { _mag = value; Changed(); } }
    public SCNFilterMode mipFilter { get => _mip; set { _mip = value; Changed(); } }
    public double maxAnisotropy { get => _maxAnisotropy; set { _maxAnisotropy = value; Changed(); } }
    public int mappingChannel { get => _mappingChannel; set { _mappingChannel = value; Changed(); } }
    public SCNColorMask textureComponents { get => _textureComponents; set { _textureComponents = value; Changed(); } }
    public NSColor borderColor { get; set; }

    private static object Normalize(object v) => v switch
    {
        float f => (double)f,
        int i => (double)i,
        long l => (double)l,
        _ => v,
    };
    internal void AddOwner(IPropertyOwner owner)
    {
        foreach (var w in owners) if (w.TryGetTarget(out var o) && ReferenceEquals(o, owner)) return;
        owners.Add(new WeakReference<IPropertyOwner>(owner));
    }
    private void Changed()
    {
        for (int i = owners.Count - 1; i >= 0; i--)
        {
            if (owners[i].TryGetTarget(out var o)) o.PropertyChanged(this);
            else owners.RemoveAt(i);
        }
    }
    internal SCNMaterialProperty CopyFor(IPropertyOwner owner)
    {
        var p = new SCNMaterialProperty(_contents, owner)
        {
            _intensity = _intensity, _contentsTransform = _contentsTransform, _wrapS = _wrapS, _wrapT = _wrapT,
            _min = _min, _mag = _mag, _mip = _mip, _maxAnisotropy = _maxAnisotropy, _mappingChannel = _mappingChannel,
            _textureComponents = _textureComponents, borderColor = borderColor,
        };
        return p;
    }

    // ---- shader-modifier argument binding (setValue(_:forKey:) with this property)
    // Measured in SceneKit (macOS 27; tools/scenekit-reference/robots/ArgumentBinding.swift): a shader program resolves
    // the property's contents when it first draws with it. When the last setValue(_:forKey:) that received the property
    // was on a primitive geometry (SCNBox, SCNPlane, SCNSphere, SCNCylinder, SCNCone, SCNFloor, SCNShape, ...), later
    // `contents` changes never reach a program that has drawn with it: every geometry keeps the contents of its first
    // draw (in-place MTLTexture writes still show) until a different program draws it (moving to a scene with another
    // lighting setup resolves the contents again; a fresh renderer or re-adding to the same scene does not). After a
    // setValue on a material or on a custom SCNGeometry, changes reach every program. The facade keys programs by scene.
    // (DirtCoating: Marvin's and R2-D2's last coated part is an SCNBox spoke, so their dirt never appears once drawn.)
    private WeakReference<object> argumentOwner;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SCNScene, Box> argumentBindings = new();
    private sealed class Box { public object contents; }
    internal void ArgumentOwner(object owner) => argumentOwner = new WeakReference<object>(owner);
    private bool ArgumentFrozen => argumentOwner != null && argumentOwner.TryGetTarget(out var o) && o is SCNGeometry g && g.GetType() != typeof(SCNGeometry);
    /// <summary>The contents a shader argument draws with in <paramref name="scene"/> (null: detached, current contents).
    /// <paramref name="draw"/>: called from the per-frame flush, i.e. the scene draws it now, which creates the binding.</summary>
    internal object ArgumentContents(SCNScene scene, bool draw)
    {
        if (scene == null) return _contents;
        if (argumentBindings.TryGetValue(scene, out var bound) && ArgumentFrozen) return bound.contents;
        if (draw) argumentBindings.AddOrUpdate(scene, new Box { contents = _contents });
        return _contents;
    }

    // ---- facade internals
    internal enum ContentKind { None, Color, Scalar, Texture }
    internal ContentKind Kind => KindOf(_contents);
    internal static ContentKind KindOf(object contents) => contents switch
    {
        null => ContentKind.None,
        NSColor => ContentKind.Color,
        double => ContentKind.Scalar,
        NSImage or MTLTexture or string or Texture2D => ContentKind.Texture,
        _ => ContentKind.None,
    };
    internal Texture2D Texture => TextureOf(_contents);
    internal static Texture2D TextureOf(object contents) => contents switch
    {
        NSImage img => img.GodotTexture,
        MTLTexture t => t.GodotTexture,
        string path => PathTexture(path),
        Texture2D t => t,
        _ => null,
    };
    /// <summary>
    /// A path's texture, loaded once: materials name the same image files many times (city scans, clay), and every
    /// material flush resolves its contents (ResourceLoader.Load and a CPU read-back of the image to test for grayscale
    /// cost 2.7 s when the race world was first shown). Image files do not change while the game runs.
    /// </summary>
    private static Texture2D PathTexture(string path)
    {
        lock (pathTextures)
        {
            if (pathTextures.TryGetValue(path, out var cached)) return cached;
        }
        var texture = NSImage.contentsOf(path)?.GodotTexture;
        lock (pathTextures) { pathTextures[path] = texture; }
        return texture;
    }
    private static readonly Dictionary<string, Texture2D> pathTextures = new();
    /// <summary>Linear RGBA for colour/scalar contents (scalars are linear values: measured).</summary>
    internal Color LinearColor(Color fallback) => LinearColorOf(_contents, fallback);
    internal static Color LinearColorOf(object contents, Color fallback) => contents switch
    {
        NSColor c => c.GodotLinear,
        double d => new Color((float)d, (float)d, (float)d, 1),
        _ => fallback,
    };
    internal bool TextureHasTranslucency => _contents is NSImage img && img.HasTranslucency;
    /// <summary>Sampler hint string for the Godot shader uniform of this property.</summary>
    internal string SamplerHints(bool color)
    {
        var hints = new List<string>();
        if (color) hints.Add("source_color");
        bool linear = _min == SCNFilterMode.linear || _mag == SCNFilterMode.linear;
        bool mip = _mip != SCNFilterMode.none && !(_contents is MTLTexture);
        bool aniso = mip && _maxAnisotropy > 1.5;
        string f = linear ? "filter_linear" : "filter_nearest";
        if (mip) f += "_mipmap";
        if (aniso) f += "_anisotropic";
        hints.Add(f);
        hints.Add(_wrapS == SCNWrapMode.repeat || _wrapS == SCNWrapMode.mirror ? "repeat_enable" : "repeat_disable");
        return string.Join(", ", hints);
    }
    internal string appliedConfigKey;
    internal string ConfigKey => $"{Kind}:{(Kind == ContentKind.Texture ? $"{_mappingChannel}{_wrapS}{_wrapT}{_min}{_mag}{_mip}{(_maxAnisotropy > 1.5 ? 1 : 0)}{(int)_textureComponents}{(_contentsTransform.IsIdentity ? 0 : 1)}{(TextureHasTranslucency ? 1 : 0)}" : _contents is NSColor c && c.alphaComponent < 1 ? "translucent" : "")}";
}

internal interface IPropertyOwner { void PropertyChanged(SCNMaterialProperty property); }

/// <summary>SCNMaterial. Compiled into Godot ShaderMaterials by <see cref="ShaderComposer"/>.</summary>
public sealed class SCNMaterial : IPropertyOwner
{
    /// <summary>SCNMaterial.LightingModel.</summary>
    public enum LightingModel { phong, blinn, lambert, constant, physicallyBased, shadowOnly }

    public string name;
    public readonly SCNMaterialProperty diffuse, ambient, specular, reflective, emission, transparent, multiply, normal,
        displacement, ambientOcclusion, selfIllumination, metalness, roughness, clearCoat, clearCoatRoughness, clearCoatNormal;
    private LightingModel _lightingModel = LightingModel.blinn;
    private bool _isDoubleSided, _writesToDepthBuffer = true, _readsFromDepthBuffer = true, _locksAmbientWithDiffuse = true, _isLitPerPixel = true;
    private SCNCullMode _cullMode = SCNCullMode.back;
    private double _transparency = 1, _shininess = 1, _fresnelExponent;
    private SCNTransparencyMode _transparencyMode = SCNTransparencyMode.aOne;
    private SCNBlendMode _blendMode = SCNBlendMode.alpha;
    private SCNColorMask _colorBufferWriteMask = SCNColorMask.all;
    private Dictionary<SCNShaderModifierEntryPoint, string> _shaderModifiers;
    internal readonly Dictionary<string, object> arguments = new();
    internal readonly MaterialGpu gpu;

    public SCNMaterial()
    {
        // Defaults measured on macOS (SCNMaterial()).
        diffuse = new SCNMaterialProperty(NSColor.white, this);
        ambient = new SCNMaterialProperty(NSColor.srgbRed(0.484529, 0.484529, 0.484529, 1), this);
        specular = new SCNMaterialProperty(NSColor.black, this);
        reflective = new SCNMaterialProperty(null, this);
        emission = new SCNMaterialProperty(NSColor.black, this);
        transparent = new SCNMaterialProperty(NSColor.white, this);
        multiply = new SCNMaterialProperty(NSColor.white, this);
        normal = new SCNMaterialProperty(null, this);
        displacement = new SCNMaterialProperty(null, this);
        ambientOcclusion = new SCNMaterialProperty(NSColor.white, this);
        selfIllumination = new SCNMaterialProperty(NSColor.black, this);
        metalness = new SCNMaterialProperty(NSColor.black, this);
        roughness = new SCNMaterialProperty(NSColor.srgbRed(0.484529, 0.484529, 0.484529, 1), this);
        clearCoat = new SCNMaterialProperty(null, this);
        clearCoatRoughness = new SCNMaterialProperty(null, this);
        clearCoatNormal = new SCNMaterialProperty(null, this);
        gpu = new MaterialGpu(this);
    }
    private SCNMaterial(SCNMaterial o)
    {
        name = o.name;
        diffuse = o.diffuse.CopyFor(this); ambient = o.ambient.CopyFor(this); specular = o.specular.CopyFor(this);
        reflective = o.reflective.CopyFor(this); emission = o.emission.CopyFor(this); transparent = o.transparent.CopyFor(this);
        multiply = o.multiply.CopyFor(this); normal = o.normal.CopyFor(this); displacement = o.displacement.CopyFor(this);
        ambientOcclusion = o.ambientOcclusion.CopyFor(this); selfIllumination = o.selfIllumination.CopyFor(this);
        metalness = o.metalness.CopyFor(this); roughness = o.roughness.CopyFor(this);
        clearCoat = o.clearCoat.CopyFor(this); clearCoatRoughness = o.clearCoatRoughness.CopyFor(this); clearCoatNormal = o.clearCoatNormal.CopyFor(this);
        _lightingModel = o._lightingModel; _isDoubleSided = o._isDoubleSided; _writesToDepthBuffer = o._writesToDepthBuffer;
        _readsFromDepthBuffer = o._readsFromDepthBuffer; _locksAmbientWithDiffuse = o._locksAmbientWithDiffuse; _isLitPerPixel = o._isLitPerPixel;
        _cullMode = o._cullMode; _transparency = o._transparency; _shininess = o._shininess; _fresnelExponent = o._fresnelExponent;
        _transparencyMode = o._transparencyMode; _blendMode = o._blendMode; _colorBufferWriteMask = o._colorBufferWriteMask;
        _shaderModifiers = o._shaderModifiers == null ? null : new Dictionary<SCNShaderModifierEntryPoint, string>(o._shaderModifiers);
        foreach (var kv in o.arguments)
        {
            arguments[kv.Key] = kv.Value;
            if (kv.Value is SCNMaterialProperty p) p.AddOwner(this);
        }
        gpu = new MaterialGpu(this);
    }
    /// <summary>copy() (NSCopying). Swift: `m.copy() as! SCNMaterial`.</summary>
    public SCNMaterial copy() => new(this);

    public LightingModel lightingModel { get => _lightingModel; set { _lightingModel = value; Changed(true); } }
    public bool isDoubleSided { get => _isDoubleSided; set { _isDoubleSided = value; Changed(true); } }
    public SCNCullMode cullMode { get => _cullMode; set { _cullMode = value; Changed(true); } }
    public double transparency { get => _transparency; set { _transparency = value; Changed(true); } }
    public SCNTransparencyMode transparencyMode { get => _transparencyMode; set { _transparencyMode = value; Changed(true); } }
    public SCNBlendMode blendMode { get => _blendMode; set { _blendMode = value; Changed(true); } }
    public bool writesToDepthBuffer { get => _writesToDepthBuffer; set { _writesToDepthBuffer = value; Changed(true); } }
    public bool readsFromDepthBuffer { get => _readsFromDepthBuffer; set { _readsFromDepthBuffer = value; Changed(true); } }
    public SCNColorMask colorBufferWriteMask { get => _colorBufferWriteMask; set { _colorBufferWriteMask = value; Changed(true); } }
    public double shininess { get => _shininess; set { _shininess = value; Changed(false); } }
    public double fresnelExponent { get => _fresnelExponent; set { _fresnelExponent = value; Changed(false); } }
    public bool locksAmbientWithDiffuse { get => _locksAmbientWithDiffuse; set { _locksAmbientWithDiffuse = value; Changed(false); } }
    public bool isLitPerPixel { get => _isLitPerPixel; set { _isLitPerPixel = value; Changed(false); } }
    /// <summary>shaderModifiers: entry point -> Godot shading language snippet (see PORTING.md).</summary>
    public Dictionary<SCNShaderModifierEntryPoint, string> shaderModifiers
    {
        get => _shaderModifiers;
        set { _shaderModifiers = value == null ? null : new Dictionary<SCNShaderModifierEntryPoint, string>(value); Changed(true); }
    }
    /// <summary>setValue(_:forKey:) - a shader-modifier argument (#pragma arguments).</summary>
    public void setValue(object value, string forKey)
    {
        if (value is SCNMaterialProperty p) { p.AddOwner(this); p.ArgumentOwner(this); }
        arguments[forKey] = value is float f ? (double)f : value;
        gpu.ArgumentChanged(forKey);
    }
    public object value(string forKey) => arguments.TryGetValue(forKey, out var v) ? v : null;

    void IPropertyOwner.PropertyChanged(SCNMaterialProperty property)
    {
        if (arguments.ContainsValue(property)) foreach (var kv in arguments) if (ReferenceEquals(kv.Value, property)) gpu.ArgumentChanged(kv.Key);
        // Only a change of content kind/sampling recomposes the shader; colours and numbers are uniform updates.
        var key = property.ConfigKey;
        bool structural = key != property.appliedConfigKey;
        property.appliedConfigKey = key;
        Changed(structural);
    }
    private void Changed(bool structural) => gpu.MarkDirty(structural);

    internal IEnumerable<(string slot, SCNMaterialProperty property)> Slots()
    {
        yield return ("diffuse", diffuse); yield return ("ambient", ambient); yield return ("specular", specular);
        yield return ("emission", emission); yield return ("transparent", transparent); yield return ("multiply", multiply);
        yield return ("normal", normal); yield return ("ambientOcclusion", ambientOcclusion); yield return ("selfIllumination", selfIllumination);
        yield return ("metalness", metalness); yield return ("roughness", roughness);
    }
}
