import AppKit
import SceneKit
import SimulationCore
import simd

extension AppController {
    func checkBinaryRaces(at directory:URL)->Bool {
        do {
            try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
            var passed=true,reports=[[String:Any]]()
            for _ in 0..<2000 {
                let s=BinaryDaylight.random()
                passed = passed && s.directions.allSatisfy { $0.y>0 && abs(simd_length($0)-1)<1e-10 }
                    && s.separationDegrees>1.1 && s.separationDegrees<13
            }
            startDirtTrack();raceHUD.isHidden=true
            let horizonCapture=ProcessInfo.processInfo.environment["MARVIN_HORIZON_CAPTURE"] == "1"
            let times = horizonCapture ? [("sunrise",0.015...0.015),("sunset",0.985...0.985)]
                : [("morning",0.025...0.045),("midday",0.43...0.57),("evening",0.95...0.975)]
            for (name,range) in times {
                reset(nil);dirtIntro=nil;race.countDown(dt:3)
                // Stratified random times exercise the full daylight range.
                let s=BinaryDaylight(fraction:Double.random(in:range),phase:horizonCapture ? 0.35 : Double.random(in:0.8...2.0))
                dirtWorld.sky.apply(s)
                for (i,node) in dirtWorld.sky.suns.enumerated() {
                    let m=node.simdWorldTransform
                    let forward = -SIMD3<Double>(Double(m.columns.2.x),Double(m.columns.2.y),Double(m.columns.2.z))
                    passed = passed && simd_dot(forward,-s.directions[i])>0.99999
                }
                var captured=false
                for frame in 0..<40000 {
                    advanceRacePhysics(DirtOpponent.driveInput(for:simulation),dt:1.0/60,raceDT:1.0/60)
                    if frame==600 {
                        updatePlayerModel();updateOpponents();updateRaceWorld(dt:1.0/60)
                        world.camera.position=SCNVector3(0,38,-33)
                        world.camera.look(at:SCNVector3Zero,up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                        try saveTownFrame("\(name)-race",at:directory)
                        let direction=simd_normalize(s.directions[0]+s.directions[1])
                        let horizontal=simd_normalize(SIMD3(direction.x,0,direction.z))
                        let eye = -horizontal*27+SIMD3<Double>(0,7.5,0)
                        world.camera.position=SCNVector3(eye)
                        let framing = name == "midday" ? direction : simd_normalize(horizontal+SIMD3(0,0.01,0))
                        world.camera.look(at:SCNVector3(eye+framing*80),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                        try saveTownFrame("\(name)-suns",at:directory)
                        captured=true
                    }
                    if ([race]+opponents.map{$0.race}).allSatisfy({$0.finished}) { break }
                }
                let races=[race]+opponents.map{$0.race}
                let finished=races.allSatisfy{$0.finished}
                let stable=dirtWorld.sky.daylight.fraction==s.fraction && dirtWorld.sky.daylight.phase==s.phase
                passed = passed && finished && captured && stable
                let report:[String:Any]=["time":name,"daylightFraction":s.fraction,"binaryPhase":s.phase,"separationDegrees":s.separationDegrees,"elevationsDegrees":s.directions.map{asin($0.y)*180/Double.pi},"allFinished":finished,"laps":races.map{$0.laps.count},"stableDuringRace":stable]
                reports.append(report);print(report);fflush(stdout)
            }
            let previous=dirtWorld.sky.daylight.fraction
            reset(nil)
            passed = passed && previous != dirtWorld.sky.daylight.fraction
            let occlusion=try checkSunOcclusion(at:directory)
            passed = passed && occlusion
            try JSONSerialization.data(withJSONObject:["passed":passed,"daylightSamples":2000,"occlusion":occlusion,"races":reports],options:[.prettyPrinted,.sortedKeys]).write(to:directory.appendingPathComponent("binary-races.json"))
            print("Binary daylight races: \(passed ? "PASS":"FAIL")");return passed
        } catch { print("Binary daylight: \(error)");return false }
    }
    /// Render a sun directly behind an opaque screen. Its glare must disappear,
    /// including the bloom input, rather than being an always-visible HUD sprite.
    private func checkSunOcclusion(at directory:URL)throws->Bool {
        let scene=SCNScene(),camera=SCNNode()
        let sky=BinarySky(scene:scene)
        camera.camera=SCNCamera();camera.camera?.zFar=250;sky.attach(camera:camera)
        scene.rootNode.addChildNode(camera)
        let s=BinaryDaylight(fraction:0.2,phase:1.2);sky.apply(s)
        camera.look(at:SCNVector3(s.directions[0]*20),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
        let screen=SCNNode(geometry:SCNPlane(width:4,height:4))
        screen.position=SCNVector3(s.directions[0]*5);screen.look(at:SCNVector3Zero)
        let material=SCNMaterial();material.lightingModel = .constant;material.diffuse.contents=NSColor.black;material.isDoubleSided=true
        screen.geometry?.materials=[material];scene.rootNode.addChildNode(screen)
        let renderer=SCNRenderer(device:view.device,options:nil);renderer.scene=scene;renderer.pointOfView=camera
        var luminance=[Double]()
        for hidden in [true,false] {
            screen.isHidden=hidden
            let image=renderer.snapshot(atTime:0,with:CGSize(width:256,height:256),antialiasingMode:.multisampling4X)
            let bitmap=NSBitmapImageRep(data:image.tiffRepresentation!)!
            var sum=0.0
            for y in 120..<136 { for x in 120..<136 {
                let c=bitmap.colorAt(x:x,y:y)!.usingColorSpace(.deviceRGB)!
                sum+=Double(c.redComponent+c.greenComponent+c.blueComponent)/3
            }}
            luminance.append(sum/256)
            try bitmap.representation(using:.png,properties:[:])!.write(to:directory.appendingPathComponent(hidden ? "sun-visible.png":"sun-occluded.png"))
        }
        print("Sun occlusion luminance: \(luminance)")
        return luminance[0]>0.6 && luminance[1]<0.05
    }
}
