import AppKit
import SceneKit
// Sphere (r=1) lit from +Y by a directional light; orthographic side view. Prints final/unshadowed along the
// sphere's silhouette-facing meridian as a function of N.L, for deferred/forward and several bias/radius values.
let renderer = SCNRenderer(device: MTLCreateSystemDefaultDevice()!, options: nil)
var sphereRadius: CGFloat = 1
func render(mode: SCNShadowMode?, bias: CGFloat, radius: CGFloat, samples: Int, mapSize: CGFloat, ortho: CGFloat, alpha: CGFloat, auto: Bool) -> NSBitmapImageRep {
    let scene = SCNScene(); scene.background.contents = NSColor.black
    let s = SCNSphere(radius: sphereRadius); s.segmentCount = 96
    let m = SCNMaterial(); m.lightingModel = .physicallyBased; m.diffuse.contents = NSColor.white; m.roughness.contents = 1.0; m.metalness.contents = 0.0
    s.materials = [m]; scene.rootNode.addChildNode(SCNNode(geometry: s))
    let l = SCNNode(); l.light = SCNLight(); l.light!.type = .directional; l.light!.intensity = 1000
    l.eulerAngles = SCNVector3(-Double.pi/2, 0, 0)
    if let mode { l.light!.castsShadow = true; l.light!.shadowMode = mode; l.light!.shadowBias = bias; l.light!.shadowRadius = radius
        l.light!.shadowSampleCount = samples; l.light!.shadowMapSize = CGSize(width: mapSize, height: mapSize); l.light!.orthographicScale = ortho
        l.light!.shadowColor = NSColor.black.withAlphaComponent(alpha); l.light!.automaticallyAdjustsShadowProjection = auto }
    scene.rootNode.addChildNode(l)
    let amb = SCNNode(); amb.light = SCNLight(); amb.light!.type = .ambient; amb.light!.intensity = 300; scene.rootNode.addChildNode(amb)
    let cam = SCNNode(); cam.camera = SCNCamera(); cam.camera!.usesOrthographicProjection = true; cam.camera!.orthographicScale = 1.2 * sphereRadius
    cam.position = SCNVector3(0, 0, 5); scene.rootNode.addChildNode(cam)
    renderer.scene = scene; renderer.pointOfView = cam
    let img = renderer.snapshot(atTime: 0, with: CGSize(width: 240, height: 240), antialiasingMode: .none)
    return NSBitmapImageRep(data: img.tiffRepresentation!)!
}
func lin(_ v: UInt8) -> Double { let x = Double(v)/255; return x <= 0.04045 ? x/12.92 : pow((x+0.055)/1.055, 2.4) }
func sample(_ b: NSBitmapImageRep, _ x: Int, _ y: Int) -> Double { lin(b.bitmapData![y*b.bytesPerRow + x*(b.bitsPerPixel/8)]) }
var base = render(mode: nil, bias: 1, radius: 3, samples: 1, mapSize: 2048, ortho: 2, alpha: 1, auto: true)
// Points on the visible hemisphere along x=0 (vertical meridian facing camera): normal angle from +Y.
let angles = [0.0, 20, 40, 60, 70, 80, 85, 90, 100, 120, 150]
func row(_ label: String, _ b: NSBitmapImageRep) {
    var out = label.padding(toLength: 34, withPad: " ", startingAt: 0)
    for a in angles {
        let t = a * Double.pi / 180
        // normal (0, cos t, sin t) on the camera-facing side; pixel = (120, 120 - cos t * 100)
        let px = 120, py = Int((120 - cos(t) * 100).rounded())
        let v = sample(b, px, py), v0 = sample(base, px, py)
        out += String(format: " %5.2f", v0 > 0.002 ? v / v0 : -1)
    }
    print(out)
}
print("angle from light (deg):".padding(toLength: 34, withPad: " ", startingAt: 0) + angles.map { String(format: " %5.0f", $0) }.joined())
for r in [CGFloat(1), 0.15] {
    sphereRadius = r
    base = render(mode: nil, bias: 1, radius: 3, samples: 1, mapSize: 2048, ortho: 2, alpha: 1, auto: true)
    for (rad, n) in [(CGFloat(3), 16), (8, 16), (16, 16), (16, 32), (32, 32), (32, 8)] {
        row("R\(r) deferred r\(rad) s\(n)", render(mode: .deferred, bias: 1, radius: rad, samples: n, mapSize: 2048, ortho: 2, alpha: 1, auto: true))
    }
    row("R\(r) deferred r32 s32 map4096", render(mode: .deferred, bias: 1, radius: 32, samples: 32, mapSize: 4096, ortho: 2, alpha: 1, auto: true))
    row("R\(r) deferred r32 s32 ortho10", render(mode: .deferred, bias: 1, radius: 32, samples: 32, mapSize: 2048, ortho: 10, alpha: 1, auto: true))
    row("R\(r) forward r32 s32", render(mode: .forward, bias: 1, radius: 32, samples: 32, mapSize: 2048, ortho: 2, alpha: 1, auto: true))
    row("R\(r) forward r3 s8", render(mode: .forward, bias: 1, radius: 3, samples: 8, mapSize: 2048, ortho: 2, alpha: 1, auto: true))
}
