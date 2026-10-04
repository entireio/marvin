import AppKit
import SceneKit
import SimulationCore
import os

private let townBenchmarkTraceLog=OSLog(subsystem:"io.entire.marvin.performance",category:.pointsOfInterest)

/// Render callback cadence is recorded separately from simulation callbacks.
/// This is not a GPU timestamp or a substitute for Instruments presentation data.
final class TownFrameMeter: NSObject, SCNSceneRendererDelegate {
    weak var fpsHUD:FrameRateHUD?
    private let lock = NSLock()
    private var previous: Double?
    private var intervals: [Double] = []
    private var collecting = false
    private var frameStart=0.0,cycleStart=0.0,animationsEnd=0.0,physicsEnd=0.0,constraintsEnd=0.0
    func renderer(_ renderer:SCNSceneRenderer,didApplyAnimationsAtTime time:TimeInterval) {
        lock.lock();animationsEnd=ProcessInfo.processInfo.systemUptime;lock.unlock()
    }
    func renderer(_ renderer:SCNSceneRenderer,didSimulatePhysicsAtTime time:TimeInterval) {
        lock.lock();physicsEnd=ProcessInfo.processInfo.systemUptime;lock.unlock()
    }
    func renderer(_ renderer:SCNSceneRenderer,didApplyConstraintsAtTime time:TimeInterval) {
        lock.lock();constraintsEnd=ProcessInfo.processInfo.systemUptime;lock.unlock()
    }
    func renderer(_ renderer:SCNSceneRenderer,updateAtTime time:TimeInterval) {
        capture?.beginFrame(renderer,time:time)
        lock.lock();cycleStart=ProcessInfo.processInfo.systemUptime;lock.unlock()
    }
    var capture: BenchmarkGPUCapture?
    private var frames:[[Double]]=[]
    func renderer(_ renderer:SCNSceneRenderer,willRenderScene scene:SCNScene,atTime time:TimeInterval) {
        capture?.willRender(renderer,time:time)
        lock.lock();frameStart=ProcessInfo.processInfo.systemUptime;lock.unlock()
    }
    func renderer(_ renderer: SCNSceneRenderer, didRenderScene scene: SCNScene, atTime time: TimeInterval) {
        capture?.didRender()
        fpsHUD?.renderer(renderer,didRenderScene:scene,atTime:time)
        let now=ProcessInfo.processInfo.systemUptime
        lock.lock(); defer { lock.unlock() }
        guard collecting else { return }
        if let previous { intervals.append(now-previous);frames.append([now,(now-previous)*1000,(now-frameStart)*1000,(now-cycleStart)*1000,(animationsEnd-cycleStart)*1000,(physicsEnd-animationsEnd)*1000,(constraintsEnd-physicsEnd)*1000,(frameStart-constraintsEnd)*1000]) }
        previous=now
    }
    func reset() { lock.lock();collecting=true;intervals=[];frames=[];previous=nil;lock.unlock() }
    func timeline()->[[Double]] { lock.lock();defer { lock.unlock() };return frames }
    func report() -> [String:Any] {
        lock.lock();let values=intervals;lock.unlock()
        let sorted=values.sorted()
        func percentile(_ p:Double)->Double { sorted.isEmpty ? 0 : sorted[min(sorted.count-1,Int(Double(sorted.count-1)*p))]*1000 }
        return ["samples":values.count,"meanFPS":values.isEmpty ? 0 : Double(values.count)/values.reduce(0,+),
                "p50MS":percentile(0.5),"p95MS":percentile(0.95),"p99MS":percentile(0.99),
                "over25MS":values.filter{$0>0.025}.count,"over50MS":values.filter{$0>0.050}.count,
                "metric":"SceneKit didRenderScene wall-clock intervals; not GPU or display presentation timing"]
    }
}

extension AppController {
    func checkTown(at directory:URL) -> Bool {
        do {
            try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
            startDirtTrack(); dirtIntro=nil; race.countDown(dt:3)
            let valid=dirtWorld.town.validate()
            let target=SCNVector3(0,1,-15), blocked=SCNVector3(0,1,-22)
            let safe=dirtWorld.town.clearCamera(from:target,to:blocked)
            let cameraPassed=safe.z>blocked.z+1 && safe.z<target.z
            let crowd=dirtWorld.town.residents?.walkers.map{$0.node} ?? []
            let before=crowd.map{$0.simdTransform}
            dirtWorld.town.update(dt:0,camera:world.camera.position,player:.zero)
            let pausePassed=zip(before,crowd).allSatisfy{$0.0==$0.1.simdTransform}
            raceHUD.isHidden=true
            for _ in 0..<90 {
                advanceRacePhysics(DirtOpponent.driveInput(for:simulation),dt:1.0/60,raceDT:1.0/60)
                updateOpponents();updateRaceWorld(dt:1.0/60)
                dirtWorld.town.update(dt:1.0/60,camera:SCNVector3(0,5,-12),player:SIMD2(simulation.x,simulation.z),robots:([simulation]+opponents.map{$0.simulation}).enumerated().map { i,s in
                RobotCollisions.Body(position:SIMD3(s.x,s.groundY,s.z),heading:s.heading,profile:RobotCollisions.profiles[lineup[i].rawValue])
            },visible:{ self.view.isNode($0,insideFrustumOf:self.world.camera) },shadowCamera:world.camera,viewportAspect:Double(view.bounds.width/view.bounds.height))
            }
            let cameras:[(String,SCNVector3,SCNVector3)] = [
                ("town-overview",SCNVector3(0,46,-52),SCNVector3(0,0,0)),
                ("town-grandstand",SCNVector3(5,4.8,-10),SCNVector3(0,1.9,-20.5)),
                ("town-citizens",SCNVector3(-6.0,1.35,-28.1),SCNVector3(-6.7,0.65,-25.4)),
                ("town-spectators",SCNVector3(3.3,2.5,DirtCourse.point(0).z-1.5),SCNVector3(3,2.03,DirtCourse.point(0).z-4.18)),
                ("town-market",SCNVector3(-14,2.6,-27.8),SCNVector3(0,1.3,-24.5)),
                ("town-ramp-ground",SCNVector3(-7.5,0.58,-8.9),SCNVector3(-7.5,0.26,-12.3)),
                ("town-ramp-side",SCNVector3(-10.0,0.85,-10.0),SCNVector3(-7.5,0.20,-11.5)),
                ("town-service-access",SCNVector3(-2,5.8,-18),SCNVector3(-7.5,0.15,-10.8)),
                ("town-repair",SCNVector3(-3.6,2.9,-13.0),SCNVector3(-7.5,0.95,-7.5)),
                ("town-outskirts",SCNVector3(-46,13,-26),SCNVector3(-21,3,-3)),
                ("town-spaceport",SCNVector3(17,9,0),SCNVector3(34,1.5,13)),
                ("town-skyline",SCNVector3(-10,13,19),SCNVector3(10,5,43)),
                ("town-game-overview",SCNVector3(0,38,-33),SCNVector3(0,0,0))]
            for (name,eye,target) in cameras {
                world.camera.position=eye;world.camera.look(at:target,up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                _=view.prepare(dirtWorld.scene,shouldAbortBlock:nil)
                try saveTownFrame(name,at:directory)
            }
            updateCamera(snap:true)
            try saveTownFrame("town-racing",at:directory)
            // Inspect from just ahead of Marvin's head, at its actual eye height.
            let forward=SIMD2(sin(simulation.heading),cos(simulation.heading))
            let eye=SCNVector3(simulation.x+forward.x*0.34,simulation.groundY+0.46,simulation.z+forward.y*0.34)
            world.camera.position=eye
            world.camera.look(at:SCNVector3(simulation.x+forward.x*12,simulation.groundY+0.46,simulation.z+forward.y*12),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
            try saveTownFrame("town-robot-pov",at:directory)
            var detailsPassed=true
            for fraction in [0.25,0.60,0.90] {
                let p=dirtWorld.town.explorationSurveyPoint(fraction),ahead=dirtWorld.town.explorationSurveyPoint(fraction+0.03)
                world.camera.position=SCNVector3(p.x,1.5,p.y)
                world.camera.look(at:SCNVector3(ahead.x,1.5,ahead.y),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                for enabled in [false,true] {
                    dirtWorld.town.explorationDetailEnabled=enabled
                    dirtWorld.town.updateExplorationDetail(camera:world.camera.position,player:p)
                    detailsPassed = detailsPassed && (enabled ? dirtWorld.town.activeExplorationCells>0:dirtWorld.town.activeExplorationCells==0)
                    try saveTownFrame("outer-\(Int(fraction*100))-\(enabled ? "detailed":"simple")",at:directory)
                }
            }
            dirtWorld.town.updateExplorationDetail(camera:SCNVector3(0,38,-33),player:SIMD2(90,45))
            detailsPassed = detailsPassed && dirtWorld.town.activeExplorationCells==0
            dirtWorld.town.updateExplorationDetail(camera:SCNVector3(25,2,-10),player:.zero)
            detailsPassed = detailsPassed && dirtWorld.town.activeExplorationCells==0
            let count=dirtWorld.town.statistics
            let children=dirtWorld.town.root.childNodes.count
            reset(nil)
            let resetPassed=dirtWorld.town.statistics==count && dirtWorld.town.root.childNodes.count==children && race.countdown==3
            let report:[String:Any] = ["passed":valid && resetPassed && cameraPassed && pausePassed && detailsPassed,"explorationDetailPassed":detailsPassed,"layoutClearancePassed":valid,"cityCoveragePassed":dirtWorld.town.cityCoveragePassed,"streetNetworkPassed":dirtWorld.town.streetNetworkPassed,"resetPassed":resetPassed,"cameraObstructionPassed":cameraPassed,"crowdPausePassed":pausePassed,
                                      "town":count,"images":cameras.map{$0.0} + ["town-racing","town-robot-pov"]]
            try JSONSerialization.data(withJSONObject:report,options:[.prettyPrinted,.sortedKeys]).write(to:directory.appendingPathComponent("town-smoke.json"))
            return valid && resetPassed && cameraPassed && pausePassed && detailsPassed
        } catch { print("Town smoke: \(error)");return false }
    }
    func saveTownFrame(_ name:String,at directory:URL) throws {
        guard let tiff=view.snapshot().tiffRepresentation,
              let bitmap=NSBitmapImageRep(data:tiff),
              let png=bitmap.representation(using:.png,properties:[:]) else { throw CocoaError(.fileWriteUnknown) }
        try png.write(to:directory.appendingPathComponent(name+".png"))
        var magenta=0
        for y in stride(from:0,to:bitmap.pixelsHigh,by:8) {
            for x in stride(from:0,to:bitmap.pixelsWide,by:8) {
                if let c=bitmap.colorAt(x:x,y:y)?.usingColorSpace(.deviceRGB),c.redComponent>0.9,c.blueComponent>0.9,c.greenComponent<0.15 { magenta += 1 }
            }
        }
        guard magenta<20 else { throw NSError(domain:"RenderAudit",code:1,userInfo:[NSLocalizedDescriptionKey:"Shader failure colour in \(name): \(magenta) sampled pixels"]) }
    }
    func startTownBenchmark(at directory:URL) {
        weatherOverride=ProcessInfo.processInfo.environment["MARVIN_SANDSTORM"]=="1"
        try? FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
        startDirtTrack(); dirtIntro=nil;race.countDown(dt:3)
        if let text=ProcessInfo.processInfo.environment["MARVIN_DAYLIGHT_FRACTION"],let fraction=Double(text),fraction.isFinite {
            dirtWorld.sky.apply(BinaryDaylight(fraction:fraction,phase:1.2))
        }
        raceHUD.isHidden=true
        simulation=Simulation(dirtTrack:true,dirtStartOffset:DirtCourse.startingGrid[0].offset,dirtStartPhase:DirtCourse.startingGrid[0].phase)
        opponent=DirtOpponent(slot:DirtCourse.startingGrid[1])
        bb8Opponent=DirtOpponent(slot:DirtCourse.startingGrid[2],laneOffset:0)
        wallEOpponent=DirtOpponent(slot:DirtCourse.startingGrid[3],laneOffset:-0.65)
        race=DirtRace(startPhase:DirtCourse.startingGrid[0].phase);race.countDown(dt:3)
        racePhysics=DirtRacePhysics(characters:lineup,townRoutes:dirtWorld.escapeRoutes)
        racePhysics.storm=Sandstorm(enabled:weatherOverride ?? false)
        if CommandLine.arguments.contains("--city-roam") {
            simulation=Simulation(dirtTrack:true,dirtStartOffset:DirtCourse.fenceOffset+8.08,dirtStartPhase:CityExit.phase)
            racePhysics.gate.wantsOpen=true
            var current=SIMD2(simulation.x,simulation.z)
            let planner=TownEscapeRoute(city:dirtWorld.town.collisionWorld,origin:current)
            for destination in [SIMD2<Double>(29,-10),SIMD2(33,3),SIMD2(35,14),SIMD2(36,28)] {
                if let route=planner.route(from:current,to:destination) { townBenchmarkRoute += route;current=route.last! }
            }
            townBenchmarkRoute += townBenchmarkRoute.reversed()
        }
        if CommandLine.arguments.contains("--dune-roam") {
            let p=DirtCourse.projection(x:175,z:57)
            simulation=Simulation(dirtTrack:true,dirtStartOffset:p.distance,dirtStartPhase:p.phase)
            townBenchmarkRoute=[SIMD2(205,65),SIMD2(178,57)]
        }
        if CommandLine.arguments.contains("--postrace-roam") {
            var roamingFrames=0
            for _ in 0..<54000 {
                advanceRacePhysics(DirtOpponent.driveInput(for:simulation),dt:1.0/60,raceDT:1.0/60)
                if racePhysics.escape.complete { roamingFrames += 1 }
                if roamingFrames>=60*120 { break }
            }
            precondition(racePhysics.escape.complete,"Post-race benchmark did not reach town roaming")
            updateDepartureHUD()
        }
        dirtWorld.town.explorationDetailEnabled = !CommandLine.arguments.contains("--benchmark-simple-town")
        updateOpponents()
        // Explicit 960x540 points at 2x backing gives the target 1080p drawable.
        window.minSize=NSSize(width:640,height:400)
        window.setContentSize(NSSize(width:960,height:540))
        if CommandLine.arguments.contains("--benchmark-msaa2") { view.antialiasingMode = .multisampling2X }
        if CommandLine.arguments.contains("--benchmark-no-shadows") {
            dirtWorld.scene.rootNode.enumerateChildNodes { node,_ in node.light?.castsShadow=false }
        }
        if CommandLine.arguments.contains("--benchmark-no-ssao") { world.camera.camera?.screenSpaceAmbientOcclusionIntensity=0 }
        if CommandLine.arguments.contains("--benchmark-no-bloom") { world.camera.camera?.bloomIntensity=0 }
        if CommandLine.arguments.contains("--benchmark-flat-ground") {
            dirtWorld.scene.rootNode.enumerateChildNodes { node,_ in
                for material in node.geometry?.materials ?? [] where (material.shaderModifiers ?? [:]).values.contains(where:{$0.contains("townNoise")}) {
                    material.shaderModifiers=nil;material.lightingModel = .constant
                }
            }
        }
        benchmarkSessionState.start(window:window)
        view.delegate=townMeter
        townBenchmarkStart=ProcessInfo.processInfo.systemUptime
        os_signpost(.event,log:townBenchmarkTraceLog,name:"TownBenchmarkStart","run=%{public}@ markerUptime=%.9f benchmarkBoundaryUptime=%.9f",townBenchmarkRunID as NSString,ProcessInfo.processInfo.systemUptime,townBenchmarkStart!)
        let identity:[String:Any]=["runID":townBenchmarkRunID,"startUptime":townBenchmarkStart!,"gpuDevice":view.device?.name ?? "Unavailable","resolution":[view.convertToBacking(view.bounds).width,view.convertToBacking(view.bounds).height]]
        if let data=try? JSONSerialization.data(withJSONObject:identity,options:[.sortedKeys]),let line=String(data:data,encoding:.utf8) {
            FileHandle.standardOutput.write(Data(("MARVIN_BENCHMARK_ID "+line+"\n").utf8))
        }
        if CommandLine.arguments.contains("--benchmark-gpu-capture") {
            benchmarkGPUCapture.configure(start:townBenchmarkStart!,runID:townBenchmarkRunID,
                player:modelRoot(playerCharacter),viewport:view.bounds.size,directory:directory)
            townMeter.capture = benchmarkGPUCapture
        }
        frameRateHUD.resetSamples();frameRateHUD.isHidden=false
        townMeter.fpsHUD=frameRateHUD;townBenchmarkHUDSamples=[]
        frameRateHUD.onSample={ [weak self] now,fps in
            guard let self,let start=self.townBenchmarkStart,now-start>=3.5 else { return }
            self.townBenchmarkHUDSamples.append(["elapsedSeconds":now-start,"fps":fps.map { $0 as Any } ?? NSNull(),"text":self.frameRateHUD.displayedText])
            if self.townBenchmarkHUDSamples.count % 120 == 0 {
                let status=String(format:"Town drive %.0f s · %@\n",now-start,self.frameRateHUD.displayedText)
                FileHandle.standardOutput.write(Data(status.utf8))
            }
        }
        townMeter.reset();townBenchmarkCPU=[]
        townBenchmarkDirectory=directory
        dirtWorld.town.root.isHidden=CommandLine.arguments.contains("--without-town")
    }
    func tickTownBenchmark(now:Double,dt:Double) {
        guard let start=townBenchmarkStart,let directory=townBenchmarkDirectory else { return }
        let elapsed=now-start
        if elapsed<3 { townMeter.reset();townBenchmarkCPU=[];townBenchmarkTimeline=[] }
        let begin=ProcessInfo.processInfo.systemUptime
        let isolateTrails=CommandLine.arguments.contains("--benchmark-isolate-trails")
        let trailsHidden=isolateTrails && ((elapsed>=300 && elapsed<330) || (elapsed>=480 && elapsed<510))
        if isolateTrails { dirtWorld.setBenchmarkTrailsHidden(trailsHidden) }
        if Int(elapsed)>=(townBenchmarkResourceSamples.last?["second"] as? Int ?? -1)+1 {
            // Instruments can miss launch-time events or retain only late POIs.
            // Refresh every 30 seconds in instrumented runs, preserving the
            // original benchmark boundary while recording each emission uptime.
            if ProcessInfo.processInfo.environment["MARVIN_BENCHMARK_POI_REFRESH"] == "1",
               elapsed >= 10,
               ((townBenchmarkResourceSamples.last?["second"] as? Int ?? -1) < 10 ||
                Int(elapsed)/30 > (townBenchmarkResourceSamples.last?["second"] as? Int ?? -1)/30) {
                os_signpost(.event,log:townBenchmarkTraceLog,name:"TownBenchmarkStart","run=%{public}@ markerUptime=%.9f benchmarkBoundaryUptime=%.9f",townBenchmarkRunID as NSString,ProcessInfo.processInfo.systemUptime,start)
            }
            townBenchmarkResourceSamples.append(["second":Int(elapsed),"uptime":now,"thermalState":ProcessInfo.processInfo.thermalState.rawValue,"trailsHidden":trailsHidden,"shadowCasters":dirtWorld.town.shadowCasterCount,"trails":dirtWorld.trailDiagnostics(),"shadowBatch":dirtWorld.town.shadowBatchTelemetry])
            if ProcessInfo.processInfo.environment["MARVIN_BENCHMARK_VISIBILITY"] == "1" {
                var visibility: [String: Any] = [
                    "uptime": ProcessInfo.processInfo.systemUptime,
                    "appActive": NSApp.isActive,
                    "windowVisible": window.isVisible,
                    "windowOcclusionVisible": window.occlusionState.contains(.visible),
                    "windowMiniaturized": window.isMiniaturized,
                    "windowFrame": NSStringFromRect(window.frame),
                    "screenName": window.screen?.localizedName ?? "UNKNOWN",
                    "screenFrame": window.screen.map { NSStringFromRect($0.frame) } ?? "UNKNOWN",
                    "backingScale": window.backingScaleFactor
                ]
                // Only retain the lock field, never the session dictionary.
                // Absence is UNKNOWN, not proof that the screen is unlocked.
                let session = CGSessionCopyCurrentDictionary() as? [String: Any]
                visibility["sessionLockFlag"] = (session?["CGSSessionScreenIsLocked"] as? Bool).map { $0 as Any } ?? NSNull()
                townBenchmarkResourceSamples[townBenchmarkResourceSamples.count - 1]["visibility"] = visibility
            }
        }
        var input=DirtOpponent.driveInput(for:simulation)
        if !townBenchmarkRoute.isEmpty {
            var target=townBenchmarkRoute[townBenchmarkWaypoint]
            if hypot(target.x-simulation.x,target.y-simulation.z)<0.24 {
                townBenchmarkWaypoint=(townBenchmarkWaypoint+1)%townBenchmarkRoute.count
                target=townBenchmarkRoute[townBenchmarkWaypoint]
            }
            let error=atan2(sin(atan2(target.x-simulation.x,target.y-simulation.z)-simulation.heading),cos(atan2(target.x-simulation.x,target.y-simulation.z)-simulation.heading))
            input=DriveInput();input.turn=max(-1,min(1,-error*2.5));input.throttle=abs(error)<0.22 ? 0.5:0
        }
        advanceRacePhysics(input,dt:dt,raceDT:dt)
        let physicsEnd=ProcessInfo.processInfo.systemUptime
        updateOpponents()
        let modelsEnd=ProcessInfo.processInfo.systemUptime
        updateRaceWorld(dt:dt)
        let effectsEnd=ProcessInfo.processInfo.systemUptime
        let aerial = !raceCameraLocked && townBenchmarkRoute.isEmpty && !CommandLine.arguments.contains("--benchmark-chase-only") && elapsed.truncatingRemainder(dividingBy:24)>18
        if aerial {
            world.camera.position=SCNVector3(0,42,-44);world.camera.look(at:SCNVector3(0,0,0),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
        } else { updateCamera(snap:false,dt:dt) }
        var detailPlayer=SIMD2(simulation.x,simulation.z)
        if CommandLine.arguments.contains("--outer-town-survey") {
            let fraction=0.10+0.85*min(1,elapsed/45)
            detailPlayer=dirtWorld.town.explorationSurveyPoint(fraction)
            dirtWorld.sky.updateShadowCenter(SIMD3(detailPlayer.x,0,detailPlayer.y))
            let ahead=dirtWorld.town.explorationSurveyPoint(min(1,fraction+0.025))
            world.camera.position=SCNVector3(detailPlayer.x,1.5,detailPlayer.y)
            world.camera.look(at:SCNVector3(ahead.x,1.5,ahead.y),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
        }
        let cameraEnd=ProcessInfo.processInfo.systemUptime
        if !dirtWorld.town.root.isHidden {
            dirtWorld.town.update(dt:dt,camera:world.camera.position,player:detailPlayer,robots:([simulation]+opponents.map{$0.simulation}).enumerated().map { i,s in
                RobotCollisions.Body(position:SIMD3(s.x,s.groundY,s.z),heading:s.heading,profile:RobotCollisions.profiles[lineup[i].rawValue])
            },visible:{ self.view.isNode($0,insideFrustumOf:self.world.camera) },shadowCamera:world.camera,viewportAspect:Double(view.bounds.width/view.bounds.height))
        }
        // townMS includes this main-thread audio update as well as town work.
        // physicsMS starts before benchmark/input setup: both are wall spans.
        updateRaceAudio(dt:dt,advancing:true)
        benchmarkGPUCapture.update(waypoint:townBenchmarkWaypoint)
        let finish=ProcessInfo.processInfo.systemUptime
        townBenchmarkCPU.append(finish-begin)
        townBenchmarkTimeline.append([now,elapsed,dt*1000,(physicsEnd-begin)*1000,(modelsEnd-physicsEnd)*1000,(effectsEnd-modelsEnd)*1000,(cameraEnd-effectsEnd)*1000,(finish-cameraEnd)*1000,(finish-begin)*1000,simulation.x,simulation.z,aerial ? 1:0])
        let duration=Double(ProcessInfo.processInfo.environment["MARVIN_BENCHMARK_SECONDS"] ?? "45") ?? 45
        if elapsed>=max(10,duration) {
            os_signpost(.event,log:townBenchmarkTraceLog,name:"TownBenchmarkEnd","run=%{public}@ markerUptime=%.9f benchmarkBoundaryUptime=%.9f",townBenchmarkRunID as NSString,ProcessInfo.processInfo.systemUptime,now)
            timer?.invalidate();view.delegate=nil
            if #available(macOS 14.0,*) { (frameDisplayLink as? CADisplayLink)?.invalidate() }
            var report=townMeter.report()
            let cpu=townBenchmarkCPU.sorted()
            report["cpuUpdateP95MS"]=cpu.isEmpty ? 0:cpu[Int(Double(cpu.count-1)*0.95)]*1000
            report["durationSeconds"]=elapsed;report["townEnabled"] = !dirtWorld.town.root.isHidden
            report["postRaceRoaming"]=racePhysics.escape.active
            report["town"]=dirtWorld.town.statistics
            report["activeExplorationCells"]=dirtWorld.town.activeExplorationCells
            report["sandstorm"]=racePhysics.storm.enabled
            report["visiblePeople"]=dirtWorld.town.visiblePopulation
            report["residentUpdates"]=dirtWorld.town.residents?.updateStatistics ?? [:]
            report["audioActive"]=raceAudio?.active ?? false
            report["updateDriver"]=frameDisplayLink == nil ? "timer":"display-link"
            report["expressionsPlayed"]=raceAudio?.expressionCount ?? 0
            report["metalRenderer"]=view.renderingAPI == .metal
            report["gpuDevice"]=view.device?.name ?? "Unavailable"
            report["daylightFraction"]=dirtWorld.sky.daylight.fraction
            report["sunElevationsDegrees"]=dirtWorld.sky.daylight.directions.map{asin($0.y)*180/Double.pi}
            report["drawableWidth"]=view.convertToBacking(view.bounds).width
            report["drawableHeight"]=view.convertToBacking(view.bounds).height
            report["simulationSeconds"]=race.elapsed;report["laps"]=race.laps.count
            var shadowWidths:[Int]=[]
            dirtWorld.scene.rootNode.enumerateChildNodes { node,_ in
                if let light=node.light,light.castsShadow { shadowWidths.append(Int(light.shadowMapSize.width)) }
            }
            report["quality"]=["msaaSamples":view.antialiasingMode == .none ? 1:(1 << view.antialiasingMode.rawValue),"shadowMapWidths":shadowWidths.sorted(),"explorationDetail":dirtWorld.town.explorationDetailEnabled]
            report["ambientOcclusion"]=["intensity":world.camera.camera?.screenSpaceAmbientOcclusionIntensity ?? 0,"radius":world.camera.camera?.screenSpaceAmbientOcclusionRadius ?? 0,"bias":world.camera.camera?.screenSpaceAmbientOcclusionBias ?? 0]
            report["startSignpostRefreshEnabled"]=ProcessInfo.processInfo.environment["MARVIN_BENCHMARK_POI_REFRESH"] == "1"
            report["startSignpostRefreshPeriodSeconds"]=30
            report["shadowBatch"]=dirtWorld.town.shadowBatchTelemetry
            report["thermalState"]=ProcessInfo.processInfo.thermalState.rawValue
            report["benchmarkArguments"]=CommandLine.arguments
            report["benchmarkRunID"]=townBenchmarkRunID
            report["startUptime"]=start
            report["endWallTime"]=Date().timeIntervalSince1970
            do {
                let timeline:[String:Any]=["renderColumns":["uptime","intervalMS","renderCallbackSpanMS","rendererCycleMS","sceneAnimationMS","scenePhysicsMS","sceneConstraintsMS","preRenderMS"],"renderFrames":townMeter.timeline(),"updateColumns":["uptime","elapsed","tickMS","physicsMS","modelsMS","effectsMS","cameraMS","townMS","totalMS","x","z","aerial"],"updates":townBenchmarkTimeline]
                try JSONSerialization.data(withJSONObject:timeline).write(to:directory.appendingPathComponent("timeline.json"))
                try JSONSerialization.data(withJSONObject:report,options:[.prettyPrinted,.sortedKeys]).write(to:directory.appendingPathComponent("benchmark.json"))
                try JSONSerialization.data(withJSONObject:townBenchmarkResourceSamples,options:[.prettyPrinted,.sortedKeys]).write(to:directory.appendingPathComponent("resources.json"))
                if ProcessInfo.processInfo.environment["MARVIN_BENCHMARK_VISIBILITY"] == "1" {
                    try JSONSerialization.data(withJSONObject:benchmarkSessionState.report(),options:[.prettyPrinted,.sortedKeys])
                        .write(to:directory.appendingPathComponent("session-events.json"))
                }
                try JSONSerialization.data(withJSONObject:townBenchmarkHUDSamples,options:[.prettyPrinted,.sortedKeys]).write(to:directory.appendingPathComponent("displayed-fps.json"))
                // SCNView snapshots omit AppKit overlays; draw the actual live
                // counter over the native scene at its unchanged view position.
                let image=NSImage(size:view.bounds.size);image.lockFocus()
                view.snapshot().draw(in:view.bounds)
                let transform=NSAffineTransform();transform.translateX(by:frameRateHUD.frame.minX,yBy:frameRateHUD.frame.minY)
                transform.concat();frameRateHUD.draw(frameRateHUD.bounds);image.unlockFocus()
                if let tiff=image.tiffRepresentation,let bitmap=NSBitmapImageRep(data:tiff),let png=bitmap.representation(using:.png,properties:[:]) {
                    try png.write(to:directory.appendingPathComponent("final-fps.png"))
                }
                if CommandLine.arguments.contains("--benchmark-marvin-visibility-audit") { try auditMarvinVisibility(at:directory) }
                if CommandLine.arguments.contains("--benchmark-visibility-audit") { try auditTownVisibility(at:directory) }
                if CommandLine.arguments.contains("--benchmark-gpu-probe") { try profileLoadedGPU(at:directory) }
                print("Town benchmark complete · \(directory)")
                exit(0)
            } catch { print(error);exit(1) }
        }
    }
}
