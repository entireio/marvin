import Foundation
import SimulationCore
import simd

extension SimulationTests {
    func testTownSpeedAndGaze() {
        for point in [SIMD2<Double>(90,40),SIMD2(210,65)] {
            let p=DirtCourse.projection(x:point.x,z:point.y)
            var sim=Simulation(seed:0,dirtTrack:true,dirtStartOffset:p.offset,dirtStartPhase:p.phase)
            var world=DirtRacePhysics(),race=DirtRace();race.countDown(dt:3)
            var opponents=DirtCourse.startingGrid.dropFirst().map{DirtOpponent(slot:$0)}
            var input=DriveInput();input.throttle=1
            for _ in 0..<240*3 { world.advance(input,player:&sim,race:&race,opponents:&opponents,dt:1.0/240,raceDT:1.0/240,robotCollisionsEnabled:false) }
            require(sim.speed>5.9);require(hypot(sim.velocity.x,sim.velocity.z)>4)
        }
        for character in RacePerformance.Character.allCases {
            var performance=RacePerformance(character)
            for i in 1...120 {
                performance.update(index:0,actors:[.init(x:35,z:12,heading:1.4,speed:5,elapsed:Double(i)/60)])
            }
            near(performance.pose.yaw,0,accuracy:0.001)
            let p=DirtCourse.point(CityExit.phase)
            for i in 121...240 {
                performance.update(index:0,actors:[.init(x:p.x,z:p.z,heading:DirtCourse.heading(CityExit.phase),speed:3,elapsed:Double(i)/60)],followingCourse:false)
            }
            near(performance.pose.yaw,0,accuracy:0.001)
        }
        print("PASS: full drivetrain speed on town/dune sand; neutral curve gaze off-track and during gate departure")
    }
}
