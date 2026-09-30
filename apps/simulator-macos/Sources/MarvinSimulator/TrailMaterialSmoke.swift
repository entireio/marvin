import AppKit
import SceneKit
import SimulationCore

extension AppController {
    func checkTrailMaterial(at directory:URL)->Bool {
        do {
            try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
            let trail=DirtTrail(style:.tracks)
            var state=Simulation(),input=DriveInput();input.throttle=1
            for _ in 0..<45 { trail.update(state,contacts:[(x:0,z:0,width:0.2)]);state.advance(input,dt:1.0/60) }
            guard let first=trail.root.childNodes.first,let geometry=first.geometry else { return false }
            let source=geometry.sources(for:.vertex)[0]
            var points:[SCNVector3]=[]
            source.data.withUnsafeBytes { bytes in
                for i in 0..<4 {
                    let offset=source.dataOffset+i*source.dataStride
                    let p=bytes.baseAddress!.advanced(by:offset).assumingMemoryBound(to:Float.self)
                    points.append(SCNVector3(p[0],p[1],p[2]))
                }
            }
            let center=SCNVector3(points.map{$0.x}.reduce(0,+)/4,points.map{$0.y}.reduce(0,+)/4,points.map{$0.z}.reduce(0,+)/4)
            let scene=SCNScene(),camera=SCNNode();camera.camera=SCNCamera();camera.camera?.usesOrthographicProjection=true;camera.camera?.orthographicScale=0.14
            camera.position=SCNVector3(center.x,center.y+2,center.z);camera.look(at:center,up:SCNVector3(0,0,-1),localFront:SCNVector3(0,0,-1));scene.rootNode.addChildNode(camera)
            let ground=SCNNode(geometry:SCNPlane(width:2,height:2));ground.eulerAngles.x = -.pi/2;ground.position=SCNVector3(center.x,center.y-0.007,center.z)
            let mat=SCNMaterial();mat.lightingModel = .constant;ground.geometry?.materials=[mat];scene.rootNode.addChildNode(ground);scene.rootNode.addChildNode(trail.root)
            let renderer=SCNRenderer(device:view.device,options:nil);renderer.scene=scene;renderer.pointOfView=camera
            var passed=true
            for (name,ink) in [("sand",0xd8b47c),("clay",0x986441),("dark-ground",0x575b60)] {
                mat.diffuse.contents=color(UInt32(ink))
                func shot(_ marks:Bool)->NSBitmapImageRep {
                    trail.root.isHidden = !marks
                    let image=renderer.snapshot(atTime:0,with:CGSize(width:256,height:256),antialiasingMode:.multisampling4X)
                    return NSBitmapImageRep(data:image.tiffRepresentation!)!
                }
                let base=shot(false),marked=shot(true)
                var changed=0,bad=0
                for y in 0..<256 { for x in 0..<256 {
                    let a=base.colorAt(x:x,y:y)!.usingColorSpace(.deviceRGB)!,b=marked.colorAt(x:x,y:y)!.usingColorSpace(.deviceRGB)!
                    let ratios=[b.redComponent/a.redComponent,b.greenComponent/a.greenComponent,b.blueComponent/a.blueComponent]
                    if ratios.min()!<0.98 {
                        changed += 1
                        if ratios.min()!<0.65 || ratios.max()!-ratios.min()!>0.04 { bad += 1 }
                    }
                }}
                passed = passed && changed>100 && bad==0
                print("Trail \(name): \(changed) impression pixels, \(bad) excessive contrast/hue errors")
                try marked.representation(using:.png,properties:[:])!.write(to:directory.appendingPathComponent("trail-\(name).png"))
            }
            print("Trail material: \(passed ? "PASS":"FAIL")");return passed
        } catch { print(error);return false }
    }
}
