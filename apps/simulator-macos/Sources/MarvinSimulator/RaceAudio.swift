import AVFoundation
import AudioToolbox
import SimulationCore
import simd

struct SpectatorSoundZone {
    var position:SIMD2<Double>
    var people:Int
    var stormPeople:Int
}
struct TownSoundZone {
    enum Kind:Int { case market,workshop,cantina }
    let position:SIMD2<Double>,kind:Kind
    var activity:Double=1
}

/// Fixed, predecoded voice budget. No file I/O or synthesis on the gameplay thread.
final class RaceAudio {
    struct Mix { var gain:Float=0,pan:Float=0,rate:Float=1,cutoff:Float=18000 }
    struct Actor {
        var position:SIMD2<Double>,speed:Double,wheelSpeed:Double,turn:Double
        var throttle:Double,boost:Bool,brake:Bool,airborne:Bool,contact:Bool,sand:Double
        var impactSerial=0,impactSpeed=0.0
        var wind=SIMD2<Double>.zero,courseWind=SIMD2<Double>.zero,shelter=1.0,stormBuild=0.0
        init(_ s:Simulation) {
            position=SIMD2(s.x,s.z);speed=s.groundSpeed
            impactSerial=s.impactSerial;impactSpeed=s.impactSpeed
            let cw=s.storm.wind(x:0,z:0);courseWind=SIMD2(cw.x,cw.z)
            let w=s.storm.wind(x:s.x,z:s.z);wind=SIMD2(w.x,w.z);shelter=s.windShelter;stormBuild=s.storm.accumulation
            wheelSpeed=(abs(s.leftSpeed)+abs(s.rightSpeed))/2;turn=abs(s.angularVelocity)
            throttle=abs(s.appliedDriveInput.throttle);boost=s.appliedDriveInput.boost
            brake=s.appliedDriveInput.isBraking;airborne = !s.hasDirtContact;contact=s.contacting
            let distance=DirtCourse.projection(x:s.x,z:s.z).distance
            sand=max(0,min(1,(distance-DirtCourse.width)/1.6))
        }
    }
    static let loopCount=30
    static func spatial(source:SIMD2<Double>,listener:SIMD2<Double>,heading:Double,range:Double)->Mix {
        let delta=source-listener,d=simd_length(delta)
        let edge=max(0,min(1,(range-d)/6))
        let gain=edge*edge*(3-2*edge)/(1+pow(d/6,2))
        let right=SIMD2(-cos(heading),sin(heading))
        return Mix(gain:Float(gain),pan:Float(max(-0.9,min(0.9,simd_dot(delta,right)/max(2,d)))),cutoff:Float(max(1400,16000/(1+d/10))))
    }
    private final class Voice {
        let player=AVAudioPlayerNode(),pitch=AVAudioUnitVarispeed(),eq=AVAudioUnitEQ(numberOfBands:1)
        var mix=Mix()
    }
    private final class Shot {
        let player=AVAudioPlayerNode()
        var robot = -1,remaining=0.0,gain:Float=0
        var speech=false
    }
    let engine=AVAudioEngine()
    private let format=AVAudioFormat(standardFormatWithSampleRate:48000,channels:2)!
    private let voices=(0..<loopCount).map{_ in Voice()}
    private let shots=(0..<20).map{_ in Shot()}
    private let limiter=AVAudioUnitEffect(audioComponentDescription:AudioComponentDescription(componentType:kAudioUnitType_Effect,componentSubType:kAudioUnitSubType_PeakLimiter,componentManufacturer:kAudioUnitManufacturer_Apple,componentFlags:0,componentFlagsMask:0))
    private var buffers:[String:AVAudioPCMBuffer]=[:]
    private let characters=["marvin","r2d2","bb8","wallE"]
    private var director=RaceVoiceDirector()
    private var playing=false,stormState=false,finishedState=false,crowdDeparted=false
    private var currentLineup:[RacePerformance.Character]=[]
    private var retryAt=0.0,clock=0.0
    private var previousBoost=[Bool](repeating:false,count:4),lastImpactSerial=[Int](repeating:0,count:4)
    private var impactReady=[Double](repeating:0,count:4)
    private var lastCountdown:Int?
    private(set) var expressionCount=0,boostOnCount=0,boostOffCount=0,impactCount=0
    private(set) var boostOnByRobot=[Int](repeating:0,count:4),boostOffByRobot=[Int](repeating:0,count:4)
    private(set) var lastMix=[Mix](repeating:Mix(),count:loopCount)
    var speakingCount:Int { shots.filter{$0.speech && $0.remaining>0}.count }
    var active:Bool { playing && engine.isRunning }
    init(resources:URL,offline:Bool=false) throws {
        var names=["crowd","sparse-crowd","finish-crowd","desert-wind","market","workshop","cantina","impact","countdown","go","finish","storm-gust","storm-grit"]
        for name in characters {
            names += [name,name+"-ground",name+"-high",name+"-boost",name+"-sand",name+"-boost-on",name+"-boost-off"]
            names += RaceVoiceDirector.moods.flatMap { mood in (0..<RaceVoiceDirector.variantCount).map { "\(name)-\(mood)-\($0)" } }
        }
        for name in names {
            let file=try AVAudioFile(forReading:resources.appendingPathComponent("Audio/\(name).wav"))
            let source=AVAudioPCMBuffer(pcmFormat:file.processingFormat,frameCapacity:AVAudioFrameCount(file.length))!
            try file.read(into:source)
            guard source.format.sampleRate==48000,source.frameLength>0 else { throw CocoaError(.fileReadCorruptFile) }
            let stereo=AVAudioPCMBuffer(pcmFormat:format,frameCapacity:source.frameLength)!
            stereo.frameLength=source.frameLength
            for channel in 0..<2 { stereo.floatChannelData![channel].update(from:source.floatChannelData![min(channel,Int(source.format.channelCount)-1)],count:Int(source.frameLength)) }
            buffers[name]=stereo
        }
        engine.attach(limiter)
        let bus=AVAudioMixerNode();engine.attach(bus)
        engine.connect(bus,to:limiter,format:format);engine.connect(limiter,to:engine.mainMixerNode,format:format)
        for voice in voices {
            engine.attach(voice.player);engine.attach(voice.pitch);engine.attach(voice.eq)
            engine.connect(voice.player,to:voice.pitch,format:format)
            engine.connect(voice.pitch,to:voice.eq,format:format);engine.connect(voice.eq,to:bus,format:format)
            voice.eq.bands[0].filterType = .lowPass;voice.eq.bands[0].bypass=false;voice.eq.bands[0].frequency=18000
            voice.player.volume=0
        }
        for shot in shots { engine.attach(shot.player);engine.connect(shot.player,to:bus,format:format);shot.player.volume=0 }
        engine.mainMixerNode.outputVolume=0.82
        if offline { try engine.enableManualRenderingMode(.offline,format:format,maximumFrameCount:1024) }
        engine.prepare()
    }
    func resetConversation() {
        stop();director=RaceVoiceDirector();crowdDeparted=false;clock=0;finishedState=false;lastCountdown=nil
        previousBoost=Array(repeating:false,count:4);lastImpactSerial=Array(repeating:0,count:4)
        impactReady=Array(repeating:0,count:4)
    }
    func stop() {
        for voice in voices { voice.player.volume=0;voice.player.stop();voice.mix=Mix() }
        for shot in shots { shot.player.stop();shot.remaining=0;shot.robot = -1 }
        engine.pause();playing=false;lastMix=Array(repeating:Mix(),count:Self.loopCount)
    }
    private func scheduleCrowd(storm:Bool,finished:Bool) {
        let buffer=buffers[storm ? "sparse-crowd":finished ? "finish-crowd":"crowd"]!
        for i in 8..<12 {
            voices[i].player.stop();voices[i].player.scheduleBuffer(buffer,at:nil,options:.loops)
            if engine.isRunning { voices[i].player.play() }
        }
    }
    private func start(lineup:[RacePerformance.Character],storm:Bool,finished:Bool) {
        currentLineup=lineup
        for i in voices.indices where !(8..<12).contains(i) {
            let name:String
            switch i {
            case 0..<4:name=characters[lineup[i].rawValue]
            case 4..<8:name=characters[lineup[i-4].rawValue]+"-ground"
            case 12..<16:name=characters[lineup[i-12].rawValue]+"-high"
            case 16..<20:name=characters[lineup[i-16].rawValue]+"-boost"
            case 20..<24:name=characters[lineup[i-20].rawValue]+"-sand"
            default:name=["desert-wind","market","workshop","cantina","storm-gust","storm-grit"][i-24]
            }
            voices[i].player.scheduleBuffer(buffers[name]!,at:nil,options:.loops)
        }
        scheduleCrowd(storm:storm,finished:finished);stormState=storm;playing=true
    }
    func update(states:[Simulation],lineup:[RacePerformance.Character],zones:[SpectatorSoundZone],heading:Double,
                storm:Bool,racing:Bool,dt:Double,finished:Bool=false,escaping:Bool=false,progress:[Double]=[],
                town:[TownSoundZone]=[],countdown:Double=0) {
        update(actors:states.map{Actor($0)},lineup:lineup,zones:zones,heading:heading,storm:storm,racing:racing,dt:dt,
               finished:finished,escaping:escaping,progress:progress,town:town,countdown:countdown)
    }
    func update(actors:[Actor],lineup:[RacePerformance.Character],zones:[SpectatorSoundZone],heading:Double,
                storm:Bool,racing:Bool,dt:Double,finished:Bool=false,escaping:Bool=false,progress:[Double]=[],
                town:[TownSoundZone]=[],countdown:Double=0,expressions:Bool=true,ambience:Bool=true) {
        guard racing,actors.count==4,lineup.count==4 else { if playing { stop() };return }
        let dt=min(0.1,max(0,dt));clock+=dt
        if currentLineup != lineup { stop() }
        if !playing { start(lineup:lineup,storm:storm,finished:finished) }
        if stormState != storm || finishedState != finished { scheduleCrowd(storm:storm,finished:finished);stormState=storm }
        if !engine.isRunning {
            guard ProcessInfo.processInfo.systemUptime>=retryAt else { return }
            do { try engine.start();for voice in voices { voice.player.play() } }
            catch { retryAt=ProcessInfo.processInfo.systemUptime+2;NSLog("Audio output unavailable: %@",String(describing:error));return }
        }
        let listener:SIMD2<Double> = finished ? .zero:actors[0].position
        for shot in shots where shot.remaining>0 {
            shot.remaining=max(0,shot.remaining-dt)
            if shot.remaining==0 { shot.player.stop() }
        }
        let count=Int(ceil(countdown))
        if count>0 && count != lastCountdown { play("countdown",robot:-1,gain:0.55) }
        if count==0,let previous=lastCountdown,previous>0 { play("go",robot:-1,gain:0.65) }
        lastCountdown=count
        if finished && !finishedState { play("finish",robot:-1,gain:0.6) }
        finishedState=finished
        if !finished && !escaping { crowdDeparted=false }
        if expressions && countdown<=0 && !finished && !escaping {
            let observations=actors.enumerated().map { i,s in RaceVoiceDirector.Observation(position:s.position,speed:s.speed,contact:s.contact,progress:progress.count==4 ? progress[i]:nil) }
            if let e=director.advance(observations:observations,dt:dt,canSpeak:speakingCount==0) {
                play("\(characters[lineup[e.robot].rawValue])-\(e.mood)-\(e.variant)",robot:e.robot,gain:e.robot==0 ? 0.28:0.24,speech:true)
            }
        }
        var target=Array(repeating:Mix(),count:Self.loopCount)
        for i in 0..<4 {
            let s=actors[i],motion=min(1,s.speed/12),rpm=min(1,max(s.wheelSpeed/12,s.turn/12))
            let powered=countdown<=0 && s.throttle>0.05 && !s.brake
            let boost=powered && s.boost
            let spatial=Self.spatial(source:s.position,listener:listener,heading:heading,range:36)
            let near=spatial.gain>0.01
            if boost != previousBoost[i],near {
                if play(characters[lineup[i].rawValue]+(boost ? "-boost-on":"-boost-off"),robot:i,gain:i==0 ? 0.55:0.22,critical:true) {
                    if boost { boostOnCount+=1;boostOnByRobot[i]+=1 } else { boostOffCount+=1;boostOffByRobot[i]+=1 }
                }
            }
            previousBoost[i]=boost
            if s.impactSerial != lastImpactSerial[i] && s.impactSpeed>0.65 && clock>=impactReady[i] && near {
                if play("impact",robot:i,gain:Float(min(0.65,s.impactSpeed/10))) { impactCount+=1 }
                impactReady[i]=clock+0.45
            }
            lastImpactSerial[i]=s.impactSerial
            let moving=min(1,max(s.speed,s.wheelSpeed)/0.6)
            let load=powered ? s.throttle:0.0
            let strength=(i==0 ? 1.0:0.7)*moving
            let high=min(1,max(0,(rpm-0.16)/0.84))
            for slot in [i,i+4,i+12,i+16,i+20] { target[slot]=spatial }
            target[i].gain *= Float(strength*(0.06+0.52*pow(rpm,1.05))*(1-0.32*high)*(0.76+0.24*load))
            target[i].rate=Float(0.66+rpm*0.72)
            target[i+12].gain *= Float(strength*0.70*pow(high,1.15)*(0.58+0.42*load))
            target[i+12].rate=Float(0.75+rpm*0.62)
            target[i+16].gain *= Float(boost ? (i==0 ? 0.62:0.42)*(s.airborne ? 0.75:1):0)
            target[i+16].rate=Float(0.85+rpm*0.35)
            let contact=s.airborne ? 0:motion
            target[i+4].gain *= Float((i==0 ? 0.22:0.14)*contact*(1-0.85*s.sand))
            target[i+4].rate=Float(0.65+motion*0.85)
            target[i+20].gain *= Float((i==0 ? 0.42:0.28)*contact*s.sand)
            target[i+20].rate=Float(0.75+motion*0.40)
            if countdown>0 { for slot in [i,i+4,i+12,i+16,i+20] { target[slot].gain=0 } }
        }
        let positions=actors.map{$0.position}
        func crowdDistance(_ z:SpectatorSoundZone)->Double { finished ? (positions.map{simd_distance($0,z.position)}.min() ?? 100):simd_distance(z.position,listener) }
        let nearby=zones.filter{(storm ? $0.stormPeople:$0.people)>0}.sorted{crowdDistance($0)<crowdDistance($1)}.prefix(4)
        if !crowdDeparted {
            for (j,zone) in nearby.enumerated() {
                var mix=Self.spatial(source:zone.position,listener:listener,heading:heading,range:32)
                if finished { mix.gain=positions.map{Self.spatial(source:zone.position,listener:$0,heading:heading,range:32).gain}.max() ?? 0 }
                mix.gain *= Float(min(1,sqrt(Double(storm ? zone.stormPeople:zone.people)/25))*(storm ? 0.14:finished ? 0.42:0.25))
                mix.rate=1+Float(j)*0.019;target[j+8]=mix
            }
            if escaping && finished && target[8..<12].allSatisfy({$0.gain<0.0001}) && lastMix[8..<12].allSatisfy({$0.gain<0.0001}) { crowdDeparted=true }
        }
        target[24]=Mix(gain:ambience ? 0.075:0,pan:Float(sin(clock*0.07)*0.25))
        if storm && ambience {
            let weather=actors[0],wind=finished ? weather.courseWind:weather.wind
            let windSpeed=simd_length(wind)
            let gust=max(0,min(1,(windSpeed-10)/15)),exposure=finished ? 1:max(0.15,min(1,weather.shelter))
            let build=0.65+0.35*weather.stormBuild
            let right=SIMD2(-cos(heading),sin(heading))
            let pan=Float(max(-0.65,min(0.65,simd_dot(wind,right)/max(1,windSpeed)*0.65)))
            target[28]=Mix(gain:Float((0.22+0.28*gust)*exposure*build),pan:pan,rate:Float(0.8+gust*0.28),cutoff:Float(900+1900*exposure))
            target[29]=Mix(gain:Float((0.12+0.20*gust)*exposure*build),pan:pan*0.8,rate:Float(0.9+gust*0.16),cutoff:Float(2500+7000*exposure))
        }
        // Mix each category into a stable voice, so nearest-zone reordering cannot pop or swap loops.
        for kind in [TownSoundZone.Kind.market,.workshop,.cantina] {
            var energy:Float=0,weightedPan:Float=0,cutoff:Float=0
            for zone in town where ambience && zone.kind==kind {
                let mix=Self.spatial(source:zone.position,listener:listener,heading:heading,range:kind == .workshop ? 26:32)
                let gain=mix.gain*Float(zone.activity)*(storm && kind != .cantina ? 0.12:1)
                energy+=gain*gain;weightedPan+=gain*gain*mix.pan;cutoff+=gain*gain*mix.cutoff
            }
            if energy>0 {
                target[25+kind.rawValue]=Mix(gain:min(1,sqrt(energy))*(kind == .workshop ? 0.40:0.48),pan:weightedPan/energy,rate:1,cutoff:cutoff/energy)
            }
        }
        for i in voices.indices {
            let v=voices[i],blend=Float(1-exp(-dt/(i>=24 ? 0.65:0.075)))
            v.mix.gain += (target[i].gain-v.mix.gain)*blend
            v.mix.pan += (target[i].pan-v.mix.pan)*blend
            v.mix.rate += (target[i].rate-v.mix.rate)*blend
            v.mix.cutoff += (target[i].cutoff-v.mix.cutoff)*blend
            v.player.volume=v.mix.gain;v.player.pan=v.mix.pan;v.pitch.rate=v.mix.rate;v.eq.bands[0].frequency=v.mix.cutoff
            lastMix[i]=v.mix
        }
        for shot in shots where shot.remaining>0 {
            let mix=shot.robot<0 ? Mix(gain:1):Self.spatial(source:actors[shot.robot].position,listener:listener,heading:heading,range:shot.speech ? 14:36)
            shot.player.volume=mix.gain*shot.gain;shot.player.pan=mix.pan
        }
    }
    @discardableResult private func play(_ name:String,robot:Int,gain:Float,speech:Bool=false,critical:Bool=false)->Bool {
        let pool=critical ? shots.suffix(16):shots.prefix(4)
        guard let buffer=buffers[name],let slot=pool.first(where:{$0.remaining<=0}) else { return false }
        slot.player.stop();slot.robot=robot;slot.gain=gain;slot.speech=speech;slot.player.volume=0
        slot.remaining=Double(buffer.frameLength)/48000
        slot.player.scheduleBuffer(buffer,at:nil);slot.player.play()
        if speech { expressionCount+=1 }
        return true
    }
}

extension AppController {
    func updateRaceAudio(dt:Double,advancing:Bool) {
        let audible = !raceSoundMuted && inSandbox && isDirtTrack && advancing && dirtIntro==nil
        guard audible else { raceAudio?.stop();return }
        let front=world.camera.simdWorldFront,heading=atan2(Double(front.x),Double(front.z))
        raceAudio?.update(states:[simulation]+opponents.map{$0.simulation},lineup:lineup,
            zones:dirtWorld.town.spectatorSoundZones,heading:heading,storm:racePhysics.storm.enabled,racing:true,dt:dt,
            finished:race.finished,escaping:racePhysics.escape.active,progress:([race]+opponents.map{$0.race}).map{$0.progress},
            town:dirtWorld.town.soundZones,countdown:race.countdown)
    }
}
