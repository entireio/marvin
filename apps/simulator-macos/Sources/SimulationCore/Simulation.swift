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

/// Differential drivetrain with optional rigid-body race motion. The sandbox
/// retains its kinematic behavior; DirtRacePhysics enables momentum and finite
/// traction. This is a game simulation, not calibrated hardware dynamics.
public struct Simulation: Sendable {
    public static let halfWidth = 6.0, halfDepth = 5.0
    public static let bodyHalfWidth = RobotCollisions.profiles[0].halfWidth
    public static let bodyHalfDepth = RobotCollisions.profiles[0].halfDepth
    public static let collisionClearance = 0.006
    // Conservative all-heading radius for route planning, not drive collision.
    public static let radius = hypot(bodyHalfWidth+collisionClearance,bodyHalfDepth+collisionClearance)
    public static let obstacles = [
        Obstacle(-2.2, -0.4, 1.25, 1.25, 0.6),
        Obstacle(1.65, 0.3, 1.1, 1.65, 0.85),
        Obstacle(-3.55, 2.4, 1.0, 1.2, 0.4),
        Obstacle(3.5, -2.3, 1.2, 0.8, 0.45),
        Obstacle(0.0, 3.3, 1.6, 0.65, 0.35),
    ]
    public private(set) var dirtTrack = false
    public let character: RacePerformance.Character
    public private(set) var groundY = 0.0, bodyPitch = 0.0, bodyRoll = 0.0
    public private(set) var airborne = false
    private var dirtStartOffset = 0.0, dirtStartPhase = 0.0
    public private(set) var robotDynamics = false
    public private(set) var velocity = SIMD3<Double>.zero
    public private(set) var angularVelocity = 0.0
    private var steering = 0.0
    private var verticalSpeed = 0.0, previousGround = 0.0
    public private(set) var checkpoints: [Checkpoint]
    public private(set) var x = 0.0, z = -2.6, heading = 0.0
    public private(set) var leftSpeed = 0.0, rightSpeed = 0.0
    public private(set) var leftTravel = 0.0, rightTravel = 0.0
    public private(set) var yaw = 0.0, pitch = 0.0, distance = 0.0, elapsed = 0.0
    public private(set) var checkpoint = 0
    public private(set) var contacting = false
    public var paused = false
    /// Rubber/loose dirt contact tolerance for visual ground effects. The center
    /// height can flutter a few millimeters above terrain over shallow ripples;
    /// do not tear the trail each time the rigid-body airborne flag toggles.
    public var hasDirtContact: Bool {
        !airborne || (dirtTrack && groundY-DirtCourse.height(x:x,z:z) <= 0.006)
    }
    public var speed: Double { (leftSpeed + rightSpeed) / 2 }
    public var complete: Bool { checkpoint == checkpoints.count }
    public init(seed: UInt64 = UInt64.random(in: UInt64.min...UInt64.max), dirtTrack: Bool = false, dirtStartOffset: Double = 0, dirtStartPhase: Double = 0, character: RacePerformance.Character = .marvin) {
        self.character = character
        self.dirtTrack = dirtTrack
        self.dirtStartOffset = dirtStartOffset; self.dirtStartPhase = dirtStartPhase
        if dirtTrack { let start = DirtCourse.point(dirtStartPhase, offset: dirtStartOffset); x = start.x; z = start.z; heading = DirtCourse.heading(dirtStartPhase); groundY = DirtCourse.height(x:x,z:z); previousGround = groundY }
        checkpoints = CourseLayout.generate(seed: seed)
    }

    public mutating func stop() { leftSpeed = 0; rightSpeed = 0; velocity = .zero; angularVelocity = 0 }
    public mutating func reset() { self = Simulation(dirtTrack: dirtTrack, dirtStartOffset: dirtStartOffset, dirtStartPhase: dirtStartPhase, character: character) }
    public mutating func centerHead() { yaw = 0; pitch = 0 }

    mutating func enableRobotDynamics() {
        guard !robotDynamics else { return }
        robotDynamics = true
        velocity = SIMD3(sin(heading)*speed,verticalSpeed,cos(heading)*speed)
        angularVelocity = (rightSpeed-leftSpeed)/0.56
    }
    func collisionBody(profile: RobotCollisions.Profile) -> RobotCollisions.Body {
        RobotCollisions.Body(position:SIMD3(x,groundY,z),velocity:velocity,heading:heading,angularVelocity:angularVelocity,profile:profile)
    }
    mutating func applyCollisionBody(_ body: RobotCollisions.Body) {
        x = body.position.x; z = body.position.z; groundY = body.position.y
        heading = atan2(sin(body.heading),cos(body.heading))
        velocity = body.velocity; angularVelocity = body.angularVelocity; verticalSpeed = velocity.y
        let ground = DirtCourse.height(x:x,z:z)
        airborne = groundY > ground+0.001
        previousGround = ground
        contacting = contacting || body.contacted
    }

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

    /// Scaled oriented footprint against arena walls and axis-aligned obstacles.
    public static func isFree(x: Double, z: Double, heading: Double, character: RacePerformance.Character = .marvin) -> Bool {
        let c = cos(heading), s = sin(heading), ac = abs(c), asn = abs(s)
        let profile = RobotCollisions.profiles[character.rawValue]
        let w = profile.halfWidth+collisionClearance, d = profile.halfDepth+collisionClearance
        let extentX = ac*w+asn*d, extentZ = asn*w+ac*d
        guard abs(x)+extentX <= halfWidth, abs(z)+extentZ <= halfDepth else { return false }
        for o in obstacles {
            let dx = x-o.x, dz = z-o.z, ow = o.width/2, od = o.depth/2
            // Separating axes: both obstacle axes and both robot axes.
            if abs(dx) >= extentX+ow || abs(dz) >= extentZ+od { continue }
            if abs(dx*c-dz*s) >= w+ow*ac+od*asn { continue }
            if abs(dx*s+dz*c) >= d+ow*asn+od*ac { continue }
            return false
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
        let grip = dirtTrack ? DirtCourse.traction(x:x,z:z) : 1
        let acceleration = dirtTrack ? 11.4 : 3.8
        let limit = dirtTrack ? (input.boost ? 12.0 : 6.0)*grip : (input.boost ? 2.2 : 1.15)
        // Dirt steering is an angular-rate request, independent of drive speed.
        // Ramp keyboard input so a short tap makes a small correction.
        steering += max(-3*dt,min(3*dt,turn-steering))
        let differential = dirtTrack ? steering * 0.28 * (abs(throttle) > 0 ? 1.15 : 1.4) : turn*0.65*limit
        let leftTarget = throttle*limit + differential
        let rightTarget = throttle*limit - differential
        let normalization = max(1, max(abs(leftTarget), abs(rightTarget)) / limit)
        func approach(_ value: Double, _ target: Double) -> Double {
            value + max(-acceleration*dt, min(acceleration*dt, target-value))
        }
        if input.brake { leftSpeed = 0; rightSpeed = 0 }
        else if dirtTrack {
            // Reserve acceleration for the track-speed difference first. Two
            // independent saturated ramps erase steering while boost accelerates.
            let currentDifference = (leftSpeed-rightSpeed)/2
            let targetDifference = (leftTarget-rightTarget)/(2*normalization)
            let difference = approach(currentDifference, targetDifference)
            let driveBudget = max(0, acceleration*dt-abs(difference-currentDifference))
            let targetSpeed = (leftTarget+rightTarget)/(2*normalization)
            let driveSpeed = speed + max(-driveBudget, min(driveBudget, targetSpeed-speed))
            leftSpeed = driveSpeed+difference
            rightSpeed = driveSpeed-difference
        } else {
            leftSpeed = approach(leftSpeed, leftTarget / normalization)
            rightSpeed = approach(rightSpeed, rightTarget / normalization)
        }
        // Marvin faces +Z: anatomical right is -X.
        var omega = (rightSpeed-leftSpeed) / 0.56
        let middleHeading = heading + omega * dt / 2
        var dx = sin(middleHeading) * speed * dt
        var dz = cos(middleHeading) * speed * dt
        if robotDynamics {
            if !airborne {
                let forward = SIMD3<Double>(sin(heading),0,cos(heading))
                let side = SIMD3<Double>(cos(heading),0,-sin(heading))
                let longitudinal = velocity.x*forward.x+velocity.z*forward.z
                let lateral = velocity.x*side.x+velocity.z*side.z
                let driveLimit = input.brake ? 16.0 : acceleration
                let drive = max(-driveLimit,min(driveLimit,(speed-longitudinal)*20))
                let slip = max(-12.0,min(12.0,-lateral*14))
                var force = forward*drive+side*slip
                let magnitude = hypot(force.x,force.z), tractionLimit = 16*grip
                if magnitude > tractionLimit { force *= tractionLimit/magnitude }
                velocity += force*dt
                // Finite steering torque lets off-center impacts rotate a robot
                // before the drivetrain progressively regains heading control.
                angularVelocity += max(-7*dt,min(7*dt,omega-angularVelocity))
            }
            omega = angularVelocity
            dx = velocity.x*dt; dz = velocity.z*dt
        }
        if dirtTrack {
            let move = robotDynamics ? (x:x+dx,z:z+dz,contact:false)
                : DirtCourse.resolveMove(x:x+dx,z:z+dz,heading:heading+omega*dt)
            distance += hypot(move.x-x,move.z-z); x = move.x; z = move.z
            contacting = contacting || move.contact
            leftTravel += leftSpeed*dt; rightTravel += rightSpeed*dt
        } else if Self.isFree(x: x+dx, z: z+dz, heading:heading+omega*dt, character:character) {
            x += dx; z += dz; distance += hypot(dx, dz)
            leftTravel += leftSpeed * dt; rightTravel += rightSpeed * dt
        } else {
            contacting = true
            // Allow turning away, but do not rotate a corner through an obstacle.
            if !Self.isFree(x:x,z:z,heading:heading+omega*dt, character:character) { omega = 0 }
            leftTravel -= omega * 0.28 * dt; rightTravel += omega * 0.28 * dt
        }
        heading = atan2(sin(heading + omega*dt), cos(heading + omega*dt))
        if dirtTrack {
            let ground = DirtCourse.height(x:x,z:z)
            let slopeVelocity = (ground-previousGround)/dt
            if !airborne && verticalSpeed > slopeVelocity+0.55 && abs(speed) > 1 { airborne = true }
            if airborne {
                verticalSpeed -= 9.8*dt; groundY += verticalSpeed*dt
                if groundY <= ground { groundY = ground; airborne = false; verticalSpeed = slopeVelocity }
            } else { groundY = ground; verticalSpeed = slopeVelocity }
            previousGround = ground
            if robotDynamics { velocity.y = verticalSpeed }
            let fx = sin(heading)*0.24, fz = cos(heading)*0.24
            let lx = cos(heading)*0.26, lz = -sin(heading)*0.26
            let pitchTarget = -atan2(DirtCourse.height(x:x+fx,z:z+fz)-DirtCourse.height(x:x-fx,z:z-fz),0.48)
            let rollTarget = atan2(DirtCourse.height(x:x+lx,z:z+lz)-DirtCourse.height(x:x-lx,z:z-lz),0.52)
            let blend = min(1,dt*15)
            bodyPitch += (pitchTarget-bodyPitch)*blend; bodyRoll += (rollTarget-bodyRoll)*blend
        }
        let nextYaw = max(-80 * .pi/180, min(80 * .pi/180, yaw - input.headYaw * dt))
        let nextPitch = max(-45 * .pi/180, min(45 * .pi/180, pitch + input.headPitch * dt))
        // Check each small integration step on both axes. Reject only the blocked
        // axis so the driver can still tilt up or pan away from a contact.
        if HeadClearance.isClear(yaw: nextYaw, pitch: pitch) { yaw = nextYaw }
        if HeadClearance.isClear(yaw: yaw, pitch: nextPitch) { pitch = nextPitch }
        if !dirtTrack && !complete {
            let target = checkpoints[checkpoint]
            if hypot(x-target.x, z-target.z) < 0.7 { checkpoint += 1 }
        }
    }
}
