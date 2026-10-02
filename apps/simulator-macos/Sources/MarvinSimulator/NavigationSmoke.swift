import AppKit
import SceneKit
import SimulationCore
import simd

extension AppController {
    func checkNavigation(at directory:URL)->Bool {
        do {
            try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
            weatherOverride=false;defer { weatherOverride=nil }
            startDirtTrack();dirtIntro=nil;race.countDown(dt:3)
            dirtWorld.sky.apply(BinaryDaylight(fraction:0.5,phase:1.2))
            simulation=Simulation(dirtTrack:true,dirtStartPhase:CityExit.phase)
            race=DirtRace(startPhase:CityExit.phase);race.countDown(dt:3)
            cameraMode=0;updateCamera(snap:true)
            func key(_ character:String,_ code:UInt16) {
                let e=NSEvent.keyEvent(with:.keyDown,location:.zero,modifierFlags:[],timestamp:0,windowNumber:window.windowNumber,context:nil,characters:character,charactersIgnoringModifiers:character,isARepeat:false,keyCode:code)!
                view.keyDown(with:e);view.keyUp(with:e)
            }
            key("c",8);key("c",8);key("g",5)
            var checkingCameraSwitch=false,switchesPassed=true
            var frames=0,maxCameraStep=0.0,maxFollowError=0.0,transitionFrame=0
            var regions=[RaceMapRegion.course.rawValue],route=[SIMD2<Double>](),captures=[String]()
            let city=dirtWorld.town.collisionWorld
            print("Town layout: \(dirtWorld.town.statistics)");fflush(stdout)
            let renderer=SCNRenderer(device:view.device,options:nil);renderer.scene=dirtWorld.scene;renderer.pointOfView=world.camera
            func capture(_ name:String,hud:Bool=true) throws {
                raceHUD.race=race;raceHUD.opponents=opponents;raceHUD.x=simulation.x;raceHUD.z=simulation.z;raceHUD.heading=simulation.heading;raceHUD.introducing=false;raceHUD.helpVisible=false
                dirtWorld.town.update(dt:0,camera:world.camera.position,player:SIMD2(simulation.x,simulation.z))
                let size=NSSize(width:1280,height:820),old=raceHUD.frame
                raceHUD.frame=NSRect(origin:.zero,size:size)
                let image=NSImage(size:size),background=renderer.snapshot(atTime:Double(frames)/60,with:size,antialiasingMode:.multisampling4X)
                image.lockFocusFlipped(true)
                background.draw(in:NSRect(origin:.zero,size:size),from:.zero,operation:.copy,fraction:1,respectFlipped:true,hints:nil)
                if hud { raceHUD.draw(raceHUD.bounds) };image.unlockFocus();raceHUD.frame=old
                let bitmap=NSBitmapImageRep(data:image.tiffRepresentation!)!
                try bitmap.representation(using:.png,properties:[:])!.write(to:directory.appendingPathComponent(name+".png"))
                captures.append(name)
            }
            func verifyTownImpressions(_ location:String) throws {
                let pose=world.camera.simdTransform
                let camera=world.camera.camera!,ortho=camera.usesOrthographicProjection,scale=camera.orthographicScale
                defer { world.camera.simdTransform=pose;camera.usesOrthographicProjection=ortho;camera.orthographicScale=scale }
                camera.usesOrthographicProjection=true;camera.orthographicScale=3
                world.camera.position=SCNVector3(simulation.x,simulation.groundY+12,simulation.z)
                world.camera.look(at:SCNVector3(simulation.x,simulation.groundY,simulation.z),up:SCNVector3(0,0,-1),localFront:SCNVector3(0,0,-1))
                let roots=dirtWorld.scene.rootNode.childNodes(passingTest: { node,_ in node.name == "Surface-aware ground impressions" })
                func shot(_ visible:Bool,_ order:Int)->NSBitmapImageRep {
                    for root in roots { root.isHidden = !visible;root.childNodes.forEach { $0.renderingOrder=order } }
                    return NSBitmapImageRep(data:renderer.snapshot(atTime:Double(frames)/60,with:CGSize(width:800,height:800),antialiasingMode:.multisampling4X).tiffRepresentation!)!
                }
                let base=shot(false,10),old=shot(true,0),fixed=shot(true,10)
                var oldPixels=0,fixedPixels=0
                for y in 0..<800 { for x in 0..<800 {
                    let a=base.colorAt(x:x,y:y)!.usingColorSpace(.deviceRGB)!
                    for (index,b) in [old,fixed].enumerated() {
                        let c=b.colorAt(x:x,y:y)!.usingColorSpace(.deviceRGB)!
                        if a.redComponent-c.redComponent>0.015 && a.greenComponent-c.greenComponent>0.015 {
                            if index==0 { oldPixels += 1 } else { fixedPixels += 1 }
                        }
                    }
                }}
                for (name,bitmap) in [("town-treads-default-order",old),("town-treads-after",fixed)] {
                    try bitmap.representation(using:.png,properties:[:])!.write(to:directory.appendingPathComponent(name+"-"+location+".png"))
                }
                print("Rendered town impressions \(location): default order \(oldPixels), corrected \(fixedPixels) visible pixels")
                guard fixedPixels>100 else { throw NSError(domain:"Town impressions invisible",code:1) }
                // Reproduce the raised plaza receiver that previously buried
                // impressions. Test final rendered pixels, not generated geometry.
                let receiver=SCNNode(geometry:SCNPlane(width:4,height:4))
                receiver.eulerAngles.x = -.pi/2
                receiver.position=SCNVector3(simulation.x,0.003,simulation.z)
                let surface=SCNMaterial();surface.lightingModel = .constant;surface.diffuse.contents=color(0xab9679)
                receiver.geometry?.materials=[surface];dirtWorld.scene.rootNode.addChildNode(receiver)
                defer { receiver.removeFromParentNode() }
                let clean=shot(false,10),imprinted=shot(true,10)
                var receiverPixels=0
                for y in 240..<560 { for x in 240..<560 {
                    let a=clean.colorAt(x:x,y:y)!.usingColorSpace(.deviceRGB)!,b=imprinted.colorAt(x:x,y:y)!.usingColorSpace(.deviceRGB)!
                    if a.redComponent-b.redComponent>0.015 && a.greenComponent-b.greenComponent>0.015 { receiverPixels += 1 }
                }}
                print("Raised town surface impressions \(location): \(receiverPixels) visible pixels")
                guard receiverPixels>100 else { throw NSError(domain:"Town surface buried impressions",code:2) }

            }
            func streetCapture(_ name:String) throws {
                let pose=world.camera.simdTransform,motion=overviewMotion,aim=cameraAim,mode=cameraMode
                cameraMode=0;updateCamera(snap:true)
                try capture(name)
                world.camera.simdTransform=pose;overviewMotion=motion;cameraAim=aim;cameraMode=mode
            }
            func step(_ input:DriveInput=DriveInput()) throws {
                let old=world.camera.simdPosition,region=raceHUD.mapRegion
                advanceRacePhysics(input,dt:1.0/60,raceDT:1.0/60)
                updateOpponents();updateRaceWorld(dt:1.0/60);updateCamera(snap:false)
                frames += 1
                if frames>180 && !checkingCameraSwitch { maxCameraStep=max(maxCameraStep,Double(simd_distance(old,world.camera.simdPosition))) }
                if raceHUD.mapRegion != region {
                    transitionFrame=frames;regions.append(raceHUD.mapRegion.rawValue)
                    print("Navigation \(regions.last!): \(simulation.x), \(simulation.z)");fflush(stdout)
                    try capture("transition-\(regions.count)-\(regions.last!)")
                }
                if !checkingCameraSwitch && frames-transitionFrame>120 && raceHUD.mapRegion != .course {
                    maxFollowError=max(maxFollowError,simd_distance(overviewMotion.aim,SIMD3(simulation.x,simulation.groundY+0.35,simulation.z)))
                }
                if frames%1200==0 { print("Navigation simulated \(frames/60)s");fflush(stdout) }
            }
            for _ in 0..<600 { try step() }
            guard cameraMode==2 else { return false }
            try capture("01-course")
            let pose=world.camera.simdTransform,far=world.camera.camera!.zFar
            let fogStart=dirtWorld.scene.fogStartDistance,fogEnd=dirtWorld.scene.fogEndDistance
            world.camera.camera!.zFar=800;dirtWorld.scene.fogStartDistance=500;dirtWorld.scene.fogEndDistance=800
            world.camera.position=SCNVector3(0,280,-180)
            world.camera.look(at:SCNVector3Zero,up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
            try capture("00-whole-town",hud:false)
            world.camera.simdTransform=pose;world.camera.camera!.zFar=far
            dirtWorld.scene.fogStartDistance=fogStart;dirtWorld.scene.fogEndDistance=fogEnd
            func drive(_ target:SIMD2<Double>) throws->Bool {
                for _ in 0..<5400 {
                    let d=target-SIMD2(simulation.x,simulation.z),distance=simd_length(d)
                    if distance<0.25 { return true }
                    let error=atan2(sin(atan2(d.x,d.y)-simulation.heading),cos(atan2(d.x,d.y)-simulation.heading))
                    var input=DriveInput();input.turn=max(-1,min(1,-error*2.5));input.throttle=abs(error)<0.22 ? min(0.65,distance*0.7):0
                    try step(input)
                }
                let body=RobotCollisions.Body(position:SIMD3(simulation.x,simulation.groundY,simulation.z),heading:simulation.heading,profile:RobotCollisions.profiles[lineup[0].rawValue])
                print("Navigation stuck: \(simulation.x),\(simulation.z) target \(target), heading \(simulation.heading), obstacles \(dirtWorld.town.collisionWorld.nearby(body))")
                try streetCapture("stuck-street");try capture("stuck-overview");return false
            }
            for p in [CityExit.point(0,-1.1),CityExit.point(0,2.5),CityExit.point(0,8)] { guard try drive(p) else { return false } }
            try capture("02-town-near-course")
            let apron=SIMD2(simulation.x,simulation.z)
            var cursor=apron
            for destination in [SIMD2(29.0,-10.0),SIMD2(33,3),SIMD2(35,14),SIMD2(36,30),SIMD2(60,48),SIMD2(82,44),SIMD2(105,42),SIMD2(128,47),SIMD2(150,52),SIMD2(170,56),SIMD2(207,65)] {
                guard let leg=TownEscapeRoute(city:city,origin:cursor).route(from:cursor,to:destination) else { return false }
                for p in leg.dropFirst() { guard try drive(p) else { return false };route.append(p) }
                cursor=SIMD2(simulation.x,simulation.z)
                if destination.x==105 { try capture("03-town");try verifyTownImpressions("outer-town") }
                if destination.x==60 { try verifyTownImpressions("street") }
                if [60.0,105,150].contains(destination.x) { try streetCapture("street-out-\(Int(destination.x))") }
            }
            for _ in 0..<180 { var input=DriveInput();input.brake=true;try step(input) }
            try capture("04-dunes")
            func verifyCameraCycle() throws {
                checkingCameraSwitch=true
                key("c",8);key("c",8)
                let before=world.camera.simdPosition
                key("c",8)
                let entryStep=simd_distance(before,world.camera.simdPosition)
                switchesPassed = switchesPassed && cameraMode==2 && entryStep<1
                for _ in 0..<240 { var input=DriveInput();input.brake=true;try step(input) }
                let targetY=max(simulation.groundY+0.35,raceHUD.mapRegion == .dunes ? DirtCourse.height(x:simulation.x,z:simulation.z)+0.35:simulation.groundY+0.35)
                let error=simd_distance(overviewMotion.aim,SIMD3(simulation.x,targetY,simulation.z))
                print("Camera cycle \(raceHUD.mapRegion.rawValue): entry step \(entryStep), settled error \(error)");fflush(stdout)
                switchesPassed = switchesPassed && error<0.05
                checkingCameraSwitch=false
            }
            try verifyCameraCycle()
            try capture("04b-dunes-camera-cycle")
            // The same physical route in reverse exercises both re-entry boundaries.
            var returnShot=false
            for p in route.dropLast().reversed() {
                guard try drive(p) else { return false }
                if !returnShot && simulation.x<100 { try streetCapture("street-return");returnShot=true }
            }
            guard try drive(apron) else { return false }
            try verifyCameraCycle()
            try capture("05-town-return")
            for p in [CityExit.point(0,2.5),CityExit.point(0,-1.1)] { guard try drive(p) else { return false } }
            for _ in 0..<180 { var input=DriveInput();input.brake=true;try step(input) }
            try capture("06-course-return")
            let passed=switchesPassed && regions==["COURSE","TOWN","DUNES","TOWN","COURSE"] && cameraMode==2 && maxCameraStep<1.2 && maxFollowError<1.5 && simd_length(overviewMotion.aim)<0.1
            let report:[String:Any]=["passed":passed,"cameraCyclesOutsidePassed":switchesPassed,"regions":regions,"simulatedSeconds":Double(frames)/60,"maximumCameraStepMetres":maxCameraStep,"maximumFollowErrorMetres":maxFollowError,"finalCourseAim":Array([overviewMotion.aim.x,overviewMotion.aim.y,overviewMotion.aim.z]),"mapPanelWidth":270,"captures":captures]
            try JSONSerialization.data(withJSONObject:report,options:[.prettyPrinted,.sortedKeys]).write(to:directory.appendingPathComponent("navigation.json"))
            print(report);return passed
        } catch { print("Navigation: \(error)");return false }
    }
}
