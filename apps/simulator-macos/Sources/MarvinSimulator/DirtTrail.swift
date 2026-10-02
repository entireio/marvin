import SceneKit
import SimulationCore

/// Batched ground decals: enough history for a three-lap race, with bounded memory.
final class DirtTrail {
    enum Style { case tracks, tires }
    let root = SCNNode()
    private let style: Style
    private var uv: [CGPoint] = []
    private let ink = material(0xffffff, roughness: 1)
    private var normals: [SCNVector3] = []
    private var strengths: [Float] = []
    private var chunks: [SCNNode] = []
    private var vertices: [SCNVector3] = []
    private var indices: [Int32] = []
    private var chunkIndex = 0
    private var previous: (x: Double, z: Double, heading: Double)?
    private var remainder = 0.0
    private(set) var count = 0

    init(style: Style = .tracks) {
        self.style = style
        root.name = "Surface-aware ground impressions"
        ink.lightingModel = .constant
        ink.blendMode = .multiply
        ink.shaderModifiers = [.geometry: """
        #pragma varyings
        float impressionStrength;
        float trailBorn;
        float trailDistance;
        #pragma body
        out.impressionStrength=_geometry.color.r;
        out.trailBorn=_geometry.color.g;
        out.trailDistance=length((scn_node.modelViewTransform*_geometry.position).xyz);
        """, .surface: """
        #pragma transparent
        #pragma body
        float2 uv=_surface.diffuseTexcoord;
        float across=smoothstep(0.0,0.15,uv.x)*(1.0-smoothstep(0.85,1.0,uv.x));
        float along=smoothstep(0.0,0.24,uv.y)*(1.0-smoothstep(0.65,1.0,uv.y));
        float shade=1.0-in.impressionStrength*across*along;
        _surface.diffuse=float4(float3(shade),1.0);
        """,.fragment:"""
        #pragma arguments
        float stormTime;
        float stormActive;
        #pragma body
        if(stormActive>0.5) {
            float age=max(0.0,stormTime-in.trailBorn);
            float visibility=exp(-age/10.0)*(1.0-smoothstep(3.0,65.0,in.trailDistance));
            // Multiply decals must fade toward white, not toward brown fog.
            _output.color=float4(mix(float3(1.0),_surface.diffuse.rgb,visibility),1.0);
        }
        """]
        ink.setValue(Float(0),forKey:"stormTime");ink.setValue(Float(0),forKey:"stormActive")
        // The housing-fitted rollers have smooth, flat rubber tread.
        ink.isDoubleSided = true
        ink.writesToDepthBuffer = false
    }
    func reset() {
        chunks.forEach { $0.removeFromParentNode() }; chunks.removeAll()
        vertices.removeAll(); normals.removeAll(); strengths.removeAll(); uv.removeAll(); indices.removeAll(); chunkIndex = 0
        previous = nil; remainder = 0; count = 0
    }
    func update(_ state: Simulation, contacts: [(x: Double, z: Double, width: Double)]) {
        ink.setValue(Float(state.storm.elapsed),forKey:"stormTime")
        ink.setValue(Float(state.storm.enabled ? 1:0),forKey:"stormActive")
        guard state.hasDirtContact else { previous = nil; remainder = 0; return }
        defer { previous = (state.x, state.z, state.heading) }
        guard let previous else { return }
        let dx = state.x-previous.x, dz = state.z-previous.z
        let distance = hypot(dx,dz)
        guard distance > 1e-8 else { return }
        // Do not draw connecting lines after a teleport/reset.
        guard distance < 2 else { remainder = 0; return }
        let angle = atan2(sin(state.heading-previous.heading),cos(state.heading-previous.heading))
        let spacing = 0.065
        var travel = spacing-remainder
        while travel <= distance {
            let t = travel/distance, heading = previous.heading+angle*t
            let x = previous.x+dx*t, z = previous.z+dz*t
            for contact in contacts {
                let load = max(abs(state.x),abs(state.z))>DesertTerrain.townEdge+8 ? (state.sand?.contactWeight(state,.init(x:contact.x,z:contact.z,width:contact.width,length:0.1)) ?? 1):1
                guard load>0 else { continue }
                if vertices.count == 256*4 {
                    flush(); chunkIndex = (chunkIndex+1)%128
                    vertices.removeAll(keepingCapacity:true); normals.removeAll(keepingCapacity:true); strengths.removeAll(keepingCapacity:true); uv.removeAll(keepingCapacity:true); indices.removeAll(keepingCapacity:true)
                }
                let base = Int32(vertices.count)
                let dune=max(0,min(1,(max(abs(x),abs(z))-DesertTerrain.townEdge)/30))
                let strength=Float((style == .tracks ? 0.24:0.19)*(1-dune)+0.14*dune)*Float(load)
                let e=0.04
                let nx=state.terrainHeight(x:x-e,z:z)-state.terrainHeight(x:x+e,z:z)
                let nz=state.terrainHeight(x:x,z:z-e)-state.terrainHeight(x:x,z:z+e)
                let length=sqrt(nx*nx+4*e*e+nz*nz)
                let normal=SCNVector3(nx/length,2*e/length,nz/length)
                for (side, along) in [(-1.0,-1.0),(1,-1),(1,1),(-1,1)] {
                    let lateral = contact.x+side*contact.width/2
                    let forward = contact.z+along*(style == .tracks ? 0.021 : spacing*0.53)
                    let px = x+cos(heading)*lateral+sin(heading)*forward
                    let pz = z-sin(heading)*lateral+cos(heading)*forward
                    normals.append(normal);strengths += [strength,Float(state.storm.elapsed),0,1]
                    uv.append(CGPoint(x:(side+1)/2,y:(along+1)/2))
                    vertices.append(SCNVector3(px,state.terrainHeight(x:px,z:pz)+0.007,pz))
                }
                indices += [base,base+2,base+1,base,base+3,base+2]
                count += 1
            }
            travel += spacing
        }
        remainder = (remainder+distance).truncatingRemainder(dividingBy:spacing)
        flush()
    }
    private func flush() {
        guard !vertices.isEmpty else { return }
        if chunkIndex == chunks.count {
            let node = SCNNode(); node.castsShadow = false
            root.addChildNode(node); chunks.append(node)
        }
        let tone=strengths.withUnsafeBytes { SCNGeometrySource(data:Data($0),semantic:.color,vectorCount:vertices.count,usesFloatComponents:true,componentsPerVector:4,bytesPerComponent:4,dataOffset:0,dataStride:16) }
        let geometry = SCNGeometry(sources:[SCNGeometrySource(vertices:vertices),SCNGeometrySource(textureCoordinates:uv),
            SCNGeometrySource(normals:normals),tone],
            elements:[SCNGeometryElement(indices:indices,primitiveType:.triangles)])
        geometry.materials = [ink]; chunks[chunkIndex].geometry = geometry
    }
}
