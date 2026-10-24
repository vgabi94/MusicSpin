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

/// <summary>A decoder of <paramref name="track"/>: a constant <see cref="Value"/>, or <paramref name="wave"/>(frame, channel) when given.</summary>
internal sealed class FakeDecoder(TrackInfo track, Func<long, int, float>? wave = null) : IAudioDecoder
{
    private long _frame;

    public TrackInfo Track { get; } = track;

    public PcmFormat Format { get; } = new(track.SampleRate, track.Channels, track.BitsPerSample ?? 32);

    public long TotalFrames => (long)(Track.Duration.TotalSeconds * Track.SampleRate);

    public TimeSpan Position => TimeSpan.FromSeconds((double)_frame / Track.SampleRate);

    public bool CanSeek { get; init; } = true;

    public bool Disposed { get; private set; }

    public float Value { get; init; } = 0.5f;

    public int Read(Span<float> buffer)
    {
        var frames = (int)Math.Min(buffer.Length / Format.Channels, TotalFrames - _frame);
        if (frames <= 0)
        {
            return 0;
        }

        if (wave is null)
        {
            buffer[..(frames * Format.Channels)].Fill(Value);
        }
        else
        {
            for (var f = 0; f < frames; f++)
            {
                for (var c = 0; c < Format.Channels; c++)
                {
                    buffer[(f * Format.Channels) + c] = wave(_frame + f, c);
                }
            }
        }

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

        var decoder = new FakeDecoder(Find(path), Waves.GetValueOrDefault(path));
        Opened.Add(decoder);
        return decoder;
    }

    /// <summary>Waveforms (frame, channel) → sample for some paths; others decode to a constant.</summary>
    public Dictionary<string, Func<long, int, float>> Waves { get; } = new(StringComparer.Ordinal);

    /// <summary>A sine of <paramref name="hz"/> at <paramref name="amplitude"/> on every channel.</summary>
    public static Func<long, int, float> Sine(double hz, int sampleRate, float amplitude = 1f)
        => (frame, _) => amplitude * (float)Math.Sin(2 * Math.PI * hz * frame / sampleRate);

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
    private volatile IPcmSource? _source;
    private volatile bool _started;

    public IPcmSource? Source { get => _source; private set => _source = value; }

    public bool Started { get => _started; private set => _started = value; }

    public int OpenCount { get; private set; }

    public bool IsOpen => Source is not null;

    /// <summary>What <see cref="Opened"/> reports for a source (what a real device would read back); null: nothing.</summary>
    public Func<IPcmSource, OpenedOutput?>? OpenedAs { get; set; }

    public OpenedOutput? Opened { get; private set; }

    /// <summary>Opening throws this (a device that cannot be opened) while set.</summary>
    public Exception? OpenError { get; set; }

    /// <summary>How many track changes kept the device open (<see cref="Retarget"/> returned true).</summary>
    public int RetargetCount { get; private set; }

    public void Open(IPcmSource source)
    {
        if (OpenError is not null)
        {
            Source = null;
            Opened = null;
            throw OpenError;
        }

        Source = source;
        Opened = OpenedAs?.Invoke(source);
        OpenCount++;
    }

    /// <summary>Retargeting throws this (a device that fails while being moved) while set.</summary>
    public Exception? RetargetError { get; set; }

    /// <summary>An exclusive device is configured for the source's sample depth too, so a different depth needs a reopen.</summary>
    public bool Exclusive { get; set; }

    /// <summary>Runs inside <see cref="Retarget"/>, before it decides (what the player's own state is at that moment).</summary>
    public Action<IPcmSource>? OnRetarget { get; set; }

    /// <summary>
    /// A device whose stream cannot confirm it stopped (WASAPI's bounded pause): Retarget closes the output and refuses,
    /// instead of switching.
    /// </summary>
    public bool RetargetUnconfirmed { get; set; }

    /// <summary>Makes converters, like a shared-mode device (null: an output that never converts, like exclusive).</summary>
    public Func<PcmFormat, PcmFormat, IPcmConverter?>? Converter { get; set; }

    /// <summary>Every converter handed out, in order.</summary>
    public List<IPcmConverter> Converters { get; } = [];

    public IPcmConverter? CreateConverter(PcmFormat from, PcmFormat to)
    {
        var converter = Converter?.Invoke(from, to);
        if (converter is not null)
        {
            lock (Converters)
            {
                Converters.Add(converter);
            }
        }

        return converter;
    }

    /// <summary>Like a real device: kept open for the same rate and channels, left paused on the new source.</summary>
    public bool Retarget(IPcmSource next)
    {
        OnRetarget?.Invoke(next);
        if (RetargetError is not null)
        {
            throw RetargetError;
        }

        if (RetargetUnconfirmed && _source is not null)
        {
            Stop();
            return false;
        }

        if (_source is not { } current || current.Format.SampleRate != next.Format.SampleRate || current.Format.Channels != next.Format.Channels
            || (Exclusive && current.Format.SourceBitsPerSample != next.Format.SourceBitsPerSample))
        {
            return false;
        }

        Started = false;
        Source = next;
        RetargetCount++;
        return true;
    }

    /// <summary>
    /// An exclusive device that is open but handed back to the system (a pause releases it, as the real output does):
    /// it stays open as far as the player is concerned, and <see cref="Start"/> takes it again.
    /// </summary>
    public bool Released { get; private set; }

    public void Start()
    {
        Released = false;
        Started = true;
    }

    public void Pause()
    {
        Started = false;
        Released = Exclusive && Source is not null;
    }

    public void Stop()
    {
        Released = false;
        Started = false;
        Source = null;
        Opened = null;
    }

    public void Dispose() => Stop();

    public event EventHandler<AudioOutputFailedEventArgs>? Failed;

    public event EventHandler<AudioOutputReroutedEventArgs>? Rerouted;

    /// <summary>The output moved to another device by itself and keeps pulling from the same source.</summary>
    public void Reroute(OpenedOutput opened, string? message) => Rerouted?.Invoke(this, new AudioOutputReroutedEventArgs(opened, message));

    /// <summary>The device goes away mid-play: closes like a real output, then raises <see cref="Failed"/>.</summary>
    public void Fail(string reason)
    {
        Stop();
        Failed?.Invoke(this, new AudioOutputFailedEventArgs(reason));
    }

    /// <summary>
    /// Pulls <paramref name="samples"/> interleaved samples like a device callback would. By default first waits for the
    /// decode thread to have them ready (deterministic content); <paramref name="wait"/> = false pulls whatever is there,
    /// like a real device that cannot wait (underruns play silence).
    /// </summary>
    public float[] Pump(int samples, bool wait = true)
    {
        var buffer = new float[samples];
        var source = Source; // the player may close the device from another thread meanwhile
        if (source is null || !Started)
        {
            return [];
        }

        if (wait && source is MusicSpin.Core.Playback.PlaybackBuffer ring && !ring.WaitUntilReadable(samples, TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("The decode thread did not fill the buffer.");
        }

        var read = source.Read(buffer);
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
