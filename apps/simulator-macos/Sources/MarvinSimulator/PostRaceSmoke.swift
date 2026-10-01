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
            var frozen=[Double?](repeating:nil,count:4),early=false,firstOpen:Double?,pauseChecked=false
            var maxPenetration=0.0
            for frame in 0..<72000 {
                advanceRacePhysics(DirtOpponent.driveInput(for:simulation),dt:1.0/60,raceDT:1.0/60)
                let races=[race]+opponents.map{$0.race}
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
                        for obstacle in dirtWorld.town.collisionWorld.nearby(body)+[racePhysics.gate.body]+CityExit.posts {
                            if let contact=RobotCollisions.contact(body,obstacle) { maxPenetration=max(maxPenetration,contact.penetration) }
                        }
                    }
                }
                if frame%6000==0 { print("Postrace \(frame/60)s laps \(races.map{$0.laps.count}) cooldown \(races.map{$0.cooldownProgress}) waypoints \(racePhysics.escape.waypoint)");fflush(stdout) }
                if racePhysics.escape.complete { break }
            }
            let states=[simulation]+opponents.map{$0.simulation},races=[race]+opponents.map{$0.race}
            let timesFrozen=races.indices.allSatisfy{frozen[$0]==races[$0].elapsed}
            let goals=racePhysics.escape.routes.compactMap{$0.last}
            let distinct=goals.count==4 && (0..<4).allSatisfy { i in (0..<i).allSatisfy{simd_distance(goals[i],goals[$0])>3} }
            let passed=racePhysics.escape.complete && !early && timesFrozen && pauseChecked && distinct && maxPenetration<0.005
            updateOpponents();updateRaceWorld(dt:1.0/60)
            world.camera.position=SCNVector3(48,35,-22);world.camera.look(at:SCNVector3(31,0,4),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
            try saveTownFrame("postrace-town",at:directory)
            let report:[String:Any]=["passed":passed,"complete":racePhysics.escape.complete,"earlyGate":early,"frozenResults":timesFrozen,"pause":pauseChecked,"differentDestinations":distinct,"maximumPenetration":maxPenetration,"gateOpenTime":firstOpen ?? -1,"waypoints":racePhysics.escape.waypoint,"positions":states.map{[$0.x,$0.z]},"routes":racePhysics.escape.routes.map{$0.map{[$0.x,$0.y]}}]
            try JSONSerialization.data(withJSONObject:report,options:[.prettyPrinted,.sortedKeys]).write(to:directory.appendingPathComponent("postrace.json"))
            print(report)
            reset(nil);return passed && !racePhysics.escape.active && racePhysics.gate.angle==0
        } catch { print(error);return false }
    }
}
