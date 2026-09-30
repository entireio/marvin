import AppKit
import SceneKit
import SimulationCore
import simd

extension AppController {
    /// Offline capture of ordinary driving physics, at 30 fps with two 60 Hz
    /// simulation steps per image. The only placed pose is the initial spawn.
    /// The current terrain beyond town is flat sand, not a dune heightfield.
    func captureTownDeparture(at directory:URL)->Bool {
        do {
            try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
            startDirtTrack();dirtIntro=nil;race.countDown(dt:3);raceHUD.isHidden=true
            let spawn=SIMD2<Double>(132,48)
            let projection=DirtCourse.projection(x:spawn.x,z:spawn.y)
            simulation=Simulation(dirtTrack:true,dirtStartOffset:projection.distance,
                dirtStartPhase:DirtCourse.phase(x:spawn.x,z:spawn.y))
            let start=SIMD2(simulation.x,simulation.z),goal=SIMD2<Double>(172,57)
            let city=dirtWorld.town.collisionWorld
            guard let route=TownEscapeRoute(city:city,origin:start).route(from:start,to:goal) else {
                print("No safe departure route from \(start)");return false
            }
            let renderer=SCNRenderer(device:view.device,options:nil)
            renderer.scene=dirtWorld.scene;renderer.pointOfView=world.camera
            var waypoint=1,frame=0,distance=0.0,maxPenetration=0.0,arrived=false
            var camera=SIMD3(start.x+6,2.9,start.y+1.3)
            // Settle the heading before the shot starts, using actual controls.
            for _ in 0..<300 {
                let d=route[1]-SIMD2(simulation.x,simulation.z)
                let error=atan2(sin(atan2(d.x,d.y)-simulation.heading),cos(atan2(d.x,d.y)-simulation.heading))
                var input=DriveInput();input.turn=max(-1,min(1,-error*2.5))
                advanceRacePhysics(input,dt:1.0/60,raceDT:1.0/60)
            }
            while frame<1800 {
                for _ in 0..<2 {
                    let position=SIMD2(simulation.x,simulation.z)
                    if simd_distance(position,route[waypoint])<0.24 {
                        if waypoint+1<route.count { waypoint += 1 } else { arrived=true }
                    }
                    let delta=route[waypoint]-position
                    let error=atan2(sin(atan2(delta.x,delta.y)-simulation.heading),cos(atan2(delta.x,delta.y)-simulation.heading))
                    var input=DriveInput()
                    input.turn=max(-1,min(1,-error*2.5))
                    input.throttle = !arrived && abs(error)<0.22 ? min(0.65,simd_length(delta)*0.7):0
                    input.brake=arrived
                    advanceRacePhysics(input,dt:1.0/60,raceDT:1.0/60)
                    distance += simd_distance(position,SIMD2(simulation.x,simulation.z))
                    let body=RobotCollisions.Body(position:SIMD3(simulation.x,simulation.groundY,simulation.z),heading:simulation.heading,profile:RobotCollisions.profiles[0])
                    for obstacle in city.nearby(body) {
                        if let contact=RobotCollisions.contact(body,obstacle) { maxPenetration=max(maxPenetration,contact.penetration) }
                    }
                    updateOpponents();updateRaceWorld(dt:1.0/60)
                }
                let target=SIMD3(simulation.x,simulation.groundY+0.4,simulation.z)
                camera += (target+SIMD3(6,2.5,1.3)-camera)*0.08
                world.camera.position=dirtWorld.town.clearCamera(from:SCNVector3(target.x,target.y,target.z),to:SCNVector3(camera.x,camera.y,camera.z))
                world.camera.look(at:SCNVector3(target.x,target.y,target.z),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                dirtWorld.town.update(dt:1.0/30,camera:world.camera.position,player:SIMD2(simulation.x,simulation.z))
                try autoreleasepool {
                    let image=renderer.snapshot(atTime:Double(frame)/30,with:CGSize(width:1280,height:720),antialiasingMode:.multisampling4X)
                    guard let tiff=image.tiffRepresentation,
                          let jpeg=NSBitmapImageRep(data:tiff)?.representation(using:.jpeg,properties:[.compressionFactor:0.92]) else { throw CocoaError(.fileWriteUnknown) }
                    try jpeg.write(to:directory.appendingPathComponent(String(format:"frame-%05d.jpg",frame)))
                }
                frame += 1
                if frame%150==0 { print("Departure frame \(frame), position \(simulation.x), \(simulation.z)");fflush(stdout) }
                if arrived { break }
            }
            let report:[String:Any]=["arrived":arrived,"frames":frame,"fps":30,"distanceMetres":distance,"maximumPenetrationMetres":maxPenetration,"terrain":"Existing flat sand outside town; dunes not implemented","start":[start.x,start.y],"end":[simulation.x,simulation.z]]
            try JSONSerialization.data(withJSONObject:report,options:[.prettyPrinted,.sortedKeys]).write(to:directory.appendingPathComponent("departure.json"))
            print(report)
            return arrived && maxPenetration<0.005
        } catch { print("Departure capture: \(error)");return false }
    }
}
