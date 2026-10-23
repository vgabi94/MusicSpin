using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Services;
using MusicSpin.Core.Library;
using MusicSpin.Core.Playback;

namespace MusicSpin.App.ViewModels;

/// <summary>One track in a list.</summary>
public sealed partial class TrackItemViewModel(TrackSummary track, AlbumItemViewModel? album, AlbumArt art) : ObservableObject
{
    public TrackSummary Track { get; } = track;

    /// <summary>The track's album, when the page read the albums (the Tracks page does, a playlist doesn't).</summary>
    public AlbumItemViewModel? AlbumItem { get; } = album;

    /// <summary>The album's cover (the row's thumbnail).</summary>
    public AlbumArt Art { get; } = art;

    /// <summary>The playlist entry this row shows; null on the Tracks page.</summary>
    public PlaylistEntryItem? Entry { get; init; }

    /// <summary>
    /// False for a playlist entry whose file is missing: the row is dimmed, and it can't be played, queued or dragged
    /// out of its playlist (it can still be removed, or moved within it).
    /// </summary>
    public bool IsAvailable => Entry?.IsAvailable ?? true;

    /// <summary>The entry's place in its playlist (1-based); null on the Tracks page.</summary>
    public int? Position => Entry?.Position;

    public string Title => Track.Title;

    public string Artist => Track.Artist;

    public string Album => Track.Album;

    /// <summary>The thumbnail's placeholder names the album and its artist.</summary>
    public string AlbumArtist => AlbumItem?.Artist ?? Track.Artist;

    public string Genre => LibraryOrder.GenreOf(Track.Genre ?? AlbumItem?.Album.Genre);

    public string Number => Track.TrackNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    public string Duration => Formats.Time(Track.DurationSeconds);

    /// <summary>The track loaded in the player, playing or paused (mockup: .tr.cur).</summary>
    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    /// <summary>The loaded track is playing: its number shows ♪.</summary>
    [ObservableProperty]
    public partial bool ShowsNote { get; set; }

    /// <summary>A favorite: its heart is filled and always shows (#48).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoriteLabel))]
    public partial bool IsFavorite { get; set; }

    /// <summary>The heart's tooltip.</summary>
    public string FavoriteLabel => FavoritesViewModel.Label(IsFavorite);

    /// <summary>A library track (a plain file in a playlist can't be a favorite: it has no heart).</summary>
    public bool CanBeFavorite => Track.Id.Length > 0;
}

/// <summary>A track row; its type says which columns it has (the group header names the rest).</summary>
public abstract record TrackRow(TrackItemViewModel Item);

/// <summary>Under an album header: number, title, duration (mockup: .tlist.m-album).</summary>
public sealed record NumberedTrackRow(TrackItemViewModel Item) : TrackRow(Item);

/// <summary>Under a genre header, and in playlists: thumbnail, title and artist, album, duration (mockup: m-genre, m-plain).</summary>
public sealed record TrackRowWithAlbum(TrackItemViewModel Item) : TrackRow(Item);

/// <summary>Ungrouped: thumbnail, title and artist, album, genre, duration (mockup: m-none).</summary>
public sealed record TrackRowWithAlbumAndGenre(TrackItemViewModel Item) : TrackRow(Item);

/// <summary>On a playlist page: as <see cref="TrackRowWithAlbum"/>, with ✕ on hover to remove it; dragging it reorders (#33).</summary>
public sealed record PlaylistTrackRow(TrackItemViewModel Item) : TrackRow(Item);

/// <summary>An album's header on the Tracks page (mockup: .talbum): its cover, title, details, Play and ＋.</summary>
public sealed class TrackAlbumHeaderViewModel(AlbumItemViewModel album, IReadOnlyList<TrackSummary> tracks, bool isFirst)
{
    public AlbumItemViewModel Album { get; } = album;

    /// <summary>All the album's tracks, in order (what Play and ＋ use, whatever the search shows).</summary>
    public IReadOnlyList<TrackSummary> Tracks { get; } = tracks;

    public bool IsFirst { get; } = isFirst;
}

/// <summary>
/// A list of tracks: the whole library (Tracks) or a playlist. Playing a track plays the list as shown from it, so the
/// next track follows in the same order. ＋ adds a track to the queue.
/// </summary>
public abstract partial class TrackListPageViewModel : PageViewModel, IDisposable
{
    private List<TrackItemViewModel> _all = [];
    private int _loads;
    private bool _loaded;

    protected TrackListPageViewModel(ICoverService covers, PlayerViewModel player, MenuViewModel menu, PlaylistActions playlists,
        FavoritesViewModel favorites, Notices notices)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(favorites);
        Covers = covers;
        Player = player;
        Menu = menu;
        Playlists = playlists;
        Favorites = favorites;
        Notices = notices;
        Player.PropertyChanged += OnPlayerChanged;
        Favorites.Changed += OnFavoritesChanged;
    }

    /// <summary>A playlist row is dragged over the list (the window reports it): the drop line shows where it would go.</summary>
    [ObservableProperty]
    public partial bool IsDragOver { get; set; }

    /// <summary>The tracks shown, in order.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<TrackItemViewModel> Tracks { get; private set; } = [];

    /// <summary>What the list shows: headers (<see cref="GroupHeaderViewModel"/>, <see cref="TrackAlbumHeaderViewModel"/>) and <see cref="TrackRow"/>s.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<object> Rows { get; private set; } = [];

    protected ICoverService Covers { get; }

    protected PlayerViewModel Player { get; }

    protected MenuViewModel Menu { get; }

    protected PlaylistActions Playlists { get; }

    protected FavoritesViewModel Favorites { get; }

    protected Notices Notices { get; }

    /// <summary>Every track read, in the library's (or the playlist's) order.</summary>
    protected IReadOnlyList<TrackItemViewModel> All => _all;

    /// <summary>Reads the tracks again. Of reads that overlap (a heart and a library change at once), the last started wins.</summary>
    public override async Task LoadAsync()
    {
        var load = ++_loads;
        var read = await ReadAsync().ConfigureAwait(true);
        if (load != _loads)
        {
            return;
        }

        // The same tracks (a heart, or a change elsewhere in the library): the rows stay, and so do hover and focus.
        if (_loaded && read.Count == _all.Count && read.Zip(_all).All(p => p.First.Track == p.Second.Track && p.First.Entry == p.Second.Entry))
        {
            MarkPlaying();
            MarkFavorites();
            return;
        }

        _all = [.. read];
        _loaded = true;
        MarkPlaying();
        MarkFavorites();
        Filter();
    }

    /// <summary>A row's heart (every row's stays enabled while one is saved).</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task<bool> ToggleFavorite(TrackItemViewModel track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return Favorites.ToggleAsync(track.Track.Id);
    }

    /// <summary>The favorites changed: the hearts follow (the Favorites view reads its list again).</summary>
    protected virtual void OnFavoritesChanged() => MarkFavorites();

    private void OnFavoritesChanged(object? sender, EventArgs e) => OnFavoritesChanged();

    /// <summary>The page is gone (its playlist was deleted): it stops following the player and the favorites.</summary>
    public void Dispose()
    {
        Player.PropertyChanged -= OnPlayerChanged;
        Favorites.Changed -= OnFavoritesChanged;
        GC.SuppressFinalize(this);
    }

    private void MarkFavorites()
    {
        foreach (var track in _all)
        {
            track.IsFavorite = Favorites.Contains(track.Track.Id);
        }
    }

    /// <summary>The page's tracks, as items.</summary>
    protected abstract Task<IReadOnlyList<TrackItemViewModel>> ReadAsync();

    protected abstract string EmptyLibraryText { get; }

    /// <summary>The whole library's tracks (not a playlist's): an empty list means an empty library.</summary>
    protected virtual bool IsLibraryPage => false;

    /// <summary>The rows for the tracks shown (<see cref="Tracks"/>, already sorted).</summary>
    protected abstract IReadOnlyList<object> LayOut(IReadOnlyList<TrackItemViewModel> shown);

    /// <summary>The tracks the search lets through, in the order shown.</summary>
    protected virtual IEnumerable<TrackItemViewModel> Shown() => _all.Where(t => Matches(t.Title, t.Artist, t.Album));

    /// <summary>
    /// Plays the list as shown from <paramref name="track"/>; entries whose files are missing are left out. When the player is
    /// already playing this list it jumps to the track and keeps the queue (what was queued up next included).
    /// </summary>
    [RelayCommand]
    private Task PlayTrack(TrackItemViewModel track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (!track.IsAvailable)
        {
            return Task.CompletedTask;
        }

        var playable = Playable();
        return Notices.GuardAsync(() => Player.PlayFromListAsync([.. playable.Select(t => t.Track)], Math.Max(0, IndexOf(playable, track))));
    }

    /// <summary>A row's ＋: Play next, Add to queue.</summary>
    [RelayCommand]
    private void OpenTrackMenu(TrackItemViewModel track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.IsAvailable)
        {
            ShowQueueMenu([track.Track]);
        }
    }

    /// <summary>The album header's Play: the whole album from its first track.</summary>
    [RelayCommand]
    private Task PlayAlbum(TrackAlbumHeaderViewModel album)
    {
        ArgumentNullException.ThrowIfNull(album);
        return Notices.GuardAsync(() => Player.PlayTracksAsync(album.Tracks));
    }

    /// <summary>The album header's ＋.</summary>
    [RelayCommand]
    private void OpenAlbumMenu(TrackAlbumHeaderViewModel album)
    {
        ArgumentNullException.ThrowIfNull(album);
        ShowQueueMenu(album.Tracks);
    }

    private bool CanPlayAll() => Tracks.Any(t => t.IsAvailable);

    /// <summary>A playlist's ▶ Play: the whole list as shown, from the start (mockup: #plPlay). It ends a radio station.</summary>
    [RelayCommand(CanExecute = nameof(CanPlayAll))]
    private Task PlayAll() => Notices.GuardAsync(() => Player.PlayTracksAsync([.. Playable().Select(t => t.Track)]));

    /// <summary>A playlist's ＋ Queue: the whole list as shown (mockup: #plQueue).</summary>
    [RelayCommand(CanExecute = nameof(CanPlayAll))]
    private void QueueAll() => ShowQueueMenu([.. Playable().Select(t => t.Track)]);

    /// <summary>The tracks shown that can play (a missing file can't).</summary>
    private List<TrackItemViewModel> Playable() => [.. Tracks.Where(t => t.IsAvailable)];

    /// <summary>The ＋ menu (mockup: queueMenu).</summary>
    protected void ShowQueueMenu(IReadOnlyList<TrackSummary> tracks) => Menu.Show(QueueMenu.Entries(tracks, Player, Playlists, Notices));

    protected override void Filter()
    {
        Tracks = [.. Shown()];
        IsLibraryEmpty = IsLibraryPage && _all.Count == 0;
        CountText = Count(Tracks.Count);
        EmptyText = _all.Count == 0 ? EmptyLibraryText : Tracks.Count == 0 ? $"Nothing matches “{SearchText.Trim()}”." : null;
        Rows = LayOut(Tracks);
        PlayAllCommand.NotifyCanExecuteChanged();
        QueueAllCommand.NotifyCanExecuteChanged();
    }

    protected override void MarkPlaying()
    {
        foreach (var track in _all)
        {
            track.IsPlaying = track.Track.Id == PlayingTrackId;
            track.ShowsNote = track.IsPlaying && Player.IsPlaying;
        }
    }

    private static int IndexOf(List<TrackItemViewModel> tracks, TrackItemViewModel track)
    {
        for (var i = 0; i < tracks.Count; i++)
        {
            if (ReferenceEquals(tracks[i], track))
            {
                return i;
            }
        }

        return -1;
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.IsPlaying))
        {
            MarkPlaying();
        }
    }
}

/// <summary>
/// Tracks: every library track, by album (mockup: renderTracks). Albums come in the Sort's order, their tracks in album
/// order; Title sorts the tracks themselves unless they are grouped by album. Grouped by album, each album has a header;
/// grouped by genre, each genre's header has Play (its tracks as listed) and Radio (its station).
/// </summary>
public sealed partial class TracksPageViewModel : TrackListPageViewModel
{
    private readonly ILibraryService _library;
    private readonly RadioViewModel _radio;
    private List<AlbumItemViewModel> _albums = [];
    private Dictionary<string, List<TrackSummary>> _albumTracks = new(StringComparer.Ordinal);
    private IReadOnlyList<GroupActionsViewModel> _groupActions = [];
    private AlbumItemViewModel? _playingAlbum;

    public TracksPageViewModel(ILibraryService library, ICoverService covers, PlayerViewModel player, RadioViewModel radio, MenuViewModel menu,
        PlaylistActions playlists, FavoritesViewModel favorites, Notices notices)
        : base(covers, player, menu, playlists, favorites, notices)
    {
        ArgumentNullException.ThrowIfNull(radio);
        _library = library;
        _radio = radio;
        _radio.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(RadioViewModel.OnAir) or nameof(RadioViewModel.IsOnAir))
            {
                foreach (var actions in _groupActions)
                {
                    actions.Refresh();
                }
            }
        };
    }

    public override string Title => "Tracks";

    [ObservableProperty]
    public partial LibrarySort Sort { get; set; } = LibrarySort.Artist;

    [ObservableProperty]
    public partial TrackGrouping Grouping { get; set; } = TrackGrouping.Album;

    /// <summary>The groups shown: genres, or album titles (empty when ungrouped).</summary>
    public IReadOnlyList<string> GroupNames { get; private set; } = [];

    protected override string EmptyLibraryText => NoMusic;

    protected override bool IsLibraryPage => true;

    [RelayCommand]
    private void OpenSortMenu() => Menu.Show(Enum.GetValues<LibrarySort>().Select(s => MenuEntryViewModel.Of(s.ToString(), () => Sort = s, s == Sort)));

    [RelayCommand]
    private void OpenGroupMenu()
        => Menu.Show(new[] { TrackGrouping.None, TrackGrouping.Album, TrackGrouping.Genre }
            .Select(g => MenuEntryViewModel.Of(g.ToString(), () => Grouping = g, g == Grouping)));

    partial void OnSortChanged(LibrarySort value) => Filter();

    partial void OnGroupingChanged(TrackGrouping value) => Filter();

    protected override async Task<IReadOnlyList<TrackItemViewModel>> ReadAsync()
    {
        // One after the other: each read holds the library's lock while it runs.
        var albums = await _library.AlbumsAsync().ConfigureAwait(true);
        var tracks = await _library.TracksAsync().ConfigureAwait(true);
        _albums = AlbumItems.Reuse(albums, _albums, a => new AlbumItemViewModel(a, new AlbumArt(a.Id, CoverSize.Small, Covers)));
        var byId = _albums.ToDictionary(a => a.Album.Id, StringComparer.Ordinal);
        _albumTracks = tracks.GroupBy(t => t.AlbumId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        return [.. tracks.Select(t => byId.GetValueOrDefault(t.AlbumId) is { } album
            ? new TrackItemViewModel(t, album, album.Art)
            : new TrackItemViewModel(t, null, new AlbumArt(t.AlbumId, CoverSize.Small, Covers)))];
    }

    protected override IEnumerable<TrackItemViewModel> Shown()
    {
        var order = LibraryOrder.Albums(Sort);
        var rank = _albums.Select(a => a.Album).Order(order).Select((a, i) => (a.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
        var shown = All.Where(t => Matches(t.Title, t.Artist, t.Album, t.AlbumItem?.Artist, t.Genre))
            .OrderBy(t => rank.GetValueOrDefault(t.Track.AlbumId, int.MaxValue));
        return Sort == LibrarySort.Title && Grouping != TrackGrouping.Album
            ? shown.OrderBy(t => t.Title, Comparer<string>.Create(LibraryOrder.Text))
            : shown;
    }

    protected override IReadOnlyList<object> LayOut(IReadOnlyList<TrackItemViewModel> shown)
    {
        var rows = new List<object>();
        var actions = new List<GroupActionsViewModel>();
        switch (Grouping)
        {
            case TrackGrouping.Album:
                var groups = LibraryOrder.GroupBy(shown, t => t.Track.AlbumId, sortKeys: false);
                foreach (var group in groups)
                {
                    if (group.Items[0].AlbumItem is { } album)
                    {
                        rows.Add(new TrackAlbumHeaderViewModel(album, _albumTracks[album.Album.Id], rows.Count == 0));
                    }

                    rows.AddRange(group.Items.Select(t => new NumberedTrackRow(t)));
                }

                GroupNames = [.. groups.Select(g => g.Items[0].Album)];
                break;
            case TrackGrouping.Genre:
                var genres = LibraryOrder.GroupBy(shown, t => t.Genre);
                foreach (var group in genres)
                {
                    // Play: the genre's tracks as listed (what the search lets through, in the Sort's order).
                    IReadOnlyList<TrackSummary> tracks = [.. group.Items.Select(t => t.Track)];
                    var genre = new GroupActionsViewModel(RadioTagKind.Genre, group.Key, () => Task.FromResult(tracks), Player, _radio, Notices);
                    actions.Add(genre);
                    rows.Add(new GroupHeaderViewModel(group.Key, group.Items.Count, " tracks", rows.Count == 0, genre));
                    rows.AddRange(group.Items.Select(t => new TrackRowWithAlbum(t)));
                }

                GroupNames = [.. genres.Select(g => g.Key)];
                break;
            default:
                rows.AddRange(shown.Select(t => new TrackRowWithAlbumAndGenre(t)));
                GroupNames = [];
                break;
        }

        _groupActions = actions;
        return rows;
    }

    protected override void MarkPlaying()
    {
        base.MarkPlaying();
        _playingAlbum?.IsPlaying = false;
        _playingAlbum = PlayingAlbumId is { } id ? _albums.FirstOrDefault(a => a.Album.Id == id) : null;
        _playingAlbum?.IsPlaying = true;
    }
}

/// <summary>
/// A saved playlist's entries, in the playlist's order (mockup: renderTracks, playlist branch): the title is its name, the
/// count its entries. An entry whose file is missing shows dimmed and is left out of ▶ Play and ＋ Queue. A row's ✕
/// removes it, and dragging a row to another place moves it there (#33); the list is read again after each edit.
/// </summary>
public sealed partial class PlaylistPageViewModel(PlaylistSummaryItem playlist, ILibraryService library, ICoverService covers, PlayerViewModel player,
    MenuViewModel menu, PlaylistActions playlists, FavoritesViewModel favorites, Notices notices)
    : TrackListPageViewModel(covers, player, menu, playlists, favorites, notices)
{
    /// <summary>The playlist (a rename elsewhere changes it, and the title with it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial PlaylistSummaryItem Playlist { get; set; } = playlist;

    public override string Title => Playlist.Name;

    protected override string EmptyLibraryText => "This playlist is empty. Drag albums or tracks onto it in the sidebar.";

    protected override async Task<IReadOnlyList<TrackItemViewModel>> ReadAsync()
    {
        var entries = await library.PlaylistEntriesAsync(Playlist.Id).ConfigureAwait(true);
        var art = new Dictionary<string, AlbumArt>(StringComparer.Ordinal);
        return [.. entries.Select(e => new TrackItemViewModel(e.Track, null,
            art.TryGetValue(e.Track.AlbumId, out var a) ? a : art[e.Track.AlbumId] = new AlbumArt(e.Track.AlbumId, CoverSize.Small, Covers))
        {
            Entry = e,
        })];
    }

    protected override IReadOnlyList<object> LayOut(IReadOnlyList<TrackItemViewModel> shown) => [.. shown.Select(t => new PlaylistTrackRow(t))];

    /// <summary>
    /// A row's ✕: the entry leaves the playlist (its track stays in the library). Other rows' ✕ stay enabled meanwhile:
    /// each removal finds its entry again by its file.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task RemoveEntry(TrackItemViewModel row) => Notices.GuardAsync(async () =>
    {
        if (row.Entry is { } entry)
        {
            await library.RemoveFromPlaylistAsync(Playlist.Id, entry).ConfigureAwait(true);
            await LoadAsync().ConfigureAwait(true);
        }
    });

    /// <summary>
    /// A row dropped before the row shown at <paramref name="at"/> (after the last when <paramref name="at"/> is past
    /// it). With a search, the rows shown are a part of the playlist: the entry lands just before (or after) the one it
    /// was dropped next to.
    /// </summary>
    public Task MoveAsync(TrackItemViewModel row, int at) => Notices.GuardAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(row);
        var shown = Tracks;
        var from = -1;
        for (var i = 0; i < shown.Count && from < 0; i++)
        {
            from = ReferenceEquals(shown[i], row) ? i : -1;
        }

        if (row.Entry is not { } entry || from < 0 || at == from || at == from + 1 || shown.Count == 0)
        {
            return;
        }

        var before = at < shown.Count ? shown[Math.Max(0, at)].Entry!.Position : shown[^1].Entry!.Position + 1;
        await library.MoveInPlaylistAsync(Playlist.Id, entry, entry.Position < before ? before - 1 : before).ConfigureAwait(true);
        await LoadAsync().ConfigureAwait(true);
    });
}

/// <summary>
/// Favorites (#48): every favorite library track, newest favorite first, laid out like a playlist (mockup: renderTracks,
/// playlist branch). It can't be renamed, deleted or reordered, so it has no such controls; un-hearting a row takes it
/// off. Favorites whose files left the library aren't shown; a line says how many. Its Radio button starts a station of
/// favorites, and while that station is on air it reads On air and opens the Radio page.
/// </summary>
public sealed partial class FavoritesPageViewModel : TrackListPageViewModel
{
    /// <summary>The station the Radio button starts: favorites only.</summary>
    public static readonly RadioFilter Station = new() { Favorites = true };

    /// <summary>Where the docs list the CLI command that names the favorites missing from the library.</summary>
    public static readonly Uri MissingDocs = new("https://github.com/vgabi94/MusicSpin/blob/main/docs/cli-reference.md#favorite--favorites");

    private readonly ILibraryService _library;
    private readonly Action _openRadio;
    private int _reads;

    /// <param name="openRadio">Opens the Radio page (the button, while the favorites station is on air).</param>
    public FavoritesPageViewModel(ILibraryService library, ICoverService covers, PlayerViewModel player, MenuViewModel menu, PlaylistActions playlists,
        FavoritesViewModel favorites, Notices notices, Action openRadio)
        : base(covers, player, menu, playlists, favorites, notices)
    {
        _library = library;
        _openRadio = openRadio;
        Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PlayerViewModel.IsRadio) or nameof(PlayerViewModel.Station))
            {
                OnPropertyChanged(nameof(IsOnAir));
                OnPropertyChanged(nameof(RadioLabel));
                RadioCommand.NotifyCanExecuteChanged();
            }
        };
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EmptyText))
            {
                OnPropertyChanged(nameof(ShowsHowToAdd));
            }
        };
    }

    public override string Title => "Favorites";

    /// <summary>How many favorites aren't in the library now (their files left it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MissingNote))]
    public partial int Missing { get; private set; }

    /// <summary>The line under the title about them, or null when there are none.</summary>
    public string? MissingNote => Missing switch
    {
        0 => null,
        1 => "1 favorite isn't in the library now.",
        var n => $"{n:N0} favorites aren't in the library now.",
    };

    /// <summary>The favorites station is on air (and nothing narrows it): the Radio button reads On air.</summary>
    public bool IsOnAir => Player.IsRadio && Player.Station is { Favorites: true, HasExcludes: false } station && station with { Favorites = false } is { IsEmpty: true };

    /// <summary>"Radio", or "On air" while the favorites station plays.</summary>
    public string RadioLabel => IsOnAir ? "On air" : "Radio";

    /// <summary>No favorites at all (not a search that matches none): the empty view says how to add one.</summary>
    public bool ShowsHowToAdd => EmptyText == EmptyLibraryText;

    /// <summary>The empty view goes on with how to add one (<see cref="ShowsHowToAdd"/>; the view draws the heart in it).</summary>
    protected override string EmptyLibraryText => "No favorites yet.";

    /// <summary>The Radio button: a station of favorites, or, while it is on air, the Radio page. Off with no favorites.</summary>
    [RelayCommand(CanExecute = nameof(CanStartRadio))]
    private Task Radio()
    {
        if (IsOnAir)
        {
            _openRadio();
            return Task.CompletedTask;
        }

        return Notices.GuardAsync(() => Player.StartRadioAsync(Station));
    }

    /// <summary>The favorites, and how many are missing (from the last read started, as the list).</summary>
    protected override async Task<IReadOnlyList<TrackItemViewModel>> ReadAsync()
    {
        var read = ++_reads;
        var favorites = await _library.FavoritesAsync().ConfigureAwait(true);
        if (read == _reads)
        {
            Missing = favorites.Missing;
        }

        var art = new Dictionary<string, AlbumArt>(StringComparer.Ordinal);
        return [.. favorites.Tracks.Select(t => new TrackItemViewModel(t, null,
            art.TryGetValue(t.AlbumId, out var a) ? a : art[t.AlbumId] = new AlbumArt(t.AlbumId, CoverSize.Small, Covers)))];
    }

    protected override IReadOnlyList<object> LayOut(IReadOnlyList<TrackItemViewModel> shown) => [.. shown.Select(t => new TrackRowWithAlbum(t))];

    private bool CanStartRadio() => IsOnAir || Favorites.Count > 0;

    /// <summary>
    /// A heart changed here or elsewhere: shown, the list is read again (an un-hearted row goes); otherwise it is read when
    /// the page is next opened.
    /// </summary>
    protected override void OnFavoritesChanged()
    {
        RadioCommand.NotifyCanExecuteChanged();
        if (IsShown)
        {
            _ = Notices.GuardAsync(LoadAsync);
        }
    }
}
