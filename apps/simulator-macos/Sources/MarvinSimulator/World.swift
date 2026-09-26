import AppKit
import SceneKit
import SimulationCore

final class World {
    let scene = SCNScene(), camera = SCNNode()
    var beacons: [SCNNode] = []
    let teal = material(0x2b8e7f), orange = material(0xd89c64)

    init() {
        scene.background.contents = color(0xdbe4df)
        scene.fogColor = color(0xdbe4df); scene.fogStartDistance = 15; scene.fogEndDistance = 38
        camera.camera = SCNCamera(); camera.camera?.fieldOfView = 48
        camera.camera?.zNear = 0.02; camera.camera?.zFar = 80
        camera.camera?.wantsHDR = false
        scene.rootNode.addChildNode(camera)
        let ambient = SCNNode(); ambient.light = SCNLight()
        ambient.light?.type = .ambient; ambient.light?.intensity = 550
        ambient.light?.color = color(0xe7f4ff); scene.rootNode.addChildNode(ambient)
        let sun = SCNNode(); sun.light = SCNLight(); sun.light?.type = .directional
        sun.light?.intensity = 1400; sun.light?.color = color(0xfff1df)
        sun.eulerAngles = SCNVector3(-0.85, -0.45, -0.25)
        sun.light?.castsShadow = true; sun.light?.shadowMode = .deferred
        sun.light?.shadowMapSize = CGSize(width: 4096, height: 4096)
        sun.light?.shadowSampleCount = 16; sun.light?.shadowColor = NSColor.black.withAlphaComponent(0.24)
        sun.light?.orthographicScale = 10; sun.light?.maximumShadowDistance = 22
        scene.rootNode.addChildNode(sun)
        // Keep the plinth top below the deck's y=0 surface. Coplanar top
        // faces cause depth-buffer fighting whenever the camera or robot moves.
        box(0, -0.175, 0, 12.5, 0.3, 10.5, material(0x927455, roughness: 0.95), radius: 0.14)
        box(0, -0.025, 0, 12, 0.05, 10, material(0xb49470, roughness: 0.98), radius: 0.04)
        let line = material(0xa48766, roughness: 0.98)
        // Leave the floor inlay clear of the raised grid strokes.
        for x in -5...5 {
            let spans = x == 0 ? [(-4.95, -3.225), (-1.975, 4.95)] : [(-4.95, 4.95)]
            for (a, b) in spans { box(Double(x), 0.003, (a+b)/2, 0.009, 0.005, b-a, line).castsShadow = false }
        }
        for z in -4...4 {
            let spans = [-3, -2].contains(z) ? [(-5.95, -0.675), (0.675, 5.95)] : [(-5.95, 5.95)]
            for (a, b) in spans { box((a+b)/2, 0.003, Double(z), b-a, 0.005, 0.009, line).castsShadow = false }
        }
        let wall = material(0xa5b9ad)
        box(-6.08, 0.18, 0, 0.16, 0.36, 10.3, wall, radius: 0.04)
        box(6.08, 0.18, 0, 0.16, 0.36, 10.3, wall, radius: 0.04)
        box(0, 0.18, -5.08, 12.3, 0.36, 0.16, wall, radius: 0.04)
        box(0, 0.18, 5.08, 12.3, 0.36, 0.16, wall, radius: 0.04)
        for (i, o) in Simulation.obstacles.enumerated() {
            let body = box(o.x, o.height/2, o.z, o.width, o.height, o.depth,
                          material(i.isMultiple(of: 2) ? 0x96b4a7 : 0xd2b59a), radius: 0.07)
            body.name = "Obstacle \(i+1)"
            box(o.x, o.height+0.006, o.z, o.width*0.88, 0.02, o.depth*0.88,
                material(i.isMultiple(of: 2) ? 0xc7d6c8 : 0xe7d6bf), radius: 0.025)
            for offset in [-0.3, 0.3] {
                box(o.x+o.width*offset, o.height+0.019, o.z, 0.035, 0.008, o.depth*0.85, teal)
            }
        }
        // Dock and course markers are painted on the floor, not collision solids.
        box(0, 0, -2.6, 1.35, 0.001, 1.25, material(0xb7cdc0), radius: 0.08).castsShadow = false
        for x in [-0.6, 0.6] { box(x, 0.0005, -2.6, 0.025, 0.001, 1.1, teal).castsShadow = false }
        for (i, p) in Simulation.checkpoints.enumerated() {
            let ring = SCNTorus(ringRadius: 0.54, pipeRadius: 0.018)
            ring.materials = [material(0xa3b8ae)]
            let node = SCNNode(geometry: ring); node.position = SCNVector3(p.x, 0.028, p.z)
            scene.rootNode.addChildNode(node); beacons.append(node)
            let text = SCNText(string: String(format: "%02d", i+1), extrusionDepth: 0.002)
            text.font = NSFont.monospacedSystemFont(ofSize: 1, weight: .medium)
            text.flatness = 0.2; text.materials = [material(0x4e756a)]
            let label = SCNNode(geometry: text); label.scale = SCNVector3(0.16, 0.16, 0.16)
            label.eulerAngles.x = -.pi/2; label.position = SCNVector3(p.x-0.10, 0.012, p.z+0.07)
            label.castsShadow = false
            scene.rootNode.addChildNode(label)
        }
    }
    @discardableResult
    func box(_ x: Double, _ y: Double, _ z: Double, _ w: Double, _ h: Double, _ d: Double,
             _ material: SCNMaterial, radius: Double = 0) -> SCNNode {
        let geometry = SCNBox(width: w, height: h, length: d, chamferRadius: radius)
        geometry.materials = [material]
        let node = SCNNode(geometry: geometry); node.position = SCNVector3(x, y, z)
        scene.rootNode.addChildNode(node); return node
    }
    func update(_ state: Simulation) {
        for (i, node) in beacons.enumerated() {
            node.geometry?.firstMaterial?.diffuse.contents = color(i < state.checkpoint ? 0x2b8e7f : i == state.checkpoint ? 0xe0a568 : 0xa3b8ae)
            node.geometry?.firstMaterial?.emission.contents = color(i == state.checkpoint ? 0x3d2513 : 0x000000)
            let scale = i == state.checkpoint ? 1 + sin(state.elapsed*2)*0.06 : 1
            node.scale = SCNVector3(scale, 1, scale)
        }
    }
}
