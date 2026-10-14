using Avalonia.Headless.XUnit;
using MusicSpin.App.Services;
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
            var first = await covers.CoverAsync(albumId, CoverSize.Small);
            var again = await covers.CoverAsync(albumId, CoverSize.Small);

            var accent = Assert.NotNull(accents[0]); // orange: the most saturated quadrant (the small cover is a JPEG)
            Assert.True(Math.Abs(accent.R - 232) + Math.Abs(accent.G - 106) + Math.Abs(accent.B - 60) < 24, accent.ToString());
            Assert.Equal(accent, accents[1]);
            Assert.Equal(1, covers.AccentsComputed);
            Assert.NotNull(first);
            Assert.Same(first, again);
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
