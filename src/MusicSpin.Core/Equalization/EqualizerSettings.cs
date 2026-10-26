using System.Globalization;

namespace MusicSpin.Core.Equalization;

/// <summary>A named set of band gains: one of the built-in presets or one the user saved.</summary>
/// <param name="Name">The name it is applied by (case-insensitive).</param>
/// <param name="Gains">One gain in dB per band of <see cref="EqualizerSettings.Bands"/>.</param>
/// <param name="BuiltIn">Shipped with MusicSpin (cannot be overwritten or deleted).</param>
public sealed record EqualizerPreset(string Name, IReadOnlyList<double> Gains, bool BuiltIn);

/// <summary>
/// The 10-band graphic equalizer (#141): on/off, a gain per band, a pre-amp and the preset the gains came from.
/// Settings.json keeps them as <c>equalizerEnabled</c>, <c>equalizerGains</c>, <c>equalizerPreampDb</c>,
/// <c>equalizerPreset</c> and the user's own presets as <c>equalizerPresets</c>.
/// </summary>
/// <param name="Enabled">Off: the samples are not touched (bit-exact).</param>
/// <param name="Gains">One gain in dB per band (<see cref="MinGainDb"/>..<see cref="MaxGainDb"/>); null or empty = flat.</param>
/// <param name="PreampDb">Added before the bands (<see cref="MinPreampDb"/>..<see cref="MaxPreampDb"/>). Never lifts the result above 0 dB: see <see cref="EqualizerDesign"/>.</param>
/// <param name="Preset">The preset the gains are from; null once a band was changed by hand (custom).</param>
public sealed record EqualizerSettings(bool Enabled = false, IReadOnlyList<double>? Gains = null, double PreampDb = 0, string? Preset = "flat")
{
    /// <summary>ISO octave centres (Hz), the standard 10-band graphic EQ.</summary>
    public static IReadOnlyList<double> Bands { get; } = [31.25, 62.5, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    public const int BandCount = 10;
    public const double MinGainDb = -12;
    public const double MaxGainDb = 12;
    public const double MinPreampDb = -12;
    public const double MaxPreampDb = 12;
    public const int MaxPresetNameLength = 40;
    public const int MaxUserPresets = 64;

    public static EqualizerSettings Default { get; } = new();

    /// <summary>The gains, always <see cref="BandCount"/> long (missing ones flat).</summary>
    public IReadOnlyList<double> BandGains => Gains is { Count: BandCount } g ? g : Flat;

    private static readonly double[] Flat = new double[BandCount];

    /// <summary>On, and something to do: every band at 0 dB and no pre-amp leaves the samples untouched.</summary>
    public bool IsActive => Enabled && (PreampDb != 0 || BandGains.Any(g => g != 0));

    public static IReadOnlyList<EqualizerPreset> BuiltInPresets { get; } =
    [
        new("flat", [0, 0, 0, 0, 0, 0, 0, 0, 0, 0], true),
        new("bass-boost", [6, 5, 4, 2, 0, 0, 0, 0, 0, 0], true),
        new("treble-boost", [0, 0, 0, 0, 0, 0, 1, 3, 5, 6], true),
        new("rock", [5, 4, 2, -1, -2, -1, 1, 3, 4, 5], true),
        new("pop", [-1, 1, 3, 4, 3, 0, -1, -1, 1, 2], true),
        new("jazz", [3, 2, 1, 2, -1, -1, 0, 1, 2, 3], true),
        new("classical", [4, 3, 2, 1, -1, -1, 0, 2, 3, 4], true),
        new("vocal", [-3, -2, -1, 1, 3, 4, 3, 1, 0, -1], true),
        new("loudness", [6, 4, 1, 0, -1, 0, -1, 1, 4, 5], true),
        new("electronic", [5, 4, 1, 0, -2, 1, 0, 1, 4, 5], true),
        new("acoustic", [3, 3, 2, 1, 1, 1, 2, 2, 2, 1], true),
    ];

    public static EqualizerPreset? BuiltIn(string? name)
        => BuiltInPresets.FirstOrDefault(p => string.Equals(p.Name, NormalizeName(name), StringComparison.OrdinalIgnoreCase));

    /// <summary>Lower-case, trimmed, spaces and underscores as dashes ("Bass boost" = "bass-boost").</summary>
    public static string NormalizeName(string? name)
        => (name ?? string.Empty).Trim().Replace(' ', '-').Replace('_', '-').ToLowerInvariant();

    /// <summary>The band index for <paramref name="text"/>: 1..10 (as listed) or a centre frequency ("31", "1k", "16000", "1khz").</summary>
    public static bool TryParseBand(string? text, out int band)
    {
        band = -1;
        var t = (text ?? string.Empty).Trim().ToLowerInvariant();
        if (t.EndsWith("hz", StringComparison.Ordinal))
        {
            t = t[..^2];
        }

        var kilo = t.EndsWith('k');
        if (kilo)
        {
            t = t[..^1];
        }

        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            return false;
        }

        if (!kilo && value is >= 1 and <= BandCount && value == Math.Floor(value) && !t.Contains('.'))
        {
            band = (int)value - 1;
            return true;
        }

        var hz = kilo ? value * 1000 : value;
        for (var i = 0; i < BandCount; i++)
        {
            // Within a fifth of the centre: "31", "62", "63", "1000", "16k" all name their band.
            if (Math.Abs(Math.Log2(hz / Bands[i])) < 0.2)
            {
                band = i;
                return true;
            }
        }

        return false;
    }

    public static string BandName(int band) => Bands[band] >= 1000
        ? string.Create(CultureInfo.InvariantCulture, $"{Bands[band] / 1000:0.##}k")
        : string.Create(CultureInfo.InvariantCulture, $"{Bands[band]:0.##}");

    /// <summary>Null when <paramref name="name"/> can name a user preset, else why not.</summary>
    public static string? CheckPresetName(string? name)
    {
        var n = NormalizeName(name);
        if (n.Length == 0)
        {
            return "A preset needs a name.";
        }

        if (n.Length > MaxPresetNameLength)
        {
            return $"A preset name has at most {MaxPresetNameLength} characters.";
        }

        if (n.Any(char.IsControl))
        {
            return "A preset name cannot contain control characters.";
        }

        return BuiltIn(n) is not null ? $"'{n}' is a built-in preset; save under another name." : null;
    }

    /// <summary>Null when <paramref name="gains"/> are a valid set of band gains, else why not.</summary>
    public static string? CheckGains(IReadOnlyList<double>? gains)
    {
        if (gains is null || gains.Count != BandCount)
        {
            return $"An equalizer needs exactly {BandCount} band gains.";
        }

        return gains.All(g => double.IsFinite(g) && g is >= MinGainDb and <= MaxGainDb)
            ? null
            : $"Band gains must be between {MinGainDb} and {MaxGainDb} dB.";
    }

    /// <summary>The same settings with out-of-range, missing or non-finite numbers brought back into range.</summary>
    public EqualizerSettings Clamped() => this with
    {
        Gains = Gains is { Count: BandCount } g
            ? [.. g.Select(v => double.IsFinite(v) ? Math.Clamp(v, MinGainDb, MaxGainDb) : 0)]
            : [.. Flat],
        PreampDb = double.IsFinite(PreampDb) ? Math.Clamp(PreampDb, MinPreampDb, MaxPreampDb) : 0,
        Preset = string.IsNullOrWhiteSpace(Preset) ? null : NormalizeName(Preset),
    };

    /// <summary>The equalizer in <paramref name="settings"/> (settings.json), brought into range.</summary>
    public static EqualizerSettings From(MusicSpin.Core.Settings.AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new EqualizerSettings(settings.EqualizerEnabled, settings.EqualizerGains, settings.EqualizerPreampDb, settings.EqualizerPreset).Clamped();
    }

    /// <summary>
    /// The user's presets in <paramref name="settings"/>. A corrupt entry (bad name, wrong band count, a gain out of range
    /// or not a number) is left out and described in <paramref name="problems"/>, never fixed up silently.
    /// </summary>
    public static IReadOnlyList<EqualizerPreset> UserPresets(MusicSpin.Core.Settings.AppSettings settings, List<string>? problems = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var list = new List<EqualizerPreset>();
        foreach (var (name, gains) in settings.EqualizerPresets ?? [])
        {
            var problem = CheckPresetName(name) ?? CheckGains(gains);
            if (problem is null && list.Any(p => p.Name == NormalizeName(name)))
            {
                problem = "a duplicate name.";
            }

            if (problem is not null)
            {
                problems?.Add($"Ignoring equalizer preset '{name}': {problem}");
                continue;
            }

            list.Add(new EqualizerPreset(NormalizeName(name), [.. gains!], false));
            if (list.Count == MaxUserPresets)
            {
                break;
            }
        }

        return list;
    }

    /// <summary><paramref name="settings"/> with this equalizer (and, when given, these user presets).</summary>
    public MusicSpin.Core.Settings.AppSettings ApplyTo(MusicSpin.Core.Settings.AppSettings settings, IReadOnlyList<EqualizerPreset>? userPresets = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var clamped = Clamped();
        var next = settings with
        {
            EqualizerEnabled = clamped.Enabled,
            EqualizerGains = [.. clamped.BandGains],
            EqualizerPreampDb = clamped.PreampDb,
            EqualizerPreset = clamped.Preset,
        };
        return userPresets is null
            ? next
            : next with { EqualizerPresets = userPresets.Where(p => !p.BuiltIn).ToDictionary(p => p.Name, p => p.Gains.ToArray(), StringComparer.Ordinal) };
    }

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture,
            $"{(Enabled ? "on" : "off")}, {Preset ?? "custom"}, pre-amp {PreampDb:+0.#;-0.#;0} dB, {string.Join(" ", BandGains.Select(g => g.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture)))}");
}
