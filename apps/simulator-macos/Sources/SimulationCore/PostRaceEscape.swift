import Foundation
import simd

/// Finish times stay frozen while racers complete a real cooldown lap. A single
/// robot owns the narrow gate approach; town routes then diverge naturally.
public struct PostRaceEscape: Sendable {
    public private(set) var active=false
    public private(set) var routes:[[SIMD2<Double>]]=[]
    public private(set) var waypoint=[Int](repeating:-1,count:4)
    public private(set) var departed=[Bool](repeating:false,count:4)
    public var complete:Bool { active && departed.allSatisfy{$0} }
    private var released=0
    private var planningAttempted=false
    public init(routes:[[SIMD2<Double>]] = []) { self.routes=routes }
    public static func makeRoutes(city:CityCollisionWorld)->[[SIMD2<Double>]] {
        let apron=CityExit.point(0,8)
        let destinations=[SIMD2<Double>(36,28),SIMD2(43,7),SIMD2(29,-21),SIMD2(33,3)]
        var plans:[[SIMD2<Double>]]=[]
        for destination in destinations {
            guard let route=TownEscapeRoute(city:city,origin:apron).route(from:apron,to:destination) else { return [] }
            plans.append([CityExit.point(0,-1.15),CityExit.point(0,2.8),apron]+route.dropFirst())
        }
        return plans
    }
    public mutating func advance(races:[DirtRace],states:[Simulation],city:CityCollisionWorld?,gateAngle:Double) {
        guard races.count==4,states.count==4 else { return }
        if !active {
            guard races.allSatisfy({$0.finished && $0.completedCooldownLap}),let city else { return }
            if routes.isEmpty && !planningAttempted { planningAttempted=true;routes=Self.makeRoutes(city:city) }
            guard routes.count==4 else { return }
            active=true
        }
        guard gateAngle>99 * .pi/180 else { return }
        if released<3 && waypoint[released]>=3 { released += 1 }
        for i in 0...released where !departed[i] {
            let p=SIMD2(states[i].x,states[i].z)
            if waypoint[i]<0 {
                let phase=DirtCourse.phase(x:p.x,z:p.y)
                let before=atan2(sin(CityExit.phase-phase),cos(CityExit.phase-phase))
                if before >= -0.025 && before<0.10 { waypoint[i]=0 }
            }
            if waypoint[i]>=0 && simd_distance(p,routes[i][waypoint[i]])<0.24 {
                waypoint[i] += 1
                if waypoint[i]>=routes[i].count { departed[i]=true }
            }
        }
    }
    public func input(for index:Int,states:[Simulation])->DriveInput? {
        guard active,index<states.count else { return nil }
        var input=DriveInput()
        if departed[index] { if index==0 && complete { return nil };input.brake=true;return input }
        guard waypoint[index]>=0 else { return DirtOpponent.driveInput(for:states[index],cruising:true) }
        let state=states[index],p=SIMD2(state.x,state.z),delta=routes[index][waypoint[index]]-p
        let distance=simd_length(delta),direction=delta/max(0.001,distance)
        let error=atan2(sin(atan2(delta.x,delta.y)-state.heading),cos(atan2(delta.x,delta.y)-state.heading))
        input.turn=max(-1,min(1,-error*2.5))
        input.throttle=abs(error)<0.23 ? min(0.48,distance*0.65):0
        for i in states.indices where i != index {
            let other=SIMD2(states[i].x,states[i].z)-p
            if simd_length(other)<1.25 && simd_dot(other,direction)>0.2 && abs(other.x*direction.y-other.y*direction.x)<0.65 {
                input.throttle=0;input.brake=true
            }
        }
        return input
    }
}
