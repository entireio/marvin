import AppKit
import Metal
import CoreImage
import CryptoKit
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
        // Keep the live owner from overriding the frozen ABBA caster decisions.
        let liveShadowBatchEnabled=dirtWorld.town.shadowBatchDiagnostics["enabled"]==1
        dirtWorld.town.setShadowBatchEnabled(false)
        defer { dirtWorld.town.setShadowBatchEnabled(liveShadowBatchEnabled) }
        let colorFormat=view.colorPixelFormat,depthFormat=view.depthPixelFormat,stencilFormat=view.stencilPixelFormat
        let reverseZ=view.usesReverseZ
        let scene=dirtWorld.scene
        view.rendersContinuously=false;view.isPlaying=false;view.scene=nil
        var renderer=SCNRenderer(device:device,options:nil)
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
        var dustMaterials:[ObjectIdentifier:(SCNMaterial,[SCNShaderModifierEntryPoint:String])]=[:]
        var dustNodes:[(SCNNode,Bool)]=[]
        scene.rootNode.enumerateChildNodes { node,_ in
            var isDust=false
            for material in node.geometry?.materials ?? [] {
                if material.lightingModel == .lambert,!material.writesToDepthBuffer,
                   let modifiers=material.shaderModifiers,modifiers[.geometry]?.contains("dustTint")==true {
                    dustMaterials[ObjectIdentifier(material)]=(material,modifiers)
                    isDust=true
                }
            }
            if isDust { dustNodes.append((node,node.isHidden)) }
        }
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
        // Compare tangent reuse against the actual production mesh, whose
        // exact-gradient sharing already removes some duplicate vertices.
        let productionMeshes=Dictionary(uniqueKeysWithValues:TownMesh.tangentProbePairs.map { (ObjectIdentifier($0.1),$0.0) })
        for (production,candidate) in TownMesh.tangentProbePairs {
            production.levelsOfDetail=candidate.levelsOfDetail?.map { level in
                let geometry=level.geometry.flatMap { productionMeshes[ObjectIdentifier($0)] } ?? level.geometry
                return level.screenSpaceRadius>0 ? SCNLevelOfDetail(geometry:geometry,screenSpaceRadius:level.screenSpaceRadius):SCNLevelOfDetail(geometry:geometry,worldSpaceDistance:level.worldSpaceDistance)
            }
        }
        var tangentNodes:[(SCNNode,SCNGeometry,SCNGeometry)]=[]
        scene.rootNode.enumerateChildNodes { node,_ in
            if let candidate=node.geometry,let production=productionMeshes[ObjectIdentifier(candidate)] { tangentNodes.append((node,production,candidate)) }
        }
        let shadowBatch=try TownShadowBatch(root:scene.rootNode,camera:world.camera.camera!)
        defer { shadowBatch.setEnabled(false) }
        let marvin=scene.rootNode.childNode(withName:"Marvin CAD assembly",recursively:true)!
        var reorderedCAD:[(SCNNode,SCNGeometry,SCNGeometry,SCNGeometry)]=[]
        var cadVerification:[[String:Any]]=[]
        if let folder=ProcessInfo.processInfo.environment["MARVIN_CAD_INDEX_DIRECTORY"] {
            func sha(_ data:Data)->String { SHA256.hash(data:data).map { String(format:"%02x",$0) }.joined() }
            let directory=URL(fileURLWithPath:folder)
            let verificationData=try Data(contentsOf:directory.appendingPathComponent("verification.json"))
            guard let verification=try JSONSerialization.jsonObject(with:verificationData) as? [String:Any],
                  let entries=verification["parts"] as? [[String:Any]],!entries.isEmpty,
                  let resources=Bundle.main.resourceURL,
                  sha(try Data(contentsOf:resources.appendingPathComponent("Marvin/geometry.bin")))==verification["sourceSHA256"] as? String else {
                throw NSError(domain:"CADIndexProbe",code:1,userInfo:[NSLocalizedDescriptionKey:"CAD source does not match verified inputs"])
            }
            var candidates:[SCNNode]=[]
            marvin.enumerateChildNodes { node,_ in if node.name != nil && node.geometry != nil { candidates.append(node) } }
            for node in candidates {
                guard !node.isHidden,let source=node.geometry,source.shaderModifiers?[.surface] != nil,source.elements.count==1,let element=source.elements.first,
                      element.primitiveType == .triangles,element.bytesPerIndex==4,
                      let entry=entries.first(where:{$0["part"] as? String==node.name}) else { continue }
                let data=try Data(contentsOf:directory.appendingPathComponent(node.name!+".bin"))
                let original=element.data
                guard sha(original)==entry["sourceIndexSHA256"] as? String,
                      sha(data)==entry["candidateSHA256"] as? String,data.count>0,data.count%12==0,
                      data.count/12==entry["candidateTriangles"] as? Int else {
                    throw NSError(domain:"CADIndexProbe",code:2,userInfo:[NSLocalizedDescriptionKey:"CAD index bytes do not match verification for \(node.name!)"])
                }
                func geometry(_ elements:[SCNGeometryElement])->SCNGeometry {
                    let result=SCNGeometry(sources:source.sources,elements:elements)
                    result.materials=source.materials;result.shaderModifiers=source.shaderModifiers
                    result.levelsOfDetail=source.levelsOfDetail;result.name=source.name
                    for key in ["dirtToBody","dirtHeight","dirtWheelX","duneContact","dirtRolling"] {
                        result.setValue(source.value(forKey:key),forKey:key)
                    }
                    return result
                }
                let replacement=SCNGeometryElement(data:data,primitiveType:.triangles,primitiveCount:data.count/12,bytesPerIndex:4)
                reorderedCAD.append((node,source,geometry(source.elements),geometry([replacement])))
                cadVerification.append(["part":node.name!,"originalTriangles":element.primitiveCount,
                    "candidateTriangles":data.count/12,"sourceIndexSHA256":sha(original),"candidateSHA256":sha(data)])
            }
            guard reorderedCAD.count==entries.count else { throw NSError(domain:"CADIndexProbe",code:3,userInfo:[NSLocalizedDescriptionKey:"Not every verified CAD part matched the runtime scene"]) }
        }
        defer { for (node,source,_,_) in reorderedCAD { node.geometry=source } }
        let marvinHidden=marvin.isHidden
        let internalNames:Set<String>=["Motor_Left","Motor_Right","Servo_Head","Servo_Tilt","Battery","Bearings","Axis_Mount"]
        var internalNodes:[(SCNNode,Bool)]=[]
        marvin.enumerateChildNodes { node,_ in
            if let name=node.name,internalNames.contains(name) { internalNodes.append((node,node.isHidden)) }
        }
        var marvinMaterials:[ObjectIdentifier:(SCNMaterial,Bool)]=[:]
        marvin.enumerateChildNodes { node,_ in
            for material in node.geometry?.materials ?? [] {
                marvinMaterials[ObjectIdentifier(material)]=(material,material.isDoubleSided)
            }
        }
        defer {
            marvin.isHidden=marvinHidden
            for (node,hidden) in internalNodes { node.isHidden=hidden }
            for (material,doubleSided) in marvinMaterials.values { material.isDoubleSided=doubleSided }
        }
        // Default to the live game's path; explicit 0 retains the historical
        // reference for comparisons with archived probes.
        let productionShadowBatch=ProcessInfo.processInfo.environment["MARVIN_GPU_PRODUCTION_SHADOW_BATCH"].map { $0=="1" } ?? liveShadowBatchEnabled
        var rows:[[String:Any]]=[]
        renderer.update(atTime:0)
        let requested=ProcessInfo.processInfo.environment["MARVIN_GPU_VARIANTS"] ?? "no-trails,no-sky,no-ssao,no-shadows,no-shadow-casters,no-base-shadow,front-to-back,no-town,constant-ground-pigment,original-base,shadow-culling,no-vertex-pow,no-crowd,no-crowd-motion"
        let neutralDescriptor=MTLTextureDescriptor.texture2DDescriptor(pixelFormat:.r32Float,width:1,height:1,mipmapped:false)
        neutralDescriptor.usage = .shaderRead
        let neutralAO=device.makeTexture(descriptor:neutralDescriptor)!
        [Float(1)].withUnsafeBytes { neutralAO.replace(region:MTLRegionMake2D(0,0,1,1),mipmapLevel:0,withBytes:$0.baseAddress!,bytesPerRow:4) }
        let captureBlocks=Set((ProcessInfo.processInfo.environment["MARVIN_GPU_CAPTURE_BLOCKS"] ?? "").split(separator:",").compactMap { Int($0) })
        let freshRenderer=ProcessInfo.processInfo.environment["MARVIN_GPU_FRESH_RENDERER"]=="1"
        let trackBatch=requested.contains("track-batch") ? try TrackBatchProbe(root:robot.root,tracks:robot.tracks):nil
        defer { try? trackBatch?.setMode(.original) }
        let variants=requested.split(separator:",").map(String.init).flatMap { ["production",$0,$0,"production"] }
        let shaderMaterials=noiseMaterials.merging(tintMaterials) { original,_ in original }
            .merging(crowdMaterials) { original,_ in original }
            .merging(dustMaterials) { original,_ in original }
        // ABBA at the same retained pose/history brackets each isolation with
        // production blocks. Keep all samples, including drift and outliers.
        for (block,variant) in variants.enumerated() {
            if freshRenderer {
                renderer=SCNRenderer(device:device,options:nil)
                renderer.scene=scene;renderer.pointOfView=world.camera;renderer.usesReverseZ=reverseZ
            }
            for (node,source,control,candidate) in reorderedCAD { node.geometry=["cad-index-order","cad-deduplicate","cad-enclosure"].contains(variant) ? candidate:variant=="cad-index-control" ? control:source }
            marvin.isHidden=marvinHidden || variant=="no-marvin"
            for (node,hidden) in internalNodes { node.isHidden=hidden || variant=="marvin-no-internals" }
            for (material,doubleSided) in marvinMaterials.values {
                material.isDoubleSided=variant=="marvin-single-sided" ? false:doubleSided
            }
            try trackBatch?.setMode(variant=="track-batch" ? .batch:variant=="track-batch-control" ? .copyControl:.original)
            var opacityMaterialCount=0
            shadowBatch.setEnabled(false)
            for (node,production,candidate) in tangentNodes { node.geometry=variant=="tangent-reuse" ? candidate:production }
            for (property,contents) in textureProperties.values { property.contents=variant=="tiny-surface-textures" ? tiny:contents }
            // A material can belong to more than one group. Restore it once,
            // then apply the requested transform without a later group undoing it.
            for (material,modifiers) in shaderMaterials.values {
                var value=modifiers
                if variant=="ground-surface-opacity",let fragment=value[.fragment],
                   fragment.contains("_output.color.a=blend;"),let surface=value[.surface] {
                    let opacity=fragment.replacingOccurrences(of:"#pragma transparent",with:"")
                        .replacingOccurrences(of:"#pragma body",with:"")
                        .replacingOccurrences(of:"_output.color.rgb *= blend;",with:"")
                        .replacingOccurrences(of:"_output.color.a=blend;",with:"_surface.diffuse.a=blend;")
                    value[.surface]=surface.replacingOccurrences(of:"#pragma body",with:"#pragma transparent\n#pragma body")+"\n"+opacity
                    value.removeValue(forKey:.fragment)
                    opacityMaterialCount += 1
                }
                if variant=="dust-zero-alpha",dustMaterials[ObjectIdentifier(material)] != nil,let surface=value[.surface] {
                    value[.surface]=surface.replacingOccurrences(of:"#pragma body",with:"#pragma body\nif (_surface.diffuse.a * in.dustTint.a == 0.0) { discard_fragment(); }")
                }
                if variant=="dust-vertex-tint",dustMaterials[ObjectIdentifier(material)] != nil {
                    value[.geometry]=value[.geometry]?.replacingOccurrences(of:"out.dustTint=_geometry.color;",with:"out.dustTint=float4(pow(max(_geometry.color.rgb,float3(0.0)),float3(2.2)),_geometry.color.a);")
                    value[.surface]=value[.surface]?.replacingOccurrences(of:"pow(max(in.dustTint.rgb,float3(0.0)),float3(2.2))",with:"in.dustTint.rgb")
                }
                if variant=="constant-ground-pigment" { value=value.mapValues { $0.replacingOccurrences(of:TownGround.pigmentFunctions,with:"float townNoise(float2 p) { return 0.5; } float3 townPigment(float2 p) { return float3(0.4); }") } }
                if variant=="no-vertex-pow" { value=value.mapValues { $0.replacingOccurrences(of:"pow(max(_geometry.color.rgb,float3(0.0)),float3(2.2))",with:"max(_geometry.color.rgb,float3(0.0))").replacingOccurrences(of:"pow(max(_geometry.color.rgb,float3(0)),float3(2.2))",with:"max(_geometry.color.rgb,float3(0))") } }
                if variant=="no-crowd-motion",let shader=value[.geometry],let start=shader.range(of:"float2 originXZ = _geometry.texcoords[1]") { value[.geometry]=String(shader[..<start.lowerBound]) }
                material.shaderModifiers=value
            }
            for (node,hidden) in crowdNodes { node.isHidden=hidden || variant=="no-crowd" }
            // Isolation only: estimate the ceiling before changing dust shaders.
            for (node,hidden) in dustNodes { node.isHidden=hidden || variant=="no-dust" }
            for (node,order) in orderedNodes {
                let p=node.position,camera=world.camera.position
                node.renderingOrder=variant=="front-to-back" && node.name?.hasPrefix("Town cell ")==true ? -9000+Int(hypot(Double(p.x-camera.x),Double(p.z-camera.z))):order
            }
            for node in shadowNodes { node.castsShadow=true }
            dirtWorld.town.root.isHidden=variant=="no-town"
            dirtWorld.setBenchmarkTrailsHidden(variant=="no-trails")
            // Hide only the sky sphere, retaining both directional lights.
            for node in sky.childNodes where node.geometry is SCNSphere { node.isHidden=variant=="no-sky" }
            world.camera.camera!.screenSpaceAmbientOcclusionIntensity=(variant=="no-ssao" || variant=="neutral-custom-ao" || variant=="literal-neutral-ao") ? 0:ao
            terrain.geometry=variant=="original-base" ? oldGround:currentGround
            dirtWorld.town.shadowCullingEnabled=variant=="shadow-culling" || variant=="shadow-batch-culling"
            dirtWorld.town.update(dt:0,camera:world.camera.position,player:SIMD2(simulation.x,simulation.z),shadowCamera:world.camera,viewportAspect:1920.0/1080)
            for light in shadowLights { light.castsShadow=variant != "no-shadows" }
            if variant=="no-shadow-casters" { for node in shadowNodes { node.castsShadow=false } }
            if variant=="no-base-shadow" { terrain.castsShadow=false }
            shadowBatch.setEnabled(productionShadowBatch || variant=="shadow-batch" || variant=="shadow-batch-culling")
            let aoBinding=(variant=="neutral-custom-ao" || variant=="literal-neutral-ao") ? try AOMaterialBinding(scene:scene,texture:neutralAO,literalNeutral:variant=="literal-neutral-ao"):nil
            let boundAOMaterialCount=aoBinding?.materialCount ?? 0
            defer {
                if ProcessInfo.processInfo.environment["MARVIN_GPU_RESTORE_AO_FIRST"]=="1" {
                    world.camera.camera!.screenSpaceAmbientOcclusionIntensity=ao
                    renderer.update(atTime:0)
                }
                aoBinding?.restore()
            }
            renderer.update(atTime:0)
            _=renderer.prepare(scene,shouldAbortBlock:nil)
            var samples:[[Double]]=[]
            for frame in 0..<90 {
                try autoreleasepool {
                    let capture=MTLCaptureManager.shared()
                    let capturing=captureBlocks.contains(block) && frame==45
                    if capturing {
                        guard capture.supportsDestination(.gpuTraceDocument) else {
                            throw NSError(domain:"LoadedGPUProbe",code:2,userInfo:[NSLocalizedDescriptionKey:"Set MTL_CAPTURE_ENABLED=1 to capture selected diagnostic blocks"])
                        }
                        let descriptor=MTLCaptureDescriptor()
                        descriptor.captureObject=queue;descriptor.destination = .gpuTraceDocument
                        descriptor.outputURL=directory.appendingPathComponent("gpu-probe-block-\(block).gputrace")
                        try capture.startCapture(with:descriptor)
                    }
                    defer { if capturing { capture.stopCapture() } }
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
            if let image=CIImage(mtlTexture:resolved,options:[.colorSpace:CGColorSpace(name:CGColorSpace.linearSRGB)!]),let cg=CIContext(mtlDevice:device).createCGImage(image.transformed(by:CGAffineTransform(translationX:0,y:1080).scaledBy(x:1,y:-1)),from:viewport) {
                let png=NSBitmapImageRep(cgImage:cg).representation(using:.png,properties:[:])!
                try png.write(to:directory.appendingPathComponent("gpu-probe-block-\(block).png"))
                if block==0 { try png.write(to:directory.appendingPathComponent("gpu-probe-resolved.png")) }
            }
            rows.append(["block":block,"variant":variant,"samples":samples,"columns":["gpuCommandBufferMS","cpuEncodingMS"],"thermalState":ProcessInfo.processInfo.thermalState.rawValue,"opacityMaterialCount":opacityMaterialCount,"boundAOMaterialCount":boundAOMaterialCount,"boundAOGeometryCount":aoBinding?.geometryCount ?? 0,"captureRequested":captureBlocks.contains(block)])
        }
        let report:[String:Any]=["gpuDevice":device.name,"resolution":[1920,1080],"msaaSamples":2,"colorPixelFormat":colorFormat.rawValue,"depthPixelFormat":depthFormat.rawValue,"stencilPixelFormat":stencilFormat.rawValue,"reverseZ":reverseZ,"method":"Frozen loaded scene, SCNRenderer owned command buffer, fully encoded before commit, 30 warmup + 60 measured frames per variant. Diagnostic GPU envelope; not display presentation or sustained acceptance.","captureBlocks":captureBlocks.sorted(),"timingEligible":captureBlocks.isEmpty,"trackBatch":trackBatch?.statistics ?? [:],"reorderedCADNodes":reorderedCAD.count,"cadVerification":cadVerification,"productionShadowBatch":productionShadowBatch,"freshRendererPerBlock":freshRenderer,"restoreAOFirst":ProcessInfo.processInfo.environment["MARVIN_GPU_RESTORE_AO_FIRST"]=="1","variants":rows,"shadowBatch":shadowBatch.statistics]
        try JSONSerialization.data(withJSONObject:report,options:[.sortedKeys,.prettyPrinted]).write(to:directory.appendingPathComponent("loaded-gpu-probe.json"))
        if CommandLine.arguments.contains("--benchmark-ao-preparation") {
            let probe=try AOPreparationProbe(device:device,source:scene,camera:world.camera,reverseZ:reverseZ)
            try probe.measure(at:directory.appendingPathComponent("ao-preparation"))
        }
    }
}
