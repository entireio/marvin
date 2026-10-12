using System;
using static Marvin.Core.Swift;

namespace Marvin.Core;

public struct Obstacle
{
    public readonly double x, z, width, depth, height;
    public Obstacle(double x, double z, double width, double depth, double height)
    {
        this.x = x; this.z = z; this.width = width; this.depth = depth; this.height = height;
    }
}

public struct DriveInput
{
    public double throttle = 0.0, turn = 0.0, headYaw = 0.0, headPitch = 0.0;
    public bool boost = false, brake = false;
    // Set only by the Dirt Track assist; preserves steering while braking.
    internal bool assistedBraking = false;
    internal double assistedBrakePressure = 0.0;
    public readonly bool isBraking => brake || assistedBrakePressure > 0;
    public DriveInput() { }
}

/// Differential drivetrain with optional rigid-body race motion. The sandbox
/// retains its kinematic behavior; DirtRacePhysics enables momentum and finite
/// traction. This is a game simulation, not calibrated hardware dynamics.
public struct Simulation
{
    public const double halfWidth = 6.0, halfDepth = 5.0;
    public static readonly double bodyHalfWidth = RobotCollisions.profiles[0].halfWidth;
    public static readonly double bodyHalfDepth = RobotCollisions.profiles[0].halfDepth;
    public const double collisionClearance = 0.006;
    // Conservative all-heading radius for route planning, not drive collision.
    public static readonly double radius = hypot(bodyHalfWidth + collisionClearance, bodyHalfDepth + collisionClearance);
    public static readonly Obstacle[] obstacles =
    {
        new Obstacle(-2.2, -0.4, 1.25, 1.25, 0.6),
        new Obstacle(1.65, 0.3, 1.1, 1.65, 0.85),
        new Obstacle(-3.55, 2.4, 1.0, 1.2, 0.4),
        new Obstacle(3.5, -2.3, 1.2, 0.8, 0.45),
        new Obstacle(0.0, 3.3, 1.6, 0.65, 0.35),
    };
    public bool dirtTrack { get; private set; } = false;
    public Sandstorm storm = new Sandstorm();
    public SandDeformation sand = null;
    public double windShelter = 1.0;
    internal RobotCollisions.Profile? aerodynamicProfile = null;
    public readonly double terrainHeight(double x, double z) => storm.height(x, z) + (sand?.offset(x, z) ?? 0);
    public readonly double supportHeight =>
        storm.height(x, z) + (sand?.supportOffset(x, z, heading, aerodynamicProfile ?? RobotCollisions.profiles[(int)character]) ?? 0);
    public readonly RacePerformance.Character character;
    public double groundY { get; private set; } = 0.0;
    public double bodyPitch { get; private set; } = 0.0;
    public double bodyRoll { get; private set; } = 0.0;
    public bool airborne { get; private set; } = false;
    private double dirtStartOffset = 0.0, dirtStartPhase = 0.0;
    public bool robotDynamics { get; private set; } = false;
    public Double3 velocity { get; private set; } = Double3.zero;
    public double angularVelocity { get; private set; } = 0.0;
    private double steering = 0.0;
    private double verticalSpeed = 0.0, previousGround = 0.0;
    public Checkpoint[] checkpoints { get; private set; }
    public double x { get; private set; } = 0.0;
    public double z { get; private set; } = -2.6;
    public double heading { get; private set; } = 0.0;
    public double leftSpeed { get; private set; } = 0.0;
    public double rightSpeed { get; private set; } = 0.0;
    public double leftTravel { get; private set; } = 0.0;
    public double rightTravel { get; private set; } = 0.0;
    /// Signed drivetrain travel in world X/Z, independent of chassis blockage
    /// and ground contact. Drives the spherical shell just like wheel travel.
    public Double2 rollingTravel { get; private set; } = Double2.zero;
    public double yaw { get; private set; } = 0.0;
    public double pitch { get; private set; } = 0.0;
    public double distance { get; private set; } = 0.0;
    public double elapsed { get; private set; } = 0.0;
    public int checkpoint { get; private set; } = 0;
    public bool contacting { get; private set; } = false;
    public long impactSerial { get; private set; } = 0;
    public double impactSpeed { get; private set; } = 0.0;
    private double impactTime = -1.0;
    public bool paused = false;
    /// The actual post-assist/autopilot command, shared with motion-driven audio.
    public DriveInput appliedDriveInput { get; private set; } = new DriveInput();
    /// Rubber/loose dirt contact tolerance for visual ground effects. The center
    /// height can flutter a few millimeters above terrain over shallow ripples;
    /// do not tear the trail each time the rigid-body airborne flag toggles.
    public readonly bool hasDirtContact => !airborne || (dirtTrack && groundY - supportHeight <= 0.006);
    /// Local support plane followed by heading; Euler XYZ would apply bank in
    /// world axes and tilt a diagonal chassis away from the slope it sampled.
    public readonly QuatD duneOrientation
    {
        get
        {
            var up = Simd.normalize(new Double3(-tan(bodyRoll), 1, tan(bodyPitch)));
            var forward = Simd.normalize(new Double3(0, -tan(bodyPitch), 1));
            var right = Simd.normalize(Simd.cross(up, forward));
            return new QuatD(heading, new Double3(0, 1, 0)) * new QuatD(new Double3x3(right, up, forward));
        }
    }
    public readonly double speed => (leftSpeed + rightSpeed) / 2;
    /// Chassis motion can differ from drivetrain speed during braking/sliding
    /// or when powered wheels spin against a barrier.
    public readonly double groundSpeed => robotDynamics ? hypot(velocity.x, velocity.z) : abs(speed);
    public readonly double forwardSpeed => robotDynamics ? velocity.x * sin(heading) + velocity.z * cos(heading) : speed;
    public readonly bool complete => checkpoint == checkpoints.Length;

    // PORT: Swift's default seed is UInt64.random(in: UInt64.min...UInt64.max); null selects it.
    // An explicit parameterless constructor keeps `new Simulation()` from zero-initializing.
    public Simulation() : this(null) { }
    public Simulation(ulong? seed = null, bool dirtTrack = false, double dirtStartOffset = 0, double dirtStartPhase = 0, RacePerformance.Character character = RacePerformance.Character.marvin)
    {
        var resolvedSeed = seed ?? SwiftRandom.uint64Closed(ulong.MinValue, ulong.MaxValue);
        this.character = character;
        this.dirtTrack = dirtTrack;
        this.dirtStartOffset = dirtStartOffset; this.dirtStartPhase = dirtStartPhase;
        if (dirtTrack) { var start = DirtCourse.point(dirtStartPhase, dirtStartOffset); x = start.x; z = start.z; heading = DirtCourse.heading(dirtStartPhase); groundY = DirtCourse.height(x, z); previousGround = groundY; }
        checkpoints = CourseLayout.generate(resolvedSeed);
    }

    public void stop() { appliedDriveInput = new DriveInput(); leftSpeed = 0; rightSpeed = 0; velocity = Double3.zero; angularVelocity = 0; }
    public void reset() { this = new Simulation(null, dirtTrack, dirtStartOffset, dirtStartPhase, character); }
    public void centerHead() { yaw = 0; pitch = 0; }

    internal void enableRobotDynamics()
    {
        if (robotDynamics) return;
        robotDynamics = true;
        velocity = new Double3(sin(heading) * speed, verticalSpeed, cos(heading) * speed);
        angularVelocity = (rightSpeed - leftSpeed) / 0.56;
    }
    internal readonly RobotCollisions.Body collisionBody(RobotCollisions.Profile profile) =>
        new RobotCollisions.Body(position: new Double3(x, groundY, z), velocity: velocity, heading: heading, angularVelocity: angularVelocity, profile: profile);
    internal void applyCollisionBody(RobotCollisions.Body body)
    {
        var change = Simd.length(velocity - body.velocity);
        if (body.contacted && change > 0.65)
        {
            impactSpeed = elapsed - impactTime < 0.1 ? max(impactSpeed, change) : change;
            impactTime = elapsed; impactSerial = unchecked(impactSerial + 1);
        }
        x = body.position.x; z = body.position.z; groundY = body.position.y;
        heading = atan2(sin(body.heading), cos(body.heading));
        velocity = body.velocity; angularVelocity = body.angularVelocity; verticalSpeed = velocity.y;
        var ground = supportHeight;
        airborne = groundY > ground + 0.001;
        previousGround = ground;
        contacting = contacting || body.contacted;
    }

    public static bool isFree(double x, double z)
    {
        var r = radius;
        if (!(abs(x) <= halfWidth - r && abs(z) <= halfDepth - r)) return false;
        foreach (var o in obstacles)
        {
            var closestX = max(o.x - o.width / 2, min(x, o.x + o.width / 2));
            var closestZ = max(o.z - o.depth / 2, min(z, o.z + o.depth / 2));
            if (hypot(x - closestX, z - closestZ) < r) return false;
        }
        return true;
    }

    /// Scaled oriented footprint against arena walls and axis-aligned obstacles.
    public static bool isFree(double x, double z, double heading, RacePerformance.Character character = RacePerformance.Character.marvin)
    {
        double c = cos(heading), s = sin(heading), ac = abs(c), asn = abs(s);
        var profile = RobotCollisions.profiles[(int)character];
        double w = profile.halfWidth + collisionClearance, d = profile.halfDepth + collisionClearance;
        double extentX = ac * w + asn * d, extentZ = asn * w + ac * d;
        if (!(abs(x) + extentX <= halfWidth && abs(z) + extentZ <= halfDepth)) return false;
        foreach (var o in obstacles)
        {
            double dx = x - o.x, dz = z - o.z, ow = o.width / 2, od = o.depth / 2;
            // Separating axes: both obstacle axes and both robot axes.
            if (abs(dx) >= extentX + ow || abs(dz) >= extentZ + od) continue;
            if (abs(dx * c - dz * s) >= w + ow * ac + od * asn) continue;
            if (abs(dx * s + dz * c) >= d + ow * asn + od * ac) continue;
            return false;
        }
        return true;
    }

    public void advance(DriveInput input, double dt)
    {
        if (!(!paused && double.IsFinite(dt) && dt > 0)) return;
        appliedDriveInput = input;
        // Clamp delayed frames and subdivide to prevent tunneling through walls.
        var duration = min(dt, 0.1);
        var steps = (int)ceil(duration / (1.0 / 120));
        var h = duration / (double)steps;
        contacting = false;
        for (var i = 0; i < steps; i++) step(input, h);
    }

    private void step(DriveInput input, double dt)
    {
        elapsed += dt;
        var throttle = max(-1, min(1, input.throttle));
        var turn = max(-1, min(1, input.turn));
        var depth = storm.depth(x, z);
        var grip = dirtTrack ? DirtCourse.traction(x, z) * max(0.55, 1 - depth * 2.5) : 1;
        var acceleration = dirtTrack ? 11.4 : 3.8;
        // The circuit shoulder penalty must not become a town-wide speed cap.
        // Loose sand still limits grip; accumulated drifts retain physical drag.
        var courseSpeed = dirtTrack && DirtCourse.projection(x, z).distance <= DirtCourse.fenceOffset ? DirtCourse.traction(x, z) : 1;
        var limit = dirtTrack ? (input.boost ? 12.0 : 6.0) * courseSpeed : (input.boost ? 2.2 : 1.15);
        // Dirt steering is an angular-rate request, independent of drive speed.
        // Ramp keyboard input so a short tap makes a small correction.
        steering += max(-3 * dt, min(3 * dt, turn - steering));
        var differential = dirtTrack ? steering * 0.28 * (abs(throttle) > 0 ? 1.15 : 1.4) : turn * 0.65 * limit;
        var leftTarget = throttle * limit + differential;
        var rightTarget = throttle * limit - differential;
        var normalization = max(1, max(abs(leftTarget), abs(rightTarget)) / limit);
        double approach(double value, double target) =>
            value + max(-acceleration * dt, min(acceleration * dt, target - value));
        var brakePressure = input.brake ? 1 : max(0, min(1, input.assistedBrakePressure));
        if (brakePressure > 0 && dirtTrack && input.assistedBraking)
        {
            // Keep steering available while braking. Never accelerate toward a
            // corner-speed target: pressure only removes forward momentum.
            var longitudinal = robotDynamics ? velocity.x * sin(heading) + velocity.z * cos(heading) : speed;
            var brakingSpeed = max(0, min(speed, longitudinal) - 16 * brakePressure * dt);
            leftSpeed = brakingSpeed + differential;
            rightSpeed = brakingSpeed - differential;
        }
        else if (input.brake) { leftSpeed = 0; rightSpeed = 0; }
        else if (dirtTrack)
        {
            // Reserve acceleration for the track-speed difference first. Two
            // independent saturated ramps erase steering while boost accelerates.
            var currentDifference = (leftSpeed - rightSpeed) / 2;
            var targetDifference = (leftTarget - rightTarget) / (2 * normalization);
            var difference = approach(currentDifference, targetDifference);
            var driveBudget = max(0, acceleration * dt - abs(difference - currentDifference));
            var targetSpeed = (leftTarget + rightTarget) / (2 * normalization);
            var driveSpeed = speed + max(-driveBudget, min(driveBudget, targetSpeed - speed));
            leftSpeed = driveSpeed + difference;
            rightSpeed = driveSpeed - difference;
        }
        else
        {
            leftSpeed = approach(leftSpeed, leftTarget / normalization);
            rightSpeed = approach(rightSpeed, rightTarget / normalization);
        }
        // Marvin faces +Z: anatomical right is -X.
        var omega = (rightSpeed - leftSpeed) / 0.56;
        var middleHeading = heading + omega * dt / 2;
        var dx = sin(middleHeading) * speed * dt;
        var dz = cos(middleHeading) * speed * dt;
        if (robotDynamics)
        {
            if (!airborne)
            {
                var forward = new Double3(sin(heading), 0, cos(heading));
                var side = new Double3(cos(heading), 0, -sin(heading));
                var longitudinal = velocity.x * forward.x + velocity.z * forward.z;
                var lateral = velocity.x * side.x + velocity.z * side.z;
                var driveLimit = brakePressure > 0 ? 16.0 : acceleration;
                double drive;
                if (brakePressure > 0 && input.assistedBraking)
                {
                    drive = -min(max(0, longitudinal) / dt, 16 * brakePressure);
                }
                else { drive = max(-driveLimit, min(driveLimit, (speed - longitudinal) * 20)); }
                var slip = max(-12.0, min(12.0, -lateral * 14));
                var tractionLimit = 16 * grip;
                var lateralForce = brakePressure > 0 && input.assistedBraking ? max(-tractionLimit, min(tractionLimit, slip)) : slip;
                var brakingBudget = sqrt(max(0, tractionLimit * tractionLimit - lateralForce * lateralForce));
                var longitudinalForce = brakePressure > 0 && input.assistedBraking ? max(-brakingBudget, min(brakingBudget, drive)) : drive;
                var force = forward * longitudinalForce + side * lateralForce;
                var magnitude = hypot(force.x, force.z);
                if (magnitude > tractionLimit) { force *= tractionLimit / magnitude; }
                if (dirtTrack && max(abs(x), abs(z)) > DesertTerrain.townEdge)
                {
                    var grade = DesertTerrain.gradient(x, z);
                    var downhill = -9.8 / (1 + grade.x * grade.x + grade.y * grade.y);
                    force += new Double3(grade.x * downhill, 0, grade.y * downhill);
                }
                if (storm.enabled)
                {
                    var e = 0.08;
                    var grade = new Double2((storm.depth(x + e, z) - storm.depth(x - e, z)) / (2 * e), (storm.depth(x, z + e) - storm.depth(x, z - e)) / (2 * e));
                    force += new Double3(-grade.x * 9.8, 0, -grade.y * 9.8);
                    force -= new Double3(velocity.x, 0, velocity.z) * (depth * 10);
                }
                velocity += force * dt;
                // Finite steering torque lets off-center impacts rotate a robot
                // before the drivetrain progressively regains heading control.
                angularVelocity += max(-7 * dt, min(7 * dt, omega - angularVelocity));
            }
            velocity += storm.acceleration(velocity, x, z, aerodynamicProfile ?? RobotCollisions.profiles[(int)character], windShelter) * dt;
            omega = angularVelocity;
            dx = velocity.x * dt; dz = velocity.z * dt;
        }
        if (dirtTrack)
        {
            var move = robotDynamics ? (x: x + dx, z: z + dz, contact: false)
                : DirtCourse.resolveMove(x + dx, z + dz, heading + omega * dt);
            distance += hypot(move.x - x, move.z - z); x = move.x; z = move.z;
            contacting = contacting || move.contact;
        }
        else if (isFree(x + dx, z + dz, heading + omega * dt, character))
        {
            x += dx; z += dz; distance += hypot(dx, dz);
        }
        else
        {
            contacting = true;
            // Allow turning away, but do not rotate a corner through an obstacle.
            if (!isFree(x, z, heading + omega * dt, character)) { omega = 0; }
        }
        // Contact constrains the chassis, not the powered running gear.
        leftTravel += leftSpeed * dt; rightTravel += rightSpeed * dt;
        var rollingHeading = heading + omega * dt / 2;
        rollingTravel += new Double2(sin(rollingHeading), cos(rollingHeading)) * speed * dt;
        heading = atan2(sin(heading + omega * dt), cos(heading + omega * dt));
        if (dirtTrack)
        {
            var ground = supportHeight;
            var slopeVelocity = (ground - previousGround) / dt;
            if (!airborne && verticalSpeed > slopeVelocity + 0.55 && abs(speed) > 1) { airborne = true; }
            if (airborne)
            {
                verticalSpeed -= 9.8 * dt; groundY += verticalSpeed * dt;
                if (groundY <= ground)
                {
                    groundY = ground; airborne = false;
                    if (robotDynamics)
                    {
                        // Landing on a rising bank must redirect the incoming
                        // momentum, not add an upward kick at unchanged speed.
                        velocity = RobotCollisions.landingVelocity(new Double3(velocity.x, verticalSpeed, velocity.z), storm.surfaceNormal(x, z));
                        verticalSpeed = velocity.y;
                    }
                    else { verticalSpeed = slopeVelocity; }
                }
            }
            else { groundY = ground; verticalSpeed = slopeVelocity; }
            previousGround = ground;
            // PORT: Swift `velocity.y = verticalSpeed` on a private(set) property.
            if (robotDynamics) { velocity = new Double3(velocity.x, verticalSpeed, velocity.z); }
            var dune = max(abs(x), abs(z)) > DesertTerrain.townEdge;
            var profile = aerodynamicProfile ?? RobotCollisions.profiles[(int)character];
            double halfLength = dune ? profile.halfDepth : 0.24, halfWidth = dune ? profile.halfWidth : 0.26;
            double fx = sin(heading) * halfLength, fz = cos(heading) * halfLength;
            double lx = cos(heading) * halfWidth, lz = -sin(heading) * halfWidth;
            var pitchTarget = -atan2(terrainHeight(x + fx, z + fz) - terrainHeight(x - fx, z - fz), halfLength * 2);
            var rollTarget = atan2(terrainHeight(x + lx, z + lz) - terrainHeight(x - lx, z - lz), halfWidth * 2);
            var blend = min(1, dt * 15);
            bodyPitch += (pitchTarget - bodyPitch) * blend; bodyRoll += (rollTarget - bodyRoll) * blend;
        }
        var nextYaw = max(-80 * Math.PI / 180, min(80 * Math.PI / 180, yaw - input.headYaw * dt));
        var nextPitch = max(-45 * Math.PI / 180, min(45 * Math.PI / 180, pitch + input.headPitch * dt));
        // Check each small integration step on both axes. Reject only the blocked
        // axis so the driver can still tilt up or pan away from a contact.
        if (HeadClearance.isClear(nextYaw, pitch)) { yaw = nextYaw; }
        if (HeadClearance.isClear(yaw, nextPitch)) { pitch = nextPitch; }
        if (!dirtTrack && !complete)
        {
            var target = checkpoints[checkpoint];
            if (hypot(x - target.x, z - target.z) < 0.7) { checkpoint += 1; }
        }
    }
}
