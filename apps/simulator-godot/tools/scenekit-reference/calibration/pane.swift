import AppKit
import SceneKit
let renderer = SCNRenderer(device: MTLCreateSystemDefaultDevice()!, options: nil)
func lin(_ v: UInt8) -> Double { let x = Double(v)/255; return x <= 0.04045 ? x/12.92 : pow((x+0.055)/1.055, 2.4) }
func run(_ label: String, model: SCNMaterial.LightingModel, box: Bool, doubleSided: Bool, alpha: CGFloat, transparency: CGFloat = 1, light: CGFloat = 1000, ambient: CGFloat = 0, rough: CGFloat = 1, mode: SCNTransparencyMode = .aOne) {
    let scene = SCNScene(); scene.background.contents = NSColor(srgbRed: 0.5, green: 0, blue: 0, alpha: 1)
    let m = SCNMaterial(); m.lightingModel = model; m.diffuse.contents = NSColor(srgbRed: 0.2, green: 0.6, blue: 0.9, alpha: alpha)
    m.roughness.contents = rough; m.metalness.contents = 0.0; m.isDoubleSided = doubleSided; m.transparency = transparency; m.transparencyMode = mode
    let g: SCNGeometry = box ? SCNBox(width: 4, height: 4, length: 0.02, chamferRadius: 0) : SCNPlane(width: 4, height: 4)
    g.materials = [m]; scene.rootNode.addChildNode(SCNNode(geometry: g))
    if light > 0 { let l = SCNNode(); l.light = SCNLight(); l.light!.type = .directional; l.light!.intensity = light; scene.rootNode.addChildNode(l) }
    if ambient > 0 { let l = SCNNode(); l.light = SCNLight(); l.light!.type = .ambient; l.light!.intensity = ambient; scene.rootNode.addChildNode(l) }
    let cam = SCNNode(); cam.camera = SCNCamera(); cam.camera!.usesOrthographicProjection = true; cam.position = SCNVector3(0, 0, 5); scene.rootNode.addChildNode(cam)
    renderer.scene = scene; renderer.pointOfView = cam
    let b = NSBitmapImageRep(data: renderer.snapshot(atTime: 0, with: CGSize(width: 64, height: 64), antialiasingMode: .none).tiffRepresentation!)!
    let i = 32*b.bytesPerRow + 32*(b.bitsPerPixel/8); let d = b.bitmapData!
    print(label.padding(toLength: 40, withPad: " ", startingAt: 0) + String(format: "%.4f %.4f %.4f", lin(d[i]), lin(d[i+1]), lin(d[i+2])))
}
run("pbr plane a0.45", model: .physicallyBased, box: false, doubleSided: false, alpha: 0.45)
run("pbr plane a0.45 2sided", model: .physicallyBased, box: false, doubleSided: true, alpha: 0.45)
run("pbr box a0.45 2sided", model: .physicallyBased, box: true, doubleSided: true, alpha: 0.45)
run("pbr box a0.45", model: .physicallyBased, box: true, doubleSided: false, alpha: 0.45)
run("pbr plane a0.45 rough0.2", model: .physicallyBased, box: false, doubleSided: false, alpha: 0.45, rough: 0.2)
run("pbr plane a0.45 amb500 nolight", model: .physicallyBased, box: false, doubleSided: false, alpha: 0.45, light: 0, ambient: 500)
run("lambert plane a0.45", model: .lambert, box: false, doubleSided: false, alpha: 0.45)
run("constant plane a0.45", model: .constant, box: false, doubleSided: false, alpha: 0.45)
run("pbr plane transparency0.45", model: .physicallyBased, box: false, doubleSided: false, alpha: 1, transparency: 0.45)
run("pbr plane a0.45 dualLayer", model: .physicallyBased, box: false, doubleSided: true, alpha: 0.45, mode: .dualLayer)
