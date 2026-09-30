import AppKit
import SceneKit
import SimulationCore
import simd

extension AppController {
    /// Drives ordinary control input through the live coupled simulation. Only
    /// the initial spawn is chosen; no route step changes Marvin's pose directly.
    func checkCityEscape(at directory:URL)->Bool {
        do {
            try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
            startDirtTrack();dirtIntro=nil;race.countDown(dt:3);raceHUD.isHidden=true
            simulation=Simulation(dirtTrack:true,dirtStartPhase:CityExit.phase)
            race=DirtRace(startPhase:CityExit.phase);race.countDown(dt:3)
            let city=dirtWorld.town.collisionWorld
            var checks:[String:Bool]=["townImpactAndReverseAllChassis":checkTownImpacts(city)],travel=0.0,maxPenetration=0.0,frames=0,motionValid=true
            func key(_ repeated:Bool=false) {
                let event=NSEvent.keyEvent(with:.keyDown,location:.zero,modifierFlags:[],timestamp:ProcessInfo.processInfo.systemUptime,windowNumber:window.windowNumber,context:nil,characters:"g",charactersIgnoringModifiers:"g",isARepeat:repeated,keyCode:5)!
                view.keyDown(with:event);view.keyUp(with:event)
            }
            func step(_ input:DriveInput=DriveInput()) {
                let old=SIMD2(simulation.x,simulation.z)
                advanceRacePhysics(input,dt:1.0/60,raceDT:1.0/60)
                travel += simd_distance(old,SIMD2(simulation.x,simulation.z));frames += 1
                motionValid = motionValid && simulation.x.isFinite && simulation.z.isFinite && simulation.groundY>=DirtCourse.height(x:simulation.x,z:simulation.z)-0.005
                let body=RobotCollisions.Body(position:SIMD3(simulation.x,simulation.groundY,simulation.z),heading:simulation.heading,profile:RobotCollisions.profiles[0])
                for obstacle in city.nearby(body)+[racePhysics.gate.body]+CityExit.posts {
                    if let c=RobotCollisions.contact(body,obstacle) { maxPenetration=max(maxPenetration,c.penetration) }
                }
            }
            func shot(_ name:String,_ eye:SIMD3<Double>,_ target:SIMD3<Double>) throws {
                updateOpponents();updateRaceWorld(dt:1.0/60)
                world.camera.position=SCNVector3(eye.x,eye.y,eye.z)
                world.camera.look(at:SCNVector3(target.x,target.y,target.z),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                dirtWorld.town.update(dt:0,camera:world.camera.position,player:SIMD2(simulation.x,simulation.z))
                _=view.prepare(dirtWorld.scene,shouldAbortBlock:nil)
                try saveTownFrame(name,at:directory)
            }
            func exitShot(_ name:String) throws {
                let eye=CityExit.point(5,7),target=CityExit.point(0,0)
                try shot(name,SIMD3(eye.x,3.2,eye.y),SIMD3(target.x,0.55,target.y))
            }
            try exitShot("escape-gate-closed")
            key();checks["hiddenGOpens"]=racePhysics.gate.wantsOpen
            key(true);checks["repeatIgnored"]=racePhysics.gate.wantsOpen
            simulation.paused=true
            let angle=racePhysics.gate.angle
            for _ in 0..<60 { step() }
            checks["pausedGateStationary"]=racePhysics.gate.angle==angle
            simulation.paused=false
            for _ in 0..<150 { step() }
            try exitShot("escape-gate-opening")
            for _ in 0..<450 { step() }
            checks["fullyOpen"]=abs(racePhysics.gate.angle-100 * .pi/180)<0.001
            func drive(to target:SIMD2<Double>)->Bool {
                for _ in 0..<2400 {
                    let d=target-SIMD2(simulation.x,simulation.z),distance=simd_length(d)
                    if distance<0.22 { return true }
                    let error=atan2(sin(atan2(d.x,d.y)-simulation.heading),cos(atan2(d.x,d.y)-simulation.heading))
                    var input=DriveInput()
                    input.turn=max(-1,min(1,-error*2.5))
                    // Slow down before changing direction; let finite steering
                    // torque turn the tracks in place instead of cutting corners.
                    input.throttle=abs(error)<0.22 ? min(0.5,distance*0.7):0
                    step(input)
                }
                print("Escape route stuck at \(simulation.x),\(simulation.z), target \(target)")
                return false
            }
            let apron=CityExit.point(0,8)
            let crossed=drive(to:CityExit.point(0,-1.1)) && drive(to:CityExit.point(0,2.5))
            try exitShot("escape-crossing")
            checks["crossedRamp"]=crossed && drive(to:apron)
            try exitShot("escape-ramp-open")
            // Find a navigable town walk, respecting actual generated solids.
            let planner=TownEscapeRoute(city:city,origin:apron)
            let destinations=[SIMD2<Double>(29,-10),SIMD2(33,3),SIMD2(35,14),SIMD2(36,28)]
            var routes:[[SIMD2<Double>]]=[],current=apron,roamed=true
            for destination in destinations {
                guard let route=planner.route(from:current,to:destination) else { roamed=false;break }
                routes.append(route)
                for point in route { if !drive(to:point) { roamed=false;break } }
                if !roamed { break }
                current=SIMD2(simulation.x,simulation.z)
                let index=routes.count
                try shot("escape-town-\(index)",SIMD3(current.x-5,3.5,current.y-5),SIMD3(current.x,0.6,current.y))
            }
            checks["roamedTown"]=roamed && routes.count==destinations.count
            let p=SIMD2(simulation.x,simulation.z),forward=SIMD2(sin(simulation.heading),cos(simulation.heading))
            try shot("escape-town-pov",SIMD3(p.x+forward.x*0.35,simulation.groundY+0.46,p.y+forward.y*0.35),SIMD3(p.x+forward.x*15,simulation.groundY+0.46,p.y+forward.y*15))
            try shot("escape-birds-eye",SIMD3(30,42,-22),SIMD3(18,0,4))
            var returned=roamed
            for route in routes.reversed() { for point in route.reversed() { if !drive(to:point) { returned=false;break } };if !returned { break } }
            if returned { returned=drive(to:apron) && drive(to:CityExit.point(0,2)) && drive(to:CityExit.point(0,-1.25)) }
            checks["returnedToTrack"]=returned
            print("Rival positions: \(opponents.map { [ $0.simulation.x,$0.simulation.z,$0.simulation.heading,CityExit.local(SIMD2($0.simulation.x,$0.simulation.z)).x,CityExit.local(SIMD2($0.simulation.x,$0.simulation.z)).y ] })")
            checks["rivalsRemainInRace"]=opponents.allSatisfy {
                let p=DirtCourse.projection(x:$0.simulation.x,z:$0.simulation.z)
                return p.offset<0 || p.distance<DirtCourse.fenceOffset
            }
            checks["noSolidPenetration"]=maxPenetration<0.005
            checks["finiteGroundedMotion"]=motionValid
            key();for _ in 0..<600 { step() }
            checks["hiddenGCloses"]=racePhysics.gate.angle<0.001
            reset(nil);checks["resetClosesGate"]=racePhysics.gate.angle==0 && !racePhysics.gate.wantsOpen
            let passed=checks.values.allSatisfy{$0}
            let report:[String:Any] = ["passed":passed,"checks":checks,"distanceMetres":travel,"simulatedSeconds":Double(frames)/60,"maximumSolidPenetrationMetres":maxPenetration,"cityCollisionSolids":city.bodies.count,"routes":routes.map{$0.map{[$0.x,$0.y]}}]
            try JSONSerialization.data(withJSONObject:report,options:[.prettyPrinted,.sortedKeys]).write(to:directory.appendingPathComponent("city-escape.json"))
            print(checks)
            return passed
        } catch { print("City escape smoke: \(error)");return false }
    }
}

/// Test driver only: conservative grid search plus line-of-sight simplification.
struct TownEscapeRoute {
    let city:CityCollisionWorld,origin:SIMD2<Double>
    func route(from start:SIMD2<Double>,to goal:SIMD2<Double>)->[SIMD2<Double>]? {
        typealias Cell=SIMD2<Int>
        func cell(_ p:SIMD2<Double>)->Cell { let d=(p-origin)*2;return Cell(Int(d.x.rounded()),Int(d.y.rounded())) }
        func point(_ c:Cell)->SIMD2<Double> { origin+SIMD2(Double(c.x),Double(c.y))/2 }
        func free(_ p:SIMD2<Double>)->Bool {
            let projection=DirtCourse.projection(x:p.x,z:p.y)
            guard projection.offset>0,projection.distance>DirtCourse.fenceOffset+0.8 else { return false }
            let body=RobotCollisions.Body(position:SIMD3(p.x,DirtCourse.height(x:p.x,z:p.y),p.y),profile:.init(mass:18,halfWidth:0.53,halfDepth:0.53,height:0.55,round:true))
            return city.nearby(body).allSatisfy{RobotCollisions.contact(body,$0)==nil}
        }
        let source=cell(start),target=cell(goal)
        var parents:[Cell:Cell]=[source:source],queue=[source],head=0,best=source,bestDistance=Double.infinity,cache:[Cell:Bool]=[:]
        let directions=[Cell(1,0),Cell(-1,0),Cell(0,1),Cell(0,-1)]
        while head<queue.count {
            let c=queue[head];head += 1
            let distance=simd_distance(point(c),goal)
            if distance<bestDistance { best=c;bestDistance=distance }
            if c==target { break }
            for d in directions {
                let next=c&+d
                guard abs(next.x)<100,abs(next.y)<100,parents[next]==nil else { continue }
                let available=cache[next] ?? free(point(next));cache[next]=available
                if available { parents[next]=c;queue.append(next) }
            }
        }
        guard bestDistance<3 else { return nil }
        var path=[point(best)],cursor=best
        while cursor != source { cursor=parents[cursor]!;path.append(point(cursor)) }
        path.reverse()
        func clear(_ a:SIMD2<Double>,_ b:SIMD2<Double>)->Bool {
            let count=max(1,Int(ceil(simd_distance(a,b)/0.15)))
            return (0...count).allSatisfy{free(a+(b-a)*Double($0)/Double(count))}
        }
        var result=[start],i=0
        while i<path.count-1 {
            var next=i+1
            for j in (i+1)..<path.count { if clear(result.last!,path[j]) { next=j } else { break } }
            result.append(path[next]);i=next
        }
        return result
    }
}

/// Boosted impacts and reversing against generated town geometry, independent
/// of the path planner (which deliberately avoids collisions).
private func checkTownImpacts(_ city:CityCollisionWorld)->Bool {
    for profile in RobotCollisions.profiles {
        var tested=0
        for obstacle in city.bodies where obstacle.position.y<0.1 && obstacle.profile.height>1 && obstacle.profile.halfWidth>0.8 {
            let side=SIMD2(cos(obstacle.heading),-sin(obstacle.heading))
            let p=SIMD2(obstacle.position.x,obstacle.position.z)+side*(obstacle.profile.halfWidth+hypot(profile.halfWidth,profile.halfDepth)+0.7)
            let projection=DirtCourse.projection(x:p.x,z:p.y)
            guard projection.offset>0,projection.distance>DirtCourse.fenceOffset+2 else { continue }
            var bodies=[RobotCollisions.Body(position:SIMD3(p.x,DirtCourse.height(x:p.x,z:p.y),p.y),heading:atan2(-side.x,-side.y),profile:profile)]
            guard city.nearby(bodies[0]).allSatisfy({RobotCollisions.contact(bodies[0],$0)==nil}) else { continue }
            var hit=false
            for _ in 0..<100 {
                let previous=bodies[0].position
                bodies[0].velocity=SIMD3(-side.x*12,0,-side.y*12)
                bodies[0].position += bodies[0].velocity/240
                _=RobotCollisions.resolve(&bodies,terrain:true,gate:CityGate(),city:city,previousPositions:[previous])
                hit = hit || bodies[0].contacted
                if city.nearby(bodies[0]).contains(where:{(RobotCollisions.contact(bodies[0],$0)?.penetration ?? 0)>0.005}) { return false }
            }
            guard hit else { continue }
            let stopped=bodies[0].position
            for _ in 0..<100 {
                let previous=bodies[0].position
                bodies[0].velocity=SIMD3(side.x*3,0,side.y*3)
                bodies[0].position += bodies[0].velocity/240
                _=RobotCollisions.resolve(&bodies,terrain:true,gate:CityGate(),city:city,previousPositions:[previous])
            }
            if simd_length(bodies[0].position-stopped)<0.5 { return false }
            tested += 1
            if tested==12 { break }
        }
        if tested<12 { return false }
    }
    return true
}
