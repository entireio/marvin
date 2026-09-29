import AppKit
import SceneKit
import RealityKit
import Metal
import QuartzCore
import CoreImage
import SimulationCore

/// Opt-in migration experiment. The shipped renderer and physics are unchanged.
/// Both backends use the same source meshes, fixed near LOD and camera path.
@available(macOS 15.0, *)
@MainActor final class RendererStudy {
    let app: AppController, directory: URL, reality: Bool
    var arView: ARView?
    let camera = PerspectiveCamera()
    var pairs: [(SCNNode, Entity)] = []
    var materials: [ObjectIdentifier: any RealityKit.Material] = [:]
    var textures: [String: TextureResource] = [:]
    var timer: Timer?, start = 0.0, frames = 0, meshCount = 0, triangles = 0
    var captured = false
    var captureStarted=false, captureFinished=false
    var captureResult="not requested"
    var screenshotSaved=false
    var library: MTLLibrary?
    init(app: AppController, directory: URL, reality: Bool) {
        self.app=app; self.directory=directory; self.reality=reality
    }
    func startStudy() async throws {
        try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
        app.timer?.invalidate(); app.startDirtTrack(); app.dirtIntro=nil; app.race.countDown(dt:3)
        app.simulation=Simulation(dirtTrack:true,dirtStartOffset:DirtCourse.startingGrid[0].offset,dirtStartPhase:DirtCourse.startingGrid[0].phase)
        app.opponent=DirtOpponent(slot:DirtCourse.startingGrid[1])
        app.bb8Opponent=DirtOpponent(slot:DirtCourse.startingGrid[2],laneOffset:0)
        app.wallEOpponent=DirtOpponent(slot:DirtCourse.startingGrid[3],laneOffset:-0.65)
        app.race=DirtRace(startPhase:DirtCourse.startingGrid[0].phase);app.race.countDown(dt:3)
        app.racePhysics=DirtRacePhysics(characters:app.lineup);app.updateOpponents()
        app.raceHUD.isHidden=true
        app.window.minSize=NSSize(width:640,height:360)
        app.window.setContentSize(NSSize(width:960,height:540))
        // The grandstand block: architecture, authored humans and mounted signs.
        for node in app.dirtWorld.town.root.childNodes {
            let p=node.simdPosition
            node.isHidden = !(abs(p.x)<=24 && p.z >= -40 && p.z <= -8)
        }
        app.dirtWorld.scene.rootNode.enumerateChildNodes { node,_ in
            node.geometry?.levelsOfDetail=[]
        }
        if reality {
            library=try makeShaderLibrary()
            let view=ARView(frame:app.view.bounds)
            view.autoresizingMask=[.width,.height]
            view.environment.background = .color(NSColor(calibratedRed:0.725,green:0.788,blue:0.812,alpha:1))
            guard let hdr=CIImage(contentsOf:CityMaterials.asset("sky.hdr")),
                  let image=CIContext().createCGImage(hdr,from:hdr.extent,format:.RGBAh,colorSpace:CGColorSpace(name:CGColorSpace.extendedLinearSRGB)!) else { throw CocoaError(.fileReadCorruptFile) }
            view.environment.lighting.resource = try await EnvironmentResource(equirectangular:image)
            view.environment.lighting.intensityExponent = log2(0.65)
            let anchor=AnchorEntity(world:.zero)
            for node in app.dirtWorld.scene.rootNode.childNodes where node.camera == nil && node.light == nil {
                if let entity=try convert(node) { anchor.addChild(entity) }
            }
            let sun=DirectionalLight();sun.light.intensity=1250
            sun.light.color=NSColor(calibratedRed:1,green:0.902,blue:0.757,alpha:1)
            sun.orientation=app.dirtWorld.scene.rootNode.childNodes.first{$0.light?.type == .directional}!.simdOrientation
            sun.shadow = .init(maximumDistance:80,depthBias:1)
            anchor.addChild(sun);anchor.addChild(camera)
            camera.camera.fieldOfViewInDegrees=Float(app.world.camera.camera!.fieldOfView)
            view.scene.addAnchor(anchor)
            arView=view;app.view.isPlaying=false;app.view.rendersContinuously=false
            app.view.scene=nil // Never render both backends during the comparison.
            // Keep the stopped SceneKit layer attached: macOS 26 HUD tracking
            // can dereference a detached layer during an asynchronous safe-area update.
            app.view.addSubview(view)
        } else {
            app.view.delegate=app.townMeter;app.townMeter.reset()
        }
        if !reality {
            func count(_ node:SCNNode) {
                guard !node.isHidden else { return }
                if let geometry=node.geometry {
                    let value=geometry.elements.filter{$0.primitiveType == .triangles || $0.primitiveType == .triangleStrip}.reduce(0){$0+$1.primitiveCount}
                    if value>0 { meshCount += 1;triangles += value }
                }
                for child in node.childNodes { count(child) }
            }
            count(app.dirtWorld.scene.rootNode)
        }
        start=ProcessInfo.processInfo.systemUptime
        print("STUDY_START \(reality ? "RealityKit":"SceneKit") \(start)")
        fflush(stdout)
        timer=Timer.scheduledTimer(withTimeInterval:1.0/60,repeats:true) { [weak self] _ in
            Task { @MainActor in self?.tick() }
        }
    }
    func tick() {
        let elapsed=ProcessInfo.processInfo.systemUptime-start
        // Fixed simulation steps produce the same replay independent of renderer.
        if CommandLine.arguments.contains("--gpu-capture") && elapsed>=2 && !captureStarted {
            captureStarted=true
            let manager=MTLCaptureManager.shared();let descriptor=MTLCaptureDescriptor()
            func device(in layer:CALayer?) -> MTLDevice? {
                guard let layer else { return nil }
                if let metal=layer as? CAMetalLayer { return metal.device }
                for child in layer.sublayers ?? [] { if let found=device(in:child) { return found } }
                return nil
            }
            descriptor.captureObject = reality ? device(in:arView?.layer):app.view.device
            descriptor.destination = .gpuTraceDocument
            descriptor.outputURL=directory.appendingPathComponent("frame.gputrace")
            do {
                guard descriptor.captureObject != nil else { throw NSError(domain:"RendererStudy",code:1,userInfo:[NSLocalizedDescriptionKey:"Renderer Metal device not exposed; use an Xcode-attached capture"] ) }
                try manager.startCapture(with:descriptor);captureResult="capturing"
            }
            catch { captureResult="unavailable: \(error)" }
        }
        if captureStarted && !captureFinished && elapsed>=2.15 {
            captureFinished=true
            if MTLCaptureManager.shared().isCapturing {
                MTLCaptureManager.shared().stopCapture()
                let url=directory.appendingPathComponent("frame.gputrace/capture")
                let bytes=(try? url.resourceValues(forKeys:[.fileSizeKey]).fileSize) ?? 0
                captureResult=bytes>8 ? "saved commands; requires Xcode inspection":"empty capture; no renderer commands recorded"
            }
        }
        let targetStep=Int(elapsed*60)
        while frames<targetStep {
            app.advanceRacePhysics(DirtOpponent.driveInput(for:app.simulation),dt:1.0/60,raceDT:1.0/60)
            app.updateOpponents()
            app.dirtWorld.town.update(dt:1.0/60,camera:app.world.camera.position,player:SIMD2(app.simulation.x,app.simulation.z))
            frames += 1
        }
        let eye=SIMD3<Float>(Float(sin(elapsed*0.15)*5),5,-10)
        let target=SIMD3<Float>(0,1.8,-22)
        app.world.camera.simdPosition=eye
        app.world.camera.look(at:SCNVector3(target),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
        camera.transform=Transform(matrix:app.world.camera.simdWorldTransform)
        for (source,destination) in pairs { destination.transform=Transform(matrix:source.simdTransform) }
        if elapsed>=5 && !captured {
            captured=true
            if let arView { arView.snapshot(saveToHDR:false) { image in self.save(image) } }
            else { save(app.view.snapshot()) }
        }
        let duration=Double(ProcessInfo.processInfo.environment["MARVIN_STUDY_SECONDS"] ?? "45") ?? 45
        if elapsed>=duration {
            timer?.invalidate()
            var report:[String:Any]=["renderer":reality ? "RealityKit":"SceneKit", "seconds":elapsed,
                "simulationSteps":frames,"meshes":meshCount,"triangles":triangles,"screenshotSaved":screenshotSaved,"gpuCapture":captureResult,
                "width":1920,"height":1080,"thermalState":ProcessInfo.processInfo.thermalState.rawValue,
                "scope":"Grandstand block, fixed near LOD, shared meshes and simulation; no dynamic trails or dust updates",
                "limitations":"Lighting, shadow filtering and tone mapping differ. City tint shader ported; other source-specific shaders are not. Simulation steps are not render FPS."]
            if !reality { report["renderCallbacks"]=app.townMeter.report() }
            do {
                try JSONSerialization.data(withJSONObject:report,options:[.prettyPrinted,.sortedKeys]).write(to:directory.appendingPathComponent("study.json"))
                print("STUDY_END");exit(0)
            } catch { print(error);exit(1) }
        }
    }
    func save(_ image:NSImage?) {
        guard let tiff=image?.tiffRepresentation,let png=NSBitmapImageRep(data:tiff)?.representation(using:.png,properties:[:]) else { return }
        do { try png.write(to:directory.appendingPathComponent("frame.png"));screenshotSaved=true }
        catch { print("Screenshot failed: \(error)") }
    }
    func values(_ geometry:SCNGeometry,_ semantic:SCNGeometrySource.Semantic,_ count:Int,_ defaults:[Float])->[[Float]] {
        guard let s=geometry.sources(for:semantic).first else { return Array(repeating:defaults,count:count) }
        return s.data.withUnsafeBytes { raw in
            (0..<count).map { i in (0..<s.componentsPerVector).map { c in
                let offset=s.dataOffset+i*s.dataStride+c*s.bytesPerComponent
                if s.bytesPerComponent==4 { return raw.loadUnaligned(fromByteOffset:offset,as:Float.self) }
                if s.bytesPerComponent==8 { return Float(raw.loadUnaligned(fromByteOffset:offset,as:Double.self)) }
                return Float(raw.load(fromByteOffset:offset,as:UInt8.self))/255
            }}
        }
    }
    func convert(_ node:SCNNode, dynamic:Bool=false) throws -> Entity? {
        guard !node.isHidden else { return nil }
        let entity=Entity();entity.name=node.name ?? "mesh";entity.transform=Transform(matrix:node.simdTransform)
        let moving=dynamic || node.name == "Animated town spectator" || app.lineup.contains{app.modelRoot($0) === node}
        if moving { pairs.append((node,entity)) }
        if let geometry=node.geometry, let positions=geometry.sources(for:.vertex).first,positions.vectorCount>0 {
            let count=positions.vectorCount
            let p=values(geometry,.vertex,count,[0,0,0]),n=values(geometry,.normal,count,[0,1,0])
            let uv=values(geometry,.texcoord,count,[0,0]),colors=values(geometry,.color,count,[1,1,1,1])
            var packed:[Float]=[];packed.reserveCapacity(count*12)
            var low=SIMD3<Float>(repeating:.infinity),high=SIMD3<Float>(repeating:-.infinity)
            for i in 0..<count {
                let point=SIMD3(p[i][0],p[i][1],p[i][2]);low=simd_min(low,point);high=simd_max(high,point)
                packed += Array(p[i].prefix(3))+Array(n[i].prefix(3))+[uv[i][0],1-uv[i][1]]
                packed += [colors[i][0],colors[i][1],colors[i][2],colors[i].count>3 ? colors[i][3]:1]
            }
            var indices:[UInt32]=[],parts:[LowLevelMesh.Part]=[]
            for (slot,element) in geometry.elements.enumerated() {
                let data=element.data
                let input:[UInt32]=data.withUnsafeBytes { bytes in
                    (0..<data.count/element.bytesPerIndex).map { i in
                        switch element.bytesPerIndex {
                        case 1:return UInt32(bytes.load(fromByteOffset:i,as:UInt8.self))
                        case 2:return UInt32(bytes.loadUnaligned(fromByteOffset:i*2,as:UInt16.self))
                        default:return bytes.loadUnaligned(fromByteOffset:i*4,as:UInt32.self)
                        }
                    }
                }
                let offset=indices.count
                if element.primitiveType == .triangles { indices += input }
                else if element.primitiveType == .triangleStrip && input.count>=3 {
                    for i in 0..<input.count-2 { indices += i%2==0 ? [input[i],input[i+1],input[i+2]]:[input[i+1],input[i],input[i+2]] }
                }
                if indices.count>offset { parts.append(.init(indexOffset:offset*4,indexCount:indices.count-offset,materialIndex:slot,bounds:.init(min:low,max:high))) }
            }
            if !indices.isEmpty {
                // The low-level mesh API needs an explicit tangent basis for normal maps.
                var tangents=Array(repeating:SIMD3<Float>.zero,count:count)
                var bitangents=tangents
                for i in stride(from:0,to:indices.count,by:3) {
                    let a=Int(indices[i]),b=Int(indices[i+1]),c=Int(indices[i+2])
                    let e1=SIMD3(p[b][0]-p[a][0],p[b][1]-p[a][1],p[b][2]-p[a][2])
                    let e2=SIMD3(p[c][0]-p[a][0],p[c][1]-p[a][1],p[c][2]-p[a][2])
                    let u1=SIMD2(uv[b][0]-uv[a][0],uv[a][1]-uv[b][1])
                    let u2=SIMD2(uv[c][0]-uv[a][0],uv[a][1]-uv[c][1])
                    let determinant=u1.x*u2.y-u1.y*u2.x
                    if abs(determinant)>1e-8 {
                        let t=(e1*u2.y-e2*u1.y)/determinant,bt=(e2*u1.x-e1*u2.x)/determinant
                        for j in [a,b,c] { tangents[j] += t;bitangents[j] += bt }
                    }
                }
                var vertices:[Float]=[];vertices.reserveCapacity(count*18)
                for i in 0..<count {
                    let normal=SIMD3(n[i][0],n[i][1],n[i][2])
                    var tangent=tangents[i]-normal*simd_dot(normal,tangents[i])
                    if simd_length_squared(tangent)<1e-10 { tangent=simd_cross(normal,abs(normal.y)<0.9 ? SIMD3(0,1,0):SIMD3(1,0,0)) }
                    tangent=simd_normalize(tangent)
                    var bitangent=simd_cross(normal,tangent)
                    if simd_dot(bitangent,bitangents[i])<0 { bitangent = -bitangent }
                    vertices += packed[i*12..<i*12+12]
                    vertices += [tangent.x,tangent.y,tangent.z,bitangent.x,bitangent.y,bitangent.z]
                }
                let mesh=try LowLevelMesh(descriptor:.init(vertexCapacity:count,vertexAttributes:[
                    .init(semantic:.position,format:.float3,offset:0),.init(semantic:.normal,format:.float3,offset:12),
                    .init(semantic:.uv0,format:.float2,offset:24),.init(semantic:.color,format:.float4,offset:32),
                    .init(semantic:.tangent,format:.float3,offset:48),.init(semantic:.bitangent,format:.float3,offset:60)],
                    vertexLayouts:[.init(bufferIndex:0,bufferStride:72)],indexCapacity:indices.count))
                vertices.withUnsafeBytes { src in mesh.withUnsafeMutableBytes(bufferIndex:0){$0.copyMemory(from:src)} }
                indices.withUnsafeBytes { src in mesh.withUnsafeMutableIndices{$0.copyMemory(from:src)} }
                mesh.parts.replaceAll(parts)
                let resource=try MeshResource(from:mesh)
                let mapped=try geometry.materials.map{try material($0)}
                entity.components.set(ModelComponent(mesh:resource,materials:mapped))
                entity.components.set(DynamicLightShadowComponent(castsShadow:node.castsShadow))
                meshCount += 1;triangles += indices.count/3
            }
        }
        for child in node.childNodes { if let value=try convert(child,dynamic:moving) { entity.addChild(value) } }
        return entity
    }
    func texture(_ contents:Any?,normal:Bool=false,raw:Bool=false) throws -> TextureResource? {
        let options=TextureResource.CreateOptions(semantic:normal ? .normal:(raw ? .raw:.color))
        if let url=contents as? URL {
            let key=url.path+(normal ? "normal":(raw ? "raw":"color"))
            if let found=textures[key] { return found }
            let value=try TextureResource.load(contentsOf:url,options:options);textures[key]=value;return value
        }
        if let image=contents as? NSImage,let cg=image.cgImage(forProposedRect:nil,context:nil,hints:nil) {
            // AppKit bitmap textures and RealityKit's shader sampling differ in Y origin.
            let context=CGContext(data:nil,width:cg.width,height:cg.height,bitsPerComponent:8,bytesPerRow:cg.width*4,space:CGColorSpace(name:CGColorSpace.sRGB)!,bitmapInfo:CGImageAlphaInfo.premultipliedLast.rawValue)!
            context.translateBy(x:0,y:CGFloat(cg.height));context.scaleBy(x:1,y:-1)
            context.draw(cg,in:CGRect(x:0,y:0,width:cg.width,height:cg.height))
            return try TextureResource.generate(from:context.makeImage()!,options:options)
        }
        return nil
    }

    func makeShaderLibrary() throws -> MTLLibrary {
        // Study-only runtime compilation using public SDK headers. Production
        // migration should ship a precompiled metallib from a full Xcode build.
        let process=Process();let pipe=Pipe();process.executableURL=URL(fileURLWithPath:"/usr/bin/xcrun")
        process.arguments=["--show-sdk-path"];process.standardOutput=pipe
        try process.run();process.waitUntilExit()
        let sdk=String(data:pipe.fileHandleForReading.readDataToEndOfFile(),encoding:.utf8)!.trimmingCharacters(in:.whitespacesAndNewlines)
        func header(_ name:String) throws -> String {
            let text=try String(contentsOfFile:sdk+"/System/Library/Frameworks/RealityKit.framework/Headers/"+name)
            return try text.components(separatedBy:"\n").map { line in
                if line.hasPrefix("#import <RealityKit/") || line.hasPrefix("#include <RealityKit/") {
                    let name=String(line.split(separator:"/").last!.dropLast())
                    return try header(name)
                }
                return line
            }.joined(separator:"\n")
        }
        let shader = try header("RealityKit.h") + """
        \nusing namespace metal;
        [[visible]] void studySurface(realitykit::surface_parameters p) {
            constexpr sampler s(coord::normalized,address::repeat,filter::linear,mip_filter::linear);
            float2 uv=p.geometry().uv0();
            float4 settings=p.uniforms().custom_parameter();
            half3 base=p.textures().base_color().sample(s,uv).rgb;
            if(settings.x>3.5) {
                half grain=dot(base,half3(0.2126,0.7152,0.0722));
                base=half3(0.34,0.205,0.145)+grain*half3(0.58,0.44,0.33);
            } else if(settings.x>0.5) {
                half3 tint=half3(pow(max(p.geometry().color().rgb,float3(0)),float3(2.2)));
                half grain=dot(base,half3(0.2126,0.7152,0.0722));
                base=tint;
                if(settings.x>2.5) base *= (0.82h+grain*0.22h);
                else if(settings.x>1.5) base *= (0.62h+grain*0.65h);
            } else { base *= half3(p.material_constants().base_color_tint()); }
            p.surface().set_base_color(base);
            p.surface().set_roughness(p.textures().roughness().sample(s,uv).r * half(p.material_constants().roughness_scale()));
            p.surface().set_metallic(p.textures().metallic().sample(s,uv).r * half(p.material_constants().metallic_scale()));
            if(settings.z>0.5) p.surface().set_normal(float3(realitykit::unpack_normal(p.textures().normal().sample(s,uv).rgb,half(settings.y))));
        }
        """
        return try MTLCreateSystemDefaultDevice()!.makeLibrary(source:shader,options:nil)
    }
    func material(_ source:SCNMaterial) throws -> any RealityKit.Material {
        let key=ObjectIdentifier(source)
        if let value=materials[key] { return value }
        var result=PhysicallyBasedMaterial()
        result.baseColor.tint=(source.diffuse.contents as? NSColor) ?? .white
        if let t=try texture(source.diffuse.contents) { result.baseColor.texture = .init(t) }
        if let t=try texture(source.normal.contents,normal:true) { result.normal.texture = .init(t) }
        result.roughness = .init(floatLiteral:Float((source.roughness.contents as? NSNumber)?.doubleValue ?? 0.85))
        if let t=try texture(source.roughness.contents,raw:true) { result.roughness.texture = .init(t) }
        result.metallic = .init(floatLiteral:Float((source.metalness.contents as? NSNumber)?.doubleValue ?? 0))
        result.faceCulling=source.isDoubleSided ? .none:.back
        let transform=source.diffuse.contentsTransform
        result.textureCoordinateTransform = .init(offset:SIMD2(Float(transform.m41),Float(transform.m42)),scale:SIMD2(Float(transform.m11),Float(transform.m22)))
        var custom=try CustomMaterial(from:result,surfaceShader:.init(named:"studySurface",in:library!))
        let city = source === CityMaterials.plaster || source === CityMaterials.cloth || source === CityMaterials.metal || source === CityMaterials.crowdCloth || source === CityMaterials.skin || source === CityMaterials.leather
        let scanned = source.diffuse.contents is URL && city
        let cloth = source === CityMaterials.cloth || source === CityMaterials.crowdCloth
        let clay=source.shaderModifiers?[.surface]?.contains("clayDetail") == true
        custom.custom.value=SIMD4(clay ? 4 : city ? (scanned ? (cloth ? 3:2):1):0,Float(source.normal.intensity),source.normal.contents == nil ? 0:1,0)
        materials[key]=custom;return custom
    }
}
