using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MusicSpin.App.Controls;
using MusicSpin.App.Theming;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Settings;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// What a frame costs, by behaviour rather than time: spinning records draw nothing new (#118), a page Now Playing covers
/// isn't drawn (#119), and the blurred background is made once per album, not blurred every frame (#124).
/// </summary>
public sealed class RenderCostTests
{
    /// <summary>Night Transit: on screen as a card, with a cover.</summary>
    private const int Album = 6;

    private static async Task<AppHarness> OpenAsync(string design = "classic", StubCovers? covers = null)
    {
        var app = await AppHarness.OpenAsync(new AppOptions
        {
            Scripted = true,
            Library = SampleLibrary.Create(),
            Covers = covers ?? SampleLibrary.Covers(accents: true),
            Settings = new AppSettings { TurntableDesign = design },
        });
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 10, "the albums");
        return app;
    }

    private static async Task PlayAsync(AppHarness app, int album = Album, int track = 1, PlayerState state = PlayerState.Playing)
    {
        var a = SampleLibrary.Albums[album - 1];
        app.Scripted!.Status = ScriptedPlayer.Playing(a.Tracks[track - 1], trackId: SampleLibrary.TrackId(album, track), state: state);
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => app.ViewModel.PlayingAlbumId == SampleLibrary.AlbumId(album) && app.ViewModel.Player.State == state
                                   && app.ViewModel.NowPlaying.Tracks.Count == a.Tracks.Length, "the album loaded");
        Gui.Settle();
    }

    /// <summary>Lets the spin clock run for <paramref name="frames"/> frames, rendering each.</summary>
    private static void Spin(AppHarness app, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            app.Advance(TimeSpan.FromMilliseconds(16));
            Gui.Settle();
            using var frame = app.Window.CaptureRenderedFrame();
        }
    }

    private static ContentControl Page(AppHarness app) => Gui.Find<ContentControl>(app.Window, "Page");

    // ---- #118: spinning records ---------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task A_spinning_record_turns_with_the_clock_without_drawing_itself_again()
    {
        await using var app = await OpenAsync();
        await PlayAsync(app);
        Spin(app, 2);
        var records = app.Window.GetVisualDescendants().OfType<VinylView>().Where(v => v.IsEffectivelyVisible && v.RenderedAngle != 0).ToList();
        Assert.NotEmpty(records); // the playing album's card record and the player bar's
        var renders = records.Select(r => r.DiscRenders).ToList();
        var angle = app.ViewModel.SpinClock.Angle;

        Spin(app, 20);

        Assert.NotEqual(angle, app.ViewModel.SpinClock.Angle);
        Assert.All(records, r => Assert.Equal(app.ViewModel.SpinClock.Angle, r.RenderedAngle));
        Assert.Equal(renders, records.Select(r => r.DiscRenders));
    }

    [AvaloniaFact]
    public void A_records_images_are_drawn_once_and_again_only_when_what_they_show_changes()
    {
        var record = new VinylView { Width = 160, Height = 160, Label = TestImages.Bitmap(TestImages.Cover(128)) };
        var holder = new LayoutTransformControl { Child = record };
        var window = new Window { Width = 500, Height = 500, Content = holder };
        window.Show();
        Gui.Settle();
        using (window.CaptureRenderedFrame())
        {
        }

        Assert.Equal((1, 1), (record.GrooveImages, record.LabelImages));

        // Turning: nothing drawn again.
        for (var angle = 10; angle <= 90; angle += 10)
        {
            record.Angle = angle;
            Gui.Settle();
            using var frame = window.CaptureRenderedFrame();
        }

        Assert.Equal((1, 1), (record.GrooveImages, record.LabelImages));

        // Another label: only the turning part.
        record.Label = TestImages.Bitmap(TestImages.Cover(64));
        Render(window);
        Assert.Equal((1, 2), (record.GrooveImages, record.LabelImages));

        // Another groove colour: only the still part.
        record.GrooveBrush = Brushes.DarkRed;
        Render(window);
        Assert.Equal((2, 2), (record.GrooveImages, record.LabelImages));

        // Another size: both, at the new size.
        record.Width = record.Height = 200;
        Render(window);
        Assert.Equal((3, 3), (record.GrooveImages, record.LabelImages));

        // Shown twice as large by a transform above it (a scaled deck, a larger window): both, sharp at the new scale.
        holder.LayoutTransform = new ScaleTransform(2, 2);
        Render(window);
        Assert.Equal((4, 4), (record.GrooveImages, record.LabelImages));
        window.Close();

        static void Render(Window window)
        {
            Gui.Settle();
            using var frame = window.CaptureRenderedFrame();
        }
    }

    // ---- #119: the page under Now Playing ---------------------------------------------------------------------

    [AvaloniaFact]
    public async Task The_page_and_sidebar_are_hidden_once_Now_Playing_covers_them_and_shown_before_it_slides_away()
    {
        await using var app = await OpenAsync();
        await PlayAsync(app);
        var scroller = Page(app).GetVisualDescendants().OfType<ScrollViewer>().First();
        scroller.Offset = new Vector(0, 300);
        Gui.Settle();
        var offset = scroller.Offset;
        Assert.True(offset.Y > 0, "the page scrolls");
        var sidebar = Gui.Find<Control>(app.Window, "SidebarView");

        app.ViewModel.NowPlaying.Open();
        Gui.Settle();
        Assert.True(Page(app).IsEffectivelyVisible); // still sliding in: the page shows round it
        Gui.FinishAnimations(900);
        Assert.False(Page(app).IsEffectivelyVisible);
        Assert.False(sidebar.IsEffectivelyVisible);

        // The covered page's records aren't drawn, nor turned into view.
        var covered = Page(app).GetVisualDescendants().OfType<VinylView>().ToList();
        var renders = covered.Select(r => r.DiscRenders).ToList();
        Spin(app, 10);
        Assert.Equal(renders, covered.Select(r => r.DiscRenders));

        app.ViewModel.NowPlaying.Close();
        Gui.Settle();
        Assert.True(Page(app).IsEffectivelyVisible); // before the slide-out: it shows behind Now Playing as it goes
        Assert.True(sidebar.IsEffectivelyVisible);
        Gui.FinishAnimations(900);
        Assert.Equal(offset, scroller.Offset);
    }

    // ---- #124: the blurred background --------------------------------------------------------------------------

    private static BlurredCover Blurrer(AppHarness app) => Assert.IsType<BlurredCover>(app.Window.FindResource("BlurredBackdrop"));

    [AvaloniaFact]
    public async Task The_blurred_background_is_made_once_per_album_and_drawn_with_no_live_blur()
    {
        await using var app = await OpenAsync();
        var blurrer = Blurrer(app);
        var made = blurrer.Made;
        await PlayAsync(app);
        app.ViewModel.NowPlaying.Open();
        Gui.FinishAnimations(900);
        var background = Gui.Find<Image>(app.Window, "Backdrop");
        Assert.Equal(made + 1, blurrer.Made);
        var image = Assert.IsAssignableFrom<Bitmap>(background.Source);
        Assert.Null(background.Effect); // already blurred: nothing is blurred per frame
        Assert.True(image.PixelSize.Width <= 64, $"{image.PixelSize}: small, stretched");

        // Another track of the album, a pause, a resize: the same image.
        await PlayAsync(app, Album, track: 2);
        await PlayAsync(app, Album, track: 2, PlayerState.Paused);
        app.Window.Width -= 200;
        Gui.Settle();
        Assert.Equal(made + 1, blurrer.Made);
        Assert.Same(image, background.Source);

        // Another album: a new one.
        await PlayAsync(app, 7);
        await Gui.UntilAsync(() => !ReferenceEquals(image, background.Source) && background.Source is not null, "the new background");
        Assert.Equal(made + 2, blurrer.Made);
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task An_earlier_albums_cover_arriving_late_never_becomes_the_background()
    {
        var covers = SampleLibrary.Covers(accents: true);
        var slow = covers.Gates[SampleLibrary.AlbumId(Album)] = new TaskCompletionSource<Bitmap?>();
        await using var app = await OpenAsync(covers: covers);
        app.ViewModel.NowPlaying.Open();

        app.Scripted!.Status = ScriptedPlayer.Playing(SampleLibrary.Albums[Album - 1].Tracks[0], trackId: SampleLibrary.TrackId(Album, 1));
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => app.ViewModel.PlayingAlbumId == SampleLibrary.AlbumId(Album), "the first album");
        await PlayAsync(app, 7); // the next one, while the first's covers are still loading
        await Gui.UntilAsync(() => app.ViewModel.NowPlaying.BackdropCover is not null, "the second album's cover");
        var second = app.ViewModel.NowPlaying.BackdropCover;

        slow.SetResult(TestImages.Bitmap(TestImages.Cover(64))); // the first album's covers arrive now
        Gui.Settle();

        Assert.Same(second, app.ViewModel.NowPlaying.BackdropCover);
        Assert.Equal(SampleLibrary.AlbumId(7), app.ViewModel.PlayingAlbumId);
    }

    [AvaloniaFact]
    public async Task Smoked_Acrylic_shows_the_same_made_background_through_its_plinth_with_no_blur_of_its_own()
    {
        await using var app = await OpenAsync("acrylic");
        var made = Blurrer(app).Made;
        await PlayAsync(app);
        app.ViewModel.NowPlaying.Open();
        Gui.FinishAnimations(900);
        var background = Gui.Find<Image>(app.Window, "Backdrop");
        var deck = Assert.IsType<KitDeckView>(Gui.Find<DeckHost>(app.Window, "Deck").Deck);
        var glass = deck.GetVisualDescendants().OfType<BackdropView>().Single();

        Assert.Same(background, glass.Source);
        Assert.NotNull(background.Source);
        Assert.Null(glass.Effect);
        Assert.Equal(made + 1, Blurrer(app).Made); // one image, drawn twice: no second pass of its own
    }
}
