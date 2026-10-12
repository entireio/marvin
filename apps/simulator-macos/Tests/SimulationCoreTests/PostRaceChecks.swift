import Foundation
import SimulationCore

extension SimulationTests {
    func testCooldownLap() {
        var race=DirtRace();race.countDown(dt:3)
        func visit(_ phase:Double) {
            let p=DirtCourse.point(phase);race.advance(x:p.x,z:p.z,dt:0.01)
        }
        for i in 1...3000 { visit(Double(i)*6 * .pi/3000) }
        require(race.finished && !race.completedCooldownLap)
        let time=race.elapsed,laps=race.laps,progress=race.progress
        // Oscillating backwards and forwards cannot satisfy the extra lap.
        for _ in 0..<4 {
            for i in 1...100 { visit(6 * .pi-Double(i)*0.002) }
            for i in 1...100 { visit(6 * .pi-0.2+Double(i)*0.002) }
        }
        require(!race.completedCooldownLap)
        for i in 1...999 { visit(6 * .pi+Double(i)*2 * .pi/1000) }
        require(!race.completedCooldownLap)
        visit(8 * .pi+0.00001)
        require(race.completedCooldownLap)
        equal(race.elapsed,time);equal(race.laps,laps);equal(race.progress,progress)
        // A long sample across the map is not lap progress.
        var teleported=DirtRace();teleported.countDown(dt:3)
        for i in 1...3000 { let p=DirtCourse.point(Double(i)*6 * .pi/3000);teleported.advance(x:p.x,z:p.z,dt:0.01) }
        let before=teleported.cooldownProgress
        let p=DirtCourse.point(.pi);teleported.advance(x:p.x,z:p.z,dt:1)
        near(teleported.cooldownProgress,before,accuracy:1e-9)
        print("Cooldown: PASS (full extra lap, reverse rejection, frozen results)")
    }
}
