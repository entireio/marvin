import Foundation
import SimulationCore

extension SimulationTests {
    func testPoweredRolling() {
        for character in RacePerformance.Character.allCases {
            var input = DriveInput(); input.throttle = 1
            var sandbox = Simulation(seed:0,character:character)
            for _ in 0..<600 { sandbox.advance(input,dt:1.0/60) }
            require(sandbox.contacting)
            let blocked = sandbox
            sandbox.advance(input,dt:0.1)
            near(sandbox.x,blocked.x,accuracy:1e-9); near(sandbox.z,blocked.z,accuracy:1e-9)
            require(sandbox.leftTravel > blocked.leftTravel && sandbox.rightTravel > blocked.rightTravel)
            require(sandbox.rollingTravel.y > blocked.rollingTravel.y)
            input.brake = true
            let braking = sandbox
            sandbox.advance(input,dt:0.1)
            equal(sandbox.leftTravel,braking.leftTravel); equal(sandbox.rollingTravel,braking.rollingTravel)
            sandbox.reset(); equal(sandbox.rollingTravel,.zero); equal(sandbox.leftTravel,0)
        }
        var input = DriveInput(); input.throttle = 1
        var fence = Simulation(seed:0,dirtTrack:true,dirtStartPhase:0.6)
        var fenceSamples = 0
        for _ in 0..<600 {
            let before = fence
            fence.advance(input,dt:1.0/60)
            if fence.contacting {
                fenceSamples += 1
                require(fence.leftTravel > before.leftTravel && fence.rightTravel > before.rightTravel)
                let delta = fence.rollingTravel-before.rollingTravel
                require(hypot(delta.x,delta.y) > 0)
            }
        }
        require(fenceSamples > 30)
        var jumper = DirtOpponent(), airborneSamples = 0
        for _ in 0..<600 {
            let before = jumper.simulation
            jumper.advance(dt:1.0/60,raceDT:1.0/60)
            let after = jumper.simulation
            if before.airborne && after.airborne {
                airborneSamples += 1
                require(after.leftTravel != before.leftTravel && after.rightTravel != before.rightTravel)
                let delta = after.rollingTravel-before.rollingTravel
                require(hypot(delta.x,delta.y) > 0)
            }
        }
        require(airborneSamples > 10)
        print("PASS: powered rolling at sandbox barriers, fences, and during jumps; brake/reset preserved")
    }
}
