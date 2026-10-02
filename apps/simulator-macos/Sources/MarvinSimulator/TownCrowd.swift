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
                if tile>0 { level=0.955+(macro-0.5)*0.12+(fine-0.5)*0.035 }
                if history>0 {
                    let exposure=history==1 ? 0.19:(history==2 ? 0.10:0.30)
                    level -= base*exposure*(0.65+macro*0.55)
                }
                if history==2 {
                    // Uneven resurfacing: one repaired patch, not a scatter of identical stains.
                    let radiusX=0.13+Double(tile%5)*0.022,radiusY=0.15+Double(tile%3)*0.04
                    let cy=0.25+Double(tile%4)*0.12
                    let boundary=pow(abs((u-centerX)/radiusX),4)+pow(abs((v-cy)/radiusY),4)+warp*12
                    let patch=max(0,min(1,(1.1-boundary)*5))
                    level=level*(1-patch)+patch*(tile%2==0 ? 0.995:0.85)
                }
                if history==3 {
                    // A narrow runoff/soot mark tied to the roof edge, fading before ground.
                    let drift=centerX+(macro-0.5)*0.065
                    let stain=exp(-pow((u-drift)/(0.025+(1-v)*0.035),2))
                        * max(0,min(1,(v-0.24)*1.6))*(0.65+fine*0.35)
                    level -= stain*0.21
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
        let m=scanned("plaster",normal:1.0)
        tint(m,surface:"""
        float grain=dot(_surface.diffuse.rgb,float3(0.2126,0.7152,0.0722));
        _surface.diffuse.rgb=float3(in.cityTint)*(0.50+grain*1.1);
        """)
        m.multiply.contents=plasterWear();m.multiply.mappingChannel=1
        m.multiply.wrapS = .clamp;m.multiply.wrapT = .clamp
        m.multiply.mipFilter = .linear;m.multiply.maxAnisotropy=4
        return m
    }()
    static let adobe:SCNMaterial = {
        let m=scanned("adobe",normal:0.85)
        tint(m,surface:"""
        float grain=dot(_surface.diffuse.rgb,float3(0.2126,0.7152,0.0722));
        _surface.diffuse.rgb=float3(in.cityTint)*(0.60+grain*1.25);
        """)
        m.multiply.contents=plasterWear();m.multiply.mappingChannel=1
        m.multiply.wrapS = .clamp;m.multiply.wrapT = .clamp;m.multiply.mipFilter = .linear
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
/// Spectator reactions stay batched; only walking residents use individual nodes.
final class TownCrowd {
    private struct Model:Decodable {
        let vertices:[[Float]],indices:[Int32],arms:[Bool],armTops:[Float]
        private enum CodingKeys:String,CodingKey { case vertices,indices }
        init(from decoder:Decoder)throws {
            let c=try decoder.container(keyedBy:CodingKeys.self)
            vertices=try c.decode([[Float]].self,forKey:.vertices);indices=try c.decode([Int32].self,forKey:.indices)
            // Clothing islands identify sleeves and hands at every authored LOD.
            // Position-only masks accidentally include a seated person's skirt.
            var parent=Array(vertices.indices)
            func root(_ input:Int)->Int { var i=input;while parent[i] != i { parent[i]=parent[parent[i]];i=parent[i] };return i }
            for i in stride(from:0,to:indices.count,by:3) {
                let a=Int(indices[i]),b=Int(indices[i+1]),d=Int(indices[i+2])
                if vertices[a][8]==0 || vertices[a][8]==1 { parent[root(b)]=root(a);parent[root(d)]=root(a) }
            }
            var bounds:[Int:SIMD2<Float>]=[:]
            for i in vertices.indices where vertices[i][8]==0 || vertices[i][8]==1 {
                let r=root(i),x=vertices[i][0],old=bounds[r] ?? SIMD2(x,x)
                bounds[r]=SIMD2(min(old.x,x),max(old.y,x))
            }
            let armFlags=vertices.indices.map { i -> Bool in
                guard let b=bounds[root(i)] else { return false };return b.x>0.07 || b.y < -0.07
            }
            arms=armFlags
            var tops:[Float]=[0,0]
            for i in vertices.indices where armFlags[i] && vertices[i][8]==1 {
                let side=vertices[i][0]<0 ? 0:1;tops[side]=max(tops[side],vertices[i][1])
            }
            armTops=vertices.map{tops[$0[0]<0 ? 0:1]}
        }
    }
    private final class Batch {
        var vertices:[SCNVector3]=[],normals:[SCNVector3]=[],uv:[CGPoint]=[],colors:[Float]=[]
        var groups:[[Int32]]=[[],[],[]]
        var motion=[[CGPoint]](repeating:[],count:7)
        func add(_ model:Model,at position:SIMD3<Float>,yaw:Float,index:Int,seated:Bool,activity:Activity = .ordinary) {
            let c=cos(yaw),s=sin(yaw),base=Int32(vertices.count)
            let outfits:[UInt32]=[0x746354,0x566866,0xa99b81,0x6d6a53,0x5b6266,0x907451,0x82756b,0xb8ab91]
            let skins:[UInt32]=[0xb68b70,0x835c48,0xc2a084,0x9c755c,0xa37c61,0xc4a591]
            // Independent deterministic hashes prevent matching outfit/skin/pose stripes.
            let seed=UInt32(truncatingIfNeeded:index &* 747796405 &+ 2891336453)
            let mix=Int((seed ^ (seed >> 16)) & 0x7fffffff)
            let palette:[UInt32]=[skins[(mix/7)%6],outfits[mix%8],outfits[(mix/13)%8],0x433c34,outfits[(mix/23)%8],0xc1b8a7,0x342b25,0x8a8170]
            let scale=SIMD3<Float>(0.90+Float(mix%19)*0.012,0.95+Float((mix/19)%11)*0.01,0.94+Float((mix/209)%13)*0.012)
            let turn=Float((mix/2717)%17-8)*(activity == .ordinary ? 0.055:0.012)
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
            func motionData(_ p:SIMD3<Float>,semantic:Int,armIsland:Bool=false,armTop:Float=0) {
                let arm:Float=armIsland ? min(1,max(0,(armTop-p.y)/0.075)):0
                motion[0].append(CGPoint(x:Double(position.x),y:Double(position.z)))
                motion[1].append(CGPoint(x:Double(yaw),y:Double(position.y)))
                motion[2].append(CGPoint(x:Double(mix%1000)*0.071,y:Double(neck*scale.y)))
                motion[3].append(CGPoint(x:Double(arm),y:p.x<0 ? -1:1))
                motion[4].append(CGPoint(x:Double(0.53*scale.y),y:Double(0.28*scale.y)))
                motion[5].append(CGPoint(x:seated ? 1:activity.rawValue,y:Double(semantic)))
                motion[6].append(CGPoint(x:Double(0.12*scale.x),y:Double(armTop*scale.y)))
            }
            for (vertexIndex,v) in model.vertices.enumerated() {
                motionData(SIMD3(v[0],v[1],v[2]),semantic:Int(v[8]),armIsland:model.arms[vertexIndex],armTop:model.armTops[vertexIndex])
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
                            motionData(q,semantic:1)
                            vertices.append(SCNVector3(v));normals.append(SCNVector3(n));uv.append(CGPoint(x:Double(q.x)*18,y:Double(q.y)*18))
                            colors += [Float((ink>>16)&255)/255,Float((ink>>8)&255)/255,Float(ink&255)/255,1]
                        }
                        groups[1] += [start,start+1,start+2]
                    }
                }}
            }
        }
        var triangles:Int { groups.reduce(0){$0+$1.count/3} }
        func geometry(materials:[SCNMaterial] = CityMaterialsArray.shared)->SCNGeometry {
            let color=colors.withUnsafeBytes { SCNGeometrySource(data:Data($0),semantic:.color,vectorCount:vertices.count,usesFloatComponents:true,componentsPerVector:4,bytesPerComponent:4,dataOffset:0,dataStride:16) }
            let g=SCNGeometry(sources:[SCNGeometrySource(vertices:vertices),SCNGeometrySource(normals:normals),SCNGeometrySource(textureCoordinates:uv),color]+motion.map{SCNGeometrySource(textureCoordinates:$0)},elements:groups.map{SCNGeometryElement(indices:$0,primitiveType:.triangles)})
            g.materials=materials
            // Shader-deformed hands and feet may extend beyond the bind pose.
            let bounds=g.boundingBox
            g.boundingBox=(SCNVector3(bounds.min.x-0.35,bounds.min.y-0.12,bounds.min.z-0.35),
                           SCNVector3(bounds.max.x+0.35,bounds.max.y+0.2,bounds.max.z+0.35))
            return g
        }
    }
    enum Activity:Double { case ordinary=0, conversation = -1, waiting = -2, trading = -3 }
    private enum CityMaterialsArray { static let shared=CitizenMotion.materials() }
    private struct Cell { let origin:SIMD3<Float>;let lod:[Batch];let stays:Bool }
    private var weatherNodes:[(SCNNode,Bool)]=[]
    private(set) var stormPopulation=0
    static func staysOutside(x:Double,z:Double,index:Int)->Bool { abs(index*17+Int(x*13)+Int(z*7))%31==0 }
    func update(time:Double) { for material in CityMaterialsArray.shared { material.setValue(Float(time),forKey:"crowdTime") } }
    func setStorm(_ active:Bool) { for (node,stays) in weatherNodes { node.isHidden=active && !stays } }
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
    func add(x:Double,y:Double,z:Double,yaw:Double,index:Int,seated:Bool,animated:Bool,shelter:Bool=false,activity:Activity = .ordinary)->SCNNode? {
        let key="\(index%2==0 ? "male":"female")-\(seated ? "sit":"stand")-\((index/2)%3)"
        guard let model=models[key] else { return nil }
        let stays = !shelter && Self.staysOutside(x:x,z:z,index:index)
        if stays && !animated { stormPopulation += 1 }
        let position=SIMD3(Float(x),Float(!seated && y<0.1 ? -0.03:y),Float(z))
        if animated {
            let lod=(0..<3).map{_ in Batch()}
            for i in 0..<3 { lod[i].add(model[i],at:.zero,yaw:0,index:index,seated:seated,activity:activity) }
            let materials=CitizenMotion.materials()
            let near=lod[0].geometry(materials:materials)
            near.levelsOfDetail=[SCNLevelOfDetail(geometry:lod[1].geometry(materials:materials),worldSpaceDistance:8),SCNLevelOfDetail(geometry:lod[2].geometry(materials:materials),worldSpaceDistance:24)]
            let node=SCNNode(geometry:near);node.simdPosition=position;node.eulerAngles.y=CGFloat(yaw)
            node.castsShadow=false
            triangles += lod[0].triangles;farTriangles += lod[2].triangles
            return node
        }
        let ix=Int(floor(x/8)),iz=Int(floor(z/8)),cellKey="\(ix),\(iz),\(stays)"
        if cells[cellKey]==nil { cells[cellKey]=Cell(origin:SIMD3(Float(ix*8+4),0,Float(iz*8+4)),lod:(0..<3).map{_ in Batch()},stays:stays) }
        let cell=cells[cellKey]!
        for i in 0..<3 { cell.lod[i].add(model[i],at:position-cell.origin,yaw:Float(yaw),index:index,seated:seated,activity:activity) }
        return nil
    }
    func finish(into root:SCNNode) {
        for key in cells.keys.sorted() {
            let cell=cells[key]!,near=cell.lod[0].geometry()
            near.levelsOfDetail=[SCNLevelOfDetail(geometry:cell.lod[1].geometry(),worldSpaceDistance:10),SCNLevelOfDetail(geometry:cell.lod[2].geometry(),worldSpaceDistance:26)]
            let node=SCNNode(geometry:near);node.simdPosition=cell.origin
            node.name="Crowd cell \(key)";node.castsShadow=false;root.addChildNode(node);weatherNodes.append((node,cell.stays))
            triangles += cell.lod[0].triangles;farTriangles += cell.lod[2].triangles
        }
        cellCount=cells.count
        cells.removeAll();models.removeAll()
    }
}

// Render every authored variant and LOD in the native regression capture.
extension TownCrowd {
    func inspectionFigures()->[(String,SCNNode)] {
        models.keys.sorted().flatMap { key in
            (0..<3).map { lod in
                let batch=Batch();batch.add(models[key]![lod],at:.zero,yaw:0,index:1,seated:key.contains("sit"))
                return ("\(key)-lod\(lod)",SCNNode(geometry:batch.geometry()))
            }
        }
    }
}
