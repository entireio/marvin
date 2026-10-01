import Foundation
import simd

/// Cooldown leads into continuous, physical town driving, never a camera movie.
public struct PostRaceEscape: Sendable {
    public private(set) var active=false
    public private(set) var routes:[[SIMD2<Double>]]=[]
    public private(set) var waypoint=[Int](repeating:-1,count:4)
    public private(set) var departed=[Bool](repeating:false,count:4)
    public private(set) var tours=[Int](repeating:0,count:4)
    public var complete:Bool { active && departed.allSatisfy{$0} }
    private var plans:[[SIMD2<Double>]]=[]
    private var released=0
    private var order=[0,1,2,3]
    private var pullouts=[SIMD2<Double>?](repeating:nil,count:4)
    private var pulloutUntil=[Double](repeating:0,count:4)
    private var yieldingTo=[Int?](repeating:nil,count:4)
    private var yieldDirection=[SIMD2<Double>](repeating:.zero,count:4)
    private var nextYieldAttempt=[Double](repeating:0,count:4)
    public private(set) var yields=[Int](repeating:0,count:4)
    private var planningAttempted=false
    private var elapsed=0.0
    private var stopped=[Double](repeating:0,count:4)
    private var backingUntil=[Double](repeating:0,count:4)
    public private(set) var reversals=[Int](repeating:0,count:4)
    private var rests=[Double](repeating:0,count:4)
    public init(routes:[[SIMD2<Double>]] = []) { self.routes=routes;plans=routes }
    public static func makeRoutes(city:CityCollisionWorld)->[[SIMD2<Double>]] {
        let apron=CityExit.point(0,8)
        // Distinct circuits pass close to the track, then visit outer streets.
        let visits:[[SIMD2<Double>]]=[
            [SIMD2(33,3),SIMD2(29,23),SIMD2(8,27),SIMD2(-18,24),SIMD2(-26,0),SIMD2(-15,-27),SIMD2(19,-26)],
            [SIMD2(43,7),SIMD2(36,28),SIMD2(12,30),SIMD2(-23,23),SIMD2(-29,-13),SIMD2(-8,-30),SIMD2(29,-21)],
            [SIMD2(29,14),SIMD2(19,27),SIMD2(-8,26),SIMD2(-27,10),SIMD2(-24,-20),SIMD2(3,-28),SIMD2(32,-20)],
            [SIMD2(39,3),SIMD2(32,30),SIMD2(1,32),SIMD2(-29,20),SIMD2(-31,-10),SIMD2(-14,-30),SIMD2(21,-29)]
        ]
        let planner=TownEscapeRoute(city:city,origin:.zero)
        var plans=[[SIMD2<Double>]]()
        for (i,goals) in visits.enumerated() {
            let lane=[-0.32,0.27,-0.12,0.38][i]
            var path=[CityExit.point(lane,-1.15),CityExit.point(lane*0.6,2.8),apron]
            var current=apron
            for goal in goals+[apron] {
                guard let leg=planner.route(from:current,to:goal,rounded:true,clearance:0.7,goalTolerance:10,avoiding:CityExit.posts+[CityGate().body(at:100 * .pi/180)]),leg.count>1 else { print("Town circuit \(i) unreachable: \(current) -> \(goal)");return [] }
                path += leg.dropFirst();current=leg.last!
            }
            // The last goal may have been snapped to a free cell. Close exactly.
            if simd_distance(current,apron)>0.01 { path.append(apron) }
            plans.append(path)
        }
        return plans
    }
    public mutating func advance(races:[DirtRace],states:[Simulation],city:CityCollisionWorld?,gateAngle:Double,dt:Double=1.0/240) {
        guard races.count==4,states.count==4 else { return }
        if !active {
            guard races.allSatisfy({$0.finished && $0.completedCooldownLap}),let city else { return }
            if routes.isEmpty && !planningAttempted { planningAttempted=true;routes=Self.makeRoutes(city:city);plans=routes }
            guard routes.count==4 else { return }
            order=states.indices.sorted { a,b in
                func remaining(_ i:Int)->Double {
                    let phase=DirtCourse.phase(x:states[i].x,z:states[i].z)
                    return (CityExit.phase-phase+4 * .pi).truncatingRemainder(dividingBy:2 * .pi)
                }
                return remaining(a)<remaining(b)
            }
            active=true
        }
        elapsed += dt
        guard gateAngle>99 * .pi/180 else { return }
        if released<3 && departed[order[released]] { released += 1 }
        for i in order.prefix(released+1) {
            rests[i]=max(0,rests[i]-dt)
            let p=SIMD2(states[i].x,states[i].z)
            if waypoint[i]<0 {
                let phase=DirtCourse.phase(x:p.x,z:p.y)
                let before=atan2(sin(CityExit.phase-phase),cos(CityExit.phase-phase))
                if before >= -0.025 && before<0.10+Double(i)*0.004 { waypoint[i]=0 }
            }
            guard waypoint[i]>=0,rests[i]==0 else { continue }
            let safelyWaiting=pullouts[i].map{simd_distance(p,$0)<0.25} ?? false
            if departed[i] && !safelyWaiting {
                stopped[i]=states[i].groundSpeed<0.06 ? stopped[i]+dt:0
                if stopped[i]>4+Double(3-i)*0.6 {
                    stopped[i]=0;backingUntil[i]=elapsed+2.5;pullouts[i]=nil;reversals[i] += 1
                }
                if backingUntil[i]>elapsed { continue }
            }
            if let other=yieldingTo[i],pullouts[i] != nil {
                let delta=SIMD2(states[other].x,states[other].z)-p
                // Stay in the pullout until the priority vehicle has actually
                // passed. A fixed short timer sends us back into its path.
                if simd_dot(delta,yieldDirection[i]) < -1.5 || simd_length(delta)>9 || elapsed>=pulloutUntil[i] {
                    pullouts[i]=nil;yieldingTo[i]=nil;stopped[i]=0
                }
            }
            if departed[i],pullouts[i]==nil,elapsed>=nextYieldAttempt[i],let city {
                let forward=SIMD2(sin(states[i].heading),cos(states[i].heading))
                let opposing=states.indices.first { j in
                    let delta=SIMD2(states[j].x,states[j].z)-p
                    let heading=SIMD2(sin(states[j].heading),cos(states[j].heading))
                    return j<i && departed[j] && simd_length(delta)<7 && simd_dot(delta,forward)>0.2
                        && abs(delta.x*forward.y-delta.y*forward.x)<1.3 && simd_dot(forward,heading)<(-0.3)
                }
                if let opposing {
                    nextYieldAttempt[i]=elapsed+2
                    let right=SIMD2(forward.y,-forward.x)
                    // Yield into checked street space before meeting nose to nose.
                    // No teleport or collision bypass: the normal drive input gets us there.
                    for offset in [right*1.3,right*1.3-forward*1.5,-right*1.3,-right*1.3-forward*1.5] {
                        let candidate=p+offset
                        guard states.indices.allSatisfy({ j in
                            j==i || (simd_distance(candidate,SIMD2(states[j].x,states[j].z))>1.1
                                && (pullouts[j].map{simd_distance(candidate,$0)>1.3} ?? true))
                        }) else { continue }
                        let free=(1...16).allSatisfy { step in
                            let q=p+offset*Double(step)/16
                            let projection=DirtCourse.projection(x:q.x,z:q.y)
                            guard projection.offset>0,projection.distance>DirtCourse.fenceOffset+0.8 else { return false }
                            let body=RobotCollisions.Body(position:SIMD3(q.x,DirtCourse.height(x:q.x,z:q.y),q.y),profile:.init(mass:18,halfWidth:0.65,halfDepth:0.65,height:1,round:true))
                            return (city.nearby(body)+CityExit.posts+[CityGate().body(at:gateAngle)]).allSatisfy{RobotCollisions.contact(body,$0)==nil}
                        }
                        if free {
                            pullouts[i]=candidate;pulloutUntil[i]=elapsed+18
                            yieldingTo[i]=opposing;yieldDirection[i]=forward
                            yields[i] += 1;break
                        }
                    }
                }
            }
            if pullouts[i] != nil { continue }
            let reach=waypoint[i]<3 ? 0.35:0.62
            while waypoint[i]<routes[i].count && simd_distance(p,routes[i][waypoint[i]])<reach {
                waypoint[i] += 1
                if waypoint[i]>=3 { departed[i]=true }
            }
            if waypoint[i]>=routes[i].count {
                tours[i] += 1
                routes[i]=plans[(i+tours[i]+tours[i]/3)%plans.count]
                waypoint[i]=3
                rests[i]=1.2+Double((i*7+tours[i]*3)%9)*0.35
            }
        }
    }
    public func input(for index:Int,states:[Simulation])->DriveInput? {
        guard active,index<states.count else { return nil }
        var input=DriveInput()
        if backingUntil[index]>elapsed { input.throttle = -0.20;return input }
        if rests[index]>0 { input.brake=true;return input }
        guard waypoint[index]>=0 else { return DirtOpponent.driveInput(for:states[index],cruising:true) }
        let state=states[index],p=SIMD2(state.x,state.z)
        var target=pullouts[index] ?? routes[index][waypoint[index]]
        if pullouts[index] != nil && simd_distance(p,target)<0.25 { input.brake=true;return input }
        // Look through closely spaced samples on rounded corners rather than
        // stopping and pivoting at every navigation-grid vertex.
        if waypoint[index]>=3 && pullouts[index]==nil {
            let a=routes[index][waypoint[index]-1],b=target,segment=b-a
            let along=max(0,min(1,simd_dot(p-a,segment)/max(0.0001,simd_length_squared(segment))))
            target=a+segment*along
            // Rejoin the local corridor after yielding. Aiming straight at a
            // distant vertex can cut through the building on the inside bend.
            var remaining=0.55+min(0.25,state.groundSpeed*0.1),j=waypoint[index]
            while j<routes[index].count {
                let next=routes[index][j],length=simd_distance(target,next)
                if length>remaining { target += (next-target)*(remaining/length);break }
                remaining -= length;target=next;j += 1
            }
        }
        let delta=target-p,distance=simd_length(delta),direction=delta/max(0.001,distance)
        let error=atan2(sin(atan2(delta.x,delta.y)-state.heading),cos(atan2(delta.x,delta.y)-state.heading))
        input.turn=max(-1,min(1,-error*1.8))
        let pace=[1.75,2.15,1.9,2.4][index]*(1+0.08*sin(elapsed*(0.13+Double(index)*0.017)+Double(index)*2))
        input.throttle=abs(error)<1.15 ? pace/6*max(0.18,cos(error)*cos(error)):0
        if pullouts[index] != nil { input.throttle=min(input.throttle,distance*0.3) }
        if waypoint[index]<3 { input.throttle=min(input.throttle,0.24+Double(index)*0.015) }
        for i in states.indices where i != index {
            let other=SIMD2(states[i].x,states[i].z)-p
            if simd_length(other)<1.35 && simd_dot(other,direction)>0.15 && abs(other.x*direction.y-other.y*direction.x)<0.65 {
                input.throttle=0;input.brake=true
            }
        }
        return input
    }
}
