import SimulationCore
import simd

/// Speech follows nearby events. Steady driving never schedules periodic chatter.
struct RaceVoiceDirector {
    static let moods=["acknowledge","effort","startle","overtake","passed"]
    static let variantCount=6
    struct Observation { let position:SIMD2<Double>,speed:Double,contact:Bool; var progress:Double?=nil }
    struct Event { let robot:Int,mood:String,variant:Int }
    private struct Pending { let robot:Int,mood:String,priority:Int,expires:Double }
    private var elapsed=0.0,globalReady=0.4
    private var ready=[Double](repeating:0,count:4),passReady=[Double](repeating:0,count:4)
    private var previousContact=[Bool](repeating:false,count:4),accelerationArmed=[Bool](repeating:true,count:4)
    private var greeted=[Bool](repeating:false,count:4),side=[Int](repeating:0,count:4)
    private var pending:[Pending]=[],bags:[String:[Int]]=[:],last:[String:Int]=[:]
    private var random:UInt64
    init(seed:UInt64=UInt64.random(in:1...UInt64.max)) { random=seed }
    private mutating func next(_ n:Int)->Int {
        random=random &* 6364136223846793005 &+ 1442695040888963407
        return Int((random >> 32)%UInt64(n))
    }
    private mutating func variant(robot:Int,mood:String)->Int {
        let key="\(robot):\(mood)"
        if bags[key,default:[]].isEmpty {
            var bag=Array(0..<Self.variantCount)
            for i in stride(from:bag.count-1,through:1,by:-1) { bag.swapAt(i,next(i+1)) }
            if bag.last==last[key] { bag.swapAt(0,bag.count-1) }
            bags[key]=bag
        }
        let value=bags[key]!.removeLast();last[key]=value;return value
    }
    mutating func advance(states:[Simulation],progress:[Double]=[],dt:Double,canSpeak:Bool=true)->Event? {
        advance(observations:states.enumerated().map { i,s in
            Observation(position:SIMD2(s.x,s.z),speed:s.groundSpeed,contact:s.contacting,progress:progress.count==4 ? progress[i]:nil)
        },dt:dt,canSpeak:canSpeak)
    }
    mutating func advance(observations:[Observation],dt:Double,canSpeak:Bool=true)->Event? {
        guard observations.count==4,dt>0 else { return nil }
        elapsed+=min(dt,0.1)
        let listener=observations[0].position
        pending.removeAll{$0.expires<elapsed}
        for i in 0..<4 {
            let s=observations[i],near=simd_distance(s.position,listener)<12
            var mood:String?,priority=0
            if i>0,let a=s.progress,let b=observations[0].progress {
                // Continuous lap progress avoids false overtakes at the start/finish seam.
                // Hysteresis rejects repeated side changes while running side by side.
                let delta=a-b,newSide=delta>0.035 ? 1:delta < -0.035 ? -1:side[i]
                if side[i] != 0,newSide != side[i],near,elapsed>=passReady[i],s.speed>1,observations[0].speed>1 {
                    mood=newSide>0 ? "overtake":"passed";priority=3;passReady[i]=elapsed+18
                }
                side[i]=newSide
            }
            if s.contact && !previousContact[i] && s.speed>3 && mood==nil { mood="startle";priority=2 }
            if s.speed<2 { accelerationArmed[i]=true }
            if s.speed>5 && accelerationArmed[i] {
                accelerationArmed[i]=false
                // Acceleration is expressed by the drivetrain, not a recurring squeak.
            }
            if i==0,!greeted[i],s.speed>0.5 { greeted[i]=true;if mood==nil { mood="acknowledge" } }
            previousContact[i]=s.contact
            if let mood=mood,near,(priority==3 || elapsed>=ready[i]) {
                if !pending.contains(where:{$0.robot==i && $0.priority>priority}) {
                    pending.removeAll{$0.robot==i}
                    pending.append(Pending(robot:i,mood:mood,priority:priority,expires:elapsed+2.0))
                }
            }
        }
        guard canSpeak,elapsed>=globalReady else { return nil }
        pending.sort { $0.priority==$1.priority ? $0.expires<$1.expires:$0.priority>$1.priority }
        guard let index=pending.firstIndex(where:{simd_distance(observations[$0.robot].position,listener)<12 && ($0.priority==3 || elapsed>=ready[$0.robot])}) else { return nil }
        let event=pending.remove(at:index)
        ready[event.robot]=elapsed+22+Double(next(9));globalReady=elapsed+8
        return Event(robot:event.robot,mood:event.mood,variant:variant(robot:event.robot,mood:event.mood))
    }
}
