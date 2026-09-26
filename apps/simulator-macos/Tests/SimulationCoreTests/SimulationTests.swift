import Foundation
import SimulationCore

// Dependency-free checks also run with standalone Apple Command Line Tools.
func require(_ value: Bool, file: StaticString = #filePath, line: UInt = #line) {
    if !value { fatalError("Check failed", file: file, line: line) }
}
func equal<T: Equatable>(_ a: T, _ b: T, file: StaticString = #filePath, line: UInt = #line) {
    require(a == b, file: file, line: line)
}
func near(_ a: Double, _ b: Double, accuracy: Double, file: StaticString = #filePath, line: UInt = #line) {
    require(abs(a-b) <= accuracy, file: file, line: line)
}
func greater(_ a: Double, _ b: Double) { require(a > b) }
func less(_ a: Double, _ b: Double) { require(a < b) }

struct SimulationTests {
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
        require(!DirtCourse.contains(x:0,z:0)); require(!DirtCourse.contains(x:20,z:0))
        for i in 0..<360 {
            let p = DirtCourse.point(Double(i)*Double.pi/180)
            require(DirtCourse.contains(x:p.x,z:p.z,margin:Simulation.radius))
        }
        var sim = Simulation(dirtTrack:true), input = DriveInput(); input.throttle = 1
        for _ in 0..<1200 { sim.advance(input,dt:1.0/60) }
        require(DirtCourse.projection(x:sim.x,z:sim.z).distance < DirtCourse.fenceOffset); require(sim.contacting)
        sim.reset(); near(sim.z,-15,accuracy:1e-9); near(sim.heading,DirtCourse.heading(0),accuracy:1e-9)
        let edge = DirtCourse.point(0,offset:1.35)
        let edgeMove = DirtCourse.resolveMove(x:edge.x,z:edge.z,heading:DirtCourse.heading(0))
        require(!edgeMove.contact)
        less(DirtCourse.traction(x:edge.x,z:edge.z),0.5)
        let outside = DirtCourse.point(0,offset:2)
        require(DirtCourse.resolveMove(x:outside.x,z:outside.z,heading:DirtCourse.heading(0)).contact)
        let retreat = DirtCourse.point(0,offset:1.2)
        require(!DirtCourse.resolveMove(x:retreat.x,z:retreat.z,heading:DirtCourse.heading(0)).contact)
        for i in 0..<160 {
            let phase = Double(i)*2 * Double.pi/160
            for offset in [-1.8,1.8] {
                let p = DirtCourse.point(phase,offset:offset)
                near(DirtCourse.height(x:p.x,z:p.z),DirtCourse.surfaceHeight(phase,offset:offset),accuracy:0.012)
            }
        }
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
        print("PASS: 11 simulation checks (drive/brake, steering, collision/course, pause/head/reset, time integration)")
    }
}
