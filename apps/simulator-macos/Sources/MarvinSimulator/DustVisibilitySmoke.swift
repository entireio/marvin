import AppKit
import SceneKit
import SimulationCore
import simd

extension AppController {
    func checkDustVisibility(at directory:URL)->Bool {
        do {
            try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
            weatherOverride=false;startDirtTrack();dirtIntro=nil;raceHUD.isHidden=true
            dirtWorld.sky.apply(BinaryDaylight(fraction:0.5,phase:1.2))
            var passed=true
            for (name,point) in [("clay",SIMD2<Double>(0,-15)),("town",SIMD2(90,40)),("dunes",SIMD2(210,65))] {
                dirtWorld.reset()
                let p=DirtCourse.projection(x:point.x,z:point.y)
                simulation = name == "clay" ? Simulation(dirtTrack:true):Simulation(dirtTrack:true,dirtStartOffset:p.offset,dirtStartPhase:p.phase)
                var physics=DirtRacePhysics(),localRace=DirtRace();localRace.countDown(dt:3)
                var rivals=DirtCourse.startingGrid.dropFirst().map{DirtOpponent(slot:$0)}
                var input=DriveInput();input.throttle=0.7
                for _ in 0..<90 {
                    physics.advance(input,player:&simulation,race:&localRace,opponents:&rivals,dt:1.0/60,raceDT:1.0/60,robotCollisionsEnabled:false)
                    updateOpponents();updateRaceWorld(dt:1.0/60)
                }
                let target=SIMD3(simulation.x,simulation.groundY+0.2,simulation.z)
                let forward=SIMD3(sin(simulation.heading),0,cos(simulation.heading)),side=SIMD3(cos(simulation.heading),0,-sin(simulation.heading))
                let eye=target-forward*3+side*2+SIMD3(0,1.2,0)
                world.camera.position=SCNVector3(eye.x,eye.y,eye.z)
                world.camera.look(at:SCNVector3(target.x,target.y,target.z),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                let renderer=SCNRenderer(device:view.device,options:nil);renderer.scene=dirtWorld.scene;renderer.pointOfView=world.camera
                var images=[NSBitmapImageRep]()
                for visible in [false,true] {
                    dirtWorld.diagnosticDust(visible)
                    let image=renderer.snapshot(atTime:0,with:CGSize(width:1280,height:800),antialiasingMode:.multisampling4X)
                    let bitmap=NSBitmapImageRep(data:image.tiffRepresentation!)!;images.append(bitmap)
                    try bitmap.representation(using:.png,properties:[:])!.write(to:directory.appendingPathComponent("\(name)-dust-\(visible ? "on":"off").png"))
                }
                var changed=0,maximum=0.0,total=0.0
                for y in 0..<800 { for x in 0..<1280 {
                    let a=images[0].colorAt(x:x,y:y)!.usingColorSpace(.deviceRGB)!,b=images[1].colorAt(x:x,y:y)!.usingColorSpace(.deviceRGB)!
                    let d=Double(max(abs(a.redComponent-b.redComponent),abs(a.greenComponent-b.greenComponent),abs(a.blueComponent-b.blueComponent)))
                    if d>2.0/255 { changed += 1;total += d };maximum=max(maximum,d)
                }}
                dirtWorld.printDustOpacity()
                print("Dust \(name): live \(dirtWorld.diagnosticDustCount), speed \(simulation.speed), pixels >2/255 \(changed), max contrast \(maximum), mean changed \(total/Double(max(1,changed)))")
                passed = passed && dirtWorld.diagnosticDustCount>0
            }
            return passed
        } catch { print(error);return false }
    }
}
