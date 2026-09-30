import AppKit
import SceneKit
import SimulationCore

/// Render callback cadence is recorded separately from simulation callbacks.
/// This is not a GPU timestamp or a substitute for Instruments presentation data.
final class TownFrameMeter: NSObject, SCNSceneRendererDelegate {
    private let lock = NSLock()
    private var previous: Double?
    private var intervals: [Double] = []
    func renderer(_ renderer: SCNSceneRenderer, didRenderScene scene: SCNScene, atTime time: TimeInterval) {
        let now=ProcessInfo.processInfo.systemUptime
        lock.lock(); defer { lock.unlock() }
        if let previous { intervals.append(now-previous) }
        previous=now
    }
    func reset() { lock.lock();intervals=[];previous=nil;lock.unlock() }
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
            let crowd=dirtWorld.town.root.childNodes.filter{$0.name=="Animated town spectator"}
            let before=crowd.map{$0.simdTransform}
            dirtWorld.town.update(dt:0,camera:world.camera.position,player:.zero)
            let pausePassed=zip(before,crowd).allSatisfy{$0.0==$0.1.simdTransform}
            raceHUD.isHidden=true
            for _ in 0..<90 {
                advanceRacePhysics(DirtOpponent.driveInput(for:simulation),dt:1.0/60,raceDT:1.0/60)
                updateOpponents();updateRaceWorld(dt:1.0/60)
                dirtWorld.town.update(dt:1.0/60,camera:SCNVector3(0,5,-12),player:SIMD2(simulation.x,simulation.z))
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
            let count=dirtWorld.town.statistics
            let children=dirtWorld.town.root.childNodes.count
            reset(nil)
            let resetPassed=dirtWorld.town.statistics==count && dirtWorld.town.root.childNodes.count==children && race.countdown==3
            let report:[String:Any] = ["passed":valid && resetPassed && cameraPassed && pausePassed,"layoutClearancePassed":valid,"cityCoveragePassed":dirtWorld.town.cityCoveragePassed,"streetNetworkPassed":dirtWorld.town.streetNetworkPassed,"resetPassed":resetPassed,"cameraObstructionPassed":cameraPassed,"crowdPausePassed":pausePassed,
                                      "town":count,"images":cameras.map{$0.0} + ["town-racing","town-robot-pov"]]
            try JSONSerialization.data(withJSONObject:report,options:[.prettyPrinted,.sortedKeys]).write(to:directory.appendingPathComponent("town-smoke.json"))
            return valid && resetPassed && cameraPassed && pausePassed
        } catch { print("Town smoke: \(error)");return false }
    }
    func saveTownFrame(_ name:String,at directory:URL) throws {
        guard let tiff=view.snapshot().tiffRepresentation,
              let png=NSBitmapImageRep(data:tiff)?.representation(using:.png,properties:[:]) else { throw CocoaError(.fileWriteUnknown) }
        try png.write(to:directory.appendingPathComponent(name+".png"))
    }
    func startTownBenchmark(at directory:URL) {
        try? FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
        startDirtTrack(); dirtIntro=nil;race.countDown(dt:3)
        raceHUD.isHidden=true
        simulation=Simulation(dirtTrack:true,dirtStartOffset:DirtCourse.startingGrid[0].offset,dirtStartPhase:DirtCourse.startingGrid[0].phase)
        opponent=DirtOpponent(slot:DirtCourse.startingGrid[1])
        bb8Opponent=DirtOpponent(slot:DirtCourse.startingGrid[2],laneOffset:0)
        wallEOpponent=DirtOpponent(slot:DirtCourse.startingGrid[3],laneOffset:-0.65)
        race=DirtRace(startPhase:DirtCourse.startingGrid[0].phase);race.countDown(dt:3)
        racePhysics=DirtRacePhysics(characters:lineup)
        updateOpponents()
        // Explicit 960x540 points at 2x backing gives the target 1080p drawable.
        window.minSize=NSSize(width:640,height:400)
        window.setContentSize(NSSize(width:960,height:540))
        view.delegate=townMeter
        townBenchmarkStart=ProcessInfo.processInfo.systemUptime
        townMeter.reset();townBenchmarkCPU=[]
        townBenchmarkDirectory=directory
        dirtWorld.town.root.isHidden=CommandLine.arguments.contains("--without-town")
    }
    func tickTownBenchmark(now:Double,dt:Double) {
        guard let start=townBenchmarkStart,let directory=townBenchmarkDirectory else { return }
        let elapsed=now-start
        if elapsed<3 { townMeter.reset();townBenchmarkCPU=[] }
        let begin=ProcessInfo.processInfo.systemUptime
        advanceRacePhysics(DirtOpponent.driveInput(for:simulation),dt:dt,raceDT:dt)
        updateOpponents();updateRaceWorld(dt:dt)
        if elapsed.truncatingRemainder(dividingBy:24)>18 {
            world.camera.position=SCNVector3(0,42,-44);world.camera.look(at:SCNVector3(0,0,0),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
        } else { updateCamera(snap:true) }
        if !dirtWorld.town.root.isHidden {
            dirtWorld.town.update(dt:dt,camera:world.camera.position,player:SIMD2(simulation.x,simulation.z))
        }
        townBenchmarkCPU.append(ProcessInfo.processInfo.systemUptime-begin)
        let duration=Double(ProcessInfo.processInfo.environment["MARVIN_BENCHMARK_SECONDS"] ?? "45") ?? 45
        if elapsed>=max(10,duration) {
            timer?.invalidate();view.delegate=nil
            var report=townMeter.report()
            let cpu=townBenchmarkCPU.sorted()
            report["cpuUpdateP95MS"]=cpu.isEmpty ? 0:cpu[Int(Double(cpu.count-1)*0.95)]*1000
            report["durationSeconds"]=elapsed;report["townEnabled"] = !dirtWorld.town.root.isHidden
            report["town"]=dirtWorld.town.statistics
            report["drawableWidth"]=view.convertToBacking(view.bounds).width
            report["drawableHeight"]=view.convertToBacking(view.bounds).height
            report["simulationSeconds"]=race.elapsed;report["laps"]=race.laps.count
            report["thermalState"]=ProcessInfo.processInfo.thermalState.rawValue
            do {
                try JSONSerialization.data(withJSONObject:report,options:[.prettyPrinted,.sortedKeys]).write(to:directory.appendingPathComponent("benchmark.json"))
                print("Town benchmark complete · \(directory)")
                exit(0)
            } catch { print(error);exit(1) }
        }
    }
}
