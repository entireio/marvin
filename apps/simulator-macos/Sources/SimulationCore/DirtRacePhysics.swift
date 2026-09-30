import Foundation
import simd

/// Advances every competitor on one 240 Hz clock, then resolves contacts before
/// awarding race progress. The small steps bound relative travel below BB-8's
/// diameter even in opposing boosted impacts, including delayed display frames.
public struct DirtRacePhysics: Sendable {
    private var pendingTime = 0.0, pendingRaceTime = 0.0
    public private(set) var contactCount = 0
    private var recovery = Array(repeating:CollisionRecovery(),count:4)
    public var gate=CityGate()
    public var storm=Sandstorm()
    private var shelterSteps=0
    private func exposure(_ state:Simulation,city:CityCollisionWorld?)->Double {
        guard storm.enabled,let city else { return 1 }
        let wind=storm.wind(x:state.x,z:state.z),direction=wind/max(1,simd_length(wind))
        for distance in [2.0,5.0,9.0] {
            let point=SIMD3(state.x,state.groundY+0.3,state.z)-direction*distance
            let probe=RobotCollisions.Body(position:point,profile:.init(mass:1,halfWidth:0.3,halfDepth:0.3,height:0.3))
            for obstacle in city.nearby(probe) where obstacle.profile.height>1 && obstacle.profile.halfWidth>0.4 {
                if RobotCollisions.contact(probe,obstacle) != nil { return 0.25 }
            }
        }
        return 1
    }
    public private(set) var escape=PostRaceEscape()
    public let characters: [RacePerformance.Character]
    public init(characters: [RacePerformance.Character] = RacePerformance.Character.allCases,townRoutes:[[SIMD2<Double>]] = []) {
        precondition(characters.count == 4 && Set(characters).count == 4)
        self.characters = characters
        escape=PostRaceEscape(routes:townRoutes)
    }
    public mutating func advance(_ input: DriveInput, player: inout Simulation, race: inout DirtRace,
                                 opponents: inout [DirtOpponent], dt: Double, raceDT: Double,
                                 robotCollisionsEnabled: Bool = true, assists: DirtDrivingAssists = .off, city:CityCollisionWorld? = nil) {
        guard opponents.count == 3, !player.paused, race.countdown <= 0,
              dt.isFinite, dt > 0, raceDT.isFinite, raceDT > 0 else { return }
        pendingTime += min(0.1,dt); pendingRaceTime += raceDT
        let h = 1.0/240
        while pendingTime >= h-1e-10 {
            let clockStep = pendingRaceTime*min(1,h/pendingTime)
            pendingTime = max(0,pendingTime-h); pendingRaceTime = max(0,pendingRaceTime-clockStep)
            storm.advance(h)
            player.storm=storm
            player.aerodynamicProfile=RobotCollisions.profiles[characters[0].rawValue]
            if shelterSteps%24==0 {
                player.windShelter=exposure(player,city:city)
                for i in opponents.indices { opponents[i].simulation.windShelter=exposure(opponents[i].simulation,city:city) }
            }
            shelterSteps += 1
            player.enableRobotDynamics()
            let projection=DirtCourse.projection(x:player.x,z:player.z)
            let exitPosition=CityExit.local(SIMD2(player.x,player.z))
            let exploring=(projection.offset>0 && projection.distance>DirtCourse.fenceOffset) || ((gate.wantsOpen || gate.angle>0.02) && abs(exitPosition.x)<3 && exitPosition.y > -5 && exitPosition.y<8)
            let initialBodies=([player]+opponents.map{$0.simulation}).map{$0.collisionBody(profile:RobotCollisions.profiles[$0.character.rawValue])}
            let states=[player]+opponents.map{$0.simulation}
            escape.advance(races:[race]+opponents.map{$0.race},states:states,city:city,gateAngle:gate.angle)
            if escape.active { gate.wantsOpen=true }
            gate.advance(dt:h,bodies:initialBodies)
            let playerInput = escape.input(for:0,states:states) ?? (exploring ? input : race.finished ? DirtOpponent.driveInput(for:player,cruising:true) : assists.apply(input,to:player))
            player.advance(playerInput,dt:h)
            for i in opponents.indices {
                opponents[i].simulation.storm=storm
                opponents[i].simulation.aerodynamicProfile=RobotCollisions.profiles[characters[i+1].rawValue]
                opponents[i].simulation.enableRobotDynamics()
                let drive = escape.input(for:i+1,states:states) ?? opponents[i].driveInput
                opponents[i].simulation.advance(drive,dt:h)
            }
            var bodies = ([player]+opponents.map { $0.simulation }).enumerated().map { $0.element.collisionBody(profile:RobotCollisions.profiles[characters[$0.offset].rawValue]) }
            contactCount += RobotCollisions.resolve(&bodies,terrain:true,betweenRobots:robotCollisionsEnabled,gate:gate,city:city,previousPositions:initialBodies.map{$0.position},storm:storm)
            for i in bodies.indices {
                let p=DirtCourse.projection(x:bodies[i].position.x,z:bodies[i].position.z)
                if !escape.active && (i != 0 || !exploring) && !(p.offset>0 && p.distance>DirtCourse.fenceOffset) { recovery[i].advance(&bodies[i],dt:h) }
            }
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
