import AppKit
import SceneKit
import SimulationCore
import simd

extension AppController {
    func checkTownEntrances(at directory:URL)->Bool {
        do {
            try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
            weatherOverride=false;defer { weatherOverride=nil }
            startDirtTrack();dirtIntro=nil;setRaceControlsHidden(true)
            dirtWorld.sky.apply(BinaryDaylight(fraction:0.48,phase:1.2))
            let town=dirtWorld.town,entries=town.entrances,city=town.collisionWorld
            // Moving people can temporarily cross a threshold. Audit permanent
            // obstructions here; PeopleSmoke separately sweeps moving bodies.
            let transient=Set((town.streetResidents?.walkers.map{$0.position} ?? [])+(town.residents?.walkers.filter{!$0.node.isHidden}.map{$0.position} ?? []))
            var blocked=[[Double]]()
            for e in entries {
                let out=SIMD2(sin(e.yaw),cos(e.yaw))
                for distance in [0.42,0.65,0.90,1.20] {
                    let p=e.center+out*distance
                    let body=RobotCollisions.Body(position:SIMD3(p.x,0.02,p.y),profile:.init(mass:70,halfWidth:0.17,halfDepth:0.17,height:1.05,round:true))
                    if city.nearby(body).contains(where:{ obstacle in
                        let moving=obstacle.profile.mass==70 && transient.contains(SIMD2(obstacle.position.x,obstacle.position.z))
                        return !moving && RobotCollisions.contact(body,obstacle) != nil
                    }) {
                        print("Entrance blocker at \(e.center): \(city.nearby(body).filter{RobotCollisions.contact(body,$0) != nil})")
                        blocked.append([e.center.x,e.center.y,e.yaw,distance,e.walkable ? 1:0]);break
                    }
                }
            }
            for variant in 0..<5 {
                let candidates=entries.filter{$0.variant==variant && !$0.walkable && max(abs($0.center.x),abs($0.center.y))<48}
                guard let e=candidates.max(by:{
                    func room(_ e:TownWorld.Entrance)->Double {
                        let out=SIMD2(sin(e.yaw),cos(e.yaw)),a=SCNVector3(e.center.x+out.x*0.4,0.85,e.center.y+out.y*0.4),b=SCNVector3(e.center.x+out.x*3.8,1.75,e.center.y+out.y*3.8)
                        let p=town.clearCamera(from:a,to:b)
                        return hypot(Double(p.x-a.x),Double(p.z-a.z))
                    }
                    return room($0)<room($1)
                }) else { return false }
                let out=SIMD2(sin(e.yaw),cos(e.yaw)),side=SIMD2(out.y,-out.x)
                let p=e.center+out*3.8+side*1.2,target=e.center+out*0.4
                let aim=SCNVector3(target.x,0.85,target.y)
                world.camera.position=town.clearCamera(from:aim,to:SCNVector3(p.x,1.75,p.y))
                world.camera.look(at:aim,up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                town.update(dt:0,camera:world.camera.position,player:p)
                try saveTownFrame("entrance-\(variant)",at:directory)
            }
            let orientations=Set(entries.map{Int((($0.yaw+Double.pi/4).truncatingRemainder(dividingBy:2*Double.pi))/(Double.pi/2))})
            let report:[String:Any]=["buildings":town.statistics["buildings"] ?? 0,"entrances":entries.count,"variants":(0..<5).map{v in entries.filter{$0.variant==v}.count},"blockedApproaches":blocked,"workingDoors":town.doorways.count,"doorConnections":town.residents?.connections ?? 0,"townValid":town.validate(),"orientations":Array(orientations).sorted(),"statistics":town.statistics]
            try JSONSerialization.data(withJSONObject:report,options:[.prettyPrinted,.sortedKeys]).write(to:directory.appendingPathComponent("entrances.json"))
            print("Entrance audit: \(report)")
            return blocked.isEmpty && entries.count>100 && orientations.count>=4 && town.validate()
        } catch { print(error);return false }
    }
}
