using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MusicSpin.App.Controls;
using MusicSpin.App.Services;
using MusicSpin.App.Theming;
using MusicSpin.Core.Library;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Ui.Harness;
using SkiaSharp;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// Now Playing's album beside the deck on wide windows (#193): it starts a gap after the deck, is at most 900 wide, and
/// a cut artist line or track title has all of itself in its tooltip.
/// </summary>
public sealed class NowPlayingInfoTests
{
    private const double Padding = 56;
    private const double Gap = 40;
    private const double DeckShare = 1.15;
    private const double MaxWidth = 900;

    /// <summary>The album's width in a 1440 × 880 window: from 40 after the deck (668) to the padding (1384).</summary>
    private const double At1440 = 676;

    private const string Mozart = "Sir Charles Mackerras Conducts Mozart";
    private const string Soloists = "Susan Gritton (soprano), Catherine Wyn-Rogers (mezzo-soprano), Timothy Robinson (tenor), Alastair Miles (bass), "
                                    + "Scottish Chamber Orchestra, Sir Charles Mackerras";

    private static readonly string[] MozartTracks =
    [
        "Wolfgang Amadeus Mozart: Symphony No. 29 in A major, K201 - I. Allegro moderato",
        "Wolfgang Amadeus Mozart: Symphony No. 29 in A major, K201 - II. Andante",
        "Wolfgang Amadeus Mozart: Requiem in D minor, K626 - III. Sequentia: Rex tremendae",
    ];

    /// <summary>One word with no spaces, far longer than any window.</summary>
    private static readonly string LongWord = string.Concat(Enumerable.Repeat("Klavierkonzertsatz", 14));

    // Album 11 is Mozart's, 12 is one long word throughout; 1–10 are the sample library's.
    private const int MozartAlbum = 11;
    private const int WordAlbum = 12;

    private static async Task<AppHarness> OpenAsync(double width, double height)
    {
        var library = SampleLibrary.Create();
        Add(library, MozartAlbum, Mozart, Soloists, MozartTracks);
        Add(library, WordAlbum, LongWord, LongWord, [LongWord]);
        var (a, b) = (SKColor.FromHsv(40, 55, 70), SKColor.FromHsv(200, 55, 50));
        var cover = TestImages.Bitmap(TestImages.Png(64, 64, (x, y) => x + y < 64 ? a : b));
        var covers = new StubCovers(_ => cover);
        var app = await AppHarness.OpenAsync(new AppOptions
        {
            Scripted = true, Library = library, Covers = covers, Theme = AppTheme.Dark, Width = width, Height = height,
        });
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 12, "the albums");
        return app;
    }

    private static void Add(StubLibrary library, int album, string title, string artist, string[] tracks)
    {
        library.Albums.Add(new AlbumSummary($"a{album}", title, artist, 2009, "Classical", "FLAC", "24/96", tracks.Length, tracks.Length * 300, true));
        library.Tracks.AddRange(tracks.Select((t, i) => new TrackSummary($"a{album}-{i + 1}", $"/music/{album}/{i + 1}.flac", t, artist, title,
            $"a{album}", i + 1, "Classical", 200 + (i * 37), "FLAC", true)));
    }

    private static async Task PlayAsync(AppHarness app, int album, string firstTrack, int tracks)
    {
        app.Scripted!.Status = ScriptedPlayer.Playing(firstTrack, trackId: $"a{album}-1");
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => app.ViewModel.PlayingAlbumId == $"a{album}" && app.ViewModel.NowPlaying.Tracks.Count == tracks, "the album loaded");
        app.ViewModel.NowPlaying.Open();
        Gui.FinishAnimations(900);
        Gui.Settle();
    }

    private static Task PlayMozartAsync(AppHarness app) => PlayAsync(app, MozartAlbum, MozartTracks[0], MozartTracks.Length);

    private static Rect InWindow(AppHarness app, Control control)
        => new(control.TranslatePoint(default, app.Window)!.Value, control.Bounds.Size);

    private static T Find<T>(AppHarness app, string name)
        where T : Control
        => Gui.Find<T>(app.Window, name);

    private static TippedText[] TrackTitles(AppHarness app) => Gui.All<TippedText>(Find<ItemsControl>(app, "Tracks")).ToArray();

    /// <summary>Checks the album sits a gap after the deck, inside the padding, clear of the deck and the close button.</summary>
    private static void AssertBesideTheDeck(AppHarness app)
    {
        var (deck, info, close) = (InWindow(app, Find<DeckHost>(app, "Deck")), InWindow(app, Find<StackPanel>(app, "AlbumInfo")),
            InWindow(app, Find<Button>(app, "CloseNowPlaying")));
        var right = app.Window.ClientSize.Width - Padding;
        Assert.Equal(deck.Right + Gap, info.Left, 1.0);
        Assert.True(info.Right <= right + 1, $"the album ends at {info.Right}, past the padding at {right}");
        Assert.False(info.Intersects(deck), "the album overlaps the deck");
        Assert.False(info.Intersects(close), "the album overlaps the close button");
        foreach (var text in Gui.All<TextBlock>(Find<StackPanel>(app, "AlbumInfo")).Where(t => t.IsEffectivelyVisible))
        {
            var r = InWindow(app, text);
            Assert.True(r.Left >= info.Left - 1 && r.Right <= info.Right + 1, $"'{text.Text}' ({r}) is outside the album ({info})");
        }
    }

    // The panel's arithmetic, on its own.

    [Theory]
    [InlineData(1328, 535, 708 - 56, 1328 - (708 - 56))] // the 1440 window's body: the deck's right at 612, the album 40 after
    [InlineData(1328, 0, 729, 599)] // no deck yet: where the grid's second column began
    [InlineData(1328, 5000, 729, 599)] // a deck wider than its column is taken as the column
    public void The_album_starts_a_gap_after_the_deck_and_runs_to_the_edge(double width, double deck, double x, double room)
    {
        var body = new NowPlayingBody { DeckShare = DeckShare, Spacing = Gap };
        var (ax, aw) = body.Album(width, deck);
        Assert.Equal(x, ax, 1.0);
        Assert.Equal(room, aw, 1.0);
    }

    [Theory]
    [Trait("Category", "Unhappy")]
    [InlineData(0)]
    [InlineData(20)] // narrower than the gap
    public void A_body_too_narrow_for_the_gap_gives_the_album_no_room_rather_than_less_than_none(double width)
    {
        var body = new NowPlayingBody { DeckShare = DeckShare, Spacing = Gap };
        Assert.Equal(0, body.DeckColumn(width));
        Assert.Equal(0, body.Album(width, 100).Width);
    }

    [AvaloniaFact]
    public void The_album_is_as_wide_as_its_cap_from_its_left_whatever_its_content_and_its_margin()
    {
        // A short album (little content) with a margin, in a body far wider than the cap.
        var deck = new Border { Width = 200, Height = 100 };
        var album = new StackPanel { MaxWidth = 300, Margin = new Thickness(0, 0, 24, 0), Children = { new TextBlock { Text = "Neon" } } };
        var body = new NowPlayingBody { DeckShare = DeckShare, Spacing = Gap, Children = { deck, album } };
        var window = new Window { Width = 2000, Height = 400, Content = body };
        window.Show();
        Gui.Settle();

        var column = (2000 - Gap) * DeckShare / (DeckShare + 1);
        Assert.Equal(((column + 200) / 2) + Gap, album.Bounds.Left, 1.0); // from its left, not centred in the room
        Assert.Equal(300, album.Bounds.Width, 1.0); // its whole cap, not its content's width, and not less its margin
        window.Close();
    }

    // In the window.

    [AvaloniaTheory]
    [InlineData(1280, 800, 584)]
    [InlineData(1440, 880, At1440)]
    [InlineData(2560, 1440, MaxWidth)]
    public async Task The_album_widens_into_the_room_beside_the_deck_up_to_900_and_the_deck_stays_put(double width, double height, double expected)
    {
        await using var app = await OpenAsync(width, height);
        await PlayMozartAsync(app);

        // The deck where the 1.15 : 1 grid put it: in the middle of its column.
        var body = InWindow(app, Find<NowPlayingBody>(app, "Body"));
        var column = (body.Width - Gap) * DeckShare / (DeckShare + 1);
        Assert.Equal(body.Left + (column / 2), InWindow(app, Find<DeckHost>(app, "Deck")).Center.X, 1.0);

        var info = InWindow(app, Find<StackPanel>(app, "AlbumInfo"));
        Assert.Equal(expected, info.Width, 1.0);
        Assert.True(info.Width > 520, "wider than the mockup's 520");
        AssertBesideTheDeck(app);
    }

    [AvaloniaFact]
    public async Task A_window_made_wider_widens_the_album_and_a_title_that_fits_loses_its_tooltip()
    {
        await using var app = await OpenAsync(1440, 880);
        await PlayMozartAsync(app);
        var first = TrackTitles(app)[0];
        Assert.True(first.IsCut);
        Assert.Equal(MozartTracks[0], ToolTip.GetTip(first));

        app.Window.Width = 2560;
        app.Window.Height = 1440;
        await Gui.UntilAsync(() => Find<StackPanel>(app, "AlbumInfo").Bounds.Width >= MaxWidth - 1, $"the album to widen to {MaxWidth}");
        Assert.False(first.IsCut);
        Assert.Null(ToolTip.GetTip(first));
        AssertBesideTheDeck(app);
    }

    [AvaloniaFact]
    public async Task A_long_artist_line_wraps_once_then_is_cut_with_all_of_it_in_its_tooltip()
    {
        await using var app = await OpenAsync(1440, 880);
        await PlayMozartAsync(app);
        var artist = Find<TippedText>(app, "AlbumArtist");

        Assert.Equal(2, artist.TextLayout.TextLines.Count);
        Assert.True(artist.IsCut);
        Assert.Equal($"{Soloists} · 2009", ToolTip.GetTip(artist));
    }

    [AvaloniaFact]
    public async Task On_a_wide_window_the_whole_artist_line_shows_in_two_lines_and_has_no_tooltip()
    {
        await using var app = await OpenAsync(2560, 1440);
        await PlayMozartAsync(app);
        var artist = Find<TippedText>(app, "AlbumArtist");

        Assert.Equal(2, artist.TextLayout.TextLines.Count);
        Assert.False(artist.IsCut);
        Assert.Null(ToolTip.GetTip(artist));
    }

    [AvaloniaFact]
    public async Task Only_cut_track_titles_have_a_tooltip_and_it_holds_the_whole_title()
    {
        await using var app = await OpenAsync(1440, 880);
        await PlayMozartAsync(app);
        var titles = TrackTitles(app);

        Assert.Equal(MozartTracks.Length, titles.Length);
        Assert.Equal(new[] { true, false, true }, titles.Select(t => t.IsCut));
        Assert.Equal(new string?[] { MozartTracks[0], null, MozartTracks[2] }, titles.Select(t => ToolTip.GetTip(t) as string));
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task A_short_album_after_a_long_one_leaves_no_tooltips_behind()
    {
        await using var app = await OpenAsync(1440, 880);
        await PlayMozartAsync(app);
        Assert.NotNull(ToolTip.GetTip(Find<TippedText>(app, "AlbumArtist")));

        var night = SampleLibrary.Albums[5];
        await PlayAsync(app, 6, night.Tracks[0], night.Tracks.Length);

        Assert.Null(ToolTip.GetTip(Find<TippedText>(app, "AlbumArtist")));
        Assert.All(TrackTitles(app), t => Assert.Null(ToolTip.GetTip(t)));
        AssertBesideTheDeck(app);
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task One_word_longer_than_the_window_breaks_inside_the_album_and_is_cut_with_its_tooltip()
    {
        await using var app = await OpenAsync(1440, 880);
        await PlayAsync(app, WordAlbum, LongWord, 1);

        // The title breaks the word over lines rather than running off the window.
        Assert.True(Find<TextBlock>(app, "AlbumTitle").TextLayout.TextLines.Count > 1);
        var artist = Find<TippedText>(app, "AlbumArtist");
        Assert.True(artist.IsCut);
        Assert.Equal($"{LongWord} · 2009", ToolTip.GetTip(artist));
        Assert.Equal(LongWord, ToolTip.GetTip(TrackTitles(app).Single()));
        AssertBesideTheDeck(app);
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task With_nothing_playing_the_empty_album_sits_beside_the_deck_with_no_tooltips()
    {
        await using var app = await OpenAsync(1440, 880);
        app.ViewModel.NowPlaying.Open();
        Gui.FinishAnimations(900);
        Gui.Settle();

        Assert.Empty(TrackTitles(app));
        var artist = Find<TippedText>(app, "AlbumArtist");
        Assert.False(artist.IsCut);
        Assert.Null(ToolTip.GetTip(artist));
        AssertBesideTheDeck(app);
    }

    [AvaloniaFact]
    public async Task The_radio_kicker_starts_the_album_where_the_title_does()
    {
        await using var app = await OpenAsync(1440, 880);
        await PlayMozartAsync(app);
        app.Scripted!.Status = app.Scripted.Status with { Radio = new RadioInfo(new RadioFilter { Genres = ["Classical"] }, "genre Classical", 40) };
        await app.Scripted.PushAsync("stateChanged");
        var kicker = Find<StackPanel>(app, "Kicker");
        await Gui.UntilAsync(() => kicker.IsVisible, "the kicker");

        Assert.Equal(InWindow(app, Find<TextBlock>(app, "AlbumTitle")).Left, InWindow(app, kicker).Left, 1.0);
        Assert.Equal(At1440, InWindow(app, Find<StackPanel>(app, "AlbumInfo")).Width, 1.0);
        AssertBesideTheDeck(app);
    }
}
