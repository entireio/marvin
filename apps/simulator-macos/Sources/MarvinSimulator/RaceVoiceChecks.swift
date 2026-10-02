import simd

/// Deterministic conversational regressions: rank crossings, hysteresis, no ambient spam,
/// non-repeating bags and audible range. Separate from subjective sound audition.
func checkRaceVoiceDirector()->[String:Bool] {
    func observations(_ delta:Double,speed:Double=4,contact:Bool=false,far:Bool=false)->[RaceVoiceDirector.Observation] {
        (0..<4).map { i in
            .init(position:SIMD2(i==1 && !far ? 2:Double(i)*30,0),speed:speed,contact:contact,
                  progress:20+(i==1 ? delta:Double(i)*2))
        }
    }
    var steady=RaceVoiceDirector(seed:1),steadyEvents=[RaceVoiceDirector.Event]()
    for _ in 0..<36000 {
        if let e=steady.advance(observations:observations(-0.1,far:true),dt:1.0/60) { steadyEvents.append(e) }
    }
    var checks=["tenMinutesNoPeriodicChatter":steadyEvents.count==1 && steadyEvents[0].robot==0]
    var director=RaceVoiceDirector(seed:2),events=[RaceVoiceDirector.Event]()
    for pass in 0..<14 {
        let delta=pass%2==0 ? -0.1:0.1
        for _ in 0..<1500 {
            if let e=director.advance(observations:observations(delta),dt:1.0/60) { events.append(e) }
        }
    }
    let overtakes=events.filter{$0.mood=="overtake"},passed=events.filter{$0.mood=="passed"}
    checks["bothOvertakeDirections"]=overtakes.count==7 && passed.count==6 && (overtakes+passed).allSatisfy{$0.robot==1}
    checks["sixVariantsBeforeRepeat"]=overtakes.count>=7 && passed.count>=6 && Set(overtakes.prefix(6).map{$0.variant}).count==6 && Set(passed.prefix(6).map{$0.variant}).count==6 && overtakes[5].variant != overtakes[6].variant
    var jitter=RaceVoiceDirector(seed:3),falsePasses=0
    for frame in 0..<600 {
        let delta=frame==0 ? -0.1:(frame%2==0 ? -0.02:0.02)
        if let e=jitter.advance(observations:observations(delta),dt:1.0/60),e.mood=="overtake" || e.mood=="passed" { falsePasses+=1 }
    }
    checks["sideBySideHysteresis"]=falsePasses==0
    var far=RaceVoiceDirector(seed:4),farSpeech=0
    for frame in 0..<1200 {
        if let e=far.advance(observations:observations(frame<600 ? -0.1:0.1,far:true),dt:1.0/60),e.robot != 0 { farSpeech+=1 }
    }
    checks["distantRobotsSilent"]=farSpeech==0
    for mood in ["acknowledge","startle"] {
        var director=RaceVoiceDirector(seed:5),events=[RaceVoiceDirector.Event]()
        for frame in 0..<180 {
            if let e=director.advance(observations:observations(-0.1,speed:frame<24 ? 0:7,contact:mood=="startle" && frame>=24,far:true),dt:1.0/60) { events.append(e) }
        }
        checks[mood]=events.count==1 && events[0].mood==mood
    }
    var busy=RaceVoiceDirector(seed:9),busyCount=0
    for frame in 0..<36000 {
        let speed=frame%240<60 ? 1.0:7.0
        if busy.advance(observations:observations(frame%600<300 ? -0.1:0.1,speed:speed,contact:frame%30==0),dt:1.0/60) != nil { busyCount+=1 }
    }
    checks["busyRaceSpeechBudget"]=busyCount<=76
    return checks
}
