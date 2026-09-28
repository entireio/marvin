import AppKit
import SceneKit
import SimulationCore

extension AppController {
    /// Drive a complete three-lap race through the actual collision world and
    /// trail renderer, checking both emission and the visible terrain surface.
    func checkFullRaceTrails(at directory:URL) throws -> Bool {
        reset(nil); dirtIntro = nil
        let slot = DirtCourse.startingGrid[3]
        simulation = Simulation(seed:0,dirtTrack:true,dirtStartOffset:slot.offset,dirtStartPhase:slot.phase)
        race = DirtRace(startPhase:slot.phase); race.countDown(dt:3)
        opponent = DirtOpponent(slot:DirtCourse.startingGrid[0])
        bb8Opponent = DirtOpponent(slot:DirtCourse.startingGrid[1],laneOffset:0)
        wallEOpponent = DirtOpponent(slot:DirtCourse.startingGrid[2],laneOffset:-0.65)
        let lane = dirtWorld.scene.rootNode.childNode(withName:"Compacted race surface",recursively:false)!
        var buried: [[String:Double]] = [], air: [[String:Double]] = []
        var groundedMissing = 0, samples = 0, smallHopFrames = 0, airborneMarks = 0
        var flights: [[String:Double]] = [], flightStart: Simulation?, flightMax = 0.0
        for frame in 0..<60*240 {
            let before = simulation, count = dirtWorld.trailCounts[0]
            let input = DirtOpponent.driveInput(for:simulation,laneOffset:0.1)
            advanceRacePhysics(input,dt:1.0/60,raceDT:1.0/60)
            dirtWorld.update(simulation,opponent:opponent.simulation,dt:1.0/60,modelScale:robot.modelScale,additional:[bb8Opponent.simulation,wallEOpponent.simulation])
            if before.hasDirtContact && simulation.hasDirtContact && hypot(simulation.x-before.x,simulation.z-before.z) > 0.07 && dirtWorld.trailCounts[0] == count { groundedMissing += 1 }
            if simulation.airborne && simulation.hasDirtContact { smallHopFrames += 1 }
            if !simulation.hasDirtContact && dirtWorld.trailCounts[0] != count { airborneMarks += 1 }
            if simulation.airborne {
                if flightStart == nil { flightStart = before; flightMax = 0 }
                flightMax = max(flightMax,simulation.groundY-DirtCourse.height(x:simulation.x,z:simulation.z))
            } else if let began = flightStart {
                flights.append(["start":began.elapsed,"duration":simulation.elapsed-began.elapsed,"distance":simulation.distance-began.distance,"phase":DirtCourse.phase(x:began.x,z:began.z),"maxClearance":flightMax])
                flightStart = nil
            }
            if simulation.airborne && !before.airborne {
                air.append(["time":race.elapsed,"phase":DirtCourse.phase(x:simulation.x,z:simulation.z),"clearance":simulation.groundY-DirtCourse.height(x:simulation.x,z:simulation.z)])
            }
            if frame.isMultiple(of:5) && !simulation.airborne {
                for side in [-1.0,1.0] {
                    let lateral = side*0.262225*robot.modelScale, forward = -0.23*robot.modelScale
                    let x = simulation.x+cos(simulation.heading)*lateral+sin(simulation.heading)*forward
                    let z = simulation.z-sin(simulation.heading)*lateral+cos(simulation.heading)*forward
                    let nominal = DirtCourse.height(x:x,z:z)+0.007
                    let hits = lane.hitTestWithSegment(from:SCNVector3(x,5,z),to:SCNVector3(x,-1,z),options:[SCNHitTestOption.backFaceCulling.rawValue:false])
                    if let hit = hits.first {
                        samples += 1
                        let surface = Double(hit.worldCoordinates.y)
                        if surface > nominal {
                            buried.append(["time":race.elapsed,"phase":DirtCourse.phase(x:x,z:z),"x":x,"z":z,"depth":surface-nominal])
                        }
                    }
                }
            }
            if race.finished { break }
        }
        robot.update(simulation); updateOpponents(); cameraMode = 2; updateCamera(snap:true)
        if let tiff = view.snapshot().tiffRepresentation, let png = NSBitmapImageRep(data:tiff)?.representation(using:.png,properties:[:]) {
            try png.write(to:directory.appendingPathComponent("full-race-trails.png"))
        }
        let report: [String:Any] = ["finished":race.finished,"total":race.elapsed,"laps":race.laps,"marks":dirtWorld.trailCounts,
            "groundedMissingFrames":groundedMissing,"smallHopContactFrames":smallHopFrames,"airborneMarkFrames":airborneMarks,"surfaceSamples":samples,"buriedSamples":buried,"takeoffs":air,"flights":flights]
        try JSONSerialization.data(withJSONObject:report,options:[.prettyPrinted,.sortedKeys]).write(to:directory.appendingPathComponent("full-race-trails.json"))
        return race.finished && samples > 500 && smallHopFrames > 0 && airborneMarks == 0 && groundedMissing == 0 && buried.isEmpty
    }
}
