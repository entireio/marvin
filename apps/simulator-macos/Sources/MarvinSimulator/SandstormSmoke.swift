import AppKit
import SceneKit
import SimulationCore
import simd

extension AppController {
    func checkSandstorm(at directory:URL)->Bool {
        do {
            try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
            weatherOverride=true
            defer { weatherOverride=nil }
            startDirtTrack();dirtIntro=nil;race.countDown(dt:3);raceHUD.isHidden=true
            dirtWorld.sky.apply(BinaryDaylight(fraction:0.55,phase:1.2))
            let population=dirtWorld.town.visiblePopulation
            let movie=ProcessInfo.processInfo.environment["MARVIN_STORM_MOVIE"]=="1"
            let renderer=SCNRenderer(device:view.device,options:nil)
            renderer.scene=dirtWorld.scene;renderer.pointOfView=world.camera
            var floorError=0.0,captured=false,maximumDepth=0.0
            for frame in 0..<54000 {
                advanceRacePhysics(DirtOpponent.driveInput(for:simulation),dt:1.0/60,raceDT:1.0/60)
                let states=[simulation]+opponents.map{$0.simulation}
                for state in states {
                    floorError=max(floorError,state.terrainHeight(x:state.x,z:state.z)-state.groundY)
                    maximumDepth=max(maximumDepth,state.storm.depth(x:state.x,z:state.z))
                }
                // Update effects at 30 Hz during this offline physics test.
                if frame%2==0 { updateCamera(snap:false);updatePlayerModel();updateOpponents();updateRaceWorld(dt:1.0/30) }
                if movie && frame>=1800 && frame<2700 && frame%2==0 {
                    cameraMode=0;updateCamera(snap:false)
                    try autoreleasepool {
                        let image=renderer.snapshot(atTime:Double(frame)/60,with:CGSize(width:1280,height:720),antialiasingMode:.multisampling4X)
                        guard let tiff=image.tiffRepresentation,let jpeg=NSBitmapImageRep(data:tiff)?.representation(using:.jpeg,properties:[.compressionFactor:0.92]) else { throw CocoaError(.fileWriteUnknown) }
                        try jpeg.write(to:directory.appendingPathComponent(String(format:"frame-%05d.jpg",(frame-1800)/2)))
                    }
                }
                if frame==7200 {
                    cameraMode=2;updateCamera(snap:true)
                    try saveTownFrame("storm-overview",at:directory)
                    cameraMode=0;updateCamera(snap:true)
                    try saveTownFrame("storm-driving",at:directory)
                    let p=Sandstorm.drifts[0].center
                    world.camera.position=SCNVector3(p.x+2,1.0,p.y-3)
                    world.camera.look(at:SCNVector3(p.x,0.2,p.y),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                    try saveTownFrame("storm-drifts",at:directory)
                    world.camera.position=SCNVector3(3,3.5,DirtCourse.point(0).z+2)
                    world.camera.look(at:SCNVector3(0,1.8,DirtCourse.point(0).z-5),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                    try saveTownFrame("storm-spectators",at:directory)
                    captured=true
                }
                if frame%3600==0 { print("Storm \(frame/60)s laps \(([race]+opponents.map{$0.race}).map{$0.laps.count}) depth \(maximumDepth)");fflush(stdout) }
                if captured && ([race]+opponents.map{$0.race}).allSatisfy({$0.finished}) { break }
            }
            let finished=([race]+opponents.map{$0.race}).allSatisfy{$0.finished}
            let before=racePhysics.storm.elapsed
            simulation.paused=true;advanceRacePhysics(DriveInput(),dt:1,raceDT:1)
            let paused=racePhysics.storm.elapsed==before;simulation.paused=false
            let wind=racePhysics.storm.wind(x:0,z:0),direction=simd_normalize(wind),p=RobotCollisions.profiles[0]
            let head=simd_length(racePhysics.storm.acceleration(velocity:-direction*5,x:0,z:0,profile:p))
            let tail=simd_length(racePhysics.storm.acceleration(velocity:direction*5,x:0,z:0,profile:p))
            let sheltered=simd_length(racePhysics.storm.acceleration(velocity:.zero,x:0,z:0,profile:p,shelter:0.25))
            let exposed=simd_length(racePhysics.storm.acceleration(velocity:.zero,x:0,z:0,profile:p))
            let stormBodies=dirtWorld.town.collisionWorld.bodies.count
            weatherOverride=false;reset(nil)
            let restored=dirtWorld.town.visiblePopulation>dirtWorld.town.population/2 && !racePhysics.storm.enabled && racePhysics.storm.elapsed==0
            let absentCollisions=stormBodies<dirtWorld.town.collisionWorld.bodies.count
            let passed=finished && captured && paused && restored && absentCollisions && population<20 && population>0 && floorError<0.003 && maximumDepth>0.025 && head>tail*1.5 && sheltered<exposed*0.2
            let report:[String:Any]=["passed":passed,"allFinished":finished,"visiblePeople":population,"normalPopulation":dirtWorld.town.population,"maximumGroundPenetration":floorError,"maximumDrivenDriftDepth":maximumDepth,"pause":paused,"clearReset":restored,"absentPeopleCollidersRemoved":absentCollisions,"headwindAcceleration":head,"tailwindAcceleration":tail,"exposedWindAcceleration":exposed,"shelteredWindAcceleration":sheltered]
            try JSONSerialization.data(withJSONObject:report,options:[.prettyPrinted,.sortedKeys]).write(to:directory.appendingPathComponent("sandstorm.json"))
            print(report);return passed
        } catch { print("Sandstorm check: \(error)");return false }
    }
}
