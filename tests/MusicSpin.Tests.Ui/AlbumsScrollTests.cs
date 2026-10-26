using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.VisualTree;
using MusicSpin.App.Controls;
using MusicSpin.App.ViewModels;
using MusicSpin.Core.Settings;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// Scrolling the albums page (#120): rows of cards and shelf boards are recycled with their cards and sleeves, so
/// scrolling builds none, logs no binding errors, and every card still acts on its own album. And the compositor keeps
/// separate dirty rectangles (#123).
/// </summary>
public sealed class AlbumsScrollTests
{
    private const int Albums = 200;

    /// <summary>Made when first used, under Avalonia (not while the tests are found, before it is set up).</summary>
    private static readonly Lazy<Avalonia.Media.Imaging.Bitmap> Cover = new(() => TestImages.Bitmap(TestImages.Png(64, 64, (_, _) => SkiaSharp.SKColors.SteelBlue)));

    /// <summary>200 albums, one track each (album i's is track i), in <paramref name="view"/>, grouped by genre (5 groups).</summary>
    private static async Task<AppHarness> OpenAsync(AlbumsView view, AlbumGrouping grouping = AlbumGrouping.Genre)
    {
        var library = new StubLibrary();
        library.Albums.AddRange(Enumerable.Range(0, Albums).Select(StubLibrary.Album));
        library.Tracks.AddRange(Enumerable.Range(0, Albums).Select(i => StubLibrary.Track(i, $"album{i:D4}")));
        var app = await AppHarness.OpenAsync(new AppOptions { Scripted = true, Library = library, Covers = new StubCovers(_ => Cover.Value) });
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == Albums, "the albums");
        var page = app.ViewModel.AlbumsPage;
        page.Grouping = grouping;
        page.ViewChoice = page.Views.Single(v => v.Value == view);
        Gui.Settle();
        return app;
    }

    /// <summary>Scrolls the page to <paramref name="share"/> of the way down.</summary>
    private static void Scroll(AppHarness app, double share)
    {
        var scroller = Gui.Find<ScrollViewer>(app.Window, "Scroller");
        scroller.Offset = new Vector(0, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height) * share);
        Gui.Settle();
    }

    /// <summary>The cards or sleeves in the page now, shown or kept hidden for later.</summary>
    private static List<Control> Items(AppHarness app, AlbumsView view)
        => view == AlbumsView.Cards
            ? [.. Gui.All<Button>(app.Window).Where(b => b.Classes.Contains("card"))]
            : [.. Gui.All<ShelfSleeve>(app.Window)];

    /// <summary>The cards or sleeves showing an album, with the album they show.</summary>
    private static List<(Control Item, AlbumItemViewModel Album)> Shown(AppHarness app, AlbumsView view)
        => [.. Items(app, view).Where(c => c.IsEffectivelyVisible && c.DataContext is AlbumItemViewModel).Select(c => (c, (AlbumItemViewModel)c.DataContext!))];

    /// <summary>
    /// Every binding warning or error while it records, from controls in the window or out of it (a recycled row is out
    /// of it): the test records only while it scrolls, synchronously on the UI thread, so nothing else logs meanwhile.
    /// </summary>
    private sealed class BindingErrors : ILogSink, IDisposable
    {
        private readonly ILogSink? _previous = Logger.Sink;

        public List<string> Lines { get; } = [];

        public BindingErrors Start()
        {
            Logger.Sink = this;
            return this;
        }

        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning && area == LogArea.Binding;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) => Add(source, messageTemplate, []);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
            => Add(source, messageTemplate, propertyValues);

        public void Dispose() => Logger.Sink = _previous;

        private void Add(object? source, string message, object?[] values)
        {
            Lines.Add($"{source?.GetType().Name}: {message} | {string.Join(" | ", values)}");
        }
    }

    public static TheoryData<AlbumsView> Views { get; } = [AlbumsView.Cards, AlbumsView.Shelf];

    [AvaloniaTheory]
    [MemberData(nameof(Views))]
    public async Task Scrolling_down_and_back_builds_no_new_cards_or_sleeves_once_the_page_has_what_it_needs(AlbumsView view)
    {
        await using var app = await OpenAsync(view);
        var seen = new HashSet<Control>(ReferenceEqualityComparer.Instance);

        // Once down: the page builds the rows (and their cards) a screenful needs, headers and rows of each kind.
        for (var step = 0; step <= 20; step++)
        {
            Scroll(app, step / 20.0);
            seen.UnionWith(Items(app, view));
        }

        var built = seen.Count;
        Assert.True(built < Albums / 2, $"{built} {view} built for {Albums} albums: rows aren't recycled");

        // Back up and down again, by small steps and by jumps: every card or sleeve is one of those.
        foreach (var share in Enumerable.Range(0, 21).Select(s => 1 - (s / 20.0)).Concat([0.5, 0.1, 0.9, 0.3, 1, 0]))
        {
            Scroll(app, share);
            var now = Items(app, view);
            Assert.All(now, c => Assert.Contains(c, seen));
            Assert.All(Shown(app, view), s => Assert.Contains(s.Album, app.ViewModel.AlbumsPage.Albums)); // each shows an album of the page
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Views))]
    public async Task Scrolling_throws_and_logs_no_binding_errors(AlbumsView view)
    {
        await using var app = await OpenAsync(view);
        using var errors = new BindingErrors().Start();
        var thrown = new List<string>();
        var ui = Environment.CurrentManagedThreadId;
        void OnThrown(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (Environment.CurrentManagedThreadId == ui)
            {
                thrown.Add($"{e.Exception.GetType().Name}: {e.Exception.Message}");
            }
        }

        AppDomain.CurrentDomain.FirstChanceException += OnThrown;
        try
        {
            foreach (var share in Enumerable.Range(0, 21).Select(s => s / 20.0).Concat(Enumerable.Range(0, 21).Select(s => 1 - (s / 20.0))))
            {
                Scroll(app, share);
            }
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= OnThrown;
        }

        Assert.Empty(errors.Lines);
        Assert.Empty(thrown.Distinct()); // caught or not: thrown on the scrolling path, they cost (BindingChainException)
    }

    [AvaloniaTheory]
    [MemberData(nameof(Views))]
    public async Task After_scrolling_each_card_or_sleeve_plays_its_own_album(AlbumsView view)
    {
        await using var app = await OpenAsync(view);
        Scroll(app, 1);
        Scroll(app, 0.5); // the rows here are recycled ones

        var scroller = Gui.BoundsIn(app.Window, Gui.Find<ScrollViewer>(app.Window, "Scroller"));
        var (item, album) = Shown(app, view).Last(s => scroller.Contains(Gui.BoundsIn(app.Window, s.Item))); // wholly on screen
        Assert.Equal(album.Title, view == AlbumsView.Cards
            ? item.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("cardTitle")).Text
            : ((ShelfSleeve)item).Title); // it shows the album it plays
        Assert.Same(album, view == AlbumsView.Cards ? ((Button)item).CommandParameter : ((ShelfSleeve)item).CommandParameter);
        Gui.Click(app.Window, item);

        await Gui.UntilAsync(() => app.Scripted!.Count("queue.set") == 1, "the album sent");
        var sent = app.Scripted!.Sent("queue.set")[0].GetProperty("items")[0].GetProperty("trackId").GetString();
        Assert.Equal($"track{int.Parse(album.Album.Id["album".Length..], System.Globalization.CultureInfo.InvariantCulture):D4}", sent);
    }

    [AvaloniaFact]
    public async Task Tilt_from_Settings_reaches_the_sleeves_of_recycled_boards()
    {
        await using var app = await OpenAsync(AlbumsView.Shelf);
        Scroll(app, 1);
        Scroll(app, 0.4);

        app.ViewModel.Settings.SetShelfTilt(60);
        Gui.Settle();
        Assert.All(Gui.All<ShelfSleeve>(app.Window).Where(s => s.IsEffectivelyVisible), s => Assert.Equal(60, s.Tilt));
    }

    // ---- a row on its own --------------------------------------------------------------------------------------

    private static (Window Window, SlotRow Row) Row(params string[] items)
    {
        var row = new SlotRow
        {
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((_, _) => new TextBlock { [!TextBlock.TextProperty] = new Avalonia.Data.Binding() }),
        };
        var window = new Window { Width = 400, Height = 100, Content = row };
        window.Show();
        row.Items = items;
        return (window, row);
    }

    private static List<string?> Texts(SlotRow row) => [.. row.Shown.Cast<TextBlock>().Select(t => t.Text)];

    [AvaloniaFact]
    public void A_shorter_row_hides_the_controls_it_has_no_item_for_lets_go_of_their_items_and_uses_them_again_later()
    {
        var (window, row) = Row("a", "b", "c", "d");
        Assert.Equal(4, row.Built);
        Assert.Equal(["a", "b", "c", "d"], Texts(row));

        row.Items = new[] { "e", "f" };
        Assert.Equal(["e", "f"], Texts(row));
        var hidden = Gui.All<TextBlock>(row).Where(t => !t.IsVisible).ToList();
        Assert.Equal(2, hidden.Count);
        Assert.All(hidden, t => Assert.Null(t.DataContext)); // the albums they showed are let go (their covers too)

        row.Items = new[] { "g", "h", "i", "j", "k" };
        Assert.Equal(["g", "h", "i", "j", "k"], Texts(row));
        Assert.Equal(5, row.Built); // one more; the four were used again

        row.Items = null;
        Assert.Empty(Texts(row));
        window.Close();
    }

    [AvaloniaFact]
    public void A_new_template_builds_the_row_again_and_a_row_without_one_shows_nothing()
    {
        var (window, row) = Row("a", "b");
        var first = row.Shown.ToList();

        row.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((_, _) => new Border());
        Assert.Equal(2, row.Shown.Count());
        Assert.All(row.Shown, c => Assert.IsType<Border>(c));
        Assert.DoesNotContain(row.Shown, first.Contains);

        row.ItemTemplate = null;
        row.Items = new[] { "c" };
        Assert.Empty(row.Shown);
        window.Close();
    }

    [AvaloniaFact]
    public void A_row_built_before_it_is_in_a_window_builds_its_items_once_it_is()
    {
        var row = new SlotRow { ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((_, _) => new TextBlock()), Items = new[] { "a", "b" } };
        Assert.Equal(0, row.Built); // not yet: built outside the window, ancestor bindings ($parent[...]) wouldn't find theirs

        var window = new Window { Content = row };
        window.Show();
        Assert.Equal(2, row.Built);
        Assert.Equal(["a", "b"], row.Shown.Select(c => c.DataContext));
        window.Close();
    }

    // ---- dirty rectangles (#123) --------------------------------------------------------------------------------

    [Fact]
    public void The_compositor_keeps_separate_dirty_rectangles_not_their_bounding_box()
    {
        var options = MusicSpin.App.Program.Composition();
        Assert.True(options.UseRegionDirtyRectClipping);
        Assert.Null(options.MaxDirtyRects); // Avalonia's 8, enough for the record, the player bar's and a card's
    }
}
