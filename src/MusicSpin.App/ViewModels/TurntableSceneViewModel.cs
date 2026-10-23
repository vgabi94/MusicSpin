using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using MusicSpin.App.Controls;
using MusicSpin.App.Services;
using MusicSpin.App.Theming;
using MusicSpin.Core.Library;

namespace MusicSpin.App.ViewModels;

/// <summary>Where the scene starts and lands, on screen (the window answers; rectangles in window coordinates).</summary>
public interface IScenePlaces
{
    /// <summary>The album's sleeve on the albums page (a card, a shelf sleeve or a crate record); null when it isn't on screen.</summary>
    Rect? Source(string albumId);

    /// <summary>The record on the deck, with Now Playing in place (invisible while the scene starts).</summary>
    Rect? Landing();

    /// <summary>Hides the album's sleeve where it sits while its copy flies (mockup: visibility hidden).</summary>
    void HideSource(string albumId, bool hidden);
}

/// <summary>
/// The turntable scene (mockup: playOnTurntable): choosing an album on the albums page carries its record from the
/// sleeve onto the deck, then the arm lowers and the music starts. See <see cref="SceneTimeline"/> for the steps; each
/// time is multiplied by Motion. The album is loaded paused at the start and play is sent as the arm lands; other
/// playback input waits until then, and another album chosen meanwhile plays after it. At Motion None, or when the
/// sleeve isn't on screen, Now Playing opens and the album plays at once.
/// </summary>
/// <remarks>
/// The scene runs on the app's clock (<see cref="TimeProvider"/>, a frame at a time like the spin clock): the view draws
/// the travelling sleeve and record from <see cref="Time"/>, and the deck's arm follows <see cref="NowPlayingViewModel.Arm"/>
/// with its own transitions.
/// </remarks>
public sealed partial class TurntableSceneViewModel : ObservableObject, IDisposable
{
    private readonly PlayerViewModel _player;
    private readonly NowPlayingViewModel _nowPlaying;
    private readonly ILibraryService _library;
    private readonly ICoverService _covers;
    private readonly SettingsViewModel _settings;
    private readonly TimeProvider _time;
    private readonly IUiDispatcher _ui;
    private readonly Notices _notices;
    private ITimer? _timer;
    private long _start;
    private double _multiplier = 1;
    private string? _albumId;
    private string? _next;
    private bool _finishing;

    public TurntableSceneViewModel(PlayerViewModel player, NowPlayingViewModel nowPlaying, ILibraryService library, ICoverService covers,
        SettingsViewModel settings, TimeProvider time, IUiDispatcher ui, Notices notices)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(nowPlaying);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(covers);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(notices);
        (_player, _nowPlaying, _library, _covers, _settings, _time, _ui, _notices) = (player, nowPlaying, library, covers, settings, time, ui, notices);
    }

    /// <summary>The window, which knows where things are on screen; without it the scene is skipped.</summary>
    public IScenePlaces? Places { get; set; }

    /// <summary>A scene is playing (Now Playing fades in place of sliding, input waits).</summary>
    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    public partial SceneStep Step { get; private set; }

    /// <summary>Milliseconds into the scene at Motion Smooth (the real time divided by the Motion multiplier).</summary>
    [ObservableProperty]
    public partial double Time { get; private set; }

    /// <summary>The travelling sleeve and record are drawn (until the record is on the platter).</summary>
    [ObservableProperty]
    public partial bool IsFlying { get; private set; }

    /// <summary>Where the sleeve starts (window coordinates).</summary>
    [ObservableProperty]
    public partial Rect Source { get; private set; }

    /// <summary>Where the record lands: the deck's record (window coordinates).</summary>
    [ObservableProperty]
    public partial Rect Landing { get; private set; }

    /// <summary>The album's cover: the travelling sleeve and the record's label. Held while the scene runs.</summary>
    [ObservableProperty]
    public partial Bitmap? Cover { get; private set; }

    private CoverLease? _cover;

    /// <summary>Plays <paramref name="albumId"/> from its first track, with the scene when it can.</summary>
    public Task PlayAlbumAsync(string albumId)
    {
        ArgumentException.ThrowIfNullOrEmpty(albumId);
        if (IsRunning)
        {
            // The album on its way already: nothing to wait for.
            _next = albumId == _albumId ? null : albumId;
            return Task.CompletedTask;
        }

        return _notices.GuardAsync(async () =>
        {
            var tracks = _library.AlbumTracksAsync(albumId);
            var cover = _covers.CoverAsync(albumId, CoverSize.Large, _nowPlaying.CoverPixels); // as Now Playing's, where it lands
            try
            {
                await Task.WhenAll(tracks, cover).ConfigureAwait(true);
                await FlyOrPlayAsync(albumId, tracks.Result, cover.Result).ConfigureAwait(true);
            }
            finally
            {
                if (cover.IsCompletedSuccessfully && !ReferenceEquals(cover.Result, _cover))
                {
                    cover.Result?.Dispose(); // not flown: nothing shows it
                }
            }
        });
    }

    /// <summary>Starts the scene with the album's cover (<paramref name="cover"/>, held till it ends), or plays at once.</summary>
    private async Task FlyOrPlayAsync(string albumId, IReadOnlyList<TrackSummary> tracks, CoverLease? cover)
    {
        if (tracks.Count == 0)
        {
            return;
        }

        if (IsRunning)
        {
            _next = albumId;
            return;
        }

        // Now Playing open covers the page: nothing to fly from.
        var multiplier = MotionModes.Multiplier(_settings.Motion);
        if (multiplier <= 0 || _nowPlaying.IsOpen || Places?.Source(albumId) is not { } source)
        {
            await PlayAtOnceAsync(albumId, tracks).ConfigureAwait(true);
            return;
        }

        // Now Playing goes in place, invisible, so the record's spot on the deck is final.
        // Measured before the platter is emptied (an empty deck hides its record).
        IsRunning = true;
        if (Places.Landing() is not { Width: > 0 } landing)
        {
            IsRunning = false;
            await PlayAtOnceAsync(albumId, tracks).ConfigureAwait(true);
            return;
        }

        _nowPlaying.HoldDeck(ArmState.Parked, spinning: false, empty: true);
        _player.HoldInput();
        (_albumId, _multiplier, _start, _finishing) = (albumId, multiplier, _time.GetTimestamp(), false);
        _cover = cover;
        (Source, Landing, Cover, Time, Step, IsFlying) = (source, landing, cover?.Bitmap, 0, SceneStep.Flying, true);
        Places.HideSource(albumId, hidden: true);
        _ = _nowPlaying.ShowAlbumAsync(albumId);
        _timer = _time.CreateTimer(_ => _ui.Post(Tick), null, SpinClock.FrameInterval, SpinClock.FrameInterval);
        await _player.LoadTracksAsync(tracks).ConfigureAwait(true);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private async Task PlayAtOnceAsync(string albumId, IReadOnlyList<TrackSummary> tracks)
    {
        _nowPlaying.Open();
        _ = _nowPlaying.ShowAlbumAsync(albumId);
        await _player.PlayTracksAsync(tracks).ConfigureAwait(true);
    }

    /// <summary>A frame: the step, Now Playing, the sleeve where it sat, and the deck at this point in the scene.</summary>
    private void Tick()
    {
        if (!IsRunning || _finishing)
        {
            return;
        }

        var t = Math.Min(_time.GetElapsedTime(_start).TotalMilliseconds / _multiplier, SceneTimeline.End);
        Time = t;
        Step = SceneTimeline.StepAt(t);
        if (t >= SceneTimeline.OpenAt && !_nowPlaying.IsOpen)
        {
            _nowPlaying.Open();
        }

        if (t >= SceneTimeline.FlightMs)
        {
            Places?.HideSource(_albumId!, hidden: false);
        }

        if (t >= SceneTimeline.LandAt)
        {
            IsFlying = false;
            var arm = t >= SceneTimeline.LowerAt ? ArmState.Lowered : t >= SceneTimeline.ArmOnAt ? ArmState.OverLeadIn : ArmState.Parked;
            _nowPlaying.HoldDeck(arm, spinning: true, empty: false);
        }

        if (t >= SceneTimeline.End)
        {
            _ = FinishAsync();
        }
    }

    /// <summary>The arm is down: the music starts, then what waited.</summary>
    private Task FinishAsync() => _notices.GuardAsync(async () =>
    {
        _finishing = true;
        Dispose();
        try
        {
            await _player.ResumeAsync().ConfigureAwait(true);
        }
        finally
        {
            (Step, IsRunning) = (SceneStep.None, false);
            _nowPlaying.ReleaseDeck();
            Cover = null;
            _cover?.Dispose(); // once nothing shows it
            _cover = null;
        }

        await _player.ReleaseInputAsync().ConfigureAwait(true);
        if (_next is { } next)
        {
            _next = null;
            await PlayAlbumAsync(next).ConfigureAwait(true);
        }
    });
}
