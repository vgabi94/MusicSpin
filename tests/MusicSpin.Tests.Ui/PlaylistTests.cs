using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using MusicSpin.App.Services;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;
using MusicSpin.Core.Library;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>Playlists in the sidebar and the playlist page (#13), over a library on disk and a scripted player.</summary>
public sealed class PlaylistTests
{
    private static AppOptions Options(Action<MusicLibrary, IReadOnlyList<string>>? prepare = null, AppTheme theme = AppTheme.Dark)
        => JazzLibrary.Options(prepare, theme);

    private static string TrackPath(IReadOnlyList<string> paths, string album, int number) => JazzLibrary.TrackPath(paths, album, number);

    private static PlaylistInput Entry(IReadOnlyList<string> paths, string album, int number) => JazzLibrary.Entry(paths, album, number);

    private static PlaylistInput Gone(IReadOnlyList<string> paths) => JazzLibrary.Gone(paths);

    /// <summary>"Mix": Kind of Blue 2, a missing file, Blue Train 1, Kind of Blue 1.</summary>
    private static void Mix(MusicLibrary library, IReadOnlyList<string> paths)
        => library.CreatePlaylist("Mix", [Entry(paths, "Kind of Blue", 2), Gone(paths), Entry(paths, "Blue Train", 1), Entry(paths, "Kind of Blue", 1)]);

    internal static async Task<PlaylistPageViewModel> OpenPlaylistAsync(AppHarness app, string name, int entries)
    {
        await Gui.UntilAsync(() => app.ViewModel.Sidebar.Playlists.Any(p => p.Title == name), $"'{name}' in the sidebar");
        app.ViewModel.Sidebar.NavigateCommand.Execute(app.ViewModel.Sidebar.Playlists.Single(p => p.Title == name));
        var page = Assert.IsType<PlaylistPageViewModel>(app.ViewModel.CurrentPage);
        await Gui.UntilAsync(() => page.Tracks.Count == entries, $"the playlist '{name}'");
        Gui.Settle();
        return page;
    }

    internal static Border Row(AppHarness app, string title)
        => Gui.All<Border>(app.Window).Single(b => b.Classes.Contains("track") && b.DataContext is TrackRow { Item.Title: var t } && t == title);

    private static string[] Titles(PlaylistPageViewModel page) => [.. page.Tracks.Select(t => t.Title)];

    private static string[] Paths(JsonElement request) => [.. request.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("path").GetString()!)];

    private static async Task<JsonElement> SentAsync(AppHarness app, string cmd, int count = 1)
    {
        await Gui.UntilAsync(() => app.Scripted!.Count(cmd) >= count, $"'{cmd}' sent");
        return app.Scripted!.Sent(cmd)[count - 1];
    }

    private static Border Flash(AppHarness app, string title)
        => Gui.All<Border>(app.Window).Single(b => b.Classes.Contains("navFlash") && b.DataContext is NavItemViewModel { Title: var t } && t == title);

    // ---- the sidebar ------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task The_sidebar_lists_the_playlists_and_follows_changes_made_elsewhere()
    {
        await using var app = await AppHarness.OpenAsync(Options((library, paths) =>
        {
            library.CreatePlaylist("Morning", [Entry(paths, "Blue Train", 1)]);
            library.CreatePlaylist("Late night", [Entry(paths, "Kind of Blue", 1)]);
        }));
        await Gui.UntilAsync(() => app.ViewModel.Sidebar.Playlists.Count == 2, "the playlists");
        Assert.Equal(["Late night", "Morning"], app.ViewModel.Sidebar.Playlists.Select(p => p.Title));
        var morning = app.ViewModel.Sidebar.Playlists[1];

        // made by another MusicLibrary, as the CLI would: it appears in name order, and the others keep their entries
        app.Edit(library => library.CreatePlaylist("Evening", [Entry(app.TrackPaths, "Blue Train", 2)]));
        await Gui.UntilAsync(() => app.ViewModel.Sidebar.Playlists.Count == 3, "the new playlist");
        Assert.Equal(["Evening", "Late night", "Morning"], app.ViewModel.Sidebar.Playlists.Select(p => p.Title));
        Assert.Same(morning, app.ViewModel.Sidebar.Playlists[2]);
        Assert.Contains(Gui.All<Button>(app.Window), b => b.Classes.Contains("nav") && b.DataContext is NavItemViewModel { Title: "Evening" });

        // renamed and deleted elsewhere
        app.Edit(library => library.RenamePlaylist("Morning", "Afternoon"));
        await Gui.UntilAsync(() => app.ViewModel.Sidebar.Playlists[0].Title == "Afternoon", "the rename");
        Assert.Same(morning, app.ViewModel.Sidebar.Playlists[0]);
        app.Edit(library => library.DeletePlaylist("Evening"));
        await Gui.UntilAsync(() => app.ViewModel.Sidebar.Playlists.Count == 2, "the delete");
        Assert.Equal(["Afternoon", "Late night"], app.ViewModel.Sidebar.Playlists.Select(p => p.Title));
    }

    [AvaloniaFact]
    public async Task A_playlist_saved_from_the_queue_flashes_in_the_sidebar_and_the_flash_fades()
    {
        await using var app = await AppHarness.OpenAsync(Options());
        var tracks = await app.Services.Library.TracksAsync(TestContext.Current.CancellationToken);
        IReadOnlyList<QueueItem> items = [.. tracks.Select(t => new QueueItem(t.Path, t.Title, t.Artist, t.Album, null, t.TrackNumber, t.DurationSeconds, t.Id))];
        app.Scripted!.SetQueue(items, 0, 2);
        await app.Scripted.PushAsync("queueChanged");
        await app.Scripted.PushAsync("stateChanged");
        var drawer = app.ViewModel.QueueDrawer;
        await Gui.UntilAsync(() => drawer.Rows.Count == 2, "the rows");

        await drawer.StartSaveCommand.ExecuteAsync(null);
        await drawer.SaveCommand.ExecuteAsync(null);
        await Gui.UntilAsync(() => app.ViewModel.Sidebar.Playlists is [{ IsFresh: true }], "the saved playlist");
        var flash = Flash(app, "New playlist");
        Assert.Equal(1, Animate.GetPlayed(flash));
        Assert.Equal(1, flash.Opacity, 0.05);

        app.Advance(TimeSpan.FromMilliseconds(900));
        Gui.Settle();
        Assert.InRange(flash.Opacity, 0.05, 0.95);

        // the save changed library.db: the reload it brings keeps the entry, and doesn't replay the flash
        app.Advance(TimeSpan.FromSeconds(1));
        app.Advance(LibraryService.PollInterval);
        await Gui.UntilAsync(() => app.ViewModel.Sidebar.Playlists[0].Playlist?.EntryCount == 2, "the reload");
        Gui.Settle();
        Assert.Same(flash, Flash(app, "New playlist"));
        Assert.Equal(0, flash.Opacity, 0.01);
        Assert.Equal(1, Animate.GetPlayed(flash));
    }

    // ---- the playlist page ------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task The_page_shows_the_name_the_count_and_the_entries_in_order()
    {
        await using var app = await AppHarness.OpenAsync(Options(Mix));
        await Gui.UntilAsync(() => app.ViewModel.Sidebar.Playlists.Count == 1, "the playlist");
        var page = await OpenPlaylistAsync(app, "Mix", 4);

        Assert.Equal("Mix", Gui.Find<TextBlock>(app.Window, "PageTitle").Text);
        Assert.Equal("4", Gui.Find<TextBlock>(app.Window, "PageCount").Text);
        Assert.Equal(["Kind of Blue 2", "Lost Take", "Blue Train 1", "Kind of Blue 1"], Titles(page));
        Assert.Equal([1, 2, 3, 4], page.Tracks.Select(t => t.Position));
        Assert.All(page.Rows, r => Assert.IsType<PlaylistTrackRow>(r));
        Assert.DoesNotContain(Gui.All<Button>(app.Window), b => b.Name is "SortButton" or "GroupButton"); // no grouping

        // the missing entry: dimmed, says so, no ＋, its cover doesn't play
        var lost = Row(app, "Lost Take");
        Assert.Contains("unavailable", lost.Classes);
        Assert.True(Gui.All<TextBlock>(lost).Single(t => t.Classes.Contains("missing")).IsEffectivelyVisible);
        Assert.False(Gui.All<Button>(lost).Single(b => b.Classes.Contains("add") && !b.Classes.Contains("remove")).IsVisible);
        Assert.True(Gui.All<Button>(lost).Single(b => b.Classes.Contains("remove")).IsVisible); // it can still be removed
        Assert.False(Gui.All<Button>(lost).Single(b => b.Classes.Contains("thumb")).IsEnabled);
        Assert.DoesNotContain("unavailable", Row(app, "Blue Train 1").Classes);
        Assert.False(Gui.All<TextBlock>(Row(app, "Blue Train 1")).Single(t => t.Classes.Contains("missing")).IsVisible);
    }

    [AvaloniaFact]
    public async Task An_empty_playlist_says_how_to_fill_it_and_its_buttons_are_off()
    {
        await using var app = await AppHarness.OpenAsync(Options((library, _) => library.CreatePlaylist("Empty")));
        await Gui.UntilAsync(() => app.ViewModel.Sidebar.Playlists.Count == 1, "the playlist");
        var page = await OpenPlaylistAsync(app, "Empty", 0);
        await Gui.UntilAsync(() => page.EmptyText is not null, "the empty text");

        Assert.Equal("This playlist is empty. Drag albums or tracks onto it in the sidebar.", Gui.Find<TextBlock>(app.Window, "EmptyText").Text);
        Assert.False(Gui.Find<Button>(app.Window, "PlayAll").IsEffectivelyEnabled);
        Assert.False(Gui.Find<Button>(app.Window, "QueueAll").IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public async Task Play_sends_one_queue_set_with_the_entries_in_order_and_ends_a_station_on_air()
    {
        await using var app = await AppHarness.OpenAsync(Options(Mix));
        app.Scripted!.SetQueue(["on air", "pick"], radio: new RadioFilter { Genres = ["Jazz"] });
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => app.ViewModel.Player.IsRadio && app.ViewModel.Sidebar.Playlists.Count == 1, "the station on air");
        await OpenPlaylistAsync(app, "Mix", 4);

        Gui.Click(app.Window, Gui.Find<Button>(app.Window, "PlayAll"));
        var set = await SentAsync(app, "queue.set");
        await Gui.UntilAsync(() => !app.ViewModel.Player.IsRadio, "the station off air");

        Assert.Equal(1, app.Scripted.Count("queue.set"));
        Assert.Equal([TrackPath(app.TrackPaths, "Kind of Blue", 2), TrackPath(app.TrackPaths, "Blue Train", 1), TrackPath(app.TrackPaths, "Kind of Blue", 1)], Paths(set));
        Assert.Equal(0, set.GetProperty("start").GetInt32());
        Assert.True(set.GetProperty("play").GetBoolean());
    }

    [AvaloniaFact]
    public async Task Queue_offers_Play_next_and_Add_to_queue_for_the_entries_that_can_play()
    {
        await using var app = await AppHarness.OpenAsync(Options(Mix));
        await Gui.UntilAsync(() => app.ViewModel.Sidebar.Playlists.Count == 1, "the playlist");
        await OpenPlaylistAsync(app, "Mix", 4);

        Gui.Click(app.Window, Gui.Find<Button>(app.Window, "QueueAll"));
        Assert.True(app.ViewModel.Menu.IsOpen);
        Assert.Equal(["Play next", "Add to queue", "Add to playlist"], app.ViewModel.Menu.Entries.Select(e => e.Label));

        await app.ViewModel.Menu.ChooseCommand.ExecuteAsync(app.ViewModel.Menu.Entries[1]);
        var added = await SentAsync(app, "queue.add");
        Assert.Equal("upnext", added.GetProperty("position").GetString());
        Assert.Equal([TrackPath(app.TrackPaths, "Kind of Blue", 2), TrackPath(app.TrackPaths, "Blue Train", 1), TrackPath(app.TrackPaths, "Kind of Blue", 1)], Paths(added));
    }

    [AvaloniaFact]
    public async Task Search_filters_the_rows()
    {
        await using var app = await AppHarness.OpenAsync(Options(Mix));
        await Gui.UntilAsync(() => app.ViewModel.Sidebar.Playlists.Count == 1, "the playlist");
        var page = await OpenPlaylistAsync(app, "Mix", 4);

        app.ViewModel.SearchText = "coltrane";
        Assert.Equal(["Blue Train 1"], Titles(page));
        Assert.Equal("1", page.CountText);

        app.ViewModel.SearchText = "kind 1";
        Assert.Equal(["Kind of Blue 1"], Titles(page));

        app.ViewModel.SearchText = "zzz";
        Assert.Empty(page.Tracks);
        Assert.Equal("Nothing matches “zzz”.", page.EmptyText);
        Assert.False(page.PlayAllCommand.CanExecute(null));

        // only the missing entry matches: nothing to play
        app.ViewModel.SearchText = "lost";
        Assert.Equal(["Lost Take"], Titles(page));
        Assert.False(page.PlayAllCommand.CanExecute(null));
        Assert.False(page.QueueAllCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task An_entry_whose_file_is_missing_cant_be_played_queued_or_dragged()
    {
        await using var app = await AppHarness.OpenAsync(Options(Mix));
        await Gui.UntilAsync(() => app.ViewModel.Sidebar.Playlists.Count == 1, "the playlist");
        await OpenPlaylistAsync(app, "Mix", 4);
        var lost = Row(app, "Lost Take");
        var at = lost.TranslatePoint(new Point(lost.Bounds.Width / 2, lost.Bounds.Height / 2), app.Window)!.Value;

        // a double-click plays nothing; nor does a right-click open a menu
        Gui.DoubleClick(app.Window, lost);
        app.Window.MouseDown(at, MouseButton.Right);
        app.Window.MouseUp(at, MouseButton.Right);
        Gui.Settle();
        Assert.False(app.ViewModel.Menu.IsOpen);

        // a drag doesn't start: no ghost, no drawer
        app.Window.MouseDown(at, MouseButton.Left);
        app.Window.MouseMove(at + new Point(12, 6), RawInputModifiers.LeftMouseButton);
        Gui.Settle();
        Assert.False(app.ViewModel.QueueDrawer.IsOpen);
        app.Window.MouseUp(at + new Point(12, 6), MouseButton.Left);
        Gui.Settle();

        // playing the entry after it starts there among the entries that can play
        Gui.DoubleClick(app.Window, Row(app, "Blue Train 1"));
        var set = await SentAsync(app, "queue.play");
        Assert.Equal(1, app.Scripted!.Count("queue.play"));
        Assert.Equal(3, Paths(set).Length);
        Assert.Equal(1, set.GetProperty("start").GetInt32());
    }

    // ---- snapshots --------------------------------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Snapshot_of_a_playlist_with_a_missing_entry(AppTheme theme)
    {
        await using var app = await AppHarness.OpenAsync(Options(Mix, theme));
        await Gui.UntilAsync(() => app.ViewModel.Sidebar.Playlists.Count == 1, "the playlist");
        var page = await OpenPlaylistAsync(app, "Mix", 4);
        await Gui.UntilAsync(() => page.Tracks.Where(t => t.Track.AlbumId.Length > 0 && t.Album == "Kind of Blue").All(t => t.Art.Cover is not null), "the covers");
        app.Window.MouseMove(new Point(1, 1));
        Gui.Settle();
        Gui.FinishAnimations();

        Snapshots.Match(app.Window, $"playlist-{theme.ToString().ToLowerInvariant()}");
    }
}
