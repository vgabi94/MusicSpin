using MusicSpin.Core;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Output;

namespace MusicSpin.Tests.Shared;

/// <summary>Helpers for player/queue/IPC tests.</summary>
internal static class PlayerTestKit
{
    /// <summary>A fake library of <paramref name="count"/> tracks, each <paramref name="seconds"/> long.</summary>
    public static (FakeDecoderFactory Decoders, string[] Paths) Tracks(int count, double seconds, string folder = "/music/q")
    {
        var decoders = new FakeDecoderFactory();
        var paths = new string[count];
        for (var i = 0; i < count; i++)
        {
            paths[i] = Path.GetFullPath($"{folder}/{i + 1:00}.flac"); // rooted per OS, as the CLI normalizes paths
            decoders.With(TestAudio.Track(paths[i], sampleRate: 8000, seconds: seconds));
        }

        return (decoders, paths);
    }
}

/// <summary>Output provider that fails on demand (device unplugged, exclusive mode refused, ...).</summary>
internal sealed class FaultyOutputProvider(IAudioOutputProvider inner) : IAudioOutputProvider
{
    /// <summary>When set, <see cref="IAudioOutput.Start"/> throws this many more times (0 = works).</summary>
    public int FailStarts { get; set; }

    public bool FailOpen { get; set; }

    /// <summary>When set, <see cref="IAudioOutput.Stop"/> throws (a driver failing while releasing the device).</summary>
    public bool FailStop { get; set; }

    public string Name => "faulty(" + inner.Name + ")";

    public IReadOnlyList<AudioDeviceInfo> GetDevices() => inner.GetDevices();

    public IAudioOutput Create(AudioOutputOptions options) => new FaultyOutput(this, inner.Create(options));

    private sealed class FaultyOutput(FaultyOutputProvider owner, IAudioOutput inner) : IAudioOutput
    {
        public bool IsOpen => inner.IsOpen;

        public void Open(IPcmSource source)
        {
            if (owner.FailOpen)
            {
                throw new InvalidOperationException("injected: device disappeared");
            }

            inner.Open(source);
        }

        public void Start()
        {
            if (owner.FailStarts > 0)
            {
                owner.FailStarts--;
                throw new MusicSpinException(MusicSpinErrorCode.AudioDeviceError, "injected: device busy");
            }

            inner.Start();
        }

        public void Pause() => inner.Pause();

        public void Stop()
        {
            inner.Stop();
            if (owner.FailStop)
            {
                throw new InvalidOperationException("injected: driver failed to stop");
            }
        }

        public void Dispose() => inner.Dispose();
    }
}

/// <summary>Null output that counts creations (to observe playback without timing assumptions).</summary>
internal static class Outputs
{
    public static NullAudioOutputProvider Realtime() => new(realtime: true);

    public static NullAudioOutputProvider Fast() => new(realtime: false);
}
