namespace MusicSpin.App.Services.MediaControls;

/// <summary>
/// The macOS Now Playing translation (#35) without any native call, so it is tested on every OS: what goes into
/// <c>MPNowPlayingInfoCenter</c> for a <see cref="MediaInfo"/>, and which command each <c>MPRemoteCommandCenter</c>
/// handler sends. <see cref="MacMediaControlsAdapter"/> only writes these values.
/// </summary>
public static class MacNowPlaying
{
    /// <summary><c>MPNowPlayingPlaybackState</c>.</summary>
    public enum State : uint
    {
        Unknown = 0,
        Playing = 1,
        Paused = 2,
        Stopped = 3,
    }

    /// <summary>The remote commands MusicSpin handles, by the selector of its handler.</summary>
    public static readonly IReadOnlyList<(string Command, string Selector)> Commands =
    [
        ("playCommand", "musicSpinPlay:"),
        ("pauseCommand", "musicSpinPause:"),
        ("togglePlayPauseCommand", "musicSpinToggle:"),
        ("stopCommand", "musicSpinStop:"),
        ("nextTrackCommand", "musicSpinNext:"),
        ("previousTrackCommand", "musicSpinPrevious:"),
        ("changePlaybackPositionCommand", "musicSpinPosition:"),
    ];

    /// <summary>
    /// The Now Playing fields. macOS runs the clock itself from the elapsed time and the rate, so this only changes when
    /// the track, the state or the position (a seek) does; a missing tag is left out.
    /// </summary>
    public static Fields Describe(MediaInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var duration = info.Duration > TimeSpan.Zero ? info.Duration.TotalSeconds : (double?)null;
        var elapsed = Math.Max(0, info.Position.TotalSeconds);
        if (duration is { } length)
        {
            elapsed = Math.Min(elapsed, length);
        }

        return new Fields(
            Blank(info.Title),
            Blank(info.Artist),
            Blank(info.Album),
            duration,
            elapsed,
            info.Playback == MediaPlayback.Playing ? 1.0 : 0.0,
            info.Playback switch
            {
                MediaPlayback.Playing => State.Playing,
                MediaPlayback.Paused => State.Paused,
                _ => State.Stopped,
            },
            info.CoverFile is { } cover && File.Exists(cover) ? cover : null);
    }

    /// <summary>
    /// The command for a handler selector (<paramref name="position"/> is the event's <c>positionTime</c>, for the
    /// scrubber); null for a selector MusicSpin doesn't know or a position it can't use.
    /// </summary>
    public static MediaCommand? CommandFor(string selector, double position, string? trackId) => selector switch
    {
        "musicSpinPlay:" => new MediaCommand(MediaCommandKind.Play),
        "musicSpinPause:" => new MediaCommand(MediaCommandKind.Pause),
        "musicSpinToggle:" => new MediaCommand(MediaCommandKind.PlayPause),
        "musicSpinStop:" => new MediaCommand(MediaCommandKind.Stop),
        "musicSpinNext:" => new MediaCommand(MediaCommandKind.Next),
        "musicSpinPrevious:" => new MediaCommand(MediaCommandKind.Previous),
        "musicSpinPosition:" when trackId is not null && double.IsFinite(position) && position >= 0
            => new MediaCommand(MediaCommandKind.SetPosition, TimeSpan.FromSeconds(position), TrackId: trackId),
        _ => null,
    };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public sealed record Fields(string? Title, string? Artist, string? Album, double? Duration, double Elapsed, double Rate, State State, string? CoverFile);
}
