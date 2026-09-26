import AppKit
import SceneKit
import SimulationCore

func color(_ hex: UInt32, alpha: CGFloat = 1) -> NSColor {
    NSColor(srgbRed: CGFloat((hex >> 16) & 255)/255,
            green: CGFloat((hex >> 8) & 255)/255,
            blue: CGFloat(hex & 255)/255, alpha: alpha)
}
func material(_ hex: UInt32, metal: CGFloat = 0, roughness: CGFloat = 0.6) -> SCNMaterial {
    let m = SCNMaterial()
    m.lightingModel = .physicallyBased
    m.diffuse.contents = color(hex); m.metalness.contents = metal
    m.roughness.contents = roughness; m.isDoubleSided = true
    return m
}

struct MeshManifest: Decodable {
    struct Part: Decodable {
        let name: String
        let vertexOffset: Int, vertexCount: Int, indexOffset: Int, triangleCount: Int
    }
    let parts: [Part]
}

final class Robot {
    let root = SCNNode(), yawNode = SCNNode(), pitchNode = SCNNode()
    var wheels: [(node: SCNNode, left: Bool)] = []
    var tracks: [TrackBelt] = []
    var eyes: [SCNNode] = []
    var partCount = 0, triangleCount = 0

    init(resources: URL) throws {
        let directory = resources.appendingPathComponent("Marvin")
        let manifest = try JSONDecoder().decode(MeshManifest.self,
            from: Data(contentsOf: directory.appendingPathComponent("manifest.json")))
        let data = try Data(contentsOf: directory.appendingPathComponent("geometry.bin"))
        root.name = "Marvin CAD assembly"
        // Keep the circular neck concentric with the body's socket during pan.
        yawNode.position = SCNVector3(HeadRig.yawPivot)
        pitchNode.position = SCNVector3(HeadRig.pitchPivot-HeadRig.yawPivot)
        root.addChildNode(yawNode); yawNode.addChildNode(pitchNode)
        let shell = material(0xe5e5e2, metal: 0.05, roughness: 0.4)
        let rubber = material(0x202a29, roughness: 0.88)
        let graphite = material(0x37403f, metal: 0.25, roughness: 0.42)
        let steel = material(0x919b9d, metal: 0.7, roughness: 0.28)
        let electronics = material(0x163e36, roughness: 0.6)
        let faceMaterial = material(0x25282b, roughness: 0.38)
        let redButton = material(0xc83a24, roughness: 0.45)
        for part in manifest.parts {
            guard part.vertexCount > 0, part.triangleCount > 0,
                  part.vertexOffset >= 0, part.indexOffset >= 0,
                  part.vertexOffset + part.vertexCount*24 <= data.count,
                  part.indexOffset + part.triangleCount*12 <= data.count else {
                throw NSError(domain: "MarvinModel", code: 1,
                    userInfo: [NSLocalizedDescriptionKey: "Invalid geometry for \(part.name)"])
            }
            let positions = SCNGeometrySource(data: data, semantic: .vertex,
                vectorCount: part.vertexCount, usesFloatComponents: true,
                componentsPerVector: 3, bytesPerComponent: 4,
                dataOffset: part.vertexOffset, dataStride: 24)
            let normals = SCNGeometrySource(data: data, semantic: .normal,
                vectorCount: part.vertexCount, usesFloatComponents: true,
                componentsPerVector: 3, bytesPerComponent: 4,
                dataOffset: part.vertexOffset+12, dataStride: 24)
            let indexData = data.subdata(in: part.indexOffset..<(part.indexOffset+part.triangleCount*12))
            let elements = SCNGeometryElement(data: indexData, primitiveType: .triangles,
                primitiveCount: part.triangleCount, bytesPerIndex: 4)
            let geometry = SCNGeometry(sources: [positions, normals], elements: [elements])
            if part.name == "09_track" { geometry.materials = [rubber] }
            else if ["04_wheel", "Top", "Servo_Head", "Servo_Tilt"].contains(part.name) {
                geometry.materials = [graphite]
            } else if ["Bearings", "Motor_Left", "Motor_Right", "Axis_Mount"].contains(part.name) {
                geometry.materials = [steel]
            } else if part.name == "Display_and_electronics" {
                // The CAD already includes the full, flush front panel.
                geometry.materials = [faceMaterial]
            } else if part.name == "Battery" {
                geometry.materials = [electronics]
            } else { geometry.materials = [shell] }
            if part.name == "Buttons" {
                // STEP merges the six controls into one mesh. Partition triangles
                // by their CAD lateral positions without replacing their geometry.
                var groups = [Data(), Data(), Data()]
                for triangle in 0..<part.triangleCount {
                    let offset = triangle * 12
                    let indices = (0..<3).map { i in
                        indexData.withUnsafeBytes { $0.loadUnaligned(fromByteOffset: offset+i*4, as: UInt32.self) }
                    }
                    let x = indices.reduce(Float(0)) { sum, index in
                        sum + data.withUnsafeBytes {
                            $0.loadUnaligned(fromByteOffset: part.vertexOffset+Int(index)*24, as: Float.self)
                        }
                    } / 3
                    let group = x < -0.061 ? 0 : x > 0.05 ? 2 : 1
                    groups[group].append(indexData.subdata(in: offset..<(offset+12)))
                }
                let buttonElements = groups.map { bytes in
                    let element = SCNGeometryElement(data: bytes, primitiveType: .triangles,
                        primitiveCount: bytes.count/12, bytesPerIndex: 4)
                    return element
                }
                let buttons = SCNGeometry(sources: [positions, normals], elements: buttonElements)
                buttons.materials = [redButton, shell, shell]
                let node = SCNNode(geometry: buttons)
                node.name = part.name; root.addChildNode(node)
                partCount += 1; triangleCount += part.triangleCount
                continue
            }
            let node = SCNNode(geometry: geometry)
            node.name = part.name; node.castsShadow = true
            // The source layer "Head" contains only an isolated 3 mm solid
            // below the shell, not the head assembly. Retain the imported data
            // but exclude this loose object from the visible simulator model.
            // The static CAD belt is retained but replaced visually by TrackBelt.
            node.isHidden = ["Head", "09_track"].contains(part.name)
            if ["05_head_base", "06_head_cover", "Display_and_electronics", "Head", "Top", "Battery"].contains(part.name) {
                node.position = SCNVector3(-HeadRig.pitchPivot); pitchNode.addChildNode(node)
            } else if ["07_neck", "08_neck_mount", "Servo_Tilt", "Axis_Mount"].contains(part.name) {
                node.position = SCNVector3(-HeadRig.yawPivot); yawNode.addChildNode(node)
            } else { root.addChildNode(node) }
            partCount += 1; triangleCount += part.triangleCount
        }
        // Only the luminous strokes sit over the CAD panel; no second bezel
        // or raised display box. Coordinates are in the assembled head frame.
        let eyePath = NSBezierPath()
        for i in 0...40 {
            let angle = Double.pi * (1-Double(i)/40)
            let point = NSPoint(x: cos(angle)*0.061, y: sin(angle)*0.046)
            if i == 0 { eyePath.move(to: point) } else { eyePath.line(to: point) }
        }
        for i in 0...40 {
            let angle = Double.pi * Double(i)/40
            eyePath.line(to: NSPoint(x: cos(angle)*0.048, y: sin(angle)*0.033))
        }
        eyePath.close()
        for x in [-0.0545, 0.0545] {
            eyePath.appendOval(in: NSRect(x: x-0.0065, y: -0.0065, width: 0.013, height: 0.013))
        }
        for x in [-0.145, 0.145] {
            let eye = SCNShape(path: eyePath, extrusionDepth: 0)
            let glow = SCNMaterial()
            glow.lightingModel = .constant; glow.diffuse.contents = NSColor.white
            glow.emission.contents = NSColor.white; glow.isDoubleSided = true
            eye.materials = [glow]
            let node = SCNNode(geometry: eye)
            node.position = SCNVector3(x, 0.008, 0.348)
            node.castsShadow = false
            pitchNode.addChildNode(node); eyes.append(node)
        }
        for x in [-0.262225, 0.262225] {
            let belt = TrackBelt(x: x, rubber: rubber)
            root.addChildNode(belt.node); tracks.append(belt)
        }
        // Hub markers rotate with the same signed travel as their belt.
        for x in [-0.337, 0.337] {
            for z in [-0.247, 0.164] {
                let hub = SCNNode()
                hub.position = SCNVector3(x, 0.112, z)
                let spoke = SCNBox(width: 0.008, height: 0.10, length: 0.022, chamferRadius: 0.004)
                spoke.materials = [material(0xb4c8c0, metal: 0.5)]
                hub.addChildNode(SCNNode(geometry: spoke))
                root.addChildNode(hub); wheels.append((hub, x > 0))
            }
        }
    }
    func update(_ state: Simulation) {
        root.position = SCNVector3(state.x, 0, state.z)
        root.eulerAngles.y = CGFloat(state.heading)
        yawNode.eulerAngles.y = CGFloat(state.yaw)
        pitchNode.eulerAngles.x = CGFloat(-state.pitch)
        for track in tracks { track.update(travel: track.left ? state.leftTravel : state.rightTravel) }
        for wheel in wheels {
            wheel.node.eulerAngles.x = CGFloat((wheel.left ? state.leftTravel : state.rightTravel) / 0.107)
        }
        let blink = state.elapsed.truncatingRemainder(dividingBy: 4.6) > 4.43
        for eye in eyes { eye.scale.y = blink ? 0.12 : 1 }
    }
}
