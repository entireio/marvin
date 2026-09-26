import AppKit
import SceneKit

private final class PortraitView: SCNView {
    override var acceptsFirstResponder: Bool { false }
    override func mouseDown(with event: NSEvent) { window?.makeFirstResponder(superview) }
}

private final class MenuButton: NSButton {
    var onHover: (() -> Void)?
    override var acceptsFirstResponder: Bool { false }
    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.inVisibleRect, .mouseEnteredAndExited, .activeInKeyWindow], owner: self))
    }
    override func mouseEntered(with event: NSEvent) { onHover?() }
}

/// A native AppKit menu alongside a separate, live SceneKit portrait.
final class MainMenuView: NSView {
    let portrait: SCNView = PortraitView(), stage = SCNScene(), camera = SCNNode()
    let title = NSTextField(labelWithString: "Marvin"), subtitle = NSTextField(labelWithString: "A little room to explore.")
    let hint = NSTextField(labelWithString: "↑ ↓ to choose   ·   Return to select")
    private var buttons: [MenuButton] = []
    var onSandbox: (() -> Void)?
    var onDirtTrack: (() -> Void)?
    private var optionCount: Int { settings ? 3 : 4 }
    var settings = false
    var selection = 0
    var idleAnimation: Bool {
        get { !UserDefaults.standard.bool(forKey: "reduceMenuMotion") }
        set { UserDefaults.standard.set(!newValue, forKey: "reduceMenuMotion") }
    }
    var showGuide: Bool {
        get { !UserDefaults.standard.bool(forKey: "hideKeyboardGuide") }
        set { UserDefaults.standard.set(!newValue, forKey: "hideKeyboardGuide") }
    }
    private var clock = 0.0, nextLook = 1.8, nextBlink = 2.7, blinkStart = -10.0
    private var yaw = -0.30, pitch = 0.0, targetYaw = -0.30, targetPitch = 0.0
    override var acceptsFirstResponder: Bool { true }

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true; layer?.backgroundColor = color(0xf2f1e9).cgColor
        portrait.scene = stage; portrait.pointOfView = camera
        portrait.antialiasingMode = .multisampling4X
        portrait.rendersContinuously = true; portrait.preferredFramesPerSecond = 60
        stage.background.contents = color(0xf2f1e9)
        camera.camera = SCNCamera(); camera.camera?.fieldOfView = 36
        camera.position = SCNVector3(0, 1.05, 2.25)
        camera.look(at: SCNVector3(0, 0.40, 0), up: SCNVector3(0, 1, 0), localFront: SCNVector3(0, 0, -1))
        stage.rootNode.addChildNode(camera)
        let ambient = SCNNode(); ambient.light = SCNLight(); ambient.light?.type = .ambient
        ambient.light?.intensity = 650; stage.rootNode.addChildNode(ambient)
        let key = SCNNode(); key.light = SCNLight(); key.light?.type = .directional
        key.light?.intensity = 1100; key.eulerAngles = SCNVector3(-1.15, -0.6, 0)
        // Deferred shadows also shade the constant-color backdrop, preserving
        // its seamless match to the native menu while grounding the model.
        key.light?.castsShadow = true; key.light?.shadowMode = .deferred
        key.light?.shadowMapSize = CGSize(width: 2048, height: 2048)
        key.light?.orthographicScale = 2
        key.light?.shadowSampleCount = 32; key.light?.shadowRadius = 32
        key.light?.shadowBias = 0.001
        key.light?.shadowColor = NSColor.black.withAlphaComponent(0.18)
        stage.rootNode.addChildNode(key)
        let floor = SCNFloor(); floor.reflectivity = 0
        let surface = SCNMaterial(); surface.diffuse.contents = color(0xf2f1e9); surface.lightingModel = .constant
        floor.materials = [surface]; stage.rootNode.addChildNode(SCNNode(geometry: floor))
        addSubview(portrait)
        title.font = .systemFont(ofSize: 58, weight: .bold)
        subtitle.font = .systemFont(ofSize: 18, weight: .regular)
        hint.font = .systemFont(ofSize: 12)
        for label in [title, subtitle, hint] { label.textColor = color(0x304e44); addSubview(label) }
        for index in 0..<4 {
            let button = MenuButton(title: "", target: self, action: #selector(activateButton(_:)))
            button.tag = index; button.isBordered = false; button.wantsLayer = true
            button.layer?.cornerRadius = 14
            button.onHover = { [weak self] in self?.selection = index; self?.refresh() }
            addSubview(button); buttons.append(button)
        }
        refresh()
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    override func layout() {
        super.layout()
        let split = bounds.width * 0.55, width = min(360, bounds.width - split - 50)
        portrait.frame = NSRect(x: 0, y: 0, width: split, height: bounds.height)
        let top = bounds.height / 2 + 205
        title.frame = NSRect(x: split, y: top - 72, width: width, height: 76)
        subtitle.frame = NSRect(x: split + 3, y: top - 108, width: width, height: 30)
        for (i, button) in buttons.enumerated() {
            button.frame = NSRect(x: split, y: top - 198 - CGFloat(i)*72, width: width, height: 58)
        }
        hint.frame = NSRect(x: split + 3, y: top - (settings ? 386 : 458), width: width, height: 24)
    }
    func refresh() {
        title.stringValue = settings ? "Settings" : "Marvin"
        subtitle.stringValue = settings ? "Make yourself at home." : "A little room to explore."
        let names = settings ? ["Idle animation: \(idleAnimation ? "On" : "Off")", "Keyboard guide: \(showGuide ? "On" : "Off")", "Back"] : ["Sandbox", "Dirt Track", "Settings", "Quit"]
        for (i, button) in buttons.enumerated() {
            button.isHidden = i >= names.count
            guard i < names.count else { continue }
            button.title = names[i]
            button.attributedTitle = NSAttributedString(string: names[i], attributes: [.font: NSFont.systemFont(ofSize: 21, weight: .medium), .foregroundColor: i == selection ? NSColor.white : color(0x304e44)])
            button.layer?.backgroundColor = (i == selection ? color(0x304e44) : color(0xe5e9df)).cgColor
            button.setAccessibilityLabel(names[i])
        }
    }
    @objc private func activateButton(_ sender: NSButton) { selection = sender.tag; activate() }
    func activate() {
        if settings {
            switch selection {
            case 0: idleAnimation.toggle()
            case 1: showGuide.toggle()
            default: settings = false; selection = 2
            }
        } else {
            switch selection {
            case 0: onSandbox?()
            case 1: onDirtTrack?()
            case 2: settings = true; selection = 0
            default: NSApp.terminate(nil)
            }
        }
        needsLayout = true; refresh(); if !isHidden { window?.makeFirstResponder(self) }
    }
    override func keyDown(with event: NSEvent) {
        guard !event.modifierFlags.contains(.command) else { super.keyDown(with: event); return }
        switch event.keyCode {
        case 125, 124: selection = (selection + 1) % optionCount; refresh()
        case 126, 123: selection = (selection + optionCount - 1) % optionCount; refresh()
        case 36, 76, 49: if !event.isARepeat { activate() }
        case 53: settings = false; selection = 0; refresh()
        default: break
        }
    }
    override func mouseDown(with event: NSEvent) { window?.makeFirstResponder(self) }
    func animate(_ robot: Robot, dt: Double) {
        clock += dt
        if clock >= nextLook {
            targetYaw = Double.random(in: -0.65...0.25)
            targetPitch = Double.random(in: -0.07...0.12)
            nextLook = clock + Double.random(in: 2.0...5.5)
        }
        if clock >= nextBlink { blinkStart = clock; nextBlink = clock + Double.random(in: 2.5...6.0) }
        let blend = 1 - exp(-dt * 3)
        yaw += (targetYaw - yaw) * blend; pitch += (targetPitch - pitch) * blend
        robot.root.position = SCNVector3Zero; robot.root.eulerAngles = SCNVector3(0,0.35,0)
        robot.yawNode.eulerAngles.y = CGFloat(idleAnimation ? yaw : -0.30)
        robot.pitchNode.eulerAngles.x = CGFloat(idleAnimation ? -pitch : 0)
        let blink = max(0, 1 - abs((clock - blinkStart) / 0.11 - 1))
        for eye in robot.eyes { eye.scale.y = idleAnimation ? CGFloat(1 - blink * 0.92) : 1 }
    }
}
