// Window chrome reference for the Godot port's title bar (scripts/SceneKit/NSWindow.cs, NSToolbar.cs).
//
// Builds App.swift's window (1280 x 820 content, titled/closable/miniaturizable/resizable/fullSizeContentView) with
// the game's toolbar items (.iconAndLabel), puts BACKGROUND.png behind it as the content view, and writes captures of
// the window with the toolbar hidden (as on the main menu), visible (sandbox and race) and with the Pause item
// relabelled "Resume", plus the content layout rect of each state.
//
// The macOS game links against the macOS 13 SDK (LC_BUILD_VERSION sdk 13.0), which keeps AppKit's pre-macOS 26 window
// style; link this probe the same way or AppKit draws the newer, taller title bar (32/66 points):
//
//   swiftc -O -target arm64-apple-macos13.0 WindowChrome.swift -o /tmp/window-chrome \
//       -Xlinker -platform_version -Xlinker macos -Xlinker 13.0 -Xlinker 13.0
//   /tmp/window-chrome OUTDIR BACKGROUND.png
//
// Measured on macOS 27 (M2, 1x display): title bar 28 points, 52 with the toolbar; opaque (236, 235, 234) bar;
// title centred at baseline 18 (13 pt bold) or at x 92, baseline 31 with the toolbar; item labels baseline 46,
// icons centred at y 21, item centres 1030.5 / 1095 / 1145.5 / 1191 / 1242 for Main Menu, Camera, Pause, Reset,
// Controls; traffic lights centred at (16, 14) and (26, 26). Window captures use CGWindowListCreateImage on the
// probe's own window (no screen-recording permission needed); a window launched from a terminal stays inactive,
// so the captures show the inactive colours.
import AppKit
import CoreGraphics

final class Delegate: NSObject, NSApplicationDelegate, NSToolbarDelegate {
    var window: NSWindow!
    var pauseItem: NSToolbarItem?
    let out: String
    let background: NSImage?
    init(out: String, background: NSImage?) { self.out = out; self.background = background }

    func applicationDidFinishLaunching(_ n: Notification) {
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1280, height: 820),
            styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView], backing: .buffered, defer: false)
        window.title = "Marvin · Dirt Track"
        window.minSize = NSSize(width: 900, height: 640)
        window.backgroundColor = .windowBackgroundColor
        let toolbar = NSToolbar(identifier: "SimulatorToolbar")
        toolbar.delegate = self; toolbar.displayMode = .iconAndLabel
        window.toolbar = toolbar
        let content = NSImageView(frame: NSRect(x: 0, y: 0, width: 1280, height: 820))
        content.imageScaling = .scaleAxesIndependently
        content.image = background
        content.autoresizingMask = [.width, .height]
        window.contentView = content
        window.toolbar?.isVisible = false
        window.center(); window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { self.capture("titlebar") }
    }

    func capture(_ name: String) {
        let w = window!
        print("\(name): frame \(w.frame) contentLayoutRect \(w.contentLayoutRect) title bar \(w.frame.height - w.contentLayoutRect.maxY)")
        // CGWindowListCreateImage is unavailable to Swift on the macOS 15+ SDK; call it through dlsym.
        typealias CreateImage = @convention(c) (CGRect, UInt32, UInt32, UInt32) -> Unmanaged<CGImage>?
        let create = unsafeBitCast(dlsym(dlopen(nil, RTLD_NOW), "CGWindowListCreateImage"), to: CreateImage.self)
        let optionIncludingWindow: UInt32 = 1 << 3, boundsIgnoreFraming: UInt32 = 1 << 0, bestResolution: UInt32 = 1 << 3
        if let image = create(.null, optionIncludingWindow, UInt32(w.windowNumber), boundsIgnoreFraming | bestResolution)?.takeRetainedValue() {
            try? NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:])?
                .write(to: URL(fileURLWithPath: out).appendingPathComponent("window-\(name).png"))
        } else { print("capture failed") }
        switch name {
        case "titlebar":
            w.toolbar?.isVisible = true
            DispatchQueue.main.asyncAfter(deadline: .now() + 1) { self.capture("toolbar") }
        case "toolbar":
            pauseItem?.label = "Resume"; pauseItem?.image = NSImage(systemSymbolName: "play.fill", accessibilityDescription: nil)
            DispatchQueue.main.asyncAfter(deadline: .now() + 1) { self.capture("toolbar-resume") }
        default: NSApp.terminate(nil)
        }
    }

    func toolbarAllowedItemIdentifiers(_ toolbar: NSToolbar) -> [NSToolbarItem.Identifier] {
        [.init("menu"), .flexibleSpace, .init("camera"), .init("pause"), .init("reset"), .init("help")]
    }
    func toolbarDefaultItemIdentifiers(_ toolbar: NSToolbar) -> [NSToolbarItem.Identifier] { toolbarAllowedItemIdentifiers(toolbar) }
    func toolbar(_ toolbar: NSToolbar, itemForItemIdentifier id: NSToolbarItem.Identifier, willBeInsertedIntoToolbar flag: Bool) -> NSToolbarItem? {
        let item = NSToolbarItem(itemIdentifier: id)
        let config: (String, String)
        switch id.rawValue {
        case "menu": config = ("Main Menu", "house")
        case "camera": config = ("Camera", "video")
        case "pause": config = ("Pause", "pause.fill"); pauseItem = item
        case "reset": config = ("Reset", "arrow.counterclockwise")
        case "help": config = ("Controls", "keyboard")
        default: return nil
        }
        item.label = config.0; item.toolTip = config.0
        item.image = NSImage(systemSymbolName: config.1, accessibilityDescription: config.0)
        item.target = self; item.action = #selector(noop)
        return item
    }
    @objc func noop() {}
}

let arguments = CommandLine.arguments
guard arguments.count > 1 else { print("usage: window-chrome OUTDIR [BACKGROUND.png]"); exit(2) }
try? FileManager.default.createDirectory(atPath: arguments[1], withIntermediateDirectories: true)
let app = NSApplication.shared
app.setActivationPolicy(.regular)
let delegate = Delegate(out: arguments[1], background: arguments.count > 2 ? NSImage(contentsOfFile: arguments[2]) : nil)
app.delegate = delegate
app.run()
