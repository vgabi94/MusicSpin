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

/// <summary>Albums in Crates (#11), in the window.</summary>
public sealed class CrateTests
{
    /// <summary>A flip: 0.8 s.</summary>
    private const int Settled = 1000;

    private static async Task<AppHarness> OpenAsync(AppTheme theme = AppTheme.Dark, AlbumGrouping grouping = AlbumGrouping.None, double height = 880)
    {
        var app = await AppHarness.OpenAsync(new AppOptions
        {
            Scripted = true, Library = SampleLibrary.Create(), Covers = SampleLibrary.Covers(), Theme = theme, Height = height,
        });
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 10, "the albums");
        app.ViewModel.AlbumsPage.Grouping = grouping;
        app.ViewModel.AlbumsPage.ViewChoice = app.ViewModel.AlbumsPage.Views[2];
        Gui.Settle();
        return app;
    }

    private static async Task PlayingAsync(AppHarness app, int album, PlayerState state = PlayerState.Playing)
    {
        app.Scripted!.Status = ScriptedPlayer.Playing(SampleLibrary.Albums[album - 1].Tracks[0], trackId: SampleLibrary.TrackId(album, 1), state: state);
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => app.ViewModel.PlayingAlbumId == SampleLibrary.AlbumId(album) && app.ViewModel.Player.State == state, "the album playing");
    }

    private static List<Panel> Crates(Window window) => [.. Gui.All<Panel>(window).Where(p => p.Classes.Contains("crate") && p.IsEffectivelyVisible)];

    private static CrateViewModel Crate(Panel crate) => (CrateViewModel)crate.DataContext!;

    private static string? Label(Panel crate)
        => Gui.All<TextBlock>(Gui.All<Border>(crate).Single(b => b.Classes.Contains("crateBox"))).Single(t => t.IsEffectivelyVisible).Text;

    /// <summary>The front record's title in each crate shown.</summary>
    private static List<string> Fronts(Window window) => [.. Crates(window).Select(c => Crate(c).Front!.Title)];

    private static CrateRecord Record(Panel crate, int position)
        => Gui.All<CrateRecord>(crate).Single(r => ((CrateRecordViewModel)r.DataContext!).Position == position);

    /// <summary>A point on the record at <paramref name="position"/> that it alone shows: the strip above the next record forward.</summary>
    private static Point Strip(Window window, Panel crate, int position)
    {
        var (record, before) = (Record(crate, position), Record(crate, position - 1));
        var size = record.Bounds.Width;
        var top = record.RecordProjection.Apply(size / 2, 0).Y;
        var cover = before.RecordProjection.Apply(size / 2, 0).Y;
        return record.TranslatePoint(new Point(size / 2, (top + cover) / 2), window)!.Value;
    }

    private static Point Middle(Window window, CrateRecord record)
        => record.TranslatePoint(record.RecordProjection.Apply(record.Bounds.Width / 2, record.Bounds.Height / 2), window)!.Value;

    private static void ClickAt(Window window, Point point)
    {
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Gui.Settle();
    }

    private static void Wheel(Window window, Panel crate, double notches)
    {
        window.MouseWheel(Gui.BoundsIn(window, crate).Center, new Vector(0, -notches));
        Gui.Settle();
    }

    [AvaloniaFact]
    public async Task Ungrouped_one_crate_fills_the_view_labelled_A_to_Z_or_by_years()
    {
        await using var app = await OpenAsync();
        var window = app.Window;

        var crate = Assert.Single(Crates(window));
        Assert.Equal("A — Z", Label(crate));
        Assert.True(crate.Bounds.Height >= 470, $"{crate.Bounds.Height} high");
        Assert.Equal("Velvet Hours", Crate(crate).Front!.Title);
        // The front record, then the ones behind up to nine back; the rest aren't made.
        Assert.Equal(Enumerable.Range(0, 10), Gui.All<CrateRecord>(crate).Select(r => ((CrateRecordViewModel)r.DataContext!).Position).Order());

        app.ViewModel.AlbumsPage.Sort = LibrarySort.Year;
        Gui.Settle();
        Assert.Equal("1959 — 2024", Label(Crates(window)[0]));
    }

    [AvaloniaFact]
    public async Task Records_behind_the_front_one_recede_and_darken_and_flipped_ones_fall_out_of_view()
    {
        await using var app = await OpenAsync();
        var window = app.Window;
        app.ViewModel.AlbumsPage.FlipActiveCrate(1);
        Gui.FinishAnimations(Settled);
        var crate = Crates(window)[0];

        var (flipped, front, behind, further) = (Record(crate, -1), Record(crate, 0), Record(crate, 1), Record(crate, 5));
        var tops = new[] { front, behind, further }.Select(r => Gui.BoundsIn(window, r).Top + r.RecordProjection.Apply(r.Bounds.Width / 2, 0).Y).ToList();
        Assert.True(tops[0] > tops[1] && tops[1] > tops[2], string.Join(", ", tops)); // each further back shows above the one in front
        Assert.Equal(0, flipped.GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "PART_Record").Opacity);
        Assert.Equal(0, Shade(front));
        Assert.Equal(0.07, Shade(behind), 3);
        Assert.Equal(0.35, Shade(further), 3);
        Assert.Equal(0.45, CrateRecord.Pose(20).Brightness);
        Assert.Equal(0, CrateRecord.Pose(10).Opacity);
    }

    private static double Shade(CrateRecord record) => record.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Shade").Opacity;

    [AvaloniaFact]
    public async Task One_wheel_notch_flips_one_record_and_a_fast_burst_flips_one_at_a_time()
    {
        await using var app = await OpenAsync();
        var window = app.Window;
        var crate = Crates(window)[0];

        Wheel(window, crate, 1);
        Assert.Equal(1, Crate(crate).FrontIndex);

        for (var i = 0; i < 5; i++)
        {
            Wheel(window, crate, 1); // within 180 ms of the last flip: held back
        }

        Assert.Equal(1, Crate(crate).FrontIndex);
        app.Time.Advance(TimeSpan.FromMilliseconds(200));
        Wheel(window, crate, 1);
        Assert.Equal(2, Crate(crate).FrontIndex);

        app.Time.Advance(TimeSpan.FromMilliseconds(200));
        Wheel(window, crate, -1);
        Assert.Equal(1, Crate(crate).FrontIndex);
    }

    [AvaloniaFact]
    public async Task Small_touchpad_turns_add_up_to_one_flip()
    {
        await using var app = await OpenAsync();
        var window = app.Window;
        var crate = Crates(window)[0];

        for (var i = 0; i < 4; i++)
        {
            Wheel(window, crate, 0.1);
            app.Time.Advance(TimeSpan.FromMilliseconds(50));
        }

        Assert.Equal(0, Crate(crate).FrontIndex);
        Wheel(window, crate, 0.1);
        Assert.Equal(1, Crate(crate).FrontIndex);
    }

    [AvaloniaFact]
    public async Task Past_either_end_the_wheel_scrolls_the_page()
    {
        await using var app = await OpenAsync(grouping: AlbumGrouping.Genre, height: 700);
        var window = app.Window;
        var scroller = Gui.Find<ScrollViewer>(window, "CrateScroller");
        var ambient = Crates(window)[0];
        Assert.True(scroller.Extent.Height > scroller.Viewport.Height, $"{scroller.Extent} in {scroller.Viewport}");

        Wheel(window, ambient, 1); // Field Notes to the front: the last of two
        Assert.Equal(1, Crate(ambient).FrontIndex);
        Assert.Equal(0, scroller.Offset.Y);

        app.Time.Advance(TimeSpan.FromMilliseconds(200));
        Wheel(window, ambient, 1);
        Assert.Equal(1, Crate(ambient).FrontIndex);
        Assert.True(scroller.Offset.Y > 0, "the page scrolls down");
        var scrolled = scroller.Offset.Y;

        Wheel(window, ambient, -1);
        Assert.Equal(0, Crate(ambient).FrontIndex);
        Assert.Equal(scrolled, scroller.Offset.Y);

        app.Time.Advance(TimeSpan.FromMilliseconds(200));
        Wheel(window, ambient, -1);
        Assert.Equal(0, Crate(ambient).FrontIndex);
        Assert.True(scroller.Offset.Y < scrolled, "the page scrolls up");
    }

    [AvaloniaFact]
    public async Task The_arrow_keys_flip_the_active_crate_unless_a_text_box_or_a_slider_has_focus()
    {
        await using var app = await OpenAsync(grouping: AlbumGrouping.Genre);
        var window = app.Window;
        var classical = Crates(window)[1];
        ClickAt(window, Strip(window, classical, 1)); // makes Classical the active crate
        Assert.Same(Crate(classical), app.ViewModel.AlbumsPage.ActiveCrate);
        Assert.Equal(1, Crate(classical).FrontIndex);

        Gui.Press(window, Key.Right, PhysicalKey.ArrowRight);
        Assert.Equal(2, Crate(classical).FrontIndex);
        Gui.Press(window, Key.Right, PhysicalKey.ArrowRight); // the last record: stays
        Assert.Equal(2, Crate(classical).FrontIndex);
        Gui.Press(window, Key.Up, PhysicalKey.ArrowUp);
        Assert.Equal(1, Crate(classical).FrontIndex);
        Gui.Press(window, Key.Left, PhysicalKey.ArrowLeft);
        Assert.Equal(0, Crate(classical).FrontIndex);
        Gui.Press(window, Key.Down, PhysicalKey.ArrowDown);
        Assert.Equal(1, Crate(classical).FrontIndex);
        Assert.Equal(0, Crate(Crates(window)[0]).FrontIndex); // the other crates stay

        Gui.Find<TextBox>(window, "SearchBox").Focus();
        Gui.Settle();
        Gui.Press(window, Key.Right, PhysicalKey.ArrowRight);
        Assert.Equal(1, Crate(classical).FrontIndex);

        Gui.Find<ScrubBar>(window, "PositionBar").Focus();
        Gui.Settle();
        Gui.Press(window, Key.Left, PhysicalKey.ArrowLeft);
        Assert.Equal(1, Crate(classical).FrontIndex);
    }

    [AvaloniaFact]
    public async Task Clicking_a_record_behind_brings_it_to_the_front_and_clicking_the_front_one_plays_it()
    {
        await using var app = await OpenAsync();
        var window = app.Window;
        var crate = Crates(window)[0];

        ClickAt(window, Strip(window, crate, 2));
        Assert.Equal("Cello Suites", Crate(crate).Front!.Title);
        Assert.Equal(0, app.Scripted!.Count("queue.set"));
        Gui.FinishAnimations(Settled);

        ClickAt(window, Middle(window, Record(crate, 0)));
        await Gui.UntilAsync(() => app.Scripted.Count("queue.set") == 1, "the album sent");
        Assert.Equal([SampleLibrary.TrackId(3, 1), SampleLibrary.TrackId(3, 2), SampleLibrary.TrackId(3, 3)],
            app.Scripted.Sent("queue.set")[0].GetProperty("items").EnumerateArray().Select(i => i.GetProperty("trackId").GetString()));
    }

    [AvaloniaFact]
    public async Task The_side_panel_shows_the_active_crates_front_record_and_plays_or_queues_it()
    {
        await using var app = await OpenAsync(grouping: AlbumGrouping.Genre);
        var window = app.Window;
        var side = Gui.Find<StackPanel>(window, "CrateSide");
        Assert.Equal(["AMBIENT · 2024", "Northern Lines", "Halvard Eik", "FLAC", "24/96", "3 tracks", "Play", "Queue"], Texts(side));

        var jazz = Crates(window)[3];
        ClickAt(window, Strip(window, jazz, 1));
        Assert.Equal(["JAZZ · 1973", "Copper Sun", "Delta Ruth", "FLAC", "16/44.1", "3 tracks", "Play", "Queue"], Texts(side));
        Assert.Contains("active", jazz.Classes);
        Assert.DoesNotContain("active", Crates(window)[0].Classes);

        Gui.Click(window, Gui.Find<Button>(window, "CratePlay"));
        await Gui.UntilAsync(() => app.Scripted!.Count("queue.set") == 1, "the album sent");
        Assert.Equal(SampleLibrary.TrackId(8, 1), app.Scripted!.Sent("queue.set")[0].GetProperty("items")[0].GetProperty("trackId").GetString());
        await PlayingAsync(app, 8);
        Assert.Contains("Pause", Texts(side));
        Gui.Click(window, Gui.Find<Button>(window, "CratePlay"));
        await Gui.UntilAsync(() => app.Scripted.Count("toggle") == 1, "pause");

        Gui.Click(window, Gui.Find<Button>(window, "CrateQueue"));
        await Gui.UntilAsync(() => app.ViewModel.Menu.IsOpen, "the queue menu");
        Assert.Equal(["Play next", "Add to queue"], app.ViewModel.Menu.Entries.Select(e => e.Label));
        Gui.Click(window, Gui.All<Button>(Gui.Find<Border>(window, "MenuBox")).Last());
        await Gui.UntilAsync(() => app.Scripted.Count("queue.add") == 1, "the album queued");
        var added = app.Scripted.Sent("queue.add")[0];
        Assert.Equal("upnext", added.GetProperty("position").GetString());
        Assert.Equal(SampleLibrary.TrackId(8, 1), added.GetProperty("items")[0].GetProperty("trackId").GetString());
    }

    [AvaloniaFact]
    public async Task Hovering_a_record_only_lightens_it()
    {
        await using var app = await OpenAsync();
        var window = app.Window;
        var crate = Crates(window)[0];
        Gui.FinishAnimations(Settled);
        var (front, behind) = (Record(crate, 0), Record(crate, 1));
        var (frontAt, behindAt) = (front.RecordProjection, behind.RecordProjection);

        window.MouseMove(Strip(window, crate, 1));
        Gui.FinishAnimations(Settled);

        Assert.True(behind.IsPointerOver);
        Assert.False(front.IsPointerOver);
        Assert.Equal(0.07, Light(behind), 3);
        Assert.Equal(0, Light(front));
        Assert.Equal(behindAt, behind.RecordProjection);
        Assert.Equal(frontAt, front.RecordProjection);
    }

    private static double Light(CrateRecord record) => record.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Light").Opacity;

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Snapshot_of_one_crate(AppTheme theme)
    {
        await using var app = await OpenAsync(theme);
        app.ViewModel.AlbumsPage.FlipActiveCrate(1);
        Gui.FinishAnimations(Settled);

        Snapshots.Match(app.Window, $"albums-crate-{theme.ToString().ToLowerInvariant()}");
    }

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Snapshot_of_crates_grouped_by_genre(AppTheme theme)
    {
        await using var app = await OpenAsync(theme, AlbumGrouping.Genre);
        Gui.FinishAnimations(Settled);

        Snapshots.Match(app.Window, $"albums-crates-genre-{theme.ToString().ToLowerInvariant()}");
    }

    private static List<string?> Texts(Visual root) => [.. root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text)];
}
