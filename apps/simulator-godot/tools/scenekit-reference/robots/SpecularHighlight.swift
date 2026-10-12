// SceneKit experiment behind the facade's direct specular (SceneKitCalibration.SpecularAntialiasing, the composer's
// light()): PBR spheres with black diffuse (specular only) under one sun.
//
//   swiftc -O apps/simulator-godot/tools/scenekit-reference/robots/SpecularHighlight.swift -o /tmp/specular-highlight
//   /tmp/specular-highlight spheres INTENSITY OUT.png        8 spheres, roughness 0 .. 0.2, 1600x800, 0.0069 rad per pixel
//   /tmp/specular-highlight zoom ROUGHNESS INTENSITY OUT.png [ORTHO]   one sphere zoomed on its highlight (ORTHO 0.04:
//                                                            400x400, 0.0005 rad per pixel)
//
// Measured on macOS 27 (M2), compared in linear light (peak, half-maximum area, sum): the highlight follows GGX with
// alpha^2 + 0.25 x (|dN/dx|^2 + |dN/dy|^2), N the shading normal's screen-space derivatives, and D is not clamped, so
// roughness 0 keeps a finite, full-energy highlight whose size depends on the pixel footprint (whole sphere: like GGX
// roughness 0.07; zoomed: like 0.018), while a flat roughness-0 plane reflects nothing (FacadeReference L_pbr_rough0_dir1000).
// The facade now matches peaks and sums within 1% for roughness 0 .. 0.1 at both scales.
import AppKit
import SceneKit

let args = CommandLine.arguments
let renderer = SCNRenderer(device: MTLCreateSystemDefaultDevice()!, options: nil)
func sphere(_ roughness: CGFloat, segments: Int) -> SCNNode {
    let s = SCNSphere(radius: 0.4); s.segmentCount = segments
    let m = SCNMaterial(); m.lightingModel = .physicallyBased; m.diffuse.contents = NSColor.black
    m.roughness.contents = roughness; m.metalness.contents = 0
    s.materials = [m]; return SCNNode(geometry: s)
}
func sun(_ scene: SCNScene, _ intensity: CGFloat) -> SCNNode {
    let n = SCNNode(); n.light = SCNLight(); n.light!.type = .directional; n.light!.intensity = intensity
    n.eulerAngles = SCNVector3(-0.3, 0.3, 0); scene.rootNode.addChildNode(n); return n
}
func render(_ scene: SCNScene, ortho: Double, at p: SIMD2<Float>, size: CGSize, to path: String) {
    let cam = SCNNode(); cam.camera = SCNCamera(); cam.camera!.usesOrthographicProjection = true; cam.camera!.orthographicScale = ortho
    cam.camera!.wantsHDR = false; cam.camera!.zNear = 0.1; cam.camera!.zFar = 20
    cam.position = SCNVector3(p.x, p.y, 10); scene.rootNode.addChildNode(cam)
    renderer.scene = scene; renderer.pointOfView = cam
    let image = renderer.snapshot(atTime: 0, with: size, antialiasingMode: .multisampling4X)
    try! NSBitmapImageRep(data: image.tiffRepresentation!)!.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: path))
}
let scene = SCNScene(); scene.background.contents = NSColor.black
if args.count >= 4 && args[1] == "spheres" {
    for (i, r) in ([0, 0.01, 0.03, 0.05, 0.08, 0.1, 0.15, 0.2] as [CGFloat]).enumerated() {
        let n = sphere(r, segments: 96); n.position = SCNVector3(Double(i % 4) - 1.5, i < 4 ? 0.5 : -0.5, 0); scene.rootNode.addChildNode(n)
    }
    _ = sun(scene, CGFloat(Double(args[2])!))
    render(scene, ortho: 1.1, at: .zero, size: CGSize(width: 1600, height: 800), to: args[3])
} else if args.count >= 5 && args[1] == "zoom" {
    scene.rootNode.addChildNode(sphere(CGFloat(Double(args[2])!), segments: 200))
    let light = sun(scene, CGFloat(Double(args[3])!))
    let h = simd_normalize(-light.simdWorldFront + SIMD3<Float>(0, 0, 1)) * 0.4
    render(scene, ortho: args.count > 5 ? Double(args[5])! : 0.04, at: SIMD2(h.x, h.y), size: CGSize(width: 400, height: 400), to: args[4])
} else {
    print("usage: SpecularHighlight spheres INTENSITY OUT.png | zoom ROUGHNESS INTENSITY OUT.png [ORTHO]"); exit(1)
}
