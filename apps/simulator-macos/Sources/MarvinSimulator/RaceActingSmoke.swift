import AppKit
import SceneKit
import SimulationCore

extension AppController {
    /// Exercise actual articulated nodes as well as the pure attention logic.
    /// Captures neutral, anticipated bend and passing poses in the same lineup.
    func checkRaceActing(at directory: URL) throws -> Bool {
        let offsets = [-1.35,-0.45,0.35,1.25]
        let states = offsets.map { Simulation(dirtTrack:true,dirtStartOffset:$0,dirtStartPhase:0) }
        let characters: [RacePerformance.Character] = [.marvin,.r2d2,.bb8,.wallE]
        let phase = (0..<360).map { Double($0)*2 * .pi/360 }.first { phase in
            let delta = DirtCourse.heading(phase+0.172)-DirtCourse.heading(phase)
            return atan2(sin(delta),cos(delta)) > 0.5
        }!
        var valid = true
        for mode in 0..<3 {
            robot.update(states[0]); r2d2.update(states[1]); bb8.update(states[2]); wallE.update(states[3])
            let originalBall = bb8.ball.simdOrientation
            let originalArms = wallE.arms.map { $0.position }
            var poses: [RacePerformance.Pose] = []
            for character in characters {
                var performer = RacePerformance(character)
                if mode > 0 {
                    let p = DirtCourse.point(mode == 1 ? phase : 0)
                    let heading = DirtCourse.heading(mode == 1 ? phase : 0)
                    for frame in 1...40 {
                        let time = Double(frame)/60
                        let actor = RacePerformance.Actor(x:p.x,z:p.z,heading:heading,speed:6,elapsed:time)
                        let along = 2.4-time*3
                        let peer = RacePerformance.Actor(x:p.x+cos(heading)+sin(heading)*along,z:p.z-sin(heading)+cos(heading)*along,heading:heading,speed:3,elapsed:time)
                        performer.update(index:0,actors:mode == 1 ? [actor] : [actor,peer])
                    }
                    valid = valid && performer.pose.yaw > 0.2
                    if mode == 2 { valid = valid && performer.lookingAt == 1 }
                }
                poses.append(performer.pose)
            }
            robot.applyExpression(poses[0],state:states[0]); r2d2.applyExpression(poses[1])
            bb8.applyExpression(poses[2],heading:states[2].heading)
            wallE.applyExpression(poses[3],heading:states[3].heading)
            valid = valid && abs(Double(robot.yawNode.eulerAngles.y)-poses[0].yaw) < 1e-6
                && abs(Double(r2d2.head.rotation.w)-poses[1].yaw) < 1e-6
                && abs(Double(bb8.head.eulerAngles.y)-poses[2].yaw) < 1e-6
                && abs(Double(wallE.head.eulerAngles.y)-poses[3].yaw) < 1e-6
                && abs(simd_dot(originalBall.vector,bb8.ball.simdOrientation.vector)) > 0.999999
                && wallE.arms.count == 2
            for (arm,anchor) in zip(wallE.arms,originalArms) {
                valid = valid && arm.position.x == anchor.x && arm.position.y == anchor.y && arm.position.z == anchor.z
            }
            let name = ["neutral","curve","passing"][mode]
            let p = DirtCourse.point(0), heading = DirtCourse.heading(0)
            world.camera.position = SCNVector3(p.x+sin(heading)*4.5+cos(heading)*0.7,1.7,p.z+cos(heading)*4.5-sin(heading)*0.7)
            world.camera.look(at:SCNVector3(p.x,0.4,p.z),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
            if let tiff = view.snapshot().tiffRepresentation, let png = NSBitmapImageRep(data:tiff)?.representation(using:.png,properties:[:]) {
                try png.write(to:directory.appendingPathComponent("acting-\(name).png"))
            }
        }
        // Restore real race state; acting checks must not contaminate dirt or
        // the rolling body's displacement history before the driving checks.
        robot.update(simulation); r2d2.update(opponent.simulation)
        bb8.update(bb8Opponent.simulation); wallE.update(wallEOpponent.simulation)
        updateOpponents(); updateCamera(snap:true)
        return valid
    }
}
