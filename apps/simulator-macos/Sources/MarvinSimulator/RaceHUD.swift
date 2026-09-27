import AppKit
import SimulationCore

final class RaceHUD: NSView {
    var opponent = DirtOpponent()
    var race = DirtRace(), scores: [DirtScore] = []
    var x = 0.0, z = -10.0, heading = 0.0
    var introducing = false
    var paused = false, helpVisible = true
    var saveError: String?
    override var isFlipped: Bool { true }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
    static func time(_ seconds: Double) -> String {
        String(format:"%d:%05.2f",Int(seconds)/60,seconds.truncatingRemainder(dividingBy:60))
    }
    private func text(_ string: String, _ x:CGFloat,_ y:CGFloat,_ size:CGFloat = 16,_ bold:Bool = false) {
        (string as NSString).draw(at:NSPoint(x:x,y:y),withAttributes:[.font:NSFont.monospacedSystemFont(ofSize:size,weight:bold ? .bold : .regular),.foregroundColor:color(0xf7eddb)])
    }
    private func panel(_ x:CGFloat,_ y:CGFloat,_ w:CGFloat,_ h:CGFloat) {
        color(0x263029,alpha:0.9).setFill(); NSBezierPath(roundedRect:NSRect(x:x,y:y,width:w,height:h),xRadius:12,yRadius:12).fill()
    }
    override func draw(_ rect:NSRect) {
        panel(22,22,280,264)
        text("DIRT TRACK",40,38,23,true)
        text("LAP \(min(3,race.laps.count+1)) / 3",40,76,18,true)
        text("CURRENT  \(Self.time(race.currentLap))",40,111)
        text("TOTAL    \(Self.time(race.elapsed))",40,140)
        let best = race.laps.min().map(Self.time) ?? "—"
        text("BEST LAP \(best)",40,169)
        text("POSITION \(opponent.playerPosition(race)) / 2",40,202,18,true)
        let rival = opponent.race.finished ? "FINISHED" : "LAP \(min(3,opponent.race.laps.count+1)) / 3"
        text("R2-D2    \(rival)",40,235,13)
        text("YOU · GOLD    R2-D2 · BLUE",40,262,10)
        let x = bounds.width-292
        panel(x,22,270,250)
        text("LOCAL HIGH SCORES",x+18,40,17,true)
        if scores.isEmpty { text("Set the first time!",x+18,80,14) }
        for (i,score) in scores.prefix(5).enumerated() {
            text("\(i+1).  \(Self.time(score.total))",x+18,78+CGFloat(i)*28,16)
        }
        text("Fastest 3-lap totals",x+18,238,12)
        panel(x,286,270,212)
        text("COURSE",x+18,300,12,true)
        let map = NSRect(x:x+18,y:330,width:234,height:150)
        func point(_ px:Double,_ pz:Double) -> NSPoint {
            NSPoint(x:map.maxX-CGFloat((px+21)/42)*map.width,y:map.maxY-CGFloat((pz+20)/42)*map.height)
        }
        let coursePoints = (0...160).map { i -> NSPoint in
            let p = DirtCourse.point(Double(i)*2 * .pi/160)
            return point(p.x,p.z)
        }
        let path = NSBezierPath()
        for (i, at) in coursePoints.enumerated() {
            if i == 0 { path.move(to:at) } else { path.line(to:at) }
        }
        // Show race progress on the exact polyline drawn above, independent
        // of lateral position on the wide course or beyond its edges.
        func marker(_ px: Double, _ pz: Double) -> NSPoint {
            let progress = DirtCourse.phase(x:px,z:pz)/(2 * .pi)*160
            let index = min(159,max(0,Int(progress)))
            let fraction = CGFloat(progress-Double(index))
            let a = coursePoints[index], b = coursePoints[index+1]
            return NSPoint(x:a.x+(b.x-a.x)*fraction,y:a.y+(b.y-a.y)*fraction)
        }
        color(0xa7865e).setStroke(); path.lineWidth = 10.5; path.lineJoinStyle = .round; path.stroke()
        // Phase zero is the same crossing used by the race timer and track.
        let start = DirtCourse.point(0), ahead = DirtCourse.point(0.001)
        let finishAt = point(start.x,start.z), finishAhead = point(ahead.x,ahead.z)
        NSGraphicsContext.saveGraphicsState()
        let transform = AffineTransform(translationByX:finishAt.x,byY:finishAt.y)
        var oriented = transform
        oriented.rotate(byRadians:atan2(finishAhead.y-finishAt.y,finishAhead.x-finishAt.x))
        (oriented as NSAffineTransform).concat()
        for row in 0..<4 {
            for column in 0..<2 {
                color((row+column).isMultiple(of:2) ? 0xf7eddb : 0x18221d).setFill()
                NSRect(x:CGFloat(column)*4-4,y:CGFloat(row)*4-8,width:4,height:4).fill()
            }
        }
        color(0x18221d).setStroke()
        let border = NSBezierPath(rect:NSRect(x:-4,y:-8,width:8,height:16))
        border.lineWidth = 1; border.stroke()
        NSGraphicsContext.restoreGraphicsState()
        text("START / FINISH",finishAt.x-38,finishAt.y+12,9,true)
        let rivalAt = marker(opponent.simulation.x,opponent.simulation.z)
        color(0x58baff).setFill()
        NSBezierPath(ovalIn:NSRect(x:rivalAt.x-4,y:rivalAt.y-4,width:8,height:8)).fill()
        let at = marker(self.x,z)
        color(0xffd78d).setFill(); NSBezierPath(ovalIn:NSRect(x:at.x-4,y:at.y-4,width:8,height:8)).fill()
        let direction = NSBezierPath(); direction.move(to:at)
        direction.line(to:NSPoint(x:at.x-sin(heading)*10,y:at.y-cos(heading)*10))
        color(0xffd78d).setStroke(); direction.lineWidth = 2; direction.stroke()
        if helpVisible {
            let lines = ["DRIVE W A S D / arrows   BOOST Shift   BRAKE Space", "CAMERA C / drag / scroll   PAUSE P / Esc   RESTART ⌘R"]
            let font = NSFont.monospacedSystemFont(ofSize:12,weight:.regular)
            let width = min(bounds.width-44,lines.map { ($0 as NSString).size(withAttributes:[.font:font]).width }.max()!+36)
            panel(22,bounds.height-84,width,62)
            text("DRIVE W A S D / arrows   BOOST Shift   BRAKE Space",40,bounds.height-71,12)
            text("CAMERA C / drag / scroll   PAUSE P / Esc   RESTART ⌘R",40,bounds.height-46,12)
        }
        let middle = bounds.width/2
        if !introducing && (race.countdown > 0 || paused) {
            let message = paused ? "PAUSED" : "READY  \(Int(ceil(race.countdown)))"
            panel(middle-120, bounds.height/2-45,240,90)
            text(message,middle-98,bounds.height/2-17,26,true)
        }
        if race.finished {
            panel(middle-210,bounds.height/2-155,420,310)
            text(opponent.playerPosition(race) == 1 ? "1ST · YOU WIN!" : "2ND · R2-D2 WINS",middle-182,bounds.height/2-131,26,true)
            for (i,lap) in race.laps.enumerated() { text("Lap \(i+1)       \(Self.time(lap))",middle-182,bounds.height/2-75+CGFloat(i)*32,19) }
            text("TOTAL       \(Self.time(race.elapsed))",middle-182,bounds.height/2+37,21,true)
            text(saveError ?? "Saved to local high scores",middle-182,bounds.height/2+82,12)
            text("⌘R race again · Main Menu to leave",middle-182,bounds.height/2+115,12)
        } else if saveError != nil {
            text("High scores unavailable",x+18,214,12)
        }
    }
}
