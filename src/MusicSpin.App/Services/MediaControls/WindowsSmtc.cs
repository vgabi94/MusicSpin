namespace MusicSpin.App.Services.MediaControls;

/// <summary>
/// The Windows SMTC translation (#34) without any native call, so it is tested on every OS: what the System Media
/// Transport Controls show for a <see cref="MediaInfo"/>, and which command a button or a timeline request sends.
/// <see cref="WindowsMediaControlsAdapter"/> only writes these values.
/// </summary>
public static class WindowsSmtc
{
    /// <summary><c>Windows.Media.MediaPlaybackStatus</c>.</summary>
    public enum Status
    {
        Closed = 0,
        Changing = 1,
        Stopped = 2,
        Playing = 3,
        Paused = 4,
    }

    /// <summary><c>Windows.Media.SystemMediaTransportControlsButton</c>.</summary>
    public enum Button
    {
        Play = 0,
        Pause = 1,
        Stop = 2,
        Record = 3,
        FastForward = 4,
        Rewind = 5,
        Next = 6,
        Previous = 7,
        ChannelUp = 8,
        ChannelDown = 9,
    }

    /// <summary>The fields; times are TimeSpan ticks as WinRT takes them. A missing tag is an empty string (SMTC has no "unset").</summary>
    public static Fields Describe(MediaInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var end = Math.Max(0, info.Duration.Ticks);
        var position = Math.Max(0, info.Position.Ticks);
        if (end > 0)
        {
            position = Math.Min(position, end);
        }

        return new Fields(
            info.Playback switch
            {
                MediaPlayback.Playing => Status.Playing,
                MediaPlayback.Paused => Status.Paused,
                _ => Status.Stopped,
            },
            info.Title ?? "",
            info.Artist ?? "",
            info.Album ?? "",
            info.CoverFile is { } cover && File.Exists(cover) ? cover : null,
            end,
            position);
    }

    /// <summary>The command for a pressed button; null for the ones MusicSpin doesn't offer (record, channel, ±wind).</summary>
    public static MediaCommand? CommandFor(int button) => (Button)button switch
    {
        Button.Play => new MediaCommand(MediaCommandKind.Play),
        Button.Pause => new MediaCommand(MediaCommandKind.Pause),
        Button.Stop => new MediaCommand(MediaCommandKind.Stop),
        Button.Next => new MediaCommand(MediaCommandKind.Next),
        Button.Previous => new MediaCommand(MediaCommandKind.Previous),
        _ => null,
    };

    /// <summary>A timeline request (the scrubber in the Windows flyout): the current track only, inside it.</summary>
    public static MediaCommand? PositionRequest(long ticks, string? trackId, long endTicks)
        => trackId is null || ticks < 0 || (endTicks > 0 && ticks > endTicks)
            ? null
            : new MediaCommand(MediaCommandKind.SetPosition, TimeSpan.FromTicks(ticks), TrackId: trackId);

    public sealed record Fields(Status Status, string Title, string Artist, string Album, string? CoverFile, long EndTicks, long PositionTicks);
}
