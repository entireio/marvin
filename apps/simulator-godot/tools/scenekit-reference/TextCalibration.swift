// CoreText reference for the facade's text rendering (FontSmoothing, NSAttributedString metrics).
//
//   swiftc -O apps/simulator-godot/tools/scenekit-reference/TextCalibration.swift -o /tmp/text-calibration
//   /tmp/text-calibration MAC_DIR
//   apps/simulator-godot/tools/godot -- --text-calibration GODOT_DIR
//   python3 apps/simulator-godot/tools/scenekit-reference/text_calibration.py MAC_DIR GODOT_DIR
//
// Writes one PNG per font (rows: raw black/white with font smoothing off, plain NSString.draw(at:) in sRGB greys
// 0...1 over the view's own fill, the same greys as NSTextField labels over a layer-backed parent) and widths.json
// (NSString.size(withAttributes:) of the game's strings). Keep the case list in step with TextCalibration.cs.
import AppKit
struct FontSpec { let name: String; let mono: Bool; let size: CGFloat; let weight: NSFont.Weight }
let specs: [FontSpec] = [
    .init(name: "sys80b", mono: false, size: 80, weight: .bold), .init(name: "sys58b", mono: false, size: 58, weight: .bold),
    .init(name: "sys44b", mono: false, size: 44, weight: .bold), .init(name: "sys34b", mono: false, size: 34, weight: .bold),
    .init(name: "sys30b", mono: false, size: 30, weight: .bold), .init(name: "sys26s", mono: false, size: 26, weight: .semibold),
    .init(name: "sys21m", mono: false, size: 21, weight: .medium), .init(name: "sys18r", mono: false, size: 18, weight: .regular),
    .init(name: "sys17r", mono: false, size: 17, weight: .regular), .init(name: "sys16b", mono: false, size: 16, weight: .bold),
    .init(name: "sys16m", mono: false, size: 16, weight: .medium), .init(name: "sys13m", mono: false, size: 13, weight: .medium),
    .init(name: "sys13r", mono: false, size: 13, weight: .regular), .init(name: "sys12s", mono: false, size: 12, weight: .semibold),
    .init(name: "sys12m", mono: false, size: 12, weight: .medium), .init(name: "sys12r", mono: false, size: 12, weight: .regular),
    .init(name: "sys11b", mono: false, size: 11, weight: .bold), .init(name: "sys11r", mono: false, size: 11, weight: .regular),
    .init(name: "sys10b", mono: false, size: 10, weight: .bold),
    .init(name: "mono23b", mono: true, size: 23, weight: .bold), .init(name: "mono18b", mono: true, size: 18, weight: .bold),
    .init(name: "mono17b", mono: true, size: 17, weight: .bold), .init(name: "mono16r", mono: true, size: 16, weight: .regular),
    .init(name: "mono14r", mono: true, size: 14, weight: .regular), .init(name: "mono13r", mono: true, size: 13, weight: .regular),
    .init(name: "mono12r", mono: true, size: 12, weight: .regular), .init(name: "mono12b", mono: true, size: 12, weight: .bold),
    .init(name: "mono9b", mono: true, size: 9, weight: .bold),
]
let text = "Race 1:29.18"
let greys: [CGFloat] = [0, 0.25, 0.5, 0.75, 1]
// case list: (kind, fg)
var cases: [(String, CGFloat)] = [("raw", 0), ("raw", 1)]
for g in greys { cases.append(("plain", g)) }
for g in greys { cases.append(("label", g)) }
func grey(_ v: CGFloat) -> NSColor { NSColor(srgbRed: v, green: v, blue: v, alpha: 1) }
final class TextView: NSView {
    var font = NSFont.systemFont(ofSize: 12), fg = NSColor.black, bg = NSColor.white, smooth = true
    override var isFlipped: Bool { true }
    override func draw(_ r: NSRect) {
        let cg = NSGraphicsContext.current!.cgContext
        if !smooth { cg.setAllowsFontSmoothing(true); cg.setShouldSmoothFonts(false) }
        bg.setFill(); bounds.fill()
        (text as NSString).draw(at: NSPoint(x: 8, y: 6), withAttributes: [.font: font, .foregroundColor: fg])
    }
}
let outDir = CommandLine.arguments[1]
try? FileManager.default.createDirectory(atPath: outDir, withIntermediateDirectories: true)
NSApplication.shared.setActivationPolicy(.prohibited)
for spec in specs {
    let font = spec.mono ? NSFont.monospacedSystemFont(ofSize: spec.size, weight: spec.weight) : NSFont.systemFont(ofSize: spec.size, weight: spec.weight)
    let rowH = ceil(spec.size * 1.5) + 12, width = ceil(spec.size * 0.62 * CGFloat(text.count)) + 24
    let height = rowH * CGFloat(cases.count)
    let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: width, height: height), styleMask: [.titled], backing: .buffered, defer: false)
    window.colorSpace = .sRGB
    let root = NSView(frame: NSRect(x: 0, y: 0, width: width, height: height)); root.wantsLayer = true; root.layer?.backgroundColor = NSColor.gray.cgColor
    window.contentView = root
    for (i, (kind, g)) in cases.enumerated() {
        let bg = g >= 0.5 ? grey(0) : grey(1)
        let frame = NSRect(x: 0, y: height - rowH * CGFloat(i + 1), width: width, height: rowH)
        if kind == "label" {
            let parent = NSView(frame: frame); parent.wantsLayer = true; parent.layer?.backgroundColor = bg.cgColor; root.addSubview(parent)
            let l = NSTextField(labelWithString: text); l.font = font; l.textColor = grey(g)
            l.frame = NSRect(x: 6, y: 0, width: width - 12, height: rowH - 6); parent.addSubview(l)
        } else {
            let t = TextView(frame: frame); t.font = font; t.fg = grey(g); t.bg = bg; t.smooth = kind != "raw"; root.addSubview(t)
        }
    }
    root.layoutSubtreeIfNeeded()
    let rep = root.bitmapImageRepForCachingDisplay(in: root.bounds)!
    root.cacheDisplay(in: root.bounds, to: rep)
    try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: "\(outDir)/\(spec.name).png"))
}
// Horizontal glyph positioning: each string drawn with draw(at:) at x = 20 + 0.05 k (k = 0...19), one 70 px row each.
final class PositionView: NSView {
    var font = NSFont.systemFont(ofSize: 12), text = "H", smooth = true
    override var isFlipped: Bool { true }
    override func draw(_ r: NSRect) {
        if !smooth { NSGraphicsContext.current!.cgContext.setAllowsFontSmoothing(true); NSGraphicsContext.current!.cgContext.setShouldSmoothFonts(false) }
        NSColor.black.setFill(); bounds.fill()
        for k in 0..<20 { (text as NSString).draw(at: NSPoint(x: 20 + 0.05 * CGFloat(k), y: 10 + CGFloat(k) * 70), withAttributes: [.font: font, .foregroundColor: NSColor.white]) }
    }
}
for (name, font, text) in positionCases() { for smooth in [true, false] {
    let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 400, height: 1420), styleMask: [.titled], backing: .buffered, defer: false)
    window.colorSpace = .sRGB
    let v = PositionView(frame: NSRect(x: 0, y: 0, width: 400, height: 1420)); v.font = font; v.text = text; v.smooth = smooth
    window.contentView = v
    let rep = v.bitmapImageRepForCachingDisplay(in: v.bounds)!; v.cacheDisplay(in: v.bounds, to: rep)
    try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: "\(outDir)/positions-\(smooth ? "" : "raw-")\(name).png"))
} }
func positionCases() -> [(String, NSFont, String)] {
    [("mono12r", NSFont.monospacedSystemFont(ofSize: 12, weight: .regular), "H"), ("sys44b", NSFont.systemFont(ofSize: 44, weight: .bold), "Paused"),
     ("sys17r", NSFont.systemFont(ofSize: 17, weight: .regular), "Get ready"), ("sys21m", NSFont.systemFont(ofSize: 21, weight: .medium), "l")]
}

// String widths and heights (AppKit layout inputs).
var sizes: [String: [Double]] = [:]
for (key, font, string) in widthCases() {
    let s = (string as NSString).size(withAttributes: [.font: font])
    sizes[key] = [Double(s.width), Double(s.height)]
}
try! JSONSerialization.data(withJSONObject: sizes, options: [.prettyPrinted, .sortedKeys]).write(to: URL(fileURLWithPath: "\(outDir)/widths.json"))
print("Text calibration: \(specs.count) fonts, \(sizes.count) widths · \(outDir)")

/// (key, font, string): the game's literal HUD/menu strings in their fonts (TextCalibration.WidthCases).
func widthCases() -> [(String, NSFont, String)] {
    let sys = { (s: CGFloat, w: NSFont.Weight) in NSFont.systemFont(ofSize: s, weight: w) }
    let mono = { (s: CGFloat, w: NSFont.Weight) in NSFont.monospacedSystemFont(ofSize: s, weight: w) }
    var list: [(String, NSFont, String)] = [
        ("sys44b Paused", sys(44, .bold), "Paused"), ("sys80b 3", sys(80, .bold), "3"),
        ("sys30b Race finished", sys(30, .bold), "Race finished"), ("sys17r Get ready to race", sys(17, .regular), "Get ready to race"),
        ("sys17r The race is waiting for you.", sys(17, .regular), "The race is waiting for you."),
        ("sys12s TAKE A BREATHER", sys(12, .semibold), "TAKE A BREATHER"), ("sys12s DIRT TRACK · 3 LAPS", sys(12, .semibold), "DIRT TRACK · 3 LAPS"),
        ("sys13m WASD", sys(13, .medium), "WASD / ↑↓←→  Drive    ·    Shift  Boost"), ("sys13m P / Esc", sys(13, .medium), "P / Esc  Resume    ·    ⌘R  Restart"),
        ("sys26s A storm is coming...", sys(26, .semibold), "A storm is coming..."), ("sys21m Sandbox", sys(21, .medium), "Sandbox"),
        ("sys21m Dirt Track", sys(21, .medium), "Dirt Track"), ("sys58b Marvin", sys(58, .bold), "Marvin"),
        ("sys18r Beep", sys(18, .regular), "Beep, boop... just some fun."), ("sys12r hint", sys(12, .regular), "↑ ↓ to choose   ·   Return to select"),
        ("sys11b FINAL CLASSIFICATION", sys(11, .bold), "FINAL CLASSIFICATION"), ("sys16b WALL-E", sys(16, .bold), "WALL-E"),
        ("sys16m Marvin", sys(16, .medium), "Marvin"), ("sys12r Autopilot", sys(12, .regular), "Autopilot · Enjoy the cooldown lap"),
        ("sys12m Race again", sys(12, .medium), "⌘R  Race again    ·    Main Menu to leave"), ("sys10b BEST LAP", sys(10, .bold), "BEST LAP"),
        ("sys16b OFF TO TOWN", sys(16, .bold), "OFF TO TOWN"), ("sys13m 1.  Marvin", sys(13, .medium), "1.  Marvin"),
        ("sys21s A little room", sys(21, .semibold), "A little room to explore."), ("sys18s Find beacon", sys(18, .semibold), "Find beacon 1 of 5"),
        ("sys11r Drive through", sys(11, .regular), "Drive through the amber ring."), ("sys34b MOS ASTER", sys(34, .bold), "MOS ASTER"),
        ("sys16m Preparing", sys(16, .medium), "Preparing the dirt race"), ("sys13r stage", sys(13, .regular), "Preparing the sand and racecourse"),
        ("mono23b DIRT TRACK", mono(23, .bold), "DIRT TRACK"), ("mono16r CURRENT", mono(16, .regular), "CURRENT  0:04.00"),
        ("mono13r standings", mono(13, .regular), "Marvin  LAP 1 / 3"), ("mono12r help", mono(12, .regular), "DRIVE W A S D / arrows   BOOST Shift   BRAKE Space"),
        ("mono11m keys", mono(11, .medium), "W A S D / ↑ ↓ ← →"), ("mono9b START", mono(9, .bold), "START / FINISH"),
        ("mono14r time", mono(14, .regular), "1:29.18"), ("mono17b LOCAL", mono(17, .bold), "LOCAL HIGH SCORES"),
    ]
    list.append(("monodigit13s 59.9 FPS", NSFont.monospacedDigitSystemFont(ofSize: 13, weight: .semibold), "59.9 FPS"))
    list.append(("monodigit13s — FPS", NSFont.monospacedDigitSystemFont(ofSize: 13, weight: .semibold), "— FPS"))
    return list
}
