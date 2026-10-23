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

    /// <summary>A favorite: its heart is filled and always shows (#48).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoriteLabel))]
    public partial bool IsFavorite { get; set; }

    /// <summary>The heart's tooltip: Add to favorites, or Remove from favorites.</summary>
    public string FavoriteLabel => FavoritesViewModel.Label(IsFavorite);

    /// <summary>The track number, or ♪ while it plays.</summary>
    public string Marker => IsCurrent && IsPlaying ? PlayingMark
        : (Track.TrackNumber ?? Index + 1).ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The Now Playing overlay (mockup: #np): the playing album on the turntable, its title, "artist · year" and tracks (each
/// with a heart, #48), over its cover blurred. The player bar's cover opens and closes it; ✕ and Esc close it. It also
/// holds the deck's state: the arm and whether the platter spins follow play/pause from anywhere (the transport, Space,
/// the CLI).
/// </summary>
public sealed partial class NowPlayingViewModel : LayerViewModel, IDisposable
{
    private readonly PlayerViewModel _player;
    private readonly ILibraryService _library;
    private readonly ICoverService _covers;
    private readonly Notices _notices;
    private readonly FavoritesViewModel _favorites;
    private readonly RadioViewModel _radio;
    private string? _albumId;
    private readonly OwedReads _owed;
    private bool _shown;
    private bool _deckHeld;
    private CoverLease? _cover;
    private CoverLease? _backdrop;
    private int _coverReads; // the latest read of the large cover wins: an older one finishing later is let go

    public NowPlayingViewModel(PlayerViewModel player, RadioViewModel radio, ILibraryService library, ICoverService covers, FavoritesViewModel favorites,
        Notices notices, TimeProvider time, IUiDispatcher ui)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(radio);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(covers);
        ArgumentNullException.ThrowIfNull(favorites);
        ArgumentNullException.ThrowIfNull(notices);
        _player = player;
        _library = library;
        _covers = covers;
        _owed = new OwedReads(time, ui);
        _notices = notices;
        _favorites = favorites;
        _radio = radio;
        _player.PropertyChanged += OnPlayerChanged;
        _favorites.Changed += (_, _) => MarkFavorites();
        _radio.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RadioViewModel.Kicker))
            {
                OnPropertyChanged(nameof(Kicker));
            }
        };
        FollowPlayer();
    }

    public override int Order => LayerOrder.NowPlaying;

    /// <summary>The album's title (or the track's, for a file outside the library).</summary>
    [ObservableProperty]
    public partial string? Title { get; private set; }

    /// <summary>"Artist · Year".</summary>
    [ObservableProperty]
    public partial string? ArtistLine { get; private set; }

    /// <summary>The cover, large: the record's label (and the turntable scene's sleeve). Held while shown.</summary>
    [ObservableProperty]
    public partial Bitmap? Cover { get; private set; }

    /// <summary>
    /// The most pixels across the deck has shown its record at (the view reports it): the large cover is decoded no
    /// larger, and read again when the deck outgrows it. It only grows, so a smaller window doesn't decode it again.
    /// </summary>
    public int CoverPixels
    {
        get;
        set
        {
            if (value <= field)
            {
                return;
            }

            var redecode = CoverService.Width(CoverSize.Large, value) != CoverService.Width(CoverSize.Large, field);
            field = value;
            if (redecode && _cover is not null && _albumId is { } albumId)
            {
                _ = ReadCoverAgainAsync(albumId);
            }
        }
    }

    /// <summary>The cover, small: the blurred background is made from it once per album (a blurred image needs few pixels).</summary>
    [ObservableProperty]
    public partial Bitmap? BackdropCover { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<NowPlayingTrackViewModel> Tracks { get; private set; } = [];

    /// <summary>"Radio · Jazz" while a station is on air; null hides the line.</summary>
    public string? Kicker => _radio.Kicker;

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
            (Title, ArtistLine, Tracks) = (_player.Album ?? _player.Title, _player.Artist, []);
            ShowCover(null, ++_coverReads);
            ShowBackdrop(null);
            return;
        }

        // The cover is asked for now but awaited apart: it waits out a busy library on its own (see CoverService).
        var read = ++_coverReads;
        var cover = _covers.CoverAsync(albumId, CoverSize.Large, CoverPixels);
        var taken = false;
        _ = ShowBackdropAsync(albumId, _covers.CoverAsync(albumId, CoverSize.Small));
        try
        {
            await _notices.GuardAsync(async () =>
            {
                IReadOnlyList<AlbumSummary> albums;
                IReadOnlyList<TrackSummary> tracks;
                try
                {
                    var albumsRead = _library.AlbumsAsync();
                    var tracksRead = _library.AlbumTracksAsync(albumId);
                    await Task.WhenAll(albumsRead, tracksRead).ConfigureAwait(true);
                    (albums, tracks) = (albumsRead.Result, tracksRead.Result);
                }
                catch (Exception ex) when (OwedReads.IsBusy(ex))
                {
                    // Another process holds the library: nobody asked for anything, so no notice; the album is read at the
                    // next check, unless another one is shown by then.
                    ShowPlayerOwn(albumId);
                    _owed.Owe(() =>
                    {
                        if (_albumId == albumId)
                        {
                            _ = ShowAlbumAsync(albumId);
                        }
                    });
                    return;
                }
                catch
                {
                    ShowPlayerOwn(albumId); // not the last album's tracks under this one's playback; the notice says why
                    throw;
                }

                if (_albumId != albumId)
                {
                    return;
                }

                var album = albums.FirstOrDefault(a => a.Id == albumId);
                Title = album?.Title ?? _player.Album;
                ArtistLine = string.Join(" · ", new[] { album?.Artist ?? _player.Artist, album?.Year?.ToString(CultureInfo.InvariantCulture) }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
                Tracks = [.. tracks.Select((t, i) => new NowPlayingTrackViewModel(t, i))];
                MarkTracks();
                MarkFavorites();
                if (!cover.IsCompleted && read == _coverReads)
                {
                    ShowCover(null, read); // not the last album's cover under this one's title meanwhile
                }

                var lease = await cover.ConfigureAwait(true);
                taken = true;
                ShowCover(lease, read);
            }).ConfigureAwait(true);
        }
        finally
        {
            if (!taken)
            {
                _ = LetGoAsync(cover); // not shown (another album wanted, or the album not read): let go once it comes
            }
        }
    }

    /// <summary>The large cover again, at the pixels the deck now shows it at (the old one shows until it comes).</summary>
    private async Task ReadCoverAgainAsync(string albumId)
    {
        var read = ++_coverReads;
        CoverLease? lease;
        try
        {
            lease = await _covers.CoverAsync(albumId, CoverSize.Large, CoverPixels).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is MusicSpinException or IOException or UnauthorizedAccessException)
        {
            return; // the smaller one stays
        }

        if (_albumId == albumId)
        {
            ShowCover(lease, read);
        }
        else
        {
            lease?.Dispose();
        }
    }

    /// <summary>
    /// Shows <paramref name="lease"/>'s cover if <paramref name="read"/> is the latest read, and lets go of the one it
    /// replaces once it shows; else lets go of it.
    /// </summary>
    private void ShowCover(CoverLease? lease, int read)
    {
        if (read != _coverReads)
        {
            lease?.Dispose();
            return;
        }

        var old = _cover;
        _cover = lease;
        Cover = lease?.Bitmap;
        if (!ReferenceEquals(old, lease))
        {
            old?.Dispose();
        }
    }

    /// <summary>Shows <paramref name="lease"/>'s small cover behind, and lets go of the one it replaces once it shows.</summary>
    private void ShowBackdrop(CoverLease? lease)
    {
        var old = _backdrop;
        _backdrop = lease;
        BackdropCover = lease?.Bitmap;
        if (!ReferenceEquals(old, lease))
        {
            old?.Dispose();
        }
    }

    /// <summary>
    /// Lets go of a cover nobody will show, once it comes (one that fails to come holds nothing): on the UI thread, as
    /// every other is let go.
    /// </summary>
    private static async Task LetGoAsync(Task<CoverLease?> read)
    {
        try
        {
            (await read.ConfigureAwait(true))?.Dispose();
        }
        catch (Exception ex) when (ex is MusicSpinException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            // nothing to let go
        }
    }

    /// <summary>
    /// The small cover the background is made from, on its own: a slow or failing one doesn't hold up the large cover (the
    /// record's label), nor the other way round. The last album asked for wins; a cover that can't be read leaves the
    /// background plain (the large one, read from the same file, says why if it fails too).
    /// </summary>
    private async Task ShowBackdropAsync(string albumId, Task<CoverLease?> backdrop)
    {
        if (!backdrop.IsCompleted)
        {
            ShowBackdrop(null); // not the last album's background under this one meanwhile
        }

        CoverLease? small;
        try
        {
            small = await backdrop.ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is MusicSpinException or IOException or InvalidOperationException or ArgumentException)
        {
            small = null;
        }

        if (_albumId == albumId)
        {
            ShowBackdrop(small);
        }
        else
        {
            small?.Dispose();
        }
    }

    /// <summary>
    /// The album couldn't be read: until it is, show what the player says is playing, as for a file outside the library,
    /// rather than the album shown before (unless another album is wanted by now).
    /// </summary>
    private void ShowPlayerOwn(string albumId)
    {
        if (_albumId == albumId)
        {
            (Title, ArtistLine, Tracks) = (_player.Album ?? _player.Title, _player.Artist, []);
            ShowCover(null, ++_coverReads);
            ShowBackdrop(null);
        }
    }

    /// <summary>Stops the reads owed to a busy library (the window closes).</summary>
    public void Dispose()
    {
        _owed.Dispose();
        ShowCover(null, ++_coverReads);
        ShowBackdrop(null);
    }

    /// <summary>A click on a track: the album plays from it (which ends radio).</summary>
    [RelayCommand]
    private Task PlayTrack(NowPlayingTrackViewModel track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return _notices.GuardAsync(() => _player.PlayTracksAsync([.. Tracks.Select(t => t.Track)], track.Index));
    }

    /// <summary>A track's heart: it becomes a favorite, or stops being one.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task<bool> ToggleFavorite(NowPlayingTrackViewModel track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return _favorites.ToggleAsync(track.Track.Id);
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

    private void MarkTracks()
    {
        foreach (var track in Tracks)
        {
            track.IsCurrent = track.Track.Id == _player.TrackId;
            track.IsPlaying = _player.IsPlaying;
        }
    }

    private void MarkFavorites()
    {
        foreach (var track in Tracks)
        {
            track.IsFavorite = _favorites.Contains(track.Track.Id);
        }
    }
}
