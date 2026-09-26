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
        equal(sim.checkpoint, 1)
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
        checks.testForwardReverseAndBrake()
        checks.testTurningInPlace()
        checks.testCollisionAndCheckpoint()
        checks.testPauseResetAndHeadLimits()
        checks.testFrameRateIndependentAndStallBounded()
        checks.testHeadClearanceSweepAndEscape()
        print("PASS: 6 simulation checks (drive/brake, steering, collision/course, pause/head/reset, time integration)")
    }
}
