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
    /// Periodic value noise baked only during asset preparation, never in a fragment shader.
    static func surfaceNoise(_ u:Double,_ v:Double,cells:Int,seed:Int)->Double {
        let x=u*Double(cells),y=v*Double(cells),ix=Int(floor(x)),iy=Int(floor(y))
        let fx=x-floor(x),fy=y-floor(y),tx=fx*fx*(3-2*fx),ty=fy*fy*(3-2*fy)
        func hash(_ a:Int,_ b:Int)->Double {
            var n=UInt32(truncatingIfNeeded:((a%cells+cells)%cells) &* 374761393 &+ ((b%cells+cells)%cells) &* 668265263 &+ seed &* 1274126177)
            n=(n ^ (n >> 13)) &* 1274126177
            return Double((n ^ (n >> 16)) & 65535)/65535
        }
        let lo=hash(ix,iy)*(1-tx)+hash(ix+1,iy)*tx,hi=hash(ix,iy+1)*(1-tx)+hash(ix+1,iy+1)*tx
        return lo*(1-ty)+hi*ty
    }
    /// An atlas of maintenance histories, with gravity-aligned and localized masks.
    /// Cell zero is neutral for non-building geometry. Each other cell has its own seed.
    private static func plasterWear()->NSImage {
        let tileSize=128,size=tileSize*8
        let bitmap=NSBitmapImageRep(bitmapDataPlanes:nil,pixelsWide:size,pixelsHigh:size,bitsPerSample:8,samplesPerPixel:4,hasAlpha:true,isPlanar:false,colorSpaceName:.deviceRGB,bytesPerRow:size*4,bitsPerPixel:32)!
        let pixels=bitmap.bitmapData!
        for tile in 0..<64 {
            let history=tile/16,seed=tile*197+31
            let centerX=0.20+Double((tile*17)%61)/100
            let baseHeight=0.12+Double((tile*13)%24)/100
            for y in 0..<tileSize { for x in 0..<tileSize {
                let u=Double(x)/Double(tileSize-1),v=1-Double(y)/Double(tileSize-1)
                let macro=surfaceNoise(u,v,cells:5,seed:seed)
                let fine=surfaceNoise(u,v,cells:27,seed:seed+17)
                let warp=(macro-0.5)*0.14+(fine-0.5)*0.045
                let base=max(0,1-v/(baseHeight+warp))
                var level=1.0
                if tile>0 { level=0.975+(macro-0.5)*0.025 }
                if history>0 {
                    let exposure=history==1 ? 0.12:(history==2 ? 0.08:0.20)
                    level -= base*exposure*(0.65+macro*0.55)
                }
                if history==2 {
                    // Uneven resurfacing: one repaired patch, not a scatter of identical stains.
                    let radiusX=0.13+Double(tile%5)*0.022,radiusY=0.15+Double(tile%3)*0.04
                    let cy=0.25+Double(tile%4)*0.12
                    let boundary=pow(abs((u-centerX)/radiusX),4)+pow(abs((v-cy)/radiusY),4)+warp*12
                    let patch=max(0,min(1,(1.1-boundary)*5))
                    level=level*(1-patch)+patch*(tile%2==0 ? 0.995:0.94)
                }
                if history==3 {
                    // A narrow runoff/soot mark tied to the roof edge, fading before ground.
                    let drift=centerX+(macro-0.5)*0.065
                    let stain=exp(-pow((u-drift)/(0.025+(1-v)*0.035),2))
                        * max(0,min(1,(v-0.24)*1.6))*(0.65+fine*0.35)
                    level -= stain*0.13
                }
                let i=((tile/8*tileSize+y)*size+tile%8*tileSize+x)*4
                pixels[i]=UInt8(255*max(0.60,min(1,level)))
                pixels[i+1]=UInt8(255*max(0.60,min(1,level-(1-level)*0.09)))
                pixels[i+2]=UInt8(255*max(0.60,min(1,level-(1-level)*0.19)))
                pixels[i+3]=255
            }}
        }
        let image=NSImage(size:NSSize(width:size,height:size));image.addRepresentation(bitmap);return image
    }
    static let plaster:SCNMaterial = {
        let m=scanned("plaster",normal:0.95)
        m.multiply.contents=plasterWear();m.multiply.mappingChannel=1
        m.multiply.wrapS = .clamp;m.multiply.wrapT = .clamp
        m.multiply.mipFilter = .linear;m.multiply.maxAnisotropy=4
        return m
    }()
    static let cloth:SCNMaterial = {
        let m=scanned("cloth",normal:0.28);m.isDoubleSided=true
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
        func add(_ model:Model,at position:SIMD3<Float>,yaw:Float,index:Int,seated:Bool) {
            let c=cos(yaw),s=sin(yaw),base=Int32(vertices.count)
            let outfits:[UInt32]=[0x746354,0x566866,0xa99b81,0x6d6a53,0x5b6266,0x907451,0x82756b,0xb8ab91]
            let skins:[UInt32]=[0xb68b70,0x835c48,0xc2a084,0x9c755c,0xa37c61,0xc4a591]
            // Independent deterministic hashes prevent matching outfit/skin/pose stripes.
            let seed=UInt32(truncatingIfNeeded:index &* 747796405 &+ 2891336453)
            let mix=Int((seed ^ (seed >> 16)) & 0x7fffffff)
            let palette:[UInt32]=[skins[(mix/7)%6],outfits[mix%8],outfits[(mix/13)%8],0x433c34,outfits[(mix/23)%8],0xc1b8a7,0x342b25,0x8a8170]
            let scale=SIMD3<Float>(0.90+Float(mix%19)*0.012,0.95+Float((mix/19)%11)*0.01,0.94+Float((mix/209)%13)*0.012)
            let turn=Float((mix/2717)%17-8)*0.055
            let neck:Float=seated ? 0.51:0.875
            let lean=Float((mix/31)%9-4)*0.013
            func posed(_ p:SIMD3<Float>,_ normal:SIMD3<Float>)->(SIMD3<Float>,SIMD3<Float>) {
                var v=p,n=normal
                let weight=min(1,max(0,(p.y-neck)/0.045)),a=turn*weight
                let cc=cos(a),ss=sin(a)
                v.x=p.x*cc+p.z*ss;v.z = -p.x*ss+p.z*cc
                n.x=normal.x*cc+normal.z*ss;n.z = -normal.x*ss+normal.z*cc
                v.z += max(0,p.y-(seated ? 0.10:0.46))*lean;n.y -= lean*n.z
                v *= scale;n=simd_normalize(n/scale)
                return (position+SIMD3(v.x*c+v.z*s,v.y,-v.x*s+v.z*c),SIMD3(n.x*c+n.z*s,n.y,-n.x*s+n.z*c))
            }
            for v in model.vertices {
                let (p,n)=posed(SIMD3(v[0],v[1],v[2]),SIMD3(v[3],v[4],v[5]))
                vertices.append(SCNVector3(p));normals.append(SCNVector3(n))
                uv.append(CGPoint(x:Double(v[6])*12,y:Double(v[7])*12))
                let color=palette[Int(v[8])]
                colors += [Float((color>>16)&255)/255,Float((color>>8)&255)/255,Float(color&255)/255,1]
            }
            for i in stride(from:0,to:model.indices.count,by:3) {
                let semantic=Int(model.vertices[Int(model.indices[i])][8])
                let slot=(semantic==1 || semantic==2 || semantic==4) ? 1 : (semantic==0 || semantic==5 ? 0:2)
                groups[slot] += model.indices[i..<i+3].map{$0+base}
            }
            // A light shoulder mantle changes selected silhouettes. It is batched
            // with the existing cloth material and costs only 32 triangles/person.
            if mix%5==0 {
                let ink=outfits[(mix/29)%8],top:Float=seated ? 0.49:0.86
                func point(_ ring:Int,_ k:Int)->SIMD3<Float> {
                    let a=Float.pi+Float(k)*Float.pi/8
                    let radius:Float=ring==0 ? 0.155:(ring==1 ? 0.213:0.226)
                    return SIMD3(cos(a)*radius,top-Float(ring)*0.135,-0.005+sin(a)*radius*0.73)
                }
                for ring in 0..<2 { for k in 0..<8 {
                    for points in [[point(ring,k),point(ring+1,k),point(ring+1,k+1)], [point(ring,k),point(ring+1,k+1),point(ring,k+1)]] {
                        let normal=simd_normalize(simd_cross(points[1]-points[0],points[2]-points[0]))
                        let start=Int32(vertices.count)
                        for q in points {
                            let (v,n)=posed(q,normal)
                            vertices.append(SCNVector3(v));normals.append(SCNVector3(n));uv.append(CGPoint(x:Double(q.x)*18,y:Double(q.y)*18))
                            colors += [Float((ink>>16)&255)/255,Float((ink>>8)&255)/255,Float(ink&255)/255,1]
                        }
                        groups[1] += [start,start+1,start+2]
                    }
                }}
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
            for i in 0..<3 { lod[i].add(model[i],at:.zero,yaw:0,index:index,seated:seated) }
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
        for i in 0..<3 { cell.lod[i].add(model[i],at:position-cell.origin,yaw:Float(yaw),index:index,seated:seated) }
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
