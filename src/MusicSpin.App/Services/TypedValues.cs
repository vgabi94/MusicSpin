using System.Globalization;
using System.Text.RegularExpressions;
using MusicSpin.Core.Playback;

namespace MusicSpin.App.Services;

/// <summary>
/// What the player bar's text boxes accept: the elapsed time takes the CLI's <c>seek</c> syntax, the volume takes
/// percents and the CLI's fractions. Absolute values out of range are refused (as in the CLI); relative ones are clamped.
/// </summary>
public static partial class TypedValues
{
    /// <summary>The smallest volume step: a tenth of a percent.</summary>
    public const double VolumeResolution = 0.1;

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
        var match = VolumePattern().Match((text ?? string.Empty).Trim().Replace(',', '.').Replace('−', '-'));
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

    public static double RoundVolume(double percent)
        => Math.Round(Math.Round(percent / VolumeResolution) * VolumeResolution, 1);

    [GeneratedRegex(@"^(?<sign>[+-]?)\s*(?<n>\d*\.?\d+)\s*(?<pct>%?)$")]
    private static partial Regex VolumePattern();
}
