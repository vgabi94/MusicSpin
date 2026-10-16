using System.ComponentModel;
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
    private CancellationTokenSource? _albumLookup;

    public MainWindowViewModel(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        Player = new PlayerViewModel(services.Player, services.Time, services.Ui);
        SpinClock = new SpinClock(services.Time, services.Ui);
        NowPlaying = new NowPlayingViewModel(Player, services.Library, services.Covers, Notices);
        NowPlaying.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NowPlayingViewModel.IsSpinning))
            {
                SpinClock.IsSpinning = NowPlaying.IsSpinning;
            }
        };
        QueueDrawer = new QueueDrawerViewModel(Player, services.Library, services.Covers, Notices, services.Time, services.Ui);
        QueueDrawer.PlaylistSaved += (_, _) => _ = Notices.GuardAsync(LoadPlaylistsAsync);
        PlayerBar = new PlayerBarViewModel(Player, NowPlaying, QueueDrawer, services.Time, services.Ui);
        Layers.Register(NowPlaying);
        Layers.Register(QueueDrawer);
        Layers.Register(QueueDrawer.SaveForm);
        Layers.Register(Menu);
        Settings = new SettingsViewModel(services.Settings, services.Appearance, SpinClock, Notices);
        Scene = new TurntableSceneViewModel(Player, NowPlaying, services.Library, services.Covers, Settings, services.Time, services.Ui, Notices);
        AlbumsPage = new AlbumsPageViewModel(services.Library, services.Covers, Player, SpinClock, NowPlaying, Scene, Menu, Notices, Settings, services.Time);
        TracksPage = new TracksPageViewModel(services.Library, services.Covers, Player, Menu, Notices);
        RadioPage = new RadioPageViewModel(Player, Notices);
        CurrentPage = AlbumsPage;
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

    /// <summary>The Sort and Group dropdowns and the ＋ menus.</summary>
    public MenuViewModel Menu { get; } = new();

    /// <summary>What Esc closes, topmost first. Menus, Settings, the queue drawer and Now Playing register here.</summary>
    public LayerRegistry Layers { get; } = new();

    public Notices Notices { get; } = new();

    public AlbumsPageViewModel AlbumsPage { get; }

    public TracksPageViewModel TracksPage { get; }

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
        Sidebar.Navigate(Sidebar.Albums);
        await Task.WhenAll(Player.StartAsync(), Notices.GuardAsync(LoadPlaylistsAsync)).ConfigureAwait(true);
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

    [RelayCommand]
    private void DismissBanner() => Banner = null;

    /// <summary>Closing the window: the music stops; a player the app started is shut down.</summary>
    public Task ShutdownAsync() => Player.ShutdownAsync();

    public async ValueTask DisposeAsync()
    {
        _albumLookup?.Cancel();
        _albumLookup?.Dispose();
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
            PageKind.Playlist when item.Playlist is { } playlist => PlaylistPage(playlist),
            _ => AlbumsPage,
        };
        page.SearchText = SearchText;
        page.PlayingAlbumId = PlayingAlbumId;
        page.PlayingTrackId = Player.TrackId;
        CurrentPage = page;
        await Notices.GuardAsync(page.LoadAsync).ConfigureAwait(true);
    }

    private PlaylistPageViewModel PlaylistPage(PlaylistSummaryItem playlist)
    {
        if (!_playlistPages.TryGetValue(playlist.Id, out var page))
        {
            page = new PlaylistPageViewModel(playlist, _services.Library, _services.Covers, Player, Menu, Notices);
            _playlistPages[playlist.Id] = page;
        }

        return page;
    }

    private async Task LoadPlaylistsAsync()
        => Sidebar.SetPlaylists(await _services.Library.PlaylistsAsync().ConfigureAwait(true));

    /// <summary>The library changed on disk (a scan, a playlist edit): re-read the playlists and the current page.</summary>
    private async Task ReloadAsync()
    {
        await Notices.GuardAsync(LoadPlaylistsAsync).ConfigureAwait(true);
        await Notices.GuardAsync(CurrentPage.LoadAsync).ConfigureAwait(true);
    }

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
                _ = FindPlayingAlbumAsync(Player.TrackId);
                break;
        }
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
        foreach (var page in new PageViewModel[] { AlbumsPage, TracksPage }.Concat(_playlistPages.Values))
        {
            page.PlayingAlbumId = albumId;
        }

        await Notices.GuardAsync(async () =>
        {
            PlayingCover = albumId is null ? null : await _services.Covers.CoverAsync(albumId, CoverSize.Small).ConfigureAwait(true);
            _services.Appearance.SetAccent(albumId is null ? null : await _services.Covers.AccentAsync(albumId).ConfigureAwait(true));
        }).ConfigureAwait(true);
    }
}
