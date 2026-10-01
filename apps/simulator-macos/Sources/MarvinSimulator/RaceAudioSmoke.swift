import AppKit
import AVFoundation
import SimulationCore
import simd

extension AppController {
    func checkRaceAudio(at directory:URL)->Bool {
        do {
            try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
            let audio=try RaceAudio(resources:Bundle.main.resourceURL!,offline:true)
            defer { audio.stop() }
            let format=audio.engine.manualRenderingFormat
            let buffer=AVAudioPCMBuffer(pcmFormat:format,frameCapacity:800)!
            let file=try AVAudioFile(forWriting:directory.appendingPathComponent("race-audio-preview.wav"),settings:format.settings)
            let right=RaceAudio.spatial(source:SIMD2(4,0),listener:.zero,heading:0,range:24)
            let left=RaceAudio.spatial(source:SIMD2(-4,0),listener:.zero,heading:0,range:24)
            let far=RaceAudio.spatial(source:SIMD2(25,0),listener:.zero,heading:0,range:24)
            let rotated=RaceAudio.spatial(source:SIMD2(4,0),listener:.zero,heading:Double.pi,range:24)
            var passed=right.pan>0 && left.pan<0 && rotated.pan<0 && far.gain==0 && right.gain>far.gain
            var peak:Float=0,squares=0.0,samples=0,blockPeaks=[Float]()
            for character in RacePerformance.Character.allCases {
                var states=(0..<4).map { Simulation(dirtTrack:true,dirtStartPhase:Double($0)*Double.pi/2,character:character) }
                audio.stop()
                for frame in 0..<300 {
                    for i in 0..<4 { var input=DriveInput();input.throttle=Double(frame)/300;states[i].advance(input,dt:1.0/60) }
                    audio.update(states:states,lineup:[character,.r2d2,.bb8,.wallE],zones:[],heading:states[0].heading,storm:false,racing:true,dt:1.0/60)
                    guard try audio.engine.renderOffline(800,to:buffer) == .success else { throw CocoaError(.fileWriteUnknown) }
                    var block:Float=0
                    for ch in 0..<2 { for i in 0..<Int(buffer.frameLength) {
                        let value=buffer.floatChannelData![ch][i]
                        passed = passed && value.isFinite;peak=max(peak,abs(value));block=max(block,abs(value));squares+=Double(value*value);samples+=1
                    }}
                    blockPeaks.append(block);try file.write(from:buffer)
                }
            }
            let states=(0..<4).map { Simulation(dirtTrack:true,dirtStartPhase:Double($0)*Double.pi/2) }
            let zone=SpectatorSoundZone(position:SIMD2(states[0].x+3,states[0].z),people:40,stormPeople:1)
            for storm in [false,true] {
                audio.stop()
                for _ in 0..<600 {
                    audio.update(states:states,lineup:[.marvin,.r2d2,.bb8,.wallE],zones:[zone],heading:0,storm:storm,racing:true,dt:1.0/60)
                    guard try audio.engine.renderOffline(800,to:buffer) == .success else { throw CocoaError(.fileWriteUnknown) }
                    for ch in 0..<2 { for i in 0..<Int(buffer.frameLength) {
                        let value=buffer.floatChannelData![ch][i]
                        passed = passed && value.isFinite;peak=max(peak,abs(value));squares+=Double(value*value);samples+=1
                    }}
                    try file.write(from:buffer)
                }
                passed = passed && audio.lastMix[4].gain>0
            }
            audio.update(states:states,lineup:[.marvin,.r2d2,.bb8,.wallE],zones:[zone],heading:0,storm:false,racing:false,dt:1.0/60)
            passed = passed && !audio.active && audio.lastMix.allSatisfy{$0.gain==0} && peak>0.005 && peak<0.95
            // Exercise the real app lifecycle gate, including startup and restart.
            let savedMute=raceSoundMuted
            defer { raceSoundMuted=savedMute }
            raceSoundMuted=false
            startDirtTrack();raceAudio=audio
            updateRaceAudio(dt:1.0/60,advancing:true);passed = passed && !audio.active
            dirtIntro=nil;race.countDown(dt:3)
            updateRaceAudio(dt:1.0/60,advancing:true);passed = passed && audio.active
            updateRaceAudio(dt:1.0/60,advancing:false);passed = passed && !audio.active
            raceSoundMuted=true;updateRaceAudio(dt:1.0/60,advancing:true);passed = passed && !audio.active
            raceSoundMuted=false;updateRaceAudio(dt:1.0/60,advancing:true);passed = passed && audio.active
            race=DirtRace();race.countDown(dt:3)
            for i in 1...601 {
                let p=DirtCourse.point(Double(i)*Double.pi/100)
                race.advance(x:p.x,z:p.z,dt:0.1)
            }
            updateRaceAudio(dt:1.0/60,advancing:true);passed = passed && race.finished && !audio.active
            reset(nil);updateRaceAudio(dt:1.0/60,advancing:true);passed = passed && !audio.active
            showMainMenu(nil);passed = passed && !audio.active
            raceAudio=nil
            let report:[String:Any]=["passed":passed,"peak":peak,"rms":sqrt(squares/Double(max(1,samples))),"renderedSeconds":40,"voiceLimit":8,"spatialAndLifecycleChecks":passed,"robotBlockPeaks":blockPeaks]
            try JSONSerialization.data(withJSONObject:report,options:[.prettyPrinted,.sortedKeys]).write(to:directory.appendingPathComponent("audio.json"))
            return passed
        } catch { print("Audio check: \(error)");return false }
    }
}
