import AppKit

/// Receipt-time visibility/lock events, enabled only for instrumented benchmarks.
final class BenchmarkSessionState {
    private var observers: [NSObjectProtocol] = []
    private var distributed: [NSObjectProtocol] = []
    private var events: [[String:Any]] = []
    private weak var window: NSWindow?
    func start(window: NSWindow) {
        guard ProcessInfo.processInfo.environment["MARVIN_BENCHMARK_VISIBILITY"] == "1" else { return }
        self.window = window
        for name in [NSApplication.didBecomeActiveNotification, NSApplication.didResignActiveNotification,
                     NSWindow.didChangeOcclusionStateNotification] {
            observers.append(NotificationCenter.default.addObserver(forName:name,object:nil,queue:.main) { [weak self] note in
                if let other = note.object as? NSWindow, other !== self?.window { return }
                self?.record(name.rawValue)
            })
        }
        for name in ["com.apple.screenIsLocked", "com.apple.screenIsUnlocked"] {
            distributed.append(DistributedNotificationCenter.default().addObserver(
                forName:NSNotification.Name(name),object:nil,queue:.main) { [weak self] _ in self?.record(name) })
        }
        record("observerStart")
    }
    private func record(_ name: String) {
        events.append(["event":name,"receiptUptime":ProcessInfo.processInfo.systemUptime,
            "appActive":NSApp.isActive,"windowOcclusionVisible":window?.occlusionState.contains(.visible) ?? false])
    }
    func report() -> [String:Any] {
        record("observerEnd")
        return ["events":events,"method":"Notification receipt uptime; delivered on main queue. Combine with one-second samples and independent UI observation."]
    }
}
