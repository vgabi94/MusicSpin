using System.ComponentModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Composition;
using MusicSpin.App.Services;
using MusicSpin.App.Theming;
using MusicSpin.Core;
using MusicSpin.Core.Library;

namespace MusicSpin.App.ViewModels;

/// <summary>
/// The window: sidebar navigation, the current page, the search box, the notice banner, the player, and the app
/// Settings dialog. Esc, Space and the Ctrl+arrow shortcuts are key bindings to its commands.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AppServices _services;
    private readonly Dictionary<string, PlaylistPageViewModel> _playlistPages = new(StringComparer.Ordinal);
    private readonly List<(int Asked, TaskCompletionSource Done)> _reloadWaiters = [];
    private CancellationTokenSource? _albumLookup;
    private OwedReads? _owedAlbumLookup;
    private int _reloadsAsked;
    private int _reloadsDone;
    private bool _reloading;
    private bool _reloadLists;
    private bool _reloadPage;
    private bool _listsOwed;
    private bool _pageOwed;
    private ITimer? _reloadRetry;

    public MainWindowViewModel(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        Player = new PlayerViewModel(services.Player, services.Time, services.Ui);
        SpinClock = new SpinClock(services.Time, services.Ui);
        Favorites = new FavoritesViewModel(services.Library, Notices);
        Favorites.Changed += (_, _) => OnFavoritesChanged();
        NowPlaying = new NowPlayingViewModel(Player, services.Library, services.Covers, Favorites, Notices, services.Time, services.Ui);
        NowPlaying.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NowPlayingViewModel.IsSpinning))
            {
                SpinClock.IsSpinning = NowPlaying.IsSpinning;
            }
        };
        QueueDrawer = new QueueDrawerViewModel(Player, services.Library, services.Covers, Notices, services.Time, services.Ui);
        QueueDrawer.PlaylistSaved += (_, playlist) => _ = Notices.GuardAsync(async () =>
        {
            await ReloadAsync().ConfigureAwait(true);
            Sidebar.MarkFresh(playlist.Id);
        });
        PlayerBar = new PlayerBarViewModel(Player, NowPlaying, QueueDrawer, services.Time, services.Ui);
        Layers.Register(NowPlaying);
        Layers.Register(QueueDrawer);
        Layers.Register(QueueDrawer.SaveForm);
        Layers.Register(Menu);
        Layers.Register(Sidebar.Rename);
        Playlists = new PlaylistActions(services.Library, Player, Sidebar, Menu, Notices, () => ReloadAsync());
        Settings = new SettingsViewModel(services.Settings, services.Appearance, SpinClock, Notices);
        Scene = new TurntableSceneViewModel(Player, NowPlaying, services.Library, services.Covers, Settings, services.Time, services.Ui, Notices);
        AlbumsPage = new AlbumsPageViewModel(services.Library, services.Covers, Player, SpinClock, NowPlaying, Scene, Menu, Playlists, Notices, Settings, services.Time);
        TracksPage = new TracksPageViewModel(services.Library, services.Covers, Player, Menu, Playlists, Favorites, Notices);
        FavoritesPage = new FavoritesPageViewModel(services.Library, services.Covers, Player, Menu, Playlists, Favorites, Notices, () => Sidebar.Navigate(Sidebar.Radio));
        RadioPage = new RadioPageViewModel(Player, Notices);
        CurrentPage = AlbumsPage;
        AlbumsPage.IsShown = true;
        Items = new LibraryItemActions(Player, services.Library, Scene, NowPlaying, Menu, Playlists, Favorites, Notices, () => CurrentPage);
        Sidebar.Navigated += (_, item) => _ = ShowAsync(item);
        Notices.Posted += (_, message) => Banner = message;
        Player.Notice += (_, message) => Notices.Post(message);
        Player.PropertyChanged += OnPlayerChanged;
        services.Library.Changed += (_, _) => services.Ui.Post(() => _ = ReloadAsync());
        Banner = services.StartupWarning;

        Layers.Register(Settings);
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.IsOpen))
            {
                NotifyShortcutsChanged();
            }
        };
    }

    public SidebarViewModel Sidebar { get; } = new();

    public PlayerViewModel Player { get; }

    public SpinClock SpinClock { get; }

    public PlayerBarViewModel PlayerBar { get; }

    public NowPlayingViewModel NowPlaying { get; }

    /// <summary>The record carried from the albums page onto the turntable.</summary>
    public TurntableSceneViewModel Scene { get; }

    /// <summary>The queue drawer: what plays next, and drops from the library.</summary>
    public QueueDrawerViewModel QueueDrawer { get; }

    /// <summary>Right-clicking and dragging albums and tracks (the window finds the item under the pointer).</summary>
    public LibraryItemActions Items { get; }

    /// <summary>Managing playlists: the sidebar's menus, rename and drops, and Add to playlist ›.</summary>
    public PlaylistActions Playlists { get; }

    /// <summary>Which tracks are favorites (#48): every heart, the menus' entry, the sidebar's count and the Favorites view.</summary>
    public FavoritesViewModel Favorites { get; }

    /// <summary>The Sort and Group dropdowns, the ＋ menus and the right-click menus.</summary>
    public MenuViewModel Menu { get; } = new();

    /// <summary>What Esc closes, topmost first. Menus, Settings, the queue drawer and Now Playing register here.</summary>
    public LayerRegistry Layers { get; } = new();

    public Notices Notices { get; } = new();

    public AlbumsPageViewModel AlbumsPage { get; }

    public TracksPageViewModel TracksPage { get; }

    public FavoritesPageViewModel FavoritesPage { get; }

    public RadioPageViewModel RadioPage { get; }

    [ObservableProperty]
    public partial PageViewModel CurrentPage { get; private set; }

    /// <summary>The title bar's search box; it filters the current page.</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>A startup warning or a notice, shown in a banner until dismissed.</summary>
    [ObservableProperty]
    public partial string? Banner { get; private set; }

    /// <summary>The library album of the playing track (null: nothing, or a plain file).</summary>
    [ObservableProperty]
    public partial string? PlayingAlbumId { get; private set; }

    /// <summary>The playing album's cover for the player bar.</summary>
    [ObservableProperty]
    public partial Bitmap? PlayingCover { get; private set; }

    /// <summary>"—" when nothing is loaded.</summary>
    public string PlayerTitle => Player.Title ?? "—";

    /// <summary>The playing track is a favorite: the player bar's heart is filled.</summary>
    public bool IsPlayingFavorite => Favorites.Contains(Player.TrackId);

    /// <summary>The player bar's heart's tooltip.</summary>
    public string PlayingFavoriteLabel => FavoritesViewModel.Label(IsPlayingFavorite);

    /// <summary>A library track is loaded (a plain file can't be a favorite, so the heart is hidden).</summary>
    public bool CanFavoritePlaying => Player.TrackId is { Length: > 0 };

    /// <summary>"Artist · Album" (whichever is known).</summary>
    public string? PlayerArtist => string.Join(" · ", new[] { Player.Artist, Player.Album }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } text ? text : null;

    /// <summary>
    /// True while a text box has focus (the window reports it): Space then types instead of toggling play. Key bindings
    /// run before the focused control sees the key, so the binding's command has to decline it.
    /// </summary>
    [ObservableProperty]
    public partial bool IsTextInputFocused { get; set; }

    /// <summary>The cog's dialog: the theme, Motion, the record speed and design, the shelf's tilt and depth.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>Connects to the player and loads the library.</summary>
    public async Task InitializeAsync()
    {
        // Showing the Albums page reads it, and the first read of the playlists and favorites follows it: both are reloads
        // like any other, so they take turns with each other and with a change seen while the app opens. (The library
        // lets one reader in at a time, even within this process: side by side, one could find it busy and be refused.)
        Sidebar.Navigate(Sidebar.Albums);
        await Task.WhenAll(Player.StartAsync(), ReloadAsync(page: false)).ConfigureAwait(true);
    }

    /// <summary>Esc: closes the topmost open layer (a menu, Settings, the queue drawer, Now Playing).</summary>
    [RelayCommand]
    private void Escape() => Layers.CloseTopmost();

    /// <summary>Space: play/pause, unless a text box has focus (then Space types; see <see cref="IsTextInputFocused"/>).</summary>
    [RelayCommand(CanExecute = nameof(CanUseShortcuts))]
    private Task Space() => Player.TogglePlayCommand.ExecuteAsync(null);

    /// <summary>Shortcuts work unless a text box has focus or Settings is open over the window.</summary>
    private bool CanUseShortcuts() => !IsTextInputFocused && !Settings.IsOpen;

    /// <summary>Ctrl+→: seeks forward, unless a text box has focus (Ctrl+arrows move its caret by words then).</summary>
    [RelayCommand(CanExecute = nameof(CanUseShortcuts), AllowConcurrentExecutions = true)]
    private Task SeekForward() => PlayerBar.Position.SeekByCommand.ExecuteAsync(PositionViewModel.KeyStep);

    /// <summary>Ctrl+←.</summary>
    [RelayCommand(CanExecute = nameof(CanUseShortcuts), AllowConcurrentExecutions = true)]
    private Task SeekBack() => PlayerBar.Position.SeekByCommand.ExecuteAsync(-PositionViewModel.KeyStep);

    /// <summary>Ctrl+↑: the volume up one step, unless a text box has focus.</summary>
    [RelayCommand(CanExecute = nameof(CanUseShortcuts), AllowConcurrentExecutions = true)]
    private Task VolumeUp() => PlayerBar.Volume.StepCommand.ExecuteAsync(VolumeViewModel.KeyStep);

    /// <summary>Ctrl+↓.</summary>
    [RelayCommand(CanExecute = nameof(CanUseShortcuts), AllowConcurrentExecutions = true)]
    private Task VolumeDown() => PlayerBar.Volume.StepCommand.ExecuteAsync(-VolumeViewModel.KeyStep);

    /// <summary>
    /// → ↓: flips the Albums page's active crate one record on, unless a text box has focus. (A focused slider, such as
    /// the position bar, takes the arrows itself first.)
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUseShortcuts))]
    private void FlipCrateOn() => FlipCrate(1);

    /// <summary>← ↑: one record back.</summary>
    [RelayCommand(CanExecute = nameof(CanUseShortcuts))]
    private void FlipCrateBack() => FlipCrate(-1);

    private void FlipCrate(int step)
    {
        if (CurrentPage == AlbumsPage)
        {
            AlbumsPage.FlipActiveCrate(step);
        }
    }

    /// <summary>The play/pause button.</summary>
    [RelayCommand]
    private Task TogglePlay() => Player.TogglePlayCommand.ExecuteAsync(null);

    /// <summary>The player bar's heart: the playing track becomes a favorite, or stops being one.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task<bool> TogglePlayingFavorite() => Favorites.ToggleAsync(Player.TrackId);

    [RelayCommand]
    private void DismissBanner() => Banner = null;

    /// <summary>
    /// Right-click on <paramref name="item"/>: a sidebar playlist's menu, or a library item's. False when it has none.
    /// </summary>
    public bool ShowMenu(object item) => item is NavItemViewModel entry ? Playlists.ShowMenu(entry) : Items.ShowMenu(item);

    /// <summary>Albums or tracks dropped on Favorites in the sidebar: their library tracks become favorites, and it flashes.</summary>
    public async Task AddToFavoritesAsync(IReadOnlyList<TrackSummary> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (await Favorites.SetAsync(tracks.Select(t => t.Id), true).ConfigureAwait(true))
        {
            Sidebar.MarkFavoritesFresh();
        }
    }

    /// <summary>Closing the window: the music stops; a player the app started is shut down.</summary>
    public Task ShutdownAsync() => Player.ShutdownAsync();

    public async ValueTask DisposeAsync()
    {
        _albumLookup?.Cancel();
        _albumLookup?.Dispose();
        _reloadRetry?.Dispose();
        _owedAlbumLookup?.Dispose();
        NowPlaying.Dispose();
        _listsOwed = _pageOwed = false;
        Scene.Dispose();
        QueueDrawer.Dispose();
        SpinClock.Dispose();
        PlayerBar.Dispose();
        await Player.DisposeAsync().ConfigureAwait(false);
    }

    partial void OnSearchTextChanged(string value) => CurrentPage.SearchText = value;

    partial void OnIsTextInputFocusedChanged(bool value) => NotifyShortcutsChanged();

    private void NotifyShortcutsChanged()
    {
        foreach (var command in new IRelayCommand[] { SpaceCommand, SeekForwardCommand, SeekBackCommand, VolumeUpCommand, VolumeDownCommand, FlipCrateOnCommand, FlipCrateBackCommand })
        {
            command.NotifyCanExecuteChanged();
        }
    }

    private async Task ShowAsync(NavItemViewModel item)
    {
        PageViewModel page = item.Kind switch
        {
            PageKind.Tracks => TracksPage,
            PageKind.Radio => RadioPage,
            PageKind.Favorites => FavoritesPage,
            PageKind.Playlist when item.Playlist is { } playlist => PlaylistPage(playlist),
            _ => AlbumsPage,
        };
        page.SearchText = SearchText;
        page.PlayingAlbumId = PlayingAlbumId;
        page.PlayingTrackId = Player.TrackId;
        CurrentPage.IsShown = false;
        page.IsShown = true;
        CurrentPage = page;
        // Read in turn with the reloads (never alongside one); a busy library leaves the read owed, not lost.
        await ReloadAsync(lists: false).ConfigureAwait(true);
    }

    private PlaylistPageViewModel PlaylistPage(PlaylistSummaryItem playlist)
    {
        if (!_playlistPages.TryGetValue(playlist.Id, out var page))
        {
            page = new PlaylistPageViewModel(playlist, _services.Library, _services.Covers, Player, Menu, Playlists, Favorites, Notices);
            _playlistPages[playlist.Id] = page;
        }

        return page;
    }

    /// <summary>The sidebar's playlists; an open page follows its playlist's new name, and a deleted one's page goes.</summary>
    private async Task LoadPlaylistsAsync()
    {
        var playlists = await _services.Library.PlaylistsAsync().ConfigureAwait(true);
        var byId = playlists.ToDictionary(p => p.Id, StringComparer.Ordinal);
        foreach (var (id, page) in _playlistPages.ToList())
        {
            if (byId.TryGetValue(id, out var playlist))
            {
                page.Playlist = playlist;
            }
            else
            {
                _playlistPages.Remove(id);
                page.Dispose();
            }
        }

        Sidebar.SetPlaylists(playlists);
    }

    /// <summary>Reloads asked for so far (tests wait on these, not on time).</summary>
    internal int ReloadsAsked => _reloadsAsked;

    /// <summary>The reloads asked for up to this one have finished (each pass covers every ask before it began).</summary>
    internal int ReloadsDone => _reloadsDone;

    /// <summary>
    /// The library changed (on disk, or by an edit here): re-reads the playlists, the favorites and the current page.
    /// Reloads never overlap: asked for while one runs, exactly one more runs after it, however often it was asked (a scan
    /// from the CLI changes the library many times). The task completes once a reload begun after the ask has finished.
    /// Showing a page is a reload of the page alone. A read the busy library refused is owed (see <see cref="Quietly"/>).
    /// </summary>
    /// <param name="lists">Read the sidebar's playlists and the favorites (not when a page is shown).</param>
    /// <param name="page">Read the current page (not at startup: showing it reads it).</param>
    private Task ReloadAsync(bool lists = true, bool page = true)
    {
        _reloadLists |= lists;
        _reloadPage |= page;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _reloadWaiters.Add((++_reloadsAsked, done));
        if (!_reloading)
        {
            _ = ReloadUntilCurrentAsync();
        }

        return done.Task;
    }

    private async Task ReloadUntilCurrentAsync()
    {
        _reloading = true;
        try
        {
            while (_reloadsDone < _reloadsAsked)
            {
                var asked = _reloadsAsked;
                var lists = _reloadLists;
                var page = _reloadPage;
                _reloadLists = false;
                _reloadPage = false;
                // What this pass reads is no longer owed (the busy library may refuse it again); what it skips still is.
                _listsOwed &= !lists;
                _pageOwed &= !page;
                Exception? failure = null;
                try
                {
                    if (lists)
                    {
                        await Quietly(LoadPlaylistsAsync, page: false).ConfigureAwait(true);
                        await Quietly(Favorites.LoadAsync, page: false).ConfigureAwait(true);
                    }

                    if (page)
                    {
                        await Quietly(CurrentPage.LoadAsync, page: true).ConfigureAwait(true);
                    }
                }
#pragma warning disable CA1031 // anything the notices don't cover goes to whoever waits for this pass, as it did before reloads were sequenced
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    failure = ex;
                }

                _reloadsDone = asked;
                foreach (var waiter in _reloadWaiters.Where(w => w.Asked <= asked).ToList())
                {
                    _reloadWaiters.Remove(waiter);
                    if (failure is null)
                    {
                        waiter.Done.SetResult();
                    }
                    else
                    {
                        waiter.Done.SetException(failure);
                    }
                }
            }
        }
        finally
        {
            _reloading = false;
        }

        if (_listsOwed || _pageOwed)
        {
            RetryReloadAtNextCheck();
        }
    }

    /// <summary>
    /// A read the busy library refused is owed: it is tried again when the library is next checked for changes, whether
    /// or not the holder changed anything (a long read-only command doesn't), and again at each check until it lands.
    /// </summary>
    private void RetryReloadAtNextCheck()
    {
        if (_reloadRetry is not null)
        {
            return;
        }

        _reloadRetry = _services.Time.CreateTimer(_ => _services.Ui.Post(() =>
        {
            _reloadRetry?.Dispose();
            _reloadRetry = null;
            if (_listsOwed || _pageOwed)
            {
                _ = ReloadAsync(lists: _listsOwed, page: _pageOwed);
            }
        }), null, LibraryService.PollInterval, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// A read in the background: if another process holds the library (a scan, say), it says nothing and the read is
    /// owed (see <see cref="RetryReloadAtNextCheck"/>): the current page's, or the playlists' and favorites'. Other
    /// failures are notices.
    /// </summary>
    private Task Quietly(Func<Task> read, bool page) => Notices.GuardAsync(async () =>
    {
        try
        {
            await read().ConfigureAwait(true);
        }
        catch (MusicSpinException ex) when (ex.Code == MusicSpinErrorCode.LibraryLocked)
        {
            // busy: owed, tried again at the next check (or the next change, if one comes first)
            if (page)
            {
                _pageOwed = true;
            }
            else
            {
                _listsOwed = true;
            }
        }
    });

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.Title):
                OnPropertyChanged(nameof(PlayerTitle));
                break;
            case nameof(PlayerViewModel.Artist):
            case nameof(PlayerViewModel.Album):
                OnPropertyChanged(nameof(PlayerArtist));
                break;
            case nameof(PlayerViewModel.TrackId):
                CurrentPage.PlayingTrackId = Player.TrackId;
                OnPropertyChanged(nameof(CanFavoritePlaying));
                NotifyPlayingFavorite();
                _ = FindPlayingAlbumAsync(Player.TrackId);
                break;
        }
    }

    private void OnFavoritesChanged()
    {
        Sidebar.Favorites.Detail = Favorites.Count > 0 ? Favorites.Count.ToString("N0", CultureInfo.CurrentCulture) : null;
        NotifyPlayingFavorite();
    }

    private void NotifyPlayingFavorite()
    {
        OnPropertyChanged(nameof(IsPlayingFavorite));
        OnPropertyChanged(nameof(PlayingFavoriteLabel));
    }

    /// <summary>The playing track's album: marks it on the pages, shows its cover and takes its accent colour.</summary>
    private async Task FindPlayingAlbumAsync(string? trackId)
    {
        _albumLookup?.Cancel();
        _albumLookup?.Dispose();
        var lookup = _albumLookup = new CancellationTokenSource();
        string? albumId = null;
        try
        {
            albumId = trackId is null ? null : await _services.Library.AlbumIdOfTrackAsync(trackId, lookup.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (OwedReads.IsBusy(ex) && !lookup.IsCancellationRequested)
        {
            // Another process holds the library: looked up again at the next check, if the track is still playing then.
            (_owedAlbumLookup ??= new OwedReads(_services.Time, _services.Ui)).Owe(() =>
            {
                if (ReferenceEquals(_albumLookup, lookup) && Player.TrackId == trackId)
                {
                    _ = FindPlayingAlbumAsync(trackId);
                }
            });
            return;
        }
        catch (Exception ex) when (ex is MusicSpinException or OperationCanceledException or IOException)
        {
            // keep what we have; the next track change tries again
            return;
        }

        if (lookup.IsCancellationRequested || albumId == PlayingAlbumId)
        {
            return;
        }

        PlayingAlbumId = albumId;
        _ = NowPlaying.ShowAlbumAsync(albumId);
        foreach (var page in new PageViewModel[] { AlbumsPage, TracksPage, FavoritesPage }.Concat(_playlistPages.Values))
        {
            page.PlayingAlbumId = albumId;
        }

        await Notices.GuardAsync(async () =>
        {
            // A cover waits out a busy library (see CoverService), so another album may be playing by the time it comes:
            // only the playing album's cover and colour are shown.
            var read = albumId is null ? Task.FromResult<Bitmap?>(null) : _services.Covers.CoverAsync(albumId, CoverSize.Small);
            if (!read.IsCompleted)
            {
                PlayingCover = null; // not the last album's cover meanwhile
            }

            var cover = await read.ConfigureAwait(true);
            if (PlayingAlbumId != albumId)
            {
                return;
            }

            PlayingCover = cover;
            var accent = albumId is null ? null : await _services.Covers.AccentAsync(albumId).ConfigureAwait(true);
            if (PlayingAlbumId == albumId)
            {
                _services.Appearance.SetAccent(accent);
            }
        }).ConfigureAwait(true);
    }
}
