import Foundation
import simd

/// Upright rigid bodies: three-axis translation and yaw inertia. Terrain owns
/// pitch/roll; this is deliberately not a six-axis tipping/suspension simulation.
public enum RobotCollisions {
    public struct Profile: Sendable {
        public let mass, halfWidth, halfDepth, height: Double
        public let round: Bool
        public init(mass: Double, halfWidth: Double, halfDepth: Double, height: Double, round: Bool = false) {
            precondition(mass > 0 && halfWidth > 0 && halfDepth > 0 && height > 0)
            self.mass = mass; self.halfWidth = halfWidth; self.halfDepth = halfDepth; self.height = height; self.round = round
        }
        var inertia: Double { round ? mass*halfWidth*halfWidth/2 : mass*(halfWidth*halfWidth+halfDepth*halfDepth)/3 }
    }
    // Footprint bounds measured from the scaled meshes. Masses are explicit game
    // tuning in kg, not claims about movie props or Marvin's physical hardware.
    public static let profiles = [
        Profile(mass:18,halfWidth:0.262,halfDepth:0.28,height:0.492),
        Profile(mass:55,halfWidth:0.288,halfDepth:0.283,height:0.885),
        Profile(mass:12,halfWidth:0.174,halfDepth:0.174,height:0.50,round:true),
        Profile(mass:85,halfWidth:0.398,halfDepth:0.246,height:0.833)
    ]
    public struct Body: Sendable {
        public var position, velocity: SIMD3<Double>
        public var heading, angularVelocity: Double
        public let profile: Profile
        public var contacted = false
        public init(position: SIMD3<Double>, velocity: SIMD3<Double> = .zero, heading: Double = 0, angularVelocity: Double = 0, profile: Profile) {
            self.position = position; self.velocity = velocity; self.heading = heading; self.angularVelocity = angularVelocity; self.profile = profile
        }
        var center: SIMD2<Double> { SIMD2(position.x,position.z) }
        var lateral: SIMD2<Double> { SIMD2(cos(heading),-sin(heading)) }
        var forward: SIMD2<Double> { SIMD2(sin(heading),cos(heading)) }
        func extent(_ axis: SIMD2<Double>) -> Double {
            profile.round ? profile.halfWidth : abs(simd_dot(lateral,axis))*profile.halfWidth+abs(simd_dot(forward,axis))*profile.halfDepth
        }
        func contactVelocity(_ at: SIMD3<Double>) -> SIMD3<Double> {
            let r = at-position
            return velocity+SIMD3(angularVelocity*r.z,0,-angularVelocity*r.x)
        }
        mutating func impulse(_ impulse: SIMD3<Double>, at: SIMD3<Double>) {
            velocity += impulse/profile.mass
            let r = at-position
            angularVelocity += (r.z*impulse.x-r.x*impulse.z)/profile.inertia
        }
    }
    public struct Contact: Sendable {
        public let normal, point: SIMD3<Double>
        public let penetration: Double
    }
    public static func contact(_ a: Body, _ b: Body) -> Contact? {
        let low = max(a.position.y,b.position.y)
        let high = min(a.position.y+a.profile.height,b.position.y+b.profile.height)
        guard high > low else { return nil }
        let delta = b.center-a.center
        var axes: [SIMD2<Double>] = []
        if !a.profile.round { axes += [a.lateral,a.forward] }
        if !b.profile.round { axes += [b.lateral,b.forward] }
        if a.profile.round && b.profile.round {
            axes = [simd_length_squared(delta) > 1e-12 ? simd_normalize(delta) : SIMD2(1,0)]
        } else if a.profile.round || b.profile.round {
            let box = a.profile.round ? b : a, circle = a.profile.round ? a : b
            let corners = [-1.0,1].flatMap { x in [-1.0,1].map { z in box.center+box.lateral*x*box.profile.halfWidth+box.forward*z*box.profile.halfDepth } }
            let nearest = corners.min { simd_length_squared($0-circle.center) < simd_length_squared($1-circle.center) }!
            let direction = nearest-circle.center
            if simd_length_squared(direction) > 1e-12 { axes.append(simd_normalize(direction)) }
        }
        var depth = Double.infinity, n = SIMD2<Double>(1,0)
        for axis in axes {
            let overlap = a.extent(axis)+b.extent(axis)-abs(simd_dot(delta,axis))
            guard overlap > 0 else { return nil }
            if overlap < depth { depth = overlap; n = simd_dot(delta,axis) >= 0 ? axis : -axis }
        }
        let verticalDepth = min(a.position.y+a.profile.height-b.position.y,b.position.y+b.profile.height-a.position.y)
        if verticalDepth < depth {
            let sign = b.position.y+b.profile.height/2 >= a.position.y+a.profile.height/2 ? 1.0 : -1.0
            return Contact(normal:SIMD3(0,sign,0),point:SIMD3((a.position.x+b.position.x)/2,(low+high)/2,(a.position.z+b.position.z)/2),penetration:verticalDepth)
        }
        let tangent = SIMD2(-n.y,n.x)
        let minT = max(simd_dot(a.center,tangent)-a.extent(tangent),simd_dot(b.center,tangent)-b.extent(tangent))
        let maxT = min(simd_dot(a.center,tangent)+a.extent(tangent),simd_dot(b.center,tangent)+b.extent(tangent))
        // Circle contact lies on its radial normal; box faces use the overlap
        // midpoint, preventing artificial spin in a symmetric head-on impact.
        let t = a.profile.round ? simd_dot(a.center,tangent) : b.profile.round ? simd_dot(b.center,tangent) : (minT+maxT)/2
        let along = (simd_dot(a.center,n)+a.extent(n)+simd_dot(b.center,n)-b.extent(n))/2
        let p = n*along+tangent*t
        return Contact(normal:SIMD3(n.x,0,n.y),point:SIMD3(p.x,(low+high)/2,p.y),penetration:depth)
    }
    private static func effectiveMass(_ body: Body, at: SIMD3<Double>, axis: SIMD3<Double>) -> Double {
        let r = at-body.position, lever = r.z*axis.x-r.x*axis.z
        return 1/body.profile.mass+lever*lever/body.profile.inertia
    }
    /// Low restitution for robot shells/rubber, with Coulomb contact friction.
    /// Positional correction is separate from velocity so overlap adds no energy.
    @discardableResult public static func resolve(_ bodies: inout [Body], terrain: Bool = false, betweenRobots: Bool = true) -> Int {
        var pairs = Set<Int>()
        for iteration in 0..<24 {
            var worstOverlap = 0.0
            for i in bodies.indices where betweenRobots { for j in bodies.indices where j > i {
                guard let c = contact(bodies[i],bodies[j]) else { continue }
                worstOverlap = max(worstOverlap,c.penetration)
                pairs.insert(i*bodies.count+j)
                bodies[i].contacted = true; bodies[j].contacted = true
                let relative = bodies[j].contactVelocity(c.point)-bodies[i].contactVelocity(c.point)
                let closing = simd_dot(relative,c.normal)
                if closing < 0 {
                    let restitution = iteration == 0 && closing < -0.5 ? 0.08 : 0
                    let inverseMass = effectiveMass(bodies[i],at:c.point,axis:c.normal)+effectiveMass(bodies[j],at:c.point,axis:c.normal)
                    let normalImpulse = -(1+restitution)*closing/inverseMass
                    bodies[i].impulse(-c.normal*normalImpulse,at:c.point); bodies[j].impulse(c.normal*normalImpulse,at:c.point)
                    let after = bodies[j].contactVelocity(c.point)-bodies[i].contactVelocity(c.point)
                    let sliding = after-c.normal*simd_dot(after,c.normal)
                    let length = simd_length(sliding)
                    if length > 1e-9 {
                        let tangent = sliding/length
                        let denominator = effectiveMass(bodies[i],at:c.point,axis:tangent)+effectiveMass(bodies[j],at:c.point,axis:tangent)
                        let friction = min(length/denominator,0.45*normalImpulse)
                        bodies[i].impulse(tangent*friction,at:c.point); bodies[j].impulse(-tangent*friction,at:c.point)
                    }
                }
                let inverseA = 1/bodies[i].profile.mass, inverseB = 1/bodies[j].profile.mass
                let correction = max(0,c.penetration-0.0002)*0.8/(inverseA+inverseB)
                bodies[i].position -= c.normal*correction*inverseA
                bodies[j].position += c.normal*correction*inverseB
            } }
            if terrain { for i in bodies.indices { constrainToCourse(&bodies[i]) } }
            if worstOverlap < 0.00025 { break }
        }
        return pairs.count
    }
    private static func constrainToCourse(_ body: inout Body) {
        let projection = DirtCourse.projection(x:body.position.x,z:body.position.z)
        let heading = DirtCourse.heading(projection.phase)
        let outward = SIMD2(cos(heading),-sin(heading))*(projection.offset < 0 ? -1.0 : 1.0)
        let insideField = projection.offset < 0 && projection.distance > DirtCourse.fenceOffset
        let support = body.extent(outward)+0.025
        let limit = DirtCourse.fenceOffset+(insideField ? DirtCourse.boundaryWallThickness+support : -support)
        let serviceAccess = projection.offset < 0 && DirtCourse.serviceAccess(x:body.position.x,z:body.position.z,clearance:body.extent(SIMD2(1,0)))
        if (insideField ? projection.distance < limit : projection.distance > limit) && !serviceAccess {
            let point = DirtCourse.point(projection.phase,offset:projection.offset < 0 ? -limit : limit)
            body.position.x = point.x; body.position.z = point.z
            let normal = SIMD3(outward.x,0,outward.y)*(insideField ? -1.0:1.0), speed = simd_dot(body.velocity,normal)
            if speed > 0 { body.velocity -= normal*speed }
            body.contacted = true
        }
        let floor = DirtCourse.height(x:body.position.x,z:body.position.z)
        if body.position.y < floor {
            body.position.y = floor
            if body.velocity.y < 0 { body.velocity.y = 0 }
        }
    }
}
