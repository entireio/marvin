import AppKit
import SceneKit
import SimulationCore
import simd

/// Attributed mesh assets with movement driven by simulation travel, never a
/// free-running walk animation. The original WALL-E track loop is retained.
final class ImportedRacer {
    enum Kind: String { case bb8 = "BB8", wallE = "WallE" }
    struct Mesh: Decodable {
        struct Surface: Decodable {
            let color: [Double], base: String?, normal: String?, emission: String?, metalRough: String?
            let metal: Double, rough: Double
        }
        struct Part: Decodable {
            let name: String, role: String, side: Int, material: Int
            let positions: [[Float]], normals: [[Float]], texcoords: [[Float]], indices: [UInt32]
            let pivot: [Double], frames: [[Float]]
        }
        let parts: [Part], materials: [Surface]
        let height: Double, ballRadius: Double, beltLength: Double
        let ballCenter: [Double], headCenter: [Double], contacts: [[Double]], armPivots: [[Double]]
    }
    let kind: Kind, root = SCNNode(), ball = SCNNode(), head = SCNNode()
    let dirtCoating = DirtCoating()
    let headMount = SCNNode()
    private(set) var arms: [SCNNode] = []
    let height: Double, ballRadius: Double, beltLength: Double
    let contacts: [(x: Double,z: Double,width: Double)]
    private(set) var triangleCount = 0
    private(set) var links: [(node: SCNNode,side: Int,frames: [simd_float4x4])] = []
    private var gears: [(node: SCNNode,side: Int,radius: Double)] = []
    private var previous: (x: Double,z: Double,distance: Double)?

    init(kind: Kind, resources: URL) throws {
        self.kind = kind
        let folder = resources.appendingPathComponent(kind.rawValue+"/Generated")
        let mesh = try JSONDecoder().decode(Mesh.self,from:Data(contentsOf:folder.appendingPathComponent("mesh.json")))
        height = mesh.height; ballRadius = mesh.ballRadius; beltLength = mesh.beltLength
        contacts = mesh.contacts.map { ($0[0],$0[1],$0[2]) }
        root.name = kind == .bb8 ? "BB-8 · Willy Decarpentrie · CC BY 4.0" : "WALL-E · Janis Zeps · CC BY 4.0"
        func texture(_ name: String?) throws -> NSImage? {
            guard let name else { return nil }
            guard let image = NSImage(contentsOf:folder.appendingPathComponent(name)) else { throw CocoaError(.fileReadCorruptFile) }
            return image
        }
        let materials = try mesh.materials.map { source -> SCNMaterial in
            let m = SCNMaterial(); m.lightingModel = .physicallyBased; m.isDoubleSided = true
            m.diffuse.contents = try texture(source.base) ?? NSColor(srgbRed:source.color[0],green:source.color[1],blue:source.color[2],alpha:source.color[3])
            m.normal.contents = try texture(source.normal)
            m.emission.contents = try texture(source.emission)
            m.metalness.contents = source.metal; m.roughness.contents = source.rough
            if let packed = try texture(source.metalRough) {
                m.metalness.contents = packed; m.metalness.textureComponents = .blue
                m.roughness.contents = packed; m.roughness.textureComponents = .green
                m.ambientOcclusion.contents = packed; m.ambientOcclusion.textureComponents = .red
            }
            return m
        }
        ball.position = SCNVector3(mesh.ballCenter[0],mesh.ballCenter[1],mesh.ballCenter[2])
        head.position = SCNVector3(mesh.headCenter[0],mesh.headCenter[1],mesh.headCenter[2])
        root.addChildNode(ball)
        if kind == .bb8 {
            headMount.position = ball.position
            head.position = SCNVector3(head.position.x-ball.position.x,head.position.y-ball.position.y,head.position.z-ball.position.z)
            root.addChildNode(headMount); headMount.addChildNode(head)
        } else { root.addChildNode(head) }
        for pivot in mesh.armPivots {
            let arm = SCNNode(); arm.position = SCNVector3(pivot[0],pivot[1],pivot[2])
            root.addChildNode(arm); arms.append(arm)
        }
        for part in mesh.parts {
            guard part.positions.count == part.normals.count, part.positions.count == part.texcoords.count,
                  part.indices.allSatisfy({ Int($0)<part.positions.count }), materials.indices.contains(part.material) else { throw CocoaError(.fileReadCorruptFile) }
            let geometry = SCNGeometry(sources:[SCNGeometrySource(vertices:part.positions.map { SCNVector3($0[0],$0[1],$0[2]) }),
                SCNGeometrySource(normals:part.normals.map { SCNVector3($0[0],$0[1],$0[2]) }),
                SCNGeometrySource(textureCoordinates:part.texcoords.map { CGPoint(x:CGFloat($0[0]),y:CGFloat($0[1])) })],
                elements:[SCNGeometryElement(indices:part.indices,primitiveType:.triangles)])
            geometry.materials = [materials[part.material]]
            let node = SCNNode(geometry:geometry); node.name = part.role+":"+part.name; node.castsShadow = true
            if part.role == "ball" { ball.addChildNode(node) }
            else if part.role == "head" { head.addChildNode(node) }
            else if part.role == "arm" { arms[part.side > 0 ? 0 : 1].addChildNode(node) }
            else {
                node.position = SCNVector3(part.pivot[0],part.pivot[1],part.pivot[2]); root.addChildNode(node)
            }
            if part.role == "link" {
                let frames = part.frames.map { values -> simd_float4x4 in
                    simd_float4x4(columns:(SIMD4(values[0],values[1],values[2],values[3]),SIMD4(values[4],values[5],values[6],values[7]),SIMD4(values[8],values[9],values[10],values[11]),SIMD4(values[12],values[13],values[14],values[15])))
                }
                links.append((node,part.side,frames))
            } else if part.role == "gear" {
                let b = geometry.boundingBox
                gears.append((node,part.side,Double(max(b.max.y-b.min.y,b.max.z-b.min.z))/2))
            }
            triangleCount += part.indices.count/3
        }
        dirtCoating.install(on:root,height:height,wheelOffset:kind == .wallE ? abs(contacts[0].x) : 0,rolling:kind == .bb8)
    }
    func update(_ state: Simulation) {
        dirtCoating.update(state)
        let reset = previous == nil || state.distance < previous!.distance || state.distance == 0
        root.position = SCNVector3(state.x,state.groundY,state.z)
        if kind == .bb8 {
            if reset { ball.simdOrientation = simd_quatf(angle:0,axis:SIMD3(1,0,0)) }
            else if let previous, !state.airborne {
                let dx = state.x-previous.x, dz = state.z-previous.z, distance = hypot(dx,dz)
                if distance > 1e-9 && distance < 2 {
                    let turn = simd_quatf(angle:Float(distance/ballRadius),axis:SIMD3(Float(dz/distance),0,Float(-dx/distance)))
                    ball.simdOrientation = simd_normalize(turn*ball.simdOrientation)
                }
            }
            // Head steers independently and remains above the rolling sphere.
            headMount.eulerAngles = SCNVector3(0,state.heading,0)
            head.eulerAngles = SCNVector3Zero
        } else {
            root.eulerAngles = SCNVector3(state.bodyPitch,state.heading,state.bodyRoll)
            for link in links {
                let travel = link.side > 0 ? state.leftTravel : state.rightTravel
                var phase = (travel/beltLength).truncatingRemainder(dividingBy:1)
                if phase < 0 { phase += 1 }
                let t = Float(phase)*Float(link.frames.count-1), i = Int(t), f = t-Float(i)
                let a = link.frames[i], b = link.frames[min(i+1,link.frames.count-1)]
                link.node.simdPosition = SIMD3(a.columns.3.x,a.columns.3.y,a.columns.3.z)*(1-f)+SIMD3(b.columns.3.x,b.columns.3.y,b.columns.3.z)*f
                link.node.simdOrientation = simd_slerp(simd_quatf(a),simd_quatf(b),f)
            }
            for gear in gears { gear.node.eulerAngles.x = CGFloat((gear.side > 0 ? state.leftTravel : state.rightTravel)/max(gear.radius,0.01)) }
            head.eulerAngles = SCNVector3Zero
            applyArms(RacePerformance.Pose())
        }
        previous = (state.x,state.z,state.distance)
    }
    func applyExpression(_ pose: RacePerformance.Pose, heading: Double) {
        if kind == .bb8 {
            // Orbit the head around the shell center, keeping the contact point
            // on the sphere while the shell rolls independently beneath it.
            headMount.eulerAngles = SCNVector3(-pose.pitch,heading,pose.roll)
            head.eulerAngles.y = CGFloat(pose.yaw)
        } else {
            head.eulerAngles = SCNVector3(-pose.pitch,pose.yaw,pose.roll)
            applyArms(pose)
        }
    }
    private func applyArms(_ pose: RacePerformance.Pose) {
        guard arms.count == 2 else { return }
        // Lower the source model's permanently raised hand into a driving pose.
        // Positive gesture values lift the appropriate arm from its shoulder.
        arms[0].eulerAngles = SCNVector3(-0.15-pose.leftArm,0,pose.leftArm*0.25)
        arms[1].eulerAngles = SCNVector3(1.20-pose.rightArm,0,-pose.rightArm*0.25)
    }
    /// Native smoke checks the actual scene nodes, including signed motion.
    func checkMotion() -> Bool {
        var state = Simulation(seed:0,dirtTrack:true), input = DriveInput()
        update(state)
        let bounds = root.boundingBox
        guard abs(Double(bounds.max.y-bounds.min.y)-height) < 0.002 else { return false }
        func same(_ a: simd_quatf, _ b: simd_quatf) -> Bool { abs(simd_dot(a.vector,b.vector)) > 0.999999 }
        if kind == .bb8 {
            let initial = ball.simdOrientation
            input.throttle = 1; state.advance(input,dt:0.1); update(state)
            let forward = ball.simdOrientation
            guard !same(initial,forward), abs(head.eulerAngles.x) < 1e-7, abs(head.eulerAngles.z) < 1e-7 else { return false }
            let expectedAxis = SIMD3<Float>(Float(cos(state.heading)),0,Float(-sin(state.heading)))
            guard simd_dot(forward.axis,expectedAxis) > 0.999 else { return false }
            input.brake = true; state.advance(input,dt:0.1); update(state)
            guard same(forward,ball.simdOrientation) else { return false }
            state.reset(); update(state)
            guard same(initial,ball.simdOrientation) else { return false }
            input.brake = false; input.throttle = -1; state.advance(input,dt:0.1); update(state)
            guard simd_dot(ball.simdOrientation.axis,expectedAxis) < -0.999 else { return false }
        } else {
            guard links.count == 58, beltLength > 1 else { return false }
            let bottoms = [1,-1].map { side in links.filter { $0.side == side }.min { $0.node.position.y < $1.node.position.y }! }
            let initial = bottoms.map { $0.node.simdPosition }
            // Pivoting drives the two belts in opposite directions.
            input.turn = 1; state.advance(input,dt:0.1); update(state)
            let turned = bottoms.map { $0.node.simdPosition }
            guard (turned[0].z-initial[0].z)*(turned[1].z-initial[1].z) < 0 else { return false }
            input.brake = true; state.advance(input,dt:0.1); update(state)
            guard zip(bottoms,turned).allSatisfy({ simd_distance($0.0.node.simdPosition,$0.1) < 1e-7 }) else { return false }
            state.reset(); update(state)
            guard zip(bottoms,initial).allSatisfy({ simd_distance($0.0.node.simdPosition,$0.1) < 1e-7 }) else { return false }
            input.turn = 0; input.brake = false; input.throttle = 1; state.advance(input,dt:0.1); update(state)
            guard zip(bottoms,initial).allSatisfy({ $0.0.node.simdPosition.z < $0.1.z }) else { return false }
            state.reset(); update(state)
            input.throttle = -1; state.advance(input,dt:0.1); update(state)
            guard zip(bottoms,initial).allSatisfy({ $0.0.node.simdPosition.z > $0.1.z }) else { return false }
        }
        state.reset(); update(state)
        return true
    }

}
