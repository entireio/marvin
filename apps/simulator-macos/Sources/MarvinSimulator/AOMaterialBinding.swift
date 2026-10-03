import Metal
import SceneKit

/// Diagnostic binding for the frozen GPU probe; restoration requires image
/// verification, not just restoring shader dictionaries. The texture enters
/// the PBR ambient-occlusion term, preserving existing material AO and direct
/// lighting. A texture of ones must reproduce the original AO-disabled image.
final class AOMaterialBinding {
    private var originals:[(NSObject,[SCNShaderModifierEntryPoint:String]?)]=[]
    private var originalMaterials:[(SCNGeometry,[SCNMaterial])]=[]
    private(set) var materialCount=0,geometryCount=0

    init(scene:SCNScene,texture:MTLTexture,literalNeutral:Bool=false) throws {
        var geometries:[SCNGeometry]=[],seenGeometry=Set<ObjectIdentifier>()
        func collect(_ geometry:SCNGeometry) {
            guard seenGeometry.insert(ObjectIdentifier(geometry)).inserted else { return }
            geometries.append(geometry)
            for lod in geometry.levelsOfDetail ?? [] { if let child=lod.geometry { collect(child) } }
        }
        scene.rootNode.enumerateChildNodes { node,_ in if let geometry=node.geometry { collect(geometry) } }
        let geometryOwners=geometries.filter { $0.shaderModifiers?[.surface] != nil }
        // Two existing owners at one entry point need explicit composition;
        // fail before mutating anything rather than silently replace either.
        guard geometryOwners.allSatisfy({ $0.materials.allSatisfy { $0.shaderModifiers?[.surface]==nil } }) else {
            throw NSError(domain:"AOMaterialBinding",code:1,userInfo:[NSLocalizedDescriptionKey:"Existing geometry and material surface modifiers require explicit composition"])
        }
        let reserved=Set(geometryOwners.flatMap { $0.materials.map { ObjectIdentifier($0) } })
        var seen=Set<ObjectIdentifier>()
        func bind(_ owner:NSObject) {
            guard seen.insert(ObjectIdentifier(owner)).inserted else { return }
            let original=(owner as? SCNGeometry)?.shaderModifiers ?? (owner as? SCNMaterial)?.shaderModifiers
            originals.append((owner,original))
            var modifiers=original ?? [:]
            var surface=modifiers[.surface] ?? "#pragma body\n"
            let argument="texture2d<float> marvinDiagnosticAO;"
            if literalNeutral {
                if !surface.contains("#pragma body") { surface="#pragma body\n"+surface }
            } else if surface.contains("#pragma arguments") {
                surface=surface.replacingOccurrences(of:"#pragma arguments",with:"#pragma arguments\n"+argument)
            } else {
                let body=surface.contains("#pragma body") ? surface:"#pragma body\n"+surface
                surface="#pragma arguments\n"+argument+"\n#pragma declaration\n"+body
            }
            surface += literalNeutral ? "\n_surface.ambientOcclusion *= 1.0;\n":"\n_surface.ambientOcclusion *= marvinDiagnosticAO.sample(sampler(filter::linear, address::clamp_to_edge), float2(0.5)).r;\n"
            // Neutral test uses a one-texel texture. Full-screen mapping is
            // deliberately not claimed by this first binding proof.
            modifiers[.surface]=surface
            if !literalNeutral { owner.setValue(SCNMaterialProperty(contents:texture),forKey:"marvinDiagnosticAO") }
            if let geometry=owner as? SCNGeometry { geometry.shaderModifiers=modifiers }
            if let material=owner as? SCNMaterial { material.shaderModifiers=modifiers }
            if owner is SCNGeometry { geometryCount += 1 } else { materialCount += 1 }
        }
        for geometry in geometries {
            if geometry.shaderModifiers?[.surface] != nil {
                if geometry.materials.contains(where:{$0.lightingModel == .physicallyBased}) { bind(geometry) }
            } else {
                // A material shared with a geometry-owned modifier cannot gain
                // its own competing surface modifier through another user.
                if geometry.materials.contains(where:{reserved.contains(ObjectIdentifier($0))}) {
                    originalMaterials.append((geometry,geometry.materials))
                    geometry.materials=geometry.materials.map { reserved.contains(ObjectIdentifier($0)) ? $0.copy() as! SCNMaterial:$0 }
                }
                for material in geometry.materials where material.lightingModel == .physicallyBased { bind(material) }
            }
        }
    }

    func restore() {
        for (owner,modifiers) in originals {
            if let geometry=owner as? SCNGeometry { geometry.shaderModifiers=modifiers }
            if let material=owner as? SCNMaterial { material.shaderModifiers=modifiers };owner.setValue(nil,forKey:"marvinDiagnosticAO")
        }
        for (geometry,materials) in originalMaterials { geometry.materials=materials }
        originals.removeAll()
        originalMaterials.removeAll()
    }
}
