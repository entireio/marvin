import AppKit
import SceneKit
import SimulationCore

final class SimulatorView: SCNView {
    var held = Set<UInt16>()
    var onCommand: ((UInt16) -> Void)?
    var onOrbit: ((CGFloat, CGFloat) -> Void)?
    var onZoom: ((CGFloat) -> Void)?
    var onFocusLost: (() -> Void)?
    override var acceptsFirstResponder: Bool { true }
    override func keyDown(with event: NSEvent) {
        guard !event.modifierFlags.contains(.command) else { super.keyDown(with: event); return }
        let movement: Set<UInt16> = [0,1,2,13,123,124,125,126,12,14,15,3,49]
        if movement.contains(event.keyCode) { held.insert(event.keyCode) }
        else if !event.isARepeat { onCommand?(event.keyCode) }
    }
    override func keyUp(with event: NSEvent) { held.remove(event.keyCode) }
    override func flagsChanged(with event: NSEvent) {
        if event.modifierFlags.contains(.shift) { held.insert(56) } else { held.remove(56) }
    }
    override func resignFirstResponder() -> Bool {
        clearInput(); return super.resignFirstResponder()
    }
    func clearInput() { held.removeAll(); onFocusLost?() }
    override func mouseDown(with event: NSEvent) { window?.makeFirstResponder(self) }
    override func mouseDragged(with event: NSEvent) { onOrbit?(event.deltaX, event.deltaY) }
    override func rightMouseDragged(with event: NSEvent) { onOrbit?(event.deltaX, event.deltaY) }
    override func scrollWheel(with event: NSEvent) { onZoom?(event.scrollingDeltaY) }
    var driveInput: DriveInput {
        func down(_ codes: UInt16...) -> Double { codes.contains(where: { held.contains($0) }) ? 1 : 0 }
        var input = DriveInput()
        input.throttle = down(13,126)-down(1,125)
        input.turn = down(2,124)-down(0,123)
        input.headYaw = down(14)-down(12)
        input.headPitch = down(15)-down(3)
        input.boost = held.contains(56); input.brake = held.contains(49)
        return input
    }
}

final class HUDView: NSView {
    var state = Simulation()
    var cameraName = "FOLLOW"
    var fps = 60
    var helpVisible = true
    override var isFlipped: Bool { true }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
    private func text(_ value: String, _ x: CGFloat, _ y: CGFloat, size: CGFloat = 12,
                      ink: UInt32 = 0x304e44, weight: NSFont.Weight = .regular, mono: Bool = false) {
        let font = mono ? NSFont.monospacedSystemFont(ofSize: size, weight: weight) : NSFont.systemFont(ofSize: size, weight: weight)
        (value as NSString).draw(at: NSPoint(x: x, y: y), withAttributes: [.font: font, .foregroundColor: color(ink)])
    }
    private func card(_ rect: NSRect) {
        color(0xf5f6ef, alpha: 0.94).setFill()
        let path = NSBezierPath(roundedRect: rect, xRadius: 14, yRadius: 14); path.fill()
        color(0x95ada0, alpha: 0.35).setStroke(); path.lineWidth = 1; path.stroke()
    }
    override func draw(_ dirtyRect: NSRect) {
        let w = bounds.width, h = bounds.height
        card(NSRect(x: 22, y: 22, width: 280, height: 96))
        text("M A R V I N   /   S I M U L A T O R", 40, 38, size: 10, mono: true)
        text("A little room to explore.", 40, 58, size: 21, weight: .semibold)
        let status = state.paused ? "PAUSED" : state.contacting ? "OBSTACLE · turn or reverse" : "READY TO ROAM"
        text(status, 40, 91, size: 10, ink: state.contacting ? 0xa56932 : 0x337e69, mono: true)
        let px = w-252
        card(NSRect(x: px, y: 22, width: 230, height: 342))
        text("EXPLORATION COURSE", px+18, 39, size: 10, mono: true)
        text(state.complete ? "Course complete!" : "Find beacon \(state.checkpoint+1) of 5", px+18, 60, size: 18, weight: .semibold)
        text(state.complete ? "Keep exploring, or reset to go again." : "Drive through the amber ring.", px+18, 86, size: 11)
        let map = NSRect(x: px+18, y: 115, width: 194, height: 162)
        color(0xe1e8dd).setFill(); NSBezierPath(roundedRect: map, xRadius: 6, yRadius: 6).fill()
        func point(_ x: Double, _ z: Double) -> NSPoint {
            NSPoint(x: map.minX+CGFloat((x+6)/12)*map.width,
                    y: map.maxY-CGFloat((z+5)/10)*map.height)
        }
        for o in Simulation.obstacles {
            let p = point(o.x-o.width/2, o.z+o.depth/2)
            color(0x9aafa2).setFill()
            NSBezierPath(roundedRect: NSRect(x: p.x, y: p.y, width: o.width/12*map.width,
                                            height: o.depth/10*map.height), xRadius: 2, yRadius: 2).fill()
        }
        for (i, p) in state.checkpoints.enumerated() {
            let at = point(p.x, p.z)
            color(i < state.checkpoint ? 0x2b8e7f : i == state.checkpoint ? 0xc88547 : 0xa3b8ae).setFill()
            NSBezierPath(ovalIn: NSRect(x: at.x-4, y: at.y-4, width: 8, height: 8)).fill()
        }
        let p = point(state.x, state.z), angle = state.heading
        let marker = NSBezierPath()
        marker.move(to: NSPoint(x: p.x+sin(angle)*8, y: p.y-cos(angle)*8))
        marker.line(to: NSPoint(x: p.x+sin(angle+2.4)*6, y: p.y-cos(angle+2.4)*6))
        marker.line(to: NSPoint(x: p.x+sin(angle-2.4)*6, y: p.y-cos(angle-2.4)*6))
        marker.close(); color(0x214d40).setFill(); marker.fill()
        text(String(format: "SPEED   %3.0f mm/s", (state.contacting ? 0 : abs(state.speed))*100), px+18, 292, size: 11, mono: true)
        text(String(format: "TRAVEL  %.2f m", state.distance/10), px+18, 313, size: 11, mono: true)
        text("\(cameraName) CAMERA", px+18, 336, size: 9, ink: 0x6d8678, mono: true)
        if helpVisible {
            let columns = [
                [("DRIVE", "W A S D / ↑ ↓ ← →"), ("HEAD", "Q / E · R / F"), ("PAUSE", "P / Esc")],
                [("BRAKE", "Space"), ("CENTER", "H"), ("RESET", "⌘R")],
                [("BOOST", "Shift"), ("CAMERA", "C · drag / scroll"), ("HELP", "?")],
            ]
            let font = NSFont.monospacedSystemFont(ofSize: 11, weight: .regular)
            let keyFont = NSFont.monospacedSystemFont(ofSize: 11, weight: .medium)
            func measure(_ value: String, _ font: NSFont) -> NSSize {
                (value as NSString).size(withAttributes: [.font: font])
            }
            let padding: CGFloat = 18, labelGap: CGFloat = 12, columnGap: CGFloat = 28, rowGap: CGFloat = 8
            let labelWidths = columns.map { column in
                ceil(column.map { measure($0.0, font).width }.max() ?? 0)
            }
            let keyWidths = columns.map { column in
                ceil(column.map { measure($0.1, keyFont).width }.max() ?? 0)
            }
            let rowHeight = ceil(columns.flatMap { $0 }.map {
                max(measure($0.0, font).height, measure($0.1, keyFont).height)
            }.max() ?? 0)
            let width = labelWidths.reduce(0, +) + keyWidths.reduce(0, +)
                + 3*labelGap + 2*columnGap + 2*padding
            let height = 3*rowHeight + 2*rowGap + 2*padding
            let panel = NSRect(x: 22, y: h-22-height, width: width, height: height)
            card(panel)
            var x = panel.minX + padding
            for (columnIndex, column) in columns.enumerated() {
                for (rowIndex, entry) in column.enumerated() {
                    let y = panel.minY + padding + CGFloat(rowIndex)*(rowHeight+rowGap)
                    text(entry.0, x, y, size: 11, ink: 0x6d8678, mono: true)
                    text(entry.1, x+labelWidths[columnIndex]+labelGap, y,
                         size: 11, weight: .medium, mono: true)
                }
                x += labelWidths[columnIndex] + labelGap + keyWidths[columnIndex] + columnGap
            }
        }
        if state.paused {
            card(NSRect(x: w/2-135, y: h/2-47, width: 270, height: 94))
            text("Taking a little break.", w/2-110, h/2-24, size: 21, weight: .semibold)
            text("Press P or click Resume to continue", w/2-110, h/2+9, size: 12)
        }
    }
}
