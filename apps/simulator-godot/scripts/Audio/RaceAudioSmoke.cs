using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Godot;
using Marvin.Core;
using static Marvin.Core.Swift;
using static Marvin.RaceVoiceChecks;

namespace Marvin;

public static class RaceAudioSmoke
{
    /// macOS `--audio-smoke-test DIR`: 61 signal checks through the offline mixer, evidence WAVs and audio.json.
    /// App.swift runs it on the AppController at the 20th main-menu frame; so does this mode.
    [GameMode("--audio-smoke-test")]
    public static async Task Run(string dir, SceneTree tree)
    {
        var app = new AppController();
        tree.Root.AddChild(app);
        for (int frame = 0; frame < 20; frame++) await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        var passed = app.checkRaceAudio(dir);
        tree.Quit(passed ? 0 : 1);
    }
}

public partial class AppController
{
    public bool checkRaceAudio(string directory)
    {
        try
        {
            directory = AVAudioFile.GlobalPath(directory);
            Directory.CreateDirectory(directory);
            var audio = new RaceAudio(resources: "res://assets", offline: true);
            try
            {
                var format = audio.engine.manualRenderingFormat;
                var buffer = new AVAudioPCMBuffer(pcmFormat: format, frameCapacity: 800);
                var lineup = RacePerformance.CharacterAllCases;
                var checks = checkRaceVoiceDirector(); var measurements = new Dictionary<string, object>(); float peak = 0;
                double render(AVAudioFile file = null)
                {
                    int attempts = 0;
                    while (audio.engine.renderOffline(800, to: buffer) != AVAudioEngineManualRenderingStatus.success)
                    {
                        attempts += 1; if (attempts > 20) throw new IOException("renderOffline failed"); // CocoaError(.fileWriteUnknown)
                    }
                    double squares = 0.0;
                    for (int ch = 0; ch < 2; ch++)
                    {
                        for (int i = 0; i < (int)buffer.frameLength; i++)
                        {
                            float value = buffer.floatChannelData[ch][i];
                            if (!float.IsFinite(value)) throw new InvalidDataException("non-finite sample"); // CocoaError(.fileReadCorruptFile)
                            peak = max(peak, abs(value)); squares += (double)(value * value);
                        }
                    }
                    file?.write(from: buffer);
                    return squares / (double)(buffer.frameLength * 2);
                }
                RaceAudio.Actor[] actors(double speed, bool boost = false) =>
                    Enumerable.Range(0, 4).Select(i =>
                    {
                        var a = new RaceAudio.Actor(new Simulation());
                        a.position = new Double2((double)i * 100, 0); a.speed = i == 0 ? speed : 0; a.wheelSpeed = a.speed;
                        a.wind = new Double2(20, 12); a.stormBuild = 0.8;
                        a.throttle = speed > 0 ? 1 : 0; a.boost = i == 0 && boost; a.sand = 0; a.turn = 0;
                        return a;
                    }).ToArray();
                var right = RaceAudio.spatial(source: new Double2(4, 0), listener: Double2.zero, heading: 0, range: 36);
                var left = RaceAudio.spatial(source: new Double2(-4, 0), listener: Double2.zero, heading: 0, range: 36);
                checks["spatial"] = right.pan < 0 && left.pan > 0 && RaceAudio.spatial(source: new Double2(37, 0), listener: Double2.zero, heading: 0, range: 36).gain == 0;
                var cameraStereo = true;
                foreach (var yaw in new[] { 0.0, 1.2, Math.PI })
                {
                    var camera = new SCNNode(); camera.position = new SCNVector3(sin(yaw) * 5, 2, cos(yaw) * 5);
                    camera.look(at: SCNVector3Zero);
                    var front = SimdBridge.Get(camera.simdWorldFront); var cameraRight = SimdBridge.Get(camera.simdWorldRight);
                    var heading = atan2((double)(float)front.x, (double)(float)front.z);
                    var screenRight = new Double2((double)(float)cameraRight.x, (double)(float)cameraRight.z) * 4;
                    cameraStereo = cameraStereo && RaceAudio.spatial(source: screenRight, listener: Double2.zero, heading: heading, range: 36).pan > 0
                        && RaceAudio.spatial(source: -screenRight, listener: Double2.zero, heading: heading, range: 36).pan < 0;
                    camera.QueueFree(); // PORT: Swift releases the node by ARC.
                }
                checks["nativeCameraStereo"] = cameraStereo;
                foreach (var character in lineup)
                {
                    var name = new[] { "marvin", "r2d2", "bb8", "wallE" }[(int)character];
                    var order = new[] { character }.Concat(lineup.Where(c => c != character)).ToArray();
                    var file = new AVAudioFile(forWriting: Path.Combine(directory, name + "-drive-boost.wav"), settings: format.settings);
                    var levels = new List<double>();
                    int onBefore = audio.boostOnCount, offBefore = audio.boostOffCount;
                    audio.resetConversation();
                    // Each section 2 seconds: idle / slow / cruise / full / cruise / boost / release / sand / airborne / idle.
                    for (int stage = 0; stage < 10; stage++)
                    {
                        var a = actors(new double[] { 0, 2, 6, 12, 6, 6, 6, 6, 6, 0 }[stage], boost: stage == 5);
                        if (stage == 7) a[0].sand = 1;
                        if (stage == 8) a[0].airborne = true;
                        double power = 0.0;
                        for (int frame = 0; frame < 120; frame++)
                        {
                            audio.update(actors: a, lineup: order, zones: Array.Empty<SpectatorSoundZone>(), heading: 0, storm: false, racing: true, dt: 1.0 / 60, expressions: false, ambience: false);
                            var p = render(file); if (frame >= 60) power += p;
                        }
                        levels.Add(10 * Math.Log10(max(1e-12, power / 60)));
                        if (stage == 8) checks[name + "AirborneContactSilent"] = audio.lastMix[4].gain < 0.0001 && audio.lastMix[20].gain < 0.0001 && audio.lastMix[0].gain > 0.1;
                    }
                    measurements[name + "StageDBFS"] = levels.ToArray();
                    checks[name + "SpeedDynamics"] = levels[0] < (-75) && levels[2] - levels[1] > 6 && levels[3] - levels[2] > 3 && levels[2] > (-30);
                    checks[name + "MatchedSpeedBoost"] = levels[5] - levels[4] > 2 && abs(levels[6] - levels[4]) < 1.5;
                    checks[name + "BoostEvents"] = audio.boostOnCount - onBefore == 1 && audio.boostOffCount - offBefore == 1;
                    // Actual Simulation commands, including powered stationary wheel load.
                    var state = new Simulation(dirtTrack: true, character: character); var input = new DriveInput(); input.throttle = 1; input.boost = true;
                    state.advance(input, dt: 0.1);
                    var telemetry = new RaceAudio.Actor(state);
                    checks[name + "ActualBoostTelemetry"] = telemetry.boost && telemetry.throttle == 1 && telemetry.wheelSpeed > 0;
                }
                audio.resetConversation();
                int tapOn = audio.boostOnCount, tapOff = audio.boostOffCount;
                for (int frame = 0; frame < 90; frame++)
                {
                    var a = actors(6, boost: frame < 36 && (frame / 6) % 2 == 0);
                    audio.update(actors: a, lineup: lineup, zones: Array.Empty<SpectatorSoundZone>(), heading: 0, storm: false, racing: true, dt: 1.0 / 60, expressions: false, ambience: false);
                    _ = render();
                }
                checks["hundredMillisecondBoostTaps"] = audio.boostOnCount - tapOn == 3 && audio.boostOffCount - tapOff == 3;
                var braking = actors(6, boost: true); braking[0].brake = true;
                for (int k = 0; k < 90; k++)
                {
                    audio.update(actors: braking, lineup: lineup, zones: Array.Empty<SpectatorSoundZone>(), heading: 0, storm: false, racing: true, dt: 1.0 / 60, expressions: false, ambience: false); _ = render();
                }
                checks["brakeOverridesBoost"] = audio.lastMix[16].gain < 0.0001;
                // Find real assisted braking commands on the actual course, then render their telemetry.
                Simulation? assistedState = null;
                foreach (var phase in strideTo(from: 0.0, to: Math.PI * 2, by: 0.2))
                {
                    var s = new Simulation(dirtTrack: true, dirtStartPhase: phase); var drive = new DriveInput();
                    drive.throttle = 1; drive.boost = true;
                    for (int k = 0; k < 14; k++)
                    {
                        s.advance(drive, dt: 0.1);
                        var assisted = new DirtDrivingAssists().apply(drive, s);
                        if (assisted.isBraking && !assisted.brake)
                        {
                            s.advance(assisted, dt: 1.0 / 60); assistedState = s; break;
                        }
                    }
                    if (assistedState != null) break;
                }
                if (assistedState is Simulation assistedSimulation)
                {
                    var a = actors(6, boost: true); a[0] = new RaceAudio.Actor(assistedSimulation); a[0].position = Double2.zero;
                    for (int k = 0; k < 90; k++)
                    {
                        audio.update(actors: a, lineup: lineup, zones: Array.Empty<SpectatorSoundZone>(), heading: 0, storm: false, racing: true, dt: 1.0 / 60, expressions: false, ambience: false); _ = render();
                    }
                    checks["actualAssistedBrakeSilencesBoost"] = a[0].brake && a[0].boost && audio.lastMix[16].gain < 0.0001;
                }
                else { checks["actualAssistedBrakeSilencesBoost"] = false; }
                audio.resetConversation();
                var crashPlayer = new Simulation(dirtTrack: true); var crashRace = new DirtRace(); var crashPhysics = new DirtRacePhysics();
                crashRace.countDown(dt: 3);
                var crashRivals = Enumerable.Range(1, 3).Select(i => new DirtOpponent(slot: DirtCourse.startingGrid[i])).ToArray();
                var obstaclePosition = new Double3(crashPlayer.x + sin(crashPlayer.heading) * 4, crashPlayer.groundY, crashPlayer.z + cos(crashPlayer.heading) * 4);
                var obstacle = new RobotCollisions.Body(position: obstaclePosition, heading: crashPlayer.heading, profile: new RobotCollisions.Profile(mass: 1, halfWidth: 3, halfDepth: 0.15, height: 2));
                var collisionWorld = new CityCollisionWorld(new[] { obstacle });
                var crashFile = new AVAudioFile(forWriting: Path.Combine(directory, "head-on-collision.wav"), settings: format.settings);
                var impactsBefore = audio.impactCount;
                double impactVelocity = 0.0; bool stoppedImpact = false;
                for (int k = 0; k < 240; k++)
                {
                    var input = new DriveInput(); input.throttle = 1; input.boost = true;
                    crashPhysics.advance(input, ref crashPlayer, ref crashRace, crashRivals, dt: 1.0 / 60, raceDT: 1.0 / 60, city: collisionWorld);
                    impactVelocity = max(impactVelocity, crashPlayer.impactSpeed);
                    if (crashPlayer.impactSerial > 0 && crashPlayer.groundSpeed < 0.5) stoppedImpact = true;
                    audio.update(states: new[] { crashPlayer }.Concat(crashRivals.Select(r => r.simulation)).ToArray(), lineup: lineup, zones: Array.Empty<SpectatorSoundZone>(), heading: crashPlayer.heading, storm: false, racing: true, dt: 1.0 / 60);
                    _ = render(crashFile);
                }
                checks["headOnCollisionHasImpact"] = stoppedImpact && impactVelocity > 2 && audio.impactCount > impactsBefore;
                measurements["headOnImpactVelocityChange"] = impactVelocity;
                // Native four-racer headroom and chatter under genuinely changing physics.
                audio.resetConversation();
                var player = new Simulation(dirtTrack: true); var race = new DirtRace();
                race.countDown(dt: 3);
                var rivals = Enumerable.Range(1, 3).Select(i => new DirtOpponent(slot: DirtCourse.startingGrid[i])).ToArray();
                var physics = new DirtRacePhysics();
                var raceFile = new AVAudioFile(forWriting: Path.Combine(directory, "physics-race.wav"), settings: format.settings);
                var beforeSpeech = audio.expressionCount; var beforeAI = audio.boostOnByRobot;
                for (int frame = 0; frame < 3600; frame++)
                {
                    var input = DirtOpponent.driveInput(player);
                    physics.advance(input, ref player, ref race, rivals, dt: 1.0 / 60, raceDT: 1.0 / 60);
                    var states = new[] { player }.Concat(rivals.Select(r => r.simulation)).ToArray();
                    audio.update(states: states, lineup: lineup, zones: new[] { new SpectatorSoundZone(position: new Double2(player.x + 3, player.z), people: 100, stormPeople: 2) }, heading: player.heading, storm: false, racing: true, dt: 1.0 / 60,
                                 progress: new[] { race }.Concat(rivals.Select(r => r.race)).Select(r => r.progress).ToArray());
                    _ = render(raceFile);
                    if (frame % 600 == 0) GD.Print($"Audio physics render {frame / 60}s");
                }
                checks["realRaceVoiceBudget"] = audio.expressionCount - beforeSpeech <= 8;
                measurements["realRaceExpressions"] = audio.expressionCount - beforeSpeech;
                measurements["realRaceBoostOnsets"] = audio.boostOnCount;
                var afterAI = audio.boostOnByRobot;
                checks["aiBoostAudible"] = Enumerable.Range(1, 3).All(i => afterAI[i] > beforeAI[i]);
                measurements["aiBoostOnsets"] = Enumerable.Range(1, 3).Select(i => afterAI[i] - beforeAI[i]).ToArray();
                // One category at a time, approach, idle, leave, storm, post-race.
                var cityFile = new AVAudioFile(forWriting: Path.Combine(directory, "city-tour.wav"), settings: format.settings);
                foreach (var kind in new[] { TownSoundZone.Kind.market, TownSoundZone.Kind.workshop, TownSoundZone.Kind.cantina })
                {
                    audio.resetConversation(); var levels = new List<double>();
                    var distances = new[] { 45.0, 4, 0, 45 };
                    for (int index = 0; index < distances.Length; index++)
                    {
                        var zone = new TownSoundZone(position: new Double2(distances[index], 0), kind: kind);
                        double power = 0.0;
                        for (int frame = 0; frame < 240; frame++)
                        {
                            audio.update(actors: actors(0), lineup: lineup, zones: Array.Empty<SpectatorSoundZone>(), heading: 0, storm: false, racing: true, dt: 1.0 / 60, town: new[] { zone }, expressions: false);
                            var p = render(cityFile); if (frame > 119) power += p;
                        }
                        levels.Add(10 * Math.Log10(max(1e-12, power / 120)));
                        if (index == 2) checks[$"city{(int)kind}Audible"] = audio.lastMix[25 + (int)kind].gain > 0.35;
                    }
                    measurements[$"city{(int)kind}DBFS"] = levels.ToArray();
                    checks[$"city{(int)kind}Range"] = levels[2] - levels[0] > 10 && levels[2] - levels[3] > 10;
                }
                // Actual authored tents and the entire racing lane: the old broad
                // workshop falloff let tools dominate even along the inner edge.
                var repairZones = dirtWorld.town.soundZones.Where(z => z.infieldRepair).ToArray();
                double repairGain(Double2 p, bool legacy = false) =>
                    sqrt(repairZones.Aggregate(0.0, (sum, zone) =>
                    {
                        var mix = legacy ? RaceAudio.spatial(source: zone.position, listener: p, heading: 0, range: 26) : RaceAudio.townSpatial(zone: zone, listener: p, heading: 0);
                        return sum + pow((double)mix.gain * zone.activity * 0.4, 2);
                    }));
                double lanePeak = 0.0, oldLanePeak = 0.0;
                for (int sample = 0; sample < 768; sample++)
                {
                    foreach (var offset in new[] { -DirtCourse.width, 0, DirtCourse.width })
                    {
                        var p = DirtCourse.point((double)sample * 2 * Math.PI / 768, offset: offset);
                        lanePeak = max(lanePeak, repairGain(new Double2(p.x, p.z)));
                        oldLanePeak = max(oldLanePeak, repairGain(new Double2(p.x, p.z), legacy: true));
                    }
                }
                checks["repairQuietAcrossRacingLane"] = repairZones.Length == 2 && lanePeak < 0.01 && lanePeak < oldLanePeak * 0.06;
                measurements["repairLanePeakBeforeAfter"] = new[] { oldLanePeak, lanePeak };
                var repairFile = new AVAudioFile(forWriting: Path.Combine(directory, "repair-approach.wav"), settings: format.settings);
                var tentLevels = new List<double[]>();
                foreach (var tent in InfieldLayout.tentOrigins)
                {
                    var projection = DirtCourse.projection(x: tent.x, z: tent.y);
                    var lane = DirtCourse.point(projection.phase);
                    var start = new Double2(lane.x, lane.z);
                    var route = Enumerable.Range(0, 5).Select(k => start + (tent - start) * (double)k / 4).ToArray();
                    var gains = route.Select(p => repairGain(p)).ToArray();
                    tentLevels.Add(gains);
                    checks[$"repairTent{tentLevels.Count}Approach"] = gains.Zip(gains.Skip(1)).All(pair => pair.First <= pair.Second) && gains[^1] > 0.12 && gains[^1] > gains[0] * 20;
                    audio.resetConversation();
                    // Continuous approach then return, rendered through the real mixer.
                    for (int frame = 0; frame < 960; frame++)
                    {
                        var t = (double)(frame < 480 ? frame : 960 - frame) / 480;
                        var a = actors(0); a[0].position = start + (tent - start) * t;
                        audio.update(actors: a, lineup: lineup, zones: Array.Empty<SpectatorSoundZone>(), heading: 0, storm: false, racing: true, dt: 1.0 / 60, town: repairZones, expressions: false);
                        _ = render(repairFile);
                    }
                }
                measurements["repairTentApproachGains"] = tentLevels.ToArray();
                audio.resetConversation();
                var close = new SpectatorSoundZone(position: new Double2(2, 0), people: 50, stormPeople: 1);
                var far = new SpectatorSoundZone(position: new Double2(500, 0), people: 50, stormPeople: 1);
                for (int k = 0; k < 120; k++) { audio.update(actors: actors(6), lineup: lineup, zones: new[] { close }, heading: 0, storm: false, racing: true, dt: 1.0 / 60, finished: true); _ = render(); }
                checks["finishCheersAndMotors"] = audio.lastMix[8].gain > 0.1 && audio.lastMix[0].gain > 0.1;
                for (int k = 0; k < 240; k++) { audio.update(actors: actors(6), lineup: lineup, zones: new[] { far }, heading: 0, storm: false, racing: true, dt: 1.0 / 60, finished: true, escaping: true); _ = render(); }
                checks["departureKeepsWorldAlive"] = audio.active && audio.lastMix[8..12].All(m => m.gain < 0.0001) && audio.lastMix[0].gain > 0.1;
                for (int k = 0; k < 120; k++) { audio.update(actors: actors(6), lineup: lineup, zones: new[] { close }, heading: 0, storm: false, racing: true, dt: 1.0 / 60, finished: true, escaping: true); _ = render(); }
                checks["departedStadiumStaysQuiet"] = audio.lastMix[8..12].All(m => m.gain < 0.0001);
                var audibleTown = new TownSoundZone(position: new Double2(2, 0), kind: TownSoundZone.Kind.workshop);
                var roaming = actors(0); roaming[0].position = new Double2(200, 0); roaming[0].courseWind = new Double2(24, 9); roaming[0].shelter = 0.2;
                for (int k = 0; k < 180; k++)
                {
                    audio.update(actors: roaming, lineup: lineup, zones: new[] { close }, heading: 0, storm: true, racing: true, dt: 1.0 / 60, finished: true, escaping: true, town: new[] { audibleTown }); _ = render();
                }
                checks["postraceWorldAndFixedStorm"] = audio.lastMix[26].gain > 0.025 && audio.lastMix[28].gain > 0.3 && audio.lastMix[8].gain < 0.0001;
                audio.resetConversation();
                for (int k = 0; k < 120; k++) { audio.update(actors: actors(6), lineup: lineup, zones: new[] { close }, heading: 0, storm: true, racing: true, dt: 1.0 / 60, town: new[] { new TownSoundZone(position: Double2.zero, kind: TownSoundZone.Kind.market) }); _ = render(); }
                checks["stormReducesOutdoorActivity"] = audio.lastMix[25].gain < 0.08 && audio.lastMix[28].gain > 0.25 && audio.lastMix[29].gain > 0.1;
                var stormFile = new AVAudioFile(forWriting: Path.Combine(directory, "sandstorm.wav"), settings: format.settings);
                var stormLevels = new List<double>();
                audio.resetConversation();
                // Clear / onset / full gust / sheltered / driving / boosting / clear again.
                for (int stage = 0; stage < 7; stage++)
                {
                    var a = actors(stage == 4 || stage == 5 ? 6 : 0, boost: stage == 5);
                    a[0].wind = new Double2(stage == 1 ? 12 : 24, stage == 1 ? 6 : 9);
                    a[0].stormBuild = stage == 1 ? 0.18 : 1; a[0].shelter = stage == 3 ? 0.25 : 1;
                    double power = 0.0;
                    for (int frame = 0; frame < 240; frame++)
                    {
                        audio.update(actors: a, lineup: lineup, zones: Array.Empty<SpectatorSoundZone>(), heading: 0, storm: stage > 0 && stage < 6, racing: true, dt: 1.0 / 60, expressions: false);
                        var value = render(stormFile); if (frame >= 120) power += value;
                    }
                    stormLevels.Add(10 * Math.Log10(max(1e-12, power / 120)));
                }
                checks["stormBuildAndShelter"] = stormLevels[2] - stormLevels[0] > 10 && stormLevels[2] - stormLevels[3] > 5 && stormLevels[2] > stormLevels[1];
                checks["stormBoostCutsThrough"] = stormLevels[5] - stormLevels[4] > 2;
                measurements["stormStageDBFS"] = stormLevels.ToArray();
                // Worst-case simultaneous boosted engines, stadium, town, impacts and expressions.
                var stress = actors(12, boost: true);
                for (int i = 0; i < 4; i++) { stress[i] = stress[0]; stress[i].position = new Double2((double)i * 0.4, 0); }
                for (int frame = 0; frame < 600; frame++)
                {
                    for (int i = 0; i < 4; i++) { stress[i].contact = frame % 120 == 0; stress[i].impactSerial = frame / 120 + 1; stress[i].impactSpeed = 8; }
                    audio.update(actors: stress, lineup: lineup, zones: Enumerable.Repeat(close, 4).ToArray(), heading: 0, storm: false, racing: true, dt: 1.0 / 60,
                                 town: new[] { new TownSoundZone(position: Double2.zero, kind: TownSoundZone.Kind.market), new TownSoundZone(position: Double2.zero, kind: TownSoundZone.Kind.workshop), new TownSoundZone(position: Double2.zero, kind: TownSoundZone.Kind.cantina) });
                    _ = render();
                }
                checks["headroom"] = peak < 0.95 && peak > 0.1;
                var savedMute = raceSoundMuted;
                try
                {
                    startDirtTrack(); raceAudio = audio; raceSoundMuted = false;
                    updateRaceAudio(dt: 1.0 / 60, advancing: true); checks["introSilent"] = !audio.active;
                    dirtIntro = null;
                    updateRaceAudio(dt: 1.0 / 60, advancing: true); checks["countdownActive"] = audio.active;
                    race.countDown(dt: 3);
                    updateRaceAudio(dt: 1.0 / 60, advancing: false); checks["pauseSilent"] = !audio.active;
                    updateRaceAudio(dt: 1.0 / 60, advancing: true); checks["resumeActive"] = audio.active;
                    raceSoundMuted = true; updateRaceAudio(dt: 1.0 / 60, advancing: true); checks["muteSilent"] = !audio.active;
                    raceSoundMuted = false; updateRaceAudio(dt: 1.0 / 60, advancing: true); checks["unmuteActive"] = audio.active;
                    reset(null); updateRaceAudio(dt: 1.0 / 60, advancing: true); checks["resetCountdownActive"] = audio.active && audio.lastMix[0..8].All(m => m.gain < 0.0001);
                    showMainMenu(null); checks["menuSilent"] = !audio.active; raceAudio = null;
                }
                finally { raceSoundMuted = savedMute; }
                var allPassed = checks.Values.All(v => v);
                var report = new Dictionary<string, object>
                {
                    ["passed"] = allPassed, ["checks"] = checks, ["measurements"] = measurements, ["peak"] = peak,
                    ["loopBudget"] = RaceAudio.loopCount, ["oneShotBudget"] = 20, ["listeningAcceptance"] = "Not established by signal tests",
                };
                File.WriteAllText(Path.Combine(directory, "audio.json"), JSONSerialization.prettyPrintedSortedKeys(report));
                var failed = checks.Where(c => !c.Value).OrderBy(c => c.Key, StringComparer.Ordinal).ToArray();
                GD.Print("Audio checks: " + (failed.Length == 0 ? "[:]" : "[" + string.Join(", ", failed.Select(c => $"\"{c.Key}\": false")) + "]"));
                return allPassed;
            }
            finally { audio.stop(); }
        }
        catch (Exception error) { GD.Print($"Audio check: {error}"); return false; }
    }

    /// <summary>
    /// Foundation JSONSerialization.data(withJSONObject:options:[.prettyPrinted, .sortedKeys]) as macOS writes
    /// it: two-space indentation, "key" : value, keys in ordinal order, doubles with 17 significant digits
    /// (%.17g, integral values without a fraction), no trailing newline.
    /// PORT: private to this smoke test so parallel ports do not collide on a shared helper.
    /// </summary>
    private static class JSONSerialization
    {
        public static string prettyPrintedSortedKeys(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value, 0);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object value, int indent)
        {
            switch (value)
            {
                case null: sb.Append("null"); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case string s: WriteString(sb, s); break;
                case int or long or uint or short: sb.Append(Convert.ToInt64(value).ToString(CultureInfo.InvariantCulture)); break;
                case float f: sb.Append(Number(f)); break;
                case double d: sb.Append(Number(d)); break;
                case IDictionary dictionary:
                {
                    var keys = dictionary.Keys.Cast<string>().OrderBy(k => k, StringComparer.Ordinal).ToArray();
                    if (keys.Length == 0) { sb.Append("{\n\n").Append(' ', indent * 2).Append('}'); break; }
                    sb.Append("{\n");
                    for (int i = 0; i < keys.Length; i++)
                    {
                        sb.Append(' ', (indent + 1) * 2); WriteString(sb, keys[i]); sb.Append(" : ");
                        Write(sb, dictionary[keys[i]], indent + 1);
                        sb.Append(i + 1 < keys.Length ? ",\n" : "\n");
                    }
                    sb.Append(' ', indent * 2).Append('}');
                    break;
                }
                case IEnumerable sequence:
                {
                    var items = sequence.Cast<object>().ToArray();
                    if (items.Length == 0) { sb.Append("[\n\n").Append(' ', indent * 2).Append(']'); break; }
                    sb.Append("[\n");
                    for (int i = 0; i < items.Length; i++)
                    {
                        sb.Append(' ', (indent + 1) * 2);
                        Write(sb, items[i], indent + 1);
                        sb.Append(i + 1 < items.Length ? ",\n" : "\n");
                    }
                    sb.Append(' ', indent * 2).Append(']');
                    break;
                }
                default: WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture)); break;
            }
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '/': sb.Append("\\/"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        /// <summary>printf("%.17g"), as NSNumber's JSON description; integral values print without a fraction.</summary>
        private static string Number(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) throw new InvalidDataException("JSON cannot encode a non-finite number");
            if (d == Math.Floor(d) && Math.Abs(d) < 1e15) return ((long)d).ToString(CultureInfo.InvariantCulture);
            // 17 significant digits, then %g's choice between fixed and exponential notation, trailing zeros removed.
            string e = d.ToString("E16", CultureInfo.InvariantCulture); // d.dddddddddddddddde+xxx
            int ePos = e.IndexOf('E');
            int exponent = int.Parse(e.Substring(ePos + 1), CultureInfo.InvariantCulture);
            bool negative = e[0] == '-';
            string digits = e.Substring(negative ? 1 : 0, ePos - (negative ? 1 : 0)).Replace(".", "");
            string result;
            if (exponent < -4 || exponent >= 17)
            {
                string mantissa = digits.Substring(0, 1) + "." + digits.Substring(1);
                mantissa = mantissa.TrimEnd('0').TrimEnd('.');
                result = mantissa + "e" + (exponent < 0 ? "-" : "+") + Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture);
            }
            else if (exponent < 0)
            {
                result = ("0." + new string('0', -exponent - 1) + digits).TrimEnd('0').TrimEnd('.');
            }
            else
            {
                string integer = digits.Substring(0, exponent + 1), fraction = digits.Substring(exponent + 1);
                result = (integer + "." + fraction).TrimEnd('0').TrimEnd('.');
            }
            return negative ? "-" + result : result;
        }
    }
}
