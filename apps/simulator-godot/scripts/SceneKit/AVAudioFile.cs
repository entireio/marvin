// AVFoundation audio data types: formats, PCM buffers and WAV files, as the game uses them.
// The engine graph (AVAudioEngine and its nodes) is in AVAudioEngine.cs.
global using AVAudioFrameCount = System.UInt32;
global using AVAudioFramePosition = System.Int64;
global using static Marvin.SceneKit.AudioToolbox;

using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace Marvin.SceneKit;

/// <summary>
/// AudioToolbox / AVFoundation global constants (four-character codes and settings keys) that Swift
/// code names unqualified. Exposed everywhere through <c>global using static</c>.
/// </summary>
public static class AudioToolbox
{
    public const uint kAudioUnitType_Output = 0x61756f75;          // 'auou'
    public const uint kAudioUnitType_Mixer = 0x61756d78;           // 'aumx'
    public const uint kAudioUnitType_Effect = 0x61756678;          // 'aufx'
    public const uint kAudioUnitType_FormatConverter = 0x61756663; // 'aufc'
    public const uint kAudioUnitType_Generator = 0x6175676e;       // 'augn'
    public const uint kAudioUnitSubType_PeakLimiter = 0x6c6d7472;  // 'lmtr'
    public const uint kAudioUnitSubType_NBandEQ = 0x6e626571;      // 'nbeq'
    public const uint kAudioUnitSubType_Varispeed = 0x76617269;    // 'vari'
    public const uint kAudioUnitSubType_MultiChannelMixer = 0x6d636d78; // 'mcmx'
    public const uint kAudioUnitSubType_ScheduledSoundPlayer = 0x7373706c; // 'sspl'
    public const uint kAudioUnitSubType_GenericOutput = 0x67656e72; // 'genr'
    public const uint kAudioUnitManufacturer_Apple = 0x6170706c;   // 'appl'
    public const uint kAudioFormatLinearPCM = 0x6c70636d;          // 'lpcm'

    public const string AVFormatIDKey = "AVFormatIDKey";
    public const string AVSampleRateKey = "AVSampleRateKey";
    public const string AVNumberOfChannelsKey = "AVNumberOfChannelsKey";
    public const string AVLinearPCMBitDepthKey = "AVLinearPCMBitDepthKey";
    public const string AVLinearPCMIsFloatKey = "AVLinearPCMIsFloatKey";
    public const string AVLinearPCMIsBigEndianKey = "AVLinearPCMIsBigEndianKey";
    public const string AVLinearPCMIsNonInterleaved = "AVLinearPCMIsNonInterleaved";
}

public enum AVAudioCommonFormat { otherFormat = 0, pcmFormatFloat32 = 1, pcmFormatFloat64 = 2, pcmFormatInt16 = 3, pcmFormatInt32 = 4 }

/// <summary>AVAudioFormat. The engine processes the standard format: deinterleaved Float32.</summary>
public sealed class AVAudioFormat : IEquatable<AVAudioFormat>
{
    public double sampleRate { get; }
    public uint channelCount { get; }
    public AVAudioCommonFormat commonFormat { get; }
    public bool isInterleaved { get; }
    public bool isStandard => commonFormat == AVAudioCommonFormat.pcmFormatFloat32 && !isInterleaved;

    /// <summary>AVAudioFormat(standardFormatWithSampleRate:channels:): deinterleaved Float32.</summary>
    public AVAudioFormat(double standardFormatWithSampleRate, uint channels)
        : this(AVAudioCommonFormat.pcmFormatFloat32, standardFormatWithSampleRate, channels, false) { }
    /// <summary>AVAudioFormat(commonFormat:sampleRate:channels:interleaved:).</summary>
    public AVAudioFormat(AVAudioCommonFormat commonFormat, double sampleRate, uint channels, bool interleaved)
    {
        if (channels < 1 || sampleRate <= 0) throw new ArgumentException("invalid audio format");
        this.commonFormat = commonFormat; this.sampleRate = sampleRate; channelCount = channels; isInterleaved = interleaved;
    }
    /// <summary>The settings dictionary (AVFormatIDKey, AVSampleRateKey, ...), e.g. for AVAudioFile(forWriting:settings:).</summary>
    public Dictionary<string, object> settings => new()
    {
        [AVFormatIDKey] = kAudioFormatLinearPCM,
        [AVSampleRateKey] = sampleRate,
        [AVNumberOfChannelsKey] = (int)channelCount,
        [AVLinearPCMBitDepthKey] = commonFormat switch { AVAudioCommonFormat.pcmFormatFloat64 => 64, AVAudioCommonFormat.pcmFormatInt16 => 16, _ => 32 },
        [AVLinearPCMIsFloatKey] = commonFormat is AVAudioCommonFormat.pcmFormatFloat32 or AVAudioCommonFormat.pcmFormatFloat64,
        [AVLinearPCMIsBigEndianKey] = false,
        [AVLinearPCMIsNonInterleaved] = !isInterleaved,
    };
    public bool Equals(AVAudioFormat other) => other is not null && sampleRate == other.sampleRate && channelCount == other.channelCount && commonFormat == other.commonFormat && isInterleaved == other.isInterleaved;
    public override bool Equals(object obj) => obj is AVAudioFormat f && Equals(f);
    public override int GetHashCode() => HashCode.Combine(sampleRate, channelCount, commonFormat, isInterleaved);
    public static bool operator ==(AVAudioFormat a, AVAudioFormat b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(AVAudioFormat a, AVAudioFormat b) => !(a == b);
}

/// <summary>AVAudioPCMBuffer with deinterleaved float channel data (<c>floatChannelData![c][i]</c> is <c>floatChannelData[c][i]</c>).</summary>
public sealed class AVAudioPCMBuffer
{
    public AVAudioFormat format { get; }
    public AVAudioFrameCount frameCapacity { get; }
    private AVAudioFrameCount _frameLength;
    public AVAudioFrameCount frameLength
    {
        get => _frameLength;
        set { if (value > frameCapacity) throw new ArgumentOutOfRangeException(nameof(frameLength), "frameLength exceeds frameCapacity"); _frameLength = value; }
    }
    public uint stride => 1;
    /// <summary>One float array per channel (non-null for the standard format, like Swift's <c>floatChannelData!</c>).</summary>
    public float[][] floatChannelData { get; }

    public AVAudioPCMBuffer(AVAudioFormat pcmFormat, AVAudioFrameCount frameCapacity)
    {
        format = pcmFormat; this.frameCapacity = frameCapacity;
        floatChannelData = new float[pcmFormat.channelCount][];
        for (int c = 0; c < floatChannelData.Length; c++) floatChannelData[c] = new float[frameCapacity];
    }
}

/// <summary>Swift's UnsafeMutablePointer&lt;Float&gt;.update(from:count:) on a channel of floatChannelData.</summary>
public static class AudioPointerExtensions
{
    public static void update(this float[] pointee, float[] from, int count) => Array.Copy(from, pointee, count);
}

/// <summary>
/// AVAudioFile for WAV files. Reading converts PCM (8/16/24/32-bit integer, 32/64-bit float, also
/// WAVE_FORMAT_EXTENSIBLE) to the deinterleaved Float32 processing format (integers divided by 2^(bits-1),
/// as Core Audio does). Writing produces 32-bit float WAV files with the layout AVAudioFile uses on macOS
/// (JUNK chunk, 'fmt ' WAVE_FORMAT_IEEE_FLOAT, FLLR padding so samples start at byte 4096, interleaved
/// samples: "Audio files cannot be non-interleaved"). Headers are rewritten after every write, so the file is
/// complete without an explicit close (Swift closes on deinit).
/// Paths: res:// and user:// go through Godot's FileAccess/globalized paths; anything else is a file system path.
/// </summary>
public sealed class AVAudioFile
{
    public AVAudioFormat fileFormat { get; }
    public AVAudioFormat processingFormat { get; }
    public string url { get; }
    public AVAudioFramePosition length { get; private set; }
    public AVAudioFramePosition framePosition { get; set; }

    // Reading: samples decoded once into the processing format.
    private readonly float[][] samples;
    // Writing.
    private FileStream stream;
    private const int dataOffset = 4096;

    /// <summary>AVAudioFile(forReading:). Throws IOException/InvalidDataException like Swift's throwing initializer.</summary>
    public AVAudioFile(string forReading)
    {
        url = forReading;
        byte[] bytes = ReadAllBytes(forReading);
        (fileFormat, samples) = DecodeWav(bytes, forReading);
        processingFormat = new AVAudioFormat(standardFormatWithSampleRate: fileFormat.sampleRate, channels: fileFormat.channelCount);
        length = samples[0].Length;
    }

    /// <summary>AVAudioFile(forWriting:settings:): Float32 WAV with the settings' sample rate and channel count.</summary>
    public AVAudioFile(string forWriting, Dictionary<string, object> settings)
    {
        url = forWriting;
        double rate = Convert.ToDouble(settings.TryGetValue(AVSampleRateKey, out var r) ? r : 44100.0);
        uint channels = (uint)Convert.ToInt32(settings.TryGetValue(AVNumberOfChannelsKey, out var c) ? c : 2);
        fileFormat = new AVAudioFormat(AVAudioCommonFormat.pcmFormatFloat32, rate, channels, true);
        processingFormat = new AVAudioFormat(standardFormatWithSampleRate: rate, channels: channels);
        string path = GlobalPath(forWriting);
        string directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        stream = new FileStream(path, FileMode.Create, System.IO.FileAccess.ReadWrite, FileShare.Read);
        WriteHeader();
    }

    /// <summary>read(into:): fills the buffer from framePosition up to its capacity.</summary>
    public void read(AVAudioPCMBuffer into) => read(into, into.frameCapacity);
    /// <summary>read(into:frameCount:).</summary>
    public void read(AVAudioPCMBuffer into, AVAudioFrameCount frameCount)
    {
        if (samples == null) throw new InvalidOperationException("AVAudioFile was opened for writing");
        if (into.format.channelCount != processingFormat.channelCount || into.format.sampleRate != processingFormat.sampleRate)
            throw new ArgumentException("buffer format does not match the file's processing format");
        long available = Math.Max(0, length - framePosition);
        int n = (int)Math.Min(Math.Min(frameCount, into.frameCapacity), available);
        if (n == 0 && frameCount > 0) throw new EndOfStreamException($"{url}: no frames left to read");
        for (int ch = 0; ch < samples.Length; ch++) Array.Copy(samples[ch], framePosition, into.floatChannelData[ch], 0, n);
        into.frameLength = (AVAudioFrameCount)n;
        framePosition += n;
    }

    /// <summary>write(from:): appends the buffer's frameLength frames (interleaved Float32).</summary>
    public void write(AVAudioPCMBuffer from)
    {
        if (stream == null) throw new InvalidOperationException("AVAudioFile is not open for writing");
        int frames = (int)from.frameLength, channels = (int)fileFormat.channelCount;
        var bytes = new byte[frames * channels * 4];
        for (int i = 0; i < frames; i++)
            for (int ch = 0; ch < channels; ch++)
                BitConverter.TryWriteBytes(bytes.AsSpan((i * channels + ch) * 4, 4), from.floatChannelData[Math.Min(ch, from.floatChannelData.Length - 1)][i]);
        stream.Seek(dataOffset + length * channels * 4, SeekOrigin.Begin);
        stream.Write(bytes, 0, bytes.Length);
        length += frames; framePosition = length;
        WriteHeader();
        stream.Flush();
    }

    /// <summary>close() (macOS 15+): finishes the file; later writes throw.</summary>
    public void close() { stream?.Flush(); stream?.Dispose(); stream = null; }

    private void WriteHeader()
    {
        int channels = (int)fileFormat.channelCount, rate = (int)fileFormat.sampleRate;
        long dataBytes = length * channels * 4;
        var h = new byte[dataOffset];
        void tag(int at, string s) { for (int i = 0; i < 4; i++) h[at + i] = (byte)s[i]; }
        void u32(int at, long v) => BitConverter.TryWriteBytes(h.AsSpan(at, 4), (uint)v);
        void u16(int at, int v) => BitConverter.TryWriteBytes(h.AsSpan(at, 2), (ushort)v);
        tag(0, "RIFF"); u32(4, dataOffset - 8 + dataBytes); tag(8, "WAVE");
        tag(12, "JUNK"); u32(16, 28);
        tag(48, "fmt "); u32(52, 16); u16(56, 3); u16(58, channels); u32(60, rate); u32(64, (long)rate * channels * 4); u16(68, channels * 4); u16(70, 32);
        tag(72, "FLLR"); u32(76, dataOffset - 8 - 80);
        tag(dataOffset - 8, "data"); u32(dataOffset - 4, dataBytes);
        stream.Seek(0, SeekOrigin.Begin);
        stream.Write(h, 0, h.Length);
    }

    internal static string GlobalPath(string path) =>
        path.StartsWith("res://") || path.StartsWith("user://") ? ProjectSettings.GlobalizePath(path) : path;

    private static byte[] ReadAllBytes(string path)
    {
        if (path.StartsWith("res://") || path.StartsWith("user://"))
        {
            if (Godot.FileAccess.FileExists(path)) return Godot.FileAccess.GetFileAsBytes(path);
            // PORT: exported builds may only contain Godot's imported copy of a .wav; use its PCM data.
            if (ResourceLoader.Exists(path) && ResourceLoader.Load(path) is AudioStreamWav wav && wav.Format == AudioStreamWav.FormatEnum.Format16Bits)
                return WavFromImported(wav);
            throw new FileNotFoundException($"audio file not found: {path}");
        }
        return File.ReadAllBytes(path);
    }

    private static byte[] WavFromImported(AudioStreamWav wav)
    {
        byte[] data = wav.Data;
        int channels = wav.Stereo ? 2 : 1, rate = wav.MixRate;
        var bytes = new byte[44 + data.Length];
        void tag(int at, string s) { for (int i = 0; i < 4; i++) bytes[at + i] = (byte)s[i]; }
        tag(0, "RIFF"); BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), (uint)(36 + data.Length)); tag(8, "WAVE");
        tag(12, "fmt "); BitConverter.TryWriteBytes(bytes.AsSpan(16, 4), 16u); BitConverter.TryWriteBytes(bytes.AsSpan(20, 2), (ushort)1);
        BitConverter.TryWriteBytes(bytes.AsSpan(22, 2), (ushort)channels); BitConverter.TryWriteBytes(bytes.AsSpan(24, 4), (uint)rate);
        BitConverter.TryWriteBytes(bytes.AsSpan(28, 4), (uint)(rate * channels * 2)); BitConverter.TryWriteBytes(bytes.AsSpan(32, 2), (ushort)(channels * 2));
        BitConverter.TryWriteBytes(bytes.AsSpan(34, 2), (ushort)16);
        tag(36, "data"); BitConverter.TryWriteBytes(bytes.AsSpan(40, 4), (uint)data.Length);
        Array.Copy(data, 0, bytes, 44, data.Length);
        return bytes;
    }

    private static (AVAudioFormat, float[][]) DecodeWav(byte[] d, string name)
    {
        if (d.Length < 12 || d[0] != 'R' || d[1] != 'I' || d[2] != 'F' || d[3] != 'F' || d[8] != 'W' || d[9] != 'A' || d[10] != 'V' || d[11] != 'E')
            throw new InvalidDataException($"{name}: not a RIFF/WAVE file");
        int pos = 12, format = 0, channels = 0, rate = 0, bits = 0, dataStart = -1, dataLength = 0;
        while (pos + 8 <= d.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(d, pos, 4);
            int size = (int)BitConverter.ToUInt32(d, pos + 4);
            int body = pos + 8;
            if (id == "fmt ")
            {
                format = BitConverter.ToUInt16(d, body); channels = BitConverter.ToUInt16(d, body + 2);
                rate = (int)BitConverter.ToUInt32(d, body + 4); bits = BitConverter.ToUInt16(d, body + 14);
                if (format == 0xFFFE && size >= 40) format = BitConverter.ToUInt16(d, body + 24); // extensible: sub-format GUID's first two bytes
            }
            else if (id == "data") { dataStart = body; dataLength = Math.Min(size, d.Length - body); break; }
            pos = body + size + (size & 1);
        }
        if (format == 0 || dataStart < 0 || channels < 1) throw new InvalidDataException($"{name}: missing fmt or data chunk");
        if (!((format == 1 && bits is 8 or 16 or 24 or 32) || (format == 3 && bits is 32 or 64)))
            throw new InvalidDataException($"{name}: unsupported WAV encoding {format}/{bits}");
        int frameBytes = channels * bits / 8, frames = dataLength / frameBytes;
        var output = new float[channels][];
        for (int c = 0; c < channels; c++) output[c] = new float[frames];
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < channels; c++)
            {
                int at = dataStart + i * frameBytes + c * bits / 8;
                output[c][i] = (format, bits) switch
                {
                    (1, 8) => (d[at] - 128) / 128f,
                    (1, 16) => BitConverter.ToInt16(d, at) / 32768f,
                    (1, 24) => ((d[at] | (d[at + 1] << 8) | ((sbyte)d[at + 2] << 16))) / 8388608f,
                    (1, 32) => (float)(BitConverter.ToInt32(d, at) / 2147483648.0),
                    (3, 32) => BitConverter.ToSingle(d, at),
                    _ => (float)BitConverter.ToDouble(d, at),
                };
            }
        var fileFormat = new AVAudioFormat(format == 3 ? (bits == 64 ? AVAudioCommonFormat.pcmFormatFloat64 : AVAudioCommonFormat.pcmFormatFloat32)
            : bits == 16 ? AVAudioCommonFormat.pcmFormatInt16 : bits == 32 ? AVAudioCommonFormat.pcmFormatInt32 : AVAudioCommonFormat.otherFormat, rate, (uint)channels, true);
        return (fileFormat, output);
    }
}
