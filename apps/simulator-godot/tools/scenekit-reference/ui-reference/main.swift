import AppKit
@testable import SimulationCore

// Mirrors the HUD part of UISmoke.RunHUD (apps/simulator-godot/scripts/UI/UISmoke.cs): same synthetic states,
// same frames, cacheDisplay captures. Keep both in step.
let out = URL(fileURLWithPath: CommandLine.arguments[1])
NSApplication.shared.setActivationPolicy(.prohibited)
let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1280, height: 792), styleMask: [.titled], backing: .buffered, defer: false)
window.colorSpace = .sRGB
let content = NSView(frame: NSRect(x: 0, y: 0, width: 1280, height: 792))
window.contentView = content

func write(_ view: NSView, _ rect: NSRect, _ name: String) {
    view.layoutSubtreeIfNeeded(); view.needsDisplay = true
    let bitmap = view.bitmapImageRepForCachingDisplay(in: rect)!
    view.cacheDisplay(in: rect, to: bitmap)
    try! bitmap.representation(using: .png, properties: [:])!.write(to: out.appendingPathComponent(name))
}
func laps(_ total: Double, _ best: Double) -> [Double] { [total - 2 * best - 0.6, best + 0.6, best] }

let raceHUD = RaceHUD(frame: NSRect(x: 0, y: 0, width: 1280, height: 792))
content.addSubview(raceHUD)
raceHUD.helpVisible = true
do {
    let slots = [DirtCourse.startingGrid[0], DirtCourse.startingGrid[3], DirtCourse.startingGrid[1], DirtCourse.startingGrid[2]]
    var simulation = Simulation(dirtTrack: true, dirtStartOffset: slots[0].offset, dirtStartPhase: slots[0].phase)
    var race = DirtRace(startPhase: slots[0].phase); race.countDown(dt: 3)
    var opponents = [DirtOpponent(slot: slots[1]), DirtOpponent(slot: slots[2], laneOffset: 0), DirtOpponent(slot: slots[3], laneOffset: -0.65)]
    var racePhysics = DirtRacePhysics()
    for _ in 0..<240 {
        let phase = DirtCourse.phase(x: simulation.x, z: simulation.z)
        let target = DirtCourse.point(phase + 0.055)
        let desired = atan2(target.x - simulation.x, target.z - simulation.z)
        let error = atan2(sin(desired - simulation.heading), cos(desired - simulation.heading))
        var input = DriveInput(); input.throttle = 1; input.boost = true; input.turn = -error * 1.5
        racePhysics.advance(input, player: &simulation, race: &race, opponents: &opponents, dt: 1.0/60, raceDT: 1.0/60,
                            robotCollisionsEnabled: true, assists: DirtDrivingAssists(steering: true, braking: true))
    }
    raceHUD.opponents = opponents; raceHUD.race = race
    raceHUD.x = simulation.x; raceHUD.z = simulation.z; raceHUD.heading = simulation.heading
    raceHUD.introducing = false; raceHUD.paused = false
    write(raceHUD, NSRect(x: 0, y: 0, width: 900, height: 550), "dirt-hud.png")
    raceHUD.frame = NSRect(x: 0, y: 0, width: 900, height: 550)
    write(raceHUD, raceHUD.bounds, "dirt-hud-900x550.png")
    raceHUD.frame = NSRect(x: 0, y: 0, width: 1280, height: 792)
    write(raceHUD, raceHUD.bounds, "dirt-hud-1280x792.png")
}
raceHUD.frame = NSRect(x: 0, y: 0, width: 900, height: 550)
let wallE = DirtRace.synthetic(elapsed: 89.18, laps: laps(89.18, 27.12))
let marvin = DirtRace.synthetic(elapsed: 90.94, laps: laps(90.94, 27.55))
let r2d2 = DirtRace.synthetic(elapsed: 93.28, laps: laps(93.28, 27.49))
let bb8Live = DirtRace.synthetic(elapsed: 66.10, laps: [34.86, 31.24], progress: 2 * .pi * 2.4)
let bb8 = DirtRace.synthetic(elapsed: 96.85, laps: laps(96.85, 27.54))
func rival(_ slot: (phase: Double, offset: Double), _ race: DirtRace, _ laneOffset: Double = 0.65) -> DirtOpponent {
    var o = DirtOpponent(slot: slot, laneOffset: laneOffset); o.race = race; return o
}
raceHUD.race = marvin; raceHUD.introducing = false; raceHUD.paused = false; raceHUD.escaping = false
raceHUD.opponents = [rival(DirtCourse.startingGrid[1], r2d2), rival(DirtCourse.startingGrid[2], bb8Live, 0), rival(DirtCourse.startingGrid[3], wallE, -0.65)]
write(raceHUD, raceHUD.bounds, "race-finish-live.png")
raceHUD.opponents = [rival(DirtCourse.startingGrid[1], r2d2), rival(DirtCourse.startingGrid[2], bb8, 0), rival(DirtCourse.startingGrid[3], wallE, -0.65)]
write(raceHUD, raceHUD.bounds, "race-results.png")
do {
    let slots = [DirtCourse.startingGrid[3], DirtCourse.startingGrid[1], DirtCourse.startingGrid[2], DirtCourse.startingGrid[0]]
    raceHUD.race = DirtRace(startPhase: slots[0].phase)
    raceHUD.opponents = [DirtOpponent(slot: slots[1]), DirtOpponent(slot: slots[2], laneOffset: 0), DirtOpponent(slot: slots[3], laneOffset: -0.65)]
    raceHUD.scores = []
    write(raceHUD, raceHUD.bounds, "race-start.png")
    raceHUD.paused = true
    write(raceHUD, raceHUD.bounds, "race-pause.png")
    raceHUD.paused = false
}
do {
    raceHUD.scores = [DirtScore(laps: laps(88.4, 27.0)), DirtScore(laps: laps(90.94, 27.55)), DirtScore(laps: laps(95.1, 29.2))]
    raceHUD.saveError = nil
    write(raceHUD, raceHUD.bounds, "race-scores.png")
    raceHUD.stormSelected = true; raceHUD.introducing = true
    write(raceHUD, raceHUD.bounds, "race-storm-warning.png")
    raceHUD.introducing = false
    write(raceHUD, raceHUD.bounds, "race-storm-start.png")
    raceHUD.stormSelected = false
    raceHUD.race = marvin
    raceHUD.opponents = [rival(DirtCourse.startingGrid[1], r2d2), rival(DirtCourse.startingGrid[2], bb8, 0), rival(DirtCourse.startingGrid[3], wallE, -0.65)]
    raceHUD.escaping = true; raceHUD.escapeComplete = false
    write(raceHUD, raceHUD.bounds, "race-departure.png")
    raceHUD.escapeComplete = true
    write(raceHUD, raceHUD.bounds, "race-departure-complete.png")
    raceHUD.escaping = false
    raceHUD.race = DirtRace(); raceHUD.race.countDown(dt: 3)
    raceHUD.opponents = [DirtOpponent(slot: DirtCourse.startingGrid[1]), DirtOpponent(slot: DirtCourse.startingGrid[2], laneOffset: 0), DirtOpponent(slot: DirtCourse.startingGrid[3], laneOffset: -0.65)]
    raceHUD.configureNavigationMap(town: TownWorld())
    raceHUD.mapRegion = .town; raceHUD.x = 72; raceHUD.z = -96; raceHUD.heading = 0.7
    write(raceHUD, raceHUD.bounds, "race-map-town.png")
    raceHUD.mapRegion = .dunes; raceHUD.x = 260; raceHUD.z = 180; raceHUD.heading = -2.2
    write(raceHUD, raceHUD.bounds, "race-map-dunes.png")
    raceHUD.mapRegion = .course
}
raceHUD.removeFromSuperview()

// Sandbox HUD (HUDView) and the FPS readout.
let hud = HUDView(frame: NSRect(x: 0, y: 0, width: 1280, height: 792))
content.addSubview(hud)
var state = Simulation(seed: 0)
var input = DriveInput(); input.throttle = 1; input.turn = 0.4
for _ in 0..<40 { state.advance(input, dt: 1.0/60) }
hud.state = state; hud.cameraName = "ORBIT"
write(hud, hud.bounds, "sandbox-hud.png")
state.paused = true; hud.state = state
write(hud, hud.bounds, "sandbox-hud-paused.png")
hud.removeFromSuperview()
let fps = FrameRateHUD(frame: NSRect(x: 0, y: 0, width: 1280, height: 792))
content.addSubview(fps)
fps.resetSamples()
write(fps, fps.bounds, "fps-hud-empty.png")
print("UI reference: \(out.path)")
