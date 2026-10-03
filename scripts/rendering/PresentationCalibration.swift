import AppKit
import Metal
import QuartzCore

// Known skipped frames calibrate Instruments exports against public Metal
// presentation timestamps. Run separately from simulator acceptance tests.
final class Calibration:NSObject,NSApplicationDelegate {
    var window:NSWindow!,layer:CAMetalLayer!,queue:MTLCommandQueue!,timer:Timer!
    var frame=0,start=0.0
    let lock=NSLock()
    var records:[[String:Any]]=[]
    func applicationDidFinishLaunching(_ note:Notification) {
        let view=NSView(frame:NSRect(x:0,y:0,width:960,height:540))
        layer=CAMetalLayer();layer.device=MTLCreateSystemDefaultDevice()!;layer.pixelFormat = .bgra8Unorm
        layer.frame=view.bounds;layer.drawableSize=CGSize(width:960,height:540)
        view.wantsLayer=true;view.layer=layer;queue=layer.device!.makeCommandQueue()!
        window=NSWindow(contentRect:view.bounds,styleMask:[.titled,.closable],backing:.buffered,defer:false)
        window.title="Presentation timing calibration";window.contentView=view;window.center();window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps:true);start=ProcessInfo.processInfo.systemUptime
        print("CALIBRATION_START \(start)");fflush(stdout)
        timer=Timer(timeInterval:1.0/60,target:self,selector:#selector(tick),userInfo:nil,repeats:true)
        RunLoop.main.add(timer,forMode:.common)
    }
    @objc func tick() {
        frame += 1
        let elapsed=ProcessInfo.processInfo.systemUptime-start
        if elapsed>=18 {
            timer.invalidate()
            DispatchQueue.main.asyncAfter(deadline:.now()+0.25) {
                self.lock.lock();let rows=self.records;self.lock.unlock()
                let data=try! JSONSerialization.data(withJSONObject:["startUptime":self.start,"records":rows],options:[.sortedKeys,.prettyPrinted])
                try! data.write(to:URL(fileURLWithPath:CommandLine.arguments[1]));NSApp.terminate(nil)
            };return
        }
        // Deliberate single skipped updates, after the trace has attached.
        if [360,480,600,720,840].contains(frame) {
            print("SKIP \(frame) \(elapsed)");fflush(stdout);return
        }
        guard let drawable=layer.nextDrawable(),let command=queue.makeCommandBuffer() else { return }
        let index=frame
        drawable.addPresentedHandler { value in
            self.lock.lock();self.records.append(["frame":index,"drawableID":value.drawableID,"presentedTime":value.presentedTime,"callbackUptime":ProcessInfo.processInfo.systemUptime]);self.lock.unlock()
        }
        let pass=MTLRenderPassDescriptor();pass.colorAttachments[0].texture=drawable.texture
        pass.colorAttachments[0].loadAction = .clear;pass.colorAttachments[0].storeAction = .store
        pass.colorAttachments[0].clearColor=MTLClearColor(red:Double(frame%120)/120,green:0.2,blue:0.4,alpha:1)
        command.makeRenderCommandEncoder(descriptor:pass)!.endEncoding();command.present(drawable);command.commit()
    }
}
let app=NSApplication.shared
let delegate=Calibration();app.delegate=delegate;app.setActivationPolicy(.regular);app.run()
