// SceneKit experiment behind the facade's shader-argument binding (SCNMaterialProperty.ArgumentContents):
// when does a `contents` change of an SCNMaterialProperty passed to setValue(_:forKey:) reach the shader?
//
//   swiftc -O apps/simulator-godot/tools/scenekit-reference/robots/ArgumentBinding.swift -o /tmp/argument-binding
//   /tmp/argument-binding OUT_DIR
//
// Each row shares one property (an rgba32Float MTLTexture whose texel (1,0).r tints the quad red) between the listed
// geometries, set in that order. Rendered (white), then the contents are replaced by new textures.
// Measured on macOS 27 (M2):
//   - set before the first draw: every row shows the latest contents;
//   - after a draw: only rows whose LAST setValue was on a custom SCNGeometry (or a material) follow the changes,
//     for every geometry of the row (primitives included); rows whose last setValue was on a primitive (SCNBox,
//     SCNPlane, SCNSphere, SCNCylinder, SCNCone, SCNFloor, SCNShape, copies of them) keep the first draw's contents;
//   - a second renderer, or removing and re-adding the nodes in the same scene, does not refresh them;
//   - moving the nodes to a scene with another lighting setup (another program) resolves the current contents
//     once, then freezes again there; moving back shows the first scene's frozen contents.
import AppKit
import SceneKit
import Metal

let out = URL(fileURLWithPath: CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "/tmp/argument-binding")
try FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)
let device = MTLCreateSystemDefaultDevice()!
func tex(_ v: Float) -> MTLTexture {
    let d = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .rgba32Float, width: 2, height: 1, mipmapped: false)
    d.storageMode = .shared; d.usage = .shaderRead
    let t = device.makeTexture(descriptor: d)!
    let values: [Float] = [0, 0, 0, 0, v, 0, 0, 0]
    values.withUnsafeBytes { t.replace(region: MTLRegionMake2D(0, 0, 2, 1), mipmapLevel: 0, withBytes: $0.baseAddress!, bytesPerRow: 32) }
    return t
}
let shader = """
#pragma arguments
texture2d<float> duneContact;
#pragma body
float a = duneContact.read(uint2(1,0)).r;
_surface.diffuse.rgb = mix(_surface.diffuse.rgb, float3(1.0,0.0,0.0), a);
"""
func custom() -> SCNGeometry {
    let v = [SCNVector3(-0.4,-0.4,0), SCNVector3(0.4,-0.4,0), SCNVector3(0.4,0.4,0), SCNVector3(-0.4,0.4,0)]
    // A custom geometry without texture coordinates is not drawn at all with a texture argument (hence DirtCoating's UV stream).
    return SCNGeometry(sources: [SCNGeometrySource(vertices: v), SCNGeometrySource(normals: [SCNVector3](repeating: SCNVector3(0,0,1), count: 4)),
                                 SCNGeometrySource(textureCoordinates: [CGPoint(x:0,y:1),CGPoint(x:1,y:1),CGPoint(x:1,y:0),CGPoint(x:0,y:0)])],
                       elements: [SCNGeometryElement(indices: [Int32(0),1,2,0,2,3], primitiveType: .triangles)])
}
func makeScene(sun: Bool) -> (SCNScene, SCNNode) {
    let scene = SCNScene(); scene.background.contents = NSColor.black
    let cam = SCNNode(); cam.camera = SCNCamera(); cam.camera!.usesOrthographicProjection = true; cam.camera!.orthographicScale = 4
    cam.position = SCNVector3(0, 0, 10); scene.rootNode.addChildNode(cam)
    let amb = SCNNode(); amb.light = SCNLight(); amb.light!.type = .ambient; amb.light!.intensity = sun ? 700 : 1000; scene.rootNode.addChildNode(amb)
    if sun {
        let s = SCNNode(); s.light = SCNLight(); s.light!.type = .directional; s.light!.intensity = 300; scene.rootNode.addChildNode(s)
        scene.fogStartDistance = 50; scene.fogEndDistance = 100; scene.fogColor = NSColor.gray
    }
    return (scene, cam)
}
let (scene, cam) = makeScene(sun: false)
let rows: [[String]] = [["box"], ["custom","box"], ["box","custom"], ["custom","plane","custom"], ["plane","custom","plane"],
                        ["custom","sphere"], ["custom","shape"], ["material:box"]]
var props: [SCNMaterialProperty] = [], nodes: [SCNNode] = []
for (row, kinds) in rows.enumerated() {
    let prop = SCNMaterialProperty(contents: tex(0)); props.append(prop)
    let m = SCNMaterial(); m.lightingModel = .physicallyBased; m.diffuse.contents = NSColor.white
    for (i, k) in kinds.enumerated() {
        let g: SCNGeometry
        switch k {
        case "box", "material:box": g = SCNBox(width: 0.8, height: 0.8, length: 0.1, chamferRadius: 0)
        case "plane": g = SCNPlane(width: 0.8, height: 0.8)
        case "sphere": g = SCNSphere(radius: 0.4)
        case "shape": g = SCNShape(path: NSBezierPath(rect: NSRect(x: -0.4, y: -0.4, width: 0.8, height: 0.8)), extrusionDepth: 0)
        default: g = custom()
        }
        g.materials = [m]
        if k.hasPrefix("material:") { m.shaderModifiers = [.surface: shader]; m.setValue(prop, forKey: "duneContact") }
        else { g.shaderModifiers = [.surface: shader]; g.setValue(prop, forKey: "duneContact") }
        let n = SCNNode(geometry: g); n.position = SCNVector3(Double(i) - 1.5, 3.5 - Double(row), 0)
        scene.rootNode.addChildNode(n); nodes.append(n)
    }
}
let (scene2, cam2) = makeScene(sun: true)
let rA = SCNRenderer(device: device, options: nil); rA.scene = scene; rA.pointOfView = cam
let rB = SCNRenderer(device: device, options: nil); rB.scene = scene2; rB.pointOfView = cam2
func shot(_ r: SCNRenderer, _ name: String) throws {
    let im = r.snapshot(atTime: 0, with: CGSize(width: 400, height: 400), antialiasingMode: .none)
    try NSBitmapImageRep(data: im.tiffRepresentation!)!.representation(using: .png, properties: [:])!.write(to: out.appendingPathComponent(name))
}
try shot(rA, "0-first-draw.png")
for p in props { p.contents = tex(1) }
try shot(rA, "1-after-change.png")
for n in nodes { n.removeFromParentNode(); scene.rootNode.addChildNode(n) }
for p in props { p.contents = tex(0.75) }
try shot(rA, "2-readded.png")
for p in props { p.contents = tex(0.5) }
for n in nodes { n.removeFromParentNode(); scene2.rootNode.addChildNode(n) }
try shot(rB, "3-other-program.png")
for p in props { p.contents = tex(0.2) }
try shot(rB, "4-other-program-changed.png")
for n in nodes { n.removeFromParentNode(); scene.rootNode.addChildNode(n) }
try shot(rA, "5-back.png")
print("Argument binding experiment: \(out.path)")
