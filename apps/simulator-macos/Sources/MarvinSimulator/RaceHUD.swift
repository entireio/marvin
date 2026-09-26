import AppKit
import SimulationCore

final class RaceHUD: NSView {
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
        panel(22,22,280,210)
        text("DIRT TRACK",40,38,23,true)
        text("LAP \(min(3,race.laps.count+1)) / 3",40,76,18,true)
        text("CURRENT  \(Self.time(race.currentLap))",40,111)
        text("TOTAL    \(Self.time(race.elapsed))",40,140)
        let best = race.laps.min().map(Self.time) ?? "—"
        text("BEST LAP \(best)",40,169)
        text("3 laps · motocross circuit",40,203,12)
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
        let path = NSBezierPath()
        for i in 0...160 {
            let p = DirtCourse.point(Double(i)*2 * .pi/160), at = point(p.x,p.z)
            if i == 0 { path.move(to:at) } else { path.line(to:at) }
        }
        color(0xa7865e).setStroke(); path.lineWidth = 7; path.lineJoinStyle = .round; path.stroke()
        let at = point(self.x,z)
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
            text("FINISH!",middle-182,bounds.height/2-131,30,true)
            for (i,lap) in race.laps.enumerated() { text("Lap \(i+1)       \(Self.time(lap))",middle-182,bounds.height/2-75+CGFloat(i)*32,19) }
            text("TOTAL       \(Self.time(race.elapsed))",middle-182,bounds.height/2+37,21,true)
            text(saveError ?? "Saved to local high scores",middle-182,bounds.height/2+82,12)
            text("⌘R race again · Main Menu to leave",middle-182,bounds.height/2+115,12)
        } else if saveError != nil {
            text("High scores unavailable",x+18,214,12)
        }
    }
}
