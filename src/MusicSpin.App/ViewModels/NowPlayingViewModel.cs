using System.ComponentModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Controls;
using MusicSpin.App.Services;
using MusicSpin.Core;
using MusicSpin.Core.Library;
using MusicSpin.Core.Playback;

namespace MusicSpin.App.ViewModels;

/// <summary>A track in Now Playing's list (mockup: .tracklist li).</summary>
public sealed partial class NowPlayingTrackViewModel(TrackSummary track, int index) : ObservableObject
{
    /// <summary>"♪" in place of the number while the current track plays.</summary>
    public const string PlayingMark = "♪";

    public TrackSummary Track { get; } = track;

    /// <summary>The track's place in the album's list.</summary>
    public int Index { get; } = index;

    public string Title => Track.Title;

    public string Duration => Formats.Time(Track.DurationSeconds);

    /// <summary>The track the player has loaded (highlighted).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Marker))]
    public partial bool IsCurrent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Marker))]
    public partial bool IsPlaying { get; set; }

    /// <summary>The track number, or ♪ while it plays.</summary>
    public string Marker => IsCurrent && IsPlaying ? PlayingMark
        : (Track.TrackNumber ?? Index + 1).ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The Now Playing overlay (mockup: #np): the playing album on the turntable, its title, "artist · year" and tracks, over
/// its cover blurred. The player bar's cover opens and closes it; ✕ and Esc close it. It also holds the deck's state: the
/// arm and whether the platter spins follow play/pause from anywhere (the transport, Space, the CLI).
/// </summary>
public sealed partial class NowPlayingViewModel : LayerViewModel
{
    private readonly PlayerViewModel _player;
    private readonly ILibraryService _library;
    private readonly ICoverService _covers;
    private readonly Notices _notices;
    private string? _albumId;
    private bool _shown;
    private bool _deckHeld;

    public NowPlayingViewModel(PlayerViewModel player, ILibraryService library, ICoverService covers, Notices notices)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(covers);
        ArgumentNullException.ThrowIfNull(notices);
        _player = player;
        _library = library;
        _covers = covers;
        _notices = notices;
        _player.PropertyChanged += OnPlayerChanged;
        FollowPlayer();
        ShowKicker();
    }

    public override int Order => LayerOrder.NowPlaying;

    /// <summary>The album's title (or the track's, for a file outside the library).</summary>
    [ObservableProperty]
    public partial string? Title { get; private set; }

    /// <summary>"Artist · Year".</summary>
    [ObservableProperty]
    public partial string? ArtistLine { get; private set; }

    /// <summary>The cover, large: the record's label and the blurred background.</summary>
    [ObservableProperty]
    public partial Bitmap? Cover { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<NowPlayingTrackViewModel> Tracks { get; private set; } = [];

    /// <summary>"Radio · Jazz" while a station is on air; null hides the line.</summary>
    [ObservableProperty]
    public partial string? Kicker { get; private set; }

    /// <summary>The tonearm: parked, over the lead-in (lifted), or lowered onto the record.</summary>
    [ObservableProperty]
    public partial ArmState Arm { get; private set; }

    /// <summary>The platter turns (and the deck's LED is lit).</summary>
    [ObservableProperty]
    public partial bool IsSpinning { get; private set; }

    /// <summary>No record on the platter (while one travels onto it).</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    /// <summary>
    /// The turntable scene drives the deck (the arm, the spin, the empty platter) until <see cref="ReleaseDeck"/>;
    /// meanwhile the player's state doesn't move it.
    /// </summary>
    public void HoldDeck(ArmState arm, bool spinning, bool empty)
    {
        _deckHeld = true;
        (Arm, IsSpinning, IsEmpty) = (arm, spinning, empty);
    }

    /// <summary>The deck follows the player again (from its next change; the scene leaves it as playback starts).</summary>
    public void ReleaseDeck()
    {
        _deckHeld = false;
        IsEmpty = false;
    }

    /// <summary>
    /// Shows the playing track's library album (<paramref name="albumId"/>; null: a file outside the library, shown with
    /// the player's own title and artist).
    /// </summary>
    public async Task ShowAlbumAsync(string? albumId)
    {
        _albumId = albumId;
        _shown = albumId is not null;
        if (albumId is null)
        {
            (Title, ArtistLine, Cover, Tracks) = (_player.Album ?? _player.Title, _player.Artist, null, []);
            return;
        }

        await _notices.GuardAsync(async () =>
        {
            var albums = _library.AlbumsAsync();
            var tracks = _library.AlbumTracksAsync(albumId);
            var cover = _covers.CoverAsync(albumId, CoverSize.Large);
            await Task.WhenAll(albums, tracks, cover).ConfigureAwait(true);
            if (_albumId != albumId)
            {
                return;
            }

            var album = albums.Result.FirstOrDefault(a => a.Id == albumId);
            Title = album?.Title ?? _player.Album;
            ArtistLine = string.Join(" · ", new[] { album?.Artist ?? _player.Artist, album?.Year?.ToString(CultureInfo.InvariantCulture) }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            Cover = cover.Result;
            Tracks = [.. tracks.Result.Select((t, i) => new NowPlayingTrackViewModel(t, i))];
            MarkTracks();
        }).ConfigureAwait(true);
    }

    /// <summary>A click on a track: the album plays from it (which ends radio).</summary>
    [RelayCommand]
    private Task PlayTrack(NowPlayingTrackViewModel track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return _notices.GuardAsync(() => _player.PlayTracksAsync([.. Tracks.Select(t => t.Track)], track.Index));
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.State):
                FollowPlayer();
                MarkTracks();
                break;
            case nameof(PlayerViewModel.TrackId):
                MarkTracks();
                break;
            case nameof(PlayerViewModel.QueueMode):
            case nameof(PlayerViewModel.RadioDescription):
                ShowKicker();
                break;
            case nameof(PlayerViewModel.Title) or nameof(PlayerViewModel.Artist) or nameof(PlayerViewModel.Album) when !_shown:
                (Title, ArtistLine) = (_player.Album ?? _player.Title, _player.Artist);
                break;
        }
    }

    /// <summary>
    /// The transport acts at once (mockup: syncPlaying): playing lowers the arm onto the record and turns the platter;
    /// a pause lifts the arm where it is and stops the platter; with nothing loaded the arm is parked.
    /// </summary>
    private void FollowPlayer()
    {
        if (_deckHeld)
        {
            return;
        }

        IsSpinning = _player.IsPlaying;
        Arm = _player.State switch
        {
            PlayerState.Playing => ArmState.Lowered,
            PlayerState.Paused when Arm != ArmState.Parked => ArmState.OverLeadIn,
            PlayerState.Paused => ArmState.Parked,
            _ => ArmState.Parked,
        };
    }

    private void ShowKicker()
        => Kicker = !_player.IsRadio ? null
            : string.IsNullOrWhiteSpace(_player.RadioDescription) ? "Radio" : $"Radio · {_player.RadioDescription}";

    private void MarkTracks()
    {
        foreach (var track in Tracks)
        {
            track.IsCurrent = track.Track.Id == _player.TrackId;
            track.IsPlaying = _player.IsPlaying;
        }
    }
}
