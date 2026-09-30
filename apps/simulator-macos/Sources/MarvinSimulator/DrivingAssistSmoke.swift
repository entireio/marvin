import AppKit

extension AppController {
    func checkDrivingAssistSettings(at directory: URL) throws -> Bool {
        let keys = ["disableRaceSteeringAssist", "disableRaceBrakingAssist", "sandstormRace"]
        let saved = keys.map { UserDefaults.standard.object(forKey:$0) }
        let frame = mainMenu.frame
        defer {
            for (key,value) in zip(keys,saved) {
                if let value { UserDefaults.standard.set(value,forKey:key) }
                else { UserDefaults.standard.removeObject(forKey:key) }
            }
            mainMenu.frame = frame; mainMenu.refresh(); mainMenu.needsLayout = true
        }
        keys.forEach { UserDefaults.standard.removeObject(forKey:$0) }
        var passed = mainMenu.steeringAssist && mainMenu.brakingAssist
        mainMenu.selection = 3; mainMenu.activate()
        passed = passed && !mainMenu.steeringAssist && mainMenu.brakingAssist
        mainMenu.selection = 4; mainMenu.activate()
        passed = passed && !mainMenu.steeringAssist && !mainMenu.brakingAssist
        mainMenu.selection = 3; mainMenu.activate()
        passed = passed && mainMenu.steeringAssist && !mainMenu.brakingAssist
        // A fresh settings view must read the persisted, independent choices.
        let reopened = MainMenuView(frame:.zero)
        passed = passed && reopened.steeringAssist && !reopened.brakingAssist
        mainMenu.selection = 4; mainMenu.activate()
        mainMenu.selection=5;mainMenu.activate()
        passed = passed && mainMenu.sandstorm
        let weatherReopened=MainMenuView(frame:.zero)
        passed = passed && weatherReopened.sandstorm
        mainMenu.selection=5;mainMenu.activate()
        passed = passed && !mainMenu.sandstorm
        mainMenu.frame = NSRect(x:0,y:0,width:900,height:550)
        mainMenu.layoutSubtreeIfNeeded()
        let buttons = mainMenu.subviews.compactMap { $0 as? NSButton }.filter { !$0.isHidden }
        passed = passed && buttons.count == 7 && buttons.allSatisfy { mainMenu.bounds.contains($0.frame) }
        if let bitmap = mainMenu.bitmapImageRepForCachingDisplay(in:mainMenu.bounds) {
            mainMenu.cacheDisplay(in:mainMenu.bounds,to:bitmap)
            if let png = bitmap.representation(using:.png,properties:[:]) {
                try png.write(to:directory.appendingPathComponent("driving-assist-settings.png"))
            } else { passed = false }
        } else { passed = false }
        return passed
    }
}
