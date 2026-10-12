import AppKit
import SceneKit
import SimulationCore
import simd

extension AppController {
    /// Native regressions for the two shoulder slots and compressed city cameras.
    func checkPassages(at directory:URL)->Bool {
        do {
            try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
            weatherOverride=false;defer { weatherOverride=nil }
            startDirtTrack();dirtIntro=nil;race.countDown(dt:3);setRaceControlsHidden(true)
            dirtWorld.sky.apply(BinaryDaylight(fraction:0.48,phase:1.2))
            let town=dirtWorld.town,city=town.collisionWorld
            func capture(_ name:String) throws {
                updateOpponents();updateRaceWorld(dt:0)
                town.update(dt:0,camera:world.camera.position,player:SIMD2(simulation.x,simulation.z))
                _=view.prepare(dirtWorld.scene,shouldAbortBlock:nil)
                try saveTownFrame(name,at:directory)
            }
            var maxJoinStep=0.0
            for side in [-1.0,1.0] {
                for out in [-1.2,-1.0] {
                    var last:Double?=nil
                    for along in stride(from:3.8,through:4.2,by:0.005) {
                        let p=CityExit.point(side*along,out),h=DirtCourse.height(x:p.x,z:p.y)
                        if let last { maxJoinStep=max(maxJoinStep,abs(h-last)) };last=h
                    }
                }
                for opened in [false,true] {
                    var gate=CityGate();gate.wantsOpen=opened
                    for _ in 0..<900 { gate.advance(dt:1.0/60,bodies:[]) };racePhysics.gate=gate;dirtWorld.updateGate(gate)
                    let p=CityExit.point(side*4,-1),eye=CityExit.point(side*6,-3.5)
                    world.camera.position=SCNVector3(eye.x,DirtCourse.height(x:eye.x,z:eye.y)+0.8,eye.y)
                    world.camera.look(at:SCNVector3(p.x,DirtCourse.height(x:p.x,z:p.y),p.y),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                    try capture("gate-flank-\(Int(side))-\(opened ? "open":"closed")")
                }
            }
            let center=CityExit.point(0,0)
            world.camera.position=SCNVector3(center.x,18,center.y)
            world.camera.look(at:SCNVector3(center.x,0,center.y),up:SCNVector3(0,0,-1),localFront:SCNVector3(0,0,-1))
            try capture("gate-overhead")
            for (name,eye,aim) in [
                ("service-track-side",SIMD3(-11.5,1.4,-15),SIMD3(-7.5,0.25,-11.5)),
                ("service-infield-side",SIMD3(-5.2,1.2,-8.5),SIMD3(-7.5,0.3,-12.4)),
                ("service-infield-shifted",SIMD3(-4.9,1.2,-8.5),SIMD3(-7.2,0.3,-12.4)),
                ("service-overhead",SIMD3(-7.5,18,-12),SIMD3(-7.5,0,-12))] {
                world.camera.position=SCNVector3(eye.x,eye.y,eye.z)
                world.camera.look(at:SCNVector3(aim.x,aim.y,aim.z),up:name == "service-overhead" ? SCNVector3(0,0,-1):SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                try capture(name)
                if name == "service-overhead" {
                    let camera=world.camera.camera!,occlusion=camera.screenSpaceAmbientOcclusionIntensity
                    camera.screenSpaceAmbientOcclusionIntensity=0
                    try capture("service-overhead-no-occlusion")
                    camera.screenSpaceAmbientOcclusionIntensity=occlusion
                    let pigment=dirtWorld.scene.rootNode.childNode(withName:"Track clay mixed into service sand",recursively:false)
                    pigment?.isHidden=true
                    try capture("service-overhead-no-clay")
                    pigment?.isHidden=false
                    var shadowLights:[SCNLight]=[]
                    dirtWorld.scene.rootNode.enumerateChildNodes { node,_ in
                        if let light=node.light,light.castsShadow { shadowLights.append(light);light.castsShadow=false }
                    }
                    try capture("service-overhead-no-shadows")
                    for light in shadowLights { light.castsShadow=true }
                }
            }
            var vestibuleClear=true,vestibuleStep=0.0
            if let door=town.doorways.min(by:{simd_distance($0.center,SIMD2(24.585,3.8))<simd_distance($1.center,SIMD2(24.585,3.8))}) {
                door.opening=1;door.place()
                for profile in RobotCollisions.profiles {
                    var previousPivot:SIMD3<Double>?=nil
                    for offset in stride(from:0.9,through:-0.4,by:-0.025) {
                        let p=door.center+door.outward*offset,ground=DirtCourse.height(x:p.x,z:p.y)
                        let pivot=town.cameraPivot(position:SIMD3(p.x,ground,p.y),chassisHeight:profile.height)
                        let current=SIMD3(Double(pivot.x),Double(pivot.y),Double(pivot.z))
                        if let previousPivot { vestibuleStep=max(vestibuleStep,simd_distance(current,previousPivot)) };previousPivot=current
                        let probe=RobotCollisions.Body(position:SIMD3(Double(pivot.x),Double(pivot.y)-0.08,Double(pivot.z)),profile:.init(mass:1,halfWidth:0.08,halfDepth:0.08,height:0.16,round:true))
                        if town.collisionWorld.nearby(probe).contains(where:{$0.profile.mass != 70 && RobotCollisions.contact(probe,$0) != nil}) { vestibuleClear=false }
                    }
                }
                door.opening=0;door.place()
            } else { vestibuleClear=false }
            var duneEyes:[SIMD3<Double>]=[]
            for x in [192.60,192.61] {
                let ground=DirtCourse.height(x:x,z:180),pivot=SCNVector3(x,ground+1.392,180)
                let desired=SCNVector3(x-cos(0.15)*9,ground+0.4+sin(0.15)*9,180)
                let lifted=town.terrainCamera(from:pivot,to:desired),eye=town.clearCamera(from:pivot,to:lifted)
                duneEyes.append(SIMD3(Double(eye.x),Double(eye.y),Double(eye.z)))
            }
            let duneCrestStep=simd_distance(duneEyes[0],duneEyes[1])
            let preflight:[String:Any]=["shoulderJoinStep":maxJoinStep,"vestibuleClear":vestibuleClear,"vestibuleStep":vestibuleStep,"duneCrestStep":duneCrestStep]
            try JSONSerialization.data(withJSONObject:preflight,options:[.prettyPrinted,.sortedKeys]).write(to:directory.appendingPathComponent("preflight.json"))
            if CommandLine.arguments.contains("--passage-preflight-only") { return maxJoinStep<0.025 && vestibuleClear && vestibuleStep<0.06 && duneCrestStep<0.1 }
            // Choose traversable routes with the least lateral camera clearance,
            // rather than the broadest entrance used by the earlier check.
            var ranked:[(Double,[SIMD2<Double>])]=[]
            for entry in town.pedestrianAccess+town.venueAccess {
                let route=Array(entry.route.dropFirst(2).reversed())
                guard route.count>2,simd_distance(route.first!,route.last!)>3 else { continue }
                var valid=true,score=9.0
                for p in route {
                    let probe=RobotCollisions.Body(position:SIMD3(p.x,0.02,p.y),profile:RobotCollisions.profiles[0])
                    if city.nearby(probe).contains(where:{$0.profile.mass != 70 && RobotCollisions.contact(probe,$0) != nil}) { valid=false;break }
                    for angle in [0.0,Double.pi/2,Double.pi,Double.pi*1.5] {
                        let a=SCNVector3(p.x,0.45,p.y),b=SCNVector3(p.x+sin(angle)*3,1.2,p.y+cos(angle)*3)
                        let c=town.clearCamera(from:a,to:b)
                        score=min(score,hypot(Double(c.x-a.x),Double(c.z-a.z)))
                    }
                }
                if valid && score>0.16 && score<1.2 { ranked.append((score,route)) }
            }
            ranked.sort{$0.0<$1.0}
            var routes:[[SIMD2<Double>]]=[]
            for item in ranked {
                if routes.allSatisfy({simd_distance($0[0],item.1[0])>12}) { routes.append(item.1) }
                if routes.count==3 { break }
            }
            var reports:[[String:Any]]=[],passed=routes.count==3 && maxJoinStep<0.025 && town.cameraRoom(at:SCNVector3(400,100,400))>9 && duneCrestStep<0.1 && vestibuleClear && vestibuleStep<0.06
            for (routeIndex,route) in routes.enumerated() {
                for hz in [30,60,120] { for mode in [0,1] {
                    let p=route[0],projection=DirtCourse.projection(x:p.x,z:p.y)
                    simulation=Simulation(seed:1,dirtTrack:true,dirtStartOffset:projection.offset,dirtStartPhase:projection.phase)
                    town.reset();cameraMode=mode;cameraDistance=3.5;orbitYaw=0.65;orbitPitch=0.5;freeCameraEye=nil;cameraBoomFraction=1
                    updateCamera(snap:true)
                    var frames=0,reached=0,penetrations=0,selfOcclusions=0,minBoom=9.0,maxStep=0.0,last=world.camera.simdPosition
                    func step(_ input:DriveInput) throws {
                        try autoreleasepool {
                        let dt=1.0/Double(hz)
                        advanceRacePhysics(input,dt:dt,raceDT:dt)
                        updatePlayerModel()
                        let player=RobotCollisions.Body(position:SIMD3(simulation.x,simulation.groundY,simulation.z),heading:simulation.heading,profile:RobotCollisions.profiles[0])
                        town.update(dt:dt,camera:world.camera.position,player:SIMD2(simulation.x,simulation.z),robots:[player],visible:{ self.view.isNode($0,insideFrustumOf:self.world.camera) })
                        if mode==1 { orbitYaw += dt*0.35 }
                        updateCamera(snap:false,dt:dt)
                        let eye=world.camera.simdPosition
                        let local=robot.root.convertPosition(world.camera.position,from:nil),bounds=robot.root.boundingBox
                        if local.x>bounds.min.x && local.x<bounds.max.x && local.y>bounds.min.y && local.y<bounds.max.y && local.z>bounds.min.z && local.z<bounds.max.z { selfOcclusions+=1 }
                        if frames>5 {
                            let jump=Double(simd_distance(last,eye))
                            if jump>max(0.8,maxStep) { print("JUMP route \(routeIndex) mode \(mode) hz \(hz) frame \(frames) robot \(simulation.x),\(simulation.z) eye \(eye) old \(last) fraction \(cameraBoomFraction)") }
                            maxStep=max(maxStep,jump)
                        };last=eye
                        let pivot=SIMD3(simulation.x,simulation.groundY+0.45,simulation.z)
                        minBoom=min(minBoom,simd_distance(SIMD3(Double(eye.x),Double(eye.y),Double(eye.z)),pivot))
                        let probe=RobotCollisions.Body(position:SIMD3(Double(eye.x),Double(eye.y)-0.08,Double(eye.z)),profile:.init(mass:1,halfWidth:0.08,halfDepth:0.08,height:0.16,round:true))
                        if town.collisionWorld.nearby(probe).contains(where:{$0.profile.mass != 70 && (RobotCollisions.contact(probe,$0)?.penetration ?? 0)>0.002}) {
                            if penetrations<2 { print("CAMERA HIT \(probe) solids \(city.nearby(probe).filter{$0.profile.mass != 70 && (RobotCollisions.contact(probe,$0)?.penetration ?? 0)>0.002})") }
                            penetrations+=1
                        }
                        if hz==60 && frames%(hz*2)==0 && frames<hz*18 { try capture("passage-\(routeIndex)-mode-\(mode)-\(frames/hz)") }
                        frames+=1
                        }
                    }
                    for target in Array(route.dropFirst())+Array(route.reversed().dropFirst()) {
                        var arrived=false
                        for _ in 0..<hz*16 {
                            let d=target-SIMD2(simulation.x,simulation.z),distance=simd_length(d)
                            if distance<0.24 { arrived=true;reached+=1;break }
                            let error=atan2(sin(atan2(d.x,d.y)-simulation.heading),cos(atan2(d.x,d.y)-simulation.heading))
                            var input=DriveInput();input.turn=max(-1,min(1,-error*2.5));input.throttle=abs(error)<0.2 ? min(0.22,distance*0.5):0
                            try step(input)
                        }
                        if !arrived { break }
                    }
                    for _ in 0..<hz { try step(DriveInput()) }
                    let success=reached==2*(route.count-1) && penetrations==0 && selfOcclusions==0 && minBoom>0.10 && maxStep<Double(12.0/Double(hz))
                    passed = passed && success
                    reports.append(["route":routeIndex,"hz":hz,"mode":mode,"passed":success,"reached":reached,"expected":2*(route.count-1),"cameraPenetrations":penetrations,"cameraInsideRobot":selfOcclusions,"cameraPeakMetresPerSecond":maxStep*Double(hz),"minBoom":minBoom,"maxFrameStep":maxStep,"seconds":Double(frames)/Double(hz)])
                    print(reports.last!);fflush(stdout)
                }}
            }
            let report:[String:Any]=["passed":passed,"vestibulePivotStepFor25mmMovement":vestibuleStep,"lowVestibuleCameraClearAllChassis":vestibuleClear,"duneCrestStepFor1cmMovement":duneCrestStep,"maximumShoulderJoinStepAt5mm":maxJoinStep,"routes":routes.map{$0.map{[$0.x,$0.y]}},"camera":reports]
            try JSONSerialization.data(withJSONObject:report,options:[.prettyPrinted,.sortedKeys]).write(to:directory.appendingPathComponent("passages.json"))
            return passed
        } catch { print("Passage check: \(error)");return false }
    }
}
