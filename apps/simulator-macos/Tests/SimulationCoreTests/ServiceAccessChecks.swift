import Foundation
import SimulationCore

extension SimulationTests {
    func testServiceAccess() {
        // Enter and leave along the same visible opening, with every playable chassis.
        for step in 0...40 {
            let z = -14.5+Double(step)*0.1,x=DirtCourse.serviceEntryX
            for heading in [0.0,Double.pi] {
                let move=DirtCourse.resolveMove(x:x,z:z,heading:heading)
                require(!move.contact);near(move.x,x,accuracy:1e-9);near(move.z,z,accuracy:1e-9)
                for profile in RobotCollisions.profiles {
                    var bodies=[RobotCollisions.Body(position:SIMD3(x,DirtCourse.height(x:x,z:z),z),heading:heading,profile:profile)]
                    _=RobotCollisions.resolve(&bodies,terrain:true,betweenRobots:false)
                    near(bodies[0].position.x,x,accuracy:1e-9);near(bodies[0].position.z,z,accuracy:1e-9)
                }
            }
        }
        // Once through, movement around the repair apron must not snap back to the course.
        for x in [-9.0,-7.5,-6.0] {
            let p=DirtCourse.projection(x:x,z:-8)
            require(p.offset < 0 && p.distance > DirtCourse.fenceOffset+0.5)
            let move=DirtCourse.resolveMove(x:x,z:-8,heading:0)
            require(!move.contact);near(move.x,x,accuracy:1e-9);near(move.z,-8,accuracy:1e-9)
        }
        // The opening is only on the inner fence. Adjacent brick wall still collides.
        for x in [-10.0,-5.0] {
            let phase=DirtCourse.projection(x:x,z:-15).phase
            let p=DirtCourse.point(phase,offset:-DirtCourse.fenceOffset)
            require(DirtCourse.resolveMove(x:p.x,z:p.z,heading:0).contact)
            let fieldSide=DirtCourse.point(phase,offset:-DirtCourse.fenceOffset-0.08)
            let blocked=DirtCourse.resolveMove(x:fieldSide.x,z:fieldSide.z,heading:0)
            require(blocked.contact)
            require(DirtCourse.projection(x:blocked.x,z:blocked.z).distance > DirtCourse.fenceOffset)
            var bodies=[RobotCollisions.Body(position:SIMD3(fieldSide.x,0,fieldSide.z),profile:RobotCollisions.profiles[3])]
            _=RobotCollisions.resolve(&bodies,terrain:true,betweenRobots:false)
            require(bodies[0].contacted)
            require(DirtCourse.projection(x:bodies[0].position.x,z:bodies[0].position.z).distance > DirtCourse.fenceOffset+DirtCourse.boundaryWallThickness)
        }
        let phase=DirtCourse.projection(x:DirtCourse.serviceEntryX,z:-15).phase
        let outer=DirtCourse.point(phase,offset:DirtCourse.fenceOffset)
        require(DirtCourse.resolveMove(x:outer.x,z:outer.z,heading:0).contact)
        require(!DirtCourse.serviceAccess(x:DirtCourse.serviceEntryX+1.0,z:-12,clearance:0.4))
        print("PASS: service opening admits all four chassis in both directions; adjacent and outer fences remain solid")
    }
}
