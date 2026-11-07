using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using MusicSpin.App.Controls;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;
using MusicSpin.App.Views;
using MusicSpin.Core.Playback;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// The title bar's layout and the app menu (#135): the title, the menu after it, the empty stretch that drags the window,
/// the search box by the window buttons; the menu's entries run the window's commands, and wait when they can't.
/// </summary>
public sealed class AppMenuTests
{
    // ---- layout --------------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task The_menu_follows_the_title_and_the_search_box_sits_by_the_window_buttons()
    {
        await using var app = await AppHarness.OpenAsync();
        var window = app.Window;

        var brand = Gui.BoundsIn(window, Gui.All<StackPanel>(window).Single(p => p.Classes.Contains("brand")));
        var menu = Gui.BoundsIn(window, Gui.Find<NativeMenuBar>(window, "AppMenuBar"));
        var drag = Gui.BoundsIn(window, Gui.Find<Panel>(window, "DragArea"));
        var search = SearchBounds(window);

        // Windows and Linux keep room on the right for the window buttons (138); macOS draws them on the left, so the
        // title starts after them (78) and the search box goes to the right edge. Either way the bar's gap (16) is
        // before the search box's right edge.
        var (titleLeft, buttonsRight) = OperatingSystem.IsMacOS() ? (78, 0) : (16, 138);
        Assert.Equal(["_File", "_Playback", "_View", "_Help"], TopItems(window).Select(i => i.Header));
        Assert.Equal(titleLeft, brand.Left);
        Assert.Equal(titleLeft + 188 + 16, menu.Left); // after the title's 188 and the gap: past the sidebar on Windows and Linux
        Assert.True(drag.Left > menu.Right && drag.Right < search.Left, $"menu {menu}, drag area {drag}, search {search}");
        Assert.True(drag.Width > 400, $"drag area {drag}");
        Assert.Equal(320, search.Width);
        Assert.Equal(1440 - buttonsRight - 16, search.Right);

        // A wider window widens the empty stretch only: the search box keeps its size and its place by the buttons.
        window.Width = 1700;
        Gui.Settle();
        Assert.Equal(new Rect(1700 - buttonsRight - 16 - 320, search.Top, 320, search.Height), SearchBounds(window));
        Assert.Equal(menu, Gui.BoundsIn(window, Gui.Find<NativeMenuBar>(window, "AppMenuBar")));
    }

    [AvaloniaFact]
    public async Task The_empty_stretch_and_the_title_drag_the_window_while_the_menu_and_search_take_their_input()
    {
        await using var app = await AppHarness.OpenAsync();
        var window = app.Window;

        // The platform moves the window (and maximizes it on a double-click) where the bar's role is TitleBar.
        Assert.Equal(WindowDecorationsElementRole.TitleBar, RoleAt(window, Gui.BoundsIn(window, Gui.Find<Panel>(window, "DragArea")).Center));
        Assert.Equal(WindowDecorationsElementRole.TitleBar, RoleAt(window, new Point(100, 22)));
        foreach (var item in TopItems(window))
        {
            Assert.Equal(WindowDecorationsElementRole.User, RoleAt(window, Gui.BoundsIn(window, item).Center));
        }

        Assert.Equal(WindowDecorationsElementRole.User, RoleAt(window, SearchBounds(window).Center));
    }

    // ---- File ----------------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task File_opens_Settings_and_Quit_closes_the_window()
    {
        await using var app = await AppHarness.OpenAsync();
        var window = app.Window;

        Choose(window, "_File", "_Settings…");
        Assert.True(app.ViewModel.Settings.IsOpen);
        Assert.DoesNotContain(TopItems(window), i => i.IsSubMenuOpen);

        app.ViewModel.Settings.Close();
        if (OperatingSystem.IsMacOS())
        {
            // The menu bar's app menu has Quit on macOS, so File doesn't; ⌘Q is still the window's.
            Open(window, "_File");
            Assert.False(Entry(window, "_File", "_Quit").IsVisible);
            Close(window);
            PressCommand(window, Key.Q, PhysicalKey.Q);
        }
        else
        {
            Choose(window, "_File", "_Quit");
        }

        await Gui.UntilAsync(() => !window.IsVisible, "the window closed");
    }

    [AvaloniaFact]
    public async Task The_quit_shortcut_closes_the_window()
    {
        await using var app = await AppHarness.OpenAsync();

        PressCommand(app.Window, Key.Q, PhysicalKey.Q);

        await Gui.UntilAsync(() => !app.Window.IsVisible, "the window closed");
    }

    [AvaloniaFact]
    public async Task File_adds_a_music_folder_and_waits_while_a_scan_runs()
    {
        var library = SampleLibrary.Create();
        var picker = new StubFolderPicker { Answer = "/more music" };
        await using var app = await AppHarness.OpenAsync(new AppOptions { Scripted = true, Library = library, Covers = SampleLibrary.Covers(), FolderPicker = picker });
        var window = app.Window;
        var hold = library.ScanHold = new TaskCompletionSource();

        // The OS's picker, then the folder is added and scanned (the scan waits here, holding the library).
        Choose(window, "_File", "_Add a music folder…");
        await Gui.UntilAsync(() => app.ViewModel.LibraryScan.IsScanning, "the new folder's scan");
        Assert.Equal(1, picker.Opened);
        Assert.Contains(library.Folders, f => f.Path == "/more music");

        // While the scan holds the library, the folder entries wait, as Settings › Library's buttons do.
        Open(window, "_File");
        Assert.False(Entry(window, "_File", "_Add a music folder…").IsEffectivelyEnabled);
        Assert.False(Entry(window, "_File", "_Rescan the library").IsEffectivelyEnabled);
        Assert.True(Entry(window, "_File", "_Settings…").IsEffectivelyEnabled);
        Close(window);

        library.ScanHold = null;
        hold.SetResult();
        await Gui.UntilAsync(() => !app.ViewModel.LibraryScan.IsScanning, "the scan done");
        Choose(window, "_File", "_Rescan the library");
        Assert.Equal(2, library.Scans);
    }

    [AvaloniaFact]
    public async Task Cancelling_the_folder_picker_adds_nothing()
    {
        var library = SampleLibrary.Create();
        var picker = new StubFolderPicker { Answer = null };
        await using var app = await AppHarness.OpenAsync(new AppOptions { Scripted = true, Library = library, Covers = SampleLibrary.Covers(), FolderPicker = picker });

        Choose(app.Window, "_File", "_Add a music folder…");
        await Gui.UntilAsync(() => picker.Opened == 1, "the picker");

        Assert.Empty(library.Folders);
        Assert.Equal(0, library.Scans);
        Assert.False(app.ViewModel.LibraryScan.IsScanning);
    }

    // ---- Playback ------------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Playback_waits_with_nothing_to_play_and_Next_waits_at_the_end_of_the_queue()
    {
        await using var app = await AppHarness.OpenAsync();
        var window = app.Window;
        var player = app.ViewModel.Player;
        await Gui.UntilAsync(() => player.IsConnected, "connected");

        Open(window, "_Playback");
        Assert.Equal("Play", Entry(window, "_Playback", 0).Header);
        Assert.False(Entry(window, "_Playback", 0).IsEffectivelyEnabled);
        Assert.False(Entry(window, "_Playback", "_Previous").IsEffectivelyEnabled);
        Assert.False(Entry(window, "_Playback", "_Next").IsEffectivelyEnabled);
        Open(window, "_View");
        Assert.False(Entry(window, "_View", "_Now Playing").IsEffectivelyEnabled);
        Close(window);

        var paths = new[] { Path.GetFullPath("/music/ui/menu-1.flac"), Path.GetFullPath("/music/ui/menu-2.flac") };
        app.Player.AddTracks(paths);
        await using (var cli = await app.Player.ConnectOtherAsync())
        {
            await cli.SendAsync("queue.set", new { items = paths, play = true }, TestContext.Current.CancellationToken);
        }

        await Gui.UntilAsync(() => player.State == PlayerState.Playing && player.QueueLength == 2, "playing the first of two");
        Open(window, "_Playback");
        Assert.Equal("Pause", Entry(window, "_Playback", 0).Header);
        Assert.True(Entry(window, "_Playback", 0).IsEffectivelyEnabled);
        Assert.True(Entry(window, "_Playback", "_Previous").IsEffectivelyEnabled);
        Assert.True(Entry(window, "_Playback", "_Next").IsEffectivelyEnabled);

        Click(window, Entry(window, "_Playback", "_Next"));
        await Gui.UntilAsync(() => player.QueueIndex == 1, "the second track");

        // The last entry, with repeat off: the player would refuse next.
        Open(window, "_Playback");
        Assert.False(Entry(window, "_Playback", "_Next").IsEffectivelyEnabled);

        // Repeat all goes round to the first again.
        Open(window, "_Playback");
        Click(window, Entry(window, "_Playback", "_Repeat"));
        Click(window, SubEntry(window, "_Playback", "_Repeat", "_All"));
        await Gui.UntilAsync(() => player.Repeat == RepeatMode.All, "repeat all");
        Open(window, "_Playback");
        Assert.True(Entry(window, "_Playback", "_Next").IsEffectivelyEnabled);
        Click(window, Entry(window, "_Playback", "_Repeat"));
        Assert.Equal([false, true, false], Repeats(window).Select(i => i.IsChecked));

        Click(window, Entry(window, "_Playback", 0));
        await Gui.UntilAsync(() => player.State == PlayerState.Paused, "paused from the menu");
        Open(window, "_Playback");
        Assert.Equal("Play", Entry(window, "_Playback", 0).Header);
    }

    [AvaloniaFact]
    public async Task Shuffle_and_Mute_show_the_players_state_whoever_changes_it()
    {
        await using var app = await AppHarness.OpenAsync();
        var window = app.Window;
        var player = app.ViewModel.Player;
        await Gui.UntilAsync(() => player.IsConnected, "connected");

        Open(window, "_Playback");
        Click(window, Entry(window, "_Playback", "S_huffle"));
        await Gui.UntilAsync(() => player.Shuffle, "shuffle on from the menu");
        Open(window, "_Playback");
        Assert.True(Entry(window, "_Playback", "S_huffle").IsChecked);
        Close(window);

        // Changed elsewhere (the CLI), the check follows: clicking it didn't cut it loose from the player.
        await using (var cli = await app.Player.ConnectOtherAsync())
        {
            await cli.SendAsync("shuffle", new { mode = "off" }, TestContext.Current.CancellationToken);
            await Gui.UntilAsync(() => !player.Shuffle, "shuffle off from the CLI");
            Open(window, "_Playback");
            Assert.False(Entry(window, "_Playback", "S_huffle").IsChecked);

        }

        Click(window, Entry(window, "_Playback", "_Mute"));
        await Gui.UntilAsync(() => player.Muted, "muted from the menu");
        Open(window, "_Playback");
        Assert.True(Entry(window, "_Playback", "_Mute").IsChecked);

        // And unmuted from the player bar's speaker.
        Close(window);
        app.ViewModel.PlayerBar.Volume.ToggleMuteCommand.Execute(null);
        await Gui.UntilAsync(() => !player.Muted, "unmuted from the player bar");
        Open(window, "_Playback");
        Assert.False(Entry(window, "_Playback", "_Mute").IsChecked);
    }

    [AvaloniaFact]
    public async Task Without_a_player_the_playback_entries_wait()
    {
        await using var app = await AppHarness.OpenAsync(new AppOptions { Initialize = false });
        var window = app.Window;
        Assert.False(app.ViewModel.Player.IsConnected);

        Open(window, "_Playback");
        Assert.All(Entries(window, "_Playback").Where(i => (string?)i.Header != "_Repeat"),
            item => Assert.False(item.IsEffectivelyEnabled, $"{item.Header} is enabled"));
        Click(window, Entry(window, "_Playback", "_Repeat")); // a submenu's title always opens it
        Assert.All(Repeats(window), item => Assert.False(item.IsEffectivelyEnabled, $"Repeat › {item.Header} is enabled"));
        Open(window, "_View");
        Assert.True(Entry(window, "_View", "_Tracks").IsEffectivelyEnabled);
        Assert.True(Entry(window, "_View", "_Queue").IsEffectivelyEnabled);
    }

    // ---- View ----------------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task View_shows_the_pages_and_opens_the_queue()
    {
        await using var app = await AppHarness.OpenAsync();
        var window = app.Window;
        var vm = app.ViewModel;

        Choose(window, "_View", "_Tracks");
        Assert.Same(vm.TracksPage, vm.CurrentPage);
        Choose(window, "_View", "_Favorites");
        Assert.Same(vm.FavoritesPage, vm.CurrentPage);
        Choose(window, "_View", "_Radio");
        Assert.Same(vm.RadioPage, vm.CurrentPage);
        Choose(window, "_View", "_Albums");
        Assert.Same(vm.AlbumsPage, vm.CurrentPage);
        Assert.True(Gui.All<Button>(window).Single(b => b.Classes.Contains("nav") && b.Classes.Contains("active")).DataContext == vm.Sidebar.Albums);

        Choose(window, "_View", "_Queue");
        Assert.True(vm.QueueDrawer.IsOpen);
    }

    /// <summary>
    /// #216: from Now Playing, each view in View's first group leaves it and shows that page. Albums is the page already
    /// underneath, Favorites is chosen again once it is current, and Albums is chosen a second time after that.
    /// </summary>
    [AvaloniaFact]
    public async Task View_leaves_Now_Playing_for_each_library_view()
    {
        await using var app = await AppHarness.OpenAsync();
        var window = app.Window;
        var vm = app.ViewModel;
        var views = new (string Header, PageViewModel Page, NavItemViewModel Item)[]
        {
            ("_Albums", vm.AlbumsPage, vm.Sidebar.Albums),
            ("_Tracks", vm.TracksPage, vm.Sidebar.Tracks),
            ("_Radio", vm.RadioPage, vm.Sidebar.Radio),
            ("_Favorites", vm.FavoritesPage, vm.Sidebar.Favorites),
            ("_Favorites", vm.FavoritesPage, vm.Sidebar.Favorites),
            ("_Albums", vm.AlbumsPage, vm.Sidebar.Albums),
        };

        foreach (var view in views)
        {
            OpenNowPlaying(window);
            Choose(window, "_View", view.Header);
            AssertLeftNowPlaying(window, vm, view.Page, view.Item, view.Header);
            Gui.FinishAnimations();
            Assert.False(Gui.Find<SlidePanel>(window, "NowPlayingLayer").IsVisible, $"{view.Header} left Now Playing on screen");
        }
    }

    /// <summary>
    /// #216: choosing a view while Now Playing is still sliding in, while it is sliding back out, and while the queue
    /// drawer is open over it, still leaves it and shows that page. The queue stays. Queue and Search, past the
    /// separator, leave Now Playing up.
    /// </summary>
    [AvaloniaFact]
    public async Task View_leaves_Now_Playing_mid_slide_and_under_the_queue()
    {
        await using var app = await AppHarness.OpenAsync();
        var window = app.Window;
        var vm = app.ViewModel;
        vm.Settings.SetMotion(MotionMode.Smooth); // the partway slide below is 0.55s only at Smooth
        var layer = Gui.Find<SlidePanel>(window, "NowPlayingLayer");

        // Still sliding in: the slide has not been given its time, so it does not cover the page yet.
        vm.NowPlaying.Open();
        Gui.Settle();
        Assert.True(vm.NowPlaying.IsOpen);
        Assert.True(layer.Shown < 1, $"Now Playing already covered the page ({layer.Shown})");
        Assert.False(layer.Covers);
        Choose(window, "_View", "_Tracks");
        AssertLeftNowPlaying(window, vm, vm.TracksPage, vm.Sidebar.Tracks, "_Tracks");

        // Fully in, then partway back down: opening again before it leaves, and choosing Albums, leaves it a second time.
        OpenNowPlaying(window);
        Choose(window, "_View", "_Radio");
        AssertLeftNowPlaying(window, vm, vm.RadioPage, vm.Sidebar.Radio, "_Radio");
        Gui.FinishAnimations(200); // the slide out is 0.55s; this is on the way, not gone
        Assert.True(layer.IsVisible, "the close has not left the screen yet");
        Assert.InRange(layer.Shown, 0.01, 0.99);
        vm.NowPlaying.Open();
        Gui.Settle();
        Assert.True(vm.NowPlaying.IsOpen);
        Choose(window, "_View", "_Albums");
        AssertLeftNowPlaying(window, vm, vm.AlbumsPage, vm.Sidebar.Albums, "_Albums");
        Gui.FinishAnimations();
        Assert.False(layer.IsVisible);

        // The drawer sits over Now Playing. A library view still dismisses Now Playing and leaves the drawer.
        OpenNowPlaying(window);
        vm.QueueDrawer.Open();
        Gui.FinishAnimations();
        Assert.True(vm.QueueDrawer.IsOpen);
        Assert.True(layer.Covers);
        Choose(window, "_View", "_Favorites");
        AssertLeftNowPlaying(window, vm, vm.FavoritesPage, vm.Sidebar.Favorites, "_Favorites");
        Assert.True(vm.QueueDrawer.IsOpen, "the queue drawer closed with Now Playing");

        // Now Playing already closed, the drawer still open: the view still changes.
        Choose(window, "_View", "_Tracks");
        AssertLeftNowPlaying(window, vm, vm.TracksPage, vm.Sidebar.Tracks, "_Tracks");
        Assert.True(vm.QueueDrawer.IsOpen);

        // Queue and Search sit past the separator. They are not library views: Now Playing stays up under them.
        OpenNowPlaying(window);
        Choose(window, "_View", "_Queue");
        Assert.True(vm.NowPlaying.IsOpen, "Queue closed Now Playing");
        Assert.True(vm.QueueDrawer.IsOpen);
        Choose(window, "_View", "_Search");
        Assert.True(Gui.Find<TextBox>(window, "SearchBox").IsFocused);
        Assert.True(vm.NowPlaying.IsOpen, "Search closed Now Playing");
    }

    // ---- keyboard ------------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Ctrl_F_or_Cmd_F_focuses_the_search_box_which_still_filters_the_page()
    {
        await using var app = await AppHarness.OpenAsync();
        var window = app.Window;
        var search = Gui.Find<TextBox>(window, "SearchBox");
        Assert.False(search.IsFocused);

        PressCommand(window, Key.F, PhysicalKey.F);
        Assert.True(search.IsFocused);
        Assert.True(app.ViewModel.IsTextInputFocused);

        foreach (var c in "kodo")
        {
            Gui.Press(window, Key.None, PhysicalKey.None, c.ToString());
        }

        Assert.Equal("kodo", app.ViewModel.SearchText);
        Assert.Equal("kodo", app.ViewModel.CurrentPage.SearchText);

        // From elsewhere, again: the box takes the focus with its text selected, to type over.
        window.FocusManager!.Focus(null);
        Gui.Settle();
        Assert.False(search.IsFocused);
        Choose(window, "_View", "_Search");
        Assert.True(search.IsFocused);
        Assert.Equal((0, 4), (search.SelectionStart, search.SelectionEnd));
        Gui.Press(window, Key.None, PhysicalKey.None, "j");
        Assert.Equal("j", app.ViewModel.CurrentPage.SearchText);
    }

    [AvaloniaFact]
    public async Task Alt_opens_the_menu_and_the_arrow_keys_and_Enter_choose_from_it()
    {
        await using var app = await AppHarness.OpenAsync();
        var window = app.Window;
        var menu = TopItems(window).First().FindAncestorOfType<Menu>()!;

        window.KeyPress(Key.LeftAlt, RawInputModifiers.Alt, PhysicalKey.AltLeft, null);
        window.KeyRelease(Key.LeftAlt, RawInputModifiers.None, PhysicalKey.AltLeft, null);
        Gui.Settle();
        Assert.True(menu.IsOpen);
        Assert.True(TopItems(window).First().IsSelected);

        Assert.True(app.ViewModel.IsAppMenuOpen);

        // The arrows are the menu's now, not the window's (→ would flip the albums page's crate).
        Gui.Press(window, Key.Right, PhysicalKey.ArrowRight);
        Gui.Press(window, Key.Right, PhysicalKey.ArrowRight);
        Assert.True(Top(window, "_View").IsSelected);
        Gui.Press(window, Key.Down, PhysicalKey.ArrowDown);
        Assert.True(TopItems(window).Single(i => (string?)i.Header == "_View").IsSubMenuOpen);
        Gui.Press(window, Key.Down, PhysicalKey.ArrowDown); // _Albums is first; this is _Tracks
        Gui.Press(window, Key.Enter, PhysicalKey.Enter);

        Assert.Same(app.ViewModel.TracksPage, app.ViewModel.CurrentPage);
        Assert.False(menu.IsOpen);
        Assert.False(app.ViewModel.IsAppMenuOpen);

        // Esc closes the menu, and nothing under it (Esc's other job is the topmost layer).
        app.ViewModel.QueueDrawer.Open();
        window.KeyPress(Key.LeftAlt, RawInputModifiers.Alt, PhysicalKey.AltLeft, null);
        window.KeyRelease(Key.LeftAlt, RawInputModifiers.None, PhysicalKey.AltLeft, null);
        Gui.Settle();
        Assert.True(menu.IsOpen);
        Gui.Press(window, Key.Escape, PhysicalKey.Escape);
        Assert.False(menu.IsOpen);
        Assert.True(app.ViewModel.QueueDrawer.IsOpen);

        // Closed, the window's keys are back: Esc closes the drawer.
        Gui.Press(window, Key.Escape, PhysicalKey.Escape);
        Assert.False(app.ViewModel.QueueDrawer.IsOpen);
    }

    [AvaloniaFact]
    public async Task Space_with_the_menu_open_does_not_play_or_pause()
    {
        await using var app = await AppHarness.OpenAsync();
        var window = app.Window;
        var paths = new[] { Path.GetFullPath("/music/ui/menu-space.flac") };
        app.Player.AddTracks(paths);
        await using (var cli = await app.Player.ConnectOtherAsync())
        {
            await cli.SendAsync("queue.set", new { items = paths, play = true }, TestContext.Current.CancellationToken);
        }

        await Gui.UntilAsync(() => app.ViewModel.Player.State == PlayerState.Playing, "playing");
        Open(window, "_View");
        Gui.Press(window, Key.Space, PhysicalKey.Space, " ");

        await app.PlayerRoundTripAsync();
        Assert.Equal(PlayerState.Playing, app.ViewModel.Player.State); // no toggle was sent
        Assert.True(Top(window, "_View").IsSubMenuOpen);

        // Closed, Space is play/pause again.
        Close(window);
        Gui.Press(window, Key.Space, PhysicalKey.Space, " ");
        await Gui.UntilAsync(() => app.ViewModel.Player.State == PlayerState.Paused, "paused by Space");
    }

    // ---- snapshots -----------------------------------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Snapshot_of_the_playback_menu(AppTheme theme)
    {
        await using var app = await AppHarness.OpenAsync(new AppOptions { Theme = theme });
        await Gui.UntilAsync(() => app.ViewModel.Player.IsConnected, "connected");
        await using (var cli = await app.Player.ConnectOtherAsync())
        {
            await cli.SendAsync("shuffle", new { mode = "on" }, TestContext.Current.CancellationToken);
        }

        await Gui.UntilAsync(() => app.ViewModel.Player.Shuffle, "shuffle on");
        Open(app.Window, "_Playback");
        Hover(app.Window, Entry(app.Window, "_Playback", "S_huffle"));
        Gui.FinishAnimations();
        Snapshots.Match(app.Window, $"menu-app-{theme.ToString().ToLowerInvariant()}");
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static Rect SearchBounds(Window window) => Gui.BoundsIn(window, Gui.All<Border>(window).Single(b => b.Classes.Contains("search")));

    private static IEnumerable<MenuItem> TopItems(Window window) => Gui.All<MenuItem>(Gui.Find<NativeMenuBar>(window, "AppMenuBar"));

    private static MenuItem Top(Window window, string header)
        => TopItems(window).SingleOrDefault(i => (string?)i.Header == header) ?? throw new InvalidOperationException($"No menu {header}.");

    /// <summary>The entries of a list (its separators left out), shown once it is open.</summary>
    private static IEnumerable<MenuItem> Entries(MenuItem parent) => parent.GetLogicalChildren().OfType<MenuItem>();

    private static IEnumerable<MenuItem> Entries(Window window, string top) => Entries(Top(window, top));

    private static MenuItem Entry(Window window, string top, string header)
        => Entries(window, top).SingleOrDefault(i => (string?)i.Header == header) ?? throw new InvalidOperationException($"No {top} › {header}.");

    private static MenuItem Entry(Window window, string top, int index) => Entries(window, top).ElementAt(index);

    private static MenuItem SubEntry(Window window, string top, string sub, string header)
        => Entries(Entry(window, top, sub)).Single(i => (string?)i.Header == header);

    private static IEnumerable<MenuItem> Repeats(Window window) => Entries(Entry(window, "_Playback", "_Repeat"));

    /// <summary>Now Playing fully in, covering the page and the sidebar (the state #216 starts from).</summary>
    private static void OpenNowPlaying(Window window)
    {
        ((MainWindowViewModel)window.DataContext!).NowPlaying.Open();
        Gui.FinishAnimations(700); // the slide is 0.55s at Motion Smooth
        var layer = Gui.Find<SlidePanel>(window, "NowPlayingLayer");
        Assert.True(layer.Covers, "Now Playing never covered the page");
        Assert.False(Gui.Find<DockPanel>(window, "ContentArea").IsVisible);
    }

    /// <summary>The chosen view is the page on screen and Now Playing no longer covers it.</summary>
    private static void AssertLeftNowPlaying(Window window, MainWindowViewModel vm, PageViewModel page, NavItemViewModel item, string header)
    {
        Assert.Same(page, vm.CurrentPage);
        Assert.True(page.IsShown, $"{header} is not the page shown");
        Assert.Same(item, vm.Sidebar.Active);
        Assert.True(item.IsActive);
        Assert.False(vm.NowPlaying.IsOpen, $"{header} left Now Playing open");
        var layer = Gui.Find<SlidePanel>(window, "NowPlayingLayer");
        Assert.False(layer.IsOpen);
        Assert.False(layer.Covers, $"{header} left Now Playing covering the page");
        Assert.True(Gui.Find<DockPanel>(window, "ContentArea").IsVisible, $"{header} left the page hidden");
        Assert.Same(page, Gui.Find<ContentControl>(window, "Page").Content);
        Assert.Same(item, Gui.All<Button>(window).Single(b => b.Classes.Contains("nav") && b.Classes.Contains("active")).DataContext);
    }

    /// <summary>Opens a menu by clicking its title (the list opens in the window's overlay layer).</summary>
    private static void Open(Window window, string top)
    {
        var item = Top(window, top);
        if (!item.IsSubMenuOpen)
        {
            if (TopItems(window).Any(i => i.IsSubMenuOpen))
            {
                Close(window);
            }

            Click(window, item);
        }

        Assert.True(item.IsSubMenuOpen, $"{top} didn't open");
    }

    private static void Close(Window window)
    {
        TopItems(window).First().FindAncestorOfType<Menu>()!.Close();
        Gui.Settle();
    }

    private static void Choose(Window window, string top, string header)
    {
        Open(window, top);
        Click(window, Entry(window, top, header));
    }

    /// <summary>Clicks the middle of <paramref name="item"/>, checking the pointer lands on it (not on something over it).</summary>
    private static void Click(Window window, MenuItem item)
    {
        var point = Gui.BoundsIn(window, item).Center;
        Assert.True(window.InputHitTest(point) is Visual hit && (hit == item || item.IsVisualAncestorOf(hit)), $"{item.Header} isn't under the pointer");
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Gui.Settle();
    }

    private static void Hover(Window window, MenuItem item)
    {
        window.MouseMove(Gui.BoundsIn(window, item).Center);
        Gui.Settle();
    }

    private static RawInputModifiers CommandKey => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    /// <summary>The key with Ctrl, or with ⌘ on macOS (the platform's command key).</summary>
    private static void PressCommand(Window window, Key key, PhysicalKey physical)
    {
        window.KeyPress(key, CommandKey, physical, null);
        window.KeyRelease(key, CommandKey, physical, null);
        Gui.Settle();
    }

    /// <summary>The decoration role the platform reads under <paramref name="point"/>: the nearest one set, from what is hit up.</summary>
    private static WindowDecorationsElementRole RoleAt(Window window, Point point)
    {
        var hit = window.InputHitTest(point) as Visual ?? throw new InvalidOperationException($"Nothing at {point}.");
        foreach (var visual in hit.GetSelfAndVisualAncestors())
        {
            if (WindowDecorationProperties.GetElementRole(visual) is var role && role != WindowDecorationsElementRole.None)
            {
                return role;
            }
        }

        return WindowDecorationsElementRole.None;
    }
}
