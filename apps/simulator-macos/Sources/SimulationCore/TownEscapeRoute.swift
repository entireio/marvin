import Foundation
import simd

/// Conservative town navigation with footprint clearance and line-of-sight simplification.
public struct TownEscapeRoute {
    let city:CityCollisionWorld,origin:SIMD2<Double>
    public init(city:CityCollisionWorld,origin:SIMD2<Double>) { self.city=city;self.origin=origin }
    public func route(from start:SIMD2<Double>,to goal:SIMD2<Double>,rounded:Bool=false,clearance:Double=0.53,goalTolerance:Double=3,avoiding:[RobotCollisions.Body]=[])->[SIMD2<Double>]? {
        typealias Cell=SIMD2<Int>
        func cell(_ p:SIMD2<Double>)->Cell { let d=(p-origin)*2;return Cell(Int(d.x.rounded()),Int(d.y.rounded())) }
        func point(_ c:Cell)->SIMD2<Double> { origin+SIMD2(Double(c.x),Double(c.y))/2 }
        func free(_ p:SIMD2<Double>)->Bool {
            let projection=DirtCourse.projection(x:p.x,z:p.y)
            guard projection.offset>0,projection.distance>DirtCourse.fenceOffset+0.8 else { return false }
            let body=RobotCollisions.Body(position:SIMD3(p.x,DirtCourse.height(x:p.x,z:p.y),p.y),profile:.init(mass:18,halfWidth:clearance,halfDepth:clearance,height:1.0,round:true))
            return (city.nearby(body)+avoiding).allSatisfy{RobotCollisions.contact(body,$0)==nil}
        }
        let source=cell(start),target=cell(goal)
        var parents:[Cell:Cell]=[source:source],queue=[source],head=0,best=source,bestDistance=Double.infinity,cache:[Cell:Bool]=[:]
        let directions=[Cell(1,0),Cell(-1,0),Cell(0,1),Cell(0,-1)]
        while head<queue.count {
            let c=queue[head];head += 1
            let distance=simd_distance(point(c),goal)
            if distance<bestDistance { best=c;bestDistance=distance }
            if c==target { break }
            for d in directions {
                let next=c&+d
                guard abs(next.x)<100,abs(next.y)<100,parents[next]==nil else { continue }
                let available=cache[next] ?? free(point(next));cache[next]=available
                if available { parents[next]=c;queue.append(next) }
            }
        }
        guard bestDistance<goalTolerance else { print("Town route nearest reachable point is \(bestDistance)m from \(goal)");return nil }
        var path=[point(best)],cursor=best
        while cursor != source { cursor=parents[cursor]!;path.append(point(cursor)) }
        path.reverse()
        func clear(_ a:SIMD2<Double>,_ b:SIMD2<Double>)->Bool {
            let count=max(1,Int(ceil(simd_distance(a,b)/0.15)))
            return (0...count).allSatisfy{free(a+(b-a)*Double($0)/Double(count))}
        }
        var result=[start],i=0
        while i<path.count-1 {
            var next=i+1
            for j in (i+1)..<path.count { if clear(result.last!,path[j]) { next=j } else { break } }
            result.append(path[next]);i=next
        }
        guard rounded,result.count>2 else { return result }
        var rounded=[result[0]]
        for k in 1..<(result.count-1) {
            let a=result[k-1],b=result[k],c=result[k+1]
            let trim=min(1.4,min(simd_distance(a,b),simd_distance(b,c))*0.35)
            let entry=b+simd_normalize(a-b)*trim,exit=b+simd_normalize(c-b)*trim
            let curve=(0...8).map { step -> SIMD2<Double> in
                let t=Double(step)/8;return entry*(1-t)*(1-t)+b*2*t*(1-t)+exit*t*t
            }
            if zip(curve,curve.dropFirst()).allSatisfy({clear($0.0,$0.1)}) { rounded += curve }
            else { rounded.append(b) }
        }
        rounded.append(result.last!)
        return zip(rounded,rounded.dropFirst()).allSatisfy({clear($0.0,$0.1)}) ? rounded:result
    }
}

