using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Godot;

namespace Marvin.SceneKit;

/// <summary>
/// Compiles an SCNMaterial (+ the geometry's own shader modifiers) into Godot
/// shader code that reproduces SceneKit's material pipeline:
///   _surface preparation (diffuse x vertex colour, premultiplied by alpha) ->
///   .surface modifier -> lighting -> multiply -> linear distance fog ->
///   .fragment modifier -> blending.
/// Godot evaluates lighting after fragment(), so SceneKit's post-lighting stage is
/// emulated (see PORTING.md, "SceneKit facade"). All numbers were calibrated
/// against SceneKit renders (tools/scenekit-reference).
/// </summary>
internal static class ShaderComposer
{
    // Calibration constants (SceneKit units -> Godot) live in SceneKitCalibration.

    private static readonly Dictionary<string, Shader> cache = new();
    internal static int CompiledShaders => cache.Count;

    internal static Shader GetShader(string code)
    {
        if (cache.TryGetValue(code, out var s)) return s;
        s = new Shader { Code = code };
        cache[code] = s;
        return s;
    }

    /// <summary>Uniform bindings of one composed shader.</summary>
    internal sealed class Plan
    {
        public string code;
        public readonly List<(string uniform, SCNMaterialProperty property, string what)> properties = new();
        public readonly HashSet<string> arguments = new();
        public bool transparent;
    }

    [Flags]
    internal enum VariantFlags { None = 0, NoNormals = 1, Background = 2, VertexColors = 4 }

    /// <summary>A parsed shader modifier snippet.</summary>
    private sealed class Snippet
    {
        public readonly List<string> arguments = new(), varyings = new(), declarations = new();
        public string body = "";
        public bool transparent, opaque;
    }

    private static Snippet Parse(string source)
    {
        var s = new Snippet();
        if (string.IsNullOrWhiteSpace(source)) return s;
        if (!source.Contains("#pragma")) { s.body = source; return s; }
        string section = "declaration";
        var body = new StringBuilder();
        var decl = new StringBuilder();
        foreach (var raw in source.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("#pragma"))
            {
                var p = line.Substring(7).Trim();
                if (p == "transparent") { s.transparent = true; continue; }
                if (p == "opaque") { s.opaque = true; continue; }
                section = p switch { "arguments" => "arguments", "varyings" => "varyings", "declaration" => "declaration", "body" => "body", _ => section };
                continue;
            }
            switch (section)
            {
                case "arguments": if (line.Length > 0 && !line.StartsWith("//")) s.arguments.Add(line.TrimEnd(';').Trim()); break;
                case "varyings": if (line.Length > 0 && !line.StartsWith("//")) s.varyings.Add(line.TrimEnd(';').Trim()); break;
                case "declaration": decl.AppendLine(raw); break;
                default: body.AppendLine(raw); break;
            }
        }
        if (decl.ToString().Trim().Length > 0) s.declarations.Add(decl.ToString());
        s.body = body.ToString();
        return s;
    }

    private static string ArgName(string decl)
    {
        // "float crowdTime", "sampler2D duneHeights : filter_nearest", "vec3 sunA"
        var head = decl.Split(':')[0].Trim();
        var parts = head.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[^1].Split('[')[0] : head;
    }

    internal static Plan Compose(SCNMaterial m, SCNGeometry geometry, VariantFlags flags)
    {
        var plan = new Plan();
        var mods = new Dictionary<SCNShaderModifierEntryPoint, List<Snippet>>();
        foreach (SCNShaderModifierEntryPoint ep in Enum.GetValues(typeof(SCNShaderModifierEntryPoint))) mods[ep] = new List<Snippet>();
        // SceneKit applies the geometry's modifiers, then the material's.
        if (geometry?.shaderModifiers != null) foreach (var kv in geometry.shaderModifiers) mods[kv.Key].Add(Parse(kv.Value));
        if (m.shaderModifiers != null) foreach (var kv in m.shaderModifiers) mods[kv.Key].Add(Parse(kv.Value));
        var all = mods.Values.SelectMany(x => x).ToList();

        var model = m.lightingModel;
        bool constant = model == SCNMaterial.LightingModel.constant;
        bool pbr = model == SCNMaterial.LightingModel.physicallyBased;
        bool depthOnly = m.colorBufferWriteMask == SCNColorMask.none;
        bool noNormals = (flags & VariantFlags.NoNormals) != 0;
        bool background = (flags & VariantFlags.Background) != 0;

        string fragmentBody = string.Join("\n", mods[SCNShaderModifierEntryPoint.fragment].Select(x => x.body));
        bool fragmentWritesAlpha = Regex.IsMatch(fragmentBody, @"\bALPHA\s*[\*\+\-/]?=(?!=)");
        bool transparent = !depthOnly && (all.Any(x => x.transparent)
            || (m.transparency < 1 && !IgnoresTransparency(m))
            || (m.diffuse.contents is NSColor dc && dc.alphaComponent < 1)
            || m.diffuse.TextureHasTranslucency
            || m.blendMode is SCNBlendMode.add or SCNBlendMode.subtract or SCNBlendMode.multiply or SCNBlendMode.screen or SCNBlendMode.max);
        plan.transparent = transparent;

        var sb = new StringBuilder();
        sb.AppendLine("// Generated by Marvin.SceneKit.ShaderComposer from an SCNMaterial.");
        sb.AppendLine("shader_type spatial;");
        var rm = new List<string>();
        if (depthOnly) rm.Add("blend_mul");
        else rm.Add(m.blendMode switch
        {
            SCNBlendMode.add => "blend_add",
            SCNBlendMode.subtract => "blend_sub",
            SCNBlendMode.multiply => "blend_mul",
            _ => "blend_mix",
        });
        if (depthOnly) rm.Add("depth_draw_always");
        else if (!m.writesToDepthBuffer) rm.Add("depth_draw_never");
        else rm.Add(transparent ? "depth_draw_always" : "depth_draw_opaque");
        if (!m.readsFromDepthBuffer && !background) rm.Add("depth_test_disabled");
        rm.Add(m.isDoubleSided ? "cull_disabled" : m.cullMode == SCNCullMode.front ? "cull_front" : "cull_back");
        // SceneKit casts shadows from transparent geometry as if opaque. Godot only puts alpha materials in the shadow pass
        // with a depth prepass (shadow pass threshold: alpha 0.1); depth_draw_never materials cannot cast (the game turns
        // castsShadow off for all of them).
        if (transparent && !depthOnly && m.writesToDepthBuffer && m.readsFromDepthBuffer) rm.Add("depth_prepass_alpha");
        if (depthOnly) { rm.Add("unshaded"); rm.Add("fog_disabled"); }
        else
        {
            rm.Add("diffuse_lambert");
            bool specular = pbr || (model is SCNMaterial.LightingModel.blinn or SCNMaterial.LightingModel.phong && !IsBlack(m.specular));
            rm.Add(specular && !constant ? "specular_schlick_ggx" : "specular_disabled");
            rm.Add("specular_occlusion_disabled"); // Godot 4.4+ dims IBL by ambient luminance; SceneKit does not
        }
        sb.AppendLine("render_mode " + string.Join(", ", rm) + ";");
        sb.AppendLine();
        sb.AppendLine("// Scene state (SCNScene fog, ambient lights, lightingEnvironment), set per frame by the facade.");
        sb.AppendLine("global uniform vec4 scn_fog_color;");
        sb.AppendLine("global uniform vec4 scn_fog_range;");
        sb.AppendLine("global uniform vec4 scn_ambient;");
        sb.AppendLine("global uniform vec4 scn_ibl;");
        for (int i = 0; i < 9; i++) sb.AppendLine($"global uniform vec4 scn_sh{i};");
        sb.AppendLine("global uniform vec4 scn_deferred;");
        sb.AppendLine("global uniform sampler2D scn_radiance : filter_linear, repeat_enable; // SCNScene.RadianceTexture bands");
        sb.AppendLine();

        // ---- Material property uniforms
        var uvChannels = new SortedSet<int> { 0 };
        string PropUv(SCNMaterialProperty p, string slot)
        {
            int ch = Math.Clamp(p.mappingChannel, 0, 7);
            uvChannels.Add(ch);
            string uv = $"scn_uv{ch}";
            if (!p.contentsTransform.IsIdentity)
            {
                sb.AppendLine($"uniform mat4 scn_{slot}_xf;");
                plan.properties.Add(($"scn_{slot}_xf", p, "transform"));
                uv = $"(scn_{slot}_xf * vec4({uv}, 0.0, 1.0)).xy";
            }
            if (p.wrapS == SCNWrapMode.mirror) uv = $"scn_mirror({uv})";
            return uv;
        }
        var fragPre = new StringBuilder();
        // Returns a GLSL expression for the property's value (vec4), declaring uniforms.
        string Value(SCNMaterialProperty p, string slot, bool color, string fallback)
        {
            switch (p.Kind)
            {
                case SCNMaterialProperty.ContentKind.Texture:
                {
                    string uv = PropUv(p, slot);
                    sb.AppendLine($"uniform sampler2D scn_{slot}_tex : {p.SamplerHints(color)};");
                    plan.properties.Add(($"scn_{slot}_tex", p, "texture"));
                    string sample = $"texture(scn_{slot}_tex, {uv})";
                    if (p.wrapS == SCNWrapMode.clampToBorder) sample = $"({sample} * scn_inside({uv}))";
                    return sample;
                }
                case SCNMaterialProperty.ContentKind.Color:
                case SCNMaterialProperty.ContentKind.Scalar:
                    sb.AppendLine($"uniform vec4 scn_{slot}_color;");
                    plan.properties.Add(($"scn_{slot}_color", p, "color"));
                    return $"scn_{slot}_color";
                default:
                    return fallback;
            }
        }
        string Channel(SCNMaterialProperty p, string v)
        {
            if (p.Kind != SCNMaterialProperty.ContentKind.Texture) return v + ".r";
            return p.textureComponents switch
            {
                SCNColorMask.green => v + ".g",
                SCNColorMask.blue => v + ".b",
                SCNColorMask.alpha => v + ".a",
                _ => v + ".r",
            };
        }

        string diffuse = Value(m.diffuse, "diffuse", true, "vec4(1.0)");
        bool diffuseIsTexture = m.diffuse.Kind == SCNMaterialProperty.ContentKind.Texture;
        string diffuseUv = diffuseIsTexture ? PropUvExpr(m.diffuse) : "scn_uv0";
        string emission = depthOnly ? "vec4(0.0)" : Value(m.emission, "emission", true, "vec4(0.0)");
        bool hasMultiply = !(m.multiply.contents is NSColor mc && mc.Equals(NSColor.white)) && m.multiply.Kind != SCNMaterialProperty.ContentKind.None;
        string multiply = hasMultiply ? Value(m.multiply, "multiply", true, "vec4(1.0)") : null;
        string normalMap = m.normal.Kind == SCNMaterialProperty.ContentKind.Texture && !constant ? Value(m.normal, "normal", false, null) : null;
        string rough = pbr ? Channel(m.roughness, Value(m.roughness, "roughness", false, "vec4(0.2)")) : null;
        string metal = pbr ? Channel(m.metalness, Value(m.metalness, "metalness", false, "vec4(0.0)")) : null;
        bool hasAo = !constant && m.ambientOcclusion.Kind != SCNMaterialProperty.ContentKind.None && !(m.ambientOcclusion.contents is NSColor ac && ac.Equals(NSColor.white));
        string ao = hasAo ? Channel(m.ambientOcclusion, Value(m.ambientOcclusion, "ambientOcclusion", false, "vec4(1.0)")) : null;
        string specularColor = model is SCNMaterial.LightingModel.blinn or SCNMaterial.LightingModel.phong && !IsBlack(m.specular) ? Value(m.specular, "specular", true, "vec4(0.0)") : null;
        sb.AppendLine("uniform float scn_diffuse_intensity = 1.0;");
        sb.AppendLine("uniform float scn_emission_intensity = 1.0;");
        sb.AppendLine("uniform float scn_multiply_intensity = 1.0;");
        sb.AppendLine("uniform float scn_normal_intensity = 1.0;");
        sb.AppendLine("uniform float scn_ao_intensity = 1.0;");
        sb.AppendLine("uniform float scn_transparency = 1.0;");
        sb.AppendLine("uniform float scn_shininess = 1.0;");
        sb.AppendLine();

        string PropUvExpr(SCNMaterialProperty p)
        {
            int ch = Math.Clamp(p.mappingChannel, 0, 7);
            string uv = $"scn_uv{ch}";
            if (!p.contentsTransform.IsIdentity) uv = $"(scn_diffuse_xf * vec4({uv}, 0.0, 1.0)).xy";
            if (p.wrapS == SCNWrapMode.mirror) uv = $"scn_mirror({uv})";
            return uv;
        }

        // ---- Modifier arguments, varyings and declarations
        var declared = new HashSet<string>();
        foreach (var snip in all)
        {
            foreach (var a in snip.arguments)
            {
                var name = ArgName(a);
                if (!declared.Add("u:" + name)) continue;
                plan.arguments.Add(name);
                sb.AppendLine($"uniform {a};");
            }
        }
        foreach (var snip in all)
            foreach (var v in snip.varyings)
            {
                if (!declared.Add("v:" + v)) continue;
                sb.AppendLine($"varying {v};");
            }
        var declBlocks = new HashSet<string>();
        foreach (var snip in all)
            foreach (var d in snip.declarations)
                if (declBlocks.Add(d.Trim())) sb.AppendLine(d);
        sb.AppendLine();

        // ---- Helpers
        sb.AppendLine("// SceneKit texture coordinate channels: 0 -> UV, 1 -> UV2, 2..7 -> CUSTOM0..CUSTOM2 (.xy/.zw).");
        sb.AppendLine("#define SCN_TEXCOORD0 UV");
        sb.AppendLine("#define SCN_TEXCOORD1 UV2");
        sb.AppendLine("#define SCN_TEXCOORD2 CUSTOM0.xy");
        sb.AppendLine("#define SCN_TEXCOORD3 CUSTOM0.zw");
        sb.AppendLine("#define SCN_TEXCOORD4 CUSTOM1.xy");
        sb.AppendLine("#define SCN_TEXCOORD5 CUSTOM1.zw");
        sb.AppendLine("#define SCN_TEXCOORD6 CUSTOM2.xy");
        sb.AppendLine("#define SCN_TEXCOORD7 CUSTOM2.zw");
        sb.AppendLine("vec2 scn_mirror(vec2 uv) { return 1.0 - abs(mod(uv, 2.0) - 1.0); }");
        sb.AppendLine("float scn_inside(vec2 uv) { return step(0.0, uv.x) * step(0.0, uv.y) * step(uv.x, 1.0) * step(uv.y, 1.0); }");
        sb.AppendLine("vec3 scn_irradiance(vec3 n) {");
        sb.AppendLine("    return max(vec3(0.0), scn_sh0.rgb + scn_sh1.rgb * n.y + scn_sh2.rgb * n.z + scn_sh3.rgb * n.x");
        sb.AppendLine("        + scn_sh4.rgb * (n.x * n.y) + scn_sh5.rgb * (n.y * n.z) + scn_sh6.rgb * (3.0 * n.z * n.z - 1.0)");
        sb.AppendLine("        + scn_sh7.rgb * (n.x * n.z) + scn_sh8.rgb * (n.x * n.x - n.y * n.y));");
        sb.AppendLine("}");
        sb.AppendLine("vec3 scn_env_radiance(vec3 d, float r) {");
        sb.AppendLine("    // Pre-filtered lightingEnvironment: " + SCNScene.RadianceLevels + " GGX bands (roughness 0..1), Godot panorama mapping.");
        sb.AppendLine("    const float L = " + SCNScene.RadianceLevels + ".0, H = " + SCNScene.RadianceHeight + ".0;");
        sb.AppendLine("    float u = atan(d.x, -d.z) / (2.0 * PI) + 0.5;");
        sb.AppendLine("    float v = clamp(acos(clamp(d.y, -1.0, 1.0)) / PI, 0.5 / H, 1.0 - 0.5 / H);");
        sb.AppendLine("    float lv = clamp(r, 0.0, 1.0) * (L - 1.0), l0 = floor(lv), l1 = min(l0 + 1.0, L - 1.0);");
        sb.AppendLine("    vec3 a = textureLod(scn_radiance, vec2(u, (l0 + v) / L), 0.0).rgb;");
        sb.AppendLine("    vec3 b = textureLod(scn_radiance, vec2(u, (l1 + v) / L), 0.0).rgb;");
        sb.AppendLine("    return mix(a, b, lv - l0);");
        sb.AppendLine("}");
        sb.AppendLine("float scn_fog_factor(vec3 view_position) {");
        sb.AppendLine("    if (scn_fog_range.w < 0.5) return 0.0;");
        sb.AppendLine("    float f = clamp((length(view_position) - scn_fog_range.x) / max(scn_fog_range.y - scn_fog_range.x, 1e-6), 0.0, 1.0);");
        sb.AppendLine("    return pow(f, scn_fog_range.z);");
        sb.AppendLine("}");
        sb.AppendLine();
        foreach (var ch in uvChannels.Where(c => c >= 2)) sb.AppendLine($"varying vec2 scn_tc{ch};");
        bool litSpecular = pbr || specularColor != null;
        if (!constant)
        {
            sb.AppendLine("varying vec3 scn_unlit; // fragment colour without direct lights (deferred shadows darken it in light())");
            sb.AppendLine("varying float scn_fog_amount; // FOG.a after .fragment modifiers (light() clamps unfogged LDR output)");
            if (litSpecular) sb.AppendLine("varying vec3 scn_dielectric_f0;");
            if (litSpecular) sb.AppendLine("varying float scn_spec_a2; // GGX alpha^2 for light(), with SceneKit's specular anti-aliasing (see fragment end)");
            if (pbr) sb.AppendLine("varying vec3 scn_albedo; varying float scn_metallic; // the material's; Godot sees a white dielectric (see fragment end)");
        }

        // ---- vertex()
        sb.AppendLine("void vertex() {");
        if ((flags & VariantFlags.VertexColors) != 0) sb.AppendLine("    COLOR = CUSTOM3; // SceneKit colour source (float RGBA, see SCNGeometry.GodotMesh)");
        foreach (var ch in uvChannels.Where(c => c >= 2)) sb.AppendLine($"    scn_tc{ch} = SCN_TEXCOORD{ch};");
        foreach (var snip in mods[SCNShaderModifierEntryPoint.geometry])
        {
            sb.AppendLine("    { // .geometry");
            sb.AppendLine(Indent(snip.body, 8));
            sb.AppendLine("    }");
        }
        if (background)
        {
            sb.AppendLine("    // readsFromDepthBuffer = false + negative renderingOrder: drawn first in SceneKit.");
            sb.AppendLine("    // Equivalent: place it on the far plane (reverse Z) so all other geometry covers it.");
            sb.AppendLine("    POSITION = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX, 1.0));");
            sb.AppendLine("    POSITION.z = 0.0;");
        }
        sb.AppendLine("}");
        sb.AppendLine();

        // ---- fragment()
        sb.AppendLine("void fragment() {");
        sb.AppendLine("    vec2 scn_uv0 = UV;");
        if (uvChannels.Contains(1)) sb.AppendLine("    vec2 scn_uv1 = UV2;");
        foreach (var ch in uvChannels.Where(c => c >= 2)) sb.AppendLine($"    vec2 scn_uv{ch} = scn_tc{ch};");
        if (depthOnly)
        {
            sb.AppendLine("    ALBEDO = vec3(1.0); // colorBufferWriteMask = []: depth only (multiply by white)");
            sb.AppendLine("}");
            plan.code = sb.ToString();
            return plan;
        }
        sb.AppendLine("    // _surface.diffuse = diffuse x vertex colour, premultiplied by alpha (measured in SceneKit).");
        sb.AppendLine($"    vec4 scn_d = {diffuse};");
        sb.AppendLine("    scn_d.rgb *= scn_diffuse_intensity;");
        // NSColor diffuse alpha premultiplies twice for .constant/.lambert/.blinn/.phong and once for .physicallyBased;
        // texture and vertex-colour alpha premultiply once for every model (measured, PORTING.md).
        bool colorAlpha = !pbr && m.diffuse.contents is NSColor dca && dca.alphaComponent < 1;
        sb.AppendLine("    float scn_color_alpha = " + (colorAlpha ? "scn_diffuse_color.a" : "1.0") + ";");
        if ((flags & VariantFlags.VertexColors) != 0) sb.AppendLine("    scn_d *= COLOR; // vertex colour (raw floats, no sRGB conversion)");
        sb.AppendLine("    float scn_alpha = scn_d.a;");
        sb.AppendLine("    ALBEDO = scn_d.rgb * scn_color_alpha * scn_alpha;");
        sb.AppendLine($"    vec2 scn_diffuse_texcoord = {diffuseUv};");
        sb.AppendLine($"    EMISSION = ({emission}).rgb * scn_emission_intensity;");
        sb.AppendLine(hasMultiply ? $"    vec3 scn_multiply = mix(vec3(1.0), ({multiply}).rgb, scn_multiply_intensity);" : "    vec3 scn_multiply = vec3(1.0);");
        if (pbr)
        {
            sb.AppendLine($"    ROUGHNESS = clamp({rough}, 0.0, 1.0);");
            sb.AppendLine($"    METALLIC = clamp({metal}, 0.0, 1.0);");
            sb.AppendLine("    SPECULAR = 0.5;");
        }
        else if (specularColor != null)
        {
            sb.AppendLine($"    vec3 scn_specular = ({specularColor}).rgb;");
            sb.AppendLine("    SPECULAR = clamp(dot(scn_specular, vec3(0.2126, 0.7152, 0.0722)), 0.0, 1.0);");
            sb.AppendLine("    ROUGHNESS = clamp(sqrt(2.0 / (scn_shininess * 100.0 + 2.0)), 0.05, 1.0);");
        }
        if (!constant)
        {
            sb.AppendLine(hasAo ? $"    AO = mix(1.0, {ao}, scn_ao_intensity);" : "    AO = 1.0;");
            sb.AppendLine("    AO_LIGHT_AFFECT = 0.0;");
        }
        if (normalMap != null)
        {
            sb.AppendLine($"    vec3 scn_nm = ({normalMap}).rgb * 2.0 - 1.0;");
            sb.AppendLine("    NORMAL = normalize(mix(NORMAL, normalize(TANGENT * scn_nm.x + BINORMAL * scn_nm.y + NORMAL * scn_nm.z), scn_normal_intensity));");
        }
        if (transparent) sb.AppendLine("    ALPHA = scn_alpha; // _surface.diffuse.a (a .surface modifier may change it)");
        foreach (var snip in mods[SCNShaderModifierEntryPoint.surface])
        {
            sb.AppendLine("    { // .surface");
            sb.AppendLine(Indent(snip.body, 8));
            sb.AppendLine("    }");
        }
        sb.AppendLine("    // ---- SceneKit lighting composition");
        if (constant)
        {
            sb.AppendLine("    // .constant: colour = diffuse + emission (ambient lights ignored), x multiply.");
            sb.AppendLine("    vec3 scn_constant = (ALBEDO + EMISSION) * scn_multiply;");
            sb.AppendLine("    ALBEDO = scn_constant; // read by light() for deferred shadows");
            sb.AppendLine("    EMISSION = scn_constant;");
            sb.AppendLine("    ROUGHNESS = 1.0; METALLIC = 0.0; SPECULAR = 0.0; AO = 1.0; AO_LIGHT_AFFECT = 0.0;");
            sb.AppendLine("    IRRADIANCE = vec4(0.0, 0.0, 0.0, 1.0);");
            sb.AppendLine("    RADIANCE = vec4(0.0, 0.0, 0.0, 1.0);");
        }
        else
        {
            sb.AppendLine("    ALBEDO *= scn_multiply;");
            sb.AppendLine("    EMISSION *= scn_multiply;");
            sb.AppendLine("    vec3 scn_wn = noNormals_placeholder;".Replace("noNormals_placeholder", noNormals ? "vec3(0.0)" : "normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz)"));
            if (pbr)
            {
                sb.AppendLine("    // Ambient = SH(lightingEnvironment) x intensity x roughness response + ambient lights.");
                sb.AppendLine("    float scn_r = ROUGHNESS;");
                sb.AppendLine("    // SceneKit's diffuse IBL falls with roughness (fit to SceneKit renders, see PORTING.md).");
                sb.AppendLine("    float scn_ibl_k = " + IblDiffuseResponse + ";");
                sb.AppendLine("    vec3 scn_amb_diffuse = scn_irradiance(scn_wn) * scn_ibl.x * scn_ibl_k;");
                sb.AppendLine("    // Specular IBL from the same SH (exact for the game's smooth gradient probe), with SceneKit's");
                sb.AppendLine("    // roughness response; replaces Godot's sky radiance and offsets its multi-scatter compensation.");
                sb.AppendLine("    vec3 scn_refl = normalize((INV_VIEW_MATRIX * vec4(reflect(-VIEW, NORMAL), 0.0)).xyz);");
                sb.AppendLine("    scn_refl = normalize(mix(scn_refl, scn_wn, scn_r * scn_r));");
                sb.AppendLine("    float scn_spec_k = " + IblSpecularResponse + ";");
                sb.AppendLine("    vec3 scn_amb_radiance = (scn_ibl.y > 0.5 ? scn_env_radiance(scn_refl, " + IblBlurLookup + ") : vec3(0.0)) * scn_ibl.x * scn_spec_k;");
                sb.AppendLine("    SPECULAR *= clamp(dot(scn_multiply, vec3(0.2126, 0.7152, 0.0722)), 0.0, 1.0);");
                sb.AppendLine("    scn_dielectric_f0 = vec3(0.04) * scn_multiply; // .multiply scales the dielectric specular too");
            }
            else
            {
                sb.AppendLine("    // Legacy models: lightingEnvironment and ambient lights both act as ambient (factor 1, measured).");
                sb.AppendLine("    IRRADIANCE = vec4(scn_irradiance(scn_wn) * scn_ibl.x + scn_ambient.rgb, 1.0);");
                sb.AppendLine("    RADIANCE = vec4(0.0, 0.0, 0.0, 1.0);");
                if (specularColor != null) sb.AppendLine("    scn_dielectric_f0 = vec3(0.16 * SPECULAR * SPECULAR);");
            }
        }
        bool defaultAlpha = transparent && !fragmentWritesAlpha;
        if (transparent) sb.AppendLine("    ALPHA *= scn_transparency;");
        sb.AppendLine("    FOG = vec4(scn_fog_color.rgb, scn_fog_factor(VERTEX));");
        foreach (var snip in mods[SCNShaderModifierEntryPoint.fragment])
        {
            sb.AppendLine("    { // .fragment");
            sb.AppendLine(Indent(snip.body, 8));
            sb.AppendLine("    }");
        }
        if (!constant) sb.AppendLine("    scn_fog_amount = FOG.a;");
        if (defaultAlpha)
        {
            sb.AppendLine("    // SceneKit blends premultiplied output; Godot multiplies by ALPHA itself.");
            sb.AppendLine("    ALBEDO /= max(ALPHA / max(scn_transparency, 1e-4), 1e-4);");
            sb.AppendLine("    EMISSION /= max(ALPHA / max(scn_transparency, 1e-4), 1e-4);");
        }
        if (!constant)
        {
            if (litSpecular)
            {
                sb.AppendLine("    // SceneKit's direct specular widens the GGX lobe by the shading normal's screen-space variation (measured:");
                sb.AppendLine("    // alpha^2 + 0.25 x (|dN/dx|^2 + |dN/dy|^2) fits highlight profiles of roughness 0 .. 0.15 spheres from 0.0005 to");
                sb.AppendLine("    // 0.007 rad per pixel); light() uses this instead of Godot's roughness limiter, which works on roughness^2.");
                sb.AppendLine("    vec3 scn_dndx = dFdx(NORMAL), scn_dndy = dFdy(NORMAL);");
                sb.AppendLine("    float scn_spec_r = ROUGHNESS;");
                sb.AppendLine("    scn_spec_a2 = min(scn_spec_r * scn_spec_r * scn_spec_r * scn_spec_r + " + SceneKitCalibration.F(SceneKitCalibration.SpecularAntialiasing) + " * (dot(scn_dndx, scn_dndx) + dot(scn_dndy, scn_dndy)), 1.0);");
            }
            sb.AppendLine("    // What Godot adds besides direct light (emission, ambient/IBL diffuse, IBL specular with its");
            sb.AppendLine("    // environment BRDF), so light() can apply SceneKit's deferred shadow to the whole colour.");
            if (pbr)
            {
                sb.AppendLine("    // All ambient light goes through IRRADIANCE, which Godot multiplies by AO and SSAO (SceneKit's SSAO darkens");
                sb.AppendLine("    // image-based specular too, Godot's never does): sky diffuse x (1 - metalness), ambient lights (independent");
                sb.AppendLine("    // of metalness, measured) and sky specular (pre-filtered radiance x analytic split-sum environment BRDF).");
                sb.AppendLine("    // Godot then sees a white dielectric; light() uses the material's albedo and metalness.");
                sb.AppendLine("    vec3 scn_f0u = mix(vec3(0.16 * SPECULAR * SPECULAR), ALBEDO, METALLIC);");
                sb.AppendLine("    vec4 scn_r4 = ROUGHNESS * vec4(-1.0, -0.0275, -0.572, 0.022) + vec4(1.0, 0.0425, 1.04, -0.04);");
                sb.AppendLine("    float scn_a004 = min(scn_r4.x * scn_r4.x, exp2(-9.28 * clamp(dot(NORMAL, VIEW), 0.0001, 1.0))) * scn_r4.x + scn_r4.y;");
                sb.AppendLine("    vec2 scn_env = vec2(-1.04, 1.04) * scn_a004 + scn_r4.zw;");
                sb.AppendLine("    vec2 scn_dfg = vec2(scn_env.y, scn_env.x + scn_env.y); // (f0 = 0, f0 = 1) responses, Godot's DFG convention");
                sb.AppendLine("    float scn_f90 = clamp(50.0 * scn_f0u.g, METALLIC, 1.0);");
                sb.AppendLine("    // No multi-scatter energy compensation (SceneKit's rough-metal reflection scales with f0, measured).");
                sb.AppendLine("    vec3 scn_ibl_spec = scn_amb_radiance * ((scn_f90 - scn_f0u) * scn_dfg.x + scn_f0u * scn_dfg.y);");
                sb.AppendLine("    // SceneKit's SSAO occludes rough reflections fully but leaves sharp ones nearly untouched (measured): the");
                sb.AppendLine("    // occluded share goes through IRRADIANCE, the rest through RADIANCE, which Godot passes through unchanged for a");
                sb.AppendLine("    // white dielectric with SPECULAR 2.5 (f0 = f90 = 1: energy compensation x DFG = 1).");
                sb.AppendLine("    float scn_so = smoothstep(" + SceneKitCalibration.F(SceneKitCalibration.SpecularOcclusionFrom) + ", " + SceneKitCalibration.F(SceneKitCalibration.SpecularOcclusionTo) + ", scn_r);");
                sb.AppendLine("    IRRADIANCE = vec4(ALBEDO * (scn_amb_diffuse * (1.0 - METALLIC) + scn_ambient.rgb) + scn_ibl_spec * scn_so, 1.0);");
                sb.AppendLine("    RADIANCE = vec4(scn_ibl_spec * (1.0 - scn_so), 1.0);");
                sb.AppendLine("    scn_albedo = ALBEDO; scn_metallic = METALLIC;");
                sb.AppendLine("    ALBEDO = vec3(1.0); METALLIC = 0.0; SPECULAR = 2.5;");
                sb.AppendLine("    scn_unlit = EMISSION + IRRADIANCE.rgb * AO + RADIANCE.rgb;");
            }
            else sb.AppendLine("    scn_unlit = EMISSION + IRRADIANCE.rgb * ALBEDO * AO;");
        }
        sb.AppendLine("}");

        // ---- light()
        if (constant)
        {
            sb.AppendLine();
            sb.AppendLine("void light() {");
            sb.AppendLine("    // Deferred shadows darken the final colour of every material, constant ones included.");
            sb.AppendLine("    if (LIGHT_IS_DIRECTIONAL) { SPECULAR_LIGHT -= ALBEDO * scn_deferred.x * (1.0 - ATTENUATION); }");
            sb.AppendLine("}");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("void light() {");
            sb.AppendLine("    float scn_att = ATTENUATION, scn_white = 1.0;");
            sb.AppendLine("    if (LIGHT_IS_DIRECTIONAL && scn_deferred.x > 0.0) {");
            sb.AppendLine("        // SceneKit deferred shadows (measured): the light itself stays unshadowed and the final colour");
            sb.AppendLine("        // (direct, ambient, IBL, emission, back faces too) is multiplied by 1 - alpha x shadow.");
            sb.AppendLine("        // Faces turned away from the light are in SceneKit's deferred shadow too, and a large shadowRadius");
            sb.AppendLine("        // self-shadows sloped lit surfaces (scn_deferred.yz, SceneKitCalibration.DeferredSelfShadow).");
            sb.AppendLine("        float scn_cos = dot(NORMAL, LIGHT);");
            sb.AppendLine("        float scn_face = clamp((0.04 - scn_cos) / 0.15, 0.0, 1.0);");
            sb.AppendLine("        float scn_self = scn_deferred.y * sqrt(clamp((degrees(acos(clamp(scn_cos, -1.0, 1.0))) - scn_deferred.z) / 28.0, 0.0, 1.0));");
            sb.AppendLine("        float scn_s = max(1.0 - ATTENUATION, max(scn_face, scn_self));");
            sb.AppendLine("        scn_att = 1.0 - scn_deferred.x * scn_s; scn_white = scn_att;");
            sb.AppendLine("        SPECULAR_LIGHT -= scn_unlit * (scn_deferred.x * scn_s);");
            sb.AppendLine("    }");
            if (noNormals)
            {
                sb.AppendLine("    // No normal source: SceneKit's normal is zero, so N.L = 0 (only ambient light remains).");
            }
            else
            {
                sb.AppendLine("    float NdotL = min(dot(NORMAL, LIGHT), 1.0);");
                sb.AppendLine("    float cNdotL = max(NdotL, 0.0);");
                sb.AppendLine("    DIFFUSE_LIGHT += LIGHT_COLOR * (cNdotL * (1.0 / PI)) * scn_att" + (pbr ? " * scn_albedo * (1.0 - scn_metallic);" : ";"));
                if (litSpecular)
                {
                    sb.AppendLine("    // SceneKit's direct specular (measured): GGX with Godot's D and Fresnel, the exact height-correlated");
                    sb.AppendLine("    // Smith visibility and no multi-scatter energy compensation (Godot's defaults differ there).");
                    sb.AppendLine("    float cNdotV = max(dot(NORMAL, VIEW), 1e-4);");
                    sb.AppendLine("    vec3 H = normalize(VIEW + LIGHT);");
                    sb.AppendLine("    float cNdotH = clamp(dot(NORMAL, H), 0.0, 1.0);");
                    sb.AppendLine("    float cLdotH = clamp(dot(LIGHT, H), 0.0, 1.0);");
                    sb.AppendLine("    float a2 = scn_spec_a2;");
                    sb.AppendLine("    float alpha_ggx = sqrt(a2);");
                    sb.AppendLine("    float a = cNdotH * alpha_ggx;");
                    sb.AppendLine("    float k = alpha_ggx / (1.0 - cNdotH * cNdotH + a * a);");
                    sb.AppendLine("    // Not clamped to the half-float maximum: SceneKit keeps sharp highlights' energy. alpha 0 (a flat mirror)");
                    sb.AppendLine("    // reflects no direct light at all, also exactly in the mirror direction (measured: L_pbr_rough0_dir1000).");
                    sb.AppendLine("    float D = alpha_ggx > 0.0 ? k * k * (1.0 / PI) : 0.0;");
                    sb.AppendLine("    float G = 0.5 / max(cNdotL * sqrt(cNdotV * cNdotV * (1.0 - a2) + a2) + cNdotV * sqrt(cNdotL * cNdotL * (1.0 - a2) + a2), 1e-5);");
                    if (pbr)
                    {
                        sb.AppendLine("    vec3 f0 = mix(scn_dielectric_f0, scn_albedo, scn_metallic);");
                        sb.AppendLine("    float f90 = clamp(dot(f0, vec3(50.0 * 0.33)), scn_metallic, 1.0);");
                    }
                    else
                    {
                        sb.AppendLine("    vec3 f0 = mix(scn_dielectric_f0, ALBEDO, METALLIC);");
                        sb.AppendLine("    float f90 = clamp(dot(f0, vec3(50.0 * 0.33)), METALLIC, 1.0);");
                    }
                    sb.AppendLine("    float m = 1.0 - cLdotH; float m5 = m * m * m * m * m;");
                    sb.AppendLine("    vec3 F = f0 + (f90 - f0) * m5;");
                    sb.AppendLine("    // (min: stay within the half-float colour target, as SceneKit's does)");
                    sb.AppendLine("    SPECULAR_LIGHT += min(cNdotL * D * F * G * LIGHT_COLOR * scn_att * SPECULAR_AMOUNT" + (transparent && pbr && !fragmentWritesAlpha ? " / max(ALPHA, 1e-3), vec3(65504.0)); // SceneKit keeps the full highlight on transparent PBR surfaces" : ", vec3(65504.0));"));
                }
            }
            sb.AppendLine("    // SceneKit renders LDR views (wantsHDR false, scn_ibl.z) into an 8-bit target: each draw is clamped to 1 before");
            sb.AppendLine("    // MSAA resolve and blending (deferred shadows then scale it), so a highlight behind glass shows as the glass over");
            sb.AppendLine("    // white (measured: WALL-E's eyes). Godot keeps float values until tonemapping; cap the fragment's total here.");
            sb.AppendLine("    if (scn_ibl.z > 0.5 && scn_fog_amount <= 0.0) {");
            sb.AppendLine("        float scn_cap = scn_white" + (transparent && !fragmentWritesAlpha ? " / max(ALPHA, 1e-3)" : "") + ";");
            sb.AppendLine("        SPECULAR_LIGHT = min(SPECULAR_LIGHT, max(vec3(scn_cap) - DIFFUSE_LIGHT * ALBEDO - scn_unlit, vec3(0.0)));");
            sb.AppendLine("    }");
            sb.AppendLine("}");
        }
        plan.code = sb.ToString();
        return plan;
    }

    /// <summary>SceneKit PBR diffuse response to the lightingEnvironment, relative to Godot's IRRADIANCE (calibrated).</summary>
    internal static string IblDiffuseResponse => SceneKitCalibration.Poly(SceneKitCalibration.IblDiffuse1, SceneKitCalibration.IblDiffuse2, SceneKitCalibration.IblDiffuse3);
    /// <summary>Radiance band lookup roughness as a function of roughness (SceneKit's reflection blur widens faster than GGX alpha = r^2).</summary>
    internal static string IblBlurLookup => $"clamp({SceneKitCalibration.F(SceneKitCalibration.IblBlurScale)} * pow(scn_r, {SceneKitCalibration.F(SceneKitCalibration.IblBlurPower)}), 0.0, 1.0)";
    internal static string IblSpecularResponse => SceneKitCalibration.Poly(SceneKitCalibration.IblSpecular1, SceneKitCalibration.IblSpecular2, SceneKitCalibration.IblSpecular3, SceneKitCalibration.IblSpecular0);

    /// <summary>Measured: .physicallyBased materials ignore `transparency` in the default .aOne mode (opaque result).</summary>
    internal static bool IgnoresTransparency(SCNMaterial m) =>
        m.lightingModel == SCNMaterial.LightingModel.physicallyBased && m.transparencyMode == SCNTransparencyMode.aOne;

    private static string Fmt(double v) => v.ToString("0.0####", System.Globalization.CultureInfo.InvariantCulture);
    private static bool IsBlack(SCNMaterialProperty p) => p.contents is NSColor c ? c.LinearRGB is (0, 0, 0) : p.Kind == SCNMaterialProperty.ContentKind.None;
    private static string Indent(string body, int n)
    {
        var pad = new string(' ', n);
        return string.Join("\n", (body ?? "").Replace("\r", "").Split('\n').Select(l => pad + l));
    }
}

/// <summary>
/// GPU state of one SCNMaterial: the ShaderMaterial variants it is drawn with
/// (one per render priority / geometry with its own modifiers / mesh traits).
/// </summary>
internal sealed class MaterialGpu
{
    private readonly SCNMaterial m;
    private readonly Dictionary<(SCNGeometry geometry, int priority, ShaderComposer.VariantFlags flags), (ShaderMaterial material, ShaderComposer.Plan plan)> variants = new();
    private bool structuralDirty = true, valuesDirty = true;
    private readonly HashSet<string> dirtyArguments = new();
    internal MaterialGpu(SCNMaterial m) { this.m = m; }

    internal void MarkDirty(bool structural)
    {
        structuralDirty |= structural;
        valuesDirty = true;
        SceneKitRuntime.MaterialDirty(this);
    }
    internal void ArgumentChanged(string key)
    {
        lock (dirtyArguments) dirtyArguments.Add(key);
        SceneKitRuntime.MaterialDirty(this);
    }

    internal ShaderMaterial Variant(SCNGeometry geometry, int renderingOrder, ShaderComposer.VariantFlags flags)
    {
        var g = geometry != null && geometry.HasOwnShading ? geometry : null;
        int priority = Math.Clamp(renderingOrder, -128, 127);
        var key = (g, priority, flags);
        if (variants.TryGetValue(key, out var v)) return v.material;
        var sm = new ShaderMaterial();
        var plan = Configure(sm, g, priority, flags);
        variants[key] = (sm, plan);
        g?.argumentVariants.Add(sm);
        return sm;
    }

    private ShaderComposer.Plan Configure(ShaderMaterial sm, SCNGeometry g, int priority, ShaderComposer.VariantFlags flags)
    {
        var plan = ShaderComposer.Compose(m, g, flags);
        sm.Shader = ShaderComposer.GetShader(plan.code);
        sm.RenderPriority = priority;
        ApplyValues(sm, plan, g);
        return plan;
    }

    internal void Flush()
    {
        if (structuralDirty)
        {
            foreach (var key in variants.Keys.ToList())
            {
                var (sm, _) = variants[key];
                variants[key] = (sm, Configure(sm, key.geometry, key.priority, key.flags));
            }
        }
        else if (valuesDirty)
        {
            foreach (var (key, (sm, plan)) in variants) ApplyValues(sm, plan, key.geometry);
        }
        else if (dirtyArguments.Count > 0)
        {
            string[] changed;
            lock (dirtyArguments) changed = dirtyArguments.ToArray();
            foreach (var (key, (sm, plan)) in variants)
                foreach (var a in changed)
                    if (key.geometry == null || !key.geometry.arguments.ContainsKey(a))
                        if (m.arguments.TryGetValue(a, out var value)) ApplyArgument(sm, a, value, key.geometry?.DrawnScene);
        }
        structuralDirty = valuesDirty = false;
        lock (dirtyArguments) dirtyArguments.Clear();
    }

    private void ApplyValues(ShaderMaterial sm, ShaderComposer.Plan plan, SCNGeometry g)
    {
        foreach (var (uniform, p, what) in plan.properties)
        {
            switch (what)
            {
                case "texture": sm.SetShaderParameter(uniform, p.Texture); break;
                case "transform": sm.SetShaderParameter(uniform, p.contentsTransform.ToGodotProjection()); break;
                case "color": sm.SetShaderParameter(uniform, V4(p.LinearColor(new Color(1, 1, 1, 1)))); break;
            }
        }
        sm.SetShaderParameter("scn_diffuse_intensity", (float)m.diffuse.intensity);
        sm.SetShaderParameter("scn_emission_intensity", (float)m.emission.intensity);
        sm.SetShaderParameter("scn_multiply_intensity", (float)m.multiply.intensity);
        sm.SetShaderParameter("scn_normal_intensity", (float)m.normal.intensity);
        sm.SetShaderParameter("scn_ao_intensity", (float)m.ambientOcclusion.intensity);
        sm.SetShaderParameter("scn_transparency", (float)(ShaderComposer.IgnoresTransparency(m) ? 1.0 : m.transparency));
        sm.SetShaderParameter("scn_shininess", (float)m.shininess);
        foreach (var name in plan.arguments)
        {
            object value = null;
            if (g != null && g.arguments.TryGetValue(name, out var gv)) value = gv;
            else if (m.arguments.TryGetValue(name, out var mv)) value = mv;
            if (value != null) ApplyArgument(sm, name, value, g?.DrawnScene);
        }
    }

    /// <summary>Godot converts Color values passed to vec4 uniforms from sRGB to linear; colours here are already linear.</summary>
    private static Vector4 V4(Color c) => new(c.R, c.G, c.B, c.A);

    /// <summary>Converts a setValue(_:forKey:) value to a Godot uniform value. An SCNMaterialProperty is bound with
    /// the contents SceneKit draws it with in <paramref name="scene"/> (see SCNMaterialProperty.ArgumentContents).</summary>
    internal static void ApplyArgument(ShaderMaterial sm, string name, object value, SCNScene scene = null)
    {
        if (value is SCNMaterialProperty property)
        {
            var contents = property.ArgumentContents(scene, SceneKitRuntime.Flushing);
            if (SCNMaterialProperty.KindOf(contents) != SCNMaterialProperty.ContentKind.Texture)
                sm.SetShaderParameter(name, V4(SCNMaterialProperty.LinearColorOf(contents, new Color(0, 0, 0, 0))));
            else if (SCNMaterialProperty.TextureOf(contents) is { } texture) sm.SetShaderParameter(name, texture);
            return;
        }
        Godot.Variant v = value switch
        {
            double d => (float)d,
            float f => f,
            int i => (float)i,
            bool b => b ? 1f : 0f,
            NSValue nv => nv.value switch
            {
                SCNVector3 v3 => v3.ToGodot(),
                SCNVector4 v4 => v4.ToGodot(),
                CGPoint p => p.ToGodot(),
                CGSize s => new Vector2((float)s.width, (float)s.height),
                SCNMatrix4 mm => mm.ToGodotProjection(),
                _ => default,
            },
            SCNVector3 v3 => v3.ToGodot(),
            SCNVector4 v4 => v4.ToGodot(),
            SCNMatrix4 mm => mm.ToGodotProjection(),
            CGPoint p => p.ToGodot(),
            NSColor c => V4(c.GodotLinear),
            MTLTexture t => t.GodotTexture,
            NSImage img => img.GodotTexture,
            _ => default,
        };
        if (v.VariantType != Godot.Variant.Type.Nil) sm.SetShaderParameter(name, v);
    }
}
