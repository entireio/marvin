import Foundation

/// Closed belt centerline in the robot's Y/Z plane. Positive travel moves the
/// bottom run backward (-Z), canceling forward chassis motion at ground contact.
public enum TrackLoop {
    public static let radius = 0.11935, centerY = 0.12485
    public static let rear = -0.24152, front = 0.18894
    public static let straight = front - rear
    public static let circumference = 2*straight + 2*Double.pi*radius
    public static func sample(_ travel: Double) -> (y: Double, z: Double, angle: Double) {
        var s = travel.truncatingRemainder(dividingBy: circumference)
        if s < 0 { s += circumference }
        if s < straight { return (centerY-radius, front-s, .pi) }
        s -= straight
        if s < .pi*radius {
            let a = s/radius
            return (centerY-radius*cos(a), rear-radius*sin(a), .pi+a)
        }
        s -= .pi*radius
        if s < straight { return (centerY+radius, rear+s, 0) }
        let a = (s-straight)/radius
        return (centerY+radius*cos(a), front+radius*sin(a), a)
    }
}
