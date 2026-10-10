using MusicSpin.Core.Metadata;

namespace MusicSpin.Core.Playback;

public enum PlayerState
{
    Stopped,
    Playing,
    Paused,
}

public sealed class PlayerStateChangedEventArgs(PlayerState oldState, PlayerState newState) : EventArgs
{
    public PlayerState OldState { get; } = oldState;
    public PlayerState NewState { get; } = newState;
}

public sealed class PlayerPositionChangedEventArgs(TimeSpan position, TimeSpan duration) : EventArgs
{
    public TimeSpan Position { get; } = position;
    public TimeSpan Duration { get; } = duration;
}

public sealed class TrackLoadedEventArgs(TrackInfo track) : EventArgs
{
    public TrackInfo Track { get; } = track;
}

/// <summary>An entry the player may continue with gaplessly; <paramref name="Token"/> identifies it to the caller (a queue slot).</summary>
public sealed record NextTrack(string Path, object Token);

/// <summary>A next-track entry that could not be opened while preparing gapless playback.</summary>
public sealed record SkippedNextTrack(NextTrack Entry, MusicSpinException Error);

/// <summary>Playback moved on to a pre-decoded next track without stopping (gapless), at its first sample.</summary>
public sealed class TrackAdvancedEventArgs(TrackInfo track, object token, TimeSpan previousPlayed, IReadOnlyList<SkippedNextTrack> skipped) : EventArgs
{
    public TrackInfo Track { get; } = track;

    /// <summary>The <see cref="NextTrack.Token"/> of the entry now playing.</summary>
    public object Token { get; } = token;

    /// <summary>How much of the previous track was played (its length when it played to the end).</summary>
    public TimeSpan PreviousPlayed { get; } = previousPlayed;

    /// <summary>Entries before this one that could not be opened (missing, unsupported) and were passed over.</summary>
    public IReadOnlyList<SkippedNextTrack> Skipped { get; } = skipped;
}

/// <summary>Decode-ahead buffer health and gapless state.</summary>
/// <param name="Underruns">Audio callbacks that found no decoded audio and played silence, since the player started.</param>
/// <param name="BufferMilliseconds">Configured decode-ahead buffer.</param>
/// <param name="BufferedMilliseconds">Audio decoded and waiting to be played right now.</param>
public sealed record PlaybackDiagnostics(long Underruns, bool Gapless, int BufferMilliseconds, double BufferedMilliseconds);

public sealed class PlayerErrorEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}

/// <summary>
/// High-level player used by both the desktop app and the CLI.
/// Events may be raised on background threads; UI callers must marshal to their dispatcher.
/// </summary>
public interface IPlayer : IDisposable
{
    PlayerState State { get; }

    TrackInfo? CurrentTrack { get; }

    TimeSpan Position { get; }

    TimeSpan Duration { get; }

    /// <summary>Linear gain 0..1. At exactly 1.0 samples pass through untouched (bit-perfect path).</summary>
    float Volume { get; set; }

    /// <summary>Opens <paramref name="path"/> and makes it the current track (state becomes Stopped).</summary>
    /// <exception cref="MusicSpinException">FileNotFound, UnsupportedFormat, DecoderUnavailable.</exception>
    TrackInfo Load(string path);

    /// <summary>Starts or resumes playback of the current track.</summary>
    /// <exception cref="MusicSpinException">InvalidState when no track is loaded; AudioDeviceError.</exception>
    void Play();

    void Pause();

    /// <summary>Stops playback and rewinds to the start. Releases the audio device.</summary>
    void Stop();

    void Seek(TimeSpan position);

    /// <summary>
    /// Positions a stopped track at <paramref name="position"/> and reports it as Paused without opening the audio device
    /// (used when a saved queue is restored). <see cref="Play"/> continues from there.
    /// </summary>
    /// <exception cref="MusicSpinException">InvalidState when no track is loaded.</exception>
    void Cue(TimeSpan position);

    event EventHandler<PlayerStateChangedEventArgs>? StateChanged;

    event EventHandler<PlayerPositionChangedEventArgs>? PositionChanged;

    event EventHandler<TrackLoadedEventArgs>? TrackLoaded;

    /// <summary>Raised when the current track played to its end.</summary>
    event EventHandler? PlaybackEnded;

    event EventHandler<PlayerErrorEventArgs>? Error;

    /// <summary>
    /// The entries to continue with when the current track ends, in order (first playable one wins). Players that support
    /// gapless playback decode ahead into them; others ignore this. Call again whenever the order changes.
    /// </summary>
    void SetNextTracks(IReadOnlyList<NextTrack> next)
    {
    }

    /// <summary>Raised instead of <see cref="PlaybackEnded"/> when playback continued gaplessly into a <see cref="NextTrack"/>.</summary>
    event EventHandler<TrackAdvancedEventArgs>? TrackAdvanced
    {
        add { }
        remove { }
    }

    PlaybackDiagnostics? Diagnostics => null;
}
