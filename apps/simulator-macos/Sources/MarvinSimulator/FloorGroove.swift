import SceneKit
import SimulationCore
import simd

/// V-shaped channel: both lips meet y=0 and the center lies below the floor.
enum FloorGroove {
    static let depth = 0.012
    static var innerRadius: Double { CourseLayout.ringRadius-CourseLayout.pipeRadius }
    static var outerRadius: Double { CourseLayout.ringRadius+CourseLayout.pipeRadius }

    static func geometry() -> SCNGeometry {
        let profile = [(innerRadius, 0.0), (CourseLayout.ringRadius, -depth), (outerRadius, 0.0)]
        var vertices: [SCNVector3] = [], normals: [SCNVector3] = [], indices: [Int32] = []
        for band in 0..<2 {
            let (r0, y0) = profile[band], (r1, y1) = profile[band+1]
            for i in 0...128 {
                let angle = Double(i)*2*Double.pi/128
                let normal = simd_normalize(SIMD3<Double>(-(y1-y0)*cos(angle), r1-r0, -(y1-y0)*sin(angle)))
                for (radius, y) in [profile[band], profile[band+1]] {
                    vertices.append(SCNVector3(radius*cos(angle), y, radius*sin(angle)))
                    normals.append(SCNVector3(normal))
                }
                if i < 128 {
                    let a = Int32(band*258+i*2)
                    indices += [a, a+2, a+1, a+1, a+2, a+3]
                }
            }
        }
        return SCNGeometry(sources: [SCNGeometrySource(vertices: vertices), SCNGeometrySource(normals: normals)],
                           elements: [SCNGeometryElement(indices: indices, primitiveType: .triangles)])
    }
}
