import AVFoundation
import SimulationCore
import simd

struct SpectatorSoundZone {
    var position:SIMD2<Double>
    var people:Int
    var stormPeople:Int
}

/// Eight preloaded stereo voices; AVAudioEngine renders on its audio thread.
/// The listener follows the player and camera orientation, never the high camera's altitude.
final class RaceAudio {
    struct Mix {
        var gain:Float=0,pan:Float=0,rate:Float=1
    }
    static func spatial(source:SIMD2<Double>,listener:SIMD2<Double>,heading:Double,range:Double)->Mix {
        let delta=source-listener,d=simd_length(delta)
        let edge=max(0,min(1,(range-d)/4))
        let gain=edge*edge*(3-2*edge)/(1+pow(d/5,2))
        let right=SIMD2(cos(heading),-sin(heading))
        return Mix(gain:Float(gain),pan:Float(max(-0.9,min(0.9,simd_dot(delta,right)/max(2,d)))))
    }
    private final class Voice {
        let player=AVAudioPlayerNode(),pitch=AVAudioUnitVarispeed()
        var mix=Mix()
    }
    let engine=AVAudioEngine()
    private let format=AVAudioFormat(standardFormatWithSampleRate:48000,channels:2)!
    private let voices=(0..<8).map{_ in Voice()}
    private var buffers:[AVAudioPCMBuffer]=[]
    private var currentLineup:[RacePerformance.Character]=[]
    private var currentStorm=false,playing=false
    private var retryAt=0.0
    private(set) var lastMix=[Mix](repeating:Mix(),count:8)
    var active:Bool { playing && engine.isRunning }
    init(resources:URL,offline:Bool=false) throws {
        for name in ["marvin","r2d2","bb8","wallE","crowd","sparse-crowd"] {
            let file=try AVAudioFile(forReading:resources.appendingPathComponent("Audio/\(name).wav"))
            let mono=AVAudioPCMBuffer(pcmFormat:file.processingFormat,frameCapacity:AVAudioFrameCount(file.length))!
            try file.read(into:mono)
            guard mono.format.sampleRate==48000,mono.frameLength>0 else { throw CocoaError(.fileReadCorruptFile) }
            let stereo=AVAudioPCMBuffer(pcmFormat:format,frameCapacity:mono.frameLength)!
            stereo.frameLength=mono.frameLength
            for channel in 0..<2 {
                stereo.floatChannelData![channel].update(from:mono.floatChannelData![0],count:Int(mono.frameLength))
            }
            buffers.append(stereo)
        }
        for voice in voices {
            engine.attach(voice.player);engine.attach(voice.pitch)
            engine.connect(voice.player,to:voice.pitch,format:format)
            engine.connect(voice.pitch,to:engine.mainMixerNode,format:format)
            voice.player.volume=0
        }
        engine.mainMixerNode.outputVolume=0.75
        if offline { try engine.enableManualRenderingMode(.offline,format:format,maximumFrameCount:1024) }
        engine.prepare()
    }
    func stop() {
        guard playing else { return }
        for voice in voices { voice.player.volume=0;voice.player.stop();voice.mix=Mix() }
        engine.pause();playing=false;lastMix=[Mix](repeating:Mix(),count:8)
    }
    func update(states:[Simulation],lineup:[RacePerformance.Character],zones:[SpectatorSoundZone],heading:Double,
                storm:Bool,racing:Bool,dt:Double) {
        guard racing,states.count==4,lineup.count==4 else { if playing { stop() };return }
        if currentLineup != lineup || currentStorm != storm { stop() }
        if !playing {
            currentLineup=lineup;currentStorm=storm
            for (i,voice) in voices.enumerated() {
                let index=i<4 ? lineup[i].rawValue:(storm ? 5:4)
                voice.player.scheduleBuffer(buffers[index],at:nil,options:.loops)
            }
            playing=true
        }
        if !engine.isRunning {
            let now=ProcessInfo.processInfo.systemUptime
            guard now>=retryAt else { return }
            do { try engine.start();for voice in voices { voice.player.play() } }
            catch { retryAt=now+2;NSLog("Race audio output unavailable: %@",String(describing:error));return }
        }
        let listener=SIMD2(states[0].x,states[0].z)
        var target=[Mix](repeating:Mix(),count:8)
        for i in 0..<4 {
            let s=states[i],motion=min(1,s.groundSpeed/10),turn=min(1,abs(s.angularVelocity)/4)
            target[i]=Self.spatial(source:SIMD2(s.x,s.z),listener:listener,heading:heading,range:24)
            target[i].gain *= Float((i==0 ? 0.22:0.17)*(0.10+0.78*motion+0.12*turn))
            target[i].rate=Float(0.72+motion*0.68+turn*0.08)
            if s.airborne { target[i].gain *= 0.6 }
        }
        let nearby=zones.filter{(storm ? $0.stormPeople:$0.people)>0}.sorted {
            simd_length_squared($0.position-listener)<simd_length_squared($1.position-listener)
        }.prefix(4)
        for (j,zone) in nearby.enumerated() {
            let count=storm ? zone.stormPeople:zone.people
            var mix=Self.spatial(source:zone.position,listener:listener,heading:heading,range:28)
            let passing=states.map{simd_distance(SIMD2($0.x,$0.z),zone.position)}.min() ?? 100
            mix.gain *= Float(min(1,sqrt(Double(count)/25))*(storm ? 0.055:0.11)*(0.55+0.45*max(0,1-passing/10)))
            mix.rate=1+Float(j)*0.013
            target[j+4]=mix
        }
        let blend=Float(1-exp(-min(0.1,max(0,dt))/0.12))
        for i in voices.indices {
            let voice=voices[i]
            voice.mix.gain += (target[i].gain-voice.mix.gain)*blend
            voice.mix.pan += (target[i].pan-voice.mix.pan)*blend
            voice.mix.rate += (target[i].rate-voice.mix.rate)*blend
            voice.player.volume=voice.mix.gain;voice.player.pan=voice.mix.pan;voice.pitch.rate=voice.mix.rate
            lastMix[i]=voice.mix
        }
    }
}

extension AppController {
    func updateRaceAudio(dt:Double,advancing:Bool) {
        let racing = !raceSoundMuted && inSandbox && isDirtTrack && advancing && dirtIntro==nil && race.countdown<=0 && !race.finished && !racePhysics.escape.active
        guard racing else { raceAudio?.stop();return }
        let front=world.camera.simdWorldFront
        let heading=atan2(Double(front.x),Double(front.z))
        raceAudio?.update(states:[simulation]+opponents.map{$0.simulation},lineup:lineup,
                          zones:dirtWorld.town.spectatorSoundZones,heading:heading,
                          storm:racePhysics.storm.enabled,racing:racing,dt:dt)
    }
}
