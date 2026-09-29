import Foundation
import simd

/// Shared authored fixtures and seeded salvage: rendering and collision use the
/// same transforms. Seeded placement keeps race resets and tests reproducible.
public enum InfieldLayout {
    public static let tentOrigins=[SIMD2<Double>(-6.6,-8.7),SIMD2<Double>(-8.5,3.5)]
    public static let orangeCorners=[SIMD2<Double>(-1.6,-1.6),SIMD2<Double>(-1.6,1.6),SIMD2<Double>(1.4,1.6)]
    public static let serviceLane:[SIMD2<Double>]=[SIMD2(-7.5,-12.5),SIMD2(-8.8,-10.8),SIMD2(-8.8,-6.2),SIMD2(-8.5,-3),SIMD2(-8.5,1.8)]
    public static func tentPoint(_ index:Int,_ local:SIMD2<Double>)->SIMD2<Double> {
        tentOrigins[index]+local*(index==0 ? -1.0:1.0)
    }
    public static func laneDistance(_ p:SIMD2<Double>)->Double {
        zip(serviceLane,serviceLane.dropFirst()).map { a,b in
            let d=b-a,t=max(0,min(1,simd_dot(p-a,d)/simd_length_squared(d)))
            return simd_length(p-a-d*t)
        }.min()!
    }

    public struct Part: Sendable {
        public let x,z,yaw,width,depth,height:Double
        public let kind:Int
    }
    public static let parts:[Part] = {
        var result:[Part]=[]
        var seed:UInt64=0x5a17cafe
        func random()->Double { seed=seed &* 6364136223846793005 &+ 1442695040888963407;return Double(seed>>11)/9007199254740992 }
        for _ in 0..<900 {
            let x = -10+random()*19,z = -9+random()*19
            let p=DirtCourse.projection(x:x,z:z)
            guard p.offset<0,p.distance>DirtCourse.fenceOffset+1.3 else { continue }
            // Reserve the entrance-to-east-side aisle and both repair footprints.
            guard laneDistance(SIMD2(x,z))>1.35,
                  hypot((x-tentOrigins[0].x)/2.7,(z-tentOrigins[0].y)/2.7)>1,
                  hypot((x+8.5)/3.0,(z-3.5)/3.0)>1 else { continue }
            guard result.allSatisfy({hypot($0.x-x,$0.z-z)>1.45}) else { continue }
            let kind=result.count%3,w=0.45+random()*0.4,d=0.4+random()*0.35
            result.append(Part(x:x,z:z,yaw:random()*2 * .pi,width:w,depth:d,height:kind==0 ? 0.32:kind==1 ? 0.48:0.18,kind:kind))
            if result.count==16 { break }
        }
        return result
    }()
    public struct Canopy:Sendable {
        public let points:[SIMD2<Double>]
        public let low,high:Double
    }
    public static let canopies=[
        Canopy(points:orangeCorners.map{tentPoint(0,$0)},low:1.97,high:2.32),
        Canopy(points:[SIMD2(-10.5,1.5),SIMD2(-10.5,5.5),SIMD2(-6.5,5.5),SIMD2(-6.5,1.5)],low:1.97,high:2.67)
    ]
    public static let obstacles:[RobotCollisions.Body] = {
        var result:[RobotCollisions.Body]=[]
        func box(_ x:Double,_ z:Double,_ w:Double,_ d:Double,_ height:Double,_ y:Double=0,_ yaw:Double=0,_ round:Bool=false) {
            result.append(.init(position:SIMD3(x,y,z),heading:yaw,profile:.init(mass:1,halfWidth:w/2,halfDepth:d/2,height:height,round:round)))
        }
        for (i,origin) in tentOrigins.enumerated() {
            let first=result.count
            let posts=i==0 ? orangeCorners : [SIMD2(-2.0,-2.0),SIMD2(-2.0,2.0),SIMD2(2.0,-2.0),SIMD2(2.0,2.0)]
            for p in posts { box(origin.x+p.x,origin.y+p.y,0.07,0.07,2.15,0,0,true) }
            let x=origin.x+(i==0 ? -0.1:0),z=origin.y+(i==0 ? 0.35:0)
            // Bench legs and elevated top leave genuine clearance beneath it.
            for zz in [-0.55,0.85] { box(x-0.92,z+zz,0.48,0.09,0.66) }
            box(x-0.92,z+0.15,0.64,1.7,0.12,0.63)
            box(x+0.24,z+0.20,0.95,1.3,0.28)
            box(x+0.24,z+0.20,0.62,0.62,0.93,0.28,0,true)
            for side in [-1.0,1.0] { box(x+0.24+side*0.38,z+0.30,0.22,0.40,0.6,0.25) }
            box(origin.x+(i==0 ? -1.625:0),origin.y+(i==0 ? -0.65:-2.035),1.85,0.05,0.46,1.51,i==0 ? -.pi/2:.pi)
            box(x+1.17,z+0.91,0.09,0.09,1.6)
            box(x+0.83,z+0.91,0.76,0.1,0.1,1.55)
            box(x+0.51,z+0.91,0.44,0.44,0.36,0.76,0,true)
            box(x+(i==0 ? -0.1:1.03),z+(i==0 ? 0.85:-0.86),0.54,0.54,0.535,0,0,true)
            box(x-0.96,z-1.06,0.6,0.4,0.4)
            box(x+(i==0 ? -1.5:1.5),z+(i==0 ? 0.5:0.95),0.32,0.4,0.94)
            if i==0 {
                box(x-1.54,z+0.15,0.08,1.75,0.77,0.665)
                box(x-0.93,z+0.93,0.57,0.13,0.43,0.115)
                box(x-1.3,z-0.85,0.50,0.50,0.50,0.08)
                box(x-1.42,z-1.43,0.39,0.39,0.39,0.08,0,true)
            }
            // Stationary mechanic occupies the same footprint as the visible figure.
            box(origin.x+(i==0 ? 0.65:0.73),origin.y+(i==0 ? -0.25:-0.35),0.40,0.4,0.9,0.055,0,true)
            if i==0 { for j in first..<result.count {
                result[j].position.x=2*origin.x-result[j].position.x
                result[j].position.z=2*origin.y-result[j].position.z
                result[j].heading += .pi
            } }
        }
        for side in [-1.0,1.0] { box(DirtCourse.serviceEntryX+side*1.4,-12.2,0.09,0.09,0.56,0,0,true) }
        box(DirtCourse.serviceEntryX-1.85,-12.21,0.05,0.05,0.67)
        for p in parts { box(p.x,p.z,p.width,p.depth,p.height,0,p.yaw,p.kind==0) }
        return result
    }()
}
