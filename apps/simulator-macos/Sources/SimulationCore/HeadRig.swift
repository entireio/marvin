import Foundation
import simd

/// The neck's lower circular sections share X=0, Z=-1.886 mm in the CAD.
/// Yaw must use that center, while retaining the inferred pitch pivot.
public enum HeadRig {
    public static let yawPivot = SIMD3<Double>(0, 0.30, -0.01886)
    public static let pitchPivot = SIMD3<Double>(0, 0.56, 0)

    public static func rotate(_ v: SIMD3<Double>, yaw: Double, pitch: Double) -> SIMD3<Double> {
        let y = cos(pitch)*v.y + sin(pitch)*v.z
        let z = -sin(pitch)*v.y + cos(pitch)*v.z
        return [cos(yaw)*v.x + sin(yaw)*z, y, -sin(yaw)*v.x + cos(yaw)*z]
    }

    public static func headPoint(_ point: SIMD3<Double>, yaw: Double, pitch: Double) -> SIMD3<Double> {
        let tilted = rotate(point-pitchPivot, yaw: 0, pitch: pitch)+pitchPivot
        return rotate(tilted-yawPivot, yaw: yaw, pitch: 0)+yawPivot
    }
}
