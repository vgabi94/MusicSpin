using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using MusicSpin.App.Controls;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;
using MusicSpin.App.Views;
using MusicSpin.Core.Library;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>The queue drawer (#20), in the window.</summary>
public sealed class QueueDrawerTests
{
    /// <summary>The drawer sliding in (0.45 s).</summary>
    private const int Slid = 500;

    /// <summary>A fresh row's flash (1.6 s).</summary>
    private const int Flashed = 1700;

    private static async Task<AppHarness> OpenAsync(AppTheme theme = AppTheme.Dark)
    {
        var app = await AppHarness.OpenAsync(new AppOptions
        {
            Scripted = true, Library = SampleLibrary.Create(), Covers = SampleLibrary.Covers(accents: true), Theme = theme,
        });
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 10, "the albums");
        return app;
    }

    /// <summary>
    /// Night Transit's first track playing, the <paramref name="upNext"/> tracks after it up next, then the rest of the
    /// album; <paramref name="radio"/> puts a station on air, whose picks follow.
    /// </summary>
    private static async Task QueueAsync(AppHarness app, IReadOnlyList<QueueItem> upNext, RadioFilter? radio = null, IReadOnlyList<QueueItem>? after = null)
    {
        IReadOnlyList<QueueItem> rest = after ?? [SampleLibrary.Item(6, 2), SampleLibrary.Item(6, 3)];
        app.Scripted!.SetQueue([SampleLibrary.Item(6, 1), .. upNext, .. rest], 0, upNext.Count, radio);
        await app.Scripted.PushAsync("queueChanged");
        await app.Scripted.PushAsync("stateChanged");
        var drawer = app.ViewModel.QueueDrawer;
        await Gui.UntilAsync(() => drawer.Rows.Count == upNext.Count && drawer.IsRadio == (radio is not null)
                                   && drawer.Picks.Count == (radio is null ? 0 : Math.Min(rest.Count, QueueDrawerViewModel.RadioPicks)), "the queue read");
    }

    /// <summary>Albums 2, 5 and 10 have no cover (they show their placeholder).</summary>
    private static bool IsPlaceholderAlbum(QueueRowViewModel row) => row.Item.Album is "Field Notes" or "Glass Garden" or "Untitled Tape";

    private static IReadOnlyList<QueueItem> Tracks(params (int Album, int Track)[] tracks) => [.. tracks.Select(t => SampleLibrary.Item(t.Album, t.Track))];

    private static readonly IReadOnlyList<QueueItem> Nine = Tracks((7, 1), (7, 2), (7, 3), (8, 1), (8, 2), (8, 3), (9, 1), (9, 2), (9, 3));

    private static T Find<T>(AppHarness app, string name)
        where T : Control
        => Gui.Find<T>(app.Window, name);

    /// <summary>Opens the drawer and waits for its covers.</summary>
    private static async Task OpenDrawerAsync(AppHarness app)
    {
        var drawer = app.ViewModel.QueueDrawer;
        drawer.Open();
        Gui.Settle();
        await Gui.UntilAsync(() => drawer.Rows.Concat(drawer.Picks).All(r => IsPlaceholderAlbum(r) || r.Art?.Cover is not null), "the covers");
        Gui.FinishAnimations(Flashed);
    }

    private static async Task<System.Text.Json.JsonElement> SentAsync(AppHarness app, string cmd, int count = 1)
    {
        await Gui.UntilAsync(() => app.Scripted!.Count(cmd) >= count, $"'{cmd}' sent");
        return app.Scripted!.Sent(cmd)[count - 1];
    }

    private static Control RowAt(AppHarness app, int index)
        => (Control)Find<ItemsControl>(app, "QueueRows").ContainerFromIndex(index)!;

    private static T In<T>(Control row, string classes)
        where T : Control
        => Gui.All<T>(row).First(c => c.Classes.Contains(classes));

    // ---- opening and closing ----------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task The_queue_button_opens_and_closes_the_drawer_and_the_close_button_and_Esc_close_it()
    {
        await using var app = await OpenAsync();
        var layer = Find<SlidePanel>(app, "QueueLayer");
        var button = Find<Button>(app, "QueueButton");
        Assert.False(layer.IsVisible);

        Gui.Click(app.Window, button);
        Gui.FinishAnimations(Slid);
        Assert.True(layer.IsVisible);
        Assert.Contains("on", button.Classes);
        Assert.Equal(Gui.BoundsIn(app.Window, app.Window.FindControl<Grid>("ShellGrid")!).Right, Gui.BoundsIn(app.Window, layer).Right);

        Gui.Click(app.Window, button);
        Gui.FinishAnimations(Slid);
        Assert.False(layer.IsVisible);

        app.ViewModel.QueueDrawer.Open();
        Gui.FinishAnimations(Slid);
        Gui.Click(app.Window, Find<Button>(app, "CloseQueue"));
        Gui.FinishAnimations(Slid);
        Assert.False(app.ViewModel.QueueDrawer.IsOpen);

        app.ViewModel.QueueDrawer.Open();
        Gui.FinishAnimations(Slid);
        Gui.Press(app.Window, Key.Escape, PhysicalKey.Escape);
        Gui.FinishAnimations(Slid);
        Assert.False(app.ViewModel.QueueDrawer.IsOpen);
        Assert.False(layer.IsVisible);
    }

    [AvaloniaTheory]
    [InlineData(MotionMode.Smooth, 450)]
    [InlineData(MotionMode.Fast, 225)]
    [InlineData(MotionMode.None, 0)]
    public async Task The_drawer_slides_in_from_the_right_in_its_time_at_each_Motion(MotionMode motion, int ms)
    {
        await using var app = await OpenAsync();
        try
        {
            app.ViewModel.Settings.SetMotion(motion);
            var layer = Find<SlidePanel>(app, "QueueLayer");
            app.ViewModel.QueueDrawer.Open();
            Gui.Settle();
            if (ms > 0)
            {
                Gui.FinishAnimations(ms - 50);
                Assert.InRange(layer.Shown, 0.01, 0.9999);
                Assert.True(((TranslateTransform)layer.RenderTransform!).X > 0, "still right of its place");
            }

            Gui.FinishAnimations(60);
            Assert.Equal(1, layer.Shown);
            Assert.Equal(0, ((TranslateTransform)layer.RenderTransform!).X);
        }
        finally
        {
            app.ViewModel.Settings.SetMotion(MotionMode.Smooth);
        }
    }

    [AvaloniaFact]
    public async Task The_drawer_lies_over_Now_Playing_and_under_the_player_bar()
    {
        await using var app = await OpenAsync();
        var grid = app.Window.FindControl<Grid>("ShellGrid")!;
        var order = grid.Children.ToList();
        Assert.True(order.IndexOf(Find<SlidePanel>(app, "NowPlayingLayer")) < order.IndexOf(Find<SlidePanel>(app, "QueueLayer")));
        Assert.True(order.IndexOf(Find<SlidePanel>(app, "QueueLayer")) < order.IndexOf(Find<Control>(app, "PlayerBarView")));
    }

    // ---- the rows ---------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task The_rows_show_each_track_and_the_header_sums_them_up()
    {
        await using var app = await OpenAsync();
        await QueueAsync(app, Tracks((7, 1), (8, 2)));
        await OpenDrawerAsync(app);

        Assert.Equal("2 tracks · 7 min", Find<TextBlock>(app, "QueueSummary").Text);
        var first = RowAt(app, 0);
        Assert.Equal(["The Hollow", "Ana Cruz Quintet", "3:19"], new[] { "title", "artist", "duration" }.Select(c => In<TextBlock>(first, c).Text));
        Assert.True(Find<Button>(app, "ClearQueue").IsVisible);
        Assert.False(Find<Panel>(app, "QueueEmpty").IsVisible);
        Assert.True(Find<Border>(app, "QueueFooter").IsVisible);
        Assert.Equal("2", Find<Border>(app, "QueueCount").FindDescendantOfType<TextBlock>()?.Text);
    }

    [AvaloniaFact]
    public async Task Empty_it_shows_the_drop_box_and_no_Clear_or_footer()
    {
        await using var app = await OpenAsync();
        await QueueAsync(app, []);
        await OpenDrawerAsync(app);
        Assert.True(Find<Panel>(app, "QueueEmpty").IsVisible);
        Assert.False(Find<Button>(app, "ClearQueue").IsVisible);
        Assert.False(Find<Border>(app, "QueueFooter").IsVisible);
        Assert.False(Find<Border>(app, "QueueCount").IsVisible);
        Assert.Null(Find<TextBlock>(app, "QueueSummary").Text);
    }

    [AvaloniaFact]
    public async Task Clicking_a_row_skips_to_it_and_its_cross_removes_it()
    {
        await using var app = await OpenAsync();
        await QueueAsync(app, Tracks((7, 1), (7, 2), (7, 3)));
        await OpenDrawerAsync(app);

        var remove = In<Button>(RowAt(app, 1), "queueRemove");
        Assert.Equal(0, remove.Opacity);
        Gui.Hover(app.Window, RowAt(app, 1));
        Assert.Equal(1, remove.Opacity);
        Gui.Click(app.Window, remove);
        Assert.Equal(2, (await SentAsync(app, "queue.remove")).GetProperty("index").GetInt32());
        Assert.Equal(0, app.Scripted!.Count("play"));

        Gui.Click(app.Window, In<Button>(RowAt(app, 2), "queueEntry"));
        Assert.Equal(3, (await SentAsync(app, "play")).GetProperty("index").GetInt32());
    }

    [AvaloniaFact]
    public async Task Play_starts_the_top_row_and_Clear_drops_the_rows()
    {
        await using var app = await OpenAsync();
        await QueueAsync(app, Tracks((7, 1), (7, 2)));
        await OpenDrawerAsync(app);

        Gui.Click(app.Window, Find<Button>(app, "PlayQueue"));
        Assert.Equal(1, (await SentAsync(app, "play")).GetProperty("index").GetInt32());

        Gui.Click(app.Window, Find<Button>(app, "ClearQueue"));
        Assert.True((await SentAsync(app, "queue.clear")).GetProperty("upNext").GetBoolean());
    }

    [AvaloniaFact]
    public async Task Rows_added_while_it_is_open_flash_and_the_ones_already_there_do_not()
    {
        await using var app = await OpenAsync();
        await QueueAsync(app, Tracks((7, 1)));
        await OpenDrawerAsync(app);
        // it came after the app's first read of the queue: it flashed then
        Assert.Equal(1, Animate.GetPlayed(In<Border>(RowAt(app, 0), "flash")));

        await QueueAsync(app, Tracks((7, 1), (8, 1), (8, 2)));
        Gui.Settle();
        Assert.Equal([0, 1, 1], Enumerable.Range(0, 3).Select(i => Animate.GetPlayed(In<Border>(RowAt(app, i), "flash"))));
        Gui.FinishAnimations(Flashed);
        Assert.Equal(0, In<Border>(RowAt(app, 1), "flash").Opacity);
    }

    [AvaloniaFact]
    public async Task The_first_row_added_scrolls_into_view()
    {
        await using var app = await OpenAsync();
        IReadOnlyList<QueueItem> many = [.. Enumerable.Range(1, 10).SelectMany(a => Enumerable.Range(1, 3).Select(t => SampleLibrary.Item(a, t)))];
        await QueueAsync(app, [.. many.Take(20)]);
        await OpenDrawerAsync(app);
        var scroll = Find<ScrollViewer>(app, "QueueScroll");
        Assert.Equal(0, scroll.Offset.Y);

        await QueueAsync(app, many);
        Gui.Settle();
        var added = Gui.BoundsIn(app.Window, RowAt(app, 20));
        var view = Gui.BoundsIn(app.Window, scroll);
        Assert.True(scroll.Offset.Y > 0, "scrolled");
        Assert.True(added.Top >= view.Top && added.Bottom <= view.Bottom, $"row 21 at {added} is inside {view}");
    }

    // ---- radio ------------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task On_radio_the_picks_follow_dimmed_and_a_click_skips_ahead_and_End_radio_keeps_the_rows()
    {
        await using var app = await OpenAsync();
        await QueueAsync(app, Tracks((7, 1)), radio: new RadioFilter { Genres = ["Jazz"] }, after: Tracks((1, 1), (3, 2)));
        await OpenDrawerAsync(app);

        Assert.True(Find<StackPanel>(app, "RadioSection").IsVisible);
        Assert.Equal("Jazz", Find<TextBlock>(app, "RadioDescription").Text);
        var picks = Find<ItemsControl>(app, "RadioPicks");
        var pick = (Control)picks.ContainerFromIndex(1)!;
        Assert.True(In<Panel>(pick, "queueRow").Opacity < 1);
        Gui.Click(app.Window, In<Button>(pick, "queueEntry"));
        Assert.Equal(3, (await SentAsync(app, "play")).GetProperty("index").GetInt32());

        Gui.Click(app.Window, Find<Button>(app, "EndRadio"));
        var set = await SentAsync(app, "queue.set");
        Assert.Equal(["The Thread", "The Hollow"], set.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()));
    }

    [AvaloniaFact]
    public async Task Off_radio_there_is_no_radio_section()
    {
        await using var app = await OpenAsync();
        await QueueAsync(app, Tracks((7, 1)));
        await OpenDrawerAsync(app);
        Assert.False(Find<StackPanel>(app, "RadioSection").IsVisible);
    }

    // ---- Save as playlist ---------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Save_as_playlist_takes_a_name_and_Enter_saves_it_to_the_sidebar()
    {
        await using var app = await OpenAsync();
        await QueueAsync(app, Tracks((7, 1), (8, 2)));
        await OpenDrawerAsync(app);

        Gui.Click(app.Window, Find<Button>(app, "SaveQueue"));
        await Gui.UntilAsync(() => Find<TextBox>(app, "PlaylistName").IsFocused, "the name field focused");
        var name = Find<TextBox>(app, "PlaylistName");
        Assert.True(Find<Grid>(app, "SaveForm").IsVisible);
        Assert.False(Find<Grid>(app, "QueueActions").IsVisible);
        Assert.Equal("New playlist", name.Text);
        Assert.Equal(name.Text!.Length, Math.Abs(name.SelectionEnd - name.SelectionStart));

        app.Window.KeyTextInput("Late night");
        Gui.Settle();
        Gui.Press(app.Window, Key.Enter, PhysicalKey.Enter);
        await Gui.UntilAsync(() => app.ViewModel.Sidebar.Playlists.Any(p => p.Title == "Late night"), "the playlist in the sidebar");
        Assert.False(Find<Grid>(app, "SaveForm").IsVisible);
        Assert.True(app.ViewModel.QueueDrawer.IsOpen);
    }

    [AvaloniaFact]
    public async Task Esc_cancels_the_name_first_and_closes_the_drawer_after()
    {
        await using var app = await OpenAsync();
        await QueueAsync(app, Tracks((7, 1)));
        await OpenDrawerAsync(app);
        Gui.Click(app.Window, Find<Button>(app, "SaveQueue"));
        await Gui.UntilAsync(() => Find<TextBox>(app, "PlaylistName").IsFocused, "the name field focused");

        Gui.Press(app.Window, Key.Escape, PhysicalKey.Escape);
        Assert.False(app.ViewModel.QueueDrawer.IsSaving);
        Assert.True(app.ViewModel.QueueDrawer.IsOpen);
        Assert.True(Find<Grid>(app, "QueueActions").IsVisible);

        Gui.Press(app.Window, Key.Escape, PhysicalKey.Escape);
        Assert.False(app.ViewModel.QueueDrawer.IsOpen);
        Assert.Empty(app.ViewModel.Sidebar.Playlists);
    }

    [AvaloniaFact]
    public async Task Cancel_closes_the_name_without_saving()
    {
        await using var app = await OpenAsync();
        await QueueAsync(app, Tracks((7, 1)));
        await OpenDrawerAsync(app);
        Gui.Click(app.Window, Find<Button>(app, "SaveQueue"));
        Gui.Click(app.Window, Find<Button>(app, "CancelSave"));
        Assert.False(app.ViewModel.QueueDrawer.IsSaving);
        Assert.Empty(((StubLibrary)app.Options.Library!).Playlists);
    }

    [AvaloniaFact]
    public async Task A_name_already_taken_shows_a_notice_and_keeps_the_field()
    {
        var library = SampleLibrary.Create();
        library.Playlists.Add(new App.Services.PlaylistSummaryItem("p1", "Mine", 1));
        await using var app = await AppHarness.OpenAsync(new AppOptions { Scripted = true, Library = library, Covers = SampleLibrary.Covers() });
        await Gui.UntilAsync(() => app.ViewModel.Sidebar.Playlists.Count == 1, "the playlists");
        await QueueAsync(app, Tracks((7, 1)));
        await OpenDrawerAsync(app);
        Gui.Click(app.Window, Find<Button>(app, "SaveQueue"));
        await Gui.UntilAsync(() => Find<TextBox>(app, "PlaylistName").IsFocused, "the name field focused");
        app.Window.KeyTextInput("mine");
        Gui.Click(app.Window, Find<Button>(app, "SavePlaylist"));

        await Gui.UntilAsync(() => app.ViewModel.Banner is not null, "the notice");
        Assert.Contains("already exists", app.ViewModel.Banner, StringComparison.Ordinal);
        Assert.True(Find<Grid>(app, "SaveForm").IsVisible);
        Assert.Single(library.Playlists);
    }

    [AvaloniaFact]
    public async Task The_name_field_takes_at_most_forty_characters()
    {
        await using var app = await OpenAsync();
        await OpenDrawerAsync(app);
        Assert.Equal(PlaylistNameViewModel.MaxLength, Find<TextBox>(app, "PlaylistName").MaxLength);
    }

    // ---- snapshots --------------------------------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Snapshot_of_the_empty_drawer(AppTheme theme)
    {
        await using var app = await OpenAsync(theme);
        await QueueAsync(app, []);
        await OpenDrawerAsync(app);
        Snapshots.Match(app.Window, $"queue-empty-{theme.ToString().ToLowerInvariant()}");
    }

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Snapshot_of_the_drawer_with_tracks_up_next(AppTheme theme)
    {
        await using var app = await OpenAsync(theme);
        await QueueAsync(app, Nine);
        await OpenDrawerAsync(app);
        Snapshots.Match(app.Window, $"queue-{theme.ToString().ToLowerInvariant()}");
    }

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Snapshot_of_the_drawer_on_radio(AppTheme theme)
    {
        await using var app = await OpenAsync(theme);
        await QueueAsync(app, Nine, radio: new RadioFilter(), after: Tracks((1, 1), (3, 2), (4, 3), (5, 1)));
        await OpenDrawerAsync(app);
        Snapshots.Match(app.Window, $"queue-radio-{theme.ToString().ToLowerInvariant()}");
    }
}
