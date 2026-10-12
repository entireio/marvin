import Foundation
import simd

/// Per-race, bounded sand heightfield. Mutated only on the serial simulation
/// clock; rendering consumes immutable snapshots, never these mutable arrays.
public final class SandDeformation: @unchecked Sendable {
    public static let step=0.0625, size=4.0, resolution=64, capacity=64
    public struct Key: Hashable, Sendable { public let x:Int,z:Int
        public init(_ x:Int,_ z:Int) { self.x=x;self.z=z }
    }
    public struct Contact: Sendable {
        public let x:Double,z:Double,width:Double,length:Double
        public init(x:Double,z:Double,width:Double,length:Double) { self.x=x;self.z=z;self.width=width;self.length=length }
    }
    public final class Tile {
        public let key:Key
        public fileprivate(set) var delta=[Float](repeating:0,count:4096)
        fileprivate var base=[Float](repeating:0,count:4096)
        fileprivate var touched=0.0
        fileprivate var minI=64,maxI=0,minJ=64,maxJ=0
        init(_ key:Key) {
            self.key=key
            for j in 0..<64 { for i in 0..<64 {
                base[j*64+i]=Float(DesertTerrain.height(x:Double(key.x)*4+Double(i)*SandDeformation.step,z:Double(key.z)*4+Double(j)*SandDeformation.step))
            }}
        }
    }
    public var contactLayouts:[Int:[Contact]]=[:]
    public private(set) var tiles:[Key:Tile]=[:]
    public private(set) var dirty=Set<Key>(), topologyVersion=0
    public private(set) var clock=0.0, displacedVolume=0.0
    private var protected:[SIMD2<Double>]=[]
    public init() {}
    public func reset() { tiles.removeAll();dirty.removeAll();topologyVersion += 1;clock=0;displacedVolume=0 }
    public func begin(dt:Double,positions:[SIMD2<Double>]) { clock += dt;protected=positions;dirty.removeAll() }
    public static func key(x:Double,z:Double)->Key { Key(Int(floor(x/4)),Int(floor(z/4))) }
    private func ensure(_ key:Key)->Tile? {
        if let t=tiles[key] { return t }
        let center=SIMD2(Double(key.x)*4+2,Double(key.z)*4+2)
        guard max(abs(center.x),abs(center.y))>DesertTerrain.townEdge+4,
              max(abs(center.x),abs(center.y))<DesertTerrain.extent-4 else { return nil }
        if tiles.count==Self.capacity {
            guard let old=tiles.values.filter({ t in
                let p=SIMD2(Double(t.key.x)*4+2,Double(t.key.z)*4+2)
                return protected.allSatisfy{simd_distance(p,$0)>8}
            }).min(by:{$0.touched<$1.touched}) else { return nil }
            tiles.removeValue(forKey:old.key)
            for z in -1...1 { for x in -1...1 { dirty.insert(Key(old.key.x+x,old.key.z+z)) } }
        }
        let t=Tile(key);tiles[key]=t;topologyVersion += 1;dirty.insert(key);return t
    }
    public func vertex(_ ix:Int,_ iz:Int)->Float {
        let tx=Int(floor(Double(ix)/64)),tz=Int(floor(Double(iz)/64))
        guard let t=tiles[Key(tx,tz)] else { return 0 }
        return t.delta[(iz-tz*64)*64+ix-tx*64]
    }
    /// One immutable snapshot including the normal halo. Resolve at most nine
    /// tile references rather than doing a dictionary lookup for every vertex.
    public func grid(_ key:Key)->[Float] {
        var values=[Float](repeating:0,count:67*67)
        for dz in -1...1 { for dx in -1...1 {
            guard let tile=tiles[Key(key.x+dx,key.z+dz)] else { continue }
            let loX=max(-1,dx*64),hiX=min(65,dx*64+63)
            let loZ=max(-1,dz*64),hiZ=min(65,dz*64+63)
            for j in loZ...hiZ { for i in loX...hiX {
                values[(j+1)*67+i+1]=tile.delta[(j-dz*64)*64+i-dx*64]
            }}
        }}
        return values
    }
    /// Same diagonal/barycentric interpolation as the detailed surface mesh.
    public func offset(x:Double,z:Double)->Double {
        guard !tiles.isEmpty,max(abs(x),abs(z))>DesertTerrain.townEdge else { return 0 }
        let gx=x/Self.step,gz=z/Self.step,ix=Int(floor(gx)),iz=Int(floor(gz)),u=Float(gx-Double(ix)),v=Float(gz-Double(iz))
        let a=vertex(ix,iz),b=vertex(ix+1,iz),c=vertex(ix,iz+1),d=vertex(ix+1,iz+1)
        return Double(u+v<=1 ? a+(b-a)*u+(c-a)*v:d+(c-d)*(1-u)+(b-d)*(1-v))
    }
    public func supportOffset(x:Double,z:Double,heading:Double,profile:RobotCollisions.Profile)->Double {
        guard !tiles.isEmpty,max(abs(x),abs(z))>DesertTerrain.townEdge,
              let index=RobotCollisions.profiles.firstIndex(where:{$0.mass==profile.mass}),let contacts=contactLayouts[index] else { return offset(x:x,z:z) }
        let c=cos(heading),s=sin(heading)
        var sum=0.0,count=0
        for contact in contacts { for f in [-0.3,0.0,0.3] {
            let zz=contact.z+contact.length*f
            sum += offset(x:x+c*contact.x+s*zz,z:z-s*contact.x+c*zz);count += 1
        }}
        return sum/Double(max(1,count))
    }
    public func soleHeight(_ state:Simulation,x:Double,z:Double)->Double {
        state.groundY+state.duneOrientation.act(SIMD3(x,0,z)).y
    }
    public func contactWeight(_ state:Simulation,_ contact:Contact)->Double {
        let x=state.x+cos(state.heading)*contact.x+sin(state.heading)*contact.z
        let z=state.z-sin(state.heading)*contact.x+cos(state.heading)*contact.z
        let penetration=DesertTerrain.height(x:x,z:z)+offset(x:x,z:z)-soleHeight(state,x:contact.x,z:contact.z)
        return max(0,min(1,(penetration+0.025)/0.05))
    }
    public func stamp(_ state:Simulation,contacts:[Contact],dt:Double) {
        guard state.hasDirtContact,max(abs(state.x),abs(state.z))>DesertTerrain.townEdge+8 else { return }
        let c=cos(state.heading),s=sin(state.heading)
        let orientation=state.duneOrientation,syX=orientation.act(SIMD3(1,0,0)).y,syZ=orientation.act(SIMD3(0,0,1)).y
        for contact in contacts {
            let cx=state.x+c*contact.x+s*contact.z,cz=state.z-s*contact.x+c*contact.z
            let radius=hypot(contact.width/2+0.20,contact.length/2+0.12)
            let minX=Int(floor((cx-radius)/Self.step)),maxX=Int(ceil((cx+radius)/Self.step))
            let minZ=Int(floor((cz-radius)/Self.step)),maxZ=Int(ceil((cz+radius)/Self.step))
            // Allocate the footprint plus a one-cell normal/settling halo.
            for tz in Int(floor(Double(minZ-2)/64))...Int(floor(Double(maxZ+2)/64)) {
                for tx in Int(floor(Double(minX-2)/64))...Int(floor(Double(maxX+2)/64)) { _=ensure(Key(tx,tz)) }
            }
            var banks:[(Tile,Int,Float)]=[],removed:Float=0,total:Float=0
            for iz in minZ...maxZ { for ix in minX...maxX {
                let key=Key(Int(floor(Double(ix)/64)),Int(floor(Double(iz)/64)))
                guard let tile=tiles[key] else { continue }
                let x=Double(ix)*Self.step,z=Double(iz)*Self.step,dx=x-cx,dz=z-cz
                let lateral=c*dx-s*dz,along=s*dx+c*dz
                let edge=abs(lateral)-contact.width/2,end=abs(along)-contact.length/2
                guard end<0.10,edge<0.19 else { continue }
                let index=(iz-key.z*64)*64+ix-key.x*64
                let base=Double(tile.base[index]),sole=state.groundY+syX*(contact.x+lateral)+syZ*(contact.z+along)
                // Exposed running gear cannot deform or spray the ground.
                guard base+Double(tile.delta[index])-sole > -0.025 else { continue }
                let irregular=0.88+0.12*sin(x*29+sin(z*19))*cos(z*23-x*11)
                let longitudinal=max(0,min(1,(0.10-end)/0.10))
                if edge<0.015 && end<0.025 {
                    let pressure=max(0,min(1,(0.015-edge)/0.045))*longitudinal
                    // Retain some burial: loose sand meets the tread above its
                    // sole. Limit cut depth; repeated passes cannot dig forever.
                    let target=Float(max(-0.12,min(-0.035*irregular,sole+0.035-base)))
                    let cut=max(0,tile.delta[index]-target)*Float(pressure*(1-exp(-dt*18)))
                    tile.delta[index] -= cut;removed += cut
                    if cut>0.000001 { changed(key,index);tile.touched=clock }
                } else if edge>0.015 && edge<0.19 {
                    let weight=Float(sin((edge-0.015)/0.175 * .pi)*longitudinal*irregular)
                    banks.append((tile,index,weight));total += weight
                }
            }}
            if total>0 {
                for (tile,index,weight) in banks where removed>0.000001 { tile.delta[index]=min(0.095,tile.delta[index]+removed*0.72*weight/total);changed(tile.key,index);tile.touched=clock }
            }
            displacedVolume += Double(removed)*Self.step*Self.step
        }
    }
    private func changed(_ key:Key,_ index:Int) {
        dirty.insert(key)
        let i=index%64,j=index/64
        if let tile=tiles[key] {
            tile.minI=min(tile.minI,i);tile.maxI=max(tile.maxI,i)
            tile.minJ=min(tile.minJ,j);tile.maxJ=max(tile.maxJ,j)
        }
        let xs=i<2 ? [-1,0]:(i==63 ? [0,1]:[0])
        let zs=j<2 ? [-1,0]:(j==63 ? [0,1]:[0])
        for z in zs { for x in xs {
            let neighbor=Key(key.x+x,key.z+z)
            if tiles[neighbor] != nil { dirty.insert(neighbor) }
        }}
    }
    public func settle(dt:Double) {
        // Conservative exchange between neighboring cells: loose berms soften
        // and slump downhill, while compacted tracks persist. Stop processing
        // old footprints; no whole-desert simulation or unbounded history.
        var changes:[Key:[Float]]=[:]
        let active=tiles.values.filter{clock-$0.touched<2.5}
        for tile in active {
            let key=tile.key
            guard tile.minI<=tile.maxI,tile.minJ<=tile.maxJ else { continue }
            for j in max(0,tile.minJ-1)...min(63,tile.maxJ+1) { for i in max(0,tile.minI-1)...min(63,tile.maxI+1) {
                let index=j*64+i,a=tile.delta[index]
                for (di,dj) in [(1,0),(0,1)] {
                    let ix=key.x*64+i+di,iz=key.z*64+j+dj
                    let otherKey=Key(Int(floor(Double(ix)/64)),Int(floor(Double(iz)/64)))
                    guard let other=tiles[otherKey] else { continue }
                    let n=(iz-otherKey.z*64)*64+ix-otherKey.x*64,b=other.delta[n]
                    guard abs(a)+abs(b)>0.0001 else { continue }
                    let slope=(tile.base[index]+a)-(other.base[n]+b)
                    let excess=max(0,abs(slope)-Float(Self.step*0.66))
                    let loose=max(0,min(1,(max(a,b)+0.004)/0.045))
                    let flux=((a-b)*0.7+(slope<0 ? -excess:excess)*2.2)*Float(dt)*loose
                    guard abs(flux)>0.000001 else { continue }
                    if changes[key]==nil { changes[key]=[Float](repeating:0,count:4096) }
                    if changes[otherKey]==nil { changes[otherKey]=[Float](repeating:0,count:4096) }
                    changes[key]![index] -= flux;changes[otherKey]![n] += flux
                }
            }}
        }
        for (key,change) in changes { if let tile=tiles[key] {
            for i in change.indices where abs(change[i])>0.000001 { tile.delta[i] += change[i];changed(key,i) }
        }}

    }
}
