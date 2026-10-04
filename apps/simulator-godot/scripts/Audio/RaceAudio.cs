using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Godot;
using Marvin.Core;
using static Marvin.Core.Swift;

namespace Marvin;

public struct SpectatorSoundZone
{
    public Double2 position;
    public int people;
    public int stormPeople;
    public SpectatorSoundZone(Double2 position, int people, int stormPeople) { this.position = position; this.people = people; this.stormPeople = stormPeople; }
}
public struct TownSoundZone
{
    public enum Kind { market = 0, workshop = 1, cantina = 2 }
    public readonly Double2 position; public readonly Kind kind;
    public double activity = 1;
    public bool infieldRepair = false;
    public TownSoundZone(Double2 position, Kind kind, double activity = 1, bool infieldRepair = false)
    {
        this.position = position; this.kind = kind; this.activity = activity; this.infieldRepair = infieldRepair;
    }
}

/// Fixed, predecoded voice budget. No file I/O or synthesis on the gameplay thread.
/// PORT: AVAudioEngine and its nodes are the facade's (scripts/SceneKit/AVAudioEngine.cs): a C# mixer
/// measured against AVAudioEngine, played through a Godot AudioStreamGenerator.
public sealed class RaceAudio
{
    public struct Mix
    {
        public float gain = 0, pan = 0, rate = 1, cutoff = 18000;
        public Mix() { }
        public Mix(float gain = 0, float pan = 0, float rate = 1, float cutoff = 18000) { this.gain = gain; this.pan = pan; this.rate = rate; this.cutoff = cutoff; }
    }
    public struct Actor
    {
        public Double2 position; public double speed, wheelSpeed, turn;
        public double throttle; public bool boost, brake, airborne, contact; public double sand;
        public long impactSerial = 0; public double impactSpeed = 0.0;
        public Double2 wind = Double2.zero, courseWind = Double2.zero; public double shelter = 1.0, stormBuild = 0.0;
        public Actor(Simulation s)
        {
            position = new Double2(s.x, s.z); speed = s.groundSpeed;
            impactSerial = s.impactSerial; impactSpeed = s.impactSpeed;
            var cw = s.storm.wind(x: 0, z: 0); courseWind = new Double2(cw.x, cw.z);
            var w = s.storm.wind(x: s.x, z: s.z); wind = new Double2(w.x, w.z); shelter = s.windShelter; stormBuild = s.storm.accumulation;
            wheelSpeed = (abs(s.leftSpeed) + abs(s.rightSpeed)) / 2; turn = abs(s.angularVelocity);
            throttle = abs(s.appliedDriveInput.throttle); boost = s.appliedDriveInput.boost;
            brake = s.appliedDriveInput.isBraking; airborne = !s.hasDirtContact; contact = s.contacting;
            var distance = DirtCourse.projection(x: s.x, z: s.z).distance;
            sand = max(0, min(1, (distance - DirtCourse.width) / 1.6));
        }
    }
    public const int loopCount = 30;
    public static Mix spatial(Double2 source, Double2 listener, double heading, double range)
    {
        Double2 delta = source - listener; double d = Simd.length(delta);
        var edge = max(0, min(1, (range - d) / 6));
        var gain = edge * edge * (3 - 2 * edge) / (1 + pow(d / 6, 2));
        var right = new Double2(-cos(heading), sin(heading));
        return new Mix(gain: (float)gain, pan: (float)max(-0.9, min(0.9, Simd.dot(delta, right) / max(2, d))), cutoff: (float)max(1400, 16000 / (1 + d / 10)));
    }
    public static Mix townSpatial(TownSoundZone zone, Double2 listener, double heading)
    {
        var mix = spatial(source: zone.position, listener: listener, heading: heading,
                          range: zone.infieldRepair ? 14 : zone.kind == TownSoundZone.Kind.workshop ? 26 : 32);
        if (zone.infieldRepair)
        {
            // The retaining wall keeps tools faint from the racing lane. Fade
            // continuously across the entrance, then localize each tent closely.
            var offset = DirtCourse.projection(x: listener.x, z: listener.y).offset;
            var depth = max(0, min(1, (-offset - DirtCourse.fenceOffset) / 2.5));
            var inside = depth * depth * (3 - 2 * depth);
            var distance = Simd.distance(zone.position, listener);
            mix.gain *= (float)((0.035 + 0.965 * inside) * (1 + pow(distance / 6, 2)) / (1 + pow(distance / 2.5, 2)));
            mix.cutoff = min(mix.cutoff, (float)(1800 + 10200 * inside));
        }
        return mix;
    }
    private sealed class Voice
    {
        public readonly AVAudioPlayerNode player = new(); public readonly AVAudioUnitVarispeed pitch = new(); public readonly AVAudioUnitEQ eq = new(numberOfBands: 1);
        public Mix mix = new Mix();
    }
    private sealed class Shot
    {
        public readonly AVAudioPlayerNode player = new();
        public int robot = -1; public double remaining = 0.0; public float gain = 0;
        public bool speech = false;
    }
    public readonly AVAudioEngine engine = new AVAudioEngine();
    private readonly AVAudioFormat format = new AVAudioFormat(standardFormatWithSampleRate: 48000, channels: 2);
    private readonly Voice[] voices = Enumerable.Range(0, loopCount).Select(_ => new Voice()).ToArray();
    private readonly Shot[] shots = Enumerable.Range(0, 20).Select(_ => new Shot()).ToArray();
    private readonly AVAudioUnitEffect limiter = new AVAudioUnitEffect(audioComponentDescription: new AudioComponentDescription(componentType: kAudioUnitType_Effect, componentSubType: kAudioUnitSubType_PeakLimiter, componentManufacturer: kAudioUnitManufacturer_Apple, componentFlags: 0, componentFlagsMask: 0));
    private readonly Dictionary<string, AVAudioPCMBuffer> buffers = new();
    private readonly string[] characters = { "marvin", "r2d2", "bb8", "wallE" };
    private RaceVoiceDirector director = new RaceVoiceDirector();
    private bool playing = false, stormState = false, finishedState = false, crowdDeparted = false;
    private RacePerformance.Character[] currentLineup = Array.Empty<RacePerformance.Character>();
    private double retryAt = 0.0, clock = 0.0;
    private bool[] previousBoost = new bool[4]; private long[] lastImpactSerial = new long[4];
    private double[] impactReady = new double[4];
    private int? lastCountdown;
    public int expressionCount { get; private set; } = 0;
    public int boostOnCount { get; private set; } = 0;
    public int boostOffCount { get; private set; } = 0;
    public int impactCount { get; private set; } = 0;
    // PORT: Swift `private(set) var` arrays are values; the getters return copies.
    private readonly int[] _boostOnByRobot = new int[4], _boostOffByRobot = new int[4];
    public int[] boostOnByRobot => (int[])_boostOnByRobot.Clone();
    public int[] boostOffByRobot => (int[])_boostOffByRobot.Clone();
    private Mix[] _lastMix = Enumerable.Repeat(new Mix(), loopCount).ToArray();
    public Mix[] lastMix => (Mix[])_lastMix.Clone();
    public int speakingCount => shots.Count(s => s.speech && s.remaining > 0);
    public bool active => playing && engine.isRunning;

    /// PORT: `resources` is the asset root ("res://assets", the macOS bundle's Resources); throws like Swift's
    /// throwing initializer (missing file, or audio that is not 48 kHz / empty).
    public RaceAudio(string resources, bool offline = false)
    {
        var names = new List<string> { "crowd", "sparse-crowd", "finish-crowd", "desert-wind", "market", "workshop", "cantina", "impact", "countdown", "go", "finish", "storm-gust", "storm-grit" };
        foreach (var name in characters)
        {
            names.AddRange(new[] { name, name + "-ground", name + "-high", name + "-boost", name + "-sand", name + "-boost-on", name + "-boost-off" });
            names.AddRange(RaceVoiceDirector.moods.SelectMany(mood => Enumerable.Range(0, RaceVoiceDirector.variantCount).Select(v => name + "-" + mood + "-" + v.ToString(CultureInfo.InvariantCulture))));
        }
        foreach (var name in names)
        {
            var file = new AVAudioFile(forReading: resources + "/Audio/" + name + ".wav");
            var source = new AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: (AVAudioFrameCount)file.length);
            file.read(into: source);
            if (!(source.format.sampleRate == 48000 && source.frameLength > 0)) throw new InvalidDataException($"{name}.wav: expected 48 kHz audio"); // CocoaError(.fileReadCorruptFile)
            var stereo = new AVAudioPCMBuffer(pcmFormat: format, frameCapacity: source.frameLength);
            stereo.frameLength = source.frameLength;
            for (int channel = 0; channel < 2; channel++) stereo.floatChannelData[channel].update(from: source.floatChannelData[min(channel, (int)source.format.channelCount - 1)], count: (int)source.frameLength);
            buffers[name] = stereo;
        }
        engine.attach(limiter);
        var bus = new AVAudioMixerNode(); engine.attach(bus);
        engine.connect(bus, to: limiter, format: format); engine.connect(limiter, to: engine.mainMixerNode, format: format);
        foreach (var voice in voices)
        {
            engine.attach(voice.player); engine.attach(voice.pitch); engine.attach(voice.eq);
            engine.connect(voice.player, to: voice.pitch, format: format);
            engine.connect(voice.pitch, to: voice.eq, format: format); engine.connect(voice.eq, to: bus, format: format);
            voice.eq.bands[0].filterType = AVAudioUnitEQFilterType.lowPass; voice.eq.bands[0].bypass = false; voice.eq.bands[0].frequency = 18000;
            voice.player.volume = 0;
        }
        foreach (var shot in shots) { engine.attach(shot.player); engine.connect(shot.player, to: bus, format: format); shot.player.volume = 0; }
        engine.mainMixerNode.outputVolume = 0.82f;
        if (offline) engine.enableManualRenderingMode(AVAudioEngineManualRenderingMode.offline, format: format, maximumFrameCount: 1024);
        engine.prepare();
    }
    public void resetConversation()
    {
        stop(); director = new RaceVoiceDirector(); crowdDeparted = false; clock = 0; finishedState = false; lastCountdown = null;
        previousBoost = new bool[4]; lastImpactSerial = new long[4];
        impactReady = new double[4];
    }
    public void stop()
    {
        foreach (var voice in voices) { voice.player.volume = 0; voice.player.stop(); voice.mix = new Mix(); }
        foreach (var shot in shots) { shot.player.stop(); shot.remaining = 0; shot.robot = -1; }
        engine.pause(); playing = false; _lastMix = Enumerable.Repeat(new Mix(), loopCount).ToArray();
    }
    private void scheduleCrowd(bool storm, bool finished)
    {
        var buffer = buffers[storm ? "sparse-crowd" : finished ? "finish-crowd" : "crowd"];
        for (int i = 8; i < 12; i++)
        {
            voices[i].player.stop(); voices[i].player.scheduleBuffer(buffer, at: null, options: AVAudioPlayerNodeBufferOptions.loops);
            if (engine.isRunning) voices[i].player.play();
        }
    }
    private void start(IReadOnlyList<RacePerformance.Character> lineup, bool storm, bool finished)
    {
        currentLineup = lineup.ToArray();
        for (int i = 0; i < voices.Length; i++)
        {
            if (i >= 8 && i < 12) continue;
            string name;
            switch (i)
            {
                case < 4: name = characters[(int)lineup[i]]; break;
                case < 8: name = characters[(int)lineup[i - 4]] + "-ground"; break;
                case >= 12 and < 16: name = characters[(int)lineup[i - 12]] + "-high"; break;
                case >= 16 and < 20: name = characters[(int)lineup[i - 16]] + "-boost"; break;
                case >= 20 and < 24: name = characters[(int)lineup[i - 20]] + "-sand"; break;
                default: name = new[] { "desert-wind", "market", "workshop", "cantina", "storm-gust", "storm-grit" }[i - 24]; break;
            }
            voices[i].player.scheduleBuffer(buffers[name], at: null, options: AVAudioPlayerNodeBufferOptions.loops);
        }
        scheduleCrowd(storm: storm, finished: finished); stormState = storm; playing = true;
    }
    public void update(IReadOnlyList<Simulation> states, IReadOnlyList<RacePerformance.Character> lineup, IReadOnlyList<SpectatorSoundZone> zones, double heading,
                       bool storm, bool racing, double dt, bool finished = false, bool escaping = false, IReadOnlyList<double> progress = null,
                       IReadOnlyList<TownSoundZone> town = null, double countdown = 0)
    {
        update(actors: states.Select(s => new Actor(s)).ToArray(), lineup: lineup, zones: zones, heading: heading, storm: storm, racing: racing, dt: dt,
               finished: finished, escaping: escaping, progress: progress, town: town, countdown: countdown);
    }
    public void update(IReadOnlyList<Actor> actors, IReadOnlyList<RacePerformance.Character> lineup, IReadOnlyList<SpectatorSoundZone> zones, double heading,
                       bool storm, bool racing, double dt, bool finished = false, bool escaping = false, IReadOnlyList<double> progress = null,
                       IReadOnlyList<TownSoundZone> town = null, double countdown = 0, bool expressions = true, bool ambience = true)
    {
        progress ??= Array.Empty<double>(); town ??= Array.Empty<TownSoundZone>();
        if (!(racing && actors.Count == 4 && lineup.Count == 4)) { if (playing) stop(); return; }
        dt = min(0.1, max(0, dt)); clock += dt;
        if (!currentLineup.SequenceEqual(lineup)) stop();
        if (!playing) start(lineup: lineup, storm: storm, finished: finished);
        if (stormState != storm || finishedState != finished) { scheduleCrowd(storm: storm, finished: finished); stormState = storm; }
        if (!engine.isRunning)
        {
            // PORT: ProcessInfo.processInfo.systemUptime is Godot's monotonic clock; NSLog is a Godot warning.
            var uptime = Time.GetTicksUsec() / 1e6;
            if (!(uptime >= retryAt)) return;
            try { engine.start(); foreach (var voice in voices) voice.player.play(); }
            catch (Exception error) { retryAt = uptime + 2; GD.PushWarning($"Audio output unavailable: {error.Message}"); return; }
        }
        Double2 listener = finished ? Double2.zero : actors[0].position;
        foreach (var shot in shots)
        {
            if (!(shot.remaining > 0)) continue;
            shot.remaining = max(0, shot.remaining - dt);
            if (shot.remaining == 0) shot.player.stop();
        }
        int count = (int)ceil(countdown);
        if (count > 0 && count != lastCountdown) play("countdown", robot: -1, gain: 0.55f);
        if (count == 0 && lastCountdown is int previous && previous > 0) play("go", robot: -1, gain: 0.65f);
        lastCountdown = count;
        if (finished && !finishedState) play("finish", robot: -1, gain: 0.6f);
        finishedState = finished;
        if (!finished && !escaping) crowdDeparted = false;
        if (expressions && countdown <= 0 && !finished && !escaping)
        {
            var observations = actors.Select((s, i) => new RaceVoiceDirector.Observation(position: s.position, speed: s.speed, contact: s.contact, progress: progress.Count == 4 ? progress[i] : null)).ToArray();
            if (director.advance(observations: observations, dt: dt, canSpeak: speakingCount == 0) is RaceVoiceDirector.Event e)
            {
                play(characters[(int)lineup[e.robot]] + "-" + e.mood + "-" + e.variant.ToString(CultureInfo.InvariantCulture), robot: e.robot, gain: e.robot == 0 ? 0.28f : 0.24f, speech: true);
            }
        }
        var target = Enumerable.Repeat(new Mix(), loopCount).ToArray();
        for (int i = 0; i < 4; i++)
        {
            var s = actors[i]; double motion = min(1, s.speed / 12), rpm = min(1, max(s.wheelSpeed / 12, s.turn / 12));
            var powered = countdown <= 0 && s.throttle > 0.05 && !s.brake;
            var boost = powered && s.boost;
            var spatial = RaceAudio.spatial(source: s.position, listener: listener, heading: heading, range: 36);
            var near = spatial.gain > 0.01;
            if (boost != previousBoost[i] && near)
            {
                if (play(characters[(int)lineup[i]] + (boost ? "-boost-on" : "-boost-off"), robot: i, gain: i == 0 ? 0.55f : 0.22f, critical: true))
                {
                    if (boost) { boostOnCount += 1; _boostOnByRobot[i] += 1; } else { boostOffCount += 1; _boostOffByRobot[i] += 1; }
                }
            }
            previousBoost[i] = boost;
            if (s.impactSerial != lastImpactSerial[i] && s.impactSpeed > 0.65 && clock >= impactReady[i] && near)
            {
                if (play("impact", robot: i, gain: (float)min(0.65, s.impactSpeed / 10))) impactCount += 1;
                impactReady[i] = clock + 0.45;
            }
            lastImpactSerial[i] = s.impactSerial;
            var moving = min(1, max(s.speed, s.wheelSpeed) / 0.6);
            var load = powered ? s.throttle : 0.0;
            var strength = (i == 0 ? 1.0 : 0.7) * moving;
            var high = min(1, max(0, (rpm - 0.16) / 0.84));
            foreach (var slot in new[] { i, i + 4, i + 12, i + 16, i + 20 }) target[slot] = spatial;
            target[i].gain *= (float)(strength * (0.06 + 0.52 * pow(rpm, 1.05)) * (1 - 0.32 * high) * (0.76 + 0.24 * load));
            target[i].rate = (float)(0.66 + rpm * 0.72);
            target[i + 12].gain *= (float)(strength * 0.70 * pow(high, 1.15) * (0.58 + 0.42 * load));
            target[i + 12].rate = (float)(0.75 + rpm * 0.62);
            target[i + 16].gain *= (float)(boost ? (i == 0 ? 0.62 : 0.42) * (s.airborne ? 0.75 : 1) : 0);
            target[i + 16].rate = (float)(0.85 + rpm * 0.35);
            var contact = s.airborne ? 0 : motion;
            target[i + 4].gain *= (float)((i == 0 ? 0.22 : 0.14) * contact * (1 - 0.85 * s.sand));
            target[i + 4].rate = (float)(0.65 + motion * 0.85);
            target[i + 20].gain *= (float)((i == 0 ? 0.42 : 0.28) * contact * s.sand);
            target[i + 20].rate = (float)(0.75 + motion * 0.40);
            if (countdown > 0) foreach (var slot in new[] { i, i + 4, i + 12, i + 16, i + 20 }) target[slot].gain = 0;
        }
        var positions = actors.Select(a => a.position).ToArray();
        double crowdDistance(SpectatorSoundZone z) => finished ? (positions.Length > 0 ? positions.Select(p => Simd.distance(p, z.position)).Min() : 100) : Simd.distance(z.position, listener);
        var nearby = sorted(zones.Where(z => (storm ? z.stormPeople : z.people) > 0), (a, b) => crowdDistance(a) < crowdDistance(b)).Take(4).ToArray();
        if (!crowdDeparted)
        {
            for (int j = 0; j < nearby.Length; j++)
            {
                var zone = nearby[j];
                var mix = RaceAudio.spatial(source: zone.position, listener: listener, heading: heading, range: 32);
                if (finished) mix.gain = positions.Length > 0 ? positions.Select(p => RaceAudio.spatial(source: zone.position, listener: p, heading: heading, range: 32).gain).Max() : 0;
                mix.gain *= (float)(min(1, sqrt((double)(storm ? zone.stormPeople : zone.people) / 25)) * (storm ? 0.14 : finished ? 0.42 : 0.25));
                mix.rate = 1 + (float)j * 0.019f; target[j + 8] = mix;
            }
            if (escaping && finished && target[8..12].All(m => m.gain < 0.0001) && _lastMix[8..12].All(m => m.gain < 0.0001)) crowdDeparted = true;
        }
        target[24] = new Mix(gain: ambience ? 0.075f : 0, pan: (float)(sin(clock * 0.07) * 0.25));
        if (storm && ambience)
        {
            var weather = actors[0]; var wind = finished ? weather.courseWind : weather.wind;
            var windSpeed = Simd.length(wind);
            double gust = max(0, min(1, (windSpeed - 10) / 15)), exposure = finished ? 1 : max(0.15, min(1, weather.shelter));
            var build = 0.65 + 0.35 * weather.stormBuild;
            var right = new Double2(-cos(heading), sin(heading));
            var pan = (float)max(-0.65, min(0.65, Simd.dot(wind, right) / max(1, windSpeed) * 0.65));
            target[28] = new Mix(gain: (float)((0.22 + 0.28 * gust) * exposure * build), pan: pan, rate: (float)(0.8 + gust * 0.28), cutoff: (float)(900 + 1900 * exposure));
            target[29] = new Mix(gain: (float)((0.12 + 0.20 * gust) * exposure * build), pan: pan * 0.8f, rate: (float)(0.9 + gust * 0.16), cutoff: (float)(2500 + 7000 * exposure));
        }
        // Mix each category into a stable voice, so nearest-zone reordering cannot pop or swap loops.
        foreach (var kind in new[] { TownSoundZone.Kind.market, TownSoundZone.Kind.workshop, TownSoundZone.Kind.cantina })
        {
            float energy = 0, weightedPan = 0, cutoff = 0;
            foreach (var zone in town)
            {
                if (!(ambience && zone.kind == kind)) continue;
                var mix = RaceAudio.townSpatial(zone: zone, listener: listener, heading: heading);
                var gain = mix.gain * (float)zone.activity * (storm && kind != TownSoundZone.Kind.cantina ? 0.12f : 1);
                energy += gain * gain; weightedPan += gain * gain * mix.pan; cutoff += gain * gain * mix.cutoff;
            }
            if (energy > 0)
            {
                target[25 + (int)kind] = new Mix(gain: min(1, sqrt(energy)) * (kind == TownSoundZone.Kind.workshop ? 0.40f : 0.48f), pan: weightedPan / energy, rate: 1, cutoff: cutoff / energy);
            }
        }
        for (int i = 0; i < voices.Length; i++)
        {
            var v = voices[i]; var blend = (float)(1 - exp(-dt / (i >= 24 ? 0.65 : 0.075)));
            v.mix.gain += (target[i].gain - v.mix.gain) * blend;
            v.mix.pan += (target[i].pan - v.mix.pan) * blend;
            v.mix.rate += (target[i].rate - v.mix.rate) * blend;
            v.mix.cutoff += (target[i].cutoff - v.mix.cutoff) * blend;
            v.player.volume = v.mix.gain; v.player.pan = v.mix.pan; v.pitch.rate = v.mix.rate; v.eq.bands[0].frequency = v.mix.cutoff;
            _lastMix[i] = v.mix;
        }
        foreach (var shot in shots)
        {
            if (!(shot.remaining > 0)) continue;
            var mix = shot.robot < 0 ? new Mix(gain: 1) : RaceAudio.spatial(source: actors[shot.robot].position, listener: listener, heading: heading, range: shot.speech ? 14 : 36);
            shot.player.volume = mix.gain * shot.gain; shot.player.pan = mix.pan;
        }
    }
    private bool play(string name, int robot, float gain, bool speech = false, bool critical = false)
    {
        var pool = critical ? shots.Skip(shots.Length - 16) : shots.Take(4);
        if (!buffers.TryGetValue(name, out var buffer)) return false;
        var slot = pool.FirstOrDefault(s => s.remaining <= 0);
        if (slot == null) return false;
        slot.player.stop(); slot.robot = robot; slot.gain = gain; slot.speech = speech; slot.player.volume = 0;
        slot.remaining = (double)buffer.frameLength / 48000;
        slot.player.scheduleBuffer(buffer, at: null); slot.player.play();
        if (speech) expressionCount += 1;
        return true;
    }
}

public partial class AppController
{
    public void updateRaceAudio(double dt, bool advancing)
    {
        var audible = !raceSoundMuted && inSandbox && isDirtTrack && advancing && dirtIntro == null;
        if (!audible) { raceAudio?.stop(); return; }
        var front = SimdBridge.Get(world.camera.simdWorldFront); var heading = atan2((double)front.x, (double)front.z);
        raceAudio?.update(states: new[] { simulation }.Concat(opponents.Select(o => o.simulation)).ToArray(), lineup: lineup,
            zones: dirtWorld.town.spectatorSoundZones, heading: heading, storm: racePhysics.storm.enabled, racing: true, dt: dt,
            finished: race.finished, escaping: racePhysics.escape.active, progress: new[] { race }.Concat(opponents.Select(o => o.race)).Select(r => r.progress).ToArray(),
            town: dirtWorld.town.soundZones, countdown: race.countdown);
    }
}
