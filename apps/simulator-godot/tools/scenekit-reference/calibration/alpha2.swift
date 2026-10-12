import AppKit
import SceneKit
let renderer = SCNRenderer(device: MTLCreateSystemDefaultDevice()!, options: nil)
func lin(_ v: UInt8) -> Double { let x = Double(v)/255; return x <= 0.04045 ? x/12.92 : pow((x+0.055)/1.055, 2.4) }
func image(alpha: UInt8) -> NSImage {
    let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 4, pixelsHigh: 4, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 16, bitsPerPixel: 32)!
    // premultiplied storage: colour (0.2,0.6,0.9) sRGB x alpha
    let a = Double(alpha)/255
    for i in 0..<16 { bitmap.bitmapData![i*4] = UInt8(0.2*255*a); bitmap.bitmapData![i*4+1] = UInt8(0.6*255*a); bitmap.bitmapData![i*4+2] = UInt8(0.9*255*a); bitmap.bitmapData![i*4+3] = alpha }
    let img = NSImage(size: NSSize(width: 4, height: 4)); img.addRepresentation(bitmap); return img
}
func run(_ label: String, model: SCNMaterial.LightingModel, contents: Any, vertexAlpha: Float? = nil, amb: CGFloat = 1000, dir: CGFloat = 0) {
    let scene = SCNScene(); scene.background.contents = NSColor(srgbRed: 0.5, green: 0, blue: 0, alpha: 1)
    let m = SCNMaterial(); m.lightingModel = model; m.diffuse.contents = contents; m.roughness.contents = 1.0; m.metalness.contents = 0.0
    var g: SCNGeometry = SCNPlane(width: 4, height: 4)
    if let va = vertexAlpha {
        let v = [SCNVector3(-2,-2,0), SCNVector3(2,-2,0), SCNVector3(2,2,0), SCNVector3(-2,2,0)]
        let c: [Float] = Array(repeating: [Float(0.0331), 0.3185, 0.7874, va], count: 4).flatMap { $0 }
        let cs = c.withUnsafeBytes { SCNGeometrySource(data: Data($0), semantic: .color, vectorCount: 4, usesFloatComponents: true, componentsPerVector: 4, bytesPerComponent: 4, dataOffset: 0, dataStride: 16) }
        g = SCNGeometry(sources: [SCNGeometrySource(vertices: v), SCNGeometrySource(normals: Array(repeating: SCNVector3(0,0,1), count: 4)), cs], elements: [SCNGeometryElement(indices: [Int32(0),1,2,0,2,3], primitiveType: .triangles)])
    }
    g.materials = [m]; scene.rootNode.addChildNode(SCNNode(geometry: g))
    if amb > 0 { let l = SCNNode(); l.light = SCNLight(); l.light!.type = .ambient; l.light!.intensity = amb; scene.rootNode.addChildNode(l) }
    if dir > 0 { let l = SCNNode(); l.light = SCNLight(); l.light!.type = .directional; l.light!.intensity = dir; scene.rootNode.addChildNode(l) }
    let cam = SCNNode(); cam.camera = SCNCamera(); cam.camera!.usesOrthographicProjection = true; cam.position = SCNVector3(0, 0, 5); scene.rootNode.addChildNode(cam)
    renderer.scene = scene; renderer.pointOfView = cam
    let b = NSBitmapImageRep(data: renderer.snapshot(atTime: 0, with: CGSize(width: 64, height: 64), antialiasingMode: .none).tiffRepresentation!)!
    let i = 32*b.bytesPerRow + 32*(b.bitsPerPixel/8); let d = b.bitmapData!
    print(label.padding(toLength: 40, withPad: " ", startingAt: 0) + String(format: "%.4f %.4f %.4f", lin(d[i]), lin(d[i+1]), lin(d[i+2])))
}
for (model, tag) in [(SCNMaterial.LightingModel.lambert, "lambert"), (.blinn, "blinn"), (.physicallyBased, "pbr")] {
    run("\(tag) opaque amb1000", model: model, contents: NSColor(srgbRed: 0.2, green: 0.6, blue: 0.9, alpha: 1))
    run("\(tag) opaque amb500", model: model, contents: NSColor(srgbRed: 0.2, green: 0.6, blue: 0.9, alpha: 1), amb: 500)
    run("\(tag) opaque dir1000", model: model, contents: NSColor(srgbRed: 0.2, green: 0.6, blue: 0.9, alpha: 1), amb: 0, dir: 1000)
    run("\(tag) color a0.45 dir1000", model: model, contents: NSColor(srgbRed: 0.2, green: 0.6, blue: 0.9, alpha: 0.45), amb: 0, dir: 1000)
    run("\(tag) texture a115 dir1000", model: model, contents: image(alpha: 115), amb: 0, dir: 1000)
    run("\(tag) texture a115 amb500", model: model, contents: image(alpha: 115), amb: 500)
    run("\(tag) texture a115 amb500 dir500", model: model, contents: image(alpha: 115), amb: 500, dir: 500)
    run("\(tag) vertex a0.45 dir1000", model: model, contents: NSColor.white, vertexAlpha: 0.45, amb: 0, dir: 1000)
    run("\(tag) vertex a0.45 amb500", model: model, contents: NSColor.white, vertexAlpha: 0.45, amb: 500)
}
