using System.Globalization;

namespace MusicSpin.Core.Normalization;

/// <summary>How volume levelling picks a track's gain.</summary>
public enum NormalizationMode
{
    /// <summary>No gain: the samples reach the output untouched (bit-exact).</summary>
    Off,

    /// <summary>Every track to the target level, whatever it is played with.</summary>
    Track,

    /// <summary>An album's tracks keep their level to each other (the album's gain); tracks without an album gain use their own.</summary>
    Album,

    /// <summary>Album gain while an album plays in order, track gain for shuffle, radio and mixed queues.</summary>
    Auto,
}

/// <summary>
/// Volume levelling (ReplayGain / EBU R128): the mode, the loudness the tracks are brought to and a pre-amp on top.
/// Settings.json keeps the three values as <c>normalization</c>, <c>normalizationTargetLufs</c> and <c>normalizationPreampDb</c>.
/// </summary>
/// <param name="Mode">Off, track, album or auto.</param>
/// <param name="TargetLufs">Loudness the tracks are brought to (LUFS, EBU R128 integrated). ReplayGain 2.0's reference, -18, by default.</param>
/// <param name="PreampDb">Added to every track's gain, in dB (negative: headroom for what the target level clips).</param>
public sealed record NormalizationSettings(NormalizationMode Mode = NormalizationMode.Auto, double TargetLufs = NormalizationSettings.DefaultTargetLufs, double PreampDb = 0)
{
    /// <summary>ReplayGain 2.0's reference level (about -18 LUFS; EBU R128 broadcast uses -23).</summary>
    public const double DefaultTargetLufs = -18;

    public const double MinTargetLufs = -30;
    public const double MaxTargetLufs = -8;
    public const double MinPreampDb = -12;
    public const double MaxPreampDb = 12;

    public static NormalizationSettings Default { get; } = new();

    public static NormalizationSettings Disabled { get; } = new(NormalizationMode.Off);

    public bool IsOn => Mode != NormalizationMode.Off;

    /// <summary>The same settings with out-of-range or non-finite numbers brought back into range.</summary>
    public NormalizationSettings Clamped() => this with
    {
        TargetLufs = double.IsFinite(TargetLufs) ? Math.Clamp(TargetLufs, MinTargetLufs, MaxTargetLufs) : DefaultTargetLufs,
        PreampDb = double.IsFinite(PreampDb) ? Math.Clamp(PreampDb, MinPreampDb, MaxPreampDb) : 0,
    };

    public static string Name(NormalizationMode mode) => mode switch
    {
        NormalizationMode.Off => "off",
        NormalizationMode.Track => "track",
        NormalizationMode.Album => "album",
        _ => "auto",
    };

    public const string ModeValues = "off|track|album|auto";

    /// <summary>"off", "track", "album" or "auto" (any case); also "on" (auto) and "false"/"true".</summary>
    public static bool TryParseMode(string? text, out NormalizationMode mode)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "off" or "false" or "no" or "0" or "none":
                mode = NormalizationMode.Off;
                return true;
            case "track":
                mode = NormalizationMode.Track;
                return true;
            case "album":
                mode = NormalizationMode.Album;
                return true;
            case "auto" or "on" or "true" or "yes" or "1":
                mode = NormalizationMode.Auto;
                return true;
            default:
                mode = NormalizationMode.Auto;
                return false;
        }
    }

    /// <summary>The mode as settings.json holds it; an unknown text counts as the default (auto).</summary>
    public static NormalizationMode ModeOrDefault(string? text) => TryParseMode(text, out var mode) ? mode : NormalizationMode.Auto;

    /// <summary>The levelling settings in <paramref name="settings"/> (settings.json), brought into range.</summary>
    public static NormalizationSettings From(MusicSpin.Core.Settings.AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new NormalizationSettings(ModeOrDefault(settings.Normalization), settings.NormalizationTargetLufs, settings.NormalizationPreampDb).Clamped();
    }

    /// <summary><paramref name="settings"/> with these levelling settings.</summary>
    public MusicSpin.Core.Settings.AppSettings ApplyTo(MusicSpin.Core.Settings.AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var clamped = Clamped();
        return settings with { Normalization = Name(clamped.Mode), NormalizationTargetLufs = clamped.TargetLufs, NormalizationPreampDb = clamped.PreampDb };
    }

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Name(Mode)}, target {TargetLufs:0.#} LUFS, pre-amp {PreampDb:+0.#;-0.#;0} dB");
}
