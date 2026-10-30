using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MusicSpin.App.Controls;
using MusicSpin.App.Services;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Settings;
using MusicSpin.Tests.Ui.Harness;
using SkiaSharp;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// What moving costs, by behaviour rather than time: while Now Playing or the queue drawer slides or fades, it and the page
/// under it draw from images (#128); the turntable scene moves by transforms and its record's shadow isn't blurred
/// (#129); and the deck's soft shadows are drawn once, not under the spinning record every frame (#122). Each image is
/// checked against the same thing drawn live.
/// </summary>
public sealed class MotionCostTests
{
    /// <summary>Night Transit: on screen as a card, with a cover.</summary>
    private const int Album = 6;

    /// <summary>Longer than Now Playing's slide (0.55 s) and fade (0.8 s), the queue drawer's (0.45 s), the arm's.</summary>
    private const int Settled = 1500;

    /// <param name="live">Nothing drawn from images: to compare with.</param>
    /// <param name="window">The window's size, when not the harness's.</param>
    /// <param name="scaling">The screen's scaling (2 on a 200% screen).</param>
    private static async Task<AppHarness> OpenAsync(string design = "classic", bool live = false, AppTheme theme = AppTheme.Dark, Size? window = null, double scaling = 1)
    {
        var app = await AppHarness.OpenAsync(new AppOptions
        {
            Width = window?.Width ?? 1440,
            Height = window?.Height ?? 880,
            Scripted = true,
            Library = SampleLibrary.Create(),
            Covers = SampleLibrary.Covers(accents: true),
            Theme = theme,
            Settings = new AppSettings { TurntableDesign = design },
        });
        app.Window.SetRenderScaling(scaling);
        if (live)
        {
            // With a class in the selector, as the app's own styles have: those outrank styles without one.
            foreach (var layer in new[] { "nowPlaying", "queue" })
            {
                app.Window.Styles.Add(new Style(x => x.OfType<SlidePanel>().Class(layer)) { Setters = { new Setter(SlidePanel.MotionCacheProperty, null) } });
            }

            app.Window.Styles.Add(new Style(x => x.Is<Control>().Class("underMotion")) { Setters = { new Setter(Visual.CacheModeProperty, null) } });
            app.Window.Styles.Add(new Style(x => x.OfType<DeckHost>().Descendant().Is<DeckView>().Template().Is<Control>().Class("soft"))
            {
                Setters = { new Setter(Visual.CacheModeProperty, null) },
            });
        }

        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 10, "the albums");
        app.ViewModel.AlbumsPage.Grouping = AlbumGrouping.None;
        app.ViewModel.AlbumsPage.ViewChoice = app.ViewModel.AlbumsPage.Views.Single(v => v.Value == AlbumsView.Cards);
        Gui.FinishAnimations(Settled);
        return app;
    }

    private static async Task PlayAsync(AppHarness app, PlayerState state = PlayerState.Playing)
    {
        var a = SampleLibrary.Albums[Album - 1];
        app.Scripted!.Status = ScriptedPlayer.Playing(a.Tracks[0], trackId: SampleLibrary.TrackId(Album, 1), state: state);
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => app.ViewModel.PlayingAlbumId == SampleLibrary.AlbumId(Album) && app.ViewModel.Player.State == state
                                   && app.ViewModel.NowPlaying.Tracks.Count == a.Tracks.Length, "the album loaded");
        Gui.Settle();
    }

    private static SlidePanel Layer(AppHarness app, string name) => Gui.Find<SlidePanel>(app.Window, name);

    /// <summary>What the panel holds: drawn from an image while it moves.</summary>
    private static Control Content(SlidePanel panel) => Assert.Single(panel.Children);

    /// <summary>The sidebar and the page under the panels.</summary>
    private static Control[] Under(AppHarness app) => [Gui.Find<Control>(app.Window, "SidebarView"), Gui.Find<Control>(app.Window, "ContentArea")];

    private static bool FromImage(Visual visual) => visual.CacheMode is BitmapCache;

    /// <summary>Drawn from images, the same, near enough, as drawn live.</summary>
    private static void AssertSame(SKBitmap fromImages, SKBitmap live, string name, int channelTolerance = 8, double pixelTolerance = 0.0005)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"musicspin-live-{Guid.NewGuid():N}.png");
        try
        {
            Snapshots.Save(live, path);
            var result = Snapshots.Compare(fromImages, path, System.IO.Path.Combine(Snapshots.FailureDirectory, name), channelTolerance, pixelTolerance);
            Assert.True(result.Matches, result.Problem);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Opens and closes the queue drawer: a panel's first slide in a new app can start where the shared test clock already
    /// is, so the two apps compared each move it once before the motion that is compared.
    /// </summary>
    private static void Warm(AppHarness app)
    {
        app.ViewModel.QueueDrawer.Open();
        Gui.FinishAnimations(Settled);
        app.ViewModel.QueueDrawer.Close();
        Gui.FinishAnimations(Settled);
    }

    /// <summary>The app's clock, a frame at a time (two apps compared step the same way).</summary>
    private static void Frames(AppHarness app, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            app.Advance(UiTime.Frame);
        }
    }

    // ---- #128: Now Playing and the queue drawer moving ----------------------------------------------------------

    [AvaloniaFact]
    public async Task While_Now_Playing_slides_it_and_the_page_under_it_draw_from_images_and_live_again_once_it_stops()
    {
        await using var app = await OpenAsync();
        await PlayAsync(app);
        var layer = Layer(app, "NowPlayingLayer");
        Assert.False(layer.IsMoving);
        Assert.All(Under(app).Append(Content(layer)), c => Assert.False(FromImage(c), c.Name));

        app.ViewModel.NowPlaying.Open();
        app.Advance(UiTime.Frame);
        var away = layer.RenderTransform;
        for (var t = 0; t < 20; t++)
        {
            app.Advance(UiTime.Frame);
            Assert.True(layer.IsMoving, $"{layer.Shown} at {t} frames");
            Assert.All(Under(app).Append(Content(layer)), c => Assert.True(FromImage(c), $"{c.Name} at {t} frames"));
            Assert.Same(away, layer.RenderTransform); // moved, not a new transform each step
        }

        Gui.FinishAnimations(Settled);
        Assert.Equal(1, layer.Shown);
        Assert.False(layer.IsMoving);
        Assert.All(Under(app).Append(Content(layer)), c => Assert.False(FromImage(c), c.Name));

        app.ViewModel.NowPlaying.Close();
        app.Advance(UiTime.Frame);
        app.Advance(UiTime.Frame);
        Assert.True(layer.IsMoving);
        Assert.All(Under(app).Append(Content(layer)), c => Assert.True(FromImage(c), c.Name));
        Gui.FinishAnimations(Settled);
        Assert.False(layer.IsVisible);
        Assert.All(Under(app).Append(Content(layer)), c => Assert.False(FromImage(c), c.Name));
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task A_slide_turned_back_midway_draws_from_images_until_it_stops_and_leaves_none_behind()
    {
        await using var app = await OpenAsync();
        await PlayAsync(app);
        var layer = Layer(app, "NowPlayingLayer");

        app.ViewModel.NowPlaying.Open();
        app.Advance(TimeSpan.FromMilliseconds(200));
        app.ViewModel.NowPlaying.Close(); // back down before it is up
        app.Advance(TimeSpan.FromMilliseconds(60));
        Assert.True(layer.IsMoving);
        Assert.All(Under(app).Append(Content(layer)), c => Assert.True(FromImage(c), c.Name));
        app.ViewModel.NowPlaying.Open(); // and up again
        app.Advance(TimeSpan.FromMilliseconds(60));
        Assert.True(layer.IsMoving);

        Gui.FinishAnimations(Settled);
        Assert.Equal(1, layer.Shown);
        Assert.All(Under(app).Append(Content(layer)), c => Assert.False(FromImage(c), c.Name));
        app.ViewModel.NowPlaying.Close();
        app.Advance(TimeSpan.FromMilliseconds(100));
        app.ViewModel.NowPlaying.Open();
        app.ViewModel.NowPlaying.Close();
        Gui.FinishAnimations(Settled);
        Assert.Equal(0, layer.Shown);
        Assert.All(Under(app).Append(Content(layer)), c => Assert.False(FromImage(c), c.Name));
    }

    [AvaloniaFact]
    public async Task While_the_queue_drawer_slides_it_the_page_and_an_open_Now_Playing_under_it_draw_from_images()
    {
        await using var app = await OpenAsync();
        await PlayAsync(app);
        var (queue, nowPlaying) = (Layer(app, "QueueLayer"), Layer(app, "NowPlayingLayer"));

        app.ViewModel.QueueDrawer.Open();
        app.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(queue.IsMoving);
        Assert.All(Under(app).Append(Content(queue)), c => Assert.True(FromImage(c), c.Name));
        Gui.FinishAnimations(Settled);
        Assert.All(Under(app).Append(Content(queue)), c => Assert.False(FromImage(c), c.Name));
        app.ViewModel.QueueDrawer.Close();
        Gui.FinishAnimations(Settled);

        app.ViewModel.NowPlaying.Open();
        Gui.FinishAnimations(Settled);
        app.ViewModel.QueueDrawer.Open(); // over Now Playing
        app.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(nowPlaying.IsUnder);
        Assert.True(FromImage(Content(nowPlaying)));
        Assert.False(nowPlaying.IsMoving);
        Gui.FinishAnimations(Settled);
        Assert.False(FromImage(Content(nowPlaying)));
        Assert.False(FromImage(Content(queue)));
    }

    /// <summary>Halfway up, halfway down, the queue drawer halfway in and Now Playing halfway faded in behind the scene.</summary>
    [AvaloniaTheory]
    [InlineData("up")]
    [InlineData("down")]
    [InlineData("queue")]
    [InlineData("fade")]
    public async Task Halfway_through_a_slide_or_fade_drawn_from_images_it_looks_the_same_as_drawn_live(string motion)
    {
        using var fromImages = await HalfwayAsync(live: false);
        using var live = await HalfwayAsync(live: true);
        AssertSame(fromImages, live, $"motion-{motion}");

        async Task<SKBitmap> HalfwayAsync(bool live)
        {
            await using var app = await OpenAsync(live: live);
            if (motion == "fade")
            {
                var source = Gui.All<Control>(app.Window).First(c => SceneSource.GetAlbumId(c) == SampleLibrary.AlbumId(Album));
                Gui.Click(app.Window, source.GetVisualAncestors().OfType<Button>().First());
                await Gui.UntilAsync(() => app.ViewModel.Scene.IsRunning, "the scene started");
                Gui.Settle();
                Frames(app, (int)((SceneTimeline.OpenAt + 400) / 16));
                Assert.True(Layer(app, "NowPlayingLayer") is { IsMoving: true, IsFading: true });
            }
            else
            {
                await PlayAsync(app);
                if (motion == "down")
                {
                    app.ViewModel.NowPlaying.Open();
                    Gui.FinishAnimations(Settled);
                    app.ViewModel.NowPlaying.Close();
                }
                else if (motion == "up")
                {
                    app.ViewModel.NowPlaying.Open(); // up and down once first (Warm)
                    Gui.FinishAnimations(Settled);
                    app.ViewModel.NowPlaying.Close();
                    Gui.FinishAnimations(Settled);
                    app.ViewModel.NowPlaying.Open();
                }
                else
                {
                    Warm(app);
                    app.ViewModel.QueueDrawer.Open();
                }

                Frames(app, 14);
            }

            Assert.Contains(Gui.All<SlidePanel>(app.Window), p => p.IsMoving);
            Assert.Equal(!live, Gui.All<SlidePanel>(app.Window).Where(p => p.IsMoving).All(p => FromImage(Content(p))));
            return Snapshots.Capture(app.Window);
        }
    }

    // ---- #129: the turntable scene ------------------------------------------------------------------------------

    private static SceneFlight Flight(AppHarness app)
        => Gui.All<SceneFlight>(OverlayLayer.GetOverlayLayer(app.Window)!).Single();

    private static async Task StartSceneAsync(AppHarness app)
    {
        var source = Gui.All<Control>(app.Window).First(c => SceneSource.GetAlbumId(c) == SampleLibrary.AlbumId(Album));
        Gui.Click(app.Window, source.GetVisualAncestors().OfType<Button>().First());
        await Gui.UntilAsync(() => app.ViewModel.Scene.IsRunning, "the scene started");
        Gui.Settle();
    }

    [AvaloniaFact]
    public async Task The_scene_moves_its_sleeve_and_record_by_transforms_alone_and_blurs_no_shadow()
    {
        await using var app = await OpenAsync();
        await StartSceneAsync(app);
        var flight = Flight(app);
        app.Advance(UiTime.Frame);
        var (pack, record, shadow) = (flight.Pack!, flight.Record!, flight.Shadow!);
        var moves = (pack.RenderTransform, record.RenderTransform, shadow.RenderTransform);
        var (lays, place, size) = (flight.Lays, (Canvas.GetLeft(pack), Canvas.GetTop(pack)), pack.Bounds.Size);
        var blurs = new HashSet<double>();

        for (var t = 0.0; t < SceneTimeline.LandAt; t += 50)
        {
            app.Advance(TimeSpan.FromMilliseconds(50));
            Assert.Equal(lays, flight.Lays); // no step laid it out again
            Assert.Equal(place, (Canvas.GetLeft(pack), Canvas.GetTop(pack)));
            Assert.Equal(size, pack.Bounds.Size);
            Assert.Equal(moves, (pack.RenderTransform, record.RenderTransform, shadow.RenderTransform));
            Assert.Null(record.Effect); // its shadow is drawn, not an effect re-blurred each frame
            Assert.Equal(record.Bounds, shadow.Bounds);
            Assert.True(shadow.ZIndex == record.ZIndex && shadow.GetVisualParent() == record.GetVisualParent());
            blurs.Add(shadow.Blur);
        }

        Assert.True(blurs.Count > 5, "its blur follows the keyframes");
        Assert.Contains(6.0, blurs); // out of the sleeve: the first keyframe's
    }

    [AvaloniaFact]
    public async Task The_records_shadow_lies_under_it_as_its_drop_shadow_would_offset_and_lifted_with_it()
    {
        await using var app = await OpenAsync();
        await StartSceneAsync(app);
        var flight = Flight(app);

        // A third of the way across: lifted highest, its shadow furthest (34 below, blurred 30, at 0xAA).
        app.Advance(TimeSpan.FromMilliseconds((SceneTimeline.CrossAt + SceneTimeline.CrossDelayMs + (0.42 * SceneTimeline.CrossMs)) * MotionScale.Current.Multiplier));
        var (record, shadow) = (flight.Record!.RenderTransform!.Value, flight.Shadow!.RenderTransform!.Value);
        var below = shadow.Transform(new Point(0, 0)) - record.Transform(new Point(0, 0));
        Assert.Equal(0, below.X, 3);
        Assert.InRange(below.Y, 30, 36 * 1.06); // its offset, scaled with the lifted record
        Assert.InRange(flight.Shadow.Blur, 25, 31);
        Assert.InRange(flight.Shadow.Strength, 0.6, 0.67);
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task Resizing_the_window_mid_scene_lays_the_pack_out_again_once_not_on_every_step_after()
    {
        await using var app = await OpenAsync();
        await StartSceneAsync(app);
        var flight = Flight(app);
        app.Advance(TimeSpan.FromMilliseconds(300));
        var lays = flight.Lays;
        var bounds = flight.Bounds;

        (app.Window.Width, app.Window.Height) = (app.Window.Width - 200, app.Window.Height - 120);
        Gui.Settle();
        app.Advance(TimeSpan.FromMilliseconds(50));

        Assert.NotEqual(bounds, flight.Bounds);
        Assert.InRange(flight.Lays - lays, 1, 3); // its place changed: laid out again, not on every step after
        var after = flight.Lays;
        app.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(after, flight.Lays);
    }

    /// <summary>Under a black disc 200 wide: its DropShadowEffect, and a DiscShadow standing in for it.</summary>
    [AvaloniaTheory]
    [InlineData(3, 2, 0.53)]
    [InlineData(24, 26, 0.6)]
    [InlineData(30, 34, 0.67)]
    public void A_disc_shadow_looks_like_the_drop_shadow_it_stands_in_for(double blur, double below, double strength)
    {
        using var effect = Draw(new Ellipse
        {
            Width = 200, Height = 200, Fill = Brushes.SlateGray,
            Effect = new DropShadowEffect { BlurRadius = blur, OffsetX = 0, OffsetY = below, Color = Colors.Black, Opacity = strength },
        });
        var shadow = new DiscShadow { Width = 200, Height = 200, Blur = blur, Strength = strength, RenderTransform = new TranslateTransform(0, below) };
        using var drawn = Draw(new Panel { Children = { shadow, new Ellipse { Width = 200, Height = 200, Fill = Brushes.SlateGray } } });

        AssertSame(drawn, effect, $"disc-shadow-{blur}", channelTolerance: 6, pixelTolerance: 0.002);
        Assert.Equal(1, shadow.Made);

        static SKBitmap Draw(Control disc)
        {
            disc.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
            disc.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
            var window = new Window { Width = 360, Height = 360, Background = Brushes.White, Content = disc };
            window.Show();
            try
            {
                return Snapshots.Capture(window);
            }
            finally
            {
                window.Close();
            }
        }
    }

    [AvaloniaTheory]
    [Trait("Category", "Unhappy")]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_disc_shadow_without_blur_is_the_sharp_disc_and_without_strength_draws_nothing(double blur)
    {
        Assert.Equal(0, DiscShadow.Sigma(blur));
        Assert.Equal(1, DiscShadow.Falloff(99, 100, 0));
        Assert.Equal(0, DiscShadow.Falloff(101, 100, 0));
        Assert.Equal(0.5, DiscShadow.Falloff(100, 100, 5), 6); // at the edge: half
        Assert.True(DiscShadow.Falloff(85, 100, 5) > 0.998 && DiscShadow.Falloff(115, 100, 5) < 0.002);

        var shadow = new DiscShadow { Width = 100, Height = 100, Blur = 10, Strength = 0 };
        var window = new Window { Width = 200, Height = 200, Content = shadow };
        window.Show();
        using (window.CaptureRenderedFrame())
        {
        }

        window.Close();
        Assert.Equal(0, shadow.Made);
    }

    // ---- #122: the deck's soft shadows under the spinning record ------------------------------------------------

    public static TheoryData<string> Designs => [.. DeckDesigns.All.Select(d => d.Id)];

    [AvaloniaTheory]
    [MemberData(nameof(Designs))]
    public async Task Now_Playings_deck_draws_its_soft_shadows_from_images_none_over_the_record(string design)
    {
        await using var app = await OpenAsync(design);
        await PlayAsync(app);
        app.ViewModel.NowPlaying.Open();
        Gui.FinishAnimations(Settled);
        var deck = Gui.Find<DeckHost>(app.Window, "Deck").Deck!;

        var soft = deck.GetVisualDescendants().OfType<Control>().Where(c => c.Classes.Contains("soft")).ToList();
        Assert.NotEmpty(soft);
        Assert.All(soft, c => Assert.True(FromImage(c), string.Join('.', c.Classes)));
        // The spinning record is in none of them, so its turning doesn't draw them again.
        Assert.All(soft, c => Assert.DoesNotContain(deck.Record!, c.GetVisualDescendants()));
        Assert.All(soft, c => Assert.Null(c.Effect is DropShadowEffect ? c : null));
    }

    [AvaloniaFact]
    public async Task Settings_still_previews_of_the_designs_hold_no_images()
    {
        await using var app = await OpenAsync();
        app.ShowSettingsPage("Turntable");
        app.ViewModel.Settings.Open();
        Gui.FinishAnimations(Settled);

        var previews = Gui.All<KitDeckView>(app.Window).ToList();
        Assert.NotEmpty(previews);
        Assert.All(previews.SelectMany(d => d.GetVisualDescendants()).OfType<Control>().Where(c => c.Classes.Contains("soft")),
            c => Assert.False(FromImage(c)));
    }

    [AvaloniaTheory]
    [MemberData(nameof(Designs))]
    public async Task A_deck_with_its_soft_shadows_from_images_looks_the_same_as_drawn_live(string design)
    {
        using var fromImages = await DeckImageAsync(design, live: false);
        using var live = await DeckImageAsync(design, live: true);
        // Soft shadows from an image scaled to the deck: their clipped edge, over a plinth's ledge, may be a few levels off
        // along a pixel. Classic's arm shadow is one shape faded, from its image, as the mockup's (.arm-shadow, opacity
        // on the whole); drawn live, Avalonia fades its tube and headshell each, so they darken where they overlap.
        AssertSame(fromImages, live, $"deck-cache-{design}", channelTolerance: 16, pixelTolerance: 0.001);
    }

    /// <summary>
    /// A bigger window shows the deck bigger, and its soft image is drawn bigger with it, not stretched; a 200% screen
    /// draws every image at twice the pixels already.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("classic", 2560, 1440, 1)]
    [InlineData("piano", 2560, 1440, 1)]
    [InlineData("classic", 1440, 880, 2)]
    [InlineData("piano", 1440, 880, 2)]
    public async Task On_a_big_window_or_a_200_percent_screen_a_deck_from_images_looks_the_same_as_drawn_live(string design, double width, double height, double scaling)
    {
        using var fromImages = await DeckImageAsync(design, live: false, new Size(width, height), scaling);
        using var live = await DeckImageAsync(design, live: true, new Size(width, height), scaling);
        AssertSame(fromImages, live, $"deck-cache-{design}-{width}x{height}@{scaling}", channelTolerance: 16, pixelTolerance: 0.001);
    }

    private static async Task<SKBitmap> DeckImageAsync(string design, bool live, Size? window = null, double scaling = 1)
    {
        await using var app = await OpenAsync(design, live, AppTheme.Light, window, scaling);
        await PlayAsync(app, PlayerState.Paused); // the record still: both apps' records at the same angle
        app.ViewModel.NowPlaying.Open();
        Gui.FinishAnimations(Settled);
        return Snapshots.Capture(app.Window, Gui.Find<DeckHost>(app.Window, "Deck"));
    }

    private static async Task<(AppHarness App, DeckHost Host)> NowPlayingDeckAsync(string design)
    {
        var app = await OpenAsync(design);
        await PlayAsync(app);
        app.ViewModel.NowPlaying.Open();
        Gui.FinishAnimations(Settled);
        return (app, Gui.Find<DeckHost>(app.Window, "Deck"));
    }

    private static Control[] Soft(DeckView deck) => [.. deck.GetVisualDescendants().OfType<Control>().Where(c => c.Classes.Contains("soft"))];

    /// <summary>How big one unit of a soft layer's drawing is in <paramref name="other"/> (in the window: its pixels, before the screen's scaling).</summary>
    private static double Shown(Control soft, Visual other)
    {
        var m = soft.TransformToVisual(other)!.Value;
        return Math.Sqrt((m.M11 * m.M11) + (m.M12 * m.M12)); // the arm's is turned
    }

    [AvaloniaTheory]
    [InlineData("classic")]
    [InlineData("piano")]
    public async Task A_decks_soft_image_has_the_pixels_the_deck_shows_its_drawing_at_however_big_the_window(string design)
    {
        var (app, host) = await NowPlayingDeckAsync(design);
        await using (app)
        {
            foreach (var (width, height) in new[] { (1440.0, 880.0), (2560.0, 1440.0), (3840.0, 2100.0), (1100.0, 720.0) })
            {
                (app.Window.Width, app.Window.Height) = (width, height);
                Gui.Settle();
                var soft = Soft(host.Deck!);
                Assert.NotEmpty(soft);
                Assert.All(soft, c => Assert.Same(host.SoftCache, c.CacheMode));
                var shown = soft.Max(c => Shown(c, app.Window));
                Assert.True(shown > 0.3 * width / 1440, $"{width} wide: the deck shows its drawing at {shown}");
                Assert.InRange(host.SoftCache.RenderAtScale, shown - (DeckHost.SoftCacheSlack * DeckHost.SoftCacheStep) - 1e-9, shown + DeckHost.SoftCacheStep);
            }
        }
    }

    /// <summary>A deck's own say of how big its drawing's units are, against how big its soft layers are drawn in it.</summary>
    [AvaloniaTheory]
    [MemberData(nameof(Designs))]
    public async Task A_decks_unit_scale_is_the_scale_its_soft_layers_are_drawn_at(string design)
    {
        var (app, host) = await NowPlayingDeckAsync(design);
        await using (app)
        {
            var deck = host.Deck!;
            Assert.All(Soft(deck), c => Assert.Equal(deck.UnitScale, Shown(c, deck), 6));
        }
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task A_decks_soft_image_stays_for_a_small_resize_follows_a_new_design_and_never_shrinks_to_nothing()
    {
        var (app, host) = await NowPlayingDeckAsync("classic");
        await using (app)
        {
            var image = host.SoftCache;
            Assert.Equal(1, image.RenderAtScale);

            app.Window.Height -= 4; // the deck a hair smaller: the same image, not drawn again
            Gui.Settle();
            Assert.Same(image, host.SoftCache);

            host.SetCurrentValue(DeckHost.DesignProperty, DeckDesigns.All.Single(d => d.Id == "piano")); // drawn in units 0.4 big
            Gui.Settle();
            Assert.Equal(0.5, host.SoftCache.RenderAtScale);
            Assert.All(Soft(host.Deck!), c => Assert.Same(host.SoftCache, c.CacheMode));

            host.HeightShare = 0.001; // a deck almost too small to see: still an image of a step's pixels
            Gui.Settle();
            Assert.Equal(DeckHost.SoftCacheStep, host.SoftCache.RenderAtScale);
        }
    }

    [AvaloniaFact]
    public async Task The_classic_arms_shadow_image_has_room_for_its_turned_headshell_and_blur()
    {
        var (app, _) = await NowPlayingDeckAsync("classic");
        await using (app)
        {
            var blur = Gui.All<Panel>(app.Window).Single(p => p.Classes.Contains("armShadowBlur"));
            Assert.True(FromImage(blur));
            var frame = Assert.Single(blur.Children, c => c.Classes.Contains("cacheFrame"));
            Assert.Equal(0, Assert.IsAssignableFrom<ISolidColorBrush>(((Border)frame).Background).Color.A); // draws nothing
            var radius = Assert.IsType<BlurEffect>(blur.Effect).Radius;
            foreach (var part in blur.Children.Where(c => c != frame))
            {
                var drawn = new Rect(part.Bounds.Size).TransformToAABB(part.TransformToVisual(blur)!.Value).Inflate(radius);
                Assert.True(frame.Bounds.Contains(drawn), $"{string.Join('.', part.Classes)} {drawn} in {frame.Bounds}");
            }
        }
    }

    // ---- review follow-ups ------------------------------------------------------------------------------------

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public void A_panel_at_rest_leaves_what_it_holds_to_its_styles_image()
    {
        var (styled, motion) = (new BitmapCache(), new BitmapCache());
        var held = new Border { Classes = { "held" } };
        var panel = new SlidePanel { IsOpen = true, MotionCache = motion, Children = { held } };
        var window = new Window { Width = 400, Height = 300, Content = panel };
        window.Styles.Add(new Style(x => x.OfType<Border>().Class("held")) { Setters = { new Setter(Visual.CacheModeProperty, styled) } });
        window.Show();
        try
        {
            Assert.Same(styled, held.CacheMode);
            panel.Shown = 0.5;
            Assert.Same(motion, held.CacheMode);
            panel.Shown = 1;
            Assert.Same(styled, held.CacheMode); // not a value of its own over the style's
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task A_landing_gone_mid_scene_stops_the_pack_where_it_is_and_one_back_carries_it_on()
    {
        await using var app = await OpenAsync();
        await StartSceneAsync(app);
        var flight = Flight(app);
        app.Advance(TimeSpan.FromMilliseconds(300));
        var landing = flight.Landing;
        Assert.True(landing.Width > 0);

        flight.SetCurrentValue(SceneFlight.LandingProperty, default(Rect)); // the deck gone from under it
        var (pack, record) = (flight.Pack!.RenderTransform!.Value, flight.Record!.RenderTransform!.Value);
        app.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(pack, flight.Pack.RenderTransform!.Value);
        Assert.Equal(record, flight.Record.RenderTransform!.Value);

        flight.SetCurrentValue(SceneFlight.LandingProperty, landing);
        app.Advance(TimeSpan.FromMilliseconds(100));
        Assert.NotEqual(pack, flight.Pack.RenderTransform!.Value);
    }
}
