import Foundation
import SimulationCore
import simd

extension SimulationTests {
    func testStormLanding() {
        // Captured failure: BB-8 descended at 4.3 m/s while carrying 10.7 m/s
        // horizontally. The old slope-velocity assignment launched it again.
        for normal in [SIMD3<Double>(0,1,0),SIMD3(-0.45,1,0),SIMD3(0.3,1,-0.5)] {
            let n=simd_normalize(normal)
            for incoming in [SIMD3<Double>(10.7,-4.3,1.5),SIMD3(0,-8,0),n*3] {
                let landed=RobotCollisions.landingVelocity(incoming,normal:normal)
                require(simd_length_squared(landed)<=simd_length_squared(incoming)+1e-10)
                require(simd_dot(landed,n)>=(-1e-10))
                let tangent=incoming-n*simd_dot(incoming,n)
                require(simd_length((landed-n*simd_dot(landed,n))-tangent)<1e-10)
                if simd_dot(incoming,n)>0 { require(simd_length(landed-incoming)<1e-10) }
            }
        }
        var boosts=0,slowdowns=0
        for i in 0..<120 {
            let phase=Double(i)*2 * Double.pi/120
            let clear=Simulation(seed:0,dirtTrack:true,dirtStartPhase:phase)
            if DirtOpponent.driveInput(for:clear).boost { boosts += 1 }
            var early=clear;early.storm=Sandstorm(enabled:true)
            var late=early;late.storm.advance(180)
            let a=DirtOpponent.driveInput(for:early),b=DirtOpponent.driveInput(for:late)
            require(!a.boost && !b.boost && b.throttle<=5.2/6+1e-10)
            require(b.throttle<=a.throttle+1e-10)
            if b.throttle<a.throttle-0.01 { slowdowns += 1 }
        }
        require(boosts>0 && slowdowns>0)
        print("PASS: terrain landings do not add kinetic energy; storm AI previews accumulating drifts; clear-weather boost remains available")
    }
}
