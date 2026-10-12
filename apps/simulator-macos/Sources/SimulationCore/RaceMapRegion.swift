import Foundation
import simd

/// Shared navigation boundary, with a small dead band to avoid flickering at
/// the wall or the town edge. The infield belongs to the course enclosure.
public enum RaceMapRegion:String,Sendable {
    case course="COURSE",town="TOWN",dunes="DUNES"
    private static let enclosure=DirtCourse.surfacePoints(offset:DirtCourse.fenceOffset)
    public static func at(_ p:SIMD2<Double>,previous:Self = .course)->Self {
        let edge=max(abs(p.x),abs(p.y))
        if TownFootprint.edgeDistance(p)>(previous == .dunes ? -1:1) { return .dunes }
        if edge>35 { return .town }
        var inside=false,distance=Double.infinity
        for i in enclosure.indices {
            let a=enclosure[i],b=enclosure[(i+1)%enclosure.count],d=b-a
            let t=max(0,min(1,simd_dot(p-a,d)/max(1e-12,simd_dot(d,d))))
            distance=min(distance,simd_length(p-a-d*t))
            if (a.y>p.y) != (b.y>p.y) && p.x<(b.x-a.x)*(p.y-a.y)/(b.y-a.y)+a.x { inside.toggle() }
        }
        if distance<0.18 { return previous == .course ? .course:.town }
        return inside ? .course:.town
    }
}

/// Critically damped camera motion: continuous position and velocity when a
/// boundary changes the destination, independent of rendering frame rate.
public struct OverviewMotion {
    public private(set) var eye=SIMD3<Double>.zero,aim=SIMD3<Double>.zero
    private var eyeVelocity=SIMD3<Double>.zero,aimVelocity=SIMD3<Double>.zero
    public init() {}
    public mutating func reset(eye:SIMD3<Double>,aim:SIMD3<Double>) {
        self.eye=eye;self.aim=aim;eyeVelocity = .zero;aimVelocity = .zero
    }
    public mutating func advance(eye goalEye:SIMD3<Double>,aim goalAim:SIMD3<Double>,dt:Double) {
        let h=max(0,min(0.1,dt)),omega=8.0,decay=exp(-omega*h)
        func step(_ p:inout SIMD3<Double>,_ v:inout SIMD3<Double>,_ goal:SIMD3<Double>) {
            let delta=p-goal,t=(v+omega*delta)*h
            p=goal+(delta+t)*decay;v=(v-omega*t)*decay
        }
        step(&eye,&eyeVelocity,goalEye);step(&aim,&aimVelocity,goalAim)
    }
}

/// Irregular urban apron shared by town generation and navigation. The dune
/// heightfield remains level underneath every foundation and exit route.
public enum TownFootprint {
    public static func radius(at angle:Double)->Double {
        130+13*sin(3*angle+0.4)+9*cos(5*angle-0.7)+6*sin(2*angle)
    }
    public static func edgeDistance(_ p:SIMD2<Double>)->Double {
        simd_length(p)-radius(at:atan2(p.y,p.x))
    }
}
