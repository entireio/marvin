import AppKit
import SceneKit
import SimulationCore
import simd

/// Procedural clay, directional ruts, world-space tread marks and pooled debris.
/// Geometry and textures are generated locally; no network assets are required.
final class DirtWorld {
    let scene = SCNScene()
    let town = TownWorld()
    private let effects = SCNNode()
    private let dustBatch = SCNNode(), clodBatch = SCNNode()
    weak var camera: SCNNode?
    private var flecks: [(node: SCNNode, velocity: SIMD3<Double>, life: Double, dust: Bool)] = []
    private let trails = [DirtTrail(style:.tracks), DirtTrail(style:.tires), DirtTrail(style:.tires), DirtTrail(style:.tracks)]
    private var emission = [[0.0, 0.0], [0.0, 0.0, 0.0], [0.0], [0.0,0.0]]
    private(set) var racerEmittedCount = [0, 0, 0, 0]
    var trailCounts: [Int] { trails.map { $0.count } }
    private let dustMaterial = SCNMaterial()
    private let clodGeometry = SCNSphere(radius: 0.012)
    private let poolSize = 1600
    private var poolIndex = 0
    private(set) var emittedCount = 0

    private func packedEarthTexture()->NSImage {
        let size=512
        let bitmap=NSBitmapImageRep(bitmapDataPlanes:nil,pixelsWide:size,pixelsHigh:size,bitsPerSample:8,samplesPerPixel:4,hasAlpha:true,isPlanar:false,colorSpaceName:.deviceRGB,bytesPerRow:size*4,bitsPerPixel:32)!
        let bytes=bitmap.bitmapData!
        for y in 0..<size { for x in 0..<size {
            var seed=UInt32(truncatingIfNeeded:x &* 374761393 &+ y &* 668265263)
            seed=(seed ^ (seed >> 13)) &* 1274126177
            let grain=Double((seed ^ (seed >> 16)) & 255)/255
            let u=Double(x)/Double(size),v=Double(y)/Double(size)
            let soil=(CityMaterials.surfaceNoise(u,v,cells:5,seed:11)-0.5)*7+(CityMaterials.surfaceNoise(u,v,cells:21,seed:31)-0.5)*5
            let pebble=grain>0.984 ? -19.0:0.0
            let value=soil+(grain-0.5)*16+pebble
            let i=(y*size+x)*4
            bytes[i]=UInt8(157+value);bytes[i+1]=UInt8(140+value);bytes[i+2]=UInt8(115+value);bytes[i+3]=255
        }}
        let image=NSImage(size:NSSize(width:size,height:size));image.addRepresentation(bitmap);return image
    }

    init() {
        scene.background.contents = color(0xb9c9cf)
        scene.lightingEnvironment.contents = CityMaterials.asset("sky.hdr")
        scene.lightingEnvironment.intensity = 0.65
        scene.fogColor = color(0xb9c9cf); scene.fogStartDistance = 115; scene.fogEndDistance = 240
        let ambient = SCNNode(); ambient.light = SCNLight(); ambient.light?.type = .ambient
        ambient.light?.intensity = 180; ambient.light?.color = color(0xd8e5f2)
        scene.rootNode.addChildNode(ambient)
        let sun = SCNNode(); sun.light = SCNLight(); sun.light?.type = .directional
        sun.eulerAngles = SCNVector3(-0.85, -0.6, 0)
        sun.light?.intensity = 1250; sun.light?.color = color(0xffe6c1)
        sun.light?.castsShadow = true; sun.light?.shadowMode = .deferred
        sun.light?.shadowMapSize = CGSize(width: 2048, height: 2048)
        sun.light?.orthographicScale = 58; sun.light?.shadowRadius = 5
        sun.light?.shadowColor = NSColor.black.withAlphaComponent(0.58)
        scene.rootNode.addChildNode(sun)
        let ground = SCNPlane(width: 300, height: 300)
        let earth = material(0x827656, roughness: 1)
        earth.diffuse.contents = packedEarthTexture()
        earth.normal.contents = nil
        for channel in [earth.diffuse, earth.normal] {
            channel.wrapS = .repeat; channel.wrapT = .repeat
            channel.contentsTransform = SCNMatrix4MakeScale(75, 75, 1)
        }
        ground.materials = [earth]
        let terrain = SCNNode(geometry: ground); terrain.eulerAngles.x = -.pi/2; terrain.position.y = -0.025
        scene.rootNode.addChildNode(terrain)
        let clay = material(0x986441, roughness: 0.94)
        clay.diffuse.contents = soilTexture(track: true, normal: false)
        clay.normal.contents = soilTexture(track: true, normal: true); clay.normal.intensity = 0.65
        if let base = Bundle.main.resourceURL?.appendingPathComponent("Dirt") {
            clay.diffuse.contents = NSImage(contentsOf:base.appendingPathComponent("diffuse.jpg"))
            clay.normal.contents = NSImage(contentsOf:base.appendingPathComponent("normal.jpg"))
            clay.roughness.contents = NSImage(contentsOf:base.appendingPathComponent("roughness.jpg"))
            for channel in [clay.diffuse,clay.normal,clay.roughness] {
                channel.wrapS = .repeat; channel.wrapT = .repeat
                channel.contentsTransform = SCNMatrix4MakeScale(48,1,1)
            }
            // Broad compacted lanes modulate the scanned microdetail independently.
            clay.multiply.contents = soilTexture(track:true,normal:false)
            clay.multiply.intensity = 0.35
        }
        // Sun-dried rose clay: retain the scan's ruts and grain while lifting
        // its dark brown albedo away from the city paving's pale sandy palette.
        // One shared material keeps lanes, berms and shoulders consistent.
        clay.shaderModifiers = [.surface: """
        #pragma body
        float clayDetail = dot(_surface.diffuse.rgb, float3(0.2126,0.7152,0.0722));
        _surface.diffuse.rgb = float3(0.34,0.205,0.145)
                            + clayDetail * float3(0.58,0.44,0.33);
        """]
        let ring = courseSurface(inner: -DirtCourse.width, outer: DirtCourse.width, y: 0)
        ring.materials = [clay]
        let lane = SCNNode(geometry:ring); lane.name = "Compacted race surface"
        scene.rootNode.addChildNode(lane)
        // Raised loose-soil berms stay outside the driveable surface.
        let berm = courseSurface(inner: DirtCourse.width, outer: DirtCourse.width+DirtCourse.bermWidth, y: 0.10)
        berm.materials = [clay]
        scene.rootNode.addChildNode(SCNNode(geometry: berm))
        let inner = courseSurface(inner: -DirtCourse.width-DirtCourse.bermWidth, outer: -DirtCourse.width, y: 0.055)
        inner.materials = [clay]
        scene.rootNode.addChildNode(SCNNode(geometry: inner))
        for (innerEdge,outerEdge) in [(-DirtCourse.fenceOffset,-DirtCourse.width-DirtCourse.bermWidth),(DirtCourse.width+DirtCourse.bermWidth,DirtCourse.fenceOffset)] {
            let shoulder = courseSurface(inner:innerEdge,outer:outerEdge,y:-1)
            shoulder.materials = [clay]; scene.rootNode.addChildNode(SCNNode(geometry:shoulder))
        }
        addServiceEmbankment(clay:clay,earth:earth)
        addInfieldDirt()
        addTrackWalls()
        // Start / finish checker paint, across the full lane at phase zero.
        let start = DirtCourse.point(0)
        for row in 0..<2 { for cell in 0..<10 {
            let tile = box(start.x+Double(row)*0.15-0.15, 0.003, start.z-DirtCourse.width+(Double(cell)+0.5)*(DirtCourse.width*2/10),
                           0.15, 0.004, DirtCourse.width*2/10, material((row+cell)%2 == 0 ? 0xddd2b9 : 0x393a32))
            tile.castsShadow = false
        }}
        // Staggered two-column starting boxes, open at the rear.
        for slot in DirtCourse.startingGrid {
            let p = DirtCourse.point(slot.phase,offset:slot.offset), heading = DirtCourse.heading(slot.phase)
            var vertices: [SCNVector3] = [], indices: [Int32] = []
            for (cx,cz,w,l) in [(-0.43,0.0,0.025,0.95),(0.43,0.0,0.025,0.95),(0.0,0.475,0.86,0.025)] {
                let base = Int32(vertices.count)
                for (sx,sz) in [(-1.0,-1.0),(1,-1),(1,1),(-1,1)] {
                    let x = p.x+cos(heading)*(cx+sx*w/2)+sin(heading)*(cz+sz*l/2)
                    let z = p.z-sin(heading)*(cx+sx*w/2)+cos(heading)*(cz+sz*l/2)
                    vertices.append(SCNVector3(x,DirtCourse.height(x:x,z:z)+0.009,z))
                }
                indices += [base,base+2,base+1,base,base+3,base+2]
            }
            let geometry = SCNGeometry(sources:[SCNGeometrySource(vertices:vertices)],elements:[SCNGeometryElement(indices:indices,primitiveType:.triangles)])
            geometry.materials = [material(0xe7dcc4,roughness:1)]
            let node = SCNNode(geometry:geometry); node.name = "Starting grid box"; node.castsShadow = false
            scene.rootNode.addChildNode(node)
        }
        for t in [0.5, 1.8, 3.5, 5.0] {
            let p = DirtCourse.point(t)
            let path = NSBezierPath(); path.move(to: NSPoint(x: -0.15,y: -0.12))
            path.line(to: NSPoint(x: 0,y: 0.15)); path.line(to: NSPoint(x: 0.15,y: -0.12))
            path.line(to: NSPoint(x: 0,y: -0.02)); path.close()
            let geometry = SCNShape(path: path, extrusionDepth: 0); geometry.materials = [material(0xd6ba85)]
            let arrow = SCNNode(geometry: geometry); arrow.position = SCNVector3(p.x,DirtCourse.elevation(t)+0.006,p.z)
            arrow.eulerAngles = SCNVector3(-Double.pi/2, DirtCourse.heading(t) + .pi, 0)
            arrow.castsShadow = false; scene.rootNode.addChildNode(arrow)
        }
        scene.rootNode.addChildNode(town.root)
        scene.rootNode.addChildNode(effects)
        clodGeometry.segmentCount = 5; clodGeometry.materials = [material(0x705033,roughness:1)]
        dustMaterial.lightingModel = .constant; dustMaterial.diffuse.contents = dustTexture()
        dustMaterial.writesToDepthBuffer = false; dustMaterial.isDoubleSided = true
        for i in 0..<poolSize {
            let dust = i % 3 == 0
            let node = SCNNode(geometry: dust ? SCNPlane(width: 0.18,height: 0.18) : clodGeometry)
            if dust { node.geometry?.materials = [dustMaterial]; node.constraints = [SCNBillboardConstraint()] }
            node.castsShadow = false; node.isHidden = true
            // Simulation slots are not individual render submissions.
            flecks.append((node,.zero,0,dust))
        }
        for (batch, mat) in [(dustBatch,dustMaterial),(clodBatch,clodGeometry.materials[0])] {
            let placeholder=SCNPlane(width:0,height:0);placeholder.materials=[mat]
            batch.geometry=placeholder;batch.castsShadow=false;effects.addChildNode(batch)
        }
        for trail in trails { effects.addChildNode(trail.root) }
    }

    /// Level brick courses rise from a common foundation, batched per boundary. The track-facing
    /// surface stays on the existing collision line; thickness extends away from racing.
    private func addTrackWalls() {
        let palette:[UInt32]=[0xa28a70,0x998068,0xb0997c,0x927963,0xa78c70,0xb29b80]
        for side in [-1.0,1.0] {
            let boundary=DirtCourse.surfacePoints(offset:side*(DirtCourse.fenceOffset+DirtCourse.boundaryWallThickness/2))
            var distances=[0.0]
            for i in 1..<boundary.count { distances.append(distances.last!+simd_length(boundary[i]-boundary[i-1])) }
            let length=distances.last!, count=Int(ceil(length/0.43)), step=length/Double(count)
            func sample(_ distance:Double)->SIMD2<Double> {
                let d=(distance.truncatingRemainder(dividingBy:length)+length).truncatingRemainder(dividingBy:length)
                var lo=0,hi=distances.count-1
                while lo+1<hi { let m=(lo+hi)/2;if distances[m]<=d { lo=m } else { hi=m } }
                let fraction=(d-distances[lo])/max(0.000001,distances[hi]-distances[lo])
                return boundary[lo]+(boundary[hi]-boundary[lo])*fraction
            }
            let mesh=TownMesh()
            func quad(_ a:SIMD3<Float>,_ b:SIMD3<Float>,_ c:SIMD3<Float>,_ d:SIMD3<Float>,_ ink:UInt32) {
                mesh.triangle(a,c,b,ink);mesh.triangle(a,d,c,ink)
            }
            let courseHeight=0.13,foundation = -0.04
            let maxHeight=boundary.map { DirtCourse.height(x:$0.x,z:$0.y) }.max()!+DirtCourse.postHeight
            let rows=Int(ceil((maxHeight-foundation)/courseHeight))
            for row in 0..<rows { for i in 0..<count {
                let distance=(Double(i)+(row%2==0 ? 0:0.5))*step
                let a=sample(distance+0.004),b=sample(distance+step-0.004),center=(a+b)*0.5
                // Do not bridge the service entrance, even with staggered end bricks.
                if side<0 && [a,b,center].contains(where:{DirtCourse.serviceAccess(x:$0.x,z:$0.y)}) { continue }
                let direction=simd_normalize(b-a),normal=SIMD2(-direction.y,direction.x)*(DirtCourse.boundaryWallThickness/2)
                let seed=i*73+row*193+(side>0 ? 31:0)
                // Global horizontal bed joints: hills add courses from the same
                // foundation instead of tilting the individual bricks uphill.
                let target=max(DirtCourse.height(x:a.x,z:a.y),DirtCourse.height(x:b.x,z:b.y))+DirtCourse.postHeight
                let localRows=max(3,Int(ceil((target-foundation)/courseHeight)))
                guard row<localRows else { continue }
                let base=foundation+Double(row)*courseHeight
                let rise=courseHeight-0.006
                let ink=palette[(seed ^ (seed>>3))%palette.count]
                let corners=[a-normal,b-normal,b+normal,a+normal]
                let bottom=corners.map { p in SIMD3<Float>(Float(p.x),Float(base),Float(p.y)) }
                let lip=corners.map { p in SIMD3<Float>(Float(p.x),Float(base+rise-0.012),Float(p.y)) }
                let top=corners.enumerated().map { j,p -> SIMD3<Float> in
                    let q=p+(center-p)*0.055
                    let chip=row==localRows-1 ? Double((seed+j*7)%7)*0.001:0
                    return SIMD3(Float(q.x),Float(base+rise-chip),Float(q.y))
                }
                for j in 0..<4 { let k=(j+1)%4
                    quad(bottom[j],bottom[k],lip[k],lip[j],ink)
                    quad(lip[j],lip[k],top[k],top[j],ink)
                }
                quad(top[0],top[1],top[2],top[3],ink)
            }}
            let node=SCNNode(geometry:mesh.geometry(material:CityMaterials.plaster))
            node.name=side<0 ? "Inner irregular brick track wall":"Outer irregular brick track wall"
            scene.rootNode.addChildNode(node)
        }
    }

    /// A closed heightfield across the entire opening, including its side slopes.
    /// No cropped offset ribbons or exposed underside; physics samples this height.
    private func addServiceEmbankment(clay:SCNMaterial,earth:SCNMaterial) {
        let nx=68,nz=62,x0=DirtCourse.serviceEntryX-3.4,z0 = -13.2
        let dx=0.1,dz=0.1
        var vertices:[SCNVector3]=[],normals:[SCNVector3]=[],uv:[CGPoint]=[],colors:[Float]=[],indices:[Int32]=[]
        func add(_ x:Double,_ z:Double,_ y:Double?=nil) {
            let h=y ?? (DirtCourse.height(x:x,z:z)-0.008)
            vertices.append(SCNVector3(x,h,z))
            let epsilon=0.025
            var n=SIMD3<Double>(DirtCourse.height(x:x-epsilon,z:z)-DirtCourse.height(x:x+epsilon,z:z),2*epsilon,DirtCourse.height(x:x,z:z-epsilon)-DirtCourse.height(x:x,z:z+epsilon))
            n /= simd_length(n);normals.append(SCNVector3(n.x,n.y,n.z))
            uv.append(CGPoint(x:x/4,y:z/4))
            let projection=DirtCourse.projection(x:x,z:z)
            let run=max(0,min(1,(projection.distance-DirtCourse.fenceOffset)/3.6))
            let side=max(0,min(1,(abs(x-DirtCourse.serviceEntryX)-1.0)/2.0))
            let noise=CityMaterials.surfaceNoise(x/12,z/12,cells:7,seed:197)
            let red=max(0,min(1,(1-run)*(1-side)+run*(1-run)*(noise-0.5)*0.85))
            let foot=max(0,min(1,(h+0.025)/0.06))
            colors += [Float(red*foot*foot*(3-2*foot)),0,0,1]
        }
        for j in 0...nz { for i in 0...nx { add(x0+Double(i)*dx,z0+Double(j)*dz) } }
        for j in 0..<nz { for i in 0..<nx {
            let a=Int32(j*(nx+1)+i),b=a+1,c=a+Int32(nx+1),d=c+1
            indices += [a,c,b,b,c,d]
        }}
        // Fill every perimeter edge down below the ground plane, including the
        // concealed track-side seam. The underside can never be seen through.
        let border=(0...nx).map{$0}+(1...nz).map{$0*(nx+1)+nx}+(0..<nx).reversed().map{nz*(nx+1)+$0}+(1..<nz).reversed().map{$0*(nx+1)}
        let bottom=Int32(vertices.count)
        for index in border { let v=vertices[index];add(Double(v.x),Double(v.z),-0.08) }
        for i in border.indices {
            let j=(i+1)%border.count,a=Int32(border[i]),b=Int32(border[j]),c=bottom+Int32(i),d=bottom+Int32(j)
            indices += [a,b,c,b,d,c]
        }
        for i in 1..<border.count-1 { indices += [bottom,bottom+Int32(i+1),bottom+Int32(i)] }
        let colorSource=colors.withUnsafeBytes { SCNGeometrySource(data:Data($0),semantic:.color,vectorCount:vertices.count,usesFloatComponents:true,componentsPerVector:4,bytesPerComponent:4,dataOffset:0,dataStride:16) }
        let sources=[SCNGeometrySource(vertices:vertices),SCNGeometrySource(normals:normals),SCNGeometrySource(textureCoordinates:uv)]
        let elements=[SCNGeometryElement(indices:indices,primitiveType:.triangles)]
        let sandGeometry=SCNGeometry(sources:sources,elements:elements)
        // Use the actual surrounding ground material, not an approximate tint.
        let sandMaterial=earth.copy() as! SCNMaterial
        sandMaterial.diffuse.contentsTransform=SCNMatrix4Identity
        sandGeometry.materials=[sandMaterial]
        let base=SCNNode(geometry:sandGeometry);base.name="Filled service embankment"
        base.castsShadow=false;scene.rootNode.addChildNode(base)
        let geometry=SCNGeometry(sources:sources+[colorSource],elements:elements)
        let material=clay.copy() as! SCNMaterial
        material.diffuse.contentsTransform=SCNMatrix4Identity
        material.normal.contentsTransform=SCNMatrix4Identity
        material.roughness.contentsTransform=SCNMatrix4Identity
        var modifiers=material.shaderModifiers ?? [:]
        modifiers[.geometry]="""
        #pragma varyings
        half redSoil;
        #pragma body
        out.redSoil=half(_geometry.color.r);
        """
        modifiers[.fragment]="""
        #pragma transparent
        #pragma body
        _output.color.rgb *= float(in.redSoil);
        _output.color.a=float(in.redSoil);
        """
        material.shaderModifiers=modifiers
        material.transparencyMode = .aOne;material.writesToDepthBuffer=false
        geometry.materials=[material]
        let pigment=SCNNode(geometry:geometry);pigment.name="Track clay mixed into service sand"
        pigment.position.y=0.002;pigment.castsShadow=false
        scene.rootNode.addChildNode(pigment)
    }

    /// One startup-baked decal: no per-frame projection work or individual dirt nodes.
    private func addInfieldDirt() {
        let size=768,span=60.0
        let bitmap=NSBitmapImageRep(bitmapDataPlanes:nil,pixelsWide:size,pixelsHigh:size,bitsPerSample:8,samplesPerPixel:4,hasAlpha:true,isPlanar:false,colorSpaceName:.deviceRGB,bytesPerRow:size*4,bitsPerPixel:32)!
        let bytes=bitmap.bitmapData!
        bytes.initialize(repeating:0,count:size*size*4)
        for row in 0..<size { for column in 0..<size {
            let x=(Double(column)+0.5)/Double(size)*span-span/2
            let z=(Double(row)+0.5)/Double(size)*span-span/2
            let p=DirtCourse.projection(x:x,z:z)
            guard p.distance > DirtCourse.terrainEdge-0.10 else { continue }
            var seed=UInt32(truncatingIfNeeded:column &* 374761393 &+ row &* 668265263)
            seed=(seed ^ (seed >> 13)) &* 1274126177
            let grain=Double((seed ^ (seed >> 16)) & 255)/255
            let broad=sin(x*2.1+sin(z*1.7))*sin(z*2.7+x*0.8)
            let reach=1.05+0.50*sin(p.phase*19)+0.22*sin(p.phase*47)
            let edge=max(0,1-(p.distance-DirtCourse.terrainEdge)/max(0.35,reach+broad*0.23))
            // Traffic fans out after the gate and thins toward the service bench.
            let progress=max(0,min(1,(z+12.0)/3.0))
            let lateral=abs(x-DirtCourse.serviceEntryX)
            let route=max(0,1-lateral/(0.95+progress*0.60+broad*0.18))
                * max(0,min(1,(z+15.0)/1.5))*max(0,min(1,(-5.1-z)/2.0))
            let wheel=exp(-pow((lateral-0.46)/0.13,2))*route
            let coverage:Double
            if p.offset < 0 {
                // Keep the existing infield deposit and service traffic mask verbatim.
                coverage=max(edge*0.78,route*0.73+wheel*0.20)
            } else {
                // Soil thrown over the wall settles in irregular fans, strongest near
                // the boundary, with broader sparse patches out toward the town.
                let macro=CityMaterials.surfaceNoise(x/60+0.5,z/60+0.5,cells:23,seed:83)
                let detail=CityMaterials.surfaceNoise(x/60+0.5,z/60+0.5,cells:79,seed:137)
                let spread=1.5+macro*2.1
                let falloff=max(0,1-(p.distance-DirtCourse.terrainEdge)/spread)
                coverage=pow(falloff,1.65)*(0.36+macro*0.49)*(0.58+detail*0.42)
            }
            let alpha=max(0,min(0.94,coverage*(0.72+grain*0.33)+broad*0.045*coverage))
            let index=(row*size+column)*4
            let value=grain*12-6-(p.offset < 0 ? wheel*15:0)
            bytes[index]=UInt8(max(0,min(255,(184+value)*alpha)))
            bytes[index+1]=UInt8(max(0,min(255,(144+value)*alpha)))
            bytes[index+2]=UInt8(max(0,min(255,(118+value)*alpha)))
            bytes[index+3]=UInt8(alpha*255)
        }}
        let image=NSImage(size:NSSize(width:size,height:size));image.addRepresentation(bitmap)
        let surface=SCNMaterial();surface.lightingModel = .physicallyBased
        surface.diffuse.contents=image;surface.roughness.contents=0.98
        surface.transparencyMode = .aOne;surface.writesToDepthBuffer=false
        surface.diffuse.mipFilter = .linear
        let vertices=[SCNVector3(-30,-0.012,-30),SCNVector3(-30,-0.012,30),SCNVector3(30,-0.012,30),SCNVector3(30,-0.012,-30)]
        let mesh=SCNGeometry(sources:[SCNGeometrySource(vertices:vertices),SCNGeometrySource(normals:Array(repeating:SCNVector3(0,1,0),count:4)),SCNGeometrySource(textureCoordinates:[CGPoint(x:0,y:0),CGPoint(x:0,y:1),CGPoint(x:1,y:1),CGPoint(x:1,y:0)])],elements:[SCNGeometryElement(indices:[Int32(0),1,2,0,2,3],primitiveType:.triangles)])
        mesh.materials=[surface]
        let node=SCNNode(geometry:mesh);node.name="Clay spill inside and outside walls and service wheel paths"
        node.castsShadow=false;node.renderingOrder=1;scene.rootNode.addChildNode(node)
    }

    private func courseSurface(inner: Double, outer: Double, y: Double, serviceOnly:Bool=false) -> SCNGeometry {
        var points: [SCNVector3] = [], uv: [CGPoint] = [], indices: [Int32] = []
        let segments = DirtCourse.sampleCount, strips = 32
        let outlines = (0...strips).map { j in
            DirtCourse.surfacePoints(offset:inner+(outer-inner)*Double(j)/Double(strips))
        }
        for i in 0...segments {
            let phase = Double(i)/Double(segments)*2*Double.pi
            for j in 0...strips {
                let across = Double(j)/Double(strips)
                let p = outlines[j][i]
                let rut = y < 0 ? 0 : y == 0 ? 0.0015*sin(across*180 + sin(phase*9)*0.8) : sin(across * .pi)*y
                let height = DirtCourse.height(x:p.x,z:p.y) + (y == 0 ? rut : 0)
                points.append(SCNVector3(p.x, height, p.y)); uv.append(CGPoint(x: Double(i)/Double(segments),y:across))
                if i < segments && j < strips {
                    let a = Int32(i*(strips+1)+j), b = a+Int32(strips+1)
                    let midpoint=(outlines[j][i]+outlines[j][i+1])*0.5
                    if !serviceOnly || DirtCourse.serviceAccess(x:midpoint.x,z:midpoint.y) {
                        indices += [a,b,a+1,a+1,b,b+1]
                    }
                }
            }
        }
        var normals: [SCNVector3] = []
        for i in 0...segments { for j in 0...strips {
            let a = points[min(segments,i+1)*(strips+1)+j], b = points[max(0,i-1)*(strips+1)+j]
            let c = points[i*(strips+1)+min(strips,j+1)], d = points[i*(strips+1)+max(0,j-1)]
            let along = SIMD3<Double>(Double(a.x-b.x),Double(a.y-b.y),Double(a.z-b.z))
            let across = SIMD3<Double>(Double(c.x-d.x),Double(c.y-d.y),Double(c.z-d.z))
            var n = SIMD3<Double>(along.y*across.z-along.z*across.y,along.z*across.x-along.x*across.z,along.x*across.y-along.y*across.x)
            if n.y < 0 { n = -n }
            let length = sqrt(n.x*n.x+n.y*n.y+n.z*n.z)
            n = length > 1e-12 ? n/length : SIMD3<Double>(0,1,0)
            normals.append(SCNVector3(n.x,n.y,n.z))
        }}
        let geometry = SCNGeometry(sources:[SCNGeometrySource(vertices:points), SCNGeometrySource(normals: normals),SCNGeometrySource(textureCoordinates:uv)],elements:[SCNGeometryElement(indices:indices,primitiveType:.triangles)])
        return geometry
    }
    private func soilTexture(track: Bool, normal: Bool) -> NSImage {
        let w = 1024, h = 256
        let bitmap = NSBitmapImageRep(bitmapDataPlanes:nil,pixelsWide:w,pixelsHigh:h,bitsPerSample:8,samplesPerPixel:4,hasAlpha:true,isPlanar:false,colorSpaceName:.deviceRGB,bytesPerRow:w*4,bitsPerPixel:32)!
        let data = bitmap.bitmapData!
        func noise(_ x: Int,_ y: Int) -> Double {
            var n = UInt32(truncatingIfNeeded: x &* 374761393 &+ y &* 668265263)
            n = (n ^ (n >> 13)) &* 1274126177
            return Double(n ^ (n >> 16)) / Double(UInt32.max)
        }
        for y in 0..<h { for x in 0..<w {
            let v = Double(y)/Double(h), u = Double(x)/Double(w)
            let grain = noise(x,y), blotch = noise(x/16,y/12)
            let groove = sin(v*190+sin(u * .pi*12)*0.7)
            let lane = exp(-pow((v-0.52)/0.27,2))
            let value = (grain-0.5)*0.20 + (blotch-0.5)*0.08 + (track ? groove*0.025-lane*0.13 : 0)
            let rgb: [Double]
            if normal {
                rgb = [0.5+(grain-noise(x+1,y))*0.22, 0.5+(grain-noise(x,y+1))*0.22 + (track ? cos(v*190)*0.11 : 0), 0.97]
            } else {
                rgb = track ? [0.57+value,0.37+value*0.8,0.23+value*0.55] : [0.66+value*0.45,0.53+value*0.4,0.37+value*0.3]
            }
            let i = (y*w+x)*4
            for c in 0..<3 { data[i+c] = UInt8(max(0,min(255,rgb[c]*255))) }; data[i+3] = 255
        }}
        let image = NSImage(size:NSSize(width:w,height:h)); image.addRepresentation(bitmap); return image
    }
    private func dustTexture() -> NSImage {
        let image = NSImage(size:NSSize(width:64,height:64)); image.lockFocus()
        NSGradient(starting:color(0xb58b5e,alpha:0.40),ending:color(0xb58b5e,alpha:0))!.draw(in:NSBezierPath(ovalIn:NSRect(x:0,y:0,width:64,height:64)),relativeCenterPosition:.zero)
        image.unlockFocus(); return image
    }
    @discardableResult private func box(_ x:Double,_ y:Double,_ z:Double,_ w:Double,_ h:Double,_ d:Double,_ mat:SCNMaterial)->SCNNode {
        let shape = SCNBox(width:w,height:h,length:d,chamferRadius:0.008); shape.materials = [mat]
        let node = SCNNode(geometry:shape); node.position = SCNVector3(x,y,z); scene.rootNode.addChildNode(node); return node
    }
    func reset() {
        town.reset()
        dustBatch.isHidden=true;clodBatch.isHidden=true
        for i in flecks.indices { flecks[i].life = 0; flecks[i].node.isHidden = true }
        trails.forEach { $0.reset() }; emission = [[0,0],[0,0,0],[0],[0,0]]
        racerEmittedCount = [0,0,0,0]; emittedCount = 0; poolIndex = 0
    }
    var additionalContacts: [[(x: Double,z: Double,width: Double)]] = []
    func update(_ state: Simulation, opponent: Simulation, dt: Double, modelScale: Double, additional: [Simulation]) {
        guard dt > 0 else { return }
        for i in flecks.indices where flecks[i].life > 0 {
            flecks[i].life -= dt
            var f = flecks[i]
            f.velocity.y -= dt * (f.dust ? 0.15 : 9.8)
            f.node.simdPosition += SIMD3<Float>(Float(f.velocity.x*dt),Float(f.velocity.y*dt),Float(f.velocity.z*dt))
            let ground = CGFloat(DirtCourse.height(x:Double(f.node.position.x),z:Double(f.node.position.z))+0.012)
            if f.node.position.y < ground {
                f.node.position.y = ground; f.velocity.y = abs(f.velocity.y)*0.2
                f.velocity.x *= 0.5; f.velocity.z *= 0.5
            }
            f.node.opacity = CGFloat(min(1,max(0,f.life)/(f.dust ? 0.7 : 0.3)))
            if f.dust { let size = CGFloat(1+(1.6-f.life)*1.5); f.node.scale = SCNVector3(size,size,size) }
            f.node.isHidden = f.life <= 0; flecks[i] = f
        }
        emit(state, racer:0, dt:dt, contacts:[
            (0.262225*modelScale, -0.23*modelScale, 0.155*modelScale),
            (-0.262225*modelScale, -0.23*modelScale, 0.155*modelScale)])
        emit(opponent, racer:1, dt:dt, contacts:R2D2.groundContacts)
        for (i, racer) in additional.enumerated() {
            emit(racer,racer:i+2,dt:dt,contacts:additionalContacts[i])
        }
        rebuildDebrisBatches()
    }
    /// Two draw submissions replace up to 1,600 individual particle nodes.
    /// Pool lifetime, emission counts, contact behavior and reset stay unchanged.
    private func rebuildDebrisBatches() {
        let transform=camera?.simdWorldTransform ?? matrix_identity_float4x4
        let right=SIMD3(transform.columns.0.x,transform.columns.0.y,transform.columns.0.z)
        let up=SIMD3(transform.columns.1.x,transform.columns.1.y,transform.columns.1.z)
        let normal=simd_normalize(simd_cross(right,up))
        for dust in [false,true] {
            var vertices:[SCNVector3]=[],normals:[SCNVector3]=[],uv:[CGPoint]=[],rgba:[Float]=[],indices:[Int32]=[]
            for f in flecks where f.life>0 && f.dust==dust {
                let center=f.node.simdPosition,base=Int32(vertices.count)
                if dust {
                    let radius=Float(0.09)*f.node.simdScale.x
                    for (sx,sy) in [(-1.0,-1.0),(1.0,-1.0),(1.0,1.0),(-1.0,1.0)] {
                        vertices.append(SCNVector3(center+right*Float(sx)*radius+up*Float(sy)*radius))
                        normals.append(SCNVector3(normal));uv.append(CGPoint(x:(sx+1)/2,y:(sy+1)/2))
                    }
                    indices += [base,base+1,base+2,base,base+2,base+3]
                } else {
                    for v:SIMD3<Float> in [SIMD3(0,1,0),SIMD3(-0.87,-0.5,-0.5),SIMD3(0.87,-0.5,-0.5),SIMD3(0,-0.5,1)] {
                        vertices.append(SCNVector3(center+v*0.014));normals.append(SCNVector3(simd_normalize(v)));uv.append(.zero)
                    }
                    indices += [base,base+2,base+1,base,base+3,base+2,base,base+1,base+3,base+1,base+2,base+3]
                }
                for _ in 0..<4 { rgba += [1,1,1,Float(f.node.opacity)] }
            }
            let batch=dust ? dustBatch:clodBatch
            batch.isHidden=indices.isEmpty
            guard !indices.isEmpty else { continue }
            let colors=rgba.withUnsafeBytes { SCNGeometrySource(data:Data($0),semantic:.color,vectorCount:vertices.count,usesFloatComponents:true,componentsPerVector:4,bytesPerComponent:4,dataOffset:0,dataStride:16) }
            let geometry=SCNGeometry(sources:[SCNGeometrySource(vertices:vertices),SCNGeometrySource(normals:normals),SCNGeometrySource(textureCoordinates:uv),colors],elements:[SCNGeometryElement(indices:indices,primitiveType:.triangles)])
            geometry.materials=[dust ? dustMaterial:clodGeometry.materials[0]]
            batch.geometry=geometry
        }
    }
    private func emit(_ state: Simulation, racer: Int, dt: Double,
                      contacts: [(x: Double, z: Double, width: Double)]) {
        trails[racer].update(state, contacts:contacts)
        let forward = SIMD3<Double>(sin(state.heading),0,cos(state.heading))
        let lateral = SIMD3<Double>(cos(state.heading),0,-sin(state.heading))
        let origin = SIMD3<Double>(state.x,state.groundY+0.035,state.z)
        for (side, contact) in contacts.enumerated() {
            let speed = contacts.count == 1 ? state.speed : side == 0 ? state.leftSpeed : side == 1 ? state.rightSpeed : state.speed
            let magnitude = abs(speed)
            guard magnitude > 0.08, !state.contacting, state.hasDirtContact else { continue }
            let sign = speed > 0 ? 1.0 : -1.0
            emission[racer][side] += dt*magnitude*42
            while emission[racer][side] >= 1 {
                emission[racer][side] -= 1
                let i = poolIndex; poolIndex = (poolIndex+1)%poolSize
                let position = origin + lateral*contact.x + forward*contact.z
                let velocity = -forward*sign*magnitude*Double.random(in:0.35...0.85) + lateral*Double.random(in:-0.35...0.35)
                flecks[i].velocity = velocity + SIMD3<Double>(0,Double.random(in:0.4...1.1)*sqrt(magnitude),0)
                flecks[i].life = flecks[i].dust ? 1.6 : 0.9
                flecks[i].node.position = SCNVector3(position.x,position.y,position.z)
                flecks[i].node.scale = SCNVector3(1,1,1); flecks[i].node.opacity = 1; flecks[i].node.isHidden = false
                emittedCount += 1; racerEmittedCount[racer] += 1
            }
        }
    }
}
