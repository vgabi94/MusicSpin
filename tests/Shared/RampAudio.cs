using MusicSpin.Core;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Metadata;

namespace MusicSpin.Tests.Shared;

/// <summary>A fake track whose every sample is unique (track id, frame, channel), so concatenations can be compared exactly.</summary>
internal sealed record RampSpec(int Id, long Frames, int SampleRate = 1000, int Channels = 2, int Bits = 24)
{
    /// <summary>Decoding throws once this frame is reached.</summary>
    public long? FailAtFrame { get; init; }

    /// <summary>Opening throws this.</summary>
    public MusicSpinException? OpenError { get; init; }

    /// <summary>Reads block until set (simulates a stalled disk/CPU).</summary>
    public ManualResetEventSlim? Gate { get; init; }

    public bool Seekable { get; init; } = true;

    public PcmFormat Format => new(SampleRate, Channels, Bits);

    public float Sample(long frame, int channel) => (Id * 1_000_000) + (frame * Channels) + channel;

    /// <summary>The samples this track must produce, from <paramref name="fromFrame"/> on.</summary>
    public float[] Pcm(long fromFrame = 0)
    {
        var samples = new float[(Frames - fromFrame) * Channels];
        for (var f = fromFrame; f < Frames; f++)
        {
            for (var c = 0; c < Channels; c++)
            {
                samples[((f - fromFrame) * Channels) + c] = Sample(f, c);
            }
        }

        return samples;
    }
}

internal sealed class RampDecoder(string path, RampSpec spec) : IAudioDecoder
{
    private long _frame;

    public RampSpec Spec { get; } = spec;

    public TrackInfo Track { get; } = TestAudio.Track(path, spec.SampleRate, spec.Channels, (double)spec.Frames / spec.SampleRate, spec.Bits);

    public PcmFormat Format => Spec.Format;

    public TimeSpan Position => TimeSpan.FromSeconds((double)_frame / Spec.SampleRate);

    public bool CanSeek => Spec.Seekable;

    public volatile bool Disposed;

    /// <summary>Set while a read is in progress (for dispose-while-decoding tests).</summary>
    public ManualResetEventSlim Reading { get; } = new(false);

    public int Read(Span<float> buffer)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        Reading.Set();
        Spec.Gate?.Wait();
        var frames = (int)Math.Min(buffer.Length / Spec.Channels, Spec.Frames - _frame);
        if (Spec.FailAtFrame is { } fail && _frame + frames >= fail)
        {
            frames = (int)Math.Max(0, fail - _frame);
            if (frames == 0)
            {
                throw new MusicSpinException(MusicSpinErrorCode.DecodeFailed, $"Corrupt data at frame {fail}.");
            }
        }

        for (var f = 0; f < frames; f++)
        {
            for (var c = 0; c < Spec.Channels; c++)
            {
                buffer[(f * Spec.Channels) + c] = Spec.Sample(_frame + f, c);
            }
        }

        _frame += Math.Max(frames, 0);
        return Math.Max(frames, 0) * Spec.Channels;
    }

    public void Seek(TimeSpan position) => _frame = Math.Clamp((long)Math.Round(position.TotalSeconds * Spec.SampleRate), 0, Spec.Frames);

    public void Dispose() => Disposed = true;
}

internal sealed class RampDecoderFactory : IAudioDecoderFactory
{
    private readonly Lock _lock = new();
    private readonly List<RampDecoder> _opened = [];

    private readonly Dictionary<string, RampSpec> _files = new(StringComparer.Ordinal);

    public void Add(string path, RampSpec spec)
    {
        lock (_lock)
        {
            _files[path] = spec;
        }
    }

    public void Remove(string path)
    {
        lock (_lock)
        {
            _files.Remove(path);
        }
    }

    public string Name => "ramp";

    public bool IsAvailable => true;

    public IReadOnlyList<RampDecoder> Opened
    {
        get { lock (_lock) { return [.. _opened]; } }
    }

    public TrackInfo Probe(string path) => new RampDecoder(path, Find(path)).Track;

    public IAudioDecoder Open(string path)
    {
        var spec = Find(path);
        if (spec.OpenError is { } error)
        {
            throw error;
        }

        var decoder = new RampDecoder(path, spec);
        lock (_lock)
        {
            _opened.Add(decoder);
        }

        return decoder;
    }

    public IReadOnlyList<AudioFormatSupport> GetSupportedFormats() => [];

    private RampSpec Find(string path)
    {
        lock (_lock)
        {
            return _files.TryGetValue(path, out var spec)
                ? spec
                : throw new MusicSpinException(MusicSpinErrorCode.FileNotFound, $"File not found: {path}");
        }
    }
}
