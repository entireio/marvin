import AppKit
import SceneKit
import SimulationCore
import simd

extension AppController {
    func checkStormRace(at directory:URL)->Bool {
        do {
            try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
            weatherOverride=true;defer { weatherOverride=nil }
            startDirtTrack();dirtIntro=nil;raceHUD.isHidden=true
            var results=[[String:Any]]()
            for a in 0..<4 { for b in 0..<4 where b != a { for c in 0..<4 where c != a && c != b {
                let d=(0..<4).first{$0 != a && $0 != b && $0 != c}!,order=[a,b,c,d]
                let slots=order.map{DirtCourse.startingGrid[$0]}
                simulation=Simulation(seed:0,dirtTrack:true,dirtStartOffset:slots[0].offset,dirtStartPhase:slots[0].phase,character:playerCharacter)
                opponent=DirtOpponent(slot:slots[1]);bb8Opponent=DirtOpponent(slot:slots[2],laneOffset:0);wallEOpponent=DirtOpponent(slot:slots[3],laneOffset:-0.65)
                race=DirtRace(startPhase:slots[0].phase);race.countDown(dt:3)
                racePhysics=DirtRacePhysics(characters:lineup,townRoutes:dirtWorld.escapeRoutes)
                racePhysics.storm=Sandstorm(enabled:true);dirtWorld.town.reset()
                var history=[[String:Any]](),finished=false
                for frame in 0..<18000 {
                    advanceRacePhysics(DirtOpponent.driveInput(for:simulation),dt:1.0/60,raceDT:1.0/60)
                    let bb=bb8Opponent.simulation,q=CityExit.local(SIMD2(bb.x,bb.z))
                    history.append(["seconds":Double(frame)/60,"x":bb.x,"z":bb.z,"y":bb.groundY,"vx":bb.velocity.x,"vy":bb.velocity.y,"vz":bb.velocity.z,"along":q.x,"out":q.y,"heading":bb.heading,"contact":bb.contacting,"shelter":bb.windShelter])
                    if history.count>120 { history.removeFirst() }
                    let p=DirtCourse.projection(x:bb.x,z:bb.z)
                    if !racePhysics.escape.active && p.offset>0 && p.distance>DirtCourse.fenceOffset+0.75 {
                        let report:[String:Any]=["grid":order,"lineup":lineup.map{$0.rawValue},"gateAngle":racePhysics.gate.angle,"history":history]
                        try JSONSerialization.data(withJSONObject:report,options:[.prettyPrinted,.sortedKeys]).write(to:directory.appendingPathComponent("escape.json"))
                        print("BB8 ESCAPED grid \(order) at \(Double(frame)/60), gate \(racePhysics.gate.angle), last \(history.suffix(12))");fflush(stdout)
                        updateOpponents();updateRaceWorld(dt:1.0/60)
                        world.camera.position=SCNVector3(bb.x+3,bb.groundY+2,bb.z-2);world.camera.look(at:SCNVector3(bb.x,bb.groundY+0.2,bb.z),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                        try saveTownFrame("premature-exit",at:directory)
                        return false
                    }
                    if race.finished && opponents.allSatisfy({$0.race.finished}) { finished=true;results.append(["grid":order,"seconds":Double(frame)/60,"laps":([race]+opponents.map{$0.race}).map{$0.laps.count}]);print("Storm grid \(order) finished at \(Double(frame)/60)");fflush(stdout);break }
                }
                guard finished else { print("Storm grid \(order) TIMEOUT: \(([race]+opponents.map{$0.race}).map{$0.laps.count})");return false }
            }}}
            try JSONSerialization.data(withJSONObject:["passed":true,"grids":results],options:[.prettyPrinted,.sortedKeys]).write(to:directory.appendingPathComponent("storm-grids.json"))
            return true
        } catch { print(error);return false }
    }
}
