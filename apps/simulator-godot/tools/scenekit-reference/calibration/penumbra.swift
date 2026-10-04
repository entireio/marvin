import AppKit
import SceneKit
// Box edge shadow on a floor; light 60 deg elevation; top-down perspective camera (fov 10, 10 m up).
// Prints the 10-90% penumbra width (cm) along x and the shadow edge position.
let renderer = SCNRenderer(device: MTLCreateSystemDefaultDevice()!, options: nil)
func lin(_ v: UInt8) -> Double { let x = Double(v)/255; return x <= 0.04045 ? x/12.92 : pow((x+0.055)/1.055, 2.4) }
func run(_ label: String, auto: Bool, ortho: CGFloat, map: CGFloat, radius: CGFloat, samples: Int, mode: SCNShadowMode = .forward, height: CGFloat = 3) {
    let scene = SCNScene(); scene.background.contents = NSColor.black
    let floor = SCNPlane(width: 40, height: 40); let fm = SCNMaterial(); fm.lightingModel = .physicallyBased; fm.diffuse.contents = NSColor.white; fm.roughness.contents = 1.0
    floor.materials = [fm]; let fn = SCNNode(geometry: floor); fn.eulerAngles.x = -.pi/2; scene.rootNode.addChildNode(fn)
    // box occupying x < 0, top at `height`, casting an edge parallel to z
    let box = SCNBox(width: 10, height: height, length: 10, chamferRadius: 0); box.materials = [fm]
    let bn = SCNNode(geometry: box); bn.position = SCNVector3(-5 - 0.0, height/2, 0); scene.rootNode.addChildNode(bn)
    let l = SCNNode(); l.light = SCNLight(); l.light!.type = .directional; l.light!.intensity = 1000
    l.light!.castsShadow = true; l.light!.shadowMode = mode; l.light!.shadowColor = NSColor.black; l.light!.shadowRadius = radius; l.light!.shadowSampleCount = samples
    l.light!.shadowMapSize = CGSize(width: map, height: map); l.light!.orthographicScale = ortho; l.light!.automaticallyAdjustsShadowProjection = auto
    l.light!.zNear = 0.1; l.light!.zFar = 220; l.light!.maximumShadowDistance = 500; l.light!.shadowBias = 0.6
    // light from -x at 60 deg elevation: shadow edge at x = height / tan(60)
    let d = SIMD3<Double>(-cos(Double.pi/3), sin(Double.pi/3), 0)
    l.position = SCNVector3(d.x*80, d.y*80, d.z*80); l.look(at: SCNVector3Zero, up: SCNVector3(0,1,0), localFront: SCNVector3(0,0,-1))
    scene.rootNode.addChildNode(l)
    let cam = SCNNode(); cam.camera = SCNCamera(); cam.camera!.fieldOfView = 10; cam.camera!.zNear = 1; cam.camera!.zFar = 250
    let edge = Double(height) / tan(Double.pi/3)
    cam.position = SCNVector3(edge, 10, 0); cam.look(at: SCNVector3(edge, 0, 0), up: SCNVector3(0, 0, -1), localFront: SCNVector3(0,0,-1))
    scene.rootNode.addChildNode(cam)
    renderer.scene = scene; renderer.pointOfView = cam
    let W = 512
    let b = NSBitmapImageRep(data: renderer.snapshot(atTime: 0, with: CGSize(width: W, height: W), antialiasingMode: .none).tiffRepresentation!)!
    // row through the centre; x pixel -> world x: half width = 10 * tan(5 deg)
    let half = 10 * tan(5 * Double.pi / 180), row = W/2
    var prof: [Double] = []
    for x in 0..<W { var s = 0.0; for dy in -4...4 { s += lin(b.bitmapData![(row+dy)*b.bytesPerRow + x*(b.bitsPerPixel/8)]) }; prof.append(s/9) }
    let lit = prof[(W-20)..<W].reduce(0,+)/20, dark = prof[0..<20].reduce(0,+)/20
    func cross(_ f: Double) -> Double { let t = dark + (lit-dark)*f; for x in 1..<W where prof[x-1] < t && prof[x] >= t { return Double(x-1) + (t-prof[x-1])/(prof[x]-prof[x-1]) }; return .nan }
    let px = 2*half/Double(W)
    let w = (cross(0.9) - cross(0.1)) * px * 100, mid = (cross(0.5) - Double(W)/2) * px * 100
    print(label.padding(toLength: 44, withPad: " ", startingAt: 0) + String(format: "penumbra10-90 %6.2f cm  edge offset %6.2f cm  dark %.3f lit %.3f", w, mid, dark, lit))
}
run("sunA ortho58 map4096 r3 s8", auto: false, ortho: 58, map: 4096, radius: 3, samples: 8)
run("sunB ortho58 map2048 r2 s8", auto: false, ortho: 58, map: 2048, radius: 2, samples: 8)
run("ortho58 map4096 r1 s8", auto: false, ortho: 58, map: 4096, radius: 1, samples: 8)
run("ortho58 map4096 r8 s8", auto: false, ortho: 58, map: 4096, radius: 8, samples: 8)
run("ortho58 map4096 r3 s1", auto: false, ortho: 58, map: 4096, radius: 3, samples: 1)
run("ortho20 map4096 r3 s8", auto: false, ortho: 20, map: 4096, radius: 3, samples: 8)
run("ortho58 map4096 r3 s8 h3", auto: false, ortho: 58, map: 4096, radius: 3, samples: 8, height: 3)
run("sandbox auto ortho10 map4096 r3 s16 deferred", auto: true, ortho: 10, map: 4096, radius: 3, samples: 16, mode: .deferred)
run("menu auto ortho2 map2048 r32 s32 deferred", auto: true, ortho: 2, map: 2048, radius: 32, samples: 32, mode: .deferred)
