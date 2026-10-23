using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MusicSpin.App.Controls;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;
using MusicSpin.App.Views.Pages;
using MusicSpin.Core.Library;
using MusicSpin.Core.Playback;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>Covers and records in the views (#8).</summary>
public sealed class ArtworkTests
{
    [AvaloniaFact]
    public async Task The_discs_of_the_playing_album_turn_together()
    {
        await using var app = await AppHarness.OpenAsync(new AppOptions
        {
            Music = music =>
            {
                TestImages.Album(music, "Quadrants", "Painter", cover: TestImages.Cover());
                TestImages.Album(music, "Other", "Someone");
            },
        });
        var window = app.Window;
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 2, "the albums");

        var card = AlbumCard(window, "Quadrants");
        Gui.Click(window, card);
        await app.PlaySceneAsync();
        await Gui.UntilAsync(() => app.ViewModel.Player.State == PlayerState.Playing && app.ViewModel.PlayingAlbumId is not null, "the album playing");
        app.Advance(TimeSpan.FromSeconds(1.5));

        var cardDisc = card.GetVisualDescendants().OfType<VinylView>().Single();
        var otherDisc = AlbumCard(window, "Other").GetVisualDescendants().OfType<VinylView>().Single();
        var miniDisc = Gui.Find<VinylView>(window, "MiniVinyl");
        Assert.True(miniDisc.RenderedAngle > 10, $"angle {miniDisc.RenderedAngle}");
        Assert.Equal(miniDisc.RenderedAngle, cardDisc.RenderedAngle);
        Assert.Equal(app.ViewModel.SpinClock.Angle, miniDisc.RenderedAngle);
        Assert.Equal(0, otherDisc.RenderedAngle);
        Assert.Contains("playing", card.Classes);
        Assert.NotNull(Gui.Find<CoverView>(window, "MiniCover").Source);

        // The playing records slide out of their sleeves: 22 px in the player bar, 24% on a card (48 of the card art's 200).
        Gui.FinishAnimations();
        Assert.Equal((22.0, 48.0, 0.0), (OffsetX(miniDisc), OffsetX(cardDisc), OffsetX(otherDisc)));
    }

    [AvaloniaFact]
    public async Task An_album_without_art_shows_its_title_and_artist()
    {
        await using var app = await AppHarness.OpenAsync(new AppOptions { Music = music => TestImages.Album(music, "Plain Sleeve", "Nobody Known") });
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 1, "the album");
        var album = app.ViewModel.AlbumsPage.Albums.Single();
        _ = album.Art.Cover; // shown, so the card has asked for its cover (reading it starts that once) …
        Assert.Null(await app.Services.Covers.CoverAsync(album.Album.Id, CoverSize.Medium)); // … the same read: it found nothing
        Gui.Settle(); // the card took the answer

        var cover = AlbumCard(app.Window, "Plain Sleeve").GetVisualDescendants().OfType<CoverView>().Single();
        Assert.False(cover.HasArt);
        var texts = cover.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Equal(["PLAIN SLEEVE", "NOBODY KNOWN"], texts);
    }

    [AvaloniaFact]
    public async Task A_page_of_500_albums_realizes_only_the_visible_cards()
    {
        var library = new StubLibrary();
        library.Albums.AddRange(Enumerable.Range(0, 500).Select(StubLibrary.Album));
        var covers = new StubCovers();
        await using var app = await AppHarness.OpenAsync(new AppOptions { Library = library, Covers = covers });
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 500, "the albums");

        var cards = Gui.All<CoverView>(Gui.All<AlbumsPage>(app.Window).Single()).Count();

        Assert.InRange(cards, 10, 40);
        Assert.InRange(covers.Requests.Count, 10, 40);
    }

    [AvaloniaFact]
    public async Task Scrolling_away_and_back_reuses_the_loaded_covers()
    {
        var library = new StubLibrary();
        library.Albums.AddRange(Enumerable.Range(0, 500).Select(StubLibrary.Album));
        var cover = TestImages.Bitmap(TestImages.Cover(64));
        var covers = new StubCovers(_ => cover);
        await using var app = await AppHarness.OpenAsync(new AppOptions { Library = library, Covers = covers });
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 500, "the albums");
        var scroller = Gui.Find<ScrollViewer>(app.Window, "Scroller");
        var firstShown = covers.Requests.Keys.ToHashSet();
        var rows = app.ViewModel.AlbumsPage.Rows.OfType<AlbumCardRowViewModel>().ToList();
        var (first, last) = (rows[0].Albums[0], rows[^1].Albums[^1]);

        scroller.Offset = new Vector(0, scroller.Extent.Height);
        Gui.Settle();
        scroller.Offset = default;
        Gui.Settle();

        // Scrolled away, a card lets go of its cover (#134) and asks again when back: the cover service still has it.
        Assert.Contains(first.Album.Id, firstShown);
        Assert.All(firstShown, id => Assert.InRange(covers.Requests[id], 1, 2));
        Assert.Contains(last.Album.Id, covers.Requests.Keys);
        Assert.Same(cover, AlbumCard(app.Window, first.Title).GetVisualDescendants().OfType<CoverView>().Single().Source);
    }

    [AvaloniaTheory]
    [InlineData("dark")]
    [InlineData("light")]
    public void Snapshot_of_a_cover_a_placeholder_and_a_record(string theme)
    {
        var art = TestImages.Bitmap(TestImages.Cover());
        var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 24, Margin = new Thickness(24) };
        panel.Children.Add(new CoverView { Width = 160, Height = 160, Source = art, Title = "Quadrants", Artist = "Painter" });
        panel.Children.Add(new CoverView { Width = 160, Height = 160, Title = "Plain Sleeve", Artist = "Nobody Known" });
        panel.Children.Add(new VinylView { Width = 160, Height = 160, Label = art, Angle = 30 });
        panel.Children.Add(new VinylView { Width = 160, Height = 160, Angle = 30 });
        var window = new Window
        {
            Width = 4 * 160 + 3 * 24 + 48,
            Height = 160 + 48,
            Content = panel,
            RequestedThemeVariant = theme == "light" ? ThemeVariant.Light : ThemeVariant.Dark,
        };
        window.Bind(Window.BackgroundProperty, window.GetResourceObservable("BgBrush"));
        window.Show();
        try
        {
            Snapshots.Match(window, $"artwork-{theme}");
        }
        finally
        {
            window.Close();
        }
    }

    private static double OffsetX(Visual visual) => (visual.RenderTransform as Avalonia.Media.Transformation.TransformOperations)?.Value.M31 ?? 0;

    private static Button AlbumCard(Window window, string title)
        => Gui.All<Button>(window).Single(b => b.Classes.Contains("card") && b.DataContext is AlbumItemViewModel a && a.Title == title);
}
