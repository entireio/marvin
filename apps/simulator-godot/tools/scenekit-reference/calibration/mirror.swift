import AppKit
import SceneKit
// Metal sphere (roughness r) lit only by the BinarySky probe; ortho side view; luminance along the vertical centre line.
let renderer = SCNRenderer(device: MTLCreateSystemDefaultDevice()!, options: nil)
func lin(_ v: UInt8) -> Double { let x = Double(v)/255; return x <= 0.04045 ? x/12.92 : pow((x+0.055)/1.055, 2.4) }
func skyProbe(zenith: SIMD3<Float>, horizon: SIMD3<Float>) -> NSImage {
    let w = 64, h = 32
    let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: w, pixelsHigh: h, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: w*4, bitsPerPixel: 32)!
    for y in 0..<h {
        let t = Float(pow(max(0, cos(Double(y)/Double(h-1)*Double.pi)), 0.42))
        let rgb = (horizon*(1-t)+zenith*t)*(y > h/2 ? 0.55 : 1)
        for x in 0..<w { let j = (y*w+x)*4; for c in 0..<3 { bitmap.bitmapData![j+c] = UInt8(max(0, min(255, rgb[c]*255))) }; bitmap.bitmapData![j+3] = 255 }
    }
    let image = NSImage(size: NSSize(width: w, height: h)); image.addRepresentation(bitmap); return image
}
let probe = skyProbe(zenith: SIMD3(0.20, 0.38, 0.62), horizon: SIMD3(0.65, 0.72, 0.75))
for r in [0.0, 0.1, 0.2, 0.3, 0.45, 0.6, 0.8, 1.0] {
    let scene = SCNScene(); scene.background.contents = NSColor.black
    scene.lightingEnvironment.contents = probe; scene.lightingEnvironment.intensity = 1
    if let a = ProcessInfo.processInfo.environment["CAL_MIRROR_AMB"], let v = Double(a) { let l = SCNNode(); l.light = SCNLight(); l.light!.type = .ambient; l.light!.intensity = CGFloat(v); l.light!.color = NSColor(calibratedRed: 0.54, green: 0.62, blue: 0.78, alpha: 1); scene.rootNode.addChildNode(l) }
    let s = SCNSphere(radius: 1); s.segmentCount = 96
    let m = SCNMaterial(); m.lightingModel = .physicallyBased; m.diffuse.contents = ProcessInfo.processInfo.environment["CAL_MIRROR_GRAY"] != nil ? NSColor(srgbRed: 0xc3/255.0, green: 0xc7/255.0, blue: 0xc9/255.0, alpha: 1) : NSColor.white; m.metalness.contents = 1.0; m.roughness.contents = r
    s.materials = [m]; scene.rootNode.addChildNode(SCNNode(geometry: s))
    let cam = SCNNode(); cam.camera = SCNCamera(); cam.camera!.usesOrthographicProjection = true; cam.camera!.orthographicScale = 1.05
    cam.position = SCNVector3(0, 0, 5); scene.rootNode.addChildNode(cam)
    renderer.scene = scene; renderer.pointOfView = cam
    let b = NSBitmapImageRep(data: renderer.snapshot(atTime: 0, with: CGSize(width: 210, height: 210), antialiasingMode: .none).tiffRepresentation!)!
    // sample the centre column at sphere heights y = sin(elev) where the reflected direction has elevation 2*asin(y)... print by normal elevation
    var out = String(format: "r%.1f ", r)
    for ne in stride(from: -80.0, through: 80.0, by: 10.0) {
        let y = sin(ne * Double.pi / 180); let py = Int((105 - y * 100).rounded())
        let i = py*b.bytesPerRow + 105*(b.bitsPerPixel/8)
        let l = 0.2126*lin(b.bitmapData![i]) + 0.7152*lin(b.bitmapData![i+1]) + 0.0722*lin(b.bitmapData![i+2])
        out += String(format: " %.3f", l)
    }
    print(out)
}
