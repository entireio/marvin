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
            let bufferComparison=CommandLine.arguments.contains("--benchmark-debris-buffer-comparison")
            var bufferRows:[[String:Any]]=[]
            let cases:[(String,SIMD2<Double>,Bool)]=[("clay",SIMD2(0,-15),false),("town",SIMD2(90,40),false),("dunes",SIMD2(210,65),false)]
                + (bufferComparison ? [("storm-clay",SIMD2(0,-15),true),("storm-town",SIMD2(90,40),true),("storm-dunes",SIMD2(210,65),true)]:[])
            for (name,point,storm) in cases {
                weatherOverride=storm;startDirtTrack();dirtIntro=nil;raceHUD.isHidden=true
                dirtWorld.sky.apply(BinaryDaylight(fraction:0.5,phase:1.2))
                dirtWorld.reset()
                let p=DirtCourse.projection(x:point.x,z:point.y)
                simulation = name.hasSuffix("clay") ? Simulation(dirtTrack:true):Simulation(dirtTrack:true,dirtStartOffset:p.offset,dirtStartPhase:p.phase)
                var physics=racePhysics,localRace=DirtRace();localRace.countDown(dt:3)
                var rivals=DirtCourse.startingGrid.dropFirst().map{DirtOpponent(slot:$0)}
                var input=DriveInput();input.throttle=0.7
                for _ in 0..<180 {
                    physics.sand=dirtWorld.duneSand.field
                    physics.advance(input,player:&simulation,race:&localRace,opponents:&rivals,dt:1.0/60,raceDT:1.0/60,robotCollisionsEnabled:false)
                    updateOpponents();updateRaceWorld(dt:1.0/60)
                }
                let target=SIMD3(simulation.x,simulation.groundY+0.2,simulation.z)
                let forward=SIMD3(sin(simulation.heading),0,cos(simulation.heading)),side=SIMD3(cos(simulation.heading),0,-sin(simulation.heading))
                let eye=target-forward*3+side*2+SIMD3(0,1.2,0)
                world.camera.position=SCNVector3(eye.x,eye.y,eye.z)
                world.camera.look(at:SCNVector3(target.x,target.y,target.z),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                let renderer=SCNRenderer(device:view.device,options:nil);renderer.scene=dirtWorld.scene;renderer.pointOfView=world.camera
                if bufferComparison {
                    var pair:[NSBitmapImageRep]=[]
                    let buildsBefore=dirtWorld.debrisMetalBuildCount
                    let fallbacksBefore=dirtWorld.debrisMetalFallbackCount
                    for (label,optimized,explicitBounds) in [("reference",false,false),("reference-bounds",false,true),("metal",true,true)] {
                        dirtWorld.diagnosticExplicitDebrisBounds=explicitBounds
                        dirtWorld.debrisMetalBuffersEnabled=optimized;dirtWorld.diagnosticDust(true)
                        dirtWorld.printDebrisBounds(name+"-"+label)
                        _=renderer.prepare(dirtWorld.scene,shouldAbortBlock:nil)
                        let image=renderer.snapshot(atTime:0,with:CGSize(width:1920,height:1080),antialiasingMode:.multisampling2X)
                        let bitmap=NSBitmapImageRep(data:image.tiffRepresentation!)!
                        if label != "reference-bounds" { pair.append(bitmap) }
                        try bitmap.representation(using:.png,properties:[:])!.write(to:directory.appendingPathComponent("\(name)-buffers-\(label).png"))
                        let repeated=renderer.snapshot(atTime:0,with:CGSize(width:1920,height:1080),antialiasingMode:.multisampling2X)
                        let repeatedBitmap=NSBitmapImageRep(data:repeated.tiffRepresentation!)!
                        try repeatedBitmap.representation(using:.png,properties:[:])!.write(to:directory.appendingPathComponent("\(name)-buffers-\(label)-repeat.png"))
                    }
                    let a=pair[0],b=pair[1]
                    guard a.bitsPerSample==8,b.bitsPerSample==8,a.samplesPerPixel==4,b.samplesPerPixel==4,
                          !a.isPlanar,!b.isPlanar,a.bitmapFormat==b.bitmapFormat,
                          let ap=a.bitmapData,let bp=b.bitmapData else { throw NSError(domain:"DebrisComparison",code:1) }
                    var maximum=0,changed=0,above5=0
                    for y in 0..<1080 { for x in 0..<1920 {
                        var error=0
                        for c in 0..<4 { error=max(error,abs(Int(ap[y*a.bytesPerRow+x*4+c])-Int(bp[y*b.bytesPerRow+x*4+c]))) }
                        maximum=max(maximum,error);if error>0 { changed += 1 };if error>5 { above5 += 1 }
                    }}
                    let ok=above5<20 && dirtWorld.debrisMetalBuildCount>buildsBefore && dirtWorld.debrisMetalFallbackCount==fallbacksBefore
                    passed = passed && ok
                    bufferRows.append(["view":name,"maximumChannelError":maximum,"changedPixels":changed,"pixelsAbove5":above5,"passed":ok,"liveDust":dirtWorld.diagnosticDustCount])
                }
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
                passed = passed && dirtWorld.diagnosticDustCount>0 && changed>0 && maximum>2.0/255
            }
            if bufferComparison {
                try JSONSerialization.data(withJSONObject:["passed":passed,"comparisons":bufferRows],options:[.sortedKeys,.prettyPrinted]).write(to:directory.appendingPathComponent("buffer-comparison.json"))
            }
            return passed
        } catch { print(error);return false }
    }
}
