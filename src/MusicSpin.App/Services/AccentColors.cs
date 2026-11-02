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
/// theme (the accent mixed 55% with black). Mixing is done in sRGB. None of that guarantees contrast (#194): a dark
/// cover gives a dark accent. <see cref="Readable"/> moves a colour's lightness, keeping its hue, until it reads on
/// what it is drawn on, and a grey cover (<see cref="IsGrey"/>) gets the default accent instead.
/// </summary>
public static class AccentColors
{
    /// <summary>A colour bucket counts as prominent from this share of the opaque pixels.</summary>
    public const double ProminentShare = 0.03;

    /// <summary>WCAG 2 AA for normal text: the least contrast accent text may have against what it is drawn on.</summary>
    public const double MinContrast = 4.5;

    /// <summary>
    /// A cover whose picked colour is closer to grey than this (max minus min channel, of 255) has no colour to speak
    /// of: dark photography, black and white, JPEG noise on grey. Its accent would be a tinted grey, so the default
    /// accent is used instead. 24 (about 9%) still keeps muted colours such as sepia or a dusty blue (30 and up).
    /// </summary>
    public const int GreyChroma = 24;

    /// <summary>Whether <paramref name="color"/> is too close to grey to make an accent of (<see cref="GreyChroma"/>).</summary>
    public static bool IsGrey(Rgb color) => color.Saturation < GreyChroma;

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

    /// <summary>
    /// <paramref name="color"/>, or the nearest colour of the same hue and HSL saturation (lighter when
    /// <paramref name="lighten"/>, darker otherwise) for which <paramref name="reads"/> holds. White and black always
    /// end the search, so it returns the extreme when nothing in between reads.
    /// </summary>
    public static Rgb Readable(Rgb color, Func<Rgb, bool> reads, bool lighten)
    {
        ArgumentNullException.ThrowIfNull(reads);
        if (reads(color))
        {
            return color;
        }

        var (h, s, l) = ToHsl(color);
        var end = lighten ? 1.0 : 0.0;
        var extreme = FromHsl(h, s, end);
        if (!reads(extreme))
        {
            return extreme;
        }

        // reads(l) is false, reads(end) is true; lightness moves contrast one way on dark surfaces and the other on light
        // ones, so the boundary is found by halving. 20 rounds take it well below one level of a channel.
        var (from, to) = (l, end);
        for (var i = 0; i < 20; i++)
        {
            var mid = (from + to) / 2;
            if (reads(FromHsl(h, s, mid)))
            {
                to = mid;
            }
            else
            {
                from = mid;
            }
        }

        return FromHsl(h, s, to);
    }

    /// <summary>
    /// Whether <paramref name="ink"/> has at least <see cref="MinContrast"/> against each of <paramref name="surfaces"/>.
    /// </summary>
    public static bool ReadsOn(Rgb ink, IEnumerable<Rgb> surfaces)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        return surfaces.All(s => Contrast(ink, s) >= MinContrast);
    }

    /// <summary>The WCAG 2 contrast ratio of two colours: 1 (the same) to 21 (black on white).</summary>
    public static double Contrast(Rgb a, Rgb b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>The WCAG 2 relative luminance of an sRGB colour: 0 (black) to 1 (white).</summary>
    public static double Luminance(Rgb c)
    {
        static double Linear(byte v)
        {
            var x = v / 255.0;
            return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linear(c.R)) + (0.7152 * Linear(c.G)) + (0.0722 * Linear(c.B));
    }

    /// <summary>The HSL hue of <paramref name="c"/> in degrees (0 to 360); 0 for a grey.</summary>
    public static double Hue(Rgb c) => ToHsl(c).H;

    /// <summary><paramref name="share"/> of <paramref name="a"/>, the rest <paramref name="b"/>.</summary>
    public static Rgb Mix(Rgb a, Rgb b, double share)
    {
        static byte Channel(byte x, byte y, double s) => (byte)Math.Round((x * s) + (y * (1 - s)), MidpointRounding.AwayFromZero);
        return new Rgb(Channel(a.R, b.R, share), Channel(a.G, b.G, share), Channel(a.B, b.B, share));
    }

    private static (double H, double S, double L) ToHsl(Rgb c)
    {
        var (r, g, b) = (c.R / 255.0, c.G / 255.0, c.B / 255.0);
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        var d = max - min;
        if (d == 0)
        {
            return (0, 0, l);
        }

        var s = d / (1 - Math.Abs((2 * l) - 1));
        var h = max == r ? ((g - b) / d) + (g < b ? 6 : 0) : max == g ? ((b - r) / d) + 2 : ((r - g) / d) + 4;
        return (h * 60, s, l);
    }

    private static Rgb FromHsl(double h, double s, double l)
    {
        var chroma = (1 - Math.Abs((2 * l) - 1)) * s;
        var x = chroma * (1 - Math.Abs(((h / 60) % 2) - 1));
        var (r, g, b) = (h / 60) switch
        {
            < 1 => (chroma, x, 0.0),
            < 2 => (x, chroma, 0.0),
            < 3 => (0.0, chroma, x),
            < 4 => (0.0, x, chroma),
            < 5 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };
        var m = l - (chroma / 2);
        static byte Channel(double v) => (byte)Math.Clamp(Math.Round(v * 255, MidpointRounding.AwayFromZero), 0, 255);
        return new Rgb(Channel(r + m), Channel(g + m), Channel(b + m));
    }
}
