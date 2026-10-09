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
}
