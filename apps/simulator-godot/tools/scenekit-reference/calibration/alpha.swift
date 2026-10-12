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
func run(_ label: String, model: SCNMaterial.LightingModel, contents: Any, vertexAlpha: Float? = nil) {
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
    let l = SCNNode(); l.light = SCNLight(); l.light!.type = .ambient; l.light!.intensity = 1000; scene.rootNode.addChildNode(l)
    let cam = SCNNode(); cam.camera = SCNCamera(); cam.camera!.usesOrthographicProjection = true; cam.position = SCNVector3(0, 0, 5); scene.rootNode.addChildNode(cam)
    renderer.scene = scene; renderer.pointOfView = cam
    let b = NSBitmapImageRep(data: renderer.snapshot(atTime: 0, with: CGSize(width: 64, height: 64), antialiasingMode: .none).tiffRepresentation!)!
    let i = 32*b.bytesPerRow + 32*(b.bitsPerPixel/8); let d = b.bitmapData!
    print(label.padding(toLength: 40, withPad: " ", startingAt: 0) + String(format: "%.4f %.4f %.4f", lin(d[i]), lin(d[i+1]), lin(d[i+2])))
}
for (model, tag) in [(SCNMaterial.LightingModel.physicallyBased, "pbr"), (.constant, "constant"), (.lambert, "lambert")] {
    run("\(tag) color a0.45 (amb1000)", model: model, contents: NSColor(srgbRed: 0.2, green: 0.6, blue: 0.9, alpha: 0.45))
    run("\(tag) texture a115", model: model, contents: image(alpha: 115))
    run("\(tag) white x vertex a0.45", model: model, contents: NSColor.white, vertexAlpha: 0.45)
}
