namespace MusicSpin.App.Services.MediaControls;

/// <summary>What the player is doing, as the operating system's media controls show it.</summary>
public enum MediaPlayback
{
    Stopped,
    Paused,
    Playing,
}

/// <summary>
/// The current track as the OS media controls show it (#34, #35, #36): tags, the cached cover file, the timeline and the
/// volume. <see cref="Seeked"/> says the position jumped (a seek here, in the OS controls or from the CLI), not just ran
/// on: MPRIS sends its <c>Seeked</c> signal then, Windows and macOS update their timeline.
/// </summary>
public sealed record MediaInfo(
    string? Title,
    string? Artist,
    string? Album,
    string? CoverFile,
    TimeSpan Duration,
    TimeSpan Position,
    MediaPlayback Playback,
    double Volume,
    string? TrackId,
    bool Seeked = false);

/// <summary>A command from the OS media controls (a media key, Control Center, a desktop widget, <c>playerctl</c>).</summary>
public enum MediaCommandKind
{
    Play,
    Pause,
    PlayPause,
    Stop,
    Next,
    Previous,

    /// <summary>Move by <see cref="MediaCommand.Offset"/> (may be negative).</summary>
    SeekBy,

    /// <summary>Go to <see cref="MediaCommand.Offset"/> in the track <see cref="MediaCommand.TrackId"/> (when given).</summary>
    SetPosition,

    /// <summary>Set the volume to <see cref="MediaCommand.Value"/> (0..1).</summary>
    SetVolume,
}

public sealed record MediaCommand(MediaCommandKind Kind, TimeSpan Offset = default, double Value = 0, string? TrackId = null);

/// <summary>
/// One operating system's media controls: a thin adapter that shows <see cref="MediaInfo"/> and reports commands. All
/// the logic (what to show when, which player command a key sends) is in <see cref="MediaControlsBridge"/>, so it is
/// tested on every OS with a fake adapter; an adapter only translates.
/// </summary>
public interface IMediaControlsAdapter : IDisposable
{
    /// <summary>"Windows SMTC", "macOS Now Playing", "MPRIS": for the log and the tests.</summary>
    string Name { get; }

    /// <summary>A command from the OS. May be raised on any thread; the bridge moves it to the UI thread.</summary>
    event EventHandler<MediaCommand>? CommandReceived;

    /// <summary>Shows <paramref name="info"/> (called on the UI thread).</summary>
    void Update(MediaInfo info);

    /// <summary>Nothing plays: the controls go blank (or away). Called on the UI thread.</summary>
    void Clear();
}

/// <summary>
/// How the app gets its OS media controls (<see cref="Composition.AppServices"/>): the adapter for this OS (null where there is none,
/// and in tests that don't want one), and the cover file of a library track.
/// </summary>
public sealed record MediaControlsSetup(Func<IMediaControlsAdapter?> CreateAdapter, Func<string, Task<string?>> CoverFile);
