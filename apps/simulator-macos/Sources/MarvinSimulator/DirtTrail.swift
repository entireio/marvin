import SceneKit
import SimulationCore

/// Batched ground decals: enough history for a three-lap race, with bounded memory.
final class DirtTrail {
    enum Style { case tracks, tires }
    let root = SCNNode()
    private let style: Style
    private var uv: [CGPoint] = []
    private let ink = material(0x382719, roughness: 1)
    private var chunks: [SCNNode] = []
    private var vertices: [SCNVector3] = []
    private var indices: [Int32] = []
    private var chunkIndex = 0
    private var previous: (x: Double, z: Double, heading: Double)?
    private var remainder = 0.0
    private(set) var count = 0

    init(style: Style = .tracks) {
        self.style = style
        ink.transparency = style == .tracks ? 0.65 : 0.48
        // The referenced Performa wheels have smooth, flat rubber tread.
        ink.isDoubleSided = true
        ink.writesToDepthBuffer = false
    }
    func reset() {
        chunks.forEach { $0.removeFromParentNode() }; chunks.removeAll()
        vertices.removeAll(); uv.removeAll(); indices.removeAll(); chunkIndex = 0
        previous = nil; remainder = 0; count = 0
    }
    func update(_ state: Simulation, contacts: [(x: Double, z: Double, width: Double)]) {
        guard !state.airborne else { previous = nil; remainder = 0; return }
        defer { previous = (state.x, state.z, state.heading) }
        guard let previous else { return }
        let dx = state.x-previous.x, dz = state.z-previous.z
        let distance = hypot(dx,dz)
        guard distance > 1e-8 else { return }
        // Do not draw connecting lines after a teleport/reset.
        guard distance < 2 else { remainder = 0; return }
        let angle = atan2(sin(state.heading-previous.heading),cos(state.heading-previous.heading))
        let spacing = 0.065
        var travel = spacing-remainder
        while travel <= distance {
            let t = travel/distance, heading = previous.heading+angle*t
            let x = previous.x+dx*t, z = previous.z+dz*t
            for contact in contacts {
                if vertices.count == 256*4 {
                    flush(); chunkIndex = (chunkIndex+1)%128
                    vertices.removeAll(keepingCapacity:true); uv.removeAll(keepingCapacity:true); indices.removeAll(keepingCapacity:true)
                }
                let base = Int32(vertices.count)
                for (side, along) in [(-1.0,-1.0),(1,-1),(1,1),(-1,1)] {
                    let lateral = contact.x+side*contact.width/2
                    let forward = contact.z+along*(style == .tracks ? 0.021 : spacing*0.53)
                    let px = x+cos(heading)*lateral+sin(heading)*forward
                    let pz = z-sin(heading)*lateral+cos(heading)*forward
                    uv.append(CGPoint(x:(side+1)/2,y:(along+1)/2))
                    vertices.append(SCNVector3(px,DirtCourse.height(x:px,z:pz)+0.007,pz))
                }
                indices += [base,base+2,base+1,base,base+3,base+2]
                count += 1
            }
            travel += spacing
        }
        remainder = (remainder+distance).truncatingRemainder(dividingBy:spacing)
        flush()
    }
    private func flush() {
        guard !vertices.isEmpty else { return }
        if chunkIndex == chunks.count {
            let node = SCNNode(); node.castsShadow = false
            root.addChildNode(node); chunks.append(node)
        }
        let geometry = SCNGeometry(sources:[SCNGeometrySource(vertices:vertices),SCNGeometrySource(textureCoordinates:uv),
            SCNGeometrySource(normals:Array(repeating:SCNVector3(0,1,0),count:vertices.count))],
            elements:[SCNGeometryElement(indices:indices,primitiveType:.triangles)])
        geometry.materials = [ink]; chunks[chunkIndex].geometry = geometry
    }
}
