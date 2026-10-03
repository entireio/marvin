import Foundation
import Metal
import SceneKit
import CryptoKit

/// Diagnostic only. No JPEG decoding:
/// its inputs must be the actual raw, uncompressed mip bytes used by SceneKit.
/// Init uploads textures and waits for that upload, so call before timed blocks.
final class GroundTexturePackProbe {
    enum Mode: String {
        case original
        case diffuseControl       // Explicit alpha=1 diffuse, original roughness binding.
        case dualTextureControl   // Explicit diffuse AND explicit original roughness.
        case packedAlphaControl   // Packed diffuse, reset alpha, original roughness.
        case packedAliased        // Same packed texture in both properties; preserve SceneKit PBR filtering.
    }
    struct Failure: Error, CustomStringConvertible {
        let description: String
        init(_ description: String) { self.description = description }
    }
    struct Level: Decodable {
        let file: String
        let sha256: String
        let width: Int
        let height: Int
        let bytesPerRow: Int
    }
    struct Chain: Decodable {
        let resource: String            // Xcode resource identity, not an inferred JPEG name.
        let format: String
        let swizzle: String             // "rgba", or observed "rrr1" for R8 roughness.
        let levels: [Level]             // Every original level, including 1 x 1.
    }
    struct Manifest: Decodable {
        let sourceKind: String         // "metal-resource-bytes"
        let captureUUID: String
        let captureDraw: String
        let shaderEvidence: String     // Recorded manual inspection of actual generated shader.
        let roughnessSampling: String  // "linear-unorm-red-before-scenekit-pbr-filter"
        let roughnessChannel: String   // "red", "green", "blue", or "alpha"
        let diffuseTextureComponents: UInt
        let roughnessTextureComponents: UInt
        let sourceDiffuseSHA256: String // Bind the capture to current source assets.
        let sourceRoughnessSHA256: String
        let surfaceModifierSHA256: String
        let diffuse: Chain
        let roughness: Chain
    }
    private struct Format {
        let metal: MTLPixelFormat
        let bytes: Int
        let offsets: [String: Int]
        let isSRGB: Bool
        init(_ name: String) throws {
            switch name {
            case "rgba8Unorm":
                metal = .rgba8Unorm; bytes = 4; offsets = ["red":0,"green":1,"blue":2,"alpha":3]; isSRGB = false
            case "rgba8Unorm_srgb":
                metal = .rgba8Unorm_srgb; bytes = 4; offsets = ["red":0,"green":1,"blue":2,"alpha":3]; isSRGB = true
            case "bgra8Unorm":
                metal = .bgra8Unorm; bytes = 4; offsets = ["blue":0,"green":1,"red":2,"alpha":3]; isSRGB = false
            case "bgra8Unorm_srgb":
                metal = .bgra8Unorm_srgb; bytes = 4; offsets = ["blue":0,"green":1,"red":2,"alpha":3]; isSRGB = true
            case "r8Unorm":
                metal = .r8Unorm; bytes = 1; offsets = ["red":0]; isSRGB = false
            case "rg8Unorm":
                metal = .rg8Unorm; bytes = 2; offsets = ["red":0,"green":1]; isSRGB = false
            default: throw Failure("Unsupported raw format: \(name). No conversion or requantization is permitted.")
            }
        }
    }
    private let material: SCNMaterial
    private let originalDiffuse: Any?
    private let originalRoughness: Any?
    private let originalModifiers: [SCNShaderModifierEntryPoint: String]?
    private let alphaControlModifiers: [SCNShaderModifierEntryPoint: String]
    private let originalRoughnessComponents: SCNColorMask
    private let diffuseControl: MTLTexture
    private let roughnessControl: MTLTexture
    private let packedTexture: MTLTexture
    let statistics: [String: Any]

    /// manifestDirectory contains manifest.json and raw mip files exported from
    /// the draw actually using TownGround.trampledGround. No NSImage/CGImage path.
    /// The caller must independently verify shaderEvidence in Xcode before use.
    init(material: SCNMaterial, device: MTLDevice, queue: MTLCommandQueue,
         manifestDirectory: URL) throws {
        let manifest = try JSONDecoder().decode(Manifest.self,
            from: Data(contentsOf: manifestDirectory.appendingPathComponent("manifest.json")))
        guard manifest.sourceKind == "metal-resource-bytes",
              !manifest.captureUUID.isEmpty, !manifest.captureDraw.isEmpty,
              !manifest.shaderEvidence.isEmpty,
              manifest.roughnessSampling == "linear-unorm-red-before-scenekit-pbr-filter",
              manifest.roughnessChannel == "red" else {
            throw Failure("Actual Metal resource and shader evidence is required; source-image guesses are unsupported.")
        }
        guard material.lightingModel == .physicallyBased, material.program == nil,
              material.diffuse.intensity == 1, material.roughness.intensity == 1,
              material.diffuse.textureComponents == .all,
              material.roughness.textureComponents == .all,
              material.diffuse.textureComponents.rawValue == manifest.diffuseTextureComponents,
              material.roughness.textureComponents.rawValue == manifest.roughnessTextureComponents,
              let diffuseURL = material.diffuse.contents as? URL,
              let roughnessURL = material.roughness.contents as? URL,
              Self.hash(try Data(contentsOf: diffuseURL)) == manifest.sourceDiffuseSHA256,
              Self.hash(try Data(contentsOf: roughnessURL)) == manifest.sourceRoughnessSHA256,
              let oldModifiers = material.shaderModifiers,
              let oldSurface = oldModifiers[.surface],
              let oldGeometry = oldModifiers[.geometry],
              oldGeometry == "#pragma varyings\nhalf groundBlend;\n#pragma body\nout.groundBlend=half(_geometry.color.a);",
              oldGeometry.components(separatedBy: "#pragma varyings").count == 2,
              oldGeometry.components(separatedBy: "#pragma body").count == 2,
              !oldGeometry.contains("groundPackVertexAlpha"),
              Self.hash(Data(oldSurface.utf8)) == manifest.surfaceModifierSHA256,
              oldSurface.components(separatedBy: "#pragma body").count == 2,
              oldSurface.contains("float grain=dot(_surface.diffuse.rgb"),
              oldSurface.contains("townPigment"),
              !oldSurface.contains("_surface.diffuse.a"),
              !oldSurface.contains("_surface.roughness") else {
            throw Failure("Material/source/shader no longer matches the captured town-ground binding.")
        }
        let a = material.diffuse, b = material.roughness
        guard a.mappingChannel == b.mappingChannel,
              SCNMatrix4EqualToMatrix4(a.contentsTransform, b.contentsTransform),
              a.wrapS == b.wrapS, a.wrapT == b.wrapT,
              a.minificationFilter == b.minificationFilter,
              a.magnificationFilter == b.magnificationFilter,
              a.mipFilter == b.mipFilter, a.maxAnisotropy == b.maxAnisotropy,
              a.wrapS == .repeat, a.wrapT == .repeat else {
            throw Failure("Diffuse and roughness samplers/UV transforms differ.")
        }
        let diffuseFormat = try Format(manifest.diffuse.format)
        let roughnessFormat = try Format(manifest.roughness.format)
        guard diffuseFormat.bytes == 4,
              manifest.diffuse.swizzle == "rgba",
              (manifest.roughness.swizzle == "rgba" ||
               (manifest.roughness.swizzle == "rrr1" && roughnessFormat.metal == .r8Unorm)),
              let roughnessOffset = roughnessFormat.offsets[manifest.roughnessChannel],
              !roughnessFormat.isSRGB || manifest.roughnessChannel == "alpha" else {
            throw Failure("Roughness must already be an 8-bit linear channel; RGB sRGB decoding cannot be copied into linear alpha.")
        }
        let diffuseBytes = try Self.read(manifest.diffuse, format: diffuseFormat, directory: manifestDirectory)
        let roughnessBytes = try Self.read(manifest.roughness, format: roughnessFormat, directory: manifestDirectory)
        guard manifest.diffuse.levels.count == manifest.roughness.levels.count else {
            throw Failure("Mip counts differ; do not regenerate or approximate either chain.")
        }
        var packedBytes: [Data] = []
        var mipChecks: [[String: Any]] = []
        for level in manifest.diffuse.levels.indices {
            let d = manifest.diffuse.levels[level], r = manifest.roughness.levels[level]
            guard d.width == r.width, d.height == r.height else {
                throw Failure("Diffuse/roughness dimensions differ at mip \(level).")
            }
            let source = [UInt8](diffuseBytes[level]), rough = [UInt8](roughnessBytes[level])
            var packed = source
            for pixel in 0..<(d.width * d.height) {
                guard source[pixel * 4 + 3] == 255 else {
                    throw Failure("Diffuse has non-opaque alpha at mip \(level); replacing it would change material behavior.")
                }
                packed[pixel * 4 + 3] = rough[pixel * roughnessFormat.bytes + roughnessOffset]
            }
            // No RGB math, color conversion, mip generation, or premultiplication.
            for pixel in 0..<(d.width * d.height) {
                for component in 0..<3 {
                    guard packed[pixel * 4 + component] == source[pixel * 4 + component] else {
                        throw Failure("Packing unexpectedly changed an RGB byte.")
                    }
                }
                guard packed[pixel * 4 + 3] == rough[pixel * roughnessFormat.bytes + roughnessOffset] else {
                    throw Failure("Packing unexpectedly changed a roughness byte.")
                }
            }
            let result = Data(packed)
            packedBytes.append(result)
            mipChecks.append(["level":level,"width":d.width,"height":d.height,
                              "diffuseSHA256":Self.hash(diffuseBytes[level]),
                              "roughnessSHA256":Self.hash(roughnessBytes[level]),
                              "packedSHA256":Self.hash(result),
                              "rgbBytesIdentical":true,"roughnessBytesIdentical":true])
        }
        guard queue.device.registryID == device.registryID,
              let command = queue.makeCommandBuffer(), let blit = command.makeBlitCommandEncoder() else {
            throw Failure("Cannot create texture upload command buffer on the rendering device.")
        }
        var uploadEncodingEnded = false
        defer { if !uploadEncodingEnded { blit.endEncoding() } }
        var uploads: [MTLTexture] = []
        // Identical destination storage and upload paths for controls and candidate.
        let explicitDiffuse = try Self.upload(chain: manifest.diffuse, format: diffuseFormat,
            data: diffuseBytes, device: device, blit: blit, staging: &uploads, label: "GroundPack alpha=1 control")
        let explicitRoughness = try Self.upload(chain: manifest.roughness, format: roughnessFormat,
            data: roughnessBytes, device: device, blit: blit, staging: &uploads, label: "GroundPack roughness control")
        let explicitPacked = try Self.upload(chain: manifest.diffuse, format: diffuseFormat,
            data: packedBytes, device: device, blit: blit, staging: &uploads, label: "GroundPack packed candidate")
        blit.endEncoding(); uploadEncodingEnded = true
        command.commit(); command.waitUntilCompleted()
        guard command.status == .completed else {
            throw Failure("Texture upload failed: \(command.error?.localizedDescription ?? "unknown error")")
        }
        // Keep staging resources alive through completion (including optimized builds).
        withExtendedLifetime(uploads) {}
        func injecting(_ code: String) -> [SCNShaderModifierEntryPoint: String] {
            var modifiers = oldModifiers
            modifiers[.surface] = oldSurface.replacingOccurrences(of: "#pragma body", with: "#pragma body\n" + code)
            return modifiers
        }
        self.material = material
        originalDiffuse = material.diffuse.contents
        originalRoughness = material.roughness.contents
        originalModifiers = material.shaderModifiers
        originalRoughnessComponents = material.roughness.textureComponents
        diffuseControl = explicitDiffuse
        roughnessControl = explicitRoughness
        packedTexture = explicitPacked
        // SceneKit multiplies diffuse alpha by vertex alpha before this surface
        // modifier. Preserve the original alpha with a FLOAT varying; the old
        // half groundBlend varying would introduce an avoidable precision change.
        var restoringVertexAlpha = injecting("_surface.diffuse.a = in.groundPackVertexAlpha;\n")
        restoringVertexAlpha[.geometry] = oldGeometry
            .replacingOccurrences(of: "#pragma varyings", with: "#pragma varyings\nfloat groundPackVertexAlpha;")
            .replacingOccurrences(of: "#pragma body", with: "#pragma body\nout.groundPackVertexAlpha = _geometry.color.a;")
        alphaControlModifiers = restoringVertexAlpha
        statistics = ["diagnosticOnly":true,"captureUUID":manifest.captureUUID,
                      "captureDraw":manifest.captureDraw,"shaderEvidence":manifest.shaderEvidence,
                      "diffuseResource":manifest.diffuse.resource,"roughnessResource":manifest.roughness.resource,
                      "diffuseFormat":manifest.diffuse.format,"roughnessFormat":manifest.roughness.format,
                      "roughnessChannel":manifest.roughnessChannel,"mips":mipChecks,
                      "roughnessSwizzle":manifest.roughness.swizzle,
                      "roughnessFilterPreservedByMaterialBinding":true,
                      "singleTextureSampleNotClaimed":true,
                      "normalMapUnchanged":true,"geometryUnchanged":true,
                      "byteProofIsNotPixelProof":true]
    }

    func setMode(_ mode: Mode) {
        restore()
        switch mode {
        case .original: break
        case .diffuseControl:
            material.diffuse.contents = diffuseControl
        case .dualTextureControl:
            material.diffuse.contents = diffuseControl
            material.roughness.contents = roughnessControl
        case .packedAlphaControl:
            material.diffuse.contents = packedTexture
            material.shaderModifiers = alphaControlModifiers
        case .packedAliased:
            material.diffuse.contents = packedTexture
            material.roughness.contents = packedTexture
            material.roughness.textureComponents = .alpha
            material.shaderModifiers = alphaControlModifiers
        }
    }
    func restore() {
        material.diffuse.contents = originalDiffuse
        material.roughness.contents = originalRoughness
        material.roughness.textureComponents = originalRoughnessComponents
        material.shaderModifiers = originalModifiers
    }
    deinit { restore() }

    private static func hash(_ data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }
    private static func read(_ chain: Chain, format: Format, directory: URL) throws -> [Data] {
        guard let first = chain.levels.first, first.width > 0, first.height > 0,
              first.width <= 16384, first.height <= 16384 else { throw Failure("Invalid texture dimensions.") }
        var width = first.width, height = first.height, result: [Data] = []
        for (index, mip) in chain.levels.enumerated() {
            guard mip.width == width, mip.height == height,
                  index == 0 || chain.levels[index-1].width > 1 || chain.levels[index-1].height > 1,
                  mip.bytesPerRow >= width * format.bytes,
                  !mip.file.isEmpty, !mip.file.contains("/"), !mip.file.contains("..") else {
                throw Failure("Invalid mip layout/path at level \(index).")
            }
            let bytes = try Data(contentsOf: directory.appendingPathComponent(mip.file))
            guard bytes.count == mip.bytesPerRow * height, hash(bytes) == mip.sha256 else {
                throw Failure("Raw mip hash/size mismatch: \(mip.file)")
            }
            var tight = Data(capacity: width * height * format.bytes)
            for row in 0..<height {
                tight.append(bytes[(row*mip.bytesPerRow)..<(row*mip.bytesPerRow + width*format.bytes)])
            }
            result.append(tight)
            width = max(1, width/2); height = max(1, height/2)
        }
        guard chain.levels.last?.width == 1, chain.levels.last?.height == 1 else {
            throw Failure("Complete original mip chain through 1x1 required; mip generation is forbidden.")
        }
        return result
    }
    private static func upload(chain: Chain, format: Format, data: [Data], device: MTLDevice,
                               blit: MTLBlitCommandEncoder, staging: inout [MTLTexture], label: String) throws -> MTLTexture {
        let first = chain.levels[0]
        let descriptor = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: format.metal,
            width: first.width, height: first.height, mipmapped: true)
        descriptor.mipmapLevelCount = data.count
        descriptor.storageMode = .shared; descriptor.usage = .shaderRead
        guard let source = device.makeTexture(descriptor: descriptor) else { throw Failure("Staging texture allocation failed.") }
        descriptor.storageMode = .private
        switch chain.swizzle {
        case "rgba":
            descriptor.swizzle = MTLTextureSwizzleChannels(red: .red, green: .green, blue: .blue, alpha: .alpha)
        case "rrr1" where format.metal == .r8Unorm:
            descriptor.swizzle = MTLTextureSwizzleChannels(red: .red, green: .red, blue: .red, alpha: .one)
        default: throw Failure("Unsupported texture swizzle.")
        }
        guard let destination = device.makeTexture(descriptor: descriptor) else { throw Failure("Private texture allocation failed.") }
        let observed = destination.swizzle, expected = descriptor.swizzle
        guard observed.red == expected.red, observed.green == expected.green,
              observed.blue == expected.blue, observed.alpha == expected.alpha else {
            throw Failure("Metal did not preserve the requested texture swizzle.")
        }
        destination.label = label
        for level in data.indices {
            let mip = chain.levels[level], region = MTLRegionMake2D(0, 0, mip.width, mip.height)
            data[level].withUnsafeBytes {
                source.replace(region: region, mipmapLevel: level, withBytes: $0.baseAddress!, bytesPerRow: mip.width * format.bytes)
            }
            blit.copy(from: source, sourceSlice: 0, sourceLevel: level, sourceOrigin: MTLOrigin(x:0,y:0,z:0),
                      sourceSize: MTLSize(width:mip.width,height:mip.height,depth:1), to: destination,
                      destinationSlice: 0, destinationLevel: level, destinationOrigin: MTLOrigin(x:0,y:0,z:0))
        }
        staging.append(source)
        return destination
    }
}
