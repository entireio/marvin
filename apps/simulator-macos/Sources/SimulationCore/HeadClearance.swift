import Foundation
import simd

/// Conservative collision envelopes measured from the assembled CAD meshes.
/// Uses oriented-box / axis-aligned-box separating axes, in 100 mm scene units.
/// The neck joint is intentionally excluded; the head shells must clear the base.
public enum HeadClearance {
    private struct Box {
        let lo: SIMD3<Double>, hi: SIMD3<Double>
        var center: SIMD3<Double> { (lo+hi)/2 }
        var half: SIMD3<Double> { (hi-lo)/2 }
    }
    private static let heads = [
        Box(lo: [-0.3358, 0.4038, -0.3114], hi: [0.3362, 0.6313, 0.3332]),
        Box(lo: [-0.3485, 0.4315, -0.3949], hi: [0.3489, 0.7066, 0.3452]),
        Box(lo: [-0.3354, 0.4146, 0.2533], hi: [0.3354, 0.6948, 0.3451]),
        Box(lo: [-0.2958, 0.4843, -0.4017], hi: [0.2961, 0.6568, -0.3676]),
    ]
    private static let bodies = [
        Box(lo: [-0.2805, 0.0184, -0.3944], hi: [0.2805, 0.299, 0.3357]),
        Box(lo: [0.0813, 0.0201, -0.3954], hi: [0.3771, 0.300, 0.3368]),
        Box(lo: [-0.3771, 0.0201, -0.3954], hi: [-0.0813, 0.300, 0.3368]),
    ]
    public static func isClear(yaw: Double, pitch: Double) -> Bool {
        guard yaw.isFinite, pitch.isFinite else { return false }
        func rotate(_ v: SIMD3<Double>) -> SIMD3<Double> {
            HeadRig.rotate(v, yaw: yaw, pitch: pitch)
        }
        let world: [SIMD3<Double>] = [[1,0,0], [0,1,0], [0,0,1]]
        let axes = world.map(rotate)
        let candidates = world + axes + world.flatMap { a in axes.map { simd_cross(a, $0) } }
        for head in heads {
            let center = HeadRig.headPoint(head.center, yaw: yaw, pitch: pitch)
            // Half-millimeter clearance, including the physical button height.
            let headHalf = head.half + SIMD3<Double>(repeating: 0.005)
            for body in bodies {
                let delta = center-body.center
                var separated = false
                for candidate in candidates {
                    let length = simd_length(candidate)
                    if length < 1e-9 { continue }
                    let axis = candidate/length
                    let a = abs(axis.x)*body.half.x + abs(axis.y)*body.half.y + abs(axis.z)*body.half.z
                    let b = (0..<3).reduce(0.0) { $0 + abs(simd_dot(axis, axes[$1]))*headHalf[$1] }
                    if abs(simd_dot(delta, axis)) > a+b { separated = true; break }
                }
                if !separated { return false }
            }
        }
        return true
    }
}
