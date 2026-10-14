using System.Globalization;

namespace MusicSpin.Core.Playback;

/// <summary>
/// The seek syntax shared by <c>music-spin seek</c>, the MCP <c>seek</c> tool and the app's elapsed-time box: seconds
/// ("90", "12.5"), [h:]mm:ss ("1:30", "1:02:03"), or relative with + or - ("+10", "-0:30").
/// </summary>
public static class SeekSyntax
{
    /// <summary>Parses a seek target. <paramref name="seconds"/> is signed for a relative target.</summary>
    public static bool TryParse(string text, out double seconds, out bool relative, out string? error)
    {
        ArgumentNullException.ThrowIfNull(text);
        seconds = 0;
        relative = false;
        error = null;
        var t = text.Trim().Replace('−', '-'); // a typographic minus counts too
        var sign = 1;
        if (t.StartsWith('+') || t.StartsWith('-'))
        {
            relative = true;
            sign = t[0] == '-' ? -1 : 1;
            t = t[1..].TrimStart();
        }

        var parts = t.Split(':');
        if (parts.Length > 3 || parts.Any(p => p.Length == 0))
        {
            error = Invalid(text);
            return false;
        }

        double total = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var isLast = i == parts.Length - 1;
            if (!double.TryParse(parts[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v)
                || (!isLast && v != Math.Floor(v)) || (i > 0 && v >= 60))
            {
                error = Invalid(text);
                return false;
            }

            total = total * 60 + v;
        }

        seconds = sign * total;
        return true;
    }

    private static string Invalid(string text)
        => $"Invalid position '{text}'. Use seconds (90), mm:ss (1:30), h:mm:ss, or +/- for relative (+10, -0:30).";
}
