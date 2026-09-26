import Foundation

public struct Checkpoint: Equatable, Sendable {
    public let x: Double, z: Double
    public init(x: Double, z: Double) { self.x = x; self.z = z }
}

public enum CourseLayout {
    public static let count = 5
    public static let ringRadius = 0.54, pipeRadius = 0.018, pulseScale = 1.06
    public static let clearance = 0.15
    public static let outerRadius = (ringRadius + pipeRadius)*pulseScale
    public static let launch = Obstacle(0, -2.6, 1.35, 1.25, 0)

    public static func isClear(_ point: Checkpoint, among placed: [Checkpoint]) -> Bool {
        let radius = outerRadius + clearance
        guard abs(point.x)+radius < Simulation.halfWidth,
              abs(point.z)+radius < Simulation.halfDepth else { return false }
        for box in Simulation.obstacles + [launch] {
            let dx = max(0, abs(point.x-box.x)-box.width/2)
            let dz = max(0, abs(point.z-box.z)-box.depth/2)
            if hypot(dx, dz) <= radius { return false }
        }
        return placed.allSatisfy { hypot(point.x-$0.x, point.z-$0.z) > 2*outerRadius+clearance }
    }

    public static func generate(seed: UInt64) -> [Checkpoint] {
        var random = SeededRandom(state: seed)
        // Jittered candidates cover the arena, then shuffle before selecting.
        // Bounded work avoids an unbounded rejection loop on the main thread.
        var candidates: [Checkpoint] = []
        for x in -13...13 {
            for z in -10...10 {
                candidates.append(Checkpoint(x: Double(x)*0.4 + Double.random(in: -0.15...0.15, using: &random),
                                             z: Double(z)*0.4 + Double.random(in: -0.15...0.15, using: &random)))
            }
        }
        candidates.shuffle(using: &random)
        var placed: [Checkpoint] = []
        for point in candidates where isClear(point, among: placed) {
            placed.append(point)
            if placed.count == count { return placed }
        }
        preconditionFailure("Arena has insufficient room for five clear rings")
    }

    private struct SeededRandom: RandomNumberGenerator {
        var state: UInt64
        mutating func next() -> UInt64 {
            state &+= 0x9e3779b97f4a7c15
            var value = state
            value = (value ^ (value >> 30)) &* 0xbf58476d1ce4e5b9
            value = (value ^ (value >> 27)) &* 0x94d049bb133111eb
            return value ^ (value >> 31)
        }
    }
}
