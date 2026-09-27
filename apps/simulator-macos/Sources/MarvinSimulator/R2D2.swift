import AppKit
import SceneKit
import SimulationCore

/// LordDiego's detailed CC BY model in its deployed three-leg driving pose.
final class R2D2 {
    static let heightMeters = 1.08
    // Exported body height 0.85 plus the 0.035 wheel clearance.
    static let sceneHeight = 0.885
    // Media-Conversions Colson drive/caster configuration; see WHEEL_REFERENCE.md.
    struct Tire {
        let diameterInches: Double, widthInches: Double
        var radius: Double { diameterInches * 0.0254 * sceneHeight / heightMeters / 2 }
        var width: Double { widthInches * 0.0254 * sceneHeight / heightMeters }
    }
    static let outerTire = Tire(diameterInches:5, widthInches:1.25)
    static let centerTire = Tire(diameterInches:3, widthInches:0.875)
    // Mount coordinates fit the existing artistic mesh, not a dimensioned chassis.
    static let groundContacts = [(x:0.205,z:-0.205,width:outerTire.width),
                                (x:-0.205,z:-0.205,width:outerTire.width),
                                (x:0.0,z:0.147,width:centerTire.width)]
    private struct Mesh: Decodable {
        struct Part: Decodable {
            let name: String, head: Bool
            let positions: [[Float]], normals: [[Float]], texcoords: [[Float]], indices: [UInt32]
            let material: Int
        }
        let parts: [Part], headPivot: [Float], headAxis: [Float]
    }
    struct Wheel {
        let node: SCNNode, side: Int, radius: Double
    }
    let dirtCoating = DirtCoating()
    let root = SCNNode(), head = SCNNode()
    private(set) var wheels: [Wheel] = []
    private(set) var triangleCount = 0
    private(set) var hasCenterLeg = false
    private var headAxis = SCNVector3(0, 1, 0)

    init(resources: URL) throws {
        let directory = resources.appendingPathComponent("R2D2")
        let mesh = try JSONDecoder().decode(Mesh.self, from: Data(contentsOf: directory.appendingPathComponent("mesh.json")))
        guard mesh.headPivot.count == 3, mesh.headAxis.count == 3 else { throw CocoaError(.fileReadCorruptFile) }
        root.name = "R2-D2 · LordDiego · CC BY 4.0"
        let pivot = SCNVector3(mesh.headPivot[0], mesh.headPivot[1], mesh.headPivot[2])
        headAxis = SCNVector3(mesh.headAxis[0], mesh.headAxis[1], mesh.headAxis[2])
        head.position = pivot; root.addChildNode(head)
        func texture(_ name: String) throws -> NSImage {
            guard let image = NSImage(contentsOf: directory.appendingPathComponent("Textures/\(name).png")) else {
                throw CocoaError(.fileReadCorruptFile)
            }
            return image
        }
        let shell = SCNMaterial()
        shell.lightingModel = .physicallyBased
        shell.diffuse.contents = try texture("R2D2_Base_Color")
        shell.metalness.contents = try texture("R2D2_Metalness")
        shell.roughness.contents = try texture("R2D2_Roughness")
        shell.emission.contents = try texture("R2D2_Emission")
        let panels = SCNMaterial()
        panels.lightingModel = .physicallyBased
        panels.diffuse.contents = try texture("R2D2_Barrel_Base")
        panels.roughness.contents = try texture("R2D2_Barrel_Roughness")
        panels.normal.contents = try texture("R2D2_Barrel_Normal")
        panels.metalness.contents = 0.15
        let surfaces = [shell, panels]
        for part in mesh.parts {
            guard !part.positions.isEmpty, part.positions.count == part.normals.count,
                  part.positions.count == part.texcoords.count,
                  part.positions.allSatisfy({ $0.count == 3 }), part.normals.allSatisfy({ $0.count == 3 }),
                  part.texcoords.allSatisfy({ $0.count == 2 }), surfaces.indices.contains(part.material),
                  part.indices.count % 3 == 0,
                  part.indices.allSatisfy({ Int($0) < part.positions.count }) else {
                throw CocoaError(.fileReadCorruptFile)
            }
            let vertices = SCNGeometrySource(vertices: part.positions.map {
                part.head ? SCNVector3(CGFloat($0[0])-pivot.x, CGFloat($0[1])-pivot.y, CGFloat($0[2])-pivot.z)
                    : SCNVector3($0[0], $0[1], $0[2])
            })
            let normals = SCNGeometrySource(normals: part.normals.map { SCNVector3($0[0], $0[1], $0[2]) })
            let uv = SCNGeometrySource(textureCoordinates: part.texcoords.map { CGPoint(x:CGFloat($0[0]), y:CGFloat($0[1])) })
            let elements = SCNGeometryElement(indices: part.indices, primitiveType: .triangles)
            let geometry = SCNGeometry(sources: [vertices, normals, uv], elements: [elements])
            geometry.materials = [surfaces[part.material]]
            let node = SCNNode(geometry: geometry); node.name = part.name
            (part.head ? head : root).addChildNode(node)
            triangleCount += part.indices.count/3
            if part.name == "R2D2_Leg_Center" { hasCenterLeg = true }
        }
        // Four outer drive wheels and one center caster, using documented
        // Colson dimensions instead of widths inferred from the foot shells.
        for side in [-1, 1] {
            for z in [-0.205, -0.045] { addWheel(x:Double(side)*0.205, z:z, tire:Self.outerTire, side:side) }
        }
        addWheel(x:0, z:0.147, tire:Self.centerTire, side:0)
        dirtCoating.install(on:root,height:Self.sceneHeight,wheelOffset:0.205)
    }

    private func addWheel(x: Double, z: Double, tire: Tire, side: Int) {
        let radius = tire.radius, width = tire.width
        let axle = SCNNode(); axle.name = "R2-D2 rolling tire"
        axle.position = SCNVector3(x, radius, z)
        let rubber = material(0x555653, roughness:0.94)
        let hub = material(0x8b969e, metal:0.75, roughness:0.38)
        func cylinder(radius:Double, width:Double, surface:SCNMaterial) -> SCNNode {
            let shape = SCNCylinder(radius:radius,height:width); shape.radialSegmentCount = 48
            shape.materials = [surface]
            let node = SCNNode(geometry:shape); node.eulerAngles.z = .pi/2
            return node
        }
        axle.addChildNode(cylinder(radius:radius,width:width,surface:rubber))
        axle.addChildNode(cylinder(radius:radius*0.52,width:width+0.002,surface:hub))
        // Performa flat rubber tread is smooth; no invented knobby bars.
        for face in [-1.0,1.0] {
            for i in 0..<5 {
                let angle = Double(i)*2 * .pi/5
                let spoke = SCNBox(width:0.003,height:radius*0.54,length:radius*0.125,chamferRadius:0.001)
                spoke.materials = [hub]
                let node = SCNNode(geometry:spoke)
                node.position = SCNVector3(face*(width/2+0.001),cos(angle)*radius*0.375,sin(angle)*radius*0.375)
                node.eulerAngles.x = angle; axle.addChildNode(node)
            }
        }
        root.addChildNode(axle); wheels.append(Wheel(node:axle,side:side,radius:radius))
    }

    func update(_ state: Simulation) {
        dirtCoating.update(state)
        root.position = SCNVector3(state.x, state.groundY, state.z)
        root.eulerAngles = SCNVector3(state.bodyPitch, state.heading, state.bodyRoll)
        for wheel in wheels {
            let travel = wheel.side > 0 ? state.leftTravel : wheel.side < 0 ? state.rightTravel : (state.leftTravel+state.rightTravel)/2
            if wheel.side == 0 {
                let omega = (state.rightSpeed-state.leftSpeed)/0.56
                let lateralSpeed = omega*Double(wheel.node.position.z)
                wheel.node.eulerAngles.y = state.distance == 0 ? 0 : CGFloat(atan2(lateralSpeed, max(0.001,abs(state.speed))) * (state.speed < 0 ? -1 : 1))
            }
            wheel.node.eulerAngles.x = CGFloat((travel/wheel.radius).truncatingRemainder(dividingBy:2 * .pi))
        }
        head.rotation = SCNVector4(headAxis.x,headAxis.y,headAxis.z,0.14*sin(state.elapsed*0.7))
    }
    func applyExpression(_ pose: RacePerformance.Pose) {
        head.rotation = SCNVector4(headAxis.x,headAxis.y,headAxis.z,pose.yaw)
    }

}
