import AppKit
import SceneKit
import SimulationCore

/// Procedural clay, directional ruts, world-space tread marks and pooled debris.
/// Geometry and textures are generated locally; no network assets are required.
final class DirtWorld {
    let scene = SCNScene()
    private let effects = SCNNode()
    private var flecks: [(node: SCNNode, velocity: SIMD3<Double>, life: Double, dust: Bool)] = []
    private let trails = [DirtTrail(style:.tracks), DirtTrail(style:.tires)]
    private var emission = [[0.0, 0.0], [0.0, 0.0, 0.0]]
    private(set) var racerEmittedCount = [0, 0]
    var trailCounts: [Int] { trails.map { $0.count } }
    private let dustMaterial = SCNMaterial()
    private let clodGeometry = SCNSphere(radius: 0.012)
    private let poolSize = 960
    private var poolIndex = 0
    private(set) var emittedCount = 0

    init() {
        scene.background.contents = color(0xc8d7df)
        scene.fogColor = color(0xc8d7df); scene.fogStartDistance = 70; scene.fogEndDistance = 140
        let ambient = SCNNode(); ambient.light = SCNLight(); ambient.light?.type = .ambient
        ambient.light?.intensity = 600; ambient.light?.color = color(0xd8e5f2)
        scene.rootNode.addChildNode(ambient)
        let sun = SCNNode(); sun.light = SCNLight(); sun.light?.type = .directional
        sun.eulerAngles = SCNVector3(-0.85, -0.6, 0)
        sun.light?.intensity = 1550; sun.light?.color = color(0xffe6c1)
        sun.light?.castsShadow = true; sun.light?.shadowMode = .deferred
        sun.light?.shadowMapSize = CGSize(width: 4096, height: 4096)
        sun.light?.orthographicScale = 28; sun.light?.shadowRadius = 5
        sun.light?.shadowColor = NSColor.black.withAlphaComponent(0.30)
        scene.rootNode.addChildNode(sun)
        let ground = SCNPlane(width: 300, height: 300)
        let earth = material(0x827656, roughness: 1)
        earth.diffuse.contents = soilTexture(track: false, normal: false)
        earth.normal.contents = soilTexture(track: false, normal: true)
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
        let ring = courseSurface(inner: -DirtCourse.width, outer: DirtCourse.width, y: 0)
        ring.materials = [clay]; scene.rootNode.addChildNode(SCNNode(geometry: ring))
        // Raised loose-soil berms stay outside the driveable surface.
        let berm = courseSurface(inner: DirtCourse.width, outer: DirtCourse.width+DirtCourse.bermWidth, y: 0.10)
        berm.materials = [clay]
        scene.rootNode.addChildNode(SCNNode(geometry: berm))
        let inner = courseSurface(inner: -DirtCourse.width-DirtCourse.bermWidth, outer: -DirtCourse.width, y: 0.055)
        inner.materials = [clay]
        scene.rootNode.addChildNode(SCNNode(geometry: inner))
        for (innerEdge,outerEdge) in [(-DirtCourse.terrainEdge,-DirtCourse.width-DirtCourse.bermWidth),(DirtCourse.width+DirtCourse.bermWidth,DirtCourse.terrainEdge)] {
            let shoulder = courseSurface(inner:innerEdge,outer:outerEdge,y:-1)
            shoulder.materials = [clay]; scene.rootNode.addChildNode(SCNNode(geometry:shoulder))
        }
        // Dense continuous tape follows the ground, including between posts.
        // Posts use the very same terrain height and extend below its surface.
        for side in [-1.0,1.0] {
            let boundary = DirtCourse.surfacePoints(offset:side*DirtCourse.fenceOffset)
            var vertices: [SCNVector3] = [], red: [Int32] = [], white: [Int32] = []
            for (i,p) in boundary.enumerated() {
                let ground = DirtCourse.height(x:p.x,z:p.y)
                for edge in [-0.0225,0.0225] {
                    vertices.append(SCNVector3(p.x,ground+DirtCourse.railClearance+edge,p.y))
                }
                if i < boundary.count-1 {
                    let a = Int32(i*2)
                    let indices: [Int32] = [a,a+2,a+1,a+1,a+2,a+3]
                    if (i/5)%2 == 0 { red += indices } else { white += indices }
                }
                if i%4 == 0 && i < boundary.count-1 && (i == 0 || p != boundary[i-1]) {
                    let height = DirtCourse.postHeight+DirtCourse.postEmbed
                    let post = box(p.x,ground-DirtCourse.postEmbed+height/2,p.y,0.035,height,0.035,material(0xe4c5a0))
                    post.name = "Dirt fence post"
                }
            }
            let rail = SCNGeometry(sources:[SCNGeometrySource(vertices:vertices)],elements:[
                SCNGeometryElement(indices:red,primitiveType:.triangles),
                SCNGeometryElement(indices:white,primitiveType:.triangles)])
            rail.materials = [material(0xa24d32),material(0xe8ddd0)]
            let node = SCNNode(geometry:rail); node.name = "Dirt fence rail"
            scene.rootNode.addChildNode(node)
        }
        // Start / finish checker paint, across the full lane at phase zero.
        let start = DirtCourse.point(0)
        for row in 0..<2 { for cell in 0..<10 {
            let tile = box(start.x+Double(row)*0.15-0.15, 0.003, start.z-DirtCourse.width+(Double(cell)+0.5)*(DirtCourse.width*2/10),
                           0.15, 0.004, DirtCourse.width*2/10, material((row+cell)%2 == 0 ? 0xddd2b9 : 0x393a32))
            tile.castsShadow = false
        }}
        // Staggered two-column starting boxes, open at the rear.
        for slot in [DirtCourse.playerGrid, DirtCourse.opponentGrid] {
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
        // Race furnishings give the course a sense of scale and purpose.
        for x in [-0.3, 0.3] { box(x,0.9,start.z-DirtCourse.terrainEdge-0.5,0.045,1.8,0.045,material(0x494c43)) }
        let sign = SCNText(string: "DIRT TRACK", extrusionDepth: 0.002)
        sign.font = .systemFont(ofSize: 0.18, weight: .heavy); sign.materials = [material(0xeee6cf)]
        let board = box(0,1.6,start.z-DirtCourse.terrainEdge-0.5,1.65,0.38,0.055,material(0x34473a))
        let label = SCNNode(geometry: sign); label.position = SCNVector3(-0.69, -0.10, 0.035); board.addChildNode(label)
        // Low bleachers beyond the back straight.
        for row in 0..<4 {
            box(0,Double(row)*0.22+0.15,20.4+Double(row)*0.45,8,0.16,0.4,material(0x8c9691,roughness:0.6))
        }
        scene.rootNode.addChildNode(effects)
        clodGeometry.segmentCount = 5; clodGeometry.materials = [material(0x705033,roughness:1)]
        dustMaterial.lightingModel = .constant; dustMaterial.diffuse.contents = dustTexture()
        dustMaterial.writesToDepthBuffer = false; dustMaterial.isDoubleSided = true
        for i in 0..<poolSize {
            let dust = i % 3 == 0
            let node = SCNNode(geometry: dust ? SCNPlane(width: 0.18,height: 0.18) : clodGeometry)
            if dust { node.geometry?.materials = [dustMaterial]; node.constraints = [SCNBillboardConstraint()] }
            node.castsShadow = false; node.isHidden = true; effects.addChildNode(node)
            flecks.append((node,.zero,0,dust))
        }
        for trail in trails { effects.addChildNode(trail.root) }
    }

    private func courseSurface(inner: Double, outer: Double, y: Double) -> SCNGeometry {
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
                    indices += [a,b,a+1,a+1,b,b+1]
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
                rgb = track ? [0.57+value,0.37+value*0.8,0.23+value*0.55] : [0.43+value,0.44+value,0.29+value*0.7]
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
        for i in flecks.indices { flecks[i].life = 0; flecks[i].node.isHidden = true }
        trails.forEach { $0.reset() }; emission = [[0,0],[0,0,0]]
        racerEmittedCount = [0,0]; emittedCount = 0; poolIndex = 0
    }
    func update(_ state: Simulation, opponent: Simulation, dt: Double, modelScale: Double) {
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
        emit(opponent, racer:1, dt:dt, contacts:[(0.205,-0.205,0.13),(-0.205,-0.205,0.13),(0,0.103,0.085)])
    }
    private func emit(_ state: Simulation, racer: Int, dt: Double,
                      contacts: [(x: Double, z: Double, width: Double)]) {
        trails[racer].update(state, contacts:contacts)
        let forward = SIMD3<Double>(sin(state.heading),0,cos(state.heading))
        let lateral = SIMD3<Double>(cos(state.heading),0,-sin(state.heading))
        let origin = SIMD3<Double>(state.x,state.groundY+0.035,state.z)
        for (side, contact) in contacts.enumerated() {
            let speed = side == 0 ? state.leftSpeed : side == 1 ? state.rightSpeed : state.speed
            let magnitude = abs(speed)
            guard magnitude > 0.08, !state.contacting, !state.airborne else { continue }
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
