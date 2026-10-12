// SceneKit twin of the Godot `--robot-closeups DIR` mode (scripts/Sandbox/RobotCloseups.cs).
//
// The macOS smoke test takes its robot close-ups on the dirt track (DirtWorld lighting, race poses), so they
// cannot be compared with a sandbox-only port. This tool compiles the macOS game's own robot and sandbox
// sources (Robot, R2D2, ImportedRacer, TrackBelt, DirtCoating, World, FloorGroove) with SimulationCore, without
// modifying them, and renders each robot alone at the sandbox dock with SceneKit, from the smoke test's
// robot-relative cameras and a few more (face, rear, running gear, top), clean and after the smoke test's
// 1800-frame dusty drive. Keep this file and RobotCloseups.cs in step.
//
//   apps/simulator-godot/tools/scenekit-reference/robots/build.sh /tmp/robot-closeups-ref
//   /tmp/robot-closeups-ref/RobotCloseups OUT_DIR [RESOURCES_DIR]   (default: the Godot project's synced assets/)
//   apps/simulator-godot/tools/godot -- --robot-closeups GODOT_DIR
import AppKit
import SceneKit
import Metal
import SimulationCore

@main struct RobotCloseups {
    static func main() throws {
        let args = CommandLine.arguments
        guard args.count >= 2 else { print("usage: RobotCloseups OUT_DIR [RESOURCES_DIR]"); exit(1) }
        let outDir = URL(fileURLWithPath: args[1])
        try FileManager.default.createDirectory(at: outDir, withIntermediateDirectories: true)
        let here = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
        let resources = args.count > 2 ? URL(fileURLWithPath: args[2])
            : here.appendingPathComponent("../../../assets").standardizedFileURL

        let world = World()
        let robot = try Robot(resources: resources)
        let r2d2 = try R2D2(resources: resources)
        let bb8 = try ImportedRacer(kind: .bb8, resources: resources)
        let wallE = try ImportedRacer(kind: .wallE, resources: resources)
        // AppController.startSandbox(): LDR, no bloom, no SSAO, zFar 80.
        world.camera.camera?.zFar = 80
        world.camera.camera?.wantsHDR = false; world.camera.camera?.bloomIntensity = 0
        world.camera.camera?.screenSpaceAmbientOcclusionIntensity = 0
        // CLOSEUP_LIGHT=race[,FRACTION,PHASE]: DirtWorld's light instead of the sandbox's (BinarySky: sky dome and probe, two
        // forward-shadow suns, ambient, fog) and the race camera (App.swift startDirtTrack: zFar 250, SSAO 0.70 / 1.6 / 0.025),
        // to compare the robots' shading under the race light. Keep in step with RobotCloseups.cs.
        var sky: BinarySky? = nil
        if let spec = ProcessInfo.processInfo.environment["CLOSEUP_LIGHT"], spec.hasPrefix("race") {
            var lights: [SCNNode] = []
            world.scene.rootNode.enumerateChildNodes { n, _ in if n.light != nil { lights.append(n) } }
            lights.forEach { $0.removeFromParentNode() }
            let b = BinarySky(scene: world.scene); sky = b
            b.attach(camera: world.camera)
            let parts = spec.split(separator: ",").dropFirst().compactMap { Double($0) }
            b.apply(BinaryDaylight(fraction: parts.first ?? 0.5, phase: parts.count > 1 ? parts[1] : 1.2))
            b.updateShadowCenter(SIMD3(0, 0, 0))
            world.camera.camera?.zFar = 250
            world.camera.camera?.screenSpaceAmbientOcclusionIntensity = 0.70
            world.camera.camera?.screenSpaceAmbientOcclusionRadius = 1.6
            world.camera.camera?.screenSpaceAmbientOcclusionBias = 0.025
            // CLOSEUP_EXP=noibl,noamb,nosun,nossao switches single light terms off (to isolate shading differences); nonormal below.
            let exp = ProcessInfo.processInfo.environment["CLOSEUP_EXP"] ?? ""
            if exp.contains("noibl") { world.scene.lightingEnvironment.intensity = 0 }
            if exp.contains("noamb") { world.scene.rootNode.enumerateChildNodes { n, _ in if n.light?.type == .ambient { n.light?.intensity = 0 } } }
            if exp.contains("nosun") { b.suns.forEach { $0.light?.intensity = 0 } }
            if exp.contains("nossao") { world.camera.camera?.screenSpaceAmbientOcclusionIntensity = 0 }
        }
        _ = sky
        let renderer = SCNRenderer(device: MTLCreateSystemDefaultDevice()!, options: nil)
        renderer.scene = world.scene; renderer.pointOfView = world.camera

        func modelRoot(_ character: RacePerformance.Character) -> SCNNode {
            switch character {
            case .marvin: return robot.root
            case .r2d2: return r2d2.root
            case .bb8: return bb8.root
            case .wallE: return wallE.root
            }
        }
        // PlayerCharacter.swift updateModel(_:state:expression:) with the neutral expression.
        func updateModel(_ character: RacePerformance.Character, state: Simulation) {
            let expression = RacePerformance.Pose()
            var pose = expression
            pose.yaw += state.yaw; pose.pitch += state.pitch
            switch character {
            case .marvin: robot.update(state); robot.applyExpression(expression, state: state)
            case .r2d2: r2d2.update(state); r2d2.applyExpression(pose)
            case .bb8: bb8.update(state); bb8.applyExpression(pose, heading: state.heading)
            case .wallE: wallE.update(state); wallE.applyExpression(pose, heading: state.heading)
            }
        }
        func modelHeight(_ character: RacePerformance.Character) -> Double {
            switch character {
            case .marvin: return robot.neutralHeight * robot.modelScale
            case .r2d2: return R2D2.sceneHeight
            case .bb8: return bb8.height
            case .wallE: return wallE.height
            }
        }

        // (file, robot, side, front, height, look height as a fraction of the robot's height (negative: metres))
        // Camera = robot + right * side + forward * front + up * height, as in App.swift's smoke close-ups.
        let views: [(String, RacePerformance.Character, Double, Double, Double, Double)] = [
            ("r2d2-front", .r2d2, 0.85, 1.5, 0.7, -0.43),
            ("r2d2-wheels", .r2d2, 0.65, 0.85, 0.16, -0.15),
            ("bb8-front", .bb8, 0.8, 1.5, 0.75, 0.5),
            ("walle-front", .wallE, 0.8, 1.5, 0.75, 0.5),
            ("marvin-front", .marvin, 0.95, 1.4, 0.8, -0.28),
            ("marvin-face", .marvin, 0.18, 0.75, 0.42, 0.72),
            ("marvin-rear", .marvin, -0.8, -1.2, 0.75, 0.5),
            ("marvin-gear", .marvin, 0.75, 0.45, 0.14, 0.2),
            ("marvin-top", .marvin, 0.35, 0.55, 1.5, 0.3),
            ("r2d2-face", .r2d2, 0.2, 0.9, 0.85, 0.8),
            ("r2d2-rear", .r2d2, -0.85, -1.4, 0.8, 0.5),
            ("r2d2-top", .r2d2, 0.35, 0.6, 1.8, 0.6),
            ("bb8-face", .bb8, 0.15, 0.85, 0.55, 0.75),
            ("bb8-rear", .bb8, -0.8, -1.3, 0.7, 0.5),
            ("bb8-top", .bb8, 0.3, 0.5, 1.5, 0.5),
            ("walle-face", .wallE, 0.15, 1.0, 0.75, 0.8),
            ("walle-rear", .wallE, -0.85, -1.4, 0.8, 0.5),
            ("walle-gear", .wallE, 0.85, 0.6, 0.16, 0.15),
            ("walle-top", .wallE, 0.35, 0.6, 1.8, 0.5),
        ]
        let dirtyViews: [(String, RacePerformance.Character, Double)] = [
            ("marvin-dirty", .marvin, 0.28), ("r2d2-dirty", .r2d2, 0.44), ("bb8-dirty", .bb8, 0.27), ("walle-dirty", .wallE, 0.42),
        ]

        var states: [RacePerformance.Character: Simulation] = [:]
        for character in RacePerformance.Character.allCases {
            states[character] = Simulation(seed: 0, character: character)
            updateModel(character, state: states[character]!)
        }
        world.update(states[.marvin]!)
        // CLOSEUP_EXP=nonormal removes the robots' normal maps (to see what SSAO and shading take from them).
        if (ProcessInfo.processInfo.environment["CLOSEUP_EXP"] ?? "").contains("nonormal") {
            for character in RacePerformance.Character.allCases {
                modelRoot(character).enumerateHierarchy { n, _ in n.geometry?.materials.forEach { $0.normal.contents = nil } }
            }
        }
        if (ProcessInfo.processInfo.environment["CLOSEUP_EXP"] ?? "").contains("nomod") {
            for character in RacePerformance.Character.allCases {
                modelRoot(character).enumerateHierarchy { n, _ in n.geometry?.shaderModifiers = nil; n.geometry?.materials.forEach { $0.shaderModifiers = nil } }
            }
        }
        func stage(_ character: RacePerformance.Character) {
            for other in RacePerformance.Character.allCases { modelRoot(other).removeFromParentNode() }
            world.scene.rootNode.addChildNode(modelRoot(character))
        }
        func capture(_ name: String, _ character: RacePerformance.Character, side: Double, front: Double, height: Double, look: Double) throws {
            let state = states[character]!, angle = state.heading
            stage(character)
            world.camera.position = SCNVector3(state.x+cos(angle)*side+sin(angle)*front, state.groundY+height, state.z-sin(angle)*side+cos(angle)*front)
            world.camera.look(at: SCNVector3(state.x, state.groundY+look, state.z), up: SCNVector3(0, 1, 0), localFront: SCNVector3(0, 0, -1))
            let image = renderer.snapshot(atTime: 0, with: CGSize(width: 1280, height: 820), antialiasingMode: .multisampling4X)
            guard let tiff = image.tiffRepresentation, let png = NSBitmapImageRep(data: tiff)?.representation(using: .png, properties: [:]) else { throw CocoaError(.fileWriteUnknown) }
            try png.write(to: outDir.appendingPathComponent(name + ".png"))
        }
        for (name, character, side, front, height, look) in views {
            try capture(name, character, side: side, front: front, height: height, look: look < 0 ? -look : modelHeight(character) * look)
        }
        // App.swift: accumulate a representative stretch of driving, then inspect the coating.
        var dustyDrive = DirtOpponent()
        for _ in 0..<1800 {
            dustyDrive.advance(dt: 1.0/60, raceDT: 1.0/60)
            robot.dirtCoating.update(dustyDrive.simulation)
            r2d2.dirtCoating.update(dustyDrive.simulation)
            bb8.dirtCoating.update(dustyDrive.simulation); wallE.dirtCoating.update(dustyDrive.simulation)
        }
        for (name, character, height) in dirtyViews {
            try capture(name, character, side: 0.95, front: 1.4, height: 0.8, look: height)
        }
        for (name, character, side, front, height, look) in views where name.hasSuffix("-face") || name.hasSuffix("-rear") {
            try capture(name + "-dirty", character, side: side, front: front, height: height, look: look < 0 ? -look : modelHeight(character) * look)
        }
        let amounts = [robot.dirtCoating.amount, r2d2.dirtCoating.amount, bb8.dirtCoating.amount, wallE.dirtCoating.amount]
        print("Robot close-ups (SceneKit): \(views.count + dirtyViews.count + 8) images, dirt \(amounts) · \(outDir.path)")
    }
}
