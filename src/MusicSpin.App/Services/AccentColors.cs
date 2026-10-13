namespace MusicSpin.App.Services;

/// <summary>An opaque sRGB colour.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>Max minus min channel: how far from grey.</summary>
    public int Saturation => Math.Max(R, Math.Max(G, B)) - Math.Min(R, Math.Min(G, B));

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// The album accent (mockup: <c>accentBase</c> / <c>pickAccent</c> in app.js): the most saturated of the cover's
/// prominent colours, mixed 80% with white so it reads on the dark theme, and an "ink" variant for text on the light
/// theme (the accent mixed 55% with black). Mixing is done in sRGB.
/// </summary>
public static class AccentColors
{
    /// <summary>A colour bucket counts as prominent from this share of the opaque pixels.</summary>
    public const double ProminentShare = 0.03;

    /// <summary>
    /// The most saturated prominent colour of <paramref name="pixels"/> (each colour is rounded to 16 levels per channel,
    /// the bucket's average is returned), or null when there are no opaque pixels.
    /// </summary>
    public static Rgb? Base(IEnumerable<(Rgb Color, byte Alpha)> pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        var buckets = new Dictionary<int, (long R, long G, long B, int Count)>();
        var total = 0;
        foreach (var (c, alpha) in pixels)
        {
            if (alpha < 128)
            {
                continue;
            }

            var key = ((c.R >> 4) << 8) | ((c.G >> 4) << 4) | (c.B >> 4);
            var b = buckets.GetValueOrDefault(key);
            buckets[key] = (b.R + c.R, b.G + c.G, b.B + c.B, b.Count + 1);
            total++;
        }

        if (total == 0)
        {
            return null;
        }

        var averages = buckets.Values
            .Select(b => (Color: new Rgb((byte)(b.R / b.Count), (byte)(b.G / b.Count), (byte)(b.B / b.Count)), b.Count))
            .OrderByDescending(b => b.Count)
            .ToList();
        var prominent = averages.Where(b => b.Count >= total * ProminentShare).ToList();
        return (prominent.Count > 0 ? prominent : averages.Take(8).ToList())
            .OrderByDescending(b => b.Color.Saturation).ThenByDescending(b => b.Count).First().Color;
    }

    /// <summary>The accent: <paramref name="color"/> mixed 80% with white.</summary>
    public static Rgb Accent(Rgb color) => Mix(color, new Rgb(255, 255, 255), 0.8);

    /// <summary>Text in the accent on the light theme: the accent mixed 55% with black.</summary>
    public static Rgb Ink(Rgb accent) => Mix(accent, new Rgb(0, 0, 0), 0.55);

    /// <summary><paramref name="share"/> of <paramref name="a"/>, the rest <paramref name="b"/>.</summary>
    public static Rgb Mix(Rgb a, Rgb b, double share)
    {
        static byte Channel(byte x, byte y, double s) => (byte)Math.Round((x * s) + (y * (1 - s)), MidpointRounding.AwayFromZero);
        return new Rgb(Channel(a.R, b.R, share), Channel(a.G, b.G, share), Channel(a.B, b.B, share));
    }
}
