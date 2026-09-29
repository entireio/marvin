import AppKit
import SceneKit
import simd

/// Shared scanned surfaces. All maps are bundled; there is no network work at runtime.
enum CityMaterials {
    static func asset(_ name:String)->URL {
        Bundle.main.resourceURL!.appendingPathComponent("City/"+name)
    }
    static func scanned(_ name:String,normal:CGFloat,metal:CGFloat=0)->SCNMaterial {
        let m=SCNMaterial();m.lightingModel = .physicallyBased
        m.diffuse.contents=asset(name+"-base.jpg")
        m.normal.contents=asset(name+"-normal.jpg");m.normal.intensity=normal
        m.roughness.contents=asset(name+"-rough.jpg");m.metalness.contents=metal
        for p in [m.diffuse,m.normal,m.roughness] {
            p.wrapS = .repeat;p.wrapT = .repeat;p.mipFilter = .linear;p.maxAnisotropy=4
        }
        // Scans supply spatial variation; vertex colors supply district/outfit palettes.
        tint(m,surface:"""
        float grain=dot(_surface.diffuse.rgb,float3(0.2126,0.7152,0.0722));
        _surface.diffuse.rgb=float3(in.cityTint)*(0.62+grain*0.65);
        """)
        return m
    }
    static func tint(_ m:SCNMaterial,surface:String="_surface.diffuse.rgb=float3(in.cityTint);") {
        m.shaderModifiers=[.geometry:"""
        #pragma varyings
        half3 cityTint;
        #pragma body
        // Palette bytes are sRGB; PBR surface inputs are linear.
        out.cityTint=half3(pow(max(_geometry.color.rgb,float3(0.0)),float3(2.2)));
        """,.surface:"#pragma body\n"+surface]
    }
    static let plaster=scanned("plaster",normal:0.85)
    static let cloth:SCNMaterial = {
        let m=scanned("cloth",normal:0.28)
        tint(m,surface:"float grain=dot(_surface.diffuse.rgb,float3(0.2126,0.7152,0.0722)); _surface.diffuse.rgb=float3(in.cityTint)*(0.82+grain*0.22);")
        return m
    }()
    static let crowdCloth:SCNMaterial = {
        let m=cloth.copy() as! SCNMaterial;m.isDoubleSided=true;return m
    }()
    static let metal=scanned("metal",normal:0.55,metal:0.55)
    static let skin:SCNMaterial = {
        let m=SCNMaterial();m.lightingModel = .physicallyBased
        m.diffuse.contents=NSColor.white;m.roughness.contents=0.64;tint(m)
        return m
    }()
    static let leather:SCNMaterial = {
        let m=SCNMaterial();m.lightingModel = .physicallyBased
        m.diffuse.contents=NSColor.white;m.roughness.contents=0.78;tint(m)
        return m
    }()
}

/// Indexed authored humans, batched by eight-meter cells, with independent crowd LOD.
/// Only the small idle-animation set has individual nodes. No crowd physics bodies.
final class TownCrowd {
    private struct Model:Decodable { let vertices:[[Float]],indices:[Int32] }
    private final class Batch {
        var vertices:[SCNVector3]=[],normals:[SCNVector3]=[],uv:[CGPoint]=[],colors:[Float]=[]
        var groups:[[Int32]]=[[],[],[]]
        func add(_ model:Model,at position:SIMD3<Float>,yaw:Float,index:Int) {
            let c=cos(yaw),s=sin(yaw),base=Int32(vertices.count)
            let outfits:[UInt32]=[0x886557,0x617677,0xb7aa8d,0x77735c,0x686e71,0xa48660,0x918378,0xc6bda6]
            let skins:[UInt32]=[0xb68b70,0x835c48,0xc2a084,0x9c755c,0xa37c61,0xc4a591]
            let palette:[UInt32]=[skins[index%6],outfits[index%8],0x665d50,0x433c34,outfits[(index+3)%8],0xc1b8a7,0x342b25,0x8a8170]
            for v in model.vertices {
                vertices.append(SCNVector3(position+SIMD3(v[0]*c+v[2]*s,v[1],-v[0]*s+v[2]*c)))
                normals.append(SCNVector3(v[3]*c+v[5]*s,v[4],-v[3]*s+v[5]*c))
                uv.append(CGPoint(x:Double(v[6])*12,y:Double(v[7])*12))
                let color=palette[Int(v[8])]
                colors += [Float((color>>16)&255)/255,Float((color>>8)&255)/255,Float(color&255)/255,1]
            }
            for i in stride(from:0,to:model.indices.count,by:3) {
                let semantic=Int(model.vertices[Int(model.indices[i])][8])
                let slot=(semantic==1 || semantic==2 || semantic==4) ? 1 : (semantic==0 || semantic==5 ? 0:2)
                groups[slot] += model.indices[i..<i+3].map{$0+base}
            }
        }
        var triangles:Int { groups.reduce(0){$0+$1.count/3} }
        func geometry()->SCNGeometry {
            let color=colors.withUnsafeBytes { SCNGeometrySource(data:Data($0),semantic:.color,vectorCount:vertices.count,usesFloatComponents:true,componentsPerVector:4,bytesPerComponent:4,dataOffset:0,dataStride:16) }
            let g=SCNGeometry(sources:[SCNGeometrySource(vertices:vertices),SCNGeometrySource(normals:normals),SCNGeometrySource(textureCoordinates:uv),color],elements:groups.map{SCNGeometryElement(indices:$0,primitiveType:.triangles)})
            g.materials=[CityMaterials.skin,CityMaterials.crowdCloth,CityMaterials.leather]
            return g
        }
    }
    private struct Cell { let origin:SIMD3<Float>;let lod:[Batch] }
    private var models:[String:[Model]]=[:]
    private var cells:[String:Cell]=[:]
    private(set) var triangles=0,farTriangles=0,cellCount=0
    private var modelCount=0
    var valid:Bool { modelCount==12 && cellCount>0 && triangles<2_100_000 && farTriangles<115_000 }
    init() {
        do { models=try JSONDecoder().decode([String:[Model]].self,from:Data(contentsOf:CityMaterials.asset("crowd.json"))) }
        catch { assertionFailure("Missing or invalid bundled crowd: \(error)") }
        modelCount=models.count
    }
    func add(x:Double,y:Double,z:Double,yaw:Double,index:Int,seated:Bool,animated:Bool)->SCNNode? {
        let key="\(index%2==0 ? "male":"female")-\(seated ? "sit":"stand")-\((index/2)%3)"
        guard let model=models[key] else { return nil }
        let position=SIMD3(Float(x),Float(y),Float(z))
        if animated {
            let lod=(0..<3).map{_ in Batch()}
            for i in 0..<3 { lod[i].add(model[i],at:.zero,yaw:0,index:index) }
            let near=lod[0].geometry()
            near.levelsOfDetail=[SCNLevelOfDetail(geometry:lod[1].geometry(),worldSpaceDistance:8),SCNLevelOfDetail(geometry:lod[2].geometry(),worldSpaceDistance:24)]
            let node=SCNNode(geometry:near);node.simdPosition=position;node.eulerAngles.y=CGFloat(yaw)
            node.castsShadow=false
            triangles += lod[0].triangles;farTriangles += lod[2].triangles
            return node
        }
        let ix=Int(floor(x/8)),iz=Int(floor(z/8)),cellKey="\(ix),\(iz)"
        if cells[cellKey]==nil { cells[cellKey]=Cell(origin:SIMD3(Float(ix*8+4),0,Float(iz*8+4)),lod:(0..<3).map{_ in Batch()}) }
        let cell=cells[cellKey]!
        for i in 0..<3 { cell.lod[i].add(model[i],at:position-cell.origin,yaw:Float(yaw),index:index) }
        return nil
    }
    func finish(into root:SCNNode) {
        for key in cells.keys.sorted() {
            let cell=cells[key]!,near=cell.lod[0].geometry()
            near.levelsOfDetail=[SCNLevelOfDetail(geometry:cell.lod[1].geometry(),worldSpaceDistance:10),SCNLevelOfDetail(geometry:cell.lod[2].geometry(),worldSpaceDistance:26)]
            let node=SCNNode(geometry:near);node.simdPosition=cell.origin
            node.name="Crowd cell \(key)";node.castsShadow=false;root.addChildNode(node)
            triangles += cell.lod[0].triangles;farTriangles += cell.lod[2].triangles
        }
        cellCount=cells.count
        cells.removeAll();models.removeAll()
    }
}
