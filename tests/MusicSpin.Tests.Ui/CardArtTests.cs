using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MusicSpin.App.Controls;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Settings;
using MusicSpin.Tests.Ui.Harness;
using SkiaSharp;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// Album cards' art (#149): the loaded album's card keeps its record still (the shelf's still turns), so nothing on the
/// card is drawn again each frame; and a card's art at rest draws from an image of it, live only while it moves.
/// </summary>
public sealed class CardArtTests
{
    /// <summary>Night Transit: on screen as a card, with a cover.</summary>
    private const int Album = 6;

    /// <summary>Longer than a card's moves (the record's 0.45 s, the sleeve's 0.35 s) and the shelf's.</summary>
    private const int Settled = 1500;

    /// <param name="live">Cards drawn live even at rest (no <see cref="CardArt.RestCache"/>), to compare with.</param>
    private static async Task<AppHarness> OpenAsync(bool live = false, AppTheme theme = AppTheme.Dark, double width = 1440, double scaling = 1)
    {
        var app = await AppHarness.OpenAsync(new AppOptions
        {
            Width = width,
            Scripted = true,
            Library = SampleLibrary.Create(),
            Covers = SampleLibrary.Covers(accents: true),
            Theme = theme,
        });
        app.Window.SetRenderScaling(scaling);
        if (live)
        {
            app.Window.Styles.Add(new Style(x => x.OfType<CardArt>()) { Setters = { new Setter(CardArt.RestCacheProperty, null) } });
        }

        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 10, "the albums");
        app.ViewModel.AlbumsPage.Grouping = AlbumGrouping.None;
        Show(app, AlbumsView.Cards);
        Gui.FinishAnimations(Settled);
        return app;
    }

    private static void Show(AppHarness app, AlbumsView view)
    {
        var page = app.ViewModel.AlbumsPage;
        page.ViewChoice = page.Views.Single(v => v.Value == view);
        Gui.Settle();
    }

    private static async Task PlayAsync(AppHarness app, int album = Album, PlayerState state = PlayerState.Playing)
    {
        var a = SampleLibrary.Albums[album - 1];
        app.Scripted!.Status = ScriptedPlayer.Playing(a.Tracks[0], trackId: SampleLibrary.TrackId(album, 1), state: state);
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => app.ViewModel.PlayingAlbumId == SampleLibrary.AlbumId(album) && app.ViewModel.Player.State == state, "the album loaded");
        Gui.Settle();
    }

    /// <summary>Lets the spin clock run for <paramref name="frames"/> frames, rendering each.</summary>
    private static void Spin(AppHarness app, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            app.Advance(UiTime.Frame);
            Gui.Settle();
            using var frame = app.Window.CaptureRenderedFrame();
        }
    }

    private static AlbumItemViewModel Item(AppHarness app, int album = Album)
        => app.ViewModel.AlbumsPage.Albums.Single(a => a.Album.Id == SampleLibrary.AlbumId(album));

    private static List<Button> Cards(Window window) => [.. Gui.All<Button>(window).Where(b => b.Classes.Contains("card") && b.IsEffectivelyVisible)];

    private static Button Card(Window window, int album = Album)
        => Cards(window).Single(b => b.DataContext is AlbumItemViewModel a && a.Album.Id == SampleLibrary.AlbumId(album));

    private static CardArt Art(Button card) => card.GetVisualDescendants().OfType<CardArt>().Single();

    private static VinylView Record(Button card) => card.GetVisualDescendants().OfType<VinylView>().Single();

    /// <summary>Drawn through the image of it at rest, not live.</summary>
    private static bool Cached(CardArt art) => art.CacheMode is not null && ReferenceEquals(art.CacheMode, art.RestCache);

    /// <summary>Counts the changes <paramref name="item"/> raises for its record's angle.</summary>
    private static Func<int> CountTurns(AlbumItemViewModel item)
    {
        var turns = 0;
        item.PropertyChanged += (_, e) => turns += e.PropertyName == nameof(AlbumItemViewModel.DiscAngle) ? 1 : 0;
        return () => turns;
    }

    // ---- The loaded album's record ------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task In_Cards_the_playing_cards_record_stays_still_and_nothing_on_the_card_changes_while_the_player_bars_turns()
    {
        await using var app = await OpenAsync();
        await PlayAsync(app);
        Gui.FinishAnimations(Settled);
        var window = app.Window;
        var card = Card(window);
        Assert.Contains("playing", card.Classes);
        var (record, art, mini) = (Record(card), Art(card), Gui.Find<VinylView>(window, "MiniVinyl"));
        Spin(app, 2);
        var (renders, angle, turns) = (record.DiscRenders, app.ViewModel.SpinClock.Angle, CountTurns(Item(app)));

        Spin(app, 30);

        Assert.NotEqual(angle, app.ViewModel.SpinClock.Angle);
        Assert.Equal(app.ViewModel.SpinClock.Angle, mini.RenderedAngle); // the player bar's record turns
        Assert.Equal(0, record.RenderedAngle); // the card's doesn't
        Assert.Equal(0, Item(app).DiscAngle);
        Assert.Equal(0, turns()); // nothing told to the card each frame
        Assert.Equal(renders, record.DiscRenders);
        Assert.True(art.IsAtRest && Cached(art), "the playing card's art is still: drawn from its image");
    }

    [AvaloniaFact]
    public async Task On_the_shelf_the_playing_sleeves_record_still_turns_with_the_clock()
    {
        await using var app = await OpenAsync();
        await PlayAsync(app);
        Show(app, AlbumsView.Shelf);
        Gui.FinishAnimations(Settled);
        var sleeve = Gui.All<ShelfSleeve>(app.Window).Single(s => s.IsVisible && s.IsPlaying);
        var record = Gui.Find<VinylView>(sleeve, "PART_Vinyl");
        Spin(app, 2);
        var angle = record.RenderedAngle;

        Spin(app, 30);

        Assert.NotEqual(angle, record.RenderedAngle);
        Assert.Equal(app.ViewModel.SpinClock.Angle, record.RenderedAngle);
        Assert.Equal(app.ViewModel.SpinClock.Angle, Item(app).DiscAngle);
    }

    [AvaloniaFact]
    public async Task Switching_from_the_shelf_to_Cards_or_Crate_stops_the_record_upright_and_back_to_the_shelf_turns_it_from_the_clock()
    {
        await using var app = await OpenAsync();
        await PlayAsync(app);
        Show(app, AlbumsView.Shelf);
        Spin(app, 30);
        Assert.NotEqual(0, Item(app).DiscAngle);

        foreach (var still in new[] { AlbumsView.Cards, AlbumsView.Crate })
        {
            Show(app, still);
            var turns = CountTurns(Item(app));
            Spin(app, 10);
            Assert.Equal((0d, 0), (Item(app).DiscAngle, turns()));
            Show(app, AlbumsView.Shelf);
            Assert.Equal(app.ViewModel.SpinClock.Angle, Item(app).DiscAngle); // where the clock is, not where it stopped
        }

        Show(app, AlbumsView.Cards);
        Gui.FinishAnimations(Settled);
        Spin(app, 2);
        Assert.Equal(0, Record(Card(app.Window)).RenderedAngle);
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task With_nothing_loaded_or_the_loaded_album_searched_away_switching_views_turns_no_record()
    {
        await using var app = await OpenAsync();
        var page = app.ViewModel.AlbumsPage;
        foreach (var view in new[] { AlbumsView.Shelf, AlbumsView.Crate, AlbumsView.Cards })
        {
            Show(app, view); // nothing loaded: nothing to turn
            Spin(app, 5);
        }

        Assert.All(page.Albums, a => Assert.Equal(0, a.DiscAngle));

        await PlayAsync(app);
        app.ViewModel.SearchText = "Partitas";
        Show(app, AlbumsView.Shelf);
        Gui.FinishAnimations(Settled);
        Spin(app, 10);
        var shown = Assert.Single(Gui.All<ShelfSleeve>(app.Window), s => s.IsVisible);
        Assert.False(shown.IsPlaying);
        Assert.Equal(0, Gui.Find<VinylView>(shown, "PART_Vinyl").RenderedAngle);

        app.ViewModel.SearchText = string.Empty; // back on the shelf: it turns with the clock
        Gui.FinishAnimations(Settled);
        Spin(app, 2);
        var playing = Gui.All<ShelfSleeve>(app.Window).Single(s => s.IsVisible && s.IsPlaying);
        Assert.Equal(app.ViewModel.SpinClock.Angle, Gui.Find<VinylView>(playing, "PART_Vinyl").RenderedAngle);
    }

    [AvaloniaFact]
    public async Task A_new_album_loaded_leaves_the_old_cards_record_upright_and_the_new_ones_still()
    {
        await using var app = await OpenAsync();
        await PlayAsync(app);
        Show(app, AlbumsView.Shelf);
        Spin(app, 20);
        await PlayAsync(app, 4); // Partitas
        Spin(app, 5);
        Assert.Equal(0, Item(app).DiscAngle); // the old one put back upright
        Assert.Equal(app.ViewModel.SpinClock.Angle, Item(app, 4).DiscAngle);

        Show(app, AlbumsView.Cards);
        await PlayAsync(app, 8); // Copper Sun, in Cards
        Spin(app, 10);
        Assert.All(app.ViewModel.AlbumsPage.Albums, a => Assert.Equal(0, a.DiscAngle));
    }

    // ---- The art's image ----------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task A_cards_art_is_drawn_from_its_image_at_rest_and_live_while_it_moves_on_hover_and_as_its_album_loads()
    {
        await using var app = await OpenAsync();
        var window = app.Window;
        var cards = Cards(window);
        Assert.IsType<BitmapCache>(Art(cards[0]).RestCache);
        Assert.All(cards, c => Assert.True(Cached(Art(c)), $"{c.DataContext} isn't drawn from its image"));

        var hovered = Card(window);
        Gui.Hover(window, hovered);
        var moving = Frames(hovered);
        Assert.True(moving > 5, $"moving for {moving} frames");
        Gui.FinishAnimations(Settled);
        Assert.True(Cached(Art(hovered)), "lifted, at rest: from its image again");

        await PlayAsync(app);
        moving = Frames(hovered);
        Assert.True(moving > 5, $"sliding out for {moving} frames");
        Gui.FinishAnimations(Settled);
        Assert.True(Cached(Art(hovered)), "loaded, its record out and still: from its image");

        // Counts the frames the card's art is drawn live while every other card stays drawn from its image.
        int Frames(Button card)
        {
            var live = 0;
            for (var t = 0; t < 40; t++)
            {
                app.Advance(UiTime.Frame);
                Assert.All(cards.Where(c => c != card), c => Assert.True(Cached(Art(c)), $"{c.DataContext} at {t} frames"));
                live += Cached(Art(card)) ? 0 : 1;
            }

            return live;
        }
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task A_card_left_mid_hover_or_given_other_albums_mid_slide_ends_at_rest_drawn_from_its_image()
    {
        await using var app = await OpenAsync();
        var window = app.Window;
        var card = Card(window);

        Gui.Hover(window, card);
        app.Advance(TimeSpan.FromMilliseconds(120));
        Assert.False(Cached(Art(card)));
        window.MouseMove(new Point(5, 5)); // off it, mid-move: it goes back from where it is
        app.Advance(TimeSpan.FromMilliseconds(60));
        Assert.False(Cached(Art(card)));
        Gui.FinishAnimations(Settled);
        Assert.True(Cached(Art(card)));

        await PlayAsync(app);
        app.Advance(TimeSpan.FromMilliseconds(150));
        Assert.False(Cached(Art(card)), "sliding out");
        app.ViewModel.AlbumsPage.Sort = LibrarySort.Title; // the rows hand their cards other albums mid-slide
        Gui.Settle();
        Gui.FinishAnimations(Settled);
        Assert.All(Cards(window), c => Assert.True(Art(c).IsAtRest && Cached(Art(c)), $"{c.DataContext}"));
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public void A_part_a_hair_off_its_target_counts_as_at_rest_one_really_moved_doesnt_and_without_an_image_its_drawn_live()
    {
        var cache = new BitmapCache();
        var (record, sleeve) = (new Border { Width = 10, Height = 10 }, new Border { Width = 10, Height = 10 });
        var art = new CardArt { RestCache = cache, Child = new Panel { Children = { record, sleeve } } };
        var window = new Window { Width = 200, Height = 200, Content = art };
        window.Show();
        Gui.Settle();
        Assert.Same(cache, art.CacheMode);

        // A transition's value (above its target) a hair off its target, then really off it, then gone.
        sleeve.RenderTransform = new TranslateTransform(4, 0); // the target its styles give it: still
        Assert.Same(cache, art.CacheMode);
        var hair = sleeve.SetValue(Visual.RenderTransformProperty, new TranslateTransform(4 + 1e-7, 0), BindingPriority.Animation);
        Assert.True(art.IsAtRest);
        Assert.Same(cache, art.CacheMode);
        hair?.Dispose();
        var moved = sleeve.SetValue(Visual.RenderTransformProperty, new TranslateTransform(2, 0), BindingPriority.Animation);
        Assert.False(art.IsAtRest);
        Assert.Null(art.CacheMode);
        var scaled = record.SetValue(Visual.RenderTransformProperty, new ScaleTransform(1.5, 1.5), BindingPriority.Animation); // both moving
        moved?.Dispose();
        Assert.Null(art.CacheMode); // the record still is
        scaled?.Dispose();
        Assert.Same(cache, art.CacheMode);

        // Another child: the old one's moves no longer count, the new one's do.
        var lone = new Border();
        art.Child = lone;
        var old = record.SetValue(Visual.RenderTransformProperty, new TranslateTransform(9, 9), BindingPriority.Animation);
        Assert.Same(cache, art.CacheMode);
        var now = lone.SetValue(Visual.RenderTransformProperty, new TranslateTransform(9, 9), BindingPriority.Animation);
        Assert.Null(art.CacheMode);
        now?.Dispose();
        old?.Dispose();
        Assert.Same(cache, art.CacheMode);

        // No image to draw through: live, still or not.
        art.RestCache = null;
        Assert.Null(art.CacheMode);
        art.Child = null;
        Assert.True(art.IsAtRest);
        Assert.Null(art.CacheMode);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_arts_invisible_frame_takes_no_pointer_over_the_card_beside_it_and_its_parts_still_do()
    {
        await using var app = await OpenAsync();
        await PlayAsync(app, state: PlayerState.Paused); // the loaded card's frame reaches furthest
        Gui.FinishAnimations(Settled);
        Spin(app, 1);
        var window = app.Window;
        var loaded = Card(window);
        var y = Gui.BoundsIn(window, loaded).Top;
        var row = Cards(window).Where(c => Gui.BoundsIn(window, c).Top == y).OrderBy(c => Gui.BoundsIn(window, c).Left).ToList();
        var at = row.IndexOf(loaded);
        Assert.True(at > 0, "the loaded card has one to its left");
        row = [row[at - 1], loaded];
        var (a, b) = (Gui.BoundsIn(window, Art(row[0])), Gui.BoundsIn(window, Art(row[1])));
        Assert.True(b.Left + Art(row[1]).DrawnFrame.Left < a.Right - 4, "the frame reaches over the card beside it");

        // Just inside the first card's art, under the second's frame: the first card's.
        Assert.Same(row[0], CardAt(new Point(a.Right - 4, a.Center.Y)));
        // Between them: the second's cover, shifted left over the gap.
        Assert.Same(row[1], CardAt(new Point((a.Right + b.Left) / 2, b.Center.Y)));
        // On the second's cover: the second's.
        Assert.Same(row[1], CardAt(b.Center));

        Button? CardAt(Point point) => (window.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault(c => c.Classes.Contains("card"));
    }

    [AvaloniaFact]
    public async Task Only_the_loaded_cards_frame_has_room_for_its_shifted_cover_and_record_out()
    {
        await using var app = await OpenAsync();
        var window = app.Window;
        var idle = Art(Card(window)).DrawnFrame;
        await PlayAsync(app, state: PlayerState.Paused);
        Gui.FinishAnimations(Settled);
        Spin(app, 1);

        var loaded = Art(Card(window)).DrawnFrame;
        Assert.True(loaded.Left < idle.Left && loaded.Right > idle.Right, $"{loaded} around {idle}");
        Assert.All(Cards(window).Where(c => c != Card(window)), c => Assert.Equal(idle.Size, Art(c).DrawnFrame.Size));
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task On_a_screen_of_another_scaling_the_frame_is_drawn_again_on_whole_pixels()
    {
        await using var app = await OpenAsync();
        Spin(app, 1);

        foreach (var scaling in new[] { 2, 1.5, 1.25, 1 })
        {
            app.Window.SetRenderScaling(scaling); // the window moved to another screen
            Spin(app, 1);
            Assert.All(Cards(app.Window), c =>
            {
                var art = Art(c);
                Assert.Equal(art.FrameRect, art.DrawnFrame);
                var origin = Gui.BoundsIn(app.Window, art).TopLeft + art.DrawnFrame.TopLeft;
                Assert.Equal(Math.Round(origin.X * scaling), origin.X * scaling, 6);
                Assert.Equal(Math.Round(origin.Y * scaling), origin.Y * scaling, 6);
            });
        }
    }

    [AvaloniaFact]
    public async Task The_art_animates_nothing_but_its_parts_transforms_which_is_all_it_watches_to_know_its_moving()
    {
        await using var app = await OpenAsync();
        await PlayAsync(app);
        Gui.FinishAnimations(Settled);
        foreach (var card in Cards(app.Window))
        {
            var art = Art(card);
            var parts = ((Panel)art.Child!).Children;
            foreach (var animated in art.GetVisualDescendants().OfType<Avalonia.Animation.Animatable>().Where(v => v.Transitions is { Count: > 0 }))
            {
                Assert.Contains((Control)animated, parts); // a part, not something in one
                Assert.All(animated.Transitions!, t => Assert.Equal(Visual.RenderTransformProperty, Assert.IsAssignableFrom<Avalonia.Animation.TransitionBase>(t).Property));
            }
        }
    }

    [AvaloniaTheory]
    [InlineData(1440, 1)]
    [InlineData(1100, 2)]
    public async Task Cards_drawn_from_their_images_look_the_same_as_drawn_live(double width, double scaling)
    {
        using var fromImages = await CardsAsync(live: false);
        using var live = await CardsAsync(live: true);
        AssertSame(fromImages, live, $"cards-cache-{width}-{scaling}");

        // A loaded (paused) card with its record out and a hovered one: all at rest. The art shown at another size than
        // it is laid out at, so a cache at the art's own size would show.
        async Task<SKBitmap> CardsAsync(bool live)
        {
            await using var app = await OpenAsync(live: live, theme: AppTheme.Light, width: width, scaling: scaling);
            await PlayAsync(app, state: PlayerState.Paused);
            Gui.Hover(app.Window, Card(app.Window, 8));
            Gui.FinishAnimations(Settled);
            var cards = Cards(app.Window);
            Assert.All(cards, c => Assert.Equal(!live, Cached(Art(c))));
            Assert.NotEqual((double)app.Window.FindResource("CardArtSize")!, Art(cards[0]).Bounds.Width, 1);
            return Snapshots.Capture(app.Window);
        }
    }

    /// <summary>Drawn from images, the same, near enough, as drawn live.</summary>
    private static void AssertSame(SKBitmap fromImages, SKBitmap live, string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"musicspin-cards-live-{Guid.NewGuid():N}.png");
        try
        {
            Snapshots.Save(live, path);
            var result = Snapshots.Compare(fromImages, path, Path.Combine(Snapshots.FailureDirectory, name), channelTolerance: 8, pixelTolerance: 0.0005);
            Assert.True(result.Matches, result.Problem);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
