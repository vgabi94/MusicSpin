using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MusicSpin.App.Controls;
using MusicSpin.App.Services;
using MusicSpin.App.Theming;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Settings;
using MusicSpin.Tests.Ui.Harness;
using SkiaSharp;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// The turntable designs from the deck studies (#25–#30): the arm in each state and where its stylus lands, the lights
/// and the accent, the fonts, and each design in Now Playing and Settings.
/// </summary>
public sealed class DeckDesignTests
{
    /// <summary>Night Transit: the mockup's album in Now Playing.</summary>
    private const int Album = 6;

    /// <summary>The slowest transition: the arm swinging over (0.85 s).</summary>
    private const int Settled = 900;

    /// <summary>
    /// The lead-in: the outermost grooves, as a share of the record's radius (the studies' styli land at 0.95; the last 2%
    /// is the record's edge).
    /// </summary>
    private const double LeadInFrom = 0.93;
    private const double LeadInTo = 0.97;

    /// <summary>Each design's theme, and how its arm moves: (rest, play) angles, or a carriage's travel.</summary>
    private static readonly Dictionary<string, (string Theme, double RestAngle, double PlayAngle, double Travel)> Kit = new()
    {
        ["walnut"] = ("WalnutDeck", 0, 24.5, 0),
        ["aluminium"] = ("AluminiumDeck", 0, 25, 0),
        ["carbon"] = ("CarbonDeck", 0, 25, 0),
        ["piano"] = ("PianoDeck", 0, 25, 0),
        ["linear"] = ("LinearDeck", 0, 0, 105),
        ["acrylic"] = ("AcrylicDeck", 0, 25, 0),
    };

    /// <summary>The font each design's engraving (and readout) is drawn in.</summary>
    private static readonly Dictionary<string, string> Font = new()
    {
        ["walnut"] = "Cormorant Garamond",
        ["aluminium"] = "Inter",
        ["carbon"] = "Barlow Semi Condensed",
        ["piano"] = "Inter",
        ["linear"] = "Inter",
        ["acrylic"] = "Inter",
    };

    /// <summary>Each design's lights: the part, its opacity off and on, and what turns it on.</summary>
    private static readonly Dictionary<string, (string Part, double Off, double On, Func<ArmState, bool, bool> Lit)[]> Lights = new()
    {
        ["walnut"] = [],
        ["aluminium"] = [("Lamp", 0, 1, (_, spinning) => spinning)],
        ["carbon"] =
        [
            ("EdgeLight", 0.12, 0.5, (_, spinning) => spinning),
            ("Readout", 0, 1, (_, spinning) => spinning),
            ("Ticks", 0.35, 1, (_, spinning) => spinning),
            ("Signal", 0.12, 1, (arm, _) => arm == ArmState.Lowered),
        ],
        ["piano"] = [],
        ["linear"] = [("Track", 0, 0.9, (arm, _) => arm == ArmState.Lowered)],
        ["acrylic"] = [("EdgeLight", 0.15, 0.9, (_, spinning) => spinning)],
    };

    /// <summary>The parts that take the album colour (lit or not).</summary>
    private static readonly Dictionary<string, string[]> AccentParts = new()
    {
        ["walnut"] = [],
        ["aluminium"] = ["Lamp", "WeightRing"],
        ["carbon"] = ["EdgeLight", "Readout", "Ticks", "Signal"],
        ["piano"] = [],
        ["linear"] = ["Track"],
        ["acrylic"] = ["EdgeLight"],
    };

    public static TheoryData<string> Designs { get; } = [.. Kit.Keys];

    // ---- a deck on its own -------------------------------------------------------------------------------------

    /// <summary>The design's deck in a window of its own, parked, in the accent <paramref name="accent"/>.</summary>
    private static (Window Window, KitDeckView Deck) Show(string design, string accent = "#E86A3C")
    {
        var deck = new KitDeckView
        {
            Theme = (ControlTheme)Application.Current!.FindResource(Kit[design].Theme)!,
            Accent = new SolidColorBrush(Color.Parse(accent)),
        };
        var window = new Window { Width = 700, Height = 600, Content = deck };
        window.Show();
        Gui.FinishAnimations(Settled);
        return (window, deck);
    }

    private static void Set(KitDeckView deck, ArmState arm, bool spinning)
    {
        deck.Arm = arm;
        deck.IsSpinning = spinning;
        Gui.FinishAnimations(Settled);
    }

    private static Control Part(KitDeckView deck, string name)
        => deck.GetVisualDescendants().OfType<Control>().Single(c => c.Name == name);

    /// <summary>The deck's drawing surface, in deck units (1320 × 1080).</summary>
    private static Canvas Units(KitDeckView deck)
        => deck.GetVisualDescendants().OfType<Canvas>().First(c => c.Classes.Contains("units"));

    /// <summary>The arm transform's angle (degrees clockwise), scale and move across.</summary>
    private static (double Angle, double Scale, double Across) Pose(KitDeckView deck)
    {
        var m = Assert.IsType<MatrixTransform>(deck.ArmPart!.RenderTransform).Matrix;
        return (Math.Round(Math.Atan2(m.M12, m.M11) * 180 / Math.PI, 3), Math.Round(Math.Sqrt((m.M11 * m.M11) + (m.M12 * m.M12)), 4), Math.Round(m.M31, 3));
    }

    /// <summary>Where the stylus is, as a share of the record's radius from its centre.</summary>
    private static double StylusOnRecord(KitDeckView deck)
    {
        var record = deck.Record!;
        var at = deck.Stylus!.TranslatePoint(default, record)!.Value;
        var radius = record.Bounds.Width / 2;
        return Point.Distance(at, new Point(radius, radius)) / radius;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Designs))]
    public void The_arm_is_parked_over_the_lead_in_and_lowered_at_the_designs_angles(string design)
    {
        var (window, deck) = Show(design);
        var (_, rest, play, travel) = Kit[design];

        Assert.Equal((0.0, 0.0), (deck.Swing, deck.Drop));
        Assert.Equal(travel == 0 ? (rest, KitDeckView.LiftedScale, 0.0) : (0.0, KitDeckView.LiftedScale, 0.0), Pose(deck));

        Set(deck, ArmState.OverLeadIn, spinning: true);
        Assert.Equal((1.0, 0.0), (deck.Swing, deck.Drop));
        Assert.Equal(travel == 0 ? (play, KitDeckView.LiftedScale, 0.0) : (0.0, KitDeckView.LiftedScale, -travel), Pose(deck));

        Set(deck, ArmState.Lowered, spinning: true);
        Assert.Equal((1.0, 1.0), (deck.Swing, deck.Drop));
        Assert.Equal(travel == 0 ? (play, 1.0, 0.0) : (0.0, 1.0, -travel), Pose(deck));

        // A pause lifts it where it is; a stop takes it home.
        Set(deck, ArmState.OverLeadIn, spinning: false);
        Assert.Equal((1.0, 0.0), (deck.Swing, deck.Drop));
        Set(deck, ArmState.Parked, spinning: false);
        Assert.Equal((0.0, 0.0), (deck.Swing, deck.Drop));
        window.Close();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Designs))]
    public void Lowered_the_stylus_lands_in_the_lead_in_groove_and_parked_it_is_off_the_record(string design)
    {
        var (window, deck) = Show(design);
        Assert.True(StylusOnRecord(deck) > 1, $"parked, the stylus is {StylusOnRecord(deck):0.000} of the radius out");

        Set(deck, ArmState.Lowered, spinning: true);

        Assert.InRange(StylusOnRecord(deck), LeadInFrom, LeadInTo);
        window.Close();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Designs))]
    public void The_lights_are_on_only_in_the_states_the_design_says(string design)
    {
        var (window, deck) = Show(design);
        (ArmState Arm, bool Spinning)[] states =
        [
            (ArmState.Parked, false),
            (ArmState.Parked, true), // the scene: the platter spins up before the arm swings over
            (ArmState.OverLeadIn, true),
            (ArmState.Lowered, true),
            (ArmState.OverLeadIn, false), // paused
        ];
        foreach (var (arm, spinning) in states)
        {
            Set(deck, arm, spinning);
            foreach (var (part, off, on, lit) in Lights[design])
            {
                Assert.Equal((arm, spinning, part, lit(arm, spinning) ? on : off), (arm, spinning, part, Math.Round(Part(deck, part).Opacity, 3)));
            }
        }

        window.Close();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Designs))]
    public void The_parts_that_take_the_album_colour_change_with_it(string design)
    {
        var (window, deck) = Show(design, "#E86A3C");
        Set(deck, ArmState.Lowered, spinning: true);
        Assert.All(AccentParts[design], p => Assert.Equal(Color.Parse("#E86A3C"), Accent(Part(deck, p))));

        deck.Accent = new SolidColorBrush(Color.Parse("#3CA0E8"));
        Gui.Settle();

        Assert.All(AccentParts[design], p => Assert.Equal(Color.Parse("#3CA0E8"), Accent(Part(deck, p))));
        window.Close();

        static Color Accent(Control part) => Assert.IsAssignableFrom<ISolidColorBrush>(part switch
        {
            Shape shape when shape.Stroke is not null => shape.Stroke,
            Shape shape => shape.Fill,
            DeckText text => text.Foreground,
            TickRing ring => ring.Fill,
            Panel panel => panel.Children.OfType<Shape>().Select(s => s.Stroke ?? s.Fill).FirstOrDefault()
                           ?? panel.Children.OfType<Border>().First().Background,
            _ => null,
        }).Color;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Designs))]
    public void The_engraving_is_drawn_in_the_designs_bundled_font_not_a_fallback(string design)
    {
        var (window, deck) = Show(design);
        var texts = deck.GetVisualDescendants().OfType<DeckText>().ToList();
        Assert.NotEmpty(texts);

        foreach (var text in texts)
        {
            Assert.True(FontManager.Current.TryGetGlyphTypeface(text.Typeface, out var face), $"{text.Text}: no face for {text.FontFamily}");
            // A weight's own file names itself "Inter Medium", "Barlow Semi Condensed SemiBold": the family, then the weight.
            Assert.StartsWith(Font[design], face.FamilyName, StringComparison.Ordinal);
            Assert.Equal((text.Text, text.FontWeight), (text.Text, face.Weight)); // the weight's own file, not a synthetic one
        }

        window.Close();
    }

    [Theory]
    [InlineData("walnut", typeof(WalnutDeck))]
    [InlineData(" Aluminium ", typeof(AluminiumDeck))]
    [InlineData("carbon", typeof(CarbonDeck))]
    [InlineData("piano", typeof(PianoDeck))]
    [InlineData("LINEAR", typeof(LinearDeck))]
    [InlineData("acrylic", typeof(AcrylicDeck))]
    public void A_saved_design_id_is_that_design(string id, Type design)
        => Assert.IsType(design, DeckDesigns.Parse(id));

    [Theory]
    [Trait("Category", "Unhappy")]
    [InlineData("bamboo")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_design_id_is_the_Classic_deck(string? id)
        => Assert.IsType<ClassicDeck>(DeckDesigns.Parse(id));

    // ---- Carbon Signal ------------------------------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(33.333, "33.3")]
    [InlineData(45, "45")]
    [InlineData(16.667, "16.7")]
    public void Carbons_readout_shows_the_speed_chosen(double rpm, string shown)
    {
        var (window, deck) = Show("carbon");

        deck.Speed = rpm;
        Gui.Settle();

        Assert.All(deck.GetVisualDescendants().OfType<DeckText>().Where(t => t.Classes.Contains("rpm")), t => Assert.Equal(shown, t.Text));
        window.Close();
    }

    [AvaloniaFact]
    public void Carbons_strobe_ticks_turn_with_the_record()
    {
        var (window, deck) = Show("carbon");

        deck.Angle = 123.5;
        Gui.Settle();

        var record = Assert.IsType<VinylView>(deck.Record);
        Assert.All(deck.GetVisualDescendants().OfType<TickRing>(), ring => Assert.Equal(record.Angle, ring.Angle));
        Assert.Equal(123.5, record.Angle);
        window.Close();
    }

    // ---- Linear Tracking ----------------------------------------------------------------------------------------

    [AvaloniaFact]
    public void Linear_Trackings_carriage_slides_105_units_in_on_its_rails_then_lowers_and_never_leaves_them()
    {
        var (window, deck) = Show("linear");
        var units = Units(deck);
        double Across() => deck.Stylus!.TranslatePoint(default, units)!.Value.X;

        Assert.Equal(1130, Across(), 1);
        deck.Arm = ArmState.OverLeadIn;
        var seen = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            Gui.FinishAnimations(50);
            seen.Add(Across());
        }

        Assert.Equal(1025, Across(), 1);
        Assert.All(seen, x => Assert.InRange(x, 770, 1200));
        Assert.Equal((0.0, KitDeckView.LiftedScale, -105.0), Pose(deck)); // over the lead-in, still lifted

        Set(deck, ArmState.Lowered, spinning: true);
        Assert.Equal((0.0, 1.0, -105.0), Pose(deck));
        Assert.Equal(1025, Across(), 1);

        // The record and its platter are shifted: their centre is at 560.
        var record = deck.Record!;
        var centre = record.TranslatePoint(new Point(record.Bounds.Width / 2, record.Bounds.Height / 2), units)!.Value;
        Assert.Equal(new Point(560, 540), new Point(Math.Round(centre.X, 1), Math.Round(centre.Y, 1)));
        window.Close();
    }

    // ---- in the app ---------------------------------------------------------------------------------------------

    private static async Task<AppHarness> OpenAsync(string design, AppTheme theme = AppTheme.Dark)
    {
        var app = await AppHarness.OpenAsync(new AppOptions
        {
            Scripted = true,
            Library = SampleLibrary.Create(),
            Covers = SampleLibrary.Covers(accents: true),
            Theme = theme,
            Settings = new AppSettings { TurntableDesign = design },
        });
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 10, "the albums");
        return app;
    }

    private static async Task LoadAsync(AppHarness app, PlayerState state = PlayerState.Playing)
    {
        var a = SampleLibrary.Albums[Album - 1];
        app.Scripted!.Status = ScriptedPlayer.Playing(a.Tracks[0], trackId: SampleLibrary.TrackId(Album, 1), state: state);
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => app.ViewModel.PlayingAlbumId == SampleLibrary.AlbumId(Album) && app.ViewModel.Player.State == state
                                   && app.ViewModel.NowPlaying.Tracks.Count == a.Tracks.Length, "the album loaded");
    }

    private static KitDeckView Deck(AppHarness app) => Assert.IsType<KitDeckView>(Gui.Find<DeckHost>(app.Window, "Deck").Deck);

    private static async Task LoadAlbumAsync(AppHarness app, int album)
    {
        var a = SampleLibrary.Albums[album - 1];
        app.Scripted!.Status = ScriptedPlayer.Playing(a.Tracks[0], trackId: SampleLibrary.TrackId(album, 1));
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => app.ViewModel.PlayingAlbumId == SampleLibrary.AlbumId(album) && app.ViewModel.NowPlaying.Tracks.Count == a.Tracks.Length,
            "the album loaded");
    }

    /// <summary>
    /// A pixel 30 units inside Smoked Acrylic's plinth and one 30 units outside, either side of its right edge at y 400,
    /// where nothing else is drawn (below the arm board, above the rest).
    /// </summary>
    private static (SKColor Inside, SKColor Outside) Sides(AppHarness app)
    {
        var plinth = Part(Deck(app), "Glass");
        var edge = plinth.TranslatePoint(new Point(plinth.Bounds.Width, 400), app.Window)!.Value;
        var reach = 30 * plinth.TransformToVisual(app.Window)!.Value.M11;
        using var frame = Capture(app);
        return (Pixel(frame, app, edge.X - reach, edge.Y), Pixel(frame, app, edge.X + reach, edge.Y));
    }

    [AvaloniaTheory]
    [MemberData(nameof(Designs))]
    public async Task Choosing_the_design_in_Settings_shows_it_in_Now_Playing_at_once_and_saves_it(string design)
    {
        await using var app = await OpenAsync("classic");
        await LoadAsync(app);
        app.ViewModel.NowPlaying.Open();
        Gui.FinishAnimations(Settled);
        Assert.IsType<ClassicDeckView>(Gui.Find<DeckHost>(app.Window, "Deck").Deck);
        app.ViewModel.Settings.Open();
        Gui.FinishAnimations();

        var card = Gui.All<ListBoxItem>(Gui.Find<ListBox>(app.Window, "DesignChoice")).Single(i => i.DataContext is DeckDesign d && d.Id == design);
        card.BringIntoView();
        Gui.Settle();
        Gui.Click(app.Window, card);

        var deck = Deck(app);
        Assert.Same(Application.Current!.FindResource(Kit[design].Theme), deck.Theme);
        Assert.Equal((ArmState.Lowered, true), (deck.Arm, deck.IsSpinning));
        Assert.Same(app.ViewModel.NowPlaying.Cover, deck.Label);
        Assert.Equal(design, new SettingsStore(app.Data).Load().TurntableDesign);
        Assert.Single(card.GetVisualDescendants().OfType<KitDeckView>()); // its card shows the design itself, small
    }

    [AvaloniaFact]
    public async Task Carbons_readout_follows_Settings_speed_and_is_lit_only_while_the_platter_turns()
    {
        await using var app = await OpenAsync("carbon");
        await LoadAsync(app, PlayerState.Paused);
        app.ViewModel.NowPlaying.Open();
        Gui.FinishAnimations(Settled);
        var deck = Deck(app);
        var readout = (DeckText)Part(deck, "Readout");
        Assert.Equal(("33.3", 0.0), (readout.Text, readout.Opacity));

        app.ViewModel.Settings.SetRecordSpeed(45);
        Gui.Settle();
        Assert.Equal("45", readout.Text);

        app.Scripted!.Status = app.Scripted.Status with { State = PlayerState.Playing };
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => deck.IsSpinning, "spinning");
        Gui.FinishAnimations(Settled);
        Assert.Equal(1, readout.Opacity);
        Assert.Equal(app.ViewModel.SpinClock.Angle, deck.GetVisualDescendants().OfType<TickRing>().First().Angle);
    }

    [AvaloniaFact]
    public async Task Smoked_Acrylic_shows_Now_Playings_blurred_cover_through_its_plinth_lined_up_with_it()
    {
        await using var app = await OpenAsync("acrylic");
        await LoadAsync(app);
        app.ViewModel.NowPlaying.Open();
        Gui.FinishAnimations(Settled);
        var deck = Deck(app);
        var background = Gui.Find<Image>(app.Window, "Backdrop");
        var glass = (BackdropView)Part(deck, "Backdrop");
        Assert.Same(background, glass.Source);
        Assert.Same(background, deck.Backdrop);

        // One point inside the plinth, one outside, 30 units either side of its right edge where nothing else is drawn
        // (y 400: below the arm board, above the rest): inside is the same background under the smoke tint.
        var plinth = Part(deck, "Glass");
        var edge = plinth.TranslatePoint(new Point(plinth.Bounds.Width, 400), app.Window)!.Value;
        var reach = 30 * plinth.TransformToVisual(app.Window)!.Value.M11;
        using var frame = Capture(app);
        var outside = Pixel(frame, app, edge.X + reach, edge.Y);
        var inside = Pixel(frame, app, edge.X - reach, edge.Y);
        var tint = Assert.IsAssignableFrom<ISolidColorBrush>(Resource(app, "AcrylicTintBrush")).Color;
        var a = tint.A / 255.0;
        byte Mix(byte under, byte over) => (byte)Math.Round((under * (1 - a)) + (over * a));
        var expected = (Mix(outside.Red, tint.R), Mix(outside.Green, tint.G), Mix(outside.Blue, tint.B));

        Assert.True(Near(expected, (inside.Red, inside.Green, inside.Blue), 3),
            $"inside {inside}, expected about {expected} (outside {outside} under the tint)");

        // Without what is behind it, the inside would be the page's flat background under the tint: not that.
        var page = Assert.IsAssignableFrom<ISolidColorBrush>(Resource(app, "BgBrush")).Color;
        var flat = (Mix(page.R, tint.R), Mix(page.G, tint.G), Mix(page.B, tint.B));
        Assert.False(Near(flat, (inside.Red, inside.Green, inside.Blue), 3), $"inside {inside} is the flat page under the tint {flat}");
    }

    /// <summary>A resource as the window's theme has it.</summary>
    private static object? Resource(AppHarness app, string key)
        => app.Window.TryFindResource(key, app.Window.ActualThemeVariant, out var value) ? value : null;

    private static SKBitmap Capture(AppHarness app)
    {
        using var frame = app.Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
        using var png = new MemoryStream();
        frame.Save(png, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        return SKBitmap.Decode(png.ToArray());
    }

    private static SKColor Pixel(SKBitmap frame, AppHarness app, double x, double y)
    {
        var scale = frame.Width / app.Window.Bounds.Width;
        return frame.GetPixel((int)Math.Round(x * scale), (int)Math.Round(y * scale));
    }

    private static bool Near((byte, byte, byte) expected, (byte, byte, byte) actual, int tolerance)
        => Math.Abs(expected.Item1 - actual.Item1) <= tolerance && Math.Abs(expected.Item2 - actual.Item2) <= tolerance
           && Math.Abs(expected.Item3 - actual.Item3) <= tolerance;

    [AvaloniaFact]
    public async Task Smoked_Acrylic_shows_the_new_cover_through_its_plinth_when_the_album_changes()
    {
        await using var app = await OpenAsync("acrylic");
        await LoadAlbumAsync(app, 6); // Night Transit: blue behind the plinth
        app.ViewModel.NowPlaying.Open();
        Gui.FinishAnimations(Settled);
        var first = app.ViewModel.NowPlaying.Cover;
        Assert.NotNull(first);
        var (before, _) = Sides(app);

        await LoadAlbumAsync(app, 9); // a red one
        // Its cover loads after its tracks: sample only once it shows, or the test would compare the page with itself.
        await Gui.UntilAsync(() => app.ViewModel.NowPlaying.Cover is { } cover && !ReferenceEquals(cover, first), "the new cover");
        Gui.FinishAnimations(Settled);
        var (inside, outside) = Sides(app);

        var tint = Assert.IsAssignableFrom<ISolidColorBrush>(Resource(app, "AcrylicTintBrush")).Color;
        var a = tint.A / 255.0;
        byte Mix(byte under, byte over) => (byte)Math.Round((under * (1 - a)) + (over * a));
        var expected = (Mix(outside.Red, tint.R), Mix(outside.Green, tint.G), Mix(outside.Blue, tint.B));
        Assert.True(Near(expected, (inside.Red, inside.Green, inside.Blue), 3), $"inside {inside}, expected about {expected} (outside {outside})");
        Assert.False(Near((before.Red, before.Green, before.Blue), (inside.Red, inside.Green, inside.Blue), 3), $"inside {inside} is still the first cover's {before}");
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task Smoked_Acrylic_without_a_cover_shows_the_page_through_its_plinth()
    {
        await using var app = await OpenAsync("acrylic");
        app.ViewModel.NowPlaying.Open();
        Gui.FinishAnimations(Settled);
        var deck = Deck(app);

        var glass = (BackdropView)Part(deck, "Backdrop");
        Assert.Null(Gui.Find<Image>(app.Window, "Backdrop").Source);
        Assert.Same(Resource(app, "BgBrush"), glass.Background);
        using var frame = Capture(app); // draws, and doesn't throw, with nothing to show
    }

    // ---- snapshots ----------------------------------------------------------------------------------------------

    public static TheoryData<string, AppTheme, PlayerState> Shots { get; } = Shoot();

    private static TheoryData<string, AppTheme, PlayerState> Shoot()
    {
        var shots = new TheoryData<string, AppTheme, PlayerState>();
        foreach (var design in Kit.Keys)
        {
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                shots.Add(design, theme, PlayerState.Playing);
                shots.Add(design, theme, PlayerState.Stopped);
            }
        }

        return shots;
    }

    /// <summary>Each design in Now Playing, playing and parked, the spin clock still (the app's clock is the tests').</summary>
    [AvaloniaTheory]
    [MemberData(nameof(Shots))]
    public async Task Snapshot_of_the_deck(string design, AppTheme theme, PlayerState state)
    {
        await using var app = await OpenAsync(design, theme);
        await LoadAsync(app, state);
        app.ViewModel.NowPlaying.Open();
        Gui.FinishAnimations(Settled);

        var name = state == PlayerState.Playing ? "playing" : "parked";
        Snapshots.Match(app.Window, Gui.Find<DeckHost>(app.Window, "Deck"), $"deck-{design}-{name}-{theme.ToString().ToLowerInvariant()}");
    }
}
