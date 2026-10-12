import Foundation

/// A deterministic look-ahead driver using the same drive, terrain and fence
/// physics as Marvin. No teleports, scripted lap awards or catch-up speed boost.
public struct DirtOpponent: Sendable {
    public internal(set) var simulation = Simulation(seed: 0, dirtTrack: true, dirtStartOffset: DirtCourse.opponentGrid.offset, dirtStartPhase: DirtCourse.opponentGrid.phase)
    public internal(set) var race = DirtRace(startPhase:DirtCourse.opponentGrid.phase)
    private var pendingTime = 0.0, pendingRaceTime = 0.0
    private let laneOffset: Double
    public init(slot: (phase: Double, offset: Double) = DirtCourse.opponentGrid, laneOffset: Double = 0.65) {
        simulation = Simulation(seed:0,dirtTrack:true,dirtStartOffset:slot.offset,dirtStartPhase:slot.phase)
        race = DirtRace(startPhase:slot.phase)
        self.laneOffset = laneOffset
        race.countDown(dt:3)
    }

    /// Caller gates this on the shared countdown, pause and focus. Finishers keep circulating at cooldown speed.
    public mutating func advance(dt: Double, raceDT: Double) {
        guard dt.isFinite, dt > 0, raceDT.isFinite, raceDT > 0 else { return }
        // Evaluate steering at a fixed rate too, so frame rate does not alter
        // cornering decisions. Preserve wall-clock timing when frames stall.
        pendingTime += min(dt, 0.1)
        pendingRaceTime += raceDT
        let h = 1.0/120
        while pendingTime >= h-1e-10 {
            let clockStep = pendingRaceTime * min(1, h/pendingTime)
            pendingTime = max(0, pendingTime-h)
            pendingRaceTime = max(0, pendingRaceTime-clockStep)
            step(dt: h, raceDT: clockStep)
        }
    }

    public var driveInput: DriveInput { Self.driveInput(for:simulation,laneOffset:laneOffset,cruising:race.finished) }

    public static func driveInput(for simulation: Simulation, laneOffset: Double = 0, cruising: Bool = false) -> DriveInput {
        let projection=DirtCourse.projection(x:simulation.x,z:simulation.z)
        let phase=projection.phase
        // A rival nudged through the open exit returns via the doorway instead
        // of steering into the retaining wall from the city side.
        let outside=projection.offset>0 && projection.distance>DirtCourse.fenceOffset
        let q=CityExit.local(SIMD2(simulation.x,simulation.z))
        let reentry=abs(q.x)>0.45 ? CityExit.point(q.y<3 ? q.x:0,3.5):CityExit.point(0,-1.25)
        let target=outside ? (x:reentry.x,z:reentry.y):DirtCourse.point(phase+0.05,offset:laneOffset)
        let desired = atan2(target.x-simulation.x, target.z-simulation.z)
        let error = atan2(sin(desired-simulation.heading), cos(desired-simulation.heading))
        var input = DriveInput()
        input.throttle = outside ? (abs(error)<0.25 ? 0.55:0):max(0.15, 1-abs(error)*1.5)
        input.boost = !outside && !cruising && abs(error) < 0.08
        if cruising { input.throttle *= 0.65 }
        if simulation.storm.enabled && !outside {
            // Preview deposits on the actual lane before reaching their crest.
            // Airborne robots cannot steer; blindly boosting here can launch a
            // light chassis over the closed exit gate.
            let depth=(0...6).map { sample -> Double in
                let p=DirtCourse.point(phase+Double(sample)*0.018,offset:laneOffset)
                return simulation.storm.depth(x:p.x,z:p.z)
            }.max() ?? 0
            let pace=max(3.2,5.2-depth*18)
            input.boost=false
            input.throttle=min(input.throttle,pace/6)
            input.assistedBrakePressure=max(0,min(1,(simulation.groundSpeed-pace)*0.65))
            input.assistedBraking=input.assistedBrakePressure>0
        }
        input.turn = -error*3
        return input
    }

    private mutating func step(dt: Double, raceDT: Double) {
        simulation.advance(driveInput, dt: dt)
        race.advance(x: simulation.x, z: simulation.z, dt: raceDT)
    }

    public func playerPosition(_ player: DirtRace) -> Int {
        if player.finished && race.finished { return player.elapsed <= race.elapsed ? 1 : 2 }
        if player.finished { return 1 }
        if race.finished { return 2 }
        return player.progress >= race.progress ? 1 : 2
    }
}
