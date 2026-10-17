using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using MusicSpin.App.Controls;
using MusicSpin.App.Services;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;
using MusicSpin.Core.Library;
using MusicSpin.Core.Remote;
using MusicSpin.Core.Settings;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// Artist and genre groups: ▶ Play queues what the group shows (#41), Radio starts its station (#23), and the station
/// on air shows across the window (#23): the group's button, the RADIO pill, Now Playing's kicker and the sidebar.
/// </summary>
public sealed class GroupRadioTests
{
    /// <summary>An artist whose name doesn't fit a crate's label, with one album.</summary>
    private const string LongArtist = "The Quiet Orchard Collective of the Far North";

    private static async Task<AppHarness> OpenAsync(AlbumGrouping grouping = AlbumGrouping.Artist, AppTheme theme = AppTheme.Dark,
        Action<StubLibrary>? library = null, AppSettings? settings = null)
    {
        var stub = SampleLibrary.Create();
        library?.Invoke(stub);
        var app = await AppHarness.OpenAsync(new AppOptions
        {
            Scripted = true, Library = stub, Covers = SampleLibrary.Covers(), Theme = theme, Settings = settings ?? new AppSettings(),
        });
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == stub.Albums.Count, "the albums");
        app.ViewModel.AlbumsPage.Grouping = grouping;
        Gui.Settle();
        return app;
    }

    private static void AddLongArtist(StubLibrary library)
    {
        library.Albums.Add(new AlbumSummary("a11", "Orchard Songs", LongArtist, 2001, "Folk", "FLAC", "16/44.1", 1, 200, false));
        library.Tracks.Add(new TrackSummary("a11-1", "/music/Orchard Songs/1.flac", "Apple Tree", LongArtist, "Orchard Songs", "a11", 1, "Folk", 200, "FLAC", false));
    }

    /// <summary>The header of group <paramref name="key"/>, scrolled to (the lists make only the rows in view).</summary>
    private static Grid Header(AppHarness app, string key)
    {
        var list = Gui.All<ItemsControl>(app.Window).First(l => l.Name is "AlbumGrid" or "TrackList" && l.IsEffectivelyVisible);
        var header = list.Items.OfType<GroupHeaderViewModel>().FirstOrDefault(h => h.Key == key)
                     ?? throw new InvalidOperationException($"No group '{key}' is shown.");
        list.ScrollIntoView(header);
        Gui.Settle();
        return Gui.All<Grid>(app.Window).First(g => g.Classes.Contains("groupHeader") && g.DataContext == header);
    }

    private static Button In(Visual host, string name) => host.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);

    private static string Label(Button button) => button.GetVisualDescendants().OfType<TextBlock>().Single().Text!;

    /// <summary>Scrolls <paramref name="control"/> into view and clicks it.</summary>
    private static void Click(AppHarness app, Control control)
    {
        control.BringIntoView();
        Gui.Settle();
        Gui.Click(app.Window, control);
    }

    /// <summary>The titles of the last queue.set's items.</summary>
    private static string[] Queued(AppHarness app)
        => [.. app.Scripted!.Sent("queue.set")[^1].GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()!)];

    private static async Task<string[]> PlayAsync(AppHarness app, Button play)
    {
        var before = app.Scripted!.Count("queue.set");
        Click(app, play);
        await Gui.UntilAsync(() => app.Scripted.Count("queue.set") == before + 1, "the queue sent");
        return Queued(app);
    }

    /// <summary>A station on air as if the CLI had started it (<c>music-spin radio</c>).</summary>
    private static async Task OnAirAsync(AppHarness app, RadioFilter station)
    {
        app.Scripted!.Status = app.Scripted.Status with { Radio = new RadioInfo(station, station.Describe(), 30) };
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => app.ViewModel.Radio.IsOnAir && RadioStation.Same(app.ViewModel.Radio.OnAir, RadioStation.TagsOf(station)), "the station on air");
    }

    private static double Left(Window window, Visual v) => v.TranslatePoint(default, window)!.Value.X;

    private static double Right(Window window, Visual v) => v.TranslatePoint(new Point(v.Bounds.Width, 0), window)!.Value.X;

    // ---- ▶ Play (#41) ------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task An_artist_plays_their_albums_in_the_order_shown_each_whole_in_one_read_and_one_queue_set()
    {
        await using var app = await OpenAsync(AlbumGrouping.Artist);
        var library = (StubLibrary)app.Services.Library;

        var queued = await PlayAsync(app, In(Header(app, "Elena Varga"), "GroupPlay"));

        // Cello Suites (2020), then Partitas (2023), as the cards show them; each in track order.
        Assert.Equal(["Prelude", "Allemande", "Courante", "Sinfonia", "Sarabande", "Gigue"], queued);
        Assert.Equal(1, library.AlbumsTracksReads);
        var sent = app.Scripted!.Sent("queue.set")[^1];
        Assert.True(sent.GetProperty("play").GetBoolean());
        Assert.False(sent.TryGetProperty("start", out _)); // so the player's shuffle, when on, orders the whole queue
        Assert.Equal(0, app.Scripted.Count("queue.add"));
    }

    [AvaloniaFact]
    public async Task A_genre_plays_in_the_sort_shown_and_with_a_search_only_what_matches()
    {
        await using var app = await OpenAsync(AlbumGrouping.Genre);
        app.ViewModel.AlbumsPage.Sort = LibrarySort.Year;
        Gui.Settle();

        // Classical by year: Cello Suites (2020), Glass Garden (2022), Partitas (2023).
        Assert.Equal(["Prelude", "Allemande", "Courante", "Glasshouse", "Fern", "Rain on Panes", "Sinfonia", "Sarabande", "Gigue"],
            await PlayAsync(app, In(Header(app, "Classical"), "GroupPlay")));

        app.ViewModel.SearchText = "Partitas";
        Gui.Settle();
        Assert.Equal(["Sinfonia", "Sarabande", "Gigue"], await PlayAsync(app, In(Header(app, "Classical"), "GroupPlay")));
    }

    [AvaloniaFact]
    public async Task An_artist_with_a_search_plays_the_albums_it_lets_through()
    {
        await using var app = await OpenAsync(AlbumGrouping.Artist);
        app.ViewModel.SearchText = "Cello";
        Gui.Settle();

        Assert.Equal(["Prelude", "Allemande", "Courante"], await PlayAsync(app, In(Header(app, "Elena Varga"), "GroupPlay")));
    }

    [AvaloniaFact]
    public async Task Tracks_by_genre_play_the_genres_tracks_as_listed_and_with_a_search_only_those_shown()
    {
        await using var app = await OpenAsync();
        app.ViewModel.Sidebar.Navigate(app.ViewModel.Sidebar.Tracks);
        await Gui.UntilAsync(() => app.ViewModel.TracksPage.Tracks.Count == 30, "the tracks");
        app.ViewModel.TracksPage.Grouping = TrackGrouping.Genre;
        Gui.Settle();

        Assert.Equal(["The Hollow", "Glass Window", "Salt Bloom", "Lantern Paper", "The Paper", "Ember Arc", "So Blue", "Dusk", "Bell"],
            await PlayAsync(app, In(Header(app, "Jazz"), "GroupPlay")));

        app.ViewModel.SearchText = "Blue";
        Gui.Settle();
        Assert.Equal(["So Blue", "Dusk", "Bell"], await PlayAsync(app, In(Header(app, "Jazz"), "GroupPlay")));
    }

    [AvaloniaFact]
    public async Task Play_sits_before_Radio_after_the_name_and_says_what_it_plays()
    {
        await using var app = await OpenAsync(AlbumGrouping.Artist);
        var header = Header(app, "Elena Varga");
        var (name, play, radio) = (header.Children.OfType<TextBlock>().Single(), In(header, "GroupPlay"), In(header, "GroupRadio"));

        Assert.Equal(12, Left(app.Window, play) - Right(app.Window, name), 0.5);
        Assert.True(Right(app.Window, play) < Left(app.Window, radio), "Play before Radio");
        Assert.Equal(("Play", "Radio"), (Label(play), Label(radio)));
        Assert.Equal("Play everything by Elena Varga", ToolTip.GetTip(play));
        Assert.Equal("Elena Varga radio", ToolTip.GetTip(radio));

        app.ViewModel.AlbumsPage.Grouping = AlbumGrouping.Genre;
        Gui.Settle();
        Assert.Equal("Play all Classical", ToolTip.GetTip(In(Header(app, "Classical"), "GroupPlay")));
    }

    [AvaloniaFact]
    public async Task Ungrouped_and_by_album_there_are_no_group_buttons()
    {
        await using var app = await OpenAsync(AlbumGrouping.None);
        Assert.DoesNotContain(Gui.All<Button>(app.Window), b => b.Name is "GroupPlay" or "GroupRadio");

        app.ViewModel.Sidebar.Navigate(app.ViewModel.Sidebar.Tracks);
        await Gui.UntilAsync(() => app.ViewModel.TracksPage.Tracks.Count == 30, "the tracks");
        Assert.Equal(TrackGrouping.Album, app.ViewModel.TracksPage.Grouping);
        Assert.DoesNotContain(Gui.All<Button>(app.Window), b => b.Name is "GroupPlay" or "GroupRadio");
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task Tracks_without_a_genre_play_but_have_no_station_and_Radio_says_why()
    {
        await using var app = await OpenAsync(AlbumGrouping.Genre);
        var header = Header(app, LibraryOrder.UnknownGenre);
        var radio = In(header, "GroupRadio");

        Assert.False(radio.IsEffectivelyEnabled);
        Assert.Equal(GroupActionsViewModel.NoStationTip, ToolTip.GetTip(radio));
        Assert.True(ToolTip.GetShowOnDisabled(radio));
        Assert.Equal(["Side A", "Side B", "Hiss"], await PlayAsync(app, In(header, "GroupPlay")));
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task A_busy_library_says_so_and_sends_nothing()
    {
        await using var app = await OpenAsync(AlbumGrouping.Artist);
        ((StubLibrary)app.Services.Library).AlbumsTracksFailure = LibraryService.BusyMessage;

        Click(app, In(Header(app, "Elena Varga"), "GroupPlay"));
        await Gui.UntilAsync(() => app.ViewModel.Banner == LibraryService.BusyMessage, "the notice");

        Assert.Equal(0, app.Scripted!.Count("queue.set"));
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task A_player_that_refuses_the_queue_says_so()
    {
        await using var app = await OpenAsync(AlbumGrouping.Artist);
        app.Scripted!.Fail("queue.set", 11, "None of the 6 queued file(s) could be played.");

        Click(app, In(Header(app, "Elena Varga"), "GroupPlay"));
        await Gui.UntilAsync(() => app.ViewModel.Banner == "None of the 6 queued file(s) could be played.", "the notice");
    }

    // ---- crate labels (#41, #23) -------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task On_a_crates_label_Play_and_Radio_are_icons_together_at_its_right_end()
    {
        await using var app = await OpenAsync(AlbumGrouping.Artist);
        app.ViewModel.AlbumsPage.ViewChoice = app.ViewModel.AlbumsPage.Views[2];
        Gui.Settle();
        var crate = Gui.All<Panel>(app.Window).First(p => p.Classes.Contains("crate") && p.DataContext is CrateViewModel { Name: "Elena Varga" });
        var box = crate.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("crateBox"));
        var (play, radio) = (In(crate, "GroupPlay"), In(crate, "GroupRadio"));

        Assert.All(new[] { play, radio }, b => Assert.False(b.GetVisualDescendants().OfType<TextBlock>().Single().IsVisible));
        Assert.True(Right(app.Window, play) <= Left(app.Window, radio), "Play before Radio");
        Assert.True(Right(app.Window, radio) < Right(app.Window, box) && Right(app.Window, box) - Right(app.Window, radio) < 12, "at the box's end");
        Assert.True(Left(app.Window, play) > (Left(app.Window, box) + Right(app.Window, box)) / 2, "on the right half");

        Assert.Equal(["Prelude", "Allemande", "Courante", "Sinfonia", "Sarabande", "Gigue"], await PlayAsync(app, play));
        Click(app, radio);
        await Gui.UntilAsync(() => app.Scripted!.Count("radio") == 1, "the station sent");
        Assert.Equal(["Elena Varga"], app.Scripted!.Sent("radio")[0].GetProperty("filter").GetProperty("artists").EnumerateArray().Select(a => a.GetString()));
    }

    [AvaloniaFact]
    public async Task A_long_name_on_a_crates_label_is_cut_and_its_count_stays_clear_of_the_icons()
    {
        await using var app = await OpenAsync(AlbumGrouping.Artist, library: AddLongArtist);
        app.ViewModel.AlbumsPage.ViewChoice = app.ViewModel.AlbumsPage.Views[2];
        Gui.Settle();
        var crate = Gui.All<Panel>(app.Window).First(p => p.Classes.Contains("crate") && p.DataContext is CrateViewModel { Name: LongArtist });
        var name = crate.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("name"));
        var count = crate.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("count"));

        Assert.Contains(name.TextLayout.TextLines, line => line.HasCollapsed);
        Assert.Equal(" · 1", count.Text);
        Assert.Equal(count.DesiredSize.Width, count.Bounds.Width, 0.5); // whole
        Assert.Equal(Right(app.Window, name), Left(app.Window, count), 0.5);
        Assert.True(Right(app.Window, count) < Left(app.Window, In(crate, "GroupPlay")), "the count stops before the icons");
    }

    // ---- Radio (#23) -------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Each_Radio_starts_its_station()
    {
        await using var app = await OpenAsync(AlbumGrouping.Artist);
        JsonElement Filter() => app.Scripted!.Sent("radio")[^1].GetProperty("filter");

        Click(app, In(Header(app, "Elena Varga"), "GroupRadio"));
        await Gui.UntilAsync(() => app.Scripted!.Count("radio") == 1, "the artist's station");
        Assert.Equal(["Elena Varga"], Filter().GetProperty("artists").EnumerateArray().Select(a => a.GetString()));
        Assert.True(app.Scripted!.Sent("radio")[^1].GetProperty("play").GetBoolean());

        app.ViewModel.AlbumsPage.Grouping = AlbumGrouping.Genre;
        Gui.Settle();
        Click(app, In(Header(app, "Classical"), "GroupRadio"));
        await Gui.UntilAsync(() => app.Scripted.Count("radio") == 2, "the genre's station");
        Assert.Equal(["Classical"], Filter().GetProperty("genres").EnumerateArray().Select(a => a.GetString()));
        Assert.False(Filter().TryGetProperty("artists", out _)); // a new station, not the artist's narrowed

        app.ViewModel.Sidebar.Navigate(app.ViewModel.Sidebar.Tracks);
        await Gui.UntilAsync(() => app.ViewModel.TracksPage.Tracks.Count == 30, "the tracks");
        app.ViewModel.TracksPage.Grouping = TrackGrouping.Genre;
        Gui.Settle();
        Click(app, In(Header(app, "Jazz"), "GroupRadio"));
        await Gui.UntilAsync(() => app.Scripted.Count("radio") == 3, "the tracks' genre's station");
        Assert.Equal(["Jazz"], Filter().GetProperty("genres").EnumerateArray().Select(a => a.GetString()));
    }

    [AvaloniaFact]
    public async Task The_station_on_airs_button_reads_On_air_and_opens_the_Radio_page()
    {
        await using var app = await OpenAsync(AlbumGrouping.Artist);
        var radio = In(Header(app, "Elena Varga"), "GroupRadio");
        Click(app, radio);
        await Gui.UntilAsync(() => Label(radio) == "On air", "On air");

        Assert.Contains("on", radio.Classes);
        Assert.True(radio.GetVisualDescendants().OfType<RadioIcon>().Single().IsPulsing);
        Assert.Equal("Radio", Label(In(Header(app, "Delta Ruth"), "GroupRadio")));
        app.ViewModel.NowPlaying.Open();

        Click(app, radio);
        Assert.Same(app.ViewModel.RadioPage, app.ViewModel.CurrentPage);
        Assert.Same(app.ViewModel.Sidebar.Radio, app.ViewModel.Sidebar.Active);
        Assert.False(app.ViewModel.NowPlaying.IsOpen);
        Assert.Equal(1, app.Scripted!.Count("radio")); // no second station
    }

    [AvaloniaFact]
    public async Task A_station_started_from_the_CLI_lights_the_pill_the_kicker_the_sidebar_and_its_group()
    {
        await using var app = await OpenAsync(AlbumGrouping.Genre);
        var pill = Gui.Find<Button>(app.Window, "OnAirPill");
        var sidebarRadio = Gui.All<Button>(Gui.Find<ItemsControl>(app.Window, "LibraryItems")).Single(b => b.DataContext == app.ViewModel.Sidebar.Radio);
        Assert.False(pill.IsVisible);
        Assert.DoesNotContain("onAir", sidebarRadio.Classes);

        // music-spin radio --genre classical: the player matches genres ignoring case, and so does the app.
        await OnAirAsync(app, new RadioFilter { Genres = ["classical"] });

        Assert.True(pill.IsVisible);
        Assert.Equal("Radio · classical", ToolTip.GetTip(pill));
        Assert.Equal("Radio · classical", app.ViewModel.NowPlaying.Kicker);
        Assert.Contains("onAir", sidebarRadio.Classes);
        Assert.True(sidebarRadio.GetVisualDescendants().OfType<RadioIcon>().Single().IsPulsing);
        Assert.Equal("On air", Label(In(Header(app, "Classical"), "GroupRadio")));
        Assert.Equal("Radio", Label(In(Header(app, "Jazz"), "GroupRadio")));

        await OnAirAsync(app, new RadioFilter { Genres = ["Classical"], ExcludeArtists = ["Ilse Morrow"] });
        Assert.Equal("Radio", Label(In(Header(app, "Classical"), "GroupRadio"))); // another station: it narrows the genre
        Assert.Equal("Radio · Classical · not Ilse Morrow", ToolTip.GetTip(pill));

        app.Scripted!.Status = app.Scripted.Status with { Radio = null };
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => !app.ViewModel.Radio.IsOnAir, "radio off");
        Assert.False(pill.IsVisible);
        Assert.Null(app.ViewModel.NowPlaying.Kicker);
        Assert.DoesNotContain("onAir", sidebarRadio.Classes);
        Assert.False(sidebarRadio.GetVisualDescendants().OfType<RadioIcon>().Single().IsPulsing);
    }

    [AvaloniaFact]
    public async Task The_RADIO_pill_opens_the_Radio_page_and_closes_Now_Playing()
    {
        await using var app = await OpenAsync();
        await OnAirAsync(app, new RadioFilter());
        app.ViewModel.NowPlaying.Open();
        Gui.FinishAnimations();

        Gui.Click(app.Window, Gui.Find<Button>(app.Window, "OnAirPill"));

        Assert.Same(app.ViewModel.RadioPage, app.ViewModel.CurrentPage);
        Assert.False(app.ViewModel.NowPlaying.IsOpen);
    }

    [AvaloniaFact]
    public async Task The_sidebars_waves_pulse_only_while_on_air_and_are_still_at_Motion_None()
    {
        await using var app = await OpenAsync();
        var icon = Gui.All<Button>(Gui.Find<ItemsControl>(app.Window, "LibraryItems")).Single(b => b.DataContext == app.ViewModel.Sidebar.Radio)
            .GetVisualDescendants().OfType<RadioIcon>().Single();
        (double, double) Waves() => (icon.NearOpacity, icon.FarOpacity);
        void Pulse() => UiTime.AdvanceAnimations(TimeSpan.FromMilliseconds(900)); // half a pulse (1.8 s): the inner waves at their faintest

        Pulse();
        Assert.Equal((1d, 1d), Waves());

        await OnAirAsync(app, new RadioFilter());
        Pulse();
        Assert.True(icon.IsPulsing);
        Assert.True(icon.NearOpacity < 0.5, $"inner waves at {icon.NearOpacity}");
        Assert.True(icon.FarOpacity > icon.NearOpacity, "the outer waves follow a little later");

        app.ViewModel.Settings.MotionChoice = app.ViewModel.Settings.Motions.Single(m => m.Value == MotionMode.None);
        Gui.Settle();
        Pulse();
        Assert.False(icon.IsPulsing);
        Assert.Equal((1d, 1d), Waves());

        app.ViewModel.Settings.MotionChoice = app.ViewModel.Settings.Motions.Single(m => m.Value == MotionMode.Fast);
        Gui.Settle();
        UiTime.AdvanceAnimations(TimeSpan.FromMilliseconds(450)); // half a pulse at half the duration
        Assert.True(icon.IsPulsing);
        Assert.True(icon.NearOpacity < 0.5, $"inner waves at {icon.NearOpacity} at Fast");
    }

    [AvaloniaFact]
    public async Task Playing_an_album_a_track_a_playlist_or_a_Now_Playing_track_ends_radio()
    {
        await using var app = await OpenAsync(AlbumGrouping.None, library: l =>
        {
            l.Playlists.Add(new PlaylistSummaryItem("p1", "Evening", 2));
            l.PlaylistEntries["p1"] = [.. l.Tracks.Take(2)];
        });
        var vm = app.ViewModel;
        async Task EndsAsync(string what, Func<Task> play)
        {
            await OnAirAsync(app, new RadioFilter { Genres = ["Jazz"] });
            var sent = app.Scripted!.Count("queue.set");
            await play();
            await Gui.UntilAsync(() => !vm.Radio.IsOnAir, $"radio ended by {what}");
            Assert.True(app.Scripted.Count("queue.set") > sent, $"{what} sent a new queue");
        }

        await EndsAsync("an album", async () =>
        {
            Gui.Click(app.Window, Gui.All<Button>(app.Window).First(b => b.Classes.Contains("card")));
            await app.PlaySceneAsync();
        });
        await EndsAsync("a track", async () =>
        {
            vm.Sidebar.Navigate(vm.Sidebar.Tracks);
            await Gui.UntilAsync(() => vm.TracksPage.Tracks.Count == 30, "the tracks");
            await vm.TracksPage.PlayTrackCommand.ExecuteAsync(vm.TracksPage.Tracks[4]);
        });
        await EndsAsync("a playlist", async () =>
        {
            vm.Sidebar.Navigate(Assert.Single(vm.Sidebar.Playlists));
            await Gui.UntilAsync(() => vm.CurrentPage is PlaylistPageViewModel { Tracks.Count: 2 }, "the playlist");
            await ((PlaylistPageViewModel)vm.CurrentPage).PlayAllCommand.ExecuteAsync(null);
        });
        await EndsAsync("a Now Playing track", async () =>
        {
            await vm.NowPlaying.ShowAlbumAsync(SampleLibrary.AlbumId(3));
            await vm.NowPlaying.PlayTrackCommand.ExecuteAsync(vm.NowPlaying.Tracks[1]);
        });
    }

    // ---- snapshots ---------------------------------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Snapshot_of_a_group_header_and_a_crate_label_with_both_buttons(AppTheme theme)
    {
        await using var app = await OpenAsync(AlbumGrouping.Genre, theme);
        Snapshots.Match(app.Window, Header(app, "Ambient"), $"group-header-{theme.ToString().ToLowerInvariant()}");

        app.ViewModel.AlbumsPage.ViewChoice = app.ViewModel.AlbumsPage.Views[2];
        Gui.Settle();
        var crate = Gui.All<Panel>(app.Window).First(p => p.Classes.Contains("crate") && p.DataContext is CrateViewModel { Name: "Ambient" });
        // The crate's whole cell (it is drawn scaled down, so the box's own bounds aren't where it shows).
        Snapshots.Match(app.Window, crate.GetVisualAncestors().OfType<Viewbox>().First(v => v.Classes.Contains("crateCell")),
            $"crate-label-{theme.ToString().ToLowerInvariant()}");
    }

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Snapshot_of_the_player_bar_and_a_group_header_on_air(AppTheme theme)
    {
        // Still waves: a pulse would make the frame depend on when it is taken.
        await using var app = await OpenAsync(AlbumGrouping.Genre, theme, settings: new AppSettings { Motion = "none" });
        app.Scripted!.SetQueue([SampleLibrary.Item(4, 2)], radio: new RadioFilter { Genres = ["Classical"] });
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => app.ViewModel.Radio.IsOnAir && app.ViewModel.PlayerTitle == "Sarabande", "the station on air");

        Snapshots.Match(app.Window, Gui.Find<App.Views.PlayerBar>(app.Window, "PlayerBarView"), $"player-bar-radio-{theme.ToString().ToLowerInvariant()}");
        Snapshots.Match(app.Window, Header(app, "Classical"), $"group-header-on-air-{theme.ToString().ToLowerInvariant()}");
    }
}
