using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using MusicSpin.App.Controls;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;
using MusicSpin.App.Views.Pages;
using MusicSpin.Core.Playback;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>The Tracks page (#12), in the window.</summary>
public sealed class TracksPageTests
{
    private static async Task<AppHarness> OpenAsync(AppTheme theme = AppTheme.Dark, StubLibrary? library = null, int tracks = 30)
    {
        var app = await AppHarness.OpenAsync(new AppOptions
        {
            Scripted = true, Library = library ?? SampleLibrary.Create(), Covers = SampleLibrary.Covers(), Theme = theme,
        });
        app.ViewModel.Sidebar.NavigateCommand.Execute(app.ViewModel.Sidebar.Tracks);
        await Gui.UntilAsync(() => app.ViewModel.TracksPage.Tracks.Count == tracks, "the tracks");
        return app;
    }

    private static async Task PlayingAsync(AppHarness app, int album, int track, PlayerState state = PlayerState.Playing)
    {
        app.Scripted!.Status = ScriptedPlayer.Playing(SampleLibrary.Albums[album - 1].Tracks[track - 1], trackId: SampleLibrary.TrackId(album, track), state: state);
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => app.ViewModel.TracksPage.PlayingAlbumId == SampleLibrary.AlbumId(album)
            && app.ViewModel.Player.TrackId == SampleLibrary.TrackId(album, track) && app.ViewModel.Player.State == state, "the track playing");
    }

    private static Border Row(Window window, string title)
        => Gui.All<Border>(window).Single(b => b.Classes.Contains("track") && b.DataContext is TrackRow r && r.Item.Title == title);

    private static List<string?> Texts(Visual root)
        => [.. root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text)];

    private static T Child<T>(Visual root, string @class)
        where T : Control
        => root.GetVisualDescendants().OfType<T>().Single(c => c.Classes.Contains(@class));

    [AvaloniaFact]
    public async Task Each_grouping_shows_the_columns_its_header_does_not_name()
    {
        await using var app = await OpenAsync();
        var window = app.Window;
        var page = app.ViewModel.TracksPage;

        var row = Row(window, "The Hollow");
        Assert.Equal(["1", "The Hollow", "3:19"], Texts(row));
        Assert.Empty(row.GetVisualDescendants().OfType<CoverView>());
        var header = Gui.All<Border>(window).First(b => b.Classes.Contains("albumHeader"));
        Assert.Equal(["Velvet Hours", "Ana Cruz Quintet · 2017 · FLAC 24/96"], Texts(header));
        Assert.Equal(2, header.GetVisualDescendants().OfType<Button>().Count());

        page.Grouping = TrackGrouping.Genre;
        Gui.Settle();
        Assert.Equal(["Aurora", "Halvard Eik", "Northern Lines", "2:37"], Texts(Row(window, "Aurora")));
        Assert.Single(Row(window, "Aurora").GetVisualDescendants().OfType<CoverView>());
        Assert.Equal("AMBIENT · 6 TRACKS", Gui.All<Grid>(window).First(g => g.Classes.Contains("groupHeader")).Children.OfType<TextBlock>().Single().Text);

        page.Grouping = TrackGrouping.None;
        Gui.Settle();
        Assert.Equal(["The Hollow", "Ana Cruz Quintet", "Velvet Hours", "Jazz", "3:19"], Texts(Row(window, "The Hollow")));
        Assert.DoesNotContain(Gui.All<Grid>(window), g => g.Classes.Contains("groupHeader"));
    }

    [AvaloniaFact]
    public async Task The_toolbar_has_the_Sort_and_Group_dropdowns()
    {
        await using var app = await OpenAsync();
        var window = app.Window;

        Assert.Equal(["Sort: ", "Artist"], Texts(Gui.Find<Button>(window, "SortButton")));
        Assert.Equal(["Group: ", "Album"], Texts(Gui.Find<Button>(window, "GroupButton")));

        Gui.Click(window, Gui.Find<Button>(window, "GroupButton"));
        var entries = Gui.All<Button>(Gui.Find<Border>(window, "MenuBox")).ToList();
        Assert.Equal(["None", "Album", "Genre"], entries.Select(e => Texts(e).Single()));
        Gui.Click(window, entries[2]);
        Assert.Equal(TrackGrouping.Genre, app.ViewModel.TracksPage.Grouping);
        Assert.Equal(["Group: ", "Genre"], Texts(Gui.Find<Button>(window, "GroupButton")));
    }

    [AvaloniaFact]
    public async Task The_current_track_highlight_follows_playback()
    {
        await using var app = await OpenAsync();
        var window = app.Window;

        await PlayingAsync(app, 7, 2);
        Assert.Equal(["Glass Window"], Current(window));
        Assert.True(Child<Icon>(Row(window, "Glass Window"), "note").IsEffectivelyVisible); // ♪ instead of its number
        Assert.Contains("playing", Gui.All<Border>(window).First(b => b.Classes.Contains("albumHeader")).Classes);

        await PlayingAsync(app, 7, 2, PlayerState.Paused);
        Assert.Equal(["Glass Window"], Current(window));
        Assert.Equal(["2", "Glass Window", "3:50"], Texts(Row(window, "Glass Window")));

        await PlayingAsync(app, 8, 1);
        Assert.Equal(["Lantern Paper"], Current(window));
    }

    [AvaloniaFact]
    public async Task Double_clicking_a_row_plays_it_but_a_single_click_on_its_title_does_not()
    {
        await using var app = await OpenAsync();
        var window = app.Window;
        var title = Child<TextBlock>(Row(window, "Salt Bloom"), "title");

        var point = title.TranslatePoint(new Point(4, title.Bounds.Height / 2), window)!.Value; // on the text
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        await app.PlayerRoundTripAsync();
        Assert.Equal(0, app.Scripted!.Count("queue.play"));

        Gui.DoubleClick(window, title);
        await Gui.UntilAsync(() => app.Scripted.Count("queue.play") >= 1, "the track played");
        await app.PlayerRoundTripAsync();
        Assert.Equal(1, app.Scripted.Count("queue.play")); // the double-click played it once
        var sent = app.Scripted.Sent("queue.play")[0];
        Assert.Equal(2, sent.GetProperty("start").GetInt32());
        Assert.Equal(30, sent.GetProperty("items").GetArrayLength());
    }

    [AvaloniaFact]
    public async Task Clicking_a_rows_number_or_thumbnail_plays_it()
    {
        await using var app = await OpenAsync();
        var window = app.Window;

        var row = Row(window, "Ember Arc");
        Gui.Hover(window, row);
        Assert.True(Child<Icon>(row, "play").IsEffectivelyVisible); // ▶ instead of the number
        Gui.Click(window, Child<Button>(row, "number"));
        await Gui.UntilAsync(() => app.Scripted!.Count("queue.play") == 1, "the track played");
        await app.PlayerRoundTripAsync(); // the play command has its answer: its buttons are enabled again for the next click
        Assert.Equal(5, app.Scripted!.Sent("queue.play")[0].GetProperty("start").GetInt32());

        app.ViewModel.TracksPage.Grouping = TrackGrouping.None;
        Gui.Settle();
        Gui.Click(window, Child<Button>(Row(window, "Glass Window"), "thumb"));
        await Gui.UntilAsync(() => app.Scripted.Count("queue.play") == 2, "the track played");
        Assert.Equal(1, app.Scripted.Sent("queue.play")[1].GetProperty("start").GetInt32());
    }

    [AvaloniaFact]
    public async Task A_row_cannot_be_played_again_until_the_player_has_answered_the_first_play()
    {
        await using var app = await OpenAsync();
        var window = app.Window;
        app.ViewModel.TracksPage.Grouping = TrackGrouping.None; // rows with a thumbnail play button
        Gui.Settle();
        var unanswered = app.Scripted!.Hold("queue.play");
        Button other;
        try
        {
            Gui.Click(window, Child<Button>(Row(window, "Ember Arc"), "thumb"));
            await Gui.UntilAsync(() => app.Scripted.Count("queue.play") == 1, "the first play reached the player");
            Gui.Settle();
            other = Child<Button>(Row(window, "Glass Window"), "thumb");
            Assert.False(other.IsEffectivelyEnabled); // one play at a time: no second queue.play for a double click
        }
        finally
        {
            unanswered.TrySetResult(); // a failed assert must not leave the app waiting for an answer at disposal
        }

        await app.PlayerRoundTripAsync();
        Gui.Settle();
        Assert.True(other.IsEffectivelyEnabled);
        Gui.Click(window, other);
        await Gui.UntilAsync(() => app.Scripted.Count("queue.play") == 2, "the second play, once the first was answered");
    }

    [AvaloniaFact]
    public async Task Plus_opens_Play_next_and_Add_to_queue_and_the_album_header_acts_on_the_album()
    {
        await using var app = await OpenAsync();
        var window = app.Window;
        var row = Row(window, "The Paper");
        Gui.Hover(window, row);
        Assert.Equal(1, Child<Button>(row, "add").Opacity);

        Gui.Click(window, Child<Button>(row, "add"));
        var menu = Gui.Find<Border>(window, "MenuBox");
        Assert.True(menu.IsEffectivelyVisible);
        var entries = Gui.All<Button>(menu).ToList();
        Assert.Equal(["Play next", "Add to queue", "Add to playlist"], entries.Select(e => Texts(e).Single()));
        Gui.Click(window, entries[0]);
        await Gui.UntilAsync(() => app.Scripted!.Count("queue.add") == 1, "Play next");
        Assert.Equal("next", app.Scripted!.Sent("queue.add")[0].GetProperty("position").GetString());
        Assert.Equal(SampleLibrary.TrackId(8, 2), app.Scripted.Sent("queue.add")[0].GetProperty("items")[0].GetProperty("trackId").GetString());

        var header = Gui.All<Border>(window).First(b => b.Classes.Contains("albumHeader"));
        var (play, plus) = (header.GetVisualDescendants().OfType<Button>().First(), header.GetVisualDescendants().OfType<Button>().Last());
        Gui.Click(window, play);
        await Gui.UntilAsync(() => app.Scripted.Count("queue.set") == 1, "the album played");
        Assert.Equal(3, app.Scripted.Sent("queue.set")[0].GetProperty("items").GetArrayLength());
        Gui.Click(window, plus);
        Gui.Click(window, Gui.All<Button>(menu).Single(b => Texts(b).Single() == "Add to queue"));
        await Gui.UntilAsync(() => app.Scripted.Count("queue.add") == 2, "Add to queue");
        Assert.Equal("upnext", app.Scripted.Sent("queue.add")[1].GetProperty("position").GetString());
        Assert.Equal(3, app.Scripted.Sent("queue.add")[1].GetProperty("items").GetArrayLength());
    }

    [AvaloniaFact]
    public async Task A_library_of_5000_tracks_realizes_only_the_visible_rows()
    {
        var library = new StubLibrary();
        library.Albums.AddRange(Enumerable.Range(0, 500).Select(StubLibrary.Album));
        library.Tracks.AddRange(Enumerable.Range(0, 5000).Select(i => StubLibrary.Track(i)));
        await using var app = await OpenAsync(library: library, tracks: 5000);

        var rows = Gui.All<Border>(Gui.All<TracksPage>(app.Window).Single()).Count(b => b.Classes.Contains("track"));

        Assert.InRange(rows, 5, 60);
    }

    [AvaloniaTheory]
    [InlineData(TrackGrouping.Album, AppTheme.Dark)]
    [InlineData(TrackGrouping.Album, AppTheme.Light)]
    [InlineData(TrackGrouping.Genre, AppTheme.Dark)]
    [InlineData(TrackGrouping.Genre, AppTheme.Light)]
    [InlineData(TrackGrouping.None, AppTheme.Dark)]
    [InlineData(TrackGrouping.None, AppTheme.Light)]
    public async Task Snapshot_of_each_grouping_with_a_current_track(TrackGrouping grouping, AppTheme theme)
    {
        await using var app = await OpenAsync(theme);
        app.ViewModel.TracksPage.Grouping = grouping;
        var (album, hovered) = grouping == TrackGrouping.Genre ? (1, "Polar Drift") : (7, "Glass Window"); // the first group's first two tracks
        await PlayingAsync(app, album, 1);
        Gui.Hover(app.Window, Row(app.Window, hovered));
        Gui.FinishAnimations();

        Snapshots.Match(app.Window, $"tracks-{grouping.ToString().ToLowerInvariant()}-{theme.ToString().ToLowerInvariant()}");
    }

    private static List<string> Current(Window window)
        => [.. Gui.All<Border>(window).Where(b => b.Classes.Contains("track") && b.Classes.Contains("current")).Select(b => ((TrackRow)b.DataContext!).Item.Title)];
}
