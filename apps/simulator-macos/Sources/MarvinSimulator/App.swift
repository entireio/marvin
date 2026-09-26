import AppKit
import SceneKit
import SimulationCore

final class AppController: NSObject, NSApplicationDelegate, NSWindowDelegate, NSToolbarDelegate {
    var window: NSWindow!
    let view = SimulatorView(), hud = HUDView(), world = World()
    var robot: Robot!
    var simulation = Simulation()
    var timer: Timer?, lastTime = ProcessInfo.processInfo.systemUptime
    var cameraMode = 1, orbitYaw = 0.65, orbitPitch = 0.5, cameraDistance = 3.5
    var active = true
    var pauseItem: NSToolbarItem?
    var appearanceObservation: NSKeyValueObservation?
    var appliedIconName = ""
    var smokeFrames = 0
    let smokeDirectory: String? = {
        let args = CommandLine.arguments
        guard let i = args.firstIndex(of: "--smoke-test"), i+1 < args.count else { return nil }
        return args[i+1]
    }()

    func applicationDidFinishLaunching(_ notification: Notification) {
        appearanceObservation = NSApp.observe(\.effectiveAppearance, options: [.initial, .new]) { [weak self] app, _ in
            self?.updateApplicationIcon(for: app.effectiveAppearance)
        }
        do {
            guard let resources = Bundle.main.resourceURL else { throw CocoaError(.fileNoSuchFile) }
            robot = try Robot(resources: resources)
        } catch {
            let alert = NSAlert(); alert.messageText = "Marvin’s model could not be loaded"
            alert.informativeText = "\(error.localizedDescription)\nRebuild with apps/simulator-macos/build-app.sh."
            alert.runModal(); NSApp.terminate(nil); return
        }
        world.scene.rootNode.addChildNode(robot.root)
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1280, height: 820),
            styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        window.title = "Marvin · Playground"
        window.minSize = NSSize(width: 900, height: 640)
        window.titlebarAppearsTransparent = true; window.backgroundColor = color(0xe6ece3)
        window.delegate = self
        let toolbar = NSToolbar(identifier: "SimulatorToolbar")
        toolbar.delegate = self; toolbar.displayMode = .iconAndLabel
        window.toolbar = toolbar
        view.scene = world.scene; view.pointOfView = world.camera
        view.antialiasingMode = .multisampling4X
        view.preferredFramesPerSecond = 60; view.rendersContinuously = true
        view.autoresizingMask = [.width, .height]
        window.contentView = view
        hud.frame = view.bounds; hud.autoresizingMask = [.width, .height]; view.addSubview(hud)
        view.onCommand = { [weak self] code in
            guard let self else { return }
            switch code {
            case 8: self.cycleCamera(nil)
            case 35, 53: self.togglePause(nil)
            case 4: self.simulation.centerHead()
            case 44: self.hud.helpVisible.toggle()
            default: break
            }
        }
        view.onFocusLost = { [weak self] in self?.simulation.stop() }
        view.onOrbit = { [weak self] dx, dy in
            guard let self else { return }
            self.cameraMode = 1
            self.orbitYaw += Double(dx)*0.008
            self.orbitPitch = max(0.15, min(1.35, self.orbitPitch+Double(dy)*0.008))
        }
        view.onZoom = { [weak self] delta in
            guard let self else { return }
            self.cameraDistance = max(1.6, min(9, self.cameraDistance+Double(delta)*0.035))
        }
        makeMenu()
        window.center(); window.makeKeyAndOrderFront(nil)
        window.makeFirstResponder(view); NSApp.activate(ignoringOtherApps: true)
        robot.update(simulation); updateCamera(snap: true)
        timer = Timer(timeInterval: 1.0/60, target: self, selector: #selector(tick), userInfo: nil, repeats: true)
        RunLoop.main.add(timer!, forMode: .common)
    }

    func updateApplicationIcon(for appearance: NSAppearance) {
        let dark = appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
        let name = dark ? "AppIconDark" : "AppIconLight"
        guard let url = Bundle.main.url(forResource: name, withExtension: "icns"),
              let icon = NSImage(contentsOf: url) else { return }
        NSApp.applicationIconImage = icon
        appliedIconName = name
    }

    @objc func tick() {
        let now = ProcessInfo.processInfo.systemUptime
        let dt = min(now-lastTime, 0.1); lastTime = now
        if active { simulation.advance(view.driveInput, dt: dt) }
        robot.update(simulation); world.update(simulation); updateCamera(snap: false)
        hud.state = simulation; hud.cameraName = ["FOLLOW", "ORBIT", "OVERVIEW"][cameraMode]
        hud.needsDisplay = true
        if smokeDirectory != nil { smokeTest() }
    }
    func updateCamera(snap: Bool) {
        let target = SCNVector3(simulation.x, 0.35, simulation.z)
        let desired: SCNVector3
        if cameraMode == 2 {
            desired = SCNVector3(0, 11.7, -10)
        } else {
            let angle = cameraMode == 0 ? simulation.heading + .pi + 0.45 : orbitYaw
            let elevation = cameraMode == 0 ? 0.48 : orbitPitch
            desired = SCNVector3(simulation.x + sin(angle)*cos(elevation)*cameraDistance,
                0.4 + sin(elevation)*cameraDistance,
                simulation.z + cos(angle)*cos(elevation)*cameraDistance)
        }
        let current = world.camera.position
        let mix: CGFloat = snap ? 1 : 0.12
        world.camera.position = SCNVector3(current.x+(desired.x-current.x)*mix,
            current.y+(desired.y-current.y)*mix, current.z+(desired.z-current.z)*mix)
        world.camera.look(at: cameraMode == 2 ? SCNVector3(0, 0, 0) : target,
            up: SCNVector3(0, 1, 0), localFront: SCNVector3(0, 0, -1))
    }
    @objc func togglePause(_ sender: Any?) {
        simulation.paused.toggle(); view.clearInput()
        pauseItem?.label = simulation.paused ? "Resume" : "Pause"
        pauseItem?.image = NSImage(systemSymbolName: simulation.paused ? "play.fill" : "pause.fill", accessibilityDescription: nil)
        window.makeFirstResponder(view)
    }
    @objc func reset(_ sender: Any?) {
        cameraMode = 1; orbitYaw = 0.65; orbitPitch = 0.5; cameraDistance = 3.5
        simulation.reset(); view.clearInput(); pauseItem?.label = "Pause"
        pauseItem?.image = NSImage(systemSymbolName: "pause.fill", accessibilityDescription: nil)
        updateCamera(snap: true); window.makeFirstResponder(view)
    }
    @objc func cycleCamera(_ sender: Any?) {
        cameraMode = (cameraMode+1)%3; updateCamera(snap: true)
        window.makeFirstResponder(view)
    }
    @objc func toggleHelp(_ sender: Any?) { hud.helpVisible.toggle() }
    func applicationWillResignActive(_ notification: Notification) {
        active = false; view.clearInput()
    }
    func applicationDidBecomeActive(_ notification: Notification) {
        active = true; lastTime = ProcessInfo.processInfo.systemUptime
    }
    func windowDidResignKey(_ notification: Notification) { view.clearInput() }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
    func applicationWillTerminate(_ notification: Notification) { timer?.invalidate() }
    func toolbarAllowedItemIdentifiers(_ toolbar: NSToolbar) -> [NSToolbarItem.Identifier] {
        [.flexibleSpace, .init("camera"), .init("pause"), .init("reset"), .init("help")]
    }
    func toolbarDefaultItemIdentifiers(_ toolbar: NSToolbar) -> [NSToolbarItem.Identifier] {
        toolbarAllowedItemIdentifiers(toolbar)
    }
    func toolbar(_ toolbar: NSToolbar, itemForItemIdentifier id: NSToolbarItem.Identifier, willBeInsertedIntoToolbar flag: Bool) -> NSToolbarItem? {
        let item = NSToolbarItem(itemIdentifier: id)
        let config: (String, String, Selector)
        switch id.rawValue {
        case "camera": config = ("Camera", "video", #selector(cycleCamera))
        case "pause": config = ("Pause", "pause.fill", #selector(togglePause)); pauseItem = item
        case "reset": config = ("Reset", "arrow.counterclockwise", #selector(reset))
        case "help": config = ("Controls", "keyboard", #selector(toggleHelp))
        default: return nil
        }
        item.label = config.0; item.toolTip = config.0
        item.image = NSImage(systemSymbolName: config.1, accessibilityDescription: config.0)
        item.target = self; item.action = config.2
        return item
    }
    func makeMenu() {
        let bar = NSMenu(), appItem = NSMenuItem(), appMenu = NSMenu()
        appMenu.addItem(withTitle: "About Marvin Simulator", action: #selector(NSApplication.orderFrontStandardAboutPanel(_:)), keyEquivalent: "")
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "Quit Marvin Simulator", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu; bar.addItem(appItem)
        let simItem = NSMenuItem(), simMenu = NSMenu(title: "Simulation")
        for (title, selector, key) in [("Reset playground", #selector(reset), "r"), ("Pause / resume", #selector(togglePause), "p"), ("Change camera", #selector(cycleCamera), "1")] {
            let item = simMenu.addItem(withTitle: title, action: selector, keyEquivalent: key); item.target = self
        }
        simItem.submenu = simMenu; bar.addItem(simItem); NSApp.mainMenu = bar
    }
    func smokeTest() {
        guard let directory = smokeDirectory else { return }
        smokeFrames += 1
        func key(_ code: UInt16, down: Bool) {
            let event = NSEvent.keyEvent(with: down ? .keyDown : .keyUp, location: .zero,
                modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime,
                windowNumber: window.windowNumber, context: nil, characters: "",
                charactersIgnoringModifiers: "", isARepeat: false, keyCode: code)!
            if down { view.keyDown(with: event) } else { view.keyUp(with: event) }
        }
        if smokeFrames == 30 { key(13, down: true) }
        if smokeFrames == 90 { key(13, down: false); key(2, down: true) }
        if smokeFrames == 120 { view.clearInput() }
        if smokeFrames == 150 {
            do {
                let url = URL(fileURLWithPath: directory, isDirectory: true)
                try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
                guard let tiff = view.snapshot().tiffRepresentation,
                      let bitmap = NSBitmapImageRep(data: tiff),
                      let png = bitmap.representation(using: .png, properties: [:]) else { throw CocoaError(.fileWriteUnknown) }
                try png.write(to: url.appendingPathComponent("native-scene.png"))
                let traveled = simulation.distance, heading = simulation.heading
                togglePause(nil)
                let elapsed = simulation.elapsed
                key(13, down: true)
                simulation.advance(view.driveInput, dt: 0.1)
                let pausePassed = simulation.paused && simulation.elapsed == elapsed
                togglePause(nil)
                key(13, down: true); key(49, down: true)
                simulation.advance(view.driveInput, dt: 0.1)
                let brakePassed = simulation.speed == 0
                view.clearInput()
                let focusPassed = view.held.isEmpty && simulation.speed == 0
                key(14, down: true); key(15, down: true)
                simulation.advance(view.driveInput, dt: 0.1)
                let headPassed = simulation.yaw < 0 && simulation.pitch > 0
                reset(nil)
                let resetPassed = simulation.z == -2.6 && simulation.distance == 0 && simulation.yaw == 0
                var cameraPassed = true
                for mode in 0...2 {
                    cameraMode = mode
                    for angle in [-2.8, -1.0, 0.0, 1.0, 2.8] {
                        orbitYaw = angle; orbitPitch = 0.7
                        updateCamera(snap: true)
                        // A level camera has a horizontal right axis and an
                        // upward-facing up axis, independent of orbit azimuth.
                        let transform = world.camera.simdWorldTransform
                        cameraPassed = cameraPassed && abs(transform.columns.0.y) < 0.00001
                            && transform.columns.1.y > 0
                    }
                }
                reset(nil)
                let originalAppearance = NSApp.appearance
                NSApp.appearance = NSAppearance(named: .aqua)
                let lightIconPassed = appliedIconName == "AppIconLight" && NSApp.applicationIconImage != nil
                NSApp.appearance = NSAppearance(named: .darkAqua)
                let darkIconPassed = appliedIconName == "AppIconDark" && NSApp.applicationIconImage != nil
                NSApp.appearance = originalAppearance
                var tracksPassed = robot.tracks.count == 2
                for (throttle, turn) in [(1.0, 0.0), (-1.0, 0.0), (0.0, 1.0), (0.0, -1.0)] {
                    var sample = Simulation(), input = DriveInput()
                    robot.update(sample)
                    let initial = robot.tracks.map { $0.shoes[7].position.z }
                    input.throttle = throttle; input.turn = turn
                    sample.advance(input, dt: 0.1); robot.update(sample)
                    for (i, track) in robot.tracks.enumerated() {
                        let expected = throttle != 0 ? throttle : (track.left ? turn : -turn)
                        tracksPassed = tracksPassed && Double(track.shoes[7].position.z-initial[i])*expected < 0
                            && (track.left == (track.node.position.x > 0))
                    }
                }
                robot.update(Simulation())
                let groundContactPassed = robot.root.position.y == 0
                    && abs(TrackLoop.sample(TrackLoop.straight/2).y-0.0055) < 0.000001
                let passed = robot.partCount == 23 && robot.triangleCount > 600_000
                    && traveled > 0.3 && abs(heading) > 0.3
                    && tracksPassed && groundContactPassed && pausePassed && brakePassed && focusPassed && headPassed && resetPassed && cameraPassed && lightIconPassed && darkIconPassed
                let report: [String: Any] = ["passed": passed, "parts": robot.partCount,
                    "triangles": robot.triangleCount, "distance": traveled,
                    "heading": heading, "pausePassed": pausePassed, "brakePassed": brakePassed,
                    "focusPassed": focusPassed, "headPassed": headPassed, "resetPassed": resetPassed,
                    "cameraPassed": cameraPassed, "lightIconPassed": lightIconPassed, "darkIconPassed": darkIconPassed,
                    "tracksPassed": tracksPassed, "groundContactPassed": groundContactPassed,
                    "renderer": "SceneKit / Metal", "width": bitmap.pixelsWide,
                    "height": bitmap.pixelsHigh]
                try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys])
                    .write(to: url.appendingPathComponent("smoke.json"))
                print("Native smoke test: \(passed ? "PASS" : "FAIL") · \(directory)")
                exit(passed ? 0 : 1)
            } catch { fputs("Smoke test failed: \(error)\n", stderr); exit(1) }
        }
    }
}
