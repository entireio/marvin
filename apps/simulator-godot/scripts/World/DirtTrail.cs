using System;
using System.Collections.Generic;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

/// Batched ground decals: enough history for a three-lap race, with bounded memory.
public sealed class DirtTrail
{
    public enum Style { tracks, tires }
    private static readonly (double, double)[] Corners = { (-1.0, -1.0), (1.0, -1.0), (1.0, 1.0), (-1.0, 1.0) };
    public readonly SCNNode root = new SCNNode();
    private readonly Style style;
    private readonly List<CGPoint> uv = new();
    private readonly SCNMaterial ink = material(0xffffff, roughness: 1);
    private readonly List<SCNVector3> normals = new();
    private readonly List<float> strengths = new();
    private readonly List<SCNNode> chunks = new();
    private readonly List<int> chunkMarks = new();
    private readonly List<SCNVector3> vertices = new();
    private readonly List<int> indices = new();
    private int chunkIndex = 0;
    private bool dirty = false;
    private float shaderStormTime = 0;
    private float shaderStormActive = 0;
    public int geometryUploads { get; private set; } = 0;
    private (double x, double z, double heading)? previous;
    private double remainder = 0.0;
    public int count { get; private set; } = 0;

    public Dictionary<string, int> diagnostics(Func<SCNNode, bool> visible = null)
    {
        int visibleQuads = -1, visibleNodes = -1;
        // isNode can synchronize with rendering. Only explicit diagnostic
        // runs may request these tests; normal telemetry reads cached counts.
        if (visible != null)
        {
            visibleQuads = 0; visibleNodes = 0;
            for (int index = 0; index < chunks.Count; index++)
            {
                if (!visible(chunks[index])) continue;
                visibleNodes += 1; visibleQuads += chunkMarks[index];
            }
        }
        return new Dictionary<string, int> { ["nodes"] = chunks.Count, ["quads"] = chunkMarks.Sum(), ["visibleNodes"] = visibleNodes, ["visibleQuads"] = visibleQuads, ["emittedMarks"] = count, ["geometryUploads"] = geometryUploads };
    }

    public DirtTrail(Style style = Style.tracks)
    {
        this.style = style;
        root.name = "Surface-aware ground impressions";
        ink.lightingModel = SCNMaterial.LightingModel.constant;
        ink.blendMode = SCNBlendMode.multiply;
        ink.shaderModifiers = new()
        {
            [SCNShaderModifierEntryPoint.geometry] = @"
#pragma varyings
float impressionStrength;
float trailBorn;
float trailDistance;
#pragma body
impressionStrength = COLOR.r;
trailBorn = COLOR.g;
trailDistance = length((MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz);
",
            [SCNShaderModifierEntryPoint.surface] = @"
#pragma transparent
#pragma body
vec2 uv = scn_diffuse_texcoord;
float across = smoothstep(0.0, 0.15, uv.x) * (1.0 - smoothstep(0.85, 1.0, uv.x));
float along = smoothstep(0.0, 0.24, uv.y) * (1.0 - smoothstep(0.65, 1.0, uv.y));
float shade = 1.0 - impressionStrength * across * along;
ALBEDO = vec3(shade); ALPHA = 1.0;
",
            [SCNShaderModifierEntryPoint.fragment] = @"
#pragma arguments
float stormTime;
float stormActive;
#pragma body
if (stormActive > 0.5) {
    float age = max(0.0, stormTime - trailBorn);
    float visibility = exp(-age / 10.0) * (1.0 - smoothstep(3.0, 65.0, trailDistance));
    // Multiply decals must fade toward white, not toward brown fog.
    vec3 faded = mix(vec3(1.0), ALBEDO, visibility);
    ALBEDO = faded; EMISSION = faded; FOG = vec4(0.0);
}
",
        };
        ink.setValue(0f, "stormTime"); ink.setValue(0f, "stormActive");
        // The housing-fitted rollers have smooth, flat rubber tread.
        ink.isDoubleSided = true;
        ink.writesToDepthBuffer = false;
    }
    public void reset()
    {
        foreach (var chunk in chunks) chunk.removeFromParentNode(); chunks.Clear(); chunkMarks.Clear();
        vertices.Clear(); normals.Clear(); strengths.Clear(); uv.Clear(); indices.Clear(); chunkIndex = 0;
        previous = null; remainder = 0; count = 0; dirty = false; geometryUploads = 0;
    }
    public void update(Simulation state, IReadOnlyList<(double x, double z, double width)> contacts)
    {
        // Shared by every retained chunk. Rewriting unchanged values can
        // invalidate SceneKit's per-draw material resources across the history.
        float stormTime = (float)state.storm.elapsed, stormActive = state.storm.enabled ? 1 : 0;
        if (stormTime != shaderStormTime)
        {
            ink.setValue(stormTime, "stormTime"); shaderStormTime = stormTime;
        }
        if (stormActive != shaderStormActive)
        {
            ink.setValue(stormActive, "stormActive"); shaderStormActive = stormActive;
        }
        if (!state.hasDirtContact) { previous = null; remainder = 0; return; }
        try
        {
            if (previous == null) return;
            var prev = previous.Value;
            double dx = state.x - prev.x, dz = state.z - prev.z;
            double distance = hypot(dx, dz);
            if (!(distance > 1e-8)) return;
            // Do not draw connecting lines after a teleport/reset.
            if (!(distance < 2)) { remainder = 0; return; }
            double angle = atan2(sin(state.heading - prev.heading), cos(state.heading - prev.heading));
            double spacing = 0.065;
            double travel = spacing - remainder;
            while (travel <= distance)
            {
                double t = travel / distance, heading = prev.heading + angle * t;
                double x = prev.x + dx * t, z = prev.z + dz * t;
                foreach (var contact in contacts)
                {
                    double load = max(abs(state.x), abs(state.z)) > DesertTerrain.townEdge + 8 ? (state.sand?.contactWeight(state, new SandDeformation.Contact(x: contact.x, z: contact.z, width: contact.width, length: 0.1)) ?? 1) : 1;
                    if (!(load > 0)) continue;
                    if (vertices.Count == 256 * 4)
                    {
                        flush(); chunkIndex = (chunkIndex + 1) % 128;
                        vertices.Clear(); normals.Clear(); strengths.Clear(); uv.Clear(); indices.Clear();
                    }
                    int @base = vertices.Count;
                    double dune = max(0, min(1, (max(abs(x), abs(z)) - DesertTerrain.townEdge) / 30));
                    float strength = (float)((style == Style.tracks ? 0.24 : 0.19) * (1 - dune) + 0.14 * dune) * (float)load;
                    double e = 0.04;
                    double nx = state.terrainHeight(x - e, z) - state.terrainHeight(x + e, z);
                    double nz = state.terrainHeight(x, z - e) - state.terrainHeight(x, z + e);
                    double length = sqrt(nx * nx + 4 * e * e + nz * nz);
                    var normal = new SCNVector3(nx / length, 2 * e / length, nz / length);
                    // PORT: the corner literals and the four tone floats are written without temporary arrays (same values).
                    foreach (var (side, along) in Corners)
                    {
                        double lateral = contact.x + side * contact.width / 2;
                        double forward = contact.z + along * (style == Style.tracks ? 0.021 : spacing * 0.53);
                        double px = x + cos(heading) * lateral + sin(heading) * forward;
                        double pz = z - sin(heading) * lateral + cos(heading) * forward;
                        normals.Add(normal); strengths.Add(strength); strengths.Add((float)state.storm.elapsed); strengths.Add(0); strengths.Add(1);
                        uv.Add(new CGPoint((side + 1) / 2, (along + 1) / 2));
                        // Flat-town road/plaza dressing rises above the base sand.
                        // Place marks over that dressing; fade this floor away before
                        // the dunes, where impressions must follow the actual terrain.
                        double edge = max(abs(px), abs(pz));
                        double blend = max(0, min(1, (edge - (DesertTerrain.townEdge - 10)) / 8));
                        double floor = -0.025 * blend * blend * (3 - 2 * blend);
                        double ground = state.terrainHeight(px, pz);
                        double height = edge < DesertTerrain.townEdge - 2 ? max(ground, floor) : ground;
                        vertices.Add(new SCNVector3(px, height + 0.007, pz));
                    }
                    indices.Add(@base); indices.Add(@base + 2); indices.Add(@base + 1); indices.Add(@base); indices.Add(@base + 3); indices.Add(@base + 2);
                    count += 1; dirty = true;
                }
                travel += spacing;
            }
            remainder = (remainder + distance) % spacing;
            flush();
        }
        finally { previous = (state.x, state.z, state.heading); }
    }
    private void flush()
    {
        if (!(dirty && vertices.Count > 0)) return;
        dirty = false; geometryUploads += 1;
        if (chunkIndex == chunks.Count)
        {
            var node = new SCNNode(); node.castsShadow = false;
            // Town soil, wear and road layers are transparent decals too. Draw
            // impressions after those layers, while retaining depth occlusion
            // by robots, buildings and terrain. Distance sorting alone erases them.
            node.renderingOrder = 10;
            root.addChildNode(node); chunks.Add(node); chunkMarks.Add(0);
        }
        var tone = new SCNGeometrySource(SCNGeometrySource.Bytes(strengths), SCNGeometrySourceSemantic.color, vertices.Count, true, 4, 4, 0, 16);
        var geometry = new SCNGeometry(new[] { SCNGeometrySource.vertices(vertices), SCNGeometrySource.textureCoordinates(uv),
            SCNGeometrySource.normals(normals), tone },
            new[] { new SCNGeometryElement(indices, SCNGeometryPrimitiveType.triangles) });
        geometry.materials = new() { ink }; chunks[chunkIndex].geometry = geometry; chunkMarks[chunkIndex] = vertices.Count / 4;
    }
}
