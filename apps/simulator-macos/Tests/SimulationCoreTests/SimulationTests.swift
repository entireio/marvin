import Foundation
import SimulationCore

// Dependency-free checks also run with standalone Apple Command Line Tools.
func require(_ value: Bool, file: StaticString = #filePath, line: UInt = #line) {
    if !value { fputs("Check failed at \(file):\(line)\n",stderr); exit(1) }
}
func equal<T: Equatable>(_ a: T, _ b: T, file: StaticString = #filePath, line: UInt = #line) {
    require(a == b, file: file, line: line)
}
func near(_ a: Double, _ b: Double, accuracy: Double, file: StaticString = #filePath, line: UInt = #line) {
    if abs(a-b) > accuracy { fputs("Expected \(a) near \(b), tolerance \(accuracy)\n",stderr) }
    require(abs(a-b) <= accuracy, file: file, line: line)
}
func greater(_ a: Double, _ b: Double) { require(a > b) }
func less(_ a: Double, _ b: Double) { require(a < b) }

struct SimulationTests {
    func testStaggeredGrid() {
        let a = DirtCourse.playerGrid, b = DirtCourse.opponentGrid
        var player = Simulation(dirtTrack:true,dirtStartOffset:a.offset,dirtStartPhase:a.phase)
        let rival = DirtOpponent().simulation
        require(a.phase < 0 && b.phase < a.phase && a.offset*b.offset < 0)
        greater(hypot(player.x-rival.x,player.z-rival.z),1.2)
        let start = player
        var input = DriveInput(); input.throttle = 1
        player.advance(input,dt:0.1); player.reset()
        equal(player.x,start.x); equal(player.z,start.z)
        var race = DirtRace(startPhase:a.phase); race.countDown(dt:3)
        for i in 0...20 {
            let p = DirtCourse.point(a.phase + (0.04-a.phase)*Double(i)/20)
            race.advance(x:p.x,z:p.z,dt:0.1)
        }
        require(race.laps.isEmpty && !race.wrongWay)
        near(race.progress,0.04,accuracy:0.001)
    }
    func testRacePerformance() {
        typealias Actor = RacePerformance.Actor
        for character in [RacePerformance.Character.marvin,.r2d2,.bb8,.wallE] {
            // Both bend directions, before the chassis has started turning.
            for direction in [-1.0,1.0] {
                let phase = (0..<360).map { Double($0)*2 * .pi/360 }.first { phase in
                    let delta = DirtCourse.heading(phase+0.172)-DirtCourse.heading(phase)
                    return atan2(sin(delta),cos(delta))*direction > 0.35
                }!
                let p = DirtCourse.point(phase), heading = DirtCourse.heading(phase)
                var results: [Double] = []
                for fps in [30.0,60.0,120.0] {
                    var actor = RacePerformance(character)
                    for frame in 1...Int(fps) {
                        actor.update(index:0,actors:[Actor(x:p.x,z:p.z,heading:heading,speed:6,elapsed:Double(frame)/fps)])
                    }
                    require(actor.pose.yaw*direction > 0.25)
                    results.append(actor.pose.yaw)
                    if character == .wallE {
                        require((actor.pose.leftArm-actor.pose.rightArm)*direction > 0.1)
                    }
                    if character == .r2d2 { equal(actor.pose.pitch,0); equal(actor.pose.roll,0) }
                }
                near(results.min()!,results.max()!,accuracy:1e-8)
            }
            for side in [-1.0,1.0] {
                var actor = RacePerformance(character)
                let p = DirtCourse.point(0), heading = DirtCourse.heading(0)
                func scene(_ time: Double) -> [Actor] {
                    let along = 2.4-time*3
                    return [Actor(x:p.x,z:p.z,heading:heading,speed:5,elapsed:time),
                        Actor(x:p.x+cos(heading)*side+sin(heading)*along,z:p.z-sin(heading)*side+cos(heading)*along,heading:heading,speed:2,elapsed:time)]
                }
                var glanced = false
                for frame in 1...50 {
                    actor.update(index:0,actors:scene(Double(frame)/60))
                    if actor.lookingAt == 1, frame > 25 { glanced = true; require(actor.pose.yaw*side > 0.2) }
                }
                require(glanced)
                let held = actor.pose
                actor.update(index:0,actors:scene(50.0/60)); equal(actor.pose,held)
                for frame in 51...120 { actor.update(index:0,actors:scene(Double(frame)/60)) }
                require(actor.lookingAt == nil)
                actor.update(index:0,actors:scene(0)); equal(actor.pose,RacePerformance.Pose())
                require(actor.lookingAt == nil)
            }
            var parked = RacePerformance(character)
            for frame in 1...120 {
                parked.update(index:0,actors:[Actor(x:0,z:0,heading:0,speed:0,elapsed:Double(frame)/60),Actor(x:1,z:0,heading:0,speed:0,elapsed:Double(frame)/60)])
            }
            equal(parked.pose,RacePerformance.Pose()); require(parked.lookingAt == nil)
        }
    }
    func testFourRacerGrid() {
        struct Random: RandomNumberGenerator {
            var seed: UInt64 = 42
            mutating func next() -> UInt64 { seed = seed &* 6364136223846793005 &+ 1442695040888963407; return seed }
        }
        var rng = Random(), allocations = Set<String>()
        for _ in 0..<60 {
            let slots = DirtCourse.shuffledGrid(using:&rng)
            equal(Set(slots.map { $0.phase }).count,4)
            equal(Set(slots.map { $0.phase }),Set(DirtCourse.startingGrid.map { $0.phase }))
            allocations.insert(slots.map { String($0.phase) }.joined(separator:","))
            let racers = slots.map { Simulation(dirtTrack:true,dirtStartOffset:$0.offset,dirtStartPhase:$0.phase) }
            for i in 0..<4 { for j in 0..<i {
                greater(hypot(racers[i].x-racers[j].x,racers[i].z-racers[j].z),1.0)
            } }
        }
        greater(Double(allocations.count),12)
        for lane in [-0.65,0,0.65] {
            for slot in DirtCourse.startingGrid {
                var times: [Double] = []
                for fps in [30.0,60.0,120.0] {
                    var rival = DirtOpponent(slot:slot,laneOffset:lane)
                    for _ in 0..<Int(180*fps) {
                        rival.advance(dt:1/fps,raceDT:1/fps)
                        require(DirtCourse.projection(x:rival.simulation.x,z:rival.simulation.z).distance < DirtCourse.fenceOffset)
                        if rival.race.finished { break }
                    }
                    require(rival.race.finished); equal(rival.race.laps.count,3)
                    times.append(rival.race.elapsed)
                }
                near(times.min()!,times.max()!,accuracy:0.001)
            }
        }
    }
    func testBoostSteeringDuringAcceleration() {
        for fps in [30.0, 60.0, 120.0] {
            for throttle in [-1.0, 1.0] {
                for turn in [-1.0, 1.0] {
                    for rolling in [false, true] {
                        var sim = Simulation(dirtTrack: true), input = DriveInput()
                        input.throttle = throttle
                        if rolling {
                            for _ in 0..<Int(fps*0.4) { sim.advance(input, dt: 1/fps) }
                        }
                        let heading = sim.heading
                        input.boost = true; input.turn = turn
                        for _ in 0..<Int(fps*0.2) {
                            let left = sim.leftSpeed, right = sim.rightSpeed
                            sim.advance(input, dt: 1/fps)
                            require(abs(sim.leftSpeed-left) <= 11.4/fps+1e-9)
                            require(abs(sim.rightSpeed-right) <= 11.4/fps+1e-9)
                        }
                        let angle = atan2(sin(sim.heading-heading), cos(sim.heading-heading))
                        require(angle * turn < -0.04)
                        require(sim.speed * throttle > 0.5)
                        input.brake = true
                        sim.advance(input, dt: 1/fps)
                        equal(sim.leftSpeed, 0); equal(sim.rightSpeed, 0)
                    }
                }
            }
        }
    }
    func testDirtOpponent() {
        var finishTimes: [Double] = []
        for fps in [30.0, 60.0, 120.0] {
            var opponent = DirtOpponent()
            let start = opponent.simulation
            opponent.advance(dt: .nan, raceDT: 1)
            opponent.advance(dt: 1, raceDT: .infinity)
            equal(opponent.simulation.distance, 0)
            greater(hypot(start.x-DirtCourse.point(0).x, start.z-DirtCourse.point(0).z), 0.7)
            var airborne = false
            for _ in 0..<Int(fps*240) {
                opponent.advance(dt: 1/fps, raceDT: 1/fps)
                airborne = airborne || opponent.simulation.airborne
                require(DirtCourse.projection(x:opponent.simulation.x,z:opponent.simulation.z).distance < DirtCourse.fenceOffset)
                if opponent.race.finished { break }
            }
            require(opponent.race.finished); require(airborne)
            equal(opponent.race.laps.count,3); equal(opponent.simulation.speed,0)
            near(opponent.race.laps.reduce(0,+),opponent.race.elapsed,accuracy:1e-8)
            equal(opponent.playerPosition(DirtRace()),2)
            let end = opponent.simulation
            opponent.advance(dt: 1/fps, raceDT: 1/fps)
            equal(end.x,opponent.simulation.x); equal(end.z,opponent.simulation.z)
            finishTimes.append(opponent.race.elapsed)
            opponent = DirtOpponent()
            equal(opponent.race.elapsed,0); equal(opponent.simulation.x,start.x)
            equal(opponent.simulation.z,start.z)
        }
        near(finishTimes.max()!,finishTimes.min()!,accuracy:0.001)
        print("R2-D2 three-lap totals at 30/60/120 fps: \(finishTimes)")
    }
    func testWiderTerrainAndFence() {
        near(DirtCourse.width*2,3.9,accuracy:1e-12)
        let center = DirtCourse.surfacePoints(offset:0)
        let length = zip(center,center.dropFirst()).reduce(0.0) { total, pair in
            total+hypot(pair.1.x-pair.0.x,pair.1.y-pair.0.y)
        }
        // Regression baseline from the previous, narrower route.
        near(length,136.25642662849137,accuracy:1e-8)
        near(DirtCourse.elevation(0),0,accuracy:1e-12)
        greater(DirtCourse.elevation(0.69*2 * .pi),1.25)
        greater(DirtCourse.elevation(0.17*2 * .pi),0.6)
        greater(DirtCourse.elevation(0.40*2 * .pi),0.85)
        for i in 0..<160 {
            let phase = Double(i)*2 * .pi/160
            for edge in [DirtCourse.width,DirtCourse.width+DirtCourse.bermWidth,DirtCourse.shoulderEdge,DirtCourse.terrainEdge] {
                for side in [-1.0,1.0] {
                    near(DirtCourse.surfaceHeight(phase,offset:side*(edge-1e-7)),
                         DirtCourse.surfaceHeight(phase,offset:side*(edge+1e-7)),accuracy:1e-5)
                }
            }
        }
        func cross(_ a:SIMD2<Double>,_ b:SIMD2<Double>) -> Double { a.x*b.y-a.y*b.x }
        for offset in [-DirtCourse.terrainEdge,-DirtCourse.fenceOffset,-DirtCourse.width,
                       DirtCourse.width,DirtCourse.fenceOffset,DirtCourse.terrainEdge] {
            let points = DirtCourse.surfacePoints(offset:offset), count = DirtCourse.sampleCount
            equal(points.first,points.last)
            for i in 0..<count {
                let p = points[i]
                near(DirtCourse.projection(x:p.x,z:p.y).distance,abs(offset),accuracy:0.012)
                let r = points[i+1]-p
                if i+2 >= count { continue }
                for j in i+2..<count {
                    if i == 0 && j == count-1 { continue }
                    let q = points[j], v = points[j+1]-q, denominator = cross(r,v)
                    if abs(denominator) < 1e-12 { continue }
                    let t = cross(q-p,v)/denominator, u = cross(q-p,r)/denominator
                    require(!(t > 1e-7 && t < 1-1e-7 && u > 1e-7 && u < 1-1e-7))
                }
            }
        }
    }
    func testDirtRaceAndScores() {
        var race = DirtRace()
        let start = DirtCourse.point(0)
        race.advance(x:start.x,z:start.z,dt:1)
        equal(race.elapsed,0)
        race.countDown(dt:3)
        // Repeated forward/backward finish crossings cannot manufacture a lap.
        for _ in 0..<30 {
            for angle in [-0.02,0.02,0.0] {
                let p = DirtCourse.point(angle); race.advance(x:p.x,z:p.z,dt:0.1)
            }
        }
        equal(race.laps.count,0)
        race = DirtRace(); race.countDown(dt:3)
        for i in 1...1080 {
            let p = DirtCourse.point(Double(i)*2*Double.pi/360)
            race.advance(x:p.x,z:p.z,dt:0.1)
        }
        require(race.finished); equal(race.laps.count,3)
        for lap in race.laps { near(lap,36,accuracy:0.003) }
        near(race.elapsed,108,accuracy:0.003)
        race.advance(x:0,z:-5,dt:2); near(race.elapsed,108,accuracy:0.003)
        var reverse = DirtRace(); reverse.countDown(dt:3)
        for i in 1...400 {
            let p = DirtCourse.point(-Double(i)*0.02); reverse.advance(x:p.x,z:p.z,dt:0.1)
        }
        equal(reverse.laps.count,0); require(reverse.wrongWay)
        require(DirtCourse.contains(x:0,z:0)); require(!DirtCourse.contains(x:20,z:0))
        for i in 0..<360 {
            let p = DirtCourse.point(Double(i)*Double.pi/180)
            require(DirtCourse.contains(x:p.x,z:p.z,margin:Simulation.radius))
        }
        var sim = Simulation(dirtTrack:true), input = DriveInput(); input.throttle = 1
        for _ in 0..<1200 { sim.advance(input,dt:1.0/60) }
        require(DirtCourse.projection(x:sim.x,z:sim.z).distance < DirtCourse.fenceOffset); require(sim.contacting)
        sim.reset(); near(sim.z,-15,accuracy:1e-9); near(sim.heading,DirtCourse.heading(0),accuracy:1e-9)
        let edge = DirtCourse.point(0,offset:DirtCourse.width+0.05)
        let edgeMove = DirtCourse.resolveMove(x:edge.x,z:edge.z,heading:DirtCourse.heading(0))
        require(!edgeMove.contact)
        less(DirtCourse.traction(x:edge.x,z:edge.z),0.5)
        let outside = DirtCourse.point(0,offset:DirtCourse.fenceOffset+0.2)
        require(DirtCourse.resolveMove(x:outside.x,z:outside.z,heading:DirtCourse.heading(0)).contact)
        let retreat = DirtCourse.point(0,offset:DirtCourse.width-0.1)
        require(!DirtCourse.resolveMove(x:retreat.x,z:retreat.z,heading:DirtCourse.heading(0)).contact)
        var driver = Simulation(dirtTrack:true), drivenRace = DirtRace()
        drivenRace.countDown(dt:3)
        var maxHeight = 0.0, airborneFrames = 0
        for _ in 0..<12000 {
            if drivenRace.finished { break }
            let phase = DirtCourse.phase(x:driver.x,z:driver.z)
            let target = DirtCourse.point(phase+0.05)
            let angle = atan2(target.x-driver.x,target.z-driver.z)
            let error = atan2(sin(angle-driver.heading),cos(angle-driver.heading))
            var command = DriveInput(); command.throttle = max(0.15,1-abs(error)*1.5); command.boost = abs(error)<0.08
            command.turn = -error*3
            driver.advance(command,dt:1.0/60)
            drivenRace.advance(x:driver.x,z:driver.z,dt:1.0/60)
            maxHeight = max(maxHeight,driver.groundY)
            if driver.airborne { airborneFrames += 1 }
        }
        require(drivenRace.finished); greater(maxHeight,0.6); require(airborneFrames > 0)
        near(drivenRace.laps.reduce(0,+),drivenRace.elapsed,accuracy:1e-8)
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString).appendingPathComponent("scores.json")
        defer { try? FileManager.default.removeItem(at:url.deletingLastPathComponent()) }
        do {
            equal(try DirtScores.load(url).count,0)
            let scores = [DirtScore(laps:[40,39,38]),DirtScore(laps:[36,35,34]),DirtScore(laps:[0,1,2])]
            try DirtScores.save(scores,to:url)
            let loaded = try DirtScores.load(url)
            equal(loaded.count,2); near(loaded[0].total,105,accuracy:1e-9)
        } catch { fatalError("Score round trip failed: \(error)") }
    }
    func testForwardReverseAndBrake() {
        var sim = Simulation(), input = DriveInput()
        let start = sim.z
        input.throttle = 1
        for _ in 0..<60 { sim.advance(input, dt: 1.0/60) }
        greater(sim.z, start+0.8)
        input.brake = true
        let z = sim.z
        sim.advance(input, dt: 1.0/60)
        equal(sim.speed, 0)
        equal(sim.z, z)
        input.brake = false; input.throttle = -1
        for _ in 0..<60 { sim.advance(input, dt: 1.0/60) }
        less(sim.z, z-0.8)
    }
    func testTurningInPlace() {
        var sim = Simulation(), input = DriveInput()
        input.turn = 1
        for _ in 0..<30 { sim.advance(input, dt: 1.0/60) }
        less(sim.heading, -0.5)
        near(sim.x, 0, accuracy: 1e-9)
        near(sim.z, -2.6, accuracy: 1e-9)
    }
    func testCollisionAndCheckpoint() {
        var sim = Simulation(), input = DriveInput()
        input.throttle = 1; input.boost = true
        for _ in 0..<600 { sim.advance(input, dt: 1.0/60) }
        require(sim.contacting)
        require(sim.checkpoint >= 0 && sim.checkpoint <= CourseLayout.count)
        less(sim.z, 3.3-0.65/2-Simulation.radius+0.01)
        require(Simulation.isFree(x: sim.x, z: sim.z))
        require(!Simulation.isFree(x: 6, z: 0))
        require(!Simulation.isFree(x: -2.2, z: -0.4))
    }
    func testPauseResetAndHeadLimits() {
        var sim = Simulation(), input = DriveInput()
        input.throttle = 1; input.headYaw = 1; input.headPitch = -1
        sim.paused = true
        sim.advance(input, dt: 1)
        equal(sim.elapsed, 0)
        sim.paused = false
        for _ in 0..<300 { sim.advance(input, dt: 1.0/60) }
        require(sim.yaw < 0)
        require(HeadClearance.isClear(yaw: sim.yaw, pitch: sim.pitch))
        require(sim.pitch > -45 * .pi/180)
        sim.reset()
        equal(sim.distance, 0)
        equal(sim.yaw, 0)
        equal(sim.z, -2.6)
    }
    func testHeadClearanceSweepAndEscape() {
        require(HeadClearance.isClear(yaw: 0, pitch: 0))
        require(!HeadClearance.isClear(yaw: 0, pitch: -.pi/4))
        for direction in [-1.0, 0.0, 1.0] {
            var sim = Simulation(), input = DriveInput()
            input.headYaw = direction; input.headPitch = -1
            for _ in 0..<400 {
                sim.advance(input, dt: 1.0/60)
                require(HeadClearance.isClear(yaw: sim.yaw, pitch: sim.pitch))
            }
            let stopped = sim.pitch
            input.headYaw = -direction; input.headPitch = 1
            for _ in 0..<60 {
                sim.advance(input, dt: 1.0/60)
                require(HeadClearance.isClear(yaw: sim.yaw, pitch: sim.pitch))
            }
            greater(sim.pitch, stopped)
        }
        for direction in [-1.0, 1.0] {
            var sim = Simulation(), input = DriveInput()
            input.turn = direction; input.headYaw = direction
            for _ in 0..<30 { sim.advance(input, dt: 1.0/60) }
            require(sim.heading * direction < 0)
            require(sim.yaw * direction < 0)
        }
    }
    func testRandomCourseClearancesAndReset() {
        var layouts = Set<String>()
        for seed in UInt64(0)..<1000 {
            let sim = Simulation(seed: seed)
            equal(sim.checkpoints.count, 5)
            equal(sim.checkpoints, Simulation(seed: seed).checkpoints)
            layouts.insert(sim.checkpoints.map { "\($0.x),\($0.z)" }.joined(separator: ";"))
            for (i, p) in sim.checkpoints.enumerated() {
                require(CourseLayout.isClear(p, among: Array(sim.checkpoints.prefix(i))))
                require(Simulation.isFree(x: p.x, z: p.z))
                // Independently sample the full clearance perimeter, including
                // the maximum pulse, against walls, rectangles, and prior rings.
                for step in 0..<72 {
                    let angle = Double(step)*2*Double.pi/72
                    let r = CourseLayout.outerRadius+CourseLayout.clearance
                    let x = p.x+cos(angle)*r, z = p.z+sin(angle)*r
                    require(abs(x) < Simulation.halfWidth && abs(z) < Simulation.halfDepth)
                    for box in Simulation.obstacles+[CourseLayout.launch] {
                        require(abs(x-box.x) > box.width/2 || abs(z-box.z) > box.depth/2)
                    }
                    for other in sim.checkpoints.prefix(i) {
                        require(hypot(x-other.x, z-other.z) > CourseLayout.outerRadius)
                    }
                }
            }
        }
        equal(layouts.count, 1000)
        var sim = Simulation(seed: 42)
        let original = sim.checkpoints
        sim.reset()
        require(sim.checkpoints != original)
        equal(sim.checkpoint, 0)
        // Choose a reproducible first target in the unobstructed forward lane.
        let seed = (UInt64(0)..<1000).first {
            let p = Simulation(seed: $0).checkpoints[0]
            return abs(p.x) < 0.3 && p.z > -1 && p.z < 1
        }!
        sim = Simulation(seed: seed)
        var input = DriveInput(); input.throttle = 1
        for _ in 0..<240 { sim.advance(input, dt: 1.0/60) }
        require(sim.checkpoint >= 1)
    }
    func testCourseApproachRoutes() {
        // Direct approach needs no detour; an obstacle-crossing route must bend.
        equal(CourseRoute.path(from: Checkpoint(x: 0, z: -2.6), to: Checkpoint(x: 0, z: -1)).count, 2)
        greater(Double(CourseRoute.path(from: Checkpoint(x: -4, z: -0.4), to: Checkpoint(x: 0, z: -0.4)).count), 2)
        for seed in UInt64(0)..<30 {
            let course = Simulation(seed: seed).checkpoints
            var start = Checkpoint(x: 0, z: -2.6)
            for goal in course {
                let route = CourseRoute.path(from: start, to: goal)
                equal(route.first, start); equal(route.last, goal)
                for (a,b) in zip(route, route.dropFirst()) {
                    for step in 0...100 {
                        let t = Double(step)/100
                        require(Simulation.isFree(x: a.x+(b.x-a.x)*t, z: a.z+(b.z-a.z)*t))
                    }
                }
                let approach = route[route.count-2], yaw = CourseRoute.labelYaw(from: start, to: goal)
                let distance = hypot(approach.x-goal.x, approach.z-goal.z)
                near(sin(yaw), (approach.x-goal.x)/distance, accuracy: 1e-9)
                near(cos(yaw), (approach.z-goal.z)/distance, accuracy: 1e-9)
                start = goal
            }
        }
    }
    func testNeckConcentricDuringPan() {
        // Compare opposite points on the CAD's lower circular neck section.
        let left = SIMD3<Double>(-0.1325, 0.295, -0.01886)
        let right = SIMD3<Double>(0.1325, 0.295, -0.01886)
        for degrees in stride(from: -80.0, through: 80.0, by: 5.0) {
            let angle = degrees * .pi/180
            let a = HeadRig.headPoint(left, yaw: angle, pitch: 0)
            let b = HeadRig.headPoint(right, yaw: angle, pitch: 0)
            let center = (a+b)/2
            near(center.x, 0, accuracy: 1e-9)
            near(center.y, 0.295, accuracy: 1e-9)
            near(center.z, -0.01886, accuracy: 1e-9)
            near(hypot(a.x, a.z+0.01886), 0.1325, accuracy: 1e-9)
            require(HeadClearance.isClear(yaw: angle, pitch: 0))
        }
        let neutral = HeadRig.headPoint([0.2, 0.6, 0.3], yaw: 0, pitch: 0)
        near(neutral.x, 0.2, accuracy: 1e-9)
        near(neutral.y, 0.6, accuracy: 1e-9)
        near(neutral.z, 0.3, accuracy: 1e-9)
    }
    func testTrackTravelAndContact() {
        let phase = TrackLoop.straight/2
        let bottom = TrackLoop.sample(phase)
        near(bottom.y-0.0055, 0, accuracy: 1e-9) // outer rib touches the floor
        for throttle in [-1.0, 1.0] {
            var sim = Simulation(), input = DriveInput()
            input.throttle = throttle
            sim.advance(input, dt: 0.1)
            require(sim.leftTravel*throttle > 0 && sim.rightTravel*throttle > 0)
            let tread = TrackLoop.sample(phase+sim.leftTravel)
            // Ground-facing rubber travels opposite the chassis.
            require((tread.z-bottom.z)*throttle < 0)
            near(tread.z-bottom.z + sim.z+2.6, 0, accuracy: 1e-9)
            input.brake = true
            let stopped = sim.leftTravel
            sim.advance(input, dt: 0.1)
            equal(sim.leftTravel, stopped)
            sim.reset(); equal(sim.leftTravel, 0); equal(sim.rightTravel, 0)
        }
        for turn in [-1.0, 1.0] {
            var sim = Simulation(), input = DriveInput()
            input.turn = turn
            sim.advance(input, dt: 0.1)
            require(sim.leftTravel*turn > 0 && sim.rightTravel*turn < 0)
            require((TrackLoop.sample(phase+sim.leftTravel).z-bottom.z)*turn < 0)
            require((TrackLoop.sample(phase+sim.rightTravel).z-bottom.z)*turn > 0)
        }
        for boundary in [0, TrackLoop.straight, TrackLoop.straight + .pi*TrackLoop.radius,
                         2*TrackLoop.straight + .pi*TrackLoop.radius, TrackLoop.circumference] {
            let a = TrackLoop.sample(boundary-1e-7), b = TrackLoop.sample(boundary+1e-7)
            near(a.y, b.y, accuracy: 3e-7); near(a.z, b.z, accuracy: 3e-7)
        }
        for distance in [-10.0, -0.1, 0.0, 0.1, 10.0] {
            let a = TrackLoop.sample(distance), b = TrackLoop.sample(distance+TrackLoop.circumference)
            near(a.y, b.y, accuracy: 1e-9); near(a.z, b.z, accuracy: 1e-9)
        }
    }
    func testFrameRateIndependentAndStallBounded() {
        var a = Simulation(), b = Simulation(), input = DriveInput()
        input.throttle = 1; input.turn = 0.2
        for _ in 0..<30 { a.advance(input, dt: 1.0/30) }
        for _ in 0..<120 { b.advance(input, dt: 1.0/120) }
        near(a.x, b.x, accuracy: 0.0001)
        near(a.z, b.z, accuracy: 0.0001)
        let elapsed = a.elapsed
        a.advance(input, dt: 30)
        near(a.elapsed-elapsed, 0.1, accuracy: 1e-9)
    }
}

@main struct CheckRunner {
    static func main() {
        let checks = SimulationTests()
        checks.testStaggeredGrid()
        checks.testFourRacerGrid()
        checks.testRacePerformance()
        checks.testBoostSteeringDuringAcceleration()
        checks.testWiderTerrainAndFence()
        checks.testDirtOpponent()
        checks.testDirtRaceAndScores()
        checks.testForwardReverseAndBrake()
        checks.testTurningInPlace()
        checks.testCollisionAndCheckpoint()
        checks.testPauseResetAndHeadLimits()
        checks.testFrameRateIndependentAndStallBounded()
        checks.testHeadClearanceSweepAndEscape()
        checks.testTrackTravelAndContact()
        checks.testNeckConcentricDuringPan()
        checks.testRandomCourseClearancesAndReset()
        checks.testCourseApproachRoutes()
        print("PASS: 17 simulation checks (drive/brake, steering, collision/course, pause/head/reset, time integration)")
    }
}
