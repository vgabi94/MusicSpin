using Avalonia.Headless.XUnit;
using MusicSpin.App.Services;
using MusicSpin.App.ViewModels;
using MusicSpin.Core.Library;
using MusicSpin.Core.Playback;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// What each page shows when its library read fails: a busy library (another process, a scan say) is quiet and read
/// again at the next check; a library that can't be read is a notice; a failed re-read leaves what was shown.
/// </summary>
[Trait("Category", "Unhappy")]
public sealed class LibraryReadFailureTests
{
    private const string Tracks = nameof(StubLibrary.TracksAsync);
    private const string AlbumTracks = nameof(StubLibrary.AlbumTracksAsync);
    private const string Entries = nameof(StubLibrary.PlaylistEntriesAsync);

    private static async Task<(AppHarness App, StubLibrary Library)> OpenAsync(Action<StubLibrary>? prepare = null)
    {
        var library = SampleLibrary.Create();
        prepare?.Invoke(library);
        var app = await AppHarness.OpenAsync(new AppOptions { Scripted = true, Library = library, Covers = SampleLibrary.Covers() });
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 10 && library.PlaylistReads >= 1, "the library");
        Gui.Settle();
        return (app, library);
    }

    private static TracksPageViewModel ShowTracks(AppHarness app)
    {
        app.ViewModel.Sidebar.NavigateCommand.Execute(app.ViewModel.Sidebar.Tracks);
        Gui.Settle();
        return Assert.IsType<TracksPageViewModel>(app.ViewModel.CurrentPage);
    }

    // ---- the Tracks page -----------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Tracks_page_on_a_library_that_cannot_be_read_says_so_and_does_not_claim_the_library_is_empty()
    {
        var (app, library) = await OpenAsync();
        await using var _ = app;
        library.Failures[Tracks] = StubLibrary.Corrupt();

        var page = ShowTracks(app);
        await Gui.UntilAsync(() => app.ViewModel.Banner is not null, "the notice");

        Assert.Equal(StubLibrary.Corrupt().Message, app.ViewModel.Banner);
        Assert.Empty(page.Tracks);
        Assert.Null(page.EmptyText);
    }

    [AvaloniaFact]
    public async Task Tracks_page_on_a_busy_library_is_quiet_and_shows_the_tracks_at_the_next_check()
    {
        var (app, library) = await OpenAsync();
        await using var _ = app;
        library.Failures[Tracks] = StubLibrary.Busy();

        var page = ShowTracks(app);
        Assert.Equal(1, library.ReadsOf(Tracks));
        Assert.Empty(page.Tracks);
        Assert.Null(app.ViewModel.Banner); // nobody did anything wrong: no notice

        library.Failures.Remove(Tracks); // the scan lets go
        app.Advance(LibraryService.PollInterval);
        await Gui.UntilAsync(() => page.Tracks.Count == library.Tracks.Count, "the owed tracks read");
        Assert.Null(app.ViewModel.Banner);

        var reads = library.ReadsOf(Tracks); // caught up: nothing more is owed
        app.Advance(LibraryService.PollInterval * 3);
        Gui.Settle();
        Assert.Equal(reads, library.ReadsOf(Tracks));
    }

    [AvaloniaFact]
    public async Task Tracks_page_keeps_its_rows_when_a_re_read_fails()
    {
        var (app, library) = await OpenAsync();
        await using var _ = app;
        var page = ShowTracks(app);
        await Gui.UntilAsync(() => page.Tracks.Count == library.Tracks.Count, "the tracks");
        var shown = page.Tracks.Select(t => t.Title).ToList();

        library.Failures[Tracks] = StubLibrary.Corrupt();
        library.RaiseChanged(); // a change elsewhere; reading it fails
        await Gui.UntilAsync(() => app.ViewModel.Banner is not null, "the notice");

        Assert.Equal(StubLibrary.Corrupt().Message, app.ViewModel.Banner);
        Assert.Equal(shown, page.Tracks.Select(t => t.Title));
        Assert.Null(page.EmptyText);
    }

    // ---- a playlist page ----------------------------------------------------------------------------------

    private static void WithPlaylist(StubLibrary library)
    {
        library.Playlists.Add(new PlaylistSummaryItem("playlist0000", "Mix", 3));
        library.PlaylistEntries["playlist0000"] = [library.Tracks[0], library.Tracks[5], library.Tracks[9]];
    }

    private static async Task<PlaylistPageViewModel> ShowPlaylistAsync(AppHarness app)
    {
        await Gui.UntilAsync(() => app.ViewModel.Sidebar.Playlists.Any(p => p.Title == "Mix"), "the playlist in the sidebar");
        app.ViewModel.Sidebar.NavigateCommand.Execute(app.ViewModel.Sidebar.Playlists.Single(p => p.Title == "Mix"));
        Gui.Settle();
        return Assert.IsType<PlaylistPageViewModel>(app.ViewModel.CurrentPage);
    }

    [AvaloniaFact]
    public async Task Playlist_page_whose_entries_cannot_be_read_says_so_and_does_not_call_the_playlist_empty()
    {
        var (app, library) = await OpenAsync(WithPlaylist);
        await using var _ = app;
        library.Failures[Entries] = StubLibrary.Corrupt();

        var page = await ShowPlaylistAsync(app);
        await Gui.UntilAsync(() => app.ViewModel.Banner is not null, "the notice");

        Assert.Equal(StubLibrary.Corrupt().Message, app.ViewModel.Banner);
        Assert.Empty(page.Tracks);
        Assert.Null(page.EmptyText); // not "This playlist is empty"
    }

    [AvaloniaFact]
    public async Task Playlist_page_on_a_busy_library_is_quiet_and_shows_the_entries_at_the_next_check()
    {
        var (app, library) = await OpenAsync(WithPlaylist);
        await using var _ = app;
        library.Failures[Entries] = StubLibrary.Busy();

        var page = await ShowPlaylistAsync(app);
        Assert.Equal(1, library.ReadsOf(Entries));
        Assert.Empty(page.Tracks);
        Assert.Null(app.ViewModel.Banner);

        library.Failures.Remove(Entries);
        app.Advance(LibraryService.PollInterval);
        await Gui.UntilAsync(() => page.Tracks.Count == 3, "the owed entries read");
        Assert.Null(app.ViewModel.Banner);
    }

    [AvaloniaFact]
    public async Task Removing_an_entry_when_the_re_read_fails_says_so_and_keeps_the_rows()
    {
        var (app, library) = await OpenAsync(WithPlaylist);
        await using var _ = app;
        var page = await ShowPlaylistAsync(app);
        await Gui.UntilAsync(() => page.Tracks.Count == 3, "the entries");

        library.Failures[Entries] = StubLibrary.Corrupt();
        page.RemoveEntryCommand.Execute(page.Tracks[1]);
        await Gui.UntilAsync(() => app.ViewModel.Banner is not null, "the notice");

        Assert.Equal(StubLibrary.Corrupt().Message, app.ViewModel.Banner);
        Assert.Equal(2, library.PlaylistEntries["playlist0000"].Count); // the removal itself went through
        Assert.Equal(3, page.Tracks.Count); // the rows read last stay until a read succeeds

        library.Failures.Remove(Entries);
        library.RaiseChanged();
        await Gui.UntilAsync(() => page.Tracks.Count == 2, "the entries read again");
    }

    // ---- Now Playing --------------------------------------------------------------------------------------

    private static async Task PlayAsync(AppHarness app, int album)
    {
        var a = SampleLibrary.Albums[album - 1];
        app.Scripted!.Status = ScriptedPlayer.Playing(a.Tracks[0], trackId: SampleLibrary.TrackId(album, 1));
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => app.ViewModel.PlayingAlbumId == SampleLibrary.AlbumId(album) && app.ViewModel.Player.State == PlayerState.Playing,
            $"album {album} playing");
    }

    [AvaloniaFact]
    public async Task Now_Playing_whose_album_cannot_be_read_says_so_and_does_not_show_the_last_albums_tracks()
    {
        var (app, library) = await OpenAsync();
        await using var _ = app;
        await PlayAsync(app, 1);
        var nowPlaying = app.ViewModel.NowPlaying;
        await Gui.UntilAsync(() => nowPlaying.Tracks.Count == SampleLibrary.Albums[0].Tracks.Length, "album 1 in Now Playing");

        library.Failures[AlbumTracks] = StubLibrary.Corrupt();
        await PlayAsync(app, 3);
        await Gui.UntilAsync(() => app.ViewModel.Banner is not null, "the notice");

        Assert.Equal(StubLibrary.Corrupt().Message, app.ViewModel.Banner);
        Assert.Empty(nowPlaying.Tracks); // not album 1's under album 3's playback
        Assert.Equal(app.ViewModel.Player.Album ?? app.ViewModel.Player.Title, nowPlaying.Title); // what the player says
        Assert.Null(nowPlaying.Cover);
    }

    [AvaloniaFact]
    public async Task Now_Playing_waiting_out_a_busy_library_shows_the_new_track_not_the_last_album()
    {
        var (app, library) = await OpenAsync();
        await using var _ = app;
        await PlayAsync(app, 1);
        var nowPlaying = app.ViewModel.NowPlaying;
        await Gui.UntilAsync(() => nowPlaying.Tracks.Count == SampleLibrary.Albums[0].Tracks.Length, "album 1 in Now Playing");

        library.Failures[AlbumTracks] = StubLibrary.Busy();
        await PlayAsync(app, 3);
        Gui.Settle();
        Assert.Empty(nowPlaying.Tracks);
        Assert.NotEqual(SampleLibrary.Albums[0].Title, nowPlaying.Title);
        Assert.Null(app.ViewModel.Banner);

        library.Failures.Remove(AlbumTracks);
        app.Advance(LibraryService.PollInterval);
        await Gui.UntilAsync(() => nowPlaying.Tracks.Count == SampleLibrary.Albums[2].Tracks.Length, "the owed album read");
        Assert.Equal(SampleLibrary.Albums[2].Title, nowPlaying.Title);
    }

    [AvaloniaFact]
    public async Task Now_Playing_on_a_busy_library_is_quiet_and_shows_the_album_at_the_next_check()
    {
        var (app, library) = await OpenAsync();
        await using var _ = app;
        library.Failures[AlbumTracks] = StubLibrary.Busy();

        await PlayAsync(app, 3);
        Gui.Settle();
        var nowPlaying = app.ViewModel.NowPlaying;
        Assert.Empty(nowPlaying.Tracks);
        Assert.Null(app.ViewModel.Banner);

        library.Failures.Remove(AlbumTracks);
        app.Advance(LibraryService.PollInterval);
        await Gui.UntilAsync(() => nowPlaying.Tracks.Count == SampleLibrary.Albums[2].Tracks.Length, "the owed album read");
        Assert.Equal(SampleLibrary.Albums[2].Title, nowPlaying.Title);
        Assert.Null(app.ViewModel.Banner);
    }
}
