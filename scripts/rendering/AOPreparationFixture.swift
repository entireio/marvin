import AppKit
import Metal
import SceneKit

/// Compile together with the actual AOPreparationProbe.swift. Known tangent-space
/// maps expose whether changing the lighting model silently removes normal maps.
@main
struct AOPreparationFixture {
    static func present(_ scene:SCNScene,camera:SCNNode,device:MTLDevice) {
        // The probe intentionally snapshots PRESENTED transforms. Fresh fixture
        // nodes have not populated their presentation world transform yet.
        let source=SCNRenderer(device:device,options:nil)
        source.scene=scene;source.pointOfView=camera
        _=source.snapshot(atTime:0,with:CGSize(width:256,height:256),antialiasingMode:.none)
    }

    static func main() throws {
        let destination=URL(fileURLWithPath:CommandLine.arguments[1],isDirectory:true)
        let device=MTLCreateSystemDefaultDevice()!
        let scene=SCNScene(),eye=SCNNode()
        eye.camera=SCNCamera();eye.position=SCNVector3(0,0,5)
        eye.camera!.usesOrthographicProjection=true;eye.camera!.orthographicScale=2
        eye.camera!.zNear=0.1;eye.camera!.zFar=20
        scene.rootNode.addChildNode(eye)
        let maps:[[UInt8]]=[[128,128,255,255],[204,128,230,255],[128,204,230,255],[128,128,255,255]]
        for index in 0..<4 {
            let descriptor=MTLTextureDescriptor.texture2DDescriptor(pixelFormat:.rgba8Unorm,width:1,height:1,mipmapped:false)
            descriptor.usage = .shaderRead
            let texture=device.makeTexture(descriptor:descriptor)!
            maps[index].withUnsafeBytes { texture.replace(region:MTLRegionMake2D(0,0,1,1),mipmapLevel:0,withBytes:$0.baseAddress!,bytesPerRow:4) }
            let material=SCNMaterial();material.lightingModel = .physicallyBased
            material.diffuse.contents=NSColor.white;material.normal.contents=texture
            if index==3 {
                material.shaderModifiers=[.geometry:"#pragma body\n_geometry.position.z += 0.25;",.fragment:"#pragma transparent\n#pragma body\n_output.color.a=0.5;"]
            }
            let plane=SCNPlane(width:1.5,height:1.5);plane.materials=[material]
            let node=SCNNode(geometry:plane)
            node.position=SCNVector3(index%2==0 ? -1:1,index<2 ? 1:-1,0)
            scene.rootNode.addChildNode(node)
        }
        present(scene,camera:eye,device:device)
        for reverseZ in [true] {
            for model:SCNMaterial.LightingModel? in [.constant,.physicallyBased,nil] {
                let probe=try AOPreparationProbe(device:device,source:scene,camera:eye,reverseZ:reverseZ,width:256,height:256,lightingModel:model)
                try probe.measure(at:destination.appendingPathComponent("\(model?.rawValue ?? "preserve-source")-reverseZ-\(reverseZ)"))
            }
        }
        // All quadrants have the same mapped solid receiver. Overlay semantics:
        // 0: untouched control; 1: multiply impression; 2: airborne dust;
        // 3: mapped surface dressing. Only dressing should change its normal.
        let overlap=SCNScene()
        overlap.rootNode.addChildNode(eye.clone())
        for index in 0..<4 {
            let receiver=SCNMaterial();receiver.lightingModel = .physicallyBased
            let normal=MTLTextureDescriptor.texture2DDescriptor(pixelFormat:.rgba8Unorm,width:1,height:1,mipmapped:false)
            normal.usage = .shaderRead
            let map=device.makeTexture(descriptor:normal)!
            maps[1].withUnsafeBytes { map.replace(region:MTLRegionMake2D(0,0,1,1),mipmapLevel:0,withBytes:$0.baseAddress!,bytesPerRow:4) }
            receiver.normal.contents=map
            let plane=SCNPlane(width:1.5,height:1.5);plane.materials=[receiver]
            let node=SCNNode(geometry:plane)
            node.position=SCNVector3(index%2==0 ? -1:1,index<2 ? 1:-1,0)
            overlap.rootNode.addChildNode(node)
            if index>0 {
                let material=SCNMaterial();material.lightingModel = .physicallyBased
                material.writesToDepthBuffer=false
                if index==1 { material.lightingModel = .constant;material.blendMode = .multiply }
                if index==2 {
                    material.lightingModel = .lambert
                    material.shaderModifiers=[.geometry:"#pragma varyings\nfloat4 dustTint;\n#pragma body\nout.dustTint=float4(1.0);",
                                              .fragment:"#pragma transparent\n#pragma body\n_output.color.a=0.5;"]
                }
                if index==3 {
                    let tilted=device.makeTexture(descriptor:normal)!
                    maps[2].withUnsafeBytes { tilted.replace(region:MTLRegionMake2D(0,0,1,1),mipmapLevel:0,withBytes:$0.baseAddress!,bytesPerRow:4) }
                    material.normal.contents=tilted
                    material.shaderModifiers=[.fragment:"#pragma transparent\n#pragma body\n_output.color.a=0.5;"]
                }
                let geometry=SCNPlane(width:1.5,height:1.5);geometry.materials=[material]
                let overlay=SCNNode(geometry:geometry);overlay.position=node.position;overlay.position.z=0.01
                overlay.renderingOrder=1;overlap.rootNode.addChildNode(overlay)
            }
        }
        present(overlap,camera:eye,device:device)
        for policy in [false,true] {
            let probe=try AOPreparationProbe(device:device,source:overlap,camera:eye,reverseZ:true,width:256,height:256,surfacePolicy:policy)
            try probe.measure(at:destination.appendingPathComponent("overlap-policy-\(policy)-reverseZ-true"))
        }
        for distance in [5,25,120] {
            let perspective=SCNScene(),camera=SCNNode()
            camera.camera=SCNCamera();camera.camera!.fieldOfView=48
            camera.camera!.zNear=0.02;camera.camera!.zFar=250
            camera.position=SCNVector3(0,0,distance);perspective.rootNode.addChildNode(camera)
            let material=SCNMaterial();material.lightingModel = .physicallyBased
            let descriptor=MTLTextureDescriptor.texture2DDescriptor(pixelFormat:.rgba8Unorm,width:1,height:1,mipmapped:false)
            descriptor.usage = .shaderRead
            let texture=device.makeTexture(descriptor:descriptor)!
            maps[1].withUnsafeBytes { texture.replace(region:MTLRegionMake2D(0,0,1,1),mipmapLevel:0,withBytes:$0.baseAddress!,bytesPerRow:4) }
            material.normal.contents=texture
            let plane=SCNPlane(width:20,height:20);plane.materials=[material]
            let node=SCNNode(geometry:plane);node.eulerAngles.y=0.35
            perspective.rootNode.addChildNode(node)
            present(perspective,camera:camera,device:device)
            let probe=try AOPreparationProbe(device:device,source:perspective,camera:camera,reverseZ:true,width:256,height:256)
            try probe.measure(at:destination.appendingPathComponent("perspective-\(distance)-reverseZ-true"))
        }
    }
}
