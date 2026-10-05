using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Godot;

namespace Marvin.SceneKit;

// AVAudioEngine facade: the node graph the game builds (players, varispeeds, EQs, mixers, an Apple
// effect unit) rendered by a sample-accurate C# mixer at the connection format's rate. Offline manual
// rendering (renderOffline) renders synchronously; a running real-time engine feeds a Godot
// AudioStreamGenerator from a render thread, the way Core Audio pulls the graph on its I/O thread.
//
// DSP measured against AVAudioEngine on macOS 27 (offline manual rendering, 48 kHz Float32 stereo;
// probes in the audio stream's report):
// - Mixer inputs: the AVAudioMixing volume/pan of the source node apply at the downstream mixer's input,
//   also through effect nodes (player -> varispeed -> EQ -> mixer). Stereo pan is a balance law:
//   left x min(1, 1 - pan), right x min(1, 1 + pan), no crossfeed. Each channel gain slews linearly
//   towards its target at 1/1200 per sample (a full 0 -> 1 change takes 25 ms), sample by sample across
//   render calls; the gain applies to a sample before the step. On the engine's first render every input
//   starts at its current target. An input whose source produced nothing (a stopped player connected
//   directly to the mixer) keeps its gain frozen; an effect chain is always active.
// - outputVolume changes ramp linearly across one render call.
// - AVAudioUnitVarispeed: rate (playback rate = pitch) applies from the next render call; the input is
//   delayed by 48 input frames. PORT: resampled with a 24-tap Kaiser-windowed sinc (unity DC gain, flat at
//   rate 1, low-passed at 1/rate above unity) rather than Apple's converter; levels match within 0.002 dB.
// - AVAudioUnitEQ .lowPass: second-order Butterworth (bilinear, -3.01 dB at the frequency). A frequency
//   change glides log-linearly over 1440 frames (30 ms), restarting from the current value.
// - AUPeakLimiter (attack 2 ms, release 5 ms, pre-gain 0 dB): delays by the attack time (96 frames) and
//   reproduces Apple's gain computer (see PeakLimiterDSP).
// - AVAudioPlayerNode: a scheduled buffer starts at the first frame of the next render call, but play()
//   starts at the engine's sample time on the player's own render timeline. Behind a varispeed that
//   timeline runs at the varispeed's rate, so after an offline pause/start a slowed-down loop stays silent
//   until it catches up (the macOS --audio-smoke-test WAVs show these gaps); real-time engines restart
//   their timeline at start(), so live playback resumes at once.
// - The real-time engine's output is a Godot AudioStreamPlayer (48 kHz AudioStreamGenerator, ~32 ms queued).
// Checked against the macOS numbers by `--audio-facade-test DIR` (AudioFacadeTest.cs).

public enum AVAudioEngineManualRenderingMode { offline = 0, realtime = 1 }
public enum AVAudioEngineManualRenderingStatus { error = -1, success = 0, insufficientDataFromInputNode = 1, cannotDoInCurrentContext = 2 }
[Flags]
public enum AVAudioPlayerNodeBufferOptions { loops = 1 << 0, interrupts = 1 << 1, interruptsAtLoop = 1 << 2 }
public enum AVAudioUnitEQFilterType
{
    parametric = 0, lowPass = 1, highPass = 2, resonantLowPass = 3, resonantHighPass = 4, bandPass = 5, bandStop = 6,
    lowShelf = 7, highShelf = 8, resonantLowShelf = 9, resonantHighShelf = 10,
}

/// <summary>AudioComponentDescription (AudioToolbox).</summary>
public struct AudioComponentDescription
{
    public uint componentType, componentSubType, componentManufacturer, componentFlags, componentFlagsMask;
    public AudioComponentDescription(uint componentType, uint componentSubType, uint componentManufacturer, uint componentFlags, uint componentFlagsMask)
    {
        this.componentType = componentType; this.componentSubType = componentSubType; this.componentManufacturer = componentManufacturer;
        this.componentFlags = componentFlags; this.componentFlagsMask = componentFlagsMask;
    }
}

/// <summary>AVAudioTime (sample time only).</summary>
public sealed class AVAudioTime
{
    public AVAudioFramePosition sampleTime { get; }
    public double sampleRate { get; }
    public bool isSampleTimeValid => true;
    public AVAudioTime(AVAudioFramePosition sampleTime, double atRate) { this.sampleTime = sampleTime; sampleRate = atRate; }
}

/// <summary>AVAudioMixing (AVAudioStereoMixing part): volume and pan applied at the downstream mixer's input.</summary>
public interface AVAudioMixing
{
    float volume { get; set; }
    float pan { get; set; }
}

/// <summary>A connection between an output bus and an input bus; mixer inputs keep their ramp state here.</summary>
internal sealed class AudioConnection
{
    public AVAudioNode source, destination;
    public int sourceBus, destinationBus;
    public AVAudioFormat format;
    public float gainL, gainR;
    public bool mixInitialized;
    public float[] l = Array.Empty<float>(), r = Array.Empty<float>();
    public void EnsureScratch(int frames)
    {
        if (l.Length >= frames) return;
        l = new float[frames]; r = new float[frames];
    }
}

/// <summary>AVAudioNode. Rendering is pull based: a node renders its input into the arrays it is given.</summary>
public abstract class AVAudioNode
{
    public AVAudioEngine engine { get; internal set; }
    internal readonly List<AudioConnection> inputs = new();
    internal AudioConnection output;
    public virtual int numberOfInputs => 1;
    public virtual int numberOfOutputs => 1;
    /// <summary>Processing latency in seconds (the delay this node adds).</summary>
    public double latency => latencyFrames / (outputFormat(0)?.sampleRate ?? 48000.0);
    internal virtual int latencyFrames => 0;
    public AVAudioFormat outputFormat(int forBus) => output?.format ?? engine?.defaultFormat;
    public AVAudioFormat inputFormat(int forBus) => forBus < inputs.Count ? inputs[forBus]?.format : null;
    /// <summary>Clears the node's DSP state (filter memories, delay lines, ramps).</summary>
    public void reset() { lock (gate) ResetState(); }
    internal virtual void ResetState() { }
    internal object gate => (object)engine?.gate ?? this;
    internal AudioConnection InputConnection(int bus) => bus < inputs.Count ? inputs[bus] : null;

    /// <summary>
    /// Renders <paramref name="frames"/> frames starting at <paramref name="sampleTime"/> (this node's render
    /// timeline: the engine's sample time, or a varispeed's input timeline upstream of it) into
    /// <paramref name="left"/>/<paramref name="right"/>. <paramref name="silent"/> means the caller multiplies the
    /// result by zero: the node may skip computing samples but must advance its state. Returns whether the node
    /// produced data (a stopped player does not).
    /// </summary>
    internal abstract bool Render(long sampleTime, int frames, float[] left, float[] right, bool silent);
    /// <summary>The sample time of the node's last render call (lastRenderTime.sampleTime).</summary>
    internal long lastSampleTime = -1;
    /// <summary>lastRenderTime: the start of the node's last render cycle on its own timeline (nil before the first).</summary>
    public AVAudioTime lastRenderTime => lastSampleTime < 0 ? null : new AVAudioTime(lastSampleTime, atRate: outputFormat(0)?.sampleRate ?? 48000);

    internal bool PullInput(int bus, long sampleTime, int frames, float[] left, float[] right, bool silent)
    {
        var c = InputConnection(bus);
        if (c == null) { Array.Clear(left, 0, frames); Array.Clear(right, 0, frames); return false; }
        c.source.lastSampleTime = sampleTime;
        return c.source.Render(sampleTime, frames, left, right, silent);
    }
}

/// <summary>AVAudioPlayerNode: plays scheduled buffers (optionally looping) from the next render call.</summary>
public sealed class AVAudioPlayerNode : AVAudioNode, AVAudioMixing
{
    public float volume { get; set; } = 1;
    public float pan { get; set; } = 0;
    public bool isPlaying { get; private set; }
    public override int numberOfInputs => 0;

    private sealed class Scheduled { public AVAudioPCMBuffer buffer; public bool loops, endAtLoop; public Action completion; }
    private readonly List<Scheduled> queue = new();
    private int position;
    private readonly List<Action> finished = new();

    /// <summary>scheduleBuffer(_:at:options:completionHandler:). Only <c>at: nil</c> (play when the previous buffers finish) is supported.</summary>
    public void scheduleBuffer(AVAudioPCMBuffer buffer, AVAudioTime at, AVAudioPlayerNodeBufferOptions options = 0, Action completionHandler = null)
    {
        // PORT: the game always schedules at nil; sample-time scheduling is not implemented.
        if (at != null) throw new NotSupportedException("AVAudioPlayerNode.scheduleBuffer(at:) with a time is not implemented by the facade");
        List<Action> flushed = null;
        lock (gate)
        {
            if ((options & AVAudioPlayerNodeBufferOptions.interrupts) != 0)
            {
                foreach (var s in queue) if (s.completion != null) (flushed ??= new()).Add(s.completion);
                queue.Clear(); position = 0;
            }
            else if ((options & AVAudioPlayerNodeBufferOptions.interruptsAtLoop) != 0)
                foreach (var s in queue) s.endAtLoop = true;
            queue.Add(new Scheduled { buffer = buffer, loops = (options & AVAudioPlayerNodeBufferOptions.loops) != 0, completion = completionHandler });
        }
        if (flushed != null) foreach (var f in flushed) f();
    }
    /// <summary>scheduleBuffer(_:completionHandler:).</summary>
    public void scheduleBuffer(AVAudioPCMBuffer buffer, Action completionHandler = null) => scheduleBuffer(buffer, null, 0, completionHandler);

    /// <summary>
    /// play(): starts at the engine's current sample time, compared with this node's own render timeline.
    /// Measured: behind a varispeed the player's timeline advances at the varispeed's rate, so offline (where
    /// the engine's sample time continues across pause/start) a restarted player stays silent until its slower
    /// timeline reaches the engine time of the play() call; a real-time engine restarts its timeline at start().
    /// </summary>
    public void play()
    {
        lock (gate)
        {
            if (!isPlaying) startTime = engine?.currentSampleTime ?? 0;
            isPlaying = true;
        }
    }
    private long startTime;
    public void pause() { lock (gate) isPlaying = false; }
    /// <summary>Stops playback and flushes every scheduled buffer (their completion handlers run).</summary>
    public void stop()
    {
        List<Action> flushed = null;
        lock (gate)
        {
            isPlaying = false;
            foreach (var s in queue) if (s.completion != null) (flushed ??= new()).Add(s.completion);
            queue.Clear(); position = 0;
        }
        if (flushed != null) foreach (var f in flushed) f();
    }

    internal override bool Render(long sampleTime, int frames, float[] left, float[] right, bool silent)
    {
        if (!isPlaying) { Array.Clear(left, 0, frames); Array.Clear(right, 0, frames); return false; }
        int i = (int)Math.Clamp(startTime - sampleTime, 0, frames);
        if (i > 0) { Array.Clear(left, 0, i); Array.Clear(right, 0, i); }
        while (i < frames)
        {
            if (queue.Count == 0) { Array.Clear(left, i, frames - i); Array.Clear(right, i, frames - i); break; }
            var item = queue[0];
            int length = (int)item.buffer.frameLength;
            int n = Math.Min(frames - i, length - position);
            if (n > 0 && !silent)
            {
                var data = item.buffer.floatChannelData;
                Array.Copy(data[0], position, left, i, n);
                Array.Copy(data[Math.Min(1, data.Length - 1)], position, right, i, n);
            }
            i += Math.Max(0, n); position += Math.Max(0, n);
            if (position >= length)
            {
                position = 0;
                if (!item.loops || item.endAtLoop || length == 0)
                {
                    queue.RemoveAt(0);
                    if (item.completion != null) finished.Add(item.completion);
                }
            }
        }
        if (finished.Count > 0) { var done = finished.ToArray(); finished.Clear(); engine?.DeferCompletions(done); }
        return true;
    }
}

/// <summary>AVAudioUnit: an Apple audio unit node.</summary>
public abstract class AVAudioUnit : AVAudioNode
{
    public AudioComponentDescription audioComponentDescription { get; protected set; }
    public string manufacturerName => "Apple";
    public virtual string name => "";
}

/// <summary>AVAudioUnitTimeEffect (varispeed, time pitch): rate-changing effects with one input.</summary>
public abstract class AVAudioUnitTimeEffect : AVAudioUnit
{
    public bool bypass { get; set; }
}

/// <summary>AVAudioUnitVarispeed: playback rate (pitch and tempo together), 0.25 ... 4.</summary>
public sealed class AVAudioUnitVarispeed : AVAudioUnitTimeEffect
{
    public float rate { get; set; } = 1;
    public override string name => "AUVarispeed";
    public AVAudioUnitVarispeed() { audioComponentDescription = new AudioComponentDescription(kAudioUnitType_FormatConverter, kAudioUnitSubType_Varispeed, kAudioUnitManufacturer_Apple, 0, 0); }

    // Measured: Apple's varispeed delays its input by about 48 input frames (an impulse at rate 1 comes out
    // over output frames 48 and 49 behind the lowPass-18000 EQ, energy centroid 48.7; at rate 0.7 near frame 69).
    // The history starts with that many zeros; an integer delay keeps rate 1 an exact pass-through.
    private const double LatencyFrames = 48;
    private const int InitialCount = SincKernel.MaxHalf - 1 + 48;
    private const double InitialPosition = InitialCount - LatencyFrames;
    private float[] historyL = new float[4096], historyR = new float[4096], pullL = Array.Empty<float>(), pullR = Array.Empty<float>();
    private int count = InitialCount;
    private double position = InitialPosition;
    private long inputTime;
    internal override int latencyFrames => bypass ? 0 : (int)Math.Round(LatencyFrames / Math.Clamp(rate, 0.25f, 4f));

    internal override void ResetState()
    {
        Array.Clear(historyL); Array.Clear(historyR);
        count = InitialCount; position = InitialPosition;
    }

    internal override bool Render(long sampleTime, int frames, float[] left, float[] right, bool silent)
    {
        if (bypass) return PullInput(0, sampleTime, frames, left, right, silent);
        double r = Math.Clamp((double)rate, 0.25, 4.0);
        var kernel = SincKernel.For(r);
        int half = kernel.half, taps = kernel.taps;
        long last = (long)Math.Floor(position + (frames - 1) * r) + half;
        int need = (int)(last + 1 - count);
        if (need > 0)
        {
            if (count + need > historyL.Length)
            {
                int size = historyL.Length;
                while (size < count + need) size *= 2;
                Array.Resize(ref historyL, size); Array.Resize(ref historyR, size);
            }
            if (pullL.Length < need) { pullL = new float[need]; pullR = new float[need]; }
            // The input's timeline is the frames this varispeed has pulled so far.
            PullInput(0, inputTime, need, pullL, pullR, false);
            inputTime += need;
            Array.Copy(pullL, 0, historyL, count, need);
            Array.Copy(pullR, 0, historyR, count, need);
            count += need;
        }
        if (!silent)
        {
            var table = kernel.table;
            int lanes = System.Numerics.Vector<float>.Count, vectorTaps = taps - taps % lanes;
            for (int i = 0; i < frames; i++)
            {
                double p = position + i * r;
                int ip = (int)Math.Floor(p);
                int phase = (int)((p - ip) * SincKernel.Phases + 0.5);
                int offset = phase * taps, start = ip - half + 1;
                // SIMD dot products of the polyphase row with both channels' history.
                var accL = System.Numerics.Vector<float>.Zero;
                var accR = System.Numerics.Vector<float>.Zero;
                for (int k = 0; k < vectorTaps; k += lanes)
                {
                    var c = new System.Numerics.Vector<float>(table, offset + k);
                    accL += c * new System.Numerics.Vector<float>(historyL, start + k);
                    accR += c * new System.Numerics.Vector<float>(historyR, start + k);
                }
                float sumL = System.Numerics.Vector.Sum(accL), sumR = System.Numerics.Vector.Sum(accR);
                for (int k = vectorTaps; k < taps; k++)
                {
                    float c = table[offset + k];
                    sumL += c * historyL[start + k];
                    sumR += c * historyR[start + k];
                }
                left[i] = sumL; right[i] = sumR;
            }
        }
        position += frames * r;
        int keepFrom = (int)Math.Floor(position) - SincKernel.MaxHalf + 1;
        if (keepFrom > 0)
        {
            Array.Copy(historyL, keepFrom, historyL, 0, count - keepFrom);
            Array.Copy(historyR, keepFrom, historyR, 0, count - keepFrom);
            count -= keepFrom; position -= keepFrom;
        }
        return true;
    }
}

/// <summary>Kaiser-windowed sinc interpolation tables (polyphase), cached per cutoff.</summary>
internal sealed class SincKernel
{
    public const int Phases = 1024, BaseHalf = 12, MaxHalf = 48;
    private const double Beta = 7.0, CutoffScale = 1.0;
    public readonly int half, taps;
    public readonly float[] table;
    private static readonly ConcurrentDictionary<int, SincKernel> cache = new();

    public static SincKernel For(double rate)
    {
        // Quantise the anti-aliasing cutoff (1/rate above unity rate) to 1/64 steps.
        int key = (int)Math.Round(Math.Min(1.0, 1.0 / rate) * 64);
        return cache.GetOrAdd(Math.Max(16, key), k => new SincKernel(k / 64.0));
    }

    private SincKernel(double cutoff)
    {
        half = Math.Min(MaxHalf, (int)Math.Ceiling(BaseHalf / cutoff));
        taps = 2 * half;
        double fc = cutoff * CutoffScale;
        table = new float[(Phases + 1) * taps];
        double i0Beta = BesselI0(Beta);
        var row = new double[taps];
        for (int phase = 0; phase <= Phases; phase++)
        {
            double frac = (double)phase / Phases, sum = 0;
            for (int k = 0; k < taps; k++)
            {
                double t = (k - half + 1) - frac;
                double x = t / half;
                double window = Math.Abs(x) >= 1 ? 0 : BesselI0(Beta * Math.Sqrt(1 - x * x)) / i0Beta;
                double sinc = t == 0 ? 1 : Math.Sin(Math.PI * fc * t) / (Math.PI * fc * t);
                row[k] = fc * sinc * window;
                sum += row[k];
            }
            for (int k = 0; k < taps; k++) table[phase * taps + k] = (float)(row[k] / sum);
        }
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, q = x * x / 4;
        for (int k = 1; k < 64; k++) { term *= q / (k * k); sum += term; if (term < sum * 1e-17) break; }
        return sum;
    }
}

/// <summary>AVAudioUnitEffect: an Apple effect audio unit. The facade implements AUPeakLimiter (and AUNBandEQ via AVAudioUnitEQ).</summary>
public class AVAudioUnitEffect : AVAudioUnit
{
    public bool bypass { get; set; }
    private readonly PeakLimiterDSP limiter;

    public AVAudioUnitEffect(AudioComponentDescription audioComponentDescription)
    {
        this.audioComponentDescription = audioComponentDescription;
        if (audioComponentDescription.componentType == kAudioUnitType_Effect && audioComponentDescription.componentSubType == kAudioUnitSubType_PeakLimiter)
            limiter = new PeakLimiterDSP();
        else if (!(audioComponentDescription.componentType == kAudioUnitType_Effect && audioComponentDescription.componentSubType == kAudioUnitSubType_NBandEQ))
            // PORT: only the audio units the game instantiates are implemented.
            throw new NotSupportedException($"AVAudioUnitEffect: audio unit {audioComponentDescription.componentType:x8}/{audioComponentDescription.componentSubType:x8} is not implemented by the facade");
    }
    public override string name => limiter != null ? "AUPeakLimiter" : "AUNBandEQ";
    internal override int latencyFrames => limiter != null && !bypass ? PeakLimiterDSP.Lookahead : 0;
    internal override void ResetState() => limiter?.Reset();

    internal override bool Render(long sampleTime, int frames, float[] left, float[] right, bool silent)
    {
        // The limiter delays audio, so it processes even when the result is unused.
        bool active = PullInput(0, sampleTime, frames, left, right, silent && limiter == null);
        if (!bypass) Process(frames, left, right, silent);
        return active || limiter != null;
    }
    internal virtual void Process(int frames, float[] left, float[] right, bool silent) => limiter?.Process(frames, left, right);
}

/// <summary>
/// AUPeakLimiter with its default parameters (attack 2 ms, release 5 ms, pre-gain 0 dB), reverse engineered
/// from AVAudioEngine renders on macOS 27; it matches Apple's output to within 1e-5 on steps, spikes, spike
/// trains, sines (50 Hz - 5 kHz, 1.1 - 4x full scale) and noise, apart from the occasional one-frame difference
/// in where a release starts. The stereo-linked peak of each incoming frame (lookahead = attack time):
/// - above 0 dBFS, and louder than the loudest frame of the current hold window, it requests a gain ramp in dB
///   of slope (-peak_dB - gain_dB) / (lookahead - 10) per frame, over the next lookahead frames (a steeper
///   request replaces the slope, any request restarts the ramp end), so ramps overshoot to gain = peak^-(96/86);
/// - any frame above 0 dBFS holds the gain (no release) until it has left the lookahead window;
/// - otherwise the gain recovers with a half-life of the release time: g = 1 - (1 - g) * 2^(-1/(release*rate)).
/// The output is the input delayed by the lookahead, times the gain.
/// </summary>
internal sealed class PeakLimiterDSP
{
    public const int Lookahead = 96; // attack time 0.002 s at 48 kHz
    private const double RampFrames = Lookahead - 10;
    private static readonly double ReleaseCoefficient = Math.Pow(2, -1.0 / 240); // release 0.005 s at 48 kHz
    private readonly float[] delayL = new float[Lookahead], delayR = new float[Lookahead];
    private int cursor;
    private long frame;
    private double gain = 1, gainDB = 0, slope = 0, peak = 0;
    private long rampEnd = -1, holdEnd = -1;

    public void Reset()
    {
        Array.Clear(delayL); Array.Clear(delayR); cursor = 0; frame = 0;
        gain = 1; gainDB = 0; slope = 0; peak = 0; rampEnd = -1; holdEnd = -1;
    }

    public void Process(int frames, float[] left, float[] right)
    {
        for (int i = 0; i < frames; i++, frame++)
        {
            float inL = left[i], inR = right[i];
            double x = Math.Max(Math.Abs(inL), Math.Abs(inR));
            if (x > 1)
            {
                if (frame > holdEnd + 1) peak = 0;
                if (x > peak)
                {
                    double s = (-20 * Math.Log10(x) - gainDB) / RampFrames;
                    if (s < 0) { slope = frame <= rampEnd ? Math.Min(slope, s) : s; rampEnd = frame + Lookahead - 1; }
                    peak = x;
                    holdEnd = frame + Lookahead - 1;
                }
                else holdEnd = frame + Lookahead;
            }
            if (frame <= rampEnd) { gainDB += slope; gain = Math.Pow(10, gainDB / 20); }
            else if (frame > holdEnd && gain < 1) { gain = 1 - (1 - gain) * ReleaseCoefficient; gainDB = 20 * Math.Log10(gain); }
            float outL = delayL[cursor], outR = delayR[cursor];
            delayL[cursor] = inL; delayR[cursor] = inR;
            cursor = cursor + 1 == Lookahead ? 0 : cursor + 1;
            left[i] = (float)(outL * gain); right[i] = (float)(outR * gain);
        }
    }
}

/// <summary>AVAudioUnitEQFilterParameters: one band of an AVAudioUnitEQ.</summary>
public sealed class AVAudioUnitEQFilterParameters
{
    public AVAudioUnitEQFilterType filterType { get; set; } = AVAudioUnitEQFilterType.parametric;
    public float frequency { get; set; } = 40;
    public float bandwidth { get; set; } = 0.5f;
    public float gain { get; set; } = 0;
    public bool bypass { get; set; } = true;

    // DSP state.
    internal const int GlideFrames = 1440, CoefficientBlock = 16;
    internal bool initialized;
    internal double current, glideFrom, glideTo;
    internal int glideDone = GlideFrames;
    private (AVAudioUnitEQFilterType type, double frequency, float bandwidth, float gain) designed;
    private double b0, b1, b2, a1, a2, z1L, z2L, z1R, z2R;

    internal void ResetState() { initialized = false; z1L = z2L = z1R = z2R = 0; glideDone = GlideFrames; }

    internal void Process(int frames, float[] left, float[] right, bool silent, double sampleRate)
    {
        double target = Math.Clamp((double)frequency, 10, sampleRate * 0.49);
        if (!initialized) { current = glideFrom = glideTo = target; glideDone = GlideFrames; initialized = true; }
        else if (target != glideTo) { glideFrom = current; glideTo = target; glideDone = 0; }
        if (silent) { z1L = z2L = z1R = z2R = 0; Glide(frames); return; }
        for (int start = 0; start < frames; start += CoefficientBlock)
        {
            int n = Math.Min(CoefficientBlock, frames - start);
            Design(sampleRate);
            for (int i = start; i < start + n; i++)
            {
                double x = left[i], y = b0 * x + z1L;
                z1L = b1 * x - a1 * y + z2L; z2L = b2 * x - a2 * y; left[i] = (float)y;
                x = right[i]; y = b0 * x + z1R;
                z1R = b1 * x - a1 * y + z2R; z2R = b2 * x - a2 * y; right[i] = (float)y;
            }
            Glide(n);
        }
    }

    private void Glide(int frames)
    {
        if (glideDone >= GlideFrames) { current = glideTo; return; }
        glideDone = Math.Min(GlideFrames, glideDone + frames);
        current = glideFrom * Math.Pow(glideTo / glideFrom, (double)glideDone / GlideFrames);
    }

    /// <summary>RBJ biquads; .lowPass/.highPass are Butterworth (Q = 1/sqrt 2), bandwidth is in octaves.</summary>
    private void Design(double sampleRate)
    {
        var key = (filterType, current, bandwidth, gain);
        if (key == designed && b0 != 0) return;
        designed = key;
        double w0 = 2 * Math.PI * current / sampleRate, cos = Math.Cos(w0), sin = Math.Sin(w0);
        double bw = Math.Clamp((double)bandwidth, 0.05, 5.0);
        double alphaBW = sin * Math.Sinh(Math.Log(2) / 2 * bw * w0 / sin);
        double alphaQ = sin / (2 / Math.Sqrt(2));
        double A = Math.Pow(10, gain / 40.0);
        double nb0, nb1, nb2, na0, na1, na2;
        switch (filterType)
        {
            case AVAudioUnitEQFilterType.lowPass:
            case AVAudioUnitEQFilterType.resonantLowPass:
            {
                double alpha = filterType == AVAudioUnitEQFilterType.lowPass ? alphaQ : alphaBW;
                nb0 = (1 - cos) / 2; nb1 = 1 - cos; nb2 = (1 - cos) / 2; na0 = 1 + alpha; na1 = -2 * cos; na2 = 1 - alpha; break;
            }
            case AVAudioUnitEQFilterType.highPass:
            case AVAudioUnitEQFilterType.resonantHighPass:
            {
                double alpha = filterType == AVAudioUnitEQFilterType.highPass ? alphaQ : alphaBW;
                nb0 = (1 + cos) / 2; nb1 = -(1 + cos); nb2 = (1 + cos) / 2; na0 = 1 + alpha; na1 = -2 * cos; na2 = 1 - alpha; break;
            }
            case AVAudioUnitEQFilterType.bandPass:
                nb0 = alphaBW; nb1 = 0; nb2 = -alphaBW; na0 = 1 + alphaBW; na1 = -2 * cos; na2 = 1 - alphaBW; break;
            case AVAudioUnitEQFilterType.bandStop:
                nb0 = 1; nb1 = -2 * cos; nb2 = 1; na0 = 1 + alphaBW; na1 = -2 * cos; na2 = 1 - alphaBW; break;
            case AVAudioUnitEQFilterType.lowShelf:
            case AVAudioUnitEQFilterType.resonantLowShelf:
            {
                double alpha = filterType == AVAudioUnitEQFilterType.lowShelf ? sin / 2 * Math.Sqrt(2) : alphaBW, s = 2 * Math.Sqrt(A) * alpha;
                nb0 = A * ((A + 1) - (A - 1) * cos + s); nb1 = 2 * A * ((A - 1) - (A + 1) * cos); nb2 = A * ((A + 1) - (A - 1) * cos - s);
                na0 = (A + 1) + (A - 1) * cos + s; na1 = -2 * ((A - 1) + (A + 1) * cos); na2 = (A + 1) + (A - 1) * cos - s; break;
            }
            case AVAudioUnitEQFilterType.highShelf:
            case AVAudioUnitEQFilterType.resonantHighShelf:
            {
                double alpha = filterType == AVAudioUnitEQFilterType.highShelf ? sin / 2 * Math.Sqrt(2) : alphaBW, s = 2 * Math.Sqrt(A) * alpha;
                nb0 = A * ((A + 1) + (A - 1) * cos + s); nb1 = -2 * A * ((A - 1) + (A + 1) * cos); nb2 = A * ((A + 1) + (A - 1) * cos - s);
                na0 = (A + 1) - (A - 1) * cos + s; na1 = 2 * ((A - 1) - (A + 1) * cos); na2 = (A + 1) - (A - 1) * cos - s; break;
            }
            default: // parametric peak
                nb0 = 1 + alphaBW * A; nb1 = -2 * cos; nb2 = 1 - alphaBW * A; na0 = 1 + alphaBW / A; na1 = -2 * cos; na2 = 1 - alphaBW / A; break;
        }
        b0 = nb0 / na0; b1 = nb1 / na0; b2 = nb2 / na0; a1 = na1 / na0; a2 = na2 / na0;
    }
}

/// <summary>AVAudioUnitEQ(numberOfBands:): AUNBandEQ. Measured: .lowPass is a bilinear Butterworth; bands start bypassed at 40 Hz.</summary>
public sealed class AVAudioUnitEQ : AVAudioUnitEffect
{
    public AVAudioUnitEQFilterParameters[] bands { get; }
    public float globalGain { get; set; }
    public AVAudioUnitEQ() : this(numberOfBands: 1) { }
    public AVAudioUnitEQ(int numberOfBands)
        : base(new AudioComponentDescription(kAudioUnitType_Effect, kAudioUnitSubType_NBandEQ, kAudioUnitManufacturer_Apple, 0, 0))
    {
        bands = new AVAudioUnitEQFilterParameters[numberOfBands];
        for (int i = 0; i < numberOfBands; i++) bands[i] = new AVAudioUnitEQFilterParameters();
    }
    internal override void ResetState() { foreach (var b in bands) b.ResetState(); }
    internal override void Process(int frames, float[] left, float[] right, bool silent)
    {
        double rate = outputFormat(0)?.sampleRate ?? 48000;
        foreach (var band in bands)
        {
            if (band.bypass) { band.ResetState(); continue; }
            band.Process(frames, left, right, silent, rate);
        }
        if (globalGain != 0 && !silent)
        {
            float g = MathF.Pow(10, globalGain / 20);
            for (int i = 0; i < frames; i++) { left[i] *= g; right[i] *= g; }
        }
    }
}

/// <summary>AVAudioMixerNode: sums its inputs with each source's AVAudioMixing volume/pan (see the measured ramps above).</summary>
public sealed class AVAudioMixerNode : AVAudioNode, AVAudioMixing
{
    public float outputVolume { get; set; } = 1;
    public float volume { get; set; } = 1;
    public float pan { get; set; } = 0;
    public override int numberOfInputs => Math.Max(1, inputs.Count);
    public int nextAvailableInputBus { get { int i = 0; while (i < inputs.Count && inputs[i] != null) i++; return i; } }

    private const float VolumeSlewPerSecond = 40; // 1/1200 per frame at 48 kHz
    private bool outputInitialized;
    private float currentOutputVolume;

    internal override void ResetState()
    {
        outputInitialized = false;
        foreach (var c in inputs) if (c != null) c.mixInitialized = false;
    }

    internal static AVAudioMixing MixingSource(AVAudioNode node)
    {
        for (int depth = 0; node != null && depth < 64; depth++)
        {
            if (node is AVAudioMixing mixing) return mixing;
            if (node.inputs.Count != 1 || node.inputs[0] == null) return null;
            node = node.inputs[0].source;
        }
        return null;
    }

    internal override bool Render(long sampleTime, int frames, float[] left, float[] right, bool silent)
    {
        Array.Clear(left, 0, frames); Array.Clear(right, 0, frames);
        float step = VolumeSlewPerSecond / (float)(outputFormat(0)?.sampleRate ?? 48000.0);
        foreach (var c in inputs)
        {
            if (c == null) continue;
            var source = MixingSource(c.source);
            float v = source?.volume ?? 1, p = Math.Clamp(source?.pan ?? 0, -1, 1);
            float targetL = v * Math.Min(1, 1 - p), targetR = v * Math.Min(1, 1 + p);
            if (!c.mixInitialized) { c.gainL = targetL; c.gainR = targetR; c.mixInitialized = true; }
            float gl = c.gainL, gr = c.gainR;
            bool inputSilent = silent || (gl == 0 && gr == 0 && targetL == 0 && targetR == 0);
            c.EnsureScratch(frames);
            c.source.lastSampleTime = sampleTime;
            bool active = c.source.Render(sampleTime, frames, c.l, c.r, inputSilent);
            if (!active) continue; // frozen ramp: the source produced nothing
            if (inputSilent)
            {
                c.gainL = Toward(gl, targetL, step * frames); c.gainR = Toward(gr, targetR, step * frames);
                continue;
            }
            float[] sl = c.l, sr = c.r;
            if (gl == targetL && gr == targetR)
            {
                for (int i = 0; i < frames; i++) { left[i] += sl[i] * gl; right[i] += sr[i] * gr; }
            }
            else
            {
                for (int i = 0; i < frames; i++)
                {
                    left[i] += sl[i] * gl; right[i] += sr[i] * gr;
                    gl = Toward(gl, targetL, step); gr = Toward(gr, targetR, step);
                }
            }
            c.gainL = gl; c.gainR = gr;
        }
        float target = outputVolume;
        if (!outputInitialized) { currentOutputVolume = target; outputInitialized = true; }
        if (currentOutputVolume == target)
        {
            if (target != 1) for (int i = 0; i < frames; i++) { left[i] *= target; right[i] *= target; }
        }
        else
        {
            float from = currentOutputVolume;
            for (int i = 0; i < frames; i++)
            {
                float g = from + (target - from) * i / frames;
                left[i] *= g; right[i] *= g;
            }
            currentOutputVolume = target;
        }
        return true;
    }

    private static float Toward(float value, float target, float step) =>
        value < target ? Math.Min(value + step, target) : Math.Max(value - step, target);
}

/// <summary>AVAudioOutputNode: the engine's output (the device, or the manual rendering buffer).</summary>
public sealed class AVAudioOutputNode : AVAudioNode
{
    public override int numberOfOutputs => 0;
    internal override bool Render(long sampleTime, int frames, float[] left, float[] right, bool silent) => PullInput(0, sampleTime, frames, left, right, silent);
}

/// <summary>
/// AVAudioEngine. attach/connect build the graph; start() runs it (real time through Godot, or offline after
/// enableManualRenderingMode); pause() stops pulling without resetting node state.
/// </summary>
public sealed class AVAudioEngine
{
    internal readonly object gate = new();
    private readonly HashSet<AVAudioNode> attached = new();
    private AVAudioMixerNode mainMixer;
    private RealtimeOutput realtime;
    private readonly ConcurrentQueue<Action> completions = new();
    /// <summary>Diagnostics: frames Godot's generator had to fill with silence (underruns) since the last start.</summary>
    internal int realtimeSkips => realtime?.skips ?? 0;

    public AVAudioOutputNode outputNode { get; }
    public bool isRunning { get; private set; }
    public bool isInManualRenderingMode { get; private set; }
    public AVAudioEngineManualRenderingMode manualRenderingMode { get; private set; }
    public AVAudioFormat manualRenderingFormat { get; private set; }
    public AVAudioFrameCount manualRenderingMaximumFrameCount { get; private set; }
    public AVAudioFramePosition manualRenderingSampleTime { get; private set; }
    public IReadOnlyCollection<AVAudioNode> attachedNodes { get { lock (gate) return new List<AVAudioNode>(attached); } }

    /// <summary>The device format: Godot's generator runs at 48 kHz stereo.</summary>
    internal AVAudioFormat defaultFormat => manualRenderingFormat ?? new AVAudioFormat(standardFormatWithSampleRate: 48000, channels: 2);

    public AVAudioEngine()
    {
        outputNode = new AVAudioOutputNode();
        attach(outputNode);
    }

    /// <summary>The main mixer, created and connected to the output node on first access (as in AVAudioEngine).</summary>
    public AVAudioMixerNode mainMixerNode
    {
        get
        {
            lock (gate)
            {
                if (mainMixer != null) return mainMixer;
                mainMixer = new AVAudioMixerNode();
                attach(mainMixer);
                connect(mainMixer, to: outputNode, format: defaultFormat);
                return mainMixer;
            }
        }
    }

    public void attach(AVAudioNode node)
    {
        lock (gate)
        {
            if (node.engine != null && node.engine != this) throw new InvalidOperationException("node is attached to another engine");
            node.engine = this; attached.Add(node);
        }
    }
    public void detach(AVAudioNode node)
    {
        lock (gate)
        {
            disconnectNodeInput(node); disconnectNodeOutput(node);
            attached.Remove(node); node.engine = null;
        }
    }

    /// <summary>connect(_:to:format:): output bus 0 to the destination's next available input (bus 0 for non-mixers).</summary>
    public void connect(AVAudioNode node1, AVAudioNode to, AVAudioFormat format)
    {
        int bus = to is AVAudioMixerNode m ? m.nextAvailableInputBus : 0;
        connect(node1, to, fromBus: 0, toBus: bus, format: format);
    }
    /// <summary>connect(_:to:fromBus:toBus:format:).</summary>
    public void connect(AVAudioNode node1, AVAudioNode to, int fromBus, int toBus, AVAudioFormat format)
    {
        lock (gate)
        {
            if (!attached.Contains(node1) || !attached.Contains(to)) throw new InvalidOperationException("connect: both nodes must be attached to the engine");
            if (node1.output != null) disconnectNodeOutput(node1);
            var existing = to.InputConnection(toBus);
            if (existing != null) existing.source.output = null;
            var c = new AudioConnection { source = node1, destination = to, sourceBus = fromBus, destinationBus = toBus, format = format ?? defaultFormat };
            while (to.inputs.Count <= toBus) to.inputs.Add(null);
            to.inputs[toBus] = c;
            node1.output = c;
        }
    }
    public void disconnectNodeOutput(AVAudioNode node)
    {
        lock (gate)
        {
            var c = node.output;
            if (c == null) return;
            c.destination.inputs[c.destinationBus] = null;
            node.output = null;
        }
    }
    public void disconnectNodeInput(AVAudioNode node)
    {
        lock (gate)
        {
            foreach (var c in node.inputs) if (c != null) c.source.output = null;
            node.inputs.Clear();
        }
    }

    public void prepare() { _ = mainMixerNode; }

    /// <summary>start(): throws if the engine has no path to its output.</summary>
    public void start()
    {
        lock (gate)
        {
            if (isRunning) return;
            _ = mainMixerNode;
            if (outputNode.InputConnection(0) == null) throw new InvalidOperationException("AVAudioEngine: the output node is not connected");
            // A real-time engine's output timeline restarts at every start (measured); offline it continues.
            if (!isInManualRenderingMode) currentSampleTime = 0;
            isRunning = true;
        }
        if (!isInManualRenderingMode) (realtime ??= new RealtimeOutput(this)).Start();
    }
    public void pause()
    {
        lock (gate)
        {
            if (!isRunning) return;
            isRunning = false;
        }
        realtime?.Pause();
    }
    /// <summary>stop(): stops rendering, stops the players and clears every node's DSP state.</summary>
    public void stop()
    {
        pause();
        List<AVAudioNode> nodes;
        lock (gate) nodes = new List<AVAudioNode>(attached);
        foreach (var node in nodes) { if (node is AVAudioPlayerNode player) player.stop(); node.reset(); }
    }
    /// <summary>reset(): clears every node's DSP state.</summary>
    public void reset() { lock (gate) foreach (var node in attached) node.ResetState(); }

    public void enableManualRenderingMode(AVAudioEngineManualRenderingMode mode, AVAudioFormat format, AVAudioFrameCount maximumFrameCount)
    {
        lock (gate)
        {
            if (isRunning) throw new InvalidOperationException("enableManualRenderingMode: the engine must be stopped");
            // PORT: .realtime manual rendering is rendered the same way as .offline (the caller pulls).
            isInManualRenderingMode = true; manualRenderingMode = mode;
            manualRenderingFormat = format; manualRenderingMaximumFrameCount = maximumFrameCount;
            if (mainMixer != null) outputNode.InputConnection(0).format = format;
        }
    }
    public void disableManualRenderingMode()
    {
        lock (gate) { isInManualRenderingMode = false; manualRenderingFormat = null; manualRenderingMaximumFrameCount = 0; }
    }

    /// <summary>renderOffline(_:to:): renders the graph into the buffer (frameLength = numberOfFrames).</summary>
    public AVAudioEngineManualRenderingStatus renderOffline(AVAudioFrameCount numberOfFrames, AVAudioPCMBuffer to)
    {
        if (!isInManualRenderingMode) throw new InvalidOperationException("renderOffline: the engine is not in manual rendering mode");
        if (numberOfFrames > manualRenderingMaximumFrameCount || numberOfFrames > to.frameCapacity)
            throw new ArgumentOutOfRangeException(nameof(numberOfFrames), "renderOffline: more frames than the maximum frame count or the buffer capacity");
        lock (gate)
        {
            if (!isRunning) return AVAudioEngineManualRenderingStatus.cannotDoInCurrentContext;
            var data = to.floatChannelData;
            RenderGraph((int)numberOfFrames, data[0], data[Math.Min(1, data.Length - 1)]);
            to.frameLength = numberOfFrames;
            manualRenderingSampleTime = currentSampleTime;
        }
        RunCompletions();
        return AVAudioEngineManualRenderingStatus.success;
    }

    /// <summary>Renders the graph (caller holds the gate).</summary>
    internal void RenderGraph(int frames, float[] left, float[] right)
    {
        outputNode.lastSampleTime = currentSampleTime;
        outputNode.Render(currentSampleTime, frames, left, right, false);
        currentSampleTime += frames;
    }
    /// <summary>
    /// The engine's render timeline: offline, manualRenderingSampleTime (continues across pause/start);
    /// in real time the output's sample time, which restarts at every start() (measured).
    /// </summary>
    internal long currentSampleTime;

    internal void DeferCompletions(Action[] done) { foreach (var d in done) completions.Enqueue(d); }
    internal void RunCompletions()
    {
        while (completions.TryDequeue(out var block))
        {
            try { block(); } catch (Exception e) { GD.PushError($"AVAudioPlayerNode completion failed: {e}"); }
        }
    }

    /// <summary>
    /// Real-time output: a Godot AudioStreamPlayer playing a 48 kHz AudioStreamGenerator, fed by a render
    /// thread that keeps about 32 ms queued (rendering 256 frames at a time, like an I/O cycle).
    /// </summary>
    private sealed class RealtimeOutput
    {
        private const int Chunk = 256, TargetQueued = 1536, DummyTargetQueued = 4800;
        private readonly AVAudioEngine engine;
        private AudioStreamPlayer player;
        private volatile AudioStreamGeneratorPlayback playback;
        private int emptyAvailable;
        private volatile bool wanted;
        // The headless Dummy driver mixes in bursts; nobody hears it, so it gets a 100 ms queue.
        private volatile int initialTarget = TargetQueued;
        private Thread thread;

        public RealtimeOutput(AVAudioEngine engine) { this.engine = engine; }

        public void Start()
        {
            wanted = true;
            if (SceneKitRuntime.OnMainThread) initialTarget = AudioServer.GetDriverName() == "Dummy" ? DummyTargetQueued : TargetQueued;
            if (!SceneKitRuntime.OnMainThread) { Callable.From(Start).CallDeferred(); return; }
            if (player == null || !GodotObject.IsInstanceValid(player))
            {
                var generator = new AudioStreamGenerator { MixRate = 48000, BufferLength = 0.25f };
                // Always processes: Core Audio keeps running when the app pauses its scene; the game stops the engine itself.
                player = new AudioStreamPlayer { Name = "AVAudioEngineOutput", Stream = generator, ProcessMode = Node.ProcessModeEnum.Always };
                player.Ready += Play;
                if (Engine.GetMainLoop() is SceneTree tree) tree.Root.CallDeferred(Node.MethodName.AddChild, player);
            }
            Play();
            if (thread == null)
            {
                thread = new Thread(Loop) { IsBackground = true, Name = "AVAudioEngine render", Priority = ThreadPriority.AboveNormal };
                thread.Start();
            }
        }

        public int skips => playback?.GetSkips() ?? 0;

        private void Play()
        {
            if (!wanted || player == null || !player.IsInsideTree() || playback != null) return;
            player.Play();
            var p = (AudioStreamGeneratorPlayback)player.GetStreamPlayback();
            emptyAvailable = p.GetFramesAvailable();
            playback = p;
        }

        public void Pause()
        {
            wanted = false;
            lock (engine.gate) playback = null; // waits for a render in progress
            if (!SceneKitRuntime.OnMainThread) { Callable.From(StopPlayer).CallDeferred(); return; }
            StopPlayer();
        }

        private void StopPlayer()
        {
            if (!wanted && player != null && GodotObject.IsInstanceValid(player) && player.IsInsideTree()) player.Stop();
        }

        private void Loop()
        {
            var left = new float[Chunk];
            var right = new float[Chunk];
            var frames = new Vector2[Chunk];
            AudioStreamGeneratorPlayback watched = null;
            int target = initialTarget, seenSkips = 0;
            long settleAt = 0;
            while (true)
            {
                var p = playback;
                if (p == null) { Thread.Sleep(4); continue; }
                // exit() quits Godot while the engine may still run (macOS ends the process, and Core Audio with it):
                // the tree frees the generator's playback under this thread. Stop feeding it instead of crashing.
                if (!GodotObject.IsInstanceValid(p)) { if (playback == p) { playback = null; } Thread.Sleep(4); continue; }
                try { Feed(p); }
                catch (ObjectDisposedException) { if (playback == p) { playback = null; } }
            }
            void Feed(AudioStreamGeneratorPlayback p)
            {
                if (p != watched) { watched = p; seenSkips = 0; settleAt = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency / 4; }
                // Godot's generator plays silence when it runs dry. After start-up, each new underrun raises
                // the queued target (bursty consumers such as the headless Dummy driver), up to 100 ms.
                int skips = p.GetSkips();
                if (skips != seenSkips)
                {
                    if (System.Diagnostics.Stopwatch.GetTimestamp() > settleAt) target = Math.Min(target + 512, 4800);
                    seenSkips = skips;
                }
                int available = p.GetFramesAvailable();
                if (emptyAvailable - available >= target || available < Chunk) { Thread.Sleep(1); return; }
                lock (engine.gate)
                {
                    if (!engine.isRunning || playback != p) return;
                    engine.RenderGraph(Chunk, left, right);
                }
                engine.RunCompletions();
                for (int i = 0; i < Chunk; i++) frames[i] = new Vector2(left[i], right[i]);
                p.PushBuffer(frames);
            }
        }
    }
}
