import AppKit
import AVFoundation
import SceneKit
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
            let lineup=RacePerformance.Character.allCases
            var checks=checkRaceVoiceDirector(),measurements:[String:Any]=[:],peak:Float=0
            func render(_ file:AVAudioFile?=nil)throws->Double {
                var attempts=0
                while try audio.engine.renderOffline(800,to:buffer) != .success {
                    attempts+=1;if attempts>20 { throw CocoaError(.fileWriteUnknown) }
                }
                var squares=0.0
                for ch in 0..<2 { for i in 0..<Int(buffer.frameLength) {
                    let value=buffer.floatChannelData![ch][i]
                    guard value.isFinite else { throw CocoaError(.fileReadCorruptFile) }
                    peak=max(peak,abs(value));squares+=Double(value*value)
                }}
                try file?.write(from:buffer)
                return squares/Double(buffer.frameLength*2)
            }
            func actors(_ speed:Double,boost:Bool=false)->[RaceAudio.Actor] {
                (0..<4).map { i in
                    var a=RaceAudio.Actor(Simulation())
                    a.position=SIMD2(Double(i)*100,0);a.speed=i==0 ? speed:0;a.wheelSpeed=a.speed
                    a.wind=SIMD2(20,12);a.stormBuild=0.8
                    a.throttle=speed>0 ? 1:0;a.boost=i==0 && boost;a.sand=0;a.turn=0
                    return a
                }
            }
            let right=RaceAudio.spatial(source:SIMD2(4,0),listener:.zero,heading:0,range:36)
            let left=RaceAudio.spatial(source:SIMD2(-4,0),listener:.zero,heading:0,range:36)
            checks["spatial"]=right.pan<0 && left.pan>0 && RaceAudio.spatial(source:SIMD2(37,0),listener:.zero,heading:0,range:36).gain==0
            var cameraStereo=true
            for yaw in [0.0,1.2,Double.pi] {
                let camera=SCNNode();camera.position=SCNVector3(sin(yaw)*5,2,cos(yaw)*5)
                camera.look(at:SCNVector3Zero)
                let front=camera.simdWorldFront,right=camera.simdWorldRight
                let heading=atan2(Double(front.x),Double(front.z))
                let screenRight=SIMD2(Double(right.x),Double(right.z))*4
                cameraStereo = cameraStereo && RaceAudio.spatial(source:screenRight,listener:.zero,heading:heading,range:36).pan>0
                    && RaceAudio.spatial(source:-screenRight,listener:.zero,heading:heading,range:36).pan<0
            }
            checks["nativeCameraStereo"]=cameraStereo
            for character in lineup {
                let name=["marvin","r2d2","bb8","wallE"][character.rawValue]
                let order=[character]+lineup.filter{$0 != character}
                let file=try AVAudioFile(forWriting:directory.appendingPathComponent(name+"-drive-boost.wav"),settings:format.settings)
                var levels=[Double]()
                let onBefore=audio.boostOnCount,offBefore=audio.boostOffCount
                audio.resetConversation()
                // Each section 2 seconds: idle / slow / cruise / full / cruise / boost / release / sand / airborne / idle.
                for stage in 0..<10 {
                    var a=actors([0,2,6,12,6,6,6,6,6,0][stage],boost:stage==5)
                    if stage==7 { a[0].sand=1 }
                    if stage==8 { a[0].airborne=true }
                    var power=0.0
                    for frame in 0..<120 {
                        audio.update(actors:a,lineup:order,zones:[],heading:0,storm:false,racing:true,dt:1.0/60,expressions:false,ambience:false)
                        let p=try render(file);if frame>=60 { power+=p }
                    }
                    levels.append(10*log10(max(1e-12,power/60)))
                    if stage==8 { checks[name+"AirborneContactSilent"]=audio.lastMix[4].gain<0.0001 && audio.lastMix[20].gain<0.0001 && audio.lastMix[0].gain>0.1 }
                }
                measurements[name+"StageDBFS"]=levels
                checks[name+"SpeedDynamics"]=levels[0]<(-75) && levels[2]-levels[1]>6 && levels[3]-levels[2]>3 && levels[2]>(-30)
                checks[name+"MatchedSpeedBoost"]=levels[5]-levels[4]>2 && abs(levels[6]-levels[4])<1.5
                checks[name+"BoostEvents"]=audio.boostOnCount-onBefore==1 && audio.boostOffCount-offBefore==1
                // Actual Simulation commands, including powered stationary wheel load.
                var state=Simulation(dirtTrack:true,character:character),input=DriveInput();input.throttle=1;input.boost=true
                state.advance(input,dt:0.1)
                let telemetry=RaceAudio.Actor(state)
                checks[name+"ActualBoostTelemetry"]=telemetry.boost && telemetry.throttle==1 && telemetry.wheelSpeed>0
            }
            audio.resetConversation()
            let tapOn=audio.boostOnCount,tapOff=audio.boostOffCount
            for frame in 0..<90 {
                let a=actors(6,boost:frame<36 && (frame/6)%2==0)
                audio.update(actors:a,lineup:lineup,zones:[],heading:0,storm:false,racing:true,dt:1.0/60,expressions:false,ambience:false)
                _=try render()
            }
            checks["hundredMillisecondBoostTaps"]=audio.boostOnCount-tapOn==3 && audio.boostOffCount-tapOff==3
            var braking=actors(6,boost:true);braking[0].brake=true
            for _ in 0..<90 {
                audio.update(actors:braking,lineup:lineup,zones:[],heading:0,storm:false,racing:true,dt:1.0/60,expressions:false,ambience:false);_=try render()
            }
            checks["brakeOverridesBoost"]=audio.lastMix[16].gain<0.0001
            // Find real assisted braking commands on the actual course, then render their telemetry.
            var assistedState:Simulation?
            for phase in stride(from:0.0,to:Double.pi*2,by:0.2) {
                var s=Simulation(dirtTrack:true,dirtStartPhase:phase),drive=DriveInput()
                drive.throttle=1;drive.boost=true
                for _ in 0..<14 {
                    s.advance(drive,dt:0.1)
                    let assisted=DirtDrivingAssists().apply(drive,to:s)
                    if assisted.isBraking && !assisted.brake {
                        s.advance(assisted,dt:1.0/60);assistedState=s;break
                    }
                }
                if assistedState != nil { break }
            }
            if let s=assistedState {
                var a=actors(6,boost:true);a[0]=RaceAudio.Actor(s);a[0].position = .zero
                for _ in 0..<90 {
                    audio.update(actors:a,lineup:lineup,zones:[],heading:0,storm:false,racing:true,dt:1.0/60,expressions:false,ambience:false);_=try render()
                }
                checks["actualAssistedBrakeSilencesBoost"]=a[0].brake && a[0].boost && audio.lastMix[16].gain<0.0001
            } else { checks["actualAssistedBrakeSilencesBoost"]=false }
            audio.resetConversation()
            var crashPlayer=Simulation(dirtTrack:true),crashRace=DirtRace(),crashPhysics=DirtRacePhysics()
            crashRace.countDown(dt:3)
            var crashRivals=(1..<4).map{DirtOpponent(slot:DirtCourse.startingGrid[$0])}
            let obstaclePosition=SIMD3(crashPlayer.x+sin(crashPlayer.heading)*4,crashPlayer.groundY,crashPlayer.z+cos(crashPlayer.heading)*4)
            let obstacle=RobotCollisions.Body(position:obstaclePosition,heading:crashPlayer.heading,profile:.init(mass:1,halfWidth:3,halfDepth:0.15,height:2))
            let collisionWorld=CityCollisionWorld([obstacle])
            let crashFile=try AVAudioFile(forWriting:directory.appendingPathComponent("head-on-collision.wav"),settings:format.settings)
            let impactsBefore=audio.impactCount
            var impactVelocity=0.0,stoppedImpact=false
            for _ in 0..<240 {
                var input=DriveInput();input.throttle=1;input.boost=true
                crashPhysics.advance(input,player:&crashPlayer,race:&crashRace,opponents:&crashRivals,dt:1.0/60,raceDT:1.0/60,city:collisionWorld)
                impactVelocity=max(impactVelocity,crashPlayer.impactSpeed)
                if crashPlayer.impactSerial>0 && crashPlayer.groundSpeed<0.5 { stoppedImpact=true }
                audio.update(states:[crashPlayer]+crashRivals.map{$0.simulation},lineup:lineup,zones:[],heading:crashPlayer.heading,storm:false,racing:true,dt:1.0/60)
                _=try render(crashFile)
            }
            checks["headOnCollisionHasImpact"]=stoppedImpact && impactVelocity>2 && audio.impactCount>impactsBefore
            measurements["headOnImpactVelocityChange"]=impactVelocity
            // Native four-racer headroom and chatter under genuinely changing physics.
            audio.resetConversation()
            var player=Simulation(dirtTrack:true),race=DirtRace()
            race.countDown(dt:3)
            var rivals=(1..<4).map { DirtOpponent(slot:DirtCourse.startingGrid[$0]) }
            var physics=DirtRacePhysics()
            let raceFile=try AVAudioFile(forWriting:directory.appendingPathComponent("physics-race.wav"),settings:format.settings)
            let beforeSpeech=audio.expressionCount,beforeAI=audio.boostOnByRobot
            for frame in 0..<3600 {
                let input=DirtOpponent.driveInput(for:player)
                physics.advance(input,player:&player,race:&race,opponents:&rivals,dt:1.0/60,raceDT:1.0/60)
                let states=[player]+rivals.map{$0.simulation}
                audio.update(states:states,lineup:lineup,zones:[SpectatorSoundZone(position:SIMD2(player.x+3,player.z),people:100,stormPeople:2)],heading:player.heading,storm:false,racing:true,dt:1.0/60,progress:([race]+rivals.map{$0.race}).map{$0.progress})
                _=try render(raceFile)
                if frame%600==0 { print("Audio physics render \(frame/60)s");fflush(stdout) }
            }
            checks["realRaceVoiceBudget"]=audio.expressionCount-beforeSpeech<=8
            measurements["realRaceExpressions"]=audio.expressionCount-beforeSpeech
            measurements["realRaceBoostOnsets"]=audio.boostOnCount
            checks["aiBoostAudible"]=(1..<4).allSatisfy{audio.boostOnByRobot[$0]>beforeAI[$0]}
            measurements["aiBoostOnsets"]=(1..<4).map{audio.boostOnByRobot[$0]-beforeAI[$0]}
            // One category at a time, approach, idle, leave, storm, post-race.
            let cityFile=try AVAudioFile(forWriting:directory.appendingPathComponent("city-tour.wav"),settings:format.settings)
            for kind in [TownSoundZone.Kind.market,.workshop,.cantina] {
                audio.resetConversation();var levels=[Double]()
                for (index,distance) in [45.0,4,0,45].enumerated() {
                    let zone=TownSoundZone(position:SIMD2(distance,0),kind:kind)
                    var power=0.0
                    for frame in 0..<240 {
                        audio.update(actors:actors(0),lineup:lineup,zones:[],heading:0,storm:false,racing:true,dt:1.0/60,town:[zone],expressions:false)
                        let p=try render(cityFile);if frame>119 { power+=p }
                    }
                    levels.append(10*log10(max(1e-12,power/120)))
                    if index==2 { checks["city\(kind.rawValue)Audible"]=audio.lastMix[25+kind.rawValue].gain>0.35 }
                }
                measurements["city\(kind.rawValue)DBFS"]=levels
                checks["city\(kind.rawValue)Range"]=levels[2]-levels[0]>10 && levels[2]-levels[3]>10
            }
            // Actual authored tents and the entire racing lane: the old broad
            // workshop falloff let tools dominate even along the inner edge.
            let repairZones=dirtWorld.town.soundZones.filter{$0.infieldRepair}
            func repairGain(_ p:SIMD2<Double>,legacy:Bool=false)->Double {
                sqrt(repairZones.reduce(0.0) { sum,zone in
                    let mix=legacy ? RaceAudio.spatial(source:zone.position,listener:p,heading:0,range:26):RaceAudio.townSpatial(zone:zone,listener:p,heading:0)
                    return sum+pow(Double(mix.gain)*zone.activity*0.4,2)
                })
            }
            var lanePeak=0.0,oldLanePeak=0.0
            for sample in 0..<768 {
                for offset in [-DirtCourse.width,0,DirtCourse.width] {
                    let p=DirtCourse.point(Double(sample)*2 * .pi/768,offset:offset)
                    lanePeak=max(lanePeak,repairGain(SIMD2(p.x,p.z)))
                    oldLanePeak=max(oldLanePeak,repairGain(SIMD2(p.x,p.z),legacy:true))
                }
            }
            checks["repairQuietAcrossRacingLane"]=repairZones.count==2 && lanePeak<0.01 && lanePeak<oldLanePeak*0.06
            measurements["repairLanePeakBeforeAfter"]=[oldLanePeak,lanePeak]
            let repairFile=try AVAudioFile(forWriting:directory.appendingPathComponent("repair-approach.wav"),settings:format.settings)
            var tentLevels=[[Double]]()
            for tent in InfieldLayout.tentOrigins {
                let projection=DirtCourse.projection(x:tent.x,z:tent.y)
                let lane=DirtCourse.point(projection.phase)
                let start=SIMD2(lane.x,lane.z)
                let route=(0...4).map { start+(tent-start)*Double($0)/4 }
                let gains=route.map{repairGain($0)}
                tentLevels.append(gains)
                checks["repairTent\(tentLevels.count)Approach"]=zip(gains,gains.dropFirst()).allSatisfy{$0 <= $1} && gains.last!>0.12 && gains.last!>gains.first!*20
                audio.resetConversation()
                // Continuous approach then return, rendered through the real mixer.
                for frame in 0..<960 {
                    let t=Double(frame<480 ? frame:960-frame)/480
                    var a=actors(0);a[0].position=start+(tent-start)*t
                    audio.update(actors:a,lineup:lineup,zones:[],heading:0,storm:false,racing:true,dt:1.0/60,town:repairZones,expressions:false)
                    _=try render(repairFile)
                }
            }
            measurements["repairTentApproachGains"]=tentLevels
            audio.resetConversation()
            let close=SpectatorSoundZone(position:SIMD2(2,0),people:50,stormPeople:1)
            let far=SpectatorSoundZone(position:SIMD2(500,0),people:50,stormPeople:1)
            for _ in 0..<120 { audio.update(actors:actors(6),lineup:lineup,zones:[close],heading:0,storm:false,racing:true,dt:1.0/60,finished:true);_=try render() }
            checks["finishCheersAndMotors"]=audio.lastMix[8].gain>0.1 && audio.lastMix[0].gain>0.1
            for _ in 0..<240 { audio.update(actors:actors(6),lineup:lineup,zones:[far],heading:0,storm:false,racing:true,dt:1.0/60,finished:true,escaping:true);_=try render() }
            checks["departureKeepsWorldAlive"]=audio.active && audio.lastMix[8..<12].allSatisfy{$0.gain<0.0001} && audio.lastMix[0].gain>0.1
            for _ in 0..<120 { audio.update(actors:actors(6),lineup:lineup,zones:[close],heading:0,storm:false,racing:true,dt:1.0/60,finished:true,escaping:true);_=try render() }
            checks["departedStadiumStaysQuiet"]=audio.lastMix[8..<12].allSatisfy{$0.gain<0.0001}
            let audibleTown=TownSoundZone(position:SIMD2(2,0),kind:.workshop)
            var roaming=actors(0);roaming[0].position=SIMD2(200,0);roaming[0].courseWind=SIMD2(24,9);roaming[0].shelter=0.2
            for _ in 0..<180 {
                audio.update(actors:roaming,lineup:lineup,zones:[close],heading:0,storm:true,racing:true,dt:1.0/60,finished:true,escaping:true,town:[audibleTown]);_=try render()
            }
            checks["postraceWorldAndFixedStorm"]=audio.lastMix[26].gain>0.025 && audio.lastMix[28].gain>0.3 && audio.lastMix[8].gain<0.0001
            audio.resetConversation()
            for _ in 0..<120 { audio.update(actors:actors(6),lineup:lineup,zones:[close],heading:0,storm:true,racing:true,dt:1.0/60,town:[TownSoundZone(position:.zero,kind:.market)]);_=try render() }
            checks["stormReducesOutdoorActivity"]=audio.lastMix[25].gain<0.08 && audio.lastMix[28].gain>0.25 && audio.lastMix[29].gain>0.1
            let stormFile=try AVAudioFile(forWriting:directory.appendingPathComponent("sandstorm.wav"),settings:format.settings)
            var stormLevels=[Double]()
            audio.resetConversation()
            // Clear / onset / full gust / sheltered / driving / boosting / clear again.
            for stage in 0..<7 {
                var a=actors(stage==4 || stage==5 ? 6:0,boost:stage==5)
                a[0].wind=SIMD2(stage==1 ? 12:24,stage==1 ? 6:9)
                a[0].stormBuild=stage==1 ? 0.18:1;a[0].shelter=stage==3 ? 0.25:1
                var power=0.0
                for frame in 0..<240 {
                    audio.update(actors:a,lineup:lineup,zones:[],heading:0,storm:stage>0 && stage<6,racing:true,dt:1.0/60,expressions:false)
                    let value=try render(stormFile);if frame>=120 { power+=value }
                }
                stormLevels.append(10*log10(max(1e-12,power/120)))
            }
            checks["stormBuildAndShelter"]=stormLevels[2]-stormLevels[0]>10 && stormLevels[2]-stormLevels[3]>5 && stormLevels[2]>stormLevels[1]
            checks["stormBoostCutsThrough"]=stormLevels[5]-stormLevels[4]>2
            measurements["stormStageDBFS"]=stormLevels
            // Worst-case simultaneous boosted engines, stadium, town, impacts and expressions.
            var stress=actors(12,boost:true)
            for i in 0..<4 { stress[i]=stress[0];stress[i].position=SIMD2(Double(i)*0.4,0) }
            for frame in 0..<600 {
                for i in 0..<4 { stress[i].contact=frame%120==0;stress[i].impactSerial=frame/120+1;stress[i].impactSpeed=8 }
                audio.update(actors:stress,lineup:lineup,zones:Array(repeating:close,count:4),heading:0,storm:false,racing:true,dt:1.0/60,town:[TownSoundZone(position:.zero,kind:.market),TownSoundZone(position:.zero,kind:.workshop),TownSoundZone(position:.zero,kind:.cantina)])
                _=try render()
            }
            checks["headroom"]=peak<0.95 && peak>0.1
            let savedMute=raceSoundMuted
            defer { raceSoundMuted=savedMute }
            startDirtTrack();raceAudio=audio;raceSoundMuted=false
            updateRaceAudio(dt:1.0/60,advancing:true);checks["introSilent"] = !audio.active
            dirtIntro=nil
            updateRaceAudio(dt:1.0/60,advancing:true);checks["countdownActive"]=audio.active
            race.countDown(dt:3)
            updateRaceAudio(dt:1.0/60,advancing:false);checks["pauseSilent"] = !audio.active
            updateRaceAudio(dt:1.0/60,advancing:true);checks["resumeActive"]=audio.active
            raceSoundMuted=true;updateRaceAudio(dt:1.0/60,advancing:true);checks["muteSilent"] = !audio.active
            raceSoundMuted=false;updateRaceAudio(dt:1.0/60,advancing:true);checks["unmuteActive"]=audio.active
            reset(nil);updateRaceAudio(dt:1.0/60,advancing:true);checks["resetCountdownActive"] = audio.active && audio.lastMix[0..<8].allSatisfy{$0.gain<0.0001}
            showMainMenu(nil);checks["menuSilent"] = !audio.active;raceAudio=nil
            let passed=checks.values.allSatisfy{$0}
            let report:[String:Any]=["passed":passed,"checks":checks,"measurements":measurements,"peak":peak,"loopBudget":RaceAudio.loopCount,"oneShotBudget":20,"listeningAcceptance":"Not established by signal tests"]
            try JSONSerialization.data(withJSONObject:report,options:[.prettyPrinted,.sortedKeys]).write(to:directory.appendingPathComponent("audio.json"))
            print("Audio checks: \(checks.filter{!$0.value})")
            return passed
        } catch { print("Audio check: \(error)");return false }
    }
}
