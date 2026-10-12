import AppKit
import CoreImage
import Metal
import SceneKit

/// Frozen diagnostic only. This is not an AO implementation or a gameplay
/// renderer: it measures and exposes a possible normal/coverage + depth input.
/// Persistent copies keep original geometry buffers, LODs and shader inputs.
final class AOPreparationProbe {
    private let renderer:SCNRenderer
    private let scene=SCNScene()
    private let queue:MTLCommandQueue
    private let color:MTLTexture,depth:MTLTexture
    private let pass=MTLRenderPassDescriptor()
    private var geometries:[ObjectIdentifier:SCNGeometry]=[:]
    private var materials:[ObjectIdentifier:SCNMaterial]=[:]
    private(set) var geometryCount=0,materialCount=0,nodeCount=0
    private let size:MTLSize
    private let lightingModel:SCNMaterial.LightingModel?
    private let surfacePolicy:Bool
    private(set) var excludedEffectMaterials=0

    init(device:MTLDevice,source:SCNScene,camera:SCNNode,reverseZ:Bool,width:Int=1920,height:Int=1080,lightingModel:SCNMaterial.LightingModel? = nil,surfacePolicy:Bool=true) throws {
        self.surfacePolicy=surfacePolicy
        self.lightingModel=lightingModel
        size=MTLSize(width:width,height:height,depth:1)
        guard let queue=device.makeCommandQueue() else { throw CocoaError(.coderInvalidValue) }
        self.queue=queue
        let descriptor=MTLTextureDescriptor.texture2DDescriptor(pixelFormat:.rgba16Float,width:width,height:height,mipmapped:false)
        descriptor.storageMode = .private;descriptor.usage=[.renderTarget,.shaderRead]
        guard let color=device.makeTexture(descriptor:descriptor) else { throw CocoaError(.coderInvalidValue) }
        self.color=color;color.label="AO diagnostic mapped normals and coverage"
        descriptor.pixelFormat = .depth32Float
        guard let depth=device.makeTexture(descriptor:descriptor) else { throw CocoaError(.coderInvalidValue) }
        self.depth=depth;depth.label="AO diagnostic geometric depth"
        renderer=SCNRenderer(device:device,options:nil);renderer.usesReverseZ=reverseZ
        pass.colorAttachments[0].texture=color;pass.colorAttachments[0].loadAction = .clear
        pass.colorAttachments[0].clearColor=MTLClearColorMake(0,0,0,0);pass.colorAttachments[0].storeAction = .store
        pass.depthAttachment.texture=depth;pass.depthAttachment.loadAction = .clear
        pass.depthAttachment.clearDepth=reverseZ ? 0:1;pass.depthAttachment.storeAction = .store
        let eye=SCNNode();eye.camera=camera.camera?.copy() as? SCNCamera
        eye.simdTransform=camera.presentation.simdWorldTransform
        eye.camera?.wantsHDR=false;eye.camera?.screenSpaceAmbientOcclusionIntensity=0
        eye.camera?.motionBlurIntensity=0;eye.camera?.bloomIntensity=0
        eye.camera?.vignettingIntensity=0;eye.camera?.colorFringeIntensity=0
        scene.rootNode.addChildNode(eye);renderer.pointOfView=eye
        let root=source.rootNode.clone();scene.rootNode.addChildNode(root)
        try copyState(source.rootNode,to:root)
        renderer.scene=scene;renderer.update(atTime:0)
        guard renderer.prepare(scene,shouldAbortBlock:nil) else { throw CocoaError(.coderInvalidValue) }
    }

    private func copyState(_ original:SCNNode,to proxy:SCNNode) throws {
        nodeCount += 1
        proxy.removeAllAnimations();proxy.removeAllActions();proxy.constraints=nil
        proxy.simdTransform=original.presentation.simdTransform
        proxy.light=nil;proxy.camera=nil;proxy.castsShadow=false
        // Sky is the clear value, not a nearby AO occluder. Other transparency
        // is intentionally retained for inspection, not declared correct here.
        if original.parent?.name=="Binary daylight",original.geometry is SCNSphere { proxy.isHidden=true }
        if let geometry=original.geometry { proxy.geometry=try copyGeometry(geometry) }
        guard original.childNodes.count==proxy.childNodes.count else { throw CocoaError(.coderInvalidValue) }
        for (a,b) in zip(original.childNodes,proxy.childNodes) { try copyState(a,to:b) }
    }

    private func copyGeometry(_ source:SCNGeometry) throws ->SCNGeometry {
        let key=ObjectIdentifier(source)
        if let existing=geometries[key] { return existing }
        guard let result=source.copy() as? SCNGeometry else { throw CocoaError(.coderInvalidValue) }
        geometries[key]=result;geometryCount += 1
        result.materials=try source.materials.map(copyMaterial)
        result.levelsOfDetail=try source.levelsOfDetail?.map { level in
            guard let original=level.geometry else { throw CocoaError(.coderInvalidValue) }
            let geometry=try copyGeometry(original)
            return level.screenSpaceRadius>0 ? SCNLevelOfDetail(geometry:geometry,screenSpaceRadius:level.screenSpaceRadius):SCNLevelOfDetail(geometry:geometry,worldSpaceDistance:level.worldSpaceDistance)
        }
        return result
    }

    private func copyMaterial(_ source:SCNMaterial) throws ->SCNMaterial {
        let key=ObjectIdentifier(source)
        if let existing=materials[key] { return existing }
        guard source.program==nil,let result=source.copy() as? SCNMaterial else { throw CocoaError(.coderInvalidValue) }
        materials[key]=result;materialCount += 1
        // Color-only impressions and airborne particles do not replace a solid
        // surface's normal/depth. Retain them unchanged in the main renderer.
        // Physical clods use the same tint modifier but DO write depth.
        if surfacePolicy && !source.writesToDepthBuffer &&
           (source.blendMode == .multiply || source.shaderModifiers?[.geometry]?.contains("dustTint")==true) {
            result.colorBufferWriteMask=[]
            excludedEffectMaterials += 1
        }
        if let lightingModel { result.lightingModel = lightingModel }
        var modifiers=source.shaderModifiers ?? [:]
        // Keep original alpha/discard behavior for this first input inspection.
        // The appended output makes unused RGB lighting/pigment work removable.
        let original=modifiers[.fragment] ?? "#pragma body\n"
        modifiers[.fragment]=original+"\n"+"""
        float aoCoverage=clamp(_output.color.a,0.0,1.0);
        float3 aoNormal=normalize(_surface.normal);
        _output.color=float4((aoNormal*0.5+0.5)*aoCoverage,aoCoverage);
        """
        result.shaderModifiers=modifiers
        return result
    }

    private func render()->(Double,Double) {
        let command=queue.makeCommandBuffer()!;command.label="AO preparation diagnostic"
        let begin=ProcessInfo.processInfo.systemUptime
        renderer.render(withViewport:CGRect(x:0,y:0,width:size.width,height:size.height),commandBuffer:command,passDescriptor:pass)
        let encoded=ProcessInfo.processInfo.systemUptime
        command.commit();command.waitUntilCompleted()
        precondition(command.status == .completed && command.gpuStartTime>0 && command.gpuEndTime>command.gpuStartTime)
        return ((command.gpuEndTime-command.gpuStartTime)*1000,(encoded-begin)*1000)
    }

    func measure(at directory:URL) throws {
        try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
        var samples:[[Double]]=[]
        for frame in 0..<120 {
            let timing=autoreleasepool { render() }
            if frame>=30 { samples.append([timing.0,timing.1]) }
        }
        let device=color.device
        let normalBytes=size.width*size.height*8,depthBytes=size.width*size.height*4
        let normals=device.makeBuffer(length:normalBytes,options:.storageModeShared)!
        let depths=device.makeBuffer(length:depthBytes,options:.storageModeShared)!
        let command=queue.makeCommandBuffer()!,blit=command.makeBlitCommandEncoder()!
        for (texture,buffer,bytes) in [(color,normals,8),(depth,depths,4)] {
            blit.copy(from:texture,sourceSlice:0,sourceLevel:0,sourceOrigin:MTLOrigin(x:0,y:0,z:0),sourceSize:size,to:buffer,destinationOffset:0,destinationBytesPerRow:size.width*bytes,destinationBytesPerImage:size.width*size.height*bytes)
        }
        blit.endEncoding();command.commit();command.waitUntilCompleted()
        if let error=command.error { throw error }
        try Data(bytes:normals.contents(),count:normalBytes).write(to:directory.appendingPathComponent("normals-coverage.rgba16f"))
        try Data(bytes:depths.contents(),count:depthBytes).write(to:directory.appendingPathComponent("depth.depth32f"))
        if let image=CIImage(mtlTexture:color,options:[.colorSpace:CGColorSpace(name:CGColorSpace.linearSRGB)!]),
           let cg=CIContext(mtlDevice:device).createCGImage(image.transformed(by:CGAffineTransform(translationX:0,y:CGFloat(size.height)).scaledBy(x:1,y:-1)),from:CGRect(x:0,y:0,width:size.width,height:size.height)) {
            try NSBitmapImageRep(cgImage:cg).representation(using:.png,properties:[:])!.write(to:directory.appendingPathComponent("normals-coverage.png"))
        }
        let projection=renderer.pointOfView!.camera!.projectionTransform
        let projectionElements=[projection.m11,projection.m12,projection.m13,projection.m14,
                                projection.m21,projection.m22,projection.m23,projection.m24,
                                projection.m31,projection.m32,projection.m33,projection.m34,
                                projection.m41,projection.m42,projection.m43,projection.m44]
        let report:[String:Any]=["accepted":false,"method":"Frozen proxy scene normal/coverage and depth preparation only; excludes AO compute and main render. Not a net performance or visual acceptance result.","resolution":[size.width,size.height],"msaaSamples":1,"lightingModel":lightingModel?.rawValue ?? "preserve-source","surfacePolicy":surfacePolicy,"excludedEffectMaterials":excludedEffectMaterials,"geometryCount":geometryCount,"materialCount":materialCount,"nodeCount":nodeCount,"samples":samples,"columns":["gpuCommandBufferMS","cpuEncodingMS"],"thermalState":ProcessInfo.processInfo.thermalState.rawValue,"projection":String(describing:projection),"projectionColumnMajor":projectionElements,"reverseZ":renderer.usesReverseZ]
        try JSONSerialization.data(withJSONObject:report,options:[.sortedKeys,.prettyPrinted]).write(to:directory.appendingPathComponent("preparation.json"))
    }
}
