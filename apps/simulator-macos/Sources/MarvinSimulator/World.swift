import AppKit
import SceneKit
import SimulationCore

final class World {
    let scene = SCNScene(), camera = SCNNode()
    var beacons: [SCNNode] = []
    var beaconLabels: [SCNNode] = []
    let floorSurface = SCNNode(), floorDetails = SCNNode()
    private var floorCheckpoints: [Checkpoint] = []
    let soil = material(0xb49470, roughness: 0.98)
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
        // Solid backing ends below the channels; the cut-out surface is at y=0.
        box(0, -0.05, 0, 12, 0.05, 10, soil, radius: 0.04)
        floorSurface.eulerAngles.x = -.pi/2
        scene.rootNode.addChildNode(floorSurface)
        scene.rootNode.addChildNode(floorDetails)
        let wall = material(0xa5b9ad)
        box(-6.08, 0.18, 0, 0.16, 0.36, 10.3, wall, radius: 0.04)
        box(6.08, 0.18, 0, 0.16, 0.36, 10.3, wall, radius: 0.04)
        box(0, 0.18, -5.08, 12.3, 0.36, 0.16, wall, radius: 0.04)
        box(0, 0.18, 5.08, 12.3, 0.36, 0.16, wall, radius: 0.04)
        for (i, o) in Simulation.obstacles.enumerated() {
            let body = box(o.x, o.height/2, o.z, o.width, o.height, o.depth,
                          material(i.isMultiple(of: 2) ? 0x96b4a7 : 0xd2b59a), radius: 0.07)
            body.name = "Obstacle \(i+1)"
        }
        // Dock and course markers are painted on the floor, not collision solids.
        box(0, 0, -2.6, 1.35, 0.001, 1.25, material(0xb7cdc0), radius: 0.08).castsShadow = false
        for x in [-0.6, 0.6] { box(x, 0.0005, -2.6, 0.025, 0.001, 1.1, teal).castsShadow = false }
        for i in 0..<CourseLayout.count {
            let ring = FloorGroove.geometry()
            ring.materials = [material(0x70583f, roughness: 0.95)]
            let node = SCNNode(geometry: ring); node.position = SCNVector3(0, 0, 0)
            scene.rootNode.addChildNode(node); beacons.append(node)
            let text = SCNText(string: String(i+1), extrusionDepth: 0)
            text.font = NSFont.monospacedSystemFont(ofSize: 1, weight: .semibold)
            text.flatness = 0.2; text.materials = [material(0x4e756a)]
            let label = SCNNode(geometry: text)
            let bounds = text.boundingBox
            let height = bounds.max.y-bounds.min.y
            let scale = CGFloat(0.36)/height
            label.scale = SCNVector3(scale, scale, scale)
            label.pivot = SCNMatrix4MakeTranslation((bounds.min.x+bounds.max.x)/2, (bounds.min.y+bounds.max.y)/2, 0)
            label.eulerAngles.x = -.pi/2
            label.position = SCNVector3(0, 0.0008, 0)
            label.castsShadow = false
            scene.rootNode.addChildNode(label); beaconLabels.append(label)
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
    private func rebuildFloor(_ checkpoints: [Checkpoint]) {
        floorCheckpoints = checkpoints
        for (i, point) in checkpoints.enumerated() {
            let start = i == 0 ? Checkpoint(x: CourseLayout.launch.x, z: CourseLayout.launch.z) : checkpoints[i-1]
            beaconLabels[i].eulerAngles = SCNVector3(-Double.pi/2, CourseRoute.labelYaw(from: start, to: point), 0)
        }
        floorDetails.childNodes.forEach { $0.removeFromParentNode() }
        let path = NSBezierPath(rect: NSRect(x: -6, y: -5, width: 12, height: 10))
        path.windingRule = .evenOdd
        for point in checkpoints {
            let radius = FloorGroove.outerRadius
            for i in 0..<128 {
                let angle = Double(i)*2*Double.pi/128
                let p = NSPoint(x: point.x+radius*cos(angle), y: -point.z-radius*sin(angle))
                if i == 0 { path.move(to: p) } else { path.line(to: p) }
            }
            path.close()
            // The untouched floor inside each annular channel.
            let disk = SCNCylinder(radius: CGFloat(FloorGroove.innerRadius), height: 0.025)
            disk.radialSegmentCount = 128; disk.materials = [soil]
            let center = SCNNode(geometry: disk)
            center.position = SCNVector3(point.x, -0.0125, point.z)
            floorDetails.addChildNode(center)
        }
        let surface = SCNShape(path: path, extrusionDepth: 0)
        surface.materials = [soil]
        floorSurface.geometry = surface

        let line = material(0xa48766, roughness: 0.98)
        func stroke(fixed: Double, alongZ: Bool, spans: [(Double, Double)]) {
            var segments = spans
            for point in checkpoints {
                let distance = fixed-(alongZ ? point.x : point.z)
                let radius = FloorGroove.outerRadius+0.006
                guard abs(distance) < radius else { continue }
                let half = sqrt(radius*radius-distance*distance)
                let center = alongZ ? point.z : point.x
                let lo = center-half, hi = center+half
                segments = segments.flatMap { a, b -> [(Double, Double)] in
                    if hi <= a || lo >= b { return [(a,b)] }
                    return [(a,min(b,lo)), (max(a,hi),b)].filter { $0.1 > $0.0 }
                }
            }
            for (a,b) in segments {
                let node = box(alongZ ? fixed : (a+b)/2, 0.003, alongZ ? (a+b)/2 : fixed,
                               alongZ ? 0.009 : b-a, 0.005, alongZ ? b-a : 0.009, line)
                node.castsShadow = false; floorDetails.addChildNode(node)
            }
        }
        for x in -5...5 {
            stroke(fixed: Double(x), alongZ: true, spans: x == 0 ? [(-4.95,-3.225),(-1.975,4.95)] : [(-4.95,4.95)])
        }
        for z in -4...4 {
            stroke(fixed: Double(z), alongZ: false, spans: [-3,-2].contains(z) ? [(-5.95,-0.675),(0.675,5.95)] : [(-5.95,5.95)])
        }
    }
    func update(_ state: Simulation) {
        if state.checkpoints != floorCheckpoints { rebuildFloor(state.checkpoints) }
        for (i, node) in beacons.enumerated() {
            let point = state.checkpoints[i]
            node.position = SCNVector3(point.x, 0, point.z)
            beaconLabels[i].position = SCNVector3(point.x, 0.0008, point.z)
            node.geometry?.firstMaterial?.diffuse.contents = color(i < state.checkpoint ? 0x2b8e7f : i == state.checkpoint ? 0xc38b41 : 0x70583f)
            // Pulse the light, not the cut-out dimensions of a physical groove.
            let glow = CGFloat(0.18+0.12*(1+sin(state.elapsed*2))/2)
            node.geometry?.firstMaterial?.emission.contents = i == state.checkpoint
                ? NSColor(srgbRed: glow, green: glow*0.55, blue: glow*0.12, alpha: 1) : NSColor.black
        }
    }
}
