using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace Marvin.SceneKit;

/// <summary>
/// <c>--audio-facade-test DIR</c>: checks the AVAudioEngine facade against values measured with AVAudioEngine
/// on macOS 27 (offline manual rendering, 48 kHz Float32 stereo, 800-frame renders unless noted), then plays
/// one second through the real-time output and captures Godot's Master bus. Writes audio-facade.json.
/// </summary>
public static class AudioFacadeTest
{
    private static readonly AVAudioFormat format = new(standardFormatWithSampleRate: 48000, channels: 2);
    private sealed class Rig
    {
        public readonly AVAudioEngine engine = new();
        public readonly AVAudioPlayerNode player = new();
        public readonly AVAudioUnitVarispeed pitch = new();
        public readonly AVAudioUnitEQ eq = new(numberOfBands: 1);
        public readonly AVAudioMixerNode bus = new();
        public readonly AVAudioUnitEffect limiter = new(new AudioComponentDescription(kAudioUnitType_Effect, kAudioUnitSubType_PeakLimiter, kAudioUnitManufacturer_Apple, 0, 0));
        private readonly AVAudioPCMBuffer output;
        public Rig(bool chain, bool useLimiter, bool offline = true)
        {
            engine.attach(player); engine.attach(bus); engine.attach(limiter);
            if (chain)
            {
                engine.attach(pitch); engine.attach(eq);
                engine.connect(player, to: pitch, format: format); engine.connect(pitch, to: eq, format: format); engine.connect(eq, to: bus, format: format);
                eq.bands[0].filterType = AVAudioUnitEQFilterType.lowPass; eq.bands[0].bypass = false; eq.bands[0].frequency = 18000;
            }
            else engine.connect(player, to: bus, format: format);
            if (useLimiter) { engine.connect(bus, to: limiter, format: format); engine.connect(limiter, to: engine.mainMixerNode, format: format); }
            else engine.connect(bus, to: engine.mainMixerNode, format: format);
            if (offline) engine.enableManualRenderingMode(AVAudioEngineManualRenderingMode.offline, format: format, maximumFrameCount: 1024);
            engine.prepare();
            output = new AVAudioPCMBuffer(pcmFormat: format, frameCapacity: 1024);
        }
        public (float[] l, float[] r) render(int frames = 800)
        {
            engine.renderOffline((AVAudioFrameCount)frames, to: output);
            return (output.floatChannelData[0][..frames], output.floatChannelData[1][..frames]);
        }
        public float[] renderLeft(int blocks, int frames = 800)
        {
            var all = new List<float>();
            for (int i = 0; i < blocks; i++) all.AddRange(render(frames).l);
            return all.ToArray();
        }
    }
    private static AVAudioPCMBuffer buffer(int frames, Func<int, int, float> f)
    {
        var b = new AVAudioPCMBuffer(pcmFormat: format, frameCapacity: (AVAudioFrameCount)frames) { frameLength = (AVAudioFrameCount)frames };
        for (int c = 0; c < 2; c++) for (int i = 0; i < frames; i++) b.floatChannelData[c][i] = f(c, i);
        return b;
    }
    private static double rms(IEnumerable<float> x) { var a = x.ToArray(); return Math.Sqrt(a.Sum(v => (double)v * v) / a.Length); }

    private static readonly List<(string name, double measured, double expected, double tolerance)> results = new();
    private static void expect(string name, double measured, double expected, double tolerance) => results.Add((name, measured, expected, tolerance));

    [Marvin.GameMode("--audio-facade-test")]
    public static async Task Run(string dir, SceneTree tree)
    {
        results.Clear();
        MixerChecks(); VarispeedChecks(); EqChecks(); LimiterChecks(); PlayerTimelineChecks();
        var realtime = await RealtimeCheck(tree);
        var path = AVAudioFile.GlobalPath(dir);
        Directory.CreateDirectory(path);
        bool passed = results.All(r => Math.Abs(r.measured - r.expected) <= r.tolerance) && realtime.ok;
        var lines = results.Select(r => string.Format(CultureInfo.InvariantCulture, "    \"{0}\" : {{ \"measured\" : {1:R}, \"macOS\" : {2:R}, \"tolerance\" : {3:R}, \"ok\" : {4} }}",
            r.name, r.measured, r.expected, r.tolerance, Math.Abs(r.measured - r.expected) <= r.tolerance ? "true" : "false"));
        File.WriteAllText(Path.Combine(path, "audio-facade.json"), "{\n  \"checks\" : {\n" + string.Join(",\n", lines) + "\n  },\n  \"realtime\" : " + realtime.json + ",\n  \"passed\" : " + (passed ? "true" : "false") + "\n}\n");
        foreach (var r in results.Where(r => Math.Abs(r.measured - r.expected) > r.tolerance))
            GD.Print(string.Format(CultureInfo.InvariantCulture, "audio facade: {0} measured {1} macOS {2}", r.name, r.measured, r.expected));
        GD.Print($"Audio facade checks: {results.Count(r => Math.Abs(r.measured - r.expected) <= r.tolerance)}/{results.Count} within tolerance, real time {(realtime.ok ? "ok" : "FAILED")}: {realtime.json}");
        tree.Quit(passed ? 0 : 1);
    }

    private static void MixerChecks()
    {
        // Volume slews at 1/1200 per frame, applied before the step (probe E8).
        var rig = new Rig(chain: false, useLimiter: false);
        rig.player.scheduleBuffer(buffer(48000, (_, _) => 1), at: null, options: AVAudioPlayerNodeBufferOptions.loops);
        rig.player.volume = 0; rig.engine.start(); rig.player.play();
        for (int i = 0; i < 3; i++) rig.render();
        rig.player.volume = 1; var up = rig.render().l;
        rig.player.volume = 0.25f; var down = rig.render().l;
        expect("volumeRamp0to1_frame0", up[0], 0.0, 1e-6);
        expect("volumeRamp0to1_frame600", up[600], 0.5, 1e-4);
        expect("volumeRamp0to1_frame775", up[775], 0.6458, 1e-4);
        expect("volumeRamp1to0.25_frame0", down[0], 0.6667, 1e-4);
        expect("volumeRamp1to0.25_frame500", down[500], 0.25, 1e-4);
        // Balance pan law (probe E3) on a 0.5 DC stereo input through the voice chain.
        foreach (var (l, r, pan, el, er) in new[] { (1f, 1f, 0.5f, 0.25, 0.5), (1f, 1f, -1f, 0.5, 0.0), (1f, 0f, -0.5f, 0.5, 0.0), (0f, 1f, -0.25f, 0.0, 0.375) })
        {
            var p = new Rig(chain: true, useLimiter: false);
            p.player.scheduleBuffer(buffer(48000, (c, _) => (c == 0 ? l : r) * 0.5f), at: null, options: AVAudioPlayerNodeBufferOptions.loops);
            p.player.pan = pan; p.engine.start(); p.player.play();
            (float[] ol, float[] or) o = default;
            for (int i = 0; i < 4; i++) o = p.render();
            expect($"pan{pan}_in{l}{r}_L", o.ol[600], el, 1e-3); expect($"pan{pan}_in{l}{r}_R", o.or[600], er, 1e-3);
        }
        // Per-channel slew: pan -1 -> 1 moves both channels over 1200 frames.
        var q = new Rig(chain: false, useLimiter: false);
        q.player.scheduleBuffer(buffer(48000, (_, _) => 1), at: null, options: AVAudioPlayerNodeBufferOptions.loops);
        q.player.pan = -1; q.engine.start(); q.player.play(); q.render(); q.render();
        q.player.pan = 1; var pl = q.renderLeft(2);
        expect("panSlewLeft_frame600", pl[600], 0.5, 1e-3);
        // A stopped direct player freezes its input gain; the first render snaps to the volume (probe ramp).
        var z = new Rig(chain: false, useLimiter: false);
        z.player.volume = 1; z.engine.start(); z.render();
        z.player.volume = 0; z.render(); z.render();
        z.player.scheduleBuffer(buffer(48000, (_, _) => 1), at: null); z.player.play(); z.player.volume = 0.5f;
        var frozen = z.render().l;
        expect("frozenGainResumesFrom1_frame0", frozen[0], 1.0, 1e-6);
        expect("frozenGainResumesFrom1_frame300", frozen[300], 0.75, 1e-3);
        // outputVolume changes ramp across one render.
        var m = new Rig(chain: false, useLimiter: false);
        m.player.scheduleBuffer(buffer(48000, (_, _) => 1), at: null, options: AVAudioPlayerNodeBufferOptions.loops);
        m.engine.start(); m.player.play(); m.render();
        m.engine.mainMixerNode.outputVolume = 0.82f; var mv = m.render().l;
        expect("outputVolumeRamp_frame50", mv[50], 0.98875, 2e-4);
    }

    private static void VarispeedChecks()
    {
        foreach (var rate in new[] { 1f, 0.66f, 1.38f, 0.5f, 2f })
        {
            var rig = new Rig(chain: true, useLimiter: false);
            rig.player.scheduleBuffer(buffer(48000, (_, i) => (float)(Math.Sin(2 * Math.PI * 1000 * i / 48000) * 0.5)), at: null, options: AVAudioPlayerNodeBufferOptions.loops);
            rig.pitch.rate = rate; rig.engine.start(); rig.player.play();
            var l = rig.renderLeft(30);
            int crossings = 0;
            for (int i = 4800; i < 4800 + 19200; i++) if (l[i - 1] < 0 && l[i] >= 0) crossings++;
            expect($"varispeedFrequency_rate{rate}", crossings / 0.4, 1000 * rate, 2.5 + 0.01 * 1000 * rate);
            expect($"varispeedRms_rate{rate}", rms(l[4800..24000]), 0.353553, 2e-3);
        }
        var imp = new Rig(chain: true, useLimiter: false);
        imp.player.scheduleBuffer(buffer(48000, (_, i) => i == 0 ? 1 : 0), at: null);
        imp.engine.start(); imp.player.play();
        var il = imp.renderLeft(1);
        double centre = 0, weight = 0;
        for (int i = 30; i < 70; i++) { centre += i * il[i] * il[i]; weight += il[i] * il[i]; }
        expect("varispeedLatencyFrames_rate1", centre / weight, 48.68, 0.3);
    }

    private static void EqChecks()
    {
        foreach (var (cutoff, freq, db) in new[] { (1400f, 1400.0, -3.0103), (1400f, 2000.0, -7.1717), (1400f, 20000.0, -64.36), (4000f, 8000.0, -13.5324), (16000f, 18000.0, -6.7893), (18000f, 12000.0, -0.1260) })
        {
            var rig = new Rig(chain: true, useLimiter: false);
            rig.player.scheduleBuffer(buffer(48000, (_, i) => (float)(Math.Sin(2 * Math.PI * freq * i / 48000) * 0.5)), at: null, options: AVAudioPlayerNodeBufferOptions.loops);
            rig.eq.bands[0].frequency = cutoff; rig.engine.start(); rig.player.play();
            var l = rig.renderLeft(12);
            expect($"lowPass{cutoff}_at{freq}Hz_dB", 20 * Math.Log10(rms(l[4800..9600]) / (0.5 / Math.Sqrt(2))), db, Math.Abs(db) > 40 ? 0.6 : 0.06);
        }
        // Frequency glide 18000 -> 1400 over 1440 frames (probe eqslew): 8 kHz tone RMS per 100 frames.
        var g = new Rig(chain: true, useLimiter: false);
        g.player.scheduleBuffer(buffer(48000, (_, i) => (float)(Math.Sin(2 * Math.PI * 8000 * i / 48000) * 0.5)), at: null, options: AVAudioPlayerNodeBufferOptions.loops);
        g.engine.start(); g.player.play();
        for (int i = 0; i < 4; i++) g.render();
        g.eq.bands[0].frequency = 1400;
        var o = g.renderLeft(3);
        expect("eqGlide_rms_500to600", rms(o[500..600]), 0.1990, 0.03);
        expect("eqGlide_rms_1000to1100", rms(o[1000..1100]), 0.0362, 0.008);
        expect("eqGlide_rms_1600to1700", rms(o[1600..1700]), 0.0090, 0.001);
    }

    private static void LimiterChecks()
    {
        double run(Func<int, float> f, int frames, Func<float[], double> measure)
        {
            var rig = new Rig(chain: false, useLimiter: true);
            rig.player.scheduleBuffer(buffer(frames, (_, i) => f(i)), at: null);
            rig.engine.start(); rig.player.play();
            return measure(rig.renderLeft(frames / 800 + 1));
        }
        foreach (var (level, expected) in new[] { (0.5f, 0.5), (1.01f, 0.99884), (1.2f, 0.97902), (2f, 0.92256), (4f, 0.85112), (16f, 0.72441) })
            expect($"limiterDC{level}", run(_ => level, 48000, l => l[30000]), expected, 1e-4);
        expect("limiterLatencyFrames", run(i => i == 0 ? 0.5f : 0, 4800, l => Array.IndexOf(l, l.Max())), 96, 0);
        expect("limiterSpikeGain_4095", run(i => i == 4000 ? 4f : 0.1f, 9600, l => l[4095] / 0.1), 0.2128, 2e-4);
        expect("limiterSpikeGain_4096", run(i => i == 4000 ? 4f : 0.1f, 9600, l => l[4096] / 4.0), 0.2151, 2e-4);
        expect("limiterStepGain_4000", run(i => i < 4000 ? 0.5f : 2f, 9600, l => l[4000] / 0.5), 0.9920, 2e-4);
        foreach (var (freq, amp, peak, r) in new[] { (100.0, 2f, 0.8827, 0.6241), (440.0, 2f, 0.6275, 0.4437), (440.0, 4f, 0.4015, 0.2839), (1000.0, 4f, 0.8556, 0.6050), (50.0, 4f, 0.5620, 0.3974), (5000.0, 2f, 0.8888, 0.6284) })
        {
            expect($"limiterSine{freq}a{amp}_peak", run(i => (float)Math.Sin(2 * Math.PI * freq * i / 48000) * amp, 48000, l => l[24000..48000].Max(v => Math.Abs(v))), peak, 2e-4);
            expect($"limiterSine{freq}a{amp}_rms", run(i => (float)Math.Sin(2 * Math.PI * freq * i / 48000) * amp, 48000, l => rms(l[24000..48000])), r, 2e-4);
        }
        // Uniform noise from the probe's LCG (seed 1).
        // One generator for the whole sequence, drawn in the probe's order (amplitude 1.0 first).
        ulong seed = 1;
        float[] draw(float amp)
        {
            var values = new float[48000];
            for (int i = 0; i < values.Length; i++)
            {
                seed = unchecked(seed * 6364136223846793005UL + 1442695040888963407UL);
                values[i] = ((float)((double)(seed >> 11) / (double)(1UL << 53)) * 2 - 1) * amp;
            }
            return values;
        }
        draw(1.0f);
        foreach (var (amp, peak, r) in new[] { (1.5f, 0.7425, 0.4288), (2f, 0.6793, 0.3913), (3f, 0.7421, 0.4304) })
        {
            var values = draw(amp);
            expect($"limiterNoise{amp}_peak", run(i => values[i], 48000, l => l[24000..48000].Max(v => Math.Abs(v))), peak, 2e-4);
            expect($"limiterNoise{amp}_rms", run(i => values[i], 48000, l => rms(l[24000..48000])), r, 2e-4);
        }
    }

    private static void PlayerTimelineChecks()
    {
        // Probe restart: offline, a player behind a varispeed at rate 0.5 restarted after 300 renders stays
        // silent until its own timeline (rate x engine time) reaches the engine time of play(): 300 renders.
        var rig = new Rig(chain: true, useLimiter: false);
        rig.eq.bands[0].bypass = true;
        var loop = buffer(48000, (_, i) => (float)Math.Sin(i * 0.05) * 0.5f);
        rig.player.scheduleBuffer(loop, at: null, options: AVAudioPlayerNodeBufferOptions.loops);
        rig.pitch.rate = 0.5f; rig.engine.start(); rig.player.play();
        for (int i = 0; i < 300; i++) rig.render();
        rig.player.stop(); rig.engine.pause();
        rig.player.scheduleBuffer(loop, at: null, options: AVAudioPlayerNodeBufferOptions.loops);
        rig.engine.start(); rig.player.play();
        int resumed = -1;
        for (int k = 0; k < 400 && resumed < 0; k++) { var l = rig.render().l; if (k > 1 && l.Any(v => Math.Abs(v) > 1e-3)) resumed = k; }
        expect("restartBehindVarispeedResumesAtRender", resumed, 300, 1);
    }

    private static async Task<(bool ok, string json)> RealtimeCheck(SceneTree tree)
    {
        // One second of a 440 Hz tone (amplitude 0.5, player volume 0.5, main mixer 0.82) through Godot.
        int master = AudioServer.GetBusIndex("Master");
        var capture = new AudioEffectCapture { BufferLength = 4 };
        AudioServer.AddBusEffect(master, capture);
        var rig = new Rig(chain: true, useLimiter: true, offline: false);
        rig.engine.mainMixerNode.outputVolume = 0.82f;
        rig.player.scheduleBuffer(buffer(48000, (_, i) => (float)Math.Sin(2 * Math.PI * 440 * i / 48000) * 0.5f), at: null, options: AVAudioPlayerNodeBufferOptions.loops);
        rig.player.volume = 0.5f;
        rig.engine.start(); rig.player.play();
        var frames = new List<Vector2>();
        ulong startUsec = Time.GetTicksUsec();
        while (Time.GetTicksUsec() - startUsec < 1_500_000)
        {
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            int available = capture.GetFramesAvailable();
            if (available > 0) frames.AddRange(capture.GetBuffer(available));
        }
        int skips = rig.engine.realtimeSkips;
        rig.engine.pause();
        double mixRate = AudioServer.GetMixRate();
        AudioServer.RemoveBusEffect(master, AudioServer.GetBusEffectCount(master) - 1);
        // Skip start-up; measure the steady tone and look for dropouts (runs of silence inside the tone).
        int skip = (int)(0.3 * mixRate);
        var steady = frames.Skip(skip).Take((int)(0.8 * mixRate)).Select(v => v.X).ToArray();
        double level = steady.Length > 0 ? rms(steady) : 0;
        int longestSilence = 0, run = 0, gaps = 0;
        foreach (var v in steady)
        {
            run = Math.Abs(v) < 1e-4 ? run + 1 : 0;
            if (run == 32) gaps++;
            longestSilence = Math.Max(longestSilence, run);
        }
        double expected = 0.5 * 0.5 * 0.82 / Math.Sqrt(2);
        bool ok = steady.Length > mixRate * 0.5 && Math.Abs(level - expected) < expected * 0.05 && longestSilence < 8;
        var json = string.Format(CultureInfo.InvariantCulture, "{{ \"driver\" : \"{0}\", \"mixRate\" : {1}, \"outputLatency\" : {2:R}, \"capturedFrames\" : {3}, \"rms\" : {4:R}, \"expectedRms\" : {5:R}, \"longestSilentRun\" : {6}, \"gaps\" : {7}, \"generatorSkips\" : {8}, \"ok\" : {9} }}",
            AudioServer.GetDriverName(), mixRate, AudioServer.GetOutputLatency(), frames.Count, level, expected, longestSilence, gaps, skips, ok ? "true" : "false");
        return (ok, json);
    }
}
