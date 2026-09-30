import AppKit

private final class LoadingProgressBar:NSView {
    var value=0.0 { didSet { needsDisplay=true;setAccessibilityValue(value) } }
    override func draw(_ dirtyRect:NSRect) {
        NSColor(calibratedWhite:0.22,alpha:1).setFill()
        NSBezierPath(roundedRect:bounds,xRadius:4,yRadius:4).fill()
        NSColor(calibratedRed:0.88,green:0.69,blue:0.41,alpha:1).setFill()
        let fill=NSRect(x:0,y:0,width:bounds.width*value,height:bounds.height)
        NSBezierPath(roundedRect:fill,xRadius:4,yRadius:4).fill()
    }
}

final class LevelLoadingView:NSView {
    private let title=NSTextField(labelWithString:"MOS ASTER")
    private let subtitle=NSTextField(labelWithString:"Preparing the dirt race")
    private let stage=NSTextField(labelWithString:"")
    private let percentage=NSTextField(labelWithString:"0%")
    private let bar=LoadingProgressBar()
    private(set) var history:[Double]=[]
    override var acceptsFirstResponder:Bool { true }
    override func keyDown(with event:NSEvent) {}
    override init(frame:NSRect) {
        super.init(frame:frame);wantsLayer=true;layer?.backgroundColor=NSColor(calibratedRed:0.085,green:0.105,blue:0.12,alpha:1).cgColor
        for label in [title,subtitle,stage,percentage] { addSubview(label);label.alignment = .center;label.textColor=NSColor(calibratedWhite:0.85,alpha:1) }
        title.font = .systemFont(ofSize:34,weight:.bold);title.textColor=NSColor(calibratedRed:0.88,green:0.69,blue:0.41,alpha:1)
        subtitle.font = .systemFont(ofSize:16,weight:.medium);stage.font = .systemFont(ofSize:13);percentage.font = .monospacedDigitSystemFont(ofSize:13,weight:.medium)
        bar.setAccessibilityRole(.progressIndicator);bar.setAccessibilityLabel("Race loading progress");addSubview(bar)
    }
    required init?(coder:NSCoder) { fatalError() }
    override func layout() {
        super.layout();let width=min(440,bounds.width-64),x=(bounds.width-width)/2,y=bounds.height/2
        title.frame=NSRect(x:x,y:y+54,width:width,height:44)
        subtitle.frame=NSRect(x:x,y:y+22,width:width,height:24)
        bar.frame=NSRect(x:x,y:y-17,width:width,height:8)
        stage.frame=NSRect(x:x,y:y-47,width:width,height:22)
        percentage.frame=NSRect(x:x,y:y-73,width:width,height:20)
    }
    func begin() { history=[];update(0,"Preparing your race") }
    func update(_ progress:Double,_ text:String) {
        let value=max(history.last ?? 0,min(1,progress));history.append(value)
        bar.value=value;percentage.stringValue="\(Int(value*100))%";stage.stringValue=text
    }
}

extension AppController {
    func loadDirtTrack() {
        guard !isLoadingDirt,let content=window.contentView else { return }
        isLoadingDirt=true;loadingHeartbeats=0;view.clearInput()
        loadingView.frame=content.bounds;loadingView.autoresizingMask=[.width,.height]
        content.addSubview(loadingView);loadingView.begin();window.makeFirstResponder(loadingView)
        window.toolbar?.isVisible=false;mainMenu.portrait.rendersContinuously=false
        if cachedDirtWorld != nil {
            DispatchQueue.main.async { [weak self] in self?.startDirtTrack() };return
        }
        DispatchQueue.global(qos:.userInitiated).async { [weak self] in
            let built=DirtWorld(progress:{ fraction,label in
                DispatchQueue.main.async { [weak self] in
                    self?.loadingView.update(fraction,label)
                    self?.captureLoadingCheckIfNeeded(fraction)
                }
            })
            DispatchQueue.main.async { [weak self] in self?.cachedDirtWorld=built;self?.startDirtTrack() }
        }
    }
    func failDirtLoading() {
        isLoadingDirt=false;loadingView.removeFromSuperview();showMainMenu(nil)
        let alert=NSAlert();alert.messageText="The race could not finish loading.";alert.informativeText="Please try again.";alert.beginSheetModal(for:window)
    }
    func captureLoadingCheckIfNeeded(_ progress:Double) {
        guard CommandLine.arguments.contains("--loading-smoke-test"),let directory=smokeDirectory,
              progress>=0.49 && progress<0.51 else { return }
        loadingView.layoutSubtreeIfNeeded();loadingView.displayIfNeeded()
        if let bitmap=loadingView.bitmapImageRepForCachingDisplay(in:loadingView.bounds) {
            loadingView.cacheDisplay(in:loadingView.bounds,to:bitmap)
            try? FileManager.default.createDirectory(atPath:directory,withIntermediateDirectories:true)
            try? bitmap.representation(using:.png,properties:[:])?.write(to:URL(fileURLWithPath:directory).appendingPathComponent("loading.png"))
        }
    }
    func finishLoadingCheckIfNeeded() {
        guard CommandLine.arguments.contains("--loading-smoke-test") else { return }
        let h=loadingView.history
        let passed=h.first==0 && h.last==1 && zip(h,h.dropFirst()).allSatisfy{$0 <= $1} && h.count>20 && loadingHeartbeats>5 && !view.isHidden && inSandbox && !isLoadingDirt
        print("Loading: \(passed ? "PASS":"FAIL"), \(h.count) progress reports, \(loadingHeartbeats) responsive ticks")
        exit(passed ? 0:1)
    }
}
