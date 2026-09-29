import AppKit
import SceneKit
import SimulationCore

final class AppController: NSObject, NSApplicationDelegate, NSWindowDelegate, NSToolbarDelegate {
    var window: NSWindow!
    let view = SimulatorView(), hud = HUDView(), world = World()
    let mainMenu = MainMenuView(frame: .zero)
    var inSandbox = false
    var isDirtTrack = false
    var playerCharacter = RacePerformance.Character.marvin
    var dirtOutro: Double?
    var outroPosition = SCNVector3Zero, outroTarget = SCNVector3Zero
    var dirtIntro: Double?
    let dirtIntroDuration = 3.2
    lazy var dirtWorld = DirtWorld()
    let raceHUD = RaceHUD()
    var race = DirtRace()
    var racePhysics = DirtRacePhysics()
    var opponent = DirtOpponent()
    var r2d2: R2D2!
    var bb8Opponent = DirtOpponent(laneOffset:0)
    var wallEOpponent = DirtOpponent(laneOffset:-0.65)
    var bb8: ImportedRacer!, wallE: ImportedRacer!
    var performances = [RacePerformance(.marvin),RacePerformance(.r2d2),RacePerformance(.bb8),RacePerformance(.wallE)]
    var opponents: [DirtOpponent] { [opponent,bb8Opponent,wallEOpponent] }
    func updateOpponents() {
        raceHUD.opponents = opponents
        let states = [simulation]+opponents.map { $0.simulation }
        let actors = states.map { RacePerformance.Actor($0) }
        for i in performances.indices { performances[i].update(index:i,actors:actors) }
        for i in lineup.indices { updateModel(lineup[i], state: states[i], expression: performances[i].pose) }
    }
    func advanceRacePhysics(_ input: DriveInput, dt: Double, raceDT: Double) {
        var rivals = opponents
        racePhysics.advance(input,player:&simulation,race:&race,opponents:&rivals,dt:dt,raceDT:raceDT,
            robotCollisionsEnabled:mainMenu.raceRobotCollisions, assists:mainMenu.raceAssists)
        opponent = rivals[0]; bb8Opponent = rivals[1]; wallEOpponent = rivals[2]
    }
    var scores: [DirtScore] = []
    var scoreSaved = false
    var scoreLoadFailed = false
    var scoreURL: URL {
        if let directory = smokeDirectory { return URL(fileURLWithPath: directory).appendingPathComponent("test-scores.json") }
        return FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Marvin Simulator/motocross-v4-scores.json")
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
    let townMeter = TownFrameMeter()
    var rendererStudy: AnyObject?
    var townBenchmarkStart: Double?
    var townBenchmarkDirectory: URL?
    var townBenchmarkCPU: [Double] = []
    var smokeFrames = 0
    let smokeDirectory: String? = {
        let args = CommandLine.arguments
        guard let i = args.firstIndex(where: { ["--smoke-test", "--menu-smoke-test", "--character-smoke-test", "--bb8-motion-smoke-test", "--town-smoke-test", "--town-benchmark", "--renderer-study"].contains($0) }), i+1 < args.count else { return nil }
        return args[i+1]
    }()

    func applicationDidFinishLaunching(_ notification: Notification) {
        appearanceObservation = NSApp.observe(\.effectiveAppearance, options: [.initial, .new]) { [weak self] app, _ in
            self?.updateApplicationIcon(for: app.effectiveAppearance)
        }
        do {
            guard let resources = Bundle.main.resourceURL else { throw CocoaError(.fileNoSuchFile) }
            robot = try Robot(resources: resources)
            r2d2 = try R2D2(resources: resources)
            bb8 = try ImportedRacer(kind:.bb8,resources:resources)
            wallE = try ImportedRacer(kind:.wallE,resources:resources)
        } catch {
            let alert = NSAlert(); alert.messageText = "A simulator model could not be loaded"
            alert.informativeText = "\(error.localizedDescription)\nRebuild with apps/simulator-macos/build-app.sh."
            alert.runModal(); NSApp.terminate(nil); return
        }
        world.scene.rootNode.addChildNode(robot.root)
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1280, height: 820),
            styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        window.title = "Marvin · Playground"
        window.minSize = NSSize(width: 900, height: 640)
        window.backgroundColor = .windowBackgroundColor
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
        view.onFocusLost = { [weak self] in
            guard let self else { return }
            // Race pause/focus gates freeze all bodies without erasing one
            // competitor's momentum. Released input decelerates after resume.
            if !self.isDirtTrack { self.simulation.stop() }
        }
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

    /// Shared lifecycle gate for all racers (also exercised by native smoke).
    func advanceRaceFrame(step: Double, raceDelta: Double, advancing: Bool) {
        if advancing {
            if let intro = dirtIntro {
                dirtIntro = intro + step
                if dirtIntro! >= dirtIntroDuration { dirtIntro = nil; view.clearInput() }
            } else if isDirtTrack && race.countdown > 0 { race.countDown(dt: raceDelta) }
            else {
                if isDirtTrack {
                    advanceRacePhysics(view.driveInput,dt:step,raceDT:raceDelta)
                    if race.finished {
                        if dirtOutro == nil {
                            dirtOutro = 0; outroPosition = world.camera.position
                            let front = world.camera.simdWorldFront
                            outroTarget = SCNVector3(outroPosition.x+CGFloat(front.x)*4.5,
                                outroPosition.y+CGFloat(front.y)*4.5,outroPosition.z+CGFloat(front.z)*4.5)
                            view.clearInput()
                        } else { dirtOutro = min(dirtIntroDuration,dirtOutro!+step) }
                    }
                }
                else { simulation.advance(view.driveInput,dt:step) }
            }
        }
    }

    @objc func tick() {
        let now = ProcessInfo.processInfo.systemUptime
        let wallDelta = max(0, now-lastTime)
        let dt = min(wallDelta, 0.1); lastTime = now
        if townBenchmarkStart != nil { tickTownBenchmark(now:now,dt:dt); return }
        if !inSandbox {
            mainMenu.animate(robot, r2d2: r2d2, bb8: bb8, wallE: wallE, dt: dt)
            if let directory = smokeDirectory {
                menuSmokeFrames += 1
                guard menuSmokeFrames == 20 else { return }
                if CommandLine.arguments.contains("--renderer-study") {
                    timer?.invalidate()
                    if #available(macOS 15.0, *) {
                        Task { @MainActor in
                            do {
                                let study=RendererStudy(app:self,directory:URL(fileURLWithPath:directory),reality:CommandLine.arguments.contains("--realitykit"))
                                rendererStudy=study;try await study.startStudy()
                            } catch { print("Renderer study failed: \(error)");exit(1) }
                        }
                    } else { print("Renderer study requires macOS 15 or newer");exit(1) }
                    return
                }
                if CommandLine.arguments.contains("--town-benchmark") {
                    startTownBenchmark(at:URL(fileURLWithPath:directory)); return
                }
                if CommandLine.arguments.contains("--town-smoke-test") {
                    timer?.invalidate()
                    let passed=checkTown(at:URL(fileURLWithPath:directory))
                    print("Town smoke: \(passed ? "PASS" : "FAIL") · \(directory)")
                    exit(passed ? 0:1)
                }
                if CommandLine.arguments.contains("--bb8-motion-smoke-test") {
                    timer?.invalidate()
                    let passed = checkBB8RenderedMotion(at: URL(fileURLWithPath:directory))
                    print("BB-8 rendered motion: \(passed ? "PASS" : "FAIL") · \(directory)")
                    exit(passed ? 0 : 1)
                }
                if CommandLine.arguments.contains("--character-smoke-test") {
                    // Snapshots can pump the run loop; keep this synchronous
                    // character check from re-entering the general smoke test.
                    timer?.invalidate()
                    let passed = checkPlayableCharacters(at: URL(fileURLWithPath: directory))
                    print("Playable character smoke test: \(passed ? "PASS" : "FAIL") · \(directory)")
                    exit(passed ? 0 : 1)
                }
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
                // Exercise portrait dragging through its native mouse handlers.
                let initialYaw = robot.root.eulerAngles.y
                let portraitPoint = mainMenu.portrait.convert(NSPoint(x: 150, y: 200), to: nil)
                func portraitMouse(_ type: NSEvent.EventType, offset: CGFloat) {
                    let event = NSEvent.mouseEvent(with: type,
                        location: NSPoint(x: portraitPoint.x + offset, y: portraitPoint.y),
                        modifierFlags: [], timestamp: 0, windowNumber: window.windowNumber,
                        context: nil, eventNumber: 0, clickCount: 1, pressure: 1)!
                    switch type {
                    case .leftMouseDown: mainMenu.portrait.mouseDown(with: event)
                    case .leftMouseDragged: mainMenu.portrait.mouseDragged(with: event)
                    default: mainMenu.portrait.mouseUp(with: event)
                    }
                }
                portraitMouse(.leftMouseDown, offset: 0)
                portraitMouse(.leftMouseDragged, offset: 80)
                portraitMouse(.leftMouseUp, offset: 80)
                mainMenu.animate(robot, r2d2: r2d2, bb8: bb8, wallE: wallE, dt: 1.0 / 60)
                menuSmokePassed = menuSmokePassed && abs(robot.root.eulerAngles.y - initialYaw - 0.8) < 0.001
                    && window.firstResponder === mainMenu
                portraitMouse(.leftMouseDown, offset: 80)
                portraitMouse(.leftMouseDragged, offset: 0)
                portraitMouse(.leftMouseUp, offset: 0)
                mainMenu.animate(robot, r2d2: r2d2, bb8: bb8, wallE: wallE, dt: 1.0 / 60)
                menuSmokePassed = menuSmokePassed && abs(robot.root.eulerAngles.y - initialYaw) < 0.001
                for (shortcut, model) in [("b", bb8.root), ("r", r2d2.root), ("w", wallE.root), ("m", robot.root)] {
                    let event = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: 0,
                        windowNumber: window.windowNumber, context: nil, characters: shortcut,
                        charactersIgnoringModifiers: shortcut, isARepeat: false, keyCode: 0)!
                    mainMenu.keyDown(with: event)
                    mainMenu.animate(robot, r2d2: r2d2, bb8: bb8, wallE: wallE, dt: 1.0 / 60)
                    let models = [robot.root, r2d2.root, bb8.root, wallE.root]
                    menuSmokePassed = menuSmokePassed && model.parent === mainMenu.stage.rootNode
                        && models.filter { $0.parent === mainMenu.stage.rootNode }.count == 1
                    portraitMouse(.leftMouseDown, offset: 0)
                    portraitMouse(.leftMouseDragged, offset: 40)
                    portraitMouse(.leftMouseUp, offset: 40)
                    mainMenu.animate(robot, r2d2: r2d2, bb8: bb8, wallE: wallE, dt: 1.0 / 60)
                    menuSmokePassed = menuSmokePassed && abs(model.eulerAngles.y - initialYaw - 0.4) < 0.001
                    if let tiff = mainMenu.portrait.snapshot().tiffRepresentation,
                       let png = NSBitmapImageRep(data: tiff)?.representation(using: .png, properties: [:]) {
                        try? png.write(to: URL(fileURLWithPath: directory).appendingPathComponent("menu-\(shortcut).png"))
                    }
                    portraitMouse(.leftMouseDown, offset: 40)
                    portraitMouse(.leftMouseDragged, offset: 0)
                    portraitMouse(.leftMouseUp, offset: 0)
                    mainMenu.animate(robot, r2d2: r2d2, bb8: bb8, wallE: wallE, dt: 0)
                }
                let down = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: 0,
                    windowNumber: window.windowNumber, context: nil, characters: "", charactersIgnoringModifiers: "", isARepeat: false, keyCode: 125)!
                let enter = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: 0,
                    windowNumber: window.windowNumber, context: nil, characters: "", charactersIgnoringModifiers: "", isARepeat: false, keyCode: 36)!
                // Mouse hover while the window appears may change selection.
                // Establish the keyboard test's starting item explicitly.
                mainMenu.settings = false; mainMenu.selection = 0; mainMenu.refresh()
                mainMenu.keyDown(with: down); mainMenu.keyDown(with: down); mainMenu.keyDown(with: enter)
                menuSmokePassed = menuSmokePassed && mainMenu.settings
                menuSmokePassed = ((try? checkDrivingAssistSettings(at: URL(fileURLWithPath: directory))) ?? false) && menuSmokePassed
                mainMenu.selection = 5; mainMenu.activate()
                menuSmokePassed = menuSmokePassed && !mainMenu.settings
                mainMenu.selection = 0; mainMenu.activate()
                menuSmokePassed = menuSmokePassed && inSandbox && mainMenu.isHidden && !hud.isHidden
                if CommandLine.arguments.contains("--menu-smoke-test") {
                    print("Native menu smoke test: \(menuSmokePassed ? "PASS" : "FAIL") · \(directory)")
                    exit(menuSmokePassed ? 0 : 1)
                }
            }
            return
        }
        // The automated renderer check uses a fixed clock and must not depend
        // on another app taking focus. Interactive play still pauses on blur.
        let step = smokeDirectory == nil ? dt : 1.0/60
        let raceDelta = smokeDirectory == nil ? wallDelta : step
        let advancing = (active || smokeDirectory != nil) && !simulation.paused
        advanceRaceFrame(step: step, raceDelta: raceDelta, advancing: advancing)
        if isDirtTrack {
            updateRaceWorld(dt: advancing ? step : 0)
            dirtWorld.town.update(dt: advancing ? step : 0, camera:world.camera.position, player:SIMD2(simulation.x,simulation.z))
            recordRaceScore()
            raceHUD.x = simulation.x; raceHUD.z = simulation.z; raceHUD.heading = simulation.heading
            raceHUD.introducing = dirtIntro != nil
            updateOpponents()
            raceHUD.race = race; raceHUD.scores = scores; raceHUD.paused = simulation.paused
            raceHUD.needsDisplay = true
        } else { updatePlayerModel(); world.update(simulation) }
        updateCamera(snap: false)
        hud.state = simulation; hud.cameraName = ["FOLLOW", "ORBIT", "OVERVIEW"][cameraMode]
        hud.needsDisplay = true
        if smokeDirectory != nil { smokeTest() }
    }
    @objc func showMainMenu(_ sender: Any?) {
        window.title = "Marvin · Playground"
        mainMenu.portrait.rendersContinuously = true
        inSandbox = false; dirtIntro = nil; view.clearInput()
        hud.isHidden = true; raceHUD.isHidden = true; mainMenu.isHidden = false; window.toolbar?.isVisible = false
        mainMenu.settings = false; mainMenu.selection = 0; mainMenu.refresh()
        mainMenu.stage.rootNode.addChildNode(robot.root)
        mainMenu.animate(robot, r2d2: r2d2, bb8: bb8, wallE: wallE, dt: 0)
        window.makeFirstResponder(mainMenu)
    }
    func startSandbox() {
        configurePlayer()
        window.title = "Marvin · Playground"
        world.camera.camera?.zFar = 80
        world.camera.camera?.screenSpaceAmbientOcclusionIntensity = 0
        isDirtTrack = false; dirtIntro = nil; simulation = Simulation(character: playerCharacter)
        mainMenu.portrait.rendersContinuously = false
        view.antialiasingMode = .multisampling4X
        view.scene = world.scene; world.scene.rootNode.addChildNode(world.camera)
        view.pointOfView = world.camera; raceHUD.isHidden = true
        inSandbox = true; mainMenu.isHidden = true; hud.isHidden = false
        window.toolbar?.isVisible = true; hud.helpVisible = mainMenu.showGuide
        for character in RacePerformance.Character.allCases { modelRoot(character).removeFromParentNode() }
        world.scene.rootNode.addChildNode(modelRoot(playerCharacter))
        reset(nil); updatePlayerModel(); world.update(simulation)
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
        configurePlayer()
        window.title = "Marvin · Dirt Track"; world.camera.camera?.zFar = 250
        world.camera.camera?.screenSpaceAmbientOcclusionIntensity = 0.70
        world.camera.camera?.screenSpaceAmbientOcclusionRadius = 1.6
        world.camera.camera?.screenSpaceAmbientOcclusionBias = 0.025
        isDirtTrack = true; inSandbox = true; simulation = Simulation(dirtTrack: true, dirtStartOffset:DirtCourse.playerGrid.offset, dirtStartPhase:DirtCourse.playerGrid.phase)
        hud.isHidden = true; raceHUD.isHidden = true
        view.isHidden = true
        SCNTransaction.begin(); SCNTransaction.disableActions = true
        raceHUD.helpVisible = mainMenu.showGuide; window.toolbar?.isVisible = true
        dirtWorld.camera = world.camera
        mainMenu.portrait.rendersContinuously = false
        view.antialiasingMode = .multisampling2X
        view.scene = dirtWorld.scene; dirtWorld.scene.rootNode.addChildNode(world.camera)
        dirtWorld.scene.rootNode.addChildNode(robot.root)
        dirtWorld.scene.rootNode.addChildNode(r2d2.root)
        dirtWorld.scene.rootNode.addChildNode(bb8.root); dirtWorld.scene.rootNode.addChildNode(wallE.root)
        dirtWorld.additionalContacts = [bb8.contacts,wallE.contacts]
        view.pointOfView = world.camera
        scoreLoadFailed = false; raceHUD.saveError = nil
        do { scores = try DirtScores.load(scoreURL) }
        catch { scores = []; scoreLoadFailed = true; raceHUD.saveError = "Could not load scores; file preserved" }
        reset(nil); updatePlayerModel()
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
        if let outro = dirtOutro, isDirtTrack {
            let t = min(1,outro/dirtIntroDuration)
            let blend = CGFloat(t*t*t*(t*(t*6-15)+10))
            let end = SCNVector3(0,38,-33)
            world.camera.position = SCNVector3(outroPosition.x+(end.x-outroPosition.x)*blend,
                outroPosition.y+(end.y-outroPosition.y)*blend,outroPosition.z+(end.z-outroPosition.z)*blend)
            world.camera.look(at:SCNVector3(outroTarget.x*(1-blend),outroTarget.y*(1-blend),outroTarget.z*(1-blend)),
                up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
            return
        }
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
        if isDirtTrack && cameraMode != 2 {
            world.camera.position = dirtWorld.town.clearCamera(from:target,to:world.camera.position)
        }
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
        dirtIntro = nil; dirtOutro = nil
        simulation.reset(); updatePlayerModel()
        if isDirtTrack {
            var random = SystemRandomNumberGenerator()
            let slots = DirtCourse.shuffledGrid(using:&random)
            simulation = Simulation(dirtTrack:true,dirtStartOffset:slots[0].offset,dirtStartPhase:slots[0].phase,character:playerCharacter)
            opponent = DirtOpponent(slot:slots[1])
            bb8Opponent = DirtOpponent(slot:slots[2],laneOffset:0)
            wallEOpponent = DirtOpponent(slot:slots[3],laneOffset:-0.65)
            performances = lineup.map { RacePerformance($0) }
            updateOpponents()
            race = DirtRace(startPhase:slots[0].phase); racePhysics = DirtRacePhysics(characters:lineup); scoreSaved = false
            dirtWorld.reset(); cameraMode = 0; cameraDistance = 4.5
        }
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
        func key(_ code: UInt16, down: Bool, modifiers: NSEvent.ModifierFlags = []) {
            let event = NSEvent.keyEvent(with: down ? .keyDown : .keyUp, location: .zero,
                modifierFlags: modifiers, timestamp: ProcessInfo.processInfo.systemUptime,
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
                var boostInputPassed = true
                for (forward, turn) in [(UInt16(13), UInt16(0)), (13, 2), (126, 123), (126, 124)] {
                    view.clearInput()
                    let shift = NSEvent.keyEvent(with: .flagsChanged, location: .zero,
                        modifierFlags: .shift, timestamp: 0, windowNumber: window.windowNumber,
                        context: nil, characters: "", charactersIgnoringModifiers: "",
                        isARepeat: false, keyCode: 56)!
                    view.flagsChanged(with: shift)
                    key(forward, down: true, modifiers: .shift); key(turn, down: true, modifiers: .shift)
                    let input = view.driveInput
                    var sample = Simulation(dirtTrack: true)
                    let startHeading = sample.heading
                    sample.advance(input, dt: 0.1); sample.advance(input, dt: 0.1)
                    let angle = atan2(sin(sample.heading-startHeading), cos(sample.heading-startHeading))
                    boostInputPassed = boostInputPassed && input.boost && input.throttle == 1
                        && abs(input.turn) == 1 && angle * input.turn < -0.04
                    key(turn, down: false, modifiers: .shift)
                    boostInputPassed = boostInputPassed && view.driveInput.turn == 0 && view.driveInput.boost
                }
                view.clearInput()
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
                let modelHeightRatio = robot.neutralHeight * Double(robot.root.scale.y) / R2D2.sceneHeight
                let modelScalePassed = abs(modelHeightRatio - 0.60/1.08) < 0.000001
                    && robot.root.scale.x == robot.root.scale.y && robot.root.scale.y == robot.root.scale.z
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
                // Verify wheel motion from signed travel, including brake,
                // reverse, and reset. These checks exercise the rendered nodes.
                var wheelState = Simulation(seed:0,dirtTrack:true), wheelInput = DriveInput()
                wheelInput.throttle = 1; wheelState.advance(wheelInput,dt:0.1); r2d2.update(wheelState)
                let forwardWheels = r2d2.wheels.map { $0.node.eulerAngles.x }
                wheelInput.brake = true; wheelState.advance(wheelInput,dt:0.1); r2d2.update(wheelState)
                let wheelsBrake = zip(r2d2.wheels,forwardWheels).allSatisfy { abs($0.0.node.eulerAngles.x-$0.1) < 0.000001 }
                wheelState.reset(); wheelInput.brake = false; wheelInput.throttle = -1
                wheelState.advance(wheelInput,dt:0.1); r2d2.update(wheelState)
                let wheelsReverse = r2d2.wheels.allSatisfy { $0.node.eulerAngles.x < 0 }
                wheelState.reset(); r2d2.update(wheelState)
                let wheelsReset = r2d2.wheels.allSatisfy { $0.node.eulerAngles.x == 0 && abs(Double($0.node.position.y)-$0.radius) < 0.000001 }
                let wheelDimensionsPassed = abs(R2D2.centerTire.width / R2D2.outerTire.width - 0.75) < 1e-9
                    && r2d2.wheels.allSatisfy {
                        let expected = $0.side == 0 ? R2D2.centerTire : R2D2.outerTire
                        guard let tire = $0.node.childNodes.first?.geometry as? SCNCylinder else { return false }
                        return abs(Double(tire.radius)-expected.radius) < 1e-7 && abs(Double(tire.height)-expected.width) < 1e-7
                    }
                let wheelsPassed = wheelDimensionsPassed && forwardWheels.allSatisfy { $0 > 0 } && wheelsBrake && wheelsReverse && wheelsReset
                r2d2.update(opponent.simulation)
                let bb8MotionPassed = bb8.checkMotion(), wallEMotionPassed = wallE.checkMotion()
                updateOpponents()
                let actingPassed = try checkRaceActing(at:url)
                let robotContactsPassed = try checkRobotContacts(at:url)
                let newModelsPassed = bb8MotionPassed && wallEMotionPassed
                    && abs(bb8.height/R2D2.sceneHeight-0.67/1.08) < 1e-7
                    && abs(wallE.height/R2D2.sceneHeight-1.016/1.08) < 1e-7
                    && bb8.root.parent === dirtWorld.scene.rootNode && wallE.root.parent === dirtWorld.scene.rootNode
                let r2 = opponent.simulation, angle = r2.heading
                for (name,side,height,front,lookHeight) in [
                    ("r2d2-front.png",0.85,0.7,1.5,0.43),
                    ("r2d2-wheels.png",0.65,0.16,0.85,0.15)] {
                    world.camera.position = SCNVector3(r2.x+cos(angle)*side+sin(angle)*front,
                        r2.groundY+height,r2.z-sin(angle)*side+cos(angle)*front)
                    world.camera.look(at:SCNVector3(r2.x,r2.groundY+lookHeight,r2.z),
                        up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                    if let tiff = view.snapshot().tiffRepresentation,
                       let image = NSBitmapImageRep(data:tiff)?.representation(using:.png,properties:[:]) {
                        try image.write(to:url.appendingPathComponent(name))
                    }
                }
                for (name, model, state) in [("bb8-front.png",bb8!,bb8Opponent.simulation),("walle-front.png",wallE!,wallEOpponent.simulation)] {
                    let angle = state.heading
                    world.camera.position = SCNVector3(state.x+cos(angle)*0.8+sin(angle)*1.5,state.groundY+0.75,state.z-sin(angle)*0.8+cos(angle)*1.5)
                    world.camera.look(at:SCNVector3(state.x,state.groundY+model.height*0.5,state.z),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                    if let tiff = view.snapshot().tiffRepresentation, let png = NSBitmapImageRep(data:tiff)?.representation(using:.png,properties:[:]) {
                        try png.write(to:url.appendingPathComponent(name))
                    }
                }
                updateCamera(snap:true)
                // No racer moves during countdown, pause or inactive frames.
                advanceRaceFrame(step:1.0/60,raceDelta:1.0/60,advancing:true)
                let heldAtStart = opponents.allSatisfy { $0.simulation.distance == 0 && $0.race.elapsed == 0 }
                race.countDown(dt:3)
                advanceRaceFrame(step:1.0/60,raceDelta:1.0/60,advancing:false)
                let opponentGatePassed = heldAtStart && opponents.allSatisfy { $0.simulation.distance == 0 && $0.race.elapsed == 0 }
                raceHUD.introducing = false; raceHUD.needsDisplay = true
                view.displayIfNeeded()
                if let tiff = view.snapshot().tiffRepresentation,
                   let image = NSBitmapImageRep(data:tiff)?.representation(using:.png,properties:[:]) {
                    try image.write(to:url.appendingPathComponent("dirt-grid.png"))
                }
                race.countDown(dt:3)
                var airborneTrailsPassed = true
                for _ in 0..<240 {
                    let phase = DirtCourse.phase(x:simulation.x,z:simulation.z)
                    let target = DirtCourse.point(phase+0.055)
                    let desired = atan2(target.x-simulation.x,target.z-simulation.z)
                    let error = atan2(sin(desired-simulation.heading),cos(desired-simulation.heading))
                    var input = DriveInput(); input.throttle = 1; input.boost = true; input.turn = -error*1.5
                    advanceRacePhysics(input,dt:1.0/60,raceDT:1.0/60)
                    let beforeTrails = dirtWorld.trailCounts
                    robot.update(simulation); updateOpponents(); dirtWorld.update(simulation,opponent:opponent.simulation,dt:1.0/60,modelScale:robot.modelScale,additional:[bb8Opponent.simulation,wallEOpponent.simulation])
                    if !simulation.hasDirtContact { airborneTrailsPassed = airborneTrailsPassed && dirtWorld.trailCounts[0] == beforeTrails[0] }
                    for (i,rival) in opponents.enumerated() where !rival.simulation.hasDirtContact {
                        airborneTrailsPassed = airborneTrailsPassed && dirtWorld.trailCounts[i+1] == beforeTrails[i+1]
                    }
                }
                let opponentPassed = opponents.allSatisfy { $0.simulation.distance > 10 && $0.race.elapsed > 3.9 } && wheelsPassed && opponentGatePassed && r2d2.triangleCount == 25158 && r2d2.hasCenterLeg && r2d2.wheels.count == 3 && r2d2.root.parent === dirtWorld.scene.rootNode
                    && opponent.simulation.distance > 10 && opponent.race.elapsed > 3.9
                raceHUD.opponents = opponents; raceHUD.race = race
                raceHUD.x = simulation.x; raceHUD.z = simulation.z; raceHUD.heading = simulation.heading
                raceHUD.introducing = false; raceHUD.needsDisplay = true
                // SCNView.snapshot excludes AppKit subviews; capture the HUD
                // separately for layout QA at the minimum supported window size.
                let hudFrame = raceHUD.frame
                raceHUD.frame = NSRect(x:0,y:0,width:900,height:550)
                if let bitmap = raceHUD.bitmapImageRepForCachingDisplay(in:raceHUD.bounds) {
                    raceHUD.cacheDisplay(in:raceHUD.bounds,to:bitmap)
                    if let png = bitmap.representation(using:.png,properties:[:]) {
                        try png.write(to:url.appendingPathComponent("dirt-hud.png"))
                    }
                }
                raceHUD.frame = hudFrame
                var fencePosts = 0, fenceRails = 0
                var fencePassed = true
                dirtWorld.scene.rootNode.enumerateChildNodes { node, _ in
                    if node.name == "Dirt fence post", let box = node.geometry as? SCNBox {
                        fencePosts += 1
                        let ground = DirtCourse.height(x:Double(node.position.x),z:Double(node.position.z))
                        let bottom = Double(node.position.y)-Double(box.height)/2
                        let top = Double(node.position.y)+Double(box.height)/2
                        fencePassed = fencePassed && abs(bottom-(ground-DirtCourse.postEmbed)) < 1e-5
                            && abs(top-(ground+DirtCourse.postHeight)) < 1e-5
                    }
                    if node.name == "Dirt fence rail", let source = node.geometry?.sources(for:.vertex).first {
                        fenceRails += 1
                        for i in 0..<source.vectorCount {
                            let values: [Double] = (0..<3).map { component in
                                source.data.withUnsafeBytes { bytes in
                                    let offset = source.dataOffset+i*source.dataStride+component*source.bytesPerComponent
                                    return source.bytesPerComponent == 4 ? Double(bytes.loadUnaligned(fromByteOffset:offset,as:Float.self))
                                        : bytes.loadUnaligned(fromByteOffset:offset,as:Double.self)
                                }
                            }
                            let ground = DirtCourse.height(x:values[0],z:values[2])
                            let clearance = DirtCourse.railClearance+(i%2 == 0 ? -0.0225 : 0.0225)
                            fencePassed = fencePassed && abs(values[1]-ground-clearance) < 0.002
                        }
                    }
                }
                fencePassed = fencePassed && fencePosts > 300 && fenceRails == 2
                let raceContactCount = racePhysics.contactCount
                let trailCounts = dirtWorld.trailCounts, racerEmissions = dirtWorld.racerEmittedCount
                dirtWorld.update(simulation,opponent:opponent.simulation,dt:0,modelScale:robot.modelScale,additional:[bb8Opponent.simulation,wallEOpponent.simulation])
                let effectsPaused = trailCounts == dirtWorld.trailCounts && racerEmissions == dirtWorld.racerEmittedCount
                var stoppedPlayer = simulation, stoppedRival = opponent.simulation
                stoppedPlayer.stop(); stoppedRival.stop()
                let stoppedOthers = [bb8Opponent.simulation,wallEOpponent.simulation].map { state -> Simulation in var stopped = state; stopped.stop(); return stopped }
                dirtWorld.update(stoppedPlayer,opponent:stoppedRival,dt:0.1,modelScale:robot.modelScale,additional:stoppedOthers)
                let dirtEffectsPassed = airborneTrailsPassed && effectsPaused && trailCounts.allSatisfy { $0 > 40 }
                    && racerEmissions.allSatisfy { $0 > 20 } && trailCounts == dirtWorld.trailCounts
                    && racerEmissions == dirtWorld.racerEmittedCount
                let dirtPassed = dirtEffectsPassed && fencePassed && opponentPassed && dirtStartPassed && dirtWorld.emittedCount > 20 && race.elapsed > 3.9
                    && DirtCourse.projection(x:simulation.x,z:simulation.z).distance < DirtCourse.fenceOffset
                for (mode,name) in [(2,"dirt-overview.png"),(0,"dirt-driving.png")] {
                    cameraMode = mode; updateCamera(snap:true)
                    if let tiff = view.snapshot().tiffRepresentation,
                       let image = NSBitmapImageRep(data:tiff)?.representation(using:.png,properties:[:]) {
                        try image.write(to:url.appendingPathComponent(name))
                    }
                }
                let trailView = DirtCourse.point(0.18)
                world.camera.position = SCNVector3(trailView.x+1.8,3.8,trailView.z+2.5)
                world.camera.look(at:SCNVector3(trailView.x,0,trailView.z),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                if let tiff = view.snapshot().tiffRepresentation,
                   let image = NSBitmapImageRep(data:tiff)?.representation(using:.png,properties:[:]) {
                    try image.write(to:url.appendingPathComponent("dirt-trails.png"))
                }
                // Accumulate a representative stretch of driving, then inspect
                // the surface treatment on all four independently posed models.
                var dustyDrive = DirtOpponent()
                for _ in 0..<1800 {
                    dustyDrive.advance(dt:1.0/60,raceDT:1.0/60)
                    robot.dirtCoating.update(dustyDrive.simulation)
                    r2d2.dirtCoating.update(dustyDrive.simulation)
                    bb8.dirtCoating.update(dustyDrive.simulation); wallE.dirtCoating.update(dustyDrive.simulation)
                }
                let dirtAmounts = [robot.dirtCoating.amount,r2d2.dirtCoating.amount,bb8.dirtCoating.amount,wallE.dirtCoating.amount]
                let coatingPassed = dirtAmounts.allSatisfy { $0 > 0.5 }
                for (name,state,height) in [("marvin-dirty.png",simulation,0.28),("r2d2-dirty.png",opponent.simulation,0.44),("bb8-dirty.png",bb8Opponent.simulation,0.27),("walle-dirty.png",wallEOpponent.simulation,0.42)] {
                    let angle = state.heading
                    world.camera.position = SCNVector3(state.x+cos(angle)*0.95+sin(angle)*1.4,state.groundY+0.8,state.z-sin(angle)*0.95+cos(angle)*1.4)
                    world.camera.look(at:SCNVector3(state.x,state.groundY+height,state.z),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                    if let tiff = view.snapshot().tiffRepresentation,
                       let image = NSBitmapImageRep(data:tiff)?.representation(using:.png,properties:[:]) {
                        try image.write(to:url.appendingPathComponent(name))
                    }
                }
                let hill = DirtCourse.point(0.69*2 * .pi)
                let hillside = DirtCourse.point(0.64*2 * .pi,offset:6)
                world.camera.position = SCNVector3(hillside.x,3.6,hillside.z)
                world.camera.look(at:SCNVector3(hill.x,0.9,hill.z),up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                if let tiff = view.snapshot().tiffRepresentation,
                   let image = NSBitmapImageRep(data:tiff)?.representation(using:.png,properties:[:]) {
                    try image.write(to:url.appendingPathComponent("dirt-fence.png"))
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
                let fullRaceTrailsPassed = try checkFullRaceTrails(at:url)
                let raceFinishPassed = try checkRaceFinish(at:url)
                reset(nil)
                let dirtResetPassed = opponent.race.elapsed == 0 && opponent.simulation.distance == 0 && race.laps.isEmpty && race.countdown == 3 && dirtWorld.emittedCount == 0 && robot.dirtCoating.amount == 0 && r2d2.dirtCoating.amount == 0 && dirtWorld.trailCounts == [0,0,0,0] && dirtWorld.racerEmittedCount == [0,0,0,0] && bb8.dirtCoating.amount == 0 && wallE.dirtCoating.amount == 0 && opponents.allSatisfy { $0.race.elapsed == 0 && $0.simulation.distance == 0 }
                showMainMenu(nil); startSandbox()
                let modeReturnPassed = !isDirtTrack && view.scene === world.scene && raceHUD.isHidden && !hud.isHidden
                let sandboxContactPassed = try checkSandboxContact(at:url)
                let passed = sandboxContactPassed && fullRaceTrailsPassed && raceFinishPassed && robotContactsPassed && actingPassed && newModelsPassed && coatingPassed && boostInputPassed && modelScalePassed && introPassed && introMidPassed && scoresPassed && dirtPassed && dirtResetPassed && modeReturnPassed && menuSmokePassed && robot.partCount == 23 && robot.triangleCount > 600_000
                    && traveled > 0.3 && abs(heading) > 0.3
                    && labelsPassed && groovesPassed && coursePassed && neckPassed && tracksPassed && groundContactPassed && pausePassed && brakePassed && focusPassed && headPassed && resetPassed && cameraPassed && lightIconPassed && darkIconPassed
                let report: [String: Any] = ["passed": passed, "sandboxContactPassed":sandboxContactPassed, "fullRaceTrailsPassed":fullRaceTrailsPassed, "raceFinishPassed":raceFinishPassed, "robotContactsPassed":robotContactsPassed, "raceContactCount":raceContactCount, "actingPassed":actingPassed, "bb8MotionPassed":bb8MotionPassed, "wallEMotionPassed":wallEMotionPassed, "newModelsPassed":newModelsPassed, "coatingPassed": coatingPassed, "bodyDirtAmounts": dirtAmounts, "dirtEffectsPassed": dirtEffectsPassed, "racerTrailMarks": trailCounts, "racerDirtParticles": racerEmissions, "boostSteeringPassed": boostInputPassed, "modelScalePassed": modelScalePassed, "marvinToR2D2HeightRatio": modelHeightRatio, "opponentPassed": opponentPassed, "fencePassed": fencePassed, "r2d2WheelsPassed": wheelsPassed, "introPassed": introPassed && introMidPassed, "scoresPassed": scoresPassed, "dirtPassed": dirtPassed, "dirtResetPassed": dirtResetPassed, "modeReturnPassed": modeReturnPassed, "menuPassed": menuSmokePassed, "parts": robot.partCount,
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
