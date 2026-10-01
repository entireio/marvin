import Foundation
import SimulationCore
import simd

extension SimulationTests {
    func testCityEscape() {
        let h=1.0/240
        for side in [-1.0,1.0] {
            require(CityExit.wallTop(CityExit.point(side*2,0))>=CityExit.floor+CityExit.gateHeight+0.035-0.00001)
        }
        // The straight leaf needs a level sill across its full width and depth.
        for along in stride(from:-CityExit.width/2,through:CityExit.width/2,by:0.025) {
            for out in [-0.055,0,0.055] {
                let p=CityExit.point(along,out)
                near(DirtCourse.height(x:p.x,z:p.y),CityExit.floor,accuracy:0.003)
            }
        }
        var gate=CityGate()
        gate.wantsOpen=true
        var previousSpeed=0.0
        for _ in 0..<2400 {
            gate.advance(dt:h,bodies:[])
            require(gate.angle>=0 && gate.angle<=100 * .pi/180)
            require(abs(gate.velocity)<=0.450001)
            require(abs(gate.velocity-previousSpeed)<=0.7*h+0.001 || gate.velocity==0)
            previousSpeed=gate.velocity
            let body=gate.body
            for t in stride(from:-1.6,through:1.6,by:0.1) {
                let p=SIMD2(body.position.x,body.position.z)+SIMD2(cos(body.heading),-sin(body.heading))*t
                require(body.position.y>DirtCourse.height(x:p.x,z:p.y)-0.01)
            }
        }
        near(gate.angle,100 * .pi/180,accuracy:0.0001)
        print("City exit center \(CityExit.center), sill \(CityExit.floor)")
        // Closed gate and either jamb stop all four boosted chassis. Open gate
        // lets them descend and return without an invisible course boundary.
        for profile in RobotCollisions.profiles {
            for open in [false,true] {
                let p=CityExit.point(0,-0.95)
                var bodies=[RobotCollisions.Body(position:SIMD3(p.x,DirtCourse.height(x:p.x,z:p.y),p.y),heading:atan2(CityExit.outward.x,CityExit.outward.y),profile:profile)]
                let door=open ? gate:CityGate()
                var furthest = -100.0
                for _ in 0..<480 {
                    bodies[0].velocity=SIMD3(CityExit.outward.x*12,0,CityExit.outward.y*12)
                    bodies[0].position += bodies[0].velocity*h
                    bodies[0].position.y=DirtCourse.height(x:bodies[0].position.x,z:bodies[0].position.z)
                    _=RobotCollisions.resolve(&bodies,terrain:true,gate:door)
                    furthest=max(furthest,CityExit.local(SIMD2(bodies[0].position.x,bodies[0].position.z)).y)
                    require(RobotCollisions.contact(bodies[0],door.body)==nil)
                }
                require(open ? furthest>8:furthest<0)
                if open {
                    for _ in 0..<480 {
                        bodies[0].velocity=SIMD3(-CityExit.outward.x*12,0,-CityExit.outward.y*12)
                        bodies[0].position += bodies[0].velocity*h
                        bodies[0].position.y=DirtCourse.height(x:bodies[0].position.x,z:bodies[0].position.z)
                        _=RobotCollisions.resolve(&bodies,terrain:true,gate:door)
                    }
                    require(CityExit.local(SIMD2(bodies[0].position.x,bodies[0].position.z)).y < -0.7)
                }
            }
        }
        // Stop a closing gate in its swing and resume smoothly when clear.
        let blocker=RobotCollisions.Body(position:gate.body(at:0.8).position,profile:RobotCollisions.profiles[0])
        gate.wantsOpen=false
        for _ in 0..<2000 { gate.advance(dt:h,bodies:[blocker]) }
        require(gate.blocked && gate.angle>0.8)
        require(RobotCollisions.contact(gate.body,blocker)==nil)
        gate.wantsOpen=true
        for _ in 0..<2400 { gate.advance(dt:h,bodies:[blocker]) }
        near(gate.angle,100 * .pi/180,accuracy:0.001)
        gate.wantsOpen=false
        for _ in 0..<2400 { gate.advance(dt:h,bodies:[]) }
        near(gate.angle,0,accuracy:0.0001)
        // Grade and continuity through the center driving corridor.
        for along in [-0.8,0.0,0.8] {
            var last:Double?
            for out in stride(from:0.15,through:6,by:0.01) {
                let p=CityExit.point(along,out),height=DirtCourse.height(x:p.x,z:p.y)
                if let last { require(abs(height-last)<0.0025) }
                last=height
            }
        }
        // A collision impulse must not push a racer through a non-gate wall.
        let wallPhase=CityExit.phase+0.4
        let before=DirtCourse.point(wallPhase,offset:DirtCourse.fenceOffset-0.35)
        let beyond=DirtCourse.point(wallPhase,offset:DirtCourse.fenceOffset+0.1)
        var pushed=[RobotCollisions.Body(position:SIMD3(beyond.x,0,beyond.z),profile:RobotCollisions.profiles[0])]
        _=RobotCollisions.resolve(&pushed,terrain:true,gate:gate,previousPositions:[SIMD3(before.x,0,before.z)])
        require(DirtCourse.projection(x:pushed[0].position.x,z:pushed[0].position.z).distance<DirtCourse.fenceOffset)
        // Spatial query must include solids straddling bucket boundaries.
        let fixture=RobotCollisions.Body(position:SIMD3(8,0,8),profile:.init(mass:1,halfWidth:2,halfDepth:2,height:2))
        let city=CityCollisionWorld([fixture])
        let body=RobotCollisions.Body(position:SIMD3(5.9,0,8),profile:RobotCollisions.profiles[0])
        require(city.nearby(body).count==1)
        // Updating a pedestrian must move its collision proxy, without stale
        // bucket entries or rebuilding the immutable architectural index.
        var pedestrian=RobotCollisions.Body(position:SIMD3(6,0,8),profile:.init(mass:70,halfWidth:0.17,halfDepth:0.17,height:1.05,round:true))
        let occupied=city.withDynamicBodies([pedestrian])
        require(occupied.nearby(body).count==2)
        pedestrian.position=SIMD3(32,0,8)
        let moved=city.withDynamicBodies([pedestrian])
        require(moved.nearby(body).count==1)
        require(moved.nearby(pedestrian).count==1)
        require(city.nearby(pedestrian).isEmpty)
        // A reverse recovery behind a corner must select the clear earlier leg,
        // not resume lookahead through the building toward the old waypoint.
        let corner=CityCollisionWorld([.init(position:SIMD3(62,0,60),profile:.init(mass:100,halfWidth:0.2,halfDepth:1,height:3))])
        let route:[SIMD2<Double>]=[.zero,.zero,.zero,SIMD2(60,62),SIMD2(64,62),SIMD2(64,60)]
        require(PostRaceEscape.recoveryWaypoint(route:route,waypoint:5,from:SIMD2(60,60),city:corner)==3)
        require(PostRaceEscape.recoveryWaypoint(route:Array(route.prefix(3))+[SIMD2(64,60)],waypoint:3,from:SIMD2(60,60),city:corner)==nil)
    }
}
