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

/// <summary>The Albums page's toolbar and Cards (#9), in the window.</summary>
public sealed class AlbumsPageTests
{
    private static async Task<AppHarness> OpenAsync(double width = 1440, AppTheme theme = AppTheme.Dark)
    {
        var app = await AppHarness.OpenAsync(new AppOptions
        {
            Scripted = true, Library = SampleLibrary.Create(), Covers = SampleLibrary.Covers(), Width = width, Theme = theme,
        });
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 10, "the albums");
        return app;
    }

    /// <summary>The player plays track <paramref name="track"/> of album <paramref name="album"/> (as after a CLI command).</summary>
    private static async Task PlayingAsync(AppHarness app, int album, int track = 1, PlayerState state = PlayerState.Playing)
    {
        app.Scripted!.Status = ScriptedPlayer.Playing(SampleLibrary.Albums[album - 1].Tracks[track - 1], trackId: SampleLibrary.TrackId(album, track), state: state);
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => app.ViewModel.PlayingAlbumId == SampleLibrary.AlbumId(album) && app.ViewModel.Player.State == state, "the album playing");
    }

    private static Button Card(Window window, string title)
        => Gui.All<Button>(window).Single(b => b.Classes.Contains("card") && b.DataContext is AlbumItemViewModel a && a.Title == title);

    private static Button Fab(Button card) => card.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("playFab"));

    private static (double X, double Y) Offset(Visual visual)
        => (visual.RenderTransform as Avalonia.Media.Transformation.TransformOperations)?.Value is { } m ? (m.M31, m.M32) : (0, 0);

    [AvaloniaTheory]
    [InlineData(1440)]
    [InlineData(1280)]
    public async Task The_grid_fills_the_width_with_columns_at_least_176_wide(double width)
    {
        await using var app = await OpenAsync(width);
        app.ViewModel.AlbumsPage.Grouping = AlbumGrouping.None;
        Gui.Settle();
        var grid = Gui.Find<ItemsControl>(app.Window, "AlbumGrid");

        var firstRow = Gui.All<Button>(grid).Where(b => b.Classes.Contains("card")).Take(app.ViewModel.AlbumsPage.Columns)
            .Select(b => Gui.BoundsIn(app.Window, b)).ToList();

        Assert.Equal(5, app.ViewModel.AlbumsPage.Columns);
        Assert.All(firstRow, card => Assert.True(card.Width >= 176, $"{card.Width} wide"));
        Assert.All(firstRow, card => Assert.Equal(firstRow[0].Top, card.Top));
        var bounds = Gui.BoundsIn(app.Window, grid);
        Assert.Equal(bounds.Left, firstRow[0].Left, 0.5);
        Assert.Equal(bounds.Right, firstRow[^1].Right, 0.5);
        Assert.Equal(26, firstRow[1].Left - firstRow[0].Right, 0.5);
    }

    [AvaloniaFact]
    public async Task The_toolbar_has_the_view_switch_and_the_Sort_and_Group_dropdowns()
    {
        await using var app = await OpenAsync();

        var views = Gui.Find<ListBox>(app.Window, "ViewSwitch");
        Assert.Equal(["Cards", "Shelf", "Crate"], app.ViewModel.AlbumsPage.Views.Select(v => v.Label));
        Assert.Equal(0, views.SelectedIndex);
        Assert.Equal([true, false, false], views.GetRealizedContainers().Select(c => c.IsEnabled)); // Shelf and Crate: #10, #11
        Assert.Equal(["Sort: ", "Artist"], Texts(Gui.Find<Button>(app.Window, "SortButton")));
        Assert.Equal(["Group: ", "Genre"], Texts(Gui.Find<Button>(app.Window, "GroupButton")));
        Assert.Equal(["AMBIENT · 2", "CLASSICAL · 3", "ELECTRONIC · 1"],
            Gui.All<Grid>(app.Window).Where(g => g.Classes.Contains("groupHeader")).Take(3).Select(g => Texts(g).Single()));
    }

    [AvaloniaFact]
    public async Task A_dropdown_opens_its_menu_under_it_with_the_current_choice_marked()
    {
        await using var app = await OpenAsync();
        var window = app.Window;
        var sort = Gui.Find<Button>(window, "SortButton");

        Gui.Click(window, sort);

        var menu = Gui.Find<Border>(window, "MenuBox");
        Assert.True(menu.IsEffectivelyVisible);
        var (at, under) = (Gui.BoundsIn(window, menu), Gui.BoundsIn(window, sort));
        Assert.Equal(under.Bottom + 4, at.Top, 0.5);
        Assert.True(at.Right <= window.Bounds.Width - 8, $"menu right {at.Right}");
        var entries = Gui.All<Button>(menu).ToList();
        Assert.Equal(["Artist", "Title", "Year"], entries.Select(e => Texts(e).Single()));
        Assert.Equal([true, false, false], entries.Select(e => e.GetVisualDescendants().OfType<Icon>().Single().IsVisible));

        Gui.Click(window, entries[1]);
        Assert.False(menu.IsEffectivelyVisible);
        Assert.Equal(LibrarySort.Title, app.ViewModel.AlbumsPage.Sort);
        Assert.Equal(["Sort: ", "Title"], Texts(sort));

        Gui.Click(window, Gui.Find<Button>(window, "GroupButton"));
        Assert.True(menu.IsEffectivelyVisible);
        Gui.Press(window, Key.Escape, PhysicalKey.Escape);
        Assert.False(menu.IsEffectivelyVisible);

        Gui.Click(window, Gui.Find<Button>(window, "GroupButton"));
        window.MouseDown(new Point(600, 600), MouseButton.Left); // outside the menu
        window.MouseUp(new Point(600, 600), MouseButton.Left);
        Gui.Settle();
        Assert.False(menu.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Hovering_a_card_shows_its_Play_button_and_slides_the_record_out_and_the_playing_card_keeps_both()
    {
        await using var app = await OpenAsync();
        var window = app.Window;
        await PlayingAsync(app, 4);
        var (hovered, playing) = (Card(window, "Field Notes"), Card(window, "Partitas"));
        var (other, idle) = (Card(window, "Cello Suites"), hovered.GetVisualDescendants().OfType<VinylView>().Single());
        Gui.FinishAnimations();
        Assert.Equal(0, Fab(hovered).Opacity);
        Assert.Equal((0, 0), Offset(idle));

        Gui.Hover(window, hovered);
        Gui.FinishAnimations();

        Assert.Equal(1, Fab(hovered).Opacity);
        Assert.Equal((20, 0), Offset(idle));
        Assert.Equal((0, -3), Offset(hovered.GetVisualDescendants().OfType<CoverView>().Single()));
        Assert.Equal(1, Fab(playing).Opacity);
        Assert.Equal((48, 0), Offset(playing.GetVisualDescendants().OfType<VinylView>().Single()));
        Assert.Equal((-16, 0), Offset(playing.GetVisualDescendants().OfType<CoverView>().Single()));
        Assert.Equal(0, Fab(other).Opacity);
        Assert.Contains("playing", playing.Classes);
    }

    [AvaloniaFact]
    public async Task The_Play_button_plays_its_album_then_pauses_and_resumes_it()
    {
        await using var app = await OpenAsync();
        var window = app.Window;
        var card = Card(window, "Northern Lines");
        Gui.Hover(window, card);

        Gui.Click(window, Fab(card));
        await Gui.UntilAsync(() => app.Scripted!.Count("queue.set") == 1, "the album sent");
        var sent = app.Scripted!.Sent("queue.set")[0];
        Assert.Equal([SampleLibrary.TrackId(1, 1), SampleLibrary.TrackId(1, 2), SampleLibrary.TrackId(1, 3)],
            sent.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("trackId").GetString()));
        Assert.Equal(0, sent.GetProperty("start").GetInt32());

        await PlayingAsync(app, 1);
        Assert.True(Fab(card).GetVisualDescendants().OfType<Icon>().Last().IsVisible); // pause
        Gui.Click(window, Fab(card));
        await Gui.UntilAsync(() => app.Scripted.Count("toggle") == 1, "pause");
        await PlayingAsync(app, 1, state: PlayerState.Paused);
        Assert.True(Fab(card).GetVisualDescendants().OfType<Icon>().First().IsVisible); // play
        Gui.Click(window, Fab(card));
        await Gui.UntilAsync(() => app.Scripted.Count("toggle") == 2, "resume");
        Assert.Equal(1, app.Scripted.Count("queue.set"));
    }

    [AvaloniaFact]
    public async Task Clicking_a_card_plays_the_album_and_clicking_the_playing_album_opens_Now_Playing()
    {
        await using var app = await OpenAsync();
        var window = app.Window;

        Gui.Click(window, Card(window, "Cello Suites"));
        await Gui.UntilAsync(() => app.Scripted!.Count("queue.set") == 1, "the album sent");
        Assert.False(app.ViewModel.NowPlaying.IsOpen);

        await PlayingAsync(app, 3);
        Gui.Click(window, Card(window, "Cello Suites"));

        Assert.True(app.ViewModel.NowPlaying.IsOpen);
        Assert.Equal(1, app.Scripted!.Count("queue.set"));
    }

    [AvaloniaFact]
    public async Task A_track_change_from_the_CLI_moves_the_playing_card()
    {
        await using var app = await OpenAsync();
        var window = app.Window;

        await PlayingAsync(app, 1, 2);
        Assert.Equal(["Northern Lines"], PlayingCards(window));

        await PlayingAsync(app, 3);
        Assert.Equal(["Cello Suites"], PlayingCards(window));
        Assert.True(app.ViewModel.AlbumsPage.Albums.Single(a => a.Title == "Cello Suites").IsPlaying);
    }

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Snapshot_of_the_cards_grouped_by_genre_with_a_hovered_and_a_playing_card(AppTheme theme)
    {
        await using var app = await OpenAsync(theme: theme);
        await PlayingAsync(app, 4);
        Gui.Hover(app.Window, Card(app.Window, "Field Notes"));
        Gui.FinishAnimations();

        Snapshots.Match(app.Window, $"albums-cards-{theme.ToString().ToLowerInvariant()}");
    }

    private static List<string> PlayingCards(Window window)
        => [.. Gui.All<Button>(window).Where(b => b.Classes.Contains("card") && b.Classes.Contains("playing")).Select(b => ((AlbumItemViewModel)b.DataContext!).Title)];

    private static List<string?> Texts(Visual root) => [.. root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text)];
}
