import AppKit
import SceneKit
import SimulationCore

final class AppController: NSObject, NSApplicationDelegate, NSWindowDelegate, NSToolbarDelegate {
    var window: NSWindow!
    let view = SimulatorView(), hud = HUDView(), world = World()
    let mainMenu = MainMenuView(frame: .zero)
    var inSandbox = false
    var isDirtTrack = false
    var dirtIntro: Double?
    let dirtIntroDuration = 3.2
    lazy var dirtWorld = DirtWorld()
    let raceHUD = RaceHUD()
    var race = DirtRace()
    var scores: [DirtScore] = []
    var scoreSaved = false
    var scoreLoadFailed = false
    var scoreURL: URL {
        if let directory = smokeDirectory { return URL(fileURLWithPath: directory).appendingPathComponent("test-scores.json") }
        return FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Marvin Simulator/motocross-v2-scores.json")
    }
    var menuSmokePassed = false
    var menuSmokeFrames = 0
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
        raceHUD.frame = view.bounds; raceHUD.autoresizingMask = [.width, .height]
        raceHUD.isHidden = true; view.addSubview(raceHUD)
        view.onCommand = { [weak self] code in
            guard let self else { return }
            switch code {
            case 8: self.cycleCamera(nil)
            case 35, 53: self.togglePause(nil)
            case 4: self.simulation.centerHead()
            case 44: self.toggleHelp(nil)
            default: break
            }
        }
        view.onFocusLost = { [weak self] in self?.simulation.stop() }
        view.onOrbit = { [weak self] dx, dy in
            guard let self, self.inSandbox, self.dirtIntro == nil else { return }
            self.cameraMode = 1
            self.orbitYaw += Double(dx)*0.008
            self.orbitPitch = max(0.15, min(1.35, self.orbitPitch+Double(dy)*0.008))
        }
        view.onZoom = { [weak self] delta in
            guard let self, self.inSandbox, self.dirtIntro == nil else { return }
            self.cameraDistance = max(1.6, min(9, self.cameraDistance+Double(delta)*0.035))
        }
        mainMenu.frame = view.bounds; mainMenu.autoresizingMask = [.width, .height]
        view.addSubview(mainMenu)
        mainMenu.onSandbox = { [weak self] in self?.startSandbox() }
        mainMenu.onDirtTrack = { [weak self] in self?.startDirtTrack() }
        makeMenu()
        window.center(); window.makeKeyAndOrderFront(nil)
        window.makeFirstResponder(view); NSApp.activate(ignoringOtherApps: true)
        robot.update(simulation); showMainMenu(nil)
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
        let wallDelta = max(0, now-lastTime)
        let dt = min(wallDelta, 0.1); lastTime = now
        if !inSandbox {
            mainMenu.animate(robot, dt: dt)
            if let directory = smokeDirectory {
                menuSmokeFrames += 1
                guard menuSmokeFrames == 20 else { return }
                // Capture the native controls and composite the Metal portrait for visual QA.
                mainMenu.layoutSubtreeIfNeeded()
                if let bitmap = mainMenu.bitmapImageRepForCachingDisplay(in: mainMenu.bounds) {
                    mainMenu.cacheDisplay(in: mainMenu.bounds, to: bitmap)
                    let image = NSImage(size: mainMenu.bounds.size)
                    image.lockFocus()
                    bitmap.draw(in: mainMenu.bounds)
                    mainMenu.portrait.snapshot().draw(in: mainMenu.portrait.frame)
                    image.unlockFocus()
                    let url = URL(fileURLWithPath: directory, isDirectory: true)
                    try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
                    if let tiff = image.tiffRepresentation, let png = NSBitmapImageRep(data: tiff)?.representation(using: .png, properties: [:]) {
                        try? png.write(to: url.appendingPathComponent("main-menu.png"))
                    }
                }
                menuSmokePassed = !mainMenu.isHidden && hud.isHidden && robot.root.parent === mainMenu.stage.rootNode
                let down = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: 0,
                    windowNumber: window.windowNumber, context: nil, characters: "", charactersIgnoringModifiers: "", isARepeat: false, keyCode: 125)!
                let enter = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: 0,
                    windowNumber: window.windowNumber, context: nil, characters: "", charactersIgnoringModifiers: "", isARepeat: false, keyCode: 36)!
                mainMenu.keyDown(with: down); mainMenu.keyDown(with: down); mainMenu.keyDown(with: enter)
                menuSmokePassed = menuSmokePassed && mainMenu.settings
                mainMenu.selection = 2; mainMenu.activate()
                menuSmokePassed = menuSmokePassed && !mainMenu.settings
                mainMenu.selection = 0; mainMenu.activate()
                menuSmokePassed = menuSmokePassed && inSandbox && mainMenu.isHidden && !hud.isHidden
            }
            return
        }
        // The automated renderer check uses a fixed clock and must not depend
        // on another app taking focus. Interactive play still pauses on blur.
        let step = smokeDirectory == nil ? dt : 1.0/60
        let raceDelta = smokeDirectory == nil ? wallDelta : step
        let advancing = (active || smokeDirectory != nil) && !simulation.paused
        if advancing {
            if let intro = dirtIntro {
                dirtIntro = intro + step
                if dirtIntro! >= dirtIntroDuration { dirtIntro = nil; view.clearInput() }
            } else if isDirtTrack && race.countdown > 0 { race.countDown(dt: raceDelta) }
            else if !isDirtTrack || !race.finished {
                simulation.advance(view.driveInput, dt: step)
                if isDirtTrack { race.advance(x: simulation.x, z: simulation.z, dt: raceDelta) }
            }
        }
        robot.update(simulation)
        if isDirtTrack {
            if race.finished { simulation.stop() }
            dirtWorld.update(simulation, dt: advancing ? step : 0)
            recordRaceScore()
            raceHUD.x = simulation.x; raceHUD.z = simulation.z; raceHUD.heading = simulation.heading
            raceHUD.introducing = dirtIntro != nil
            raceHUD.race = race; raceHUD.scores = scores; raceHUD.paused = simulation.paused
            raceHUD.needsDisplay = true
        } else { world.update(simulation) }
        updateCamera(snap: false)
        hud.state = simulation; hud.cameraName = ["FOLLOW", "ORBIT", "OVERVIEW"][cameraMode]
        hud.needsDisplay = true
        if smokeDirectory != nil { smokeTest() }
    }
    @objc func showMainMenu(_ sender: Any?) {
        window.title = "Marvin · Playground"
        inSandbox = false; dirtIntro = nil; view.clearInput()
        hud.isHidden = true; raceHUD.isHidden = true; mainMenu.isHidden = false; window.toolbar?.isVisible = false
        mainMenu.settings = false; mainMenu.selection = 0; mainMenu.refresh()
        mainMenu.stage.rootNode.addChildNode(robot.root)
        mainMenu.animate(robot, dt: 0)
        window.makeFirstResponder(mainMenu)
    }
    func startSandbox() {
        window.title = "Marvin · Playground"
        world.camera.camera?.zFar = 80
        isDirtTrack = false; dirtIntro = nil; simulation = Simulation()
        view.scene = world.scene; world.scene.rootNode.addChildNode(world.camera)
        view.pointOfView = world.camera; raceHUD.isHidden = true
        inSandbox = true; mainMenu.isHidden = true; hud.isHidden = false
        window.toolbar?.isVisible = true; hud.helpVisible = mainMenu.showGuide
        world.scene.rootNode.addChildNode(robot.root)
        reset(nil); robot.update(simulation); world.update(simulation)
    }
    func recordRaceScore() {
        guard race.finished, !scoreSaved else { return }
        scoreSaved = true
        guard !scoreLoadFailed else { return }
        scores = DirtScores.ranked(scores + [DirtScore(laps: race.laps)])
        do { try DirtScores.save(scores, to: scoreURL) }
        catch { raceHUD.saveError = "Could not save score" }
    }
    func startDirtTrack() {
        window.title = "Marvin · Dirt Track"; world.camera.camera?.zFar = 250
        isDirtTrack = true; inSandbox = true; simulation = Simulation(dirtTrack: true)
        hud.isHidden = true; raceHUD.isHidden = true
        view.isHidden = true
        SCNTransaction.begin(); SCNTransaction.disableActions = true
        raceHUD.helpVisible = mainMenu.showGuide; window.toolbar?.isVisible = true
        view.scene = dirtWorld.scene; dirtWorld.scene.rootNode.addChildNode(world.camera)
        dirtWorld.scene.rootNode.addChildNode(robot.root); view.pointOfView = world.camera
        scoreLoadFailed = false; raceHUD.saveError = nil
        do { scores = try DirtScores.load(scoreURL) }
        catch { scores = []; scoreLoadFailed = true; raceHUD.saveError = "Could not load scores; file preserved" }
        reset(nil); robot.update(simulation)
        dirtIntro = 0; updateCamera(snap:true)
        SCNTransaction.commit()
        // Compile materials/upload geometry and draw the first correct overview
        // before revealing the scene, avoiding a frame from the previous camera.
        _ = view.prepare(dirtWorld.scene, shouldAbortBlock:nil)
        _ = view.snapshot()
        raceHUD.race = race; raceHUD.introducing = true; raceHUD.scores = scores
        mainMenu.isHidden = true; raceHUD.isHidden = false; view.isHidden = false
        window.makeFirstResponder(view)
        lastTime = ProcessInfo.processInfo.systemUptime
    }
    func updateCamera(snap: Bool) {
        let lookAhead = isDirtTrack && cameraMode == 0 ? 1.5 : 0.0
        let target = SCNVector3(simulation.x+sin(simulation.heading)*lookAhead,
            simulation.groundY+0.35,simulation.z+cos(simulation.heading)*lookAhead)
        let desired: SCNVector3
        if cameraMode == 2 {
            desired = isDirtTrack ? SCNVector3(0, 38, -33) : SCNVector3(0, 11.7, -10)
        } else {
            let angle = cameraMode == 0 ? simulation.heading + .pi + (isDirtTrack ? 0 : 0.45) : orbitYaw
            let elevation = cameraMode == 0 ? (isDirtTrack ? 0.30 : 0.48) : orbitPitch
            desired = SCNVector3(simulation.x + sin(angle)*cos(elevation)*cameraDistance,
                simulation.groundY + 0.4 + sin(elevation)*cameraDistance,
                simulation.z + cos(angle)*cos(elevation)*cameraDistance)
        }
        if let intro = dirtIntro, isDirtTrack {
            let t = max(0,min(1,(intro-0.35)/(dirtIntroDuration-0.35)))
            let blend = CGFloat(t*t*t*(t*(t*6-15)+10))
            let start = SCNVector3(0,38,-33)
            world.camera.position = SCNVector3(start.x+(desired.x-start.x)*blend,
                start.y+(desired.y-start.y)*blend,start.z+(desired.z-start.z)*blend)
            world.camera.look(at:SCNVector3(target.x*blend,target.y*blend,target.z*blend),
                up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
            return
        }
        let current = world.camera.position
        let mix: CGFloat = snap ? 1 : 0.12
        world.camera.position = SCNVector3(current.x+(desired.x-current.x)*mix,
            current.y+(desired.y-current.y)*mix, current.z+(desired.z-current.z)*mix)
        world.camera.look(at: cameraMode == 2 ? SCNVector3(0, 0, 0) : target,
            up: SCNVector3(0, 1, 0), localFront: SCNVector3(0, 0, -1))
    }
    @objc func togglePause(_ sender: Any?) {
        guard inSandbox else { return }
        simulation.paused.toggle(); view.clearInput()
        pauseItem?.label = simulation.paused ? "Resume" : "Pause"
        pauseItem?.image = NSImage(systemSymbolName: simulation.paused ? "play.fill" : "pause.fill", accessibilityDescription: nil)
        window.makeFirstResponder(view)
    }
    @objc func reset(_ sender: Any?) {
        guard inSandbox else { return }
        cameraMode = 1; orbitYaw = 0.65; orbitPitch = 0.5; cameraDistance = 3.5
        dirtIntro = nil
        simulation.reset()
        if isDirtTrack { race = DirtRace(); scoreSaved = false; dirtWorld.reset(); cameraMode = 0; cameraDistance = 4.5 }
        view.clearInput(); pauseItem?.label = "Pause"
        pauseItem?.image = NSImage(systemSymbolName: "pause.fill", accessibilityDescription: nil)
        updateCamera(snap: true); window.makeFirstResponder(view)
    }
    @objc func cycleCamera(_ sender: Any?) {
        guard inSandbox, dirtIntro == nil else { return }
        cameraMode = (cameraMode+1)%3; updateCamera(snap: true)
        window.makeFirstResponder(view)
    }
    @objc func toggleHelp(_ sender: Any?) { if isDirtTrack { raceHUD.helpVisible.toggle() } else { hud.helpVisible.toggle() } }
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
        [.init("menu"), .flexibleSpace, .init("camera"), .init("pause"), .init("reset"), .init("help")]
    }
    func toolbarDefaultItemIdentifiers(_ toolbar: NSToolbar) -> [NSToolbarItem.Identifier] {
        toolbarAllowedItemIdentifiers(toolbar)
    }
    func toolbar(_ toolbar: NSToolbar, itemForItemIdentifier id: NSToolbarItem.Identifier, willBeInsertedIntoToolbar flag: Bool) -> NSToolbarItem? {
        let item = NSToolbarItem(itemIdentifier: id)
        let config: (String, String, Selector)
        switch id.rawValue {
        case "menu": config = ("Main Menu", "house", #selector(showMainMenu))
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
        for (title, selector, key) in [("Main Menu", #selector(showMainMenu), "m"), ("Reset playground", #selector(reset), "r"), ("Pause / resume", #selector(togglePause), "p"), ("Change camera", #selector(cycleCamera), "1")] {
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
        if (30..<90).contains(smokeFrames) { key(13, down: true) }
        if (90..<120).contains(smokeFrames) { key(13, down: false); key(2, down: true) }
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
                var neckPassed = true
                let neck = robot.root.childNode(withName: "07_neck", recursively: true)!
                for degrees in [-80.0, 0.0, 80.0] {
                    robot.yawNode.eulerAngles.y = CGFloat(degrees * .pi/180)
                    let center = neck.convertPosition(SCNVector3(0, 0.295, -0.01886), to: robot.root)
                    neckPassed = neckPassed && abs(center.x) < 0.000001
                        && abs(center.y-0.295) < 0.000001 && abs(center.z+0.01886) < 0.000001
                    world.camera.position = SCNVector3(0.85, 0.8, -1.1)
                    world.camera.look(at: SCNVector3(0, 0.37, -2.6), up: SCNVector3(0, 1, 0), localFront: SCNVector3(0, 0, -1))
                    if let tiff = view.snapshot().tiffRepresentation,
                       let image = NSBitmapImageRep(data: tiff),
                       let png = image.representation(using: .png, properties: [:]) {
                        try png.write(to: url.appendingPathComponent("neck-pan-\(Int(degrees)).png"))
                    }
                }
                robot.update(Simulation()); updateCamera(snap: true)
                let previousCourse = simulation.checkpoints
                reset(nil); world.update(simulation)
                let coursePassed = previousCourse != simulation.checkpoints
                    && world.beacons.count == 5 && world.beaconLabels.count == 5
                    && simulation.checkpoints.enumerated().allSatisfy { i, point in
                        let ring = world.beacons[i].position, label = world.beaconLabels[i].position
                        return abs(Double(ring.x)-point.x) < 0.00001 && abs(Double(ring.z)-point.z) < 0.00001
                            && abs(Double(label.x)-point.x) < 0.00001 && abs(Double(label.z)-point.z) < 0.00001
                            && CourseLayout.isClear(point, among: Array(simulation.checkpoints.prefix(i)))
                    }
                let groovesPassed = world.floorSurface.geometry != nil && world.beacons.allSatisfy { node in
                    guard let geometry = node.geometry else { return false }
                    let bounds = geometry.boundingBox
                    return node.position.y == 0 && abs(Double(bounds.min.y)+FloorGroove.depth) < 0.000001
                        && bounds.max.y == 0 && node.scale.x == 1 && node.scale.z == 1
                }
                let labelsPassed = simulation.checkpoints.enumerated().allSatisfy { i, point in
                    let label = world.beaconLabels[i], bounds = world.beaconLabels[i].geometry!.boundingBox
                    let center = SIMD4<Float>(Float((bounds.min.x+bounds.max.x)/2), Float((bounds.min.y+bounds.max.y)/2), 0, 1)
                    let positioned = label.simdTransform * simd_inverse(label.simdPivot) * center
                    let bottom = label.simdTransform * SIMD4<Float>(0, -1, 0, 0)
                    let start = i == 0 ? Checkpoint(x: 0, z: -2.6) : simulation.checkpoints[i-1]
                    let angle = CourseRoute.labelYaw(from: start, to: point)
                    let length = hypot(Double(bottom.x), Double(bottom.z))
                    return abs(Double(positioned.x)-point.x) < 0.00001 && abs(Double(positioned.z)-point.z) < 0.00001
                        && abs(Double(bottom.x)/length-sin(angle)) < 0.00001
                        && abs(Double(bottom.z)/length-cos(angle)) < 0.00001
                        && abs(Double((bounds.max.y-bounds.min.y)*label.scale.y)-0.36) < 0.00001
                }
                startDirtTrack()
                let dirtStartPassed = isDirtTrack && race.countdown == 3 && !raceHUD.isHidden && hud.isHidden
                    && view.scene === dirtWorld.scene && simulation.dirtTrack
                let introPassed = dirtIntro == 0 && abs(world.camera.position.y-38) < 0.001 && race.elapsed == 0
                dirtIntro = dirtIntroDuration/2; updateCamera(snap:true)
                let introMidPassed = world.camera.position.y > 3 && world.camera.position.y < 38
                dirtIntro = nil; updateCamera(snap:true)
                race.countDown(dt:3)
                for _ in 0..<240 {
                    let phase = DirtCourse.phase(x:simulation.x,z:simulation.z)
                    let target = DirtCourse.point(phase+0.055)
                    let desired = atan2(target.x-simulation.x,target.z-simulation.z)
                    let error = atan2(sin(desired-simulation.heading),cos(desired-simulation.heading))
                    var input = DriveInput(); input.throttle = 1; input.boost = true; input.turn = -error*1.5
                    simulation.advance(input,dt:1.0/60)
                    race.advance(x:simulation.x,z:simulation.z,dt:1.0/60)
                    robot.update(simulation); dirtWorld.update(simulation,dt:1.0/60)
                }
                let dirtPassed = dirtStartPassed && dirtWorld.emittedCount > 20 && race.elapsed > 3.9
                    && DirtCourse.projection(x:simulation.x,z:simulation.z).distance < DirtCourse.fenceOffset
                for (mode,name) in [(2,"dirt-overview.png"),(0,"dirt-driving.png")] {
                    cameraMode = mode; updateCamera(snap:true)
                    if let tiff = view.snapshot().tiffRepresentation,
                       let image = NSBitmapImageRep(data:tiff)?.representation(using:.png,properties:[:]) {
                        try image.write(to:url.appendingPathComponent(name))
                    }
                }
                race = DirtRace(); race.countDown(dt:3)
                for i in 1...1080 {
                    let p = DirtCourse.point(Double(i)*2 * .pi/360)
                    race.advance(x:p.x,z:p.z,dt:0.1)
                }
                let scoreCount = scores.count
                recordRaceScore(); recordRaceScore()
                let stored = try DirtScores.load(scoreURL)
                let scoresPassed = race.finished && scores.count == min(10,scoreCount+1) && stored.count == scores.count
                reset(nil)
                let dirtResetPassed = race.laps.isEmpty && race.countdown == 3 && dirtWorld.emittedCount == 0
                showMainMenu(nil); startSandbox()
                let modeReturnPassed = !isDirtTrack && view.scene === world.scene && raceHUD.isHidden && !hud.isHidden
                let passed = introPassed && introMidPassed && scoresPassed && dirtPassed && dirtResetPassed && modeReturnPassed && menuSmokePassed && robot.partCount == 23 && robot.triangleCount > 600_000
                    && traveled > 0.3 && abs(heading) > 0.3
                    && labelsPassed && groovesPassed && coursePassed && neckPassed && tracksPassed && groundContactPassed && pausePassed && brakePassed && focusPassed && headPassed && resetPassed && cameraPassed && lightIconPassed && darkIconPassed
                let report: [String: Any] = ["passed": passed, "introPassed": introPassed && introMidPassed, "scoresPassed": scoresPassed, "dirtPassed": dirtPassed, "dirtResetPassed": dirtResetPassed, "modeReturnPassed": modeReturnPassed, "menuPassed": menuSmokePassed, "parts": robot.partCount,
                    "triangles": robot.triangleCount, "distance": traveled,
                    "heading": heading, "pausePassed": pausePassed, "brakePassed": brakePassed,
                    "focusPassed": focusPassed, "headPassed": headPassed, "resetPassed": resetPassed,
                    "cameraPassed": cameraPassed, "lightIconPassed": lightIconPassed, "darkIconPassed": darkIconPassed,
                    "labelsPassed": labelsPassed, "groovesPassed": groovesPassed, "coursePassed": coursePassed, "neckPassed": neckPassed, "tracksPassed": tracksPassed, "groundContactPassed": groundContactPassed,
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
