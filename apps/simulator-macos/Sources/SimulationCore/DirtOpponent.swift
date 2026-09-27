import Foundation

/// A deterministic look-ahead driver using the same drive, terrain and fence
/// physics as Marvin. No teleports, scripted lap awards or catch-up speed boost.
public struct DirtOpponent: Sendable {
    public private(set) var simulation = Simulation(seed: 0, dirtTrack: true, dirtStartOffset: DirtCourse.opponentGrid.offset, dirtStartPhase: DirtCourse.opponentGrid.phase)
    public private(set) var race = DirtRace(startPhase:DirtCourse.opponentGrid.phase)
    private var pendingTime = 0.0, pendingRaceTime = 0.0
    private let laneOffset: Double
    public init(slot: (phase: Double, offset: Double) = DirtCourse.opponentGrid, laneOffset: Double = 0.65) {
        simulation = Simulation(seed:0,dirtTrack:true,dirtStartOffset:slot.offset,dirtStartPhase:slot.phase)
        race = DirtRace(startPhase:slot.phase)
        self.laneOffset = laneOffset
        race.countDown(dt:3)
    }

    /// Caller gates this on the shared countdown, pause, focus and race finish.
    public mutating func advance(dt: Double, raceDT: Double) {
        guard !race.finished, dt.isFinite, dt > 0, raceDT.isFinite, raceDT > 0 else { return }
        // Evaluate steering at a fixed rate too, so frame rate does not alter
        // cornering decisions. Preserve wall-clock timing when frames stall.
        pendingTime += min(dt, 0.1)
        pendingRaceTime += raceDT
        let h = 1.0/120
        while pendingTime >= h-1e-10 && !race.finished {
            let clockStep = pendingRaceTime * min(1, h/pendingTime)
            pendingTime = max(0, pendingTime-h)
            pendingRaceTime = max(0, pendingRaceTime-clockStep)
            step(dt: h, raceDT: clockStep)
        }
    }

    private mutating func step(dt: Double, raceDT: Double) {
        let phase = DirtCourse.phase(x: simulation.x, z: simulation.z)
        let target = DirtCourse.point(phase+0.05, offset: laneOffset)
        let desired = atan2(target.x-simulation.x, target.z-simulation.z)
        let error = atan2(sin(desired-simulation.heading), cos(desired-simulation.heading))
        var input = DriveInput()
        input.throttle = max(0.15, 1-abs(error)*1.5)
        input.boost = abs(error) < 0.08
        input.turn = -error*3
        simulation.advance(input, dt: dt)
        race.advance(x: simulation.x, z: simulation.z, dt: raceDT)
        if race.finished { simulation.stop() }
    }

    public func playerPosition(_ player: DirtRace) -> Int {
        if player.finished && race.finished { return player.elapsed <= race.elapsed ? 1 : 2 }
        if player.finished { return 1 }
        if race.finished { return 2 }
        return player.progress >= race.progress ? 1 : 2
    }
}
