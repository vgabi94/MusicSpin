using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using MusicSpin.App.Controls;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;
using MusicSpin.App.Views;
using MusicSpin.Core.Playback;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>Right-click menus and dragging albums and tracks into the queue (#21), in the window.</summary>
public sealed class MenusAndDragTests
{
    /// <summary>The queue drawer sliding in (0.45 s).</summary>
    private const int Slid = 500;

    /// <summary>The slowest Now Playing transition (0.85 s).</summary>
    private const int Settled = 900;

    public enum Kind
    {
        Card,
        Sleeve,
        CrateRecord,
        TrackRow,
        AlbumHeader,
        NowPlayingTrack,
    }

    private static async Task<AppHarness> OpenAsync(AppTheme theme = AppTheme.Dark)
    {
        var app = await AppHarness.OpenAsync(new AppOptions
        {
            Scripted = true, Library = SampleLibrary.Create(), Covers = SampleLibrary.Covers(), Theme = theme,
        });
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 10, "the albums");
        return app;
    }

    /// <summary>Shows an item of <paramref name="kind"/> and returns it (the control right-clicked or dragged).</summary>
    private static async Task<Control> ShowAsync(AppHarness app, Kind kind)
    {
        var page = app.ViewModel.AlbumsPage;
        switch (kind)
        {
            case Kind.Card:
                break;
            case Kind.Sleeve:
                page.ViewChoice = page.Views[1];
                break;
            case Kind.CrateRecord:
                page.ViewChoice = page.Views[2];
                break;
            case Kind.TrackRow:
            case Kind.AlbumHeader:
                app.ViewModel.Sidebar.NavigateCommand.Execute(app.ViewModel.Sidebar.Tracks);
                await Gui.UntilAsync(() => app.ViewModel.TracksPage.Tracks.Count == 30, "the tracks");
                break;
            case Kind.NowPlayingTrack:
                app.Scripted!.Status = ScriptedPlayer.Playing("The Thread", trackId: SampleLibrary.TrackId(6, 1));
                await app.Scripted.PushAsync("stateChanged");
                await Gui.UntilAsync(() => app.ViewModel.NowPlaying.Tracks.Count == 3, "Night Transit loaded");
                app.ViewModel.NowPlaying.Open();
                break;
        }

        Gui.Settle();
        Gui.FinishAnimations(Settled);
        var window = app.Window;
        return kind switch
        {
            Kind.Card => Gui.All<Button>(window).First(b => b.Classes.Contains("card") && b.DataContext is AlbumItemViewModel { Title: "Northern Lines" }),
            Kind.Sleeve => Gui.All<ShelfSleeve>(window).First(s => s.DataContext is AlbumItemViewModel { Title: "Northern Lines" }),
            Kind.CrateRecord => Gui.All<Button>(window).First(b => b.Classes.Contains("crateRecord") && b.DataContext is CrateRecordViewModel { IsFront: true }),
            Kind.TrackRow => Gui.All<Border>(window).First(b => b.Classes.Contains("track") && b.DataContext is TrackRow { Item.Title: "The Paper" }),
            Kind.AlbumHeader => Gui.All<Border>(window).First(b => b.Classes.Contains("albumHeader")),
            _ => Gui.All<Button>(window).First(b => b.Classes.Contains("npTrack") && b.DataContext is NowPlayingTrackViewModel { Index: 1 }),
        };
    }

    /// <summary>The track ids an item stands for, and the ghost's two lines.</summary>
    private static (string[] Ids, string Title, string Detail) Expected(Control item) => item.DataContext switch
    {
        AlbumItemViewModel album => (AlbumTracks(album.Album.Id), album.Title, "3 tracks"),
        CrateRecordViewModel record => (AlbumTracks(record.Album.Album.Id), record.Album.Title, "3 tracks"),
        TrackAlbumHeaderViewModel header => (AlbumTracks(header.Album.Album.Id), header.Album.Title, "3 tracks"),
        TrackRow row => ([row.Item.Track.Id], row.Item.Title, row.Item.Artist),
        NowPlayingTrackViewModel track => ([track.Track.Id], track.Title, track.Track.Artist),
        var other => throw new InvalidOperationException($"No expectation for {other}."),
    };

    private static string[] AlbumTracks(string albumId) => [.. Enumerable.Range(1, 3).Select(t => $"{albumId}-{t}")];

    private static Point Middle(Window window, Control control)
        => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    /// <summary>
    /// The middle of <paramref name="item"/> once the pointer rests on it: a shelf sleeve turns to face the pointer and
    /// its slot widens, so where it is depends on the hover.
    /// </summary>
    private static Point Aim(Window window, Control item)
    {
        // From nowhere: a sleeve the pointer was over (under a menu entry just clicked, say) closes first, so it can't
        // still be turning over this one.
        window.MouseMove(new Point(1, 1));
        Gui.Settle();
        Gui.FinishAnimations(Settled);
        window.MouseMove(Middle(window, item));
        Gui.Settle();
        Gui.FinishAnimations(Settled);
        return Middle(window, item);
    }

    private static void RightClick(Window window, Point at)
    {
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Right);
        window.MouseUp(at, MouseButton.Right);
        Gui.Settle();
    }

    private static Border MenuBox(AppHarness app) => Gui.Find<Border>(app.Window, "MenuBox");

    private static List<Button> Entries(AppHarness app) => [.. Gui.All<Button>(MenuBox(app))];

    private static string[] Labels(AppHarness app) => [.. Entries(app).Select(e => e.FindDescendantOfType<TextBlock>()?.Text ?? string.Empty)];

    private static async Task<JsonElement> SentAsync(AppHarness app, string cmd, int count = 1)
    {
        await Gui.UntilAsync(() => app.Scripted!.Count(cmd) >= count, $"'{cmd}' sent");
        return app.Scripted!.Sent(cmd)[count - 1];
    }

    /// <summary>Holds the player's answers to <paramref name="cmd"/>; disposing lets them go (a failed assert can't hang the app's close).</summary>
    private sealed class Held(TaskCompletionSource gate) : IDisposable
    {
        public void Release() => gate.TrySetResult();

        public void Dispose() => Release();
    }

    private static Held Hold(AppHarness app, string cmd) => new(app.Scripted!.Hold(cmd));

    /// <summary>Waits for every menu choice made to finish (the player's answer and what follows it).</summary>
    private static Task ChosenAsync(AppHarness app)
        => Gui.UntilAsync(() => !app.ViewModel.Menu.ChooseCommand.IsRunning, "the menu choices done");

    private static string[] TrackIds(JsonElement request) => [.. request.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("trackId").GetString() ?? string.Empty)];

    /// <summary>Within half a pixel each way (layout rounds positions to whole pixels).</summary>
    private static void AssertNear(Point expected, Point actual)
    {
        Assert.Equal(expected.X, actual.X, 0.51);
        Assert.Equal(expected.Y, actual.Y, 0.51);
    }

    private static DragGhostView Ghost(AppHarness app)
        => OverlayLayer.GetOverlayLayer(app.Window)!.Children.OfType<DragGhostView>().Single();

    /// <summary>Presses on <paramref name="item"/> and moves past the drag threshold; returns where the pointer is.</summary>
    private static Point StartDrag(Window window, Control item)
    {
        var at = Aim(window, item);
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        at += new Point(6, 3);
        window.MouseMove(at, RawInputModifiers.LeftMouseButton);
        Gui.Settle();
        return at;
    }

    private static void MoveTo(Window window, Point at)
    {
        window.MouseMove(at, RawInputModifiers.LeftMouseButton);
        Gui.Settle();
    }

    private static void Release(Window window, Point at)
    {
        window.MouseUp(at, MouseButton.Left);
        Gui.Settle();
    }

    private static Point DrawerMiddle(AppHarness app) => Middle(app.Window, Gui.Find<QueueDrawerView>(app.Window, "QueueDrawerView"));

    // ---- right-click menus ------------------------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(Kind.Card)]
    [InlineData(Kind.Sleeve)]
    [InlineData(Kind.CrateRecord)]
    [InlineData(Kind.TrackRow)]
    [InlineData(Kind.AlbumHeader)]
    [InlineData(Kind.NowPlayingTrack)]
    public async Task Right_clicking_an_item_offers_Play_Play_next_and_Add_to_queue_at_the_pointer(Kind kind)
    {
        await using var app = await OpenAsync();
        var item = await ShowAsync(app, kind);
        var expected = Expected(item);
        var at = Aim(app.Window, item);

        RightClick(app.Window, at);
        Assert.True(MenuBox(app).IsEffectivelyVisible);
        // a single track can also be a favorite (an album can't: it is never one favorite)
        string[] queue = ["Play", "Play next", "Add to queue", "Add to playlist"];
        Assert.Equal(kind is Kind.TrackRow or Kind.NowPlayingTrack ? [.. queue, FavoritesViewModel.AddLabel] : queue, Labels(app));
        var menu = Gui.BoundsIn(app.Window, MenuBox(app));
        Assert.Equal(at.X, menu.Left, 0.5);
        Assert.Equal(at.Y, menu.Top, 0.5);

        Gui.Click(app.Window, Entries(app)[1]);
        var next = await SentAsync(app, "queue.add");
        Assert.Equal("next", next.GetProperty("position").GetString());
        Assert.Equal(expected.Ids, TrackIds(next));
        await ChosenAsync(app); // the choice done (the answer and the status read after it), not only its request sent

        RightClick(app.Window, Aim(app.Window, item));
        Gui.Click(app.Window, Entries(app)[2]);
        var last = await SentAsync(app, "queue.add", 2);
        Assert.Equal("upnext", last.GetProperty("position").GetString());
        Assert.Equal(expected.Ids, TrackIds(last));
        await ChosenAsync(app);
    }

    [AvaloniaTheory]
    [InlineData(Kind.Card)]
    [InlineData(Kind.TrackRow)]
    [InlineData(Kind.NowPlayingTrack)]
    public async Task A_menu_opened_while_a_choice_waits_on_the_player_works_and_the_choices_go_in_order(Kind kind)
    {
        await using var app = await OpenAsync();
        var item = await ShowAsync(app, kind);
        var expected = Expected(item);
        using var gate = Hold(app, "queue.add");

        RightClick(app.Window, Aim(app.Window, item));
        Gui.Click(app.Window, Entries(app)[1]); // Play next: its answer held
        await SentAsync(app, "queue.add");
        Assert.True(app.ViewModel.Menu.ChooseCommand.IsRunning);

        // Another menu, the choice still waiting: every entry enabled, and choosing one works.
        RightClick(app.Window, Aim(app.Window, item));
        Assert.True(MenuBox(app).IsEffectivelyVisible);
        Assert.All(Entries(app), e => Assert.True(e.IsEffectivelyEnabled, $"'{e.FindDescendantOfType<TextBlock>()?.Text}' disabled"));
        Gui.Click(app.Window, Entries(app)[2]); // Add to queue
        Assert.False(MenuBox(app).IsEffectivelyVisible);

        gate.Release();
        var last = await SentAsync(app, "queue.add", 2);
        await ChosenAsync(app);
        // in the order chosen
        Assert.Equal(["next", "upnext"], app.Scripted!.Sent("queue.add").Select(r => r.GetProperty("position").GetString()));
        Assert.Equal(expected.Ids, TrackIds(last));
        Assert.Null(app.ViewModel.Banner);
    }

    [AvaloniaFact]
    public async Task The_Sort_and_Group_dropdowns_work_while_a_choice_waits_on_the_player()
    {
        await using var app = await OpenAsync();
        var item = await ShowAsync(app, Kind.Card);
        using var gate = Hold(app, "queue.add");
        RightClick(app.Window, Aim(app.Window, item));
        Gui.Click(app.Window, Entries(app)[1]);
        await SentAsync(app, "queue.add");

        Gui.Click(app.Window, Gui.Find<Button>(app.Window, "SortButton"));
        Assert.All(Entries(app), e => Assert.True(e.IsEffectivelyEnabled));
        Gui.Click(app.Window, Entries(app)[1]); // Title
        Assert.Equal(LibrarySort.Title, app.ViewModel.AlbumsPage.Sort);

        Gui.Click(app.Window, Gui.Find<Button>(app.Window, "GroupButton"));
        Assert.True(MenuBox(app).IsEffectivelyVisible);
        Assert.All(Entries(app), e => Assert.True(e.IsEffectivelyEnabled));

        gate.Release();
        await ChosenAsync(app);
    }

    [AvaloniaFact]
    public async Task A_choice_that_fails_while_another_menu_is_open_leaves_that_menu_working()
    {
        await using var app = await OpenAsync();
        var item = await ShowAsync(app, Kind.TrackRow);
        var expected = Expected(item);
        app.Scripted!.Fail("queue.add", 1, "The queue is full.");
        using var gate = Hold(app, "queue.add");

        RightClick(app.Window, Aim(app.Window, item));
        Gui.Click(app.Window, Entries(app)[1]); // Play next: it will fail
        await SentAsync(app, "queue.add");

        RightClick(app.Window, Aim(app.Window, item));
        gate.Release(); // the failure arrives with this menu open
        await ChosenAsync(app);
        await Gui.UntilAsync(() => app.ViewModel.Banner == "The queue is full.", "the failure's notice");

        // the menu is still open, its entries enabled, and the next choice goes through
        Assert.True(MenuBox(app).IsEffectivelyVisible);
        Assert.All(Entries(app), e => Assert.True(e.IsEffectivelyEnabled));
        app.Scripted!.Succeed("queue.add");
        Gui.Click(app.Window, Entries(app)[2]);
        var last = await SentAsync(app, "queue.add", 2);
        await ChosenAsync(app);
        Assert.Equal("upnext", last.GetProperty("position").GetString());
        Assert.Equal(expected.Ids, TrackIds(last));
        Assert.Equal(2, app.Scripted.Count("queue.add")); // the failed one isn't sent again
    }

    [AvaloniaTheory]
    [InlineData(Kind.Card)]
    [InlineData(Kind.Sleeve)]
    [InlineData(Kind.CrateRecord)]
    [InlineData(Kind.TrackRow)]
    [InlineData(Kind.AlbumHeader)]
    [InlineData(Kind.NowPlayingTrack)]
    public async Task Play_in_the_menu_plays_the_item(Kind kind)
    {
        await using var app = await OpenAsync();
        try
        {
            app.ViewModel.Settings.SetMotion(MotionMode.None); // the album plays without the turntable scene
            var item = await ShowAsync(app, kind);
            var expected = Expected(item);
            RightClick(app.Window, Aim(app.Window, item));
            Gui.Click(app.Window, Entries(app)[0]);

            var set = await SentAsync(app, "queue.set");
            var start = set.TryGetProperty("start", out var s) ? s.GetInt32() : 0;
            Assert.Equal(expected.Ids[0], TrackIds(set)[start]);
            Assert.True(set.GetProperty("play").GetBoolean());
        }
        finally
        {
            app.ViewModel.Settings.SetMotion(MotionMode.Smooth);
        }
    }

    [AvaloniaFact]
    public async Task Right_clicking_what_is_not_an_album_or_a_track_opens_nothing()
    {
        await using var app = await OpenAsync();
        RightClick(app.Window, Middle(app.Window, Gui.Find<TextBlock>(app.Window, "PageTitle")));
        Assert.False(app.ViewModel.Menu.IsOpen);

        await QueueRowsAsync(app, 2);
        app.ViewModel.QueueDrawer.Open();
        Gui.FinishAnimations(Slid);
        RightClick(app.Window, Middle(app.Window, QueueRow(app, 0)));
        Assert.False(app.ViewModel.Menu.IsOpen);
    }

    [AvaloniaFact]
    public async Task A_playlists_plus_Queue_offers_Play_next_and_Add_to_queue_for_the_whole_list()
    {
        var library = SampleLibrary.Create();
        library.Playlists.Add(new App.Services.PlaylistSummaryItem("p1", "Late night", 2));
        library.PlaylistEntries["p1"] = [library.Tracks[4], library.Tracks[7]];
        await using var app = await AppHarness.OpenAsync(new AppOptions { Scripted = true, Library = library, Covers = SampleLibrary.Covers() });
        await Gui.UntilAsync(() => app.ViewModel.Sidebar.Playlists.Count == 1, "the playlist");
        app.ViewModel.Sidebar.NavigateCommand.Execute(app.ViewModel.Sidebar.Playlists[0]);
        await Gui.UntilAsync(() => app.ViewModel.CurrentPage is PlaylistPageViewModel { Tracks.Count: 2 }, "the playlist page");
        Gui.Settle();

        Gui.Click(app.Window, Gui.Find<Button>(app.Window, "QueueAll"));
        Assert.Equal(["Play next", "Add to queue", "Add to playlist"], Labels(app));
        Gui.Click(app.Window, Entries(app)[1]);
        var added = await SentAsync(app, "queue.add");
        Assert.Equal("upnext", added.GetProperty("position").GetString());
        Assert.Equal([library.Tracks[4].Id, library.Tracks[7].Id], TrackIds(added));
    }

    // ---- menus close ----------------------------------------------------------------------------------------

    private static async Task<AppHarness> MenuOnTracksAsync()
    {
        var app = await OpenAsync();
        var row = await ShowAsync(app, Kind.TrackRow);
        RightClick(app.Window, Middle(app.Window, row));
        Assert.True(app.ViewModel.Menu.IsOpen);
        return app;
    }

    [AvaloniaFact]
    public async Task A_menu_closes_on_a_click_outside()
    {
        await using var app = await MenuOnTracksAsync();
        var at = Middle(app.Window, Gui.Find<TextBlock>(app.Window, "PageTitle"));
        app.Window.MouseDown(at, MouseButton.Left);
        app.Window.MouseUp(at, MouseButton.Left);
        Gui.Settle();
        Assert.False(app.ViewModel.Menu.IsOpen);
    }

    [AvaloniaFact]
    public async Task A_menu_closes_on_Esc()
    {
        await using var app = await MenuOnTracksAsync();
        Gui.Press(app.Window, Key.Escape, PhysicalKey.Escape);
        Assert.False(app.ViewModel.Menu.IsOpen);
    }

    [AvaloniaFact]
    public async Task A_menu_closes_when_the_page_scrolls()
    {
        await using var app = await MenuOnTracksAsync();
        var scroller = Gui.All<ScrollViewer>(Gui.Find<ContentControl>(app.Window, "Page")).First();
        scroller.Offset = new Vector(0, 60);
        Gui.Settle();
        Assert.False(app.ViewModel.Menu.IsOpen);
    }

    [AvaloniaFact]
    public async Task A_menu_stays_open_when_a_scroller_only_changes_size()
    {
        await using var app = await MenuOnTracksAsync();
        app.ViewModel.QueueDrawer.Open();
        Gui.FinishAnimations(Slid);
        Assert.True(app.ViewModel.Menu.IsOpen);
    }

    [AvaloniaFact]
    public async Task A_menu_stays_open_when_the_queue_drawer_scrolls()
    {
        await using var app = await OpenAsync();
        await QueueRowsAsync(app, 20);
        app.ViewModel.QueueDrawer.Open();
        Gui.FinishAnimations(Slid);
        var row = await ShowAsync(app, Kind.TrackRow);
        RightClick(app.Window, Middle(app.Window, row));
        Assert.True(app.ViewModel.Menu.IsOpen);

        // as when the CLI adds rows and the drawer brings them into view
        var drawer = Gui.Find<ScrollViewer>(app.Window, "QueueScroll");
        Assert.True(drawer.Extent.Height > drawer.Viewport.Height, "the rows scroll");
        drawer.Offset = new Vector(0, 60);
        Gui.Settle();
        Assert.Equal(60, drawer.Offset.Y);
        Assert.True(app.ViewModel.Menu.IsOpen);
    }

    [AvaloniaFact]
    public async Task A_menu_closes_when_the_window_loses_the_focus()
    {
        await using var app = await MenuOnTracksAsync();
        // as the platform reports it (WindowBase.HandleDeactivated, which the window impl's Deactivated callback calls)
        var deactivate = typeof(WindowBase).GetMethod("HandleDeactivated", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        Assert.NotNull(deactivate);
        deactivate.Invoke(app.Window, null);
        Gui.Settle();
        Assert.False(app.ViewModel.Menu.IsOpen);
    }

    // ---- where menus go ---------------------------------------------------------------------------------------

    private static readonly Size Area = new(1440, 880);
    private static readonly Size MenuSize = new(170, 100);

    [Theory]
    [InlineData(500, 300, 500, 300)] // room everywhere: its corner at the pointer
    [InlineData(1400, 300, 1262, 300)] // right edge: moved left, 8 inside
    [InlineData(500, 850, 500, 750)] // bottom edge: above the pointer
    [InlineData(1435, 875, 1262, 775)] // bottom right corner: both
    [InlineData(2, 2, 8, 8)] // top left corner: 8 inside
    [InlineData(2, 860, 8, 760)] // bottom left corner
    [InlineData(1439, 1, 1262, 8)] // top right corner
    public void A_menu_at_the_pointer_stays_inside_the_window(double x, double y, double left, double top)
        => Assert.Equal(new Point(left, top), MenuPlacement.Place(new Rect(x, y, 0, 0), MenuSize, Area, 0, 8));

    [Fact]
    public void A_menu_under_a_button_keeps_its_gap_and_goes_above_it_when_there_is_no_room()
    {
        Assert.Equal(new Point(100, 134), MenuPlacement.Place(new Rect(100, 100, 80, 30), MenuSize, Area, 4, 8));
        Assert.Equal(new Point(100, 696), MenuPlacement.Place(new Rect(100, 800, 80, 30), MenuSize, Area, 4, 8));
    }

    [Fact]
    public void A_menu_taller_than_the_room_above_and_below_keeps_to_the_top()
        => Assert.Equal(new Point(8, 8), MenuPlacement.Place(new Rect(0, 40, 0, 0), new Size(170, 860), Area, 0, 8));

    [AvaloniaFact]
    public async Task A_menu_opened_near_the_bottom_right_of_the_window_opens_above_and_left_of_the_pointer()
    {
        await using var app = await OpenAsync();
        var item = await ShowAsync(app, Kind.NowPlayingTrack);
        var row = Gui.BoundsIn(app.Window, item);
        var at = new Point(row.Right - 2, row.Bottom - 2);
        RightClick(app.Window, at);

        var menu = Gui.BoundsIn(app.Window, MenuBox(app));
        Assert.True(menu.Right <= app.Window.Bounds.Width - 8, $"menu right {menu.Right}");
        Assert.True(menu.Bottom <= app.Window.Bounds.Height - 8, $"menu bottom {menu.Bottom}");
        Assert.True(menu.Left < at.X, "moved left of the pointer");
    }

    // ---- dragging ---------------------------------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(Kind.Card)]
    [InlineData(Kind.Sleeve)]
    [InlineData(Kind.CrateRecord)]
    [InlineData(Kind.TrackRow)]
    [InlineData(Kind.AlbumHeader)]
    [InlineData(Kind.NowPlayingTrack)]
    public async Task Dragging_an_item_shows_its_ghost_under_the_pointer_and_drops_it_into_the_queue(Kind kind)
    {
        await using var app = await OpenAsync();
        var item = await ShowAsync(app, kind);
        var expected = Expected(item);
        var ghost = Ghost(app);
        Assert.False(ghost.IsVisible);

        var at = StartDrag(app.Window, item);
        Assert.True(ghost.IsVisible);
        Assert.Equal(expected.Title, Gui.Find<TextBlock>(ghost, "GhostTitle").Text);
        Assert.Equal(expected.Detail, Gui.Find<TextBlock>(ghost, "GhostDetail").Text);
        AssertNear(at - new Point(24, 24), Gui.BoundsIn(app.Window, ghost).TopLeft);
        Assert.True(app.ViewModel.QueueDrawer.IsOpen, "the drawer opens for the drag");

        Gui.FinishAnimations(Slid);
        var over = DrawerMiddle(app);
        MoveTo(app.Window, over);
        AssertNear(over - new Point(24, 24), Gui.BoundsIn(app.Window, ghost).TopLeft);
        Assert.True(app.ViewModel.QueueDrawer.IsDragOver);
        Assert.Contains("over", Gui.Find<QueueDrawerView>(app.Window, "QueueDrawerView").Classes);

        var drops = app.Window.Drag.Drops;
        Release(app.Window, over);
        Assert.False(ghost.IsVisible);
        Assert.Equal(drops + 1, app.Window.Drag.Drops);
        var added = await SentAsync(app, "queue.add");
        Assert.Equal("next", added.GetProperty("position").GetString());
        Assert.Equal(expected.Ids, TrackIds(added));
        Assert.False(app.ViewModel.QueueDrawer.IsDragOver);
    }

    [AvaloniaFact]
    public async Task A_drag_does_not_click_what_it_started_on()
    {
        await using var app = await OpenAsync();
        var card = await ShowAsync(app, Kind.Card);
        var at = StartDrag(app.Window, card);
        Release(app.Window, at + new Point(40, 0));
        Gui.Settle();
        Assert.Equal(0, app.Scripted!.Count("queue.set"));
        Assert.False(app.ViewModel.Scene.IsRunning);
    }

    [AvaloniaFact]
    public async Task A_press_that_moves_less_than_the_threshold_is_still_a_click()
    {
        await using var app = await OpenAsync();
        var row = await ShowAsync(app, Kind.TrackRow);
        var number = Gui.All<Button>(row).First(b => b.Classes.Contains("number") || b.Classes.Contains("thumb"));
        var at = Middle(app.Window, number);
        app.Window.MouseMove(at);
        app.Window.MouseDown(at, MouseButton.Left);
        app.Window.MouseMove(at + new Point(2, 1), RawInputModifiers.LeftMouseButton);
        app.Window.MouseUp(at + new Point(2, 1), MouseButton.Left);
        Gui.Settle();
        Assert.False(Ghost(app).IsVisible);
        Assert.Equal(SampleLibrary.TrackId(8, 2), TrackIds(await SentAsync(app, "queue.set"))[(await SentAsync(app, "queue.set")).GetProperty("start").GetInt32()]);
    }

    [AvaloniaFact]
    public async Task A_text_box_does_not_take_a_drop_and_the_drawer_that_opened_for_it_closes()
    {
        await using var app = await OpenAsync();
        var row = await ShowAsync(app, Kind.TrackRow);
        var search = Gui.All<TextBox>(app.Window).First(t => t.Classes.Contains("search"));
        StartDrag(app.Window, row);
        Assert.True(app.ViewModel.QueueDrawer.IsOpen);

        var at = Middle(app.Window, search);
        MoveTo(app.Window, at);
        Assert.False(app.ViewModel.QueueDrawer.IsDragOver);
        var drops = app.Window.Drag.Drops;
        Release(app.Window, at);

        // The release decides the drop there and then, so no add can follow: none started, and none was sent.
        Assert.Equal(drops, app.Window.Drag.Drops);
        Assert.Equal(0, app.Scripted!.Count("queue.add"));
        Assert.True(string.IsNullOrEmpty(search.Text));
        Assert.False(Ghost(app).IsVisible);
        Assert.False(app.ViewModel.QueueDrawer.IsOpen);
    }

    [AvaloniaFact]
    public async Task Pressing_in_a_text_box_starts_no_drag()
    {
        await using var app = await OpenAsync();
        var search = Gui.All<TextBox>(app.Window).First(t => t.Classes.Contains("search"));
        StartDrag(app.Window, search);
        Assert.False(Ghost(app).IsVisible);
        Assert.False(app.ViewModel.QueueDrawer.IsOpen);
        Release(app.Window, Middle(app.Window, search));
    }

    [AvaloniaFact]
    public async Task After_a_drop_the_drawer_it_opened_closes_once_the_pointer_has_left()
    {
        await using var app = await OpenAsync();
        var row = await ShowAsync(app, Kind.TrackRow);
        StartDrag(app.Window, row);
        Gui.FinishAnimations(Slid);
        var over = DrawerMiddle(app);
        MoveTo(app.Window, over);
        Release(app.Window, over);
        await SentAsync(app, "queue.add");

        app.Advance(QueueDrawerViewModel.CloseAfterDrop + QueueDrawerViewModel.CloseRetry);
        Gui.Settle();
        Assert.True(app.ViewModel.QueueDrawer.IsOpen, "the pointer is still over it");

        app.Window.MouseMove(Middle(app.Window, Gui.Find<TextBlock>(app.Window, "PageTitle")));
        Gui.Settle();
        app.Advance(QueueDrawerViewModel.CloseRetry);
        Gui.Settle();
        Assert.False(app.ViewModel.QueueDrawer.IsOpen);
    }

    [AvaloniaFact]
    public async Task A_drag_dropped_outside_the_drawer_closes_it_at_once()
    {
        await using var app = await OpenAsync();
        var card = await ShowAsync(app, Kind.Card);
        var at = StartDrag(app.Window, card);
        Gui.FinishAnimations(Slid);
        Release(app.Window, at + new Point(100, 100));
        Assert.Equal(0, app.Window.Drag.Drops);
        Assert.False(app.ViewModel.QueueDrawer.IsOpen);
    }

    // ---- moving queue rows ------------------------------------------------------------------------------------

    private static async Task QueueRowsAsync(AppHarness app, int count)
    {
        // From album 7 on, three tracks each, round to album 1 after album 10.
        IReadOnlyList<Core.Remote.QueueItem> rows = [.. Enumerable.Range(1, count).Select(i => SampleLibrary.Item(((6 + ((i - 1) / 3)) % 10) + 1, ((i - 1) % 3) + 1))];
        app.Scripted!.SetQueue([SampleLibrary.Item(6, 1), .. rows, SampleLibrary.Item(6, 2)], 0, count);
        await app.Scripted.PushAsync("queueChanged");
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => app.ViewModel.QueueDrawer.Rows.Count == count, "the rows");
    }

    private static Control QueueRow(AppHarness app, int index)
        => (Control)Gui.Find<ItemsControl>(app.Window, "QueueRows").ContainerFromIndex(index)!;

    [AvaloniaFact]
    public async Task Dragging_a_row_shows_the_drop_line_where_it_lands_and_moves_it_there()
    {
        await using var app = await OpenAsync();
        await QueueRowsAsync(app, 3);
        app.ViewModel.QueueDrawer.Open();
        Gui.FinishAnimations(Slid);
        var line = Gui.Find<Border>(app.Window, "DropLine");
        Assert.False(line.IsVisible);

        StartDrag(app.Window, QueueRow(app, 0));
        var dragged = Gui.All<Panel>(QueueRow(app, 0)).First(p => p.Classes.Contains("queueRow"));
        Assert.Contains("dragged", dragged.Classes);
        Assert.True(dragged.Opacity < 1);
        Assert.Equal("The Hollow", Gui.Find<TextBlock>(Ghost(app), "GhostTitle").Text);

        // below the middle of the last row: the line under it
        var last = Gui.BoundsIn(app.Window, QueueRow(app, 2));
        MoveTo(app.Window, new Point(last.Center.X, last.Bottom - 2));
        Assert.True(line.IsVisible);
        Assert.Equal(last.Bottom, Gui.BoundsIn(app.Window, line).Center.Y, 1);

        // above the middle of the second row: the line over it
        var second = Gui.BoundsIn(app.Window, QueueRow(app, 1));
        MoveTo(app.Window, new Point(second.Center.X, second.Top + 2));
        Assert.Equal(second.Top, Gui.BoundsIn(app.Window, line).Center.Y, 1);

        MoveTo(app.Window, new Point(last.Center.X, last.Bottom - 2));
        Release(app.Window, new Point(last.Center.X, last.Bottom - 2));
        var move = await SentAsync(app, "queue.move");
        Assert.Equal((1, 3), (move.GetProperty("from").GetInt32(), move.GetProperty("to").GetInt32()));
        Assert.DoesNotContain("dragged", dragged.Classes);
        Assert.True(app.ViewModel.QueueDrawer.IsOpen, "it was open before the drag");
    }

    [AvaloniaFact]
    public async Task Dropping_a_track_between_rows_puts_it_there()
    {
        await using var app = await OpenAsync();
        var row = await ShowAsync(app, Kind.TrackRow);
        await QueueRowsAsync(app, 3);
        app.ViewModel.QueueDrawer.Open();
        Gui.FinishAnimations(Slid);

        StartDrag(app.Window, row);
        var second = Gui.BoundsIn(app.Window, QueueRow(app, 1));
        var at = new Point(second.Center.X, second.Top + 2);
        MoveTo(app.Window, at);
        Release(app.Window, at);

        var added = await SentAsync(app, "queue.add");
        Assert.Equal("upnext", added.GetProperty("position").GetString());
        var move = await SentAsync(app, "queue.move");
        Assert.Equal((4, 2), (move.GetProperty("from").GetInt32(), move.GetProperty("to").GetInt32()));
    }

    // ---- snapshots --------------------------------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Snapshot_of_a_track_menu(AppTheme theme)
    {
        await using var app = await OpenAsync(theme);
        var row = await ShowAsync(app, Kind.TrackRow);
        RightClick(app.Window, Middle(app.Window, row));
        Gui.FinishAnimations();
        Snapshots.Match(app.Window, $"menu-track-{theme.ToString().ToLowerInvariant()}");
    }

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Snapshot_of_an_album_ghost_mid_drag(AppTheme theme)
    {
        await using var app = await OpenAsync(theme);
        var card = await ShowAsync(app, Kind.Card);
        await Gui.UntilAsync(() => ((AlbumItemViewModel)card.DataContext!).Art.Cover is not null, "the cover");
        StartDrag(app.Window, card);
        Gui.FinishAnimations(Slid);
        MoveTo(app.Window, new Point(1000, 300));
        Snapshots.Match(app.Window, $"drag-album-{theme.ToString().ToLowerInvariant()}");
        Release(app.Window, new Point(1000, 300));
    }
}
