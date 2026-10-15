using Microsoft.Extensions.Time.Testing;
using MusicSpin.App.Composition;
using MusicSpin.App.Services;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Settings;
using MusicSpin.Tests.Shared;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>The shell and sidebar view models (#6) without a window.</summary>
public sealed class ShellViewModelTests : IAsyncDisposable
{
    private readonly string _root = TempDir.Create("shellvm");
    private readonly DataDirectory _data;
    private readonly ScriptedPlayer _player;
    private readonly StubLibrary _library = new();
    private readonly StubAppearance _appearance = new();
    private readonly QueueDispatcher _ui = new();
    private MainWindowViewModel? _vm;

    public ShellViewModelTests()
    {
        _data = new DataDirectory(Path.Combine(_root, "data"), DataDirectorySource.Override);
        _player = new ScriptedPlayer(_data);
        _library.Albums.AddRange([StubLibrary.Album(1), StubLibrary.Album(2) with { Title = "Blue Train" }]);
        _library.Tracks.AddRange([StubLibrary.Track(10), StubLibrary.Track(11)]);
        _library.Playlists.Add(new PlaylistSummaryItem("pl1", "Evening", 1));
        _library.PlaylistEntries["pl1"] = [StubLibrary.Track(10)];
    }

    private async Task<MainWindowViewModel> CreateAsync(AppSettings? settings = null, string? warning = null)
    {
        new SettingsStore(_data).Save(settings ?? new AppSettings());
        _vm = new MainWindowViewModel(new AppServices(new SettingsService(_data), _library, new StubCovers(), _player, _appearance,
            new FakeTimeProvider(), _ui, warning));
        await _vm.InitializeAsync();
        await _ui.UntilAsync(() => _vm.Player.IsConnected, "the player");
        return _vm;
    }

    [Fact]
    public async Task It_starts_on_Albums_with_the_library_loaded()
    {
        var vm = await CreateAsync();

        Assert.Same(vm.AlbumsPage, vm.CurrentPage);
        Assert.Same(vm.Sidebar.Albums, vm.Sidebar.Active);
        Assert.True(vm.Sidebar.Albums.IsActive);
        Assert.Equal(2, vm.AlbumsPage.Albums.Count);
        Assert.Equal("2", vm.AlbumsPage.CountText);
        Assert.Equal(["Evening"], vm.Sidebar.Playlists.Select(p => p.Title));
    }

    [Fact]
    public async Task Navigating_shows_the_page_and_moves_the_highlight()
    {
        var vm = await CreateAsync();

        vm.Sidebar.NavigateCommand.Execute(vm.Sidebar.Tracks);
        await _ui.UntilAsync(() => vm.TracksPage.Tracks.Count == 2, "tracks");
        Assert.Same(vm.TracksPage, vm.CurrentPage);
        Assert.Equal((false, true), (vm.Sidebar.Albums.IsActive, vm.Sidebar.Tracks.IsActive));

        vm.Sidebar.NavigateCommand.Execute(vm.Sidebar.Radio);
        Assert.Same(vm.RadioPage, vm.CurrentPage);

        var evening = vm.Sidebar.Playlists[0];
        vm.Sidebar.NavigateCommand.Execute(evening);
        var page = Assert.IsType<PlaylistPageViewModel>(vm.CurrentPage);
        await _ui.UntilAsync(() => page.Tracks.Count == 1, "the playlist");
        Assert.Equal("Evening", page.Title);
        Assert.Equal([evening], vm.Sidebar.Library.Concat(vm.Sidebar.Playlists).Where(i => i.IsActive));

        vm.Sidebar.NavigateCommand.Execute(evening);
        Assert.Same(page, vm.CurrentPage); // one page per playlist
    }

    [Fact]
    public async Task A_deleted_playlist_that_was_open_falls_back_to_Albums()
    {
        var vm = await CreateAsync();
        vm.Sidebar.NavigateCommand.Execute(vm.Sidebar.Playlists[0]);

        vm.Sidebar.SetPlaylists([]);

        Assert.Same(vm.Sidebar.Albums, vm.Sidebar.Active);
        Assert.Same(vm.AlbumsPage, vm.CurrentPage);
    }

    [Fact]
    public async Task A_renamed_list_keeps_the_open_playlist_highlighted()
    {
        var vm = await CreateAsync();
        vm.Sidebar.NavigateCommand.Execute(vm.Sidebar.Playlists[0]);

        vm.Sidebar.SetPlaylists([new PlaylistSummaryItem("pl0", "Morning", 3), new PlaylistSummaryItem("pl1", "Evening", 2)]);

        Assert.Equal("pl1", vm.Sidebar.Active?.Playlist?.Id);
        Assert.True(vm.Sidebar.Playlists[1].IsActive);
    }

    [Fact]
    public async Task The_search_text_filters_the_current_page_and_follows_navigation()
    {
        var vm = await CreateAsync();

        vm.SearchText = "blue";
        Assert.Equal(["Blue Train"], vm.AlbumsPage.Albums.Select(a => a.Title));
        Assert.Equal("1", vm.AlbumsPage.CountText);

        vm.SearchText = "nothing like this";
        Assert.Equal("Nothing matches “nothing like this”.", vm.AlbumsPage.EmptyText);

        vm.Sidebar.NavigateCommand.Execute(vm.Sidebar.Tracks);
        await _ui.UntilAsync(() => vm.TracksPage.CountText is not null, "tracks");
        Assert.Equal("nothing like this", vm.TracksPage.SearchText);
        Assert.Empty(vm.TracksPage.Tracks);
    }

    [Fact]
    public async Task Esc_closes_layers_topmost_first_and_nothing_when_none_is_open()
    {
        var vm = await CreateAsync();
        var closed = new List<int>();
        foreach (var order in new[] { LayerOrder.Settings, LayerOrder.NowPlaying, LayerOrder.Menu, LayerOrder.QueueDrawer })
        {
            vm.Layers.Register(new Layer(order, closed));
        }

        for (var i = 0; i < 5; i++)
        {
            vm.EscapeCommand.Execute(null);
        }

        Assert.Equal([LayerOrder.Menu, LayerOrder.Settings, LayerOrder.QueueDrawer, LayerOrder.NowPlaying], closed);
        Assert.False(vm.Layers.CloseTopmost());
    }

    [Fact]
    public async Task Space_is_declined_while_a_text_box_has_focus()
    {
        var vm = await CreateAsync();

        Assert.True(vm.SpaceCommand.CanExecute(null));
        vm.IsTextInputFocused = true;
        Assert.False(vm.SpaceCommand.CanExecute(null));
        Assert.True(vm.TogglePlayCommand.CanExecute(null)); // the button still works

        vm.IsTextInputFocused = false;
        await vm.SpaceCommand.ExecuteAsync(null);
        Assert.Equal(1, _player.Count("toggle"));
    }

    [Fact]
    public async Task Settings_apply_at_start_and_are_saved_on_change()
    {
        var vm = await CreateAsync(new AppSettings { Theme = "light", Motion = "none", RecordSpeedRpm = 45, Shuffle = true });

        Assert.Equal((AppTheme.Light, MotionMode.None), (_appearance.Theme, _appearance.Motion));
        Assert.Equal((45.0, 0.0), (vm.SpinClock.Rpm, vm.SpinClock.MotionMultiplier));

        vm.Settings.SetThemeCommand.Execute(AppTheme.Dark);
        vm.Settings.SetMotionCommand.Execute(MotionMode.Fast);
        vm.Settings.SetRecordSpeedCommand.Execute(16.667);

        Assert.Equal((AppTheme.Dark, MotionMode.Fast), (_appearance.Theme, _appearance.Motion));
        Assert.Equal((16.667, 0.5), (vm.SpinClock.Rpm, vm.SpinClock.MotionMultiplier));
        var saved = new SettingsStore(_data).Load();
        Assert.Equal(("dark", "fast", 16.667, true), (saved.Theme, saved.Motion, saved.RecordSpeedRpm, saved.Shuffle));
    }

    [Fact]
    public async Task An_unknown_record_speed_falls_back_to_33()
    {
        var vm = await CreateAsync(new AppSettings { RecordSpeedRpm = 78 });

        Assert.Equal(33.333, vm.Settings.RecordSpeed);
    }

    [Fact]
    public async Task The_startup_warning_and_later_notices_show_in_the_banner()
    {
        var vm = await CreateAsync(warning: "settings.json was unreadable");
        Assert.Equal("settings.json was unreadable", vm.Banner);

        vm.DismissBannerCommand.Execute(null);
        Assert.Null(vm.Banner);

        _player.Fail("next", 8, "Nothing to skip to.");
        await vm.Player.NextCommand.ExecuteAsync(null);
        await _ui.UntilAsync(() => vm.Banner == "Nothing to skip to.", "the notice");
    }

    [Fact]
    public async Task A_library_change_reloads_the_playlists_and_the_page()
    {
        var vm = await CreateAsync();
        _library.Albums.Add(StubLibrary.Album(3));
        _library.Playlists.Add(new PlaylistSummaryItem("pl2", "Night", 0));

        _library.RaiseChanged();
        await _ui.UntilAsync(() => vm.AlbumsPage.Albums.Count == 3 && vm.Sidebar.Playlists.Count == 2, "the reload");
    }

    [Fact]
    public async Task The_playing_track_marks_its_album_and_takes_no_accent_without_a_cover()
    {
        _library.Tracks.Add(StubLibrary.Track(20, albumId: "album0002"));
        var vm = await CreateAsync();

        _player.Status = ScriptedPlayer.Playing("Track 20", trackId: "track0020");
        await _player.PushAsync("trackLoaded");

        await _ui.UntilAsync(() => vm.PlayingAlbumId == "album0002", "the playing album");
        Assert.True(vm.AlbumsPage.Albums.Single(a => a.Album.Id == "album0002").IsPlaying);
        Assert.False(vm.AlbumsPage.Albums.Single(a => a.Album.Id == "album0001").IsPlaying);
        Assert.True(vm.SpinClock.IsSpinning);
        Assert.Null(_appearance.Accent);
        Assert.Equal("Track 20", vm.PlayerTitle);
    }

    public async ValueTask DisposeAsync()
    {
        if (_vm is not null)
        {
            await _vm.DisposeAsync();
        }

        await _player.DisposeAsync();
        TempDir.TryDelete(_root);
    }

    private sealed class Layer(int order, List<int> closed) : ILayer
    {
        public int Order { get; } = order;

        public bool IsOpen { get; private set; } = true;

        public void Close()
        {
            IsOpen = false;
            closed.Add(Order);
        }
    }
}
