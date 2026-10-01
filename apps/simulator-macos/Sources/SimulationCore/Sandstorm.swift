import Foundation
import simd

/// Deterministic weather state, advanced only by the race's fixed-step clock.
/// Wind is m/s; drift depth is metres. No shared mutable terrain globals.
public struct Sandstorm: Sendable {
    /// Independent 10% chance for each race/reset, not every tenth race.
    public static func drawForRace<R:RandomNumberGenerator>(using generator:inout R)->Bool {
        Int.random(in:0..<10,using:&generator)==0
    }
    public static func drawForRace()->Bool {
        var generator=SystemRandomNumberGenerator()
        return drawForRace(using:&generator)
    }
    public var enabled:Bool
    public private(set) var elapsed=0.0
    public init(enabled:Bool=false) { self.enabled=enabled }
    public mutating func advance(_ dt:Double) { if enabled { elapsed += max(0,dt) } }
    public var accumulation:Double { enabled ? min(1,0.18+elapsed/180):0 }
    public func wind(x:Double,z:Double)->SIMD3<Double> {
        guard enabled else { return .zero }
        let gust=19+4*sin(elapsed*0.47+x*0.013)+2*sin(elapsed*1.13+z*0.02)
        let angle=0.65+0.09*sin(elapsed*0.21)
        return SIMD3(cos(angle)*gust,0,sin(angle)*gust)
    }
    public struct Drift:Sendable {
        public let center:SIMD2<Double>,along:Double,across:Double,height:Double
    }
    public static let drifts:[Drift]=(0..<17).map { i in
        let phase=Double(i)*2*Double.pi/17+0.037*sin(Double(i)*7)
        let p=DirtCourse.point(phase,offset:Double(i%3-1)*0.72)
        return Drift(center:SIMD2(p.x,p.z),along:0.75+Double(i%4)*0.18,across:1.1+Double(i%5)*0.27,height:0.065+Double((i*7)%9)*0.01)
    }
    /// Compact, asymmetric deposits aligned with prevailing wind. Zero at every
    /// patch boundary, so no vertical skirts or floating sheets are needed.
    public static func deposit(x:Double,z:Double)->Double {
        guard max(abs(x),abs(z))<24 else { return 0 }
        var height=0.0
        for drift in drifts {
            let d=SIMD2(x,z)-drift.center
            let u=(d.x*0.796+d.y*0.605)/drift.along+0.12*sin(d.y*3.1+drift.center.x)
            let v=(-d.x*0.605+d.y*0.796)/drift.across+0.10*sin(d.x*2.7+drift.center.y)
            let r=u*u+v*v
            if r<1 { height += drift.height*pow(1-r,2)*(1+0.3*u) }
        }
        return height
    }
    public func depth(x:Double,z:Double)->Double { enabled ? accumulation*Self.deposit(x:x,z:z):0 }
    public func height(x:Double,z:Double)->Double { DirtCourse.height(x:x,z:z)+depth(x:x,z:z) }
    public func acceleration(velocity:SIMD3<Double>,x:Double,z:Double,profile:RobotCollisions.Profile,shelter:Double=1)->SIMD3<Double> {
        guard enabled else { return .zero }
        let relative=wind(x:x,z:z)*shelter-SIMD3(velocity.x,0,velocity.z)
        // Quadratic aerodynamic drag / mass. Coefficient is game-tuned, with
        // chassis cross-section and mass retaining their physical relationship.
        let area=2*profile.halfWidth*profile.height // effective drag coefficient 0.55
        return relative*simd_length(relative)*(0.5*1.1*0.55*area/profile.mass)
    }
}
