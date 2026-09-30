import Foundation
import SimulationCore
import simd

extension SimulationTests {
    func testSandstorm() {
        var clear=Sandstorm(),storm=Sandstorm(enabled:true)
        clear.advance(300);equal(clear.elapsed,0);equal(clear.wind(x:0,z:0),.zero)
        let p=Sandstorm.drifts[0].center,early=storm.depth(x:p.x,z:p.y)
        storm.advance(180);require(storm.depth(x:p.x,z:p.y)>early*4)
        var maximumError=0.0
        // Compare the actual rendered 10 cm triangle interpolation to the
        // analytic collision surface at interior samples, including edges.
        for drift in Sandstorm.drifts {
            for iz in -12...12 { for ix in -12...12 {
                let x=drift.center.x+Double(ix)*0.13,z=drift.center.y+Double(iz)*0.13
                let gx=floor(x/0.10)*0.10,gz=floor(z/0.10)*0.10,u=(x-gx)/0.10,v=(z-gz)/0.10
                let a=storm.depth(x:gx,z:gz),b=storm.depth(x:gx+0.10,z:gz),c=storm.depth(x:gx+0.10,z:gz+0.10),d=storm.depth(x:gx,z:gz+0.10)
                let mesh=u>=v ? a*(1-u)+b*(u-v)+c*v:a*(1-v)+c*u+d*(v-u)
                maximumError=max(maximumError,abs(mesh-storm.depth(x:x,z:z)))
                equal(clear.height(x:x,z:z),DirtCourse.height(x:x,z:z))
            }}
        }
        print("Maximum drift interpolation error: \(maximumError)");fflush(stdout)
        require(maximumError<0.005)
        var results=[SIMD3<Double>]()
        for fps in [30,60,120] {
            let slot=DirtCourse.startingGrid[0]
            var player=Simulation(seed:0,dirtTrack:true,dirtStartOffset:slot.offset,dirtStartPhase:slot.phase)
            var race=DirtRace(startPhase:slot.phase),world=DirtRacePhysics()
            var rivals=DirtCourse.startingGrid.dropFirst().map{DirtOpponent(slot:$0)}
            world.storm=Sandstorm(enabled:true);race.countDown(dt:3)
            var input=DriveInput();input.throttle=0.6
            for _ in 0..<fps*8 { world.advance(input,player:&player,race:&race,opponents:&rivals,dt:1/Double(fps),raceDT:1/Double(fps)) }
            near(world.storm.elapsed,8,accuracy:1e-8)
            results.append(SIMD3(player.x,player.z,player.heading))
        }
        for result in results { require(simd_length(result-results[0])<0.00001) }
        print("Storm checks: rendered/collision depth error \(maximumError)m; identical 30/60/120 Hz physics")
    }
}
