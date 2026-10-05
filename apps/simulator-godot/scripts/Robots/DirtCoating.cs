// Port of Sources/MarvinSimulator/DirtCoating.swift.
using System;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

/// Surface-attached dust and splatter, concentrated near the running gear.
public sealed class DirtCoating
{
    private readonly SCNMaterialProperty contactTexture = new SCNMaterialProperty();
    private readonly MTLDevice contactDevice = MTLCreateSystemDefaultDevice();
    private SCNVector4 contactPlane = new SCNVector4(0, 0, 0, 0);
    private double lastDistance = 0.0;
    private double uploadedLevel = 0.0;
    private readonly bool contactCoating = !commandLineContains("--benchmark-no-contact-coating");
    /// Swift `CommandLine.arguments.contains(_:)`: Godot's engine and user arguments (user arguments follow "--").
    private static bool commandLineContains(string argument) =>
        Godot.OS.GetCmdlineArgs().Contains(argument) || Godot.OS.GetCmdlineUserArgs().Contains(argument);
    private double sandClock = -1.0; private bool sandActive = false;
    public double amount { get; private set; } = 0.0;

    /// Swift `install(on root:height:wheelOffset:rolling:)`.
    public void install(SCNNode root, double height, double wheelOffset, bool rolling = false)
    {
        uploadContactPlane(contactPlane);
        root.enumerateChildNodes((node, _) =>
        {
            var source = node.geometry;
            if (node.isHidden || source == null
                || source.materials.All(m => m.lightingModel == SCNMaterial.LightingModel.constant)) { return; }
            SCNGeometry geometry;
            var positions = source.sourcesFor(SCNGeometrySourceSemantic.vertex).FirstOrDefault();
            if (source.sourcesFor(SCNGeometrySourceSemantic.texcoord).Length == 0 && positions != null)
            {
                // SceneKit's generated textured pipeline requires a UV stream
                // even though this shader reads a fixed texture coordinate.
                // Imported CAD surfaces contain only positions and normals.
                var uv = new SCNGeometrySource(new byte[positions.vectorCount * 8], SCNGeometrySourceSemantic.texcoord, positions.vectorCount, true, 2, 4, 0, 8);
                geometry = new SCNGeometry(source.sources.Append(uv), source.elements);
                geometry.materials = source.materials; geometry.levelsOfDetail = source.levelsOfDetail;
                geometry.name = source.name;
            }
            else { geometry = source.copy(); }
            node.geometry = geometry;
            geometry.shaderModifiers = new() { [SCNShaderModifierEntryPoint.surface] = shader };
            geometry.setValue(NSValue.scnMatrix4(node.convertTransform(SCNMatrix4Identity, to: root)), "dirtToBody");
            geometry.setValue(height, "dirtHeight");
            geometry.setValue(wheelOffset / height, "dirtWheelX");
            geometry.setValue(contactTexture, "duneContact");
            geometry.setValue((rolling && node.name?.StartsWith("ball:") == true) || node.name?.StartsWith("link:") == true ? 1.0 : 0.0, "dirtRolling");
        });
    }
    public void update(Simulation state)
    {
        var resetting = !state.dirtTrack || state.distance < lastDistance;
        if (resetting) { amount = 0; }
        var travel = max(0, state.distance - lastDistance);
        if (state.dirtTrack && state.hasDirtContact)
        {
            amount = min(1, amount + travel / 75);
        }
        lastDistance = state.distance;
        double level = floor(amount * 32) / 32; bool dirtChanged = level != uploadedLevel;
        uploadedLevel = level;
        var inSand = contactCoating && state.dirtTrack && state.sand?.tiles.Count > 0 && max(abs(state.x), abs(state.z)) > DesertTerrain.townEdge + 8;
        if ((inSand && (state.elapsed - sandClock >= 1.0 / 30 || state.elapsed < sandClock)) || (!inSand && sandActive))
        {
            var e = 0.35;
            var gx = (state.terrainHeight(x: state.x + e, z: state.z) - state.terrainHeight(x: state.x - e, z: state.z)) / (2 * e);
            var gz = (state.terrainHeight(x: state.x, z: state.z + e) - state.terrainHeight(x: state.x, z: state.z - e)) / (2 * e);
            var intercept = state.terrainHeight(x: state.x, z: state.z) - gx * state.x - gz * state.z;
            var plane = new SCNVector4(gx, gz, intercept, inSand ? 1 : 0);
            contactPlane = plane;
            uploadContactPlane(plane);
            sandActive = inSand; sandClock = state.elapsed;
        }
        else if (dirtChanged) { uploadContactPlane(contactPlane); }
    }
    /// Native screenshot A/B: verify the shared material uniform reaches the
    /// geometry modifier, and only changes the opaque contact rim.
    public void diagnosticContact(bool visible)
    {
        var plane = contactPlane; if (!visible) { plane.w = 0; }
        uploadContactPlane(plane);
    }
    private void uploadContactPlane(SCNVector4 plane)
    {
        var descriptor = MTLTextureDescriptor.texture2DDescriptor(MTLPixelFormat.rgba32Float, 2, 1, false);
        descriptor.storageMode = contactDevice.supportsFamily(MTLGPUFamily.apple1) ? MTLStorageMode.shared : MTLStorageMode.managed; descriptor.usage = MTLTextureUsage.shaderRead;
        var texture = contactDevice.makeTexture(descriptor);
        float[] values = { (float)plane.x, (float)plane.y, (float)plane.z, (float)plane.w, (float)uploadedLevel, 0, 0, 0 };
        texture.replace(MTLRegionMake2D(0, 0, 2, 1), 0, values, 32);
        // Change the texture resource, not per-part shader uniforms. SceneKit
        // retains each immutable resource through its in-flight GPU reads.
        contactTexture.contents = texture;
    }
    // MSL .surface modifier translated to Godot shading language (PORTING.md, "Shader modifier translation guide"):
    // scn_node.inverseModelViewTransform -> inverse(MODEL_MATRIX) * INV_VIEW_MATRIX, scn_node.modelTransform -> MODEL_MATRIX,
    // duneContact.read(uint2(...)) -> texelFetch, _surface.diffuse/roughness/metalness -> ALBEDO/ROUGHNESS/METALLIC.
    // _surface.selfIllumination -> scn_self_illumination: mentioning it makes SceneKit take the diffuse sky light from it
    // (black here), see ShaderComposer.
    private const string shader = @"
#pragma arguments
mat4 dirtToBody;
float dirtHeight;
float dirtWheelX;
float dirtRolling;
sampler2D duneContact : filter_nearest;
#pragma declaration
float dirtHash(vec3 p) { return fract(sin(dot(p, vec3(127.1, 311.7, 74.7))) * 43758.5453); }
float dirtNoise(vec3 p) {
    vec3 i = floor(p), f = fract(p); f = f * f * (3.0 - 2.0 * f);
    return mix(mix(mix(dirtHash(i), dirtHash(i + vec3(1.0, 0.0, 0.0)), f.x),
                   mix(dirtHash(i + vec3(0.0, 1.0, 0.0)), dirtHash(i + vec3(1.0, 1.0, 0.0)), f.x), f.y),
               mix(mix(dirtHash(i + vec3(0.0, 0.0, 1.0)), dirtHash(i + vec3(1.0, 0.0, 1.0)), f.x),
                   mix(dirtHash(i + vec3(0.0, 1.0, 1.0)), dirtHash(i + vec3(1.0, 1.0, 1.0)), f.x), f.y), f.z);
}
#pragma body
float dirtAmount = texelFetch(duneContact, ivec2(1, 0), 0).r;
vec3 local = (inverse(MODEL_MATRIX) * INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
vec3 p = (dirtToBody * vec4(local, 1.0)).xyz / dirtHeight;
float low = mix(1.0 - smoothstep(0.08, 0.58, p.y), 1.0, dirtRolling);
float nearWheel = exp(-pow((abs(p.x) - dirtWheelX) * 9.0, 2.0));
float rear = 1.0 - smoothstep(-0.22, 0.24, p.z);
float patches = dirtNoise(p * 28.0);
float specks = smoothstep(0.58, 0.76, dirtNoise(p * 145.0));
float splash = smoothstep(0.38, 0.68, patches) * low * (0.45 + 0.35 * nearWheel + 0.20 * rear);
float dust = (0.04 + low * 0.22) * (0.55 + 0.45 * patches);
float coverage = clamp(dirtAmount * (dust + splash * 0.85 + specks * low * 0.40), 0.0, 0.92);
vec3 soil = mix(vec3(0.19, 0.105, 0.045), vec3(0.42, 0.29, 0.16), patches);
ALBEDO = mix(ALBEDO, soil, coverage);
ROUGHNESS = mix(ROUGHNESS, 1.0, coverage);
METALLIC *= 1.0 - coverage;
scn_self_illumination *= 1.0 - coverage;
vec4 dunePlane = texelFetch(duneContact, ivec2(0), 0);
if (dunePlane.w > 0.5) {
vec3 world = (MODEL_MATRIX * vec4(local, 1.0)).xyz;
float sandHeight = dot(dunePlane.xy, world.xz) + dunePlane.z;
float sandGrain = dirtNoise(world * 130.0);
float rim = (1.0 - smoothstep(-0.004, 0.035, world.y - sandHeight + (sandGrain - 0.5) * 0.016)) * dunePlane.w;
vec3 sand = vec3(0.64, 0.43, 0.23) * (0.84 + 0.25 * sandGrain);
ALBEDO = mix(ALBEDO, sand, rim * 0.88);
ROUGHNESS = mix(ROUGHNESS, 0.96, rim);
METALLIC *= 1.0 - rim;
scn_self_illumination *= 1.0 - rim;
}
";
}

