import AVFoundation
import SimulationCore
import simd

struct SpectatorSoundZone {
    var position:SIMD2<Double>
    var people:Int
    var stormPeople:Int
}

/// Twelve loop voices and two bounded expressive voices; AVAudioEngine renders on its audio thread.
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
    private let voices=(0..<12).map{_ in Voice()}
    private var buffers:[AVAudioPCMBuffer]=[]
    private var expressions:[String:AVAudioPCMBuffer]=[:]
    private final class Utterance {
        let player=AVAudioPlayerNode()
        var robot = -1
        var remaining=0.0
    }
    private let utterances=(0..<2).map{_ in Utterance()}
    private var director=RaceVoiceDirector()
    private(set) var expressionCount=0
    var speakingCount:Int { utterances.filter{$0.remaining>0}.count }
    private var currentLineup:[RacePerformance.Character]=[]
    private var currentStorm=false,currentFinished=false,playing=false
    private var departureSilent=false
    private var retryAt=0.0
    private(set) var lastMix=[Mix](repeating:Mix(),count:12)
    var active:Bool { playing && engine.isRunning }
    init(resources:URL,offline:Bool=false) throws {
        let loops=["marvin","r2d2","bb8","wallE","marvin-ground","r2d2-ground","bb8-ground","wallE-ground","crowd","sparse-crowd","finish-crowd"]
        let names=loops+loops.prefix(4).flatMap { name in
            RaceVoiceDirector.moods.flatMap { mood in (0..<RaceVoiceDirector.variantCount).map { "\(name)-\(mood)-\($0)" } }
        }
        for name in names {
            let file=try AVAudioFile(forReading:resources.appendingPathComponent("Audio/\(name).wav"))
            let mono=AVAudioPCMBuffer(pcmFormat:file.processingFormat,frameCapacity:AVAudioFrameCount(file.length))!
            try file.read(into:mono)
            guard mono.format.sampleRate==48000,mono.frameLength>0 else { throw CocoaError(.fileReadCorruptFile) }
            let stereo=AVAudioPCMBuffer(pcmFormat:format,frameCapacity:mono.frameLength)!
            stereo.frameLength=mono.frameLength
            for channel in 0..<2 {
                stereo.floatChannelData![channel].update(from:mono.floatChannelData![0],count:Int(mono.frameLength))
            }
            if loops.contains(name) { buffers.append(stereo) } else { expressions[name]=stereo }
        }
        for voice in voices {
            engine.attach(voice.player);engine.attach(voice.pitch)
            engine.connect(voice.player,to:voice.pitch,format:format)
            engine.connect(voice.pitch,to:engine.mainMixerNode,format:format)
            voice.player.volume=0
        }
        for voice in utterances {
            engine.attach(voice.player);engine.connect(voice.player,to:engine.mainMixerNode,format:format)
            voice.player.volume=0
        }
        engine.mainMixerNode.outputVolume=0.65
        if offline { try engine.enableManualRenderingMode(.offline,format:format,maximumFrameCount:1024) }
        engine.prepare()
    }
    func resetConversation() { stop();director=RaceVoiceDirector();departureSilent=false }
    func stop() {
        guard playing else { return }
        for voice in voices { voice.player.volume=0;voice.player.stop();voice.mix=Mix() }
        for voice in utterances { voice.player.stop();voice.remaining=0;voice.robot = -1 }
        engine.pause();playing=false;lastMix=[Mix](repeating:Mix(),count:12)
    }
    func update(states:[Simulation],lineup:[RacePerformance.Character],zones:[SpectatorSoundZone],heading:Double,
                storm:Bool,racing:Bool,dt:Double,finished:Bool=false,escaping:Bool=false,progress:[Double]=[]) {
        guard racing,states.count==4,lineup.count==4 else { if playing { stop() };return }
        if !finished && !escaping { departureSilent=false }
        guard !departureSilent else { return }
        if currentLineup != lineup || currentStorm != storm || currentFinished != finished { stop() }
        if !playing {
            currentLineup=lineup;currentStorm=storm;currentFinished=finished
            for (i,voice) in voices.enumerated() {
                let index=i<8 ? lineup[i%4].rawValue+(i<4 ? 0:4):(storm ? 9:finished ? 10:8)
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
        if !finished && !escaping { updateExpressions(states:states,lineup:lineup,listener:listener,heading:heading,dt:dt,progress:progress) }
        var target=[Mix](repeating:Mix(),count:12)
        for i in 0..<4 {
            let s=states[i],motion=min(1,s.groundSpeed/10),turn=min(1,abs(s.angularVelocity)/4)
            target[i]=Self.spatial(source:SIMD2(s.x,s.z),listener:listener,heading:heading,range:24)
            target[i+4]=target[i]
            target[i].gain *= Float((i==0 ? 0.18:0.14)*max(motion,turn*0.45))
            target[i].rate=Float(0.82+motion*0.50+turn*0.06)
            target[i+4].gain *= Float((i==0 ? 0.13:0.10)*motion*(s.airborne ? 0:1))
            target[i+4].rate=Float(0.6+motion*0.9)
            if s.airborne { target[i].gain *= 0.6 }
            if finished || escaping { target[i].gain=0;target[i+4].gain=0 }
        }
        let positions=states.map{SIMD2($0.x,$0.z)}
        func crowdDistance(_ zone:SpectatorSoundZone)->Double {
            finished ? (positions.map{simd_distance($0,zone.position)}.min() ?? 100):simd_distance(zone.position,listener)
        }
        let nearby=zones.filter{(storm ? $0.stormPeople:$0.people)>0}.sorted {
            crowdDistance($0)<crowdDistance($1)
        }.prefix(4)
        for (j,zone) in nearby.enumerated() {
            let count=storm ? zone.stormPeople:zone.people
            var mix=Self.spatial(source:zone.position,listener:finished ? .zero:listener,heading:heading,range:28)
            let passing=positions.map{simd_distance($0,zone.position)}.min() ?? 100
            // Overhead outro shows all four racers. Keep cheers until the LAST racer leaves earshot.
            if finished { mix.gain=positions.map{Self.spatial(source:zone.position,listener:$0,heading:heading,range:28).gain}.max() ?? 0 }
            mix.gain *= Float(min(1,sqrt(Double(count)/25))*(storm ? 0.055:0.11)*(0.55+0.45*max(0,1-passing/10)))
            mix.rate=1+Float(j)*0.013
            if finished { mix.gain *= storm ? 1.5:1.8 }
            target[j+8]=mix
        }
        // Once all racers have left earshot, roaming near town must not restart the stadium.
        if escaping && target[8..<12].allSatisfy({$0.gain<0.0001}) && lastMix[8..<12].allSatisfy({$0.gain<0.0001}) {
            stop();departureSilent=true;return
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
    private func updateExpressions(states:[Simulation],lineup:[RacePerformance.Character],listener:SIMD2<Double>,heading:Double,dt:Double,progress:[Double]) {
        for voice in utterances where voice.remaining>0 {
            voice.remaining=max(0,voice.remaining-dt)
            if voice.remaining==0 { voice.player.stop();voice.robot = -1 }
        }
        if let event=director.advance(states:states,progress:progress,dt:dt,canSpeak:utterances.contains{$0.remaining==0}),let slot=utterances.first(where:{$0.remaining==0}) {
            playExpression(character:lineup[event.robot],mood:event.mood,variant:event.variant,robot:event.robot,slot:slot)
        }
        for voice in utterances where voice.remaining>0 {
            let s=states[voice.robot]
            let mix=Self.spatial(source:SIMD2(s.x,s.z),listener:listener,heading:heading,range:24)
            let blend=Float(1-exp(-max(0,dt)/0.06))
            voice.player.volume += (mix.gain*(voice.robot==0 ? 0.42:0.30)-voice.player.volume)*blend
            voice.player.pan += (mix.pan-voice.player.pan)*blend
        }
    }
    private func playExpression(character:RacePerformance.Character,mood:String,variant:Int,robot:Int,slot:Utterance) {
        let name=["marvin","r2d2","bb8","wallE"][character.rawValue]
        guard let buffer=expressions["\(name)-\(mood)-\(variant)"] else { return }
        slot.player.stop();slot.player.volume=0;slot.player.pan=0;slot.robot=robot
        slot.remaining=Double(buffer.frameLength)/buffer.format.sampleRate
        slot.player.scheduleBuffer(buffer,at:nil);slot.player.play();expressionCount+=1
    }
}

extension AppController {
    func updateRaceAudio(dt:Double,advancing:Bool) {
        let racing = !raceSoundMuted && inSandbox && isDirtTrack && advancing && dirtIntro==nil && race.countdown<=0
        guard racing else { raceAudio?.stop();return }
        let front=world.camera.simdWorldFront
        let heading=atan2(Double(front.x),Double(front.z))
        raceAudio?.update(states:[simulation]+opponents.map{$0.simulation},lineup:lineup,
                          zones:dirtWorld.town.spectatorSoundZones,heading:heading,
                          storm:racePhysics.storm.enabled,racing:racing,dt:dt,finished:race.finished,escaping:racePhysics.escape.active,
                          progress:([race]+opponents.map{$0.race}).map{$0.progress})
    }
}
