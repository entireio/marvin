import AppKit

let app = NSApplication.shared
app.setActivationPolicy(.regular)
let controller = AppController()
app.delegate = controller
app.run()
