using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using MusicSpin.App.Services;
using MusicSpin.App.Theming;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Shared;
using MusicSpin.Tests.Ui.Harness;
using SkiaSharp;

namespace MusicSpin.Tests.Ui;

/// <summary>Album accent colours (#8) and the cover service's caching.</summary>
public sealed class AccentColorTests
{
    private static IEnumerable<(Rgb, byte)> Pixels(params (Rgb Color, int Count)[] runs)
        => runs.SelectMany(r => Enumerable.Repeat((r.Color, (byte)255), r.Count));

    [Fact]
    public void The_most_saturated_prominent_colour_is_picked()
    {
        var grey = new Rgb(90, 90, 90);
        var orange = new Rgb(232, 106, 60);
        var paleBlue = new Rgb(170, 180, 200);

        Assert.Equal(orange, AccentColors.Base(Pixels((grey, 700), (orange, 100), (paleBlue, 200))));
    }

    [Fact]
    public void A_speck_of_colour_is_not_prominent()
    {
        var grey = new Rgb(90, 90, 90);
        var white = new Rgb(250, 250, 250);

        Assert.Equal(grey, AccentColors.Base(Pixels((grey, 900), (white, 80), (new Rgb(255, 0, 0), 20))));
    }

    [Fact]
    public void Transparent_pixels_are_ignored_and_an_empty_image_has_no_accent()
    {
        Assert.Null(AccentColors.Base([(new Rgb(255, 0, 0), 0)]));
        Assert.Null(AccentColors.Base([]));
    }

    [Fact]
    public void The_accent_is_mixed_80_percent_with_white_and_the_ink_55_percent_with_black()
    {
        var accent = AccentColors.Accent(new Rgb(0xE8, 0x6A, 0x3C));

        Assert.Equal(new Rgb(0xED, 0x88, 0x63), accent);
        Assert.Equal(new Rgb(0x82, 0x4B, 0x36), AccentColors.Ink(accent));
    }

    // ---- contrast (#194) --------------------------------------------------------------------------------------

    /// <summary>The covers of #194: their picked colour, and whether it has a colour (a grey gets the default accent).</summary>
    public static TheoryData<string, AppTheme> Covers()
    {
        var data = new TheoryData<string, AppTheme>();
        foreach (var cover in CoverColours.Keys)
        {
            data.Add(cover, AppTheme.Dark);
            data.Add(cover, AppTheme.Light);
        }

        return data;
    }

    private static readonly Dictionary<string, Rgb?> CoverColours = new()
    {
        ["near-black"] = new Rgb(0x2A, 0x0C, 0x14), // a dark wine red: dark photography with a hint of colour
        ["navy"] = new Rgb(0x10, 0x18, 0x60),
        ["dark grey"] = new Rgb(0x2C, 0x2E, 0x33), // Sir Charles Mackerras Conducts Mozart, roughly
        ["grey"] = new Rgb(0x80, 0x80, 0x80),
        ["very light"] = new Rgb(0xFA, 0xF0, 0xC8), // cream: light on light
        ["pale yellow"] = new Rgb(0xF0, 0xE6, 0x50),
        ["saturated mid-tone"] = new Rgb(0xE8, 0x6A, 0x3C),
        ["default (no art)"] = null,
    };

    /// <summary>Styles/Tokens.axaml: BgBrush (the page), Bg2Brush, Bg3Brush of each theme.</summary>
    private static readonly Rgb[] DarkSurfaces = [new(0x0E, 0x0F, 0x12), new(0x15, 0x17, 0x1C), new(0x1D, 0x20, 0x27)];

    private static readonly Rgb[] LightSurfaces = [new(0xF4, 0xF2, 0xEE), new(0xEB, 0xE8, 0xE2), new(0xDF, 0xDB, 0xD3)];

    private static readonly Rgb Black = new(0, 0, 0);

    [Fact]
    public void Contrast_follows_WCAG_2()
    {
        Assert.Equal(21, AccentColors.Contrast(Black, new Rgb(255, 255, 255)), 3);
        Assert.Equal(1, AccentColors.Contrast(new Rgb(0x80, 0x40, 0x20), new Rgb(0x80, 0x40, 0x20)), 3);
        Assert.Equal(AccentColors.Contrast(Black, new Rgb(0x77, 0x77, 0x77)), AccentColors.Contrast(new Rgb(0x77, 0x77, 0x77), Black), 9);
        Assert.Equal(4.48, AccentColors.Contrast(new Rgb(0x77, 0x77, 0x77), new Rgb(255, 255, 255)), 2); // the classic #777 on white
    }

    [Theory]
    [MemberData(nameof(Covers))]
    public void The_accent_and_its_ink_read_on_every_background_of_the_theme_and_under_the_tint(string cover, AppTheme theme)
    {
        var surfaces = theme == AppTheme.Light ? LightSurfaces : DarkSurfaces;
        var (accent, ink) = Appearance.Colors(CoverColours[cover], theme, surfaces, Black);

        foreach (var surface in surfaces.Append(Appearance.Tinted(accent, surfaces[0])))
        {
            Assert.True(AccentColors.Contrast(ink, surface) >= AccentColors.MinContrast, $"{cover}: ink {ink} on {surface}: {AccentColors.Contrast(ink, surface):0.00}");
        }

        // Black text on an accent fill (OnAccentBrush: Play buttons, badges) reads too.
        Assert.True(AccentColors.Contrast(accent, Black) >= AccentColors.MinContrast, $"{cover}: black on {accent}");
        Assert.Equal(theme == AppTheme.Dark, accent == ink);
    }

    /// <summary>
    /// A dark cover's accent is lightened (on the dark theme for the text, on the light theme for the black text on
    /// accent fills); a light cover's ink is darkened on the light theme. Either way the hue stays.
    /// </summary>
    [Theory]
    [InlineData(0x2A, 0x0C, 0x14, AppTheme.Dark, true, false)]
    [InlineData(0x10, 0x18, 0x60, AppTheme.Dark, true, false)]
    [InlineData(0x10, 0x18, 0x60, AppTheme.Light, true, false)]
    [InlineData(0xFA, 0xF0, 0xC8, AppTheme.Light, false, true)]
    [InlineData(0xF0, 0xE6, 0x50, AppTheme.Light, false, true)]
    public void Lightening_or_darkening_keeps_the_hue(byte r, byte g, byte b, AppTheme theme, bool lightened, bool darkened)
    {
        var cover = new Rgb(r, g, b);
        var start = AccentColors.Accent(cover);
        var (accent, ink) = Appearance.Colors(cover, theme, theme == AppTheme.Light ? LightSurfaces : DarkSurfaces, Black);

        Assert.Equal(lightened, AccentColors.Luminance(accent) > AccentColors.Luminance(start));
        Assert.Equal(lightened, accent != start);
        Assert.Equal(darkened, AccentColors.Luminance(ink) < AccentColors.Luminance(AccentColors.Ink(accent)));
        Assert.InRange(HueDistance(AccentColors.Hue(start), AccentColors.Hue(accent)), 0, 3);
        Assert.InRange(HueDistance(AccentColors.Hue(start), AccentColors.Hue(ink)), 0, 3);

        static double HueDistance(double a, double b) => Math.Min(Math.Abs(a - b), 360 - Math.Abs(a - b));
    }

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public void A_cover_that_already_reads_is_left_as_the_mockup_makes_it(AppTheme theme)
    {
        var cover = new Rgb(0xE8, 0x6A, 0x3C);
        var accent = AccentColors.Accent(cover);

        Assert.Equal((accent, theme == AppTheme.Light ? AccentColors.Ink(accent) : accent), Appearance.Colors(cover, theme, theme == AppTheme.Light ? LightSurfaces : DarkSurfaces, Black));
        Assert.Equal((Appearance.DefaultAccent, theme == AppTheme.Light ? AccentColors.Ink(Appearance.DefaultAccent) : Appearance.DefaultAccent),
            Appearance.Colors(null, theme, theme == AppTheme.Light ? LightSurfaces : DarkSurfaces, Black));
    }

    [Theory]
    [InlineData(0x2C, 0x2E, 0x33, true)] // dark grey with a blue cast: chroma 7
    [InlineData(0x80, 0x80, 0x80, true)]
    [InlineData(0x0A, 0x0A, 0x0A, true)]
    [InlineData(0xF0, 0xEE, 0xE0, true)] // off-white paper: chroma 16
    [InlineData(0x40, 0x40, 0x57, true)] // chroma 23, just under
    [InlineData(0x40, 0x40, 0x58, false)] // chroma 24
    [InlineData(0x6E, 0x5A, 0x46, false)] // sepia: chroma 40
    public void A_cover_with_no_real_colour_gets_the_default_accent(byte r, byte g, byte b, bool grey)
    {
        var cover = new Rgb(r, g, b);

        Assert.Equal(grey, AccentColors.IsGrey(cover));
        var (accent, _) = Appearance.Colors(cover, AppTheme.Dark, DarkSurfaces, Black);
        Assert.Equal(grey, accent == Appearance.DefaultAccent);
    }

    [Fact]
    public void Readable_returns_the_colour_when_it_reads_and_the_extreme_of_its_hue_when_nothing_does()
    {
        var navy = new Rgb(0x10, 0x18, 0x60);

        Assert.Equal(navy, AccentColors.Readable(navy, _ => true, lighten: true));
        Assert.Equal(new Rgb(255, 255, 255), AccentColors.Readable(navy, _ => false, lighten: true));
        Assert.Equal(Black, AccentColors.Readable(navy, _ => false, lighten: false));

        // The least lightening that reads: one step less doesn't.
        var lifted = AccentColors.Readable(navy, c => AccentColors.Contrast(c, Black) >= 7, lighten: true);
        Assert.InRange(AccentColors.Contrast(lifted, Black), 7, 7.3);
        Assert.Throws<ArgumentNullException>(() => AccentColors.Readable(navy, null!, lighten: true));
        Assert.Throws<ArgumentNullException>(() => AccentColors.ReadsOn(navy, null!));
        Assert.Throws<ArgumentNullException>(() => Appearance.Colors(navy, AppTheme.Dark, null!, Black));
    }

    [Fact]
    public void Without_surfaces_only_the_text_on_the_accent_is_checked()
    {
        var navy = new Rgb(0x10, 0x18, 0x60);

        var (accent, ink) = Appearance.Colors(navy, AppTheme.Light, [], Black);

        Assert.True(AccentColors.Contrast(accent, Black) >= AccentColors.MinContrast);
        Assert.Equal(AccentColors.Ink(accent), ink); // nothing to read on, so the mockup's ink
    }

    /// <summary>The same covers against the app's own tokens (Styles/Tokens.axaml), as <see cref="Appearance"/> reads them.</summary>
    [AvaloniaTheory]
    [MemberData(nameof(Covers))]
    public void The_app_s_theme_tokens_give_an_ink_that_reads_on_its_background(string cover, AppTheme theme)
    {
        var app = Application.Current!;
        var variant = theme == AppTheme.Light ? ThemeVariant.Light : ThemeVariant.Dark;
        var (accent, ink) = new Appearance(app).ColorsFor(CoverColours[cover], theme);

        foreach (var key in new[] { "BgBrush", "Bg2Brush", "Bg3Brush" })
        {
            Assert.True(app.TryGetResource(key, variant, out var value), key);
            var c = Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
            var surface = new Rgb(c.R, c.G, c.B);
            Assert.True(AccentColors.Contrast(ink, surface) >= AccentColors.MinContrast, $"{cover}: ink {ink} on {key} {surface}");
        }

        Assert.Equal(Appearance.Colors(CoverColours[cover], theme, theme == AppTheme.Light ? LightSurfaces : DarkSurfaces, Black), (accent, ink));
    }

    [Fact]
    public void The_fixture_covers_give_their_colour()
    {
        Assert.Equal(new Rgb(255, 0, 0), Quantized(Accent(File.ReadAllBytes(Path.Combine(LibraryTestData.FixturesDirectory, "cover-red.jpg")))));
        Assert.Equal(new Rgb(0, 0, 255), Quantized(Accent(File.ReadAllBytes(Path.Combine(LibraryTestData.FixturesDirectory, "cover-blue.png")))));

        static Rgb Accent(byte[] image)
        {
            using var bitmap = SKBitmap.Decode(image);
            return AccentColors.Base(Enumerable.Range(0, bitmap.Width * bitmap.Height)
                .Select(i => bitmap.GetPixel(i % bitmap.Width, i / bitmap.Width))
                .Select(c => (new Rgb(c.Red, c.Green, c.Blue), c.Alpha)))!.Value;
        }

        // JPEG noise: round to the nearest of 0 or 255
        static Rgb Quantized(Rgb c) => new((byte)(c.R > 127 ? 255 : 0), (byte)(c.G > 127 ? 255 : 0), (byte)(c.B > 127 ? 255 : 0));
    }

    [AvaloniaFact]
    public async Task Each_album_is_computed_once_and_each_cover_decoded_once()
    {
        var root = TempDir.Create("covers");
        try
        {
            var music = Path.Combine(root, "music");
            var data = new DataDirectory(Path.Combine(root, "data"), DataDirectorySource.Override);
            TestImages.Album(music, "Quadrants", "Painter", cover: TestImages.Cover());
            string albumId;
            using (var library = MusicLibrary.OpenWritable(data))
            {
                library.AddFolder(music);
                library.Scan(cancellationToken: TestContext.Current.CancellationToken);
                albumId = library.Albums(new AlbumQuery()).Items.Single().Id;
            }

            var covers = new CoverService(data, new SkiaCoverResizer());

            var accents = await Task.WhenAll(covers.AccentAsync(albumId), covers.AccentAsync(albumId));
            await covers.AccentAsync(albumId);
            using var first = await covers.CoverAsync(albumId, CoverSize.Small);
            using var again = await covers.CoverAsync(albumId, CoverSize.Small);

            var accent = Assert.NotNull(accents[0]); // orange: the most saturated quadrant (the small cover is a JPEG)
            Assert.True(Math.Abs(accent.R - 232) + Math.Abs(accent.G - 106) + Math.Abs(accent.B - 60) < 24, accent.ToString());
            Assert.Equal(accent, accents[1]);
            Assert.Equal(1, covers.AccentsComputed);
            Assert.NotNull(first);
            Assert.Same(first.Bitmap, again?.Bitmap); // two leases on the one bitmap
            Assert.Equal(1, covers.BitmapsLoaded);
        }
        finally
        {
            TempDir.TryDelete(root);
        }
    }

    [AvaloniaFact]
    public async Task An_album_without_art_has_no_cover_and_no_accent()
    {
        var root = TempDir.Create("covers");
        try
        {
            var music = Path.Combine(root, "music");
            var data = new DataDirectory(Path.Combine(root, "data"), DataDirectorySource.Override);
            TestImages.Album(music, "Plain", "Nobody");
            string albumId;
            using (var library = MusicLibrary.OpenWritable(data))
            {
                library.AddFolder(music);
                library.Scan(cancellationToken: TestContext.Current.CancellationToken);
                albumId = library.Albums(new AlbumQuery()).Items.Single().Id;
            }

            var covers = new CoverService(data, new SkiaCoverResizer());

            Assert.Null(await covers.CoverAsync(albumId, CoverSize.Small));
            Assert.Null(await covers.AccentAsync(albumId));
            Assert.Equal(0, covers.BitmapsLoaded);
        }
        finally
        {
            TempDir.TryDelete(root);
        }
    }
}
