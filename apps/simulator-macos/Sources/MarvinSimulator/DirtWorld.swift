import AppKit
import SceneKit
import SimulationCore
import simd

/// Procedural clay, directional ruts, world-space tread marks and pooled debris.
/// Geometry and textures are generated locally; no network assets are required.
final class DirtWorld {
    let scene = SCNScene()
    let duneSand=DeformableSand()
    private(set) var sky: BinarySky!
    private var stormVisual:SandstormWorld!
    private(set) var storm=Sandstorm()
    func configureStorm(_ value:Sandstorm) {
        storm=value;stormVisual.root.isHidden = !value.enabled;stormVisual.reset()
        town.setStorm(value.enabled);sky.setStorm(value.enabled)
        if let camera { stormVisual.update(value,camera:camera,dt:1.0/60) }
    }
    let cityGateNode=SCNNode()
    let town: TownWorld
    let escapeRoutes:[[SIMD2<Double>]]
    private let effects = SCNNode()
    private let dustBatch = SCNNode(), clodBatch = SCNNode()
    weak var camera: SCNNode?
    private struct Fleck {
        let node: SCNNode
        var velocity = SIMD3<Double>.zero
        var life = 0.0
        var dust: Bool
        var duration = 1.0
        var tint = SIMD3<Float>(repeating: 1)
        var radius: Float = 0.003
        var opacity = 0.0
    }
    private var flecks: [Fleck] = []
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

    init(progress: ((Double,String)->Void)? = nil) {
        town=TownWorld(progress:{ fraction,label in progress?(0.05+fraction*0.43,label) })
        progress?(0.49,"Planning routes through town")
        escapeRoutes=PostRaceEscape.makeRoutes(city:town.collisionWorld)
        progress?(0.495,"Preparing the sand and racecourse")
        sky=BinarySky(scene:scene)
        stormVisual=SandstormWorld(texture:packedEarthTexture());scene.rootNode.addChildNode(stormVisual.root)
        let ground = SCNPlane(width: 256, height: 256)
        let earth = material(0x827656, roughness: 1)
        earth.diffuse.contents = packedEarthTexture()
        earth.normal.contents = nil
        for channel in [earth.diffuse, earth.normal] {
            channel.wrapS = .repeat; channel.wrapT = .repeat
            channel.contentsTransform = SCNMatrix4MakeScale(64, 64, 1)
        }
        ground.materials = [earth]
        let terrain = SCNNode(geometry: ground); terrain.eulerAngles.x = -.pi/2; terrain.position.y = -0.025
        scene.rootNode.addChildNode(terrain)
        addDesertTerrain(earth:earth,progress:{ fraction in progress?(0.50+fraction*0.34,"Building the dunes") })
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
        progress?(0.86,"Building track walls and gate")
        addTrackWalls()
        addCityExit(clay:clay,earth:earth)
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
        clodGeometry.segmentCount = 5; clodGeometry.materials = [material(0xffffff,roughness:1)]
        WindblownDust.configure(clodGeometry.materials[0])
        dustMaterial.lightingModel = .lambert; dustMaterial.diffuse.contents = dustTexture()
        WindblownDust.configure(dustMaterial)
        dustMaterial.writesToDepthBuffer = false; dustMaterial.isDoubleSided = true
        for i in 0..<poolSize {
            let dust = i % 3 != 0
            let node = SCNNode(geometry: dust ? SCNPlane(width: 0.18,height: 0.18) : clodGeometry)
            if dust { node.geometry?.materials = [dustMaterial]; node.constraints = [SCNBillboardConstraint()] }
            node.castsShadow = false; node.isHidden = true
            // Simulation slots are not individual render submissions.
            flecks.append(Fleck(node:node,dust:dust))
        }
        for (batch, mat) in [(dustBatch,dustMaterial),(clodBatch,clodGeometry.materials[0])] {
            let placeholder=SCNPlane(width:0,height:0);placeholder.materials=[mat]
            batch.geometry=placeholder;batch.castsShadow=false;effects.addChildNode(batch)
        }
        for trail in trails { effects.addChildNode(trail.root) }
        progress?(0.90,"Preparing robots and race")
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
            func wallGround(_ p:SIMD2<Double>)->Double {
                // The exit apron slopes away below this retaining wall; its
                // tapered fill must not carve a notch into adjacent masonry.
                side>0 ? DirtCourse.elevation(DirtCourse.phase(x:p.x,z:p.y),offset:DirtCourse.width):DirtCourse.height(x:p.x,z:p.y)
            }
            let courseHeight=0.13,foundation = -0.04
            let maxHeight=boundary.map { DirtCourse.height(x:$0.x,z:$0.y) }.max()!+DirtCourse.postHeight
            let rows=Int(ceil((maxHeight-foundation)/courseHeight))
            for row in 0..<rows { for i in 0..<count {
                let distance=(Double(i)+(row%2==0 ? 0:0.5))*step
                let a=sample(distance+0.004),b=sample(distance+step-0.004),center=(a+b)*0.5
                let direction=simd_normalize(b-a),normal=SIMD2(-direction.y,direction.x)*(DirtCourse.boundaryWallThickness/2)
                let seed=i*73+row*193+(side>0 ? 31:0)
                // Global horizontal bed joints: hills add courses from the same
                // foundation instead of tilting the individual bricks uphill.
                let target=side>0 ? max(CityExit.wallTop(a),CityExit.wallTop(b)):max(wallGround(a),wallGround(b))+DirtCourse.postHeight
                let localRows=max(3,Int(ceil((target-foundation)/courseHeight)))
                guard row<localRows else { continue }
                let base=foundation+Double(row)*courseHeight
                let rise=courseHeight-0.006
                let ink=palette[(seed ^ (seed>>3))%palette.count]
                var corners=[a-normal,b-normal,b+normal,a+normal]
                // Cut crossing bricks at a fixed vertical jamb plane instead of
                // dropping a whole stretcher. Alternate courses retain their bond;
                // the exposed cut faces close the wall right up to the gate posts.
                let exitLocal=CityExit.local(center)
                let atExit=side>0 && exitLocal.y > -1.1 && exitLocal.y<CityExit.run+1
                let atService=side<0 && center.y > -15.4 && center.y < -5.3
                if atExit || atService {
                    let along: (SIMD2<Double>)->Double = atExit
                        ? { CityExit.local($0).x } : { $0.x-DirtCourse.serviceEntryX }
                    let halfWidth=atExit ? CityExit.width/2+0.08:DirtCourse.serviceEntryHalfWidth
                    let jambSide=along(center)<0 ? -1.0:1.0
                    func signedDistance(_ p:SIMD2<Double>)->Double { jambSide*along(p)-halfWidth }
                    var clipped:[SIMD2<Double>]=[]
                    for j in corners.indices {
                        let p=corners[j],q=corners[(j+1)%corners.count]
                        let dp=signedDistance(p),dq=signedDistance(q)
                        if dp>=0 { clipped.append(p) }
                        if (dp>=0) != (dq>=0) { clipped.append(p+(q-p)*(dp/(dp-dq))) }
                    }
                    corners=clipped
                    guard corners.count>=3 else { continue }
                }
                let brickCenter=corners.reduce(SIMD2<Double>.zero,+)/Double(corners.count)
                let bottom=corners.map { p in SIMD3<Float>(Float(p.x),Float(base),Float(p.y)) }
                let lip=corners.map { p in SIMD3<Float>(Float(p.x),Float(base+rise-0.012),Float(p.y)) }
                let top=corners.enumerated().map { j,p -> SIMD3<Float> in
                    let q=p+(brickCenter-p)*0.055
                    let chip=row==localRows-1 ? Double((seed+j*7)%7)*0.001:0
                    return SIMD3(Float(q.x),Float(base+rise-chip),Float(q.y))
                }
                for j in corners.indices { let k=(j+1)%corners.count
                    quad(bottom[j],bottom[k],lip[k],lip[j],ink)
                    quad(lip[j],lip[k],top[k],top[j],ink)
                }
                for j in 1..<top.count-1 { mesh.triangle(top[0],top[j+1],top[j],ink) }
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
        WindblownDust.texture(plume:true)
    }
    @discardableResult private func box(_ x:Double,_ y:Double,_ z:Double,_ w:Double,_ h:Double,_ d:Double,_ mat:SCNMaterial)->SCNNode {
        let shape = SCNBox(width:w,height:h,length:d,chamferRadius:0.008); shape.materials = [mat]
        let node = SCNNode(geometry:shape); node.position = SCNVector3(x,y,z); scene.rootNode.addChildNode(node); return node
    }
    func reset() {
        town.reset();duneSand.reset()
        dustBatch.isHidden=true;clodBatch.isHidden=true
        for i in flecks.indices { flecks[i].life = 0; flecks[i].node.isHidden = true }
        trails.forEach { $0.reset() }; emission = [[0,0],[0,0,0],[0],[0,0]]
        racerEmittedCount = [0,0,0,0]; emittedCount = 0; poolIndex = 0
    }
    var additionalContacts: [[(x: Double,z: Double,width: Double)]] = []
    func update(_ state: Simulation, opponent: Simulation, dt: Double, modelScale: Double, additional: [Simulation]) {
        guard dt > 0 else { return }
        storm=state.storm
        let allStates=[state,opponent]+additional
        let allContacts=[[(x:0.262225*modelScale,z:-0.23*modelScale,width:0.155*modelScale),(x: -0.262225*modelScale,z:-0.23*modelScale,width:0.155*modelScale)],R2D2.groundContacts]+additionalContacts
        duneSand.update(states:allStates,contacts:allContacts.enumerated().map { index,feet in
            let tracked=index==0 || index==3
            return feet.map { SandDeformation.Contact(x:$0.x,z:tracked ? 0:$0.z,width:max(0.10,$0.width),length:tracked ? max(abs($0.z)*2,RobotCollisions.profiles[index].halfDepth*1.8):0.12) }
        },dt:dt)
        if let camera { stormVisual.update(storm,camera:camera,dt:dt) }
        for i in flecks.indices where flecks[i].life > 0 {
            flecks[i].life -= dt
            var f = flecks[i]
            // Fine dust loses its launch momentum quickly; grains fall and settle.
            f.velocity *= exp(-dt * (f.dust ? 1.4 : 0.7))
            f.velocity.y += dt * (f.dust ? 0.045 : -9.8)
            if storm.enabled {
                let wind=storm.wind(x:Double(f.node.position.x),z:Double(f.node.position.z))
                f.velocity += (wind-f.velocity)*min(1,dt*(f.dust ? 1.8:0.10))
            }
            f.node.simdPosition += SIMD3<Float>(Float(f.velocity.x*dt),Float(f.velocity.y*dt),Float(f.velocity.z*dt))
            let ground = CGFloat(storm.height(x:Double(f.node.position.x),z:Double(f.node.position.z))+duneSand.field.offset(x:Double(f.node.position.x),z:Double(f.node.position.z)))
            if f.node.position.y < ground + CGFloat(f.radius) {
                f.node.position.y = ground + CGFloat(f.radius)
                if f.dust {
                    // Floor correction must not cancel the initial upward kick.
                    f.velocity.y = max(0,f.velocity.y)
                } else {
                    f.velocity = .zero;f.life = min(f.life,0.09)
                }
            }
            let age = f.duration - f.life
            let fadeIn = min(1,age / (f.dust ? 0.12 : 0.025))
            let fadeOut = min(1,max(0,f.life) / (f.dust ? f.duration*0.65 : 0.12))
            f.node.opacity = CGFloat(f.opacity * fadeIn * fadeOut)
            if f.dust {
                let size = CGFloat(1 + age*0.9)
                f.node.scale = SCNVector3(size,size,size)
            }
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
    /// Per-vertex tints keep the source soil color without per-particle materials.
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
                    let radius=f.radius*f.node.simdScale.x
                    let w=storm.wind(x:Double(center.x),z:Double(center.z))
                    let wind=SIMD3<Float>(Float(w.x),0,Float(w.z))
                    let projected=wind-normal*simd_dot(wind,normal)
                    let along=storm.enabled && simd_length(projected)>0.01 ? simd_normalize(projected):right
                    let across=simd_normalize(simd_cross(normal,along))
                    let stretch:Float=storm.enabled ? 3.8:1.6
                    for (sx,sy) in [(-1.0,-1.0),(1.0,-1.0),(1.0,1.0),(-1.0,1.0)] {
                        vertices.append(SCNVector3(center+along*Float(sx)*radius*stretch+across*Float(sy)*radius*0.65))
                        normals.append(SCNVector3(normal));uv.append(CGPoint(x:(sx+1)/2,y:(sy+1)/2))
                    }
                    indices += [base,base+1,base+2,base,base+2,base+3]
                } else {
                    for v:SIMD3<Float> in [SIMD3(0,1,0),SIMD3(-0.87,-0.5,-0.5),SIMD3(0.87,-0.5,-0.5),SIMD3(0,-0.5,1)] {
                        vertices.append(SCNVector3(center+v*f.radius));normals.append(SCNVector3(simd_normalize(v)));uv.append(.zero)
                    }
                    indices += [base,base+2,base+1,base,base+3,base+2,base,base+1,base+3,base+1,base+2,base+3]
                }
                for _ in 0..<4 { rgba += [f.tint.x,f.tint.y,f.tint.z,Float(f.node.opacity)] }
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
            guard magnitude > 0.18, !state.contacting, state.hasDirtContact else { continue }
            let sign = speed > 0 ? 1.0 : -1.0
            let contactZ = (racer == 0 || racer == 3) ? -max(abs(contact.z),RobotCollisions.profiles[racer].halfDepth*0.85)*sign : contact.z
            let inDunes=max(abs(state.x),abs(state.z))>DesertTerrain.townEdge+8
            let contactLoad=inDunes ? duneSand.field.contactWeight(state,.init(x:contact.x,z:contactZ,width:contact.width,length:0.1)):1
            guard contactLoad>0 else { continue }
            emission[racer][side] += dt*min(2.5,magnitude)*22*contactLoad
            while emission[racer][side] >= 1 {
                emission[racer][side] -= 1
                let i = poolIndex; poolIndex = (poolIndex+1)%poolSize
                // Tracks shed from their trailing end. Fixed wheel contacts stay
                // in place when reversing (including R2's front wheel).
                var position = origin + lateral*(contact.x + Double.random(in:-contact.width*0.35...contact.width*0.35)) + forward*contactZ
                position.y = state.terrainHeight(x:position.x,z:position.z) + 0.018
                // Match the dune shader's feather at the town edge. Track clay
                // transitions to town soil across the same sandy shoulder.
                let edge = max(abs(position.x),abs(position.z))
                let t = max(0,min(1,(edge-156)/36))
                let dune = t*t*(3-2*t)
                let cover=min(1,state.storm.depth(x:position.x,z:position.z)/0.025)
                let clay = edge < 80 ? max(0,min(1,(DirtCourse.width+0.7-DirtCourse.projection(x:position.x,z:position.z).distance)/0.9)) : 0
                let town = SIMD3<Float>(0.62,0.55,0.45)
                let track = SIMD3<Float>(0.64,0.43,0.31)
                let sand = SIMD3<Float>(0.78,0.57,0.34)
                let soil = ((town+(track-town)*Float(clay))*(1-Float(dune))+sand*Float(dune))*(1-Float(cover))+sand*Float(cover)
                var f = flecks[i]
                let clayWeight = clay*(1-dune)*(1-cover)
                // Clay throws cohesive clods; dry dune sand mostly lofts fines.
                f.dust = Double.random(in:0...1) > (0.25 + 0.42*clayWeight)
                f.tint = soil * Float.random(in:0.94...1.06)
                // Suspended fines scatter light; grains retain the soil albedo.
                if f.dust { f.tint = f.tint*0.90 + SIMD3<Float>(0.07,0.055,0.035) }
                let kick = min(1.6,magnitude)
                f.velocity = -forward*sign*kick*Double.random(in:0.08...0.22)
                    + lateral*Double.random(in:-0.09...0.09)
                    + SIMD3(0,Double.random(in:f.dust ? 0.30...0.55 : 0.16...0.38)*sqrt(kick),0)
                if !f.dust {
                    f.velocity += -forward*sign*kick*(0.35*clayWeight)
                        + SIMD3(0,Double.random(in:0.25...0.65)*sqrt(kick)*clayWeight,0)
                }
                f.duration = f.dust ? Double.random(in:1.2...1.8) : Double.random(in:0.22...0.38)+clayWeight*0.5
                f.life = f.duration
                f.radius = f.dust ? Float.random(in:0.12...0.20) : Float.random(in:0.0015...0.0035)+Float(clayWeight)*Float.random(in:0.006...0.012)
                f.opacity = f.dust ? (state.storm.enabled ? 0.36:0.48) : 0.70
                f.node.position = SCNVector3(position.x,position.y,position.z)
                f.node.scale = SCNVector3(1,1,1); f.node.opacity = 0; f.node.isHidden = false
                flecks[i] = f
                emittedCount += 1; racerEmittedCount[racer] += 1
            }
        }
    }
}

extension DirtWorld {
    /// Exercise actual emission and pooled geometry on clay, town soil and dunes.
    func checkDebris() -> Bool {
        var passed = checkClodRendering()
        var grainSizes: [Double] = []
        var grainColors: [SIMD3<Float>] = []
        for (name, point) in [("clay",SIMD2<Double>(0,-15)),("town",SIMD2<Double>(90,40)),("dunes",SIMD2<Double>(210,65))] {
            reset()
            let p = DirtCourse.projection(x:point.x,z:point.y)
            var state = Simulation(dirtTrack:true,dirtStartOffset:p.offset,dirtStartPhase:p.phase)
            if name == "clay" { state = Simulation(dirtTrack:true) }
            var input = DriveInput(); input.throttle = 0.7
            var physics = DirtRacePhysics(), race = DirtRace()
            race.countDown(dt:3)
            var opponents = [DirtOpponent(),DirtOpponent(slot:DirtCourse.startingGrid[2]),DirtOpponent(slot:DirtCourse.startingGrid[3])]
            for _ in 0..<30 { physics.advance(input,player:&state,race:&race,opponents:&opponents,dt:1.0/60,raceDT:1.0/60,robotCollisionsEnabled:false) }
            for _ in 0..<90 { emit(state,racer:0,dt:1.0/60,contacts:[(0.26,-0.23,0.15),(-0.26,-0.23,0.15)]) }
            let live = flecks.filter { $0.life > 0 }, grains = live.filter { !$0.dust }
            let average = grains.map { Double($0.radius) }.reduce(0,+)/Double(max(1,grains.count))
            grainSizes.append(average)
            grainColors.append(grains.map { $0.tint }.reduce(.zero,+)/Float(max(1,grains.count)))
            let grounded = live.allSatisfy { abs(Double($0.node.position.y)-DirtCourse.height(x:Double($0.node.position.x),z:Double($0.node.position.z))-0.018)<0.0001 }
            let finite = live.allSatisfy { $0.tint.x.isFinite && $0.velocity.y.isFinite }
            passed = passed && live.count>20 && !grains.isEmpty && grounded && finite
            if name == "dunes" { passed = passed && grains.allSatisfy { $0.radius<=0.0035 } }
            rebuildDebrisBatches()
            let colors = clodBatch.geometry?.sources(for:.color).first
            passed = passed && colors != nil && !(clodBatch.isHidden || dustBatch.isHidden)
            let count = emittedCount
            let tint = live.first?.tint
            update(state,opponent:state,dt:0,modelScale:1,additional:[])
            passed = passed && count == emittedCount && tint == flecks.first(where:{$0.life>0})?.tint
            state.stop()
            for _ in 0..<120 { update(state,opponent:state,dt:1.0/60,modelScale:1,additional:[]) }
            passed = passed && emittedCount == count && flecks.allSatisfy { $0.life<=0 } && dustBatch.isHidden && clodBatch.isHidden
            print("Debris \(name): \(live.count) particles, average grain radius \(average*1000) mm, contact heights \(grounded)")
        }
        passed = passed && grainSizes[0]>grainSizes[2]*3
            && grainColors[2].x>grainColors[0].x+0.08
            && grainColors[2].y>grainColors[0].y+0.08
        reset()
        passed = passed && emittedCount == 0 && racerEmittedCount == [0,0,0,0]
        print("Ground-dependent debris: \(passed ? "PASS":"FAIL")")
        return passed
    }
}


extension DirtWorld {
    private func checkClodRendering()->Bool {
        let testScene=SCNScene(),camera=SCNNode(),light=SCNNode(),sample=SCNNode()
        testScene.background.contents=NSColor.black
        camera.camera=SCNCamera();camera.camera?.usesOrthographicProjection=true;camera.camera?.orthographicScale=1
        camera.position=SCNVector3(0,0,2)
        light.light=SCNLight();light.light?.type = .ambient;light.light?.color=NSColor.white;light.light?.intensity=1000
        for node in [camera,light,sample] { testScene.rootNode.addChildNode(node) }
        let renderer=SCNRenderer(device:nil,options:nil);renderer.scene=testScene;renderer.pointOfView=camera
        var pixels=[SIMD3<Double>]()
        for tint:SIMD3<Float> in [SIMD3(0.64,0.43,0.31),SIMD3(0.78,0.57,0.34)] {
            let rgba=(0..<4).flatMap{_ in [tint.x,tint.y,tint.z,Float(1)]}
            let colors=rgba.withUnsafeBytes { SCNGeometrySource(data:Data($0),semantic:.color,vectorCount:4,usesFloatComponents:true,componentsPerVector:4,bytesPerComponent:4,dataOffset:0,dataStride:16) }
            let mesh=SCNGeometry(sources:[SCNGeometrySource(vertices:[SCNVector3(-1,-1,0),SCNVector3(1,-1,0),SCNVector3(1,1,0),SCNVector3(-1,1,0)]),SCNGeometrySource(normals:Array(repeating:SCNVector3(0,0,1),count:4)),colors],elements:[SCNGeometryElement(indices:[Int32(0),1,2,0,2,3],primitiveType:.triangles)])
            mesh.materials=clodGeometry.materials;sample.geometry=mesh
            let image=renderer.snapshot(atTime:0,with:CGSize(width:64,height:64),antialiasingMode:.none)
            guard let tiff=image.tiffRepresentation,let bitmap=NSBitmapImageRep(data:tiff),let c=bitmap.colorAt(x:32,y:32)?.usingColorSpace(.deviceRGB) else { return false }
            pixels.append(SIMD3(Double(c.redComponent),Double(c.greenComponent),Double(c.blueComponent)))
        }
        let clay=pixels[0],sand=pixels[1]
        let passed=clay.x>clay.y*1.15 && clay.y>clay.z*1.1 && sand.x>clay.x+0.03 && sand.y>clay.y+0.03
        print("Rendered clod colors: clay \(clay), sand \(sand): \(passed ? "PASS":"FAIL")")
        return passed
    }
}

// Diagnostic pairs retain identical simulation state and vary only dust visibility.
extension DirtWorld {
    func diagnosticDust(_ visible:Bool) { rebuildDebrisBatches();dustBatch.isHidden = !visible }
    var diagnosticDustCount:Int { flecks.filter{$0.life>0 && $0.dust}.count }
}

extension DirtWorld {
    func printDustOpacity() {
        let bitmap=NSBitmapImageRep(data:dustTexture().tiffRepresentation!)!
        var maximum=0.0,total=0.0
        for y in 0..<bitmap.pixelsHigh { for x in 0..<bitmap.pixelsWide {
            let a=Double(bitmap.colorAt(x:x,y:y)!.alphaComponent);maximum=max(maximum,a);total += a
        }}
        let live=flecks.filter{$0.life>0 && $0.dust}
        let opacity=live.map{Double($0.node.opacity)}.max() ?? 0
        print("Dust opacity: texture peak \(maximum), texture mean \(total/Double(bitmap.pixelsHigh*bitmap.pixelsWide)), live peak \(opacity), effective peak \(maximum*opacity)")
    }
}
