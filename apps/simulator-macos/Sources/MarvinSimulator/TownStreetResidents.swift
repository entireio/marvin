import SceneKit
import SimulationCore
import simd

/// Street errands complement doorway visits. Routes are clipped against actual
/// scenery, and every step is swept; neither LOD nor camera cuts teleport people.
final class TownStreetResidents {
    final class Walker {
        let node:SCNNode,path:[SIMD2<Double>],feet:CitizenMotion.FootPlacement,materials:[SCNMaterial]
        let start:Int,speed:Double,restingSole:Float
        var wasDetailed=true
        var position:SIMD2<Double>,target:Int,direction:Int,heading:Double
        var distance=0.0,blend=0.0,pending=0.0,wait=0.0,blocked=0.0
        init(node:SCNNode,path:[SIMD2<Double>],index:Int) {
            self.node=node;self.path=path;start=path.count/2;position=path[start]
            direction=index%2==0 ? 1:-1;target=start+direction
            let d=path[target]-position;heading=atan2(d.x,d.y)
            speed=0.65+Double(index%7)*0.047
            feet=CitizenMotion.FootPlacement(node.geometry!);materials=node.geometry!.materials
            restingSole=feet.minimum(cycle:0,blend:0)
        }
    }
    private(set) var walkers:[Walker]=[]
    private let city:CityCollisionWorld
    private var storm=false,time=0.0
    private(set) var maximumPenetration=0.0,poseUpdates=0,navigationUpdates=0
    private static func body(_ p:SIMD2<Double>)->RobotCollisions.Body {
        .init(position:SIMD3(p.x,0,p.y),profile:.init(mass:70,halfWidth:0.17,halfDepth:0.17,height:1.05,round:true))
    }
    var bodies:[RobotCollisions.Body] { storm ? []:walkers.map {
        var body=Self.body($0.position);body.heading=$0.heading;return body
    } }
    var visible:Int { storm ? 0:walkers.count }
    init(paths:[[SIMD2<Double>]],city:CityCollisionWorld,crowd:TownCrowd,root:SCNNode) {
        self.city=city
        // Interleave the authored streets so a population cap cannot fill only
        // the first block. Each route is resampled for reliable collision sweeps.
        let ordered=paths.enumerated().sorted{ (($0.offset*7919)%max(1,paths.count)) < (($1.offset*7919)%max(1,paths.count)) }
        for (_,path) in ordered {
            guard walkers.count<24 else { break }
            var runs=[[SIMD2<Double>]](),run=[SIMD2<Double>]()
            for (a,b) in zip(path,path.dropFirst()) {
                let steps=max(1,Int(ceil(simd_distance(a,b)/0.12)))
                for j in 0..<steps {
                    let p=a+(b-a)*Double(j)/Double(steps),probe=Self.body(p)
                    let free=city.nearby(probe).allSatisfy{RobotCollisions.contact(probe,$0)==nil}
                    if free { run.append(p) } else if !run.isEmpty { runs.append(run);run=[] }
                }
            }
            if !run.isEmpty { runs.append(run) }
            guard let route=runs.max(by:{$0.count<$1.count}),route.count>35,
                  walkers.allSatisfy({simd_distance($0.position,route[route.count/2])>5}) else { continue }
            let i=walkers.count,p=route[route.count/2]
            guard let node=crowd.add(x:p.x,y:0,z:p.y,yaw:0,index:7200+(i%2)+(i/2%2)*2+i/4*12,seated:false,animated:true) else { continue }
            node.name="Street pedestrian \(i)";node.castsShadow=true;root.addChildNode(node)
            walkers.append(Walker(node:node,path:route,index:i))
        }
        reset()
        print("Street residents: \(walkers.count) distributed walking routes")
    }
    func setStorm(_ value:Bool) { storm=value;reset() }
    func reset() {
        time=0;maximumPenetration=0;poseUpdates=0;navigationUpdates=0
        for (i,w) in walkers.enumerated() {
            w.position=w.path[w.start];w.direction=i%2==0 ? 1:-1;w.target=w.start+w.direction
            let d=w.path[w.target]-w.position;w.heading=atan2(d.x,d.y)
            w.distance=0;w.blend=0;w.pending=0;w.wait=Double(i%4)*0.35;w.blocked=0
            w.node.isHidden=storm;pose(w,detailed:true)
        }
    }
    private func pose(_ w:Walker,detailed:Bool) {
        let cycle=Float(w.distance/0.24 * .pi),blend=detailed ? Float(w.blend):0
        w.node.position=SCNVector3(w.position.x,-0.016-Double(detailed ? w.feet.minimum(cycle:cycle,blend:blend):w.restingSole),w.position.y)
        w.node.eulerAngles.y=CGFloat(w.heading)
        // Off-screen navigation continues at 10 Hz, but gait deformation and
        // uniform writes sleep until the resident returns to the camera.
        guard detailed || w.wasDetailed else { return }
        w.wasDetailed=detailed
        for m in w.materials {
            m.setValue(Float(time),forKey:"crowdTime");m.setValue(cycle,forKey:"walkCycle");m.setValue(blend,forKey:"walkBlend")
        }
    }
    func update(dt:Double,obstacles:[RobotCollisions.Body],visible:((SCNNode)->Bool)?=nil) {
        guard dt>0,!storm else { return };time += dt
        for (i,w) in walkers.enumerated() {
            w.pending += min(dt,0.05)
            let detailed=visible?(w.node) ?? true
            let near=obstacles.contains{simd_distance(SIMD2($0.position.x,$0.position.z),w.position)<3}
            guard detailed || near || w.pending>=0.1-1e-8 else { continue }
            let step=w.pending;w.pending=0;navigationUpdates += 1
            if simd_distance(w.position,w.path[w.target])<0.15 {
                let next=w.target+w.direction
                if w.path.indices.contains(next) { w.target=next }
                else { w.direction *= -1;w.target += w.direction;w.wait=1.5+Double(i%5)*0.6 }
            }
            let delta=w.path[w.target]-w.position,angle=atan2(delta.x,delta.y)
            let turn=atan2(sin(angle-w.heading),cos(angle-w.heading))
            w.heading += max(-step*2.4,min(step*2.4,turn))
            w.wait=max(0,w.wait-step)
            // Turn before stepping after a reversal. Sweeping a turning arc
            // can leave the validated corridor and pin a walker to a wall.
            let length=w.wait>0 || abs(turn)>0.25 ? 0:min(simd_length(delta),w.speed*step)
            let next=w.position+delta/max(0.001,simd_length(delta))*length
            let solids=city.nearby(Self.body(next))+obstacles
            let samples=max(1,Int(ceil(length/0.015)))
            let free=(1...samples).allSatisfy { j in
                let p=w.position+(next-w.position)*Double(j)/Double(samples)
                return solids.allSatisfy{RobotCollisions.contact(Self.body(p),$0)==nil}
                    && walkers.allSatisfy{$0 === w || simd_distance(p,$0.position)>0.38}
            }
            let moved=free ? simd_distance(w.position,next):0
            if free { w.position=next }
            w.distance += moved;w.blend += ((moved>0.0001 ? 1.0:0)-w.blend)*min(1,step*10)
            w.blocked = !free ? w.blocked+step:0
            if w.blocked>2.5+Double(i%3)*0.4 {
                // A one-sample reversal can still aim ahead of the body and
                // oscillate forever against an oncoming visitor. Retreat toward
                // a point a metre behind our actual position on the path.
                let nearest=w.path.indices.min { simd_distance(w.path[$0],w.position)<simd_distance(w.path[$1],w.position) }!
                w.direction *= -1
                w.target=max(0,min(w.path.count-1,nearest+w.direction*8));w.blocked=0
            }
            if detailed { poseUpdates += 1 }
            pose(w,detailed:detailed)
            for b in solids { if let c=RobotCollisions.contact(Self.body(w.position),b) { maximumPenetration=max(maximumPenetration,c.penetration) } }
        }
    }
}
