import SceneKit
import Metal
import SimulationCore
import simd

/// Opaque replacement patches, not overlays. Coarse terrain triangles are
/// removed beneath them so troughs cannot expose an undeformed second surface.
final class DeformableSand {
    let field=SandDeformation(),root=SCNNode()
    private let enabled = !CommandLine.arguments.contains("--benchmark-no-deformation")
    private let device=MTLCreateSystemDefaultDevice()!
    private var material:SCNMaterial!
    private struct Base { let node:SCNNode,geometry:SCNGeometry,x:Int,z:Int }
    private var bases:[SandDeformation.Key:Base]=[:]
    private final class Patch {
        var lastGrid:[Float]=[]
        let heights=SCNMaterialProperty()
        let node=SCNNode(),uv:SCNGeometrySource,base:[Float],normals:[SIMD3<Float>]
        init(_ key:SandDeformation.Key) {
            node.position=SCNVector3(Double(key.x)*4,0,Double(key.z)*4)
            node.name="Displaced dune sand";node.castsShadow = !CommandLine.arguments.contains("--benchmark-no-sand-shadows")
            var points=[CGPoint](),heights=[Float](),normals=[SIMD3<Float>]()
            var cornerNormals:[SIMD2<Int>:SIMD3<Float>]=[:]
            func normal(_ ix:Int,_ iz:Int)->SIMD3<Float> {
                let key=SIMD2(ix,iz)
                if let n=cornerNormals[key] { return n }
                let g=DesertTerrain.gradient(x:Double(ix)*2,z:Double(iz)*2)
                let n=simd_normalize(SIMD3(Float(-g.x),1,Float(-g.y)));cornerNormals[key]=n;return n
            }
            for j in -1...65 { for i in -1...65 {
                let x=Double(key.x)*4+Double(i)*SandDeformation.step,z=Double(key.z)*4+Double(j)*SandDeformation.step
                heights.append(Float(DesertTerrain.height(x:x,z:z)))
                if i>=0 && i<=64 && j>=0 && j<=64 {
                    points.append(CGPoint(x:x/4,y:z/4))
                    let ix=Int(floor(x/2)),iz=Int(floor(z/2)),u=Float(x/2-Double(ix)),v=Float(z/2-Double(iz))
                    let a=normal(ix,iz),b=normal(ix+1,iz),c=normal(ix,iz+1),d=normal(ix+1,iz+1)
                    normals.append(simd_normalize(u+v<=1 ? a+(b-a)*u+(c-a)*v:d+(c-d)*(1-u)+(b-d)*(1-v)))
                }
            }}
            base=heights;self.normals=normals;uv=SCNGeometrySource(textureCoordinates:points)
        }
    }
    private var patches:[SandDeformation.Key:Patch]=[:]
    private let elements:SCNGeometryElement
    private var topology = -1,pending=0.0
    private(set) var updates=0
    var patchCount:Int { patches.count }
    init() {
        var indices=[UInt32]()
        for j in 0..<64 { for i in 0..<64 {
            let a=UInt32(j*65+i),b=a+1,c=a+65,d=c+1
            indices += [a,c,b,b,c,d]
        }}
        elements=SCNGeometryElement(indices:indices,primitiveType:.triangles)
    }
    func configure(material:SCNMaterial,root:SCNNode) { self.material=material;root.addChildNode(self.root) }
    func register(_ node:SCNNode,x:Double,z:Double) {
        bases[.init(Int(x/64),Int(z/64))]=Base(node:node,geometry:node.geometry!,x:Int(x),z:Int(z))
    }
    func reset() {
        field.reset();patches.values.forEach{$0.node.removeFromParentNode()};patches.removeAll()
        for base in bases.values { base.node.geometry=base.geometry }
        topology = -1;pending=0;updates=0
    }
    func update(states:[Simulation],contacts:[[SandDeformation.Contact]],dt:Double) {
        guard enabled else { return }
        for i in contacts.indices { field.contactLayouts[i]=contacts[i] }
        pending += dt
        guard pending>=1.0/30-1e-8 else { return }
        let step=min(pending,0.05);pending=0
        guard !field.tiles.isEmpty || states.contains(where:{max(abs($0.x),abs($0.z))>DesertTerrain.townEdge+8}) else { return }
        field.begin(dt:step,positions:states.map{SIMD2($0.x,$0.z)})
        for (state,feet) in zip(states,contacts) { field.stamp(state,contacts:feet,dt:step) }
        field.settle(dt:step)
        if topology != field.topologyVersion {
            let removed=Set(patches.keys).subtracting(field.tiles.keys)
            for key in removed { patches.removeValue(forKey:key)?.node.removeFromParentNode() }
            for key in field.tiles.keys where patches[key]==nil {
                let patch=Patch(key);patches[key]=patch;root.addChildNode(patch.node)
            }
            for base in bases.values {
                let holes=Set(field.tiles.keys.filter{$0.x*4>=base.x && $0.x*4<base.x+64 && $0.z*4>=base.z && $0.z*4<base.z+64})
                guard !holes.isEmpty else { base.node.geometry=base.geometry;continue }
                var indices=[UInt32]()
                for j in 0..<32 { for i in 0..<32 {
                    let key=SandDeformation.Key(Int(floor(Double(base.x+i*2)/4)),Int(floor(Double(base.z+j*2)/4)))
                    guard !holes.contains(key) else { continue }
                    let a=UInt32(j*33+i),b=a+1,c=a+33,d=c+1
                    indices += [a,c,b,b,c,d]
                }}
                let mesh=SCNGeometry(sources:base.geometry.sources,elements:[SCNGeometryElement(indices:indices,primitiveType:.triangles)])
                mesh.materials=base.geometry.materials;base.node.geometry=mesh
            }
            topology=field.topologyVersion
        }
        for key in field.dirty { if let patch=patches[key] { rebuild(patch,key:key) } }
    }
    private struct Vertex { var position:SIMD4<Float>,normal:SIMD4<Float> }
    private func rebuild(_ patch:Patch,key:SandDeformation.Key) {
        let delta=field.grid(key)
        // Accumulate submillimetre settling until it changes the visible shape.
        if !patch.lastGrid.isEmpty && zip(delta,patch.lastGrid).allSatisfy({abs($0-$1)<0.0005}) { return }
        patch.lastGrid=delta
        if patch.node.geometry==nil {
            let buffer=device.makeBuffer(length:65*65*MemoryLayout<Vertex>.stride,options:.storageModeShared)!
            let vertices=buffer.contents().bindMemory(to:Vertex.self,capacity:65*65)
            var low:Float=1000,high:Float = -1000
            for j in 0...64 { for i in 0...64 {
                let h=patch.base[(j+1)*67+i+1],normal=patch.normals[j*65+i]
                vertices[j*65+i]=Vertex(position:SIMD4(Float(i)*0.0625,h,Float(j)*0.0625,1),normal:SIMD4(normal,0))
                low=min(low,h);high=max(high,h)
            }}
            let position=SCNGeometrySource(buffer:buffer,vertexFormat:.float3,semantic:.vertex,vertexCount:4225,dataOffset:0,dataStride:32)
            let normal=SCNGeometrySource(buffer:buffer,vertexFormat:.float3,semantic:.normal,vertexCount:4225,dataOffset:16,dataStride:32)
            let mesh=SCNGeometry(sources:[position,normal,patch.uv],elements:[elements]);mesh.materials=[material]
            mesh.shaderModifiers=[.geometry:Self.displacement]
            mesh.boundingBox=(SCNVector3(0,Double(low)-0.14,0),SCNVector3(4,Double(high)+0.11,4))
            patch.heights.minificationFilter = .nearest;patch.heights.magnificationFilter = .nearest;patch.heights.mipFilter = .none
            mesh.setValue(patch.heights,forKey:"duneHeights")
            patch.node.geometry=mesh
        }
        // Immutable small height texture, retained by SceneKit until its draw
        // completes. Static vertex/index buffers stay on the GPU throughout.
        let descriptor=MTLTextureDescriptor.texture2DDescriptor(pixelFormat:.r32Float,width:67,height:67,mipmapped:false)
        descriptor.storageMode = device.supportsFamily(.apple1) ? .shared:.managed;descriptor.usage = .shaderRead
        let texture=device.makeTexture(descriptor:descriptor)!
        delta.withUnsafeBytes { texture.replace(region:MTLRegionMake2D(0,0,67,67),mipmapLevel:0,withBytes:$0.baseAddress!,bytesPerRow:67*4) }
        patch.heights.contents=texture;updates += 1
    }
    private static let displacement="""
    #pragma arguments
    texture2d<float> duneHeights;
    #pragma body
    uint2 p=uint2(round(_geometry.position.xz*16.0))+uint2(1);
    float h=duneHeights.read(p).r;
    float dx=(duneHeights.read(p+uint2(1,0)).r-duneHeights.read(p-uint2(1,0)).r)*8.0;
    float dz=(duneHeights.read(p+uint2(0,1)).r-duneHeights.read(p-uint2(0,1)).r)*8.0;
    _geometry.position.y += h;
    _geometry.normal=normalize(_geometry.normal+float3(-dx,0,-dz)*_geometry.normal.y);
    """
}
