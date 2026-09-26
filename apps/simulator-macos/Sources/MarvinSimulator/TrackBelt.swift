import SceneKit
import SimulationCore
import simd

/// Moving shoes over a continuous rubber carcass, sized to the widened CAD belt.
final class TrackBelt {
    let node = SCNNode()
    let left: Bool
    let shoes: [SCNNode]
    init(x: Double, rubber: SCNMaterial) {
        left = x > 0 // Marvin faces +Z; anatomical left is +X.
        node.position.x = CGFloat(x)
        let width = 0.165324
        let count = 56
        let pitch = TrackLoop.circumference / Double(count)
        let shoe = SCNBox(width: width, height: 0.008, length: pitch*0.80, chamferRadius: 0.001)
        shoe.materials = [rubber]
        shoes = (0..<count).map { _ in SCNNode(geometry: shoe) }
        for (i, shoeNode) in shoes.enumerated() {
            // Offset center ribs break up the broad tread, making its motion legible.
            let rib = SCNBox(width: width*0.38, height: 0.003, length: pitch*0.42, chamferRadius: 0.0007)
            rib.materials = [material(0x2b3432, roughness: 0.95)]
            let ribNode = SCNNode(geometry: rib)
            ribNode.position = SCNVector3(i.isMultiple(of: 2) ? -width*0.23 : width*0.23, 0.004, 0)
            shoeNode.addChildNode(ribNode)
            node.addChildNode(shoeNode)
        }
        // A watertight ring beneath the shoes; its outer surface is inward of
        // the tread so the belt is solid without a stationary tread overlay.
        var vertices: [SCNVector3] = [], normals: [SCNVector3] = [], indices: [Int32] = []
        func ring(_ i: Int) -> [SIMD3<Float>] {
            let p = TrackLoop.sample(Double(i)/160 * TrackLoop.circumference)
            let outward = SIMD3<Float>(0, Float(cos(p.angle)), Float(sin(p.angle)))
            let center = SIMD3<Float>(0, Float(p.y), Float(p.z))
            return [center + SIMD3<Float>(-Float(width/2), 0, 0) - outward*0.004,
                    center + SIMD3<Float>(Float(width/2), 0, 0) - outward*0.004,
                    center + SIMD3<Float>(Float(width/2), 0, 0) - outward*0.013,
                    center + SIMD3<Float>(-Float(width/2), 0, 0) - outward*0.013]
        }
        for i in 0..<160 {
            let a = ring(i), b = ring(i+1)
            for j in 0..<4 {
                let k = (j+1)%4
                let points = [a[j], b[j], b[k], a[k]]
                let normal = simd_normalize(simd_cross(points[1]-points[0], points[2]-points[0]))
                let start = Int32(vertices.count)
                for point in points { vertices.append(SCNVector3(point)); normals.append(SCNVector3(normal)) }
                indices += [start, start+1, start+2, start, start+2, start+3]
            }
        }
        let mesh = SCNGeometry(sources: [SCNGeometrySource(vertices: vertices), SCNGeometrySource(normals: normals)],
                              elements: [SCNGeometryElement(indices: indices, primitiveType: .triangles)])
        mesh.materials = [rubber]
        node.addChildNode(SCNNode(geometry: mesh))
        update(travel: 0)
    }
    func update(travel: Double) {
        for (i, shoe) in shoes.enumerated() {
            let p = TrackLoop.sample(travel + Double(i)/Double(shoes.count)*TrackLoop.circumference)
            shoe.position = SCNVector3(0, p.y, p.z)
            shoe.eulerAngles.x = CGFloat(p.angle)
        }
    }
}
