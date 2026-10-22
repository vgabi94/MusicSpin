using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using MusicSpin.App.Controls;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;
using MusicSpin.Core.Playback;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>Albums as a Shelf (#10), in the window.</summary>
public sealed class ShelfTests
{
    /// <summary>The shelf's transitions: the sleeve's turn (0.7 s after 60 ms) and the record coming out (1 s after 0.1 s).</summary>
    private const int Settled = 1300;

    /// <param name="live">Sleeves drawn live even at rest (no <see cref="ShelfSleeve.RestCache"/>), to compare with.</param>
    private static async Task<AppHarness> OpenAsync(AppTheme theme = AppTheme.Dark, AlbumGrouping grouping = AlbumGrouping.None, double width = 1440, bool live = false)
    {
        var app = await AppHarness.OpenAsync(new AppOptions
        {
            Scripted = true, Library = SampleLibrary.Create(), Covers = SampleLibrary.Covers(accents: true), Theme = theme, Width = width,
        });
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 10, "the albums");
        if (live)
        {
            app.Window.Styles.Add(new Avalonia.Styling.Style(x => Avalonia.Styling.Selectors.OfType<ShelfSleeve>(x)) { Setters = { new Avalonia.Styling.Setter(ShelfSleeve.RestCacheProperty, null) } });
        }

        app.ViewModel.AlbumsPage.Grouping = grouping;
        app.ViewModel.AlbumsPage.ViewChoice = app.ViewModel.AlbumsPage.Views[1];
        Gui.Settle();
        return app;
    }

    private static async Task PlayingAsync(AppHarness app, int album, PlayerState state = PlayerState.Playing)
    {
        app.Scripted!.Status = ScriptedPlayer.Playing(SampleLibrary.Albums[album - 1].Tracks[0], trackId: SampleLibrary.TrackId(album, 1), state: state);
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => app.ViewModel.PlayingAlbumId == SampleLibrary.AlbumId(album) && app.ViewModel.Player.State == state, "the album playing");
    }

    private static List<ShelfSleeve> Sleeves(Visual root) => [.. Gui.All<ShelfSleeve>(root)];

    private static ShelfSleeve Sleeve(Window window, string title)
        => Sleeves(window).Single(s => s.DataContext is AlbumItemViewModel a && a.Title == title);

    private static List<StackPanel> Boards(Window window) => [.. Gui.All<StackPanel>(window).Where(p => p.Classes.Contains("shelfBoard"))];

    /// <summary>The titles on each board, top to bottom.</summary>
    private static List<string> Layout(Window window)
        => [.. Boards(window).Select(b => string.Join(", ", Sleeves(b).Select(s => ((AlbumItemViewModel)s.DataContext!).Title)))];

    /// <summary>The sleeve whose slot is under <paramref name="point"/>, as the pointer finds it.</summary>
    private static ShelfSleeve? Under(Window window, Point point)
        => (window.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().OfType<ShelfSleeve>().FirstOrDefault();

    [AvaloniaFact]
    public async Task A_board_holds_as_many_sleeves_as_fit_with_room_for_a_hovered_and_a_playing_one()
    {
        await using var app = await OpenAsync();
        var window = app.Window;
        var page = app.ViewModel.AlbumsPage;

        Assert.Equal(8, page.PerBoard);
        Assert.Equal(["Velvet Hours, Copper Sun, Cello Suites, Partitas, Northern Lines, Glass Garden, Night Transit, Blue Hour",
            "Untitled Tape, Field Notes"], Layout(window));
        await PlayingAsync(app, 4);
        Gui.Hover(window, Sleeves(Boards(window)[0])[0]);
        Gui.FinishAnimations(Settled);

        var row = Gui.All<ItemsControl>(Boards(window)[0]).Single(c => c.Classes.Contains("shelfRow"));
        var sleeves = Sleeves(Boards(window)[0]);
        Assert.True(Gui.BoundsIn(window, sleeves[^1]).Right <= Gui.BoundsIn(window, row).Right - 30 + 0.5,
            $"the last sleeve ends at {Gui.BoundsIn(window, sleeves[^1]).Right}, the row at {Gui.BoundsIn(window, row).Right}");
        Assert.Equal(2, sleeves.Count(s => s.Turn == 1));
    }

    [AvaloniaFact]
    public async Task Moving_the_pointer_along_a_row_never_flickers_or_reflows_it()
    {
        await using var app = await OpenAsync();
        var window = app.Window;
        await PlayingAsync(app, 4);
        Gui.FinishAnimations(Settled);
        var layout = Layout(window);
        var rows = app.ViewModel.AlbumsPage.Rows;
        var board = Boards(window)[0];
        var sleeves = Sleeves(board);
        var row = Gui.All<ItemsControl>(board).Single(c => c.Classes.Contains("shelfRow"));
        var top = sleeves.Select(s => Gui.BoundsIn(window, s).Top).ToList();
        var y = Gui.BoundsIn(window, sleeves[0]).Center.Y;
        var lastHovered = -1;

        for (var x = Gui.BoundsIn(window, sleeves[0]).Left + 2; x < Gui.BoundsIn(window, sleeves[^1]).Right - 2; x += 6)
        {
            window.MouseMove(new Point(x, y));
            app.Advance(UiTime.Frame);

            var under = Under(window, new Point(x, y));
            Assert.NotNull(under);
            var index = sleeves.IndexOf(under);
            Assert.True(index >= lastHovered, $"at x = {x} the pointer went back from sleeve {lastHovered} to {index}");
            lastHovered = index;
            Assert.True(under.IsPointerOver);
            Assert.Single(sleeves, s => s.IsPointerOver);
            Assert.Equal(top, sleeves.Select(s => Gui.BoundsIn(window, s).Top));
            Assert.True(Gui.BoundsIn(window, sleeves[^1]).Right <= Gui.BoundsIn(window, row).Right + 0.5, $"the row overflows at x = {x}: {Gui.BoundsIn(window, sleeves[^1]).Right} > {Gui.BoundsIn(window, row).Right}; {string.Join(", ", sleeves.Select(s => $"{s.Bounds.Width}/{s.Turn:0.###}/{s.DesiredSize.Width}"))}");
        }

        Assert.Equal(sleeves.Count - 1, lastHovered);
        Assert.Same(rows, app.ViewModel.AlbumsPage.Rows);
        Assert.Equal(layout, Layout(window));
    }

    [AvaloniaFact]
    public async Task The_sleeve_under_the_pointer_stays_under_it_while_it_turns()
    {
        await using var app = await OpenAsync();
        var window = app.Window;
        var sleeve = Sleeves(Boards(window)[0])[2];
        var bounds = Gui.BoundsIn(window, sleeve);

        foreach (var at in new[] { new Point(bounds.Left + 2, bounds.Center.Y), new Point(bounds.Right - 2, bounds.Top + 2) })
        {
            window.MouseMove(new Point(bounds.Left - 300, bounds.Top - 60)); // off the shelf
            Gui.FinishAnimations(Settled);
            window.MouseMove(at);
            for (var t = 0; t < 60; t++)
            {
                app.Advance(UiTime.Frame);
                Assert.Same(sleeve, Under(window, at));
            }

            Assert.Equal(1, sleeve.Turn);
        }
    }

    [AvaloniaFact]
    public async Task A_hovered_sleeve_starts_turning_after_60_ms_and_turns_back_when_the_pointer_leaves()
    {
        await using var app = await OpenAsync();
        var window = app.Window;
        var sleeve = Sleeves(Boards(window)[0])[1];
        var closed = sleeve.SleeveProjection;

        Gui.Hover(window, sleeve);
        app.Advance(TimeSpan.FromMilliseconds(55));
        Assert.True(sleeve.IsPointerOver);
        Assert.Equal(0, sleeve.Turn);
        Assert.Equal(closed, sleeve.SleeveProjection);

        app.Advance(TimeSpan.FromMilliseconds(15)); // 10 ms into the turn
        Assert.InRange(sleeve.Turn, 0.001, 0.5);
        Gui.FinishAnimations(Settled);
        Assert.Equal(1, sleeve.Turn);
        Assert.NotEqual(closed, sleeve.SleeveProjection);

        window.MouseMove(new Point(5, 5));
        Gui.Settle();
        Assert.False(sleeve.IsPointerOver);
        Gui.FinishAnimations(Settled);
        Assert.Equal(0, sleeve.Turn);
        Assert.Equal(closed, sleeve.SleeveProjection);
    }

    [AvaloniaFact]
    public async Task A_sleeve_left_while_it_turns_turns_back_from_where_it_is()
    {
        await using var app = await OpenAsync();
        var window = app.Window;
        var sleeve = Sleeves(Boards(window)[0])[1];

        Gui.Hover(window, sleeve);
        app.Advance(TimeSpan.FromMilliseconds(200));
        var turned = sleeve.Turn;
        Assert.InRange(turned, 0.2, 0.99);

        window.MouseMove(new Point(5, 5));
        app.Advance(TimeSpan.FromMilliseconds(50)); // held for the delay …
        Assert.Equal(turned, sleeve.Turn, 6);
        app.Advance(TimeSpan.FromMilliseconds(60)); // … then back, not on to 1
        Assert.True(sleeve.Turn < turned, $"{sleeve.Turn} after {turned}");
        Gui.FinishAnimations(Settled);
        Assert.Equal(0, sleeve.Turn);
    }

    [AvaloniaFact]
    public async Task The_playing_sleeve_faces_you_with_its_record_half_out()
    {
        await using var app = await OpenAsync();
        var window = app.Window;
        await PlayingAsync(app, 4);
        Gui.FinishAnimations(Settled);
        var (playing, other) = (Sleeve(window, "Partitas"), Sleeve(window, "Copper Sun"));
        var size = playing.Size;

        Assert.True(playing.IsPlaying);
        Assert.Equal((1, 1, 1), (playing.Turn, playing.Pull, playing.RecordOut));
        Assert.Equal((0, 0, 0), (other.Turn, other.Pull, other.RecordOut));
        // Facing: the sleeve's right edge is nearly a full width out; turned away it shows about cos 58° of it.
        Assert.InRange(playing.SleeveProjection.Apply(size, size).X, 0.95 * size, 1.05 * size);
        Assert.InRange(other.SleeveProjection.Apply(size, size).X, 0.45 * size, 0.6 * size);
        // Half out: the record's left edge is past the sleeve's middle.
        Assert.InRange(playing.VinylProjection.Apply(0, 0).X, 0.5 * size, 0.6 * size);
        Assert.Equal(ShelfGeometry.SlotWidth(size, 58, 1, 1), playing.Bounds.Width, 0.5);
    }

    [AvaloniaFact]
    public async Task Tilt_and_depth_from_Settings_turn_the_sleeves_without_rebuilding_the_shelf()
    {
        await using var app = await OpenAsync();
        var window = app.Window;
        var sleeves = Sleeves(window);
        var before = sleeves.Select(s => s.SleeveProjection).ToList();
        var width = sleeves[0].Bounds.Width;

        app.ViewModel.Settings.SetShelfDepth(80);
        Gui.Settle();
        Assert.Equal(sleeves, Sleeves(window));
        Assert.All(sleeves, s => Assert.Equal(80, s.Depth));
        Assert.All(sleeves.Zip(before), p => Assert.NotEqual(p.Second, p.First.SleeveProjection));

        app.ViewModel.Settings.SetShelfTilt(60);
        Gui.Settle();
        Assert.Equal(8, app.ViewModel.AlbumsPage.PerBoard); // still 8 a board: the same sleeves
        Assert.Equal(sleeves, Sleeves(window));
        Assert.All(sleeves, s => Assert.Equal(60, s.Tilt));
        Assert.True(sleeves[0].Bounds.Width < width, $"{sleeves[0].Bounds.Width} vs {width}");
        Assert.Equal(ShelfGeometry.SlotWidth(sleeves[0].Size, 60, 0, 0), sleeves[0].Bounds.Width, 0.5);

        app.ViewModel.Settings.SetShelfTilt(30); // upright: fewer sleeves a board
        Gui.Settle();
        Assert.Equal(6, app.ViewModel.AlbumsPage.PerBoard);
        Assert.Equal([6, 4], Boards(window).Select(b => Sleeves(b).Count));
    }

    [AvaloniaFact]
    public async Task Resizing_the_window_lays_the_boards_out_again()
    {
        await using var app = await OpenAsync();
        var window = app.Window;
        Assert.Equal([8, 2], Boards(window).Select(b => Sleeves(b).Count));

        window.Width = 1280;
        Gui.Settle();
        Gui.Settle();

        Assert.Equal(6, app.ViewModel.AlbumsPage.PerBoard);
        Assert.Equal([6, 4], Boards(window).Select(b => Sleeves(b).Count));
    }

    [AvaloniaFact]
    public async Task Grouped_each_group_has_its_own_boards_under_its_header()
    {
        await using var app = await OpenAsync(grouping: AlbumGrouping.Artist);
        var window = app.Window;

        var headers = Gui.All<Grid>(window).Where(g => g.Classes.Contains("groupHeader")).ToList();
        Assert.Equal("DELTA RUTH · 1", headers[1].Children.OfType<TextBlock>().Single().Text);
        Assert.Equal(Gui.BoundsIn(window, headers[0]).Bottom, Gui.BoundsIn(window, Boards(window)[0]).Top, 0.5);
        Assert.Equal(["Velvet Hours", "Copper Sun", "Cello Suites, Partitas"], Layout(window).Take(3));
        var first = Gui.BoundsIn(window, Boards(window)[0]);
        Assert.Equal(first.Bottom + 34, Gui.BoundsIn(window, headers[1]).Top, 0.5);
    }

    [AvaloniaFact]
    public async Task Clicking_a_sleeve_plays_its_album_and_clicking_the_playing_one_opens_Now_Playing()
    {
        await using var app = await OpenAsync();
        var window = app.Window;

        Gui.Click(window, Sleeve(window, "Cello Suites"));
        await Gui.UntilAsync(() => app.Scripted!.Count("queue.set") == 1, "the album sent");
        Assert.Equal([SampleLibrary.TrackId(3, 1), SampleLibrary.TrackId(3, 2), SampleLibrary.TrackId(3, 3)],
            app.Scripted!.Sent("queue.set")[0].GetProperty("items").EnumerateArray().Select(i => i.GetProperty("trackId").GetString()));
        Assert.False(app.ViewModel.NowPlaying.IsOpen);

        await PlayingAsync(app, 3);
        Gui.Click(window, Sleeve(window, "Cello Suites"));

        Assert.True(app.ViewModel.NowPlaying.IsOpen);
        Assert.Equal(1, app.Scripted.Count("queue.set"));
    }

    // ---- #131: what a hover redraws ---------------------------------------------------------------------------------

    private static Control Record(ShelfSleeve sleeve) => Gui.Find<Control>(sleeve, "PART_Vinyl");

    /// <summary>Drawn through the image of it at rest, not live through its 3D projection.</summary>
    private static bool Cached(ShelfSleeve sleeve) => sleeve.CacheMode is not null && ReferenceEquals(sleeve.CacheMode, sleeve.RestCache);

    [AvaloniaFact]
    public async Task While_a_hovered_sleeve_turns_the_sleeves_moving_along_the_row_are_drawn_from_their_images()
    {
        await using var app = await OpenAsync();
        var window = app.Window;
        var sleeves = Sleeves(Boards(window)[0]);
        Assert.IsType<Avalonia.Media.BitmapCache>(sleeves[0].RestCache);
        Assert.All(sleeves, s => Assert.True(s.IsAtRest && Cached(s), $"{s.Title} isn't drawn from its image"));
        var hovered = sleeves[1];
        var after = Gui.BoundsIn(window, sleeves[2]).Left;

        Gui.Hover(window, hovered);
        for (var t = 0; t < 50; t++)
        {
            app.Advance(UiTime.Frame);
            Assert.All(sleeves.Where(s => s != hovered), s => Assert.True(Cached(s), $"{s.Title} at {t} frames"));
            if (hovered.Turn is > 0 and < 1)
            {
                Assert.False(Cached(hovered)); // turning: drawn live
            }
        }

        Assert.True(Gui.BoundsIn(window, sleeves[2]).Left > after, "the slot widens: the sleeves after it move along");
        Gui.FinishAnimations(Settled);
        Assert.Equal(1, hovered.Turn);
        Assert.True(Cached(hovered), "facing you, at rest: from its image again");
    }

    [AvaloniaFact]
    public async Task A_sleeve_left_while_it_turns_is_drawn_live_until_it_is_closed_again()
    {
        await using var app = await OpenAsync();
        var window = app.Window;
        var sleeve = Sleeves(Boards(window)[0])[3];

        Gui.Hover(window, sleeve);
        app.Advance(TimeSpan.FromMilliseconds(250));
        Assert.InRange(sleeve.Turn, 0.01, 0.99);
        window.MouseMove(new Point(5, 5)); // off it, mid-turn: it turns back from where it is
        app.Advance(TimeSpan.FromMilliseconds(150));
        Assert.InRange(sleeve.Turn, 0.01, 0.99);
        Assert.False(Cached(sleeve));

        Gui.FinishAnimations(Settled);
        Assert.Equal(0, sleeve.Turn);
        Assert.True(Cached(sleeve));
    }

    [AvaloniaFact]
    public async Task A_record_in_its_sleeve_isnt_drawn_and_the_playing_one_is_while_it_comes_out()
    {
        await using var app = await OpenAsync();
        var window = app.Window;
        Assert.All(Sleeves(window), s => Assert.False(Record(s).IsVisible, $"{s.Title}'s record, out of sight in its sleeve"));

        await PlayingAsync(app, 4);
        var playing = Sleeve(window, "Partitas");
        for (var t = 0; t < 30 && playing.RecordOut == 0; t++)
        {
            app.Advance(UiTime.Frame);
        }

        Assert.InRange(playing.RecordOut, 0.0001, 0.99); // coming out
        Assert.True(Record(playing).IsVisible);
        Assert.False(Cached(playing));
        Gui.FinishAnimations(Settled);
        Assert.True(Record(playing).IsVisible);
        Assert.False(Cached(playing), "its record spins: drawn live");
        Assert.InRange(playing.VinylProjection.Apply(0, 0).X, 0.5 * playing.Size, 0.6 * playing.Size); // placed while hidden, then out

        await PlayingAsync(app, 6); // another album: the record goes back in
        var next = Sleeve(window, "Night Transit");
        app.Advance(TimeSpan.FromMilliseconds(400));
        Assert.InRange(playing.RecordOut, 0.01, 0.99);
        Assert.True(Record(playing).IsVisible, "still going back in");
        Gui.FinishAnimations(Settled);
        Assert.False(Record(playing).IsVisible);
        Assert.True(Cached(playing));
        Assert.True(Record(next).IsVisible);
        Assert.False(Cached(next));
    }

    [AvaloniaTheory]
    [InlineData(0, 20)]
    [InlineData(100, 20)]
    [InlineData(100, 80)]
    public async Task A_sleeve_at_rest_looks_the_same_from_its_image_as_drawn_live(double depth, double tilt)
    {
        using var fromImages = await ShelfAsync(live: false);
        using var live = await ShelfAsync(live: true);
        var path = Path.Combine(Path.GetTempPath(), $"musicspin-shelf-live-{Guid.NewGuid():N}.png");
        try
        {
            Snapshots.Save(live, path);
            var result = Snapshots.Compare(fromImages, path, Path.Combine(Snapshots.FailureDirectory, $"shelf-cache-{depth}-{tilt}"),
                channelTolerance: 8, pixelTolerance: 0.0005);
            Assert.True(result.Matches, result.Problem);
        }
        finally
        {
            File.Delete(path);
        }

        // The shelf at this depth and tilt, its second sleeve facing you: every sleeve at rest.
        async Task<SkiaSharp.SKBitmap> ShelfAsync(bool live)
        {
            await using var app = await OpenAsync(AppTheme.Light, live: live);
            var window = app.Window;
            app.ViewModel.Settings.SetShelfDepth(depth);
            app.ViewModel.Settings.SetShelfTilt(tilt);
            Gui.Settle();
            Gui.Hover(window, Sleeves(Boards(window)[0])[1]);
            Gui.FinishAnimations(Settled);
            var sleeves = Sleeves(window);
            Assert.All(sleeves, s => Assert.True(s.IsAtRest && Cached(s) != live, $"{s.Title}"));

            // All a sleeve at rest draws is inside its frame, so its image starts on a whole pixel as the sleeve does.
            var frame = -((Thickness)window.FindResource("ShelfCacheFrame")!).Left;
            foreach (var s in sleeves)
            {
                var corners = new[] { (0d, 0d), (s.Size, 0d), (0d, s.Size), (s.Size, s.Size) }.Select(c => s.SleeveProjection.Apply(c.Item1, c.Item2));
                Assert.All(corners, c => Assert.True(c.X > -frame && c.Y > -frame, $"{s.Title}: {c} is outside the frame"));
            }

            return Capture(window);
        }
    }

    private static SkiaSharp.SKBitmap Capture(TopLevel top)
    {
        using var frame = top.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
        using var png = new MemoryStream();
        frame.Save(png, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        return SkiaSharp.SKBitmap.Decode(png.ToArray());
    }

    [AvaloniaFact]
    public async Task The_spine_takes_the_album_colour_in_shade()
    {
        await using var app = await OpenAsync();
        var sleeve = Sleeve(app.Window, "Northern Lines");

        sleeve.SpineColor = Avalonia.Media.Color.FromRgb(200, 100, 0);

        Assert.Equal(Avalonia.Media.Color.FromRgb(110, 55, 0), ((Avalonia.Media.ISolidColorBrush)sleeve.Spine!).Color);
        sleeve.SpineColor = null;
        Assert.Same(sleeve.SpineFallback, sleeve.Spine);
    }

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Snapshot_of_the_shelf_grouped_by_artist_with_a_hovered_and_a_playing_sleeve(AppTheme theme)
    {
        await using var app = await OpenAsync(theme, AlbumGrouping.Artist);
        await PlayingAsync(app, 8);
        Gui.Hover(app.Window, Sleeve(app.Window, "Velvet Hours"));
        Gui.FinishAnimations(Settled);

        Snapshots.Match(app.Window, $"albums-shelf-{theme.ToString().ToLowerInvariant()}");
    }

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Snapshot_of_the_shelf_ungrouped_with_the_sleeves_tilted(AppTheme theme)
    {
        await using var app = await OpenAsync(theme);
        await PlayingAsync(app, 2);
        Gui.FinishAnimations(Settled);

        Snapshots.Match(app.Window, $"albums-shelf-tilted-{theme.ToString().ToLowerInvariant()}");
    }
}
