import AppKit
import SceneKit
import SimulationCore

extension AppController {
    func checkPlayableCharacters(at directory: URL) -> Bool {
        var results: [String: Bool] = [:]
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            for (key, character) in [("b", RacePerformance.Character.bb8), ("r", .r2d2), ("w", .wallE), ("m", .marvin)] {
                showMainMenu(nil)
                let event = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: 0,
                    windowNumber: window.windowNumber, context: nil, characters: key,
                    charactersIgnoringModifiers: key, isARepeat: false, keyCode: 0)!
                mainMenu.keyDown(with: event)
                startSandbox()
                let root = modelRoot(character)
                var passed = playerCharacter == character && simulation.character == character
                    && root.parent === world.scene.rootNode
                    && RacePerformance.Character.allCases.filter { modelRoot($0).parent === world.scene.rootNode }.count == 1
                var input = DriveInput(); input.throttle = 1; input.headYaw = 0.3; input.headPitch = 0.1
                for _ in 0..<45 { simulation.advance(input, dt: 1.0/60); updatePlayerModel() }
                passed = passed && simulation.distance > 0.1 && abs(Double(root.position.z)-simulation.z) < 1e-6
                updateCamera(snap: true)
                try saveCharacterFrame("\(key)-sandbox", at: directory)
                reset(nil)
                passed = passed && playerCharacter == character && simulation.character == character && simulation.distance == 0
                showMainMenu(nil)
                passed = passed && mainMenu.character == character && root.parent === mainMenu.stage.rootNode
                startDirtTrack()
                dirtIntro = nil; race.countDown(dt: 3)
                passed = passed && racePhysics.characters == lineup && Set(lineup).count == 4 && lineup[0] == character
                    && raceHUD.playerName == character.displayName
                    && raceHUD.racerNames == lineup.dropFirst().map { $0.displayName }
                for _ in 0..<120 {
                    let drive = DirtOpponent.driveInput(for: simulation)
                    advanceRacePhysics(drive, dt: 1.0/60, raceDT: 1.0/60)
                    updateOpponents(); updateRaceWorld(dt: 1.0/60)
                }
                let states = [simulation] + opponents.map { $0.simulation }
                passed = passed && simulation.distance > 0.1 && race.elapsed > 1.9
                for i in lineup.indices {
                    let node = modelRoot(lineup[i]), state = states[i]
                    passed = passed && node.parent === dirtWorld.scene.rootNode
                        && abs(Double(node.position.x)-state.x) < 1e-5
                        && abs(Double(node.position.z)-state.z) < 1e-5
                }
                passed = passed && dirtWorld.trailCounts.allSatisfy { $0 > 0 }
                updateCamera(snap: true)
                try saveCharacterFrame("\(key)-race", at: directory)
                reset(nil)
                passed = passed && playerCharacter == character && simulation.character == character
                    && racePhysics.characters == lineup && race.countdown == 3 && simulation.distance == 0
                results[character.displayName] = passed
            }
            try JSONSerialization.data(withJSONObject: results, options: [.prettyPrinted, .sortedKeys])
                .write(to: directory.appendingPathComponent("characters.json"))
        } catch { print("Character smoke failed: \(error)"); return false }
        return results.count == 4 && results.values.allSatisfy { $0 }
    }

    private func saveCharacterFrame(_ name: String, at directory: URL) throws {
        guard let data = view.snapshot().tiffRepresentation,
              let png = NSBitmapImageRep(data: data)?.representation(using: .png, properties: [:]) else {
            throw CocoaError(.fileWriteUnknown)
        }
        try png.write(to: directory.appendingPathComponent(name + ".png"))
    }
}
