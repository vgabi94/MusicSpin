using System.Buffers.Binary;
using MusicSpin.Core;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Metadata;

namespace MusicSpin.Tests.Shared;

/// <summary>Deterministic test signals and in-memory fakes shared by the test projects.</summary>
internal static class TestAudio
{
    /// <summary>Deterministic 24-bit sample value for frame <paramref name="frame"/>, channel <paramref name="channel"/>.</summary>
    public static int Sample24(long frame, int channel)
    {
        // Full-scale-ish pseudo random but reproducible; exercises all 24 bits.
        var x = unchecked((uint)(frame * 2654435761L + channel * 40503L));
        return (int)(x % (1 << 23)) - (1 << 22) + (channel == 0 ? 1 : -1);
    }

    /// <summary>Writes a 24-bit PCM WAV with <see cref="Sample24"/> content. Returns the path.</summary>
    public static string WriteWav24(string path, int sampleRate, int channels, int frames)
    {
        const int bytesPerSample = 3;
        var dataSize = frames * channels * bytesPerSample;
        using var stream = File.Create(path);
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], 36 + dataSize);
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], sampleRate * channels * bytesPerSample);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], (short)(channels * bytesPerSample));
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], 24);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], dataSize);
        stream.Write(header);

        var data = new byte[dataSize];
        var o = 0;
        for (var f = 0; f < frames; f++)
        {
            for (var c = 0; c < channels; c++)
            {
                var s = Sample24(f, c);
                data[o++] = (byte)s;
                data[o++] = (byte)(s >> 8);
                data[o++] = (byte)(s >> 16);
            }
        }

        stream.Write(data);
        return path;
    }

    public static TrackInfo Track(string path = "/music/fake.flac", int sampleRate = 48_000, int channels = 2, double seconds = 1, int bits = 24)
        => new()
        {
            Path = path,
            Container = "flac",
            Codec = "flac",
            CodecLongName = "FLAC (Free Lossless Audio Codec)",
            SampleRate = sampleRate,
            Channels = channels,
            ChannelLayout = channels == 2 ? "stereo" : null,
            BitsPerSample = bits,
            SampleFormat = "s32",
            Duration = TimeSpan.FromSeconds(seconds),
            IsLossless = true,
            Tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["title"] = "Fake Song", ["artist"] = "Fake Artist" },
        };
}

/// <summary>Decoder producing a constant 0.5 signal for a fixed duration.</summary>
internal sealed class FakeDecoder(TrackInfo track) : IAudioDecoder
{
    private long _frame;

    public TrackInfo Track { get; } = track;

    public PcmFormat Format { get; } = new(track.SampleRate, track.Channels, track.BitsPerSample ?? 32);

    public long TotalFrames => (long)(Track.Duration.TotalSeconds * Track.SampleRate);

    public TimeSpan Position => TimeSpan.FromSeconds((double)_frame / Track.SampleRate);

    public bool CanSeek => true;

    public bool Disposed { get; private set; }

    public float Value { get; init; } = 0.5f;

    public int Read(Span<float> buffer)
    {
        var frames = (int)Math.Min(buffer.Length / Format.Channels, TotalFrames - _frame);
        if (frames <= 0)
        {
            return 0;
        }

        buffer[..(frames * Format.Channels)].Fill(Value);
        _frame += frames;
        return frames * Format.Channels;
    }

    public void Seek(TimeSpan position)
        => _frame = Math.Clamp((long)(position.TotalSeconds * Track.SampleRate), 0, TotalFrames);

    public void Dispose() => Disposed = true;
}

internal sealed class FakeDecoderFactory : IAudioDecoderFactory
{
    public Dictionary<string, TrackInfo> Files { get; } = new(StringComparer.Ordinal);

    public List<FakeDecoder> Opened { get; } = [];

    public string Name => "fake";

    public bool IsAvailable { get; init; } = true;

    public TrackInfo Probe(string path) => Find(path);

    /// <summary>Simulated slow open (a busy machine or slow disk).</summary>
    public TimeSpan OpenDelay { get; set; }

    public IAudioDecoder Open(string path)
    {
        if (OpenDelay > TimeSpan.Zero)
        {
            Thread.Sleep(OpenDelay);
        }

        var decoder = new FakeDecoder(Find(path));
        Opened.Add(decoder);
        return decoder;
    }

    public IReadOnlyList<AudioFormatSupport> GetSupportedFormats()
        => KnownFormats.All.Select(f => new AudioFormatSupport(f.Id, f.Name, f.Extensions, f.Lossless, f.HiResCapable, IsAvailable, f.Decoders)).ToArray();

    public FakeDecoderFactory With(TrackInfo track)
    {
        Files[track.Path] = track;
        return this;
    }

    private TrackInfo Find(string path)
    {
        if (!IsAvailable)
        {
            throw new MusicSpinException(MusicSpinErrorCode.DecoderUnavailable, "fake decoder unavailable");
        }

        return Files.TryGetValue(path, out var t)
            ? t
            : throw new MusicSpinException(MusicSpinErrorCode.FileNotFound, $"File not found: {path}");
    }
}

/// <summary>Output whose "audio thread" is driven manually by the test via <see cref="Pump"/>.</summary>
internal sealed class ManualAudioOutput : IAudioOutput
{
    public IPcmSource? Source { get; private set; }

    public bool Started { get; private set; }

    public int OpenCount { get; private set; }

    public bool IsOpen => Source is not null;

    public void Open(IPcmSource source)
    {
        Source = source;
        OpenCount++;
    }

    public void Start() => Started = true;

    public void Pause() => Started = false;

    public void Stop()
    {
        Started = false;
        Source = null;
    }

    public void Dispose() => Stop();

    /// <summary>Pulls <paramref name="samples"/> interleaved samples like a device callback would.</summary>
    public float[] Pump(int samples)
    {
        var buffer = new float[samples];
        if (Source is null || !Started)
        {
            return [];
        }

        var read = Source.Read(buffer);
        return buffer[..read];
    }
}

internal sealed class ManualOutputProvider : IAudioOutputProvider
{
    public ManualAudioOutput Output { get; } = new();

    public string Name => "manual";

    public IReadOnlyList<AudioDeviceInfo> GetDevices() => [new("0", "Manual", true, "manual", ["f32"], [48_000], [2])];

    public IAudioOutput Create(AudioOutputOptions options) => Output;
}
