import AppKit

final class PetConnectionWindow: NSWindowController {
    private let connection: PetConnection
    private let server=NSTextField(), code=NSSecureTextField(), status=NSTextField(wrappingLabelWithString:"Not connected")
    private let pair=NSButton(title:"Pair simulator",target:nil,action:nil)
    private let reconnect=NSButton(title:"Reconnect",target:nil,action:nil)
    init(connection: PetConnection) {
        self.connection=connection
        let panel=NSPanel(contentRect:NSRect(x:0,y:0,width:560,height:340),styleMask:[.titled,.closable],backing:.buffered,defer:false)
        panel.title="Connect to Marvin";panel.isReleasedWhenClosed=false
        super.init(window:panel)
        let stack=NSStackView();stack.orientation = .vertical;stack.alignment = .leading;stack.spacing=12
        let intro=NSTextField(wrappingLabelWithString:"In the Marvin web app, choose Use simulator and create a pairing code. Your simulator will use the same remote controls and settings as a Desktop Pet.")
        intro.font = .systemFont(ofSize:13)
        server.placeholderString="https://marvin.example or http://127.0.0.1:5173";server.stringValue=connection.binding?.serverUrl ?? "http://127.0.0.1:5173";server.setAccessibilityLabel("Marvin server")
        code.placeholderString="One-time simulator pairing code";code.setAccessibilityLabel("Simulator pairing code")
        pair.target=self;pair.action=#selector(pairNow);reconnect.target=self;reconnect.action=#selector(reconnectNow)
        let disconnect=NSButton(title:"Disconnect",target:self,action:#selector(disconnectNow))
        let actions=NSStackView(views:[pair,reconnect,disconnect]);actions.spacing=10
        status.font = .systemFont(ofSize:13,weight:.medium);status.setAccessibilityLabel("Simulator connection status")
        for item in [intro,NSTextField(labelWithString:"Marvin server"),server,NSTextField(labelWithString:"Pairing code"),code,actions,status] { stack.addArrangedSubview(item) }
        panel.contentView!.addSubview(stack);stack.translatesAutoresizingMaskIntoConstraints=false
        NSLayoutConstraint.activate([stack.leadingAnchor.constraint(equalTo:panel.contentView!.leadingAnchor,constant:24),stack.trailingAnchor.constraint(equalTo:panel.contentView!.trailingAnchor,constant:-24),stack.topAnchor.constraint(equalTo:panel.contentView!.topAnchor,constant:24),server.widthAnchor.constraint(equalTo:stack.widthAnchor),code.widthAnchor.constraint(equalTo:stack.widthAnchor)])
        refresh();panel.center()
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    func refresh() { status.stringValue=connection.credentialNotice ?? connection.status;pair.isEnabled=connection.binding==nil && !connection.pairing;code.isEnabled=connection.binding==nil;server.isEnabled=connection.binding==nil;reconnect.isEnabled=connection.binding != nil && !connection.online }
    @objc private func pairNow() { let value=code.stringValue;code.stringValue="";connection.pair(server:server.stringValue,code:value) }
    @objc private func reconnectNow() { connection.connect() }
    @objc private func disconnectNow() { connection.disconnect() }
}
