using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using MusicSpin.App.Controls;
using MusicSpin.App.Services;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;
using MusicSpin.App.Views;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// Favorites (#48) over a library on disk and a scripted player: the hearts (player bar, rows, Now Playing), the menus'
/// entry, drops on the sidebar's Favorites, the Favorites view and its Radio button. After each action the hearts and
/// the library read afresh (what <c>music-spin favorites</c> lists) agree.
/// </summary>
public sealed class FavoritesTests
{
    private static async Task<AppHarness> OpenAsync(Action<MusicLibrary, IReadOnlyList<string>>? prepare = null, TimeSpan? lockTimeout = null,
        AppTheme theme = AppTheme.Dark)
    {
        var app = await AppHarness.OpenAsync(JazzLibrary.Options(prepare, theme, lockTimeout));
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 2, "the library");
        Gui.Settle();
        return app;
    }

    /// <summary>Kind of Blue 2, then Blue Train 1, are favorites (Blue Train 1 the newer).</summary>
    private static void Two(MusicLibrary library, IReadOnlyList<string> paths)
    {
        Favor(library, paths, "Kind of Blue", 2);
        Favor(library, paths, "Blue Train", 1);
    }

    private static void Favor(MusicLibrary library, IReadOnlyList<string> paths, string album, int number)
        => library.AddFavorites([library.TrackIdsAt([JazzLibrary.TrackPath(paths, album, number)])[0].TrackId!]);

    /// <summary>The favorites' titles, newest first, as <c>music-spin favorites</c> lists them.</summary>
    private static string[] Favorites(AppHarness app) => app.Read(library => library.FavoritesQueue().Select(t => t.Title ?? "?").ToArray());

    private static async Task<TracksPageViewModel> OpenTracksAsync(AppHarness app)
    {
        app.ViewModel.Sidebar.NavigateCommand.Execute(app.ViewModel.Sidebar.Tracks);
        await Gui.UntilAsync(() => app.ViewModel.TracksPage.Tracks.Count == 5, "the tracks");
        Gui.Settle();
        return app.ViewModel.TracksPage;
    }

    private static async Task<FavoritesPageViewModel> OpenFavoritesAsync(AppHarness app, int count)
    {
        app.ViewModel.Sidebar.NavigateCommand.Execute(app.ViewModel.Sidebar.Favorites);
        var page = Assert.IsType<FavoritesPageViewModel>(app.ViewModel.CurrentPage);
        await Gui.UntilAsync(() => page.Tracks.Count == count && (count > 0 || page.EmptyText is not null), "the favorites");
        Gui.Settle();
        return page;
    }

    private static TrackItemViewModel Track(TrackListPageViewModel page, string title) => page.Tracks.Single(t => t.Title == title);

    private static Button Heart(Control row) => Gui.All<Button>(row).Single(b => b.Classes.Contains("heart"));

    private static Button RowHeart(AppHarness app, string title) => Heart(PlaylistTests.Row(app, title));

    private static Button PlayingHeart(AppHarness app) => Gui.Find<Button>(app.Window, "PlayingHeart");

    private static Icon HeartIcon(Button heart) => Gui.All<Icon>(heart).Single();

    private static Panel FavoritesNav(AppHarness app)
        => Gui.All<Panel>(app.Window).Single(p => p.Classes.Contains("navItem") && p.DataContext == app.ViewModel.Sidebar.Favorites);

    private static string[] Labels(AppHarness app) => [.. app.ViewModel.Menu.Entries.Select(e => e.Label)];

    private static void Choose(AppHarness app, string label)
        => Gui.Click(app.Window, Gui.All<Button>(Gui.Find<Border>(app.Window, "MenuBox")).Single(b => b.DataContext is MenuEntryViewModel { Label: var l } && l == label));

    /// <summary>Plays <paramref name="title"/> from the Tracks page (the scripted player follows).</summary>
    private static async Task PlayAsync(AppHarness app, string title)
    {
        var page = await OpenTracksAsync(app);
        var track = Track(page, title);
        await page.PlayTrackCommand.ExecuteAsync(track);
        await Gui.UntilAsync(() => app.ViewModel.Player.TrackId == track.Track.Id, $"'{title}' playing");
        Gui.Settle();
    }

    // ---- the heart --------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Hearting_the_playing_track_in_the_player_bar_fills_its_row_and_lists_it_in_Favorites_and_back()
    {
        await using var app = await OpenAsync();
        await PlayAsync(app, "Kind of Blue 1");
        var heart = PlayingHeart(app);
        Assert.True(heart.IsEffectivelyVisible);
        Assert.Equal(FavoritesViewModel.AddLabel, ToolTip.GetTip(heart));
        Assert.True(HeartIcon(heart).IsStroked);

        Gui.Click(app.Window, heart);
        await Gui.UntilAsync(() => app.ViewModel.IsPlayingFavorite, "the playing track a favorite");
        Assert.Equal(["Kind of Blue 1"], Favorites(app));
        Assert.Contains("on", heart.Classes);
        Assert.False(HeartIcon(heart).IsStroked);
        Assert.Equal(FavoritesViewModel.RemoveLabel, ToolTip.GetTip(heart));
        Assert.True(Track(app.ViewModel.TracksPage, "Kind of Blue 1").IsFavorite);
        Assert.Contains("on", RowHeart(app, "Kind of Blue 1").Classes);
        Assert.Equal("1", app.ViewModel.Sidebar.Favorites.Detail);

        var page = await OpenFavoritesAsync(app, 1);
        Assert.Equal(["Kind of Blue 1"], page.Tracks.Select(t => t.Title));
        Assert.Contains("on", RowHeart(app, "Kind of Blue 1").Classes);

        // and back: off the player bar, the view and the library
        Gui.Click(app.Window, heart);
        await Gui.UntilAsync(() => page.Tracks.Count == 0, "the view emptied");
        Assert.Empty(Favorites(app));
        Assert.False(app.ViewModel.IsPlayingFavorite);
        Assert.DoesNotContain("on", heart.Classes);
        Assert.Null(app.ViewModel.Sidebar.Favorites.Detail);
    }

    [AvaloniaFact]
    public async Task A_rows_heart_shows_its_outline_on_hover_and_keyboard_focus_and_a_filled_one_always()
    {
        await using var app = await OpenAsync();
        await OpenTracksAsync(app);
        var away = Gui.Find<TextBlock>(app.Window, "PageTitle");
        var heart = RowHeart(app, "Kind of Blue 1");
        Gui.Hover(app.Window, away);
        Assert.Equal(0, heart.Opacity);

        Gui.Hover(app.Window, PlaylistTests.Row(app, "Kind of Blue 1"));
        Assert.Equal(1, heart.Opacity);
        Assert.True(HeartIcon(heart).IsStroked);
        Assert.Equal(0, RowHeart(app, "Kind of Blue 2").Opacity);

        Gui.Hover(app.Window, away);
        Assert.Equal(0, heart.Opacity);
        heart.Focus(NavigationMethod.Tab);
        Gui.Settle();
        Assert.Equal(1, heart.Opacity);

        // Enter on the focused heart favorites it. The library changed, so the page reads it again; the tracks are the
        // same, so the rows stay, and the heart keeps the focus.
        var page = app.ViewModel.TracksPage;
        var track = Track(page, "Kind of Blue 1");
        Gui.Press(app.Window, Key.Enter, PhysicalKey.Enter);
        await Gui.UntilAsync(() => track.IsFavorite, "the favorite");
        await page.LoadAsync();
        Gui.Settle();
        Assert.Same(track, Track(page, "Kind of Blue 1"));
        Assert.Same(heart, RowHeart(app, "Kind of Blue 1"));
        Assert.True(heart.IsFocused);
        Assert.Contains("on", heart.Classes);

        // filled, it shows with the pointer and the focus elsewhere
        Gui.Hover(app.Window, away);
        RowHeart(app, "Kind of Blue 2").Focus(NavigationMethod.Tab);
        Gui.Settle();
        Assert.Equal(1, heart.Opacity);
        Assert.False(HeartIcon(heart).IsStroked);
        Assert.Equal(FavoritesViewModel.RemoveLabel, ToolTip.GetTip(heart));
        Assert.Equal(["Kind of Blue 1"], Favorites(app));
    }

    [AvaloniaFact]
    public async Task A_playlist_entry_whose_file_is_missing_has_no_heart_and_neither_has_a_plain_file_in_the_player_bar()
    {
        await using var app = await OpenAsync((library, paths) => library.CreatePlaylist("Mix", [JazzLibrary.Entry(paths, "Kind of Blue", 1), JazzLibrary.Gone(paths)]));
        await PlaylistTests.OpenPlaylistAsync(app, "Mix", 2);
        Assert.True(RowHeart(app, "Kind of Blue 1").IsVisible);
        Assert.False(RowHeart(app, "Lost Take").IsVisible);

        app.Scripted!.Status = ScriptedPlayer.Playing("A file");
        await app.Scripted.PushAsync("status", app.Scripted.Status);
        await Gui.UntilAsync(() => app.ViewModel.PlayerTitle == "A file", "the file playing");
        Assert.False(PlayingHeart(app).IsVisible);
    }

    [AvaloniaFact]
    public async Task Now_Playings_tracks_have_hearts_too()
    {
        await using var app = await OpenAsync(Two);
        await PlayAsync(app, "Kind of Blue 1");
        var nowPlaying = app.ViewModel.NowPlaying;
        await Gui.UntilAsync(() => nowPlaying.Tracks.Count == 3, "Now Playing's tracks");
        Assert.Equal([false, true, false], nowPlaying.Tracks.Select(t => t.IsFavorite));

        await nowPlaying.ToggleFavoriteCommand.ExecuteAsync(nowPlaying.Tracks[0]);
        Assert.Equal([true, true, false], nowPlaying.Tracks.Select(t => t.IsFavorite));
        Assert.True(app.ViewModel.IsPlayingFavorite);
        Assert.Equal(["Blue Train 1", "Kind of Blue 1", "Kind of Blue 2"], Favorites(app).Order());
        Assert.Equal(FavoritesViewModel.RemoveLabel, nowPlaying.Tracks[0].FavoriteLabel);
    }

    // ---- menus ------------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task A_track_rows_menu_offers_Add_to_favorites_then_Remove_from_favorites_and_an_albums_does_not()
    {
        await using var app = await OpenAsync();
        await OpenTracksAsync(app);
        Mouse.RightClick(app.Window, PlaylistTests.Row(app, "Blue Train 2"));
        Assert.Equal(FavoritesViewModel.AddLabel, Labels(app)[^1]);
        Choose(app, FavoritesViewModel.AddLabel);
        await Gui.UntilAsync(() => Track(app.ViewModel.TracksPage, "Blue Train 2").IsFavorite, "the favorite");
        Assert.Equal(["Blue Train 2"], Favorites(app));

        Mouse.RightClick(app.Window, PlaylistTests.Row(app, "Blue Train 2"));
        Assert.Equal(FavoritesViewModel.RemoveLabel, Labels(app)[^1]);
        Choose(app, FavoritesViewModel.RemoveLabel);
        await Gui.UntilAsync(() => !Track(app.ViewModel.TracksPage, "Blue Train 2").IsFavorite, "not a favorite");
        Assert.Empty(Favorites(app));

        // an album is never one favorite
        var header = app.ViewModel.TracksPage.Rows.OfType<TrackAlbumHeaderViewModel>().First();
        Assert.True(app.ViewModel.ShowMenu(header));
        Assert.DoesNotContain(FavoritesViewModel.AddLabel, Labels(app));
        app.ViewModel.Menu.Close();
        app.ViewModel.Sidebar.NavigateCommand.Execute(app.ViewModel.Sidebar.Albums);
        Assert.True(app.ViewModel.ShowMenu(app.ViewModel.AlbumsPage.Albums[0]));
        Assert.DoesNotContain(FavoritesViewModel.AddLabel, Labels(app));
    }

    [AvaloniaFact]
    public async Task A_Now_Playing_tracks_menu_offers_the_favorite_entry()
    {
        await using var app = await OpenAsync(Two);
        await PlayAsync(app, "Kind of Blue 1");
        await Gui.UntilAsync(() => app.ViewModel.NowPlaying.Tracks.Count == 3, "Now Playing's tracks");

        Assert.True(app.ViewModel.ShowMenu(app.ViewModel.NowPlaying.Tracks[1]));
        Assert.Equal(FavoritesViewModel.RemoveLabel, Labels(app)[^1]);
        await app.ViewModel.Menu.Entries[^1].Run();
        await Gui.UntilAsync(() => !app.ViewModel.NowPlaying.Tracks[1].IsFavorite, "not a favorite");
        Assert.Equal(["Blue Train 1"], Favorites(app));
    }

    // ---- the sidebar's Favorites ------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Favorites_is_pinned_first_under_Playlists_with_its_count_and_has_no_menu()
    {
        await using var app = await OpenAsync(Two);
        await Gui.UntilAsync(() => app.ViewModel.Sidebar.Favorites.Detail == "2", "the count");
        var nav = FavoritesNav(app);
        Assert.True(nav.IsEffectivelyVisible);
        Assert.Contains(Gui.All<TextBlock>(nav), t => t.Classes.Contains("navCount") && t.Text == "2" && t.IsEffectivelyVisible);
        Assert.Equal("PLAYLISTS", Gui.All<TextBlock>(app.Window).Single(t => t.Classes.Contains("navHeader") && t.Text == "PLAYLISTS" && t.IsEffectivelyVisible).Text);

        Mouse.RightClick(app.Window, nav);
        Assert.False(app.ViewModel.Menu.IsOpen);
        Assert.False(app.ViewModel.Sidebar.Favorites.IsPlaylist); // no rename, no delete
        Assert.True(app.ViewModel.Sidebar.Favorites.TakesDrops);
    }

    [AvaloniaFact]
    public async Task An_album_dropped_on_Favorites_favorites_its_tracks_and_flashes_it()
    {
        await using var app = await OpenAsync(Two);
        var favorites = app.ViewModel.Sidebar.Favorites;
        var card = Gui.All<Button>(app.Window).Single(b => b.Classes.Contains("card") && b.DataContext is AlbumItemViewModel { Title: "Kind of Blue" });
        Mouse.StartDrag(app.Window, card);
        var at = Mouse.Middle(app.Window, FavoritesNav(app));
        Mouse.MoveTo(app.Window, at);
        Assert.True(favorites.IsDropTarget);
        Assert.Contains("dropTarget", FavoritesNav(app).Classes);

        Mouse.Release(app.Window, at);
        Assert.False(favorites.IsDropTarget);
        await Gui.UntilAsync(() => favorites.IsFresh, "Favorites flashed");
        Assert.Equal(["Blue Train 1", "Kind of Blue 1", "Kind of Blue 2", "Kind of Blue 3"], Favorites(app).Order());
        Assert.Equal("4", favorites.Detail);
        Assert.Empty(app.Read(library => library.Playlists())); // no playlist was made
    }

    [AvaloniaFact]
    public async Task A_track_dropped_on_Favorites_that_already_is_one_changes_nothing_but_still_flashes()
    {
        await using var app = await OpenAsync(Two);
        await OpenTracksAsync(app);
        Mouse.StartDrag(app.Window, PlaylistTests.Row(app, "Blue Train 1"));
        var at = Mouse.Middle(app.Window, FavoritesNav(app));
        Mouse.MoveTo(app.Window, at);
        Mouse.Release(app.Window, at);
        await Gui.UntilAsync(() => app.ViewModel.Sidebar.Favorites.IsFresh, "Favorites flashed");
        Assert.Equal(["Blue Train 1", "Kind of Blue 2"], Favorites(app));
    }

    // ---- the Favorites view -----------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task The_view_lists_the_favorites_newest_first_and_unhearting_a_row_takes_it_off()
    {
        await using var app = await OpenAsync(Two);
        var page = await OpenFavoritesAsync(app, 2);
        Assert.Equal("Favorites", Gui.Find<TextBlock>(app.Window, "PageTitle").Text);
        Assert.Equal(["Blue Train 1", "Kind of Blue 2"], page.Tracks.Select(t => t.Title));
        Assert.All(page.Rows, r => Assert.IsType<TrackRowWithAlbum>(r));
        Assert.DoesNotContain(Gui.All<Button>(app.Window), b => b.Classes.Contains("remove")); // no ✕: it isn't a playlist
        Assert.False(Gui.Find<StackPanel>(app.Window, "MissingNote").IsVisible);

        Gui.Click(app.Window, RowHeart(app, "Blue Train 1"));
        await Gui.UntilAsync(() => page.Tracks.Count == 1, "the row gone");
        Assert.Equal(["Kind of Blue 2"], page.Tracks.Select(t => t.Title));
        Assert.Equal(["Kind of Blue 2"], Favorites(app));
    }

    [AvaloniaFact]
    public async Task Search_filters_the_view_and_Play_and_Queue_play_its_tracks()
    {
        await using var app = await OpenAsync(Two);
        var page = await OpenFavoritesAsync(app, 2);
        app.ViewModel.SearchText = "train";
        await Gui.UntilAsync(() => page.Rows.Count == 1, "the search");
        Assert.Equal("Blue Train 1", Assert.IsType<TrackRowWithAlbum>(page.Rows[0]).Item.Title);
        app.ViewModel.SearchText = string.Empty;
        await Gui.UntilAsync(() => page.Rows.Count == 2, "the search cleared");

        Gui.Click(app.Window, Gui.Find<Button>(app.Window, "PlayAll"));
        await Gui.UntilAsync(() => app.Scripted!.Count("queue.set") == 1, "queue.set sent");
        var paths = app.Scripted!.Sent("queue.set")[0].GetProperty("items").EnumerateArray().Select(i => i.GetProperty("path").GetString()!).ToArray();
        Assert.Equal([JazzLibrary.TrackPath(app.TrackPaths, "Blue Train", 1), JazzLibrary.TrackPath(app.TrackPaths, "Kind of Blue", 2)], paths);

        Gui.Click(app.Window, Gui.Find<Button>(app.Window, "QueueAll"));
        Choose(app, "Add to queue");
        await Gui.UntilAsync(() => app.Scripted.Count("queue.add") == 1, "queue.add sent");
        Assert.Equal(paths, app.Scripted.Sent("queue.add")[0].GetProperty("items").EnumerateArray().Select(i => i.GetProperty("path").GetString()!));
    }

    [AvaloniaFact]
    public async Task Empty_it_says_how_to_add_one_and_Play_Queue_are_off_but_a_search_with_no_match_says_only_that()
    {
        await using var app = await OpenAsync();
        var page = await OpenFavoritesAsync(app, 0);
        Assert.Equal("No favorites yet.", Gui.Find<TextBlock>(app.Window, "EmptyText").Text);
        var hint = Gui.Find<StackPanel>(app.Window, "EmptyHint");
        Assert.True(hint.IsEffectivelyVisible);
        Assert.Equal(["Click", "on a track to add it."], Gui.All<TextBlock>(hint).Select(t => t.Text));
        Assert.Single(Gui.All<Icon>(hint));
        Assert.False(Gui.Find<Button>(app.Window, "PlayAll").IsEffectivelyEnabled);
        Assert.False(Gui.Find<Button>(app.Window, "QueueAll").IsEffectivelyEnabled);
        Assert.False(Gui.Find<Button>(app.Window, "FavoritesRadio").IsEffectivelyEnabled); // a station of no favorites would play nothing
        Assert.Null(app.ViewModel.Sidebar.Favorites.Detail);

        // with favorites, a search that matches none says so, without the hint
        app.Edit(library => { Favor(library, app.TrackPaths, "Blue Train", 2); return 0; });
        await Gui.UntilAsync(() => page.Tracks.Count == 1, "the favorite from elsewhere");
        Assert.True(Gui.Find<Button>(app.Window, "FavoritesRadio").IsEffectivelyEnabled);
        app.ViewModel.SearchText = "zzz";
        await Gui.UntilAsync(() => page.EmptyText == "Nothing matches “zzz”.", "the search");
        Assert.False(hint.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Favorites_whose_files_left_the_library_are_not_listed_and_a_line_says_how_many()
    {
        await using var app = await OpenAsync((library, paths) =>
        {
            Two(library, paths);
            File.Delete(JazzLibrary.TrackPath(paths, "Kind of Blue", 2));
            library.Scan(cancellationToken: TestContext.Current.CancellationToken);
        });
        var page = await OpenFavoritesAsync(app, 1);
        Assert.Equal(["Blue Train 1"], page.Tracks.Select(t => t.Title));
        Assert.Equal("1", app.ViewModel.Sidebar.Favorites.Detail);
        var note = Gui.Find<StackPanel>(app.Window, "MissingNote");
        Assert.True(note.IsEffectivelyVisible);
        Assert.Equal("1 favorite isn't in the library now.", Gui.All<TextBlock>(note).First().Text);
        Assert.Equal(FavoritesPageViewModel.MissingDocs, Gui.Find<HyperlinkButton>(app.Window, "MissingLink").NavigateUri);

        // only on Favorites
        app.ViewModel.Sidebar.NavigateCommand.Execute(app.ViewModel.Sidebar.Tracks);
        Gui.Settle();
        Assert.False(note.IsVisible);
    }

    // ---- radio ------------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Radio_starts_a_station_of_favorites_then_reads_On_air_and_opens_the_Radio_page()
    {
        await using var app = await OpenAsync(Two);
        await OpenFavoritesAsync(app, 2);
        var radio = Gui.Find<Button>(app.Window, "FavoritesRadio");
        Assert.Equal("Radio", Gui.All<TextBlock>(radio).Single().Text);

        Gui.Click(app.Window, radio);
        await Gui.UntilAsync(() => app.ViewModel.Player.IsRadio, "the station on air");
        var sent = Assert.Single(app.Scripted!.Sent("radio"));
        Assert.True(sent.GetProperty("filter").GetProperty("favorites").GetBoolean());
        Assert.Equal("favorites", app.ViewModel.Player.RadioDescription);
        await Gui.UntilAsync(() => Gui.All<TextBlock>(radio).Single().Text == "On air", "On air");

        Gui.Click(app.Window, radio);
        Assert.Same(app.ViewModel.RadioPage, app.ViewModel.CurrentPage);
        Assert.Single(app.Scripted.Sent("radio")); // no second station
    }

    [AvaloniaFact]
    public async Task Another_station_on_air_leaves_the_button_reading_Radio()
    {
        await using var app = await OpenAsync(Two);
        var page = await OpenFavoritesAsync(app, 2);
        await app.ViewModel.Player.StartRadioAsync(FavoritesPageViewModel.Station with { HiRes = true });
        await Gui.UntilAsync(() => app.ViewModel.Player.IsRadio, "the station on air");
        Assert.False(page.IsOnAir);
        Assert.Equal("Radio", page.RadioLabel);

        await app.ViewModel.Player.StartRadioAsync(new RadioFilter());
        await Gui.UntilAsync(() => app.ViewModel.Player.RadioDescription == "whole library", "the other station");
        Assert.False(page.IsOnAir);
    }

    // ---- changes elsewhere, and a busy library ----------------------------------------------------------------

    [AvaloniaFact]
    public async Task A_favorite_added_or_removed_from_the_CLI_shows_on_the_hearts_and_in_the_view()
    {
        await using var app = await OpenAsync(Two);
        var tracks = await OpenTracksAsync(app);
        await Gui.UntilAsync(() => Track(tracks, "Kind of Blue 2").IsFavorite, "the favorites read");

        app.Edit(library => { Favor(library, app.TrackPaths, "Kind of Blue", 3); return 0; });
        await Gui.UntilAsync(() => Track(tracks, "Kind of Blue 3").IsFavorite, "the CLI's favorite");
        Assert.Contains("on", RowHeart(app, "Kind of Blue 3").Classes);
        Assert.Equal("3", app.ViewModel.Sidebar.Favorites.Detail);

        var page = await OpenFavoritesAsync(app, 3);
        app.Edit(library => library.RemoveFavorites([library.TrackIdsAt([JazzLibrary.TrackPath(app.TrackPaths, "Blue Train", 1)])[0].TrackId!]));
        await Gui.UntilAsync(() => page.Tracks.Count == 2, "the CLI's removal");
        Assert.Equal(["Kind of Blue 3", "Kind of Blue 2"], page.Tracks.Select(t => t.Title));
        Assert.Equal("2", app.ViewModel.Sidebar.Favorites.Detail);
    }

    [AvaloniaFact]
    public async Task While_another_process_holds_the_library_a_heart_says_it_is_busy_and_does_not_change()
    {
        await using var app = await OpenAsync(Two, lockTimeout: TimeSpan.FromMilliseconds(200));
        var tracks = await OpenTracksAsync(app);
        await Gui.UntilAsync(() => Track(tracks, "Kind of Blue 2").IsFavorite, "the favorites read");
        using (app.OpenLibrary())
        {
            Gui.Click(app.Window, RowHeart(app, "Kind of Blue 1"));
            await Gui.UntilAsync(() => app.ViewModel.Banner is not null, "the notice");
            Assert.Equal(LibraryService.BusyMessage, app.ViewModel.Banner);
            Assert.False(Track(tracks, "Kind of Blue 1").IsFavorite);
            Assert.DoesNotContain("on", RowHeart(app, "Kind of Blue 1").Classes);

            // un-hearting fails the same way, and a drop on Favorites doesn't flash
            app.ViewModel.DismissBannerCommand.Execute(null);
            Gui.Settle();
            Gui.Click(app.Window, RowHeart(app, "Kind of Blue 2"));
            await Gui.UntilAsync(() => app.ViewModel.Banner is not null, "the notice");
            Assert.True(Track(tracks, "Kind of Blue 2").IsFavorite);

            app.ViewModel.DismissBannerCommand.Execute(null);
            Gui.Settle();
            Mouse.StartDrag(app.Window, PlaylistTests.Row(app, "Kind of Blue 3"));
            var at = Mouse.Middle(app.Window, FavoritesNav(app));
            Mouse.MoveTo(app.Window, at);
            Mouse.Release(app.Window, at);
            await Gui.UntilAsync(() => app.ViewModel.Banner is not null, "the notice");
            Assert.False(app.ViewModel.Sidebar.Favorites.IsFresh);
            Assert.Equal("2", app.ViewModel.Sidebar.Favorites.Detail);
        }

        Assert.Equal(["Blue Train 1", "Kind of Blue 2"], Favorites(app));
    }

    // ---- view model -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_view_model_takes_a_change_only_once_the_library_has()
    {
        var library = new StubLibrary { Tracks = { StubLibrary.Track(1), StubLibrary.Track(2) } };
        var notices = new Notices();
        string? notice = null;
        notices.Posted += (_, message) => notice = message;
        var favorites = new FavoritesViewModel(library, notices);
        var changes = 0;
        favorites.Changed += (_, _) => changes++;

        Assert.True(await favorites.ToggleAsync("track0001"));
        Assert.True(favorites.Contains("track0001"));
        Assert.Equal((1, 1), (favorites.Count, changes));

        // a plain file, or nothing, isn't one: nothing is written
        Assert.False(await favorites.ToggleAsync(""));
        Assert.False(await favorites.ToggleAsync(null));
        Assert.False(await favorites.SetAsync(["", ""], true));
        Assert.Equal(1, changes);

        library.FavoritesFailure = LibraryService.BusyMessage;
        Assert.False(await favorites.ToggleAsync("track0002"));
        Assert.False(await favorites.ToggleAsync("track0001"));
        Assert.Equal(LibraryService.BusyMessage, notice);
        Assert.True(favorites.Contains("track0001"));
        Assert.False(favorites.Contains("track0002"));
        Assert.Equal((1, 1), (favorites.Count, changes));

        // a read that finds what it has already doesn't count as a change; one that differs does
        library.FavoritesFailure = null;
        await favorites.LoadAsync();
        Assert.Equal(1, changes);
        library.Favorites.Insert(0, "track0002");
        await favorites.LoadAsync();
        Assert.Equal((2, 2), (favorites.Count, changes));
    }

    // ---- snapshots --------------------------------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Snapshot_of_the_Favorites_view_playing_a_favorite(AppTheme theme)
    {
        await using var app = await OpenAsync(Two, theme: theme);
        await PlayAsync(app, "Kind of Blue 2");
        var page = await OpenFavoritesAsync(app, 2);
        await Gui.UntilAsync(() => page.Tracks.Where(t => t.Album == "Kind of Blue").All(t => t.Art.Cover is not null) && app.ViewModel.PlayingCover is not null, "the covers");
        app.Window.MouseMove(new Point(1, 1));
        Gui.Settle();
        Gui.FinishAnimations();

        Snapshots.Match(app.Window, $"favorites-{theme.ToString().ToLowerInvariant()}");
    }

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Snapshot_of_hearts_filled_on_a_row_and_outlined_on_a_hovered_one_and_in_the_player_bar(AppTheme theme)
    {
        await using var app = await OpenAsync(Two, theme: theme);
        await PlayAsync(app, "Kind of Blue 1");
        await Gui.UntilAsync(() => app.ViewModel.TracksPage.Tracks.Where(t => t.Album == "Kind of Blue").All(t => t.Art.Cover is not null) && app.ViewModel.PlayingCover is not null, "the covers");
        Gui.Hover(app.Window, PlaylistTests.Row(app, "Kind of Blue 3"));
        Gui.FinishAnimations();

        Snapshots.Match(app.Window, $"hearts-{theme.ToString().ToLowerInvariant()}");
    }

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Snapshot_of_the_Favorites_view_empty(AppTheme theme)
    {
        await using var app = await OpenAsync(theme: theme);
        await OpenFavoritesAsync(app, 0);
        app.Window.MouseMove(new Point(1, 1));
        Gui.Settle();
        Gui.FinishAnimations();

        Snapshots.Match(app.Window, $"favorites-empty-{theme.ToString().ToLowerInvariant()}");
    }
}
