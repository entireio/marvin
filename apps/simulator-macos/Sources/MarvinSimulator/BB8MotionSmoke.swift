import AppKit
import SceneKit
import SimulationCore
import simd

extension AppController {
    func checkBB8RenderedMotion(at directory: URL) -> Bool {
        do {
            try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
            let event = NSEvent.keyEvent(with:.keyDown,location:.zero,modifierFlags:[],timestamp:0,
                windowNumber:window.windowNumber,context:nil,characters:"b",charactersIgnoringModifiers:"b",isARepeat:false,keyCode:0)!
            mainMenu.keyDown(with:event)
            var report: [[String:Any]] = []
            var passed = true
            for dirt in [false,true] {
                if dirt { startDirtTrack(); dirtIntro = nil; race.countDown(dt:3) }
                else { startSandbox() }
                // Fix the camera in world space; following the robot can mask
                // a reversed roll. Inspect the source lens and rendered nodes.
                simulation = Simulation(seed:0,dirtTrack:dirt,dirtStartPhase:0,character:.bb8)
                updatePlayerModel()
                let origin = bb8.root.simdPosition
                world.camera.position = SCNVector3(origin.x+1.2,origin.y+0.6,origin.z+0.75)
                world.camera.look(at:SCNVector3(origin.x,origin.y+0.27,origin.z+0.3))
                var previousContact: SIMD3<Float>?, contactInBall: SIMD3<Float>?, previousCenter: SIMD3<Float>?
                for frame in 0...48 {
                    if frame > 0 {
                        var input = DriveInput(); input.throttle = 0.35; input.turn = 0.2
                        simulation.advance(input,dt:1.0/60); updatePlayerModel()
                    }
                    let image = view.snapshot()
                    let center = bb8.ball.presentation.simdWorldPosition
                    var slip = Float(0)
                    if let previousContact, let contactInBall, let previousCenter, !simulation.airborne {
                        let moved = bb8.ball.presentation.simdConvertPosition(contactInBall,to:nil)-previousContact
                        slip = hypot(moved.x,moved.z)
                        let travel = hypot(center.x-previousCenter.x,center.z-previousCenter.z)
                        passed = passed && slip < max(0.00001,travel*0.03)
                    }
                    previousCenter = center
                    previousContact = center+SIMD3<Float>(0,-Float(bb8.ballRadius),0)
                    contactInBall = bb8.ball.presentation.simdConvertPosition(previousContact!,from:nil)
                    let orientation = bb8.ball.simdWorldOrientation
                    let rendered = bb8.ball.presentation.simdWorldOrientation
                    let eye = bb8.head.simdConvertVector(SIMD3<Float>(0,0,1),to:nil)
                    let forward = SIMD3<Float>(Float(sin(simulation.heading)),0,Float(cos(simulation.heading)))
                    let match = abs(simd_dot(orientation.vector,rendered.vector))
                    passed = passed && match > 0.999 && simd_dot(eye,forward) > 0.999
                    if frame.isMultiple(of:8) {
                        guard let tiff = image.tiffRepresentation,
                              let png = NSBitmapImageRep(data:tiff)?.representation(using:.png,properties:[:]) else { return false }
                        try png.write(to:directory.appendingPathComponent("\(dirt ? "race" : "sandbox")-\(frame).png"))
                        report.append(["dirt":dirt,"frame":frame,"x":simulation.x,"z":simulation.z,
                            "contactSlip":slip,"modelVsPresentation":match,"eyeForward":simd_dot(eye,forward)])
                    }
                }
            }
            try JSONSerialization.data(withJSONObject:report,options:[.prettyPrinted,.sortedKeys])
                .write(to:directory.appendingPathComponent("motion.json"))
            return passed
        } catch { print("BB-8 motion check failed: \(error)"); return false }
    }
}
