import AppKit
import simd

// From Robot.swift (the robot classes themselves are not needed by the HUD files).
func color(_ hex: UInt32, alpha: CGFloat = 1) -> NSColor {
    NSColor(srgbRed: CGFloat((hex >> 16) & 255)/255, green: CGFloat((hex >> 8) & 255)/255, blue: CGFloat(hex & 255)/255, alpha: alpha)
}
enum Robot { static let silverColor: UInt32 = 0xc3c7c9 }
/// An empty town, like the Godot port's stub until the town stream lands (the TOWN map then shows the course only).
final class TownWorld {
    var mapBuildings: [[SIMD2<Double>]] = []
    var mapStreets: [[SIMD2<Double>]] = []
}
