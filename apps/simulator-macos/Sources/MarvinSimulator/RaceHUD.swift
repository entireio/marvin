import AppKit
import SimulationCore

final class RaceHUD: NSView {
    var opponents: [DirtOpponent] = []
    let racerNames = ["R2-D2", "BB-8", "WALL-E"]
    let racerColors: [UInt32] = [0x58baff, 0xff914b, 0xf2c94c]
    var position: Int { 1 + opponents.filter { $0.playerPosition(race) == 2 }.count }
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
    private func textAttributes(_ size:CGFloat, _ bold:Bool = false) -> [NSAttributedString.Key:Any] {
        [.font:NSFont.monospacedSystemFont(ofSize:size,weight:bold ? .bold : .regular),.foregroundColor:color(0xf7eddb)]
    }
    private func text(_ string: String, _ x:CGFloat,_ y:CGFloat,_ size:CGFloat = 16,_ bold:Bool = false) {
        (string as NSString).draw(at:NSPoint(x:x,y:y),withAttributes:textAttributes(size,bold))
    }
    private func panel(_ x:CGFloat,_ y:CGFloat,_ w:CGFloat,_ h:CGFloat) {
        color(0x263029,alpha:0.9).setFill(); NSBezierPath(roundedRect:NSRect(x:x,y:y,width:w,height:h),xRadius:12,yRadius:12).fill()
    }
    override func draw(_ rect:NSRect) {
        if race.finished && !paused { drawResults(); return }
        let races = [race]+opponents.map { $0.race }
        let inset:CGFloat = 18, rowsY:CGFloat = 242, rowSpacing:CGFloat = 22
        let rowHeight = ceil(("Marvin" as NSString).size(withAttributes:textAttributes(13)).height)
        let panelBottom = rowsY+CGFloat(races.count-1)*rowSpacing+rowHeight+inset
        let best = race.laps.min().map(Self.time) ?? "—"
        let lines: [(value:String,y:CGFloat,size:CGFloat,bold:Bool)] = [
            ("DIRT TRACK",40,23,true),
            ("LAP \(min(3,race.laps.count+1)) / 3",76,18,true),
            ("CURRENT  \(Self.time(race.currentLap))",111,16,false),
            ("TOTAL    \(Self.time(race.elapsed))",140,16,false),
            ("BEST LAP \(best)",169,16,false),
            ("POSITION \(position) / 4",202,18,true)
        ]
        let names = ["Marvin"]+racerNames, colors = [Robot.silverColor]+racerColors
        let standings = races.enumerated().map { i,competitor in
            let status = competitor.finished ? "FINISHED" : "LAP \(min(3,competitor.laps.count+1)) / 3"
            return "\(names[i].padding(toLength:8,withPad:" ",startingAt:0))\(status)"
        }
        // Measure the actual rendered strings so every edge keeps the same
        // inset, expanding only when longer times or standings need the space.
        let lineWidths = lines.map { ($0.value as NSString).size(withAttributes:textAttributes($0.size,$0.bold)).width }
        let rowWidths = standings.map { 15+($0 as NSString).size(withAttributes:textAttributes(13)).width }
        let panelWidth = ceil((lineWidths+rowWidths).max() ?? 0)+inset*2
        panel(22,22,panelWidth,panelBottom-22)
        for line in lines { text(line.value,40,line.y,line.size,line.bold) }
        for (i, standing) in standings.enumerated() {
            color(colors[i]).setFill()
            let rowY = rowsY+CGFloat(i)*rowSpacing
            NSBezierPath(ovalIn:NSRect(x:40,y:rowY+(rowHeight-7)/2,width:7,height:7)).fill()
            text(standing,55,rowY,13)
        }
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
        for (i, opponent) in opponents.enumerated() {
            let rivalAt = marker(opponent.simulation.x,opponent.simulation.z)
            color(racerColors[i]).setFill()
            NSBezierPath(ovalIn:NSRect(x:rivalAt.x-4,y:rivalAt.y-4,width:8,height:8)).fill()
        }
        let at = marker(self.x,z)
        color(Robot.silverColor).setFill(); NSBezierPath(ovalIn:NSRect(x:at.x-4,y:at.y-4,width:8,height:8)).fill()
        let direction = NSBezierPath(); direction.move(to:at)
        direction.line(to:NSPoint(x:at.x-sin(heading)*10,y:at.y-cos(heading)*10))
        color(Robot.silverColor).setStroke(); direction.lineWidth = 2; direction.stroke()
        if helpVisible {
            let lines = ["DRIVE W A S D / arrows   BOOST Shift   BRAKE Space", "CAMERA C / drag / scroll   PAUSE P / Esc   RESTART ⌘R"]
            let font = NSFont.monospacedSystemFont(ofSize:12,weight:.regular)
            let width = min(bounds.width-44,lines.map { ($0 as NSString).size(withAttributes:[.font:font]).width }.max()!+36)
            panel(22,bounds.height-84,width,62)
            text("DRIVE W A S D / arrows   BOOST Shift   BRAKE Space",40,bounds.height-71,12)
            text("CAMERA C / drag / scroll   PAUSE P / Esc   RESTART ⌘R",40,bounds.height-46,12)
        }
        if paused { drawModal(paused:true) }
        else if !introducing && race.countdown > 0 { drawModal(paused:false) }
        if saveError != nil { text("High scores unavailable",x+18,214,12) }
    }

    private func label(_ string:String, in rect:NSRect, size:CGFloat, weight:NSFont.Weight = .regular,
                       tint:UInt32 = 0xf7eddb, alignment:NSTextAlignment = .left) {
        let style = NSMutableParagraphStyle(); style.alignment = alignment
        (string as NSString).draw(in:rect,withAttributes:[.font:NSFont.systemFont(ofSize:size,weight:weight),
            .foregroundColor:color(tint),.paragraphStyle:style])
    }
    private func rule(_ rect:NSRect) { color(0xf7eddb,alpha:0.15).setFill(); rect.fill() }
    private func drawModal(paused:Bool) {
        color(0x101a15,alpha:0.42).setFill(); bounds.fill()
        let w:CGFloat = 380, h:CGFloat = paused ? 250 : 290
        let x = (bounds.width-w)/2, y = (bounds.height-h)/2
        panel(x,y,w,h)
        label(paused ? "TAKE A BREATHER" : "DIRT TRACK · 3 LAPS",in:NSRect(x:x+24,y:y+26,width:w-48,height:22),size:12,weight:.semibold,tint:0xffd78d,alignment:.center)
        label(paused ? "Paused" : "\(Int(ceil(race.countdown)))",in:NSRect(x:x+24,y:y+58,width:w-48,height:100),size:paused ? 44 : 80,weight:.bold,alignment:.center)
        label(paused ? "The race is waiting for you." : "Get ready to race",in:NSRect(x:x+24,y:y+h-106,width:w-48,height:25),size:17,alignment:.center)
        rule(NSRect(x:x+28,y:y+h-68,width:w-56,height:1))
        label(paused ? "P / Esc  Resume    ·    ⌘R  Restart" : "WASD / ↑↓←→  Drive    ·    Shift  Boost",in:NSRect(x:x+16,y:y+h-46,width:w-32,height:24),size:13,weight:.medium,tint:0xffd78d,alignment:.center)
    }
    private func drawResults() {
        let races = [race]+opponents.map { $0.race }
        let names = ["Marvin"]+racerNames, colors = [Robot.silverColor]+racerColors
        let order = DirtStandings.order(races)
        let complete = races.allSatisfy { $0.finished }
        let w = min(CGFloat(560),bounds.width-44), h:CGFloat = 344
        let x = bounds.width-w-22, y = bounds.height-h-22
        panel(x,y,w,h)
        label(complete ? "FINAL CLASSIFICATION" : "LIVE CLASSIFICATION",in:NSRect(x:x+24,y:y+22,width:w-48,height:20),size:11,weight:.bold,tint:0xffd78d)
        label("Race finished",in:NSRect(x:x+24,y:y+44,width:w-48,height:42),size:30,weight:.bold)
        let totalX = x+w-216, lapX = x+w-108
        label("ROBOT",in:NSRect(x:x+58,y:y+106,width:190,height:20),size:10,weight:.bold,tint:0xb8c2b6)
        label("TOTAL",in:NSRect(x:totalX,y:y+106,width:96,height:20),size:10,weight:.bold,tint:0xb8c2b6)
        label("BEST LAP",in:NSRect(x:lapX,y:y+106,width:96,height:20),size:10,weight:.bold,tint:0xb8c2b6)
        for (rank,index) in order.enumerated() {
            let rowY = y+134+CGFloat(rank)*36, result = races[index]
            rule(NSRect(x:x+24,y:rowY-8,width:w-48,height:1))
            label("\(rank+1)",in:NSRect(x:x+24,y:rowY,width:28,height:24),size:16,weight:.bold,tint:colors[index])
            label(names[index],in:NSRect(x:x+58,y:rowY,width:w-282,height:24),size:16,weight:index == 0 ? .bold : .medium,tint:colors[index])
            text(result.finished ? Self.time(result.elapsed) : "LAP \(result.laps.count+1)/3",totalX,rowY,14)
            text(result.laps.min().map(Self.time) ?? "—",lapX,rowY,14)
        }
        label(saveError ?? (complete ? "Autopilot · Enjoy the cooldown lap" : "Autopilot · Waiting for the remaining finishers"),in:NSRect(x:x+24,y:y+282,width:w-48,height:22),size:12,tint:0xb8c2b6)
        label("⌘R  Race again    ·    Main Menu to leave",in:NSRect(x:x+24,y:y+309,width:w-48,height:22),size:12,weight:.medium,tint:0xffd78d)
    }
}
