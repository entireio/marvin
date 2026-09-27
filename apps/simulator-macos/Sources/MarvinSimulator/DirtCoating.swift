import SceneKit
import SimulationCore

/// Surface-attached dust and splatter, concentrated near the running gear.
final class DirtCoating {
    private var surfaces: [SCNGeometry] = []
    private var lastDistance = 0.0
    private(set) var amount = 0.0

    func install(on root: SCNNode, height: Double, wheelOffset: Double, rolling: Bool = false) {
        root.enumerateChildNodes { node, _ in
            guard !node.isHidden, let source = node.geometry,
                  !source.materials.allSatisfy({ $0.lightingModel == .constant }) else { return }
            let geometry = source.copy() as! SCNGeometry
            node.geometry = geometry
            geometry.shaderModifiers = [.surface: Self.shader]
            geometry.setValue(NSValue(scnMatrix4:node.convertTransform(SCNMatrix4Identity,to:root)),forKey:"dirtToBody")
            geometry.setValue(height,forKey:"dirtHeight")
            geometry.setValue(wheelOffset/height,forKey:"dirtWheelX")
            geometry.setValue(0.0,forKey:"dirtAmount")
            geometry.setValue((rolling && node.name?.hasPrefix("ball:") == true) || node.name?.hasPrefix("link:") == true ? 1.0 : 0.0,forKey:"dirtRolling")
            surfaces.append(geometry)
        }
    }
    func update(_ state: Simulation) {
        if !state.dirtTrack || state.distance < lastDistance { amount = 0 }
        let travel = max(0,state.distance-lastDistance)
        if state.dirtTrack && !state.airborne {
            amount = min(1,amount+travel/75)
        }
        lastDistance = state.distance
        for surface in surfaces { surface.setValue(amount,forKey:"dirtAmount") }
    }
    private static let shader = """
    #pragma arguments
    float4x4 dirtToBody;
    float dirtHeight;
    float dirtWheelX;
    float dirtAmount;
    float dirtRolling;
    #pragma declaration
    float dirtHash(float3 p) { return fract(sin(dot(p,float3(127.1,311.7,74.7)))*43758.5453); }
    float dirtNoise(float3 p) {
        float3 i = floor(p), f = fract(p); f = f*f*(3.0-2.0*f);
        return mix(mix(mix(dirtHash(i),dirtHash(i+float3(1,0,0)),f.x),
                       mix(dirtHash(i+float3(0,1,0)),dirtHash(i+float3(1,1,0)),f.x),f.y),
                   mix(mix(dirtHash(i+float3(0,0,1)),dirtHash(i+float3(1,0,1)),f.x),
                       mix(dirtHash(i+float3(0,1,1)),dirtHash(i+float3(1,1,1)),f.x),f.y),f.z);
    }
    #pragma body
    float3 local = (scn_node.inverseModelViewTransform * float4(_surface.position,1.0)).xyz;
    float3 p = (dirtToBody * float4(local,1.0)).xyz / dirtHeight;
    float low = mix(1.0-smoothstep(0.08,0.58,p.y),1.0,dirtRolling);
    float nearWheel = exp(-pow((abs(p.x)-dirtWheelX)*9.0,2.0));
    float rear = 1.0-smoothstep(-0.22,0.24,p.z);
    float patches = dirtNoise(p*28.0);
    float specks = smoothstep(0.58,0.76,dirtNoise(p*145.0));
    float splash = smoothstep(0.38,0.68,patches) * low * (0.45+0.35*nearWheel+0.20*rear);
    float dust = (0.04+low*0.22)*(0.55+0.45*patches);
    float coverage = clamp(dirtAmount*(dust+splash*0.85+specks*low*0.40),0.0,0.92);
    float3 soil = mix(float3(0.19,0.105,0.045),float3(0.42,0.29,0.16),patches);
    _surface.diffuse.rgb = mix(_surface.diffuse.rgb,soil,coverage);
    _surface.roughness = mix(_surface.roughness,1.0,coverage);
    _surface.metalness *= 1.0-coverage;
    _surface.selfIllumination.rgb *= 1.0-coverage;
    """
}
