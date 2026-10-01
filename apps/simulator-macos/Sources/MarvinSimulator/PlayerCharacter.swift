import AppKit
import SceneKit
import SimulationCore

extension RacePerformance.Character {
    var displayName: String { ["Marvin", "R2-D2", "BB-8", "WALL-E"][rawValue] }
    var tint: UInt32 { [Robot.silverColor, 0x58baff, 0xff914b, 0xf2c94c][rawValue] }
    /// Keep the existing opponent slots, exchanging the chosen robot and Marvin.
    var lineup: [Self] {
        var order = Self.allCases
        order.swapAt(0, rawValue)
        return order
    }
}

extension AppController {
    var lineup: [RacePerformance.Character] { playerCharacter.lineup }
    func modelRoot(_ character: RacePerformance.Character) -> SCNNode {
        switch character {
        case .marvin: return robot.root
        case .r2d2: return r2d2.root
        case .bb8: return bb8.root
        case .wallE: return wallE.root
        }
    }
    func updateModel(_ character: RacePerformance.Character, state: Simulation,
                     expression: RacePerformance.Pose = RacePerformance.Pose()) {
        var pose = expression
        pose.yaw += state.yaw; pose.pitch += state.pitch
        switch character {
        case .marvin:
            robot.update(state); robot.applyExpression(expression, state: state)
        case .r2d2:
            r2d2.update(state); r2d2.applyExpression(pose)
        case .bb8:
            bb8.update(state); bb8.applyExpression(pose, heading: state.heading)
        case .wallE:
            wallE.update(state); wallE.applyExpression(pose, heading: state.heading)
        }
    }
    func updatePlayerModel() { updateModel(playerCharacter, state: simulation) }

    func configurePlayer() {
        playerCharacter = mainMenu.character
        raceHUD.playerName = playerCharacter.displayName
        raceHUD.playerColor = playerCharacter.tint
        raceHUD.racerNames = lineup.dropFirst().map { $0.displayName }
        raceHUD.racerColors = lineup.dropFirst().map { $0.tint }
    }

    func updateRaceWorld(dt: Double) {
        dirtWorld.sky.updateShadowCenter(raceCameraLocked ? .zero:SIMD3(simulation.x,simulation.groundY,simulation.z))
        // Effects stay in model order so tires/tracks and emitter counts match
        // their geometry, independent of who occupies the player slot.
        let states = [simulation] + opponents.map { $0.simulation }
        let canonical = RacePerformance.Character.allCases.map { states[lineup.firstIndex(of: $0)!] }
        dirtWorld.update(canonical[0], opponent: canonical[1], dt: dt,
                         modelScale: robot.modelScale, additional: [canonical[2], canonical[3]])
    }
}
