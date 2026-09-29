import Foundation
import simd
import SimulationCore

extension SimulationTests {
    func testInfieldObstacles() {
        require(InfieldLayout.parts.count>=6)
        // Largest and smallest chassis can travel from the opening past the
        // clipped corner. No giant tent bounding box may close this aisle.
        let route=InfieldLayout.serviceLane
        for profile in RobotCollisions.profiles {
            for i in 1..<route.count {
                let delta=route[i]-route[i-1],heading=atan2(delta.x,delta.y)
                for step in 0...40 {
                    let p=route[i-1]+delta*Double(step)/40
                    var bodies=[RobotCollisions.Body(position:SIMD3(p.x,DirtCourse.height(x:p.x,z:p.y),p.y),heading:heading,profile:profile)]
                    _=RobotCollisions.resolve(&bodies,terrain:true,betweenRobots:false)
                    if abs(bodies[0].position.x-p.x)>0.001 || abs(bodies[0].position.z-p.y)>0.001 { print("Blocked route: \(p), heading \(heading), result \(bodies[0].position), chassis \(profile.halfWidth)") }
                    near(bodies[0].position.x,p.x,accuracy:0.001)
                    near(bodies[0].position.z,p.y,accuracy:0.001)
                }
            }
            // Turning and reversing must fit too, not just facing each segment.
            for p in route { for turn in 0..<24 {
                var bodies=[RobotCollisions.Body(position:SIMD3(p.x,DirtCourse.height(x:p.x,z:p.y),p.y),heading:Double(turn)*Double.pi/12,profile:profile)]
                _=RobotCollisions.resolve(&bodies,terrain:true,betweenRobots:false)
                near(bodies[0].position.x,p.x,accuracy:0.001)
                near(bodies[0].position.z,p.y,accuracy:0.001)
            }}
            // Continuous 240 Hz boosted approaches: no tunneling through salvage,
            // including the low metal panels. Static contacts remain enabled when
            // robot-to-robot collisions are disabled.
            for part in InfieldLayout.parts {
                let obstacle=InfieldLayout.obstacles[InfieldLayout.obstacles.count-InfieldLayout.parts.count+InfieldLayout.parts.firstIndex(where:{$0.x==part.x && $0.z==part.z})!]
                let axis=SIMD2(cos(part.yaw),-sin(part.yaw))
                let start=SIMD2(part.x,part.z)+axis*(part.width/2+0.8)
                var bodies=[RobotCollisions.Body(position:SIMD3(start.x,0,start.y),heading:part.yaw,profile:profile)]
                var touched=false
                for _ in 0..<65 {
                    bodies[0].velocity=SIMD3(-axis.x*8,0,-axis.y*8)
                    bodies[0].position += bodies[0].velocity/240
                    _=RobotCollisions.resolve(&bodies,terrain:true,betweenRobots:false)
                    touched = touched || bodies[0].contacted
                    if let c=RobotCollisions.contact(bodies[0],obstacle) { require(c.penetration<0.001) }
                }
                require(touched)
                let final=SIMD2(bodies[0].position.x-part.x,bodies[0].position.z-part.z)
                require(simd_dot(final,axis)>0)
            }
        }
        let canopy=InfieldLayout.canopies[0]
        let profile=RobotCollisions.profiles[0]
        for (position,expected) in [(SIMD3<Double>(-5.5,0,-9),false),(SIMD3<Double>(-7.8,2,-7.4),false),(SIMD3<Double>(-5.5,2,-9),true)] {
            let body=RobotCollisions.Body(position:position,profile:profile)
            require((RobotCollisions.canopyContact(body,footprint:canopy.points,low:canopy.low,high:canopy.high) != nil)==expected)
        }
        // A narrow pole must stop an approaching robot but allow backing away.
        let pole=InfieldLayout.obstacles.first { abs($0.position.x+6.5)<0.001 && abs($0.position.z-1.5)<0.001 }!
        var probe=[RobotCollisions.Body(position:pole.position+SIMD3(0.29,0,0),velocity:SIMD3(-8,0,0),profile:profile)]
        _=RobotCollisions.resolve(&probe,terrain:true,betweenRobots:false)
        require(probe[0].contacted)
        require(RobotCollisions.contact(probe[0],pole)==nil)
        let before=probe[0].position
        probe[0].velocity=SIMD3(1,0,0);probe[0].position.x += 0.05
        _=RobotCollisions.resolve(&probe,terrain:true,betweenRobots:false)
        require(probe[0].position.x>before.x)
        print("PASS: triangular tent aisle, overhead clearance, and boosted salvage collisions for all four chassis; \(InfieldLayout.parts.count) seeded parts")
    }
}
