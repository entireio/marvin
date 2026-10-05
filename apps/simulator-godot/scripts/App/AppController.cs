// Port of Sources/MarvinSimulator/App.swift (AppController).
//
// One partial class for the whole app, as in Swift: this file holds App.swift's members with their names
// verbatim; the files that extend AppController in Swift add theirs as further `partial class AppController`
// declarations (PlayerCharacter.cs, LevelLoading.cs, RaceAudio.cs and the smoke checks: AppSmokeTest.cs,
// BinarySkySmoke.cs, SandstormSmoke.cs, DuneContactSmoke.cs, DustVisibilitySmoke.cs, TrailMaterialSmoke.cs,
// TownSmoke.cs (with the town benchmark), EntranceSmoke.cs, PeopleSmoke.cs, CityEscapeSmoke.cs, PostRaceSmoke.cs,
// NavigationSmoke.cs, PassageSmoke.cs, TownDepartureMovie.cs, GroundPerformanceSmoke.cs, CharacterSmoke.cs,
// RaceAudioSmoke.cs, MenuSmoke.cs).
//
// PORT: AppController is a Godot Node and the NSApplication's delegate. `launch(tree:smokeDirectory:)` (AppModes.cs)
// is main.swift: NSApp.run() adds it to the scene tree and calls applicationDidFinishLaunching. The window, its
// toolbar and the menu bar are the facade's (NSWindow.cs, NSToolbar.cs, NSMenu.cs): the Godot window is the
// NSWindow, the title bar and toolbar are drawn over the full-size content view, the menu bar is the macOS global
// menu (in the title bar elsewhere), and Command shortcuts are Control shortcuts outside macOS. tick() runs from
// _Process while `timer` is valid (PORTING.md: Timer -> _Process). Game modes are dispatched by GameModes (each flag
// once) instead of tick's smoke branch; their entry points run the same 20 main-menu frames first. Not ported (marked
// "PORT"): the display link, BenchmarkGPUCapture, the renderer study and the display-link lifecycle check.
using System;
using System.Collections.Generic;
using System.Linq;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

public partial class AppController : Godot.Node, NSApplicationDelegate, NSWindowDelegate, NSToolbarDelegate
{
    public NSWindow window;
    public readonly SimulatorView view = new SimulatorView(); public readonly HUDView hud = new HUDView(); public readonly World world = new World();
    public readonly MainMenuView mainMenu = new MainMenuView(NSRect.zero);
    public bool inSandbox = false;
    public bool isDirtTrack = false;
    public RacePerformance.Character playerCharacter = RacePerformance.Character.marvin;
    public double? dirtOutro;
    public SCNVector3 outroPosition = SCNVector3Zero, outroTarget = SCNVector3Zero;
    public double? dirtIntro;
    public readonly double dirtIntroDuration = 3.2;
    public DirtWorld cachedDirtWorld;
    public DirtWorld dirtWorld
    {
        get { if (cachedDirtWorld is DirtWorld value) { return value; } var built = new DirtWorld(); cachedDirtWorld = built; return built; }
        set { cachedDirtWorld = value; }
    }
    public bool isLoadingDirt = false;
    public int loadingHeartbeats = 0;
    public readonly LevelLoadingView loadingView = new LevelLoadingView();
    public readonly RaceHUD raceHUD = new RaceHUD();
    public readonly FrameRateHUD frameRateHUD = new FrameRateHUD();
    // PORT: `let benchmarkGPUCapture = BenchmarkGPUCapture()` (Metal GPU capture for the town benchmark) is not ported.
    public DirtRace race = new DirtRace();
    public DirtRacePhysics racePhysics = new DirtRacePhysics();
    public DirtOpponent opponent = new DirtOpponent();
    public R2D2 r2d2;
    public DirtOpponent bb8Opponent = new DirtOpponent(laneOffset: 0);
    public DirtOpponent wallEOpponent = new DirtOpponent(laneOffset: -0.65);
    public ImportedRacer bb8, wallE;
    public RacePerformance[] performances = { new RacePerformance(RacePerformance.Character.marvin), new RacePerformance(RacePerformance.Character.r2d2), new RacePerformance(RacePerformance.Character.bb8), new RacePerformance(RacePerformance.Character.wallE) };
    public DirtOpponent[] opponents => new[] { opponent, bb8Opponent, wallEOpponent };
    public void updateOpponents()
    {
        raceHUD.opponents = opponents;
        var states = new[] { simulation }.Concat(opponents.Select(o => o.simulation)).ToArray();
        var actors = states.Select(s => new RacePerformance.Actor(s)).ToArray();
        for (int i = 0; i < performances.Length; i++) { performances[i].update(index: i, actors: actors, followingCourse: racePhysics.escape.waypoint[i] < 0); }
        var order = lineup;
        for (int i = 0; i < order.Length; i++) { updateModel(order[i], state: states[i], expression: performances[i].pose); }
    }
    public void advanceRacePhysics(DriveInput input, double dt, double raceDT)
    {
        var rivals = opponents;
        racePhysics.sand = dirtWorld.duneSand.field;
        racePhysics.advance(input, ref simulation, ref race, rivals, dt: dt, raceDT: raceDT,
            robotCollisionsEnabled: mainMenu.raceRobotCollisions, assists: mainMenu.raceAssists, city: dirtWorld.town.collisionWorld);
        dirtWorld.updateGate(racePhysics.gate);
        opponent = rivals[0]; bb8Opponent = rivals[1]; wallEOpponent = rivals[2];
    }
    public DirtScore[] scores = Array.Empty<DirtScore>();
    public bool scoreSaved = false;
    public bool scoreLoadFailed = false;
    /// PORT: Application Support/Marvin Simulator is Godot's user:// (the app's own data directory, PORTING.md),
    /// globalized to a file path. The file keeps the Swift JSONEncoder format ([{"date":..,"laps":[..]}], DirtScores).
    /// `scoreDirectory` is a test hook (the --playthrough mode) for a run that must not touch the player's scores.
    public URL scoreURL
    {
        get
        {
            if ((smokeDirectory ?? scoreDirectory) is string directory) { return URL.fileURLWithPath(directory).appendingPathComponent("test-scores.json"); }
            return URL.fileURLWithPath("user://").appendingPathComponent("motocross-v4-scores.json");
        }
    }
    public string scoreDirectory;
    public bool menuSmokePassed = false;
    public int menuSmokeFrames = 0;
    public Robot robot;
    public Simulation simulation = new Simulation();
    public Marvin.SceneKit.Timer timer; public double lastTime = ProcessInfo.processInfo.systemUptime;
    public bool raceCameraLocked => isDirtTrack && race.finished;
    public OverviewMotion overviewMotion = new OverviewMotion();
    public SCNVector3? freeCameraEye;
    public double cameraBoomFraction = 1.0;
    public SCNVector3 cameraAim = SCNVector3Zero;
    public int cameraMode = 1; public double orbitYaw = 0.65, orbitPitch = 0.5, cameraDistance = 3.5;
    public bool active = true;
    public NSToolbarItem pauseItem;
    public NSKeyValueObservation appearanceObservation;
    public string appliedIconName = "";
    public readonly TownFrameMeter townMeter = new TownFrameMeter();
    public object rendererStudy;
    public double? townBenchmarkStart;
    public string townBenchmarkRunID = Guid.NewGuid().ToString().ToUpperInvariant();
    public URL? townBenchmarkDirectory;
    public List<double> townBenchmarkCPU = new();
    public List<double[]> townBenchmarkTimeline = new();
    public List<Dictionary<string, object>> townBenchmarkHUDSamples = new();
    public List<Dictionary<string, object>> townBenchmarkResourceSamples = new();
    public object frameDisplayLink;
    public List<Double2> townBenchmarkRoute = new();
    public int townBenchmarkWaypoint = 0;
    public RaceAudio raceAudio;
    public bool raceSoundMuted = UserDefaults.standard.@bool("raceSoundMuted");
    public bool? weatherOverride; // deterministic native test hook; never a user setting
    public int smokeFrames = 0;
    /// The flags App.swift reads its smoke directory after. PORT: a game mode passes its directory to the
    /// constructor instead (Godot-only modes are not in this list).
    public static readonly string[] smokeFlags = { "--mesh-reuse-test", "--shadow-culling-test", "--ground-performance-test", "--passage-smoke-test", "--navigation-smoke-test", "--dune-contact-test", "--entrance-smoke-test", "--viewport-smoke-test", "--audio-smoke-test", "--weather-reset-test", "--storm-race-test", "--people-smoke-test", "--visual-regression-test", "--dust-visibility-test", "--sandstorm-smoke-test", "--binary-sky-smoke-test", "--debris-smoke-test", "--loading-smoke-test", "--postrace-smoke-test", "--trail-material-smoke-test", "--town-departure-movie", "--smoke-test", "--menu-smoke-test", "--character-smoke-test", "--bb8-motion-smoke-test", "--town-smoke-test", "--city-escape-smoke-test", "--town-benchmark", "--renderer-study" };
    public readonly string smokeDirectory;

    /// The app's resource directory (Bundle.main.resourceURL): the assets copied by tools/sync-assets.py.
    public const string resources = "res://assets";

    public AppController() : this(null) { }
    /// PORT: `smokeDirectory` is App.swift's `let` closure over CommandLine.arguments; a game mode passes its
    /// (already resolved) output directory, otherwise the directory after a flag of `smokeFlags` is used.
    public AppController(string smokeDirectory)
    {
        if (smokeDirectory != null) { this.smokeDirectory = smokeDirectory; return; }
        var args = CommandLine.arguments;
        var i = Array.FindIndex(args, a => smokeFlags.Contains(a));
        this.smokeDirectory = i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public void applicationDidFinishLaunching(Notification notification)
    {
        if (smokeDirectory != null) { weatherOverride = false; }
        appearanceObservation = NSApp.observe("effectiveAppearance", NSKeyValueObservingOptions.initial | NSKeyValueObservingOptions.@new, (app, _) =>
        {
            updateApplicationIcon(app.effectiveAppearance);
        });
        try
        {
            robot = new Robot(resources);
            r2d2 = new R2D2(resources);
            bb8 = new ImportedRacer(ImportedRacer.Kind.bb8, resources);
            wallE = new ImportedRacer(ImportedRacer.Kind.wallE, resources);
        }
        catch (Exception error)
        {
            var alert = new NSAlert(); alert.messageText = "A simulator model could not be loaded";
            alert.informativeText = $"{error.Message}\nRun apps/simulator-godot/tools/sync-assets.py.";
            alert.runModal(); NSApp.terminate(null); return;
        }
        world.scene.rootNode.addChildNode(robot.root);
        window = new NSWindow(new NSRect(0, 0, 1280, 820),
            NSWindow.StyleMask.titled | NSWindow.StyleMask.closable | NSWindow.StyleMask.miniaturizable | NSWindow.StyleMask.resizable | NSWindow.StyleMask.fullSizeContentView, NSWindow.BackingStoreType.buffered, false);
        window.title = "Marvin · Playground";
        window.minSize = new NSSize(900, 640);
        window.backgroundColor = NSColor.windowBackgroundColor;
        window.@delegate = this;
        var toolbar = new NSToolbar("SimulatorToolbar");
        toolbar.@delegate = this; toolbar.displayMode = NSToolbar.DisplayMode.iconAndLabel;
        window.toolbar = toolbar;
        view.scene = world.scene; view.pointOfView = world.camera;
        view.antialiasingMode = SCNAntialiasingMode.multisampling4X;
        view.preferredFramesPerSecond = 60; view.rendersContinuously = true;
        // Keep the renderer independent of toolbar visibility; only overlays use the safe content area.
        // PORT: `view.autoresizingMask = [.width, .height]`: the facade's content view always fills the window.
        window.contentView = view;
        installContentOverlay(hud);
        raceHUD.isHidden = true; installContentOverlay(raceHUD);
        view.onCommand = code =>
        {
            switch (code)
            {
                case 5: if (isDirtTrack && inSandbox) { racePhysics.gate.wantsOpen = !racePhysics.gate.wantsOpen; } break;
                case 8: cycleCamera(null); break;
                case 35: case 53: togglePause(null); break;
                case 4: simulation.centerHead(); break;
                case 44: toggleHelp(null); break;
                default: break;
            }
        };
        view.onFocusLost = () =>
        {
            // Race pause/focus gates freeze all bodies without erasing one
            // competitor's momentum. Released input decelerates after resume.
            if (!isDirtTrack) { simulation.stop(); }
        };
        view.onOrbit = (dx, dy) =>
        {
            if (!(inSandbox && dirtIntro == null && !raceCameraLocked)) { return; }
            cameraMode = 1;
            orbitYaw += (double)dx * 0.008;
            orbitPitch = max(0.15, min(1.35, orbitPitch + (double)dy * 0.008));
        };
        view.onZoom = delta =>
        {
            if (!(inSandbox && dirtIntro == null && !raceCameraLocked)) { return; }
            cameraDistance = max(1.6, min(9, cameraDistance + (double)delta * 0.035));
        };
        installContentOverlay(mainMenu);
        frameRateHUD.isHidden = true; installContentOverlay(frameRateHUD);
        // Keep the delegate's optional callback capabilities stable while
        // SceneKit renders on its background queue.
        townMeter.fpsHUD = frameRateHUD;
        view.@delegate = townMeter;
        mainMenu.onSandbox = () => startSandbox();
        mainMenu.onDirtTrack = () => loadDirtTrack();
        makeMenu();
        window.center(); window.makeKeyAndOrderFront(null);
        window.makeFirstResponder(view); NSApp.activate(ignoringOtherApps: true);
        robot.update(simulation); showMainMenu(null);
        // Retain display-synchronised updates as an explicit diagnostic option:
        // native comparisons have not established a repeatable stutter reduction.
        // PORT: the display-link variant (--display-link-updates, --benchmark-display-link) and
        // --display-link-lifecycle-check are not ported: the 1/60 s timer ticks from Godot's frame loop, which is
        // display-synchronised (vsync) and capped at the view's preferredFramesPerSecond.
        timer = new Marvin.SceneKit.Timer(1.0 / 60);
    }

    public void updateApplicationIcon(NSAppearance appearance)
    {
        var dark = appearance.bestMatch(new[] { NSAppearance.Name.aqua, NSAppearance.Name.darkAqua }) == NSAppearance.Name.darkAqua;
        var name = dark ? "AppIconDark" : "AppIconLight";
        // PORT: Bundle.main.url(forResource: name, withExtension: "icns"): the icon's PNG in the synced assets.
        if (!(NSImage.contentsOf($"{resources}/Icons/{name}.png") is NSImage icon)) { return; }
        NSApp.applicationIconImage = icon;
        appliedIconName = name;
    }

    /// Shared lifecycle gate for all racers (also exercised by native smoke).
    public void advanceRaceFrame(double step, double raceDelta, bool advancing)
    {
        if (advancing)
        {
            if (dirtIntro is double intro)
            {
                dirtIntro = intro + step;
                if (dirtIntro.Value >= dirtIntroDuration) { dirtIntro = null; view.clearInput(); }
            }
            else if (isDirtTrack && race.countdown > 0) { race.countDown(dt: raceDelta); }
            else
            {
                if (isDirtTrack)
                {
                    advanceRacePhysics(view.driveInput, dt: step, raceDT: raceDelta);
                    if (race.finished)
                    {
                        if (dirtOutro == null)
                        {
                            dirtOutro = 0; outroPosition = world.camera.position;
                            var front = world.camera.worldFront;
                            outroTarget = new SCNVector3(outroPosition.x + (CGFloat)front.x * 4.5,
                                outroPosition.y + (CGFloat)front.y * 4.5, outroPosition.z + (CGFloat)front.z * 4.5);
                            view.clearInput();
                        }
                        else { dirtOutro = min(dirtIntroDuration, dirtOutro.Value + step); }
                    }
                }
                else { simulation.advance(view.driveInput, dt: step); }
            }
        }
    }

    public void displayTick(object sender) { tick(); }

    public override void _Process(double delta)
    {
        if (timer is { isValid: true }) { tick(); }
    }

    public void tick()
    {
        var now = ProcessInfo.processInfo.systemUptime;
        var wallDelta = max(0, now - lastTime);
        var dt = min(wallDelta, 0.1); lastTime = now;
        if (isLoadingDirt) { loadingHeartbeats += 1; return; }
        if (townBenchmarkStart != null) { tickTownBenchmark(now: now, dt: dt); return; }
        if (!inSandbox)
        {
            mainMenu.animate(robot, r2d2: r2d2, bb8: bb8, wallE: wallE, dt: dt);
            if (smokeDirectory != null)
            {
                // PORT: App.swift dispatches the smoke checks here at the 20th menu frame. In Godot each flag is a game
                // mode (GameModes) whose entry point waits for these 20 frames and then runs the same check
                // (AppModes.cs); the menu check of --smoke-test and --menu-smoke-test is checkMainMenu (MenuSmoke.cs).
                menuSmokeFrames += 1;
            }
            return;
        }
        // The automated renderer check uses a fixed clock and must not depend
        // on another app taking focus. Interactive play still pauses on blur.
        var step = smokeDirectory == null ? dt : 1.0 / 60;
        var raceDelta = smokeDirectory == null ? wallDelta : step;
        var advancing = ((active && window.occlusionState.contains(NSWindow.OcclusionState.visible)) || smokeDirectory != null) && !simulation.paused;
        advanceRaceFrame(step: step, raceDelta: raceDelta, advancing: advancing);
        if (isDirtTrack)
        {
            updateRaceWorld(dt: advancing ? step : 0);

            recordRaceScore();
            raceHUD.x = simulation.x; raceHUD.z = simulation.z; raceHUD.heading = simulation.heading;
            raceHUD.introducing = dirtIntro != null;
            updateDepartureHUD();
            raceHUD.escaping = racePhysics.escape.active; raceHUD.escapeComplete = racePhysics.escape.complete;
            updateOpponents();
            raceHUD.race = race; raceHUD.scores = scores; raceHUD.paused = simulation.paused;
            raceHUD.needsDisplay = true;
        }
        else { updatePlayerModel(); world.update(simulation); }
        updateCamera(snap: false, dt: step);
        // Classify residents against this frame's camera, including camera cuts.
        if (isDirtTrack)
        {
            dirtWorld.town.update(dt: advancing ? step : 0, camera: world.camera.position, player: new Double2(simulation.x, simulation.z), robots: robotBodies(),
                visible: node => view.isNode(node, insideFrustumOf: world.camera), shadowCamera: world.camera, viewportAspect: (double)(view.bounds.width / view.bounds.height));
        }
        hud.state = simulation; hud.cameraName = new[] { "FOLLOW", "ORBIT", "OVERVIEW" }[cameraMode];
        updateRaceAudio(dt: step, advancing: advancing);
        hud.needsDisplay = true;
        frameRateHUD.isHidden = !inSandbox || (isDirtTrack && racePhysics.escape.active);
        if (smokeDirectory != null) { smokeTest(); }
    }
    /// The racers as collision bodies, player first, in lineup order (App.swift tick and the town smoke checks).
    public List<RobotCollisions.Body> robotBodies() =>
        new[] { simulation }.Concat(opponents.Select(o => o.simulation)).Select((s, i) =>
            new RobotCollisions.Body(position: new Double3(s.x, s.groundY, s.z), heading: s.heading, profile: RobotCollisions.profiles[(int)lineup[i]])).ToList();

    public void showMainMenu(object sender)
    {
        raceAudio?.resetConversation();
        window.title = "Marvin · Playground";
        mainMenu.portrait.rendersContinuously = true;
        inSandbox = false; dirtIntro = null; view.clearInput();
        frameRateHUD.isHidden = true;
        hud.isHidden = true; raceHUD.isHidden = true; mainMenu.isHidden = false; if (window.toolbar != null) { window.toolbar.isVisible = false; }
        mainMenu.settings = false; mainMenu.selection = 0; mainMenu.refresh();
        mainMenu.stage.rootNode.addChildNode(robot.root);
        mainMenu.animate(robot, r2d2: r2d2, bb8: bb8, wallE: wallE, dt: 0);
        window.makeFirstResponder(mainMenu);
    }
    public void startSandbox()
    {
        configurePlayer();
        window.title = "Marvin · Playground";
        world.camera.camera.zFar = 80;
        world.camera.camera.wantsHDR = false; world.camera.camera.bloomIntensity = 0;
        world.camera.camera.screenSpaceAmbientOcclusionIntensity = 0;
        isDirtTrack = false; dirtIntro = null; simulation = new Simulation(character: playerCharacter);
        mainMenu.portrait.rendersContinuously = false;
        view.antialiasingMode = SCNAntialiasingMode.multisampling4X;
        view.scene = world.scene; world.scene.rootNode.addChildNode(world.camera);
        view.pointOfView = world.camera; raceHUD.isHidden = true;
        inSandbox = true; mainMenu.isHidden = true; hud.isHidden = false;
        if (window.toolbar != null) { window.toolbar.isVisible = true; } hud.helpVisible = mainMenu.showGuide;
        foreach (var character in RacePerformance.CharacterAllCases) { modelRoot(character).removeFromParentNode(); }
        world.scene.rootNode.addChildNode(modelRoot(playerCharacter));
        reset(null); updatePlayerModel(); world.update(simulation);
    }
    public void recordRaceScore()
    {
        if (!(race.finished && !scoreSaved)) { return; }
        scoreSaved = true;
        if (scoreLoadFailed) { return; }
        scores = DirtScores.ranked(scores.Concat(new[] { new DirtScore(laps: race.laps) }));
        try { DirtScores.save(scores, scoreURL.path); }
        catch (Exception) { raceHUD.saveError = "Could not save score"; }
    }
    public void startDirtTrack()
    {
        if (raceAudio == null && (smokeDirectory == null || CommandLine.arguments.Contains("--town-benchmark")))
        {
            try { raceAudio = new RaceAudio(resources: resources); }
            catch (Exception error) { NSLog($"Race audio assets unavailable: {error}"); }
        }
        configurePlayer();
        window.title = "Marvin · Dirt Track"; world.camera.camera.zFar = 250;
        world.camera.camera.screenSpaceAmbientOcclusionIntensity = 0.70;
        world.camera.camera.screenSpaceAmbientOcclusionRadius = 1.6;
        world.camera.camera.screenSpaceAmbientOcclusionBias = 0.025;
        isDirtTrack = true; inSandbox = true; simulation = new Simulation(dirtTrack: true, dirtStartOffset: DirtCourse.playerGrid.offset, dirtStartPhase: DirtCourse.playerGrid.phase);
        hud.isHidden = true; raceHUD.isHidden = true;
        view.isHidden = true;
        SCNTransaction.begin(); SCNTransaction.disableActions = true;
        raceHUD.helpVisible = mainMenu.showGuide; if (window.toolbar != null) { window.toolbar.isVisible = !isLoadingDirt; }
        dirtWorld.camera = world.camera;
        dirtWorld.sky.attach(camera: world.camera);
        // Geometry comparison tools own their temporary reference/proxy pairs.
        // Normal gameplay uses the exact-position shadow mesh by default.
        var shadowArguments = CommandLine.arguments;
        var independentGeometryComparison = new[] { "--ground-performance-test", "--shadow-culling-test", "--mesh-reuse-test" }.Any(shadowArguments.Contains);
        var useShadowBatch = !shadowArguments.Contains("--benchmark-shadow-batch-reference")
            && (!independentGeometryComparison || shadowArguments.Contains("--benchmark-shadow-batch-live"));
        if (useShadowBatch)
        {
            try { dirtWorld.town.prepareShadowBatch(camera: world.camera.camera); }
            catch (Exception error) { NSLog($"Shadow batch unavailable; retaining original shadow geometry: {error}"); }
        }
        mainMenu.portrait.rendersContinuously = false;
        view.antialiasingMode = SCNAntialiasingMode.multisampling2X;
        view.scene = dirtWorld.scene; dirtWorld.scene.rootNode.addChildNode(world.camera);
        dirtWorld.scene.rootNode.addChildNode(robot.root);
        dirtWorld.scene.rootNode.addChildNode(r2d2.root);
        dirtWorld.scene.rootNode.addChildNode(bb8.root); dirtWorld.scene.rootNode.addChildNode(wallE.root);
        dirtWorld.additionalContacts = new[] { bb8.contacts, wallE.contacts };
        raceHUD.mapRegion = RaceMapRegion.course; cameraAim = SCNVector3Zero;
        raceHUD.configureNavigationMap(town: dirtWorld.town);
        view.pointOfView = world.camera;
        scoreLoadFailed = false; raceHUD.saveError = null;
        try { scores = DirtScores.load(scoreURL.path); }
        catch (Exception) { scores = Array.Empty<DirtScore>(); scoreLoadFailed = true; raceHUD.saveError = "Could not load scores; file preserved"; }
        reset(null); updatePlayerModel();
        dirtIntro = 0; updateCamera(snap: true);
        SCNTransaction.commit();
        // Compile materials/upload geometry and draw the first correct overview
        // before revealing the scene, avoiding a frame from the previous camera.
        if (isLoadingDirt)
        {
            loadingView.update(0.93, "Getting ready to race");
            view.prepare(new object[] { dirtWorld.scene }, success =>
            {
                DispatchQueue.main.async(() =>
                {
                    if (success) { revealDirtTrack(); }
                    else { failDirtLoading(); }
                });
            });
        }
        else
        {
            _ = view.prepare(dirtWorld.scene, shouldAbortBlock: null);
            revealDirtTrack();
        }
    }
    public void revealDirtTrack()
    {
        _ = view.snapshot();
        raceHUD.race = race; raceHUD.introducing = true; raceHUD.scores = scores;
        mainMenu.isHidden = true; raceHUD.isHidden = false; view.isHidden = false;
        loadingView.update(1, "Ready");
        isLoadingDirt = false; loadingView.removeFromSuperview(); if (window.toolbar != null) { window.toolbar.isVisible = true; }
        window.makeFirstResponder(view);
        lastTime = ProcessInfo.processInfo.systemUptime;
        finishLoadingCheckIfNeeded();
    }
    public void updateCamera(bool snap, double dt = 1.0 / 60)
    {
        if (isDirtTrack) { raceHUD.mapRegion = RaceMapRegion.at(new Double2(simulation.x, simulation.z), previous: raceHUD.mapRegion); }
        if (raceCameraLocked)
        {
            cameraMode = 2;
            world.camera.position = new SCNVector3(0, 38, -33);
            world.camera.look(at: SCNVector3Zero, up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
            return;
        }
        if (dirtOutro is double outro && isDirtTrack)
        {
            var t = min(1, outro / dirtIntroDuration);
            var blend = (CGFloat)(t * t * t * (t * (t * 6 - 15) + 10));
            var end = new SCNVector3(0, 38, -33);
            world.camera.position = new SCNVector3(outroPosition.x + (end.x - outroPosition.x) * blend,
                outroPosition.y + (end.y - outroPosition.y) * blend, outroPosition.z + (end.z - outroPosition.z) * blend);
            world.camera.look(at: new SCNVector3(outroTarget.x * (1 - blend), outroTarget.y * (1 - blend), outroTarget.z * (1 - blend)),
                up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
            return;
        }
        var lookAhead = isDirtTrack && cameraMode == 0 ? 1.5 : 0.0;
        var target = new SCNVector3(simulation.x + sin(simulation.heading) * lookAhead,
            simulation.groundY + 0.35, simulation.z + cos(simulation.heading) * lookAhead);
        if (isDirtTrack && max(abs(simulation.x), abs(simulation.z)) > DesertTerrain.townEdge)
        {
            target.y = max(target.y, (CGFloat)(DirtCourse.height(x: (double)target.x, z: (double)target.z) + 0.35));
        }
        SCNVector3 desired;
        if (cameraMode == 2)
        {
            if (isDirtTrack && raceHUD.mapRegion != RaceMapRegion.course)
            {
                desired = new SCNVector3(target.x, target.y + 38, target.z - 33);
            }
            else { desired = isDirtTrack ? new SCNVector3(0, 38, -33) : new SCNVector3(0, 11.7, -10); }
        }
        else
        {
            var angle = cameraMode == 0 ? simulation.heading + Math.PI + (isDirtTrack ? 0 : 0.45) : orbitYaw;
            var elevation = cameraMode == 0 ? (isDirtTrack ? 0.30 : 0.48) : orbitPitch;
            desired = new SCNVector3(simulation.x + sin(angle) * cos(elevation) * cameraDistance,
                simulation.groundY + 0.4 + sin(elevation) * cameraDistance,
                simulation.z + cos(angle) * cos(elevation) * cameraDistance);
        }
        if (dirtIntro is double intro && isDirtTrack)
        {
            var t = max(0, min(1, (intro - 0.35) / (dirtIntroDuration - 0.35)));
            var blend = (CGFloat)(t * t * t * (t * (t * 6 - 15) + 10));
            var start = new SCNVector3(0, 38, -33);
            world.camera.position = new SCNVector3(start.x + (desired.x - start.x) * blend,
                start.y + (desired.y - start.y) * blend, start.z + (desired.z - start.z) * blend);
            world.camera.look(at: new SCNVector3(target.x * blend, target.y * blend, target.z * blend),
                up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
            return;
        }
        var aim = cameraMode == 2 && (!isDirtTrack || raceHUD.mapRegion == RaceMapRegion.course) ? SCNVector3Zero : target;
        if (cameraMode == 2)
        {
            Double3 eye = new Double3((double)desired.x, (double)desired.y, (double)desired.z), focus = new Double3((double)aim.x, (double)aim.y, (double)aim.z);
            if (snap) { overviewMotion.reset(eye: eye, aim: focus); }
            else { overviewMotion.advance(eye: eye, aim: focus, dt: dt); }
            Double3 p = overviewMotion.eye, q = overviewMotion.aim;
            world.camera.position = new SCNVector3(p.x, p.y, p.z); cameraAim = new SCNVector3(q.x, q.y, q.z);
        }
        else
        {
            var current = freeCameraEye ?? desired; var mix = snap ? 1 : (CGFloat)(1 - exp(-7.67 * max(0, dt)));
            var eye = new SCNVector3(current.x + (desired.x - current.x) * mix, current.y + (desired.y - current.y) * mix, current.z + (desired.z - current.z) * mix);
            freeCameraEye = eye;
            world.camera.position = eye;
            cameraAim = target;
        }
        if (isDirtTrack)
        {
            if (cameraMode != 2 || world.camera.position.y < cameraAim.y + 12)
            {
                // Collision starts at the robot, never the look-ahead point which
                // can already be through a wall during a narrow turn.
                var pivot = dirtWorld.town.cameraPivot(position: new Double3(simulation.x, simulation.groundY, simulation.z), chassisHeight: RobotCollisions.profiles[(int)lineup[0]].height);
                SCNVector3 eye = dirtWorld.town.terrainCamera(from: pivot, to: world.camera.position), clear = dirtWorld.town.clearCamera(from: pivot, to: eye);
                double distance(SCNVector3 p) => sqrt(pow((double)(p.x - pivot.x), 2) + pow((double)(p.y - pivot.y), 2) + pow((double)(p.z - pivot.z), 2));
                var allowed = min(1, min(distance(clear), dirtWorld.town.cameraRoom(at: pivot, range: distance(eye))) / max(0.001, distance(eye)));
                if (snap || allowed < cameraBoomFraction) { cameraBoomFraction = allowed; }
                else { cameraBoomFraction += (allowed - cameraBoomFraction) * (1 - exp(-3.5 * max(0, dt))); }
                var f = (CGFloat)cameraBoomFraction;
                world.camera.position = new SCNVector3(pivot.x + (eye.x - pivot.x) * f, pivot.y + (eye.y - pivot.y) * f, pivot.z + (eye.z - pivot.z) * f);
                // In a tight alley keep the route visible over the chassis rather
                // than pointing the compressed camera down into its head.
                var close = (CGFloat)(1 - min(1, distance(world.camera.position) / 1.5));
                var raised = min(pivot.y - 0.12, (CGFloat)(simulation.groundY + RobotCollisions.profiles[(int)lineup[0]].height + 0.45));
                if (cameraMode == 0)
                {
                    cameraAim = new SCNVector3(target.x, target.y + (raised - target.y) * close, target.z);
                }
                else
                {
                    CGFloat dx = pivot.x - eye.x, dz = pivot.z - eye.z, length = max(0.001, sqrt(dx * dx + dz * dz));
                    cameraAim = new SCNVector3(target.x + dx / length * close, target.y + (raised - target.y) * close, target.z + dz / length * close);
                }
            }
            if (cameraMode == 2) { world.camera.position.y = max(world.camera.position.y, (CGFloat)(DirtCourse.height(x: (double)world.camera.position.x, z: (double)world.camera.position.z) + 0.18)); }
        }
        world.camera.look(at: cameraAim, up: new SCNVector3(0, 1, 0), localFront: new SCNVector3(0, 0, -1));
        if (cameraMode == 2) { freeCameraEye = null; cameraBoomFraction = 1; }
        if (cameraMode != 2)
        {
            SCNVector3 p = world.camera.position, q = cameraAim;
            overviewMotion.reset(eye: new Double3((double)p.x, (double)p.y, (double)p.z), aim: new Double3((double)q.x, (double)q.y, (double)q.z));
        }
    }

    public void togglePause(object sender)
    {
        if (!inSandbox) { return; }
        simulation.paused = !simulation.paused; view.clearInput();
        if (simulation.paused) { raceAudio?.stop(); }
        if (pauseItem != null) { pauseItem.label = simulation.paused ? "Resume" : "Pause"; }
        if (pauseItem != null) { pauseItem.image = NSImage.systemSymbolName(simulation.paused ? "play.fill" : "pause.fill", accessibilityDescription: null); }
        window.makeFirstResponder(view);
    }
    public void reset(object sender)
    {
        raceAudio?.resetConversation();
        if (isLoadingDirt && sender != null) { return; }
        if (!inSandbox) { return; }
        cameraMode = 1; orbitYaw = 0.65; orbitPitch = 0.5; cameraDistance = 3.5;
        dirtIntro = null; dirtOutro = null;
        raceHUD.escaping = false; raceHUD.escapeComplete = false;
        if (isDirtTrack) { setRaceControlsHidden(false); }
        simulation.reset(); updatePlayerModel();
        if (isDirtTrack)
        {
            var random = new SystemRandomNumberGenerator();
            var slots = DirtCourse.shuffledGrid(ref random);
            if (TestPins.gridSlots() is (double phase, double offset)[] pinned) { slots = pinned; }
            simulation = new Simulation(dirtTrack: true, dirtStartOffset: slots[0].offset, dirtStartPhase: slots[0].phase, character: playerCharacter);
            opponent = new DirtOpponent(slot: slots[1]);
            bb8Opponent = new DirtOpponent(slot: slots[2], laneOffset: 0);
            wallEOpponent = new DirtOpponent(slot: slots[3], laneOffset: -0.65);
            performances = lineup.Select(c => new RacePerformance(c)).ToArray();
            updateOpponents();
            race = new DirtRace(startPhase: slots[0].phase); racePhysics = new DirtRacePhysics(characters: lineup, townRoutes: dirtWorld.escapeRoutes); dirtWorld.updateGate(racePhysics.gate); scoreSaved = false;
            dirtWorld.reset(); dirtWorld.sky.apply(TestPins.daylight() ?? BinaryDaylight.random());
            racePhysics.storm = new Sandstorm(enabled: weatherOverride ?? Sandstorm.drawForRace());
            raceHUD.stormSelected = racePhysics.storm.enabled;
            window.title = racePhysics.storm.enabled ? "Marvin · Dirt Track · Sandstorm" : "Marvin · Dirt Track";
            dirtWorld.configureStorm(racePhysics.storm);
            cameraMode = 0; cameraDistance = 4.5; raceHUD.mapRegion = RaceMapRegion.course; cameraAim = SCNVector3Zero;
        }
        view.clearInput(); if (pauseItem != null) { pauseItem.label = "Pause"; }
        if (pauseItem != null) { pauseItem.image = NSImage.systemSymbolName("pause.fill", accessibilityDescription: null); }
        updateCamera(snap: true); window.makeFirstResponder(view);
    }
    public void cycleCamera(object sender)
    {
        if (!(inSandbox && dirtIntro == null && !raceCameraLocked)) { return; }
        cameraMode = (cameraMode + 1) % 3; updateCamera(snap: cameraMode != 2);
        window.makeFirstResponder(view);
    }
    public void updateDepartureHUD()
    {
        if (!isDirtTrack) { return; }
        setRaceControlsHidden(racePhysics.escape.active);
    }
    public void setRaceControlsHidden(bool hidden)
    {
        frameRateHUD.isHidden = hidden;
        raceHUD.isHidden = hidden;
        hud.isHidden = true;
        if (window.toolbar != null && window.toolbar.isVisible == hidden)
        {
            // AppKit can grow a smaller window when restoring its toolbar. Keep chrome
            // transitions from changing either the window or the renderer's dimensions.
            var frame = window.frame;
            window.toolbar.isVisible = !hidden;
            if (window.frame != frame) { window.setFrame(frame, display: false); }
        }
    }
    /// Overlay layout can follow native chrome; the SceneKit viewport must not.
    public void installContentOverlay(NSView overlay)
    {
        if (!(window.contentLayoutGuide is NSLayoutGuide guide)) { return; }
        overlay.translatesAutoresizingMaskIntoConstraints = false;
        view.addSubview(overlay);
        NSLayoutConstraint.activate(new[] { NSLayoutConstraint.Attribute.leading, NSLayoutConstraint.Attribute.trailing, NSLayoutConstraint.Attribute.top, NSLayoutConstraint.Attribute.bottom }.Select(edge =>
            new NSLayoutConstraint(item: overlay, attribute: edge, relatedBy: NSLayoutConstraint.Relation.equal, toItem: guide, attribute2: edge, multiplier: 1, constant: 0)));
    }
    public void toggleHelp(object sender) { if (isDirtTrack) { raceHUD.helpVisible = !raceHUD.helpVisible; } else { hud.helpVisible = !hud.helpVisible; } }
    public void applicationWillResignActive(Notification notification)
    {
        active = false; view.clearInput(); raceAudio?.stop();
    }
    public void applicationDidBecomeActive(Notification notification)
    {
        active = true; lastTime = ProcessInfo.processInfo.systemUptime;
    }
    public void windowDidResignKey(Notification notification) { view.clearInput(); }
    public void windowDidChangeOcclusionState(Notification notification)
    {
        frameRateHUD.resetSamples();
        // NSView display links suspend while hidden. Never count that suspended
        // interval as race time when the window becomes visible again.
        lastTime = ProcessInfo.processInfo.systemUptime;
        if (!window.occlusionState.contains(NSWindow.OcclusionState.visible)) { view.clearInput(); raceAudio?.stop(); }
    }
    public void windowDidMiniaturize(Notification notification)
    {
        frameRateHUD.resetSamples();
        lastTime = ProcessInfo.processInfo.systemUptime; view.clearInput(); raceAudio?.stop();
    }
    public void windowDidDeminiaturize(Notification notification)
    {
        frameRateHUD.resetSamples();
        lastTime = ProcessInfo.processInfo.systemUptime;
    }
    public bool applicationShouldTerminateAfterLastWindowClosed(NSApplication sender) => true;
    public void applicationWillTerminate(Notification notification)
    {
        timer?.invalidate();
        // PORT: (frameDisplayLink as? CADisplayLink)?.invalidate(): no display link (see applicationDidFinishLaunching).
        raceAudio?.stop();
    }
    public string[] toolbarAllowedItemIdentifiers(NSToolbar toolbar) =>
        new[] { "menu", NSToolbarItem.Identifier.flexibleSpace, "camera", "pause", "reset", "help" };
    public string[] toolbarDefaultItemIdentifiers(NSToolbar toolbar) => toolbarAllowedItemIdentifiers(toolbar);
    public NSToolbarItem toolbar(NSToolbar toolbar, string itemForItemIdentifier, bool willBeInsertedIntoToolbar)
    {
        var id = itemForItemIdentifier;
        var item = new NSToolbarItem(itemIdentifier: id);
        (string, string, Action<object>) config;
        switch (id)
        {
            case "menu": config = ("Main Menu", "house", showMainMenu); break;
            case "camera": config = ("Camera", "video", cycleCamera); break;
            case "pause": config = ("Pause", "pause.fill", togglePause); pauseItem = item; break;
            case "reset": config = ("Reset", "arrow.counterclockwise", reset); break;
            case "help": config = ("Controls", "keyboard", toggleHelp); break;
            default: return null;
        }
        item.label = config.Item1; item.toolTip = config.Item1;
        item.image = NSImage.systemSymbolName(config.Item2, accessibilityDescription: config.Item1);
        item.target = this; item.action = config.Item3;
        return item;
    }
    public void toggleRaceSound(NSMenuItem sender)
    {
        raceSoundMuted = !raceSoundMuted; UserDefaults.standard.set(raceSoundMuted, "raceSoundMuted");
        sender.state = raceSoundMuted ? NSControl.StateValue.on : NSControl.StateValue.off;
        if (raceSoundMuted) { raceAudio?.stop(); }
    }
    public void makeMenu()
    {
        NSMenu bar = new NSMenu(), appMenu = new NSMenu(); var appItem = new NSMenuItem();
        appMenu.addItem(withTitle: "About Marvin Simulator", action: NSApp.orderFrontStandardAboutPanel, keyEquivalent: "");
        appMenu.addItem(NSMenuItem.separator());
        appMenu.addItem(withTitle: "Quit Marvin Simulator", action: NSApp.terminate, keyEquivalent: "q");
        appItem.submenu = appMenu; bar.addItem(appItem);
        NSMenuItem simItem = new NSMenuItem(); var simMenu = new NSMenu(title: "Simulation");
        foreach (var (title, selector, key) in new (string, Action<object>, string)[] { ("Main Menu", showMainMenu, "m"), ("Reset playground", reset, "r"), ("Pause / resume", togglePause, "p"), ("Change camera", cycleCamera, "1") })
        {
            var item = simMenu.addItem(withTitle: title, action: selector, keyEquivalent: key); item.target = this;
        }
        var sound = simMenu.addItem(withTitle: "Mute race sound", action: sender => toggleRaceSound((NSMenuItem)sender), keyEquivalent: "");
        sound.target = this; sound.state = raceSoundMuted ? NSControl.StateValue.on : NSControl.StateValue.off;
        simItem.submenu = simMenu; bar.addItem(simItem); NSApp.mainMenu = bar;
    }
}
