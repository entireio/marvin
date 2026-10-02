import AppKit
import SceneKit
import SimulationCore
import simd

final class TownDoorway {
    let center:SIMD2<Double>, outward:SIMD2<Double>, yaw:Double, leaf:SCNNode
    var opening=0.0,hold=0.0
    var outside:SIMD2<Double> { center+outward*0.52 }
    var inside:SIMD2<Double> { center-outward*0.68 }
    var body:RobotCollisions.Body {
        let slide=opening*opening*(3-2*opening)*0.94
        let p=center+SIMD2(outward.y,-outward.x)*slide-outward*0.06
        return .init(position:SIMD3(p.x,0.01,p.y),heading:yaw,profile:.init(mass:100,halfWidth:0.44,halfDepth:0.035,height:1.28))
    }
    init(center:SIMD2<Double>,yaw:Double,root:SCNNode,variant:Int=0) {
        self.center=center;self.yaw=yaw;outward=SIMD2(sin(yaw),cos(yaw))
        let geometry=SCNBox(width:0.88,height:1.28,length:0.07,chamferRadius:0.015)
        let material=SCNMaterial();material.lightingModel = .physicallyBased
        let colors:[UInt32]=[0x72634f,0x65716b,0x846654,0x574b40,0x74716a],color=colors[variant%5]
        material.diffuse.contents=NSColor(calibratedRed:Double((color>>16)&255)/255,green:Double((color>>8)&255)/255,blue:Double(color&255)/255,alpha:1);material.roughness.contents=0.8
        geometry.materials=[material];leaf=SCNNode(geometry:geometry)
        let seam=SCNMaterial();seam.diffuse.contents=NSColor(calibratedWhite:0.18,alpha:1);seam.roughness.contents=0.8
        for k in 0..<(variant%3+1) {
            let rib=SCNBox(width:0.018,height:1.14,length:0.012,chamferRadius:0)
            rib.materials=[seam];let n=SCNNode(geometry:rib)
            n.position=SCNVector3(-0.27+Double(k)*0.23,0,0.041);leaf.addChildNode(n)
        }
        leaf.name="Sliding resident doorway";root.addChildNode(leaf);place()
    }
    func place() { let p=body.position;leaf.position=SCNVector3(p.x,p.y+0.64,p.z);leaf.eulerAngles.y=CGFloat(yaw) }
    func update(dt:Double,requested:Bool) {
        if requested { hold=1.5 } else { hold=max(0,hold-dt) }
        opening=max(0,min(1,opening+(hold>0 ? dt:-dt)*1.25));place()
    }
}

/// Small bounded pedestrian population. Cached routes, physical swept footsteps,
/// distance-driven gait, independent schedules, and real doorway thresholds.
final class TownResidents {
    final class Walker {
        let node:SCNNode,index:Int,initialHome:Int,speed:Double,materials:[SCNMaterial],feet:CitizenMotion.FootPlacement
        let restingSole:Float
        var home:Int,destination:Int,path:[SIMD2<Double>]=[],waypoint=0
        var position:SIMD2<Double>,heading=0.0,wait:Double,indoors=true,distance=0.0,blend=0.0
        var visits=0,blocked=0.0,nextAttempt=0.0,firstEntry = -1.0
        var pending=0.0,lastSeen = -1.0,wasDetailed=true
        var settlingInside=false
        var yieldPoint:SIMD2<Double>?,yieldUntil=0.0
        var reserved=Set<SIMD2<Int>>()
        init(node:SCNNode,index:Int,home:Int,position:SIMD2<Double>) {
            self.node=node;self.index=index;self.home=home;initialHome=home;destination=home;self.position=position
            speed=0.52+Double(index%5)*0.045;wait=Double(index%9)*1.7
            materials=node.geometry?.materials ?? [];feet=CitizenMotion.FootPlacement(node.geometry!)
            restingSole=feet.minimum(cycle:0,blend:0)
        }
    }
    let doors:[TownDoorway],staticWorld:CityCollisionWorld
    private(set) var walkers:[Walker]=[]
    private var routes:[Int:[(Int,[SIMD2<Double>])]]=[:]
    private var storm=false,clock=0.0
    private var reservationCells:[String:Set<SIMD2<Int>>]=[:]
    private func cells(_ path:[SIMD2<Double>])->Set<SIMD2<Int>> {
        var cells=Set<SIMD2<Int>>()
        for (a,b) in zip(path,path.dropFirst()) {
            let steps=max(1,Int(ceil(simd_distance(a,b)/0.3)))
            for step in 0...steps {
                let p=a+(b-a)*Double(step)/Double(steps),cell=SIMD2(Int(floor(p.x*2)),Int(floor(p.y*2)))
                for dx in -1...1 { for dz in -1...1 { cells.insert(cell &+ SIMD2(dx,dz)) } }
            }
        }
        return cells
    }
    private(set) var entries=0,exits=0,maximumPenetration=0.0
    private(set) var navigationUpdates=0,coarseUpdates=0,poseUpdates=0
    var updateStatistics:[String:Int] { ["navigationUpdates":navigationUpdates,"coarseUpdates":coarseUpdates,"poseUpdates":poseUpdates] }
    var bodies:[RobotCollisions.Body] { walkers.filter{!$0.node.isHidden}.map{body($0.position)}+doors.map{$0.body} }
    var visible:Int { walkers.filter{!$0.node.isHidden}.count }
    var connections:Int { routes.values.reduce(0){$0+$1.count} }
    init(doors:[TownDoorway],city:CityCollisionWorld,crowd:TownCrowd,root:SCNNode,count:Int) {
        self.doors=doors;staticWorld=city
        let planner=TownEscapeRoute(city:city,origin:.zero)
        for i in doors.indices {
            let candidates=doors.indices.filter{$0 != i}.sorted{simd_distance(doors[i].outside,doors[$0].outside)<simd_distance(doors[i].outside,doors[$1].outside)}
            for j in candidates {
                guard routes[i,default:[]].count<2 else { break }
                if let path=planner.route(from:doors[i].outside,to:doors[j].outside,rounded:true,clearance:0.24,goalTolerance:0.3,cellSize:0.25),path.count>1,
                   simd_distance(path.last!,doors[j].outside)<0.4 {
                    let path=path+[doors[j].outside]
                    routes[i,default:[]].append((j,path))
                    reservationCells["\(i):\(j)"]=cells([doors[i].inside]+path+[doors[j].inside])
                }
            }
        }
        let homes=routes.keys.filter{!(routes[$0] ?? []).isEmpty}.sorted()
        guard homes.count>1 else { print("Resident navigation: no connected doors");return }
        for i in 0..<min(count,homes.count) {
            let home=homes[i%homes.count],p=doors[home].inside
            // Use short tunics for walking; seated and long-robed figures stay in the stands.
            let modelIndex=6000+(i%2)+(i/2%2)*2+i/4*12
            guard let node=crowd.add(x:p.x,y:0,z:p.y,yaw:0,index:modelIndex,seated:false,animated:true) else { continue }
            node.name="Walking resident \(i)";node.castsShadow=true;node.isHidden=true;root.addChildNode(node)
            walkers.append(Walker(node:node,index:i,home:home,position:p))
        }
        print("Resident navigation: \(walkers.count) walkers, \(doors.count) doors, \(connections) routes")
    }
    private func body(_ p:SIMD2<Double>)->RobotCollisions.Body {
        .init(position:SIMD3(p.x,0,p.y),profile:.init(mass:70,halfWidth:0.17,halfDepth:0.17,height:1.05,round:true))
    }
    func setStorm(_ value:Bool) { storm=value;reset() }
    func reset() {
        clock=0;entries=0;exits=0;maximumPenetration=0
        navigationUpdates=0;coarseUpdates=0;poseUpdates=0
        for w in walkers {
            w.home=w.initialHome;w.position=doors[w.home].inside;w.indoors=true;w.wait=Double(w.index%9)*1.7
            w.path=[];w.settlingInside=false;w.firstEntry = -1;w.reserved=[];w.nextAttempt=0;w.waypoint=0;w.node.isHidden=true;w.distance=0;w.visits=0;w.blocked=0
            w.yieldPoint=nil;w.yieldUntil=0;w.pending=0;w.lastSeen = -1;w.wasDetailed=true
        }
        for door in doors { door.opening=0;door.hold=0;door.place() }
    }
    func update(dt:Double,robots:[RobotCollisions.Body],pedestrians:[RobotCollisions.Body]=[],visible:((SCNNode)->Bool)?=nil) {
        guard dt>0 else { return }
        let dt=min(dt,0.05);clock += dt
        for (i,door) in doors.enumerated() {
            let request=walkers.contains { w in
                if storm && w.index%11 != 0 { return false }
                return (w.indoors && !w.settlingInside && w.home==i && w.wait<1.2 && !w.reserved.isEmpty)
                    || (!w.indoors && simd_distance(w.position,door.center)<1.6)
            } || robots.contains{simd_distance(SIMD2($0.position.x,$0.position.z),door.center)<1.1}
            door.update(dt:dt,requested:request)
        }
        for w in walkers {
            guard !storm || w.index%11==0 else { continue }
            w.pending += dt
            if !w.node.isHidden && (visible?(w.node) ?? true) { w.lastSeen=clock }
            // Grace prevents rate oscillation at a camera edge. Collision-critical
            // residents stay responsive even outside the camera frustum.
            let detailed=clock-w.lastSeen<0.35
            let interactive=(robots+pedestrians).contains{simd_distance(SIMD2($0.position.x,$0.position.z),w.position)<4}
                || (!w.node.isHidden && doors.contains{simd_distance($0.center,w.position)<1.6})
                || (!w.node.isHidden && walkers.contains{$0 !== w && !$0.node.isHidden && simd_distance($0.position,w.position)<1.2})
            guard detailed || interactive || w.pending>=0.1-1e-8 else { continue }
            let dt=w.pending;w.pending=0
            navigationUpdates += 1
            if !detailed && !interactive { coarseUpdates += 1 }
            if w.wasDetailed && !detailed {
                for m in w.materials { m.setValue(Float(0),forKey:"walkBlend") }
            }
            w.wasDetailed=detailed
            if w.indoors {
                w.wait=max(0,w.wait-dt)
                if w.settlingInside {
                    if doors[w.home].opening<0.02 { w.node.isHidden=true;w.settlingInside=false;w.reserved=[] }
                    continue
                }
                guard w.wait==0,let choices=routes[w.home],!choices.isEmpty else { continue }
                if w.reserved.isEmpty {
                    guard clock>=w.nextAttempt,doors[w.home].opening<0.02 else { continue }
                    w.nextAttempt=clock+0.5
                    let route=choices[(w.visits+w.index)%choices.count]
                    let cells=reservationCells["\(w.home):\(route.0)"] ?? []
                    guard walkers.allSatisfy({$0 === w || $0.reserved.isDisjoint(with:cells)}) else { continue }
                    w.reserved=cells;w.destination=route.0
                    w.path=[doors[w.home].outside]+route.1.dropFirst()+[doors[w.destination].inside]
                    // Reveal behind a closed door, never pop into an open doorway.
                    w.heading=doors[w.home].yaw;w.blend=0
                    w.node.position=SCNVector3(w.position.x,0.01-Double(w.restingSole),w.position.y)
                    w.node.eulerAngles.y=CGFloat(w.heading);w.node.isHidden=false
                    for m in w.materials { m.setValue(Float(0),forKey:"walkBlend") }
                }
                guard doors[w.home].opening>0.97,
                      walkers.allSatisfy({$0 === w || $0.node.isHidden || simd_distance($0.position,doors[w.home].center)>1.5}) else { continue }
                w.waypoint=0;w.indoors=false;w.node.isHidden=false;exits += 1
            }
            while w.waypoint<w.path.count && simd_distance(w.position,w.path[w.waypoint])<0.13 { w.waypoint += 1 }
            if w.waypoint==w.path.count {
                w.home=w.destination;w.visits += 1;entries += 1;w.indoors=true;w.settlingInside=true
                if w.firstEntry<0 { w.firstEntry=clock }
                for m in w.materials { m.setValue(Float(0),forKey:"walkBlend") }
                w.node.position.y=CGFloat(0.01-Double(w.restingSole))
                w.wait=8+Double((w.index*7+w.visits*13)%24);w.blend=0;continue
            }
            var target=w.path[w.waypoint]
            if w.waypoint>0 {
                let a=w.path[w.waypoint-1],segment=target-a
                let t=max(0,min(1,simd_dot(w.position-a,segment)/max(0.0001,simd_length_squared(segment))))
                let closest=a+segment*t,remaining=simd_distance(closest,target)
                if remaining>0.3 { target=closest+simd_normalize(segment)*0.3 }
            }
            // Step out of an approaching robot's lane, but only through swept,
            // unoccupied ground. Keep that space until the robot has passed.
            let approaching=robots.filter { robot in
                let d=w.position-SIMD2(robot.position.x,robot.position.z)
                return simd_length(d)<2.4 && simd_dot(d,SIMD2(sin(robot.heading),cos(robot.heading))) > -0.2
            }
            if !approaching.isEmpty { w.yieldUntil=clock+1.2 }
            if w.yieldPoint==nil,let robot=approaching.min(by:{simd_distance(SIMD2($0.position.x,$0.position.z),w.position)<simd_distance(SIMD2($1.position.x,$1.position.z),w.position)}) {
                let side=SIMD2(cos(robot.heading),-sin(robot.heading))
                let obstacles=staticWorld.nearby(.init(position:SIMD3(w.position.x,0,w.position.y),profile:.init(mass:70,halfWidth:1.5,halfDepth:1.5,height:1.05)))+doors.map{$0.body}+robots+pedestrians
                outer: for width in [0.7,1.0] { for sign in [-1.0,1.0] {
                    let candidate=w.position+side*(width*sign)
                    let lane=abs(simd_dot(candidate-SIMD2(robot.position.x,robot.position.z),side))
                    guard lane>robot.profile.halfWidth+0.3 else { continue }
                    let free=(1...20).allSatisfy { step in
                        let p=w.position+(candidate-w.position)*Double(step)/20
                        return obstacles.allSatisfy{RobotCollisions.contact(body(p),$0)==nil}
                            && walkers.allSatisfy{$0 === w || $0.node.isHidden || simd_distance(p,$0.position)>0.38}
                    }
                    if free { w.yieldPoint=candidate;break outer }
                }}
            }
            if let point=w.yieldPoint {
                if clock>w.yieldUntil { w.yieldPoint=nil }
                else { target=point }
            }
            let delta=target-w.position,direction=delta/max(0.001,simd_length(delta))
            let steering=direction
            let robotNear=robots.contains { robot in
                let d=SIMD2(robot.position.x,robot.position.z)-w.position
                return simd_length(d)<1.2 && simd_dot(d,direction)>0
            }
            var moved=0.0
            let heading=atan2(steering.x,steering.y)
            let angle=atan2(sin(heading-w.heading),cos(heading-w.heading))
            w.heading += max(-dt*3,min(dt*3,angle))
            let forward=SIMD2(sin(w.heading),cos(w.heading))
            let step=w.speed*dt*max(0,cos(angle))*(robotNear && w.yieldPoint==nil ? 0:1)
            let next=w.position+forward*min(step,simd_length(delta))
            let probe=body(next)
            let solids=staticWorld.nearby(probe)+doors.map{$0.body}+robots+pedestrians
            // Coarse updates still sweep the whole step; thin scenery cannot
            // disappear between the old and new positions.
            let subdivisions=max(1,Int(ceil(simd_distance(next,w.position)/0.015)))
            let free=(1...subdivisions).allSatisfy { i in
                let p=w.position+(next-w.position)*Double(i)/Double(subdivisions)
                return solids.allSatisfy{RobotCollisions.contact(body(p),$0)==nil}
                    && walkers.allSatisfy{$0 === w || $0.node.isHidden || simd_distance(p,$0.position)>0.35}
            }
            if free { moved=simd_distance(next,w.position);w.position=next }
            w.blocked=moved<0.0001 ? w.blocked+dt:0
            w.distance += moved;w.blend += ((moved>0.0001 ? 1.0:0)-w.blend)*min(1,dt*10)
            let cycle=Float(w.distance/0.24 * .pi)
            let nearDoor=doors.first{simd_distance(w.position,$0.center)<1}
            let threshold=nearDoor.map{max(0,min(1,0.5-simd_dot(w.position-$0.center,$0.outward)*3))} ?? 0
            let floor = -0.016+threshold*0.026
            let y=floor-Double(detailed ? w.feet.minimum(cycle:cycle,blend:Float(w.blend)):w.restingSole)
            w.node.position=SCNVector3(w.position.x,y,w.position.y);w.node.eulerAngles.y=CGFloat(w.heading)
            if detailed {
                poseUpdates += 1
                for m in w.materials {
                    m.setValue(Float(clock),forKey:"crowdTime");m.setValue(Float(w.distance/0.24 * .pi),forKey:"walkCycle")
                    m.setValue(Float(w.blend),forKey:"walkBlend")
                }
            }
            for obstacle in solids { if let c=RobotCollisions.contact(body(w.position),obstacle) { maximumPenetration=max(maximumPenetration,c.penetration) } }
        }
    }
}
