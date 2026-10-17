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

    /// <summary>False for a playlist entry whose file is missing: the row is dimmed, and it can't be played, queued or dragged.</summary>
    public bool IsAvailable { get; init; } = true;

    /// <summary>The entry's place in its playlist (1-based); null on the Tracks page.</summary>
    public int? Position { get; init; }

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
}

/// <summary>A track row; its type says which columns it has (the group header names the rest).</summary>
public abstract record TrackRow(TrackItemViewModel Item);

/// <summary>Under an album header: number, title, duration (mockup: .tlist.m-album).</summary>
public sealed record NumberedTrackRow(TrackItemViewModel Item) : TrackRow(Item);

/// <summary>Under a genre header, and in playlists: thumbnail, title and artist, album, duration (mockup: m-genre, m-plain).</summary>
public sealed record TrackRowWithAlbum(TrackItemViewModel Item) : TrackRow(Item);

/// <summary>Ungrouped: thumbnail, title and artist, album, genre, duration (mockup: m-none).</summary>
public sealed record TrackRowWithAlbumAndGenre(TrackItemViewModel Item) : TrackRow(Item);

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
public abstract partial class TrackListPageViewModel : PageViewModel
{
    private List<TrackItemViewModel> _all = [];

    protected TrackListPageViewModel(ICoverService covers, PlayerViewModel player, MenuViewModel menu, Notices notices)
    {
        ArgumentNullException.ThrowIfNull(player);
        Covers = covers;
        Player = player;
        Menu = menu;
        Notices = notices;
        Player.PropertyChanged += OnPlayerChanged;
    }

    /// <summary>The tracks shown, in order.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<TrackItemViewModel> Tracks { get; private set; } = [];

    /// <summary>What the list shows: headers (<see cref="GroupHeaderViewModel"/>, <see cref="TrackAlbumHeaderViewModel"/>) and <see cref="TrackRow"/>s.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<object> Rows { get; private set; } = [];

    protected ICoverService Covers { get; }

    protected PlayerViewModel Player { get; }

    protected MenuViewModel Menu { get; }

    protected Notices Notices { get; }

    /// <summary>Every track read, in the library's (or the playlist's) order.</summary>
    protected IReadOnlyList<TrackItemViewModel> All => _all;

    public override async Task LoadAsync()
    {
        _all = [.. await ReadAsync().ConfigureAwait(true)];
        MarkPlaying();
        Filter();
    }

    /// <summary>The page's tracks, as items.</summary>
    protected abstract Task<IReadOnlyList<TrackItemViewModel>> ReadAsync();

    protected abstract string EmptyLibraryText { get; }

    /// <summary>The rows for the tracks shown (<see cref="Tracks"/>, already sorted).</summary>
    protected abstract IReadOnlyList<object> LayOut(IReadOnlyList<TrackItemViewModel> shown);

    /// <summary>The tracks the search lets through, in the order shown.</summary>
    protected virtual IEnumerable<TrackItemViewModel> Shown() => _all.Where(t => Matches(t.Title, t.Artist, t.Album));

    /// <summary>Plays the list as shown from <paramref name="track"/>; entries whose files are missing are left out.</summary>
    [RelayCommand]
    private Task PlayTrack(TrackItemViewModel track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (!track.IsAvailable)
        {
            return Task.CompletedTask;
        }

        var playable = Playable();
        return Notices.GuardAsync(() => Player.PlayTracksAsync([.. playable.Select(t => t.Track)], Math.Max(0, IndexOf(playable, track))));
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
    protected void ShowQueueMenu(IReadOnlyList<TrackSummary> tracks) => Menu.Show(QueueMenu.Entries(tracks, Player, Notices));

    protected override void Filter()
    {
        Tracks = [.. Shown()];
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
/// order; Title sorts the tracks themselves unless they are grouped by album. Grouped by album, each album has a header.
/// </summary>
public sealed partial class TracksPageViewModel(ILibraryService library, ICoverService covers, PlayerViewModel player, MenuViewModel menu, Notices notices)
    : TrackListPageViewModel(covers, player, menu, notices)
{
    private List<AlbumItemViewModel> _albums = [];
    private Dictionary<string, List<TrackSummary>> _albumTracks = new(StringComparer.Ordinal);
    private AlbumItemViewModel? _playingAlbum;

    public override string Title => "Tracks";

    [ObservableProperty]
    public partial LibrarySort Sort { get; set; } = LibrarySort.Artist;

    [ObservableProperty]
    public partial TrackGrouping Grouping { get; set; } = TrackGrouping.Album;

    /// <summary>The groups shown: genres, or album titles (empty when ungrouped).</summary>
    public IReadOnlyList<string> GroupNames { get; private set; } = [];

    protected override string EmptyLibraryText => "No tracks yet. Add a music folder with 'music-spin library add <folder>' and scan it.";

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
        var albums = await library.AlbumsAsync().ConfigureAwait(true);
        var tracks = await library.TracksAsync().ConfigureAwait(true);
        _albums = [.. albums.Select(a => _albums.FirstOrDefault(x => x.Album == a) ?? new AlbumItemViewModel(a, new AlbumArt(a.Id, CoverSize.Small, Covers)))];
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
                    rows.Add(new GroupHeaderViewModel(group.Key, group.Items.Count, " tracks", rows.Count == 0));
                    rows.AddRange(group.Items.Select(t => new TrackRowWithAlbum(t)));
                }

                GroupNames = [.. genres.Select(g => g.Key)];
                break;
            default:
                rows.AddRange(shown.Select(t => new TrackRowWithAlbumAndGenre(t)));
                GroupNames = [];
                break;
        }

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
/// count its entries. An entry whose file is missing shows dimmed and is left out of ▶ Play and ＋ Queue.
/// </summary>
public sealed partial class PlaylistPageViewModel(PlaylistSummaryItem playlist, ILibraryService library, ICoverService covers, PlayerViewModel player,
    MenuViewModel menu, Notices notices) : TrackListPageViewModel(covers, player, menu, notices)
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
            IsAvailable = e.IsAvailable,
            Position = e.Position,
        })];
    }

    protected override IReadOnlyList<object> LayOut(IReadOnlyList<TrackItemViewModel> shown) => [.. shown.Select(t => new TrackRowWithAlbum(t))];
}
