using MusicSpin.Core.Audio;

namespace MusicSpin.Core.Playback;

public sealed record PlayerOptions
{
    public const int DefaultBufferMilliseconds = 2000;
    public const int MinBufferMilliseconds = 100;
    public const int MaxBufferMilliseconds = 10_000;

    public AudioOutputOptions Output { get; init; } = new();

    /// <summary>How often <see cref="IPlayer.PositionChanged"/> fires while playing.</summary>
    public TimeSpan PositionInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How much audio the decode thread keeps ready ahead of the output (memory scales with sample rate and channels:
    /// 2 s of 192 kHz stereo is 3 MB). Clamped to <see cref="MinBufferMilliseconds"/>..<see cref="MaxBufferMilliseconds"/>.
    /// </summary>
    public int BufferMilliseconds { get; init; } = DefaultBufferMilliseconds;

    /// <summary>Continue into the next track without a gap when it has the same sample rate, channels and bit depth.</summary>
    public bool Gapless { get; init; } = true;

    /// <summary>Longest wait for the buffer to fill before the device starts (a slow disk starts late rather than stuttering).</summary>
    public TimeSpan PrefillTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The buffer size actually used.</summary>
    public int EffectiveBufferMilliseconds => ClampBuffer(BufferMilliseconds);

    public static int ClampBuffer(int milliseconds) => Math.Clamp(milliseconds, MinBufferMilliseconds, MaxBufferMilliseconds);
}
