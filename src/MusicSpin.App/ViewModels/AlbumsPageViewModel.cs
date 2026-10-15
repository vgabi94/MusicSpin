using System.ComponentModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Services;
using MusicSpin.Core;
using MusicSpin.Core.Library;

namespace MusicSpin.App.ViewModels;

/// <summary>An album's cover at one size, read on first use (only shown items ask), then kept by <see cref="ICoverService"/>.</summary>
public sealed class AlbumArt(string albumId, CoverSize size, ICoverService covers) : ObservableObject
{
    private bool _requested;

    public Bitmap? Cover
    {
        get
        {
            if (!_requested)
            {
                _requested = true;
                _ = LoadAsync();
            }

            return field;
        }

        private set => SetProperty(ref field, value);
    }

    private async Task LoadAsync()
    {
        try
        {
            Cover = await covers.CoverAsync(albumId, size).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is MusicSpinException or IOException or UnauthorizedAccessException)
        {
            // no cover: the placeholder stays
        }
    }
}

/// <summary>One album: its text, its cover and whether it is the album playing.</summary>
public sealed partial class AlbumItemViewModel(AlbumSummary album, AlbumArt art) : ObservableObject
{
    public AlbumSummary Album { get; } = album;

    public AlbumArt Art { get; } = art;

    public string Title => Album.Title;

    public string Artist => Album.Artist;

    public string Genre => LibraryOrder.GenreOf(Album.Genre);

    /// <summary>"Halvard Eik · 2024" (mockup: .card .a).</summary>
    public string ArtistAndYear => Join(Album.Artist, Year);

    /// <summary>"Ana Cruz Quintet · 2017 · FLAC 24/96" (mockup: .talbum .sub).</summary>
    public string Details => Join(Album.Artist, Year, Join(Album.Format, Album.Resolution, separator: " "));

    public bool HiRes => Album.HiRes;

    /// <summary>The album loaded in the player, playing or paused (mockup: .card.playing): its record is out and spins.</summary>
    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    /// <summary>The loaded album is playing, so its Play button shows pause.</summary>
    [ObservableProperty]
    public partial bool ShowsPause { get; set; }

    /// <summary>The angle of this album's record (the shared spin clock's while it is loaded).</summary>
    [ObservableProperty]
    public partial double DiscAngle { get; set; }

    private string? Year => Album.Year?.ToString(CultureInfo.InvariantCulture);

    private static string Join(string? a, string? b, string? c = null, string separator = " · ")
        => string.Join(separator, new[] { a, b, c }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>A group's header (mockup: .group-h): "AMBIENT · 2", or "JAZZ · 12 tracks".</summary>
public sealed class GroupHeaderViewModel(string key, int count, string unit, bool isFirst)
{
    public string Key { get; } = key;

    public string Text { get; } = string.Create(CultureInfo.CurrentCulture, $"{key} · {count:N0}{unit}");

    /// <summary>The first group has no space above it (mockup: .group + .group).</summary>
    public bool IsFirst { get; } = isFirst;
}

/// <summary>One row of cards: the grid is a list of rows, so it can be virtualized.</summary>
public sealed class AlbumCardRowViewModel(IReadOnlyList<AlbumItemViewModel> albums, int columns, bool isFirst)
{
    public IReadOnlyList<AlbumItemViewModel> Albums { get; } = albums;

    public int Columns { get; } = columns;

    /// <summary>The group's first row sits right under its header (mockup: .grid gap).</summary>
    public bool IsFirst { get; } = isFirst;
}

/// <summary>
/// Albums: the library's albums, sorted, grouped and filtered by the search box. A card plays its album; the playing
/// album's card opens Now Playing; its Play button plays or pauses that album.
/// </summary>
public sealed partial class AlbumsPageViewModel : PageViewModel
{
    private readonly ILibraryService _library;
    private readonly ICoverService _covers;
    private readonly PlayerViewModel _player;
    private readonly SpinClock _clock;
    private readonly LayerViewModel _nowPlaying;
    private readonly MenuViewModel _menu;
    private readonly Notices _notices;
    private List<AlbumItemViewModel> _all = [];
    private AlbumItemViewModel? _playing;

    public AlbumsPageViewModel(ILibraryService library, ICoverService covers, PlayerViewModel player, SpinClock clock,
        LayerViewModel nowPlaying, MenuViewModel menu, Notices notices)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(clock);
        _library = library;
        _covers = covers;
        _player = player;
        _clock = clock;
        _nowPlaying = nowPlaying;
        _menu = menu;
        _notices = notices;
        ViewChoice = Views[0];
        _clock.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SpinClock.Angle) && _playing is { } playing)
            {
                playing.DiscAngle = _clock.Angle;
            }
        };
        _player.PropertyChanged += OnPlayerChanged;
    }

    public override string Title => "Albums";

    /// <summary>Cards, Shelf, Crate (mockup: #viewSeg). Shelf and Crate come with their own issues (#10, #11).</summary>
    public IReadOnlyList<Choice<AlbumsView>> Views { get; } = [new("Cards", AlbumsView.Cards), new("Shelf", AlbumsView.Shelf), new("Crate", AlbumsView.Crate)];

    [ObservableProperty]
    public partial Choice<AlbumsView> ViewChoice { get; set; }

    [ObservableProperty]
    public partial LibrarySort Sort { get; set; } = LibrarySort.Artist;

    [ObservableProperty]
    public partial AlbumGrouping Grouping { get; set; } = AlbumGrouping.Genre;

    /// <summary>How many cards fit across (the view reports it from its width); the rows follow it.</summary>
    [ObservableProperty]
    public partial int Columns { get; set; } = 1;

    /// <summary>The albums shown, in order.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<AlbumItemViewModel> Albums { get; private set; } = [];

    /// <summary>What the grid shows: <see cref="GroupHeaderViewModel"/>s and <see cref="AlbumCardRowViewModel"/>s.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<object> Rows { get; private set; } = [];

    /// <summary>The groups shown (empty when ungrouped).</summary>
    public IReadOnlyList<string> GroupNames { get; private set; } = [];

    public override async Task LoadAsync()
    {
        var albums = await _library.AlbumsAsync().ConfigureAwait(true);
        _all = [.. albums.Select(a => _all.FirstOrDefault(x => x.Album == a) ?? new AlbumItemViewModel(a, new AlbumArt(a.Id, CoverSize.Medium, _covers)))];
        MarkPlaying();
        Filter();
    }

    [RelayCommand]
    private void OpenSortMenu() => _menu.Show(Enum.GetValues<LibrarySort>().Select(s => MenuEntryViewModel.Of(s.ToString(), () => Sort = s, s == Sort)));

    [RelayCommand]
    private void OpenGroupMenu()
        => _menu.Show(Enum.GetValues<AlbumGrouping>().Select(g => MenuEntryViewModel.Of(g.ToString(), () => Grouping = g, g == Grouping)));

    /// <summary>A click on a card: plays the album from its first track; the album playing opens Now Playing instead.</summary>
    [RelayCommand]
    private Task OpenAlbum(AlbumItemViewModel album)
    {
        ArgumentNullException.ThrowIfNull(album);
        if (album.IsPlaying && _player.IsPlaying)
        {
            _nowPlaying.Open();
            return Task.CompletedTask;
        }

        return PlayAsync(album);
    }

    /// <summary>The card's Play button: plays or pauses the loaded album; another album starts from its first track.</summary>
    [RelayCommand]
    private Task TogglePlayAlbum(AlbumItemViewModel album)
    {
        ArgumentNullException.ThrowIfNull(album);
        return album.IsPlaying ? _player.TogglePlayCommand.ExecuteAsync(null) : PlayAsync(album);
    }

    private Task PlayAsync(AlbumItemViewModel album) => _notices.GuardAsync(async () =>
    {
        var tracks = await _library.AlbumTracksAsync(album.Album.Id).ConfigureAwait(true);
        await _player.PlayTracksAsync(tracks).ConfigureAwait(true);
    });

    partial void OnSortChanged(LibrarySort value) => Filter();

    partial void OnGroupingChanged(AlbumGrouping value) => Filter();

    partial void OnColumnsChanged(int value) => LayOut();

    protected override void Filter()
    {
        Albums = [.. _all.Where(a => Matches(a.Title, a.Artist, a.Album.Genre, a.Album.Year?.ToString(CultureInfo.InvariantCulture)))
            .OrderBy(a => a.Album, LibraryOrder.Albums(Sort))];
        CountText = Count(Albums.Count);
        EmptyText = _all.Count == 0 ? "No albums yet. Add a music folder with 'music-spin library add <folder>' and scan it."
            : Albums.Count == 0 ? $"Nothing matches “{SearchText.Trim()}”." : null;
        LayOut();
    }

    private void LayOut()
    {
        var groups = Grouping switch
        {
            AlbumGrouping.Artist => LibraryOrder.GroupBy(Albums, a => a.Artist),
            AlbumGrouping.Genre => LibraryOrder.GroupBy(Albums, a => a.Genre),
            _ => [new Group<AlbumItemViewModel>(string.Empty, Albums)],
        };
        GroupNames = Grouping == AlbumGrouping.None ? [] : [.. groups.Select(g => g.Key)];
        var columns = Math.Max(1, Columns);
        var rows = new List<object>();
        foreach (var group in groups)
        {
            if (Grouping != AlbumGrouping.None)
            {
                rows.Add(new GroupHeaderViewModel(group.Key, group.Items.Count, string.Empty, rows.Count == 0));
            }

            rows.AddRange(group.Items.Chunk(columns).Select((cards, i) => new AlbumCardRowViewModel(cards, columns, i == 0)));
        }

        Rows = rows;
    }

    protected override void MarkPlaying()
    {
        if (_playing is { } old)
        {
            old.IsPlaying = old.ShowsPause = false;
            old.DiscAngle = 0;
        }

        _playing = PlayingAlbumId is { } id ? _all.FirstOrDefault(a => a.Album.Id == id) : null;
        if (_playing is { } now)
        {
            now.IsPlaying = true;
            now.ShowsPause = _player.IsPlaying;
            now.DiscAngle = _clock.Angle;
        }
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.IsPlaying) && _playing is { } playing)
        {
            playing.ShowsPause = _player.IsPlaying;
        }
    }
}
