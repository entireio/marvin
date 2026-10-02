import simd
import SimulationCore

/// Shared, load-time pedestrian reachability. A doorway may face an alley, but
/// that alley must connect to a town street with pedestrian-width clearance.
final class TownAccessMap {
    private let size=1320,origin = -165.0,step=0.25
    private var distance:[Int32]
    struct Footprint {
        let center:SIMD2<Double>,width:Double,depth:Double,yaw:Double
    }
    init(lots:[TownWorld.TownLot]=[],footprints:[Footprint]=[],streets:[[SIMD2<Double>]]) {
        distance=Array(repeating:-1,count:size*size)
        let obstacles=footprints+lots.map{Footprint(center:SIMD2($0.x,$0.z),width:$0.width,depth:$0.depth,yaw:0)}
        for lot in obstacles {
            let c=cos(lot.yaw),s=sin(lot.yaw),halfW=lot.width/2+0.38,halfD=lot.depth/2+0.38
            let radius=SIMD2(abs(c)*halfW+abs(s)*halfD,abs(s)*halfW+abs(c)*halfD)
            let lo=cell(lot.center-radius),hi=cell(lot.center+radius)
            for z in max(0,lo.y)...min(size-1,hi.y) { for x in max(0,lo.x)...min(size-1,hi.x) {
                let q=SIMD2(origin+(Double(x)+0.5)*step,origin+(Double(z)+0.5)*step)-lot.center
                if abs(q.x*c-q.y*s)<halfW && abs(q.x*s+q.y*c)<halfD { distance[z*size+x] = -2 }
            }}
        }
        // The racing surface and infield are not public alley connections.
        let lo=cell(SIMD2(-28.0,-28.0)),hi=cell(SIMD2(28.0,28.0))
        for z in lo.y...hi.y { for x in lo.x...hi.x {
            let p=SIMD2(origin+(Double(x)+0.5)*step,origin+(Double(z)+0.5)*step)
            let projection=DirtCourse.projection(x:p.x,z:p.y)
            if projection.offset<=0 || projection.distance<DirtCourse.fenceOffset+0.22 { distance[z*size+x] = -2 }
        }}
        var queue=[Int]();queue.reserveCapacity(size*size/3)
        for path in streets { for p in path {
            let c=cell(p),i=c.y*size+c.x
            if c.x>=0 && c.x<size && c.y>=0 && c.y<size && distance[i] == -1 { distance[i]=0;queue.append(i) }
        }}
        var head=0
        while head<queue.count {
            let i=queue[head];head += 1
            let x=i%size,z=i/size
            for (dx,dz) in [(1,0),(-1,0),(0,1),(0,-1)] {
                let xx=x+dx,zz=z+dz
                guard xx>=0,xx<size,zz>=0,zz<size else { continue }
                let j=zz*size+xx
                if distance[j] == -1 { distance[j]=distance[i]+1;queue.append(j) }
            }
        }
    }
    private func cell(_ p:SIMD2<Double>)->SIMD2<Int> { SIMD2(Int(floor((p.x-origin)/step)),Int(floor((p.y-origin)/step))) }
    func streetDistance(_ p:SIMD2<Double>)->Double? {
        let c=cell(p)
        guard c.x>=0,c.x<size,c.y>=0,c.y<size else { return nil }
        let d=distance[c.y*size+c.x]
        return d>=0 ? Double(d)*step:nil
    }
    /// Follow the shared flood field to a public street, retaining enough
    /// samples to audit the entire corridor against the final collision mesh.
    func routeToStreet(from p:SIMD2<Double>)->[SIMD2<Double>]? {
        var c=cell(p)
        guard c.x>=0,c.x<size,c.y>=0,c.y<size,distance[c.y*size+c.x]>=0 else { return nil }
        var result=[p]
        for _ in 0..<3000 {
            let value=distance[c.y*size+c.x]
            result.append(SIMD2(origin+(Double(c.x)+0.5)*step,origin+(Double(c.y)+0.5)*step))
            if value==0 { return result }
            guard let next=[SIMD2(c.x+1,c.y),SIMD2(c.x-1,c.y),SIMD2(c.x,c.y+1),SIMD2(c.x,c.y-1)].first(where:{ q in
                q.x>=0 && q.x<size && q.y>=0 && q.y<size && distance[q.y*size+q.x]==value-1
            }) else { return nil }
            c=next
        }
        return nil
    }

}
