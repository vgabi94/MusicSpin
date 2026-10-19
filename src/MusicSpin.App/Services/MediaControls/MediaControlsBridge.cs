using System.ComponentModel;
using MusicSpin.App.ViewModels;
using MusicSpin.Core.Playback;

namespace MusicSpin.App.Services.MediaControls;

/// <summary>
/// The platform-neutral half of the OS media controls (#34): player state in, OS commands out. It follows
/// <see cref="PlayerViewModel"/>, so a track the CLI starts, a pause from another client or a seek all reach the OS, and it
/// turns the OS's commands into player commands. Nothing is sent before the player is connected, and the controls are
/// cleared when playback stops or the player goes away.
/// <para>Updates are only sent when something the OS shows changed: a status every poll that only moves the position on
/// as expected is not passed on (the OS runs its own clock from the last position), but a jump is, marked
/// <see cref="MediaInfo.Seeked"/>.</para>
/// </summary>
public sealed class MediaControlsBridge : IDisposable
{
    /// <summary>A position this far from where it should be by now counts as a seek.</summary>
    public static readonly TimeSpan SeekTolerance = TimeSpan.FromSeconds(1.5);

    private readonly PlayerViewModel _player;
    private readonly IMediaControlsAdapter _adapter;
    private readonly Func<string, Task<string?>> _coverFile;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private MediaInfo? _shown;
    private long _shownAt;
    private bool _cleared = true;
    private string? _coverTrack;
    private string? _cover;
    private bool _disposed;

    /// <param name="player">The player to follow and to command.</param>
    /// <param name="adapter">The OS media controls.</param>
    /// <param name="coverFile">The cover image file of a library track id (null when it has none); may fail.</param>
    /// <param name="ui">Where the adapter's commands are handled.</param>
    /// <param name="time">The clock the position runs on between statuses.</param>
    public MediaControlsBridge(PlayerViewModel player, IMediaControlsAdapter adapter, Func<string, Task<string?>> coverFile, IUiDispatcher ui, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(coverFile);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(time);
        _player = player;
        _adapter = adapter;
        _coverFile = coverFile;
        _ui = ui;
        _time = time;
        _player.StatusApplied += OnStatusApplied;
        _player.PropertyChanged += OnPlayerChanged;
        _adapter.CommandReceived += OnCommand;
        Follow();
    }

    /// <summary>Commands handled so far (tests wait on this rather than on time).</summary>
    public int CommandsHandled { get; private set; }

    /// <summary>For tests: the cover lookup of the current track, once started.</summary>
    internal Task CoverLookup { get; private set; } = Task.CompletedTask;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _player.StatusApplied -= OnStatusApplied;
        _player.PropertyChanged -= OnPlayerChanged;
        _adapter.CommandReceived -= OnCommand;
        try
        {
            _adapter.Clear();
        }
        finally
        {
            _adapter.Dispose();
        }
    }

    private void OnStatusApplied(object? sender, EventArgs e) => Follow();

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.IsConnected) && !_player.IsConnected)
        {
            Follow();
        }
    }

    private void Follow()
    {
        if (_disposed)
        {
            return;
        }

        var playback = _player.State switch
        {
            PlayerState.Playing => MediaPlayback.Playing,
            PlayerState.Paused => MediaPlayback.Paused,
            _ => MediaPlayback.Stopped,
        };
        if (!_player.IsConnected || !_player.HasTrack || playback == MediaPlayback.Stopped)
        {
            if (!_cleared)
            {
                _adapter.Clear();
                _cleared = true;
            }

            _shown = null;
            return;
        }

        var trackKey = _player.TrackId ?? _player.Path;
        if (!string.Equals(trackKey, _coverTrack, StringComparison.Ordinal))
        {
            _coverTrack = trackKey;
            _cover = null;
            if (_player.TrackId is { } id)
            {
                CoverLookup = LookUpCoverAsync(id, trackKey);
            }
        }

        var info = new MediaInfo(_player.Title, _player.Artist, _player.Album, _cover, Seconds(_player.Duration), Seconds(_player.Position), playback,
            _player.Muted ? 0 : _player.Volume, _player.TrackId);
        Show(info);
    }

    private void Show(MediaInfo info)
    {
        var now = _time.GetTimestamp();
        var previous = _shown;
        var seeked = false;
        if (previous is not null && _cleared is false && SameTrack(previous, info))
        {
            var expected = previous.Position + (previous.Playback == MediaPlayback.Playing ? _time.GetElapsedTime(_shownAt, now) : TimeSpan.Zero);
            seeked = (info.Position - expected).Duration() > SeekTolerance;
            if (!seeked && previous with { Position = info.Position, Seeked = false } == info)
            {
                return; // only the clock ran on: the OS runs its own
            }
        }

        _shown = info;
        _shownAt = now;
        _cleared = false;
        _adapter.Update(seeked ? info with { Seeked = true } : info);
    }

    private static bool SameTrack(MediaInfo a, MediaInfo b)
        => string.Equals(a.TrackId, b.TrackId, StringComparison.Ordinal) && string.Equals(a.Title, b.Title, StringComparison.Ordinal);

    private async Task LookUpCoverAsync(string trackId, string? trackKey)
    {
        string? file;
        try
        {
            file = await _coverFile(trackId).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Core.MusicSpinException or IOException or UnauthorizedAccessException)
        {
            return; // no cover: the controls show the track without one
        }

        _ui.Post(() =>
        {
            if (!_disposed && file is not null && string.Equals(trackKey, _coverTrack, StringComparison.Ordinal))
            {
                _cover = file;
                Follow();
            }
        });
    }

    private void OnCommand(object? sender, MediaCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _ui.Post(() => _ = HandleAsync(command));
    }

    private async Task HandleAsync(MediaCommand command)
    {
        try
        {
            await SendAsync(command).ConfigureAwait(true);
        }
        finally
        {
            CommandsHandled++;
        }
    }

    private async Task SendAsync(MediaCommand command)
    {
        if (_disposed || !_player.IsConnected)
        {
            return; // nothing is sent before the player is connected (a media key must not start one)
        }

        var (cmd, args) = command.Kind switch
        {
            MediaCommandKind.Play => ("resume", null),
            MediaCommandKind.Pause => ("pause", null),
            MediaCommandKind.PlayPause => ("toggle", null),
            MediaCommandKind.Stop => ("stop", null),
            MediaCommandKind.Next => ("next", null),
            MediaCommandKind.Previous => ("prev", null),
            MediaCommandKind.SeekBy => ("seek", (object?)new { delta = command.Offset.TotalSeconds }),
            MediaCommandKind.SetPosition when command.TrackId is null || string.Equals(command.TrackId, _player.TrackId, StringComparison.Ordinal)
                => ("seek", new { seconds = Math.Clamp(command.Offset.TotalSeconds, 0, Math.Max(0, _player.Duration)) }),
            MediaCommandKind.SetVolume => ("volume", new { value = Math.Clamp(command.Value, 0, 1) }),
            _ => ((string?)null, (object?)null), // a position for a track that is no longer current (MPRIS says: ignore it)
        };
        if (cmd is not null)
        {
            await _player.SendAsync(cmd, args).ConfigureAwait(true);
        }
    }

    private static TimeSpan Seconds(double seconds) => double.IsFinite(seconds) && seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
}
