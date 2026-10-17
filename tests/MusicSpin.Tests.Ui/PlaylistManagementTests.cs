using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using MusicSpin.App.Services;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;
using MusicSpin.App.Views;
using MusicSpin.App.Views.Pages;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// Managing playlists (#33) over a library on disk: after each action the sidebar, the playlist page and the playlist
/// read back through a fresh <see cref="MusicLibrary"/> (what <c>music-spin playlist show</c> prints) agree.
/// </summary>
public sealed class PlaylistManagementTests
{
    /// <summary>The queue drawer sliding in (0.45 s).</summary>
    private const int Slid = 500;

    /// <summary>"Mix": Kind of Blue 1, Kind of Blue 2, Blue Train 1 and a missing file; "Other": Blue Train 2.</summary>
    private static void Two(MusicLibrary library, IReadOnlyList<string> paths)
    {
        library.CreatePlaylist("Mix",
            [JazzLibrary.Entry(paths, "Kind of Blue", 1), JazzLibrary.Entry(paths, "Kind of Blue", 2), JazzLibrary.Entry(paths, "Blue Train", 1), JazzLibrary.Gone(paths)]);
        library.CreatePlaylist("Other", [JazzLibrary.Entry(paths, "Blue Train", 2)]);
    }

    private static readonly string[] Mix = ["Kind of Blue 1", "Kind of Blue 2", "Blue Train 1", "Lost Take"];

    private static async Task<AppHarness> OpenAsync(TimeSpan? lockTimeout = null, AppTheme theme = AppTheme.Dark)
    {
        var app = await AppHarness.OpenAsync(JazzLibrary.Options(Two, theme, lockTimeout));
        await Gui.UntilAsync(() => app.ViewModel.Sidebar.Playlists.Count == 2 && app.ViewModel.AlbumsPage.Albums.Count == 2, "the library");
        Gui.Settle();
        return app;
    }

    private static string[] Show(AppHarness app, string playlist) => app.Read(library => JazzLibrary.Show(library, playlist));

    private static string[] Names(AppHarness app) => app.Read(library => library.Playlists().Select(p => p.Name).ToArray());

    private static NavItemViewModel Nav(AppHarness app, string title) => app.ViewModel.Sidebar.Playlists.Single(p => p.Title == title);

    private static Panel NavPanel(AppHarness app, string title)
        => Gui.All<Panel>(app.Window).Single(p => p.Classes.Contains("navItem") && p.DataContext is NavItemViewModel { Title: var t } && t == title);

    private static TextBox RenameBox(AppHarness app)
        => Gui.All<TextBox>(app.Window).Single(t => t.Classes.Contains("rename") && t.IsEffectivelyVisible);

    private static Button Card(AppHarness app, string title)
        => Gui.All<Button>(app.Window).Single(b => b.Classes.Contains("card") && b.DataContext is AlbumItemViewModel { Title: var t } && t == title);

    private static string[] Labels(AppHarness app) => [.. app.ViewModel.Menu.Entries.Select(e => e.Label)];

    private static Border MenuBox(AppHarness app) => Gui.Find<Border>(app.Window, "MenuBox");

    private static Point MenuAt(AppHarness app) => new(Canvas.GetLeft(MenuBox(app)), Canvas.GetTop(MenuBox(app)));

    /// <summary>Clicks the menu entry labelled <paramref name="label"/>.</summary>
    private static void Choose(AppHarness app, string label)
        => Gui.Click(app.Window, Gui.All<Button>(MenuBox(app)).Single(b => b.DataContext is MenuEntryViewModel { Label: var l } && l == label));

    private static TracksPage PageView(AppHarness app) => Gui.All<TracksPage>(app.Window).Single();

    private static string[] Titles(PlaylistPageViewModel page) => [.. page.Tracks.Select(t => t.Title)];

    // ---- dropping onto a sidebar playlist ---------------------------------------------------------------------

    [AvaloniaFact]
    public async Task An_album_dragged_over_a_sidebar_playlist_lights_it_and_a_drop_adds_its_tracks_at_the_end()
    {
        await using var app = await OpenAsync();
        var mix = Nav(app, "Mix");
        var other = Nav(app, "Other");
        Mouse.StartDrag(app.Window, Card(app, "Blue Train"));

        Mouse.MoveTo(app.Window, Mouse.Middle(app.Window, NavPanel(app, "Other")));
        Assert.True(other.IsDropTarget);
        Assert.False(mix.IsDropTarget);
        Mouse.MoveTo(app.Window, Mouse.Middle(app.Window, NavPanel(app, "Mix")));
        Assert.True(mix.IsDropTarget);
        Assert.False(other.IsDropTarget);
        Assert.Contains("dropTarget", NavPanel(app, "Mix").Classes);
        Assert.False(app.ViewModel.QueueDrawer.IsDragOver);

        // Albums is no playlist: nothing lights up over it
        var albums = Gui.All<Panel>(app.Window).Single(p => p.Classes.Contains("navItem") && p.DataContext == app.ViewModel.Sidebar.Albums);
        Mouse.MoveTo(app.Window, Mouse.Middle(app.Window, albums));
        Assert.All(app.ViewModel.Sidebar.Playlists, p => Assert.False(p.IsDropTarget));

        var at = Mouse.Middle(app.Window, NavPanel(app, "Mix"));
        Mouse.MoveTo(app.Window, at);
        Mouse.Release(app.Window, at);
        Assert.False(mix.IsDropTarget);
        Assert.False(app.ViewModel.QueueDrawer.IsOpen); // it opened for the drag, and nothing went into it
        await Gui.UntilAsync(() => mix.IsFresh, "the playlist flashed");

        Assert.Equal([.. Mix, "Blue Train 1", "Blue Train 2"], Show(app, "Mix"));
        Assert.Equal(["Blue Train 2"], Show(app, "Other"));
        var page = await PlaylistTests.OpenPlaylistAsync(app, "Mix", 6);
        Assert.Equal([.. Mix, "Blue Train 1", "Blue Train 2"], Titles(page));
    }

    [AvaloniaFact]
    public async Task A_track_dropped_on_a_sidebar_playlist_goes_there_and_a_drop_elsewhere_adds_nothing()
    {
        await using var app = await OpenAsync();
        app.ViewModel.Sidebar.NavigateCommand.Execute(app.ViewModel.Sidebar.Tracks);
        await Gui.UntilAsync(() => app.ViewModel.TracksPage.Tracks.Count == 5, "the tracks");
        Gui.Settle();

        // let go over Albums (no playlist): nothing is written, at once
        Mouse.StartDrag(app.Window, PlaylistTests.Row(app, "Kind of Blue 3"));
        var albums = Mouse.Middle(app.Window, Gui.All<Panel>(app.Window).Single(p => p.Classes.Contains("navItem") && p.DataContext == app.ViewModel.Sidebar.Albums));
        Mouse.MoveTo(app.Window, albums);
        Mouse.Release(app.Window, albums);
        Assert.Equal(Mix, Show(app, "Mix"));
        Assert.Equal(["Blue Train 2"], Show(app, "Other"));

        Mouse.StartDrag(app.Window, PlaylistTests.Row(app, "Kind of Blue 3"));
        var at = Mouse.Middle(app.Window, NavPanel(app, "Other"));
        Mouse.MoveTo(app.Window, at);
        Mouse.Release(app.Window, at);
        await Gui.UntilAsync(() => Nav(app, "Other").IsFresh, "the playlist flashed");
        Assert.Equal(["Blue Train 2", "Kind of Blue 3"], Show(app, "Other"));
    }

    // ---- the sidebar playlist's menu --------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Rename_edits_the_name_in_place_and_Enter_saves_it()
    {
        await using var app = await OpenAsync();
        var page = await PlaylistTests.OpenPlaylistAsync(app, "Mix", 4);
        Mouse.RightClick(app.Window, NavPanel(app, "Mix"));
        Assert.Equal(["Play", "Rename", "Delete…"], Labels(app));

        Choose(app, "Rename");
        var box = RenameBox(app);
        await Gui.UntilAsync(() => box.IsFocused, "the name box focused");
        Assert.Equal("Mix", box.Text);
        Assert.Equal(3, Math.Abs(box.SelectionEnd - box.SelectionStart));
        Assert.False(Gui.All<Button>(NavPanel(app, "Mix")).Single(b => b.Classes.Contains("nav")).IsVisible);

        app.Window.KeyTextInput("Evening Jazz");
        Gui.Settle();
        Gui.Press(app.Window, Key.Enter, PhysicalKey.Enter);
        await Gui.UntilAsync(() => app.ViewModel.Sidebar.Playlists.Any(p => p.Title == "Evening Jazz") && !app.ViewModel.Sidebar.Rename.IsOpen, "the rename");

        Assert.Equal(["Evening Jazz", "Other"], app.ViewModel.Sidebar.Playlists.Select(p => p.Title));
        Assert.Equal(["Evening Jazz", "Other"], Names(app));
        Assert.Equal(Mix, Show(app, "Evening Jazz"));
        Assert.Equal("Evening Jazz", Gui.Find<TextBlock>(app.Window, "PageTitle").Text);
        Assert.Same(page, app.ViewModel.CurrentPage);
        Assert.True(Nav(app, "Evening Jazz").IsActive);
    }

    [AvaloniaFact]
    public async Task Esc_or_a_click_elsewhere_cancels_a_rename()
    {
        await using var app = await OpenAsync();
        var rename = app.ViewModel.Sidebar.Rename;

        rename.Start(Nav(app, "Mix"));
        await Gui.UntilAsync(() => RenameBox(app).IsFocused, "the name box focused");
        app.Window.KeyTextInput("Gone");
        Gui.Press(app.Window, Key.Escape, PhysicalKey.Escape);
        Assert.False(rename.IsOpen);
        Assert.False(Nav(app, "Mix").IsRenaming);

        rename.Start(Nav(app, "Mix"));
        await Gui.UntilAsync(() => RenameBox(app).IsFocused, "the name box focused");
        app.Window.KeyTextInput("Gone too");
        var elsewhere = Mouse.Middle(app.Window, Gui.Find<TextBlock>(app.Window, "PageTitle"));
        app.Window.MouseDown(elsewhere, MouseButton.Left);
        app.Window.MouseUp(elsewhere, MouseButton.Left);
        Gui.Settle();
        Assert.False(rename.IsOpen);

        Assert.Equal(["Mix", "Other"], app.ViewModel.Sidebar.Playlists.Select(p => p.Title));
        Assert.Equal(["Mix", "Other"], Names(app));
    }

    [AvaloniaFact]
    public async Task A_name_another_playlist_has_or_no_name_is_refused_and_the_box_stays_open()
    {
        await using var app = await OpenAsync();
        var rename = app.ViewModel.Sidebar.Rename;
        rename.Start(Nav(app, "Mix"));
        await Gui.UntilAsync(() => RenameBox(app).IsFocused, "the name box focused");

        app.Window.KeyTextInput("other");
        Gui.Press(app.Window, Key.Enter, PhysicalKey.Enter);
        await Gui.UntilAsync(() => app.ViewModel.Banner is not null, "the notice");
        Assert.Equal("A playlist named “Other” already exists.", app.ViewModel.Banner);
        Assert.True(rename.IsOpen);
        Assert.True(RenameBox(app).IsFocused);

        app.ViewModel.DismissBannerCommand.Execute(null);
        RenameBox(app).Text = "  ";
        Gui.Press(app.Window, Key.Enter, PhysicalKey.Enter);
        await Gui.UntilAsync(() => app.ViewModel.Banner is not null, "the notice");
        Assert.Equal("A playlist name must not be empty.", app.ViewModel.Banner);
        Assert.True(rename.IsOpen);

        // the same name in another case is a rename
        RenameBox(app).Text = "MIX";
        Gui.Press(app.Window, Key.Enter, PhysicalKey.Enter);
        await Gui.UntilAsync(() => !rename.IsOpen, "the rename");
        Assert.Equal(["MIX", "Other"], Names(app));
    }

    [AvaloniaFact]
    public async Task Delete_asks_first_where_the_menu_was_and_Cancel_keeps_the_playlist()
    {
        await using var app = await OpenAsync();
        await PlaylistTests.OpenPlaylistAsync(app, "Mix", 4);
        Mouse.RightClick(app.Window, NavPanel(app, "Mix"));
        var at = MenuAt(app);

        Choose(app, "Delete…");
        Assert.True(app.ViewModel.Menu.IsOpen);
        Assert.Equal(at, MenuAt(app));
        Assert.Equal("Delete “Mix”? Its tracks stay in the library.", Gui.Find<TextBlock>(app.Window, "MenuCaption").Text);
        Assert.True(Gui.Find<TextBlock>(app.Window, "MenuCaption").IsEffectivelyVisible);
        Assert.Equal(["Delete", "Cancel"], Labels(app));
        Assert.Contains("destructive", Gui.All<Button>(MenuBox(app)).First().Classes);

        Choose(app, "Cancel");
        Assert.False(app.ViewModel.Menu.IsOpen);
        Assert.Equal(["Mix", "Other"], Names(app));

        Mouse.RightClick(app.Window, NavPanel(app, "Mix"));
        Choose(app, "Delete…");
        Choose(app, "Delete");
        await Gui.UntilAsync(() => app.ViewModel.Sidebar.Playlists.Count == 1, "the playlist gone");
        Assert.Equal(["Other"], Names(app));
        Assert.Same(app.ViewModel.AlbumsPage, app.ViewModel.CurrentPage); // its page was open

        // a plain menu has no caption
        Mouse.RightClick(app.Window, NavPanel(app, "Other"));
        Assert.False(Gui.Find<TextBlock>(app.Window, "MenuCaption").IsVisible);
    }

    [AvaloniaFact]
    public async Task Play_in_the_menu_plays_the_playlist_from_its_first_entry()
    {
        await using var app = await OpenAsync();
        Mouse.RightClick(app.Window, NavPanel(app, "Mix"));
        Choose(app, "Play");
        await Gui.UntilAsync(() => app.Scripted!.Count("queue.set") == 1, "queue.set");

        var set = app.Scripted!.Sent("queue.set")[0];
        Assert.Equal(Mix[..3], set.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()));
        Assert.Equal(0, set.GetProperty("start").GetInt32());
    }

    // ---- the playlist page: ✕ and reordering ------------------------------------------------------------------

    [AvaloniaFact]
    public async Task A_rows_cross_removes_it_also_a_missing_file()
    {
        await using var app = await OpenAsync();
        var page = await PlaylistTests.OpenPlaylistAsync(app, "Mix", 4);
        var row = PlaylistTests.Row(app, "Kind of Blue 2");
        var cross = Gui.All<Button>(row).Single(b => b.Classes.Contains("remove"));
        Assert.Equal(0, cross.Opacity);
        Gui.Hover(app.Window, row);
        Assert.Equal(1, cross.Opacity);

        Gui.Click(app.Window, cross);
        await Gui.UntilAsync(() => page.Tracks.Count == 3, "the row gone");
        Assert.Equal(["Kind of Blue 1", "Blue Train 1", "Lost Take"], Titles(page));
        Assert.Equal(["Kind of Blue 1", "Blue Train 1", "Lost Take"], Show(app, "Mix"));

        Gui.Click(app.Window, Gui.All<Button>(PlaylistTests.Row(app, "Lost Take")).Single(b => b.Classes.Contains("remove")));
        await Gui.UntilAsync(() => page.Tracks.Count == 2, "the missing entry gone");
        Assert.Equal(["Kind of Blue 1", "Blue Train 1"], Show(app, "Mix"));
        Assert.Equal("2", page.CountText);
    }

    [AvaloniaFact]
    public async Task A_removal_after_the_playlist_changed_elsewhere_removes_that_track_or_says_it_is_gone()
    {
        await using var app = await OpenAsync();
        var page = await PlaylistTests.OpenPlaylistAsync(app, "Mix", 4);

        // the CLI puts a track first: every entry the page shows is one place further down now
        using (var cli = app.OpenLibrary())
        {
            cli.AddToPlaylist("Mix", [JazzLibrary.Entry(app.TrackPaths, "Kind of Blue", 3)], position: 1);
        }

        Gui.Click(app.Window, Gui.All<Button>(PlaylistTests.Row(app, "Blue Train 1")).Single(b => b.Classes.Contains("remove")));
        await Gui.UntilAsync(() => page.Tracks.Count == 4 && page.Tracks[0].Title == "Kind of Blue 3", "the page read again");
        Assert.Equal(["Kind of Blue 3", "Kind of Blue 1", "Kind of Blue 2", "Lost Take"], Show(app, "Mix"));

        // and the CLI removes the one the page is about to remove
        using (var cli = app.OpenLibrary())
        {
            cli.RemoveFromPlaylist("Mix", "2");
        }

        Gui.Click(app.Window, Gui.All<Button>(PlaylistTests.Row(app, "Kind of Blue 1")).Single(b => b.Classes.Contains("remove")));
        await Gui.UntilAsync(() => app.ViewModel.Banner is not null, "the notice");
        Assert.Equal(LibraryService.EntryGone, app.ViewModel.Banner);
        Assert.Equal(["Kind of Blue 3", "Kind of Blue 2", "Lost Take"], Show(app, "Mix"));
    }

    [AvaloniaFact]
    public async Task Dragging_a_row_shows_the_drop_line_where_it_lands_and_moves_it_there()
    {
        await using var app = await OpenAsync();
        var page = await PlaylistTests.OpenPlaylistAsync(app, "Mix", 4);
        var line = Gui.All<Border>(PageView(app)).Single(b => b.Name == "DropLine");
        Assert.False(line.IsVisible);

        Mouse.StartDrag(app.Window, PlaylistTests.Row(app, "Blue Train 1"));
        Gui.FinishAnimations(Slid); // the queue drawer opened for the drag: the rows' left part stays clear of it
        var target = PlaylistTests.Row(app, "Kind of Blue 2");
        var above = target.TranslatePoint(new Point(60, 4), app.Window)!.Value;
        Mouse.MoveTo(app.Window, above);
        Assert.True(page.IsDragOver);
        Assert.True(line.IsEffectivelyVisible);
        var lineMiddle = line.TranslatePoint(new Point(0, line.Bounds.Height / 2), app.Window)!.Value.Y;
        Assert.Equal(target.TranslatePoint(default, app.Window)!.Value.Y, lineMiddle, 1.5);
        Assert.False(app.ViewModel.QueueDrawer.IsDragOver);

        Mouse.Release(app.Window, above);
        Assert.False(page.IsDragOver);
        await Gui.UntilAsync(() => page.Tracks.Count == 4 && page.Tracks[1].Title == "Blue Train 1", "the move");
        Assert.Equal(["Kind of Blue 1", "Blue Train 1", "Kind of Blue 2", "Lost Take"], Titles(page));
        Assert.Equal(["Kind of Blue 1", "Blue Train 1", "Kind of Blue 2", "Lost Take"], Show(app, "Mix"));
    }

    [AvaloniaFact]
    public async Task A_missing_entry_moves_too_and_a_drop_on_its_own_place_writes_nothing()
    {
        await using var app = await OpenAsync();
        var page = await PlaylistTests.OpenPlaylistAsync(app, "Mix", 4);

        // its own place (just under the row above it): nothing is written, at once
        Mouse.StartDrag(app.Window, PlaylistTests.Row(app, "Lost Take"));
        Assert.False(app.ViewModel.QueueDrawer.IsOpen); // it can't go into the queue
        var own = PlaylistTests.Row(app, "Lost Take").TranslatePoint(new Point(60, 4), app.Window)!.Value;
        Mouse.MoveTo(app.Window, own);
        Mouse.Release(app.Window, own);
        Assert.Equal(Mix, Show(app, "Mix"));

        Mouse.StartDrag(app.Window, PlaylistTests.Row(app, "Lost Take"));
        var top = PlaylistTests.Row(app, "Kind of Blue 1").TranslatePoint(new Point(60, 3), app.Window)!.Value;
        Mouse.MoveTo(app.Window, top);
        Mouse.Release(app.Window, top);
        await Gui.UntilAsync(() => page.Tracks.Count == 4 && page.Tracks[0].Title == "Lost Take", "the move");
        Assert.Equal(["Lost Take", "Kind of Blue 1", "Kind of Blue 2", "Blue Train 1"], Show(app, "Mix"));
    }

    [AvaloniaFact]
    public async Task With_a_search_a_row_lands_next_to_the_row_it_was_dropped_by()
    {
        await using var app = await OpenAsync();
        var page = await PlaylistTests.OpenPlaylistAsync(app, "Mix", 4);
        app.ViewModel.SearchText = "blue";
        Gui.Settle();
        Assert.Equal(Mix[..3], Titles(page));

        // under the last row shown: after Blue Train 1, before the missing file the search hides
        Mouse.StartDrag(app.Window, PlaylistTests.Row(app, "Kind of Blue 1"));
        Gui.FinishAnimations(Slid);
        var last = PlaylistTests.Row(app, "Blue Train 1");
        var below = last.TranslatePoint(new Point(60, last.Bounds.Height - 3), app.Window)!.Value;
        Mouse.MoveTo(app.Window, below);
        Mouse.Release(app.Window, below);
        await Gui.UntilAsync(() => page.Tracks.Count == 3 && page.Tracks[2].Title == "Kind of Blue 1", "the move");
        Assert.Equal(["Kind of Blue 2", "Blue Train 1", "Kind of Blue 1", "Lost Take"], Show(app, "Mix"));
    }

    // ---- Add to playlist › ------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Add_to_playlist_opens_the_playlists_in_the_menus_place_and_adds_to_the_one_chosen()
    {
        await using var app = await OpenAsync();
        Mouse.RightClick(app.Window, Card(app, "Blue Train"));
        Assert.Equal(["Play", "Play next", "Add to queue", "Add to playlist"], Labels(app));
        Assert.True(Gui.All<Control>(MenuBox(app)).Single(c => c.Classes.Contains("submenu") && c.DataContext is MenuEntryViewModel { HasSubmenu: true }).IsVisible);
        var at = MenuAt(app);

        Choose(app, "Add to playlist");
        Assert.True(app.ViewModel.Menu.IsOpen);
        Assert.Equal(at, MenuAt(app));
        Assert.Equal(["Mix", "Other", PlaylistActions.NewPlaylistLabel], Labels(app));

        Choose(app, "Other");
        await Gui.UntilAsync(() => Nav(app, "Other").IsFresh, "the playlist flashed");
        Assert.Equal(["Blue Train 2", "Blue Train 1", "Blue Train 2"], Show(app, "Other"));
    }

    [AvaloniaFact]
    public async Task The_track_menus_offer_Add_to_playlist_too()
    {
        await using var app = await OpenAsync();
        app.ViewModel.Sidebar.NavigateCommand.Execute(app.ViewModel.Sidebar.Tracks);
        await Gui.UntilAsync(() => app.ViewModel.TracksPage.Tracks.Count == 5, "the tracks");
        Gui.Settle();
        var row = PlaylistTests.Row(app, "Kind of Blue 3");
        Gui.Hover(app.Window, row);

        Gui.Click(app.Window, Gui.All<Button>(row).Single(b => b.Classes.Contains("add")));
        Assert.Equal(["Play next", "Add to queue", "Add to playlist"], Labels(app));
        Choose(app, "Add to playlist");
        Choose(app, "Mix");
        await Gui.UntilAsync(() => Show(app, "Mix").Length == 5, "the track added");
        Assert.Equal([.. Mix, "Kind of Blue 3"], Show(app, "Mix"));
    }

    [AvaloniaFact]
    public async Task New_playlist_makes_one_of_the_tracks_and_renames_it_in_place()
    {
        await using var app = await OpenAsync();
        app.Edit(library => library.CreatePlaylist(PlaylistNameViewModel.DefaultName));
        await Gui.UntilAsync(() => app.ViewModel.Sidebar.Playlists.Count == 3, "the third playlist");
        Mouse.RightClick(app.Window, Card(app, "Blue Train"));
        Choose(app, "Add to playlist");
        Choose(app, PlaylistActions.NewPlaylistLabel);

        await Gui.UntilAsync(() => app.ViewModel.Sidebar.Rename.IsOpen && RenameBox(app).IsFocused, "the new playlist renamed in place");
        var created = app.ViewModel.Sidebar.Rename.Item!;
        Assert.Equal("New playlist 2", created.Title);
        Assert.True(created.IsFresh);
        Assert.Equal(["Blue Train 1", "Blue Train 2"], Show(app, "New playlist 2"));

        app.Window.KeyTextInput("Coltrane");
        Gui.Press(app.Window, Key.Enter, PhysicalKey.Enter);
        await Gui.UntilAsync(() => !app.ViewModel.Sidebar.Rename.IsOpen, "the rename");
        Assert.Equal(["Coltrane", "Mix", PlaylistNameViewModel.DefaultName, "Other"], Names(app));
        Assert.Equal(["Blue Train 1", "Blue Train 2"], Show(app, "Coltrane"));
    }

    // ---- the library busy -------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task While_another_process_holds_the_library_an_edit_says_it_is_busy_and_changes_nothing()
    {
        await using var app = await OpenAsync(lockTimeout: TimeSpan.FromMilliseconds(200));
        var mix = Nav(app, "Mix");
        using (app.OpenLibrary())
        {
            Mouse.StartDrag(app.Window, Card(app, "Blue Train"));
            var at = Mouse.Middle(app.Window, NavPanel(app, "Mix"));
            Mouse.MoveTo(app.Window, at);
            Mouse.Release(app.Window, at);
            await Gui.UntilAsync(() => app.ViewModel.Banner is not null, "the notice");
            Assert.Equal(LibraryService.BusyMessage, app.ViewModel.Banner);
            Assert.False(mix.IsFresh);

            app.ViewModel.DismissBannerCommand.Execute(null);
            app.ViewModel.Sidebar.Rename.Start(mix);
            await Gui.UntilAsync(() => RenameBox(app).IsFocused, "the name box focused");
            app.Window.KeyTextInput("Busy");
            Gui.Press(app.Window, Key.Enter, PhysicalKey.Enter);
            await Gui.UntilAsync(() => app.ViewModel.Banner is not null, "the notice");
            Assert.Equal(LibraryService.BusyMessage, app.ViewModel.Banner);
            Assert.True(app.ViewModel.Sidebar.Rename.IsOpen);
        }

        Assert.Equal(Mix, Show(app, "Mix"));
        Assert.Equal(["Mix", "Other"], Names(app));
    }

    // ---- snapshots --------------------------------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Snapshot_of_a_sidebar_playlist_lit_as_a_drop_target(AppTheme theme)
    {
        await using var app = await OpenAsync(theme: theme);
        Mouse.StartDrag(app.Window, Card(app, "Blue Train"));
        var at = Mouse.Middle(app.Window, NavPanel(app, "Mix"));
        Mouse.MoveTo(app.Window, at);
        Gui.FinishAnimations(Slid);

        Snapshots.Match(app.Window, Gui.Find<SidebarView>(app.Window, "SidebarView"), $"sidebar-drop-{theme.ToString().ToLowerInvariant()}");
        Mouse.Release(app.Window, Mouse.Middle(app.Window, Gui.Find<TextBlock>(app.Window, "PageTitle")));
    }

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Snapshot_of_a_playlist_renamed_in_place(AppTheme theme)
    {
        await using var app = await OpenAsync(theme: theme);
        app.ViewModel.Sidebar.Rename.Start(Nav(app, "Mix"));
        var box = RenameBox(app);
        await Gui.UntilAsync(() => box.IsFocused, "the name box focused");
        box.CaretBrush = Brushes.Transparent; // the caret blinks on a timer of its own
        Gui.Settle();

        Snapshots.Match(app.Window, Gui.Find<SidebarView>(app.Window, "SidebarView"), $"sidebar-rename-{theme.ToString().ToLowerInvariant()}");
    }
}
