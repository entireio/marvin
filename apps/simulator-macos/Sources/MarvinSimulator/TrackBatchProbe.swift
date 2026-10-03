import AppKit
import SceneKit
import simd

/// FROZEN-POSE DIAGNOSTIC ONLY. No animation/update path and no game default.
/// Build after DirtCoating.install and after the native renderer has settled.
/// Keep simulation stopped while this object exists. Camera/root motion is OK;
/// shoe/rib local transforms and geometry must remain the captured pose.
final class TrackBatchProbe {
    enum Mode: String { case original, copyControl, batch }
    struct Failure: Error, CustomStringConvertible {
        let description: String
        init(_ description: String) { self.description = description }
    }
    private struct Original {
        let node: SCNNode
        let geometry: SCNGeometry
        let copy: SCNGeometry
        let transform: simd_float4x4
        let hidden: Bool
    }
    private struct Built {
        let parent: SCNNode
        let node: SCNNode
        let row: [String: Any]
    }
    private var originals: [Original] = []
    private var batches: [Built] = []
    private(set) var mode: Mode = .original
    private static let uniformKeys = ["dirtToBody", "dirtHeight", "dirtWheelX", "duneContact", "dirtRolling"]
    private static let referenceLine = "float3 p = (dirtToBody * float4(local,1.0)).xyz / dirtHeight;"
    private static let geometryModifier = """
    #pragma varyings
    float3 trackFrozenRestBody;
    #pragma body
    out.trackFrozenRestBody = _geometry.color.xyz;
    _geometry.color = float4(1.0);
    """

    init(root: SCNNode, tracks: [TrackBelt]) throws {
        try Self.check(tracks.count == 2, "Expected two native TrackBelt instances")
        var seen = Set<ObjectIdentifier>()
        var pending: [Built] = []
        for (beltIndex, belt) in tracks.enumerated() {
            try Self.check(belt.node.parent === root && belt.shoes.count == 56,
                           "Unexpected belt parent or shoe count")
            try Self.check(!belt.node.isHidden && belt.node.opacity == 1,
                           "Hidden or translucent belt")
            var ribs: [SCNNode] = []
            for shoe in belt.shoes {
                try Self.check(shoe.parent === belt.node && shoe.childNodes.count == 1,
                               "Unexpected shoe hierarchy")
                let rib = shoe.childNodes[0]
                try Self.check(rib.childNodes.isEmpty, "Unexpected rib hierarchy")
                for node in [shoe, rib] {
                    try Self.check(seen.insert(ObjectIdentifier(node)).inserted,
                                   "A source node was selected twice")
                    try Self.check(!node.isHidden && node.opacity == 1 && node.skinner == nil && node.morpher == nil,
                                   "Hidden, translucent, skinned or morphed source")
                    try Self.check(Self.equal(Self.matrix(node.pivot), matrix_identity_float4x4) && node.animationKeys.isEmpty,
                                   "Nonidentity pivot or animated source")
                    try Self.check(node.constraints?.isEmpty ?? true, "Constrained source")
                    guard let source = node.geometry else { throw Failure("Missing source geometry") }
                    try Self.validateGeometry(source)
                    originals.append(Original(node: node, geometry: source,
                                              copy: try Self.clone(source),
                                              transform: node.simdTransform, hidden: node.isHidden))
                }
                ribs.append(rib)
            }
            // Keep the carcass and all nonselected nodes untouched.
            pending.append(try build(nodes: belt.shoes, parent: belt.node,
                                     name: "Frozen belt \(beltIndex) shoes"))
            pending.append(try build(nodes: ribs, parent: belt.node,
                                     name: "Frozen belt \(beltIndex) ribs"))
        }
        // Do not mutate the scene until all four batches have passed preflight.
        batches = pending
        for item in batches { item.parent.addChildNode(item.node) }
    }

    deinit {
        restore()
        for item in batches { item.node.removeFromParentNode() }
    }

    func setEnabled(_ value: Bool) throws { try setMode(value ? .batch : .original) }

    func setMode(_ value: Mode) throws {
        if value != .original {
            for item in originals {
                try Self.check(Self.equal(item.node.simdTransform, item.transform),
                               "Track moved after frozen capture; rebuild the probe")
                try Self.check(item.node.geometry === item.geometry || item.node.geometry === item.copy,
                               "Source geometry changed after capture")
            }
        }
        restore()
        if value == .copyControl {
            for item in originals { item.node.geometry = item.copy }
        } else if value == .batch {
            for item in originals { item.node.isHidden = true }
            for item in batches { item.node.isHidden = false }
        }
        mode = value
    }

    private func restore() {
        for item in originals {
            item.node.geometry = item.geometry
            item.node.isHidden = item.hidden
        }
        for item in batches { item.node.isHidden = true }
        mode = .original
    }

    var statistics: [String: Any] {
        ["method": "Frozen native shoe/rib geometry batching; every original vertex and triangle retained. Initial dirtToBody reference encoded per vertex; current world contact retains the original surface expression.",
         "mode": mode.rawValue, "originalGeometryNodes": originals.count,
         "batchGeometryNodes": batches.count, "batches": batches.map(\.row),
         "copyControl": "Original nodes, unchanged source/index objects, materials and geometry-owned modifier/uniforms; new SCNGeometry wrappers only.",
         "frozenPoseOnly": true, "dynamicAnimationValidated": false,
         "performanceOrAppearanceAccepted": false,
         "limitations": ["CPU Float baking and rest-coordinate interpolation may change rounding versus SceneKit transforms/inverse reconstruction.",
                         "Reported numeric errors are against Double evaluation of the captured Float matrices, not native shader-output or image errors.",
                         "Merged material draws change cross-node ordering and culling granularity. Inspect color, AO, both shadows, dirt and dune contact.",
                         "The new rest-coordinate varying uses an added color stream, reset to white in the geometry modifier; verify generated shader behavior."]]
    }

    private func build(nodes: [SCNNode], parent: SCNNode, name: String) throws -> Built {
        guard let firstNode = nodes.first, let first = firstNode.geometry,
              let material = first.materials.first,
              let surface = first.shaderModifiers?[.surface] else { throw Failure("Empty batch") }
        let layout = first.sources
        var output = layout.map { _ in Data() }
        var rest = Data(), indices: [UInt32] = []
        var vertexCount = 0, primitiveCount = 0, inputElements = 0
        var positionError = 0.0, normalError = 0.0, tangentError = 0.0, restError = 0.0
        var rows: [[String: Any]] = []
        for (ordinal, node) in nodes.enumerated() {
            guard let source = node.geometry,
                  let cachedRest = source.value(forKey: "dirtToBody") as? NSValue else {
                throw Failure("Missing dirtToBody transform")
            }
            try Self.validateGeometry(source)
            try Self.check(source.sources.count == layout.count, "Source layout count differs")
            for (a, b) in zip(source.sources, layout) {
                try Self.check(a.semantic == b.semantic && a.componentsPerVector == b.componentsPerVector &&
                               a.bytesPerComponent == b.bytesPerComponent && a.usesFloatComponents == b.usesFloatComponents,
                               "Source layout differs")
            }
            try Self.check(source.shaderModifiers?[.surface] == surface, "Coating shader differs")
            try Self.check(node.castsShadow == firstNode.castsShadow &&
                           node.categoryBitMask == firstNode.categoryBitMask &&
                           node.renderingOrder == firstNode.renderingOrder,
                           "Node render state differs")
            for m in source.materials { try Self.sameMaterial(m, material) }
            for key in Self.uniformKeys where key != "dirtToBody" {
                try Self.check(Self.sameValue(source.value(forKey: key), first.value(forKey: key)),
                               "Coating uniform differs: \(key)")
            }
            // Compose only local transforms. Converting via world coordinates
            // loses micrometres when Marvin is far from the scene origin.
            let current: simd_float4x4
            if node.parent === parent {
                current = node.simdTransform
            } else if let shoe = node.parent, shoe.parent === parent {
                current = shoe.simdTransform * node.simdTransform
            } else {
                throw Failure("Source is outside the validated local belt hierarchy")
            }
            let reference = Self.matrix(cachedRest.scnMatrix4Value)
            try Self.checkRigid(current)
            try Self.checkRigid(reference)
            let linear = simd_float3x3(columns: (SIMD3(current[0].x,current[0].y,current[0].z),
                                                SIMD3(current[1].x,current[1].y,current[1].z),
                                                SIMD3(current[2].x,current[2].y,current[2].z)))
            let inverseTranspose = simd_transpose(simd_inverse(linear))
            let normalMatrix = simd_float4x4(columns: (
                SIMD4(inverseTranspose[0].x,inverseTranspose[0].y,inverseTranspose[0].z,0),
                SIMD4(inverseTranspose[1].x,inverseTranspose[1].y,inverseTranspose[1].z,0),
                SIMD4(inverseTranspose[2].x,inverseTranspose[2].y,inverseTranspose[2].z,0),
                SIMD4<Float>(0,0,0,1)))
            let positionSource = source.sources(for: .vertex)[0]
            let count = positionSource.vectorCount
            let base = UInt32(vertexCount)
            for vertex in 0..<count {
                let p = try Self.read3(positionSource, vertex)
                let q = reference * SIMD4<Float>(p.x, p.y, p.z, 1)
                restError = max(restError, Self.error(q, Self.precise(reference, SIMD4<Float>(p.x, p.y, p.z, 1))))
                Self.append([q.x, q.y, q.z, 1], to: &rest)
                for (slot, attribute) in source.sources.enumerated() {
                    if attribute.semantic == .vertex {
                        let moved = current * SIMD4<Float>(p.x, p.y, p.z, 1)
                        positionError = max(positionError, Self.error(moved, Self.precise(current, SIMD4<Float>(p.x, p.y, p.z, 1))))
                        Self.append([moved.x, moved.y, moved.z], to: &output[slot])
                    } else if attribute.semantic == .normal || attribute.semantic == .tangent {
                        let n = try Self.read3(attribute, vertex)
                        let transform = attribute.semantic == .normal ? normalMatrix : current
                        let moved = transform * SIMD4<Float>(n.x, n.y, n.z, 0)
                        let error = Self.error(moved, Self.precise(transform, SIMD4<Float>(n.x, n.y, n.z, 0)))
                        if attribute.semantic == .normal { normalError = max(normalError, error) }
                        else { tangentError = max(tangentError, error) }
                        Self.append([moved.x, moved.y, moved.z], to: &output[slot])
                        if attribute.componentsPerVector == 4 {
                            let offset = attribute.dataOffset + vertex * attribute.dataStride + 12
                            output[slot].append(attribute.data.subdata(in: offset..<(offset + 4)))
                        }
                    } else {
                        let offset = attribute.dataOffset + vertex * attribute.dataStride
                        output[slot].append(attribute.data.subdata(in: offset..<(offset + attribute.componentsPerVector * 4)))
                    }
                }
            }
            var nodeTriangles = 0
            for element in source.elements {
                try Self.check(element.primitiveType == .triangles && [1, 2, 4].contains(element.bytesPerIndex),
                               "Nontriangle or unsupported indices")
                try Self.check(element.primitiveRange.location == NSNotFound ||
                               (element.primitiveRange.location == 0 && element.primitiveRange.length == element.primitiveCount),
                               "Partial primitive range is unsupported")
                let bytes = element.data
                try Self.check(bytes.count >= element.primitiveCount * 3 * element.bytesPerIndex,
                               "Truncated index data")
                for index in 0..<(element.primitiveCount * 3) {
                    let old = bytes.withUnsafeBytes { raw -> UInt32 in
                        let offset = index * element.bytesPerIndex
                        if element.bytesPerIndex == 1 { return UInt32(raw.loadUnaligned(fromByteOffset: offset, as: UInt8.self)) }
                        if element.bytesPerIndex == 2 { return UInt32(raw.loadUnaligned(fromByteOffset: offset, as: UInt16.self)) }
                        return raw.loadUnaligned(fromByteOffset: offset, as: UInt32.self)
                    }
                    try Self.check(old < UInt32(count), "Index is out of bounds")
                    indices.append(base + old)
                }
                nodeTriangles += element.primitiveCount
            }
            primitiveCount += nodeTriangles
            inputElements += source.elements.count
            vertexCount += count
            rows.append(["ordinal": ordinal, "vertices": count, "triangles": nodeTriangles,
                         "currentToBelt": Self.values(current), "initialToBody": Self.values(reference)])
        }
        var sources = zip(layout, output).map { source, data in
            SCNGeometrySource(data: data, semantic: source.semantic, vectorCount: vertexCount,
                              usesFloatComponents: true, componentsPerVector: source.componentsPerVector,
                              bytesPerComponent: 4, dataOffset: 0, dataStride: source.componentsPerVector * 4)
        }
        sources.append(SCNGeometrySource(data: rest, semantic: .color, vectorCount: vertexCount,
                                         usesFloatComponents: true, componentsPerVector: 4,
                                         bytesPerComponent: 4, dataOffset: 0, dataStride: 16))
        let element = SCNGeometryElement(indices: indices, primitiveType: .triangles)
        let geometry = SCNGeometry(sources: sources, elements: [element])
        geometry.materials = [material]
        geometry.shaderModifiers = [.geometry: Self.geometryModifier,
            .surface: surface.replacingOccurrences(of: Self.referenceLine,
                       with: "float3 p = in.trackFrozenRestBody / dirtHeight;")]
        for key in Self.uniformKeys { geometry.setValue(first.value(forKey: key), forKey: key) }
        let batch = SCNNode(geometry: geometry)
        batch.name = name; batch.isHidden = true
        batch.castsShadow = firstNode.castsShadow
        batch.categoryBitMask = firstNode.categoryBitMask
        batch.renderingOrder = firstNode.renderingOrder
        return Built(parent: parent, node: batch, row: [
            "name": name, "inputNodes": nodes.count, "inputElements": inputElements,
            "outputNodes": 1, "outputElements": 1, "vertices": vertexCount,
            "triangles": primitiveCount, "triangleCountPreserved": indices.count == primitiveCount * 3,
            "allOriginalVerticesRetained": true, "indicesOnlyRebasedInOriginalNodeElementTriangleOrder": true,
            "untouchedAttributeBytesCopied": true,
            "maxPositionComponentErrorVsDouble": positionError,
            "maxNormalComponentErrorVsDouble": normalError,
            "maxTangentComponentErrorVsDouble": tangentError,
            "maxRestBodyComponentErrorVsDouble": restError,
            "poses": rows])
    }

    private static func validateGeometry(_ geometry: SCNGeometry) throws {
        try check(geometry.program == nil && geometry.tessellator == nil && geometry.subdivisionLevel == 0 &&
                  (geometry.levelsOfDetail?.isEmpty ?? true), "Program, tessellation, subdivision or LOD is unsupported")
        guard let modifiers = geometry.shaderModifiers, modifiers.count == 1,
              let surface = modifiers[.surface] else { throw Failure("Expected only the DirtCoating surface modifier") }
        try check(surface.components(separatedBy: referenceLine).count == 2 &&
                  surface.contains("float3 local = (scn_node.inverseModelViewTransform * float4(_surface.position,1.0)).xyz;") &&
                  surface.contains("float3 world=(scn_node.modelTransform*float4(local,1.0)).xyz;"),
                  "Unexpected DirtCoating coordinate expressions")
        for forbidden in ["#pragma transparent", "discard", "_surface.diffuse.a", "_surface.transparent", "_output"] {
            try check(!surface.contains(forbidden), "Unsupported side effect in coating modifier")
        }
        try check(geometry.sources(for: .vertex).count == 1 && geometry.sources(for: .normal).count == 1 &&
                  geometry.sources(for: .color).isEmpty, "Expected position/normal and no existing color stream")
        let count = geometry.sources(for: .vertex)[0].vectorCount
        try check(count > 0 && count < Int(UInt32.max), "Invalid vertex count")
        for source in geometry.sources {
            let allowed: [SCNGeometrySource.Semantic] = [.vertex, .normal, .tangent, .texcoord]
            try check(allowed.contains(source.semantic) && source.usesFloatComponents &&
                      source.bytesPerComponent == 4 && source.vectorCount == count,
                      "Unsupported attribute semantic, format or count")
            let expected = source.semantic == .texcoord ? 2 : source.semantic == .tangent ? 4 : 3
            try check(source.componentsPerVector == expected && source.dataOffset >= 0 &&
                      source.dataStride >= expected * 4 &&
                      source.dataOffset + (count - 1) * source.dataStride + expected * 4 <= source.data.count,
                      "Unsupported/truncated attribute layout")
        }
        try check(!geometry.materials.isEmpty, "No source materials")
        for material in geometry.materials {
            try check(material.lightingModel == .physicallyBased && material.program == nil &&
                      (material.shaderModifiers?.isEmpty ?? true) && material.transparency == 1 &&
                      material.writesToDepthBuffer && material.readsFromDepthBuffer,
                      "Nonopaque/custom material is unsupported")
            guard let color = material.diffuse.contents as? NSColor else { throw Failure("Textured diffuse is unsupported") }
            try check(color.alphaComponent == 1, "Translucent diffuse is unsupported")
        }
        for key in uniformKeys { try check(geometry.value(forKey: key) != nil, "Missing coating uniform \(key)") }
    }

    private static func clone(_ original: SCNGeometry) throws -> SCNGeometry {
        let copy = SCNGeometry(sources: original.sources, elements: original.elements)
        copy.materials = original.materials; copy.shaderModifiers = original.shaderModifiers
        copy.name = original.name
        for key in uniformKeys { copy.setValue(original.value(forKey: key), forKey: key) }
        return copy
    }

    private static func sameMaterial(_ a: SCNMaterial, _ b: SCNMaterial) throws {
        try check(a.lightingModel == b.lightingModel && a.isDoubleSided == b.isDoubleSided &&
                  a.cullMode == b.cullMode && a.blendMode == b.blendMode &&
                  a.transparencyMode == b.transparencyMode && a.transparency == b.transparency &&
                  a.shininess == b.shininess && a.fresnelExponent == b.fresnelExponent &&
                  a.locksAmbientWithDiffuse == b.locksAmbientWithDiffuse &&
                  a.readsFromDepthBuffer == b.readsFromDepthBuffer && a.writesToDepthBuffer == b.writesToDepthBuffer &&
                  a.colorBufferWriteMask == b.colorBufferWriteMask,
                  "Material render properties differ")
        let ap = [a.diffuse,a.ambient,a.specular,a.emission,a.transparent,a.reflective,a.multiply,
                  a.normal,a.displacement,a.ambientOcclusion,a.metalness,a.roughness]
        let bp = [b.diffuse,b.ambient,b.specular,b.emission,b.transparent,b.reflective,b.multiply,
                  b.normal,b.displacement,b.ambientOcclusion,b.metalness,b.roughness]
        for (x,y) in zip(ap,bp) {
            try check(sameValue(x.contents,y.contents) && x.intensity == y.intensity &&
                      x.mappingChannel == y.mappingChannel && x.wrapS == y.wrapS && x.wrapT == y.wrapT &&
                      x.minificationFilter == y.minificationFilter && x.magnificationFilter == y.magnificationFilter &&
                      x.mipFilter == y.mipFilter && x.maxAnisotropy == y.maxAnisotropy &&
                      equal(matrix(x.contentsTransform),matrix(y.contentsTransform)), "Material contents differ")
        }
    }

    private static func sameValue(_ a: Any?, _ b: Any?) -> Bool {
        if a == nil && b == nil { return true }
        guard let x = a as? NSObject, let y = b as? NSObject else { return false }
        return x === y || x.isEqual(y)
    }
    private static func check(_ condition: Bool, _ message: String) throws {
        if !condition { throw Failure(message) }
    }
    private static func read3(_ source: SCNGeometrySource, _ vertex: Int) throws -> SIMD3<Float> {
        let offset = source.dataOffset + vertex * source.dataStride
        let result = source.data.withUnsafeBytes { raw in
            SIMD3<Float>(raw.loadUnaligned(fromByteOffset: offset, as: Float.self),
                         raw.loadUnaligned(fromByteOffset: offset + 4, as: Float.self),
                         raw.loadUnaligned(fromByteOffset: offset + 8, as: Float.self))
        }
        try check(result.x.isFinite && result.y.isFinite && result.z.isFinite, "Nonfinite attribute")
        return result
    }
    private static func append(_ values: [Float], to data: inout Data) {
        values.withUnsafeBytes { data.append(contentsOf: $0) }
    }
    private static func matrix(_ m: SCNMatrix4) -> simd_float4x4 {
        simd_float4x4(columns: (SIMD4(Float(m.m11),Float(m.m12),Float(m.m13),Float(m.m14)),
                                SIMD4(Float(m.m21),Float(m.m22),Float(m.m23),Float(m.m24)),
                                SIMD4(Float(m.m31),Float(m.m32),Float(m.m33),Float(m.m34)),
                                SIMD4(Float(m.m41),Float(m.m42),Float(m.m43),Float(m.m44))))
    }
    private static func equal(_ a: simd_float4x4, _ b: simd_float4x4) -> Bool {
        (0..<4).allSatisfy { a[$0] == b[$0] }
    }
    private static func checkRigid(_ m: simd_float4x4) throws {
        for c in 0..<4 { for r in 0..<4 { try check(m[c][r].isFinite, "Nonfinite transform") } }
        try check(m[0].w == 0 && m[1].w == 0 && m[2].w == 0 && m[3].w == 1, "Nonaffine transform")
        let a = SIMD3(m[0].x,m[0].y,m[0].z), b = SIMD3(m[1].x,m[1].y,m[1].z), c = SIMD3(m[2].x,m[2].y,m[2].z)
        try check(abs(simd_length_squared(a)-1) < 0.000002 && abs(simd_length_squared(b)-1) < 0.000002 &&
                  abs(simd_length_squared(c)-1) < 0.000002 && abs(simd_dot(a,b)) < 0.000002 &&
                  abs(simd_dot(a,c)) < 0.000002 && abs(simd_dot(b,c)) < 0.000002 &&
                  abs(simd_dot(simd_cross(a,b),c)-1) < 0.000004,
                  "Only rigid, orientation-preserving transforms are supported")
    }
    private static func precise(_ m: simd_float4x4, _ v: SIMD4<Float>) -> SIMD4<Double> {
        var result = SIMD4<Double>.zero
        for row in 0..<4 { for column in 0..<4 { result[row] += Double(m[column][row]) * Double(v[column]) } }
        return result
    }
    private static func error(_ value: SIMD4<Float>, _ reference: SIMD4<Double>) -> Double {
        (0..<3).map { abs(Double(value[$0])-reference[$0]) }.max() ?? 0
    }
    private static func values(_ m: simd_float4x4) -> [Float] {
        (0..<4).flatMap { column in (0..<4).map { row in m[column][row] } }
    }
}
