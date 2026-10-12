import AppKit
import SceneKit

extension AppController {
    /// Real asynchronous window transitions with the normal display-driven clock.
    /// Unlike fixed-step smoke fixtures this must leave smokeDirectory nil.
    func checkDisplayLinkLifecycle(at directory:URL) {
        var checks=["displayLinkSelected":frameDisplayLink != nil,"normalClock":smokeDirectory == nil]
        func later(_ seconds:Double,_ action:@escaping ()->Void) {
            DispatchQueue.main.asyncAfter(deadline:.now()+seconds,execute:action)
        }
        func finish() {
            do {
                try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
                try saveTownFrame("restored-window",at:directory)
                checks["fpsReadoutLive"]=(frameRateHUD.framesPerSecond ?? 0)>0 && !frameRateHUD.isHidden
                checks["fpsPassesMouseInput"]=frameRateHUD.hitTest(.zero)==nil
                // Composite the native view layers, since SCNView.snapshot omits
                // AppKit overlays. Preserve their actual layout and rendered text.
                let image=NSImage(size:view.bounds.size)
                image.lockFocus();view.snapshot().draw(in:view.bounds)
                for overlay in [raceHUD as NSView,frameRateHUD as NSView] where !overlay.isHidden {
                    NSGraphicsContext.saveGraphicsState()
                    let transform=NSAffineTransform()
                    transform.translateX(by:overlay.frame.minX,yBy:overlay.frame.minY)
                    if overlay.isFlipped {
                        transform.translateX(by:0,yBy:overlay.bounds.height)
                        transform.scaleX(by:1,yBy:-1)
                    }
                    transform.concat()
                    let context=NSGraphicsContext.current!
                    NSGraphicsContext.current=NSGraphicsContext(cgContext:context.cgContext,flipped:overlay.isFlipped)
                    overlay.draw(overlay.bounds)
                    NSGraphicsContext.current=context
                    NSGraphicsContext.restoreGraphicsState()
                }
                image.unlockFocus()
                if let tiff=image.tiffRepresentation,let bitmap=NSBitmapImageRep(data:tiff),let png=bitmap.representation(using:.png,properties:[:]) {
                    try png.write(to:directory.appendingPathComponent("fps-overlay.png"))
                }
                let report:[String:Any]=["checks":checks,"passed":checks.values.allSatisfy{$0}]
                try JSONSerialization.data(withJSONObject:report,options:[.prettyPrinted,.sortedKeys]).write(to:directory.appendingPathComponent("lifecycle.json"))
                print("Display-link lifecycle: \(checks)")
                exit(checks.values.allSatisfy{$0} ? 0:1)
            } catch { print(error);exit(1) }
        }
        later(0.5) {
            checks["menuVisible"] = !self.mainMenu.isHidden
            self.loadDirtTrack()
            func awaitLoaded(_ remaining:Int) {
                guard !self.isLoadingDirt else {
                    if remaining>0 { later(0.25) { awaitLoaded(remaining-1) } }
                    else { checks["loaded"]=false;finish() }
                    return
                }
                checks["loaded"]=self.inSandbox && self.isDirtTrack && !self.view.isHidden
                self.dirtIntro=nil;self.race.countDown(dt:3)
                let initial=self.race.elapsed
                later(0.75) {
                    checks["raceAdvances"]=self.race.elapsed-initial>0.3
                    self.togglePause(nil);let paused=self.race.elapsed
                    later(0.5) {
                        checks["pauseFreezesClock"]=self.race.elapsed==paused
                        checks["pauseStopsAudio"]=self.raceAudio?.active==false
                        self.togglePause(nil)
                        later(0.5) {
                            checks["resumeAdvances"]=self.race.elapsed-paused>0.2
                            self.window.miniaturize(nil)
                            later(0.5) {
                                let hidden=self.race.elapsed
                                checks["windowMiniaturized"]=self.window.isMiniaturized
                                checks["hiddenStopsAudio"]=self.raceAudio?.active==false
                                later(1.0) {
                                    checks["hiddenFreezesClock"]=self.race.elapsed==hidden
                                    let resumedAt=ProcessInfo.processInfo.systemUptime
                                    self.window.deminiaturize(nil)
                                    self.window.makeKeyAndOrderFront(nil);NSApp.activate(ignoringOtherApps:true)
                                    later(0.75) {
                                        let advance=self.race.elapsed-hidden
                                        let restoredWall=ProcessInfo.processInfo.systemUptime-resumedAt
                                        checks["restoreWithoutCatchup"]=advance>0.15 && advance<restoredWall+0.1
                                        print("Restore race delta: \(advance), visible wall interval: \(restoredWall)")
                                        checks["restoredAudio"]=self.raceAudio?.active==true
                                        finish()
                                    }
                                }
                            }
                        }
                    }
                }
            }
            later(0.25) { awaitLoaded(240) }
        }
    }
}
