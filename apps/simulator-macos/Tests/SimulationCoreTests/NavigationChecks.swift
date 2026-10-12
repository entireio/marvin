import Foundation
import SimulationCore
import simd

extension SimulationTests {
    func testNavigationMap() {
        require(RaceMapRegion.at(.zero) == .course)
        require(RaceMapRegion.at(CityExit.point(0,-1)) == .course)
        require(RaceMapRegion.at(CityExit.point(0,2)) == .town)
        require(RaceMapRegion.at(SIMD2(100,30)) == .town)
        require(RaceMapRegion.at(SIMD2(180,50)) == .dunes)
        let boundary=TownFootprint.radius(at:0)
        require(RaceMapRegion.at(SIMD2(boundary,0),previous:.town) == .town)
        require(RaceMapRegion.at(SIMD2(boundary,0),previous:.dunes) == .dunes)
        require(RaceMapRegion.at(SIMD2(boundary-2,0),previous:.dunes) == .town)
        require(RaceMapRegion.at(CityExit.point(0,-1),previous:.town) == .course)
        var ends=[SIMD3<Double>]()
        for fps in [30.0,60,120] {
            var motion=OverviewMotion();motion.reset(eye:SIMD3(0,38,-33),aim:.zero)
            for _ in 0..<Int(fps) { motion.advance(eye:SIMD3(25,41,-18),aim:SIMD3(25,3,15),dt:1/fps) }
            ends.append(motion.eye)
            require(simd_distance(motion.eye,SIMD3(25,41,-18))<0.6)
            near(simd_distance(motion.eye-motion.aim,SIMD3(0,38,-33)),0,accuracy:1e-9)
        }
        require(simd_distance(ends[0],ends[2])<1e-9)
        print("Navigation: COURSE/TOWN/DUNES both directions, boundary hysteresis, smooth 30/60/120 Hz overview: PASS")
    }
}
