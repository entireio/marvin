import Foundation
import SimulationCore

extension SimulationTests {
    func testAssistedCornering() {
        struct Result { var error = 0.0, peak = 0.0, contacts = 0, progress = 0.0 }
        func drive(_ assists: DirtDrivingAssists, phase: Double, handsOff: Bool = false, manualBrakes:Bool = true) -> Result {
            var state = Simulation(seed:0,dirtTrack:true,dirtStartPhase:phase)
            var race = DirtRace(startPhase:phase), physics = DirtRacePhysics()
            var rivals = (0..<3).map { DirtOpponent(slot:(phase:phase-1-Double($0)*0.15,offset:0)) }
            race.countDown(dt:3)
            var result = Result(), input = DriveInput()
            for frame in 0..<360 {
                // Independent centerline driver with 150 ms reaction time.
                // Never consult the assist's line or corner-speed recommendation.
                if frame.isMultiple(of:9) {
                    let at = DirtCourse.phase(x:state.x,z:state.z), target = DirtCourse.point(at+0.075)
                    let desired = atan2(target.x-state.x,target.z-state.z)
                    let error = atan2(sin(desired-state.heading),cos(desired-state.heading))
                    let delta = DirtCourse.heading(at+0.15)-DirtCourse.heading(at)
                    let bend = abs(atan2(sin(delta),cos(delta)))
                    input.throttle = 1
                    if !handsOff {
                        input.turn = abs(error) < 0.08 ? 0 : error > 0 ? -1 : 1
                        input.brake = manualBrakes && state.groundSpeed > (bend > 0.4 ? 3.0 : 5.5)
                    }
                }
                physics.advance(input,player:&state,race:&race,opponents:&rivals,dt:1.0/60,raceDT:1.0/60,
                                robotCollisionsEnabled:false,assists:assists)
                let distance = DirtCourse.projection(x:state.x,z:state.z).distance
                result.error += distance/360; result.peak = max(result.peak,distance)
                if state.contacting { result.contacts += 1 }
            }
            result.progress = race.progress-phase
            return result
        }
        let settings = [DirtDrivingAssists.off, DirtDrivingAssists(braking:false),
                        DirtDrivingAssists(steering:false), DirtDrivingAssists()]
        for i in 0..<12 {
            let phase = Double(i)*2 * .pi/12
            let runs = settings.map { drive($0,phase:phase) }
            for run in runs {
                require(run.contacts == 0 && run.peak < DirtCourse.width)
                require(run.progress > 0.5)
            }
            print(String(format:"Corner %.3f: manual %.3f, assisted %.3f; no fence contacts",phase,runs[0].error,runs[3].error))
        }
        // Centerline error is descriptive, not the objective: the driver must
        // retain room to choose a different line. Test missed braking instead.
        var unbrakedContacts=0,helpedContacts=0,unbrakedPeak=0.0,helpedPeak=0.0
        for i in 0..<12 {
            let phase=Double(i)*2 * .pi/12
            let a=drive(.off,phase:phase,manualBrakes:false)
            let b=drive(DirtDrivingAssists(),phase:phase,manualBrakes:false)
            unbrakedContacts += a.contacts;helpedContacts += b.contacts
            unbrakedPeak += a.peak;helpedPeak += b.peak
        }
        print("Missed braking: contacts \(unbrakedContacts) -> \(helpedContacts), summed peak error \(unbrakedPeak) -> \(helpedPeak)")
        require(helpedContacts<unbrakedContacts && helpedPeak<unbrakedPeak)
        let manual = drive(.off,phase:2.4,handsOff:true)
        let handsOff = drive(DirtDrivingAssists(braking:false),phase:2.4,handsOff:true)
        near(manual.error,handsOff.error,accuracy:1e-10)
        near(manual.progress,handsOff.progress,accuracy:1e-10)
        equal(manual.contacts,handsOff.contacts)
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
        print("PASS: predictive braking reduces missed-braking contacts; steering-only hands-off behavior is unchanged")
    }

    func testBrakingAuthority() {
        func stop(assisted:Bool, turn:Double = 0) -> (distance:Double,heading:Double,speed:Double) {
            var state = Simulation(seed:0,dirtTrack:true,dirtStartPhase:-0.15)
            var race = DirtRace(startPhase:-0.15), physics = DirtRacePhysics()
            var rivals = (0..<3).map { DirtOpponent(slot:(phase:-1-Double($0)*0.15,offset:0)) }
            race.countDown(dt:3)
            var input = DriveInput(); input.throttle = 1; input.boost = true
            for _ in 0..<30 {
                physics.advance(input,player:&state,race:&race,opponents:&rivals,dt:1.0/60,raceDT:1.0/60,robotCollisionsEnabled:false)
            }
            let initial = state
            require(state.groundSpeed > 4)
            input.brake = true; input.turn = turn
            for _ in 0..<60 {
                let before = state.groundSpeed
                physics.advance(input,player:&state,race:&race,opponents:&rivals,dt:1.0/60,raceDT:1.0/60,
                                robotCollisionsEnabled:false,assists:DirtDrivingAssists(steering:false,braking:assisted))
                require(state.groundSpeed <= before+0.00001)
            }
            let delta = state.heading-initial.heading
            return (hypot(state.x-initial.x,state.z-initial.z),atan2(sin(delta),cos(delta)),state.groundSpeed)
        }
        let manual = stop(assisted:false), assisted = stop(assisted:true)
        require(assisted.distance <= manual.distance+0.001)
        near(assisted.speed,0,accuracy:0.001)
        for turn in [-0.5,0.5] {
            let result = stop(assisted:true,turn:turn)
            require(result.heading*turn < -0.01)
            near(result.speed,0,accuracy:0.001)
        }
        print("PASS: brake help preserves stopping strength and steering in both directions")
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
            // No steering input stays neutral; corner braking may reduce throttle.
            let idle = assists.apply(input,to:state)
            equal(idle.turn,0); equal(idle.brake,false); require(idle.throttle>0 && idle.throttle<=1)
            for turn in [-1.0,1.0] {
                input.turn = turn; input.brake = true
                let manual = DirtDrivingAssists.off.apply(input,to:state)
                equal(manual.turn,input.turn); equal(manual.brake,input.brake)
                let brakeOnly = DirtDrivingAssists(steering:false).apply(input,to:state)
                equal(brakeOnly.turn,turn)
                let assisted = assists.apply(input,to:state)
                require(assisted.turn*turn >= 0.8 && abs(assisted.turn) <= 1)
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
        // Sweep through the reference turn's sign change. A held steering
        // key must not jump back to full strength as the curve straightens.
        for direction in [-1.0,1.0] {
            var previous: Double?
            for i in -200...200 {
                var state = Simulation(seed:0,dirtTrack:true,dirtStartOffset:Double(i)*0.005)
                var input = DriveInput(); input.throttle = 1
                state.advance(input,dt:0.1); input.turn = direction
                let output = assists.apply(input,to:state).turn
                if let previous { require(abs(output-previous) < 0.04) }
                require(output*direction > 0)
                previous = output
            }
        }
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
