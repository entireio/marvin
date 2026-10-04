import AppKit
import SceneKit
// Lit floor under a deferred light (alpha 1): brightness with shadows / without, vs camera elevation and distance.
let renderer = SCNRenderer(device: MTLCreateSystemDefaultDevice()!, options: nil)
func lin(_ v: UInt8) -> Double { let x = Double(v)/255; return x <= 0.04045 ? x/12.92 : pow((x+0.055)/1.055, 2.4) }
func render(shadow: Bool, elev: Double, dist: Double, radius: CGFloat, bias: CGFloat, map: CGFloat, near: CGFloat, maxDist: CGFloat) -> Double {
    let scene = SCNScene(); scene.background.contents = NSColor.black
    let floor = SCNPlane(width: 200, height: 200); let fm = SCNMaterial(); fm.lightingModel = .physicallyBased; fm.diffuse.contents = NSColor.white; fm.roughness.contents = 1.0
    floor.materials = [fm]; let fn = SCNNode(geometry: floor); fn.eulerAngles.x = -.pi/2; scene.rootNode.addChildNode(fn)
    let box = SCNBox(width: 1, height: 1, length: 1, chamferRadius: 0); box.materials = [fm]
    let bn = SCNNode(geometry: box); bn.position = SCNVector3(-3, 0.5, -3); scene.rootNode.addChildNode(bn)
    let l = SCNNode(); l.light = SCNLight(); l.light!.type = .directional; l.light!.intensity = 1000
    l.eulerAngles = SCNVector3(-0.85, -0.45, -0.25)
    if shadow { l.light!.castsShadow = true; l.light!.shadowMode = .deferred; l.light!.shadowColor = NSColor.black; l.light!.shadowRadius = radius; l.light!.shadowSampleCount = 16
        l.light!.shadowMapSize = CGSize(width: map, height: map); l.light!.orthographicScale = 10; l.light!.maximumShadowDistance = maxDist; l.light!.shadowBias = bias }
    scene.rootNode.addChildNode(l)
    let cam = SCNNode(); cam.camera = SCNCamera(); cam.camera!.fieldOfView = 48; cam.camera!.zNear = near; cam.camera!.zFar = 80
    let e = elev * Double.pi / 180
    cam.position = SCNVector3(0, dist * sin(e), dist * cos(e)); cam.look(at: SCNVector3Zero, up: SCNVector3(0, 1, 0), localFront: SCNVector3(0, 0, -1))
    scene.rootNode.addChildNode(cam)
    renderer.scene = scene; renderer.pointOfView = cam
    let b = NSBitmapImageRep(data: renderer.snapshot(atTime: 0, with: CGSize(width: 256, height: 164), antialiasingMode: .none).tiffRepresentation!)!
    var s = 0.0; for dy in -3...3 { for dx in -3...3 { s += lin(b.bitmapData![(82+dy)*b.bytesPerRow + (128+dx)*(b.bitsPerPixel/8)]) } }
    return s / 49
}
print("elev  dist   factor(r3,b1,map4096,near0.02,max22)   near1   bias10   r1   map2048")
for (elev, dist) in [(80.0, 7.0), (60, 7), (45, 7), (35, 7), (25, 7), (15, 7), (35, 3), (35, 15), (10, 15)] {
    let base = render(shadow: false, elev: elev, dist: dist, radius: 3, bias: 1, map: 4096, near: 0.02, maxDist: 22)
    let f = [render(shadow: true, elev: elev, dist: dist, radius: 3, bias: 1, map: 4096, near: 0.02, maxDist: 22),
             render(shadow: true, elev: elev, dist: dist, radius: 3, bias: 1, map: 4096, near: 1, maxDist: 22),
             render(shadow: true, elev: elev, dist: dist, radius: 3, bias: 10, map: 4096, near: 0.02, maxDist: 22),
             render(shadow: true, elev: elev, dist: dist, radius: 1, bias: 1, map: 4096, near: 0.02, maxDist: 22),
             render(shadow: true, elev: elev, dist: dist, radius: 3, bias: 1, map: 2048, near: 0.02, maxDist: 22)].map { $0 / base }
    print(String(format: "%4.0f %5.1f   ", elev, dist) + f.map { String(format: "%.3f", $0) }.joined(separator: "   "))
}
