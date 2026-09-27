import Foundation

/// Race-only acting. It observes driving without changing steering or physics.
/// All timing uses the simulation clock so focus loss, pause and braking do not
/// produce wall-clock animation jumps.
public struct RacePerformance: Sendable {
    public enum Character: Int, Sendable { case marvin, r2d2, bb8, wallE }
    public struct Actor: Sendable {
        public let x: Double, z: Double, heading: Double, speed: Double, elapsed: Double
        public init(x: Double, z: Double, heading: Double, speed: Double, elapsed: Double) {
            self.x = x; self.z = z; self.heading = heading; self.speed = speed; self.elapsed = elapsed
        }
        public init(_ state: Simulation) {
            self.init(x:state.x,z:state.z,heading:state.heading,speed:state.speed,elapsed:state.elapsed)
        }
    }
    public struct Pose: Sendable, Equatable {
        public var yaw = 0.0, pitch = 0.0, roll = 0.0
        public var leftArm = 0.0, rightArm = 0.0, focus = 0.0
        public init() {}
    }
    public let character: Character
    public private(set) var pose = Pose()
    public private(set) var lookingAt: Int?
    public private(set) var curveYaw = 0.0
    private var lastTime = 0.0, glanceEnd = 0.0, nextGlance = 0.0
    private var previousAlong: [Int: Double] = [:]
    private var nearby = Set<Int>()
    public init(_ character: Character) { self.character = character }
    private func angle(_ value: Double) -> Double { atan2(sin(value),cos(value)) }
    private func clamp(_ value: Double, _ limit: Double) -> Double { max(-limit,min(limit,value)) }

    public mutating func update(index: Int, actors: [Actor]) {
        guard actors.indices.contains(index) else { return }
        let me = actors[index]
        if me.elapsed < lastTime || me.elapsed == 0 { self = Self(character); return }
        let dt = min(0.1,max(0,me.elapsed-lastTime))
        guard dt > 0 else { return }
        lastTime = me.elapsed
        let moving = min(1,abs(me.speed)/0.8)
        // Look beyond the driver's immediate steering target into the bend.
        let phase = DirtCourse.phase(x:me.x,z:me.z)
        let ahead = 0.10+min(12,abs(me.speed))*0.012
        curveYaw = me.speed > 0 ? clamp(angle(DirtCourse.heading(phase+ahead)-me.heading),0.8)*moving : 0
        let maxYaw = [0.95,1.65,1.25,1.15][character.rawValue]
        var yaw = curveYaw, glance = 0.0
        var candidates: [(index:Int,distance:Double)] = [], newNearby = Set<Int>()
        for (i,other) in actors.enumerated() where i != index {
            let dx = other.x-me.x, dz = other.z-me.z
            let along = dx*sin(me.heading)+dz*cos(me.heading)
            let side = dx*cos(me.heading)-dz*sin(me.heading)
            let distance = hypot(dx,dz), bearing = angle(atan2(dx,dz)-me.heading)
            let relative = previousAlong[i].map { abs(along-$0)/dt } ?? 0
            let close = distance < 2.8 && abs(along) < 1.9 && abs(side) > 0.28 && abs(bearing) < 2.15
            if close {
                newNearby.insert(i)
                if !nearby.contains(i), moving > 0.3, relative > 0.15 || abs(other.speed-me.speed) > 0.2 {
                    candidates.append((i,distance))
                }
            }
            previousAlong[i] = along
        }
        nearby = newNearby
        if me.elapsed >= glanceEnd { lookingAt = nil }
        if lookingAt == nil, me.elapsed >= nextGlance, let target = candidates.min(by: { $0.distance < $1.distance }) {
            lookingAt = target.index; glanceEnd = me.elapsed+0.85; nextGlance = me.elapsed+2.6
        }
        if let target = lookingAt, actors.indices.contains(target) {
            let other = actors[target], dx = other.x-me.x, dz = other.z-me.z
            let bearing = angle(atan2(dx,dz)-me.heading)
            if hypot(dx,dz) < 3.5 && abs(bearing) < 2.35 {
                yaw = clamp(bearing,maxYaw)
                glance = sin(.pi*max(0,min(1,(glanceEnd-me.elapsed)/0.85)))
            } else { lookingAt = nil }
        }
        var target = Pose(); target.yaw = clamp(yaw,maxYaw)
        target.focus = min(1,abs(curveYaw)*1.2)
        switch character {
        case .marvin:
            target.pitch = -0.035*target.focus+0.07*glance
        case .r2d2:
            // A dome can swivel; no invented neck tilt or roll.
            break
        case .bb8:
            target.pitch = 0.025*moving+0.065*glance
            target.roll = -0.10*curveYaw+0.12*glance*(yaw < 0 ? -1 : 1)
        case .wallE:
            target.pitch = -0.035*target.focus+0.09*glance
            target.roll = -0.07*curveYaw+0.09*glance*(yaw < 0 ? -1 : 1)
            // Raise the inside hand to indicate a bend; a brief greeting while
            // alongside is layered onto that side, rather than constant waving.
            target.leftArm = max(0,curveYaw)*0.85+(yaw > 0 ? glance*0.35 : 0)
            target.rightArm = max(0,-curveYaw)*0.85+(yaw < 0 ? glance*0.35 : 0)
        }
        let blend = 1-exp(-dt*(character == .r2d2 ? 5.5 : 8))
        pose.yaw += (target.yaw-pose.yaw)*blend
        pose.pitch += (target.pitch-pose.pitch)*blend
        pose.roll += (target.roll-pose.roll)*blend
        pose.leftArm += (target.leftArm-pose.leftArm)*blend
        pose.rightArm += (target.rightArm-pose.rightArm)*blend
        pose.focus += (target.focus-pose.focus)*blend
    }
}
