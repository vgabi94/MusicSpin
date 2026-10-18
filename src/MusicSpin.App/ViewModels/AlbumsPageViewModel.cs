using System.ComponentModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Services;
using MusicSpin.Core;
using MusicSpin.Core.Library;
using MusicSpin.Core.Playback;

namespace MusicSpin.App.ViewModels;

/// <summary>
/// An album's cover at one size, and its colour, each read on first use (only shown items ask), then kept by
/// <see cref="ICoverService"/>. When the service finds the album's cover changed (a rescan), both are read again; the
/// old ones show until the new ones arrive.
/// </summary>
public sealed class AlbumArt(string albumId, CoverSize size, ICoverService covers) : ObservableObject, ICoverObserver
{
    private bool _requested;
    private bool _accentRequested;
    private bool _observing;
    private int _coverReads; // the latest read wins: an older one finishing later doesn't overwrite it
    private int _accentReads;

    public Bitmap? Cover
    {
        get
        {
            if (!_requested)
            {
                _requested = true;
                Observe();
                _ = LoadAsync();
            }

            return field;
        }

        private set => SetProperty(ref field, value);
    }

    /// <summary>The album's colour (a shelf sleeve's spine); null without a cover.</summary>
    public Color? Accent
    {
        get
        {
            if (!_accentRequested)
            {
                _accentRequested = true;
                Observe();
                _ = LoadAccentAsync();
            }

            return field;
        }

        private set => SetProperty(ref field, value);
    }

    void ICoverObserver.CoverChanged()
    {
        if (_requested)
        {
            _ = LoadAsync();
        }

        if (_accentRequested)
        {
            _ = LoadAccentAsync();
        }
    }

    private void Observe()
    {
        if (!_observing && albumId.Length > 0)
        {
            _observing = true;
            covers.Observe(albumId, this);
        }
    }

    private async Task LoadAsync()
    {
        if (albumId.Length == 0)
        {
            return; // a plain file: no album, the placeholder stays
        }

        var read = ++_coverReads;
        try
        {
            var cover = await covers.CoverAsync(albumId, size).ConfigureAwait(true);
            if (read == _coverReads)
            {
                Cover = cover;
            }
        }
        catch (Exception ex) when (ex is MusicSpinException or IOException or UnauthorizedAccessException)
        {
            // no cover: the placeholder stays
        }
    }

    private async Task LoadAccentAsync()
    {
        var read = ++_accentReads;
        try
        {
            var accent = await covers.AccentAsync(albumId).ConfigureAwait(true);
            if (read == _accentReads)
            {
                Accent = accent is { } c ? Color.FromRgb(c.R, c.G, c.B) : null;
            }
        }
        catch (Exception ex) when (ex is MusicSpinException or IOException or UnauthorizedAccessException)
        {
            // no colour: the plain spine stays
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

    /// <summary>"Jazz · 2023" above the title in the crate's side panel (mockup: .np-kicker).</summary>
    public string Kicker => Join(Album.Genre?.Trim(), Year);

    /// <summary>The format chips: "FLAC", "24/192", "8 tracks" (mockup: .np-tech).</summary>
    public IReadOnlyList<string> Chips =>
        [.. new[] { Album.Format, Album.Resolution }.Where(s => !string.IsNullOrWhiteSpace(s)),
            string.Create(CultureInfo.CurrentCulture, $"{Album.TrackCount:N0} {(Album.TrackCount == 1 ? "track" : "tracks")}")];

    public bool HiRes => Album.HiRes;

    /// <summary>"Copper Sun — Delta Ruth": a shelf sleeve's name for screen readers (its title isn't readable sideways).</summary>
    public string Name => $"{Album.Title} — {Album.Artist}";

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

/// <summary>
/// A group's header (mockup: .group-h): "AMBIENT · 2", or "JAZZ · 12 tracks", and for an artist or a genre its Play and
/// Radio (<see cref="Actions"/>).
/// </summary>
public sealed class GroupHeaderViewModel(string key, int count, string unit, bool isFirst, GroupActionsViewModel? actions = null)
{
    public string Key { get; } = key;

    public string Text { get; } = string.Create(CultureInfo.CurrentCulture, $"{key} · {count:N0}{unit}");

    /// <summary>The first group has no space above it (mockup: .group + .group).</summary>
    public bool IsFirst { get; } = isFirst;

    /// <summary>Play and Radio after the name (an artist's or a genre's group); null for others.</summary>
    public GroupActionsViewModel? Actions { get; } = actions;
}

/// <summary>One row of cards: the grid is a list of rows, so it can be virtualized.</summary>
public sealed class AlbumCardRowViewModel(IReadOnlyList<AlbumItemViewModel> albums, int columns, bool isFirst)
{
    public IReadOnlyList<AlbumItemViewModel> Albums { get; } = albums;

    public int Columns { get; } = columns;

    /// <summary>The group's first row sits right under its header (mockup: .grid gap).</summary>
    public bool IsFirst { get; } = isFirst;
}

/// <summary>One board of the shelf and the sleeves standing on it (mockup: .shelf-row + .board).</summary>
public sealed class ShelfBoardViewModel(IReadOnlyList<AlbumItemViewModel> albums, bool isFirst, bool isLast)
{
    public IReadOnlyList<AlbumItemViewModel> Albums { get; } = albums;

    /// <summary>The group's first board sits right under its header; later ones stand a little below the board above.</summary>
    public bool IsFirst { get; } = isFirst;

    /// <summary>The group's last board: the next group starts further down (mockup: .shelf margin-bottom).</summary>
    public bool IsLast { get; } = isLast;
}

/// <summary>
/// Albums: the library's albums, sorted, grouped and filtered by the search box, as Cards, a Shelf or Crates. A card or a
/// sleeve plays its album; the playing album's opens Now Playing; a card's Play button plays or pauses that album. In a
/// crate, the wheel, the arrow keys and clicks flip the records; the front record plays, and the side panel shows it.
/// </summary>
public sealed partial class AlbumsPageViewModel : PageViewModel
{
    /// <summary>
    /// The wheel flips a crate once its turns add up to half a notch (mockup: 50 px of a 100 px notch), at most once per
    /// <see cref="FlipInterval"/>; what adds up is capped at <see cref="WheelBank"/> notches, so a fast burst flips one
    /// record at a time instead of racing on (mockup: crateWheel).
    /// </summary>
    public const double WheelNotch = 0.5;

    public const double WheelBank = 1.2;

    public static readonly TimeSpan FlipInterval = TimeSpan.FromMilliseconds(180);

    private readonly ILibraryService _library;
    private readonly ICoverService _covers;
    private readonly PlayerViewModel _player;
    private readonly RadioViewModel _radio;
    private readonly SpinClock _clock;
    private readonly LayerViewModel _nowPlaying;
    private readonly TurntableSceneViewModel _scene;
    private readonly MenuViewModel _menu;
    private readonly PlaylistActions _playlists;
    private readonly Notices _notices;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, string> _crateFronts = new(StringComparer.Ordinal);
    private List<AlbumItemViewModel> _all = [];
    private IReadOnlyList<GroupActionsViewModel> _groupActions = [];
    private bool _loaded;
    private AlbumItemViewModel? _playing;
    private string? _activeCrateKey;
    private CrateViewModel? _wheelCrate;
    private double _wheelSum;
    private long? _lastFlip;

    public AlbumsPageViewModel(ILibraryService library, ICoverService covers, PlayerViewModel player, RadioViewModel radio, SpinClock clock,
        LayerViewModel nowPlaying, TurntableSceneViewModel scene, MenuViewModel menu, PlaylistActions playlists, Notices notices,
        SettingsViewModel settings, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(radio);
        ArgumentNullException.ThrowIfNull(clock);
        _library = library;
        _covers = covers;
        _player = player;
        _radio = radio;
        _clock = clock;
        _nowPlaying = nowPlaying;
        _scene = scene;
        _menu = menu;
        _playlists = playlists;
        _notices = notices;
        _time = time;
        Settings = settings;
        ViewChoice = Views[0];
        _clock.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SpinClock.Angle) && _playing is { } playing)
            {
                playing.DiscAngle = _clock.Angle;
            }
        };
        _player.PropertyChanged += OnPlayerChanged;
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

    public override string Title => "Albums";

    /// <summary>Settings › Shelf: the sleeves' tilt and depth.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>Cards, Shelf, Crate (mockup: #viewSeg).</summary>
    public IReadOnlyList<Choice<AlbumsView>> Views { get; } = [new("Cards", AlbumsView.Cards), new("Shelf", AlbumsView.Shelf), new("Crate", AlbumsView.Crate)];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShelf), nameof(IsCrate))]
    public partial Choice<AlbumsView> ViewChoice { get; set; }

    public bool IsShelf => ViewChoice.Value == AlbumsView.Shelf;

    public bool IsCrate => ViewChoice.Value == AlbumsView.Crate;

    [ObservableProperty]
    public partial LibrarySort Sort { get; set; } = LibrarySort.Artist;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGrouped))]
    public partial AlbumGrouping Grouping { get; set; } = AlbumGrouping.Genre;

    public bool IsGrouped => Grouping != AlbumGrouping.None;

    /// <summary>How many cards fit across (the view reports it from its width); the rows follow it.</summary>
    [ObservableProperty]
    public partial int Columns { get; set; } = 1;

    /// <summary>How many sleeves a shelf board holds (the view reports it from its width and the tilt); the boards follow it.</summary>
    [ObservableProperty]
    public partial int PerBoard { get; set; } = 1;

    /// <summary>The albums shown, in order.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<AlbumItemViewModel> Albums { get; private set; } = [];

    /// <summary>
    /// What Cards and Shelf show: <see cref="GroupHeaderViewModel"/>s, and <see cref="AlbumCardRowViewModel"/>s or
    /// <see cref="ShelfBoardViewModel"/>s.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<object> Rows { get; private set; } = [];

    /// <summary>What Crate shows: one crate, or one per group.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SoleCrate), nameof(GroupedCrates))]
    public partial IReadOnlyList<CrateViewModel> Crates { get; private set; } = [];

    /// <summary>The one crate, when ungrouped: it fills the view.</summary>
    public CrateViewModel? SoleCrate => IsGrouped || Crates.Count == 0 ? null : Crates[0];

    /// <summary>The crates, when grouped: smaller ones in a grid.</summary>
    public IReadOnlyList<CrateViewModel> GroupedCrates => IsGrouped ? Crates : [];

    /// <summary>How many grouped crates fit across (the view reports it from its width).</summary>
    [ObservableProperty]
    public partial int CrateColumns { get; set; } = 1;

    /// <summary>The crate the arrow keys flip and the side panel shows.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayFrontCommand), nameof(QueueFrontCommand))]
    public partial CrateViewModel? ActiveCrate { get; private set; }

    /// <summary>The groups shown (empty when ungrouped).</summary>
    public IReadOnlyList<string> GroupNames { get; private set; } = [];

    /// <summary>
    /// Reads the albums again. A read that brings back the albums shown (the library changed elsewhere: a playlist, a
    /// favorite) leaves the page as it is: laying it out again would replace every card under the pointer, and a click
    /// pressed on the old card and released on the new one would be lost (a right-click would open no menu).
    /// </summary>
    public override async Task LoadAsync()
    {
        var albums = await _library.AlbumsAsync().ConfigureAwait(true);
        List<AlbumItemViewModel> all = [.. albums.Select(a => _all.FirstOrDefault(x => x.Album == a) ?? new AlbumItemViewModel(a, new AlbumArt(a.Id, CoverSize.Medium, _covers)))];
        var same = _loaded && all.SequenceEqual(_all);
        _all = all;
        _loaded = true;
        MarkPlaying();
        if (!same)
        {
            Filter();
        }
    }

    [RelayCommand]
    private void OpenSortMenu() => _menu.Show(Enum.GetValues<LibrarySort>().Select(s => MenuEntryViewModel.Of(s.ToString(), () => Sort = s, s == Sort)));

    [RelayCommand]
    private void OpenGroupMenu()
        => _menu.Show(Enum.GetValues<AlbumGrouping>().Select(g => MenuEntryViewModel.Of(g.ToString(), () => Grouping = g, g == Grouping)));

    /// <summary>A click on a card or a sleeve: plays the album from its first track; the album playing opens Now Playing instead.</summary>
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

    /// <summary>A click on a crate record: the front one plays (as a card does); one behind comes to the front.</summary>
    [RelayCommand]
    private Task OpenRecord(CrateRecordViewModel record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.IsFront)
        {
            Activate(record.Crate);
            return OpenAlbum(record.Album);
        }

        Show(record.Crate, record.Index);
        return Task.CompletedTask;
    }

    private bool HasFront() => ActiveCrate?.Front is not null;

    /// <summary>The side panel's Play/Pause: the active crate's front record.</summary>
    [RelayCommand(CanExecute = nameof(HasFront))]
    private Task PlayFront() => ActiveCrate?.Front is { } front ? TogglePlayAlbum(front) : Task.CompletedTask;

    /// <summary>The side panel's ＋ Queue: the queue menu for the front record's album (mockup: data-act="menu").</summary>
    [RelayCommand(CanExecute = nameof(HasFront))]
    private Task QueueFront() => ActiveCrate?.Front is { } front ? _notices.GuardAsync(async () =>
    {
        var tracks = await _library.AlbumTracksAsync(front.Album.Id).ConfigureAwait(true);
        _menu.Show(QueueMenu.Entries(tracks, _player, _playlists, _notices));
    }) : Task.CompletedTask;

    /// <summary>
    /// The wheel over <paramref name="crate"/>, in notches (positive: on, toward the back). Returns false when the crate
    /// can't flip that way (past either end), so the page scrolls instead.
    /// </summary>
    public bool WheelCrate(CrateViewModel crate, double notches)
    {
        ArgumentNullException.ThrowIfNull(crate);
        if (!crate.CanFlip(Math.Sign(notches)))
        {
            return false;
        }

        if (_wheelCrate != crate)
        {
            _wheelCrate = crate;
            _wheelSum = 0;
        }

        _wheelSum = Math.Clamp(_wheelSum + notches, -WheelBank, WheelBank);
        var now = _time.GetTimestamp();
        if (Math.Abs(_wheelSum) >= WheelNotch && (_lastFlip is not { } last || _time.GetElapsedTime(last, now) > FlipInterval))
        {
            Show(crate, crate.FrontIndex + Math.Sign(_wheelSum));
            _wheelSum = 0;
            _lastFlip = now;
        }

        return true;
    }

    /// <summary>← → ↑ ↓: flips the active crate one record; false when it can't (no crate shown, or at its end).</summary>
    public bool FlipActiveCrate(int step)
    {
        if (!IsCrate || ActiveCrate is not { } crate || !crate.CanFlip(step))
        {
            return false;
        }

        Show(crate, crate.FrontIndex + step);
        return true;
    }

    /// <summary>The album from its first track, its record carried onto the turntable (the scene).</summary>
    private Task PlayAsync(AlbumItemViewModel album) => _scene.PlayAlbumAsync(album.Album.Id);

    partial void OnViewChoiceChanged(Choice<AlbumsView> value) => LayOut();

    partial void OnSortChanged(LibrarySort value) => Filter();

    partial void OnGroupingChanged(AlbumGrouping value) => Filter();

    partial void OnColumnsChanged(int value)
    {
        if (ViewChoice.Value == AlbumsView.Cards)
        {
            LayOut();
        }
    }

    partial void OnPerBoardChanged(int value)
    {
        if (IsShelf)
        {
            LayOut();
        }
    }

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
        var actions = groups.ToDictionary(g => g, ActionsFor);
        _groupActions = [.. actions.Values.OfType<GroupActionsViewModel>()];
        if (IsCrate)
        {
            Rows = [];
            LayOutCrates(groups, actions);
            return;
        }

        Crates = [];
        ActiveCrate = null;
        var rows = new List<object>();
        foreach (var group in groups)
        {
            if (Grouping != AlbumGrouping.None)
            {
                rows.Add(new GroupHeaderViewModel(group.Key, group.Items.Count, string.Empty, rows.Count == 0, actions[group]));
            }

            if (IsShelf)
            {
                var boards = group.Items.Chunk(Math.Max(1, PerBoard)).ToList();
                rows.AddRange(boards.Select((sleeves, i) => new ShelfBoardViewModel(sleeves, i == 0, i == boards.Count - 1)));
            }
            else
            {
                var columns = Math.Max(1, Columns);
                rows.AddRange(group.Items.Chunk(columns).Select((cards, i) => new AlbumCardRowViewModel(cards, columns, i == 0)));
            }
        }

        Rows = rows;
    }

    /// <summary>
    /// An artist's or a genre's Play and Radio: Play queues the group's albums as shown, each whole, in one read.
    /// </summary>
    private GroupActionsViewModel? ActionsFor(Group<AlbumItemViewModel> group)
    {
        if (Grouping == AlbumGrouping.None)
        {
            return null;
        }

        IReadOnlyList<string> albumIds = [.. group.Items.Select(a => a.Album.Id)];
        return new GroupActionsViewModel(Grouping == AlbumGrouping.Artist ? RadioTagKind.Artist : RadioTagKind.Genre, group.Key,
            () => _library.AlbumsTracksAsync(albumIds), _player, _radio, _notices);
    }

    /// <summary>
    /// One crate per group (one in all when ungrouped), each with the record it showed last at the front, or else the
    /// album playing (mockup: renderCrate). The active crate stays active while it is shown.
    /// </summary>
    private void LayOutCrates(IReadOnlyList<Group<AlbumItemViewModel>> groups, Dictionary<Group<AlbumItemViewModel>, GroupActionsViewModel?> actions)
    {
        var crates = groups.Where(g => g.Items.Count > 0).Select(g => new CrateViewModel(g.Key,
            IsGrouped ? g.Key : null, IsGrouped ? CrateViewModel.GroupLabel(g.Items.Count) : CrateLabel(g.Items), g.Items,
            FrontOf(g.Key, g.Items))
        {
            Actions = actions[g],
        }).ToList();
        Crates = crates;
        foreach (var crate in crates)
        {
            crate.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CrateViewModel.FrontIndex) && crate.Front is { } front)
                {
                    _crateFronts[crate.Key] = front.Album.Id;
                }
            };
        }

        Activate(crates.FirstOrDefault(c => c.Key == _activeCrateKey)
                 ?? crates.FirstOrDefault(c => c.Albums.Contains(_playing))
                 ?? crates.FirstOrDefault());
    }

    private int FrontOf(string key, IReadOnlyList<AlbumItemViewModel> albums)
    {
        var shown = _crateFronts.TryGetValue(key, out var id) ? IndexOf(albums, a => a.Album.Id == id) : -1;
        return shown >= 0 ? shown : Math.Max(0, IndexOf(albums, a => a == _playing));
    }

    private static int IndexOf(IReadOnlyList<AlbumItemViewModel> albums, Func<AlbumItemViewModel, bool> match)
    {
        for (var i = 0; i < albums.Count; i++)
        {
            if (match(albums[i]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>"A — Z", or the years when sorted by year (mockup: .crate-box .lbl).</summary>
    private string CrateLabel(IReadOnlyList<AlbumItemViewModel> albums)
    {
        var years = albums.Select(a => a.Album.Year).OfType<int>().ToList();
        return Sort == LibrarySort.Year && years.Count > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{years[0]} — {years[^1]}")
            : "A — Z";
    }

    private void Show(CrateViewModel crate, int index)
    {
        crate.Show(index);
        Activate(crate);
    }

    private void Activate(CrateViewModel? crate)
    {
        foreach (var other in Crates)
        {
            other.IsActive = other == crate;
        }

        ActiveCrate = crate;
        if (crate is not null)
        {
            _activeCrateKey = crate.Key;
        }
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
