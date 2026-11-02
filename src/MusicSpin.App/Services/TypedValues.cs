using System.Globalization;
using System.Text.RegularExpressions;
using MusicSpin.Core.Playback;

namespace MusicSpin.App.Services;

/// <summary>
/// What the player bar's text boxes accept: the elapsed time takes the CLI's <c>seek</c> syntax, the volume takes
/// percents and the CLI's fractions, and decibels (#39). Absolute values out of range are refused (as in the CLI);
/// relative ones are clamped.
/// </summary>
public static partial class TypedValues
{
    /// <summary>The smallest volume step: a tenth of a percent.</summary>
    public const double VolumeResolution = 0.1;

    /// <summary>The smallest volume step in decibels: a tenth of a dB.</summary>
    public const double DecibelResolution = 0.1;

    /// <summary>A relative decibel step from silence starts here (the bottom of the decibel bar's even range).</summary>
    public const double DecibelFloor = -60;

    /// <summary>
    /// "90", "1:30", "1:02:03" (absolute, at most <paramref name="duration"/>), or "+10", "-10", "+0:30" (from
    /// <paramref name="position"/>, clamped to the track).
    /// </summary>
    public static bool TryPosition(string? text, double position, double duration, out double seconds)
    {
        seconds = 0;
        if (text is null || !SeekSyntax.TryParse(text, out var value, out var relative, out _))
        {
            return false;
        }

        if (relative)
        {
            seconds = Math.Clamp(position + value, 0, Math.Max(0, duration));
            return true;
        }

        seconds = value;
        return value <= duration;
    }

    /// <summary>
    /// A volume in percent: "37", "37%", "37.5", "12,5"; relative "+5", "-10%" (from <paramref name="percent"/>, stopping
    /// at 0 and 100); and the CLI's fractions "0.37" and "+0.1". A bare whole number is percent ("1" is 1%). Over 100 is
    /// refused. The result is rounded to <see cref="VolumeResolution"/>.
    /// </summary>
    public static bool TryVolume(string? text, double percent, out double result)
    {
        result = 0;
        var match = VolumePattern().Match(Clean(text));
        if (!match.Success || !double.TryParse(match.Groups["n"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n))
        {
            return false;
        }

        var isFraction = match.Groups["pct"].Length == 0 && match.Groups["n"].Value.Contains('.', StringComparison.Ordinal) && n <= 1;
        if (isFraction)
        {
            n *= 100;
        }

        var sign = match.Groups["sign"].Value;
        if (sign.Length > 0)
        {
            result = RoundVolume(Math.Clamp(percent + (sign == "-" ? -n : n), 0, 100));
            return true;
        }

        result = RoundVolume(n);
        return n <= 100;
    }

    /// <summary>
    /// A volume in either unit; <paramref name="inDecibels"/> says which one is shown, and so what a bare number means.
    /// A number with "%" is always percent (as in <see cref="TryVolume(string?, double, out double)"/>), one with "dB"
    /// always decibels. In decibels a level is "-12", "-12 dB", "0 dB" (100%) or "-inf dB" (silence), and "+3" or "+3 dB"
    /// is 3 dB louder (stopping at 0 dB; from silence, from <see cref="DecibelFloor"/>); a level above 0 dB is over 100%
    /// and refused. The result is in percent, from a decibel value rounded to <see cref="DecibelResolution"/>.
    /// </summary>
    public static bool TryVolume(string? text, double percent, bool inDecibels, out double result)
    {
        var match = DecibelPattern().Match(Clean(text));
        if (!match.Success || (!inDecibels && match.Groups["db"].Length == 0))
        {
            return TryVolume(text, percent, out result);
        }

        result = 0;
        var isInfinite = match.Groups["inf"].Length > 0;
        var n = 0.0;
        if (!isInfinite && !double.TryParse(match.Groups["n"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out n))
        {
            return false;
        }

        switch (match.Groups["sign"].Value)
        {
            case "+":
                if (isInfinite)
                {
                    return false;
                }

                var from = percent > 0 ? ToDecibels(percent) : DecibelFloor;
                result = FromDecibels(Math.Min(0, RoundDecibels(from + n)));
                return true;
            case "-":
                result = isInfinite ? 0 : FromDecibels(RoundDecibels(-n));
                return true;
            default:
                result = 100;
                return !isInfinite && n == 0; // "0 dB" is 100%; a level above it is over 100%
        }
    }

    public static double RoundVolume(double percent)
        => Math.Round(Math.Round(percent / VolumeResolution) * VolumeResolution, 1);

    public static double RoundDecibels(double decibels)
        => Math.Round(Math.Round(decibels / DecibelResolution) * DecibelResolution, 1);

    /// <summary>A volume in percent as decibels: 100% is 0 dB, 50% about −6 dB, 0% −∞.</summary>
    public static double ToDecibels(double percent) => percent > 0 ? 20 * Math.Log10(percent / 100) : double.NegativeInfinity;

    /// <summary>Decibels as a volume in percent: 0 dB is 100%, −∞ is 0%.</summary>
    public static double FromDecibels(double decibels) => double.IsNegativeInfinity(decibels) ? 0 : 100 * Math.Pow(10, decibels / 20);

    /// <summary>
    /// A volume in percent rounded to a tenth of a dB, kept in percent. 0% and 100% stay as they are, and nothing else
    /// rounds up to the bit-perfect 0 dB: 99.9% is −0.1 dB.
    /// </summary>
    public static double RoundToDecibels(double percent)
        => double.IsNaN(percent) || percent <= 0 ? 0 : percent >= 100 ? 100 : FromDecibels(DecibelsOf(percent));

    /// <summary>
    /// A volume in percent as decibels to a tenth: −∞ at 0% (or less), 0 dB only at 100% (or more), and at most −0.1 dB
    /// below it.
    /// </summary>
    public static double DecibelsOf(double percent)
        => double.IsNaN(percent) || percent <= 0 ? double.NegativeInfinity
            : percent >= 100 ? 0
            : Math.Min(-DecibelResolution, RoundDecibels(ToDecibels(percent)));

    private static string Clean(string? text) => (text ?? string.Empty).Trim().Replace(',', '.').Replace('−', '-');

    [GeneratedRegex(@"^(?<sign>[+-]?)\s*(?<n>\d*\.?\d+)\s*(?<pct>%?)$")]
    private static partial Regex VolumePattern();

    /// <summary>A decibel value: an optional sign, a number or "inf"/"∞", then "dB" in any case (optional).</summary>
    [GeneratedRegex(@"^(?<sign>[+-]?)\s*(?:(?<inf>inf|∞)|(?<n>\d*\.?\d+))\s*(?<db>db)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DecibelPattern();
}
