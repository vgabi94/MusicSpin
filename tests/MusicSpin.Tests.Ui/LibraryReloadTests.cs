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
    public async Task A_reload_that_fails_unexpectedly_fails_the_edit_waiting_on_it_and_the_next_reload_still_runs()
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
        Assert.Equal("Unexpected.", Assert.IsType<InvalidOperationException>(add.Exception?.InnerException).Message);

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
