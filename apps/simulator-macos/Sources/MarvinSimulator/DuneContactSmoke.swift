import AppKit
import SceneKit
import SimulationCore
import simd

extension AppController {
    func checkDuneContacts(at directory:URL)->Bool {
        do {
            try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
            weatherOverride=false;defer { weatherOverride=nil }
            startDirtTrack();dirtIntro=nil;setRaceControlsHidden(true)
            dirtWorld.sky.apply(BinaryDaylight(fraction:0.35,phase:1.2))
            let live=ProcessInfo.processInfo.environment["MARVIN_DUNE_LIVE"]=="1"
            let movie=ProcessInfo.processInfo.environment["MARVIN_DUNE_MOVIE"]=="1"
            let meter=TownFrameMeter()
            if live {
                window.minSize=NSSize(width:640,height:400);window.setContentSize(NSSize(width:960,height:540))
                view.delegate=meter
            }
            if movie { try FileManager.default.createDirectory(at:directory.appendingPathComponent("movie"),withIntermediateDirectories:true) }
            defer { if live { view.delegate=nil } }
            let liveStart=CACurrentMediaTime()
            let characters=RacePerformance.Character.allCases
            var states=characters.enumerated().map { i,character in
                let p=DirtCourse.projection(x:211,z:62+Double(i)*3)
                return Simulation(dirtTrack:true,dirtStartOffset:p.offset,dirtStartPhase:p.phase,character:character)
            }
            var physics=characters.map { character in DirtRacePhysics(characters:[character]+characters.filter{$0 != character}) }
            var races=characters.map{_ in DirtRace()}
            var rivals=characters.map { character in
                characters.filter{$0 != character}.enumerated().map { i,c in DirtOpponent(slot:DirtCourse.startingGrid[i+1]) }
            }
            for i in races.indices { races[i].countDown(dt:3) }
            let renderer=SCNRenderer(device:view.device,options:nil);renderer.scene=dirtWorld.scene;renderer.pointOfView=world.camera
            var cpu=[Double](),maximumPatches=0,settleBefore=0.0,settleAfter=0.0,contactPixels=0,contactTop=900,bodyPixels=0
            func photo(_ name:String,_ state:Simulation,angle:Double,height:Double=0.7,distance:Double=1.5) throws {
                if live { return }
                let p=SIMD2(state.x,state.z),eye=p+SIMD2(sin(angle),cos(angle))*distance
                let ground=state.terrainHeight(x:eye.x,z:eye.y)
                world.camera.position=SCNVector3(eye.x,max(state.groundY+height,ground+0.20),eye.y)
                world.camera.look(at:SCNVector3(state.x,state.groundY+0.22,state.z),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                dirtWorld.sky.updateShadowCenter(SIMD3(state.x,state.groundY,state.z))
                let image=renderer.snapshot(atTime:state.elapsed,with:CGSize(width:1440,height:900),antialiasingMode:.multisampling4X)
                let bitmap=NSBitmapImageRep(data:image.tiffRepresentation!)!
                try bitmap.representation(using:.png,properties:[:])!.write(to:directory.appendingPathComponent(name+".png"))
            }
            for frame in 0..<1800 {
                let time=Double(frame)/60,start=CACurrentMediaTime()
                for i in states.indices {
                    var input=DriveInput()
                    if time<3 {
                        let error=atan2(sin(0.72-states[i].heading),cos(0.72-states[i].heading))
                        input.turn=max(-1,min(1,-error*2.5))
                    } else if time<11 { input.throttle=0.6 }
                    else if time<14 { input.brake=true }
                    else if time<18 { input.throttle = -0.45 }
                    else if time<21 { input.turn=0.45 }
                    else if time<26 { input.throttle=0.55 }
                    else { input.brake=true }
                    physics[i].sand=dirtWorld.duneSand.field
                    physics[i].advance(input,player:&states[i],race:&races[i],opponents:&rivals[i],dt:1.0/60,raceDT:1.0/60,robotCollisionsEnabled:false)
                    updateModel(characters[i],state:states[i])
                }
                dirtWorld.update(states[0],opponent:states[1],dt:1.0/60,modelScale:robot.modelScale,additional:[states[2],states[3]])
                cpu.append((CACurrentMediaTime()-start)*1000);maximumPatches=max(maximumPatches,dirtWorld.duneSand.patchCount)
                if frame==660 { try photo("wall-e-diagonal",states[3],angle:states[3].heading+1.2) }
                if frame==820 {
                    try photo("wall-e-stopped",states[3],angle:states[3].heading-1.1,height:0.4)
                    if !live {
                        wallE.dirtCoating.diagnosticContact(false)
                        try photo("wall-e-coating-off",states[3],angle:states[3].heading-1.1,height:0.4)
                        wallE.dirtCoating.diagnosticContact(true)
                        let on=NSBitmapImageRep(data:try Data(contentsOf:directory.appendingPathComponent("wall-e-stopped.png")))!
                        let off=NSBitmapImageRep(data:try Data(contentsOf:directory.appendingPathComponent("wall-e-coating-off.png")))!
                        for y in stride(from:0,to:on.pixelsHigh,by:3) { for x in stride(from:0,to:on.pixelsWide,by:3) {
                            let a=on.colorAt(x:x,y:y)!.usingColorSpace(.deviceRGB)!,b=off.colorAt(x:x,y:y)!.usingColorSpace(.deviceRGB)!
                            if max(abs(a.redComponent-b.redComponent),abs(a.greenComponent-b.greenComponent),abs(a.blueComponent-b.blueComponent))>0.02 {
                                contactPixels += 1;contactTop=min(contactTop,y)
                            }
                        }}
                    }
                }
                if frame==1079 { try photo("wall-e-reverse",states[3],angle:states[3].heading+2.4) }
                if frame==1259 { try photo("wall-e-pivot",states[3],angle:states[3].heading+1.4,height:0.45) }
                if frame==1560 { settleBefore=dirtWorld.duneSand.field.tiles.values.reduce(0){$0+$1.delta.reduce(0){$0+Double(abs($1))}} }
                if frame==1799 {
                    settleAfter=dirtWorld.duneSand.field.tiles.values.reduce(0){$0+$1.delta.reduce(0){$0+Double(abs($1))}}
                    for i in states.indices { try photo("\(characters[i].displayName)-contact",states[i],angle:states[i].heading+1.2) }
                    for (i,angle) in [0.0,1.5,3.0,4.5].enumerated() { try photo("wall-e-orbit-\(i)",states[3],angle:angle,height:0.35) }
                    try photo("four-robots-dunes",states[1],angle:2.5,height:9,distance:13)
                    if !live {
                        let body=NSBitmapImageRep(data:try Data(contentsOf:directory.appendingPathComponent("Marvin-contact.png")))!
                        // The fixed camera puts Marvin's opaque head here. A
                        // missing CAD texture-coordinate stream used to leave
                        // only eyes and belts visible without a shader error.
                        for y in stride(from:250,to:430,by:3) { for x in stride(from:550,to:1000,by:3) {
                            let c=body.colorAt(x:x,y:y)!.usingColorSpace(.deviceRGB)!
                            if c.redComponent*0.30+c.greenComponent*0.59+c.blueComponent*0.11<0.5 { bodyPixels += 1 }
                        }}
                    }
                }
                if movie && frame>=480 && frame<960 && frame%3==0 {
                    let state=states[3],angle=state.heading+1.15
                    let eye=SIMD2(state.x,state.z)+SIMD2(sin(angle),cos(angle))*1.7
                    world.camera.position=SCNVector3(eye.x,max(state.groundY+0.6,state.terrainHeight(x:eye.x,z:eye.y)+0.2),eye.y)
                    world.camera.look(at:SCNVector3(state.x,state.groundY+0.25,state.z),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                    dirtWorld.sky.updateShadowCenter(SIMD3(state.x,state.groundY,state.z))
                    let image=renderer.snapshot(atTime:time,with:CGSize(width:1280,height:720),antialiasingMode:.multisampling4X)
                    let bitmap=NSBitmapImageRep(data:image.tiffRepresentation!)!
                    try bitmap.representation(using:.jpeg,properties:[.compressionFactor:0.94])!.write(to:directory.appendingPathComponent("movie").appendingPathComponent(String(format:"frame-%04d.jpg",(frame-480)/3)))
                }
                if live {
                    let state=states[3]
                    world.camera.position=SCNVector3(state.x+3,state.groundY+2.5,state.z+5)
                    world.camera.look(at:SCNVector3(state.x,state.groundY+0.25,state.z),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                    dirtWorld.sky.updateShadowCenter(SIMD3(state.x,state.groundY,state.z))
                    RunLoop.main.run(until:Date(timeIntervalSinceNow:max(0,liveStart+Double(frame+1)/60-CACurrentMediaTime())))
                    if frame==180 { meter.reset() }
                }
                if frame%300==0 { print("Dune contact \(Int(time))s: patches \(maximumPatches), displaced \(dirtWorld.duneSand.field.displacedVolume)");fflush(stdout) }
            }
            let pausedClock=dirtWorld.duneSand.field.clock,pausedUploads=dirtWorld.duneSand.updates
            dirtWorld.update(states[0],opponent:states[1],dt:0,modelScale:robot.modelScale,additional:[states[2],states[3]])
            let pausePassed=pausedClock==dirtWorld.duneSand.field.clock && pausedUploads==dirtWorld.duneSand.updates
            let heights=dirtWorld.duneSand.field.tiles.values.flatMap{$0.delta}
            let minimum=heights.min() ?? 0,maximum=heights.max() ?? 0
            var report:[String:Any]=["seconds":30,"maximumPatches":maximumPatches,"minimumOffset":minimum,"maximumOffset":maximum,"displacedVolume":dirtWorld.duneSand.field.displacedVolume,"heightTextureUploads":dirtWorld.duneSand.updates,"cpuUpdateP95MS":cpu.sorted()[Int(Double(cpu.count)*0.95)],"settleBefore":settleBefore,"settleAfter":settleAfter,"positions":states.map{[$0.x,$0.groundY,$0.z]},"pausePassed":pausePassed,"opaqueMarvinBodySamples":bodyPixels,"contactCoatingChangedSamples":contactPixels,"contactCoatingTopPixel":contactTop,"finite":heights.allSatisfy{$0.isFinite},"emissions":dirtWorld.racerEmittedCount]
            if live { report["renderCadence"]=meter.report();report["drawableWidth"]=view.convertToBacking(view.bounds).width;report["drawableHeight"]=view.convertToBacking(view.bounds).height }
            try JSONSerialization.data(withJSONObject:report,options:[.prettyPrinted,.sortedKeys]).write(to:directory.appendingPathComponent("dune-contact.json"))
            print(report)
            let passed=pausePassed && (live || (contactPixels>20 && contactTop>400 && bodyPixels>1500)) && minimum < -0.01 && maximum>0.005 && maximumPatches<=SandDeformation.capacity && heights.allSatisfy{$0.isFinite && $0 > -0.14 && $0 < 0.11}
            dirtWorld.reset()
            return passed && dirtWorld.duneSand.patchCount==0 && dirtWorld.duneSand.field.tiles.isEmpty
        } catch { print("Dune contact: \(error)");return false }
    }
}
