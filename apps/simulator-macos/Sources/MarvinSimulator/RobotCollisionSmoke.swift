import AppKit
import SceneKit
import SimulationCore

extension AppController {
    func checkSandboxContact(at directory:URL) throws -> Bool {
        var contact = Simulation(seed:0), input = DriveInput(); input.throttle = 1
        for _ in 0..<600 { contact.advance(input,dt:1.0/60) }
        let face = 3.3-0.65/2
        let clearance = face-contact.z-Simulation.bodyHalfDepth
        robot.update(contact)
        world.camera.position = SCNVector3(1.4,1.2,contact.z-1.1)
        world.camera.look(at:SCNVector3(0,0.28,contact.z+0.15),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
        if let tiff = view.snapshot().tiffRepresentation, let png = NSBitmapImageRep(data:tiff)?.representation(using:.png,properties:[:]) {
            try png.write(to:directory.appendingPathComponent("sandbox-contact.png"))
        }
        robot.update(simulation); updateCamera(snap:true)
        return contact.contacting && clearance >= Simulation.collisionClearance-1e-8 && clearance < 0.025
            && Simulation.isFree(x:contact.x,z:contact.z,heading:contact.heading)
    }

    func checkRobotContacts(at directory: URL) throws -> Bool {
        // Deliberately crowded setup exercises separation through the same race
        // controller used by interactive play, then renders the resulting poses.
        var player = Simulation(seed:0,dirtTrack:true,dirtStartOffset:0,dirtStartPhase:-0.055)
        var clock = DirtRace(startPhase:-0.055), physics = DirtRacePhysics()
        var rivals = [DirtOpponent(slot:(phase:-0.055,offset:0.35)),
                      DirtOpponent(slot:(phase:-0.14,offset:-0.72),laneOffset:0),
                      DirtOpponent(slot:(phase:-0.14,offset:0.72),laneOffset:-0.65)]
        clock.countDown(dt:3)
        var input = DriveInput(); input.throttle = 1
        for _ in 0..<30 { physics.advance(input,player:&player,race:&clock,opponents:&rivals,dt:1.0/60,raceDT:1.0/60) }
        let states = [player]+rivals.map { $0.simulation }
        let bodies = states.enumerated().map { i,state in
            RobotCollisions.Body(position:SIMD3(state.x,state.groundY,state.z),velocity:state.velocity,heading:state.heading,angularVelocity:state.angularVelocity,profile:RobotCollisions.profiles[i])
        }
        var passed = physics.contactCount > 0 && states.allSatisfy { $0.robotDynamics }
        for i in bodies.indices { for j in bodies.indices where j > i {
            passed = passed && (RobotCollisions.contact(bodies[i],bodies[j])?.penetration ?? 0) < 0.003
        } }
        robot.update(player); r2d2.update(rivals[0].simulation)
        bb8.update(rivals[1].simulation); wallE.update(rivals[2].simulation)
        let heading = player.heading
        world.camera.position = SCNVector3(player.x+sin(heading)*3.8+cos(heading)*1.4,player.groundY+1.8,player.z+cos(heading)*3.8-sin(heading)*1.4)
        world.camera.look(at:SCNVector3(player.x,player.groundY+0.35,player.z),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
        if let tiff = view.snapshot().tiffRepresentation, let png = NSBitmapImageRep(data:tiff)?.representation(using:.png,properties:[:]) {
            try png.write(to:directory.appendingPathComponent("robot-contact.png"))
        }
        let before = states.map { SIMD3($0.x,$0.groundY,$0.z) }
        let velocities = states.map { $0.velocity }
        player.paused = true
        physics.advance(input,player:&player,race:&clock,opponents:&rivals,dt:0.1,raceDT:0.1)
        passed = passed && before == ([player]+rivals.map { $0.simulation }).map { SIMD3($0.x,$0.groundY,$0.z) }
        passed = passed && velocities == ([player]+rivals.map { $0.simulation }).map { $0.velocity }
        robot.update(simulation); updateOpponents(); updateCamera(snap:true)
        return passed
    }
}
