import Foundation

/// Predictive corner braking and light, player-directed steering correction.
/// Manual braking always takes priority; steering never starts on its own.
public struct DirtDrivingAssists: Sendable {
    public var steering: Bool, braking: Bool
    public init(steering: Bool = true, braking: Bool = true) {
        self.steering = steering; self.braking = braking
    }
    public static let off = Self(steering:false, braking:false)

    public func apply(_ input: DriveInput, to state: Simulation) -> DriveInput {
        guard steering || braking, state.dirtTrack, !state.airborne, !state.paused,
              state.forwardSpeed > 0.5, input.throttle >= 0 else { return input }
        var result = input
        if braking && input.brake { result.assistedBraking = true }
        let projection = DirtCourse.projection(x:state.x,z:state.z)
        let tangent = DirtCourse.heading(projection.phase)
        guard projection.distance < DirtCourse.width,
              cos(tangent-state.heading) > 0.5 else { return result }
        if braking && !input.brake && input.throttle>0 {
            let speed=state.groundSpeed
            let grip=DirtCourse.traction(x:state.x,z:state.z)*max(0.55,1-state.storm.depth(x:state.x,z:state.z)*2.5)
            let target=DirtRacingLine.safeSpeed(phase:projection.phase,speed:speed,grip:grip)
            result.throttle=min(input.throttle,target/(input.boost ? 12:6))
            result.assistedBrakePressure=max(0,min(1,(speed-target)*0.65))
            result.assistedBraking=result.assistedBrakePressure>0
        }
        guard steering, abs(input.turn) > 0.01 else { return result }
        let targetTurn = DirtRacingLine.steering(x:state.x,z:state.z,heading:state.heading,
                                          speed:state.groundSpeed,phase:projection.phase)
        let manual = max(-1,min(1,input.turn)), direction = manual > 0 ? 1.0 : -1.0
        let aligned = max(0,targetTurn*direction)
        let helped = manual*0.80 + direction*aligned*0.20*min(1,abs(manual)*3)
        // At a curve exit the ideal turn crosses zero. Fade back to manual
        // control as the driver steers away from it; never jump from softened to
        // 100% steering at the sign change, and never reverse the chosen turn.
        let opposition = max(0,min(1,-targetTurn*direction/0.4))
        let override = opposition*opposition*(3-2*opposition)
        result.turn = helped+(manual-helped)*override
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
    /// Braking envelope integrates distance to future bends. Limits account for
    /// both available lateral grip and the chassis' maximum yaw rate.
    public static func safeSpeed(phase:Double,speed:Double,grip:Double)->Double {
        let start=Int(((phase/(2 * .pi)).truncatingRemainder(dividingBy:1)+1).truncatingRemainder(dividingBy:1)*Double(count))%count
        var distance=0.0,limit=12.0,index=start
        let deceleration=max(3,8*grip),preview=max(8,min(24,speed*speed/(2*deceleration)+5))
        repeat {
            let before=points[(index+count-3)%count],at=points[index],after=points[(index+3)%count]
            let a=at-before,b=after-at
            let bend=abs(atan2(a.x*b.y-a.y*b.x,a.x*b.x+a.y*b.y))
            let curvature=bend/max(0.1,(length(a)+length(b))*0.5)
            let corner=min(12,min(0.95/max(0.02,curvature),sqrt(9*grip/max(0.02,curvature))))
            limit=min(limit,sqrt(corner*corner+2*deceleration*max(0,distance-speed*0.18)))
            let next=(index+1)%count;distance += length(points[next]-points[index]);index=next
        } while distance<preview && index != start
        return max(1.8,limit)
    }
    private static func length(_ v: SIMD2<Double>) -> Double { hypot(v.x,v.y) }
    public static func point(_ phase: Double) -> (x: Double,z: Double) {
        let t = ((phase/(2 * .pi)).truncatingRemainder(dividingBy:1)+1).truncatingRemainder(dividingBy:1)*Double(count)
        let i = Int(t)%count, f = t-Double(Int(t))
        let p = points[i]*(1-f)+points[(i+1)%count]*f
        return (p.x,p.y)
    }
    public static func steering(x:Double,z:Double,heading:Double,speed:Double,phase:Double) -> Double {
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
        return max(-1,min(1,-omega/1.15))
    }
}
