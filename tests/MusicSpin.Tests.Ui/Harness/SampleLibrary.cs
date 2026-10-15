using Avalonia.Media.Imaging;
using MusicSpin.App.Services;
using SkiaSharp;

namespace MusicSpin.Tests.Ui.Harness;

/// <summary>
/// A small library like the mockup's: ten albums over four genres (one untagged, one without a year), three tracks
/// each. Album ids are "a1".."a10"; track ids "a1-1".."a10-3".
/// </summary>
internal static class SampleLibrary
{
    public static readonly (string Title, string Artist, int? Year, string? Genre, bool HiRes, string[] Tracks)[] Albums =
    [
        ("Northern Lines", "Halvard Eik", 2024, "Ambient", true, ["Aurora", "Polar Drift", "Snowfield"]),
        ("Field Notes", "Tomas Lind", 2018, "Ambient", true, ["Meadow", "Birch", "Low Sun"]),
        ("Cello Suites", "Elena Varga", 2020, "Classical", true, ["Prelude", "Allemande", "Courante"]),
        ("Partitas", "Elena Varga", 2023, "Classical", true, ["Sinfonia", "Sarabande", "Gigue"]),
        ("Glass Garden", "Ilse Morrow", 2022, "Classical", true, ["Glasshouse", "Fern", "Rain on Panes"]),
        ("Night Transit", "Kodo Arai", 2021, "Electronic", false, ["The Thread", "Night Bus", "Neon"]),
        ("Velvet Hours", "Ana Cruz Quintet", 2017, "Jazz", true, ["The Hollow", "Glass Window", "Salt Bloom"]),
        ("Copper Sun", "Delta Ruth", 1973, "Jazz", false, ["Lantern Paper", "The Paper", "Ember Arc"]),
        ("Blue Hour", "Miles Ahead", 1959, "Jazz", false, ["So Blue", "Dusk", "Bell"]),
        ("Untitled Tape", "Nobody", null, null, false, ["Side A", "Side B", "Hiss"]),
    ];

    public static string AlbumId(int album) => $"a{album}";

    public static string TrackId(int album, int track) => $"a{album}-{track}";

    /// <summary>Album <paramref name="album"/> (1-based) as the app reads it.</summary>
    public static AlbumSummary Album(int album)
    {
        var a = Albums[album - 1];
        return new AlbumSummary(AlbumId(album), a.Title, a.Artist, a.Year, a.Genre, "FLAC", a.HiRes ? "24/96" : "16/44.1",
            a.Tracks.Length, a.Tracks.Length * 200, a.HiRes);
    }

    public static StubLibrary Create()
    {
        var library = new StubLibrary();
        for (var i = 1; i <= Albums.Length; i++)
        {
            var a = Albums[i - 1];
            library.Albums.Add(Album(i));
            library.Tracks.AddRange(a.Tracks.Select((title, t) => new TrackSummary(TrackId(i, t + 1), $"/music/{a.Title}/{t + 1}.flac", title,
                a.Artist, a.Title, AlbumId(i), t + 1, a.Genre, 150 + (i * 7) + (t * 31), "FLAC", a.HiRes)));
        }

        return library;
    }

    /// <summary>
    /// Covers in two colours per album (a diagonal split), except for albums 2, 5 and 10, which show their placeholder;
    /// with <paramref name="accents"/>, each cover's first colour is its album's colour.
    /// </summary>
    public static StubCovers Covers(bool accents = false)
    {
        var bitmaps = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
        var colours = new Dictionary<string, Rgb>(StringComparer.Ordinal);
        for (var i = 1; i <= Albums.Length; i++)
        {
            if (i is 2 or 5 or 10)
            {
                continue;
            }

            var (a, b) = (Hue(i * 37), Hue((i * 37) + 150));
            bitmaps[AlbumId(i)] = TestImages.Bitmap(TestImages.Png(64, 64, (x, y) => x + y < 64 ? a : b));
            colours[AlbumId(i)] = new Rgb(a.Red, a.Green, a.Blue);
        }

        Rgb? Accent(string id) => colours.TryGetValue(id, out var c) ? c : null;
        return new StubCovers(id => bitmaps.GetValueOrDefault(id), accents ? Accent : null);
    }

    private static SKColor Hue(int degrees) => SKColor.FromHsv(degrees % 360, 55, 70);
}
