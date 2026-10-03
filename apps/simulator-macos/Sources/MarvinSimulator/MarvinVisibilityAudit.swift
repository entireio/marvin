import AppKit
import SceneKit

extension AppController {
    /// Diagnostic only. A sampled absence of contribution is not enclosure proof.
    func auditMarvinVisibility(at directory:URL)throws {
        let priorScene=view.scene,priorContinuous=view.rendersContinuously,priorPlaying=view.isPlaying
        let cameraTransform=world.camera.transform,yaw=robot.yawNode.transform,pitch=robot.pitchNode.transform
        let names=["Motor_Left","Motor_Right","Servo_Head","Servo_Tilt","Battery","Bearings","Axis_Mount"]
        let parts=try names.map { name -> SCNNode in
            guard let node=robot.root.childNode(withName:name,recursively:true),!node.isHidden else {
                throw NSError(domain:"MarvinVisibilityAudit",code:1,userInfo:[NSLocalizedDescriptionKey:"Missing visible part: \(name)"])
            }
            return node
        }
        let cameraOnly=ProcessInfo.processInfo.environment["MARVIN_INTERNALS_CAMERA_ONLY"]=="1"
        let exclusionBit=1 << 21,cameraMask=world.camera.camera!.categoryBitMask
        let partMasks=parts.map { $0.categoryBitMask }
        var lights:[(SCNLight,Int)]=[]
        dirtWorld.scene.rootNode.enumerateChildNodes { node,_ in
            if let light=node.light { lights.append((light,light.categoryBitMask)) }
        }
        if cameraOnly {
            world.camera.camera!.categoryBitMask=cameraMask & ~exclusionBit
            for (light,mask) in lights { light.categoryBitMask=mask | exclusionBit }
        }
        let rootHidden=robot.root.isHidden
        defer {
            for (index,node) in parts.enumerated() { node.isHidden=false;node.categoryBitMask=partMasks[index] }
            world.camera.camera!.categoryBitMask=cameraMask
            for (light,mask) in lights { light.categoryBitMask=mask }
            robot.root.isHidden=rootHidden;robot.yawNode.transform=yaw;robot.pitchNode.transform=pitch
            world.camera.transform=cameraTransform
            view.scene=priorScene;view.isPlaying=priorPlaying;view.rendersContinuously=priorContinuous
        }
        view.rendersContinuously=false;view.isPlaying=false;view.scene=nil
        let renderer=SCNRenderer(device:view.device,options:nil)
        renderer.scene=dirtWorld.scene;renderer.pointOfView=world.camera;renderer.usesReverseZ=view.usesReverseZ
        func snapshot()->NSBitmapImageRep {
            autoreleasepool { NSBitmapImageRep(data:renderer.snapshot(atTime:0,with:CGSize(width:1920,height:1080),antialiasingMode:.multisampling2X).tiffRepresentation!)! }
        }
        func difference(_ a:NSBitmapImageRep,_ b:NSBitmapImageRep)throws -> [String:Int] {
            guard a.bitsPerSample==8,b.bitsPerSample==8,a.samplesPerPixel==b.samplesPerPixel,
                  a.bitsPerPixel==a.samplesPerPixel*8,b.bitsPerPixel==b.samplesPerPixel*8,a.bitmapFormat==b.bitmapFormat,
                  a.pixelsWide==b.pixelsWide,a.pixelsHigh==b.pixelsHigh,!a.isPlanar,!b.isPlanar,
                  let aa=a.bitmapData,let bb=b.bitmapData else { throw NSError(domain:"MarvinVisibilityAudit",code:2) }
            var changed=0,maximum=0
            for y in 0..<a.pixelsHigh { for x in 0..<a.pixelsWide {
                var delta=0
                for c in 0..<a.samplesPerPixel {
                    delta=max(delta,abs(Int(aa[y*a.bytesPerRow+x*a.samplesPerPixel+c])-Int(bb[y*b.bytesPerRow+x*b.samplesPerPixel+c])))
                }
                if delta>0 { changed += 1 };maximum=max(maximum,delta)
            }}
            return ["changedPixels":changed,"maximumChannelDelta":maximum]
        }
        func save(_ bitmap:NSBitmapImageRep,_ name:String)throws {
            try bitmap.representation(using:.png,properties:[:])!.write(to:directory.appendingPathComponent(name+".png"))
        }
        var rows:[[String:Any]]=[]
        let center=robot.root.worldPosition
        for pose in 0..<3 {
            robot.yawNode.transform=yaw;robot.pitchNode.transform=pitch
            if pose>0 {
                robot.yawNode.eulerAngles.y=pose==1 ? -0.8:0.8
                robot.pitchNode.eulerAngles.x=pose==1 ? -0.25:0.25
            }
            for angle in 0..<12 {
                let a=Double(angle)*Double.pi/6
                let altitude=angle.isMultiple(of:2) ? 0.45:1.15
                world.camera.position=SCNVector3(Double(center.x)+sin(a)*1.5,Double(center.y)+altitude,Double(center.z)+cos(a)*1.5)
                world.camera.look(at:SCNVector3(center.x,center.y+0.25,center.z),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                _=snapshot();_=snapshot()
                let baseline=snapshot(),repeatBaseline=snapshot()
                let repeated=try difference(baseline,repeatBaseline)
                let prefix="marvin-pose-\(pose)-angle-\(angle)"
                try save(baseline,prefix+"-reference")
                robot.root.isHidden=true;let positive=try difference(baseline,snapshot());robot.root.isHidden=rootHidden
                let positiveRestored=try difference(baseline,snapshot())
                for (partIndex,node) in parts.enumerated() {
                    let before=snapshot(),beforeDifference=try difference(baseline,before)
                    if cameraOnly { node.categoryBitMask=exclusionBit } else { node.isHidden=true }
                    let removed=snapshot()
                    node.isHidden=false;node.categoryBitMask=partMasks[partIndex]
                    let restoredImage=snapshot()
                    let delta=try difference(baseline,removed),restored=try difference(baseline,restoredImage)
                    let controlsValid=beforeDifference["changedPixels"]==0 && repeated["changedPixels"]==0 && positiveRestored["changedPixels"]==0 && restored["changedPixels"]==0 && positive["changedPixels",default:0]>0
                    if delta["changedPixels",default:0]>0 { try save(removed,prefix+"-without-"+node.name!) }
                    if restored["changedPixels",default:0]>0 { try save(restoredImage,prefix+"-restored-"+node.name!) }
                    if beforeDifference["changedPixels",default:0]>0 { try save(before,prefix+"-before-"+node.name!) }
                    rows.append(["pose":pose,"angle":angle,"part":node.name!,"repeatBaseline":repeated,"preRemoval":beforeDifference,"positiveControl":positive,
                        "positiveRestoration":positiveRestored,"removed":delta,"restored":restored,"controlsValid":controlsValid,
                        "classification":controlsValid ? (delta["changedPixels"]==0 ? "no contribution observed":"contribution observed"):"invalid controls",
                        "noContributionInSample":controlsValid && delta["changedPixels"]==0])
                }
                print("Marvin visibility pose \(pose), angle \(angle) complete")
            }
        }
        let report:[String:Any]=["cameraOnlyExclusion":cameraOnly,"resolution":[1920,1080],"msaaSamples":2,"rows":rows,
            "method":"Frozen full-scene per-part exclusion, optionally camera-only retaining shadow eligibility; exact repeated/restored controls and whole-robot positive control.",
            "limitations":"Sampled camera and head poses only, one loaded daylight/weather state. Zero sampled contribution is not proof of enclosure or safe removal in all gameplay. No timing claim."]
        try JSONSerialization.data(withJSONObject:report,options:[.prettyPrinted,.sortedKeys]).write(to:directory.appendingPathComponent("marvin-visibility-audit.json"))
    }
}
