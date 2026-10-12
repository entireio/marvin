import SceneKit
import SimulationCore
import simd
import Metal

/// Surface-attached dust and splatter, concentrated near the running gear.
final class DirtCoating {
    private let contactTexture=SCNMaterialProperty()
    private let contactDevice=MTLCreateSystemDefaultDevice()!
    private var contactPlane=SCNVector4(0,0,0,0)
    private var lastDistance = 0.0
    private var uploadedLevel=0.0
    private let contactCoating = !CommandLine.arguments.contains("--benchmark-no-contact-coating")
    private var sandClock = -1.0,sandActive=false
    private(set) var amount = 0.0

    func install(on root: SCNNode, height: Double, wheelOffset: Double, rolling: Bool = false) {
        uploadContactPlane(contactPlane)
        root.enumerateChildNodes { node, _ in
            guard !node.isHidden, let source = node.geometry,
                  !source.materials.allSatisfy({ $0.lightingModel == .constant }) else { return }
            let geometry:SCNGeometry
            if source.sources(for:.texcoord).isEmpty,let positions=source.sources(for:.vertex).first {
                // SceneKit's generated textured pipeline requires a UV stream
                // even though this shader reads a fixed texture coordinate.
                // Imported CAD surfaces contain only positions and normals.
                let uv=SCNGeometrySource(data:Data(count:positions.vectorCount*8),semantic:.texcoord,vectorCount:positions.vectorCount,usesFloatComponents:true,componentsPerVector:2,bytesPerComponent:4,dataOffset:0,dataStride:8)
                geometry=SCNGeometry(sources:source.sources+[uv],elements:source.elements)
                geometry.materials=source.materials;geometry.levelsOfDetail=source.levelsOfDetail
                geometry.name=source.name
            } else { geometry=source.copy() as! SCNGeometry }
            node.geometry = geometry
            geometry.shaderModifiers = [.surface: Self.shader]
            geometry.setValue(NSValue(scnMatrix4:node.convertTransform(SCNMatrix4Identity,to:root)),forKey:"dirtToBody")
            geometry.setValue(height,forKey:"dirtHeight")
            geometry.setValue(wheelOffset/height,forKey:"dirtWheelX")
            geometry.setValue(contactTexture,forKey:"duneContact")
            geometry.setValue((rolling && node.name?.hasPrefix("ball:") == true) || node.name?.hasPrefix("link:") == true ? 1.0 : 0.0,forKey:"dirtRolling")
        }
    }
    func update(_ state: Simulation) {
        let resetting = !state.dirtTrack || state.distance < lastDistance
        if resetting { amount = 0 }
        let travel = max(0,state.distance-lastDistance)
        if state.dirtTrack && state.hasDirtContact {
            amount = min(1,amount+travel/75)
        }
        lastDistance = state.distance
        let level=floor(amount*32)/32,dirtChanged=level != uploadedLevel
        uploadedLevel=level
        let inSand=contactCoating && state.dirtTrack && state.sand?.tiles.isEmpty == false && max(abs(state.x),abs(state.z))>DesertTerrain.townEdge+8
        if (inSand && (state.elapsed-sandClock>=1.0/30 || state.elapsed<sandClock)) || (!inSand && sandActive) {
            let e=0.35
            let gx=(state.terrainHeight(x:state.x+e,z:state.z)-state.terrainHeight(x:state.x-e,z:state.z))/(2*e)
            let gz=(state.terrainHeight(x:state.x,z:state.z+e)-state.terrainHeight(x:state.x,z:state.z-e))/(2*e)
            let intercept=state.terrainHeight(x:state.x,z:state.z)-gx*state.x-gz*state.z
            let plane=SCNVector4(gx,gz,intercept,inSand ? 1:0)
            contactPlane=plane
            uploadContactPlane(plane)
            sandActive=inSand;sandClock=state.elapsed
        } else if dirtChanged { uploadContactPlane(contactPlane) }
    }
    /// Native screenshot A/B: verify the shared material uniform reaches the
    /// geometry modifier, and only changes the opaque contact rim.
    func diagnosticContact(_ visible:Bool) {
        var plane=contactPlane;if !visible { plane.w=0 }
        uploadContactPlane(plane)
    }
    private func uploadContactPlane(_ plane:SCNVector4) {
        let descriptor=MTLTextureDescriptor.texture2DDescriptor(pixelFormat:.rgba32Float,width:2,height:1,mipmapped:false)
        descriptor.storageMode = contactDevice.supportsFamily(.apple1) ? .shared:.managed;descriptor.usage = .shaderRead
        let texture=contactDevice.makeTexture(descriptor:descriptor)!
        let values:[Float]=[Float(plane.x),Float(plane.y),Float(plane.z),Float(plane.w),Float(uploadedLevel),0,0,0]
        values.withUnsafeBytes { texture.replace(region:MTLRegionMake2D(0,0,2,1),mipmapLevel:0,withBytes:$0.baseAddress!,bytesPerRow:32) }
        // Change the texture resource, not per-part shader uniforms. SceneKit
        // retains each immutable resource through its in-flight GPU reads.
        contactTexture.contents=texture
    }
    private static let shader = """
    #pragma arguments
    float4x4 dirtToBody;
    float dirtHeight;
    float dirtWheelX;
    float dirtRolling;
    texture2d<float> duneContact;
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
    float dirtAmount=duneContact.read(uint2(1,0)).r;
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
    float4 dunePlane=duneContact.read(uint2(0));
    if (dunePlane.w>0.5) {
    float3 world=(scn_node.modelTransform*float4(local,1.0)).xyz;
    float sandHeight=dot(dunePlane.xy,world.xz)+dunePlane.z;
    float sandGrain=dirtNoise(world*130.0);
    float rim=(1.0-smoothstep(-0.004,0.035,world.y-sandHeight+(sandGrain-0.5)*0.016))*dunePlane.w;
    float3 sand=float3(0.64,0.43,0.23)*(0.84+0.25*sandGrain);
    _surface.diffuse.rgb=mix(_surface.diffuse.rgb,sand,rim*0.88);
    _surface.roughness=mix(_surface.roughness,0.96,rim);
    _surface.metalness *= 1.0-rim;
    _surface.selfIllumination.rgb *= 1.0-rim;
    }
    """
}
