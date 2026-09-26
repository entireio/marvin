import Foundation

public struct Obstacle: Sendable {
    public let x: Double, z: Double, width: Double, depth: Double, height: Double
    public init(_ x: Double, _ z: Double, _ width: Double, _ depth: Double, _ height: Double) {
        self.x = x; self.z = z; self.width = width; self.depth = depth; self.height = height
    }
}

public struct DriveInput: Sendable {
    public var throttle = 0.0, turn = 0.0, headYaw = 0.0, headPitch = 0.0
    public var boost = false, brake = false
    public init() {}
}

/// Kinematic differential drive. One world unit is 100 mm; this is not a
/// calibrated motor, suspension, traction, or contact dynamics model.
public struct Simulation: Sendable {
    public static let halfWidth = 6.0, halfDepth = 5.0, radius = 0.53
    public static let obstacles = [
        Obstacle(-2.2, -0.4, 1.25, 1.25, 0.6),
        Obstacle(1.65, 0.3, 1.1, 1.65, 0.85),
        Obstacle(-3.55, 2.4, 1.0, 1.2, 0.4),
        Obstacle(3.5, -2.3, 1.2, 0.8, 0.45),
        Obstacle(0.0, 3.3, 1.6, 0.65, 0.35),
    ]
    public private(set) var checkpoints: [Checkpoint]
    public private(set) var x = 0.0, z = -2.6, heading = 0.0
    public private(set) var leftSpeed = 0.0, rightSpeed = 0.0
    public private(set) var leftTravel = 0.0, rightTravel = 0.0
    public private(set) var yaw = 0.0, pitch = 0.0, distance = 0.0, elapsed = 0.0
    public private(set) var checkpoint = 0
    public private(set) var contacting = false
    public var paused = false
    public var speed: Double { (leftSpeed + rightSpeed) / 2 }
    public var complete: Bool { checkpoint == checkpoints.count }
    public init(seed: UInt64 = UInt64.random(in: UInt64.min...UInt64.max)) {
        checkpoints = CourseLayout.generate(seed: seed)
    }

    public mutating func stop() { leftSpeed = 0; rightSpeed = 0 }
    public mutating func reset() { self = Simulation() }
    public mutating func centerHead() { yaw = 0; pitch = 0 }

    public static func isFree(x: Double, z: Double) -> Bool {
        let r = radius
        guard abs(x) <= halfWidth-r, abs(z) <= halfDepth-r else { return false }
        for o in obstacles {
            let closestX = max(o.x-o.width/2, min(x, o.x+o.width/2))
            let closestZ = max(o.z-o.depth/2, min(z, o.z+o.depth/2))
            if hypot(x-closestX, z-closestZ) < r { return false }
        }
        return true
    }

    public mutating func advance(_ input: DriveInput, dt: Double) {
        guard !paused, dt.isFinite, dt > 0 else { return }
        // Clamp delayed frames and subdivide to prevent tunneling through walls.
        let duration = min(dt, 0.1)
        let steps = Int(ceil(duration / (1.0/120)))
        let h = duration / Double(steps)
        contacting = false
        for _ in 0..<steps { step(input, dt: h) }
    }

    private mutating func step(_ input: DriveInput, dt: Double) {
        elapsed += dt
        let throttle = max(-1, min(1, input.throttle))
        let turn = max(-1, min(1, input.turn))
        let limit = input.boost ? 2.2 : 1.15
        let leftTarget = (throttle + turn * 0.65) * limit
        let rightTarget = (throttle - turn * 0.65) * limit
        let normalization = max(1, max(abs(leftTarget), abs(rightTarget)) / limit)
        func approach(_ value: Double, _ target: Double) -> Double {
            value + max(-3.8*dt, min(3.8*dt, target-value))
        }
        if input.brake { stop() }
        else {
            leftSpeed = approach(leftSpeed, leftTarget / normalization)
            rightSpeed = approach(rightSpeed, rightTarget / normalization)
        }
        // Marvin faces +Z: anatomical right is -X.
        let omega = (rightSpeed-leftSpeed) / 0.56
        let middleHeading = heading + omega * dt / 2
        let dx = sin(middleHeading) * speed * dt
        let dz = cos(middleHeading) * speed * dt
        if Self.isFree(x: x+dx, z: z+dz) {
            x += dx; z += dz; distance += hypot(dx, dz)
            leftTravel += leftSpeed * dt; rightTravel += rightSpeed * dt
        } else {
            contacting = true
            // Preserve steering at contact so the driver can turn away.
            leftTravel -= omega * 0.28 * dt; rightTravel += omega * 0.28 * dt
        }
        heading = atan2(sin(heading + omega*dt), cos(heading + omega*dt))
        let nextYaw = max(-80 * .pi/180, min(80 * .pi/180, yaw - input.headYaw * dt))
        let nextPitch = max(-45 * .pi/180, min(45 * .pi/180, pitch + input.headPitch * dt))
        // Check each small integration step on both axes. Reject only the blocked
        // axis so the driver can still tilt up or pan away from a contact.
        if HeadClearance.isClear(yaw: nextYaw, pitch: pitch) { yaw = nextYaw }
        if HeadClearance.isClear(yaw: yaw, pitch: nextPitch) { pitch = nextPitch }
        if !complete {
            let target = checkpoints[checkpoint]
            if hypot(x-target.x, z-target.z) < 0.7 { checkpoint += 1 }
        }
    }
}
