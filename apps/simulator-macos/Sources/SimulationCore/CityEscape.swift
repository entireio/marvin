import Foundation
import simd

/// Authored at the outside of the first right-hand bend. One coordinate frame
/// owns the wall opening, hinge, ramp, clearance reservation and test route.
public enum CityExit {
    public static let phase=0.125 * 2 * Double.pi
    public static let width=3.2, run=5.5
    public static let center:SIMD2<Double> = {
        let p=DirtCourse.point(phase,offset:DirtCourse.fenceOffset+0.08)
        return SIMD2(p.x,p.z)
    }()
    public static let tangent=SIMD2(sin(DirtCourse.heading(phase)),cos(DirtCourse.heading(phase)))
    public static let outward=SIMD2(tangent.y,-tangent.x)
    public static let hinge=center-tangent*width/2
    public static let floor:Double = [-width/2,0,width/2].map { along in
        let p=point(along,0)
        return DirtCourse.elevation(DirtCourse.phase(x:p.x,z:p.y),offset:DirtCourse.width)
    }.max()!
    public static let gateHeight=0.95
    /// Reinforced entry walls meet the gate head, then descend in level brick
    /// courses toward the existing retaining wall over the next few metres.
    public static func wallTop(_ p:SIMD2<Double>)->Double {
        let base=DirtCourse.elevation(DirtCourse.phase(x:p.x,z:p.y),offset:DirtCourse.width)+DirtCourse.postHeight
        let q=local(p)
        guard abs(q.y)<3.5 else { return base }
        let t=max(0,min(1,(abs(q.x)-2.8)/3.2)),blend=1-t*t*(3-2*t)
        return base+max(0,floor+0.035+gateHeight-base)*blend
    }
    public static let posts:[RobotCollisions.Body] = [-1.0,1.0].map { side in
        let p=point(side*(width/2+0.09),0)
        return .init(position:SIMD3(p.x,-0.025,p.y),profile:.init(mass:100,halfWidth:0.085,halfDepth:0.085,height:floor+gateHeight+0.16,round:true))
    }
    public static func point(_ along:Double,_ out:Double)->SIMD2<Double> { center+tangent*along+outward*out }
    public static func local(_ p:SIMD2<Double>)->SIMD2<Double> { let d=p-center;return SIMD2(simd_dot(d,tangent),simd_dot(d,outward)) }
    public static func opening(_ p:SIMD2<Double>,clearance:Double=0)->Bool {
        let q=local(p)
        return abs(q.x)<width/2-clearance && q.y > -1.1 && q.y<run+1
    }
    public static func reserved(_ p:SIMD2<Double>,radius:Double)->Bool {
        let q=local(p)
        return abs(q.x)<3.5+radius && q.y > -0.5-radius && q.y<10+radius
    }
    public static func rampHeight(_ p:SIMD2<Double>)->Double? {
        let q=local(p)
        guard abs(q.x)<=4.001,q.y > -2.5,q.y<run else { return nil }
        // A level sill supports the straight gate; the original bank varied
        // along its length, leaving a triangular opening beneath the leaf.
        let t=max(0,min(1,(q.y-0.2)/(run-0.2)))
        let s=max(0,min(1,(abs(q.x)-(width/2+0.1))/(4-width/2-0.1)))
        let across=1-s*s*(3-2*s)
        let approach=max(0,min(1,(q.y+2.5)/2.3))
        let projection=DirtCourse.projection(x:p.x,z:p.y)
        let bank=DirtCourse.elevation(projection.phase,offset:max(-DirtCourse.width,min(DirtCourse.width,projection.offset)))
        // Side shoulders join the existing raised bank, not the town floor.
        // Multiplying the whole bank by `across` cut two deep slots at ±4m.
        let base=DirtCourse.courseHeight(projection.phase,offset:projection.offset)
        let centerHeight=q.y<0 ? bank+max(0,floor-bank)*approach*approach*(3-2*approach)
            : (floor+0.025)*(1-t*t*(3-2*t))-0.025
        return base+(centerHeight-base)*across
    }
}

/// Motor-driven hinge with acceleration, damping, end stops and obstacle sensing.
/// Blocked motion stalls; the door never teleports or sweeps a robot through a wall.
public struct CityGate:Sendable {
    public private(set) var angle=0.0, velocity=0.0
    public var wantsOpen=false
    public private(set) var blocked=false
    public init() {}
    public var body:RobotCollisions.Body { body(at:angle) }
    public func body(at angle:Double)->RobotCollisions.Body {
        let along=CityExit.tangent*cos(angle)+CityExit.outward*sin(angle)
        let center=CityExit.hinge+along*CityExit.width/2
        return .init(position:SIMD3(center.x,CityExit.floor+0.035,center.y),heading:atan2(-along.y,along.x),profile:.init(mass:95,halfWidth:CityExit.width/2,halfDepth:0.055,height:CityExit.gateHeight))
    }
    public mutating func advance(dt:Double,bodies:[RobotCollisions.Body]) {
        guard dt>0,dt.isFinite else { return }
        let duration=min(dt,0.1),count=max(1,Int(ceil(duration*240)))
        for _ in 0..<count { step(dt:duration/Double(count),bodies:bodies) }
    }
    private mutating func step(dt:Double,bodies:[RobotCollisions.Body]) {
        let target=wantsOpen ? 100 * Double.pi/180:0
        let error=target-angle
        let desired=max(-0.45,min(0.45,error*2.5))
        velocity += max(-0.7*dt,min(0.7*dt,desired-velocity))
        let next=max(0,min(100 * Double.pi/180,angle+velocity*dt))
        let candidate=body(at:next)
        blocked=bodies.contains { body in
            var predicted=body;predicted.position += body.velocity*dt
            return RobotCollisions.contact(body,candidate) != nil || RobotCollisions.contact(predicted,candidate) != nil
        }
        if blocked { velocity=0;return }
        angle=next
        if abs(error)<0.0001 { angle=target;velocity=0 }
    }
}

/// Immutable spatial buckets keep town contacts local even with thousands of
/// authored solids. Render builders supply the same transformed primitives.
public struct CityCollisionWorld:Sendable {
    public let bodies:[RobotCollisions.Body]
    private var buckets:[SIMD2<Int>:[Int]]=[:]
    private var dynamicBodies:[RobotCollisions.Body]=[]
    public func withDynamicBodies(_ bodies:[RobotCollisions.Body])->Self {
        var copy=self;copy.dynamicBodies=bodies;return copy
    }
    public init(_ bodies:[RobotCollisions.Body]) {
        self.bodies=bodies
        for (i,b) in bodies.enumerated() {
            let r=hypot(b.profile.halfWidth,b.profile.halfDepth)
            for x in Int(floor((b.position.x-r)/8))...Int(floor((b.position.x+r)/8)) {
                for z in Int(floor((b.position.z-r)/8))...Int(floor((b.position.z+r)/8)) { buckets[SIMD2(x,z),default:[]].append(i) }
            }
        }
    }
    public func nearby(_ body:RobotCollisions.Body)->[RobotCollisions.Body] {
        let r=hypot(body.profile.halfWidth,body.profile.halfDepth)+0.1
        var ids=Set<Int>()
        for x in Int(floor((body.position.x-r)/8))...Int(floor((body.position.x+r)/8)) {
            for z in Int(floor((body.position.z-r)/8))...Int(floor((body.position.z+r)/8)) { for i in buckets[SIMD2(x,z)] ?? [] { ids.insert(i) } }
        }
        let fixed=ids.sorted().compactMap { i -> RobotCollisions.Body? in
            let b=bodies[i],reach=r+hypot(b.profile.halfWidth,b.profile.halfDepth)
            return hypot(body.position.x-b.position.x,body.position.z-b.position.z)<reach ? b:nil
        }
        return fixed+dynamicBodies.filter { b in
            simd_distance(body.center,b.center)<r+hypot(b.profile.halfWidth,b.profile.halfDepth)
        }
    }
}
