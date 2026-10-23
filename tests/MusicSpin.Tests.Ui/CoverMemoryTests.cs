using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MusicSpin.App.Controls;
using MusicSpin.App.Services;
using MusicSpin.App.ViewModels;
using MusicSpin.App.Views.Pages;
using MusicSpin.Core.Library;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Settings;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// What covers hold of memory (#134): a cover is held (<see cref="CoverLease"/>) while it shows and let go after, so the
/// cover service's bound is real; covers are asked for at the pixels they show at; and Now Playing holds one large cover
/// and one small however many albums it shows. The service's own side is in <see cref="CoverServiceCacheTests"/>.
/// </summary>
public sealed class CoverMemoryTests
{
    private const string AlbumId = "album0001";

    private static readonly Bitmap Cover = TestImages.Bitmap(TestImages.Cover(64));

    private static readonly PropertyChangedEventHandler Shown = (_, _) => { };

    // ---- an album's art ------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public void An_albums_art_holds_its_cover_while_watched_and_lets_go_after()
    {
        var covers = new StubCovers(_ => Cover);
        var art = new AlbumArt(AlbumId, CoverSize.Medium, covers);
        art.PropertyChanged += Shown;
        Assert.Same(Cover, art.Cover); // one the service has comes at once
        Assert.Equal(1, covers.Held);

        art.PropertyChanged -= Shown; // scrolled away
        Assert.Equal(0, covers.Held);
        Assert.False(art.IsHolding);

        art.PropertyChanged += Shown; // and back
        Assert.Same(Cover, art.Cover);
        Assert.Equal((1, 2), (covers.Held, covers.Requests[AlbumId])); // asked for again: the service kept it
    }

    [AvaloniaFact]
    public void Two_watchers_of_one_art_hold_its_cover_once_till_both_go()
    {
        var covers = new StubCovers(_ => Cover);
        var art = new AlbumArt(AlbumId, CoverSize.Medium, covers);
        PropertyChangedEventHandler record = (_, _) => { };
        art.PropertyChanged += Shown; // a card's sleeve …
        art.PropertyChanged += record; // … and its record
        _ = art.Cover;

        art.PropertyChanged -= record;
        Assert.Equal(1, covers.Held);
        art.PropertyChanged -= Shown;
        Assert.Equal(0, covers.Held);
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public void Removing_a_handler_never_added_doesnt_let_go_of_the_cover()
    {
        var covers = new StubCovers(_ => Cover);
        var art = new AlbumArt(AlbumId, CoverSize.Medium, covers);
        art.PropertyChanged += Shown;
        _ = art.Cover;

        art.PropertyChanged -= (_, _) => { };
        art.PropertyChanged -= null;
        Assert.Equal(1, covers.Held);
        Assert.Same(Cover, art.Cover);

        art.PropertyChanged -= Shown;
        art.PropertyChanged -= Shown; // twice: still nothing to let go
        Assert.Equal(0, covers.Held);
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public void A_cover_coming_after_its_art_was_let_go_is_let_go_too()
    {
        var covers = new StubCovers(_ => Cover);
        var gate = covers.Gates[AlbumId] = new TaskCompletionSource<Bitmap?>();
        var art = new AlbumArt(AlbumId, CoverSize.Medium, covers);
        art.PropertyChanged += Shown;
        Assert.Null(art.Cover); // on its way

        art.PropertyChanged -= Shown; // scrolled past before it came
        gate.SetResult(Cover);
        Gui.Settle();

        Assert.Equal(0, covers.Held);
        Assert.False(art.IsHolding);
    }

    [AvaloniaFact]
    public void A_shown_cover_is_asked_for_again_at_more_pixels_and_the_smaller_one_let_go()
    {
        var covers = new StubCovers(_ => Cover);
        var art = new AlbumArt(AlbumId, CoverSize.Medium, covers) { Pixels = 200 };
        art.PropertyChanged += Shown;
        _ = art.Cover;
        Assert.Equal(200, covers.Asked.Last().Pixels);

        art.Pixels = 250; // the same step (256): nothing to decode again
        Assert.Single(covers.Asked);
        art.Pixels = 400; // a bigger screen's (512)
        Assert.Equal(400, covers.Asked.Last().Pixels);
        Assert.Equal(1, covers.Held); // the new one; the smaller let go

        var unshown = new AlbumArt("album0002", CoverSize.Medium, covers) { Pixels = 200 };
        unshown.Pixels = 800; // never asked for: nothing to ask again
        Assert.DoesNotContain(covers.Asked, a => a.AlbumId == "album0002");
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public void What_an_art_has_loaded_is_read_without_asking_for_or_holding_its_cover()
    {
        var covers = new StubCovers(_ => Cover);
        var art = new AlbumArt(AlbumId, CoverSize.Medium, covers);

        Assert.Null(art.Loaded); // never shown: a ghost of it shows the placeholder
        Assert.Empty(covers.Asked);
        Assert.Equal(0, covers.Held);

        art.PropertyChanged += Shown;
        _ = art.Cover;
        Assert.Same(Cover, art.Loaded); // shown: the ghost copies it
        art.PropertyChanged -= Shown;
        Assert.Null(art.Loaded);
        Assert.Equal(0, covers.Held);
    }

    // ---- the pages -----------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Scrolling_through_500_albums_holds_only_the_covers_of_the_cards_there()
    {
        var library = new StubLibrary();
        library.Albums.AddRange(Enumerable.Range(0, 500).Select(StubLibrary.Album));
        var covers = new StubCovers(_ => Cover);
        await using var app = await AppHarness.OpenAsync(new AppOptions { Library = library, Covers = covers });
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 500, "the albums");
        var page = Gui.All<AlbumsPage>(app.Window).Single();
        var scroller = Gui.Find<ScrollViewer>(app.Window, "Scroller");
        AssertHeldAreTheCards(page, covers);

        for (var step = 1; step <= 8; step++)
        {
            scroller.Offset = new Vector(0, scroller.Extent.Height * step / 8);
            Gui.Settle();
            AssertHeldAreTheCards(page, covers);
        }

        Assert.True(covers.Requests.Count > 100, $"{covers.Requests.Count} albums shown on the way");
    }

    [AvaloniaFact]
    public async Task Switching_views_lets_go_of_the_covers_the_last_one_showed()
    {
        var covers = SampleLibrary.Covers();
        await using var app = await AppHarness.OpenAsync(new AppOptions { Scripted = true, Library = SampleLibrary.Create(), Covers = covers });
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 10, "the albums");
        var page = Gui.All<AlbumsPage>(app.Window).Single();

        foreach (var view in new[] { AlbumsView.Shelf, AlbumsView.Crate, AlbumsView.Cards })
        {
            app.ViewModel.AlbumsPage.ViewChoice = app.ViewModel.AlbumsPage.Views.Single(v => v.Value == view);
            Gui.Settle();
            var shown = page.GetVisualDescendants().Select(v => v.DataContext switch
                {
                    AlbumItemViewModel a => a.Album.Id,
                    CrateRecordViewModel r => r.Album.Album.Id,
                    _ => null,
                })
                .OfType<string>().Where(id => id is not ("a2" or "a5" or "a10")).Distinct().Order().ToList(); // those three have none
            Assert.NotEmpty(shown);
            Assert.Equal(shown, covers.Leases.Values.Select(l => l.AlbumId).Order()); // one each, the new view's: the last view's let go
            Assert.All(covers.Leases.Values, l => Assert.Equal(CoverSize.Medium, l.Size));
        }

        app.ViewModel.Sidebar.Navigate(app.ViewModel.Sidebar.Tracks);
        await Gui.UntilAsync(() => app.ViewModel.CurrentPage == app.ViewModel.TracksPage, "the tracks page");
        Gui.Settle();
        Assert.DoesNotContain(covers.Leases.Values, l => l.Size == CoverSize.Medium); // the albums page gone: none of its covers held
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public async Task Covers_are_asked_for_at_no_fewer_pixels_than_any_view_shows_them_at(double scaling)
    {
        var covers = SampleLibrary.Covers();
        await using var app = await AppHarness.OpenAsync(new AppOptions { Scripted = true, Library = SampleLibrary.Create(), Covers = covers });
        app.Window.SetRenderScaling(scaling);
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 10, "the albums");
        var page = Gui.All<AlbumsPage>(app.Window).Single();

        foreach (var view in new[] { AlbumsView.Cards, AlbumsView.Shelf, AlbumsView.Crate })
        {
            app.ViewModel.AlbumsPage.ViewChoice = app.ViewModel.AlbumsPage.Views.Single(v => v.Value == view);
            Gui.FinishAnimations();
            var widest = CoverWidths(page, app.Window).Max();
            Assert.True(widest * scaling <= app.ViewModel.AlbumsPage.CoverPixels,
                $"{view}: a cover shows {widest * scaling:F1} pixels across, asked for {app.ViewModel.AlbumsPage.CoverPixels}");
        }

        Assert.All(covers.Asked.Where(a => a.Size == CoverSize.Medium), a => Assert.InRange(a.Pixels, 1, app.ViewModel.AlbumsPage.CoverPixels));
        Assert.InRange(app.ViewModel.AlbumsPage.CoverPixels, 300 * scaling, 400 * scaling); // not the 720 cached
    }

    // ---- Now Playing ---------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Now_playing_20_albums_in_turn_holds_one_large_cover_and_one_small()
    {
        var covers = SampleLibrary.Covers(accents: true);
        await using var app = await OpenAsync(covers);
        app.ViewModel.NowPlaying.Open();

        foreach (var album in Enumerable.Range(0, 20).Select(i => new[] { 1, 3, 4, 6, 7, 8, 9 }[i % 7]))
        {
            await PlayAsync(app, album);
            await Gui.UntilAsync(() => app.ViewModel.NowPlaying.Cover is not null && app.ViewModel.NowPlaying.BackdropCover is not null, "its covers");
            Gui.Settle();
            var large = covers.Leases.Values.Where(l => l.Size == CoverSize.Large).ToList();
            Assert.Equal([(SampleLibrary.AlbumId(album), CoverSize.Large)], large);
            Assert.Equal(2, covers.Leases.Values.Count(l => l.Size == CoverSize.Small)); // the background's and the player bar's
            Assert.All(covers.Leases.Values.Where(l => l.Size == CoverSize.Small), l => Assert.Equal(SampleLibrary.AlbumId(album), l.AlbumId));
        }
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task An_earlier_albums_covers_coming_late_are_let_go_not_shown()
    {
        var covers = SampleLibrary.Covers(accents: true);
        var slow = covers.Gates[SampleLibrary.AlbumId(6)] = new TaskCompletionSource<Bitmap?>();
        await using var app = await OpenAsync(covers);
        app.ViewModel.NowPlaying.Open();

        app.Scripted!.Status = ScriptedPlayer.Playing(SampleLibrary.Albums[5].Tracks[0], trackId: SampleLibrary.TrackId(6, 1));
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => app.ViewModel.PlayingAlbumId == SampleLibrary.AlbumId(6), "the first album");
        await PlayAsync(app, 7); // the next, while the first's covers are on their way
        await Gui.UntilAsync(() => app.ViewModel.NowPlaying.Cover is not null, "the second album's cover");

        slow.SetResult(TestImages.Bitmap(TestImages.Cover(64))); // all of the first album's covers come now
        Gui.Settle();

        Assert.DoesNotContain(covers.Leases.Values, l => l.AlbumId == SampleLibrary.AlbumId(6));
        Assert.Equal("Velvet Hours", app.ViewModel.NowPlaying.Title); // the second album's, its cover shown
        Assert.Contains(covers.Leases.Values, l => l == (SampleLibrary.AlbumId(7), CoverSize.Large));
    }

    [AvaloniaFact]
    public async Task The_large_cover_is_asked_for_at_the_pixels_across_the_platter_and_again_when_it_grows()
    {
        var covers = SampleLibrary.Covers(accents: true);
        await using var app = await OpenAsync(covers);
        app.ViewModel.NowPlaying.Open();
        await PlayAsync(app, 6);
        var deck = Gui.Find<DeckHost>(app.Window, "Deck");
        var platter = Math.Min(app.Window.ClientSize.Height * deck.HeightShare, app.Window.ClientSize.Width * deck.WidthShare);
        Assert.Equal((int)Math.Ceiling(platter), app.ViewModel.NowPlaying.CoverPixels);
        Assert.Equal(app.ViewModel.NowPlaying.CoverPixels, covers.Asked.Last(a => a.Size == CoverSize.Large).Pixels);
        var first = app.ViewModel.NowPlaying.Cover;

        (app.Window.Width, app.Window.Height) = (2560, 1440); // a step bigger
        Gui.Settle();
        await Gui.UntilAsync(() => covers.Asked.Last(a => a.Size == CoverSize.Large).Pixels > (int)Math.Ceiling(platter), "the cover asked for again");
        Assert.Equal(app.ViewModel.NowPlaying.CoverPixels, covers.Asked.Last(a => a.Size == CoverSize.Large).Pixels);
        Assert.Single(covers.Leases.Values, l => l.Size == CoverSize.Large); // the old one let go
        Assert.NotNull(first);

        var asked = covers.Asked.Count(a => a.Size == CoverSize.Large);
        (app.Window.Width, app.Window.Height) = (1100, 720); // smaller: what it has will do
        Gui.Settle();
        Assert.Equal(asked, covers.Asked.Count(a => a.Size == CoverSize.Large));
    }

    [AvaloniaFact]
    public async Task The_turntable_scene_lets_go_of_its_cover_once_the_record_lands()
    {
        var covers = SampleLibrary.Covers(accents: true);
        await using var app = await OpenAsync(covers);
        var card = Gui.All<Button>(app.Window).Single(b => b.Classes.Contains("card") && b.DataContext is AlbumItemViewModel a && a.Album.Id == SampleLibrary.AlbumId(1));

        Gui.Click(app.Window, card);
        await Gui.UntilAsync(() => app.ViewModel.Scene.IsRunning, "the scene");
        Assert.Contains(covers.Leases.Values, l => l == (SampleLibrary.AlbumId(1), CoverSize.Large));
        await app.PlaySceneAsync();
        Gui.Settle();

        Assert.False(app.ViewModel.Scene.IsRunning);
        Assert.Null(app.ViewModel.Scene.Cover);
        Assert.Single(covers.Leases.Values, l => l.Size == CoverSize.Large); // Now Playing's, the deck's label
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static async Task<AppHarness> OpenAsync(StubCovers covers)
    {
        var app = await AppHarness.OpenAsync(new AppOptions
        {
            Scripted = true,
            Library = SampleLibrary.Create(),
            Covers = covers,
            Settings = new AppSettings { TurntableDesign = "classic" },
        });
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 10, "the albums");
        return app;
    }

    private static async Task PlayAsync(AppHarness app, int album)
    {
        var a = SampleLibrary.Albums[album - 1];
        app.Scripted!.Status = ScriptedPlayer.Playing(a.Tracks[0], trackId: SampleLibrary.TrackId(album, 1));
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => app.ViewModel.PlayingAlbumId == SampleLibrary.AlbumId(album) && app.ViewModel.NowPlaying.Tracks.Count == a.Tracks.Length,
            "the album loaded");
        Gui.Settle();
    }

    /// <summary>The covers held are those of the cards there (on screen, or just past it), one each.</summary>
    private static void AssertHeldAreTheCards(AlbumsPage page, StubCovers covers)
    {
        var cards = page.GetVisualDescendants().OfType<CoverView>().Select(c => c.DataContext).OfType<AlbumItemViewModel>()
            .Select(a => a.Album.Id).Distinct().Order().ToList();
        Assert.InRange(cards.Count, 10, 60);
        Assert.Equal(cards, covers.Leases.Values.Select(l => l.AlbumId).Order());
    }

    /// <summary>How wide each cover on the page shows, in the window's units: its sleeve as laid out and transformed.</summary>
    private static IEnumerable<double> CoverWidths(AlbumsPage page, Window window)
    {
        var sleeves = page.GetVisualDescendants().OfType<Control>()
            .Where(c => c.IsEffectivelyVisible && (c is CoverView || c.Name == "PART_Record" || c is ShelfSleeve));
        foreach (var sleeve in sleeves)
        {
            if (sleeve.TransformToVisual(window) is { } m)
            {
                yield return new Rect(sleeve.Bounds.Size).TransformToAABB(m).Width;
            }
        }
    }
}
