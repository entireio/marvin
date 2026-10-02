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
                                    self.window.deminiaturize(nil)
                                    self.window.makeKeyAndOrderFront(nil);NSApp.activate(ignoringOtherApps:true)
                                    later(0.5) {
                                        let advance=self.race.elapsed-hidden
                                        checks["restoreWithoutCatchup"]=advance>0.15 && advance<0.8
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
