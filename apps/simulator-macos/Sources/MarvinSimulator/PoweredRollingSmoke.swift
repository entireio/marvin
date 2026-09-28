import SceneKit
import SimulationCore
import simd

extension AppController {
    func checkPoweredRolling() -> Bool {
        func update(_ state: Simulation) {
            for character in RacePerformance.Character.allCases { updateModel(character,state:state) }
        }
        func spins(from before: Simulation, to after: Simulation) -> Bool {
            update(Simulation(seed:0))
            update(before)
            let marvin = robot.wheels.map { $0.node.eulerAngles.x }
            let r2 = r2d2.wheels.map { $0.node.eulerAngles.x }
            let sphere = bb8.ball.simdOrientation
            let links = wallE.links.map { $0.node.simdPosition }
            update(after)
            return zip(robot.wheels,marvin).allSatisfy { abs($0.0.node.eulerAngles.x-$0.1) > 1e-5 }
                && zip(r2d2.wheels,r2).allSatisfy { abs($0.0.node.eulerAngles.x-$0.1) > 1e-5 }
                && abs(simd_dot(sphere.vector,bb8.ball.simdOrientation.vector)) < 0.99999
                && zip(wallE.links,links).allSatisfy { simd_distance($0.0.node.simdPosition,$0.1) > 1e-5 }
        }
        var input = DriveInput(); input.throttle = 1
        var blocked = Simulation(seed:0)
        for _ in 0..<600 { blocked.advance(input,dt:1.0/60) }
        let before = blocked
        blocked.advance(input,dt:0.1)
        guard blocked.contacting, blocked.x == before.x, blocked.z == before.z,
              spins(from:before,to:blocked) else { return false }
        var fence = Simulation(seed:0,dirtTrack:true,dirtStartPhase:0.6), fencePassed = false
        for _ in 0..<600 {
            let before = fence; fence.advance(input,dt:1.0/60)
            if before.contacting && fence.contacting {
                fencePassed = spins(from:before,to:fence); break
            }
        }
        var jumper = DirtOpponent(), jumpPassed = false
        for _ in 0..<600 {
            let before = jumper.simulation
            jumper.advance(dt:1.0/60,raceDT:1.0/60)
            if before.airborne && jumper.simulation.airborne {
                jumpPassed = spins(from:before,to:jumper.simulation); break
            }
        }
        update(Simulation(seed:0))
        print("All robot running gear: sandbox \(true), fence \(fencePassed), jump \(jumpPassed)")
        return fencePassed && jumpPassed
    }
}
