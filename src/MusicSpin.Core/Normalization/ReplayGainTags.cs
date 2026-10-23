using System.Globalization;

namespace MusicSpin.Core.Normalization;

/// <summary>
/// The levelling tags of a file, normalized: gains in dB relative to the ReplayGain 2.0 reference (-18 LUFS), peaks as linear
/// full-scale ratios. Missing, unparsable and out-of-range values are null. Opus' <c>R128_TRACK_GAIN</c> / <c>R128_ALBUM_GAIN</c>
/// (Q7.8 fixed point, relative to -23 LUFS) are converted to the same reference.
/// </summary>
public readonly record struct ReplayGainTags(double? TrackGainDb, double? TrackPeak, double? AlbumGainDb, double? AlbumPeak)
{
    /// <summary>Gains beyond this are taken for a broken tag.</summary>
    public const double MaxAbsGainDb = 60;

    /// <summary>Peaks above this (+24 dBFS) are taken for a broken tag.</summary>
    public const double MaxPeak = 16;

    /// <summary>EBU R128 puts programme loudness at -23 LUFS; ReplayGain 2.0 at -18: an R128 gain is 5 dB short of the latter.</summary>
    public const double R128ReferenceOffsetDb = 5;

    public static ReplayGainTags None => default;

    public bool HasAny => TrackGainDb is not null || AlbumGainDb is not null;

    /// <summary>"-6.54 dB", "+3.2", "-1,5 dB": the number, or null.</summary>
    public static double? ParseGain(string? text)
    {
        if (!TryNumber(text, "dB", out var value))
        {
            return null;
        }

        return ValidGain(value);
    }

    /// <summary>"0.988553": a linear peak above 0.</summary>
    public static double? ParsePeak(string? text) => TryNumber(text, null, out var value) ? ValidPeak(value) : null;

    /// <summary>An Opus <c>R128_*_GAIN</c> value ("-512": Q7.8 dB relative to -23 LUFS) as a gain against the -18 LUFS reference.</summary>
    public static double? ParseR128(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)
            || !int.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var q78)
            || q78 is < short.MinValue or > short.MaxValue)
        {
            return null;
        }

        return ValidGain(q78 / 256.0 + R128ReferenceOffsetDb);
    }

    /// <summary>The gain when it is a usable number, else null.</summary>
    public static double? ValidGainOrNull(double? value) => value is { } v ? ValidGain(v) : null;

    /// <summary>The peak when it is a usable number, else null.</summary>
    public static double? ValidPeakOrNull(double? value) => value is { } v ? ValidPeak(v) : null;

    public static double? ValidGain(double value) => double.IsFinite(value) && Math.Abs(value) <= MaxAbsGainDb ? value : null;

    public static double? ValidPeak(double value) => double.IsFinite(value) && value > 0 && value <= MaxPeak ? value : null;

    /// <summary>Builds the tags from the raw text values of a file (any may be null); ReplayGain tags win over R128 ones.</summary>
    public static ReplayGainTags FromText(string? trackGain, string? trackPeak, string? albumGain, string? albumPeak, string? r128Track = null, string? r128Album = null)
        => new(ParseGain(trackGain) ?? ParseR128(r128Track), ParsePeak(trackPeak), ParseGain(albumGain) ?? ParseR128(r128Album), ParsePeak(albumPeak));

    private static bool TryNumber(string? text, string? unit, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var cleaned = text.Trim();
        if (unit is not null)
        {
            cleaned = cleaned.Replace(unit, string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        }

        if (cleaned.Contains(',', StringComparison.Ordinal) && !cleaned.Contains('.', StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace(',', '.'); // some taggers write the locale's decimal comma
        }

        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
