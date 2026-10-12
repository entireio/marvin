using System;
using System.Collections.Generic;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

/// Joint-space deformation on the GPU keeps the large spectator batches intact.
/// Per-person bind frames and joint masks travel with every LOD's vertices.
public static class CitizenMotion
{
    // PORT: the MSL .geometry modifier in Godot shading language. `_geometry.texcoords[n]` is SCN_TEXCOORDn
    // (texcoord channels 1..7 are the crowd's motion sources: CUSTOM0..CUSTOM2 and UV2, see SCNGeometry),
    // `_geometry.position.xyz` is VERTEX, `_geometry.normal` is NORMAL, `fmod` is `mod` (the phase is never
    // negative). Godot runs vertex() for shadow maps too, so walking residents cast deformed shadows as in
    // SceneKit.
    public const string shader = @"#pragma arguments
float crowdTime;
float walkCycle;
float walkBlend;
#pragma body
vec2 originXZ = SCN_TEXCOORD1;
vec2 frame = SCN_TEXCOORD2;
vec2 person = SCN_TEXCOORD3;
vec2 arm = SCN_TEXCOORD4;
vec2 joints = SCN_TEXCOORD5;
vec2 kind = SCN_TEXCOORD6;
float c = cos(frame.x), s = sin(frame.x);
vec3 p = VERTEX - vec3(originXZ.x, frame.y, originXZ.y);
vec3 n = NORMAL;
p = vec3(c * p.x - s * p.z, p.y, s * p.x + c * p.z);
n = vec3(c * n.x - s * n.z, n.y, s * n.x + c * n.z);
float phase = person.x, neck = person.y;
// Idle residents look around and shift their upper bodies; their feet
// remain planted. Person-specific timing avoids a synchronized crowd.
float idle = (1.0 - walkBlend) * (kind.x > 0.5 ? 0.0 : 1.0);
float tempo = 0.8 + 0.4 * fract(phase * 0.73);
float bodyTurn = idle * 0.12 * sin(crowdTime * 0.53 * tempo + phase);
float torso = smoothstep(joints.x, joints.x + 0.24, p.y);
float bc = cos(bodyTurn * torso), bs = sin(bodyTurn * torso);
p.xz = vec2(bc * p.x + bs * p.z, -bs * p.x + bc * p.z);
n.xz = vec2(bc * n.x + bs * n.z, -bs * n.x + bc * n.z);
float headAngle = (0.44 * sin(crowdTime * 0.47 * tempo + phase) + 0.12 * sin(crowdTime * 0.19 + phase * 2.0));
float headWeight = smoothstep(neck - 0.012, neck + 0.045, p.y);
// Purposeful residents keep attention on their partner, door or counter.
headAngle *= kind.x < 0.0 ? 0.28 : 1.0;
float ha = headAngle * headWeight, hc = cos(ha), hs = sin(ha);
p.xz = vec2(hc * p.x + hs * p.z, -hs * p.x + hc * p.z);
n.xz = vec2(hc * n.x + hs * n.z, -hs * n.x + hc * n.z);
float cycle = walkCycle + phase;
float waveWindow = pow(max(0.0, sin(crowdTime * 0.31 + phase)), 6.0);
// Seated spectators and a few standing onlookers react independently.
float gesture = kind.x == -2.0 ? 0.0 : (kind.x < 0.0 ? 0.32 : (kind.x > 0.5 ? 1.0 : 0.75));
float wave = (1.0 - walkBlend) * waveWindow * gesture;
// Alternate arms; a bent elbow reads as a wave rather than a T-pose.
wave *= (mod(floor(phase), 2.0) < 1.0 ? (arm.y > 0.0 ? 1.0 : 0.0) : (arm.y < 0.0 ? 1.0 : 0.0));
vec2 bind = SCN_TEXCOORD7;
vec3 shoulder = vec3(arm.y * bind.x, bind.y, 0.0);
float elbowWeight = arm.x * (1.0 - smoothstep(bind.y - 0.20, bind.y - 0.12, p.y));
float ea = arm.y * (0.9 + 0.23 * sin(crowdTime * 5.0 + phase)) * wave * elbowWeight;
float ec = cos(ea), es = sin(ea);
vec3 elbow = shoulder + vec3(arm.y * 0.025, -0.18, 0.04), q = p - elbow;
p = elbow + vec3(ec * q.x - es * q.y, es * q.x + ec * q.y, q.z);
n = vec3(ec * n.x - es * n.y, es * n.x + ec * n.y, n.z);
float a = arm.y * 0.92 * wave * arm.x, ac = cos(a), sa = sin(a);
q = p - shoulder; p = shoulder + vec3(ac * q.x - sa * q.y, sa * q.x + ac * q.y, q.z);
n = vec3(ac * n.x - sa * n.y, sa * n.x + ac * n.y, n.z);
float swing = arm.y * 0.18 * sin(cycle) * walkBlend * arm.x, sc = cos(swing), ss = sin(swing);
q = p - shoulder; p = shoulder + vec3(q.x, sc * q.y - ss * q.z, ss * q.y + sc * q.z);
n = vec3(n.x, sc * n.y - ss * n.z, ss * n.y + sc * n.z);
if (walkBlend > 0.001 && kind.x < 0.5) {
    float side = p.x < 0.0 ? -1.0 : 1.0;
    float legPhase = cycle + (side < 0.0 ? 3.14159265 : 0.0);
    float legWeight = (kind.y == 2.0 || kind.y == 3.0) ? 1.0 : 0.32;
    legWeight *= 1.0 - smoothstep(joints.x - 0.05, joints.x + 0.02, p.y);
    float angle = 0.24 * cos(legPhase) * walkBlend * legWeight;
    float lc = cos(angle), ls = sin(angle);
    q = p - vec3(side * 0.06, joints.x, 0.0);
    p = vec3(q.x, lc * q.y - ls * q.z, ls * q.y + lc * q.z) + vec3(side * 0.06, joints.x, 0.0);
    n = vec3(n.x, lc * n.y - ls * n.z, ls * n.y + lc * n.z);
    // Both the boot and trouser hem receive the same lower-leg transform.
    float kneeWeight = 1.0 - smoothstep(joints.y - 0.025, joints.y + 0.04, VERTEX.y - frame.y);
    float bend = 0.42 * max(0.0, sin(legPhase)) * walkBlend * kneeWeight * legWeight;
    float kc = cos(bend), ks = sin(bend);
    q = p - vec3(side * 0.06, joints.y, 0.0);
    p = vec3(q.x, kc * q.y - ks * q.z, ks * q.y + kc * q.z) + vec3(side * 0.06, joints.y, 0.0);
    n = vec3(n.x, kc * n.y - ks * n.z, ks * n.y + kc * n.z);
    p.y -= joints.x * (1.0 - cos(0.24 * cos(cycle))) * walkBlend;
}
VERTEX = vec3(c * p.x + s * p.z, p.y, -s * p.x + c * p.z) + vec3(originXZ.x, frame.y, originXZ.y);
NORMAL = normalize(vec3(c * n.x + s * n.z, n.y, -s * n.x + c * n.z));";
    public static List<SCNMaterial> materials() =>
        new[] { CityMaterials.skin, CityMaterials.crowdCloth, CityMaterials.leather }.Select(source =>
        {
            var material = source.copy();
            var modifiers = material.shaderModifiers ?? new Dictionary<SCNShaderModifierEntryPoint, string>();
            var original = modifiers.TryGetValue(SCNShaderModifierEntryPoint.geometry, out var existing) ? existing : "#pragma body\n";
            // Keep palette conversion in the same geometry modifier.
            modifiers[SCNShaderModifierEntryPoint.geometry] = shader.Replace("#pragma body", original);
            material.shaderModifiers = modifiers;
            material.setValue(0f, "crowdTime");
            material.setValue(0f, "walkCycle");
            material.setValue(0f, "walkBlend");
            return material;
        }).ToList();

    /// Evaluate only the sole vertices, using the same joint transforms as the
    /// GPU. One planted foot stays on the ground throughout the stride.
    public readonly struct FootPlacement
    {
        public readonly List<Float3> soles; public readonly float phase, hip, knee;
        public FootPlacement(SCNGeometry geometry)
        {
            static float[] values(SCNGeometrySource source, int index)
            {
                var bytes = source.data; var result = new float[source.componentsPerVector];
                for (var axis = 0; axis < source.componentsPerVector; axis++)
                {
                    var offset = source.dataOffset + index * source.dataStride + axis * source.bytesPerComponent;
                    result[axis] = source.bytesPerComponent == 4 ? BitConverter.ToSingle(bytes, offset) : (float)BitConverter.ToDouble(bytes, offset);
                }
                return result;
            }
            var positions = geometry.sourcesFor(SCNGeometrySourceSemantic.vertex)[0]; var uv = geometry.sourcesFor(SCNGeometrySourceSemantic.texcoord);
            phase = values(uv[3], 0)[0]; var joints = values(uv[5], 0); hip = joints[0]; knee = joints[1];
            var found = new List<Float3>();
            for (var i = 0; i < positions.vectorCount; i++)
            {
                var p = values(positions, i);
                if (values(uv[6], i)[1] == 3 && p[1] < 0.19f) { found.Add(new Float3(p[0], p[1], p[2])); }
            }
            soles = found;
        }
        public float minimum(float cycle, float blend)
        {
            // PORT: Swift rebinds `cycle`; C# cannot redeclare the parameter.
            var cycle_ = cycle + this.phase; var bob = hip * (1 - cos(0.24f * cos(cycle_))) * blend;
            var lowest = float.PositiveInfinity;
            foreach (var side in new float[] { -1, 1 })
            {
                // PORT: `.pi` here is Float.pi (Swift.floatPi).
                float phase = cycle_ + (side < 0 ? floatPi : 0), angle = 0.24f * cos(phase) * blend;
                float c = cos(angle), s = sin(angle), bend = 0.42f * max(0, sin(phase)) * blend, kc = cos(bend), ks = sin(bend);
                foreach (var p in soles)
                {
                    if (!(p.x * side > 0)) { continue; }
                    float y = (p.y - hip) * c - p.z * s + hip, z = (p.y - hip) * s + p.z * c;
                    lowest = min(lowest, (y - knee) * kc - z * ks + knee - bob);
                }
            }
            return float.IsFinite(lowest) ? lowest : 0;
        }
    }
}
