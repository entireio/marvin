import AppKit
import SceneKit
import SimulationCore
import simd

extension AppController {
    func checkTownPeople(at directory:URL)->Bool {
        do {
            try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
            weatherOverride=false;defer { weatherOverride=nil }
            startDirtTrack();dirtIntro=nil;raceHUD.isHidden=true;window.toolbar?.isVisible=false
            dirtWorld.sky.apply(BinaryDaylight(fraction:0.48,phase:1.2))
            guard let residents=dirtWorld.town.residents,residents.walkers.count>=6,residents.connections>=4 else { return false }
            guard let street=dirtWorld.town.streetResidents,street.walkers.count>=12 else { return false }
            var streetPositions=[[Double]](),streetStopped=Array(repeating:0.0,count:street.walkers.count)
            var longestStreetStop=0.0
            let offscreen=ProcessInfo.processInfo.environment["MARVIN_PEOPLE_OFFSCREEN"]=="1"
            let visibility:(SCNNode)->Bool={ node in !offscreen && self.view.isNode(node,insideFrustumOf:self.world.camera) }
            let streetMovie=ProcessInfo.processInfo.environment["MARVIN_STREET_MOVIE"]=="1"
            let movie=ProcessInfo.processInfo.environment["MARVIN_PEOPLE_MOVIE"]=="1"
            let renderer=SCNRenderer(device:view.device,options:nil)
            renderer.scene=dirtWorld.scene;renderer.pointOfView=world.camera
            var movieFrames=[0,0,0]
            let names=["resident-trip","spectator-reactions","town-life"]
            if movie { for name in names { try FileManager.default.createDirectory(at:directory.appendingPathComponent(name),withIntermediateDirectories:true) } }
            if streetMovie { try FileManager.default.createDirectory(at:directory.appendingPathComponent("street-movie"),withIntermediateDirectories:true) }
            for frame in 0..<36000 {
                let previousStreet=street.walkers.map{$0.position}
                dirtWorld.town.update(dt:1.0/60,camera:world.camera.position,player:SIMD2(Double(world.camera.position.x),Double(world.camera.position.z)),visible:visibility)
                for (i,w) in street.walkers.enumerated() {
                    streetStopped[i]=simd_distance(previousStreet[i],w.position)<0.0001 ? streetStopped[i]+1.0/60:0
                    longestStreetStop=max(longestStreetStop,streetStopped[i])
                }
                if [120,480,900,1260,1800,2160].contains(frame) {
                    let district=(frame<900 ? 0:(frame<1800 ? 1:2))
                    let w=street.walkers[district*street.walkers.count/3],p=w.path[w.start]
                    world.camera.position=SCNVector3(p.x+4,2.0,p.y+5)
                    world.camera.look(at:SCNVector3(p.x,0.6,p.y),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                    try saveTownFrame("street-\(district)-\(frame)",at:directory)
                    streetPositions.append([Double(frame),w.position.x,w.position.y])
                }
                if [0,180,360].contains(frame) {
                    let z=DirtCourse.point(0).z
                    world.camera.position=SCNVector3(-4.4,2.6,z-0.5)
                    world.camera.look(at:SCNVector3(-4.4,2.35,z-4.4),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                    try saveTownFrame("spectators-\(frame)",at:directory)
                }
                if frame%120==0 && frame<2400,let walker=residents.walkers.first(where:{!$0.node.isHidden}) {
                    let p=walker.node.position
                    world.camera.position=SCNVector3(p.x+1.1,p.y+1.1,p.z+2.0)
                    world.camera.look(at:SCNVector3(p.x,p.y+0.55,p.z),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                    try saveTownFrame("walking-\(frame)",at:directory)
                }
                if movie && frame%3==0 {
                    let clip=frame<2400 ? 0:(frame>=2400 && frame<3120 ? 1:(frame>=3600 && frame<4320 ? 2:-1))
                    if clip>=0 {
                        if clip==0 {
                            let w=residents.walkers[min(3,residents.walkers.count-1)],p=w.position
                            world.camera.position=SCNVector3(p.x+1.5,1.5,p.y+2.8)
                            world.camera.look(at:SCNVector3(p.x,0.55,p.y),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                        } else if clip==1 {
                            let z=DirtCourse.point(0).z
                            world.camera.position=SCNVector3(-4.4,2.6,z-0.5);world.camera.look(at:SCNVector3(-4.4,2.35,z-4.4),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                        } else {
                            world.camera.position=SCNVector3(0,38,-33);world.camera.look(at:SCNVector3Zero,up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                        }
                        try autoreleasepool {
                            let image=renderer.snapshot(atTime:Double(frame)/60,with:CGSize(width:1280,height:720),antialiasingMode:.multisampling4X)
                            guard let tiff=image.tiffRepresentation,let jpeg=NSBitmapImageRep(data:tiff)?.representation(using:.jpeg,properties:[.compressionFactor:0.93]) else { throw CocoaError(.fileWriteUnknown) }
                            try jpeg.write(to:directory.appendingPathComponent(names[clip]).appendingPathComponent(String(format:"frame-%05d.jpg",movieFrames[clip])))
                        }
                        movieFrames[clip] += 1
                    }
                }
                if streetMovie && frame>=120 && frame<600 && frame%3==0 {
                    let w=street.walkers[0],p=w.path[w.start]
                    world.camera.position=SCNVector3(p.x+4,2,p.y+5)
                    world.camera.look(at:SCNVector3(p.x,0.6,p.y),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                    try autoreleasepool {
                        let image=renderer.snapshot(atTime:Double(frame)/60,with:CGSize(width:1280,height:720),antialiasingMode:.multisampling4X)
                        guard let tiff=image.tiffRepresentation,let jpeg=NSBitmapImageRep(data:tiff)?.representation(using:.jpeg,properties:[.compressionFactor:0.93]) else { throw CocoaError(.fileWriteUnknown) }
                        try jpeg.write(to:directory.appendingPathComponent("street-movie").appendingPathComponent(String(format:"frame-%05d.jpg",(frame-120)/3)))
                    }
                }
                if frame%3600==0 {
                    print("Residents \(frame/60)s: entries \(residents.entries), exits \(residents.exits), visits \(residents.walkers.map{$0.visits}), blocked \(residents.walkers.map{Int($0.blocked)})")
                    fflush(stdout)
                }
            }
            let streetReport:[String:Any]=["count":street.walkers.count,"distance":street.walkers.map{$0.distance},"longestStop":longestStreetStop,"positions":streetPositions,"maximumPenetration":street.maximumPenetration,"poseUpdates":street.poseUpdates,"navigationUpdates":street.navigationUpdates]
            try JSONSerialization.data(withJSONObject:streetReport,options:[.prettyPrinted,.sortedKeys]).write(to:directory.appendingPathComponent("street-people.json"))
            let streetRatePassed = !offscreen || (street.poseUpdates==0 && street.navigationUpdates<36000*street.walkers.count/2)
            let streetPassed=streetRatePassed && street.walkers.allSatisfy{$0.distance>30} && street.maximumPenetration<0.005 && longestStreetStop<15
            print("Street residents: \(streetReport), passed \(streetPassed)")
            let report:[String:Any]=["offscreen":offscreen,"updateStatistics":residents.updateStatistics,"seconds":600,"movieFrames":movieFrames,"entries":residents.entries,"firstEntries":residents.walkers.map{$0.firstEntry},"exits":residents.exits,"doors":residents.doors.count,"routes":residents.connections,"visits":residents.walkers.map{$0.visits},"distance":residents.walkers.map{$0.distance},"blocked":residents.walkers.map{$0.blocked},"waypoints":residents.walkers.map{$0.waypoint},"paths":residents.walkers.map{$0.path.map{[$0.x,$0.y]}},"positions":residents.walkers.map{[$0.position.x,$0.position.y]},"maximumPenetration":residents.maximumPenetration]
            try JSONSerialization.data(withJSONObject:report,options:[.prettyPrinted,.sortedKeys]).write(to:directory.appendingPathComponent("people.json"))
            print(report.filter{$0.key != "paths"})
            world.camera.position=SCNVector3(0,38,-33);world.camera.look(at:SCNVector3Zero,up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
            try saveTownFrame("town-people-overhead",at:directory)
            let moving=residents.walkers.allSatisfy{$0.visits>0 && $0.distance>20} && residents.maximumPenetration<0.005
            let ratePassed = !offscreen || (residents.poseUpdates==0 && residents.navigationUpdates<36000*residents.walkers.count/2 && residents.coarseUpdates>0)
            // A camera cut must wake poses on the next update, without resetting
            // distance-driven gait or teleporting the resident to catch up.
            let previous=residents.walkers.map{$0.position},poses=residents.poseUpdates
            residents.update(dt:1.0/60,robots:[],visible:{_ in true})
            let woke=residents.poseUpdates>poses && zip(previous,residents.walkers).allSatisfy{simd_distance($0.0,$0.1.position)<0.1}
            let phasePassed=residents.walkers.filter{!$0.indoors}.allSatisfy { w in
                guard let cycle=w.materials.first?.value(forKey:"walkCycle") as? NSNumber else { return false }
                return abs(cycle.doubleValue-w.distance/0.24 * .pi)<0.001
            }
            let positions=residents.walkers.map{$0.position}
            dirtWorld.town.update(dt:0,camera:world.camera.position,player:.zero)
            let paused=positions==residents.walkers.map{$0.position}
            dirtWorld.town.setStorm(true)
            for _ in 0..<1200 { dirtWorld.town.update(dt:1.0/60,camera:world.camera.position,player:SIMD2(Double(world.camera.position.x),Double(world.camera.position.z)),visible:visibility) }
            let sheltered=residents.visible<=2 && dirtWorld.town.visiblePopulation<30
            dirtWorld.town.setStorm(false);dirtWorld.town.reset()
            street.reset()
            let pedestrian=street.walkers[0],block=pedestrian.path[min(pedestrian.path.count-1,pedestrian.start+20)]
            let parkedRobot=RobotCollisions.Body(position:SIMD3(block.x,0,block.y),profile:.init(mass:30,halfWidth:0.45,halfDepth:0.45,height:0.8))
            for _ in 0..<1800 { street.update(dt:1.0/60,obstacles:[parkedRobot],visible:{_ in true}) }
            let avoidedRobot=street.maximumPenetration<0.005 && pedestrian.distance>5
            print("Street robot avoidance: \(avoidedRobot), distance \(pedestrian.distance), penetration \(street.maximumPenetration)")
            return avoidedRobot && streetPassed && moving && ratePassed && woke && phasePassed && paused && sheltered && residents.walkers.allSatisfy{$0.node.isHidden && $0.distance==0}
        } catch { print(error);return false }
    }
}
