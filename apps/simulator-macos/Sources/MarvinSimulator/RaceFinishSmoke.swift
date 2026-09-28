import AppKit
import SceneKit
import SimulationCore

extension AppController {
    func checkRaceFinish(at directory:URL) throws -> Bool {
        let finishTime = race.elapsed, distance = simulation.distance
        dirtIntro = nil
        for _ in 0..<210 { advanceRaceFrame(step:1.0/60,raceDelta:1.0/60,advancing:true); updateCamera(snap:false) }
        var passed = race.finished && race.elapsed == finishTime && simulation.distance > distance+0.2
            && dirtOutro == dirtIntroDuration && abs(world.camera.position.y-38) < 0.001
        let before = simulation.elapsed, animation = dirtOutro
        advanceRaceFrame(step:0.1,raceDelta:0.1,advancing:false)
        passed = passed && simulation.elapsed == before && dirtOutro == animation
        let frame = raceHUD.frame
        defer { raceHUD.frame = frame; raceHUD.paused = false }
        raceHUD.frame = NSRect(x:0,y:0,width:900,height:550)
        func capture(_ name:String) throws {
            robot.update(simulation); updateOpponents()
            raceHUD.race = race; raceHUD.introducing = false
            let image = NSImage(size:raceHUD.bounds.size)
            image.lockFocus()
            view.snapshot().draw(in:raceHUD.bounds)
            // Draw the overlay directly: AppKit cacheDisplay cannot capture the
            // Metal layer beneath it and would composite an opaque black base.
            NSGraphicsContext.saveGraphicsState()
            let transform = NSAffineTransform()
            transform.translateX(by:0,yBy:raceHUD.bounds.height); transform.scaleX(by:1,yBy:-1)
            transform.concat()
            NSGraphicsContext.current = NSGraphicsContext(cgContext:NSGraphicsContext.current!.cgContext,flipped:true)
            raceHUD.draw(raceHUD.bounds)
            NSGraphicsContext.restoreGraphicsState()
            image.unlockFocus()
            if let tiff = image.tiffRepresentation, let png = NSBitmapImageRep(data:tiff)?.representation(using:.png,properties:[:]) {
                try png.write(to:directory.appendingPathComponent(name))
            }
        }
        try capture("race-finish-live.png")
        for _ in 0..<60*180 {
            if opponents.allSatisfy({ $0.race.finished }) { break }
            advanceRaceFrame(step:1.0/60,raceDelta:1.0/60,advancing:true)
        }
        passed = passed && opponents.allSatisfy { $0.race.finished }
        try capture("race-results.png")
        reset(nil)
        passed = passed && dirtOutro == nil
        try capture("race-start.png")
        raceHUD.paused = true
        try capture("race-pause.png")
        return passed
    }
}
