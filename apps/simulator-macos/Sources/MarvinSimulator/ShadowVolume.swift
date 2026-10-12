import SceneKit
import simd

/// Conservative side-plane test. Deliberately omit near/far rejection: keeping
/// an extra caster is harmless, while clipping a shadow volume is not.
struct ShadowBounds {
    let low:SIMD3<Double>,high:SIMD3<Double>
    let corners:[SIMD3<Double>]
    init(low:SIMD3<Double>,high:SIMD3<Double>) {
        self.low=low;self.high=high
        var result:[SIMD3<Double>]=[]
        for x in [low.x,high.x] { for y in [low.y,high.y] { for z in [low.z,high.z] { result.append(SIMD3(x,y,z)) } } }
        self.corners=result
    }
}
struct ShadowFrustum {
    let inverse:simd_float4x4
    let tanX:Float,tanY:Float
    static func cameras(_ node:SCNNode?,aspect:Double)->[ShadowFrustum] {
        guard let node,let camera=node.camera,!camera.usesOrthographicProjection,
              aspect.isFinite,aspect>0,camera.fieldOfView>0,camera.fieldOfView<175 else { return [] }
        // A small extra angular guard only retains additional shadow casters.
        let tangent=Float(tan((Double(camera.fieldOfView)+1)*Double.pi/360))
        let x=camera.projectionDirection == .horizontal ? tangent:tangent*Float(aspect)
        let y=camera.projectionDirection == .horizontal ? tangent/Float(aspect):tangent
        return [node.simdWorldTransform].compactMap { matrix in
            guard (0..<4).allSatisfy({ i in (0..<4).allSatisfy({ matrix[i][$0].isFinite }) }) else { return nil }
            return ShadowFrustum(inverse:simd_inverse(matrix),tanX:x,tanY:y)
        }
    }
    func intersects(_ bounds:ShadowBounds)->Bool {
        var right=true,left=true,top=true,bottom=true,behind=true
        for point in bounds.corners {
            let p=inverse*SIMD4(Float(point.x),Float(point.y),Float(point.z),1)
            guard (0..<4).allSatisfy({p[$0].isFinite}) else { return true }
            right = right && p.x > -p.z*tanX;left = left && p.x < p.z*tanX
            top = top && p.y > -p.z*tanY;bottom = bottom && p.y < p.z*tanY
            behind = behind && p.z>0
        }
        return !(right || left || top || bottom || behind)
    }
}
