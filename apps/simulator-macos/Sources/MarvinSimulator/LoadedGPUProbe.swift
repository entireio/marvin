import AppKit
import Metal
import CoreImage
import SceneKit

extension AppController {
    /// Diagnostic only: freeze the completed native drive and encode the full
    /// scene into an owned command buffer before committing it. This removes
    /// display pacing from the GPU envelope, but is not presentation evidence.
    func profileLoadedGPU(at directory:URL) throws {
        guard let device=view.device,let queue=device.makeCommandQueue() else { return }
        let live=view.snapshot()
        if let tiff=live.tiffRepresentation,let bitmap=NSBitmapImageRep(data:tiff) {
            try bitmap.representation(using:.png,properties:[:])!.write(to:directory.appendingPathComponent("gpu-probe-live.png"))
        }
        let colorFormat=view.colorPixelFormat,depthFormat=view.depthPixelFormat,stencilFormat=view.stencilPixelFormat
        let reverseZ=view.usesReverseZ
        let scene=dirtWorld.scene
        view.rendersContinuously=false;view.isPlaying=false;view.scene=nil
        let renderer=SCNRenderer(device:device,options:nil)
        renderer.scene=scene;renderer.pointOfView=world.camera;renderer.usesReverseZ=reverseZ
        let color=MTLTextureDescriptor.texture2DDescriptor(pixelFormat:colorFormat,width:1920,height:1080,mipmapped:false)
        color.usage=[.renderTarget,.shaderRead];color.storageMode = .private
        let resolved=device.makeTexture(descriptor:color)!
        color.textureType = .type2DMultisample;color.sampleCount=2
        let multisample=device.makeTexture(descriptor:color)!
        let depth=MTLTextureDescriptor.texture2DDescriptor(pixelFormat:depthFormat,width:1920,height:1080,mipmapped:false)
        depth.textureType = .type2DMultisample;depth.sampleCount=2;depth.storageMode = .private;depth.usage = .renderTarget
        let depthTexture=device.makeTexture(descriptor:depth)!
        let pass=MTLRenderPassDescriptor()
        pass.colorAttachments[0].texture=multisample;pass.colorAttachments[0].resolveTexture=resolved
        pass.colorAttachments[0].loadAction = .clear;pass.colorAttachments[0].storeAction = .multisampleResolve
        pass.depthAttachment.clearDepth=reverseZ ? 0:1
        pass.depthAttachment.texture=depthTexture;pass.depthAttachment.loadAction = .clear;pass.depthAttachment.storeAction = .dontCare
        if stencilFormat != .invalid {
            if stencilFormat==depthFormat { pass.stencilAttachment.texture=depthTexture }
            else { depth.pixelFormat=stencilFormat;pass.stencilAttachment.texture=device.makeTexture(descriptor:depth)! }
            pass.stencilAttachment.loadAction = .clear;pass.stencilAttachment.storeAction = .dontCare
        }
        let viewport=CGRect(x:0,y:0,width:1920,height:1080)
        let terrain=scene.rootNode.childNode(withName:"Town base terrain",recursively:true)!
        let currentGround=terrain.geometry!
        let oldGround=SCNPlane(width:256,height:256);oldGround.materials=[currentGround.materials[0]]
        let sky=scene.rootNode.childNode(withName:"Binary daylight",recursively:true)!
        var shadowLights:[SCNLight]=[]
        var shadowNodes:[SCNNode]=[]
        scene.rootNode.enumerateChildNodes { node,_ in
            if let light=node.light,light.castsShadow { shadowLights.append(light) }
            if node.geometry != nil && node.castsShadow { shadowNodes.append(node) }
        }
        var noiseMaterials:[ObjectIdentifier:(SCNMaterial,[SCNShaderModifierEntryPoint:String])]=[:]
        scene.rootNode.enumerateChildNodes { node,_ in
            for material in node.geometry?.materials ?? [] {
                if let modifiers=material.shaderModifiers,modifiers.values.contains(where:{$0.contains(TownGround.pigmentFunctions)}) { noiseMaterials[ObjectIdentifier(material)]=(material,modifiers) }
            }
        }
        var tintMaterials:[ObjectIdentifier:(SCNMaterial,[SCNShaderModifierEntryPoint:String])]=[:]
        scene.rootNode.enumerateChildNodes { node,_ in
            for geometry in [node.geometry]+(node.geometry?.levelsOfDetail ?? []).map({$0.geometry}) {
                for material in geometry?.materials ?? [] {
                    if let modifiers=material.shaderModifiers,modifiers.values.contains(where:{$0.contains("pow(max(_geometry.color.rgb")}) { tintMaterials[ObjectIdentifier(material)]=(material,modifiers) }
                }
            }
        }
        var crowdMaterials:[ObjectIdentifier:(SCNMaterial,[SCNShaderModifierEntryPoint:String])]=[:]
        var crowdNodes:[(SCNNode,Bool)]=[]
        scene.rootNode.enumerateChildNodes { node,_ in
            var animated=false
            for geometry in [node.geometry]+(node.geometry?.levelsOfDetail ?? []).map({$0.geometry}) {
                for material in geometry?.materials ?? [] {
                    if let modifiers=material.shaderModifiers,modifiers[.geometry]?.contains("float2 originXZ = _geometry.texcoords[1]")==true {
                        crowdMaterials[ObjectIdentifier(material)]=(material,modifiers);animated=true
                    }
                }
            }
            if animated { crowdNodes.append((node,node.isHidden)) }
        }
        var textureProperties:[ObjectIdentifier:(SCNMaterialProperty,Any?)]=[:]
        scene.rootNode.enumerateChildNodes { node,_ in
            for geometry in [node.geometry]+(node.geometry?.levelsOfDetail ?? []).map({$0.geometry}) {
                for material in geometry?.materials ?? [] {
                    for property in [material.normal,material.roughness] where property.contents is URL || property.contents is NSImage || property.contents is MTLTexture {
                        textureProperties[ObjectIdentifier(property)]=(property,property.contents)
                    }
                }
            }
        }
        let tinyDescriptor=MTLTextureDescriptor.texture2DDescriptor(pixelFormat:.rgba8Unorm,width:1,height:1,mipmapped:false)
        tinyDescriptor.usage = .shaderRead
        let tiny=device.makeTexture(descriptor:tinyDescriptor)!
        [UInt8(128),128,255,255].withUnsafeBytes { tiny.replace(region:MTLRegionMake2D(0,0,1,1),mipmapLevel:0,withBytes:$0.baseAddress!,bytesPerRow:4) }
        var orderedNodes:[(SCNNode,Int)]=[]
        scene.rootNode.enumerateChildNodes { node,_ in
            if node.name?.hasPrefix("Town cell ")==true || node.name?.hasPrefix("Exploration detail ")==true { orderedNodes.append((node,node.renderingOrder)) }
        }
        let ao=world.camera.camera!.screenSpaceAmbientOcclusionIntensity
        var rows:[[String:Any]]=[]
        renderer.update(atTime:0)
        let requested=ProcessInfo.processInfo.environment["MARVIN_GPU_VARIANTS"] ?? "no-trails,no-sky,no-ssao,no-shadows,no-shadow-casters,no-base-shadow,front-to-back,no-town,constant-ground-pigment,original-base,shadow-culling,no-vertex-pow,no-crowd,no-crowd-motion"
        let variants=requested.split(separator:",").map(String.init).flatMap { ["production",$0,$0,"production"] }
        let shaderMaterials=noiseMaterials.merging(tintMaterials) { original,_ in original }
            .merging(crowdMaterials) { original,_ in original }
        // ABBA at the same retained pose/history brackets each isolation with
        // production blocks. Keep all samples, including drift and outliers.
        for (block,variant) in variants.enumerated() {
            for (property,contents) in textureProperties.values { property.contents=variant=="tiny-surface-textures" ? tiny:contents }
            // A material can belong to more than one group. Restore it once,
            // then apply the requested transform without a later group undoing it.
            for (material,modifiers) in shaderMaterials.values {
                var value=modifiers
                if variant=="constant-ground-pigment" { value=value.mapValues { $0.replacingOccurrences(of:TownGround.pigmentFunctions,with:"float townNoise(float2 p) { return 0.5; } float3 townPigment(float2 p) { return float3(0.4); }") } }
                if variant=="no-vertex-pow" { value=value.mapValues { $0.replacingOccurrences(of:"pow(max(_geometry.color.rgb,float3(0.0)),float3(2.2))",with:"max(_geometry.color.rgb,float3(0.0))").replacingOccurrences(of:"pow(max(_geometry.color.rgb,float3(0)),float3(2.2))",with:"max(_geometry.color.rgb,float3(0))") } }
                if variant=="no-crowd-motion",let shader=value[.geometry],let start=shader.range(of:"float2 originXZ = _geometry.texcoords[1]") { value[.geometry]=String(shader[..<start.lowerBound]) }
                material.shaderModifiers=value
            }
            for (node,hidden) in crowdNodes { node.isHidden=hidden || variant=="no-crowd" }
            for (node,order) in orderedNodes {
                let p=node.position,camera=world.camera.position
                node.renderingOrder=variant=="front-to-back" && node.name?.hasPrefix("Town cell ")==true ? -9000+Int(hypot(Double(p.x-camera.x),Double(p.z-camera.z))):order
            }
            for node in shadowNodes { node.castsShadow=true }
            dirtWorld.town.root.isHidden=variant=="no-town"
            dirtWorld.setBenchmarkTrailsHidden(variant=="no-trails")
            // Hide only the sky sphere, retaining both directional lights.
            for node in sky.childNodes where node.geometry is SCNSphere { node.isHidden=variant=="no-sky" }
            world.camera.camera!.screenSpaceAmbientOcclusionIntensity=variant=="no-ssao" ? 0:ao
            terrain.geometry=variant=="original-base" ? oldGround:currentGround
            dirtWorld.town.shadowCullingEnabled=variant=="shadow-culling"
            dirtWorld.town.update(dt:0,camera:world.camera.position,player:SIMD2(simulation.x,simulation.z),shadowCamera:world.camera,viewportAspect:1920.0/1080)
            for light in shadowLights { light.castsShadow=variant != "no-shadows" }
            if variant=="no-shadow-casters" { for node in shadowNodes { node.castsShadow=false } }
            if variant=="no-base-shadow" { terrain.castsShadow=false }
            renderer.update(atTime:0)
            _=renderer.prepare(scene,shouldAbortBlock:nil)
            var samples:[[Double]]=[]
            for frame in 0..<90 {
                try autoreleasepool {
                    let command=queue.makeCommandBuffer()!
                    command.label="Loaded town GPU diagnostic: \(variant)"
                    let begin=ProcessInfo.processInfo.systemUptime
                    renderer.render(withViewport:viewport,commandBuffer:command,passDescriptor:pass)
                    let encoded=ProcessInfo.processInfo.systemUptime
                    command.commit();command.waitUntilCompleted()
                    if let error=command.error { throw error }
                    guard command.gpuEndTime>command.gpuStartTime,command.gpuStartTime>0 else {
                        throw NSError(domain:"LoadedGPUProbe",code:1,userInfo:[NSLocalizedDescriptionKey:"GPU timestamps unavailable"])
                    }
                    if frame>=30 { samples.append([(command.gpuEndTime-command.gpuStartTime)*1000,(encoded-begin)*1000]) }
                }
            }
            if block==0,let image=CIImage(mtlTexture:resolved,options:[.colorSpace:CGColorSpace(name:CGColorSpace.linearSRGB)!]),let cg=CIContext(mtlDevice:device).createCGImage(image.transformed(by:CGAffineTransform(translationX:0,y:1080).scaledBy(x:1,y:-1)),from:viewport) {
                try NSBitmapImageRep(cgImage:cg).representation(using:.png,properties:[:])!.write(to:directory.appendingPathComponent("gpu-probe-resolved.png"))
            }
            rows.append(["block":block,"variant":variant,"samples":samples,"columns":["gpuCommandBufferMS","cpuEncodingMS"],"thermalState":ProcessInfo.processInfo.thermalState.rawValue])
        }
        let report:[String:Any]=["gpuDevice":device.name,"resolution":[1920,1080],"msaaSamples":2,"colorPixelFormat":colorFormat.rawValue,"depthPixelFormat":depthFormat.rawValue,"stencilPixelFormat":stencilFormat.rawValue,"reverseZ":reverseZ,"method":"Frozen loaded scene, SCNRenderer owned command buffer, fully encoded before commit, 30 warmup + 60 measured frames per variant. Diagnostic GPU envelope; not display presentation or sustained acceptance.","variants":rows]
        try JSONSerialization.data(withJSONObject:report,options:[.sortedKeys,.prettyPrinted]).write(to:directory.appendingPathComponent("loaded-gpu-probe.json"))
    }
}
