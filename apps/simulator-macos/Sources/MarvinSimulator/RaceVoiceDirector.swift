import SimulationCore
import simd

/// Event-based utterances, never a repeating voice track. No rendering/audio APIs.
struct RaceVoiceDirector {
    struct Observation { let position:SIMD2<Double>,speed:Double,contact:Bool }
    struct Event { let robot:Int,mood:String,variant:Int }
    private var elapsed=0.0,globalReady=0.4
    private var ready=[Double](repeating:0,count:4),serial=[Int](repeating:0,count:4)
    private var previousContact=[Bool](repeating:false,count:4),previousSpeed=[Double](repeating:0,count:4)
    mutating func advance(states:[Simulation],dt:Double)->Event? {
        advance(observations:states.map{Observation(position:SIMD2($0.x,$0.z),speed:$0.groundSpeed,contact:$0.contacting)},dt:dt)
    }
    mutating func advance(observations:[Observation],dt:Double)->Event? {
        guard observations.count==4,dt>0 else { return nil }
        elapsed+=min(dt,0.1)
        let listener=observations[0].position
        var result:Event?
        for i in 0..<4 {
            let s=observations[i],bump=s.contact && !previousContact[i]
            let accelerating=s.speed>5 && s.speed-previousSpeed[i]>0.05
            previousContact[i]=s.contact;previousSpeed[i]=s.speed
            guard result==nil,elapsed>=globalReady,elapsed>=ready[i],
                  simd_distance(s.position,listener)<16 else { continue }
            let first=serial[i]==0
            guard first || bump || accelerating || s.speed>1 else { continue }
            let mood=bump ? "startle":(accelerating ? "effort":"acknowledge")
            result=Event(robot:i,mood:mood,variant:serial[i]%3)
            serial[i]+=1;ready[i]=elapsed+9+Double((serial[i]*7+i*3)%6)
            globalReady=elapsed+2.0 // Leave space between phrases and other robots.
        }
        return result
    }
}
