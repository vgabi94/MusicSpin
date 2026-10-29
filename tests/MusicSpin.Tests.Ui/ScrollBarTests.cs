using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MusicSpin.App.Services;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;
using MusicSpin.App.Views;
using MusicSpin.App.Views.Pages;
using MusicSpin.Core.Settings;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// The app's scroll bars (#130): every scrollable view uses the ScrollBar theme in Styles/Controls.axaml, not Fluent's; a
/// slim thumb that widens under the pointer, lighter while hovered and in the accent while dragged; theme colours.
/// </summary>
public sealed class ScrollBarTests
{
    private const int Count = 200;

    /// <summary>200 albums with a track each and a 200-track playlist, at the window's smallest size.</summary>
    private static async Task<AppHarness> OpenAsync()
    {
        var library = new StubLibrary();
        library.Albums.AddRange(Enumerable.Range(0, Count).Select(StubLibrary.Album));
        library.Tracks.AddRange(Enumerable.Range(0, Count).Select(i => StubLibrary.Track(i, $"album{i:D4}")));
        library.Playlists.Add(new PlaylistSummaryItem("pl1", "Evening", Count));
        library.PlaylistEntries["pl1"] = [.. library.Tracks];
        var app = await AppHarness.OpenAsync(new AppOptions
        {
            Scripted = true, Library = library, Covers = new StubCovers(), Width = 1280, Height = 720,
        });
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == Count, "the albums");
        return app;
    }

    private static ControlTheme AppThumb => (ControlTheme)Application.Current!.FindResource("ScrollBarThumb")!;

    /// <summary>The scroll bars of the ScrollViewers shown under <paramref name="root"/>, each with its template applied.</summary>
    private static List<ScrollBar> BarsIn(Visual root)
    {
        var bars = Gui.All<ScrollViewer>(root).Where(v => v.IsEffectivelyVisible)
            .SelectMany(v => v.GetVisualDescendants().OfType<ScrollBar>().Where(b => b.TemplatedParent == v))
            .ToList();
        foreach (var bar in bars)
        {
            bar.ApplyTemplate(); // a bar with nothing to scroll is hidden, so never measured or templated
        }

        return bars;
    }

    private static void AssertAppTheme(ScrollBar bar, string where)
    {
        var parts = bar.GetVisualDescendants().OfType<Control>().Where(c => c.TemplatedParent == bar).ToList();
        var thumb = parts.OfType<Thumb>().SingleOrDefault();
        Assert.True(thumb is not null, $"{where}: the {bar.Orientation} scroll bar has no thumb.");
        Assert.True(ReferenceEquals(AppThumb, thumb.Theme), $"{where}: the {bar.Orientation} scroll bar's thumb isn't the app's.");
        Assert.Contains(parts, p => p.Name == "PART_TrackFill");
        Assert.DoesNotContain(parts, p => p.Name is "PART_LineUpButton" or "PART_LineDownButton");
        Assert.Null(bar.ContextFlyout); // Fluent's has a menu of its own
    }

    private static void AssertAllAppTheme(AppHarness app, Visual root, string where)
    {
        var bars = BarsIn(root);
        Assert.True(bars.Count > 0, $"{where}: no scroll viewer shown.");
        foreach (var bar in bars)
        {
            AssertAppTheme(bar, where);
        }

        // And nothing else in the window has a Fluent bar.
        foreach (var bar in BarsIn(app.Window))
        {
            AssertAppTheme(bar, where + " (window)");
        }
    }

    private static Button NavButton(Window window, string title)
        => Gui.All<Button>(window).Single(b => b.Classes.Contains("nav") && b.DataContext is NavItemViewModel item && item.Title == title);

    private static Control Page(AppHarness app) => Gui.Find<ContentControl>(app.Window, "Page").Presenter!.Child!;

    [AvaloniaFact]
    public async Task Every_scrollable_view_uses_the_app_scroll_bar()
    {
        await using var app = await OpenAsync();
        var window = app.Window;
        var albums = app.ViewModel.AlbumsPage;

        foreach (var (view, grouping) in new[] { (AlbumsView.Cards, AlbumGrouping.None), (AlbumsView.Shelf, AlbumGrouping.None), (AlbumsView.Crate, AlbumGrouping.Genre) })
        {
            albums.Grouping = grouping;
            albums.ViewChoice = albums.Views.Single(v => v.Value == view);
            Gui.Settle();
            AssertAllAppTheme(app, Assert.IsType<AlbumsPage>(Page(app)), $"Albums › {view}");
        }

        Gui.Click(window, NavButton(window, "Tracks"));
        await Gui.UntilAsync(() => app.ViewModel.TracksPage.CountText?.StartsWith("200", StringComparison.Ordinal) == true, "the tracks");
        AssertAllAppTheme(app, Assert.IsType<TracksPage>(Page(app)), "Tracks");

        Gui.Click(window, NavButton(window, "Evening"));
        await Gui.UntilAsync(() => app.ViewModel.CurrentPage.CountText?.StartsWith("200", StringComparison.Ordinal) == true, "the playlist");
        AssertAllAppTheme(app, Assert.IsType<TracksPage>(Page(app)), "a playlist");

        Gui.Click(window, NavButton(window, "Radio"));
        AssertAllAppTheme(app, Assert.IsType<RadioPage>(Page(app)), "Radio");

        AssertAllAppTheme(app, Gui.Find<SidebarView>(window, "SidebarView"), "the sidebar");

        app.Scripted!.SetQueue([.. Enumerable.Range(0, Count).Select(i => $"Track {i}")], 0, Count - 1);
        await app.Scripted.PushAsync("queueChanged");
        app.ViewModel.QueueDrawer.Open();
        await Gui.UntilAsync(() => app.ViewModel.QueueDrawer.Rows.Count == Count - 1, "the queue");
        Gui.FinishAnimations();
        AssertAllAppTheme(app, Gui.All<QueueDrawerView>(window).Single(), "the queue drawer");
        app.ViewModel.QueueDrawer.Close();

        Gui.Click(window, Gui.Find<Button>(window, "SettingsButton"));
        Assert.True(app.ViewModel.Settings.IsOpen);
        Gui.FinishAnimations();
        AssertAllAppTheme(app, Gui.All<SettingsDialog>(window).Single(), "Settings");
        app.ViewModel.Settings.Close();

        app.Scripted.Status = ScriptedPlayer.Playing("Track 0", trackId: "track0000");
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => app.ViewModel.NowPlaying.Tracks.Count == 1, "the album playing");
        app.ViewModel.NowPlaying.Open();
        Gui.FinishAnimations(900);
        AssertAllAppTheme(app, Gui.All<NowPlayingView>(window).Single(), "Now Playing");
    }

    [AvaloniaFact]
    public void A_horizontal_bar_is_slim_along_its_bottom_edge_with_no_arrow_buttons()
    {
        var viewer = new ScrollViewer
        {
            Width = 200, Height = 100, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border { Width = 800, Height = 50 },
        };
        var window = new Window { Width = 200, Height = 100, Content = viewer };
        window.Show();
        try
        {
            Gui.Settle();
            var bar = viewer.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Avalonia.Layout.Orientation.Horizontal);
            var thumb = Gui.All<Thumb>(bar).Single();
            Gui.FinishAnimations();

            Assert.True(bar.IsVisible);
            AssertAppTheme(bar, "a horizontal bar");
            Assert.Equal(0, Gui.BoundsIn(window, thumb).Left - Gui.BoundsIn(window, bar).Left, 1);
            Assert.Equal(0.5, thumb.RenderTransform!.Value.M22, 3);
            Assert.Equal(1, thumb.RenderTransform.Value.M11, 3);
            Assert.Equal(new RelativePoint(0.5, 1, RelativeUnit.Relative), thumb.RenderTransformOrigin);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task The_thumb_is_slim_at_rest_and_widens_lightens_and_takes_the_accent_under_the_pointer()
    {
        await using var app = await OpenAsync();
        var window = app.Window;
        Gui.Click(window, NavButton(window, "Tracks"));
        await Gui.UntilAsync(() => app.ViewModel.TracksPage.CountText?.StartsWith("200", StringComparison.Ordinal) == true, "the tracks");
        var scroller = Gui.Find<ScrollViewer>(Page(app), "Scroller");
        var bar = scroller.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
        var thumb = Gui.All<Thumb>(bar).Single();
        var pill = Gui.Find<Border>(thumb, "PART_Pill");
        var track = Gui.Find<Border>(bar, "PART_TrackFill");
        Gui.FinishAnimations();

        // At rest: half as wide against the edge, no track, the plain thumb colour. No arrow buttons take room: the thumb
        // can reach both ends of the bar.
        Assert.True(bar.IsVisible);
        Assert.False(bar.IsExpanded);
        Assert.Equal(0.5, ScaleX(thumb), 3);
        Assert.Equal(0, track.Opacity);
        Assert.Equal(Argb(0x4DEEF0F4), Colour(pill.Background));
        Assert.Equal(0, Gui.BoundsIn(window, thumb).Top - Gui.BoundsIn(window, bar).Top, 1);

        // The pointer on the bar: it widens (render-only, so the thumb's layout box stays the same) and shows its track.
        var layoutBox = thumb.Bounds;
        window.MouseMove(new Point(Mouse.Middle(window, bar).X, Gui.BoundsIn(window, bar).Bottom - 4));
        await Gui.UntilAsync(() => bar.IsExpanded, "the bar expanded");
        Gui.FinishAnimations();
        Assert.Equal(1, ScaleX(thumb), 3);
        Assert.Equal(1, track.Opacity, 3);
        Assert.Equal(layoutBox, thumb.Bounds);
        Assert.Equal(Argb(0x4DEEF0F4), Colour(pill.Background)); // the track, not the thumb, is under the pointer

        // On the thumb: lighter. Pressed and dragged: the accent, and the page scrolls.
        var onThumb = Mouse.Middle(window, thumb);
        window.MouseMove(onThumb);
        Gui.Settle();
        Gui.FinishAnimations();
        Assert.Equal(Argb(0x80EEF0F4), Colour(pill.Background));
        window.MouseDown(onThumb, MouseButton.Left);
        Gui.Settle();
        Gui.FinishAnimations();
        Assert.Equal(Colour(Brush(app, "AccentBrush")), Colour(pill.Background));
        window.MouseMove(onThumb + new Point(0, 100));
        Gui.Settle();
        Assert.True(scroller.Offset.Y > 0, $"offset {scroller.Offset.Y}");
        window.MouseUp(onThumb + new Point(0, 100), MouseButton.Left);
        Gui.Settle();
        Gui.FinishAnimations();
        Assert.Equal(Argb(0x80EEF0F4), Colour(pill.Background));

        // Away from the bar: back to slim and plain once its hide delay is over.
        window.MouseMove(Mouse.Middle(window, scroller));
        Gui.Settle();
        Gui.FinishAnimations();
        Assert.Equal(Argb(0x4DEEF0F4), Colour(pill.Background));
        await Gui.UntilAsync(() => !bar.IsExpanded, "the bar collapsed");
        Gui.FinishAnimations();
        Assert.Equal(0.5, ScaleX(thumb), 3);
        Assert.Equal(0, track.Opacity, 3);
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task Clicking_beside_the_thumb_pages_and_a_list_that_fits_has_no_bar()
    {
        await using var app = await OpenAsync();
        var window = app.Window;
        Gui.Click(window, NavButton(window, "Tracks"));
        await Gui.UntilAsync(() => app.ViewModel.TracksPage.CountText?.StartsWith("200", StringComparison.Ordinal) == true, "the tracks");
        var scroller = Gui.Find<ScrollViewer>(Page(app), "Scroller");
        var bar = scroller.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
        var below = Gui.Find<RepeatButton>(bar, "PART_PageDownButton");

        Gui.Click(window, below);

        Assert.Equal(scroller.Viewport.Height, scroller.Offset.Y, 1);

        // The sidebar's few playlists fit: its bar isn't shown at all.
        var sidebar = Gui.Find<SidebarView>(window, "SidebarView");
        var sidebarBar = BarsIn(sidebar).Single(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
        Assert.False(sidebarBar.IsVisible);
    }

    [AvaloniaFact]
    public async Task Switching_the_theme_repaints_the_scroll_bars()
    {
        await using var app = await OpenAsync();
        var window = app.Window;
        Gui.Click(window, NavButton(window, "Tracks"));
        await Gui.UntilAsync(() => app.ViewModel.TracksPage.CountText?.StartsWith("200", StringComparison.Ordinal) == true, "the tracks");
        var bar = Gui.Find<ScrollViewer>(Page(app), "Scroller").GetVisualDescendants().OfType<ScrollBar>()
            .Single(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
        var pill = Gui.Find<Border>(bar, "PART_Pill");
        var track = Gui.Find<Border>(bar, "PART_TrackFill");
        Gui.FinishAnimations();
        Assert.Equal((Argb(0x4DEEF0F4), Argb(0x0DEEF0F4)), (Colour(pill.Background), Colour(track.Background)));

        app.ViewModel.Settings.SetTheme(AppTheme.Light);
        Gui.Settle();
        Gui.FinishAnimations();
        Assert.Equal((Argb(0x4D1B1C1F), Argb(0x0D1B1C1F)), (Colour(pill.Background), Colour(track.Background)));

        app.ViewModel.Settings.SetTheme(AppTheme.Dark);
        Gui.Settle();
        Gui.FinishAnimations();
        Assert.Equal((Argb(0x4DEEF0F4), Argb(0x0DEEF0F4)), (Colour(pill.Background), Colour(track.Background)));
    }

    private static double ScaleX(Visual visual)
        => visual.RenderTransform is { } transform ? transform.Value.M11 : 1;

    private static IBrush Brush(AppHarness app, string key)
        => (IBrush)app.Window.FindResource(app.Window.ActualThemeVariant, key)!;

    private static Color Colour(IBrush? brush) => Assert.IsType<ISolidColorBrush>(brush, exactMatch: false).Color;

    private static Color Argb(uint argb) => Color.FromUInt32(argb);
}
