// SceneKit reference renders for the Godot SceneKit facade.
//
//   swiftc -O apps/simulator-godot/tools/scenekit-reference/FacadeReference.swift -o /tmp/facade-reference
//   /tmp/facade-reference OUT_DIR [GODOT_DIR]
//
// Renders the scenes of apps/simulator-godot/scripts/SceneKit/FacadeTest.cs with SceneKit
// (same names, same construction; the shader modifiers here are the game's original MSL),
// writes OUT_DIR/*.png and OUT_DIR/measurements.json. With GODOT_DIR (output of
// `tools/godot -- --facade-test GODOT_DIR`) it also prints measurement deltas and writes
// OUT_DIR/compare-*.png (SceneKit | Godot | |difference| x 4).
// Keep this file and FacadeTest.cs in step.
import AppKit
import SceneKit
import Metal

let args = CommandLine.arguments
guard args.count >= 2 else { print("usage: FacadeReference OUT_DIR [GODOT_DIR]"); exit(1) }
let outDir = URL(fileURLWithPath: args[1])
try? FileManager.default.createDirectory(at: outDir, withIntermediateDirectories: true)
let device = MTLCreateSystemDefaultDevice()!
let renderer = SCNRenderer(device: device, options: nil)
var measurements: [(String, [Double])] = []

func color(_ hex: UInt32, alpha: CGFloat = 1) -> NSColor {
    NSColor(srgbRed: CGFloat((hex >> 16) & 255)/255, green: CGFloat((hex >> 8) & 255)/255, blue: CGFloat(hex & 255)/255, alpha: alpha)
}
func gray(_ v: CGFloat) -> NSColor { NSColor(srgbRed: v, green: v, blue: v, alpha: 1) }
func mat(_ model: SCNMaterial.LightingModel, _ diffuse: Any = NSColor.white, rough: CGFloat = 1, metal: CGFloat = 0) -> SCNMaterial {
    let m = SCNMaterial(); m.lightingModel = model; m.diffuse.contents = diffuse; m.roughness.contents = rough; m.metalness.contents = metal; return m
}
@discardableResult func light(_ scene: SCNScene, _ type: SCNLight.LightType, _ intensity: CGFloat, euler: SCNVector3 = SCNVector3Zero, color: NSColor = .white) -> SCNNode {
    let n = SCNNode(); n.light = SCNLight(); n.light!.type = type; n.light!.intensity = intensity; n.light!.color = color; n.eulerAngles = euler
    scene.rootNode.addChildNode(n); return n
}
func uniformImage(_ v: UInt8, w: Int = 64, h: Int = 32) -> NSImage {
    let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: w, pixelsHigh: h, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: w*4, bitsPerPixel: 32)!
    for i in 0..<(w*h) { bitmap.bitmapData![i*4] = v; bitmap.bitmapData![i*4+1] = v; bitmap.bitmapData![i*4+2] = v; bitmap.bitmapData![i*4+3] = 255 }
    let image = NSImage(size: NSSize(width: w, height: h)); image.addRepresentation(bitmap); return image
}
func flatScene(_ material: SCNMaterial, geometry: SCNGeometry? = nil, hdr: Bool = false, configure: ((SCNScene) -> Void)? = nil) -> (SCNScene, SCNNode) {
    let scene = SCNScene(); scene.background.contents = NSColor.black
    let plane = geometry ?? SCNPlane(width: 4, height: 4); plane.materials = [material]
    scene.rootNode.addChildNode(SCNNode(geometry: plane))
    let cam = SCNNode(); cam.camera = SCNCamera(); cam.camera!.usesOrthographicProjection = true; cam.camera!.orthographicScale = 1
    cam.camera!.wantsHDR = hdr; cam.camera!.wantsExposureAdaptation = false
    cam.position = SCNVector3(0, 0, 5); scene.rootNode.addChildNode(cam)
    configure?(scene); return (scene, cam)
}
// MARVIN_FACADE_SCALE (default 1) scales the visual scenes (not the 64 px calibration renders), like FacadeTest.cs.
let visualScale = max(1, Int(ProcessInfo.processInfo.environment["MARVIN_FACADE_SCALE"] ?? "1") ?? 1)
func render(_ scene: SCNScene, _ camera: SCNNode, _ w0: Int, _ h0: Int, _ aa: SCNAntialiasingMode = .none) -> NSBitmapImageRep {
    let w = w0 >= 320 ? w0 * visualScale : w0, h = w0 >= 320 ? h0 * visualScale : h0
    renderer.scene = scene; renderer.pointOfView = camera
    let image = renderer.snapshot(atTime: 0, with: CGSize(width: w, height: h), antialiasingMode: aa)
    return NSBitmapImageRep(data: image.tiffRepresentation!)!
}
func lin(_ v: Double) -> Double { v <= 0.04045 ? v/12.92 : pow((v+0.055)/1.055, 2.4) }
@discardableResult func measure(_ name: String, _ b: NSBitmapImageRep, _ x: Int = -1, _ y: Int = -1) -> [Double] {
    let px = x < 0 ? b.pixelsWide/2 : x, py = y < 0 ? b.pixelsHigh/2 : y
    let d = b.bitmapData!, i = py*b.bytesPerRow + px*(b.bitsPerPixel/8)
    let v = [lin(Double(d[i])/255), lin(Double(d[i+1])/255), lin(Double(d[i+2])/255)]
    measurements.append((name, v)); return v
}
func cal(_ name: String, _ s: (SCNScene, SCNNode)) { measure(name, render(s.0, s.1, 64, 64)) }
func save(_ name: String, _ b: NSBitmapImageRep) { try! b.representation(using: .png, properties: [:])!.write(to: outDir.appendingPathComponent(name + ".png")) }
func coloredPlane(_ c: [Float], normals: Bool = true) -> SCNGeometry {
    let v = [SCNVector3(-2,-2,0), SCNVector3(2,-2,0), SCNVector3(2,2,0), SCNVector3(-2,2,0)]
    let colors = c + c + c + c
    let cs = colors.withUnsafeBytes { SCNGeometrySource(data: Data($0), semantic: .color, vectorCount: 4, usesFloatComponents: true, componentsPerVector: 4, bytesPerComponent: 4, dataOffset: 0, dataStride: 16) }
    var sources = [SCNGeometrySource(vertices: v), cs]
    if normals { sources.append(SCNGeometrySource(normals: Array(repeating: SCNVector3(0,0,1), count: 4))) }
    return SCNGeometry(sources: sources, elements: [SCNGeometryElement(indices: [Int32(0),1,2,0,2,3], primitiveType: .triangles)])
}

// =====================================================================
// Calibration (centre-pixel measurements)
cal("C_constant_srgb_0.5", flatScene(mat(.constant, gray(0.5))))
cal("C_constant_calibrated_0.5", flatScene(mat(.constant, NSColor(calibratedRed: 0.5, green: 0.5, blue: 0.5, alpha: 1))))
do { let m = mat(.constant, gray(0.5)); m.emission.contents = gray(0.25); cal("C_constant_plus_emission", flatScene(m)) }
do { let m = mat(.constant, gray(0.5)); cal("C_constant_ambient_ignored", flatScene(m) { light($0, .ambient, 1000) }) }
do { let m = mat(.constant, gray(0.5)); m.multiply.contents = gray(0.5); cal("C_constant_multiply", flatScene(m)) }
for (model, tag) in [(SCNMaterial.LightingModel.physicallyBased, "pbr"), (.lambert, "lambert"), (.blinn, "blinn")] {
    cal("L_\(tag)_dir1000", flatScene(mat(model, gray(0.5))) { light($0, .directional, 1000) })
    cal("L_\(tag)_dir500", flatScene(mat(model, gray(0.5))) { light($0, .directional, 500) })
    cal("L_\(tag)_dir1000_60deg", flatScene(mat(model, gray(0.5))) { light($0, .directional, 1000, euler: SCNVector3(Double.pi/3, 0, 0)) })
}
cal("L_pbr_diffuse0.25_dir1000", flatScene(mat(.physicallyBased, gray(0.25))) { light($0, .directional, 1000) })
cal("L_pbr_calibrated_light", flatScene(mat(.physicallyBased, gray(0.5))) { light($0, .directional, 1000, color: NSColor(calibratedRed: 0.5, green: 0.5, blue: 0.5, alpha: 1)) })
for rough in [0.0, 0.3, 0.6, 1.0] {
    let r = rough == 0 ? "0" : rough == 1 ? "1" : "\(rough)"
    cal("L_pbr_rough\(r)_dir1000", flatScene(mat(.physicallyBased, gray(0.5), rough: rough, metal: 0)) { light($0, .directional, 1000) })
    cal("L_pbr_metal_rough\(r)_dir1000", flatScene(mat(.physicallyBased, gray(0.5), rough: rough, metal: 1)) { light($0, .directional, 1000) })
    cal("L_pbr_rough\(r)_dir1000_60deg", flatScene(mat(.physicallyBased, gray(0.5), rough: rough, metal: 0)) { light($0, .directional, 1000, euler: SCNVector3(Double.pi/3, 0, 0)) })
    cal("I_pbr_rough\(r)_env255", flatScene(mat(.physicallyBased, gray(0.5), rough: rough, metal: 0)) { $0.lightingEnvironment.contents = uniformImage(255) })
    cal("I_pbr_metal_rough\(r)_env128", flatScene(mat(.physicallyBased, gray(0.5), rough: rough, metal: 1)) { $0.lightingEnvironment.contents = uniformImage(128) })
}
cal("I_pbr_env255_intensity0.5", flatScene(mat(.physicallyBased, gray(0.5))) { $0.lightingEnvironment.contents = uniformImage(255); $0.lightingEnvironment.intensity = 0.5 })
cal("I_lambert_env255", flatScene(mat(.lambert, gray(0.5))) { $0.lightingEnvironment.contents = uniformImage(255) })
cal("A_pbr_ambient1000", flatScene(mat(.physicallyBased, gray(0.5))) { light($0, .ambient, 1000) })
cal("A_pbr_ambient500", flatScene(mat(.physicallyBased, gray(0.5))) { light($0, .ambient, 500) })
cal("A_pbr_metal_ambient300", flatScene(mat(.physicallyBased, gray(0.5), rough: 0.3, metal: 1)) { light($0, .ambient, 300) })
do { let m = mat(.physicallyBased, NSColor.black); m.emission.contents = gray(0.5); cal("E_pbr_emission", flatScene(m)) }
do { let m = mat(.physicallyBased, NSColor.black); m.emission.contents = gray(0.5); m.emission.intensity = 0.5; cal("E_pbr_emission_intensity0.5", flatScene(m)) }
do { let m = mat(.physicallyBased, NSColor.white); m.multiply.contents = gray(0.5); cal("M_pbr_multiply", flatScene(m) { light($0, .directional, 1000) }) }
do { let m = mat(.physicallyBased, NSColor.white); m.multiply.contents = gray(0.5); m.multiply.intensity = 0.35; cal("M_pbr_multiply_intensity0.35", flatScene(m) { light($0, .directional, 1000) }) }
cal("V_constant_vertexcolor", flatScene(mat(.constant), geometry: coloredPlane([0.5, 0.2, 0.1, 1])))
cal("V_pbr_vertexcolor_ambient", flatScene(mat(.physicallyBased), geometry: coloredPlane([0.5, 0.2, 0.1, 1])) { light($0, .ambient, 1000) })
cal("N_pbr_no_normals", flatScene(mat(.physicallyBased, gray(0.5)), geometry: coloredPlane([1, 0, 0, 1], normals: false)) { light($0, .directional, 1000); light($0, .ambient, 300) })
for k in [0.1, 0.5, 0.75, 1.5] {
    let m = mat(.constant, NSColor.black); m.emission.contents = NSColor.white; m.emission.intensity = CGFloat(k)
    cal("H_hdr_emission\(k)", flatScene(m, hdr: true))
}
for (start, end, exponent, fogColor, tag) in [(10.0, 20.0, 1.0, NSColor.black, "F_fog_10_20"), (10.0, 20.0, 2.0, NSColor.black, "F_fog_10_20_exp2"), (10.0, 20.0, 1.0, gray(0.5), "F_fog_10_20_gray")] {
    let scene = SCNScene(); scene.background.contents = NSColor.black
    let plane = SCNPlane(width: 60, height: 60); plane.materials = [mat(.constant, NSColor.white)]
    let pn = SCNNode(geometry: plane); pn.position = SCNVector3(0, 0, -10); scene.rootNode.addChildNode(pn)
    scene.fogStartDistance = CGFloat(start); scene.fogEndDistance = CGFloat(end); scene.fogDensityExponent = CGFloat(exponent); scene.fogColor = fogColor
    let cam = SCNNode(); cam.camera = SCNCamera(); cam.camera!.fieldOfView = 90; cam.camera!.zFar = 200; cam.position = SCNVector3(0, 0, 5); scene.rootNode.addChildNode(cam)
    let img = render(scene, cam, 65, 65)
    measure(tag, img, 32, 32); measure(tag + "_corner", img, 0, 0)
}
do { let m = mat(.constant, NSColor(srgbRed: 1, green: 1, blue: 1, alpha: 0.5)); cal("Y_color_alpha0.5_over_red", flatScene(m) { $0.background.contents = NSColor(srgbRed: 0.5, green: 0, blue: 0, alpha: 1) }) }
do { let m = mat(.constant, NSColor.white); m.transparency = 0.5; cal("Y_transparency0.5_over_red", flatScene(m) { $0.background.contents = NSColor(srgbRed: 0.5, green: 0, blue: 0, alpha: 1) }) }
do { let m = mat(.constant, NSColor.white); cal("Y_opacity0.5_over_red", flatScene(m) { $0.background.contents = NSColor(srgbRed: 0.5, green: 0, blue: 0, alpha: 1); $0.rootNode.childNodes[0].opacity = 0.5 }) }
do { let m = mat(.constant, gray(0.5)); m.blendMode = .multiply; cal("Y_multiply_blend", flatScene(m) { $0.background.contents = gray(0.8) }) }
do {
    let m = mat(.constant, NSColor.white); m.transparencyMode = .aOne
    m.shaderModifiers = [.fragment: "#pragma transparent\n#pragma body\n_output.color.rgb *= 0.25;\n_output.color.a = 0.25;"]
    cal("Y_fragment_alpha_idiom", flatScene(m) { $0.background.contents = NSColor(srgbRed: 0.5, green: 0, blue: 0, alpha: 1) })
}
for (distance, tag) in [(6.0, "D_lod_near"), (14.0, "D_lod_far")] {
    let scene = SCNScene(); scene.background.contents = NSColor.black
    let near = SCNBox(width: 2, height: 2, length: 2, chamferRadius: 0); near.materials = [mat(.constant, NSColor.red)]
    let far = SCNBox(width: 2, height: 2, length: 2, chamferRadius: 0); far.materials = [mat(.constant, NSColor.green)]
    near.levelsOfDetail = [SCNLevelOfDetail(geometry: far, worldSpaceDistance: 10)]
    scene.rootNode.addChildNode(SCNNode(geometry: near))
    let cam = SCNNode(); cam.camera = SCNCamera(); cam.position = SCNVector3(0, 0, distance); scene.rootNode.addChildNode(cam)
    cal(tag, (scene, cam))
}
do {
    let scene = SCNScene(); scene.background.contents = NSColor.black
    let plane = SCNPlane(width: 1, height: 1); plane.materials = [mat(.constant, gray(0.5))]
    let n = SCNNode(geometry: plane); n.eulerAngles.y = .pi/2; n.constraints = [SCNBillboardConstraint()]
    scene.rootNode.addChildNode(n)
    let cam = SCNNode(); cam.camera = SCNCamera(); cam.position = SCNVector3(3, 1, 3); cam.look(at: SCNVector3Zero, up: SCNVector3(0, 1, 0), localFront: SCNVector3(0, 0, -1))
    scene.rootNode.addChildNode(cam)
    cal("B_billboard", (scene, cam))
}
do {
    let descriptor = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .r32Float, width: 2, height: 1, mipmapped: false)
    descriptor.usage = .shaderRead
    let texture = device.makeTexture(descriptor: descriptor)!
    let values: [Float] = [0.25, 0.75]
    values.withUnsafeBytes { texture.replace(region: MTLRegionMake2D(0, 0, 2, 1), mipmapLevel: 0, withBytes: $0.baseAddress!, bytesPerRow: 8) }
    let heights = SCNMaterialProperty(); heights.contents = texture
    let plane = SCNPlane(width: 4, height: 4)
    plane.materials = [mat(.constant, NSColor.white)]
    plane.shaderModifiers = [.surface: "#pragma arguments\ntexture2d<float> duneHeights;\n#pragma body\n_surface.diffuse.rgb = float3(duneHeights.read(uint2(1,0)).r, duneHeights.read(uint2(0,0)).r, 0.0);"]
    plane.setValue(heights, forKey: "duneHeights")
    let scene = SCNScene(); scene.background.contents = NSColor.black
    scene.rootNode.addChildNode(SCNNode(geometry: plane))
    let cam = SCNNode(); cam.camera = SCNCamera(); cam.camera!.usesOrthographicProjection = true; cam.position = SCNVector3(0, 0, 5); scene.rootNode.addChildNode(cam)
    cal("G_texture_argument", (scene, cam))
}
do {
    let cyl = SCNCylinder(radius: 1.5, height: 1); cyl.materials = [mat(.constant, NSColor.red), mat(.constant, NSColor.green)]
    let scene = SCNScene(); scene.background.contents = NSColor.black
    scene.rootNode.addChildNode(SCNNode(geometry: cyl))
    for (y, tag) in [(5.0, "K_cycle_top"), (-5.0, "K_cycle_bottom")] {
        let cam = SCNNode(); cam.camera = SCNCamera(); cam.camera!.usesOrthographicProjection = true
        cam.position = SCNVector3(0, y, 0); cam.eulerAngles.x = y > 0 ? -.pi/2 : .pi/2; scene.rootNode.addChildNode(cam)
        cal(tag, (scene, cam))
    }
}
for (mode, alpha, constantFloor, tag) in [(SCNShadowMode.forward, 1.0, false, "S_forward_alpha1"), (.forward, 0.5, false, "S_forward_alpha0.5"),
                                         (.deferred, 0.24, false, "S_deferred_alpha0.24"), (.deferred, 0.24, true, "S_deferred_alpha0.24_constant")] {
    let scene = SCNScene(); scene.background.contents = NSColor.black
    let floor = SCNPlane(width: 8, height: 8); floor.materials = [mat(constantFloor ? .constant : .physicallyBased, gray(0.8))]
    let fn = SCNNode(geometry: floor); fn.eulerAngles.x = -.pi/2; scene.rootNode.addChildNode(fn)
    let box = SCNBox(width: 1, height: 1, length: 1, chamferRadius: 0); box.materials = [mat(.physicallyBased)]
    let bn = SCNNode(geometry: box); bn.position = SCNVector3(0, 1, 0); scene.rootNode.addChildNode(bn)
    let sun = light(scene, .directional, 1000, euler: SCNVector3(-Double.pi/4, 0, 0))
    sun.light!.castsShadow = true; sun.light!.shadowMode = mode; sun.light!.shadowColor = NSColor.black.withAlphaComponent(CGFloat(alpha)); sun.light!.orthographicScale = 5
    light(scene, .ambient, 300)
    let cam = SCNNode(); cam.camera = SCNCamera(); cam.camera!.usesOrthographicProjection = true; cam.camera!.orthographicScale = 4
    cam.position = SCNVector3(0, 10, 0); cam.eulerAngles.x = -.pi/2; scene.rootNode.addChildNode(cam)
    let img = render(scene, cam, 64, 64)
    measure(tag + "_lit", img, 32, 4); measure(tag + "_shadow", img, 32, 22); save(tag, img)
}

// =====================================================================
// Visual scenes
func material(_ hex: UInt32, metal: CGFloat = 0, roughness: CGFloat = 0.6) -> SCNMaterial {
    let m = SCNMaterial(); m.lightingModel = .physicallyBased
    m.diffuse.contents = color(hex); m.metalness.contents = metal; m.roughness.contents = roughness; m.isDoubleSided = true
    return m
}
do { // V_primitives
    let scene = SCNScene()
    scene.background.contents = color(0xdbe4df)
    scene.fogColor = color(0xdbe4df); scene.fogStartDistance = 15; scene.fogEndDistance = 38
    let camera = SCNNode(); camera.camera = SCNCamera()
    camera.camera!.fieldOfView = 48; camera.camera!.zNear = 0.02; camera.camera!.zFar = 80
    camera.position = SCNVector3(0, 3.2, 6.5)
    camera.look(at: SCNVector3(0, 0.4, 0), up: SCNVector3(0, 1, 0), localFront: SCNVector3(0, 0, -1))
    scene.rootNode.addChildNode(camera)
    light(scene, .ambient, 550, color: color(0xe7f4ff))
    let sun = light(scene, .directional, 1400, euler: SCNVector3(-0.85, -0.45, -0.25), color: color(0xfff1df))
    sun.light!.castsShadow = true; sun.light!.shadowMode = .deferred
    sun.light!.shadowMapSize = CGSize(width: 4096, height: 4096); sun.light!.shadowSampleCount = 16
    sun.light!.shadowColor = NSColor.black.withAlphaComponent(0.24); sun.light!.orthographicScale = 10; sun.light!.maximumShadowDistance = 22
    @discardableResult func box(_ x: Double, _ y: Double, _ z: Double, _ w: Double, _ h: Double, _ d: Double, _ m: SCNMaterial, radius: Double = 0) -> SCNNode {
        let g = SCNBox(width: w, height: h, length: d, chamferRadius: radius); g.materials = [m]
        let n = SCNNode(geometry: g); n.position = SCNVector3(x, y, z); scene.rootNode.addChildNode(n); return n
    }
    box(0, -0.175, 0, 12.5, 0.3, 10.5, material(0x927455, roughness: 0.95), radius: 0.14)
    box(0, -0.05, 0, 12, 0.05, 10, material(0xb49470, roughness: 0.98), radius: 0.04)
    box(-1.6, 0.4, 0, 0.8, 0.8, 0.8, material(0x96b4a7), radius: 0.07)
    box(1.6, 0.25, 0.8, 0.6, 0.5, 1.2, material(0xd2b59a), radius: 0.07).eulerAngles.y = 0.4
    let sphere = SCNSphere(radius: 0.45); sphere.materials = [material(0xc3c7c9, metal: 0.8, roughness: 0.65)]
    let sn = SCNNode(geometry: sphere); sn.position = SCNVector3(0, 0.45, 0.4); scene.rootNode.addChildNode(sn)
    let cyl = SCNCylinder(radius: 0.3, height: 0.9); cyl.radialSegmentCount = 48; cyl.materials = [material(0x2b8e7f)]
    let cn = SCNNode(geometry: cyl); cn.position = SCNVector3(0.2, 0.45, -1.4); scene.rootNode.addChildNode(cn)
    let wheel = SCNCylinder(radius: 0.25, height: 0.12); wheel.materials = [material(0x555653, roughness: 0.94)]
    let wn = SCNNode(geometry: wheel); wn.position = SCNVector3(-1.6, 0.25, 1.3); wn.eulerAngles.z = .pi/2; scene.rootNode.addChildNode(wn)
    let path = NSBezierPath(); path.move(to: NSPoint(x: -0.15, y: -0.12))
    path.line(to: NSPoint(x: 0, y: 0.15)); path.line(to: NSPoint(x: 0.15, y: -0.12)); path.line(to: NSPoint(x: 0, y: -0.02)); path.close()
    let arrowShape = SCNShape(path: path, extrusionDepth: 0); arrowShape.materials = [material(0xd6ba85)]
    let arrow = SCNNode(geometry: arrowShape); arrow.scale = SCNVector3(3, 3, 3); arrow.position = SCNVector3(1.4, 0.006, 2.2)
    arrow.eulerAngles = SCNVector3(-Double.pi/2, 0.6, 0); scene.rootNode.addChildNode(arrow)
    let holes = NSBezierPath(rect: NSRect(x: -1, y: -0.6, width: 2, height: 1.2)); holes.windingRule = .evenOdd
    for k in 0..<2 {
        let cx = -0.45 + 0.9 * Double(k)
        for i in 0..<128 {
            let a = Double(i) * 2 * Double.pi / 128
            let p = NSPoint(x: cx + 0.35 * cos(a), y: 0.35 * sin(a))
            if i == 0 { holes.move(to: p) } else { holes.line(to: p) }
        }
        holes.close()
    }
    let holeShape = SCNShape(path: holes, extrusionDepth: 0); holeShape.materials = [material(0xa48766, roughness: 0.98)]
    let hn = SCNNode(geometry: holeShape); hn.position = SCNVector3(-0.5, 0.003, 2.6); hn.eulerAngles.x = -.pi/2; scene.rootNode.addChildNode(hn)
    let star = NSBezierPath()
    for i in 0..<10 { let a = Double.pi/2 + Double(i) * Double.pi/5, r = i % 2 == 0 ? 0.35 : 0.15; let p = NSPoint(x: r*cos(a), y: r*sin(a)); if i == 0 { star.move(to: p) } else { star.line(to: p) } }
    star.close()
    let starShape = SCNShape(path: star, extrusionDepth: 0.1); starShape.materials = [material(0xc83a24, roughness: 0.45)]
    let stn = SCNNode(geometry: starShape); stn.position = SCNVector3(2.6, 0.5, -0.6); stn.eulerAngles.y = -0.5; scene.rootNode.addChildNode(stn)
    let text = SCNText(string: "12", extrusionDepth: 0); text.font = NSFont.monospacedSystemFont(ofSize: 1, weight: .semibold); text.flatness = 0.2
    text.materials = [material(0x4e756a)]
    let label = SCNNode(geometry: text)
    let bounds = text.boundingBox
    let height = bounds.max.y - bounds.min.y, scale = CGFloat(0.36)/height
    label.scale = SCNVector3(scale, scale, scale)
    label.pivot = SCNMatrix4MakeTranslation((bounds.min.x+bounds.max.x)/2, (bounds.min.y+bounds.max.y)/2, 0)
    label.eulerAngles.x = -.pi/2; label.position = SCNVector3(-0.4, 0.0008, 1.4); label.castsShadow = false
    scene.rootNode.addChildNode(label)
    let hub = SCNNode(); hub.position = SCNVector3(2.4, 0.6, 1.2); hub.eulerAngles = SCNVector3(0.3, -0.7, 0.2)
    for i in 0..<5 {
        let spoke = SCNBox(width: 0.04, height: 0.5, length: 0.08, chamferRadius: 0.01); spoke.materials = [material(0x8b969e, metal: 0.75, roughness: 0.38)]
        let s = SCNNode(geometry: spoke); s.eulerAngles.x = CGFloat(Double(i) * 2 * Double.pi / 5)
        s.position = SCNVector3(0, cos(Double(i) * 2 * Double.pi / 5) * 0.2, sin(Double(i) * 2 * Double.pi / 5) * 0.2)
        hub.addChildNode(s)
    }
    scene.rootNode.addChildNode(hub)
    save("V_primitives", render(scene, camera, 640, 400, .multisampling4X))
}
do { // V_sky: the game's BinarySky dome modifiers (MSL, verbatim).
    let scene = SCNScene()
    let camera = SCNNode(); camera.camera = SCNCamera()
    camera.camera!.fieldOfView = 48; camera.camera!.zNear = 0.02; camera.camera!.zFar = 250
    camera.camera!.wantsHDR = true; camera.camera!.wantsExposureAdaptation = false; camera.camera!.exposureOffset = 0
    camera.camera!.bloomIntensity = 0.38; camera.camera!.bloomThreshold = 1.2; camera.camera!.bloomBlurRadius = 12
    camera.position = SCNVector3(0, 2, 6); scene.rootNode.addChildNode(camera)
    let sky = SCNMaterial(); sky.lightingModel = .constant; sky.cullMode = .front; sky.readsFromDepthBuffer = false; sky.writesToDepthBuffer = false
    sky.shaderModifiers = [
        .geometry: """
        #pragma varyings
        float3 skyDirection;
        #pragma body
        out.skyDirection = _geometry.position.xyz;
        """,
        .fragment: """
        #pragma arguments
        float3 sunA;
        float3 sunB;
        float3 zenith;
        float3 horizon;
        float3 tintA;
        float3 tintB;
        float2 sunRadii;
        float daylight;
        float3 duskBand;
        float storm;
        float3 stormTint;
        #pragma body
        float3 d=normalize(in.skyDirection);
        float height=max(d.y,0.0);
        float h=1.0-exp(-height/mix(0.085,0.35,daylight));
        float3 sky=mix(horizon,zenith,h);
        float3 solarAxis=normalize(sunA+sunB);
        float facing=pow(max(0.0,dot(d.xz,solarAxis.xz)/(max(length(d.xz),0.00001)*max(length(solarAxis.xz),0.00001))),3.0);
        float band=exp(-pow((height-0.12)/0.10,2.0));
        sky+=duskBand*band*(1.0-daylight)*(0.35+0.65*facing);
        sky+=float3(0.34,0.095,0.018)*facing*exp(-height/0.15)*(1.0-daylight);
        float a=acos(clamp(dot(d,sunA),-1.0,1.0));
        float b=acos(clamp(dot(d,sunB),-1.0,1.0));
        float haze=mix(1.0,0.35,daylight);
        sky+=tintA*haze*(0.5*exp(-a*a/0.006)+0.65*exp(-a/0.017));
        sky+=tintB*haze*(0.3*exp(-b*b/0.004)+0.4*exp(-b/0.013));
        float discA=1.0-smoothstep(sunRadii.x*0.91,sunRadii.x*1.05,a);
        float discB=1.0-smoothstep(sunRadii.y*0.91,sunRadii.y*1.05,b);
        float limbA=sqrt(max(0.0,1.0-pow(a/sunRadii.x,2.0)));
        float limbB=sqrt(max(0.0,1.0-pow(b/sunRadii.y,2.0)));
        sky=mix(sky,tintA*(3.8+2.4*limbA),discA);
        sky=mix(sky,tintB*(2.5+1.5*limbB),discB);
        // This is an infinitely distant sky: bypass scene distance fog.
        _output.color=float4(mix(sky,stormTint+sky*0.025,storm*0.97),1.0);
        """]
    let sphere = SCNSphere(radius: 220); sphere.segmentCount = 48; sphere.materials = [sky]
    let dome = SCNNode(geometry: sphere); dome.castsShadow = false; dome.renderingOrder = -10000
    dome.constraints = [SCNTransformConstraint.positionConstraint(inWorldSpace: true) { _, _ in camera.presentation.worldPosition }]
    scene.rootNode.addChildNode(dome)
    let day: Float = 0.25
    let lowZenith = SIMD3<Float>(0.065, 0.027, 0.10), lowHorizon = SIMD3<Float>(0.85, 0.19, 0.035)
    let zenith = lowZenith + (SIMD3<Float>(0.20, 0.38, 0.62) - lowZenith) * day
    let horizon = lowHorizon + (SIMD3<Float>(0.65, 0.72, 0.75) - lowHorizon) * day
    func norm(_ v: SIMD3<Double>) -> SIMD3<Double> { v / (v*v).sum().squareRoot() }
    let sunA = norm(SIMD3(-0.55, 0.14, -0.82)), sunB = norm(SIMD3(-0.62, 0.10, -0.78))
    sky.setValue(NSValue(scnVector3: SCNVector3(sunA.x, sunA.y, sunA.z)), forKey: "sunA"); sky.setValue(NSValue(scnVector3: SCNVector3(sunB.x, sunB.y, sunB.z)), forKey: "sunB")
    sky.setValue(NSValue(scnVector3: SCNVector3(zenith)), forKey: "zenith"); sky.setValue(NSValue(scnVector3: SCNVector3(horizon)), forKey: "horizon")
    sky.setValue(NSValue(scnVector3: SCNVector3(1, 0.62, 0.30)), forKey: "tintA"); sky.setValue(NSValue(scnVector3: SCNVector3(1, 0.42, 0.14)), forKey: "tintB")
    sky.setValue(NSValue(point: NSPoint(x: 0.55 * Double.pi / 180 * 6, y: 0.38 * Double.pi / 180 * 6)), forKey: "sunRadii")
    sky.setValue(Float(day), forKey: "daylight"); sky.setValue(NSValue(scnVector3: SCNVector3(0.22, 0.045, 0.085)), forKey: "duskBand")
    sky.setValue(Float(0), forKey: "storm"); sky.setValue(NSValue(scnVector3: SCNVector3(0.46, 0.29, 0.14)), forKey: "stormTint")
    camera.look(at: SCNVector3(sunA.x * 20, 1.5, sunA.z * 20), up: SCNVector3(0, 1, 0), localFront: SCNVector3(0, 0, -1))
    scene.fogColor = NSColor(calibratedRed: CGFloat(horizon.x), green: CGFloat(horizon.y), blue: CGFloat(horizon.z), alpha: 1); scene.fogStartDistance = 15; scene.fogEndDistance = 120
    let ground = SCNPlane(width: 400, height: 400); ground.materials = [material(0x827656, roughness: 1)]
    let gn = SCNNode(geometry: ground); gn.eulerAngles.x = -.pi/2; scene.rootNode.addChildNode(gn)
    for i in 0..<12 {
        let post = SCNBox(width: 0.4, height: 2.5, length: 0.4, chamferRadius: 0.05); post.materials = [material(0x805443, roughness: 0.94)]
        let pn = SCNNode(geometry: post); pn.position = SCNVector3(-4 + Double(i % 4) * 2.5 - 8, 1.25, -6 - Double(i / 4) * 18 - 4); scene.rootNode.addChildNode(pn)
    }
    let s1 = light(scene, .directional, 1550, color: NSColor(calibratedRed: 1, green: 0.62, blue: 0.30, alpha: 1))
    s1.position = SCNVector3(sunA.x * 80, sunA.y * 80, sunA.z * 80); s1.look(at: SCNVector3Zero, up: SCNVector3(0, 1, 0), localFront: SCNVector3(0, 0, -1))
    light(scene, .ambient, 200, color: NSColor(calibratedRed: 0.54, green: 0.62, blue: 0.78, alpha: 1))
    save("V_sky", render(scene, camera, 640, 400, .multisampling4X))
}
// TownGround / DirtTrail modifiers (MSL, verbatim from the game).
let pigmentFunctions = """
float townNoise(float2 p) {
    float2 i=floor(p),f=fract(p);f=f*f*(3.0-2.0*f);
    float4 h=fract(sin(float4(dot(i,float2(127.1,311.7)),dot(i+float2(1,0),float2(127.1,311.7)),dot(i+float2(0,1),float2(127.1,311.7)),dot(i+1.0,float2(127.1,311.7))))*43758.5453);
    return mix(mix(h.x,h.y,f.x),mix(h.z,h.w,f.x),f.y);
}
float3 townPigment(float2 p) {
    float2 warp=float2(townNoise(p/31.0),townNoise(p/37.0+19.0))*9.0;
    float broad=townNoise((p+warp)/22.0),fine=townNoise((p+warp)/5.5);
    float pale=smoothstep(0.28,0.72,broad*0.50+fine*0.50);
    float3 soil=mix(float3(0.255,0.208,0.145),float3(0.46,0.36,0.235),pale);
    return soil*(0.89+0.22*townNoise(p/2.1+7.0));
}
"""
let terrainSurface = pigmentFunctions + "\n" + """
#pragma body
float2 p=(scn_frame.inverseViewTransform*float4(_surface.position,1.0)).xz;
float angle=atan2(p.y,p.x);
float radius=123.0+13.0*sin(3.0*angle+0.4)+9.0*cos(5.0*angle-0.7)+6.0*sin(2.0*angle);
float desert=smoothstep(radius-12.0,radius+32.0,length(p));
float grain=dot(_surface.diffuse.rgb,float3(0.299,0.587,0.114));
float bands=townNoise(p/43.0);
float3 dune=float3(0.64,0.43,0.23)*(0.83+0.30*grain)+bands*float3(0.075,0.058,0.029);
float3 soil=mix(_surface.diffuse.rgb,townPigment(p)*(1.30+grain),smoothstep(28.0,42.0,length(p)));
_surface.diffuse.rgb=mix(soil,dune,desert);
float ripple=sin(p.x*17.0+p.y*5.8+1.8*sin(p.y*0.41)+sin(p.x*0.22));
_surface.normal=normalize(_surface.normal+float3(0.022*ripple,0.0,0.008*ripple)*desert);
"""
func surfaceNoise(_ u: Double, _ v: Double, cells: Int, seed: Int) -> Double {
    let x = u*Double(cells), y = v*Double(cells), ix = Int(floor(x)), iy = Int(floor(y))
    let fx = x-floor(x), fy = y-floor(y), tx = fx*fx*(3-2*fx), ty = fy*fy*(3-2*fy)
    func hash(_ a: Int, _ b: Int) -> Double {
        var n = UInt32(truncatingIfNeeded: ((a%cells+cells)%cells) &* 374761393 &+ ((b%cells+cells)%cells) &* 668265263 &+ seed &* 1274126177)
        n = (n ^ (n >> 13)) &* 1274126177
        return Double((n ^ (n >> 16)) & 65535)/65535
    }
    let lo = hash(ix,iy)*(1-tx)+hash(ix+1,iy)*tx, hi = hash(ix,iy+1)*(1-tx)+hash(ix+1,iy+1)*tx
    return lo*(1-ty)+hi*ty
}
func packedEarthTexture() -> NSImage {
    let size = 512
    let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: size*4, bitsPerPixel: 32)!
    let bytes = bitmap.bitmapData!
    for y in 0..<size { for x in 0..<size {
        var seed = UInt32(truncatingIfNeeded: x &* 374761393 &+ y &* 668265263)
        seed = (seed ^ (seed >> 13)) &* 1274126177
        let grain = Double((seed ^ (seed >> 16)) & 255)/255
        let u = Double(x)/Double(size), v = Double(y)/Double(size)
        let soil = (surfaceNoise(u, v, cells: 5, seed: 11)-0.5)*7+(surfaceNoise(u, v, cells: 21, seed: 31)-0.5)*5
        let pebble = grain > 0.984 ? -19.0 : 0.0
        let value = soil+(grain-0.5)*16+pebble
        let i = (y*size+x)*4
        bytes[i] = UInt8(157+value); bytes[i+1] = UInt8(140+value); bytes[i+2] = UInt8(115+value); bytes[i+3] = 255
    }}
    let image = NSImage(size: NSSize(width: size, height: size)); image.addRepresentation(bitmap); return image
}
do { // V_ground
    let scene = SCNScene(); scene.background.contents = color(0xb9c5ca)
    let camera = SCNNode(); camera.camera = SCNCamera()
    camera.camera!.fieldOfView = 48; camera.camera!.zNear = 0.02; camera.camera!.zFar = 250
    camera.position = SCNVector3(30, 9, 30); camera.look(at: SCNVector3(36, 0, 18), up: SCNVector3(0, 1, 0), localFront: SCNVector3(0, 0, -1))
    scene.rootNode.addChildNode(camera)
    light(scene, .ambient, 260, color: NSColor(calibratedRed: 0.54, green: 0.62, blue: 0.78, alpha: 1))
    let sun = light(scene, .directional, 1550, color: NSColor(calibratedRed: 1, green: 0.94, blue: 0.83, alpha: 1))
    sun.position = SCNVector3(-40, 60, 30); sun.look(at: SCNVector3(30, 0, 20), up: SCNVector3(0, 1, 0), localFront: SCNVector3(0, 0, -1))
    sun.light!.castsShadow = true; sun.light!.shadowMode = .forward; sun.light!.automaticallyAdjustsShadowProjection = false; sun.light!.orthographicScale = 58
    let earth = material(0x827656, roughness: 1)
    earth.diffuse.contents = packedEarthTexture()
    earth.diffuse.wrapS = .repeat; earth.diffuse.wrapT = .repeat
    earth.diffuse.contentsTransform = SCNMatrix4MakeScale(64, 64, 1)
    earth.shaderModifiers = [.surface: terrainSurface]
    var gv: [SCNVector3] = [], guv: [CGPoint] = [], gnrm: [SCNVector3] = [], gidx: [Int32] = []
    for y in stride(from: -128, to: 128, by: 4) { for x in stride(from: -128, to: 128, by: 4) {
        let b = Int32(gv.count)
        for (dx, dy) in [(0, 0), (4, 0), (4, 4), (0, 4)] {
            gv.append(SCNVector3(x + dx, y + dy, 0)); gnrm.append(SCNVector3(0, 0, 1))
            guv.append(CGPoint(x: Double(x + dx + 128) / 256, y: Double(128 - y - dy) / 256))
        }
        gidx += [b, b + 1, b + 2, b, b + 2, b + 3]
    }}
    let ground = SCNGeometry(sources: [SCNGeometrySource(vertices: gv), SCNGeometrySource(normals: gnrm), SCNGeometrySource(textureCoordinates: guv)],
                             elements: [SCNGeometryElement(indices: gidx, primitiveType: .triangles)])
    ground.materials = [earth]
    let gn = SCNNode(geometry: ground); gn.eulerAngles.x = -.pi/2; gn.position = SCNVector3(0, -0.025, 0); scene.rootNode.addChildNode(gn)
    let overlay = SCNMaterial(); overlay.lightingModel = .physicallyBased
    overlay.roughness.contents = 1.0; overlay.diffuse.contents = NSColor.white
    overlay.writesToDepthBuffer = false; overlay.transparencyMode = .aOne
    overlay.shaderModifiers = [.geometry: """
    #pragma varyings
    half4 groundTint;
    #pragma body
    out.groundTint=half4(half3(pow(max(_geometry.color.rgb,float3(0)),float3(2.2))),half(_geometry.color.a));
    """, .surface: "_surface.diffuse.rgb=float3(in.groundTint.rgb);", .fragment: """
    #pragma transparent
    #pragma body
    _output.color.rgb *= float(in.groundTint.a);
    _output.color.a=float(in.groundTint.a);
    """]
    var verts: [SCNVector3] = [], colors: [Float] = [], idx: [Int32] = []
    for k in 0..<6 {
        let cx = 30 + Double(k) * 2.2, cz = 20 - Double(k) * 0.8
        let center = Int32(verts.count); verts.append(SCNVector3(cx, -0.019, cz)); colors += [0x8e/255, 0x80/255, 0x6a/255, 0.6]
        for i in 0..<18 {
            let a = Double(i) * 2 * Double.pi / 18
            verts.append(SCNVector3(cx + cos(a) * 1.1, -0.019, cz + sin(a) * 0.8)); colors += [0x8e/255, 0x80/255, 0x6a/255, 0]
        }
        for i in 0..<18 { idx += [center, center + 1 + Int32((i + 1) % 18), center + 1 + Int32(i)] }
    }
    let tint = colors.withUnsafeBytes { SCNGeometrySource(data: Data($0), semantic: .color, vectorCount: verts.count, usesFloatComponents: true, componentsPerVector: 4, bytesPerComponent: 4, dataOffset: 0, dataStride: 16) }
    let patches = SCNGeometry(sources: [SCNGeometrySource(vertices: verts), SCNGeometrySource(normals: Array(repeating: SCNVector3(0, 1, 0), count: verts.count)), tint],
                              elements: [SCNGeometryElement(indices: idx, primitiveType: .triangles)])
    patches.materials = [overlay]
    let pn = SCNNode(geometry: patches); pn.castsShadow = false; scene.rootNode.addChildNode(pn)
    let ink = material(0xffffff, roughness: 1)
    ink.lightingModel = .constant; ink.blendMode = .multiply
    ink.shaderModifiers = [.geometry: """
    #pragma varyings
    float impressionStrength;
    float trailBorn;
    float trailDistance;
    #pragma body
    out.impressionStrength=_geometry.color.r;
    out.trailBorn=_geometry.color.g;
    out.trailDistance=length((scn_node.modelViewTransform*_geometry.position).xyz);
    """, .surface: """
    #pragma transparent
    #pragma body
    float2 uv=_surface.diffuseTexcoord;
    float across=smoothstep(0.0,0.15,uv.x)*(1.0-smoothstep(0.85,1.0,uv.x));
    float along=smoothstep(0.0,0.24,uv.y)*(1.0-smoothstep(0.65,1.0,uv.y));
    float shade=1.0-in.impressionStrength*across*along;
    _surface.diffuse=float4(float3(shade),1.0);
    """, .fragment: """
    #pragma arguments
    float stormTime;
    float stormActive;
    #pragma body
    if(stormActive>0.5) {
        float age=max(0.0,stormTime-in.trailBorn);
        float visibility=exp(-age/10.0)*(1.0-smoothstep(3.0,65.0,in.trailDistance));
        _output.color=float4(mix(float3(1.0),_surface.diffuse.rgb,visibility),1.0);
    }
    """]
    ink.setValue(Float(0), forKey: "stormTime"); ink.setValue(Float(0), forKey: "stormActive")
    ink.isDoubleSided = true; ink.writesToDepthBuffer = false
    var tv: [SCNVector3] = [], tuv: [CGPoint] = [], tcol: [Float] = [], tidx: [Int32] = []
    for i in 0..<120 {
        let t = Double(i) * 0.065, x = 33 + t, z = 22 + sin(t * 0.8) * 1.5
        for side in [-0.3, 0.3] {
            let base = Int32(tv.count)
            for (sx, sz) in [(-1.0, -1.0), (1.0, -1.0), (1.0, 1.0), (-1.0, 1.0)] {
                tv.append(SCNVector3(x + sz * 0.021, 0.007 - 0.025, z + side + sx * 0.08))
                tuv.append(CGPoint(x: (sx + 1) / 2, y: (sz + 1) / 2)); tcol += [0.24, 0, 0, 1]
            }
            tidx += [base, base + 2, base + 1, base, base + 3, base + 2]
        }
    }
    let tcolors = tcol.withUnsafeBytes { SCNGeometrySource(data: Data($0), semantic: .color, vectorCount: tv.count, usesFloatComponents: true, componentsPerVector: 4, bytesPerComponent: 4, dataOffset: 0, dataStride: 16) }
    let trail = SCNGeometry(sources: [SCNGeometrySource(vertices: tv), SCNGeometrySource(textureCoordinates: tuv), SCNGeometrySource(normals: Array(repeating: SCNVector3(0, 1, 0), count: tv.count)), tcolors],
                            elements: [SCNGeometryElement(indices: tidx, primitiveType: .triangles)])
    trail.materials = [ink]
    let tn = SCNNode(geometry: trail); tn.castsShadow = false; tn.renderingOrder = 10; scene.rootNode.addChildNode(tn)
    let rock = SCNBox(width: 1.2, height: 1.2, length: 1.2, chamferRadius: 0.1); rock.materials = [material(0xa28a70)]
    let rn = SCNNode(geometry: rock); rn.position = SCNVector3(37, 0.6, 19); rn.eulerAngles.y = 0.5; scene.rootNode.addChildNode(rn)
    save("V_ground", render(scene, camera, 640, 400, .multisampling4X))
}
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
do { // V_materials
    let scene = SCNScene(); scene.background.contents = color(0x30343a)
    let camera = SCNNode(); camera.camera = SCNCamera(); camera.camera!.fieldOfView = 36
    camera.position = SCNVector3(0, 0, 9); scene.rootNode.addChildNode(camera)
    scene.lightingEnvironment.contents = skyProbe(zenith: SIMD3(0.20, 0.38, 0.62), horizon: SIMD3(0.65, 0.72, 0.75))
    scene.lightingEnvironment.intensity = 0.95
    light(scene, .directional, 1550, euler: SCNVector3(-0.7, 0.6, 0), color: NSColor(calibratedRed: 1, green: 0.94, blue: 0.83, alpha: 1))
    light(scene, .ambient, 200, color: NSColor(calibratedRed: 0.54, green: 0.62, blue: 0.78, alpha: 1))
    for i in 0..<5 { for j in 0..<3 {
        let s = SCNSphere(radius: 0.42); s.segmentCount = 48
        s.materials = [material(0xc3c7c9, metal: CGFloat(j) * 0.5, roughness: 0.1 + CGFloat(i) * 0.2)]
        let n = SCNNode(geometry: s); n.position = SCNVector3(-2.2 + Double(i) * 1.1, 1.1 - Double(j) * 1.1, 0); scene.rootNode.addChildNode(n)
    }}
    save("V_materials", render(scene, camera, 640, 400, .multisampling4X))
}
do { // V_sign: TownSigns.image(...) verbatim.
    func image(_ title: String, eyebrow: String, footer: String, badge: String, aspect: CGFloat, accent: UInt32) -> NSBitmapImageRep {
        let w = 1024, h = Int((1024/aspect).rounded())
        let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: w, pixelsHigh: h, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: w*4, bitsPerPixel: 32)!
        NSGraphicsContext.saveGraphicsState(); NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bitmap)
        let W = CGFloat(w), H = CGFloat(h)
        func color(_ rgb: UInt32) -> NSColor { NSColor(calibratedRed: CGFloat((rgb>>16)&255)/255, green: CGFloat((rgb>>8)&255)/255, blue: CGFloat(rgb&255)/255, alpha: 1) }
        color(0x263c3c).setFill(); NSRect(x: 0, y: 0, width: W, height: H).fill()
        color(accent).setFill(); NSRect(x: 0, y: H-10, width: W, height: 10).fill()
        color(0xb8aa8c).withAlphaComponent(0.45).setStroke()
        let outline = NSBezierPath(rect: NSRect(x: 12, y: 12, width: W-24, height: H-31)); outline.lineWidth = 2; outline.stroke()
        let badgeW = H*0.55, left = badgeW+H*0.15, right = W-H*0.10
        color(accent).setFill(); NSRect(x: H*0.10, y: H*0.25, width: badgeW, height: H*0.49).fill()
        func text(_ value: String, in rect: NSRect, size: CGFloat, ink: UInt32, weight: NSFont.Weight, tracking: CGFloat = 0) {
            let paragraph = NSMutableParagraphStyle(); paragraph.alignment = .center; paragraph.lineBreakMode = .byClipping
            var size = size
            func attributes() -> [NSAttributedString.Key: Any] {
                [.font: NSFont(name: weight == .bold ? "AvenirNextCondensed-DemiBold" : "AvenirNextCondensed-Medium", size: size) ?? NSFont.systemFont(ofSize: size, weight: weight), .foregroundColor: color(ink), .paragraphStyle: paragraph, .kern: tracking]
            }
            while size > 8 {
                let measured = NSAttributedString(string: value, attributes: attributes()).size()
                if measured.width <= rect.width && measured.height <= rect.height { break }
                size -= 1
            }
            let string = NSAttributedString(string: value, attributes: attributes()), measured = string.size()
            string.draw(in: NSRect(x: rect.minX, y: rect.midY-measured.height/2, width: rect.width, height: measured.height+1))
        }
        text(badge, in: NSRect(x: H*0.10, y: H*0.25, width: badgeW, height: H*0.49), size: H*0.30, ink: 0x263c3c, weight: .bold)
        let x = left+H*0.08, tw = right-x
        text(eyebrow, in: NSRect(x: x, y: H*0.73, width: tw, height: H*0.16), size: H*0.105, ink: 0xc2b69c, weight: .medium, tracking: 2.6)
        text(title, in: NSRect(x: x, y: H*0.29, width: tw, height: H*0.43), size: H*0.34, ink: 0xf1e1bc, weight: .bold, tracking: 1.5)
        color(accent).withAlphaComponent(0.70).setFill(); NSRect(x: x+tw*0.1, y: H*0.28, width: tw*0.8, height: 1.5).fill()
        text(footer, in: NSRect(x: x, y: H*0.09, width: tw, height: H*0.17), size: H*0.105, ink: 0xc2b69c, weight: .medium, tracking: 1.8)
        color(0xd9ccb0).withAlphaComponent(0.08).setFill()
        for i in 0..<65 { NSRect(x: CGFloat((i*179+23)%w), y: CGFloat((i*43+17)%h), width: CGFloat(1+i%6), height: 1).fill() }
        NSGraphicsContext.restoreGraphicsState()
        return bitmap
    }
    save("V_sign", image("DROID EXCHANGE", eyebrow: "REPAIRS  /  REBUILT MOTORS", footer: "PARTS · TRACKS · POWER CELLS", badge: "08", aspect: 6.8/0.95, accent: 0xb98450))
}
do { // Transform probes
    let e = SCNNode(); e.eulerAngles = SCNVector3(0.3, 0.5, 0.7)
    let t = e.transform
    measurements.append(("T_euler_0.3_0.5_0.7_m11_m12_m13", [Double(t.m11), Double(t.m12), Double(t.m13)]))
    let a = SCNNode(); a.position = SCNVector3(1, 2, 3); a.eulerAngles = SCNVector3(0, Double.pi/2, 0); a.scale = SCNVector3(2, 2, 2); a.pivot = SCNMatrix4MakeTranslation(0.5, 0, 0)
    let w = a.convertPosition(SCNVector3(1, 0, 0), to: nil)
    measurements.append(("T_pivot_convertPosition_1_0_0", [Double(w.x), Double(w.y), Double(w.z)]))
    let e7 = SCNNode(); e7.eulerAngles = SCNVector3(0.2, 0, 0); e7.eulerAngles.x = 7.0
    measurements.append(("T_euler_readback_x7_rotation", [Double(e7.eulerAngles.x), Double(e7.rotation.x), Double(e7.rotation.w)]))
    let look = SCNNode(); look.position = SCNVector3(0, 1.05, 2.25)
    look.look(at: SCNVector3(0, 0.40, 0), up: SCNVector3(0, 1, 0), localFront: SCNVector3(0, 0, -1))
    let o = look.orientation
    measurements.append(("T_look_orientation", [Double(o.x), Double(o.y), Double(o.z), Double(o.w)]))
}

// =====================================================================
let json = "{\n" + measurements.map { "  \"\($0.0)\": [\($0.1.map { String(format: "%.4f", $0) }.joined(separator: ", "))]" }.joined(separator: ",\n") + "\n}\n"
try! json.write(to: outDir.appendingPathComponent("measurements.json"), atomically: true, encoding: .utf8)
print("SceneKit reference: \(measurements.count) measurements written to \(outDir.path)")

// Comparison with a Godot facade-test directory.
if args.count >= 3 {
    let godotDir = URL(fileURLWithPath: args[2])
    if let data = try? Data(contentsOf: godotDir.appendingPathComponent("measurements.json")),
       let godot = try? JSONSerialization.jsonObject(with: data) as? [String: [Double]] {
        var worst = 0.0, sum = 0.0, n = 0
        print(String(format: "%-40@ %-26@ %-26@ %@", "measurement" as NSString, "SceneKit (linear)" as NSString, "Godot facade" as NSString, "max |d|"))
        for (name, sk) in measurements {
            guard let gd = godot[name] else { print("\(name): missing in Godot output"); continue }
            let d = zip(sk, gd).map { abs($0 - $1) }.max() ?? 0
            if !name.hasPrefix("T_") { worst = max(worst, d); sum += d; n += 1 }
            func f(_ v: [Double]) -> String { v.map { String(format: "%.4f", $0) }.joined(separator: " ") }
            print(String(format: "%-40@ %-26@ %-26@ %.4f%@", name as NSString, f(sk) as NSString, f(gd) as NSString, d, d > 0.02 ? "  <-" : ""))
        }
        print(String(format: "pixel measurements: %d, mean max-channel difference %.4f, worst %.4f (linear units)", n, sum / Double(max(n, 1)), worst))
    }
    for name in ["V_primitives", "V_sky", "V_ground", "V_materials", "V_sign", "S_forward_alpha1", "S_deferred_alpha0.24", "S_deferred_alpha0.24_constant"] {
        guard let a = NSBitmapImageRep(data: (try? Data(contentsOf: outDir.appendingPathComponent(name + ".png"))) ?? Data()),
              let b = NSBitmapImageRep(data: (try? Data(contentsOf: godotDir.appendingPathComponent(name + ".png"))) ?? Data()),
              a.pixelsWide == b.pixelsWide, a.pixelsHigh == b.pixelsHigh else { print("\(name): cannot compare"); continue }
        let w = a.pixelsWide, h = a.pixelsHigh
        let out = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: w * 3, pixelsHigh: h, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: w * 12, bitsPerPixel: 32)!
        var total = 0.0
        for y in 0..<h { for x in 0..<w {
            let ia = y * a.bytesPerRow + x * (a.bitsPerPixel / 8), ib = y * b.bytesPerRow + x * (b.bitsPerPixel / 8)
            for c in 0..<3 {
                let va = Int(a.bitmapData![ia + c]), vb = Int(b.bitmapData![ib + c])
                out.bitmapData![(y * w * 3 + x) * 4 + c] = UInt8(va)
                out.bitmapData![(y * w * 3 + w + x) * 4 + c] = UInt8(vb)
                out.bitmapData![(y * w * 3 + 2 * w + x) * 4 + c] = UInt8(min(255, abs(va - vb) * 4))
                total += Double(abs(va - vb))
            }
            for k in 0..<3 { out.bitmapData![(y * w * 3 + k * w + x) * 4 + 3] = 255 }
        }}
        try! out.representation(using: .png, properties: [:])!.write(to: outDir.appendingPathComponent("compare-" + name + ".png"))
        print(String(format: "%@: mean absolute difference %.2f / 255 (sRGB bytes)", name, total / Double(w * h * 3)))
    }
}
