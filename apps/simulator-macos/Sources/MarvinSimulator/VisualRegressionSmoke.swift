import AppKit
import SceneKit
import SimulationCore
import simd

extension AppController {
    func checkVisualRegressions(at directory:URL)->Bool {
        do {
            try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
            weatherOverride=false;startDirtTrack();dirtIntro=nil;raceHUD.isHidden=true
            dirtWorld.sky.apply(BinaryDaylight(fraction:0.32,phase:1.2))
            let scene=SCNScene(),camera=SCNNode();camera.camera=SCNCamera();camera.camera?.zNear=0.05;camera.camera?.zFar=250
            scene.background.contents=color(0xb9c5ca);scene.rootNode.addChildNode(camera)
            let floor=SCNNode(geometry:SCNPlane(width:1536,height:1536));floor.eulerAngles.x = -.pi/2;floor.position.y = -0.006;floor.name="QA floor"
            let mat=SCNMaterial();mat.diffuse.contents=color(0xa88864);mat.lightingModel = .lambert;floor.geometry?.materials=[mat];scene.rootNode.addChildNode(floor)
            // Match the town's depth range. A tiny isolated receiver hides the
            // automatic projection failure caused by distant city geometry.
            for i in 0..<16 {
                let a=Double(i)*Double.pi/8,r=Double(i%2==0 ? 60:110)
                let tower=SCNNode(geometry:SCNBox(width:8,height:12,length:8,chamferRadius:0))
                tower.position=SCNVector3(sin(a)*r,6,cos(a)*r);scene.rootNode.addChildNode(tower)
            }
            let ambient=SCNNode();ambient.light=SCNLight();ambient.light?.type = .ambient;ambient.light?.intensity=250;scene.rootNode.addChildNode(ambient)
            let lights=dirtWorld.sky.suns.map{$0.clone()};lights.forEach{scene.rootNode.addChildNode($0)}
            if ProcessInfo.processInfo.environment["MARVIN_SHADOW_AUTO"]=="1" {
                for (i,light) in lights.enumerated() {
                    light.light?.automaticallyAdjustsShadowProjection=true
                    light.light?.shadowMapSize=CGSize(width:i==0 ? 2048:1024,height:i==0 ? 2048:1024)
                    light.light?.shadowSampleCount=4
                }
            }
            var robots=[SCNNode]()
            for (i,c) in RacePerformance.Character.allCases.enumerated() {
                let node=modelRoot(c).clone();node.position=SCNVector3(Double(i)*2.3-3.45,0,0);node.eulerAngles=SCNVector3Zero;scene.rootNode.addChildNode(node);robots.append(node)
            }
            let renderer=SCNRenderer(device:view.device,options:nil);renderer.scene=scene;renderer.pointOfView=camera
            func capture()->NSBitmapImageRep { NSBitmapImageRep(data:renderer.snapshot(atTime:0,with:CGSize(width:1280,height:800),antialiasingMode:.multisampling4X).tiffRepresentation!)! }
            var observations=[Int:[Double]](),perRobotShadowSamples=[Int](repeating:0,count:4)
            for i in 0..<24 {
                let angle=Double(i%12)*Double.pi/6,near=i>=12
                let altitude=near ? (i%3==0 ? 0.75:1.6):(i%3==0 ? 1.7:4.2)
                let radius=near ? 2.2:7.0,centerX=near ? -3.45:0.0
                camera.position=SCNVector3(centerX+sin(angle)*radius,altitude,cos(angle)*radius)
                camera.look(at:SCNVector3(centerX,0.2,0),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                lights.forEach{$0.light?.castsShadow=false};let unshadowed=capture()
                lights.forEach{$0.light?.castsShadow=true};let shadowed=capture()
                try shadowed.representation(using:.png,properties:[:])!.write(to:directory.appendingPathComponent("shadow-angle-\(i).png"))
                for z in -15...15 { for x in -45...45 {
                    let p=SCNVector3(Double(x)*0.1,0,Double(z)*0.1),screen=renderer.projectPoint(p)
                    let px=Int(screen.x),py=799-Int(screen.y)
                    guard px>=2,px<1278,py>=2,py<798,screen.z>0,screen.z<1 else { continue }
                    // Reject samples whose view is occluded by a robot.
                    let hits=renderer.hitTest(CGPoint(x:Double(screen.x),y:Double(screen.y)),options:[.firstFoundOnly:true,.categoryBitMask:1])
                    guard hits.first?.node.name=="QA floor" else { continue }
                    // Exclude silhouette edges: their subpixel coverage changes
                    // with viewing angle even when the world shadow is stable.
                    var localRatios=[Double]()
                    for dy in [-2,0,2] { for dx in [-2,0,2] {
                        let u=unshadowed.colorAt(x:px+dx,y:py+dy)!.usingColorSpace(.deviceRGB)!
                        let v=shadowed.colorAt(x:px+dx,y:py+dy)!.usingColorSpace(.deviceRGB)!
                        localRatios.append(Double(v.redComponent+v.greenComponent+v.blueComponent)/max(0.01,Double(u.redComponent+u.greenComponent+u.blueComponent)))
                    }}

                    let a=unshadowed.colorAt(x:px,y:py)!.usingColorSpace(.deviceRGB)!,b=shadowed.colorAt(x:px,y:py)!.usingColorSpace(.deviceRGB)!
                    let reference=Double(a.redComponent+a.greenComponent+a.blueComponent)
                    let ratio=Double(b.redComponent+b.greenComponent+b.blueComponent)/max(0.01,reference)
                    if ratio<0.85 { let nearest=max(0,min(3,Int(((Double(x)*0.1+3.45)/2.3).rounded())));perRobotShadowSamples[nearest] += 1 }
                    guard localRatios.max()!-localRatios.min()!<0.08 else { continue }
                    observations[(z+15)*91+x+45,default:[]].append(ratio)
                }}
            }
            let repeated=observations.values.filter{$0.count>=3}
            let unstable=repeated.filter{($0.max()!-$0.min()!)>0.15}.count
            let shadowSamples=observations.values.flatMap{$0}.filter{$0<0.85}.count
            var passed=repeated.count>100 && perRobotShadowSamples.allSatisfy{$0>15} && shadowSamples>30 && unstable<max(8,repeated.count/100)
            print("Shadow sweep: \(repeated.count) world samples, \(shadowSamples) shadow observations, \(unstable) unstable")
            updateOpponents()
            let center=robot.root.worldPosition
            renderer.scene=dirtWorld.scene;renderer.pointOfView=world.camera
            for i in 0..<12 {
                let a=Double(i)*Double.pi/6,high=i%2==0
                world.camera.position=SCNVector3(Double(center.x)+sin(a)*1.5,Double(center.y)+(high ? 1.6:0.7),Double(center.z)+cos(a)*1.5)
                world.camera.look(at:SCNVector3(center.x,center.y+0.3,center.z),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                try capture().representation(using:.png,properties:[:])!.write(to:directory.appendingPathComponent("track-closeup-\(i).png"))
                if i==1 {
                    dirtWorld.sky.suns.forEach{$0.light?.castsShadow=false}
                    try capture().representation(using:.png,properties:[:])!.write(to:directory.appendingPathComponent("track-no-shadows.png"))
                    dirtWorld.sky.suns.forEach{$0.light?.castsShadow=true}
                }
            }
            renderer.scene=scene;renderer.pointOfView=camera
            robots.forEach{$0.removeFromParentNode()}
            let crowd=TownCrowd(),figures=crowd.inspectionFigures()
            passed = passed && figures.count==36
            for lod in 0..<3 { for back in [false,true] {
                let sheet=NSImage(size:NSSize(width:1280,height:960));sheet.lockFocus();color(0xb9c5ca).setFill();NSRect(x:0,y:0,width:1280,height:960).fill();sheet.unlockFocus()
                for (index,entry) in figures.filter({$0.0.hasSuffix("lod\(lod)")}).enumerated() {
                    let node=entry.1;scene.rootNode.addChildNode(node)
                    let seated=entry.0.contains("sit"),target=SCNVector3(0,seated ? 0.30:0.51,0)
                    camera.position=SCNVector3(back ? -0.3:0.3,seated ? 0.55:0.62,back ? -1.8:1.8);camera.look(at:target,up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                    let shot=renderer.snapshot(atTime:0,with:CGSize(width:320,height:320),antialiasingMode:.multisampling4X)
                    sheet.lockFocus();let rect=NSRect(x:index%4*320,y:(2-index/4)*320,width:320,height:320);shot.draw(in:rect)
                    (entry.0 as NSString).draw(at:NSPoint(x:rect.minX+8,y:rect.minY+8),withAttributes:[.font:NSFont.systemFont(ofSize:12),.foregroundColor:NSColor.black]);sheet.unlockFocus()
                    node.removeFromParentNode()
                }
                try NSBitmapImageRep(data:sheet.tiffRepresentation!)!.representation(using:.png,properties:[:])!.write(to:directory.appendingPathComponent("citizens-lod\(lod)-\(back ? "back":"front").png"))
            }}
            let report:[String:Any]=["passed":passed,"repeatedWorldSamples":repeated.count,"shadowSamples":shadowSamples,"unstableSamples":unstable,"citizenVariantsAndLODs":figures.count,"perRobotShadowSamples":perRobotShadowSamples]
            try JSONSerialization.data(withJSONObject:report,options:.prettyPrinted).write(to:directory.appendingPathComponent("visual-regression.json"))
            return passed
        } catch { print(error);return false }
    }
}
