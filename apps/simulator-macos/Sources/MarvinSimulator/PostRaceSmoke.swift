import AppKit
import SceneKit
import SimulationCore
import simd

extension AppController {
    func checkPostRaceEscape(at directory:URL)->Bool {
        do {
            try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
            weatherOverride=ProcessInfo.processInfo.environment["MARVIN_SANDSTORM"]=="1"
            defer { weatherOverride=nil }
            startDirtTrack();dirtIntro=nil;race.countDown(dt:3)
            if let value=ProcessInfo.processInfo.environment["MARVIN_DAYLIGHT_FRACTION"],let fraction=Double(value),fraction.isFinite {
                dirtWorld.sky.apply(BinaryDaylight(fraction:fraction,phase:1.2))
            }
            guard dirtWorld.escapeRoutes.count==4 else { print("FAIL: town circuit planning");return false }
            var frozen=[Double?](repeating:nil,count:4),early=false,firstOpen:Double?,pauseChecked=false
            var maxPenetration=0.0,cameraLocked=true
            var peakResidentBlocked=0.0
            var photographed=Set<Int>(),activeStart:Int?,allDepartedStart:Int?
            var roamingSeconds=0.0,secondsAfterAllDeparted=0.0
            var visibleWindows=[Set<Int>](repeating:[],count:12)
            var stationary=[Double](repeating:0,count:4),maxStationary=[Double](repeating:0,count:4)
            var lastPositions=[SIMD2<Double>](repeating:.zero,count:4),hudHidden=true
            var travelled=[Double](repeating:0,count:4),lateDistance=[Double](repeating:0,count:4)
            var lateCells=[Set<SIMD2<Int>>](repeating:[],count:4)
            var minuteCells=[[Set<SIMD2<Int>>]](repeating:[Set<SIMD2<Int>>](repeating:[],count:4),count:12)
            let capture=ProcessInfo.processInfo.environment["MARVIN_ROAM_MOVIE"]=="1"
            let renderer=SCNRenderer(device:view.device,options:nil)
            renderer.scene=dirtWorld.scene;renderer.pointOfView=world.camera
            var movieFrames=[0,0]
            if capture {
                for name in ["departure","minute-eleven"] {
                    try FileManager.default.createDirectory(at:directory.appendingPathComponent(name),withIntermediateDirectories:true)
                }
            }
            for frame in 0..<108000 {
                advanceRacePhysics(DirtOpponent.driveInput(for:simulation),dt:1.0/60,raceDT:1.0/60)
                let robotBodies=([simulation]+opponents.map{$0.simulation}).enumerated().map { i,s in
                    RobotCollisions.Body(position:SIMD3(s.x,s.groundY,s.z),heading:s.heading,profile:RobotCollisions.profiles[lineup[i].rawValue])
                }
                dirtWorld.town.update(dt:1.0/60,camera:world.camera.position,player:SIMD2(simulation.x,simulation.z),robots:robotBodies,visible:{ self.view.isNode($0,insideFrustumOf:self.world.camera) })
                peakResidentBlocked=max(peakResidentBlocked,dirtWorld.town.residents?.walkers.map{$0.blocked}.max() ?? 0)
                let races=[race]+opponents.map{$0.race}
                if racePhysics.escape.active && activeStart==nil { activeStart=frame }
                if racePhysics.escape.complete && allDepartedStart==nil { allDepartedStart=frame }
                if let start=activeStart {
                    updateDepartureHUD()
                    hudHidden = hudHidden && raceHUD.isHidden && hud.isHidden && window.toolbar?.isVisible==false
                    let seconds=Double(frame-start)/60,bin=Int(seconds/60)
                    roamingSeconds=seconds
                    while visibleWindows.count<=bin {
                        visibleWindows.append([])
                        minuteCells.append([Set<SIMD2<Int>>](repeating:[],count:4))
                    }
                    if frame%60==0 {
                        let all=[simulation]+opponents.map{$0.simulation}
                        for (i,state) in all.enumerated() {
                            let p=SIMD2(state.x,state.z),distance=simd_distance(p,lastPositions[i])
                            if racePhysics.escape.departed[i] {
                                travelled[i] += min(10,distance)
                                minuteCells[bin][i].insert(SIMD2(Int(floor(p.x/2)),Int(floor(p.y/2))))
                                stationary[i] = distance<0.08 ? stationary[i]+1:0
                                maxStationary[i]=max(maxStationary[i],stationary[i])
                                if distance>0.25 && max(abs(p.x),abs(p.y))<29 { visibleWindows[bin].insert(i) }
                                if seconds>=540 {
                                    lateDistance[i] += distance
                                    lateCells[i].insert(SIMD2(Int(floor(p.x/2)),Int(floor(p.y/2))))
                                }
                            }
                            lastPositions[i]=p
                        }
                    }
                    if capture && frame%6==0 {
                        // Render excerpts at normal simulation speed, including a
                        // late pass. Keep the same locked gameplay camera.
                        let clip=seconds<180 ? 0:(seconds>=660 && seconds<690 ? 1:-1)
                        if clip>=0 || (seconds>=655 && seconds<660) {
                            updateOpponents();updateRaceWorld(dt:0.1);updateCamera(snap:true)
                            if clip>=0 {
                                try autoreleasepool {
                                    let image=renderer.snapshot(atTime:Double(frame)/60,with:CGSize(width:1280,height:720),antialiasingMode:.multisampling4X)
                                    guard let tiff=image.tiffRepresentation,
                                          let jpeg=NSBitmapImageRep(data:tiff)?.representation(using:.jpeg,properties:[.compressionFactor:0.9]) else { throw CocoaError(.fileWriteUnknown) }
                                    let folder=clip==0 ? "departure":"minute-eleven"
                                    try jpeg.write(to:directory.appendingPathComponent(folder).appendingPathComponent(String(format:"frame-%05d.jpg",movieFrames[clip])))
                                }
                                movieFrames[clip] += 1
                            }
                        }
                    }
                    if let departed=allDepartedStart {
                        secondsAfterAllDeparted=Double(frame-departed)/60
                        if secondsAfterAllDeparted>=720 { break }
                    }
                    if (frame-start)%(60*60)==0 {
                        updateOpponents();updateRaceWorld(dt:1.0/60);updateCamera(snap:true)
                        try saveTownFrame("town-roaming-minute-\(Int(seconds/60))",at:directory)
                    }
                }
                if race.finished {
                    updateCamera(snap:true)
                    let before=world.camera.simdTransform
                    cycleCamera(nil);view.onOrbit?(120,80);view.onZoom?(50)
                    updateCamera(snap:true)
                    cameraLocked = cameraLocked && cameraMode==2 && world.camera.simdTransform==before
                        && world.camera.simdPosition==SIMD3<Float>(0,38,-33)
                    let escaped=racePhysics.escape.waypoint.filter{$0>=3}.count
                    if photographed.insert(escaped).inserted {
                        updateOpponents();updateRaceWorld(dt:1.0/60)
                        try saveTownFrame("postrace-overhead-\(escaped)",at:directory)
                    }
                }
                for i in races.indices { if races[i].finished && frozen[i]==nil { frozen[i]=races[i].elapsed } }
                if racePhysics.escape.active {
                    if !races.allSatisfy({$0.finished && $0.completedCooldownLap}) { early=true }
                    if firstOpen==nil {
                        firstOpen=Double(frame)/60
                        simulation.paused=true;let gate=racePhysics.gate.angle,elapsed=simulation.elapsed
                        advanceRacePhysics(DriveInput(),dt:0.1,raceDT:0.1)
                        pauseChecked=gate==racePhysics.gate.angle && elapsed==simulation.elapsed;simulation.paused=false
                    }
                    let states=[simulation]+opponents.map{$0.simulation}
                    for (i,state) in states.enumerated() where racePhysics.escape.waypoint[i]>=1 {
                        let body=RobotCollisions.Body(position:SIMD3(state.x,state.groundY,state.z),heading:state.heading,profile:RobotCollisions.profiles[lineup[i].rawValue])
                        let robots=states.enumerated().filter{$0.offset != i}.map { j,other in
                            RobotCollisions.Body(position:SIMD3(other.x,other.groundY,other.z),heading:other.heading,profile:RobotCollisions.profiles[lineup[j].rawValue])
                        }
                        for obstacle in dirtWorld.town.collisionWorld.nearby(body)+[racePhysics.gate.body]+CityExit.posts+robots {
                            if let contact=RobotCollisions.contact(body,obstacle) { maxPenetration=max(maxPenetration,contact.penetration) }
                        }
                    }
                }
                if frame%6000==0 { print("Postrace \(frame/60)s laps \(races.map{$0.laps.count}) cooldown \(races.map{$0.cooldownProgress}) waypoints \(racePhysics.escape.waypoint)");fflush(stdout) }

            }
            let states=[simulation]+opponents.map{$0.simulation},races=[race]+opponents.map{$0.race}
            let timesFrozen=races.indices.allSatisfy{frozen[$0]==races[$0].elapsed}
            let distinct=Set(racePhysics.escape.routes.map{String(describing:$0)}).count>1
            let sustained=secondsAfterAllDeparted>=720 && racePhysics.escape.tours.allSatisfy{$0>=2}
                && travelled.allSatisfy{$0>400} && maxStationary.allSatisfy{$0<20}
                && visibleWindows.dropLast().dropFirst(3).allSatisfy{!$0.isEmpty}
                && racePhysics.escape.reversals.allSatisfy{$0<20}
                && minuteCells.dropLast().dropFirst(3).allSatisfy{$0.allSatisfy{$0.count>8}}
                && lateDistance.allSatisfy{$0>100} && lateCells.allSatisfy{$0.count>25}
            let people=dirtWorld.town.residents
            let peopleMoving=people?.walkers.allSatisfy{racePhysics.storm.enabled && $0.index%11 != 0 ? $0.node.isHidden:($0.visits>0 && $0.blocked<20)} ?? false
            let passed=peopleMoving && peakResidentBlocked<20 && (people?.maximumPenetration ?? 1)<0.005 && sustained && hudHidden && cameraLocked && racePhysics.escape.complete && !early && timesFrozen && pauseChecked && distinct && maxPenetration<0.005
            updateOpponents();updateRaceWorld(dt:1.0/60)
            updateCamera(snap:true)
            try saveTownFrame("postrace-town",at:directory)
            let report:[String:Any]=["passed":passed,"peopleMoving":peopleMoving,"peakResidentBlockedSeconds":peakResidentBlocked,"houseEntries":people?.entries ?? 0,"residentVisits":people?.walkers.map{$0.visits} ?? [],"residentBlocked":people?.walkers.map{$0.blocked} ?? [],"roamingSeconds":roamingSeconds,"secondsAfterAllDeparted":secondsAfterAllDeparted,"lateDistance":lateDistance,"lateUniqueCells":lateCells.map{$0.count},"uniqueCellsPerMinute":minuteCells.map{$0.map{$0.count}},"movieFrames":movieFrames,"reversals":racePhysics.escape.reversals,"yields":racePhysics.escape.yields,"tours":racePhysics.escape.tours,"distanceTravelled":travelled,"maximumStationarySeconds":maxStationary,"visibleRobotsPerMinute":visibleWindows.map{Array($0).sorted()},"hudHidden":hudHidden,"sustained":sustained,"cameraLocked":cameraLocked,"complete":racePhysics.escape.complete,"earlyGate":early,"frozenResults":timesFrozen,"pause":pauseChecked,"differentDestinations":distinct,"maximumPenetration":maxPenetration,"gateOpenTime":firstOpen ?? -1,"waypoints":racePhysics.escape.waypoint,"positions":states.map{[$0.x,$0.z]},"routes":racePhysics.escape.routes.map{$0.map{[$0.x,$0.y]}}]
            try JSONSerialization.data(withJSONObject:report,options:[.prettyPrinted,.sortedKeys]).write(to:directory.appendingPathComponent("postrace.json"))
            print(report.filter{$0.key != "routes"})
            reset(nil);return passed && !racePhysics.escape.active && racePhysics.gate.angle==0 && !raceHUD.isHidden && window.toolbar?.isVisible==true
        } catch { print(error);return false }
    }
}
