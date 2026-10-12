// SceneKit experiment behind the facade's SCNShape decimation (SCNShape.Decimate in scripts/SceneKit/SCNPrimitives.cs):
// which flattened path points does SCNShape keep? Prints the number of distinct vertices SceneKit generates.
//
//   swiftc -O apps/simulator-godot/tools/scenekit-reference/robots/ShapeTessellation.swift -o /tmp/shape-tessellation
//   /tmp/shape-tessellation
//
// Measured on macOS 27 (M2): a point is kept when it is at least 0.01 path units from the last kept point (a 64-gon
// keeps all 64 points from a spacing of 0.0101, 32 from 0.006 to 0.0099, 21 at 0.004, 10 at 0.002, for every
// flatness and radius), trailing points closer than 0.01 to the first point are dropped, contours with fewer than
// three points vanish (ovals of diameter 0.013), collinear points and slivers are kept, small squares (0.0105) are
// kept. SCNShape builds its sources lazily: they are empty until the shape has been rendered once.
import AppKit
import SceneKit
let renderer = SCNRenderer(device: MTLCreateSystemDefaultDevice()!, options: nil)
func uniqueVerts(_ shape: SCNShape) -> [SIMD2<Float>] {
    let scene = SCNScene(); scene.rootNode.addChildNode(SCNNode(geometry: shape))
    let cam = SCNNode(); cam.camera = SCNCamera(); cam.position = SCNVector3(0, 0, 5); scene.rootNode.addChildNode(cam)
    renderer.scene = scene; renderer.pointOfView = cam
    _ = renderer.snapshot(atTime: 0, with: CGSize(width: 16, height: 16), antialiasingMode: .none)
    guard let v = shape.sources(for: .vertex).first else { return [] }
    var pts: [SIMD2<Float>] = []
    v.data.withUnsafeBytes { b in
        for i in 0..<v.vectorCount { let o = v.dataOffset + i*v.dataStride
            let p = SIMD2(b.loadUnaligned(fromByteOffset: o, as: Float.self), b.loadUnaligned(fromByteOffset: o+4, as: Float.self))
            if !pts.contains(where: { simd_distance($0, p) < 1e-7 }) { pts.append(p) } }
    }
    return pts
}
func polygon(n: Int, radius: Double, flatness: CGFloat) -> NSBezierPath {
    let p = NSBezierPath(); p.flatness = flatness
    for i in 0..<n { let a = Double(i) * 2 * .pi / Double(n); let q = NSPoint(x: radius*cos(a), y: radius*sin(a)); if i == 0 { p.move(to: q) } else { p.line(to: q) } }
    p.close(); return p
}
print("flatness spacing -> kept of 64")
for f in [0.6, 0.3, 1.2, 0.06] as [CGFloat] {
    var line = "f=\(f):"
    for s in [0.002, 0.004, 0.006, 0.008, 0.009, 0.0095, 0.0099, 0.01, 0.0101, 0.0105, 0.011, 0.015, 0.02, 0.05] {
        let r = s / (2 * sin(Double.pi / 64))
        let k = uniqueVerts(SCNShape(path: polygon(n: 64, radius: r, flatness: f), extrusionDepth: 0)).count
        line += " \(s):\(k)"
    }
    print(line)
}
// Scale invariance: a regular 64-gon of radius 1 vs 0.01
for r in [10.0, 1.0, 0.1, 0.05, 0.02, 0.01] {
    print("radius", r, "spacing", 2*r*sin(Double.pi/64), "kept", uniqueVerts(SCNShape(path: polygon(n: 64, radius: r, flatness: 0.6), extrusionDepth: 0)).count)
}
// An open-ended polyline (eye) scaled
for scale in [0.1, 1.0, 10.0] {
    let p = NSBezierPath()
    for i in 0...40 { let a = Double.pi*(1-Double(i)/40); let q = NSPoint(x: cos(a)*0.061*scale, y: sin(a)*0.046*scale); if i == 0 { p.move(to: q) } else { p.line(to: q) } }
    for i in 0...40 { let a = Double.pi*Double(i)/40; p.line(to: NSPoint(x: cos(a)*0.048*scale, y: sin(a)*0.033*scale)) }
    p.close()
    print("eye scale", scale, "kept", uniqueVerts(SCNShape(path: p, extrusionDepth: 0)).count)
}
// Small squares: does an area rule drop them?
for f in [0.6, 0.06] as [CGFloat] {
    var line = "squares f=\(f):"
    for side in [0.0105, 0.011, 0.012, 0.013, 0.015, 0.02, 0.03] {
        let p = NSBezierPath(rect: NSRect(x: 0, y: 0, width: side, height: side)); p.flatness = f
        line += " \(side):\(uniqueVerts(SCNShape(path: p, extrusionDepth: 0)).count)"
    }
    print(line)
}
// Ovals: flattening vs flatness
for f in [0.6, 0.1, 0.01, 0.001] as [CGFloat] {
    var line = "ovals f=\(f):"
    for d in [0.013, 0.03, 0.05, 0.1, 0.5, 2.0] {
        let p = NSBezierPath(ovalIn: NSRect(x: 0, y: 0, width: d, height: d)); p.flatness = f
        line += " \(d):\(uniqueVerts(SCNShape(path: p, extrusionDepth: 0)).count)"
    }
    print(line)
}
// Two-contour path: eye band plus ovals overlapping the band ends (diameter 0.013 -> vanish?), and bigger ovals
for d in [0.013, 0.02, 0.03] {
    let p = NSBezierPath(rect: NSRect(x: 0, y: 0, width: 0.2, height: 0.05))
    p.appendOval(in: NSRect(x: 0.3, y: 0, width: d, height: d))
    print("rect+oval", d, uniqueVerts(SCNShape(path: p, extrusionDepth: 0)).count)
}
do {
    let p = NSBezierPath(); p.move(to: NSPoint(x: 0, y: 0))
    for i in 1...10 { p.line(to: NSPoint(x: Double(i)*0.05, y: 0)) }
    p.line(to: NSPoint(x: 0.5, y: 0.3)); p.line(to: NSPoint(x: 0, y: 0.3)); p.close()
    print("collinear rect (13 pts):", uniqueVerts(SCNShape(path: p, extrusionDepth: 0)).count)
    let q = NSBezierPath(); q.move(to: NSPoint(x: 0, y: 0)); q.line(to: NSPoint(x: 0.5, y: 0)); q.line(to: NSPoint(x: 0.25, y: 0.0004)); q.close()
    print("sliver:", uniqueVerts(SCNShape(path: q, extrusionDepth: 0)).count)
    // decimation reference: start point choice, closing
    let r = NSBezierPath(); r.move(to: NSPoint(x: 0, y: 0)); r.line(to: NSPoint(x: 0.1, y: 0)); r.line(to: NSPoint(x: 0.1, y: 0.1)); r.line(to: NSPoint(x: 0.005, y: 0.1)); r.line(to: NSPoint(x: 0, y: 0.1)); r.line(to: NSPoint(x: 0, y: 0.005)); r.close()
    print("near-start points:", uniqueVerts(SCNShape(path: r, extrusionDepth: 0)).map { "(\($0.x),\($0.y))" })
}
