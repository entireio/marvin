import AppKit
import PetDeviceCore

extension AppController {
    var petSmokeDirectory: URL? {
        let args=CommandLine.arguments
        guard let i=args.firstIndex(of:"--pet-smoke-test"),i+1<args.count else { return nil }
        return URL(fileURLWithPath:args[i+1],isDirectory:true)
    }
    func recordPetSmoke() {
        guard let folder=petSmokeDirectory,let pet=petConnection else { return }
        do {
            try FileManager.default.createDirectory(at:folder,withIntermediateDirectories:true)
            let value:[String:Any]=["online":pet.online,"mode":connectedPetMode,"active":active,"x":simulation.x,"z":simulation.z,"yaw":simulation.yaw,"pitch":simulation.pitch,"speed":simulation.speed,"paused":simulation.paused,"eyeDesign":pet.state.preferences.eyeDesign,"yawCenter":pet.calibration.yawCenter,"showBatteryIcon":pet.state.preferences.showBatteryIcon]
            try JSONSerialization.data(withJSONObject:value,options:.sortedKeys).write(to:folder.appendingPathComponent("state.json"),options:.atomic)
            if let binding=pet.binding {
                let receipt=folder.appendingPathComponent("binding.json")
                if !FileManager.default.fileExists(atPath:receipt.path) {
                    try JSONEncoder().encode(binding).write(to:receipt,options:.atomic)
                    try FileManager.default.setAttributes([.posixPermissions:0o600],ofItemAtPath:receipt.path)
                }
            }
            let capture=folder.appendingPathComponent("capture.request")
            if FileManager.default.fileExists(atPath:capture.path) {
                if let tiff=view.snapshot().tiffRepresentation,let bitmap=NSBitmapImageRep(data:tiff),let png=bitmap.representation(using:.png,properties:[:]) { try png.write(to:folder.appendingPathComponent("pet.png"),options:.atomic) }
                if let content=petConnectionWindow?.window?.contentView,let bitmap=content.bitmapImageRepForCachingDisplay(in:content.bounds) {
                    content.cacheDisplay(in:content.bounds,to:bitmap)
                    if let png=bitmap.representation(using:.png,properties:[:]) { try png.write(to:folder.appendingPathComponent("connection.png"),options:.atomic) }
                }
                try FileManager.default.removeItem(at:capture)
            }
        } catch { NSLog("Simulator pet smoke capture failed.") }
    }
    @objc func showPetConnection(_ sender: Any?) {
        guard let connection=petConnection else {
            let alert=NSAlert();alert.messageText="The simulator connection could not initialize.";alert.informativeText="Check the simulator’s saved settings and command ledger in Application Support. Offline play remains available.";alert.runModal();return
        }
        if petConnectionWindow==nil { petConnectionWindow=PetConnectionWindow(connection:connection) }
        petConnectionWindow?.refresh();petConnectionWindow?.showWindow(nil)
    }
}
