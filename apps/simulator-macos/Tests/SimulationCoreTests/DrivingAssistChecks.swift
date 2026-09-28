import Foundation
import SimulationCore

extension SimulationTests {
    func testAssistedCornering() {
        func drive(_ assists: DirtDrivingAssists, phase: Double, fps: Double = 60,
                   handsOff: Bool = false) -> (error:Double, progress:Double, speed:Double, heading:Double) {
            var state = Simulation(seed:0,dirtTrack:true,dirtStartPhase:phase)
            var race = DirtRace(startPhase:phase), physics = DirtRacePhysics()
            var rivals = (0..<3).map { DirtOpponent(slot:(phase:phase-1-Double($0)*0.15,offset:0)) }
            race.countDown(dt:3)
            var error = 0.0
            for _ in 0..<Int(fps*8) {
                let at = DirtCourse.phase(x:state.x,z:state.z)
                let guide = DirtRacingLine.guidance(x:state.x,z:state.z,heading:state.heading,speed:state.speed,phase:at)
                // A coarse keyboard driver presses the right direction, with
                // a dead zone, and brakes when approaching corner speed.
                var input = DriveInput(); input.throttle = 1; input.boost = true
                if !handsOff {
                    input.turn = abs(guide.turn) < 0.15 ? 0 : guide.turn > 0 ? 1 : -1
                    input.brake = state.speed > guide.speed+0.3
                }
                physics.advance(input,player:&state,race:&race,opponents:&rivals,dt:1/fps,raceDT:1/fps,
                                robotCollisionsEnabled:false,assists:assists)
                let p = DirtRacingLine.point(DirtCourse.phase(x:state.x,z:state.z))
                error += hypot(p.x-state.x,p.z-state.z)/(fps*8)
            }
            return (error,race.progress-phase,state.speed,state.heading)
        }
        var manualError = 0.0, assistedError = 0.0
        for phase in [0.6,2.4,4.2] {
            let manual = drive(.off,phase:phase), assisted = drive(DirtDrivingAssists(),phase:phase)
            print("Corner \(phase): manual error \(manual.error), assisted \(assisted.error); progress \(manual.progress), \(assisted.progress)")
            manualError += manual.error; assistedError += assisted.error
            require(assisted.progress > 0.1)
        }
        require(assistedError < manualError*0.9)
        let manual = drive(.off,phase:2.4,handsOff:true)
        let handsOff = drive(DirtDrivingAssists(),phase:2.4,handsOff:true)
        near(manual.error,handsOff.error,accuracy:1e-10)
        near(manual.progress,handsOff.progress,accuracy:1e-10)
        near(manual.speed,handsOff.speed,accuracy:1e-10)
        // Active assists re-evaluate at the physics clock, not the renderer.
        func scripted(fps: Double, assists: DirtDrivingAssists) -> Simulation {
            var state = Simulation(seed:0,dirtTrack:true,dirtStartPhase:2.4)
            var race = DirtRace(startPhase:2.4), physics = DirtRacePhysics()
            var rivals = (0..<3).map { DirtOpponent(slot:(phase:1.0-Double($0)*0.15,offset:0)) }
            race.countDown(dt:3)
            for frame in 0..<Int(fps*4) {
                var input = DriveInput(); input.throttle = 1; input.turn = -0.6
                input.brake = frame >= Int(fps*2)
                physics.advance(input,player:&state,race:&race,opponents:&rivals,dt:1/fps,raceDT:1/fps,
                                robotCollisionsEnabled:false,assists:assists)
            }
            return state
        }
        let slow = scripted(fps:30,assists:DirtDrivingAssists())
        let fast = scripted(fps:120,assists:DirtDrivingAssists())
        let unassisted = scripted(fps:60,assists:.off)
        near(slow.x,fast.x,accuracy:1e-8); near(slow.z,fast.z,accuracy:1e-8)
        near(slow.heading,fast.heading,accuracy:1e-8)
        near(slow.speed,0,accuracy:0.01)
        require(hypot(fast.x-unassisted.x,fast.z-unassisted.z) > 0.1)
        print("PASS: assisted cornering improves line error; hands-off behavior is unchanged")
    }

    func testDrivingAssists() {
        let assists = DirtDrivingAssists()
        // Check the entire reference line, including interpolation at the seam.
        for i in 0..<768 {
            let p = DirtRacingLine.point(Double(i)*2 * .pi/768)
            require(DirtCourse.contains(x:p.x,z:p.z,margin:0.9))
        }
        let start = DirtRacingLine.point(0), end = DirtRacingLine.point(2 * .pi)
        near(start.x,end.x,accuracy:1e-9); near(start.z,end.z,accuracy:1e-9)
        var changed = 0
        for phase in [0.6,1.2,1.8,2.4,3.0,3.6,4.2,4.8,5.4] {
            var state = Simulation(seed:0,dirtTrack:true,dirtStartPhase:phase)
            var input = DriveInput(); input.throttle = 1
            for _ in 0..<8 { state.advance(input,dt:0.05) }
            // No input is never converted into auto-steering or auto-braking.
            let idle = assists.apply(input,to:state)
            equal(idle.turn,0); equal(idle.brake,false); equal(idle.throttle,1)
            for turn in [-1.0,1.0] {
                input.turn = turn; input.brake = true
                let manual = DirtDrivingAssists.off.apply(input,to:state)
                equal(manual.turn,input.turn); equal(manual.brake,input.brake)
                let brakeOnly = DirtDrivingAssists(steering:false).apply(input,to:state)
                equal(brakeOnly.turn,turn)
                let assisted = assists.apply(input,to:state)
                require(assisted.turn*turn > 0 && abs(assisted.turn) <= 1)
                if abs(assisted.turn-turn) > 0.05 { changed += 1 }
                let steerOnly = DirtDrivingAssists(braking:false).apply(input,to:state)
                equal(steerOnly.turn,assisted.turn)
                // Braking must reduce speed without the manual instant wheel stop.
                var braking = state, manualBraking = state
                braking.advance(brakeOnly,dt:0.02)
                manualBraking.advance(manual,dt:0.02)
                if !state.airborne && state.speed > 0.5 {
                    less(braking.speed,state.speed)
                    require(braking.speed >= manualBraking.speed)
                }
            }
        }
        require(changed > 0)
        // Sandbox controls stay exact, regardless of the two settings.
        var sandbox = Simulation(seed:0), input = DriveInput()
        input.throttle = 1; input.turn = 0.6; input.brake = true
        let untouched = assists.apply(input,to:sandbox)
        sandbox.advance(untouched,dt:0.1)
        equal(untouched.turn,input.turn); equal(sandbox.speed,0)
        var reverse = Simulation(seed:0,dirtTrack:true,dirtStartPhase:2.4)
        var reverseInput = DriveInput(); reverseInput.throttle = -1
        reverse.advance(reverseInput,dt:0.1); reverseInput.turn = 0.6; reverseInput.brake = true
        equal(assists.apply(reverseInput,to:reverse).turn,reverseInput.turn)
        reverse.paused = true
        let paused = reverse
        reverse.advance(assists.apply(reverseInput,to:reverse),dt:0.1)
        equal(reverse.distance,paused.distance); equal(reverse.heading,paused.heading)
        print("PASS: assist input gates, independent switches, line bounds, brake deceleration")
    }
}
