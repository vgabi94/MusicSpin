using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Time.Testing;
using MusicSpin.App.Composition;
using MusicSpin.App.Services;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Settings;
using MusicSpin.Tests.Shared;
using MusicSpin.Tests.Ui.Harness;
using SkiaSharp;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// The playing album (the player bar's cover and colour, Now Playing) while another process holds the library: nothing
/// is said, nothing is lost, it is read at the library's next check; and a cover that comes late never replaces the cover
/// of an album that started since.
/// </summary>
public sealed class BusyLibraryTests : IAsyncDisposable
{
    private static readonly Rgb Red = new(200, 40, 40);
    private static readonly Rgb Blue = new(40, 40, 200);

    private readonly string _root = TempDir.Create("busylib");
    private readonly DataDirectory _data;
    private readonly ScriptedPlayer _player;
    private readonly StubLibrary _library = new();
    private readonly StubAppearance _appearance = new();
    private readonly QueueDispatcher _ui = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly Dictionary<string, Bitmap> _bitmaps = new(StringComparer.Ordinal);
    private readonly StubCovers _covers;
    private MainWindowViewModel? _vm;

    public BusyLibraryTests()
    {
        _data = new DataDirectory(Path.Combine(_root, "data"), DataDirectorySource.Override);
        _player = new ScriptedPlayer(_data);
        _library.Albums.AddRange([StubLibrary.Album(1), StubLibrary.Album(2)]);
        _library.Tracks.AddRange([StubLibrary.Track(10), StubLibrary.Track(20)]);
        _covers = new StubCovers(id => _bitmaps.GetValueOrDefault(id), id => id == "album0001" ? Red : Blue);
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

    private async Task<MainWindowViewModel> CreateAsync()
    {
        new SettingsStore(_data).Save(new AppSettings());
        _vm = new MainWindowViewModel(new AppServices(new SettingsService(_data), _library, _covers, _player, _appearance, _time, _ui));
        await _vm.InitializeAsync();
        await _ui.UntilAsync(() => _vm.Player.IsConnected && _library.PlaylistReads >= 1, "the player and the library");
        return _vm;
    }

    private async Task PlayAsync(int track)
    {
        _player.Status = ScriptedPlayer.Playing($"Track {track}", trackId: $"track{track:D4}");
        await _player.PushAsync("trackLoaded");
    }

    private static Bitmap Cover(SKColor colour) => TestImages.Bitmap(TestImages.Png(8, 8, (_, _) => colour));

    [Fact]
    public async Task A_track_that_starts_while_the_library_is_held_shows_its_album_at_the_next_check_without_a_notice()
    {
        var vm = await CreateAsync();
        _library.AlbumIdFailure = StubLibrary.Busy();
        await PlayAsync(20);
        await _ui.UntilAsync(() => vm.Player.TrackId == "track0020" && _library.AlbumIdReads == 1, "the album lookup refused");
        _ui.Run();
        Assert.Null(vm.PlayingAlbumId);
        Assert.Null(vm.Banner);

        // still held at the next check: asked again, nothing said
        _time.Advance(LibraryService.PollInterval);
        await _ui.UntilAsync(() => _library.AlbumIdReads == 2, "the lookup tried again");
        _ui.Run();
        Assert.Null(vm.PlayingAlbumId);

        _library.AlbumIdFailure = null;
        _time.Advance(LibraryService.PollInterval);
        await _ui.UntilAsync(() => vm.PlayingAlbumId == "album0002" && vm.NowPlaying.Tracks.Count == 1, "the album after the scan");
        Assert.Equal("Album 2", vm.NowPlaying.Title);
        Assert.Equal(Blue, _appearance.Accent);
        Assert.Null(vm.Banner);

        // caught up: nothing more is owed
        var reads = _library.AlbumIdReads;
        _time.Advance(LibraryService.PollInterval * 3);
        _ui.Run();
        Assert.Equal(reads, _library.AlbumIdReads);
    }

    [Fact]
    public async Task A_lookup_owed_for_a_track_that_has_since_ended_is_dropped_for_the_playing_ones()
    {
        var vm = await CreateAsync();
        _library.AlbumIdFailure = StubLibrary.Busy();
        await PlayAsync(10);
        await _ui.UntilAsync(() => _library.AlbumIdReads == 1, "track 10's lookup refused");
        await PlayAsync(20);
        await _ui.UntilAsync(() => vm.Player.TrackId == "track0020" && _library.AlbumIdReads == 2, "track 20's lookup refused");

        _library.AlbumIdFailure = null;
        _time.Advance(LibraryService.PollInterval);
        await _ui.UntilAsync(() => vm.PlayingAlbumId == "album0002", "the playing track's album");
        _ui.Run();
        Assert.Equal(3, _library.AlbumIdReads); // track 10's owed lookup isn't run
        Assert.Null(vm.Banner);
    }

    [Fact]
    public async Task A_lookup_that_fails_for_another_reason_is_left_to_the_next_track_change()
    {
        var vm = await CreateAsync();
        _library.AlbumIdFailure = new IOException("The disk went away.");
        await PlayAsync(20);
        await _ui.UntilAsync(() => _library.AlbumIdReads == 1, "the lookup");

        _library.AlbumIdFailure = null;
        _time.Advance(LibraryService.PollInterval * 3);
        _ui.Run();
        Assert.Equal(1, _library.AlbumIdReads);
        Assert.Null(vm.PlayingAlbumId);
    }

    [Fact]
    public async Task Now_Playing_read_while_the_library_is_held_says_nothing_and_shows_the_album_at_the_next_check()
    {
        var vm = await CreateAsync();
        _library.AlbumsFailure = LibraryService.BusyMessage;
        await PlayAsync(20);
        await _ui.UntilAsync(() => vm.PlayingAlbumId == "album0002" && _library.AlbumReads >= 2, "Now Playing's read refused");
        _ui.Run();
        Assert.Empty(vm.NowPlaying.Tracks);
        Assert.Null(vm.Banner);

        _library.AlbumsFailure = null;
        _time.Advance(LibraryService.PollInterval);
        await _ui.UntilAsync(() => vm.NowPlaying.Tracks.Count == 1, "Now Playing after the scan");
        Assert.Equal("Album 2", vm.NowPlaying.Title);
        Assert.Null(vm.Banner);
    }

    [Fact]
    public async Task Now_Playing_does_not_read_an_album_owed_from_before_another_one_started()
    {
        var vm = await CreateAsync();
        _library.AlbumsFailure = LibraryService.BusyMessage;
        await PlayAsync(10);
        await _ui.UntilAsync(() => vm.PlayingAlbumId == "album0001" && _library.AlbumReads >= 2, "album 1's read refused");
        _library.AlbumsFailure = null;
        await PlayAsync(20);
        await _ui.UntilAsync(() => vm.NowPlaying.Title == "Album 2", "album 2 shown");
        var reads = _library.AlbumReads;

        _time.Advance(LibraryService.PollInterval);
        _ui.Run();
        Assert.Equal("Album 2", vm.NowPlaying.Title); // album 1's owed read doesn't run, or replace it
        Assert.Equal(reads, _library.AlbumReads);
    }

    [AvaloniaFact]
    public async Task A_cover_that_comes_late_does_not_replace_the_cover_of_the_album_playing_since()
    {
        var (red, blue) = (Cover(SKColors.Red), Cover(SKColors.Blue));
        _bitmaps["album0002"] = blue;
        var vm = await CreateAsync();
        var late = _covers.Gates["album0001"] = new TaskCompletionSource<Bitmap?>(); // album 1's cover waits out a busy library

        await PlayAsync(10);
        await _ui.UntilAsync(() => vm.PlayingAlbumId == "album0001" && vm.NowPlaying.Title == "Album 1", "album 1 playing");
        Assert.Null(vm.PlayingCover); // no cover yet, and not some other album's
        Assert.Null(vm.NowPlaying.Cover);

        await PlayAsync(20);
        await _ui.UntilAsync(() => vm.PlayingCover == blue && vm.NowPlaying.Cover == blue && _appearance.Accent == Blue, "album 2's cover and colour");

        late.SetResult(red); // the library is free again: album 1's cover comes at last
        _ui.Run();
        Assert.Same(blue, vm.PlayingCover);
        Assert.Same(blue, vm.NowPlaying.Cover);
        Assert.Equal(Blue, _appearance.Accent);
        Assert.Null(vm.Banner);
    }
}
