// SceneKit reference renders for the facade calibration (game-like scenes).
//   swiftc -O CalibrationReference.swift -o calref && ./calref OUT_DIR [RESOURCES_DIR]
// Mirrors apps/simulator-godot/scripts/SceneKit/Calibration.cs (same scene names and construction).
import AppKit
import SceneKit
import Metal
import simd

let args = CommandLine.arguments
guard args.count >= 2 else { print("usage: calref OUT_DIR [RESOURCES]"); exit(1) }
let outDir = URL(fileURLWithPath: args[1])
try? FileManager.default.createDirectory(at: outDir, withIntermediateDirectories: true)
let resources = URL(fileURLWithPath: args.count > 2 ? args[2] : "/Users/m1/marvin/apps/simulator-macos/Resources")
let only = ProcessInfo.processInfo.environment["CAL_ONLY"]
let W = 1280, H = 820
let device = MTLCreateSystemDefaultDevice()!
let renderer = SCNRenderer(device: device, options: nil)

func color(_ hex: UInt32, alpha: CGFloat = 1) -> NSColor {
    NSColor(srgbRed: CGFloat((hex >> 16) & 255)/255, green: CGFloat((hex >> 8) & 255)/255, blue: CGFloat(hex & 255)/255, alpha: alpha)
}
func material(_ hex: UInt32, metal: CGFloat = 0, roughness: CGFloat = 0.6) -> SCNMaterial {
    let m = SCNMaterial(); m.lightingModel = .physicallyBased
    m.diffuse.contents = color(hex); m.metalness.contents = metal; m.roughness.contents = roughness; m.isDoubleSided = true
    return m
}
func ink(_ p: SIMD3<Float>) -> NSColor { NSColor(calibratedRed: CGFloat(p.x), green: CGFloat(p.y), blue: CGFloat(p.z), alpha: 1) }
func asset(_ path: String) -> URL { resources.appendingPathComponent(path) }
func add(_ parent: SCNNode, _ g: SCNGeometry, _ p: SCNVector3) -> SCNNode {
    let n = SCNNode(geometry: g); n.position = p; parent.addChildNode(n); return n
}

// ---- Game materials
func scanned(_ name: String, normal: CGFloat, scale: CGFloat) -> SCNMaterial {
    let m = SCNMaterial(); m.lightingModel = .physicallyBased
    m.diffuse.contents = asset("City/\(name)-base.jpg")
    m.normal.contents = asset("City/\(name)-normal.jpg"); m.normal.intensity = normal
    m.roughness.contents = asset("City/\(name)-rough.jpg"); m.metalness.contents = 0
    for p in [m.diffuse, m.normal, m.roughness] {
        p.wrapS = .repeat; p.wrapT = .repeat; p.mipFilter = .linear; p.maxAnisotropy = 4
        p.contentsTransform = SCNMatrix4MakeScale(scale, scale, 1)
    }
    return m
}
func clay(scale: CGFloat) -> SCNMaterial {
    let m = material(0x986441, roughness: 0.94)
    m.diffuse.contents = NSImage(contentsOf: asset("Dirt/diffuse.jpg"))
    m.normal.contents = NSImage(contentsOf: asset("Dirt/normal.jpg")); m.normal.intensity = 0.65
    m.roughness.contents = NSImage(contentsOf: asset("Dirt/roughness.jpg"))
    for p in [m.diffuse, m.normal, m.roughness] { p.wrapS = .repeat; p.wrapT = .repeat; p.contentsTransform = SCNMatrix4MakeScale(scale, scale, 1) }
    m.shaderModifiers = [.surface: """
    #pragma body
    float clayDetail = dot(_surface.diffuse.rgb, float3(0.2126,0.7152,0.0722));
    _surface.diffuse.rgb = float3(0.34,0.205,0.145)
                        + clayDetail * float3(0.58,0.44,0.33);
    """]
    return m
}
let groundTintModifiers: [SCNShaderModifierEntryPoint: String] = [.geometry: """
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
let cityTintModifiers: [SCNShaderModifierEntryPoint: String] = [.geometry: """
    #pragma varyings
    half3 cityTint;
    #pragma body
    out.cityTint=half3(pow(max(_geometry.color.rgb,float3(0.0)),float3(2.2)));
    """, .surface: """
    #pragma body
    float grain=dot(_surface.diffuse.rgb,float3(0.2126,0.7152,0.0722));
    _surface.diffuse.rgb=float3(in.cityTint)*(0.62+grain*0.65);
    """]
func colorSource(_ c: [Float], count: Int) -> SCNGeometrySource {
    c.withUnsafeBytes { SCNGeometrySource(data: Data($0), semantic: .color, vectorCount: count, usesFloatComponents: true, componentsPerVector: 4, bytesPerComponent: 4, dataOffset: 0, dataStride: 16) }
}

// ---- Test objects
func spheres(_ root: SCNNode, _ o: SCNVector3) {
    for i in 0..<5 { for j in 0..<3 {
        let s = SCNSphere(radius: 0.2); s.segmentCount = 48
        s.materials = [material(j == 1 ? 0xb5532f : 0xc3c7c9, metal: j == 2 ? 1 : 0, roughness: 0.1 + CGFloat(i) * 0.2)]
        _ = add(root, s, SCNVector3(o.x + CGFloat(i) * 0.5, 0.2, o.z + CGFloat(j) * 0.5))
    }}
}
struct Part: Decodable { let name: String; let vertexOffset: Int, vertexCount: Int, indexOffset: Int, triangleCount: Int }
struct Manifest: Decodable { let parts: [Part] }
let marvinData = try! Data(contentsOf: asset("Marvin/geometry.bin"))
let marvinManifest = try! JSONDecoder().decode(Manifest.self, from: Data(contentsOf: asset("Marvin/manifest.json")))
func marvin(_ parent: SCNNode, _ p: SCNVector3, yaw: CGFloat, headYaw: CGFloat) {
    let root = SCNNode(), yawNode = SCNNode(), pitchNode = SCNNode()
    let yawPivot = SCNVector3(0, 0.30, -0.01886), pitchPivot = SCNVector3(0, 0.56, 0)
    yawNode.position = yawPivot; pitchNode.position = SCNVector3(pitchPivot.x - yawPivot.x, pitchPivot.y - yawPivot.y, pitchPivot.z - yawPivot.z)
    root.addChildNode(yawNode); yawNode.addChildNode(pitchNode)
    let shell = material(0xc3c7c9, metal: 0.8, roughness: 0.65)
    let rubber = material(0x202a29, roughness: 0.88)
    let graphite = material(0x37403f, metal: 0.25, roughness: 0.42)
    let steel = material(0x919b9d, metal: 0.7, roughness: 0.28)
    let electronics = material(0x163e36, roughness: 0.6)
    let face = material(0x25282b, roughness: 0.38)
    var neutral = 0.0
    for part in marvinManifest.parts where part.vertexCount > 0 && part.triangleCount > 0 {
        let positions = SCNGeometrySource(data: marvinData, semantic: .vertex, vectorCount: part.vertexCount, usesFloatComponents: true, componentsPerVector: 3, bytesPerComponent: 4, dataOffset: part.vertexOffset, dataStride: 24)
        let normals = SCNGeometrySource(data: marvinData, semantic: .normal, vectorCount: part.vertexCount, usesFloatComponents: true, componentsPerVector: 3, bytesPerComponent: 4, dataOffset: part.vertexOffset + 12, dataStride: 24)
        let indices = marvinData.subdata(in: part.indexOffset..<(part.indexOffset + part.triangleCount * 12))
        let g = SCNGeometry(sources: [positions, normals], elements: [SCNGeometryElement(data: indices, primitiveType: .triangles, primitiveCount: part.triangleCount, bytesPerIndex: 4)])
        if !["Head", "09_track"].contains(part.name) { neutral = max(neutral, Double(g.boundingBox.max.y)) }
        if part.name == "09_track" { g.materials = [rubber] }
        else if ["04_wheel", "Top", "Servo_Head", "Servo_Tilt"].contains(part.name) { g.materials = [graphite] }
        else if ["Bearings", "Motor_Left", "Motor_Right", "Axis_Mount"].contains(part.name) { g.materials = [steel] }
        else if part.name == "Display_and_electronics" { g.materials = [face] }
        else if part.name == "Battery" { g.materials = [electronics] }
        else { g.materials = [shell] }
        let node = SCNNode(geometry: g); node.isHidden = part.name == "Head"
        if ["05_head_base", "06_head_cover", "Display_and_electronics", "Head", "Top", "Battery"].contains(part.name) {
            node.position = SCNVector3(-pitchPivot.x, -pitchPivot.y, -pitchPivot.z); pitchNode.addChildNode(node)
        } else if ["07_neck", "08_neck_mount", "Servo_Tilt", "Axis_Mount"].contains(part.name) {
            node.position = SCNVector3(-yawPivot.x, -yawPivot.y, -yawPivot.z); yawNode.addChildNode(node)
        } else { root.addChildNode(node) }
    }
    let eyePath = NSBezierPath()
    for i in 0...40 { let a = Double.pi * (1 - Double(i)/40); let q = NSPoint(x: cos(a)*0.061, y: sin(a)*0.046); if i == 0 { eyePath.move(to: q) } else { eyePath.line(to: q) } }
    for i in 0...40 { let a = Double.pi * Double(i)/40; eyePath.line(to: NSPoint(x: cos(a)*0.048, y: sin(a)*0.033)) }
    eyePath.close()
    for x in [-0.0545, 0.0545] { eyePath.appendOval(in: NSRect(x: x-0.0065, y: -0.0065, width: 0.013, height: 0.013)) }
    for x in [-0.145, 0.145] {
        let eye = SCNShape(path: eyePath, extrusionDepth: 0)
        let glow = SCNMaterial(); glow.lightingModel = .constant; glow.diffuse.contents = NSColor.white
        glow.emission.contents = NSColor.white; glow.isDoubleSided = true; eye.materials = [glow]
        let n = SCNNode(geometry: eye); n.position = SCNVector3(x, 0.008, 0.348); n.castsShadow = false; pitchNode.addChildNode(n)
    }
    for x in [-0.337, 0.337] { for z in [-0.247, 0.164] {
        let spoke = SCNBox(width: 0.008, height: 0.10, length: 0.022, chamferRadius: 0.004); spoke.materials = [material(0xb4c8c0, metal: 0.5)]
        _ = add(root, spoke, SCNVector3(x, 0.112, z))
    }}
    let scale = 0.885 * (0.60 / 1.08) / neutral
    root.scale = SCNVector3(scale, scale, scale)
    yawNode.eulerAngles.y = headYaw
    root.position = p; root.eulerAngles = SCNVector3(0, yaw, 0)
    parent.addChildNode(root)
}
func plasterBox(_ root: SCNNode, _ p: SCNVector3) {
    let b = SCNBox(width: 0.8, height: 0.8, length: 0.8, chamferRadius: 0.08); b.materials = [scanned("plaster", normal: 1, scale: 1)]
    add(root, b, SCNVector3(p.x, 0.4, p.z)).eulerAngles.y = 0.5
}
func coloredBox(_ root: SCNNode, _ p: SCNVector3) {
    let b = SCNBox(width: 0.5, height: 1.0, length: 0.5, chamferRadius: 0.06); b.materials = [material(0x96b4a7)]
    add(root, b, SCNVector3(p.x, 0.5, p.z)).eulerAngles.y = -0.3
}
func tintedHouse(_ root: SCNNode, _ p: SCNVector3) {
    // Box mesh with sRGB palette bytes in its colour source, scanned adobe and the cityTint modifier.
    let w: Float = 1.6, h: Float = 1.2, d: Float = 1.2
    var v: [SCNVector3] = [], n: [SCNVector3] = [], uv: [CGPoint] = [], c: [Float] = [], idx: [Int32] = []
    let faces: [(SIMD3<Float>, SIMD3<Float>, SIMD3<Float>)] = [
        (SIMD3(0, 0, 1), SIMD3(1, 0, 0), SIMD3(0, 1, 0)), (SIMD3(0, 0, -1), SIMD3(-1, 0, 0), SIMD3(0, 1, 0)),
        (SIMD3(1, 0, 0), SIMD3(0, 0, -1), SIMD3(0, 1, 0)), (SIMD3(-1, 0, 0), SIMD3(0, 0, 1), SIMD3(0, 1, 0)),
        (SIMD3(0, 1, 0), SIMD3(1, 0, 0), SIMD3(0, 0, -1))]
    let palette: [SIMD3<Float>] = [SIMD3(0.79, 0.64, 0.48), SIMD3(0.72, 0.52, 0.38), SIMD3(0.86, 0.78, 0.62), SIMD3(0.62, 0.44, 0.32), SIMD3(0.80, 0.70, 0.56)]
    let half = SIMD3<Float>(w/2, h/2, d/2)
    for (k, f) in faces.enumerated() {
        let (normal, u, up) = f
        let center = normal * half + SIMD3(0, h/2, 0)
        let eu = abs(simd_dot(u, half)), ev = abs(simd_dot(up, half))
        let base = Int32(v.count)
        for (su, sv) in [(-1, -1), (1, -1), (1, 1), (-1, 1)] as [(Float, Float)] {
            let q = center + u * eu * su + up * ev * sv
            v.append(SCNVector3(q)); n.append(SCNVector3(normal))
            uv.append(CGPoint(x: Double((su + 1) * eu), y: Double((1 - sv) * ev)))
            let col = palette[k] * (sv > 0 ? 1.0 : 0.82)
            c += [col.x, col.y, col.z, 1]
        }
        idx += [base, base + 1, base + 2, base, base + 2, base + 3]
    }
    let g = SCNGeometry(sources: [SCNGeometrySource(vertices: v), SCNGeometrySource(normals: n), SCNGeometrySource(textureCoordinates: uv), colorSource(c, count: v.count)],
                        elements: [SCNGeometryElement(indices: idx, primitiveType: .triangles)])
    let m = scanned("adobe", normal: 0.8, scale: 0.5); m.shaderModifiers = cityTintModifiers
    g.materials = [m]
    add(root, g, SCNVector3(p.x, 0, p.z)).eulerAngles.y = 0.25
}
func banner(_ root: SCNNode, _ p: SCNVector3) {
    // Raw float vertex colours multiply the diffuse (PBR, roughness 0.8).
    var v: [SCNVector3] = [], n: [SCNVector3] = [], c: [Float] = [], idx: [Int32] = []
    let cols: [[Float]] = [[0.80, 0.10, 0.05], [0.90, 0.60, 0.05], [0.10, 0.50, 0.20], [0.05, 0.20, 0.70], [0.50, 0.10, 0.60]]
    for row in 0..<3 { for col in 0..<5 {
        v.append(SCNVector3(-0.8 + Double(col) * 0.4, 0.15 + Double(row) * 0.4, 0)); n.append(SCNVector3(0, 0, 1))
        let k = cols[(col + row) % 5]; let s: Float = row == 1 ? 1 : 0.6
        c += [k[0] * s, k[1] * s, k[2] * s, 1]
    }}
    for row in 0..<2 { for col in 0..<4 {
        let a = Int32(row * 5 + col); idx += [a, a + 1, a + 6, a, a + 6, a + 5]
    }}
    let g = SCNGeometry(sources: [SCNGeometrySource(vertices: v), SCNGeometrySource(normals: n), colorSource(c, count: v.count)], elements: [SCNGeometryElement(indices: idx, primitiveType: .triangles)])
    g.materials = [material(0xffffff, roughness: 0.8)]
    add(root, g, SCNVector3(p.x, 0, p.z)).eulerAngles.y = 0.15
}
func emissive(_ root: SCNNode, _ p: SCNVector3) {
    for (k, (hex, intensity)) in [(UInt32(0xffa040), CGFloat(3.0)), (UInt32(0x40c0ff), CGFloat(0.8))].enumerated() {
        let plane = SCNPlane(width: 0.4, height: 0.25)
        let m = material(0x202020, roughness: 0.5); m.emission.contents = color(hex); m.emission.intensity = intensity
        plane.materials = [m]
        _ = add(root, plane, SCNVector3(p.x + CGFloat(k) * 0.55, 0.3, p.z))
    }
}
func overlays(_ root: SCNNode, _ p: SCNVector3) {
    let overlay = SCNMaterial(); overlay.lightingModel = .physicallyBased
    overlay.roughness.contents = 1.0; overlay.diffuse.contents = NSColor.white
    overlay.writesToDepthBuffer = false; overlay.transparencyMode = .aOne
    overlay.shaderModifiers = groundTintModifiers
    var verts: [SCNVector3] = [], colors: [Float] = [], idx: [Int32] = []
    let tints: [[Float]] = [[0x8e/255, 0x80/255, 0x6a/255], [0x5a/255, 0x4a/255, 0x36/255], [0xc8/255, 0xb8/255, 0x9a/255]]
    for (k, off) in [(0.0, 0.0), (1.0, 0.3), (-0.9, 0.4)].enumerated() {
        let cx = Double(p.x) + off.0, cz = Double(p.z) + off.1
        let center = Int32(verts.count); verts.append(SCNVector3(cx, 0.004, cz)); colors += tints[k] + [0.7]
        for i in 0..<18 {
            let a = Double(i) * 2 * Double.pi / 18
            verts.append(SCNVector3(cx + cos(a) * 0.6, 0.004, cz + sin(a) * 0.45)); colors += tints[k] + [0]
        }
        for i in 0..<18 { idx += [center, center + 1 + Int32((i + 1) % 18), center + 1 + Int32(i)] }
    }
    let g = SCNGeometry(sources: [SCNGeometrySource(vertices: verts), SCNGeometrySource(normals: Array(repeating: SCNVector3(0, 1, 0), count: verts.count)), colorSource(colors, count: verts.count)],
                        elements: [SCNGeometryElement(indices: idx, primitiveType: .triangles)])
    g.materials = [overlay]
    add(root, g, SCNVector3Zero).castsShadow = false
}
func pane(_ root: SCNNode, _ p: SCNVector3) {
    let b = SCNBox(width: 0.9, height: 0.6, length: 0.02, chamferRadius: 0); b.materials = [material(0x6fa8dc, roughness: 0.2)]
    b.firstMaterial!.diffuse.contents = color(0x6fa8dc, alpha: 0.45)
    add(root, b, SCNVector3(p.x, 0.42, p.z)).eulerAngles.y = 0.35
}
func pole(_ root: SCNNode, _ p: SCNVector3) {
    let c = SCNCylinder(radius: 0.04, height: 2.0); c.materials = [material(0x555653, roughness: 0.94)]
    _ = add(root, c, SCNVector3(p.x, 1.0, p.z))
}
func table(_ root: SCNNode, _ p: SCNVector3) {
    let m = material(0x805443, roughness: 0.94)
    let top = SCNBox(width: 1.0, height: 0.04, length: 0.6, chamferRadius: 0.01); top.materials = [m]
    _ = add(root, top, SCNVector3(p.x, 0.7, p.z))
    for (dx, dz) in [(-0.45, -0.25), (0.45, -0.25), (-0.45, 0.25), (0.45, 0.25)] {
        let leg = SCNBox(width: 0.04, height: 0.68, length: 0.04, chamferRadius: 0); leg.materials = [m]
        _ = add(root, leg, SCNVector3(p.x + dx, 0.34, p.z + dz))
    }
}
func groundPlane(_ root: SCNNode, _ m: SCNMaterial, _ w: CGFloat, _ l: CGFloat, _ p: SCNVector3) {
    let plane = SCNPlane(width: w, height: l); plane.materials = [m]
    add(root, plane, p).eulerAngles.x = -.pi/2
}
func cluster(_ root: SCNNode) {
    spheres(root, SCNVector3(-2.4, 0, -0.9))
    marvin(root, SCNVector3(0.6, 0, 0.1), yaw: 0.35, headYaw: -0.30)
    plasterBox(root, SCNVector3(2.0, 0, -0.6)); coloredBox(root, SCNVector3(1.2, 0, -1.4))
    tintedHouse(root, SCNVector3(3.2, 0, -2.4)); banner(root, SCNVector3(-1.2, 0, -1.9))
    emissive(root, SCNVector3(-0.1, 0, 0.9)); overlays(root, SCNVector3(0.9, 0, 1.1))
    pane(root, SCNVector3(-2.1, 0, 0.8)); pole(root, SCNVector3(-0.2, 0, -1.4)); table(root, SCNVector3(2.5, 0, 0.7))
}

// ---- Rendering helpers
func camera(_ scene: SCNScene, _ p: SCNVector3, _ target: SCNVector3, fov: CGFloat, near: CGFloat, far: CGFloat) -> SCNNode {
    let c = SCNNode(); c.camera = SCNCamera(); c.camera!.fieldOfView = fov; c.camera!.zNear = near; c.camera!.zFar = far
    c.position = p; c.look(at: target, up: SCNVector3(0, 1, 0), localFront: SCNVector3(0, 0, -1))
    scene.rootNode.addChildNode(c); return c
}
var probes: [String: [[String: Any]]] = [:]
func render(_ name: String, _ scene: SCNScene, _ cam: SCNNode, _ aa: SCNAntialiasingMode, probePoints: [(String, SIMD3<Double>)]) {
    if let only, !name.hasPrefix(only) { return }
    let env = ProcessInfo.processInfo.environment
    scene.rootNode.enumerateHierarchy { n, _ in
        if env["CAL_NOSHADOW"] != nil, let l = n.light { l.castsShadow = false }
        if let c = n.camera {
            if env["CAL_NOSSAO"] != nil { c.screenSpaceAmbientOcclusionIntensity = 0 }
            if env["CAL_NOBLOOM"] != nil { c.bloomIntensity = 0 }
        }
    }
    renderer.scene = scene; renderer.pointOfView = cam
    _ = renderer.snapshot(atTime: 0, with: CGSize(width: W, height: H), antialiasingMode: aa)
    let image = renderer.snapshot(atTime: 0, with: CGSize(width: W, height: H), antialiasingMode: aa)
    let rep = NSBitmapImageRep(data: image.tiffRepresentation!)!
    try! rep.representation(using: .png, properties: [:])!.write(to: outDir.appendingPathComponent(name + ".png"))
    let inv = simd_inverse(cam.simdWorldTransform), t = tan(Double(cam.camera!.fieldOfView) * Double.pi / 360)
    probes[name] = probePoints.map { (label, q) in
        let v = inv * SIMD4<Float>(Float(q.x), Float(q.y), Float(q.z), 1)
        let x = Double(v.x) / Double(-v.z) / (t * Double(W) / Double(H)), y = Double(v.y) / Double(-v.z) / t
        return ["name": label, "x": (x * 0.5 + 0.5) * Double(W), "y": (0.5 - y * 0.5) * Double(H)]
    }
    print("rendered \(name)")
}
func clusterProbes(_ eye: SIMD3<Double>, sun: SIMD3<Double>) -> [(String, SIMD3<Double>)] {
    var list: [(String, SIMD3<Double>)] = []
    for j in 0..<3 { for i in 0..<5 {
        let c = SIMD3(-2.4 + Double(i) * 0.5, 0.2, -0.9 + Double(j) * 0.5)
        list.append(("sphere_m\(j)_r\(i)", c + simd_normalize(eye - c) * 0.2))
    }}
    let floorShadow = SIMD3(2.5, 0.7, 0.7) - sun * (0.7 / sun.y)
    list += [("table_shadow", SIMD3(floorShadow.x, 0, floorShadow.z)), ("ground_lit", SIMD3(-0.6, 0, 1.7)), ("ground_lit2", SIMD3(1.6, 0, 2.2)),
             ("plaster_box", SIMD3(2.0, 0.45, -0.6) + simd_normalize(eye - SIMD3(2.0, 0.45, -0.6)) * 0.42),
             ("colored_box", SIMD3(1.2, 0.6, -1.4) + simd_normalize(eye - SIMD3(1.2, 0.6, -1.4)) * 0.27),
             ("banner_mid", SIMD3(-1.2, 0.55, -1.9)), ("emissive_hot", SIMD3(-0.1, 0.3, 0.9)), ("emissive_cool", SIMD3(0.45, 0.3, 0.9)),
             ("overlay", SIMD3(0.9, 0.004, 1.1)), ("pane", SIMD3(-2.1, 0.42, 0.8)), ("marvin_shell", SIMD3(0.6, 0.2, 0.32)),
             ("house_wall", SIMD3(3.2, 0.6, -1.8))]
    return list
}

// ---- Race (BinarySky): two forward-shadow suns, ambient, sky probe, fog, HDR + bloom + SSAO, 2x MSAA.
struct BinaryDaylight {
    let fraction: Double, phase: Double, directions: [SIMD3<Double>]
    init(fraction: Double, phase: Double) {
        self.fraction = max(0.015, min(0.985, fraction)); self.phase = phase
        let offsets = [atan2(-0.06*sin(phase), 1+0.06*cos(phase)), atan2(0.14*sin(phase), 1-0.14*cos(phase))]
        let first = max(-Double.pi/2-offsets[0], -Double.pi/2-offsets[1]), last = min(Double.pi/2-offsets[0], Double.pi/2-offsets[1])
        let h = first+(last-first)*self.fraction, latitude = 25*Double.pi/180
        directions = offsets.map { d in SIMD3(-sin(h+d), cos(latitude)*cos(h+d), -sin(latitude)*cos(h+d)) }
    }
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
let skyFragment = """
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
_output.color=float4(mix(sky,stormTint+sky*0.025,storm*0.97),1.0);
"""
func raceScene(fraction: Double) -> (SCNScene, BinaryDaylight, (SCNNode) -> Void) {
    let scene = SCNScene()
    let value = BinaryDaylight(fraction: fraction, phase: 1.2)
    let skyMaterial = SCNMaterial(); skyMaterial.lightingModel = .constant; skyMaterial.cullMode = .front
    skyMaterial.readsFromDepthBuffer = false; skyMaterial.writesToDepthBuffer = false
    skyMaterial.shaderModifiers = [.geometry: "#pragma varyings\nfloat3 skyDirection;\n#pragma body\nout.skyDirection = _geometry.position.xyz;", .fragment: skyFragment]
    let sphere = SCNSphere(radius: 220); sphere.segmentCount = 48; sphere.materials = [skyMaterial]
    let dome = SCNNode(geometry: sphere); dome.castsShadow = false; dome.renderingOrder = -10000; scene.rootNode.addChildNode(dome)
    let ambient = SCNNode(); ambient.light = SCNLight(); ambient.light!.type = .ambient; scene.rootNode.addChildNode(ambient)
    let suns = [SCNNode(), SCNNode()]
    for (i, sun) in suns.enumerated() {
        sun.light = SCNLight(); sun.light!.type = .directional
        sun.light!.castsShadow = true; sun.light!.shadowMode = .forward
        let resolution = i == 0 ? 4096 : 2048
        sun.light!.shadowMapSize = CGSize(width: resolution, height: resolution)
        sun.light!.automaticallyAdjustsShadowProjection = false; sun.light!.sampleDistributedShadowMaps = false; sun.light!.forcesBackFaceCasters = false
        sun.light!.zNear = 0.1; sun.light!.zFar = 220; sun.light!.maximumShadowDistance = 500
        sun.light!.orthographicScale = 58; sun.light!.shadowRadius = i == 0 ? 3 : 2
        sun.light!.shadowSampleCount = 8; sun.light!.shadowColor = NSColor.black; sun.light!.shadowBias = 0.6
        scene.rootNode.addChildNode(sun)
    }
    // BinarySky.apply
    skyMaterial.setValue(Float(0), forKey: "storm")
    skyMaterial.setValue(NSValue(scnVector3: SCNVector3(0.46, 0.29, 0.14)), forKey: "stormTint")
    skyMaterial.setValue(NSValue(point: NSPoint(x: 0.55*Double.pi/180, y: 0.38*Double.pi/180)), forKey: "sunRadii")
    let elevation = max(0, value.directions.map { $0.y }.max()!)
    let day = min(1, elevation/0.55)
    let evening = Float(value.fraction > 0.5 ? 1 : 0)
    let lowZenith = SIMD3<Float>(0.018, 0.070, 0.17)*(1-evening)+SIMD3<Float>(0.065, 0.027, 0.10)*evening
    let lowHorizon = SIMD3<Float>(0.95, 0.36, 0.09)*(1-evening)+SIMD3<Float>(0.85, 0.19, 0.035)*evening
    let zenith = lowZenith+(SIMD3<Float>(0.20, 0.38, 0.62)-lowZenith)*Float(day)
    let horizon = lowHorizon+(SIMD3<Float>(0.65, 0.72, 0.75)-lowHorizon)*Float(day)
    skyMaterial.setValue(Float(day), forKey: "daylight")
    let band = SIMD3<Float>(0.04, 0.055, 0.06)*(1-evening)+SIMD3<Float>(0.22, 0.045, 0.085)*evening
    skyMaterial.setValue(NSValue(scnVector3: SCNVector3(band)), forKey: "duskBand")
    for (key, v) in [("zenith", zenith), ("horizon", horizon)] { skyMaterial.setValue(NSValue(scnVector3: SCNVector3(v)), forKey: key) }
    for i in 0..<2 {
        let d = value.directions[i], height = max(0, d.y)
        let altitude = asin(height)*180/Double.pi
        let mass = 1/(height+0.50572*pow(altitude+6.07995, -1.6364))
        let attenuation = SIMD3(exp(-0.035*mass), exp(-0.070*mass), exp(-0.15*mass))
        let intrinsic = i == 0 ? SIMD3<Double>(1, 0.94, 0.83) : SIMD3<Double>(1, 0.73, 0.46)
        let rgb = intrinsic*attenuation
        let tint = SIMD3<Float>(Float(rgb.x), Float(rgb.y), Float(rgb.z))
        suns[i].position = SCNVector3(d*80)
        suns[i].look(at: SCNVector3Zero, up: SCNVector3(0, 1, 0), localFront: SCNVector3(0, 0, -1))
        suns[i].light!.color = ink(tint)
        suns[i].light!.intensity = i == 0 ? 1550 : 470
        skyMaterial.setValue(NSValue(scnVector3: SCNVector3(d)), forKey: i == 0 ? "sunA" : "sunB")
        skyMaterial.setValue(NSValue(scnVector3: SCNVector3(tint)), forKey: i == 0 ? "tintA" : "tintB")
    }
    ambient.light!.color = ink(SIMD3<Float>(0.54, 0.62, 0.78)); ambient.light!.intensity = 260-70*day
    scene.fogColor = ink(horizon); scene.fogStartDistance = 115; scene.fogEndDistance = 240
    scene.lightingEnvironment.contents = skyProbe(zenith: zenith, horizon: horizon)
    scene.lightingEnvironment.intensity = 0.95-0.20*day
    // Ground: city scan around a clay pad, houses, fog posts, test cluster.
    let root = scene.rootNode
    groundPlane(root, scanned("ground", normal: 0.65, scale: 125), 500, 500, SCNVector3(0, -0.01, 0))
    if let g = root.childNodes.last?.geometry?.firstMaterial { for p in [g.diffuse, g.normal, g.roughness] { p.maxAnisotropy = 8 } }
    groundPlane(root, clay(scale: 7.5), 30, 30, SCNVector3(0, 0, 0))
    for k in 0..<10 {
        let a = Double(k) * 0.628 + 0.3, r = 10.0 + Double(k % 3) * 6
        let b = SCNBox(width: 3, height: 2.5 + Double(k % 2), length: 3, chamferRadius: 0.12); b.materials = [scanned("plaster", normal: 1, scale: 1.5)]
        add(root, b, SCNVector3(cos(a) * r, (2.5 + Double(k % 2)) / 2, sin(a) * r)).eulerAngles.y = CGFloat(a)
    }
    for k in 0..<5 {
        let b = SCNBox(width: 1.5, height: 6, length: 1.5, chamferRadius: 0.05); b.materials = [material(0x805443, roughness: 0.94)]
        _ = add(root, b, SCNVector3(-6 + Double(k) * 3, 3, -30 - Double(k) * 45))
    }
    cluster(root)
    let attach: (SCNNode) -> Void = { cam in
        dome.constraints = [SCNTransformConstraint.positionConstraint(inWorldSpace: true) { _, _ in cam.presentation.worldPosition }]
        let lens = cam.camera!
        lens.wantsHDR = true; lens.wantsExposureAdaptation = false
        lens.exposureOffset = 0; lens.bloomIntensity = 0.38; lens.bloomThreshold = 1.2; lens.bloomBlurRadius = 12
        lens.screenSpaceAmbientOcclusionIntensity = 0.70; lens.screenSpaceAmbientOcclusionRadius = 1.6; lens.screenSpaceAmbientOcclusionBias = 0.025
    }
    return (scene, value, attach)
}
do {
    let (scene, value, attach) = raceScene(fraction: 0.5)
    let chase = camera(scene, SCNVector3(0.4, 1.9, 5.6), SCNVector3(0.2, 0.35, -0.4), fov: 48, near: 0.02, far: 250)
    attach(chase)
    render("race_midday_chase", scene, chase, .multisampling2X, probePoints: clusterProbes(SIMD3(0.4, 1.9, 5.6), sun: value.directions[0]) +
           [("sky_up", SIMD3(0.4, 1.9 + 60, 5.6 - 100)), ("fog_post2", SIMD3(0, 2, -120)), ("fog_post4", SIMD3(6, 2, -210))])
    let overview = camera(scene, SCNVector3(0, 38, -33), SCNVector3Zero, fov: 48, near: 0.02, far: 250)
    attach(overview)
    render("race_midday_overview", scene, overview, .multisampling2X, probePoints: clusterProbes(SIMD3(0, 38, -33), sun: value.directions[0]))
}
do {
    let (scene, value, attach) = raceScene(fraction: 0.96)
    let chase = camera(scene, SCNVector3(5.5, 1.5, 1.8), SCNVector3(-2.0, 1.0, -0.8), fov: 48, near: 0.02, far: 250)
    attach(chase)
    render("race_evening_chase", scene, chase, .multisampling2X, probePoints: clusterProbes(SIMD3(5.5, 1.5, 1.8), sun: value.directions[0]) +
           [("sun_a", SIMD3(5.5, 1.5, 1.8) + value.directions[0] * 200), ("sun_b", SIMD3(5.5, 1.5, 1.8) + value.directions[1] * 200)])
}

// ---- Sandbox (World.swift): LDR, deferred shadows, fog 15-38, 4x MSAA.
do {
    let scene = SCNScene()
    scene.background.contents = color(0xdbe4df)
    scene.fogColor = color(0xdbe4df); scene.fogStartDistance = 15; scene.fogEndDistance = 38
    let root = scene.rootNode
    let ambient = SCNNode(); ambient.light = SCNLight(); ambient.light!.type = .ambient; ambient.light!.intensity = 550
    ambient.light!.color = color(0xe7f4ff); root.addChildNode(ambient)
    let sun = SCNNode(); sun.light = SCNLight(); sun.light!.type = .directional
    sun.light!.intensity = 1400; sun.light!.color = color(0xfff1df)
    sun.eulerAngles = SCNVector3(-0.85, -0.45, -0.25)
    sun.light!.castsShadow = true; sun.light!.shadowMode = .deferred
    sun.light!.shadowMapSize = CGSize(width: 4096, height: 4096)
    sun.light!.shadowSampleCount = 16; sun.light!.shadowColor = NSColor.black.withAlphaComponent(0.24)
    sun.light!.orthographicScale = 10; sun.light!.maximumShadowDistance = 22
    root.addChildNode(sun)
    func box(_ x: Double, _ y: Double, _ z: Double, _ w: Double, _ h: Double, _ d: Double, _ m: SCNMaterial, radius: Double = 0) -> SCNNode {
        let g = SCNBox(width: w, height: h, length: d, chamferRadius: radius); g.materials = [m]; return add(root, g, SCNVector3(x, y, z))
    }
    let soil = material(0xb49470, roughness: 0.98)
    let variant = ProcessInfo.processInfo.environment["CAL_SANDBOX_VARIANT"] ?? ""
    if !variant.contains("nobox") {
        _ = box(0, -0.175, 0, 12.5, 0.3, 10.5, material(0x927455, roughness: 0.95), radius: 0.14)
        _ = box(0, -0.05, 0, 12, 0.05, 10, soil, radius: 0.04)
    }
    let soilTop = variant.contains("single") ? { () -> SCNMaterial in let m = material(0xb49470, roughness: 0.98); m.isDoubleSided = false; return m }() : soil
    groundPlane(root, soilTop, 12, 10, SCNVector3Zero)
    let wall = material(0xa5b9ad)
    _ = box(-6.08, 0.18, 0, 0.16, 0.36, 10.3, wall, radius: 0.04); _ = box(6.08, 0.18, 0, 0.16, 0.36, 10.3, wall, radius: 0.04)
    _ = box(0, 0.18, -5.08, 12.3, 0.36, 0.16, wall, radius: 0.04); _ = box(0, 0.18, 5.08, 12.3, 0.36, 0.16, wall, radius: 0.04)
    for (i, o) in [(-2.2, -0.4, 1.25, 1.25, 0.6), (1.65, 0.3, 1.1, 1.65, 0.85), (-3.55, 2.4, 1.0, 1.2, 0.4), (3.5, -2.3, 1.2, 0.8, 0.45), (0.0, 3.3, 1.6, 0.65, 0.35)].enumerated() {
        _ = box(o.0, o.4/2, o.1, o.2, o.4, o.3, material(i % 2 == 0 ? 0x96b4a7 : 0xd2b59a), radius: 0.07)
    }
    _ = box(0, 0, -2.6, 1.35, 0.001, 1.25, material(0xb7cdc0), radius: 0.08).castsShadow = false
    groundPlane(root, clay(scale: 1), 1.8, 1.4, SCNVector3(2.6, 0.001, 2.0))
    spheres(root, SCNVector3(-1.0, 0, -4.3))
    marvin(root, SCNVector3(0.2, 0, 1.6), yaw: 0.35, headYaw: -0.30)
    plasterBox(root, SCNVector3(-4.4, 0, -1.2)); tintedHouse(root, SCNVector3(4.3, 0, 2.6)); banner(root, SCNVector3(2.2, 0, -4.2))
    emissive(root, SCNVector3(-0.3, 0, -1.6)); overlays(root, SCNVector3(-1.4, 0, 1.4)); pane(root, SCNVector3(-4.0, 0, 1.0))
    pole(root, SCNVector3(1.0, 0, -1.4)); table(root, SCNVector3(-4.3, 0, -3.6))
    let sunDir = SIMD3<Double>(sun.simdWorldFront * -1)
    var list: [(String, SIMD3<Double>)] = []
    for j in 0..<3 { for i in 0..<5 { list.append(("sphere_m\(j)_r\(i)", SIMD3(-1.0 + Double(i) * 0.5, 0.2, -4.3 + Double(j) * 0.5))) } }
    let ts = SIMD3(-4.3, 0.7, -3.6) - sunDir * (0.7 / sunDir.y)
    list += [("table_shadow", SIMD3(ts.x, 0, ts.z)), ("soil_lit", SIMD3(-1.5, 0, -2.6)), ("obstacle_top", SIMD3(1.65, 0.85, 0.3)), ("wall", SIMD3(6.0, 0.2, 0)),
             ("plinth_side", SIMD3(0, -0.2, 5.25)), ("marvin_shell", SIMD3(0.2, 0.2, 1.82)), ("overlay", SIMD3(-1.4, 0, 1.4)), ("clay", SIMD3(2.6, 0, 2.0)),
             ("emissive_hot", SIMD3(-0.3, 0.3, -1.6)), ("emissive_cool", SIMD3(0.25, 0.3, -1.6)), ("pane", SIMD3(-4.0, 0.42, 1.0)), ("house_wall", SIMD3(4.3, 0.6, 3.2)), ("banner_mid", SIMD3(2.2, 0.55, -4.2))]
    let orbit = camera(scene, SCNVector3(4.27, 3.76, 4.9), SCNVector3(0.5, 0.4, -0.3), fov: 48, near: 0.02, far: 80)
    orbit.camera!.wantsHDR = false
    func withSpheres(_ eye: SIMD3<Double>) -> [(String, SIMD3<Double>)] {
        list.map { (n, q) in n.hasPrefix("sphere") ? (n, q + simd_normalize(eye - q) * 0.2) : (n, q) }
    }
    render("sandbox_orbit", scene, orbit, .multisampling4X, probePoints: withSpheres(SIMD3(4.27, 3.76, 4.9)))
    let overview = camera(scene, SCNVector3(0, 11.7, -10), SCNVector3Zero, fov: 48, near: 0.02, far: 80)
    overview.camera!.wantsHDR = false
    render("sandbox_overview", scene, overview, .multisampling4X, probePoints: withSpheres(SIMD3(0, 11.7, -10)))
}

// ---- Main menu portrait (MainMenu.swift): constant SCNFloor, deferred key light, 4x MSAA.
do {
    let stage = SCNScene()
    stage.background.contents = color(0xf2f1e9)
    let cam = camera(stage, SCNVector3(0, 1.05, 2.25), SCNVector3(0, 0.40, 0), fov: 36, near: 1, far: 100)
    let ambient = SCNNode(); ambient.light = SCNLight(); ambient.light!.type = .ambient
    ambient.light!.intensity = 650; stage.rootNode.addChildNode(ambient)
    let key = SCNNode(); key.light = SCNLight(); key.light!.type = .directional
    key.light!.intensity = 1100; key.eulerAngles = SCNVector3(-1.15, -0.6, 0)
    key.light!.castsShadow = true; key.light!.shadowMode = .deferred
    key.light!.shadowMapSize = CGSize(width: 2048, height: 2048)
    key.light!.orthographicScale = 2
    key.light!.shadowSampleCount = 32; key.light!.shadowRadius = 32
    key.light!.shadowBias = 0.001
    key.light!.shadowColor = NSColor.black.withAlphaComponent(0.18)
    stage.rootNode.addChildNode(key)
    let floor = SCNFloor(); floor.reflectivity = 0
    let surface = SCNMaterial(); surface.diffuse.contents = color(0xf2f1e9); surface.lightingModel = .constant
    floor.materials = [surface]; stage.rootNode.addChildNode(SCNNode(geometry: floor))
    marvin(stage.rootNode, SCNVector3Zero, yaw: 0.35, headYaw: -0.30)
    let s1 = SCNSphere(radius: 0.14); s1.segmentCount = 48; s1.materials = [material(0xc3c7c9, metal: 0.8, roughness: 0.65)]
    _ = add(stage.rootNode, s1, SCNVector3(-0.8, 0.14, -0.2))
    let s2 = SCNSphere(radius: 0.14); s2.segmentCount = 48; s2.materials = [material(0xc83a24, roughness: 0.45)]
    _ = add(stage.rootNode, s2, SCNVector3(0.8, 0.14, -0.2))
    let eye = SIMD3<Double>(0, 1.05, 2.25)
    render("menu_portrait", stage, cam, .multisampling4X, probePoints: [
        ("floor_far", SIMD3(0, 0, -3)), ("floor_near", SIMD3(0.6, 0, 0.6)), ("marvin_shell", SIMD3(0, 0.2, 0.25)),
        ("sphere_metal", SIMD3(-0.8, 0.14, -0.2) + simd_normalize(eye - SIMD3(-0.8, 0.14, -0.2)) * 0.14),
        ("sphere_red", SIMD3(0.8, 0.14, -0.2) + simd_normalize(eye - SIMD3(0.8, 0.14, -0.2)) * 0.14),
        ("shadow_marvin", SIMD3(0.25, 0, -0.35)), ("shadow_sphere", SIMD3(-0.75, 0, -0.45))])
}

let json = try! JSONSerialization.data(withJSONObject: probes, options: [.prettyPrinted, .sortedKeys])
let probeURL = outDir.appendingPathComponent("probes.json")
if let only, let old = try? Data(contentsOf: probeURL), var merged = try? JSONSerialization.jsonObject(with: old) as? [String: Any] {
    for (k, v) in probes { merged[k] = v }
    try! JSONSerialization.data(withJSONObject: merged, options: [.prettyPrinted, .sortedKeys]).write(to: probeURL)
    _ = only
} else { try! json.write(to: probeURL) }
print("done: \(outDir.path)")
