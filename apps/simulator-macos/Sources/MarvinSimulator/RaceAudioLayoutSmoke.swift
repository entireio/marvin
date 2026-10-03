import AppKit
import AVFoundation
import SimulationCore
import simd

extension AppController {
    /// Compare the actual assets and mixer output, including changing spatial
    /// parameters. This is signal equivalence, not a subjective audio-quality claim.
    func checkAudioMonoLayout(at directory:URL)->Bool {
        do {
            try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
            let reference=try RaceAudio(resources:Bundle.main.resourceURL!,offline:true,monoLoops:false,skipUnchangedParameters:false)
            let skipUnchanged=CommandLine.arguments.contains("--benchmark-audio-skip-unchanged")
            let useMono = !skipUnchanged && !CommandLine.arguments.contains("--benchmark-audio-layout-control")
            let candidate=try RaceAudio(resources:Bundle.main.resourceURL!,offline:true,monoLoops:useMono,skipUnchangedParameters:skipUnchanged)
            defer { reference.stop();candidate.stop() }
            guard candidate.monoLoopsEnabled==useMono,!reference.monoLoopsEnabled else { return false }
            let engines=[reference,candidate],format=reference.engine.manualRenderingFormat
            let buffers=engines.map { _ in AVAudioPCMBuffer(pcmFormat:format,frameCapacity:800)! }
            var rows:[[String:Any]]=[],passed=true
            for scenario in ["steady","speed-only","moving","snap","storm","sheltered-storm","restart"] {
                engines.forEach { $0.resetConversation() }
                let onBefore=engines.map{$0.boostOnCount},offBefore=engines.map{$0.boostOffCount}
                let files=try ["reference","candidate"].map { try AVAudioFile(forWriting:directory.appendingPathComponent(scenario+"-"+$0+".wav"),settings:format.settings) }
                var maximum=0.0,errorEnergy=0.0,signalEnergy=0.0,count=0,renderElapsed=[0.0,0.0],updateElapsed=[0.0,0.0]
                for frame in 0..<480 {
                    let time=Double(frame)/60,steady=scenario=="steady" || scenario=="restart",storm=scenario.contains("storm")
                    let fixedPan=steady || scenario=="speed-only"
                    if scenario=="restart",frame==240 { engines.forEach { $0.resetConversation() } }
                    let actors=(0..<4).map { i -> RaceAudio.Actor in
                        var actor=RaceAudio.Actor(Simulation())
                        actor.position=i==0 ? .zero:SIMD2(fixedPan ? Double(i)*2:sin(time*0.7+Double(i))*7,Double(i)*2)
                        actor.speed=steady ? 6:5+4*sin(time*0.8+Double(i));actor.wheelSpeed=actor.speed
                        actor.throttle=0.8;actor.turn=steady ? 0:sin(time)*2;actor.sand=storm ? 1:0.3
                        actor.wind=SIMD2(20,12);actor.courseWind=actor.wind;actor.stormBuild=0.9
                        actor.shelter=scenario=="sheltered-storm" ? 0.2+0.6*(0.5+0.5*sin(time)):1
                        actor.boost = !steady && frame>=180 && frame<300
                        return actor
                    }
                    let zones=[SpectatorSoundZone(position:SIMD2(-3,5),people:80,stormPeople:2)]
                    let town=[TownSoundZone(position:SIMD2(4,2),kind:.market),TownSoundZone(position:SIMD2(-4,2),kind:.workshop),TownSoundZone(position:SIMD2(2,-3),kind:.cantina)]
                    // Alternate order to reduce systematic CPU-cache bias.
                    for i in frame%2==0 ? [0,1]:[1,0] {
                        let updateStart=ProcessInfo.processInfo.systemUptime
                        engines[i].update(actors:actors,lineup:RacePerformance.Character.allCases,zones:zones,heading:fixedPan ? 0:(scenario=="snap" ? (frame/30)%2==0 ? -Double.pi/2:Double.pi/2:sin(time*0.4)),storm:storm,racing:true,dt:1.0/60,town:town,expressions:false)
                        let start=ProcessInfo.processInfo.systemUptime
                        if frame>=120 { updateElapsed[i] += start-updateStart }
                        guard try engines[i].engine.renderOffline(800,to:buffers[i]) == .success else { throw CocoaError(.fileWriteUnknown) }
                        if frame>=120 { renderElapsed[i] += ProcessInfo.processInfo.systemUptime-start }
                        try files[i].write(from:buffers[i])
                    }
                    do {
                        for ch in 0..<2 { for sample in 0..<800 {
                            let a=Double(buffers[0].floatChannelData![ch][sample]),b=Double(buffers[1].floatChannelData![ch][sample])
                            guard a.isFinite,b.isFinite else { throw CocoaError(.fileReadCorruptFile) }
                            let error=abs(a-b);maximum=max(maximum,error);errorEnergy+=error*error;signalEnergy+=a*a;count+=1
                        }}
                    }
                }
                let relativeRMS=sqrt(errorEnergy/max(signalEnergy,1e-30))
                let expectedEvents=scenario=="steady" || scenario=="restart" ? 0:4
                let eventsMatch=engines.indices.allSatisfy { engines[$0].boostOnCount-onBefore[$0]==expectedEvents && engines[$0].boostOffCount-offBefore[$0]==expectedEvents }
                let ok=maximum<0.001 && relativeRMS<0.001 && signalEnergy/Double(count)>1e-8 && eventsMatch
                passed = passed && ok
                let row:[String:Any]=["scenario":scenario,"passed":ok,"maximumSampleError":maximum,"rmsError":sqrt(errorEnergy/Double(count)),"relativeRMSError":relativeRMS,"referenceRenderElapsedSeconds":renderElapsed[0],"candidateRenderElapsedSeconds":renderElapsed[1],"referenceUpdateElapsedSeconds":updateElapsed[0],"candidateUpdateElapsedSeconds":updateElapsed[1],"referenceTotalElapsedSeconds":renderElapsed[0]+updateElapsed[0],"candidateTotalElapsedSeconds":renderElapsed[1]+updateElapsed[1],"measuredAudioSeconds":6,"comparedAudioSeconds":8,"expectedBoostEvents":expectedEvents,"boostEventsPassed":eventsMatch]
                rows.append(row);print("Audio layout: \(row)");fflush(stdout)
            }
            try JSONSerialization.data(withJSONObject:["passed":passed,"scenarios":rows,"monoLoopsEnabled":candidate.monoLoopsEnabled,"skipUnchangedParameters":skipUnchanged,"timingMethod":"monotonic elapsed update and offline render, excluding file writes; not thread CPU time"],options:[.sortedKeys,.prettyPrinted]).write(to:directory.appendingPathComponent("audio-layout-comparison.json"))
            return passed
        } catch { print(error);return false }
    }
}
