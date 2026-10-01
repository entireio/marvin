import AppKit
import SceneKit
import SimulationCore
import simd

/// Bounded windblown sand: two batches and one shared-height drift mesh.
final class SandstormWorld {
    let root=SCNNode()
    private let drifts=SCNNode(),driftMaterial=SCNMaterial()
    private let grains=SCNNode(),haze=SCNNode()
    private var particles=[SIMD3<Float>]()
    private let count=960
    init(texture:NSImage) {
        root.name="Sandstorm";root.isHidden=true
        driftMaterial.lightingModel = .physicallyBased;driftMaterial.diffuse.contents=texture
        driftMaterial.diffuse.wrapS = .repeat;driftMaterial.diffuse.wrapT = .repeat
        driftMaterial.roughness.contents=1
        driftMaterial.shaderModifiers=[.geometry:"""
        #pragma arguments
        float deposition;
        #pragma varyings
        float driftDepth;
        #pragma body
        out.driftDepth=_geometry.color.r*deposition;
        _geometry.position.y+=out.driftDepth;
        _geometry.normal=normalize(_geometry.normal+float3(-_geometry.color.g,0,-_geometry.color.b)*deposition);
        """,.surface:"""
        #pragma transparent
        #pragma body
        float grain=dot(_surface.diffuse.rgb,float3(0.333));
        _surface.diffuse=float4(float3(0.57,0.38,0.23)*(0.82+grain*0.5),1.0);
        """,.fragment:"""
        #pragma transparent
        #pragma body
        if(in.driftDepth<0.0004) discard_fragment();
        float coverage=smoothstep(0.0004,0.025,in.driftDepth);
        _output.color.rgb *= coverage;
        _output.color.a=coverage;
        """]
        driftMaterial.setValue(Float(0.18),forKey:"deposition")
        var vertices=[SCNVector3](),normals=[SCNVector3](),uv=[CGPoint](),colors=[Float](),indices=[Int32]()
        let step=0.10
        for iz in -232..<232 { for ix in -232..<232 {
            let x=Double(ix)*step,z=Double(iz)*step
            let points=[(x,z),(x+step,z),(x+step,z+step),(x,z+step)]
            let depths=points.map{Sandstorm.deposit(x:$0.0,z:$0.1)}
            guard depths.max()!>0.00001 else { continue }
            let base=Int32(vertices.count)
            for (i,p) in points.enumerated() {
                let e=0.03
                vertices.append(SCNVector3(p.0,DirtCourse.height(x:p.0,z:p.1)+0.002,p.1))
                let nx=(DirtCourse.height(x:p.0-e,z:p.1)-DirtCourse.height(x:p.0+e,z:p.1))/(2*e)
                let nz=(DirtCourse.height(x:p.0,z:p.1-e)-DirtCourse.height(x:p.0,z:p.1+e))/(2*e)
                normals.append(SCNVector3(simd_normalize(SIMD3(nx,1,nz))));uv.append(CGPoint(x:p.0/2,y:p.1/2))
                colors += [Float(depths[i]),Float((Sandstorm.deposit(x:p.0+e,z:p.1)-Sandstorm.deposit(x:p.0-e,z:p.1))/(2*e)),Float((Sandstorm.deposit(x:p.0,z:p.1+e)-Sandstorm.deposit(x:p.0,z:p.1-e))/(2*e)),1]
            }
            indices += [base,base+2,base+1,base,base+3,base+2]
        }}
        let data=colors.withUnsafeBytes{Data($0)}
        let colorSource=SCNGeometrySource(data:data,semantic:.color,vectorCount:vertices.count,usesFloatComponents:true,componentsPerVector:4,bytesPerComponent:4,dataOffset:0,dataStride:16)
        let mesh=SCNGeometry(sources:[SCNGeometrySource(vertices:vertices),SCNGeometrySource(normals:normals),SCNGeometrySource(textureCoordinates:uv),colorSource],elements:[SCNGeometryElement(indices:indices,primitiveType:.triangles)])
        mesh.materials=[driftMaterial];drifts.geometry=mesh;drifts.castsShadow=false;root.addChildNode(drifts)
        for (node,isHaze) in [(grains,false),(haze,true)] {
            let mat=SCNMaterial();mat.lightingModel = .constant;mat.diffuse.contents=color(0xd5b17c)
            mat.isDoubleSided=true;mat.writesToDepthBuffer=false
            if isHaze {
                mat.diffuse.contents=WindblownDust.texture()
            }
            WindblownDust.configure(mat)
            let placeholder=SCNPlane(width:0,height:0);placeholder.materials=[mat];node.geometry=placeholder;node.castsShadow=false;root.addChildNode(node)
        }
        reset()
    }
    func reset() {
        particles=(0..<count).map { i in SIMD3(Float((i*37)%101)/101*64-32,Float((i*31)%97)/97*15-5,Float((i*47)%103)/103*64-32) }
    }
    func update(_ storm:Sandstorm,camera:SCNNode,dt:Double) {
        root.isHidden = !storm.enabled
        guard storm.enabled,dt>0 else { return }
        driftMaterial.setValue(Float(storm.accumulation),forKey:"deposition")
        let eye=camera.simdWorldPosition,transform=camera.simdWorldTransform
        let right=SIMD3(transform.columns.0.x,transform.columns.0.y,transform.columns.0.z)
        let up=SIMD3(transform.columns.1.x,transform.columns.1.y,transform.columns.1.z)
        let w=storm.wind(x:Double(eye.x),z:Double(eye.z)),wind=SIMD3<Float>(Float(w.x),0,Float(w.z))
        for i in particles.indices {
            particles[i] += wind*Float(dt)
            // Positions are camera-relative only when recycled; all visible
            // motion between recycling events is world-space wind advection.
            for axis in [0,2] { let d=particles[i][axis]-eye[axis];if abs(d)>32 { particles[i][axis]=eye[axis]+(d>0 ? -31.9:31.9) } }
            if i%12==0 {
                particles[i].y=Float(DirtCourse.height(x:Double(particles[i].x),z:Double(particles[i].z)))+0.3+Float(i%7)*0.12
            } else if abs(particles[i].y-eye.y)>10 { particles[i].y=eye.y+Float(i%17)-6 }
        }
        for isHaze in [false,true] {
            var vertices=[SCNVector3](),uv=[CGPoint](),colors=[Float](),indices=[Int32]()
            for i in particles.indices where (i%12==0)==isHaze {
                let p=particles[i],base=Int32(vertices.count)
                let across=isHaze ? right*Float(1.8+Double(i%7)*0.2):right*0.012
                let along=isHaze ? up*0.32:simd_normalize(wind)*0.13
                let distance=simd_length(p-eye)
                let alpha=Float(isHaze ? 0.28:0.34)*min(1,max(0,(32-distance)/8))
                for (x,y) in [(-1.0,-1.0),(1,-1),(1,1),(-1,1)] {
                    vertices.append(SCNVector3(p+across*Float(x)+along*Float(y)));uv.append(CGPoint(x:(x+1)/2,y:(y+1)/2))
                    colors += isHaze ? [0.68,0.49,0.29,alpha]:[1,1,1,alpha]
                }
                indices += [base,base+1,base+2,base,base+2,base+3]
            }
            let source=colors.withUnsafeBytes { SCNGeometrySource(data:Data($0),semantic:.color,vectorCount:vertices.count,usesFloatComponents:true,componentsPerVector:4,bytesPerComponent:4,dataOffset:0,dataStride:16) }
            let mesh=SCNGeometry(sources:[SCNGeometrySource(vertices:vertices),SCNGeometrySource(textureCoordinates:uv),source],elements:[SCNGeometryElement(indices:indices,primitiveType:.triangles)])
            let node=isHaze ? haze:grains;mesh.materials=node.geometry!.materials;node.geometry=mesh
        }
    }
}
