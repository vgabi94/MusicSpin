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
using MusicSpin.Core.Settings;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>The Radio page (#24): composing a station from tags, against the sample library and a scripted player.</summary>
public sealed class RadioPageTests
{
    private static async Task<AppHarness> OpenAsync(AppTheme theme = AppTheme.Dark, AppSettings? settings = null, Action<AppHarness>? before = null)
    {
        var library = SampleLibrary.Create();
        // Two more artists with a track each: eleven in all, so the Artists row shows ten and "+1 more".
        foreach (var (id, artist) in new[] { ("a11", "Quinn Ferro"), ("a12", "Rosa Lind") })
        {
            library.Albums.Add(new AlbumSummary(id, artist + " Live", artist, 2010, "Jazz", "MP3", "16/44.1", 1, 180, false));
            library.Tracks.Add(new TrackSummary(id + "-1", $"/music/{id}/1.mp3", "Live at Noon", artist, artist + " Live", id, 1, "Jazz", 180, "MP3", false));
        }

        var app = await AppHarness.OpenAsync(new AppOptions
        {
            Scripted = true, Library = library, Covers = SampleLibrary.Covers(), Theme = theme, Settings = settings ?? new AppSettings(),
        });
        before?.Invoke(app);
        app.ViewModel.Sidebar.Navigate(app.ViewModel.Sidebar.Radio);
        await Gui.UntilAsync(() => Page(app).Rows.Count > 0, "the tags");
        await CountedAsync(app);
        return app;
    }

    private static RadioPageViewModel Page(AppHarness app) => app.ViewModel.RadioPage;

    private static StubLibrary Library(AppHarness app) => (StubLibrary)app.Services.Library;

    /// <summary>Lets the count's delay pass and waits for the pool's size.</summary>
    private static async Task CountedAsync(AppHarness app)
    {
        var reads = Library(app).RadioPoolReads;
        app.Advance(RadioPageViewModel.CountDelay);
        await Gui.UntilAsync(() => Library(app).RadioPoolReads > reads && Page(app).Pool is not null, "the pool counted");
        Gui.Settle();
    }

    private static Button Tag(AppHarness app, string label)
        => Gui.All<Button>(app.Window).Single(b => b.Classes.Contains("tag") && b.DataContext is RadioChipViewModel c && c.Label == label);

    private static RadioChipViewModel Chip(AppHarness app, string label) => (RadioChipViewModel)Tag(app, label).DataContext!;

    private static RadioFacetViewModel Row(AppHarness app, string title) => Page(app).Rows.Single(r => r.Title == title);

    private static string[] Labels(AppHarness app, string row) => [.. Row(app, row).Items.OfType<RadioChipViewModel>().Select(c => c.Label)];

    private static string? Note(AppHarness app, string row) => Row(app, row).Items.OfType<RadioNote>().SingleOrDefault()?.Text;

    private static string Description(AppHarness app) => string.Concat(Page(app).Description.Select(p => p.Text));

    private static void RightClick(AppHarness app, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), app.Window)!.Value;
        app.Window.MouseMove(point);
        app.Window.MouseDown(point, MouseButton.Right);
        app.Window.MouseUp(point, MouseButton.Right);
        Gui.Settle();
    }

    private static void Click(AppHarness app, Control control)
    {
        control.BringIntoView();
        Gui.Settle();
        Gui.Click(app.Window, control);
    }

    // ---- tags ----------------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task A_click_cycles_a_tag_off_included_left_out_off_and_a_right_click_leaves_it_out_at_once()
    {
        await using var app = await OpenAsync();

        Click(app, Tag(app, "Jazz"));
        Assert.Equal(RadioTagState.Included, Chip(app, "Jazz").State);
        Assert.Contains("inc", Tag(app, "Jazz").Classes);
        Assert.Equal("+", Chip(app, "Jazz").Mark);

        Click(app, Tag(app, "Jazz"));
        Assert.Equal(RadioTagState.LeftOut, Chip(app, "Jazz").State);
        Assert.Contains("exc", Tag(app, "Jazz").Classes);
        Assert.Equal("−", Chip(app, "Jazz").Mark);
        Assert.True(Tag(app, "Jazz").GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Rectangle>().Single().IsVisible); // the dashes

        Click(app, Tag(app, "Jazz"));
        Assert.Equal(RadioTagState.Off, Chip(app, "Jazz").State);
        Assert.Empty(Page(app).Draft);

        RightClick(app, Tag(app, "Ambient"));
        Assert.Equal(RadioTagState.LeftOut, Chip(app, "Ambient").State);
        RightClick(app, Tag(app, "Ambient"));
        Assert.Equal(RadioTagState.Off, Chip(app, "Ambient").State);
        Click(app, Tag(app, "Ambient"));
        RightClick(app, Tag(app, "Ambient"));
        Assert.Equal(RadioTagState.LeftOut, Chip(app, "Ambient").State);
    }

    [AvaloniaFact]
    public async Task Hi_Res_is_only_on_or_off()
    {
        await using var app = await OpenAsync();

        Click(app, Tag(app, "Hi-Res"));
        Assert.Equal(RadioTagState.Included, Chip(app, "Hi-Res").State);
        RightClick(app, Tag(app, "Hi-Res"));
        Assert.Equal(RadioTagState.Included, Chip(app, "Hi-Res").State); // can't be left out
        Click(app, Tag(app, "Hi-Res"));
        Assert.Equal(RadioTagState.Off, Chip(app, "Hi-Res").State);
        RightClick(app, Tag(app, "Hi-Res"));
        Assert.Equal(RadioTagState.Off, Chip(app, "Hi-Res").State);
    }

    [AvaloniaFact]
    public async Task Each_row_shows_its_tags_with_their_counts_and_the_artists_with_the_most_tracks()
    {
        await using var app = await OpenAsync();

        Assert.Equal(["Genres", "Decades", "Formats", "Artists", "Albums", "Words"], Page(app).Rows.Select(r => r.Title));
        Assert.Equal(["Ambient", "Classical", "Electronic", "Jazz"], Labels(app, "Genres"));
        Assert.Equal(["6", "9", "3", "11"], Row(app, "Genres").Items.OfType<RadioChipViewModel>().Select(c => c.Count));
        Assert.Equal(["1950s", "1970s", "2010s", "2020s"], Labels(app, "Decades"));
        Assert.Equal(["FLAC", "MP3", "Hi-Res"], Labels(app, "Formats"));
        Assert.Equal("18", Chip(app, "Hi-Res").Count);
        // Elena Varga has six tracks, the others three, Quinn Ferro and Rosa Lind one: Rosa Lind is the one left out.
        Assert.Equal(RadioPageViewModel.TopArtists, Labels(app, "Artists").Length);
        Assert.Equal("Elena Varga", Labels(app, "Artists")[0]);
        Assert.DoesNotContain("Rosa Lind", Labels(app, "Artists"));
        Assert.Equal("+1 more · search to find them", Note(app, "Artists"));
        Assert.Empty(Labels(app, "Albums"));
        Assert.Equal("Search to add an album", Note(app, "Albums"));
        Assert.Equal("Search to match words in titles, like “live”", Note(app, "Words"));
        Assert.Equal(RadioPageViewModel.Hint, Gui.All<TextBlock>(app.Window).Single(t => t.Classes.Contains("radioHint")).Text);
    }

    [AvaloniaFact]
    public async Task The_search_filters_the_tags_and_chosen_tags_stay_first()
    {
        await using var app = await OpenAsync();
        Click(app, Tag(app, "Jazz"));
        Click(app, Tag(app, "Jazz")); // left out

        app.ViewModel.SearchText = "lind";
        Gui.Settle();

        Assert.Equal(["Jazz"], Labels(app, "Genres")); // chosen: still there, first
        Assert.Equal(["Rosa Lind", "Tomas Lind"], Labels(app, "Artists").Order(StringComparer.Ordinal));
        Assert.Null(Note(app, "Artists"));
        Assert.DoesNotContain(Page(app).Rows, r => r.Title is "Decades" or "Formats");
        Assert.Equal(["“lind”"], Labels(app, "Words"));

        app.ViewModel.SearchText = "glass";
        Gui.Settle();
        Assert.Equal(["Glass Garden"], Labels(app, "Albums"));
        Click(app, Tag(app, "Glass Garden"));
        app.ViewModel.SearchText = string.Empty;
        Gui.Settle();
        Assert.Equal(["Glass Garden"], Labels(app, "Albums")); // chosen: it stays without the search
        Assert.Equal("Glass Garden · not Jazz", Description(app));
    }

    [AvaloniaFact]
    public async Task The_search_finds_each_word_anywhere_as_on_the_other_pages_and_albums_name_their_artist()
    {
        await using var app = await OpenAsync();

        app.ViewModel.SearchText = "ruth delta";
        Gui.Settle();
        Assert.Equal(["Delta Ruth"], Labels(app, "Artists"));

        app.ViewModel.SearchText = "copper";
        Gui.Settle();
        Assert.Equal("Copper Sun — Delta Ruth", ToolTip.GetTip(Tag(app, "Copper Sun")));
        Assert.StartsWith("Copper Sun — Delta Ruth, 3 tracks", Chip(app, "Copper Sun").Name, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Typed_words_are_a_tag_counted_a_moment_after_the_typing()
    {
        await using var app = await OpenAsync();

        app.ViewModel.SearchText = "live";
        Gui.Settle();
        Assert.Null(Chip(app, "“live”").Count);
        await CountedAsync(app);

        Assert.Equal("2", Chip(app, "“live”").Count);
        Click(app, Tag(app, "“live”"));
        Assert.Equal([new RadioTag(RadioTagKind.Words, "live")], Page(app).Draft);
    }

    // ---- the station card ---------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task The_size_line_and_Nothing_matches_which_turns_Start_off()
    {
        await using var app = await OpenAsync();
        var start = Gui.Find<Button>(app.Window, "StartStation");
        Assert.Equal("32 tracks · 11 artists · 1 h 56 min", Page(app).PoolText);
        Assert.Equal("Whole library", Description(app));
        Assert.Equal("NEW STATION", Gui.Find<TextBlock>(app.Window, "StationKicker").Text);
        Assert.True(start.IsEffectivelyEnabled);
        Assert.False(Gui.Find<Button>(app.Window, "ClearTags").IsVisible);

        Click(app, Tag(app, "Electronic"));
        await CountedAsync(app);
        Assert.Equal("3 tracks · 1 artist · 11 min", Page(app).PoolText);

        Click(app, Tag(app, "Hi-Res"));
        await CountedAsync(app);
        Assert.Equal(RadioPageViewModel.NothingMatches, Page(app).PoolText);
        Assert.Equal(RadioPageViewModel.NothingMatches, Gui.Find<TextBlock>(app.Window, "PoolText").Text);
        Assert.False(start.IsEffectivelyEnabled);

        Click(app, Gui.Find<Button>(app.Window, "ClearTags"));
        await CountedAsync(app);
        Assert.Empty(Page(app).Draft);
        Assert.True(start.IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public async Task Counts_wait_until_the_tags_have_been_still_for_a_moment()
    {
        await using var app = await OpenAsync();
        var library = Library(app);
        var reads = library.RadioPoolReads;

        foreach (var genre in new[] { "Jazz", "Classical", "Ambient" })
        {
            Click(app, Tag(app, genre));
            app.Advance(RadioPageViewModel.CountDelay / 2);
            Gui.Settle();
        }

        Assert.Equal(reads, library.RadioPoolReads); // never still long enough
        app.Advance(RadioPageViewModel.CountDelay);
        await Gui.UntilAsync(() => library.RadioPoolReads == reads + 1, "one count");
        Assert.Equal(["Jazz", "Classical", "Ambient"], library.RadioPoolFilters[^1].Genres!);
        await Gui.UntilAsync(() => Page(app).Pool?.Tracks == 26, "the three genres' size");
    }

    [AvaloniaFact]
    public async Task Start_sends_the_filter_music_spin_radio_builds_from_the_same_options()
    {
        await using var app = await OpenAsync();
        Click(app, Tag(app, "Jazz"));
        Click(app, Tag(app, "Classical"));
        RightClick(app, Tag(app, "Delta Ruth"));
        RightClick(app, Tag(app, "MP3"));
        Click(app, Tag(app, "Hi-Res"));
        Click(app, Tag(app, "2020s"));
        await CountedAsync(app);

        Click(app, Gui.Find<Button>(app.Window, "StartStation"));
        await Gui.UntilAsync(() => app.Scripted!.Count("radio") == 1, "the station sent");

        // music-spin radio --genre Jazz --genre Classical --years 2020-2029 --hires --exclude-artist "Delta Ruth" --exclude-format mp3
        var cli = new RadioFilter
        {
            Genres = ["Jazz", "Classical"], Years = [new YearRange(2020, 2029)], HiRes = true, ExcludeArtists = ["Delta Ruth"], ExcludeFormats = ["mp3"],
        };
        var sent = app.Scripted!.Sent("radio")[0];
        Assert.Equal(ProtocolJson.Serialize(cli), sent.GetProperty("filter").GetRawText());
        Assert.True(sent.GetProperty("play").GetBoolean());
    }

    [AvaloniaFact]
    public async Task The_station_that_starts_is_the_one_music_spin_radio_plays_with_the_same_filters()
    {
        // The real player, reading the library on disk.
        await using var app = await AppHarness.OpenAsync(new AppOptions
        {
            PlayerReadsLibrary = true,
            Music = music =>
            {
                TestImages.Album(music, "Kind of Blue", "Miles Davis", tracks: 3, year: 1959);
                TestImages.Album(music, "Doo-Bop", "Miles Davis", tracks: 2, year: 1992);
                TestImages.Album(music, "Blue Train", "John Coltrane", tracks: 2, year: 1957);
            },
        });
        app.ViewModel.Sidebar.Navigate(app.ViewModel.Sidebar.Radio);
        await Gui.UntilAsync(() => Page(app).Rows.Count > 0, "the tags");
        Click(app, Tag(app, "Miles Davis"));
        Click(app, Tag(app, "1950s"));
        app.Advance(RadioPageViewModel.CountDelay);
        await Gui.UntilAsync(() => Page(app).PoolText?.StartsWith("3 tracks", StringComparison.Ordinal) == true, "the pool counted");

        Click(app, Gui.Find<Button>(app.Window, "StartStation"));
        await Gui.UntilAsync(() => app.ViewModel.Player.Status?.Radio is not null && app.ViewModel.Player.Path is not null, "the station playing");

        // music-spin radio --artist "Miles Davis" --years 1950-1959
        var cli = new RadioFilter { Artists = ["Miles Davis"], Years = [new YearRange(1950, 1959)] };
        var expected = app.Read(library => library.RadioPool(cli)).Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
        var radio = app.ViewModel.Player.Status!.Radio!;
        Assert.Equal(ProtocolJson.Serialize(cli), ProtocolJson.Serialize(radio.Filter));
        Assert.Equal(expected.Count, radio.PoolSize);
        Assert.Contains(app.ViewModel.Player.Path!, expected);
        Assert.True(Page(app).IsLive);
    }

    [AvaloniaFact]
    public async Task On_air_the_card_ends_radio_and_shows_the_next_three_picks()
    {
        await using var app = await OpenAsync();
        Click(app, Tag(app, "Jazz"));
        await CountedAsync(app);
        Click(app, Gui.Find<Button>(app.Window, "StartStation"));
        await Gui.UntilAsync(() => Page(app).IsLive, "on air");
        // The player's queue on air: the current track, one up next, then the station's picks.
        app.Scripted!.SetQueue([SampleLibrary.Item(7, 1), SampleLibrary.Item(1, 1), SampleLibrary.Item(8, 2), SampleLibrary.Item(9, 1),
            SampleLibrary.Item(7, 3), SampleLibrary.Item(8, 1)], current: 0, upNext: 1, radio: new RadioFilter { Genres = ["Jazz"] });
        await app.Scripted.PushAsync("queueChanged");
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => Page(app).NextPicks.Count == RadioPageViewModel.NextPickCount, "the next picks");

        Assert.Equal("ON AIR", Gui.Find<TextBlock>(app.Window, "StationKicker").Text);
        Assert.Contains("live", Gui.Find<Border>(app.Window, "Station").Classes);
        Assert.Equal(["The Paper", "So Blue", "Salt Bloom"], Page(app).NextPicks.Select(p => p.Title));
        Assert.True(Gui.Find<Border>(app.Window, "NextPicks").IsVisible);
        Assert.False(Gui.Find<Button>(app.Window, "StartStation").IsVisible);
        Assert.False(Gui.Find<Button>(app.Window, "UndoStation").IsVisible);

        Click(app, Gui.Find<Button>(app.Window, "EndStation"));
        await Gui.UntilAsync(() => app.Scripted.Count("radio.end") == 1, "End radio sent");
        // One atomic radio.end: the app doesn't replace or clear the queue itself.
        Assert.Equal(0, app.Scripted.Count("queue.set"));
        Assert.Equal(0, app.Scripted.Count("queue.clear"));
    }

    [AvaloniaFact]
    public async Task With_another_station_on_air_the_card_offers_Switch_and_Undo_changes()
    {
        await using var app = await OpenAsync();
        app.Scripted!.Status = app.Scripted.Status with { Radio = new RadioInfo(new RadioFilter { Genres = ["Jazz"] }, "genre Jazz", 11) };
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => Page(app).IsLive, "the CLI's station as the draft");
        Assert.Equal("Jazz", Description(app));
        Assert.Equal(RadioTagState.Included, Chip(app, "Jazz").State);

        Click(app, Tag(app, "Ambient"));
        await CountedAsync(app);
        Assert.False(Page(app).IsLive);
        Assert.Equal("NEW STATION", Gui.Find<TextBlock>(app.Window, "StationKicker").Text);
        var start = Gui.Find<Button>(app.Window, "StartStation");
        Assert.Equal("Switch to this station", Page(app).StartLabel);
        Assert.True(start.IsVisible && start.IsEffectivelyEnabled);
        Assert.True(Gui.Find<Button>(app.Window, "UndoStation").IsVisible);
        Assert.False(Gui.Find<Button>(app.Window, "EndStation").IsVisible);

        Click(app, Gui.Find<Button>(app.Window, "UndoStation"));
        Assert.True(Page(app).IsLive);
        Assert.Equal("Jazz", Description(app));

        Click(app, Tag(app, "Ambient"));
        await CountedAsync(app);
        Click(app, start);
        await Gui.UntilAsync(() => app.Scripted.Count("radio") == 1, "the switch");
        Assert.Equal(["Jazz", "Ambient"], app.Scripted.Sent("radio")[0].GetProperty("filter").GetProperty("genres").EnumerateArray().Select(g => g.GetString()));
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task A_busy_library_leaves_the_count_owed_without_a_notice_and_it_lands_at_the_next_check()
    {
        await using var app = await OpenAsync();
        var library = Library(app);
        library.RadioPoolFailure = LibraryService.BusyMessage; // a scan from the CLI holds the library
        var reads = library.RadioPoolReads;

        Click(app, Tag(app, "Jazz"));
        app.Advance(RadioPageViewModel.CountDelay);
        await Gui.UntilAsync(() => library.RadioPoolReads == reads + 1, "the refused count");

        Assert.Equal(RadioPageViewModel.Counting, Page(app).PoolText); // not the whole library's size from before
        Assert.False(Gui.Find<Button>(app.Window, "StartStation").IsEffectivelyEnabled);
        Assert.Null(app.ViewModel.Banner); // the count runs in the background: no notice

        library.RadioPoolFailure = null;
        app.Advance(LibraryService.PollInterval);
        await Gui.UntilAsync(() => Page(app).Pool is not null, "the owed count");
        Assert.Equal("11 tracks · 5 artists · 42 min", Page(app).PoolText);
        Assert.Null(app.ViewModel.Banner);
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task Until_a_new_draft_is_counted_the_card_says_so_and_Start_waits()
    {
        await using var app = await OpenAsync();
        var start = Gui.Find<Button>(app.Window, "StartStation");
        Assert.True(start.IsEffectivelyEnabled);

        // Hi-Res Electronic matches nothing; Start right away must not go by the whole library's count.
        Click(app, Tag(app, "Electronic"));
        Click(app, Tag(app, "Hi-Res"));

        Assert.Equal(RadioPageViewModel.Counting, Gui.Find<TextBlock>(app.Window, "PoolText").Text);
        Assert.False(start.IsEffectivelyEnabled);
        await CountedAsync(app);
        Assert.Equal(RadioPageViewModel.NothingMatches, Page(app).PoolText);
        Assert.False(start.IsEffectivelyEnabled);
        Assert.Equal(0, app.Scripted!.Count("radio"));
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task A_slow_count_that_ends_after_a_later_one_is_dropped()
    {
        await using var app = await OpenAsync();
        var library = Library(app);
        var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        library.RadioPoolHold = filter => filter.Genres is ["Jazz"] ? slow.Task : null;
        var reads = library.RadioPoolReads;

        Click(app, Tag(app, "Jazz"));
        app.Advance(RadioPageViewModel.CountDelay);
        await Gui.UntilAsync(() => library.RadioPoolReads == reads + 1, "the slow count under way");
        Click(app, Tag(app, "Ambient"));
        await CountedAsync(app);
        Assert.Equal("17 tracks · 7 artists · 1 h 1 min", Page(app).PoolText); // Jazz or Ambient

        var finished = Page(app).CountsFinished;
        slow.SetResult();
        await Gui.UntilAsync(() => Page(app).CountsFinished == finished + 1, "the slow count ended");
        Assert.Equal("17 tracks · 7 artists · 1 h 1 min", Page(app).PoolText); // not Jazz alone
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task End_radio_the_player_refuses_says_why_and_stays_on_air()
    {
        await using var app = await OpenAsync();
        app.Scripted!.SetQueue([SampleLibrary.Item(7, 1), SampleLibrary.Item(8, 2)], radio: new RadioFilter { Genres = ["Jazz"] });
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => Page(app).IsLive, "on air");
        app.Scripted.Fail("radio.end", 13, "The player is shutting down.");

        Click(app, Gui.Find<Button>(app.Window, "EndStation"));
        await Gui.UntilAsync(() => app.ViewModel.Banner?.Contains("shutting down", StringComparison.Ordinal) == true, "the notice");

        Assert.Equal(0, app.Scripted.Count("queue.set"));
        Assert.Equal(0, app.Scripted.Count("queue.clear"));
        Assert.True(app.ViewModel.Radio.IsOnAir);
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task End_radio_after_the_station_went_off_air_sends_nothing()
    {
        await using var app = await OpenAsync();
        app.Scripted!.SetQueue([SampleLibrary.Item(7, 1), SampleLibrary.Item(8, 2)]);
        await app.Scripted.PushAsync("stateChanged");

        await app.ViewModel.Radio.EndAsync();

        Assert.Equal(0, app.Scripted.Count("radio.end"));
        Assert.Equal(0, app.Scripted.Count("queue.set"));
        Assert.Equal(0, app.Scripted.Count("queue.clear"));
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task A_station_the_player_refuses_says_why()
    {
        await using var app = await OpenAsync();
        app.Scripted!.Fail("radio", 12, "No library tracks match the radio filters (genre Jazz).");

        Click(app, Tag(app, "Jazz"));
        await CountedAsync(app);
        Click(app, Gui.Find<Button>(app.Window, "StartStation"));

        await Gui.UntilAsync(() => app.ViewModel.Banner == "No library tracks match the radio filters (genre Jazz).", "the notice");
        Assert.False(Page(app).IsLive);
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task An_empty_library_says_how_to_add_music()
    {
        await using var app = await AppHarness.OpenAsync(new AppOptions { Scripted = true, Library = new StubLibrary(), Covers = new StubCovers() });
        app.ViewModel.Sidebar.Navigate(app.ViewModel.Sidebar.Radio);
        await Gui.UntilAsync(() => Page(app).EmptyText is not null, "the empty page");

        Assert.Equal(RadioPageViewModel.EmptyLibrary, Gui.Find<TextBlock>(app.Window, "EmptyText").Text);
    }

    // ---- snapshots ---------------------------------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Snapshot_of_a_draft_with_included_and_left_out_tags(AppTheme theme)
    {
        await using var app = await OpenAsync(theme);
        Click(app, Tag(app, "Jazz"));
        Click(app, Tag(app, "Electronic"));
        RightClick(app, Tag(app, "Kodo Arai"));
        RightClick(app, Tag(app, "MP3"));
        Click(app, Tag(app, "1950s"));
        await CountedAsync(app);

        Snapshots.Match(app.Window, $"radio-draft-{theme.ToString().ToLowerInvariant()}");
    }

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Snapshot_of_the_page_on_air(AppTheme theme)
    {
        // Still waves, so the frame doesn't depend on when it is taken.
        await using var app = await OpenAsync(theme, new AppSettings { Motion = "none" });
        app.Scripted!.SetQueue([SampleLibrary.Item(7, 1), SampleLibrary.Item(8, 2), SampleLibrary.Item(9, 1), SampleLibrary.Item(7, 3)],
            radio: new RadioFilter { Genres = ["Jazz"] });
        await app.Scripted.PushAsync("queueChanged");
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => Page(app).IsLive && Page(app).NextPicks.Count == 3, "on air");
        await CountedAsync(app);

        Snapshots.Match(app.Window, $"radio-on-air-{theme.ToString().ToLowerInvariant()}");
    }
}
