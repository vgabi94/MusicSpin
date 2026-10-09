using MusicSpin.Core.Audio;

namespace MusicSpin.Core.Playback;

public sealed record PlayerOptions
{
    public AudioOutputOptions Output { get; init; } = new();

    /// <summary>How often <see cref="IPlayer.PositionChanged"/> fires while playing.</summary>
    public TimeSpan PositionInterval { get; init; } = TimeSpan.FromMilliseconds(250);
}
