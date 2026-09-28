import Foundation

/// Advances every competitor on one 240 Hz clock, then resolves contacts before
/// awarding race progress. The small steps bound relative travel below BB-8's
/// diameter even in opposing boosted impacts, including delayed display frames.
public struct DirtRacePhysics: Sendable {
    private var pendingTime = 0.0, pendingRaceTime = 0.0
    public private(set) var contactCount = 0
    private var recovery = Array(repeating:CollisionRecovery(),count:4)
    public let characters: [RacePerformance.Character]
    public init(characters: [RacePerformance.Character] = RacePerformance.Character.allCases) {
        precondition(characters.count == 4 && Set(characters).count == 4)
        self.characters = characters
    }
    public mutating func advance(_ input: DriveInput, player: inout Simulation, race: inout DirtRace,
                                 opponents: inout [DirtOpponent], dt: Double, raceDT: Double,
                                 robotCollisionsEnabled: Bool = true, assists: DirtDrivingAssists = .off) {
        guard opponents.count == 3, !player.paused, race.countdown <= 0,
              dt.isFinite, dt > 0, raceDT.isFinite, raceDT > 0 else { return }
        pendingTime += min(0.1,dt); pendingRaceTime += raceDT
        let h = 1.0/240
        while pendingTime >= h-1e-10 {
            let clockStep = pendingRaceTime*min(1,h/pendingTime)
            pendingTime = max(0,pendingTime-h); pendingRaceTime = max(0,pendingRaceTime-clockStep)
            player.enableRobotDynamics()
            let playerInput = race.finished ? DirtOpponent.driveInput(for:player,cruising:true) : assists.apply(input,to:player)
            player.advance(playerInput,dt:h)
            for i in opponents.indices {
                opponents[i].simulation.enableRobotDynamics()
                let drive = opponents[i].driveInput
                opponents[i].simulation.advance(drive,dt:h)
            }
            var bodies = ([player]+opponents.map { $0.simulation }).enumerated().map { $0.element.collisionBody(profile:RobotCollisions.profiles[characters[$0.offset].rawValue]) }
            contactCount += RobotCollisions.resolve(&bodies,terrain:true,betweenRobots:robotCollisionsEnabled)
            for i in bodies.indices { recovery[i].advance(&bodies[i],dt:h) }
            player.applyCollisionBody(bodies[0])
            race.advance(x:player.x,z:player.z,dt:clockStep)
            for i in opponents.indices {
                opponents[i].simulation.applyCollisionBody(bodies[i+1])
                opponents[i].race.advance(x:opponents[i].simulation.x,z:opponents[i].simulation.z,dt:clockStep)
            }
        }
    }
}

/// Deliberately arcade-like yaw assistance after a contact. Leaves the initial
/// impact visible, then eases toward the local forward tangent without teleporting
/// or adding translational speed. The rigid-body solver itself stays physical.
public struct CollisionRecovery: Sendable {
    private var remaining = 0.0
    public init() {}
    public mutating func advance(_ body: inout RobotCollisions.Body, dt: Double) {
        guard dt.isFinite, dt > 0, body.contacted || remaining > 0 else { return }
        let phase = DirtCourse.phase(x:body.position.x,z:body.position.z)
        let error = atan2(sin(DirtCourse.heading(phase)-body.heading),cos(DirtCourse.heading(phase)-body.heading))
        if body.contacted && (abs(body.angularVelocity) > 1.2 || abs(error) > 0.6) { remaining = 3 }
        guard remaining > 0 else { return }
        remaining = max(0,remaining-dt)
        let target = max(-2.5,min(2.5,error*3.5))
        body.angularVelocity += (target-body.angularVelocity)*(1-exp(-10*dt))
    }
}
