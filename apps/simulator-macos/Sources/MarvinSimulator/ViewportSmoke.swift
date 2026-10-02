import AppKit
import SceneKit
import SimulationCore

extension AppController {
    func checkDepartureViewport(at directory:URL)->Bool {
        do {
            try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
            startDirtTrack();dirtIntro=nil;race.countDown(dt:3)
            for i in 1...601 {
                let p=DirtCourse.point(Double(i)*Double.pi/100)
                race.advance(x:p.x,z:p.z,dt:0.1)
            }
            updateCamera(snap:true)
            let landmarks=[SCNVector3Zero,SCNVector3(-12,0,-10),SCNVector3(12,0,10)]
            func settle() { window.contentView?.layoutSubtreeIfNeeded();window.displayIfNeeded() }
            func capture(_ name:String) throws {
                let image=view.snapshot()
                if let tiff=image.tiffRepresentation,let png=NSBitmapImageRep(data:tiff)?.representation(using:.png,properties:[:]) {
                    try png.write(to:directory.appendingPathComponent(name+".png"))
                }
            }
            var results=[[String:Any]](),passed=race.finished
            for (index,size) in [NSSize(width:1280,height:820),NSSize(width:900,height:640),NSSize(width:1600,height:900)].enumerated() {
                window.setContentSize(size);setRaceControlsHidden(false);settle()
                let before=view.bounds,windowBefore=window.frame,transform=world.camera.simdTransform
                let pixels=landmarks.map{view.projectPoint($0)}
                let safe=view.convert(window.contentLayoutRect,from:nil)
                let overlaySafe=abs(raceHUD.frame.maxY-safe.maxY)<0.5 && abs(raceHUD.frame.minY-safe.minY)<0.5
                if index==0 { try capture("before-hiding-controls") }
                setRaceControlsHidden(true);settle();updateCamera(snap:false)
                let after=view.bounds,newPixels=landmarks.map{view.projectPoint($0)}
                let drift=zip(pixels,newPixels).map{hypot(Double($0.x-$1.x),Double($0.y-$1.y))}.max() ?? 100
                let hidden=raceHUD.isHidden && hud.isHidden && window.toolbar?.isVisible==false
                let stable=before==after && window.frame==windowBefore && world.camera.simdTransform==transform && drift<0.01
                if index==0 { try capture("after-hiding-controls") }
                setRaceControlsHidden(false);settle()
                let restored=view.bounds==before && !raceHUD.isHidden && window.toolbar?.isVisible==true
                passed = passed && stable && hidden && restored && overlaySafe
                results.append(["width":before.width,"height":before.height,"afterHeight":after.height,"restoredHeight":view.bounds.height,"restoredWidth":view.bounds.width,
                                "stableViewport":stable,"landmarkDriftPixels":drift,"controlsHidden":hidden,"restored":restored,"overlayBelowToolbar":overlaySafe])
            }
            // Verify normal resizes still reach the renderer; only chrome changes are isolated.
            let resizeWorks=(results[0]["width"] as? CGFloat) != (results[1]["width"] as? CGFloat)
            passed = passed && resizeWorks
            setRaceControlsHidden(true);settle()
            let restartBounds=view.bounds,restartFrame=window.frame
            reset(nil);settle()
            let restartStable=view.bounds==restartBounds && window.frame==restartFrame
            passed = passed && restartStable && !raceHUD.isHidden && window.toolbar?.isVisible==true
            let report:[String:Any]=["passed":passed,"sizes":results,"normalResizeWorks":resizeWorks,"restartStable":restartStable]
            try JSONSerialization.data(withJSONObject:report,options:[.prettyPrinted,.sortedKeys]).write(to:directory.appendingPathComponent("viewport.json"))
            print(report);return passed
        } catch { print("Viewport check: \(error)");return false }
    }
}
