import AppKit
import SceneKit

/// Render-callback rate, independent of the simulation's update clock.
final class FrameRateHUD: NSView, SCNSceneRendererDelegate {
    private let sampleLock=NSLock()
    private var sampleStart:Double?
    private var frames=0
    private(set) var framesPerSecond:Double?
    var onSample:((Double,Double?)->Void)?
    var displayedText:String { framesPerSecond.map { String(format:"%.1f FPS",$0) } ?? "— FPS" }
    override func hitTest(_ point:NSPoint)->NSView? { nil }

    func resetSamples() {
        sampleLock.lock();sampleStart=nil;frames=0;sampleLock.unlock()
        framesPerSecond=nil;needsDisplay=true
    }

    func renderer(_ renderer:SCNSceneRenderer,didRenderScene scene:SCNScene,atTime time:TimeInterval) {
        let now=ProcessInfo.processInfo.systemUptime
        sampleLock.lock()
        guard let start=sampleStart else { sampleStart=now;sampleLock.unlock();return }
        frames += 1
        let elapsed=now-start
        guard elapsed>=0.5 else { sampleLock.unlock();return }
        let fps=elapsed<2 ? Double(frames)/elapsed:nil
        sampleStart=now;frames=0;sampleLock.unlock()
        DispatchQueue.main.async { [weak self] in
            self?.framesPerSecond=fps;self?.needsDisplay=true
            self?.onSample?(now,fps)
        }
    }

    override func draw(_ dirtyRect:NSRect) {
        let label=displayedText
        let attributes:[NSAttributedString.Key:Any]=[
            .font:NSFont.monospacedDigitSystemFont(ofSize:13,weight:.semibold),
            .foregroundColor:NSColor(calibratedWhite:0.97,alpha:1)]
        let size=(label as NSString).size(withAttributes:attributes)
        let panel=NSRect(x:bounds.width-132,y:22,width:110,height:32)
        NSColor(calibratedRed:0.15,green:0.19,blue:0.16,alpha:0.90).setFill()
        NSBezierPath(roundedRect:panel,xRadius:9,yRadius:9).fill()
        (label as NSString).draw(at:NSPoint(x:panel.midX-size.width/2,y:panel.midY-size.height/2),withAttributes:attributes)
    }
}
