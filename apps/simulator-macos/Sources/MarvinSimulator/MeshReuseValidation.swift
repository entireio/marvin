import SceneKit

/// Compare expanded indexed attributes, not just triangle/vertex counts. This
/// catches UV seams, material partitioning and normal/color wiring regressions.
func validateMeshReuse(_ original:SCNGeometry,_ optimized:SCNGeometry)->Bool {
    guard original.elements.count==optimized.elements.count,original.sources.count==optimized.sources.count,
          original.materials.count==optimized.materials.count else { return false }
    for (a,b) in zip(original.materials,optimized.materials) where a !== b { return false }
    func indices(_ e:SCNGeometryElement)->[Int] {
        e.data.withUnsafeBytes { bytes in
            (0..<(e.primitiveCount*3)).map { offset in
                switch e.bytesPerIndex {
                case 1:return Int(bytes.loadUnaligned(fromByteOffset:offset,as:UInt8.self))
                case 2:return Int(bytes.loadUnaligned(fromByteOffset:offset*2,as:UInt16.self))
                default:return Int(bytes.loadUnaligned(fromByteOffset:offset*4,as:UInt32.self))
                }
            }
        }
    }
    func values(_ s:SCNGeometrySource)->[Double] {
        s.data.withUnsafeBytes { bytes in
            var result:[Double]=[];result.reserveCapacity(s.vectorCount*s.componentsPerVector)
            for index in 0..<s.vectorCount { for axis in 0..<s.componentsPerVector {
                let offset=s.dataOffset+index*s.dataStride+axis*s.bytesPerComponent
                result.append(s.bytesPerComponent==4 ? Double(bytes.loadUnaligned(fromByteOffset:offset,as:Float.self)):bytes.loadUnaligned(fromByteOffset:offset,as:Double.self))
            }}
            return result
        }
    }
    for (a,b) in zip(original.sources,optimized.sources) {
        guard a.semantic==b.semantic,a.componentsPerVector==b.componentsPerVector,a.usesFloatComponents,b.usesFloatComponents else { return false }
    }
    let sources=zip(original.sources,optimized.sources).map { (a,b) in (a.componentsPerVector,values(a),values(b)) }
    for (a,b) in zip(original.elements,optimized.elements) {
        guard a.primitiveType == .triangles,b.primitiveType == .triangles,a.primitiveCount==b.primitiveCount else { return false }
        for (i,j) in zip(indices(a),indices(b)) {
            for (width,va,vb) in sources {
                guard (i+1)*width<=va.count,(j+1)*width<=vb.count else { return false }
                for axis in 0..<width where va[i*width+axis] != vb[j*width+axis] { return false }
            }
        }
    }
    return true
}
