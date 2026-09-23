import AppKit

@main
@MainActor
final class MarvinEntireConnectorApp: NSObject, NSApplicationDelegate {
    private var item: NSStatusItem!
    private var process: Process?
    private var lastFailure: String?
    private var status = "Not connected" { didSet { rebuildMenu() } }

    static func main() {
        let app = NSApplication.shared
        let delegate = MarvinEntireConnectorApp()
        app.delegate = delegate
        app.setActivationPolicy(.accessory)
        app.run()
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        installEditMenu()
        item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let button = item.button {
            button.image = connectorIcon()
            button.imagePosition = .imageOnly
            button.imageScaling = .scaleProportionallyDown
        }
        rebuildMenu()
        if UserDefaults.standard.bool(forKey: "autoConnect") { start(pairCode: nil) }
    }

    func applicationWillTerminate(_ notification: Notification) {
        process?.terminate()
    }

    private func installEditMenu() {
        let mainMenu = NSMenu()
        let editMenuItem = NSMenuItem()
        let editMenu = NSMenu(title: "Edit")
        editMenu.addItem(withTitle: "Undo", action: Selector(("undo:")), keyEquivalent: "z")
        let redo = editMenu.addItem(withTitle: "Redo", action: Selector(("redo:")), keyEquivalent: "z")
        redo.keyEquivalentModifierMask = [.command, .shift]
        editMenu.addItem(.separator())
        editMenu.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        editMenu.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        editMenu.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        editMenu.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editMenuItem.submenu = editMenu
        mainMenu.addItem(editMenuItem)
        NSApp.mainMenu = mainMenu
    }

    private func connectorIcon() -> NSImage? {
        if let url = Bundle.main.url(forResource: "entire-symbol-dark-icon", withExtension: "svg"),
           let image = NSImage(contentsOf: url) {
            image.size = NSSize(width: 18, height: 18)
            image.isTemplate = true
            image.accessibilityDescription = "Marvin Entire Connector"
            return image
        }
        return NSImage(systemSymbolName: "link.circle", accessibilityDescription: "Marvin Entire Connector")
    }

    private func rebuildMenu() {
        guard item != nil else { return }
        let menu = NSMenu()
        let state = NSMenuItem(title: status, action: nil, keyEquivalent: "")
        state.isEnabled = false
        menu.addItem(state)
        menu.addItem(.separator())
        let connect = NSMenuItem(title: process == nil ? "Connect…" : "Reconnect…", action: #selector(showConnect), keyEquivalent: "")
        connect.target = self
        menu.addItem(connect)
        if process != nil {
            let stop = NSMenuItem(title: "Pause connector", action: #selector(stopConnector), keyEquivalent: "")
            stop.target = self
            menu.addItem(stop)
        }
        let auto = NSMenuItem(title: "Connect at launch", action: #selector(toggleAutoConnect), keyEquivalent: "")
        auto.target = self
        auto.state = UserDefaults.standard.bool(forKey: "autoConnect") ? .on : .off
        menu.addItem(auto)
        menu.addItem(.separator())
        let quit = NSMenuItem(title: "Quit", action: #selector(quit), keyEquivalent: "q")
        quit.target = self
        menu.addItem(quit)
        item.menu = menu
    }

    @objc private func showConnect() {
        let alert = NSAlert()
        alert.messageText = "Connect Marvin to Entire"
        alert.informativeText = "In Marvin, open Settings → Connections and choose Set up local connector. Paste the Marvin URL and one-time code shown there. Entire credentials stay in your local CLI."
        alert.addButton(withTitle: "Connect")
        alert.addButton(withTitle: "Cancel")
        let stack = NSStackView()
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 6
        stack.frame = NSRect(x: 0, y: 0, width: 420, height: 108)
        let serverLabel = NSTextField(labelWithString: "Marvin URL")
        let server = NSTextField(string: displayServerURL(UserDefaults.standard.string(forKey: "server")))
        server.placeholderString = "https://marvin.example/"
        server.frame.size.width = 420
        let codeLabel = NSTextField(labelWithString: "One-time pairing code")
        let code = NSTextField(string: "")
        code.placeholderString = "One-time pairing code"
        code.frame.size.width = 420
        stack.addArrangedSubview(serverLabel)
        stack.addArrangedSubview(server)
        stack.addArrangedSubview(codeLabel)
        stack.addArrangedSubview(code)
        alert.accessoryView = stack
        NSApp.activate(ignoringOtherApps: true)
        while alert.runModal() == .alertFirstButtonReturn {
            do {
                let endpoint = try connectorEndpoint(server.stringValue)
                let pair = code.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
                UserDefaults.standard.set(endpoint, forKey: "server")
                start(pairCode: pair.isEmpty ? nil : pair)
                return
            } catch {
                alert.alertStyle = .warning
                alert.informativeText = error.localizedDescription
            }
        }
    }

    private func connectorEndpoint(_ raw: String) throws -> String {
        var value = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        if !value.contains("://") { value = "https://" + value }
        guard var components = URLComponents(string: value), let host = components.host,
              components.user == nil, components.password == nil,
              components.query == nil, components.fragment == nil else {
            throw ConnectorInputError.invalidURL
        }
        let local = host == "localhost" || host == "127.0.0.1" || host == "::1"
        switch components.scheme?.lowercased() {
        case "https", "wss": components.scheme = "wss"
        case "http" where local, "ws" where local: components.scheme = "ws"
        default: throw ConnectorInputError.secureURLRequired
        }
        guard components.path.isEmpty || components.path == "/" || components.path == "/api/entire/connector/socket" else {
            throw ConnectorInputError.originRequired
        }
        components.path = "/api/entire/connector/socket"
        guard let endpoint = components.url?.absoluteString else { throw ConnectorInputError.invalidURL }
        return endpoint
    }

    private func displayServerURL(_ raw: String?) -> String {
        guard let raw, var components = URLComponents(string: raw), components.host != nil else { return "" }
        if components.scheme == "wss" { components.scheme = "https" }
        if components.scheme == "ws" { components.scheme = "http" }
        components.path = "/"
        components.query = nil
        components.fragment = nil
        return components.url?.absoluteString ?? raw
    }

    private func start(pairCode: String?) {
        stopConnector()
        guard let server = UserDefaults.standard.string(forKey: "server"), !server.isEmpty else {
            status = "Open Connect…"
            return
        }
        let core = ProcessInfo.processInfo.environment["MARVIN_CONNECTOR_CORE"].map(URL.init(fileURLWithPath:))
            ?? Bundle.main.url(forResource: "marvin-entire-connector", withExtension: nil)
            ?? Bundle.main.executableURL?.deletingLastPathComponent().appendingPathComponent("marvin-entire-connector")
        guard let core, FileManager.default.isExecutableFile(atPath: core.path) else {
            status = "Connector core is missing"
            return
        }
        let task = Process()
        task.executableURL = core
        task.arguments = ["--server", server] + (pairCode == nil ? [] : ["--pair-stdin"])
        let input = pairCode.map { _ in Pipe() }
        task.standardInput = input
        var environment = ProcessInfo.processInfo.environment
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        let searchPaths = [home + "/.local/bin", "/opt/homebrew/bin", "/usr/local/bin"]
        let existingPaths = (environment["PATH"] ?? "").split(separator: ":").map(String.init)
        environment["PATH"] = Array(NSOrderedSet(array: searchPaths + existingPaths)).compactMap { $0 as? String }.joined(separator: ":")
        task.environment = environment
        let output = Pipe()
        task.standardOutput = output
        task.standardError = output
        output.fileHandleForReading.readabilityHandler = { handle in
            let text = String(decoding: handle.availableData, as: UTF8.self)
            Task { @MainActor in
                guard let connector = NSApp.delegate as? MarvinEntireConnectorApp else { return }
                connector.consumeOutput(text)
            }
        }
        task.terminationHandler = { _ in Task { @MainActor in
            guard let connector = NSApp.delegate as? MarvinEntireConnectorApp else { return }
            guard connector.process === task else { return }
            connector.process = nil
            if connector.lastFailure == nil { connector.status = "Not connected" }
        } }
        do {
            lastFailure = nil
            try task.run()
            if let pairCode, let input {
                input.fileHandleForWriting.write(Data((pairCode + "\n").utf8))
                input.fileHandleForWriting.closeFile()
            }
            process = task
            status = "Connecting…"
        } catch {
            status = "Could not start connector"
        }
    }

    private func consumeOutput(_ text: String) {
        if text.contains("is online") {
            lastFailure = nil
            status = "Connected · read-only"
        } else if text.contains("Paired with Marvin") {
            status = "Pairing accepted…"
        } else if text.contains("Entire CLI not found") {
            lastFailure = "Entire CLI not found"
            status = "Connection failed · Entire CLI not found"
        } else if text.localizedCaseInsensitiveContains("pairing expired") || text.localizedCaseInsensitiveContains("pairing_invalid") {
            lastFailure = "Pairing code expired"
            status = "Pairing code expired · try again"
        } else if text.contains("no saved connector credential") {
            lastFailure = "Pairing code required"
            status = "Pairing code required"
        } else if text.contains("Connector authorization failed") {
            lastFailure = "Authorization was revoked"
            status = "Authorization revoked · connect again"
        } else if text.contains("failed to WebSocket dial") {
            lastFailure = "Cannot reach Marvin"
            status = "Cannot reach Marvin · retrying"
        } else if text.contains("unknown flag") || text.contains("unsupported repository data") {
            lastFailure = "Entire CLI compatibility error"
            status = "Update the Marvin connector"
        } else if text.contains("Entire request failed:") {
            lastFailure = "Entire CLI read failed"
            status = "Entire CLI read failed"
        } else if text.contains("Connector offline:") || text.contains("Error:") {
            lastFailure = "Connection failed"
            status = "Could not connect · try again"
        }
    }

    @objc private func stopConnector() {
        process?.terminate()
        process = nil
        status = "Paused"
    }

    @objc private func toggleAutoConnect() {
        UserDefaults.standard.set(!UserDefaults.standard.bool(forKey: "autoConnect"), forKey: "autoConnect")
        rebuildMenu()
    }

    @objc private func quit() { stopConnector(); NSApp.terminate(nil) }
}

private enum ConnectorInputError: LocalizedError {
    case invalidURL, secureURLRequired, originRequired
    var errorDescription: String? {
        switch self {
        case .invalidURL: return "Enter the Marvin URL shown in Settings → Connections."
        case .secureURLRequired: return "The Marvin URL must begin with https://."
        case .originRequired: return "Paste only the Marvin site address, without an additional path."
        }
    }
}
