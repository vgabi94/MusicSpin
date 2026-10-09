using System.Diagnostics;
using MusicSpin.Core.Audio;

namespace MusicSpin.Core.Output;

/// <summary>
/// Output that consumes samples without a sound card. Useful for CI, headless servers and agents
/// that want to verify a file decodes end-to-end (<c>music-spin play --output null</c>).
/// </summary>
public sealed class NullAudioOutputProvider(bool realtime = true) : IAudioOutputProvider
{
    public string Name => realtime ? "null (realtime)" : "null (fast)";

    public IReadOnlyList<AudioDeviceInfo> GetDevices() =>
        [new AudioDeviceInfo("null", "Null output (discards audio)", true, "null", ["f32"], [], [])];

    public IAudioOutput Create(AudioOutputOptions options) => new NullAudioOutput(realtime);
}

/// <summary>Pulls 10 ms blocks on a background thread, paced to real time unless <c>realtime</c> is false.</summary>
public sealed class NullAudioOutput(bool realtime = true) : IAudioOutput
{
    private readonly Lock _lock = new();
    private IPcmSource? _source;
    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _stopRequested;

    public bool IsOpen
    {
        get { lock (_lock) { return _source is not null; } }
    }

    /// <summary>Total interleaved samples consumed (for tests/diagnostics).</summary>
    public long SamplesConsumed => Interlocked.Read(ref _consumed);

    private long _consumed;

    public void Open(IPcmSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Stop();
        lock (_lock)
        {
            _source = source;
            _stopRequested = false;
            _thread = new Thread(Pump) { IsBackground = true, Name = "MusicSpin null output" };
            _thread.Start();
        }
    }

    public void Start() => _running = true;

    public void Pause() => _running = false;

    public void Stop()
    {
        Thread? thread;
        lock (_lock)
        {
            _running = false;
            _stopRequested = true;
            thread = _thread;
            _thread = null;
            _source = null;
        }

        if (thread is not null && thread != Thread.CurrentThread)
        {
            thread.Join();
        }
    }

    public void Dispose() => Stop();

    private void Pump()
    {
        IPcmSource? source;
        lock (_lock)
        {
            source = _source;
        }

        if (source is null)
        {
            return;
        }

        var format = source.Format;
        var block = new float[Math.Max(format.SamplesPerSecond / 100, format.Channels)];
        var clock = Stopwatch.StartNew();
        long played = 0;

        while (!_stopRequested)
        {
            if (!_running)
            {
                Thread.Sleep(5);
                clock.Restart();
                played = 0;
                continue;
            }

            var read = source.Read(block);
            if (read == 0)
            {
                Thread.Sleep(5);
                continue;
            }

            Interlocked.Add(ref _consumed, read);
            played += read;

            if (realtime && format.SamplesPerSecond > 0)
            {
                var ahead = TimeSpan.FromSeconds((double)played / format.SamplesPerSecond) - clock.Elapsed;
                if (ahead > TimeSpan.FromMilliseconds(1))
                {
                    Thread.Sleep(ahead);
                }
            }
        }
    }
}
