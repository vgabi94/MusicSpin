using Avalonia.Headless.XUnit;
using MusicSpin.App.Services;
using MusicSpin.App.ViewModels;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// Reading the library again when it changes: one reload at a time however often it changes (a scan from the CLI changes
/// it many times), quiet while another process holds it, and Favorites read only when it is shown.
/// </summary>
public sealed class LibraryReloadTests
{
    private static async Task<(AppHarness App, StubLibrary Library)> OpenAsync()
    {
        var library = new StubLibrary { Tracks = { StubLibrary.Track(1), StubLibrary.Track(2) } };
        var app = await AppHarness.OpenAsync(new AppOptions { Scripted = true, Library = library });
        await Gui.UntilAsync(() => library.PlaylistReads >= 1, "the playlists read");
        Gui.Settle();
        return (app, library);
    }

    [AvaloniaFact]
    public async Task A_change_while_the_app_starts_reloads_after_the_first_read_not_alongside_it()
    {
        var library = new StubLibrary { Tracks = { StubLibrary.Track(1) } };
        await using var app = await AppHarness.OpenAsync(new AppOptions { Scripted = true, Library = library, Initialize = false });
        var gate = library.PlaylistsGate = new TaskCompletionSource();
        var start = app.ViewModel.InitializeAsync();
        await Gui.UntilAsync(() => library.PlaylistReads == 1, "the first read under way");

        library.RaiseChanged(); // a scan from the CLI, say, while the app opens
        Gui.Settle();
        Assert.Equal(1, library.PlaylistReads); // it waits for the first read

        library.Playlists.Add(new PlaylistSummaryItem("playlist0000", "During startup", 0));
        library.PlaylistsGate = null;
        gate.SetResult();
        await Gui.UntilAsync(() => start.IsCompleted && app.ViewModel.Sidebar.Playlists.Any(p => p.Title == "During startup"), "the reload after it");
        Assert.Equal(2, library.PlaylistReads);
        Assert.Equal(1, library.MostPlaylistReadsAtOnce);
    }

    [AvaloniaFact]
    public async Task At_startup_the_albums_read_and_the_first_playlists_read_take_turns_never_side_by_side()
    {
        var library = new StubLibrary { Albums = { StubLibrary.Album(1) }, Tracks = { StubLibrary.Track(1) } };
        await using var app = await AppHarness.OpenAsync(new AppOptions { Scripted = true, Library = library, Initialize = false });
        var albums = library.AlbumsGate = new TaskCompletionSource();
        var start = app.ViewModel.InitializeAsync();
        await Gui.UntilAsync(() => library.AlbumReads == 1, "the albums read under way");
        Gui.Settle();
        Assert.Equal(0, library.PlaylistReads); // side by side, one of them could find the library busy and be refused

        albums.SetResult();
        await Gui.UntilAsync(() => start.IsCompleted && library.PlaylistReads == 1, "the playlists read after it");
        Assert.Single(app.ViewModel.AlbumsPage.Albums);
        Assert.Equal(1, library.AlbumReads);
        Assert.Equal(1, library.MostReadsAtOnce);
        Assert.Null(app.ViewModel.Banner);
    }

    [AvaloniaFact]
    public async Task At_startup_a_busy_library_leaves_the_albums_owed_says_nothing_and_shows_them_once_it_is_free()
    {
        var library = new StubLibrary { Albums = { StubLibrary.Album(1), StubLibrary.Album(2) }, Tracks = { StubLibrary.Track(1) } };
        library.AlbumsFailure = LibraryService.BusyMessage; // a scan from the CLI holds the library as the app opens
        await using var app = await AppHarness.OpenAsync(new AppOptions { Scripted = true, Library = library, Initialize = false });
        await app.ViewModel.InitializeAsync();
        Gui.Settle();
        Assert.Empty(app.ViewModel.AlbumsPage.Albums);
        Assert.Null(app.ViewModel.Banner); // nobody asked for anything: no notice
        Assert.Equal(1, library.AlbumReads); // and no retry before the next check

        // The scan lets go, having changed nothing the app would notice: the next check reads the page again.
        library.AlbumsFailure = null;
        app.Advance(LibraryService.PollInterval);
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 2, "the owed albums read");
        Assert.Null(app.ViewModel.Banner);

        // caught up: nothing more is owed
        var reads = library.AlbumReads;
        app.Advance(LibraryService.PollInterval * 3);
        Gui.Settle();
        Assert.Equal(reads, library.AlbumReads);
    }

    [AvaloniaFact]
    public async Task A_page_shown_while_another_process_holds_the_library_is_read_once_it_is_free()
    {
        var library = new StubLibrary { Albums = { StubLibrary.Album(1) }, Tracks = { StubLibrary.Track(1) } };
        await using var app = await AppHarness.OpenAsync(new AppOptions { Scripted = true, Library = library });
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 1 && library.PlaylistReads >= 1, "the library");
        var sidebar = app.ViewModel.Sidebar;
        sidebar.NavigateCommand.Execute(sidebar.Tracks);
        Gui.Settle();

        library.Albums.Add(StubLibrary.Album(2));
        library.AlbumsFailure = LibraryService.BusyMessage;
        sidebar.NavigateCommand.Execute(sidebar.Albums);
        var refused = library.AlbumReads;
        Gui.Settle();
        Assert.Single(app.ViewModel.AlbumsPage.Albums); // what it showed before stays
        Assert.Null(app.ViewModel.Banner);

        library.AlbumsFailure = null;
        app.Advance(LibraryService.PollInterval);
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 2, "the owed page read");
        Assert.True(library.AlbumReads > refused);
        Assert.Null(app.ViewModel.Banner);
    }

    [AvaloniaFact]
    public async Task A_page_shown_during_a_slow_reload_is_read_after_it_not_alongside()
    {
        var (app, library) = await OpenAsync();
        await using var _ = app;
        library.Albums.Add(StubLibrary.Album(1));
        var sidebar = app.ViewModel.Sidebar;
        sidebar.NavigateCommand.Execute(sidebar.Tracks);
        Gui.Settle();
        var before = (library.PlaylistReads, library.AlbumReads);
        var gate = library.PlaylistsGate = new TaskCompletionSource();
        library.RaiseChanged();
        await Gui.UntilAsync(() => library.PlaylistReads == before.PlaylistReads + 1, "a reload under way");

        sidebar.NavigateCommand.Execute(sidebar.Albums);
        Gui.Settle();
        Assert.Equal(before.AlbumReads, library.AlbumReads); // it waits its turn

        library.PlaylistsGate = null;
        gate.SetResult();
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 1, "the page read after the reload");
        Assert.Equal(1, library.MostReadsAtOnce);
        Assert.Null(app.ViewModel.Banner);
    }

    [AvaloniaFact]
    public async Task A_page_read_that_fails_for_another_reason_says_so_and_is_not_retried()
    {
        var (app, library) = await OpenAsync();
        await using var _ = app;
        var sidebar = app.ViewModel.Sidebar;
        sidebar.NavigateCommand.Execute(sidebar.Tracks);
        Gui.Settle();
        library.AlbumsGate = new TaskCompletionSource();
        library.AlbumsGate.SetException(new IOException("The disk went away."));

        sidebar.NavigateCommand.Execute(sidebar.Albums);
        await Gui.UntilAsync(() => app.ViewModel.Banner is not null, "the notice");
        Assert.Equal("The disk went away.", app.ViewModel.Banner);
        var reads = library.AlbumReads;
        app.Advance(LibraryService.PollInterval * 3);
        Gui.Settle();
        Assert.Equal(reads, library.AlbumReads); // only a busy library's refusal is owed
    }

    [AvaloniaFact]
    public async Task Changes_during_a_slow_reload_make_one_more_reload_after_it_never_one_alongside()
    {
        var (app, library) = await OpenAsync();
        await using var _ = app;
        var before = library.PlaylistReads;
        var gate = library.PlaylistsGate = new TaskCompletionSource();

        library.RaiseChanged();
        await Gui.UntilAsync(() => library.PlaylistReads == before + 1, "a reload under way");
        for (var i = 0; i < 5; i++)
        {
            library.RaiseChanged();
            Gui.Settle();
        }

        Assert.Equal(before + 1, library.PlaylistReads); // the others wait for it
        library.Playlists.Add(new PlaylistSummaryItem("playlist0000", "Late", 0));
        library.PlaylistsGate = null;
        gate.SetResult();
        await Gui.UntilAsync(() => app.ViewModel.Sidebar.Playlists.Any(p => p.Title == "Late"), "the last reload");
        Gui.Settle();

        Assert.Equal(before + 2, library.PlaylistReads); // five changes, one more reload
        Assert.Equal(1, library.MostPlaylistReadsAtOnce);
    }

    [AvaloniaFact]
    public async Task A_reload_while_another_process_holds_the_library_says_nothing_and_the_next_change_reads_it()
    {
        var (app, library) = await OpenAsync();
        await using var _ = app;
        var before = library.PlaylistReads;
        library.PlaylistsFailure = LibraryService.BusyMessage;

        for (var i = 1; i <= 3; i++)
        {
            library.RaiseChanged();
            var reads = before + i;
            await Gui.UntilAsync(() => library.PlaylistReads == reads, "a reload");
            Gui.Settle();
        }

        Assert.Null(app.ViewModel.Banner);

        // the scan is done: its last change reads the library
        library.PlaylistsFailure = null;
        library.Playlists.Add(new PlaylistSummaryItem("playlist0000", "After the scan", 0));
        library.RaiseChanged();
        await Gui.UntilAsync(() => app.ViewModel.Sidebar.Playlists.Any(p => p.Title == "After the scan"), "the reload");
        Assert.Null(app.ViewModel.Banner);
    }

    [AvaloniaFact]
    public async Task A_reload_the_busy_library_refused_is_tried_again_on_the_next_check_even_with_no_new_change()
    {
        var (app, library) = await OpenAsync();
        await using var _ = app;
        var before = library.PlaylistReads;
        library.PlaylistsFailure = LibraryService.BusyMessage;
        library.RaiseChanged(); // say a favorite added from the CLI, then a long read-only command holds the library
        await Gui.UntilAsync(() => library.PlaylistReads == before + 1, "the refused reload");
        Gui.Settle();

        // The holder lets go, having changed nothing: no change comes to read it.
        library.PlaylistsFailure = null;
        library.Playlists.Add(new PlaylistSummaryItem("playlist0000", "Missed", 0));
        Gui.Settle();
        Assert.Equal(before + 1, library.PlaylistReads); // no retry before the next check

        app.Advance(LibraryService.PollInterval);
        await Gui.UntilAsync(() => app.ViewModel.Sidebar.Playlists.Any(p => p.Title == "Missed"), "the reload tried again");
        Assert.Null(app.ViewModel.Banner);

        // caught up: nothing more is owed
        var reads = library.PlaylistReads;
        app.Advance(LibraryService.PollInterval * 3);
        Gui.Settle();
        Assert.Equal(reads, library.PlaylistReads);
    }

    [AvaloniaFact]
    public async Task A_reload_that_fails_for_another_reason_says_so()
    {
        var (app, library) = await OpenAsync();
        await using var _ = app;
        library.PlaylistsGate = new TaskCompletionSource();
        library.PlaylistsGate.SetException(new IOException("The disk went away."));

        library.RaiseChanged();
        await Gui.UntilAsync(() => app.ViewModel.Banner is not null, "the notice");
        Assert.Equal("The disk went away.", app.ViewModel.Banner);
    }

    [AvaloniaFact]
    public async Task A_reload_that_fails_unexpectedly_is_a_notice_the_edit_waiting_on_it_finishes_and_the_next_reload_still_runs()
    {
        var (app, library) = await OpenAsync();
        await using var _ = app;
        library.Playlists.Add(new PlaylistSummaryItem("playlist0000", "Mix", 0));
        library.PlaylistEntries["playlist0000"] = [];
        library.RaiseChanged();
        await Gui.UntilAsync(() => app.ViewModel.Sidebar.PlaylistItem("playlist0000") is not null, "the playlist");
        var mix = app.ViewModel.Sidebar.PlaylistItem("playlist0000")!;

        // Something no notice covers: the reload after the add throws it.
        var gate = new TaskCompletionSource();
        gate.SetException(new InvalidOperationException("Unexpected."));
        library.PlaylistsGate = gate;
        var add = app.ViewModel.Playlists.AddAsync(mix, [StubLibrary.Track(1)]);
        await Gui.UntilAsync(() => add.IsCompleted, "the add to finish, not wait for a reload that won't come");
        await add; // the failure is a notice now, not the add's (#185)
        Assert.Equal($"Something went wrong: Unexpected. The details are in {Path.Combine(app.Data.Path, AppLog.FileName)}.",
            app.ViewModel.Banner);

        // and the reloads go on
        library.PlaylistsGate = null;
        library.Playlists.Add(new PlaylistSummaryItem("playlist0001", "Later", 0));
        library.RaiseChanged();
        await Gui.UntilAsync(() => app.ViewModel.Sidebar.Playlists.Any(p => p.Title == "Later"), "the next reload");
    }

    [AvaloniaFact]
    public async Task Favorites_reads_its_list_when_it_is_shown_not_on_every_change_while_it_is_not()
    {
        var (app, library) = await OpenAsync();
        await using var _ = app;
        var sidebar = app.ViewModel.Sidebar;
        sidebar.NavigateCommand.Execute(sidebar.Favorites);
        await Gui.UntilAsync(() => library.FavoritesReads == 1, "Favorites read");
        sidebar.NavigateCommand.Execute(sidebar.Tracks);
        Gui.Settle();

        await app.ViewModel.Favorites.ToggleAsync("track0001");
        await app.ViewModel.Favorites.ToggleAsync("track0002");
        Gui.Settle();
        Assert.Equal(1, library.FavoritesReads);
        Assert.True(app.ViewModel.FavoritesPage.RadioCommand.CanExecute(null)); // the button still follows

        sidebar.NavigateCommand.Execute(sidebar.Favorites);
        await Gui.UntilAsync(() => app.ViewModel.FavoritesPage.Tracks.Count == 2, "Favorites read again");
        Assert.Equal(2, library.FavoritesReads);

        // shown, a heart reads it at once
        await app.ViewModel.Favorites.ToggleAsync("track0001");
        await Gui.UntilAsync(() => app.ViewModel.FavoritesPage.Tracks.Count == 1, "the row gone");
        Assert.Equal(3, library.FavoritesReads);
    }
}
