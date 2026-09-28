import Foundation

/// Player-triggered help only: no steering without steering input and no
/// braking without the brake button. The two settings are independent.
public struct DirtDrivingAssists: Sendable {
    public var steering: Bool, braking: Bool
    public init(steering: Bool = true, braking: Bool = true) {
        self.steering = steering; self.braking = braking
    }
    public static let off = Self(steering:false, braking:false)

    public func apply(_ input: DriveInput, to state: Simulation) -> DriveInput {
        guard steering || braking, state.dirtTrack, !state.airborne, !state.paused,
              state.speed > 0.5, input.throttle >= 0 else { return input }
        let projection = DirtCourse.projection(x:state.x,z:state.z)
        let tangent = DirtCourse.heading(projection.phase)
        guard projection.distance < DirtCourse.width,
              cos(tangent-state.heading) > 0.5 else { return input }
        let guide = DirtRacingLine.guidance(x:state.x,z:state.z,heading:state.heading,
                                          speed:state.speed,phase:projection.phase)
        var result = input
        // Preserve deliberate opposite steering (for passing or avoiding an
        // obstacle). Even with help, holding a key still contributes 25%.
        if steering && abs(input.turn) > 0.01 && input.turn*guide.turn > 0 {
            let manual = max(-1,min(1,input.turn))
            let weight = min(1,abs(manual)*3)
            result.turn = manual*0.25 + guide.turn*0.75*weight
        }
        if braking && input.brake {
            // Full pressure above corner speed; ease pressure near it. Holding
            // the brake continues to slow down, so releasing it still matters.
            result.assistedBrakePressure = max(0.18,min(1,0.18+(state.speed-guide.speed)*0.5))
        }
        return result
    }
}

/// A bounded, smoothed reference line through the compacted lane. This is a
/// forgiving arcade racing line, not an optimal vehicle-dynamics lap solution.
public enum DirtRacingLine {
    private static let count = DirtCourse.sampleCount
    private static let points: [SIMD2<Double>] = {
        let centers = (0..<count).map { i -> SIMD2<Double> in
            let p = DirtCourse.point(Double(i)*2 * .pi/Double(count))
            return SIMD2(p.x,p.z)
        }
        let normals = (0..<count).map { i -> SIMD2<Double> in
            let h = DirtCourse.heading(Double(i)*2 * .pi/Double(count))
            return SIMD2(cos(h),-sin(h))
        }
        var line = centers
        // Shorten/smooth bends while retaining generous room for every racer.
        for _ in 0..<240 {
            let old = line
            for i in 0..<count {
                let midpoint = (old[(i+count-1)%count]+old[(i+1)%count])*0.5
                let delta = midpoint-centers[i], normal = normals[i]
                let offset = max(-0.8,min(0.8,delta.x*normal.x+delta.y*normal.y))
                line[i] = centers[i]+normal*offset
            }
        }
        return line
    }()
    private static func length(_ v: SIMD2<Double>) -> Double { hypot(v.x,v.y) }
    private static let speeds: [Double] = {
        var speeds = (0..<count).map { i -> Double in
            let a = points[(i+count-4)%count], b = points[i], c = points[(i+4)%count]
            let u = b-a, v = c-b
            let curvature = abs(2*(u.x*v.y-u.y*v.x))/max(1e-8,length(u)*length(v)*length(c-a))
            // Leave yaw and grip in reserve for a human's correction.
            return max(1.5,min(10,min(0.85/max(curvature,0.001),sqrt(7/max(curvature,0.001)))))
        }
        // Back-propagate braking distance through the lap, including its seam.
        for _ in 0..<2 {
            for i in (0..<count).reversed() {
                let next = (i+1)%count, distance = length(points[next]-points[i])
                speeds[i] = min(speeds[i],sqrt(speeds[next]*speeds[next]+2*8*distance))
            }
        }
        return speeds
    }()
    public static func point(_ phase: Double) -> (x: Double,z: Double) {
        let t = ((phase/(2 * .pi)).truncatingRemainder(dividingBy:1)+1).truncatingRemainder(dividingBy:1)*Double(count)
        let i = Int(t)%count, f = t-Double(Int(t))
        let p = points[i]*(1-f)+points[(i+1)%count]*f
        return (p.x,p.y)
    }
    public static func guidance(x:Double,z:Double,heading:Double,speed:Double,phase:Double) -> (turn:Double,speed:Double) {
        let t = ((phase/(2 * .pi)).truncatingRemainder(dividingBy:1)+1).truncatingRemainder(dividingBy:1)*Double(count)
        let i = Int(t)%count
        var target = i, distance = 0.0
        let lookAhead = max(1.0,min(3.5,abs(speed)*0.35))
        while distance < lookAhead {
            let next = (target+1)%count
            distance += length(points[next]-points[target]); target = next
            if target == i { break }
        }
        let delta = points[target]-SIMD2(x,z)
        let error = atan2(sin(atan2(delta.x,delta.y)-heading),cos(atan2(delta.x,delta.y)-heading))
        let omega = 2*max(0,speed)*sin(error)/max(0.5,length(delta))
        let f = t-Double(Int(t)), targetSpeed = speeds[i]*(1-f)+speeds[(i+1)%count]*f
        return (max(-1,min(1,-omega/1.15)),targetSpeed)
    }
}
